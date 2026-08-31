using System;
using System.Collections.Generic;
using UnityEngine;

namespace UEBS2PathingMod
{
    /// <summary>
    /// Emergent sub-group autonomy within a single team.
    ///
    /// Every few seconds, armies on the same team are clustered into spatial
    /// sub-groups. Each sub-group is assessed for its local tactical situation
    /// (engaged, outnumbered, flanked, winning, isolated) and may "fracture"
    /// from the player's blanket order to act autonomously:
    ///   - Reinforce a nearby losing friendly sub-group
    ///   - Retreat toward friendlies when isolated and outnumbered
    ///   - Pursue retreating enemies when winning
    ///   - Reposition to avoid being flanked/surrounded
    ///
    /// The mechanism is the same safe flow-field Target injection used by
    /// strategic targeting — we place NavGrid Target objects that redirect
    /// the GPU pathfinding, never touching GPU buffers directly.
    /// </summary>
    public static class SubGroupFracture
    {
        // ---- Tuning (mirrored to config) ----
        internal static bool Enabled = true;
        internal static float ClusterRadius = 150f;       // armies within this distance form a sub-group
        internal static float ReassessInterval = 3f;      // seconds between fracture re-evaluation
        internal static float ReinforceThreshold = 0.4f;  // ally health ratio below which we reinforce
        internal static float RetreatIsolationDist = 250f; // distance from nearest friendly sub-group to count as "isolated"
        internal static float OutnumberedRatio = 0.6f;    // enemy/ally count ratio above which we're "outnumbered"
        internal static float WinningKillRatio = 2.0f;    // our kills / their kills above which we're "winning"
        internal static bool BehaviorReinforce = true;
        internal static bool BehaviorRetreat = true;
        internal static bool BehaviorPursue = true;
        internal static bool BehaviorAntiFlank = true;
        internal static bool MoraleEnabled = true;
        internal static float RoutThreshold = 0.15f;
        internal static float RetreatThreshold = 0.35f;
        internal static bool AggressionBoost = true;
        internal static float DispersionFactor = 0.5f;   // 0=vanilla tight, 1=very wide spread
        internal static float DispersionWidth = 200f;     // max lateral spread for multi-point targets
        internal static bool FlowFieldModulationEnabled = true;

        // ---- Runtime state ----
        private static float _timer;
        private static readonly List<SubGroup> _subGroups = new List<SubGroup>();
        private static readonly List<Target> _fractureTargets = new List<Target>();

        // Armies that are currently fracturing (action != None), for fast lookup
        // by the RunGpuAi prefix. Keyed by the Army instance's GetInstanceID().
        private static readonly HashSet<int> _fracturingArmyIds = new HashSet<int>();
        // Armies that are routing/retreating — need WalkAttack suppressed too.
        private static readonly HashSet<int> _retreatingArmyIds = new HashSet<int>();
        // Store original field values so we can restore them after the GPU dispatch.
        private static readonly Dictionary<int, bool> _originalHoldPos = new Dictionary<int, bool>();
        private static readonly Dictionary<int, bool> _originalWalkAttack = new Dictionary<int, bool>();
        private static readonly Dictionary<int, bool> _originalHoldGuard = new Dictionary<int, bool>();

        /// <summary>One spatial cluster of armies on the same team.</summary>
        private class SubGroup
        {
            public int Team;
            public List<Army> Armies = new List<Army>();
            public Vector3 Centroid;
            public int TotalRemaining;
            public int TotalArmyCount;
            public float HealthRatio;       // remaining / armycount
            public float NearestEnemyDist;
            public Vector3 NearestEnemyPos;
            public int NearestEnemyRemaining;
            public float NearestFriendlyDist;
            public Vector3 NearestFriendlyCentroid;
            public int NearestFriendlyRemaining;
            public float OurKills;
            public float EnemyKills;
            public bool IsEngaged;
            public bool IsIsolated;
            public bool IsOutnumbered;
            public bool IsWinning;
            public bool IsFlanked;
            public bool IsRanged;       // primarily ranged units
            public bool IsHolding;      // majority have HoldPosition or HoldGuard
            public float Morale;        // 0..1, derived from situation
            public TerrainAnalysis.TerrainAssessment Terrain;
            public FractureAction CurrentAction = FractureAction.None;
        }

        /// <summary>What a sub-group has decided to do this cycle.</summary>
        public enum FractureAction
        {
            None,               // follow player order as-is
            Reinforce,          // move toward a losing friendly sub-group
            Retreat,            // fall back toward friendly forces
            Pursue,             // chase a retreating enemy
            Reposition,         // reposition to avoid encirclement
            SeekHighGround,     // move to nearby high ground for advantage
            SeekCover,          // move to nearby cover (ranged units under fire)
            Rout,               // full panic retreat — morale has collapsed
            Regroup,            // rallying point for routed units to reform
            FlankFortification, // approach enemy fortification from a distributed side
        }

