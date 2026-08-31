using UnityEngine;

namespace UEBS2PathingMod
{
    /// <summary>
    /// Terrain-aware tactical analysis for sub-group decision-making.
    ///
    /// Uses the active Unity terrain (TerrainSettings.MapTerrain / Terrain.activeTerrain)
    /// and physics raycasts to evaluate:
    ///   - High/low ground relative to enemies
    ///   - Cover between a position and the nearest enemy
    ///   - Nearby high-ground positions worth seeking
    ///   - Nearby cover positions worth retreating to
    ///
    /// All queries are CPU-only and cheap (terrain height sampling + a handful of
    /// raycasts per sub-group per assessment cycle). No GPU readback required.
    /// </summary>
    public static class TerrainAnalysis
    {
        // Sample height at a world position using the active terrain.
        // Falls back to Physics raycast if no terrain is available.
        internal static float GetGroundHeight(Vector3 worldPos)
        {
            Terrain terrain = TerrainSettings.MapTerrain != null
                ? TerrainSettings.MapTerrain
                : Terrain.activeTerrain;
            if (terrain != null)
            {
                return terrain.SampleHeight(worldPos) + terrain.transform.position.y;
            }
            // Fallback: raycast down from high up.
            if (Physics.Raycast(worldPos + Vector3.up * 500f, Vector3.down, out var hit, 10000f, NavGrid.walkable))
            {
                return hit.point.y;
            }
            return worldPos.y;
        }

        /// <summary>
        /// Height advantage of 'us' relative to 'them'.
        /// Positive = we're higher (advantage). Negative = we're lower (disadvantage).
        /// </summary>
        internal static float HeightAdvantage(Vector3 us, Vector3 them)
        {
            return GetGroundHeight(us) - GetGroundHeight(them);
        }

        /// <summary>
        /// Check if there's terrain cover between 'from' and 'to'.
        /// Raycasts from unit height (~1.5m) toward the target. If terrain
        /// blocks the line of sight before reaching the target, there's cover.
        /// </summary>
        internal static bool HasCoverBetween(Vector3 from, Vector3 to)
        {
            Vector3 origin = from + Vector3.up * 1.5f;
            Vector3 dir = (to + Vector3.up * 1.5f) - origin;
            float dist = dir.magnitude;
            if (dist < 1f) return false;
            dir /= dist;

            // Raycast against the walkable layer (terrain). If we hit terrain
            // before reaching the target, the terrain is blocking LOS = cover.
            if (Physics.Raycast(origin, dir, out var hit, dist - 2f, NavGrid.walkable))
            {
                // Only count it as cover if the hit point is higher than both
                // endpoints (i.e. a ridge/hill between us, not just the ground).
                float hitHeight = hit.point.y;
                float fromHeight = GetGroundHeight(from);
                float toHeight = GetGroundHeight(to);
                return hitHeight > Mathf.Min(fromHeight, toHeight) + 1f;
            }
            return false;
        }

        /// <summary>
        /// Search for a nearby position that offers high ground relative to
        /// the nearest enemy. Samples points in a spiral around 'center'.
        /// Returns the best position found, or center if none is better.
        /// </summary>
        internal static Vector3 FindNearbyHighGround(Vector3 center, Vector3 enemyPos, float searchRadius, int samples)
        {
            float ourHeight = GetGroundHeight(center);
            float enemyHeight = GetGroundHeight(enemyPos);
            float bestAdvantage = ourHeight - enemyHeight;
            Vector3 bestPos = center;

            // Spiral sampling: walk outward in rings.
            for (int i = 0; i < samples; i++)
            {
                float t = (float)i / samples;
                float radius = Mathf.Lerp(20f, searchRadius, t);
                float angle = i * 2.399963f; // golden angle for good spread
                Vector3 candidate = center + new Vector3(
                    Mathf.Cos(angle) * radius, 0f, Mathf.Sin(angle) * radius);

                float candHeight = GetGroundHeight(candidate);
                float advantage = candHeight - enemyHeight;
                // Prefer higher ground, but don't wander too far from current position.
                // Penalize distance so we don't send units on a hike across the map.
                float distPenalty = Vector3.Distance(center, candidate) / searchRadius * 5f;
                float score = advantage - distPenalty;
                if (score > bestAdvantage - enemyHeight + bestAdvantage * 0f) // simplified: just compare raw advantage minus penalty
                {
                    // Recompute properly: we want max (advantage - distPenalty)
                }
            }

            // Redo with proper scoring (cleaner than the above).
            bestAdvantage = float.MinValue;
            bestPos = center;
            for (int i = 0; i < samples; i++)
            {
                float t = (float)i / samples;
                float radius = Mathf.Lerp(20f, searchRadius, t);
                float angle = i * 2.399963f;
                Vector3 candidate = center + new Vector3(
                    Mathf.Cos(angle) * radius, 0f, Mathf.Sin(angle) * radius);

                float candHeight = GetGroundHeight(candidate);
                float advantage = candHeight - enemyHeight;
                float distPenalty = Vector3.Distance(center, candidate) / searchRadius * 5f;
                float score = advantage - distPenalty;
                if (score > bestAdvantage)
                {
                    bestAdvantage = score;
                    bestPos = candidate;
                }
            }

            // Only use the new position if it's actually higher than where we are.
            if (GetGroundHeight(bestPos) > ourHeight + 2f)
                return bestPos;
            return center;
        }

