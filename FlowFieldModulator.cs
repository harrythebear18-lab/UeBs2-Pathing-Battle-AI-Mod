using System.Collections.Generic;
using UnityEngine;

namespace UEBS2PathingMod
{
    /// <summary>
    /// Flow field modulation via soft obstacle injection.
    ///
    /// The game's GPU pathfinding reads from ObstacleGrid (an R8 RenderTexture)
    /// where non-zero pixels mark non-walkable cells. By temporarily writing
    /// our own obstacle pixels into ObstacleGrid before the game's compute
    /// dispatch, we can:
    ///   - Block direct paths to force flanking routes
    ///   - Create corridors that channel units through specific areas
    ///   - Block retreat paths that would lead into enemies
    ///   - Create dispersion fans that spread flow-field attractors
    ///
    /// The modulation is applied in the NavGrid.Update prefix (after backup)
    /// and removed in the postfix (restore from backup), so we never
    /// permanently pollute the game's obstacle grid.
    /// </summary>
    public static class FlowFieldModulator
    {
        // The modulation texture (same format as ObstacleGrid).
        private static RenderTexture _modTexture;
        // Backup of ObstacleGrid before our modulation.
        private static RenderTexture _backupTexture;
        // Simple material for rendering blocker quads.
        private static Material _blockMat;
        // Whether the system is initialized.
        private static bool _initialized;

        // Active blockers for this frame. Cleared and repopulated each cycle.
        private static readonly List<Blocker> _blockers = new List<Blocker>();
        // Global strength multiplier for all blockers.
        internal static float BlockStrength = 0.7f;

        /// <summary>A rectangular obstacle to write into the grid.</summary>
        internal struct Blocker
        {
            public Vector3 WorldCenter;
            public float WorldWidth;    // X extent
            public float WorldDepth;    // Z extent
            public float Strength;      // 0..1, how strongly to block (1=hard wall, 0.3=soft discourage)
        }

        /// <summary>Add a blocker for this frame.</summary>
        internal static void AddBlocker(Vector3 worldCenter, float worldWidth, float worldDepth, float strength = 1f)
        {
            _blockers.Add(new Blocker
            {
                WorldCenter = worldCenter,
                WorldWidth = worldWidth,
                WorldDepth = worldDepth,
                Strength = Mathf.Clamp01(strength),
            });
        }

        /// <summary>Clear all blockers.</summary>
        internal static void ClearBlockers() => _blockers.Clear();

        /// <summary>
        /// Initialize the modulation system. Called lazily on first use.
        /// Creates textures matching the game's ObstacleGrid format.
        /// </summary>
        private static void EnsureInit()
        {
            if (_initialized) return;
            if (NavGrid.ObstacleGrid == null) return;

            int size = NavGrid.ObstacleGrid.width;
            _modTexture = new RenderTexture(size, size, 0, RenderTextureFormat.R8);
            _modTexture.enableRandomWrite = true;
            _modTexture.autoGenerateMips = false;
            _modTexture.Create();

            _backupTexture = new RenderTexture(size, size, 0, RenderTextureFormat.R8);
            _backupTexture.enableRandomWrite = true;
            _backupTexture.autoGenerateMips = false;
            _backupTexture.Create();

            // Simple unlit material for rendering white quads.
            _blockMat = new Material(Shader.Find("Unlit/Color"));
            _blockMat.color = Color.white;

            _initialized = true;
        }

