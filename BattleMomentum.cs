using System.Collections.Generic;
using UnityEngine;

namespace UEBS2PathingMod
{
    /// <summary>
    /// Battle momentum and ebb-and-flow system.
    ///
    /// Real battles don't grind at constant intensity — they pulse. One side
    /// surges forward, stalls as exhaustion sets in, pauses to consolidate,
    /// then pushes again. The other side falls back, regroups, and counter-
    /// attacks when the attacker's momentum fades.
    ///
    /// This system tracks per-sub-group momentum and assigns each group a
    /// "wave phase" that modulates its behavior:
    ///
    ///   SURGE     — high momentum, aggressive advance/attack
    ///   ENGAGED   — momentum draining, still fighting but slowing
    ///   CONSOLIDATE — momentum depleted, tactical pause to recover
    ///   RECOVERING — momentum rebuilding, holding position, regrouping
    ///   READY     — momentum restored, about to surge again
    ///
    /// The cycle: READY → SURGE → ENGAGED → CONSOLIDATE → RECOVERING → READY
    ///
    /// This creates the natural "ebb and flow" of battle without scripting
    /// specific events — it emerges from each sub-group's local situation.
    /// </summary>
    public static class BattleMomentum
    {
        // ---- Config (synced from plugin config) ----
        internal static bool Enabled = true;
        internal static float ExhaustionRate = 0.08f;   // base momentum lost per second while engaged
        internal static float RecoveryRate = 0.05f;     // base momentum regained per second while consolidating
        internal static float SurgeThreshold = 0.7f;    // momentum to trigger SURGE from READY
        internal static float ConsolidateThreshold = 0.2f; // momentum to trigger CONSOLIDATE from ENGAGED
        internal static float ReadyThreshold = 0.6f;    // momentum to trigger READY from RECOVERING
        internal static float KillMomentumGain = 0.02f; // momentum gained per kill (relative)
        internal static float LossMomentumDrain = 0.03f; // momentum lost per death (relative)
        internal static float AdvanceMomentumGain = 0.01f; // momentum gained while advancing toward enemy
        internal static bool WavePoolingEnabled = true;  // cycle NavGrid targets in waves

        // ---- Envelope shaping parameters ----
        // These control the non-linear momentum curve to match real battle dynamics:
        //   Initial contact → spike (adrenaline)
        //   Sustained fighting → plateau (momentum holds before decaying)
        //   Exhaustion → decay (accelerating drain after plateau)
        //   Pause → trough (bottoms out, slow recovery begins)
        //   Counter-push → new spike (sharp jump when recovered enough)
        //   Collapse → sharp drop (cliff drop on heavy losses)
        //   Regroup → slow rise (S-curve recovery)
        internal static float ContactSpike = 0.15f;       // instant momentum jump on first contact
        internal static float PlateauDuration = 8f;       // seconds momentum holds before decay starts
        internal static float PlateauHold = 0.6f;         // momentum level the plateau holds at
        internal static float DecayAcceleration = 1.5f;   // how much exhaustion accelerates after plateau
        internal static float CollapseThreshold = 0.08f;  // loss fraction in one cycle that triggers collapse
        internal static float CollapseDrop = 0.3f;        // momentum cliff drop on collapse
        internal static float SCurveSteepness = 2f;       // steepness of regroup S-curve recovery

        // ---- Wave phases ----
        public enum WavePhase
        {
            Ready,          // momentum restored, about to surge
            Surge,          // high momentum, aggressive advance
            Engaged,        // momentum draining, still fighting
            Consolidate,    // momentum depleted, tactical pause
            Recovering,     // momentum rebuilding, holding
        }

        /// <summary>Per-sub-group momentum state.</summary>
        internal class MomentumState
        {
            public int Team;
            public Vector3 Centroid;
            public float Momentum = 1f;        // 0..1, starts fresh
            public WavePhase Phase = WavePhase.Surge;
            public float PhaseTimer;           // time in current phase
            public int LastRemaining;          // for tracking losses/gains
            public float LastKillCount;        // for tracking kill momentum
            public bool IsEngaged;             // currently in combat
            public bool IsAdvancing;           // currently moving toward enemy
            public int PoolPriority;           // for wave-based target pooling (higher = more priority)

            // Envelope shaping state.
            public bool WasEngaged;            // was engaged last cycle (for contact spike detection)
            public float EngagedDuration;      // total time spent continuously engaged (for plateau/decay)
            public float OriginalStrength;     // unit count when first tracked (for loss fraction)
            public bool Collapsed;             // true if collapse drop was applied this engagement
        }

