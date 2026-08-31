using HarmonyLib;
using UnityEngine;

namespace UEBS2PathingMod
{
    /// <summary>
    /// Battlefield paint mode for manually placing flow field blockers.
    ///
    /// This is SEPARATE from the settings UI (PathingModUI):
    ///   - Settings UI (Numpad+): captures mouse, shows tuning sliders
    ///   - Paint mode (Numpad-):  game mouse stays active, click to place blockers
    ///
    /// In paint mode:
    ///   - Left click: place a blocker at the mouse's world position on terrain
    ///   - Right click: remove nearest blocker
    ///   - Shift+scroll: change blocker size
    ///   - Numpad- again: exit paint mode (clears all manual blockers)
    ///
    /// The camera works normally (RTS pan/zoom) so you can position yourself
    /// and then click to paint. A small HUD shows current tool state.
    /// </summary>
    public static class PaintModeUI
    {
        internal static bool Active => _active;
        private static bool _active;

        // Current brush settings.
        private static float _brushWidth = 60f;
        private static float _brushDepth = 60f;
        private static float _brushStrength = 0.8f;
        private static float _corridorWidth = 80f;

        // Manual blockers placed by the user. These persist until removed.
        private static readonly System.Collections.Generic.List<FlowFieldModulator.Blocker> _manualBlockers =
            new System.Collections.Generic.List<FlowFieldModulator.Blocker>();

        // Tool modes.
        public enum PaintTool
        {
            Block,      // place obstacle
            Corridor,   // click two points to create a corridor
            Clear,      // remove blockers
        }
        private static PaintTool _tool = PaintTool.Block;

        // Corridor placement needs two clicks.
        private static bool _corridorFirstClick = true;
        private static Vector3 _corridorStart;

        // HUD window — auto-sized like the settings UI.
        private static Rect _hud = new Rect(Screen.width - 320, 20, 300, 400);
        private static Vector2 _hudScroll;
        private static float _hudContentHeight = 400;

        /// <summary>
        /// Toggle paint mode. Called from ThreadManager.Update prefix.
        /// </summary>
        internal static void Toggle()
        {
            _active = !_active;
            if (_active)
            {
                // Entering paint mode — make sure cursor is visible for RTS clicking.
                Cursor.visible = true;
                Cursor.lockState = CursorLockMode.None;
                // Reposition HUD to top-right on first open.
                _hud.x = Screen.width - 320;
                _hud.y = 20;
            }
            else
            {
                // Exiting paint mode — clear manual blockers.
                _manualBlockers.Clear();
                FlowFieldModulator.ClearBlockers();
            }
        }

        /// <summary>
        /// Per-frame logic: handle input for placing/removing blockers.
        /// Called from ThreadManager.Update prefix (after PathingModUI check).
        /// </summary>
        internal static void Tick()
        {
            if (!_active) return;

            // Brush size with shift+scroll.
            if (Input.GetKey(KeyCode.LeftShift) && Input.GetAxis("Mouse ScrollWheel") != 0f)
            {
                float scroll = Input.GetAxis("Mouse ScrollWheel");
                _brushWidth = Mathf.Clamp(_brushWidth + scroll * 30f, 10f, 300f);
                _brushDepth = _brushWidth; // keep square by default
            }

            // Tool switching with number keys.
            if (Input.GetKeyUp(KeyCode.Alpha1)) _tool = PaintTool.Block;
            if (Input.GetKeyUp(KeyCode.Alpha2)) { _tool = PaintTool.Corridor; _corridorFirstClick = true; }
            if (Input.GetKeyUp(KeyCode.Alpha3)) _tool = PaintTool.Clear;

            // Left click — place blocker or corridor point.
            if (Input.GetMouseButtonUp(0) && !PathingModUI.Visible)
            {
                Vector3? worldPos = GetMouseWorldPosition();
                if (worldPos.HasValue)
                {
                    if (_tool == PaintTool.Block)
                    {
                        _manualBlockers.Add(new FlowFieldModulator.Blocker
                        {
                            WorldCenter = worldPos.Value,
                            WorldWidth = _brushWidth,
                            WorldDepth = _brushDepth,
                            Strength = _brushStrength,
                        });
                    }
                    else if (_tool == PaintTool.Corridor)
                    {
                        if (_corridorFirstClick)
                        {
                            _corridorStart = worldPos.Value;
                            _corridorFirstClick = false;
                        }
                        else
                        {
                            // Create corridor between the two points.
                            CreateCorridorBlockers(_corridorStart, worldPos.Value);
                            _corridorFirstClick = true;
                        }
                    }
                }
            }

            // Right click — remove nearest blocker (Block tool) or cancel corridor.
            if (Input.GetMouseButtonUp(1))
            {
                if (_tool == PaintTool.Corridor && !_corridorFirstClick)
                {
                    _corridorFirstClick = true; // cancel corridor placement
                }
                else
                {
                    Vector3? worldPos = GetMouseWorldPosition();
                    if (worldPos.HasValue)
                    {
                        RemoveNearestBlocker(worldPos.Value);
                    }
                }
            }

            // Sync manual blockers to the modulator each frame.
            // The fracture system also adds blockers, so we merge.
            // FlowFieldModulator.ClearBlockers() is called by the fracture
            // system each cycle; we re-add our manual ones here.
            foreach (var b in _manualBlockers)
            {
                FlowFieldModulator.AddBlocker(b.WorldCenter, b.WorldWidth, b.WorldDepth, b.Strength);
            }
        }