        /// <summary>
        /// Apply modulation: backup ObstacleGrid, render our blockers into it.
        /// Called in NavGrid.Update prefix, before the game's compute dispatch.
        /// </summary>
        internal static void Apply()
        {
            if (_blockers.Count == 0) return;
            EnsureInit();
            if (!_initialized) return;

            // 1. Backup the current ObstacleGrid.
            Graphics.CopyTexture(NavGrid.ObstacleGrid, _backupTexture);

            // 2. Clear our modulation texture to black (no obstacles).
            var prevRT = RenderTexture.active;
            RenderTexture.active = _modTexture;
            GL.Clear(false, true, Color.black);
            RenderTexture.active = prevRT;

            // 3. Render each blocker as a white quad into the modulation texture.
            RenderTexture.active = _modTexture;
            _blockMat.SetPass(0);

            GL.PushMatrix();
            GL.LoadPixelMatrix(0, _modTexture.width, 0, _modTexture.height);

            GL.Begin(GL.QUADS);
            foreach (var b in _blockers)
            {
                // Convert world position to grid pixel coordinates.
                // gridX = (worldX - gpos.x) / us, scaled to texture pixels.
                float gridScale = NavGrid.gs; // Size (e.g., 1000)
                float cellSize = NavGrid.us;  // GridWidth (world units per cell)

                float px = (b.WorldCenter.x - NavGrid.gpos.x) / cellSize / gridScale * _modTexture.width;
                float pz = (b.WorldCenter.z - NavGrid.gpos.z) / cellSize / gridScale * _modTexture.height;
                float pw = b.WorldWidth / cellSize / gridScale * _modTexture.width;
                float pd = b.WorldDepth / cellSize / gridScale * _modTexture.height;

                // Set color with strength as alpha (R channel for R8).
                // For R8 format, we use the red channel.
                // Multiply by global BlockStrength for user control.
                GL.Color(new Color(b.Strength * BlockStrength, 0, 0, 1));

                // Draw quad centered at (px, pz).
                GL.Vertex3(px - pw * 0.5f, pz - pd * 0.5f, 0);
                GL.Vertex3(px + pw * 0.5f, pz - pd * 0.5f, 0);
                GL.Vertex3(px + pw * 0.5f, pz + pd * 0.5f, 0);
                GL.Vertex3(px - pw * 0.5f, pz + pd * 0.5f, 0);
            }
            GL.End();

            GL.PopMatrix();
            RenderTexture.active = prevRT;

            // 4. Add our modulation to ObstacleGrid (additive blend).
            // We use Graphics.Blit with a blend material to add our obstacles
            // on top of the game's existing obstacles.
            // Since we can't easily do custom blend with Blit, we'll use
            // a different approach: set the render target to ObstacleGrid
            // and render our modulation texture on top with additive blending.
            RenderTexture.active = NavGrid.ObstacleGrid;
            GL.PushMatrix();
            GL.LoadOrtho();
            _blockMat.SetPass(0);
            // Set the modulation texture for sampling.
            // Actually, for R8 additive, the simplest approach is to
            // use Graphics.Blit with a custom blend material.
            GL.PopMatrix();
            RenderTexture.active = prevRT;

            // Use a blit with additive blend to merge our obstacles.
            // We need a material with Blend One One (additive).
            var addMat = GetAdditiveMaterial();
            Graphics.Blit(_modTexture, NavGrid.ObstacleGrid, addMat);
        }

        private static Material _addMat;
        private static Material GetAdditiveMaterial()
        {
            if (_addMat == null)
            {
                // Create a material that adds the source texture to the destination.
                var shader = Shader.Find("Hidden/BlitAdd");
                if (shader == null)
                {
                    // Fallback: use Unlit/Texture with additive blend via SetPass.
                    shader = Shader.Find("Unlit/Texture");
                }
                _addMat = new Material(shader);
            }
            return _addMat;
        }

        /// <summary>
        /// Restore ObstacleGrid from backup after the game's dispatch.
        /// Called in NavGrid.Update postfix.
        /// </summary>
        internal static void Restore()
        {
            if (!_initialized || _blockers.Count == 0) return;
            // Restore the original ObstacleGrid.
            Graphics.CopyTexture(_backupTexture, NavGrid.ObstacleGrid);
        }

        // ---- High-level modulation helpers ----

