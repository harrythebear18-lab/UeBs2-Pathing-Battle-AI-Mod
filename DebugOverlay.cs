using System.Collections.Generic;
using UnityEngine;

namespace UEBS2PathingMod
{
    /// <summary>
    /// Visual debug overlay for the battle AI systems.
    ///
    /// Renders world-space markers and screen-space labels on top of the
    /// battlefield so you can visually confirm:
    ///   - Threat scores (per enemy cluster, color-coded by magnitude)
    ///   - Cohesion state (per friendly sub-group, realign status)
    ///   - Realign triggers (flash line from old target to new target)
    ///   - Flow-field modulation zones (blockers, corridors, retreat waves)
    ///   - Momentum curves (per sub-group, scrolling graph)
    ///
    /// Toggled with Numpad7. Individual layers can be enabled/disabled
    /// from the settings UI.
    /// </summary>
    internal class DebugOverlay : MonoBehaviour
    {
        // ---- Layer toggles ----
        internal bool ShowThreat = true;
        internal bool ShowCohesion = true;
        internal bool ShowRealign = true;
        internal bool ShowFlowField = true;
        internal bool ShowMomentum = true;
        internal bool Enabled = false;

        // ---- Momentum history (for scrolling curve graph) ----
        private class MomentumHistory
        {
            public int Team;
            public Color Color;
            public readonly float[] Buffer = new float[MaxHistoryPoints];
            public int Head;
        }
        private const int MaxHistoryPoints = 180; // ~3 seconds at 60fps
        private readonly List<MomentumHistory> _momentumHistories = new List<MomentumHistory>();
        private float _momentumSampleTimer;

        // ---- Realign flash tracking ----
        private struct RealignFlash
        {
            public Vector3 FromPos;     // old enemy position
            public Vector3 ToPos;       // new enemy position
            public Vector3 GroupPos;    // sub-group position
            public float StartTime;
        }
        private readonly List<RealignFlash> _realignFlashes = new List<RealignFlash>();
        private const float FlashDuration = 3f;

        // ---- Colors ----
        private static readonly Color _lowThreat = new Color(0.4f, 0.8f, 0.4f, 0.7f);   // green
        private static readonly Color _medThreat = new Color(0.9f, 0.8f, 0.2f, 0.7f);   // yellow
        private static readonly Color _highThreat = new Color(0.9f, 0.3f, 0.2f, 0.8f);  // red
        private static readonly Color _realignColor = new Color(0.2f, 0.9f, 1f, 0.9f);  // cyan
        private static readonly Color _blockerColor = new Color(0.9f, 0.3f, 0.2f, 0.3f);// red, transparent
        private static readonly Color _corridorColor = new Color(0.2f, 0.9f, 0.3f, 0.3f);// green, transparent
        private static readonly Color _retreatColor = new Color(0.9f, 0.6f, 0.2f, 0.3f);// orange, transparent

        // ---- Textures for drawing ----
        private static Texture2D _whiteTex;
        private static GUIStyle _labelStyle;
        private static GUIStyle _smallStyle;
        private static bool _stylesReady;

        // ---- Realign detection (compare each frame) ----
        private bool _wasRealigning;
        private Vector3 _lastRealignFrom;
        private Vector3 _lastRealignTo;
        private Vector3 _lastRealignGroup;

