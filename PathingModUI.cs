using HarmonyLib;
using UnityEngine;

namespace UEBS2PathingMod
{
    /// <summary>
    /// In-game tuning window. Toggled with Numpad+.
    /// Renders via Unity's IMGUI (OnGUI) so it works regardless of the
    /// game's uGUI canvas setup. Patched onto the game's ThreadManager
    /// so it ticks every frame in the battle scene.
    /// </summary>
    public static class PathingModUI
    {
        internal static bool Visible => _visible;

        private static bool _visible;
        private static bool _wasVisible;          // edge-detect for cursor restore
        private static Rect _window = new Rect(20, 20, 440, 620);
        private static GUIStyle _header;
        private static GUIStyle _small;
        private static bool _stylesReady;

        // Toggle on Numpad+ every frame via a prefix on ThreadManager.Update.
        [HarmonyPrefix]
        [HarmonyPatch(typeof(ThreadManager), "Update")]
        public static void ThreadManagerUpdatePrefix()
        {
            if (Input.GetKeyUp(KeyCode.KeypadPlus) ||
                (_visible && Input.GetKeyUp(KeyCode.Escape)))
            {
                _visible = !_visible;
            }
        }

        /// <summary>
        /// Prefix on FlyCam.Update: when our UI is open, suppress the camera's
        /// cursor locking and mouse-look so the user can interact with the window.
        /// We zero out the mouse axes and force cursor freedom before the camera
        /// code reads them.
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(FlyCam), "Update")]
        public static void FlyCamUpdatePrefix(FlyCam __instance)
        {
            if (!_visible) return;

            // Force cursor free for the UI.
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;

            // Kill mouse look input so the camera doesn't rotate while we
            // interact with the window. We can't stop Input.GetAxis directly,
            // but we can zero the camera's rotation accumulator after the fact
            // by patching the field via Traverse. Simpler: just set the axes
            // to 0 by overriding the camera rotation after Update runs.
            // Actually the cleanest way: skip the camera Update entirely.
        }

        /// <summary>
        /// Postfix on FlyCam.Update: after the camera has processed its input,
        /// if our UI is open, undo any mouse-look rotation the camera applied
        /// and re-assert cursor freedom (the camera code locks it).
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(FlyCam), "Update")]
        public static void FlyCamUpdatePostfix(FlyCam __instance)
        {
            if (!_visible) return;

            // Re-assert cursor freedom — FlyCam.Update locks it.
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;

            // Zero out the rotation accumulator so mouse movement doesn't
            // accumulate into camera rotation while the UI is open.
            var t = Traverse.Create(__instance);
            t.Field("RotAdd").SetValue(Vector2.zero);
        }

        // Draw the window after the game's own OnGUI pass so it sits on top.
        [HarmonyPostfix]
        [HarmonyPatch(typeof(ThreadManager), "OnGUI")]
        public static void ThreadManagerOnGUIPostfix()
        {
            // When the window was just closed, hand the cursor back to the game.
            if (_wasVisible && !_visible)
            {
                Cursor.visible = false;
                Cursor.lockState = CursorLockMode.Locked;
            }
            _wasVisible = _visible;

            if (!_visible) return;

            // Re-assert cursor freedom every frame while open.
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;

            EnsureStyles();
            // Use GUI.Window (fixed rect) instead of GUILayout.Window.
            // GUILayout.Window + scroll view fights for layout control and
            // collapses to a tiny size. Fixed rect gives us reliable dimensions.
            _window = GUI.Window(9876543, _window, DrawWindow, "UEBS2 Pathing & Team AI");
        }

        private static void EnsureStyles()
        {
            if (_stylesReady) return;
            _header = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold, fontSize = 13 };
            _small = new GUIStyle(GUI.skin.label) { fontSize = 10, richText = true };
            _stylesReady = true;
        }

        private static Vector2 _scroll;
        private static float _contentHeight = 1000; // updated each frame by GUILayout

        private static void DrawWindow(int id)
        {
            var i = PathingModPlugin.Instance;
            if (i == null)
            {
                GUILayout.Label("Mod not loaded.");
                GUI.DragWindow();
                return;
            }

            // Fixed-size scroll view inside the window rect.
            // Leave 20px at top for the title bar drag handle.
            Rect scrollArea = new Rect(10, 25, _window.width - 20, _window.height - 45);
            _scroll = GUI.BeginScrollView(scrollArea, _scroll,
                new Rect(0, 0, _window.width - 40, _contentHeight));

            GUILayout.BeginVertical();
            {
                GUILayout.Space(4);
                GUILayout.Label("General", _header);
                i.ModEnabled.Value = GUILayout.Toggle(i.ModEnabled.Value, "  Mod enabled");
                i.DynamicScaling.Value = GUILayout.Toggle(i.DynamicScaling.Value, "  Dynamic scaling (auto-tune by battle size)");
                GUILayout.Label($"  Aggressiveness: {i.Aggressiveness.Value:F2}");
                i.Aggressiveness.Value = GUILayout.HorizontalSlider(i.Aggressiveness.Value, 0f, 1f);
                GUILayout.Label($"  Effective: {PathingModPlugin.EffectiveAggressiveness:F2}  (AI: {PathingModPlugin.TotalAiCount:N0})",
                    _small);

                GUILayout.Space(6);
                GUILayout.Label("Pathing", _header);
                i.SmartScheduling.Value = GUILayout.Toggle(i.SmartScheduling.Value, "  Smart search scheduling");
                i.StrategicTargeting.Value = GUILayout.Toggle(i.StrategicTargeting.Value, "  Strategic target assignment");
                GUILayout.Label($"  Re-assign interval: {i.StrategicTargetInterval.Value}s");
                i.StrategicTargetInterval.Value = (int)GUILayout.HorizontalSlider(i.StrategicTargetInterval.Value, 1, 20);

                GUILayout.Space(6);
                GUILayout.Label("NavGrid", _header);
                i.NavGridTuning.Value = GUILayout.Toggle(i.NavGridTuning.Value, "  Adaptive NavGrid tuning");
                GUILayout.Label($"  Max active targets cap: {i.MaxActiveTargetsCap.Value}");
                i.MaxActiveTargetsCap.Value = (int)GUILayout.HorizontalSlider(i.MaxActiveTargetsCap.Value, 4, 60);
                GUILayout.Label($"  Obstacle update speed: {i.ObstacleUpdateSpeed.Value:F3}");
                i.ObstacleUpdateSpeed.Value = GUILayout.HorizontalSlider(i.ObstacleUpdateSpeed.Value, 0.01f, 0.2f);
                GUILayout.Label($"  Scaled MaxActiveTargets: {PathingModPlugin.ScaledMaxActiveTargets}", _small);

                GUILayout.Space(6);
                GUILayout.Label("Cinematic Camera", _header);
                i.CinematicCombatBias.Value = GUILayout.Toggle(i.CinematicCombatBias.Value, "  Bias toward active combat/clashes");
                GUILayout.Label($"  Bias strength: {i.CinematicBiasStrength.Value:F2}  (0=random, 1=always combat)");
                i.CinematicBiasStrength.Value = GUILayout.HorizontalSlider(i.CinematicBiasStrength.Value, 0f, 1f);
                GUILayout.Label($"  Focus interval: {i.CinematicFocusInterval.Value:F1}s  (time between cuts)");
                i.CinematicFocusInterval.Value = GUILayout.HorizontalSlider(i.CinematicFocusInterval.Value, 2f, 10f);

                GUILayout.Space(6);
                GUILayout.Label("Sub-Group Fracture", _header);
                i.FractureEnabled.Value = GUILayout.Toggle(i.FractureEnabled.Value, "  Enable sub-group autonomy");
                GUILayout.Label($"  Cluster radius: {i.FractureClusterRadius.Value:F0}  (armies within this dist form a group)");
                i.FractureClusterRadius.Value = GUILayout.HorizontalSlider(i.FractureClusterRadius.Value, 50f, 400f);
                GUILayout.Label($"  Reassess interval: {i.FractureReassessInterval.Value:F1}s");
                i.FractureReassessInterval.Value = GUILayout.HorizontalSlider(i.FractureReassessInterval.Value, 1f, 10f);
                i.FractureReinforce.Value = GUILayout.Toggle(i.FractureReinforce.Value, "  Reinforce losing allies");
                i.FractureRetreat.Value = GUILayout.Toggle(i.FractureRetreat.Value, "  Retreat when isolated & outnumbered");
                i.FracturePursue.Value = GUILayout.Toggle(i.FracturePursue.Value, "  Pursue when winning");
                i.FractureAntiFlank.Value = GUILayout.Toggle(i.FractureAntiFlank.Value, "  Anti-flank repositioning");
                i.FractureMorale.Value = GUILayout.Toggle(i.FractureMorale.Value, "  Morale system (rout/retreat/pursue)");
                GUILayout.Label($"  Rout threshold: {i.FractureRoutThreshold.Value:F2}  (morale below = panic)");
                i.FractureRoutThreshold.Value = GUILayout.HorizontalSlider(i.FractureRoutThreshold.Value, 0.05f, 0.4f);
                GUILayout.Label($"  Retreat threshold: {i.FractureRetreatThreshold.Value:F2}  (morale below = fallback)");
                i.FractureRetreatThreshold.Value = GUILayout.HorizontalSlider(i.FractureRetreatThreshold.Value, 0.15f, 0.6f);
                i.FractureAggressionBoost.Value = GUILayout.Toggle(i.FractureAggressionBoost.Value, "  Aggression boost (winners pursue harder)");
                GUILayout.Label($"  Dispersion: {i.FractureDispersion.Value:F2}  (0=tight columns, 1=wide front)");
                i.FractureDispersion.Value = GUILayout.HorizontalSlider(i.FractureDispersion.Value, 0f, 1f);
                GUILayout.Label($"  Dispersion width: {i.FractureDispersionWidth.Value:F0}  (max lateral spread)");
                i.FractureDispersionWidth.Value = GUILayout.HorizontalSlider(i.FractureDispersionWidth.Value, 50f, 500f);

                GUILayout.Space(6);
                GUILayout.Label("UI", _header);
                i.ShowDebugStats.Value = GUILayout.Toggle(i.ShowDebugStats.Value, "  Show debug stats");
                if (i.ShowDebugStats.Value)
                {
                    GUILayout.Space(4);
                    GUILayout.Label("Live stats", _header);
                    var teams = ThreadManager.Teams;
                    if (teams != null)
                    {
                        GUILayout.Label($"  Teams: {teams.Count}   Armies: {(ThreadManager.AllArmies != null ? ThreadManager.AllArmies.Count : 0)}", _small);
                        var pri = Patches.TeamPriorities;
                        if (pri != null && pri.Length > 0)
                        {
                            for (int t = 0; t < pri.Length && t < 8; t++)
                            {
                                GUILayout.Label($"  Team {t} priority: {pri[t]:F3}", _small);
                            }
                        }
                    }
                    if (NavGrid.TargetList != null)
                        GUILayout.Label($"  Active targets: {NavGrid.TargetList.Count}", _small);
                    GUILayout.Label($"  FarthestArmies: {Army.FarthestArmies:F0}", _small);
                    GUILayout.Label($"  CinematicMode: {FlyCam.CinematicMode}  Auto: {FlyCam.AutoCinematicMode}", _small);

                    // Sub-group fracture stats
                    if (i.FractureEnabled.Value)
                    {
                        GUILayout.Space(2);
                        GUILayout.Label("  Sub-groups:", _small);
                        var sgStats = SubGroupFracture.GetStats();
                        for (int s = 0; s < sgStats.Count && s < 12; s++)
                        {
                            var st = sgStats[s];
                            string flags = "";
                            if (st.Engaged) flags += "ENG ";
                            if (st.Outnumbered) flags += "OUT ";
                            if (st.Flanked) flags += "FLK ";
                            if (st.Ranged) flags += "RNG ";
                            if (st.Holding) flags += "GRD ";
                            if (st.HasCover) flags += "COV ";
                            flags += $"h{st.HeightAdv:+0.0;-0.0;0}m";
                            // Color-code morale: green >0.6, yellow 0.35-0.6, red <0.35
                            string moraleColor = st.Morale > 0.6f ? "green"
                                : st.Morale > 0.35f ? "yellow" : "red";
                            GUILayout.Label($"    T{st.Team} {st.ArmyCount}arm {st.Remaining}alive hp{st.HealthRatio:F2} <color={moraleColor}>M{st.Morale:F2}</color> {st.Action} [{flags}]", _small);
                        }

                        // Fortification stats
                        var forts = FortificationAnalysis.GetFortifications();
                        if (forts != null && forts.Count > 0)
                        {
                            GUILayout.Space(2);
                            GUILayout.Label("  Fortifications:", _small);
                            for (int f = 0; f < forts.Count && f < 6; f++)
                            {
                                var fort = forts[f];
                                GUILayout.Label($"    T{fort.DefendingTeam} {fort.DefenderCount}def r{fort.Radius:F0} {(fort.HasStructures ? "WALLS" : "open")}", _small);
                            }
                        }
                    }
                }

                GUILayout.Space(6);
                GUILayout.Label("Numpad+ or Esc to close (releases mouse back to the game).", _small);
            }
            GUILayout.EndVertical();

            // Capture the actual content height for the scroll view's content rect.
            if (Event.current.type == EventType.Repaint)
            {
                _contentHeight = GUILayoutUtility.GetLastRect().height + 20f;
            }

            GUI.EndScrollView();

            // Drag handle at the top of the window.
            GUI.DragWindow(new Rect(0, 0, 10000, 22));
        }
    }
}