        /// <summary>Called from the NavGrid.Update prefix each frame.</summary>
        internal static void Tick()
        {
            if (!Enabled) return;
            _timer += Time.deltaTime;

            // Dynamic scaling: for massive battles, reassess less frequently
            // to avoid CPU spikes from terrain sampling + clustering.
            float effectiveInterval = ReassessInterval;
            int totalAi = PathingModPlugin.TotalAiCount;
            if (totalAi > 50000) effectiveInterval = Mathf.Max(ReassessInterval, 6f);
            if (totalAi > 150000) effectiveInterval = Mathf.Max(ReassessInterval, 10f);

            if (_timer < effectiveInterval) return;
            _timer = 0f;

            try
            {
                AssessAndAct();
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[PathingMod] fracture tick error: {e}");
            }
        }

        /// <summary>Main pipeline: cluster -> assess -> decide -> issue targets.</summary>
        private static void AssessAndAct()
        {
            var teams = ThreadManager.Teams;
            if (teams == null || teams.Count == 0) return;
            if (ThreadManager.AllArmies == null || ThreadManager.AllArmies.Count == 0) return;

            // Clear last cycle's fracture targets.
            foreach (var t in _fractureTargets)
            {
                if (t != null)
                {
                    t.Active = false;
                    UnityEngine.Object.Destroy(t.gameObject);
                }
            }
            _fractureTargets.Clear();
            _subGroups.Clear();
            _fracturingArmyIds.Clear();
            _retreatingArmyIds.Clear();
            FlowFieldModulator.ClearBlockers();

            // 1. Cluster each team's armies into spatial sub-groups.
            for (int ti = 0; ti < teams.Count; ti++)
            {
                ClusterTeam(teams[ti], ti);
            }

            if (_subGroups.Count == 0) return;

            // 1b. Detect fortifications (HoldGuard armies, structures).
            // Done before assessment so morale can include fort bonuses.
            FortificationAnalysis.DetectFortifications(null);

            // 2. Assess each sub-group's tactical situation.
            for (int i = 0; i < _subGroups.Count; i++)
            {
                AssessSubGroup(_subGroups[i]);
            }

            // 3. Decide and issue fracture actions.
            for (int i = 0; i < _subGroups.Count; i++)
            {
                DecideAndAct(_subGroups[i], i);
            }

            // 4. Build the fast-lookup set of armies that are fracturing this cycle.
            //    The RunGpuAi prefix uses this to override HoldPosition on the GPU.
            //    Also track retreating/routing armies that need WalkAttack suppressed.
            foreach (var sg in _subGroups)
            {
                if (sg.CurrentAction == FractureAction.None) continue;
                bool isRetreating = sg.CurrentAction == FractureAction.Rout
                    || sg.CurrentAction == FractureAction.Retreat
                    || sg.CurrentAction == FractureAction.Regroup;
                foreach (var a in sg.Armies)
                {
                    if (a == null) continue;
                    _fracturingArmyIds.Add(a.GetInstanceID());
                    if (isRetreating)
                        _retreatingArmyIds.Add(a.GetInstanceID());
                }
            }
        }

        /// <summary>
        /// Check if a specific army instance is currently in a fracturing sub-group.
        /// Called by the RunGpuAi prefix every frame for every army — must be fast.
        /// </summary>
        internal static bool IsArmyFracturing(Army army)
        {
            return _fracturingArmyIds.Count > 0 && _fracturingArmyIds.Contains(army.GetInstanceID());
        }

        /// <summary>
        /// Temporarily override GPU flags for a fracturing army so the compute
        /// shader actually follows our flow-field targets:
        ///   - HoldPosition = false (so flow field is read)
        ///   - HoldGuard = false (CRITICAL: the game's RunGpuAi re-sets
        ///     HoldPosition = true if HoldGuard is true, so we MUST clear
        ///     HoldGuard for ALL fracturing armies, not just retreating ones)
        ///   - For retreating/routing armies: also WalkAttack = false
        ///     (so the GPU doesn't seek enemies, letting the flow field pull them away)
        /// Called in the RunGpuAi prefix. Original values are saved for restoration.
        /// </summary>
        internal static void OverrideHoldPosition(Army army)
        {
            int id = army.GetInstanceID();
            bool isRetreating = _retreatingArmyIds.Contains(id);

            // HoldPosition — so the GPU reads the flow field.
            if (!_originalHoldPos.ContainsKey(id))
                _originalHoldPos[id] = army.HoldPosition;
            army.HoldPosition = false;

            // HoldGuard — MUST be cleared for ALL fracturing armies, because
            // the game's RunGpuAi does: if (HoldGuard) { HoldPosition = true; }
            // which would undo our HoldPosition override above.
            if (!_originalHoldGuard.ContainsKey(id))
                _originalHoldGuard[id] = army.HoldGuard;
            army.HoldGuard = false;

            // WalkAttack — only suppress for retreating/routing armies.
            // For other fractures (reinforce, pursue, flank), we still want
            // units to attack enemies they encounter en route.
            if (isRetreating)
            {
                if (!_originalWalkAttack.ContainsKey(id))
                    _originalWalkAttack[id] = army.WalkAttack;
                army.WalkAttack = false;
            }
        }