        private void EnsureStyles()
        {
            if (_stylesReady) return;
            _whiteTex = new Texture2D(1, 1, TextureFormat.ARGB32, false);
            _whiteTex.SetPixel(0, 0, Color.white);
            _whiteTex.Apply();
            _labelStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 11,
                fontStyle = FontStyle.Bold,
                richText = true,
                alignment = TextAnchor.MiddleCenter
            };
            _smallStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 9,
                richText = true
            };
            _stylesReady = true;
        }

        internal void Update()
        {
            if (!Enabled) return;

            // Sample momentum for curve graph.
            _momentumSampleTimer += Time.deltaTime;
            if (_momentumSampleTimer >= 0.05f) // 20 samples/sec
            {
                _momentumSampleTimer = 0f;
                SampleMomentum();
            }

            // Clean up expired realign flashes.
            for (int i = _realignFlashes.Count - 1; i >= 0; i--)
            {
                if (Time.time - _realignFlashes[i].StartTime > FlashDuration)
                    _realignFlashes.RemoveAt(i);
            }
        }

        /// <summary>Called by SubGroupFracture when a Realign action is issued.</summary>
        internal void RecordRealign(Vector3 groupPos, Vector3 fromEnemy, Vector3 toEnemy)
        {
            _realignFlashes.Add(new RealignFlash
            {
                FromPos = fromEnemy,
                ToPos = toEnemy,
                GroupPos = groupPos,
                StartTime = Time.time
            });
        }

        private void SampleMomentum()
        {
            var states = BattleMomentum.States;
            // Ensure we have a history entry for each state.
            while (_momentumHistories.Count < states.Count)
            {
                _momentumHistories.Add(new MomentumHistory
                {
                    Team = states[_momentumHistories.Count].Team,
                    Color = GetTeamColor(states[_momentumHistories.Count].Team)
                });
            }
            // Remove excess.
            while (_momentumHistories.Count > states.Count)
                _momentumHistories.RemoveAt(_momentumHistories.Count - 1);

            for (int i = 0; i < states.Count && i < _momentumHistories.Count; i++)
            {
                var h = _momentumHistories[i];
                h.Team = states[i].Team;
                h.Buffer[h.Head] = states[i].Momentum;
                h.Head = (h.Head + 1) % MaxHistoryPoints;
            }
        }

        private static Color GetTeamColor(int team)
        {
            switch (team % 6)
            {
                case 0: return new Color(0.3f, 0.6f, 1f);    // blue
                case 1: return new Color(1f, 0.3f, 0.3f);    // red
                case 2: return new Color(0.3f, 0.9f, 0.3f);  // green
                case 3: return new Color(1f, 0.8f, 0.2f);    // yellow
                case 4: return new Color(0.9f, 0.3f, 0.9f);  // magenta
                default: return new Color(0.3f, 0.9f, 0.9f); // cyan
            }
        }

        // ---- Main render ----

        internal void OnGUI()
        {
            if (!Enabled) return;
            EnsureStyles();

            if (ShowThreat) DrawThreatScores();
            if (ShowCohesion) DrawCohesionState();
            if (ShowRealign) DrawRealignFlashes();
            if (ShowFlowField) DrawFlowFieldZones();
            if (ShowMomentum) DrawMomentumGraph();
        }

        // ---- 1. Threat scores ----
        // Draws a colored marker + score at each enemy sub-group position.
        // Color: green (low) → yellow (medium) → red (high).

        private void DrawThreatScores()
        {
            var stats = SubGroupFracture.GetStats();
            if (stats == null) return;

            // We need the high-threat data from the sub-groups. GetStats doesn't
            // include it, so we read from the fracture system's internal state
            // via a helper. For now, we'll draw threat scores at each sub-group's
            // nearest enemy position, color-coded by the threat score magnitude.
            //
            // Since SubGroup is private, we use the stats we have and project
            // positions. We'll draw at each sub-group's centroid showing their
            // highest threat score.

            // Actually, the threat data is internal to SubGroupFracture.
            // Let's add a public getter for the debug overlay.
            var threatData = SubGroupFracture.GetThreatDebugData();
            if (threatData == null) return;

            float maxScore = 0.001f;
            foreach (var td in threatData)
                if (td.Score > maxScore) maxScore = td.Score;

            foreach (var td in threatData)
            {
                var screenPos = WorldToScreen(td.EnemyPos);
                if (!screenPos.HasValue) continue;

                float normalized = td.Score / maxScore;
                Color color = normalized < 0.33f ? _lowThreat
                    : normalized < 0.66f ? _medThreat : _highThreat;

                // Draw a colored circle (approximated with a box).
                int size = Mathf.RoundToInt(20 + normalized * 30);
                DrawBox(screenPos.Value, size, size, color);

                // Draw threat score label.
                string label = $"T{td.Team}\n{td.Score:F0}\n{td.Remaining}u";
                DrawLabel(screenPos.Value + new Vector2(0, -size), label, color);
            }
        }

        // ---- 2. Cohesion state ----
        // Draws each friendly sub-group's cohesion state: current action,
        // whether realign is pending, current vs high-threat target.

        private void DrawCohesionState()
        {
            var cohesionData = SubGroupFracture.GetCohesionDebugData();
            if (cohesionData == null) return;

            foreach (var cd in cohesionData)
            {
                var screenPos = WorldToScreen(cd.GroupPos);
                if (!screenPos.HasValue) continue;

                Color color = cd.ShouldRealign ? _realignColor
                    : cd.IsEngaged ? _medThreat : _lowThreat;

                // Draw marker at the sub-group.
                DrawBox(screenPos.Value, 16, 16, color);

                // Draw state label.
                string label = $"<color=#{ColorToHex(color)}>T{cd.Team} {cd.Action}</color>";
                if (cd.ShouldRealign)
                    label += "\n<color=cyan>REALIGN!</color>";
                label += $"\n{cd.CurrentRemaining}vs{cd.HighThreatRemaining}";
                DrawLabel(screenPos.Value + new Vector2(0, -20), label, Color.white);
            }
        }

        // ---- 3. Realign triggers ----
        // Draws a bright line from old target → group → new target when a
        // realign happens. Fades over FlashDuration seconds.

        private void DrawRealignFlashes()
        {
            foreach (var flash in _realignFlashes)
            {
                float age = Time.time - flash.StartTime;
                float alpha = 1f - (age / FlashDuration);
                if (alpha <= 0) continue;

                Color color = new Color(_realignColor.r, _realignColor.g, _realignColor.b, alpha);

                var groupScreen = WorldToScreen(flash.GroupPos);
                var fromScreen = WorldToScreen(flash.FromPos);
                var toScreen = WorldToScreen(flash.ToPos);

                // Draw line: old target → group (red, what we're leaving)
                if (fromScreen.HasValue && groupScreen.HasValue)
                    DrawLine(fromScreen.Value, groupScreen.Value,
                        new Color(0.9f, 0.3f, 0.2f, alpha * 0.7f), 3);

                // Draw line: group → new target (cyan, what we're switching to)
                if (groupScreen.HasValue && toScreen.HasValue)
                    DrawLine(groupScreen.Value, toScreen.Value, color, 3);

                // Label at new target.
                if (toScreen.HasValue)
                {
                    DrawBox(toScreen.Value, 24, 24, color);
                    DrawLabel(toScreen.Value + new Vector2(0, -30),
                        $"<color=cyan>NEW TARGET</color>", Color.white);
                }

                // Label at old target.
                if (fromScreen.HasValue)
                {
                    DrawLabel(fromScreen.Value + new Vector2(0, -20),
                        $"<color=red>OLD (abandoned)</color>", Color.white);
                }
            }
        }

        // ---- 4. Flow-field modulation zones ----
        // Draws semi-transparent rectangles at each active blocker position.
        // Red = block, green = corridor, orange = retreat wave.

        private void DrawFlowFieldZones()
        {
            var blockers = FlowFieldModulator.GetBlockersForDebug();
            if (blockers == null) return;

            foreach (var b in blockers)
            {
                var screenPos = WorldToScreen(b.WorldCenter);
                if (!screenPos.HasValue) continue;

                // Estimate screen size based on world size and distance.
                float dist = Vector3.Distance(Camera.main.transform.position, b.WorldCenter);
                float screenScale = Mathf.Clamp(500f / dist, 0.5f, 5f);
                int w = Mathf.RoundToInt(b.WorldWidth * screenScale * 0.1f);
                int h = Mathf.RoundToInt(b.WorldDepth * screenScale * 0.1f);
                w = Mathf.Clamp(w, 4, 200);
                h = Mathf.Clamp(h, 4, 200);

                // Color by strength: strong = red, medium = orange, soft = green.
                Color color;
                if (b.Strength > 0.7f)
                    color = _blockerColor;
                else if (b.Strength > 0.4f)
                    color = _retreatColor;
                else
                    color = _corridorColor;

                DrawBox(screenPos.Value, w, h, color);

                // Draw border.
                DrawBoxOutline(screenPos.Value, w, h, new Color(color.r, color.g, color.b, 0.8f));
            }
        }

        // ---- 5. Momentum curves ----
        // Draws a scrolling graph in the bottom-left corner showing each
        // sub-group's momentum over the last ~3 seconds.

        private void DrawMomentumGraph()
        {
            if (_momentumHistories.Count == 0) return;

            // Graph area.
            const int graphW = 300;
            const int graphH = 120;
            const int graphX = 10;
            int graphY = Screen.height - graphH - 10;

            // Background.
            DrawRect(new Rect(graphX, graphY, graphW, graphH),
                new Color(0, 0, 0, 0.7f));

            // Border.
            DrawRectOutline(new Rect(graphX, graphY, graphW, graphH),
                new Color(1, 1, 1, 0.3f));

            // Title.
            GUI.Label(new Rect(graphX + 5, graphY + 2, graphW - 10, 14),
                "<b>Momentum (Ebb & Flow)</b>", _smallStyle);

            // Grid lines at 0.0, 0.2, 0.4, 0.6, 0.8, 1.0.
            for (int g = 0; g <= 5; g++)
            {
                float y = graphY + graphH - 16 - (g / 5f) * (graphH - 24);
                DrawLine(new Vector2(graphX + 5, y), new Vector2(graphX + graphW - 5, y),
                    new Color(1, 1, 1, 0.1f), 1);
                GUI.Label(new Rect(graphX + graphW - 30, y - 6, 25, 12),
                    $"{g * 0.2f:F1}", _smallStyle);
            }

            // Draw each sub-group's curve.
            int plotX = graphX + 5;
            int plotW = graphW - 40;
            int plotY = graphY + 16;
            int plotH = graphH - 24;

            foreach (var h in _momentumHistories)
            {
                // Draw the curve by connecting consecutive samples.
                Vector2 prev = Vector2.zero;
                bool hasPrev = false;
                for (int i = 0; i < MaxHistoryPoints; i++)
                {
                    int idx = (h.Head + i) % MaxHistoryPoints;
                    float val = h.Buffer[idx];
                    float x = plotX + (float)i / MaxHistoryPoints * plotW;
                    float y = plotY + plotH - val * plotH;
                    var cur = new Vector2(x, y);
                    if (hasPrev)
                        DrawLine(prev, cur, h.Color, 1.5f);
                    prev = cur;
                    hasPrev = true;
                }

                // Legend entry.
                int legendY = graphY + 2;
                int legendX = graphX + 130;
                int idx2 = _momentumHistories.IndexOf(h);
                DrawRect(new Rect(legendX + idx2 * 60, legendY, 8, 8), h.Color);
                GUI.Label(new Rect(legendX + idx2 * 60 + 10, legendY - 2, 50, 12),
                    $"T{h.Team}", _smallStyle);
            }
        }

        // ---- Drawing helpers ----

        private static Vector2? WorldToScreen(Vector3 world)
        {
            if (Camera.main == null) return null;
            Vector3 screen = Camera.main.WorldToScreenPoint(world);
            if (screen.z <= 0) return null; // behind camera
            // Convert from Unity screen coords (bottom-left origin) to GUI coords (top-left).
            return new Vector2(screen.x, Screen.height - screen.y);
        }

        private static void DrawBox(Vector2 center, int w, int h, Color color)
        {
            var rect = new Rect(center.x - w * 0.5f, center.y - h * 0.5f, w, h);
            DrawRect(rect, color);
        }

        private static void DrawBoxOutline(Vector2 center, int w, int h, Color color)
        {
            var rect = new Rect(center.x - w * 0.5f, center.y - h * 0.5f, w, h);
            DrawRectOutline(rect, color);
        }

        private static void DrawRect(Rect rect, Color color)
        {
            Color prev = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(rect, _whiteTex);
            GUI.color = prev;
        }

        private static void DrawRectOutline(Rect rect, Color color)
        {
            DrawRect(new Rect(rect.x, rect.y, rect.width, 1), color);
            DrawRect(new Rect(rect.x, rect.y + rect.height - 1, rect.width, 1), color);
            DrawRect(new Rect(rect.x, rect.y, 1, rect.height), color);
            DrawRect(new Rect(rect.x + rect.width - 1, rect.y, 1, rect.height), color);
        }

        private static void DrawLine(Vector2 a, Vector2 b, Color color, float width)
        {
            // Save state.
            Color prevColor = GUI.color;
            Matrix4x4 prevMatrix = GUI.matrix;
            GUI.color = color;

            var delta = b - a;
            float angle = Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg;
            float length = delta.magnitude;

            // Rotate a 1x1 texture to form a line.
            GUIUtility.RotateAroundPivot(angle, a);
            GUI.DrawTexture(new Rect(a.x, a.y - width * 0.5f, length, width), _whiteTex);

            GUI.matrix = prevMatrix;
            GUI.color = prevColor;
        }

        private static void DrawLabel(Vector2 pos, string text, Color color)
        {
            var content = new GUIContent(text);
            Vector2 size = _labelStyle.CalcSize(content);
            var rect = new Rect(pos.x - size.x * 0.5f, pos.y - size.y * 0.5f, size.x, size.y);
            // Background for readability.
            DrawRect(new Rect(rect.x - 2, rect.y - 1, rect.width + 4, rect.height + 2),
                new Color(0, 0, 0, 0.6f));
            Color prev = GUI.color;
            GUI.color = color;
            GUI.Label(rect, text, _labelStyle);
            GUI.color = prev;
        }

        private static string ColorToHex(Color c)
        {
            return $"{(int)(c.r * 255):X2}{(int)(c.g * 255):X2}{(int)(c.b * 255):X2}";
        }
    }
}