        /// <summary>
        /// Search for a nearby position that offers cover from the nearest enemy.
        /// Samples points in a spiral and checks if terrain blocks LOS to the enemy.
        /// </summary>
        internal static Vector3 FindNearbyCover(Vector3 center, Vector3 enemyPos, float searchRadius, int samples)
        {
            // If we already have cover, stay put.
            if (HasCoverBetween(center, enemyPos))
                return center;

            for (int i = 0; i < samples; i++)
            {
                float t = (float)i / samples;
                float radius = Mathf.Lerp(30f, searchRadius, t);
                float angle = i * 2.399963f;
                Vector3 candidate = center + new Vector3(
                    Mathf.Cos(angle) * radius, 0f, Mathf.Sin(angle) * radius);

                if (HasCoverBetween(candidate, enemyPos))
                {
                    // Found cover — but don't move too far. Prefer the closest cover.
                    if (Vector3.Distance(center, candidate) < searchRadius * 0.7f)
                        return candidate;
                }
            }
            return center; // no cover found nearby
        }

        /// <summary>
        /// Is an army ranged? (has a projectile or long attack range)
        /// </summary>
        internal static bool IsRanged(Army army)
        {
            return army.Projectile != null || army.AttackRange > 5f;
        }

        /// <summary>
        /// Comprehensive terrain assessment for a sub-group at 'ourPos'
        /// facing an enemy at 'enemyPos'.
        /// </summary>
        internal struct TerrainAssessment
        {
            public float HeightAdvantage;      // + = we're higher
            public bool HasHighGround;         // height advantage > 5m
            public bool AtHeightDisadvantage;  // height advantage < -5m
            public bool HasCover;              // terrain blocks LOS to enemy
            public bool IsRanged;              // sub-group is primarily ranged
            public Vector3 NearbyHighGround;   // best nearby high-ground position
            public Vector3 NearbyCover;        // best nearby cover position
        }

        internal static TerrainAssessment Assess(Vector3 ourPos, Vector3 enemyPos, bool isRanged)
        {
            float heightAdv = HeightAdvantage(ourPos, enemyPos);
            bool hasCover = HasCoverBetween(ourPos, enemyPos);

            // Only search for better positions if we're at a disadvantage
            // or if we're ranged and lack cover.
            Vector3 highGround = ourPos;
            Vector3 cover = ourPos;

            if (heightAdv < -5f)
            {
                // We're on low ground — look for high ground nearby.
                highGround = FindNearbyHighGround(ourPos, enemyPos, 200f, 16);
            }
            if (isRanged && !hasCover)
            {
                // Ranged units want cover. Melee units care less.
                cover = FindNearbyCover(ourPos, enemyPos, 150f, 12);
            }

            return new TerrainAssessment
            {
                HeightAdvantage = heightAdv,
                HasHighGround = heightAdv > 5f,
                AtHeightDisadvantage = heightAdv < -5f,
                HasCover = hasCover,
                IsRanged = isRanged,
                NearbyHighGround = highGround,
                NearbyCover = cover,
            };
        }
    }
}