        /// <summary>
        /// Block the direct path between two points to force routing around.
        /// Creates a wall perpendicular to the line between 'from' and 'to'.
        /// </summary>
        internal static void BlockDirectPath(Vector3 from, Vector3 to, float wallWidth = 60f, float strength = 0.8f)
        {
            Vector3 dir = (to - from).normalized;
            dir.y = 0;
            if (dir.sqrMagnitude < 0.01f) return;

            // Perpendicular direction (the wall extends sideways).
            Vector3 perp = new Vector3(-dir.z, 0, dir.x).normalized;

            // Place the wall at 40% of the way from 'from' to 'to' —
            // close enough to block the direct path, far enough that the
            // flow field will route around it.
            Vector3 wallCenter = from + (to - from) * 0.4f;

            // The wall is a thin rectangle perpendicular to the path direction.
            AddBlocker(wallCenter, wallWidth, 15f, strength);
        }

        /// <summary>
        /// Create a corridor by blocking both sides of a desired path.
        /// Units will be funneled through the gap between the two walls.
        /// </summary>
        internal static void CreateCorridor(Vector3 from, Vector3 to, float corridorWidth = 80f, float strength = 0.7f)
        {
            Vector3 dir = (to - from).normalized;
            dir.y = 0;
            if (dir.sqrMagnitude < 0.01f) return;

            Vector3 perp = new Vector3(-dir.z, 0, dir.x).normalized;
            float pathLength = Vector3.Distance(from, to);
            Vector3 midpoint = (from + to) * 0.5f;

            // Two walls parallel to the path, offset by corridorWidth on each side.
            Vector3 wall1Center = midpoint + perp * corridorWidth;
            Vector3 wall2Center = midpoint - perp * corridorWidth;

            // Walls run along the path direction.
            AddBlocker(wall1Center, 15f, pathLength * 0.7f, strength);
            AddBlocker(wall2Center, 15f, pathLength * 0.7f, strength);
        }

        /// <summary>
        /// Create a dispersion fan behind a target point.
        /// Multiple small blocks radiating outward, spreading the flow field
        /// attractor so units approach from a wider angle.
        /// </summary>
        internal static void CreateDispersionFan(Vector3 target, Vector3 approachDir, float radius = 100f, int numBlocks = 5, float strength = 0.4f)
        {
            approachDir.y = 0;
            if (approachDir.sqrMagnitude < 0.01f) approachDir = Vector3.forward;
            approachDir.Normalize();

            // Place blocks in an arc behind the target, perpendicular to approach.
            Vector3 perp = new Vector3(-approachDir.z, 0, approachDir.x).normalized;

            for (int i = 0; i < numBlocks; i++)
            {
                float t = numBlocks == 1 ? 0f : (float)i / (numBlocks - 1) - 0.5f;
                Vector3 offset = perp * t * radius * 2f - approachDir * radius * 0.3f;
                Vector3 blockPos = target + offset;
                AddBlocker(blockPos, 20f, 20f, strength);
            }
        }

