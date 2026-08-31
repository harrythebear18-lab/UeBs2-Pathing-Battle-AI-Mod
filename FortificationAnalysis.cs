using UnityEngine;

namespace UEBS2PathingMod
{
    /// <summary>
    /// Fortification detection and tactical analysis for multi-team battles.
    ///
    /// Detects fortified positions (HoldGuard armies, structures on NotWalkable
    /// layer) and provides:
    ///   - Morale bonuses for defenders in fortifications
    ///   - Flank routing for attackers (don't all path through the same gate)
    ///   - Multi-team coordination (different attacking teams hit different sides)
    /// </summary>
    public static class FortificationAnalysis
    {
        /// <summary>A detected fortified position.</summary>
        internal struct Fortification
        {
            public Vector3 Center;
            public float Radius;          // approximate defensive radius
            public int DefendingTeam;
            public int DefenderCount;
            public bool HasStructures;    // walls/buildings on NotWalkable layer
            public Vector3[] FlankPoints; // suggested attack vectors (per side)
        }

        // Reusable buffer of detected fortifications, refreshed each assessment cycle.
        private static readonly System.Collections.Generic.List<Fortification> _forts =
            new System.Collections.Generic.List<Fortification>();

        internal static System.Collections.Generic.List<Fortification> GetFortifications() => _forts;

        /// <summary>
        /// Scan all sub-groups for fortified positions. Called once per fracture
        /// assessment cycle before sub-group decisions are made.
        /// </summary>
        internal static void DetectFortifications(System.Collections.Generic.List<object> subGroups)
        {
            _forts.Clear();

            // We can't directly reference SubGroup (it's private), so we use
            // reflection-free access via the public GetStats. Instead, we'll
            // scan armies directly for HoldGuard and cluster them.
            var armies = ThreadManager.AllArmies;
            if (armies == null || armies.Count == 0) return;

            // Find all HoldGuard armies and cluster them by position.
            var guardArmies = new System.Collections.Generic.List<Army>();
            foreach (var a in armies)
            {
                if (a == null || a.Remaining <= 0) continue;
                if (a.HoldGuard || a.HoldPosition) guardArmies.Add(a);
            }

            if (guardArmies.Count == 0) return;

            // Cluster guard armies by team + proximity (100-unit radius).
            var clustered = new System.Collections.Generic.List<bool>();
            for (int i = 0; i < guardArmies.Count; i++) clustered.Add(false);

            for (int i = 0; i < guardArmies.Count; i++)
            {
                if (clustered[i]) continue;
                clustered[i] = true;

                var fort = new Fortification
                {
                    DefendingTeam = guardArmies[i].Team,
                    Center = guardArmies[i].transform.position,
                    Radius = 50f, // base radius, grows with cluster size
                    DefenderCount = guardArmies[i].Remaining,
                    HasStructures = false,
                };

                // Absorb nearby same-team guard armies.
                for (int j = i + 1; j < guardArmies.Count; j++)
                {
                    if (clustered[j]) continue;
                    if (guardArmies[j].Team != fort.DefendingTeam) continue;
                    float d = Vector3.Distance(guardArmies[j].transform.position, fort.Center);
                    if (d < 120f)
                    {
                        clustered[j] = true;
                        fort.Center = (fort.Center + guardArmies[j].transform.position) * 0.5f;
                        fort.DefenderCount += guardArmies[j].Remaining;
                        fort.Radius = Mathf.Max(fort.Radius, d + 50f);
                    }
                }

                // Check for nearby structures on the NotWalkable layer.
                fort.HasStructures = DetectNearbyStructures(fort.Center, fort.Radius);

                // Generate flank points: 4 attack vectors around the fortification.
                // Attackers should approach from multiple sides, not all through one.
                fort.FlankPoints = GenerateFlankPoints(fort.Center, fort.Radius);

                _forts.Add(fort);
            }
        }