        /// <summary>Restore original GPU flags after the dispatch. Called in RunGpuAi postfix.</summary>
        internal static void RestoreHoldPosition(Army army)
        {
            int id = army.GetInstanceID();
            if (_originalHoldPos.TryGetValue(id, out var holdPos))
            {
                army.HoldPosition = holdPos;
                _originalHoldPos.Remove(id);
            }
            if (_originalWalkAttack.TryGetValue(id, out var walkAttack))
            {
                army.WalkAttack = walkAttack;
                _originalWalkAttack.Remove(id);
            }
            if (_originalHoldGuard.TryGetValue(id, out var holdGuard))
            {
                army.HoldGuard = holdGuard;
                _originalHoldGuard.Remove(id);
            }
        }

        // ---- Step 1: Spatial clustering ----

        /// <summary>
        /// Greedy distance-based clustering: pick an unassigned army, grow a
        /// cluster by absorbing any same-team army within ClusterRadius of the
        /// cluster centroid, repeat. Simple and O(n^2) which is fine for the
        /// typical army count per team (usually &lt; 50).
        /// </summary>
        private static void ClusterTeam(ThreadManager.Team team, int teamIndex)
        {
            if (team.Armies == null || team.Armies.Count == 0) return;

            var unassigned = new List<Army>(team.Armies);
            while (unassigned.Count > 0)
            {
                var sg = new SubGroup { Team = team.TeamNumber };
                sg.Armies.Add(unassigned[0]);
                unassigned.RemoveAt(0);

                // Grow cluster: absorb nearby same-team armies.
                bool grew = true;
                while (grew)
                {
                    grew = false;
                    sg.Centroid = ComputeCentroid(sg.Armies);
                    for (int j = unassigned.Count - 1; j >= 0; j--)
                    {
                        if (unassigned[j] == null) { unassigned.RemoveAt(j); continue; }
                        if (Vector3.Distance(unassigned[j].transform.position, sg.Centroid) <= ClusterRadius)
                        {
                            sg.Armies.Add(unassigned[j]);
                            unassigned.RemoveAt(j);
                            grew = true;
                        }
                    }
                }

                sg.Centroid = ComputeCentroid(sg.Armies);
                _subGroups.Add(sg);
            }
        }

        private static Vector3 ComputeCentroid(List<Army> armies)
        {
            Vector3 sum = Vector3.zero;
            int n = 0;
            foreach (var a in armies)
            {
                if (a == null) continue;
                sum += a.transform.position;
                n++;
            }
            return n > 0 ? sum / n : Vector3.zero;
        }

        // ---- Step 2: Tactical assessment ----