        /// <summary>
        /// Create a retreat wavefield that drives units backward away from the enemy.
        ///
        /// This is the inverse of a blocker — instead of stopping movement, it
        /// creates a "push" effect by:
        ///   1. Walling off the forward path (between unit and enemy) so units
        ///      can't drift back toward the enemy
        ///   2. Creating a fan of soft obstacles BEHIND the unit on the sides,
        ///      funneling them into a clean retreat corridor
        ///   3. The NavGrid target (placed by SubGroupFracture) pulls them back
        ///
        /// Combined, this creates a wave that pushes units backward: the forward
        /// path is blocked, the sides are walled, and the backward path is clear
        /// with a pull target. Units have no choice but to retreat.
        /// </summary>
        /// <param name="unitPos">Current position of the retreating unit group</param>
        /// <param name="enemyPos">Position of the nearest enemy threat</param>
        /// <param name="retreatDest">Where we want them to go (behind friendly lines)</param>
        /// <param name="strength">Obstacle strength (0=off, 1=hard wall)</param>
        internal static void CreateRetreatWavefield(
            Vector3 unitPos, Vector3 enemyPos, Vector3 retreatDest, float strength = 0.8f)
        {
            Vector3 awayFromEnemy = (unitPos - enemyPos).normalized;
            awayFromEnemy.y = 0;
            if (awayFromEnemy.sqrMagnitude < 0.01f) awayFromEnemy = Vector3.forward;

            // Perpendicular to the retreat direction (across the front).
            Vector3 right = new Vector3(-awayFromEnemy.z, 0, awayFromEnemy.x).normalized;

            float distToEnemy = Vector3.Distance(unitPos, enemyPos);
            float distToRetreat = Vector3.Distance(unitPos, retreatDest);

            // 1. Forward wall — between the unit and the enemy.
            //    Place it at 40% of the distance to the enemy, close enough
            //    to block forward drift but not on top of the unit.
            float wallDist = Mathf.Min(distToEnemy * 0.4f, 80f);
            Vector3 forwardWallPos = unitPos - awayFromEnemy * wallDist;
            // Wide wall to block the entire forward front.
            float wallWidth = 120f;
            AddBlocker(forwardWallPos, wallWidth, 20f, strength);

            // 2. Side funnels — diagonal walls on both sides of the retreat path,
            //    angled backward to funnel units into the retreat corridor.
            //    These prevent units from scattering sideways during retreat.
            if (distToRetreat > 30f)
            {
                float funnelSpacing = 60f;
                float funnelLength = Mathf.Min(distToRetreat * 0.5f, 100f);

                // Left funnel — diagonal wall from the unit's left side angling back.
                Vector3 leftFunnelCenter = unitPos + right * funnelSpacing - awayFromEnemy * funnelLength * 0.3f;
                AddBlocker(leftFunnelCenter, 15f, funnelLength, strength * 0.6f);

                // Right funnel — mirror.
                Vector3 rightFunnelCenter = unitPos - right * funnelSpacing - awayFromEnemy * funnelLength * 0.3f;
                AddBlocker(rightFunnelCenter, 15f, funnelLength, strength * 0.6f);
            }

            // 3. Staggered rear blockers — a few soft obstacles behind the unit
            //    on alternating sides, creating a braided retreat corridor that
            //    keeps units moving backward in a mixed stream rather than a
            //    rigid column. Same braiding concept as the advance, but reversed.
            if (distToRetreat > 80f && strength > 0.3f)
            {
                int numZags = Mathf.Clamp(Mathf.RoundToInt(distToRetreat / 100f), 2, 5);
                for (int z = 0; z < numZags; z++)
                {
                    float t = (float)(z + 1) / (numZags + 1);
                    Vector3 along = unitPos + (retreatDest - unitPos) * t;
                    bool blockRight = (z % 2 == 0);
                    Vector3 blockOffset = (blockRight ? right : -right) * 30f;
                    Vector3 blockCenter = along + blockOffset;
                    AddBlocker(blockCenter, 50f, 10f, strength * 0.3f);
                }
            }
        }

        /// <summary>
        /// Block all direct approaches to a fortification except the assigned flank.
        /// This forces attacking units to route to their assigned flank point
        /// instead of beelining for the fort center.
        /// </summary>
        internal static void BlockFortificationApproaches(
            Vector3 fortCenter, float fortRadius, Vector3 allowedFlank, float strength = 0.7f)
        {
            // Block the 3 cardinal directions that are NOT the allowed flank.
            float offset = fortRadius + 60f;
            Vector3[] directions = {
                new Vector3(offset, 0, 0),    // east
                new Vector3(-offset, 0, 0),   // west
                new Vector3(0, 0, offset),    // north
                new Vector3(0, 0, -offset),   // south
            };

            foreach (var dir in directions)
            {
                Vector3 blockPos = fortCenter + dir;
                // Don't block the allowed flank direction.
                float angleToFlank = Vector3.Angle(
                    (allowedFlank - fortCenter).normalized,
                    dir.normalized);
                if (angleToFlank < 60f) continue; // This is the allowed approach — don't block it.

                // Place a wall between the fort and this direction.
                AddBlocker(blockPos, 80f, 15f, strength);
            }
        }
    }
}
