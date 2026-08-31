using System;
using HarmonyLib;
using UnityEngine;

namespace UEBS2PathingMod
{
    /// <summary>
    /// Patches for FlyCam's auto-cinematic camera.
    ///
    /// The vanilla auto-cinematic cycles teams round-robin and picks a random
    /// army within the selected team. These patches replace that with
    /// combat-proximity-weighted selection so the camera favors where
    /// fighting is actually happening.
    /// </summary>
    public static class CinematicPatches
    {
        // Reusable scratch array for scoring armies.
        private static float[] _armyScores = Array.Empty<float>();

        /// <summary>
        /// Replaces FlyCam.GetNewTeamTarget. The original picks a random army
        /// belonging to the round-robin team. We ignore the passed team and
        /// instead score every army by how close it is to combat, then do a
        /// weighted-random pick favoring the hottest clashes.
        ///
        /// Returns true to skip the original method (we set __result).
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(FlyCam), "GetNewTeamTarget")]
        public static bool GetNewTeamTargetPrefix(ref int __result, int TargetTeam)
        {
            var i = PathingModPlugin.Instance;
            if (i == null || !i.ModEnabled.Value || !i.CinematicCombatBias.Value)
                return true; // run original

            var armies = ThreadManager.AllArmies;
            if (armies == null || armies.Count == 0)
                return true; // nothing we can do, let original handle it

            try
            {
                float bias = i.CinematicBiasStrength.Value;
                if (bias <= 0f) return true; // fully random = vanilla

                if (_armyScores.Length != armies.Count)
                    _armyScores = new float[armies.Count];

                float totalScore = 0f;
                for (int a = 0; a < armies.Count; a++)
                {
                    var army = armies[a];
                    if (army == null || army.Remaining <= 0)
                    {
                        _armyScores[a] = 0f;
                        continue;
                    }

                    _armyScores[a] = ScoreArmyCombat(army);
                    totalScore += _armyScores[a];
                }

                // If nobody is in combat, fall back to vanilla random.
                if (totalScore <= 0f)
                    return true;

                // Blend between weighted and uniform based on bias strength.
                // bias=1 -> fully weighted; bias=0.5 -> half weighted, half uniform.
                float roll = UnityEngine.Random.value * totalScore;
                float cumulative = 0f;
                int chosen = 0;
                for (int a = 0; a < armies.Count; a++)
                {
                    // Mix weighted score with a uniform floor so low-combat
                    // armies still get occasional coverage.
                    float mixed = Mathf.Lerp(1f, _armyScores[a], bias);
                    cumulative += mixed;
                    if (roll <= cumulative)
                    {
                        chosen = a;
                        break;
                    }
                }

                __result = chosen;
                return false; // skip original
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[PathingMod] cinematic target error: {e}");
                return true; // fall back to vanilla
            }
        }

        /// <summary>
        /// Score an army by how likely it is to be in active combat right now.
        /// Factors: proximity to nearest enemy, kill count, remaining units.
        /// </summary>
        private static float ScoreArmyCombat(Army army)
        {
            if (army.Remaining <= 0) return 0f;

            // 1. Nearest enemy distance — closer means more likely fighting.
            float nearestEnemy = float.MaxValue;
            var all = ThreadManager.AllArmies;
            for (int i = 0; i < all.Count; i++)
            {
                var other = all[i];
                if (other == null || other.Team == army.Team || other.Remaining <= 0)
                    continue;
                float d = Vector3.Distance(army.transform.position, other.transform.position);
                if (d < nearestEnemy) nearestEnemy = d;
            }
            if (nearestEnemy == float.MaxValue) return 0f;

            // Proximity score: 1 at contact, fading to 0 at 500 units.
            float proxScore = Mathf.Clamp01(1f - nearestEnemy / 500f);

            // 2. Kill activity — armies that have been killing are in combat.
            // TotalKills is cumulative; we use it as a soft proxy for engagement.
            float killScore = Mathf.Clamp01(army.TotalKills / 200f);

            // 3. Don't waste camera time on nearly-wiped armies.
            float aliveRatio = Mathf.Clamp01((float)army.Remaining / Mathf.Max(1, army.ArmyCount));

            // Weighted combination. Proximity dominates (that's the "clash" signal),
            // kills add confirmation, alive ratio prevents focusing on the dead.
            return proxScore * 0.6f + killScore * 0.3f + aliveRatio * 0.1f;
        }

        /// <summary>
        /// Postfix on the cinematic refocus timer. The vanilla code sets
        /// CinTimer = 4f + Random.value after each cut. We shorten or
        /// lengthen that based on the user's FocusInterval config.
        /// We do this by patching the private field via Traverse after the
        /// Update runs — but since we can't time-travel inside Update, we
        /// instead just adjust CinTimer each frame toward our target interval.
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(FlyCam), "Update")]
        public static void FlyCamUpdatePostfix(FlyCam __instance)
        {
            var i = PathingModPlugin.Instance;
            if (i == null || !i.ModEnabled.Value || !i.CinematicCombatBias.Value)
                return;
            if (!FlyCam.AutoCinematicMode) return;

            try
            {
                // Gently steer the refocus timer toward the configured interval.
                // We can't overwrite the game's set without fighting it each frame,
                // so we only nudge when it's wildly off.
                float desired = i.CinematicFocusInterval.Value;
                float current = Traverse.Create(__instance).Field("CinTimer").GetValue<float>();
                // Only shorten (don't fight the game's "4+random" by lengthening).
                if (current > desired + 2f)
                {
                    Traverse.Create(__instance).Field("CinTimer").SetValue(desired);
                }
            }
            catch { /* non-critical */ }
        }
    }
}