        private static void AssessSubGroup(SubGroup sg)
        {
            // Aggregate stats.
            sg.TotalRemaining = 0;
            sg.TotalArmyCount = 0;
            sg.OurKills = 0;
            foreach (var a in sg.Armies)
            {
                if (a == null) continue;
                sg.TotalRemaining += a.Remaining;
                sg.TotalArmyCount += a.ArmyCount;
                sg.OurKills += a.TotalKills;
            }
            sg.HealthRatio = sg.TotalArmyCount > 0
                ? (float)sg.TotalRemaining / sg.TotalArmyCount : 0f;

            // Nearest enemy sub-group (across all other teams).
            sg.NearestEnemyDist = float.MaxValue;
            sg.NearestEnemyRemaining = 0;
            sg.NearestEnemyPos = Vector3.zero;
            sg.EnemyKills = 0;

            foreach (var other in _subGroups)
            {
                if (other.Team == sg.Team) continue;
                float d = Vector3.Distance(sg.Centroid, other.Centroid);
                if (d < sg.NearestEnemyDist)
                {
                    sg.NearestEnemyDist = d;
                    sg.NearestEnemyPos = other.Centroid;
                    sg.NearestEnemyRemaining = other.TotalRemaining;
                    sg.EnemyKills = other.OurKills;
                }
            }

            // Nearest friendly sub-group (same team, different cluster).
            sg.NearestFriendlyDist = float.MaxValue;
            sg.NearestFriendlyCentroid = sg.Centroid;
            sg.NearestFriendlyRemaining = 0;
            foreach (var ally in _subGroups)
            {
                if (ally == sg || ally.Team != sg.Team) continue;
                float d = Vector3.Distance(sg.Centroid, ally.Centroid);
                if (d < sg.NearestFriendlyDist)
                {
                    sg.NearestFriendlyDist = d;
                    sg.NearestFriendlyCentroid = ally.Centroid;
                    sg.NearestFriendlyRemaining = ally.TotalRemaining;
                }
            }

            // Situation flags.
            sg.IsEngaged = sg.NearestEnemyDist < 100f;
            sg.IsIsolated = sg.NearestFriendlyDist > RetreatIsolationDist;
            sg.IsOutnumbered = sg.NearestEnemyRemaining > 0 &&
                (float)sg.NearestEnemyRemaining / Mathf.Max(1, sg.TotalRemaining) > 1f / OutnumberedRatio;
            sg.IsWinning = sg.EnemyKills > 0 &&
                sg.OurKills / sg.EnemyKills > WinningKillRatio;
            sg.IsFlanked = CheckFlanked(sg);

            // Determine if this sub-group is primarily ranged.
            int rangedCount = 0;
            foreach (var a in sg.Armies)
            {
                if (a == null) continue;
                if (TerrainAnalysis.IsRanged(a)) rangedCount++;
            }
            sg.IsRanged = rangedCount * 2 > sg.Armies.Count; // majority ranged

            // Determine if this sub-group is primarily holding (hold/defend orders).
            int holdingCount = 0;
            foreach (var a in sg.Armies)
            {
                if (a == null) continue;
                if (a.HoldPosition || a.HoldGuard) holdingCount++;
            }
            sg.IsHolding = holdingCount * 2 > sg.Armies.Count;

            // ---- Morale calculation ----
            // Morale is a 0..1 score representing unit cohesion and willingness to fight.
            // It drives rout/retreat/pursue decisions for BOTH attacking and holding armies.
            sg.Morale = ComputeMorale(sg);

            // Terrain assessment (only if we have a known enemy position).
            // Skip terrain analysis for huge battles to save CPU — the basic
            // tactical decisions (reinforce/retreat/pursue) still work without it.
            int totalAi = PathingModPlugin.TotalAiCount;
            bool doTerrain = totalAi < 100000;
            if (doTerrain && sg.NearestEnemyDist < float.MaxValue)
            {
                sg.Terrain = TerrainAnalysis.Assess(sg.Centroid, sg.NearestEnemyPos, sg.IsRanged);
            }
            else
            {
                // Still get height advantage (cheap single sample) even in huge battles.
                if (sg.NearestEnemyDist < float.MaxValue)
                {
                    float hAdv = TerrainAnalysis.HeightAdvantage(sg.Centroid, sg.NearestEnemyPos);
                    sg.Terrain = new TerrainAnalysis.TerrainAssessment
                    {
                        HeightAdvantage = hAdv,
                        HasHighGround = hAdv > 5f,
                        AtHeightDisadvantage = hAdv < -5f,
                        HasCover = false,
                        IsRanged = sg.IsRanged,
                        NearbyHighGround = sg.Centroid,
                        NearbyCover = sg.Centroid,
                    };
                }
                else
                {
                    sg.Terrain = default(TerrainAnalysis.TerrainAssessment);
                }
            }
        }

        /// <summary>
        /// Compute morale (0..1) for a sub-group based on real-battle factors:
        ///   - Health ratio (casualties destroy morale)
        ///   - Kill ratio (winning boosts morale)
        ///   - Outnumbered (being swarmed drops morale)
        ///   - Flanked (encirclement is devastating to morale)
        ///   - Isolated (no support nearby drops morale)
        ///   - Height disadvantage (fighting uphill drops morale)
        ///   - Has cover (defensive position boosts morale)
        ///   - Has high ground (strong position boosts morale)
        /// </summary>
        private static float ComputeMorale(SubGroup sg)
        {
            // Base morale from health — dead men don't fight.
            float morale = sg.HealthRatio;

            // Kill ratio: winning lifts morale, losing crushes it.
            if (sg.EnemyKills > 0 && sg.OurKills > 0)
            {
                float killRatio = sg.OurKills / sg.EnemyKills;
                // killRatio of 2 = +0.2, killRatio of 0.5 = -0.2
                morale += Mathf.Clamp((killRatio - 1f) * 0.15f, -0.3f, 0.2f);
            }
            else if (sg.OurKills == 0 && sg.EnemyKills > 10)
            {
                // Taking losses without inflicting any — morale craters.
                morale -= 0.2f;
            }

            // Outnumbered — being swarmed.
            if (sg.IsOutnumbered) morale -= 0.15f;

            // Flanked — encirclement is the most devastating situation.
            if (sg.IsFlanked) morale -= 0.25f;

            // Isolated — no friendly support nearby.
            if (sg.IsIsolated) morale -= 0.1f;

            // Terrain effects.
            if (sg.Terrain.AtHeightDisadvantage) morale -= 0.1f;
            if (sg.Terrain.HasHighGround) morale += 0.1f;
            if (sg.Terrain.HasCover) morale += 0.05f;

            // Ranged units in the open lose morale faster (they're vulnerable).
            if (sg.IsRanged && !sg.Terrain.HasCover && sg.IsEngaged) morale -= 0.1f;

            // Fortification bonus — defenders in forts fight much harder.
            // Real battles: fortified positions are force multipliers.
            float fortBonus = FortificationAnalysis.GetFortificationMoraleBonus(sg.Centroid, sg.Team);
            morale += fortBonus;

            // Attacking a fortification is costly — morale penalty for attackers.
            if (FortificationAnalysis.IsInEnemyFortification(sg.Centroid, sg.Team))
            {
                morale -= 0.15f;
            }

            return Mathf.Clamp01(morale);
        }