        /// <summary>
        /// Get the world position where the mouse is pointing on the terrain.
        /// </summary>
        private static Vector3? GetMouseWorldPosition()
        {
            Ray ray = Camera.main.ScreenPointToRay(Input.mousePosition);
            if (Physics.Raycast(ray, out var hit, 10000f, NavGrid.walkable))
            {
                return hit.point;
            }
            return null;
        }

        /// <summary>
        /// Create corridor blockers between two points.
        /// Two parallel walls funnel units through the corridor.
        /// </summary>
        private static void CreateCorridorBlockers(Vector3 from, Vector3 to)
        {
            Vector3 dir = (to - from).normalized;
            dir.y = 0;
            if (dir.sqrMagnitude < 0.01f) return;

            Vector3 perp = new Vector3(-dir.z, 0, dir.x).normalized;
            float pathLength = Vector3.Distance(from, to);
            Vector3 midpoint = (from + to) * 0.5f;

            float corridorWidth = _corridorWidth;

            // Two walls parallel to the path, offset by corridorWidth.
            _manualBlockers.Add(new FlowFieldModulator.Blocker
            {
                WorldCenter = midpoint + perp * corridorWidth,
                WorldWidth = 15f,
                WorldDepth = pathLength * 0.8f,
                Strength = _brushStrength,
            });
            _manualBlockers.Add(new FlowFieldModulator.Blocker
            {
                WorldCenter = midpoint - perp * corridorWidth,
                WorldWidth = 15f,
                WorldDepth = pathLength * 0.8f,
                Strength = _brushStrength,
            });
        }

        /// <summary>
        /// Remove the blocker nearest to the given world position.
        /// </summary>
        private static void RemoveNearestBlocker(Vector3 pos)
        {
            if (_manualBlockers.Count == 0) return;

            int nearestIdx = 0;
            float nearestDist = float.MaxValue;
            for (int i = 0; i < _manualBlockers.Count; i++)
            {
                float d = Vector3.Distance(_manualBlockers[i].WorldCenter, pos);
                if (d < nearestDist)
                {
                    nearestDist = d;
                    nearestIdx = i;
                }
            }

            // Only remove if within a reasonable distance.
            if (nearestDist < 200f)
            {
                _manualBlockers.RemoveAt(nearestIdx);
            }
        }

        /// <summary>
        /// Clear all manual blockers.
        /// </summary>
        internal static void ClearAll()
        {
            _manualBlockers.Clear();
        }

        /// <summary>
        /// Draw the paint mode HUD. Called from ThreadManager.OnGUI postfix.
        /// Uses GUILayout.Window with scroll view — same approach as the settings UI.
        /// </summary>
        internal static void DrawHUD()
        {
            if (!_active) return;

            EnsureStyles();
            _hud = GUILayout.Window(9876544, _hud, DrawHUDWindow, "Paint Mode");
        }