        // Active momentum states, keyed by sub-group index.
        // Rebuilt each fracture cycle to match the current sub-groups.
        private static readonly List<MomentumState> _states = new List<MomentumState>();
        internal static IReadOnlyList<MomentumState> States => _states;

        /// <summary>
        /// Rebuild the momentum state list to match the current sub-groups.
        /// Preserves momentum for groups that still exist (matched by team + centroid proximity).
        /// Called at the start of each fracture cycle.
        /// </summary>
        internal static void SyncStates(List<(int team, Vector3 centroid, int remaining, float kills, bool engaged, bool advancing)> subGroups)
        {
            var newStates = new List<MomentumState>();

            for (int i = 0; i < subGroups.Count; i++)
            {
                var sg = subGroups[i];
                MomentumState existing = null;

                // Try to find a matching previous state (same team, nearby centroid).
                float bestDist = 80f; // max match distance
                int bestIdx = -1;
                for (int j = 0; j < _states.Count; j++)
                {
                    var s = _states[j];
                    if (s.Team != sg.team) continue;
                    float d = Vector3.Distance(s.Centroid, sg.centroid);
                    if (d < bestDist)
                    {
                        bestDist = d;
                        bestIdx = j;
                    }
                }

                if (bestIdx >= 0)
                {
                    // Reuse existing state — update position, track changes.
                    existing = _states[bestIdx];
                    _states.RemoveAt(bestIdx);

                    // Track losses and kills for momentum delta.
                    int lost = existing.LastRemaining - sg.remaining;
                    float killDelta = sg.kills - existing.LastKillCount;
                    float lossFraction = existing.OriginalStrength > 0
                        ? lost / existing.OriginalStrength : 0f;

                    // ---- ENVELOPE SHAPING ----
                    // The momentum curve follows the natural rhythm of battle:
                    //
                    //   Initial contact → spike      (adrenaline surge)
                    //   Sustained fighting → plateau  (momentum holds, then slowly decays)
                    //   Exhaustion → decay           (accelerating drain after plateau)
                    //   Pause → trough               (bottoms out, slow recovery begins)
                    //   Counter-push → new spike     (sharp jump when recovered enough)
                    //   Collapse → sharp drop        (cliff drop on heavy losses)
                    //   Regroup → slow rise          (S-curve recovery)

                    if (sg.engaged)
                    {
                        // 1. INITIAL CONTACT SPIKE — first engagement after being unengaged.
                        if (!existing.WasEngaged)
                        {
                            existing.Momentum += ContactSpike;
                            existing.EngagedDuration = 0f;
                            existing.Collapsed = false;
                        }
                        existing.EngagedDuration += existing.PhaseTimer;

                        // 2. SUSTAINED FIGHTING PLATEAU — momentum holds steady for
                        //    PlateauDuration seconds before decay kicks in.
                        if (existing.EngagedDuration < PlateauDuration)
                        {
                            // Plateau: momentum gravitates toward PlateauHold, minimal drain.
                            existing.Momentum = Mathf.Lerp(existing.Momentum,
                                PlateauHold, 0.3f * existing.PhaseTimer);
                        }
                        else
                        {
                            // 3. EXHAUSTION DECAY — after plateau, drain accelerates.
                            //    The longer the engagement continues, the faster momentum drops.
                            float overtime = existing.EngagedDuration - PlateauDuration;
                            float decayMultiplier = 1f + overtime * DecayAcceleration * 0.1f;
                            existing.Momentum -= ExhaustionRate * decayMultiplier * existing.PhaseTimer;
                        }

                        // 6. COLLAPSE — sharp cliff drop when losses are heavy in one cycle.
                        //    This is NOT gradual exhaustion — it's a sudden break.
                        if (lossFraction > CollapseThreshold && !existing.Collapsed)
                        {
                            existing.Momentum -= CollapseDrop;
                            existing.Collapsed = true;
                        }

                        // Kill momentum — small boosts for inflicting losses.
                        if (killDelta > 0)
                            existing.Momentum += KillMomentumGain * killDelta;

                        // Per-death drain (gradual, separate from collapse).
                        if (lost > 0)
                            existing.Momentum -= LossMomentumDrain * lost;
                    }
                    else if (sg.advancing)
                    {
                        // Advancing unopposed — the thrill of the charge builds momentum.
                        existing.Momentum += AdvanceMomentumGain * existing.PhaseTimer;
                        existing.EngagedDuration = 0f; // reset engagement timer
                        existing.Collapsed = false;
                    }
                    else
                    {
                        // 4. PAUSE / 7. REGROUP — not engaged, not advancing.
                        // Recovery follows an S-curve: slow at first (trough),
                        // accelerates (regroup), then slows as it approaches full (ready).
                        //
                        // S-curve: f(t) = 1 / (1 + e^(-k*(t - midpoint)))
                        // We approximate with a piecewise approach:
                        //   - Low momentum: slow recovery (trough)
                        //   - Mid momentum: fast recovery (regroup accelerating)
                        //   - High momentum: slow recovery (approaching full)
                        float mom = existing.Momentum;
                        float recoverySpeed;
                        if (mom < 0.2f)
                            recoverySpeed = RecoveryRate * 0.3f; // trough — slow
                        else if (mom < 0.5f)
                            recoverySpeed = RecoveryRate * 1.5f; // regroup — accelerating
                        else
                            recoverySpeed = RecoveryRate * 0.7f; // approaching full — slowing

                        existing.Momentum += recoverySpeed * existing.PhaseTimer;
                        existing.EngagedDuration = 0f;
                        existing.Collapsed = false;
                    }

                    existing.Momentum = Mathf.Clamp01(existing.Momentum);
                    existing.WasEngaged = sg.engaged;
                    existing.Centroid = sg.centroid;
                    existing.LastRemaining = sg.remaining;
                    existing.LastKillCount = sg.kills;
                    existing.IsEngaged = sg.engaged;
                    existing.IsAdvancing = sg.advancing;
                    existing.PhaseTimer = 0f; // reset, will accumulate

                    // Phase transitions.
                    UpdatePhase(existing);
                }
                else
                {
                    // New sub-group — starts with full momentum in SURGE.
                    existing = new MomentumState
                    {
                        Team = sg.team,
                        Centroid = sg.centroid,
                        Momentum = 1f,
                        Phase = WavePhase.Surge,
                        LastRemaining = sg.remaining,
                        LastKillCount = sg.kills,
                        IsEngaged = sg.engaged,
                        IsAdvancing = sg.advancing,
                        WasEngaged = sg.engaged,
                        OriginalStrength = sg.remaining,
                    };
                }

                // Compute pool priority for wave-based target allocation.
                existing.PoolPriority = ComputePoolPriority(existing);
                newStates.Add(existing);
            }

            _states.Clear();
            _states.AddRange(newStates);
        }