        /// <summary>
        /// Rough flank detection: if there are enemy sub-groups on opposite
        /// sides of us within a moderate distance, we're being flanked.
        /// </summary>
        private static bool CheckFlanked(SubGroup sg)
        {
            if (_subGroups.Count < 2) return false;
            Vector3 toNearest = sg.NearestEnemyPos - sg.Centroid;
            toNearest.y = 0;
            if (toNearest.sqrMagnitude < 1f) return false;
            toNearest.Normalize();

            // Look for another enemy sub-group roughly behind us (dot < -0.3).
            foreach (var other in _subGroups)
            {
                if (other.Team == sg.Team) continue;
                if (other == null) continue;
                float d = Vector3.Distance(sg.Centroid, other.Centroid);
                if (d > 200f) continue;
                Vector3 toOther = other.Centroid - sg.Centroid;
                toOther.y = 0;
                toOther.Normalize();
                if (Vector3.Dot(toNearest, toOther) < -0.3f)
                    return true;
            }
            return false;
        }

        // ---- Step 3: Decision and action ----

        private static void DecideAndAct(SubGroup sg, int sgIndex)
        {
            FractureAction action = FractureAction.None;

            // ================================================================
            // Morale-driven decision matrix — applies to BOTH attacking and
            // holding armies. In real battles, units don't blindly follow
            // orders; they react to their local situation:
            //   - Losing units fall back or rout regardless of orders
            //   - Winning units pursue regardless of orders
            //   - Flanked units try to escape encirclement
            //   - Unengaged units reinforce allies in trouble
            // ================================================================

            // ---- SURVIVAL LAYER (highest priority — overrides everything) ----

            // 1. ROUT — morale has collapsed. Full panic retreat for both
            //    attackers and holders. This is the "every man for himself" state.
            if (MoraleEnabled && sg.Morale < RoutThreshold)
            {
                action = FractureAction.Rout;
            }
            // 2. Anti-flank — encirclement is fatal, reposition immediately.
            else if (BehaviorAntiFlank && sg.IsFlanked && sg.IsEngaged)
            {
                action = FractureAction.Reposition;
            }
            // 3. Seek cover — ranged units caught in the open under fire.
            else if (sg.IsRanged && sg.IsEngaged && !sg.Terrain.HasCover && sg.HealthRatio < 0.7f)
            {
                if (sg.Terrain.NearbyCover != sg.Centroid)
                    action = FractureAction.SeekCover;
            }

            // ---- TACTICAL RETREAT LAYER (morale-driven fallback) ----
            // Both attacking and holding armies fall back when morale is low.
            // This is the key change: attacking armies that are losing WILL
            // retreat instead of suicide-charging.
            else if (MoraleEnabled && BehaviorRetreat && sg.Morale < RetreatThreshold && sg.IsEngaged)
            {
                // Don't retreat if we're on high ground with good position —
                // the terrain advantage may let us hold even with low morale.
                if (!(sg.Terrain.HasHighGround && !sg.IsOutnumbered))
                    action = FractureAction.Retreat;
            }
            // Legacy retreat: isolated + outnumbered + low health (even without
            // morale system, this is a clear "get out" signal).
            else if (BehaviorRetreat && sg.IsIsolated && sg.IsOutnumbered && sg.HealthRatio < 0.5f)
            {
                action = FractureAction.Retreat;
            }

            // ---- TERRAIN LAYER (tactical repositioning) ----
            // Seek high ground when fighting uphill and not winning.
            else if (sg.IsEngaged && sg.Terrain.AtHeightDisadvantage && !sg.IsWinning)
            {
                if (sg.Terrain.NearbyHighGround != sg.Centroid)
                    action = FractureAction.SeekHighGround;
            }

            // ---- SUPPORT LAYER ----
            // Unengaged groups reinforce losing allies — applies to both
            // attacking and holding armies. A holding group will break to
            // save a nearby ally; an attacking group will divert to help.
            // BUT: if there's an enemy fortification nearby and we're not
            // engaged, flank it instead of reinforcing (multi-team sieges).
            else if (BehaviorReinforce && !sg.IsEngaged)
            {
                // Check for nearby enemy fortification to flank.
                var enemyFort = FortificationAnalysis.GetNearestEnemyFortification(sg.Centroid, sg.Team);
                if (enemyFort.HasValue && Vector3.Distance(sg.Centroid, enemyFort.Value.Center) < 600f)
                {
                    // Flank the fortification — approach from a distributed side.
                    action = FractureAction.FlankFortification;
                    sg.NearestEnemyPos = FortificationAnalysis.GetFlankAttackPoint(
                        sg.Centroid, enemyFort.Value, sg.GetHashCode());
                }
                else
                {
                    var losingAlly = FindLosingAlly(sg);
                    if (losingAlly != null)
                    {
                        action = FractureAction.Reinforce;
                        sg.NearestFriendlyCentroid = losingAlly.Centroid;
                    }
                }
            }

            // ---- EXPLOITATION LAYER (winning units get aggressive) ----
            // High-morale winning groups pursue retreating enemies.
            // With AggressionBoost, they also ignore hold orders to chase.
            else if (BehaviorPursue && sg.IsWinning && sg.IsEngaged && sg.NearestEnemyDist < 150f)
            {
                bool enemyRetreating = false;
                foreach (var other in _subGroups)
                {
                    if (other.Team == sg.Team) continue;
                    if (Vector3.Distance(sg.Centroid, other.Centroid) < 200f)
                    {
                        // Enemy is retreating if low health or low morale.
                        if (other.HealthRatio < 0.4f || (MoraleEnabled && other.Morale < RetreatThreshold))
                        { enemyRetreating = true; break; }
                    }
                }
                if (enemyRetreating) action = FractureAction.Pursue;
            }

            // ---- HIGH GROUND PRESERVATION ----
            // If we're on high ground and engaged, don't abandon it for anything
            // less than a survival-level threat (rout/flank/retreat/cover already
            // handled above). High ground is a strong defensive position.
            if (action != FractureAction.None &&
                action != FractureAction.Rout &&
                action != FractureAction.Reposition &&
                action != FractureAction.Retreat &&
                action != FractureAction.SeekCover &&
                sg.Terrain.HasHighGround && sg.IsEngaged)
            {
                action = FractureAction.None;
            }

            // ---- AGGRESSION BOOST ----
            // If aggression boost is on and this group has high morale + is winning,
            // force them to pursue even if they were going to hold.
            if (AggressionBoost && sg.Morale > 0.7f && sg.IsWinning && sg.IsEngaged
                && sg.NearestEnemyDist < 200f && action == FractureAction.None)
            {
                action = FractureAction.Pursue;
            }

            sg.CurrentAction = action;
            if (action == FractureAction.None) return;

            IssueFractureTarget(sg, action, sgIndex);
        }

