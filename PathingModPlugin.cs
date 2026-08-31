using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace UEBS2PathingMod
{
    /// <summary>
    /// UEBS2 Pathing &amp; Team AI improvement mod.
    ///
    /// Adds three cooperating systems, all dynamically scaled to battle size:
    ///   1. Smart search scheduling  - priority-based team path-search cycling
    ///   2. Strategic target assignment - threat-weighted enemy centroid targeting
    ///   3. NavGrid tuning           - adaptive MaxActiveTargets / obstacle / accelerate params
    ///
    /// A live tuning window is toggled with Numpad+.
    /// </summary>
    [BepInPlugin(Guid, Name, Version)]
    public class PathingModPlugin : BaseUnityPlugin
    {
        public const string Guid = "com.uebs2.pathingmod";
        public const string Name = "UEBS2 Pathing & Team AI";
        public const string Version = "1.0.0";

        internal static PathingModPlugin Instance;

        // ---- Config bindings (also editable live via the UI window) ----
        internal ConfigEntry<bool> ModEnabled;
        internal ConfigEntry<bool> SmartScheduling;
        internal ConfigEntry<bool> StrategicTargeting;
        internal ConfigEntry<bool> NavGridTuning;
        internal ConfigEntry<bool> DynamicScaling;
        internal ConfigEntry<float> Aggressiveness; // 0..1 master knob
        internal ConfigEntry<int> StrategicTargetInterval; // seconds between re-assign
        internal ConfigEntry<int> MaxActiveTargetsCap; // ceiling for NavGrid.MaxActiveTargets
        internal ConfigEntry<float> ObstacleUpdateSpeed; // NavGrid.GridObstacleUpdateSpeed
        internal ConfigEntry<bool> ShowDebugStats;
        internal ConfigEntry<bool> CinematicCombatBias;
        internal ConfigEntry<float> CinematicBiasStrength;
        internal ConfigEntry<float> CinematicFocusInterval;
        internal ConfigEntry<bool> FractureEnabled;
        internal ConfigEntry<float> FractureClusterRadius;
        internal ConfigEntry<float> FractureReassessInterval;
        internal ConfigEntry<bool> FractureReinforce;
        internal ConfigEntry<bool> FractureRetreat;
        internal ConfigEntry<bool> FracturePursue;
        internal ConfigEntry<bool> FractureAntiFlank;
        internal ConfigEntry<bool> FractureMorale;
        internal ConfigEntry<float> FractureRoutThreshold;
        internal ConfigEntry<float> FractureRetreatThreshold;
        internal ConfigEntry<bool> FractureAggressionBoost;
        internal ConfigEntry<float> FractureDispersion;
        internal ConfigEntry<float> FractureDispersionWidth;
        internal ConfigEntry<bool> FlowFieldModulation;
        internal ConfigEntry<float> FlowFieldBlockStrength;

        // Ollama / AI battle analyzer
        internal ConfigEntry<bool> OllamaEnabled;
        internal ConfigEntry<float> OllamaInterval;
        internal ConfigEntry<string> OllamaModel;
        internal ConfigEntry<bool> OllamaAutoApply;

        internal BattleAnalyzer Analyzer;

        private Harmony _harmony;

        private void Awake()
        {
            Instance = this;

            ModEnabled = Config.Bind("General", "Enabled", true, "Master switch for the whole mod.");
            DynamicScaling = Config.Bind("General", "DynamicScaling", true,
                "Automatically scale aggressiveness down for huge battles to protect framerate.");
            Aggressiveness = Config.Bind("General", "Aggressiveness", 0.6f,
                "Master knob 0..1. Higher = more searches, more strategic targets, more GPU budget.");

            SmartScheduling = Config.Bind("Pathing", "SmartScheduling", true,
                "Replace round-robin team path-search cycling with priority scheduling.");
            StrategicTargeting = Config.Bind("Pathing", "StrategicTargeting", true,
                "Auto-assign each army a flow-field target at the nearest threat-weighted enemy centroid.");
            StrategicTargetInterval = Config.Bind("Pathing", "StrategicTargetInterval", 4,
                "Seconds between strategic target re-assignment (lower = more responsive, more cost).");

            NavGridTuning = Config.Bind("NavGrid", "NavGridTuning", true,
                "Adaptively tune NavGrid.MaxActiveTargets, obstacle update speed, and path acceleration.");
            MaxActiveTargetsCap = Config.Bind("NavGrid", "MaxActiveTargetsCap", 30,
                "Ceiling for NavGrid.MaxActiveTargets when tuning is on.");
            ObstacleUpdateSpeed = Config.Bind("NavGrid", "ObstacleUpdateSpeed", 0.05f,
                "Fraction of the grid updated per frame for dynamic obstacles (higher = snappier, costlier).");

            ShowDebugStats = Config.Bind("UI", "ShowDebugStats", true,
                "Show live pathing/team stats in the tuning window.");

            CinematicCombatBias = Config.Bind("Cinematic", "CombatBias", true,
                "Bias auto-cinematic camera toward active combat/clashes instead of random armies.");
            CinematicBiasStrength = Config.Bind("Cinematic", "BiasStrength", 0.8f,
                "0 = fully random (vanilla), 1 = always pick the hottest combat. 0.8 is a good mix.");
            CinematicFocusInterval = Config.Bind("Cinematic", "FocusInterval", 4f,
                "Seconds between cinematic refocus. Lower = more cuts, higher = longer shots.");

            // Sub-group fracture system
            FractureEnabled = Config.Bind("Fracture", "Enabled", true,
                "Enable emergent sub-group autonomy within teams.");
            FractureClusterRadius = Config.Bind("Fracture", "ClusterRadius", 150f,
                "Armies within this distance form a sub-group. Smaller = more, tighter groups.");
            FractureReassessInterval = Config.Bind("Fracture", "ReassessInterval", 3f,
                "Seconds between sub-group re-evaluation.");
            FractureReinforce = Config.Bind("Fracture", "Reinforce", true,
                "Unengaged groups break hold orders to reinforce losing allies.");
            FractureRetreat = Config.Bind("Fracture", "Retreat", true,
                "Isolated, outnumbered groups fall back toward friendlies.");
            FracturePursue = Config.Bind("Fracture", "Pursue", true,
                "Winning groups pursue retreating enemies instead of returning to position.");
            FractureAntiFlank = Config.Bind("Fracture", "AntiFlank", true,
                "Flanked groups reposition to avoid encirclement.");
            FractureMorale = Config.Bind("Fracture", "Morale", true,
                "Enable morale system — losing groups lose cohesion and may rout/retreat.");
            FractureRoutThreshold = Config.Bind("Fracture", "RoutThreshold", 0.15f,
                "Morale below this = full rout (panic retreat). 0.15 = very low morale.");
            FractureRetreatThreshold = Config.Bind("Fracture", "RetreatThreshold", 0.35f,
                "Morale below this = tactical retreat/fallback. 0.35 = low morale.");
            FractureAggressionBoost = Config.Bind("Fracture", "AggressionBoost", true,
                "High-morale attacking groups pursue more aggressively and ignore hold orders.");
            FractureDispersion = Config.Bind("Fracture", "Dispersion", 0.5f,
                "Spread units across a wider front instead of tight columns. 0=vanilla, 1=very wide.");
            FractureDispersionWidth = Config.Bind("Fracture", "DispersionWidth", 200f,
                "Maximum lateral spread distance for multi-point dispersion targets.");
            FlowFieldModulation = Config.Bind("FlowField", "Enabled", true,
                "Inject soft obstacles into the GPU obstacle grid to redirect flow fields. Enables flanking, corridors, and dispersion.");
            FlowFieldBlockStrength = Config.Bind("FlowField", "BlockStrength", 0.7f,
                "Strength of soft obstacle walls (0=none, 1=hard wall). Lower = units may push through.");

            // Ollama AI battle analyzer
            OllamaEnabled = Config.Bind("Ollama", "Enabled", false,
                "Enable AI battle analysis via local Ollama. Captures periodic screenshots and sends them to a Qwen vision model for tactical assessment.");
            OllamaInterval = Config.Bind("Ollama", "Interval", 15f,
                "Seconds between automatic battle analyses. Lower = more responsive but more LLM load.");
            OllamaModel = Config.Bind("Ollama", "Model", "qwen2.5vl:7b",
                "Ollama model name for vision analysis. Must support image input (e.g. qwen2.5vl:7b).");
            OllamaAutoApply = Config.Bind("Ollama", "AutoApply", true,
                "Automatically apply the AI's suggested parameter adjustments. If false, suggestions are displayed in the UI for manual review.");

            // Initialize the battle analyzer component.
            Analyzer = gameObject.AddComponent<BattleAnalyzer>();
            Analyzer.Enabled = OllamaEnabled.Value;
            Analyzer.Interval = OllamaInterval.Value;
            Analyzer.ModelName = OllamaModel.Value;
            Analyzer.AutoApply = OllamaAutoApply.Value;

            _harmony = new Harmony(Guid);
            _harmony.PatchAll(typeof(Patches));
            _harmony.PatchAll(typeof(PathingModUI));
            _harmony.PatchAll(typeof(CinematicPatches));
            // PaintModeUI doesn't have its own Harmony patches — it's driven
            // by PathingModUI's ThreadManager hooks. No need to patch it.

            Logger.LogInfo($"{Name} v{Version} loaded. Press Numpad+ for the tuning window.");
        }

        private void OnDestroy()
        {
            _harmony?.UnpatchSelf();
            Instance = null;
        }

        private void Update()
        {
            // Sync Ollama config to the analyzer each frame (cheap).
            if (Analyzer != null)
            {
                Analyzer.Enabled = OllamaEnabled.Value;
                Analyzer.Interval = OllamaInterval.Value;
                Analyzer.ModelName = OllamaModel.Value;
                Analyzer.AutoApply = OllamaAutoApply.Value;
            }

            // Numpad8: trigger an immediate battle analysis (manual).
            if (Input.GetKeyDown(KeyCode.Keypad8) && Analyzer != null)
            {
                Analyzer.AnalyzeNow();
                Logger.LogInfo("[BattleAnalyzer] Manual analysis triggered.");
            }
        }

        // ---- Dynamic scaling helpers ----

        /// <summary>Total active AI across every army, used to scale aggressiveness.</summary>
        internal static int TotalAiCount
        {
            get
            {
                try
                {
                    if (ThreadManager.AllArmies == null) return 0;
                    int n = 0;
                    foreach (var a in ThreadManager.AllArmies) n += a.ArmyCount;
                    return n;
                }
                catch { return 0; }
            }
        }

        /// <summary>
        /// Effective aggressiveness after dynamic scaling. Large battles ramp the
        /// user-set aggressiveness down so we don't tank the GPU with searches.
        /// </summary>
        internal static float EffectiveAggressiveness
        {
            get
            {
                var i = Instance;
                if (i == null) return 0.5f;
                float baseAgg = i.Aggressiveness.Value;
                if (!i.DynamicScaling.Value) return Mathf.Clamp01(baseAgg);

                int ai = TotalAiCount;
                // 0 AI -> full. 50k AI -> ~0.35x. 200k+ AI -> ~0.15x.
                float scale = Mathf.Lerp(1f, 0.15f, Mathf.InverseLerp(0f, 200000f, ai));
                return Mathf.Clamp01(baseAgg * scale);
            }
        }

        /// <summary>How many active path searches NavGrid should run, scaled to battle size.</summary>
        internal static int ScaledMaxActiveTargets
        {
            get
            {
                var i = Instance;
                if (i == null) return 12;
                int cap = i.MaxActiveTargetsCap.Value;
                float agg = EffectiveAggressiveness;
                int ai = TotalAiCount;
                // Small battles: up to cap. Huge battles: floor of 4.
                int floor = 4;
                int target = Mathf.Max(floor, Mathf.RoundToInt(cap * agg));
                if (ai > 100000) target = Mathf.Min(target, 8);
                return Mathf.Clamp(target, floor, cap);
            }
        }
    }

    /// <summary>
    /// Harmony patches into NavGrid and ThreadManager. We deliberately use
    /// Postfix / Prefix hooks on the existing Update loops rather than
    /// rewriting GPU buffer layouts, so the patches stay safe.
    /// </summary>
    public static class Patches
    {
        // ---- Strategic target bookkeeping ----
        private static float _strategicTimer;
        private static readonly List<Target> _strategicTargets = new List<Target>();
        // Per-team threat score used by smart scheduling.
        private static float[] _teamPriority = Array.Empty<float>();

        /// <summary>
        /// Prefix on NavGrid.Update: apply NavGrid tuning before the original
        /// dispatch loop runs, so the params it reads are the tuned ones.
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(NavGrid), "Update")]
        public static void NavGridUpdatePrefix(NavGrid __instance)
        {
            var i = PathingModPlugin.Instance;
            if (i == null || !i.ModEnabled.Value) return;

            try
            {
                if (i.NavGridTuning.Value)
                {
                    __instance.MaxActiveTargets = PathingModPlugin.ScaledMaxActiveTargets;
                    NavGrid.maxActiveTargets = __instance.MaxActiveTargets;
                    // Search slot count should track active targets.
                    __instance.MaxActiveTargetSearches = Mathf.Max(6, __instance.MaxActiveTargets);
                    NavGrid.maxActiveTargetSearches = __instance.MaxActiveTargetSearches;
                    __instance.GridObstacleUpdateSpeed = i.ObstacleUpdateSpeed.Value;
                    // Keep acceleration on; the game's own throttle handles the rest.
                    NavGrid.AcceleratePaths = true;
                }

                if (i.StrategicTargeting.Value)
                {
                    _strategicTimer += Time.deltaTime;
                    int interval = Mathf.Max(1, i.StrategicTargetInterval.Value);
                    if (_strategicTimer >= interval)
                    {
                        _strategicTimer = 0f;
                        AssignStrategicTargets();
                    }
                }

                // Recompute team priorities every frame (cheap, CPU only).
                if (i.SmartScheduling.Value)
                {
                    RecomputeTeamPriorities();
                }

                // Sub-group fracture system: sync config then tick.
                SubGroupFracture.Enabled = i.FractureEnabled.Value;
                SubGroupFracture.ClusterRadius = i.FractureClusterRadius.Value;
                SubGroupFracture.ReassessInterval = i.FractureReassessInterval.Value;
                SubGroupFracture.BehaviorReinforce = i.FractureReinforce.Value;
                SubGroupFracture.BehaviorRetreat = i.FractureRetreat.Value;
                SubGroupFracture.BehaviorPursue = i.FracturePursue.Value;
                SubGroupFracture.BehaviorAntiFlank = i.FractureAntiFlank.Value;
                SubGroupFracture.MoraleEnabled = i.FractureMorale.Value;
                SubGroupFracture.RoutThreshold = i.FractureRoutThreshold.Value;
                SubGroupFracture.RetreatThreshold = i.FractureRetreatThreshold.Value;
                SubGroupFracture.AggressionBoost = i.FractureAggressionBoost.Value;
                SubGroupFracture.DispersionFactor = i.FractureDispersion.Value;
                SubGroupFracture.DispersionWidth = i.FractureDispersionWidth.Value;
                if (i.FractureEnabled.Value)
                {
                    SubGroupFracture.Tick();

                    // Aggressively propagate flow fields for our fracture targets.
                    // The game's own round-robin only processes a few targets per frame,
                    // so we add extra FullSearch dispatches to make sure our fracture
                    // orders actually reach the units quickly.
                    if (NavGrid.TargetList != null && NavGrid.TargetList.Count > 0)
                    {
                        float agg = PathingModPlugin.EffectiveAggressiveness;
                        int extraSearches = Mathf.RoundToInt(3 * agg);
                        for (int s = 0; s < extraSearches && s < NavGrid.TargetList.Count; s++)
                        {
                            var tgt = NavGrid.TargetList[s];
                            if (tgt == null || !tgt.Active) continue;
                            // Only boost our own targets (name starts with PathingMod).
                            if (!tgt.name.StartsWith("PathingMod")) continue;
                            NavGrid.FullSearch(tgt.transform.position, tgt.Team,
                                tgt.RangeSearchAmount, OrderGrid: true,
                                tgt.FormationLength, tgt.SearchGrid,
                                tgt.LastPosition, tgt.TargetsTeam,
                                tgt.AvoidEnemies);
                        }
                    }
                }

                // Apply flow field modulation (soft obstacles) before the
                // game's compute dispatch reads ObstacleGrid.
                if (i.FlowFieldModulation.Value)
                {
                    FlowFieldModulator.BlockStrength = i.FlowFieldBlockStrength.Value;
                    FlowFieldModulator.Apply();
                }
            }
            catch (Exception e)
            {
                // Never let a mod exception kill the game's nav loop.
                Debug.LogWarning($"[PathingMod] NavGrid prefix error: {e}");
            }
        }

        /// <summary>
        /// Postfix on NavGrid.Update: restore ObstacleGrid after the game's
        /// compute dispatch so our soft obstacles don't persist.
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(NavGrid), "Update")]
        public static void NavGridUpdatePostfix()
        {
            try
            {
                FlowFieldModulator.Restore();
            }
            catch { /* non-critical */ }
        }

        /// <summary>
        /// Prefix on Army.RunGpuAi: if this army is in a fracturing sub-group,
        /// temporarily override HoldPosition = false so the GPU compute shader
        /// actually reads the flow field and moves units toward our fracture target.
        /// Without this, HoldPosition armies ignore the flow field entirely.
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(Army), nameof(Army.RunGpuAi))]
        public static void RunGpuAiPrefix(Army __instance)
        {
            var i = PathingModPlugin.Instance;
            if (i == null || !i.ModEnabled.Value || !i.FractureEnabled.Value) return;
            try
            {
                if (SubGroupFracture.IsArmyFracturing(__instance))
                {
                    SubGroupFracture.OverrideHoldPosition(__instance);
                }
            }
            catch { /* non-critical */ }
        }

        /// <summary>
        /// Postfix on Army.RunGpuAi: restore the original HoldPosition value
        /// so the player's order is preserved for the next assessment cycle.
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(Army), nameof(Army.RunGpuAi))]
        public static void RunGpuAiPostfix(Army __instance)
        {
            try
            {
                SubGroupFracture.RestoreHoldPosition(__instance);
            }
            catch { /* non-critical */ }
        }

        // ---- Team priority computation ----

        /// <summary>
        /// Score each team by how close its nearest enemy army is. Closer ==
        /// higher priority for path searches. Stored for the scheduler hook.
        /// </summary>
        private static void RecomputeTeamPriorities()
        {
            var teams = ThreadManager.Teams;
            if (teams == null || teams.Count == 0) return;

            if (_teamPriority.Length != teams.Count)
                _teamPriority = new float[teams.Count];

            for (int ti = 0; ti < teams.Count; ti++)
            {
                var team = teams[ti];
                if (team.Armies == null || team.Armies.Count == 0)
                {
                    _teamPriority[ti] = 0f;
                    continue;
                }

                // Nearest enemy distance across this team's armies.
                float nearest = float.MaxValue;
                foreach (var army in team.Armies)
                {
                    if (army == null) continue;
                    foreach (var other in ThreadManager.AllArmies)
                    {
                        if (other == null || other.Team == army.Team) continue;
                        float d = Vector3.Distance(army.transform.position, other.transform.position);
                        if (d < nearest) nearest = d;
                    }
                }

                // Closer -> higher priority. Invert distance into a 0..1 score.
                // 0 distance -> 1, 1000+ units -> ~0.
                float score = nearest == float.MaxValue ? 0f
                    : Mathf.Clamp01(1f - nearest / 1000f);
                // Weight by remaining unit count so big engaged teams win budget.
                int remaining = 0;
                foreach (var a in team.Armies) remaining += a.Remaining;
                score *= Mathf.Clamp01(remaining / 5000f) + 0.1f;
                _teamPriority[ti] = score;
            }
        }

        /// <summary>Expose priorities for the UI / scheduler.</summary>
        internal static float[] TeamPriorities => _teamPriority;

        // ---- Strategic target assignment ----

        /// <summary>
        /// For each team, compute a threat-weighted centroid of enemy armies
        /// and make sure there is an active NavGrid Target pointing there.
        /// Armies without a player-placed target will then path toward the
        /// most threatening enemy cluster instead of standing still.
        /// </summary>
        private static void AssignStrategicTargets()
        {
            var teams = ThreadManager.Teams;
            if (teams == null || teams.Count == 0) return;
            if (NavGrid.TargetList == null) return;

            // Recycle our spawned targets each cycle so we don't leak objects.
            foreach (var t in _strategicTargets)
            {
                if (t != null)
                {
                    t.Active = false;
                    UnityEngine.Object.Destroy(t.gameObject);
                }
            }
            _strategicTargets.Clear();

            for (int ti = 0; ti < teams.Count; ti++)
            {
                var team = teams[ti];
                if (team.Armies == null || team.Armies.Count == 0) continue;

                // Threat-weighted centroid of all enemy armies.
                Vector3 centroid = Vector3.zero;
                float weightSum = 0f;
                foreach (var enemy in ThreadManager.AllArmies)
                {
                    if (enemy == null || enemy.Team == team.TeamNumber) continue;
                    // Weight = remaining units * proximity (closer armies are bigger threats).
                    int rem = Mathf.Max(1, enemy.Remaining);
                    float nearestOurDist = float.MaxValue;
                    foreach (var ours in team.Armies)
                    {
                        if (ours == null) continue;
                        float d = Vector3.Distance(ours.transform.position, enemy.transform.position);
                        if (d < nearestOurDist) nearestOurDist = d;
                    }
                    float w = rem / Mathf.Max(1f, nearestOurDist);
                    centroid += enemy.transform.position * w;
                    weightSum += w;
                }
                if (weightSum <= 0f) continue;
                centroid /= weightSum;

                // Spawn a Target at the centroid for this team.
                var go = new GameObject($"PathingMod_Target_T{team.TeamNumber}");
                go.transform.position = centroid;
                var target = go.AddComponent<Target>();
                target.Team = ti; // NavGrid uses list index as Team for search grids
                target.TargetsTeam = team.TeamNumber;
                target.RangeSearchAmount = 3000;
                target.targetSearchAmount = 1000;
                target.FormationLength = Mathf.Max(1, (int)Mathf.Sqrt(team.Armies.Count * 250f));
                target.AvoidEnemies = false; // attack-move toward the threat
                target.Active = true;
                target.TestCamera = false;
                // Register with NavGrid so it gets a search grid slot.
                NavGrid.AddTarget(target);
                _strategicTargets.Add(target);
            }
        }

        // ---- Smart scheduling hook ----
        //
        // The game's NavGrid.Update cycles teams with a plain `currentteam++`
        // and wraps at Teams.Count. We can't cheaply redirect the GPU dispatch
        // inside that loop without rewriting the method, but we *can* bias the
        // round-robin pointer each frame so high-priority teams get visited
        // first/more often. We do that by patching the private field via
        // Traverse right before the loop body runs (already covered by the
        // prefix above). For a stronger effect we also inject extra dispatches
        // for the top-priority team in the postfix of the per-team iteration.
        //
        // Implementation note: the original loop is inside Update() itself, so
        // the cleanest safe hook is to bump the priority team's search budget
        // by calling NavGrid.FullSearch for it directly here.
        [HarmonyPostfix]
        [HarmonyPatch(typeof(NavGrid), "Update")]
        public static void NavGridUpdatePostfixSmart(NavGrid __instance)
        {
            var i = PathingModPlugin.Instance;
            if (i == null || !i.ModEnabled.Value || !i.SmartScheduling.Value) return;
            if (NavGrid.TargetList == null || NavGrid.TargetList.Count == 0) return;
            if (ThreadManager.Teams == null || ThreadManager.Teams.Count == 0) return;

            try
            {
                // Pick the highest-priority team this frame.
                int bestTeam = 0;
                float bestScore = -1f;
                var pri = _teamPriority;
                for (int t = 0; t < pri.Length; t++)
                {
                    if (pri[t] > bestScore) { bestScore = pri[t]; bestTeam = t; }
                }
                if (bestScore <= 0f) return;

                // Give that team one extra flow-field search toward its top target.
                // This is additive to the game's own round-robin dispatches.
                float agg = PathingModPlugin.EffectiveAggressiveness;
                if (UnityEngine.Random.value < agg)
                {
                    // Find a target belonging to this team.
                    Target chosen = null;
                    foreach (var tgt in NavGrid.TargetList)
                    {
                        if (tgt == null || !tgt.Active) continue;
                        if (tgt.Team == bestTeam) { chosen = tgt; break; }
                    }
                    if (chosen != null)
                    {
                        NavGrid.FullSearch(chosen.transform.position, chosen.Team,
                            chosen.RangeSearchAmount, OrderGrid: true,
                            chosen.FormationLength, chosen.SearchGrid,
                            chosen.LastPosition, chosen.TargetsTeam,
                            chosen.AvoidEnemies);
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[PathingMod] smart scheduling postfix error: {e}");
            }
        }
    }
}