        private static GUIStyle _hudHeader;
        private static GUIStyle _hudSmall;
        private static GUIStyle _hudBox;
        private static bool _hudStylesReady;

        private static void EnsureStyles()
        {
            if (_hudStylesReady) return;
            _hudHeader = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold, fontSize = 13 };
            _hudSmall = new GUIStyle(GUI.skin.label) { fontSize = 10, richText = true };
            _hudBox = new GUIStyle(GUI.skin.box);
            _hudStylesReady = true;
        }

        private static void DrawHUDWindow(int id)
        {
            EnsureStyles();

            // Scroll view with auto-sized content height (same pattern as settings UI).
            var scrollRect = new Rect(4, 22, _hud.width - 8, _hud.height - 26);
            _hudScroll = GUILayout.BeginScrollView(_hudScroll,
                GUILayout.Width(scrollRect.width), GUILayout.Height(scrollRect.height));

            GUILayout.BeginVertical();
            {
                GUILayout.Label("Flow Field Paint", _hudHeader);

                GUILayout.Space(4);

                // ---- Tool selection ----
                GUILayout.Label("Tool (keys 1/2/3):", _hudSmall);
                _tool = (PaintTool)GUILayout.SelectionGrid((int)_tool,
                    new string[] { "Block", "Corridor", "Clear" }, 3);

                GUILayout.Space(6);

                // ---- Brush settings ----
                GUILayout.Label("Brush", _hudHeader);
                GUILayout.Label($"  Size: {_brushWidth:F0}  (Shift+Scroll to adjust)", _hudSmall);
                _brushWidth = GUILayout.HorizontalSlider(_brushWidth, 10f, 300f);
                _brushDepth = _brushWidth;

                GUILayout.Label($"  Strength: {_brushStrength:F2}  (0.1=soft, 1.0=hard wall)", _hudSmall);
                _brushStrength = GUILayout.HorizontalSlider(_brushStrength, 0.1f, 1f);

                GUILayout.Space(6);

                // ---- Corridor width ----
                if (_tool == PaintTool.Corridor)
                {
                    GUILayout.Label($"  Corridor width: {_corridorWidth:F0}", _hudSmall);
                    _corridorWidth = GUILayout.HorizontalSlider(_corridorWidth, 20f, 200f);
                    GUILayout.Space(4);
                }

                // ---- Status ----
                GUILayout.Label("Status", _hudHeader);
                GUILayout.Label($"  Active blockers: {_manualBlockers.Count}", _hudSmall);

                if (_tool == PaintTool.Corridor)
                {
                    GUILayout.Space(2);
                    if (_corridorFirstClick)
                        GUILayout.Label("  <color=yellow>Click first point...</color>", _hudSmall);
                    else
                        GUILayout.Label("  <color=yellow>Click second point...</color>", _hudSmall);
                }

                GUILayout.Space(6);

                // ---- Actions ----
                GUILayout.Label("Actions", _hudHeader);
                if (GUILayout.Button("Clear All Blockers", GUILayout.Height(24)))
                {
                    _manualBlockers.Clear();
                }

                GUILayout.Space(4);

                // ---- Controls help ----
                GUILayout.Label("Controls", _hudHeader);
                GUILayout.Label("  LMB: place blocker / corridor point", _hudSmall);
                GUILayout.Label("  RMB: remove nearest blocker / cancel corridor", _hudSmall);
                GUILayout.Label("  Shift+Scroll: adjust brush size", _hudSmall);
                GUILayout.Label("  1: Block tool  |  2: Corridor  |  3: Clear", _hudSmall);
                GUILayout.Label("  Numpad9: exit paint mode", _hudSmall);

                GUILayout.Space(6);
                GUILayout.Label("Numpad+ for full settings window.", _hudSmall);
            }
            GUILayout.EndVertical();

            // Capture content height for scroll sizing.
            if (Event.current.type == EventType.Repaint)
            {
                _hudContentHeight = GUILayoutUtility.GetLastRect().height + 20f;
            }

            GUILayout.EndScrollView();

            // Drag handle at the top.
            GUI.DragWindow(new Rect(0, 0, 10000, 22));
        }
    }
}