        /// <summary>Find a same-team sub-group that's engaged and losing.</summary>
        private static SubGroup FindLosingAlly(SubGroup sg)
        {
            SubGroup best = null;
            float bestDist = float.MaxValue;
            foreach (var ally in _subGroups)
            {
                if (ally == sg || ally.Team != sg.Team) continue;
                if (!ally.IsEngaged) continue;
                if (ally.HealthRatio > ReinforceThreshold) continue;
                float d = Vector3.Distance(sg.Centroid, ally.Centroid);
                if (d < bestDist && d < 400f) { bestDist = d; best = ally; }
            }
            return best;
        }

        /// <summary>
        /// Spawn a NavGrid Target that redirects this sub-group's flow-field
        /// pathing toward the chosen destination.
        /// </summary>
        private static void IssueFractureTarget(SubGroup sg, FractureAction action, int sgIndex)
        {
            Vector3 dest;
            bool avoidEnemies;

            switch (action)
            {
                case FractureAction.Reinforce:
                    // Move toward the losing ally's centroid.
                    dest = sg.NearestFriendlyCentroid;
                    avoidEnemies = true;  // get there safely
                    break;

                case FractureAction.Retreat:
                    // Fall back toward nearest friendly sub-group centroid.
                    dest = sg.NearestFriendlyCentroid;
                    avoidEnemies = true;

                    // Retreat wavefield: wall off the forward path toward the enemy,
                    // funnel units into a clean retreat corridor, and braid the
                    // rear path so they mix backward instead of clumping.
                    if (FlowFieldModulationEnabled && sg.NearestEnemyDist < float.MaxValue)
                    {
                        FlowFieldModulator.CreateRetreatWavefield(
                            sg.Centroid, sg.NearestEnemyPos, dest, strength: 0.7f);
                    }
                    break;

                case FractureAction.Pursue:
                    // Chase toward the nearest enemy position.
                    dest = sg.NearestEnemyPos;
                    avoidEnemies = false; // attack-move
                    break;

                case FractureAction.Reposition:
                    // Move perpendicular to the nearest enemy to escape encirclement,
                    // toward the direction of friendly forces.
                    Vector3 toEnemy = (sg.NearestEnemyPos - sg.Centroid).normalized;
                    Vector3 perp = new Vector3(-toEnemy.z, 0, toEnemy.x).normalized;
                    // Bias the perpendicular direction toward friendlies.
                    Vector3 toFriend = sg.NearestFriendlyCentroid - sg.Centroid;
                    if (toFriend.sqrMagnitude > 1f)
                    {
                        toFriend.Normalize();
                        if (Vector3.Dot(perp, toFriend) < 0) perp = -perp;
                    }
                    dest = sg.Centroid + perp * 100f;
                    avoidEnemies = true;
                    break;

                case FractureAction.SeekHighGround:
                    // Move to the nearby high-ground position found by terrain analysis.
                    dest = sg.Terrain.NearbyHighGround;
                    avoidEnemies = true; // reposition, don't charge through enemies
                    break;

                case FractureAction.SeekCover:
                    // Move to nearby cover from enemy LOS.
                    dest = sg.Terrain.NearbyCover;
                    avoidEnemies = true;
                    break;

                case FractureAction.Rout:
                    // Full panic retreat — run directly away from the nearest enemy,
                    // toward friendly forces if any exist. This is NOT a tactical
                    // retreat; it's a rout, so the destination is far from the enemy.
                    {
                        Vector3 awayFromEnemy = (sg.Centroid - sg.NearestEnemyPos).normalized;
                        awayFromEnemy.y = 0;
                        // Run far — 300 units away from the enemy.
                        dest = sg.Centroid + awayFromEnemy * 300f;
                        // If there's a friendly sub-group, bias toward them.
                        if (sg.NearestFriendlyDist < float.MaxValue)
                        {
                            Vector3 toFriendRout = (sg.NearestFriendlyCentroid - sg.Centroid).normalized;
                            toFriendRout.y = 0;
                            // Blend: 70% away from enemy, 30% toward friendlies.
                            dest = sg.Centroid + (awayFromEnemy * 0.7f + toFriendRout * 0.3f).normalized * 300f;
                        }
                        avoidEnemies = true;

                        // Rout wavefield: stronger than retreat — hard forward wall
                        // to prevent any forward drift, plus funnels to drive units
                        // backward in panic. The braided rear corridor keeps the
                        // rout from becoming a single-file stampede.
                        if (FlowFieldModulationEnabled && sg.NearestEnemyDist < float.MaxValue)
                        {
                            FlowFieldModulator.CreateRetreatWavefield(
                                sg.Centroid, sg.NearestEnemyPos, dest, strength: 0.9f);
                        }
                    }
                    break;

                case FractureAction.Regroup:
                    // Rallied units reform at the nearest friendly sub-group.
                    dest = sg.NearestFriendlyCentroid;
                    avoidEnemies = true;

                    // Light retreat wavefield — regrouping units are still shaky,
                    // so keep a soft forward wall to prevent them from re-engaging
                    // prematurely before they've reformed.
                    if (FlowFieldModulationEnabled && sg.NearestEnemyDist < float.MaxValue)
                    {
                        FlowFieldModulator.CreateRetreatWavefield(
                            sg.Centroid, sg.NearestEnemyPos, dest, strength: 0.4f);
                    }
                    break;

                case FractureAction.FlankFortification:
                    // Approach the enemy fortification from a distributed flank point.
                    // NearestEnemyPos was set to the flank point by DecideAndAct.
                    dest = sg.NearestEnemyPos;
                    avoidEnemies = false; // attack-move toward the fort

                    // Block the direct path to the fort center so the flow field
                    // routes units to the flank point instead of beelining.
                    {
                        var fort = FortificationAnalysis.GetNearestEnemyFortification(sg.Centroid, sg.Team);
                        if (fort.HasValue)
                        {
                            FlowFieldModulator.BlockDirectPath(sg.Centroid, fort.Value.Center,
                                wallWidth: 80f, strength: 0.7f);
                        }
                    }
                    break;

                default:
                    return;
            }

            // Find the team list index for NavGrid.AddTarget (which uses list index as Team).
            int teamListIndex = -1;
            for (int t = 0; t < ThreadManager.Teams.Count; t++)
            {
                if (ThreadManager.Teams[t].TeamNumber == sg.Team) { teamListIndex = t; break; }
            }
            if (teamListIndex < 0) return;

            // ---- Wide front formation + braided mixing ----
            // The old approach placed multiple parallel targets, creating N
            // parallel columns (deep, narrow). Real armies march in wide,
            // shallow lines perpendicular to the direction of travel.
            //
            // New approach:
            // 1. ONE target with a very wide formation length (wide front)
            // 2. Braided diagonal soft obstacles along the path that force
            //    units to weave side-to-side as they advance, mixing them
            //    into a broad front instead of parallel channels.

            float dispersion = DispersionFactor;
            int baseFormation = Mathf.Max(1, (int)Mathf.Sqrt(sg.TotalRemaining * 0.5f));

            if (dispersion <= 0f)
            {
                // Vanilla: single target, game-default formation width.
                IssueSingleTarget(dest, sg, teamListIndex, action, sgIndex, avoidEnemies,
                    formationLength: baseFormation);
                return;
            }

            // Movement direction (from current position to destination).
            Vector3 moveDir = (dest - sg.Centroid).normalized;
            moveDir.y = 0;
            if (moveDir.sqrMagnitude < 0.01f) moveDir = Vector3.forward;
            // Perpendicular axis (formation right vector = across the front).
            Vector3 right = new Vector3(-moveDir.z, 0, moveDir.x).normalized;

            // Wide formation length = broader GPU dispatch = wider front.
            // The GPU dispatches `1 + FormationLength/16` threads in X,
            // so a larger value directly widens the formation search.
            // We want the front to be WIDE (perpendicular to travel) and
            // SHALLOW (not deep in the travel direction).
            int wideFormation = Mathf.Max(baseFormation,
                (int)(baseFormation * (1f + dispersion * 4f)));

            // Issue a SINGLE target with the wide formation.
            // This creates one broad attractor, not parallel channels.
            IssueSingleTarget(dest, sg, teamListIndex, action, sgIndex, avoidEnemies,
                formationLength: wideFormation);

            // ---- Braided mixing obstacles ----
            // Place diagonal soft obstacles along the path in a zigzag pattern.
            // These force the flow field to weave, causing units to shift
            // laterally as they advance. The result is a braided/mixed front
            // instead of a rigid line or parallel columns.
            //
            // The zigzag pattern: alternating diagonal walls that block half
            // the path width, forcing units to shift left then right.
            if (FlowFieldModulationEnabled && dispersion > 0.2f)
            {
                float pathLength = Vector3.Distance(sg.Centroid, dest);
                if (pathLength > 50f)
                {
                    float frontWidth = DispersionWidth * dispersion *
                        Mathf.Clamp01(Mathf.Sqrt(sg.TotalRemaining) / 30f);
                    // Number of zigzag segments along the path.
                    int numZags = Mathf.Clamp(
                        Mathf.RoundToInt(pathLength / 80f * dispersion),
                        2, 8);
                    // Each zigzag blocks half the front, alternating sides.
                    // The diagonal angle forces units to weave.
                    for (int z = 0; z < numZags; z++)
                    {
                        // Position along the path (0 = start, 1 = dest).
                        float t = (float)(z + 1) / (numZags + 1);
                        Vector3 along = sg.Centroid + (dest - sg.Centroid) * t;

                        // Alternate which side is blocked.
                        bool blockRight = (z % 2 == 0);
                        Vector3 blockOffset = (blockRight ? right : -right) * frontWidth * 0.25f;
                        Vector3 blockCenter = along + blockOffset;

                        // Diagonal wall: extends from the blocked side toward
                        // the center, at an angle that forces lateral shift.
                        // Width = half the front, depth = shallow (just a nudge).
                        FlowFieldModulator.AddBlocker(
                            blockCenter,
                            frontWidth * 0.5f,  // wide (across front)
                            12f,                // shallow (along path)
                            0.3f * dispersion   // soft — just enough to redirect
                        );
                    }
                }
            }
        }