        /// <summary>
        /// Advance the phase timer and check for phase transitions.
        /// Called each frame (not just each fracture cycle) for smooth timing.
        /// Also applies continuous momentum drift for smooth envelope shaping.
        /// </summary>
        internal static void Tick(float dt)
        {
            if (!Enabled) return;
            foreach (var s in _states)
            {
                s.PhaseTimer += dt;

                // Continuous momentum drift for smooth envelope shaping.
                // This runs every frame (not just each fracture cycle) so the
                // momentum curve is smooth, not stepped.
                if (s.IsEngaged && s.Phase == WavePhase.Engaged)
                {
                    // Sustained fighting: plateau then accelerating decay.
                    s.EngagedDuration += dt;
                    if (s.EngagedDuration > PlateauDuration)
                    {
                        float overtime = s.EngagedDuration - PlateauDuration;
                        float decayMul = 1f + overtime * DecayAcceleration * 0.1f;
                        s.Momentum -= ExhaustionRate * decayMul * dt;
                    }
                    else
                    {
                        // Plateau: hold momentum near PlateauHold.
                        s.Momentum = Mathf.Lerp(s.Momentum, PlateauHold, 0.1f * dt);
                    }
                }
                else if (s.Phase == WavePhase.Consolidate || s.Phase == WavePhase.Recovering)
                {
                    // S-curve recovery: slow at trough, fast in middle, slow at top.
                    float mom = s.Momentum;
                    float speed;
                    if (mom < 0.2f) speed = RecoveryRate * 0.3f;
                    else if (mom < 0.5f) speed = RecoveryRate * 1.5f;
                    else speed = RecoveryRate * 0.7f;
                    s.Momentum += speed * dt;
                }

                s.Momentum = Mathf.Clamp01(s.Momentum);
                UpdatePhase(s);
                s.PoolPriority = ComputePoolPriority(s);
            }
        }