        /// <summary>
        /// Check if there are structures (walls, buildings) near a position
        /// by testing the NotWalkable layer with physics overlap.
        /// </summary>
        private static bool DetectNearbyStructures(Vector3 center, float radius)
        {
            // The game's NotWalkable layer mask marks structures that block pathing.
            // We check if any colliders on that layer exist within the fort radius.
            var notWalkable = NavGrid.walkable; // We invert this to get NotWalkable
            // Actually we need the NotWalkable mask. The game stores it on the NavGrid
            // instance but we only have the static 'walkable'. Let's use a broad check:
            // Physics.OverlapSphere against all layers, then filter for non-terrain.
            var colliders = Physics.OverlapSphere(center, radius);
            foreach (var col in colliders)
            {
                if (col == null) continue;
                // Skip terrain colliders (check by type name to avoid assembly ref).
                if (col.GetType().Name == "TerrainCollider") continue;
                // If it's on the walkable layer, it's terrain/ground — skip.
                if ((NavGrid.walkable.value & (1 << col.gameObject.layer)) != 0) continue;
                // Non-walkable collider found — likely a structure.
                return true;
            }
            return false;
        }

        /// <summary>
        /// Generate 4 flank attack points around a fortification at cardinal offsets.
        /// These are used to distribute attacking sub-groups across multiple approach
        /// vectors so they don't all path through the same chokepoint.
        /// </summary>
        private static Vector3[] GenerateFlankPoints(Vector3 center, float radius)
        {
            float offset = radius + 80f; // attack from outside the defensive radius
            return new Vector3[]
            {
                center + new Vector3(offset, 0, 0),    // east
                center + new Vector3(-offset, 0, 0),   // west
                center + new Vector3(0, 0, offset),    // north
                center + new Vector3(0, 0, -offset),   // south
            };
        }

        /// <summary>
        /// Get the morale bonus for a sub-group defending a fortification.
        /// Real battles: fortified defenders fight much harder.
        /// </summary>
        internal static float GetFortificationMoraleBonus(Vector3 position, int team)
        {
            foreach (var fort in _forts)
            {
                if (fort.DefendingTeam != team) continue;
                float d = Vector3.Distance(position, fort.Center);
                if (d <= fort.Radius)
                {
                    // Inside a fortification: big morale bonus.
                    // Structures (walls) give even more than open-field guard orders.
                    return fort.HasStructures ? 0.35f : 0.2f;
                }
            }
            return 0f;
        }

        /// <summary>
        /// Check if a position is inside an enemy fortification.
        /// Used by attackers to know they're assaulting a fortified position.
        /// </summary>
        internal static bool IsInEnemyFortification(Vector3 position, int ourTeam)
        {
            foreach (var fort in _forts)
            {
                if (fort.DefendingTeam == ourTeam) continue;
                if (Vector3.Distance(position, fort.Center) <= fort.Radius + 50f)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Get the nearest enemy fortification to a position.
        /// Returns null if none found.
        /// </summary>
        internal static Fortification? GetNearestEnemyFortification(Vector3 position, int ourTeam)
        {
            Fortification? nearest = null;
            float nearestDist = float.MaxValue;
            foreach (var fort in _forts)
            {
                if (fort.DefendingTeam == ourTeam) continue;
                float d = Vector3.Distance(position, fort.Center);
                if (d < nearestDist)
                {
                    nearestDist = d;
                    nearest = fort;
                }
            }
            return nearest;
        }

        /// <summary>
        /// Get a suggested flank attack point for an attacking sub-group.
        /// Distributes attackers across different sides of the fortification
        /// so they don't all converge on one point.
        /// </summary>
        internal static Vector3 GetFlankAttackPoint(Vector3 attackerPos, Fortification fort, int hashSeed)
        {
            if (fort.FlankPoints == null || fort.FlankPoints.Length == 0)
                return fort.Center;

            // Pick the flank point closest to the attacker, but use a hash
            // of the attacker's position to distribute multiple attackers
            // across different flanks. This prevents all attackers from
            // choosing the same closest flank.
            int baseIndex = Mathf.Abs(hashSeed) % fort.FlankPoints.Length;

            // 60% chance to use the hashed flank, 40% to use nearest.
            // This gives some randomness while still distributing.
            if (UnityEngine.Random.value < 0.4f)
            {
                // Use nearest flank point.
                float nearestDist = float.MaxValue;
                int nearestIdx = 0;
                for (int i = 0; i < fort.FlankPoints.Length; i++)
                {
                    float d = Vector3.Distance(attackerPos, fort.FlankPoints[i]);
                    if (d < nearestDist) { nearestDist = d; nearestIdx = i; }
                }
                baseIndex = nearestIdx;
            }

            return fort.FlankPoints[baseIndex];
        }
    }
}