        /// <summary>Issue a single NavGrid target at the given position.</summary>
        private static void IssueSingleTarget(Vector3 dest, SubGroup sg, int teamListIndex,
            FractureAction action, int sgIndex, bool avoidEnemies, int formationLength,
            string suffix = "")
        {
            var go = new GameObject($"PathingMod_Fracture_SG{sgIndex}_{action}{suffix}");
            go.transform.position = dest;
            var target = go.AddComponent<Target>();
            target.Team = teamListIndex;
            target.TargetsTeam = sg.Team;
            target.RangeSearchAmount = 2000;
            target.targetSearchAmount = 800;
            target.FormationLength = formationLength;
            target.AvoidEnemies = avoidEnemies;
            target.Active = true;
            target.TestCamera = false;
            NavGrid.AddTarget(target);
            _fractureTargets.Add(target);
        }

        // ---- Stats for UI ----

        /// <summary>Snapshot of sub-group states for the debug panel.</summary>
        internal struct SubGroupStat
        {
            public int Team;
            public int ArmyCount;
            public int Remaining;
            public float HealthRatio;
            public float Morale;
            public FractureAction Action;
            public bool Engaged;
            public bool Outnumbered;
            public bool Flanked;
            public bool Ranged;
            public bool Holding;
            public float HeightAdv;
            public bool HasCover;
        }

        internal static List<SubGroupStat> GetStats()
        {
            var list = new List<SubGroupStat>();
            foreach (var sg in _subGroups)
            {
                list.Add(new SubGroupStat
                {
                    Team = sg.Team,
                    ArmyCount = sg.Armies.Count,
                    Remaining = sg.TotalRemaining,
                    HealthRatio = sg.HealthRatio,
                    Morale = sg.Morale,
                    Action = sg.CurrentAction,
                    Engaged = sg.IsEngaged,
                    Outnumbered = sg.IsOutnumbered,
                    Flanked = sg.IsFlanked,
                    Ranged = sg.IsRanged,
                    Holding = sg.IsHolding,
                    HeightAdv = sg.Terrain.HeightAdvantage,
                    HasCover = sg.Terrain.HasCover,
                });
            }
            return list;
        }
    }
}