        /// <summary>Update the wave phase based on current momentum and context.</summary>
        private static void UpdatePhase(MomentumState s)
        {
            var prevPhase = s.Phase;

            switch (s.Phase)
            {
                case WavePhase.Surge:
                    // Surge continues while momentum is high enough.
                    if (s.Momentum < ConsolidateThreshold)
                        s.Phase = WavePhase.Consolidate;
                    else if (s.Momentum < SurgeThreshold * 0.5f && s.IsEngaged)
                        s.Phase = WavePhase.Engaged;
                    break;

                case WavePhase.Engaged:
                    // Still fighting but momentum draining.
                    if (s.Momentum < ConsolidateThreshold)
                        s.Phase = WavePhase.Consolidate;
                    else if (s.Momentum > SurgeThreshold && !s.IsEngaged)
                        s.Phase = WavePhase.Surge;
                    break;

                case WavePhase.Consolidate:
                    // Tactical pause — hold position, recover.
                    // Stay here until momentum recovers somewhat.
                    if (s.Momentum > ReadyThreshold * 0.7f)
                        s.Phase = WavePhase.Recovering;
                    break;

                case WavePhase.Recovering:
                    // Rebuilding momentum, holding position.
                    if (s.Momentum >= ReadyThreshold)
                        s.Phase = WavePhase.Ready;
                    // If suddenly engaged while recovering, jump to engaged.
                    else if (s.IsEngaged)
                        s.Phase = WavePhase.Engaged;
                    break;

                case WavePhase.Ready:
                    // Ready to surge — trigger when momentum is high or enemy is close.
                    if (s.Momentum >= SurgeThreshold || s.IsEngaged)
                        s.Phase = WavePhase.Surge;
                    break;
            }

            if (s.Phase != prevPhase)
            {
                s.PhaseTimer = 0f;
            }
        }

        /// <summary>
        /// Pool priority for wave-based NavGrid target allocation.
        /// Higher priority = gets a target slot first.
        /// SURGE/READY groups get priority; CONSOLIDATE/RECOVERING yield slots.
        /// </summary>
        private static int ComputePoolPriority(MomentumState s)
        {
            if (!WavePoolingEnabled) return 100; // all equal if pooling disabled

            switch (s.Phase)
            {
                case WavePhase.Surge: return 100;       // top priority — active push
                case WavePhase.Ready: return 80;        // about to surge — keep slot warm
                case WavePhase.Engaged: return 60;      // still fighting — keep slot
                case WavePhase.Consolidate: return 20;  // pausing — yield slot to fresh groups
                case WavePhase.Recovering: return 10;   // rebuilding — lowest priority
                default: return 50;
            }
        }

        /// <summary>
        /// Whether a sub-group should hold position (tactical pause) rather than
        /// advance. Used by SubGroupFracture to suppress target issuance during
        /// consolidation/recovery phases.
        /// </summary>
        internal static bool ShouldHoldPosition(int stateIndex)
        {
            if (!Enabled || stateIndex < 0 || stateIndex >= _states.Count) return false;
            var s = _states[stateIndex];
            return s.Phase == WavePhase.Consolidate || s.Phase == WavePhase.Recovering;
        }

        /// <summary>
        /// Whether a sub-group should get an aggressive boost (extra pursuit,
        /// ignore hold orders). Used by SubGroupFracture during SURGE phase.
        /// </summary>
        internal static bool ShouldSurge(int stateIndex)
        {
            if (!Enabled || stateIndex < 0 || stateIndex >= _states.Count) return false;
            return _states[stateIndex].Phase == WavePhase.Surge;
        }

        /// <summary>Get the momentum state for a sub-group by index.</summary>
        internal static MomentumState GetState(int stateIndex)
        {
            if (stateIndex < 0 || stateIndex >= _states.Count) return null;
            return _states[stateIndex];
        }

        /// <summary>Get the pool priority for a sub-group by index.</summary>
        internal static int GetPoolPriority(int stateIndex)
        {
            if (stateIndex < 0 || stateIndex >= _states.Count) return 50;
            return _states[stateIndex].PoolPriority;
        }

        /// <summary>Clear all momentum states (e.g. when battle resets).</summary>
        internal static void Clear()
        {
            _states.Clear();
        }

        // ---- Stats for UI ----

        internal struct MomentumStat
        {
            public int Team;
            public float Momentum;
            public WavePhase Phase;
            public float PhaseTime;
            public int PoolPriority;
            public bool Engaged;
        }

        internal static List<MomentumStat> GetStats()
        {
            var list = new List<MomentumStat>();
            foreach (var s in _states)
            {
                list.Add(new MomentumStat
                {
                    Team = s.Team,
                    Momentum = s.Momentum,
                    Phase = s.Phase,
                    PhaseTime = s.PhaseTimer,
                    PoolPriority = s.PoolPriority,
                    Engaged = s.IsEngaged,
                });
            }
            return list;
        }
    }
}
