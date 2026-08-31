using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using UnityEngine;

namespace UEBS2PathingMod
{
    /// <summary>
    /// Two-stage AI battle agent:
    ///
    ///   Stage 1 — EYES: Qwen 2.5 VL vision model analyzes a screenshot
    ///     and produces a tactical assessment (who's winning, where are
    ///     the pressure points, what formations look like).
    ///
    ///   Stage 2 — ENGINEER/ACTOR: Qwen 2.5 Coder receives the vision
    ///     assessment + structured battle state (team positions, unit
    ///     counts, morale, fortifications) and proposes a set of flow
    ///     field commands. Commands are shown in the UI for player
    ///     approval before execution.
    ///
    /// Command protocol (JSON returned by the coder):
    /// {
    ///   "reasoning": "Left flank is collapsing...",
    ///   "commands": [
    ///     {"action":"block_path","from":[x,y,z],"to":[x,y,z],"width":60,"strength":0.7},
    ///     {"action":"corridor","from":[x,y,z],"to":[x,y,z],"width":80,"strength":0.6},
    ///     {"action":"retreat_wave","unit_pos":[x,y,z],"enemy_pos":[x,y,z],"retreat_dest":[x,y,z],"strength":0.8},
    ///     {"action":"set_param","param":"dispersion","value":0.7},
    ///     {"action":"set_param","param":"block_strength","value":0.6},
    ///     {"action":"set_param","param":"retreat_threshold","value":0.4},
    ///     {"action":"set_param","param":"rout_threshold","value":0.15},
    ///     {"action":"set_param","param":"aggression","value":true}
    ///   ]
    /// }
    /// </summary>
    internal class BattleAgent : MonoBehaviour
    {
        // ---- Config ----
        internal bool Enabled = false;
        internal float Interval = 15f;
        internal string VisionModel = "qwen2.5vl:7b";
        internal string CoderModel = "qwen2.5-coder:7b";
        internal int ScreenshotWidth = 640;

        // ---- State ----
        internal enum AgentState { Idle, Capturing, VisionAnalyzing, CoderAnalyzing, AwaitingApproval, Applying }
        internal AgentState State = AgentState.Idle;
        internal string LastVisionAssessment = "(no analysis yet)";
        internal string LastCoderReasoning = "";
        internal string LastError = "";
        internal float LastAnalysisTime;
        internal List<FlowFieldCommand> PendingCommands = new List<FlowFieldCommand>();

        // Background thread results.
        private string _visionResponse;
        private string _coderResponse;
        private string _threadError;
        private bool _visionReady;
        private bool _coderReady;
        private Thread _workerThread;
        private string _capturedBase64;
        private string _battleStateJson;

        // ---- Vision prompt ----
        private const string VisionPrompt = @"You are the eyes of a battle AI system for Ultimate Epic Battle Simulator 2. Analyze this battlefield screenshot.

Describe what you see in 2-3 sentences, focusing on:
- Which side appears to be winning or losing
- Formation quality (tight columns, spread lines, clumping, gaps)
- Flank threats or encirclement attempts
- Terrain features affecting the battle (high ground, chokepoints, open field)
- Any units retreating, routing, or breaking formation

Be concise and tactical. This description will be passed to a decision-making AI.";

        // ---- Coder prompt (built dynamically with battle state) ----
        private const string CoderPromptHeader = @"You are the engineer/actor AI for Ultimate Epic Battle Simulator 2. You control a flow field modulation system that can place soft obstacles on the battlefield to redirect unit movement.

You receive:
1. A visual assessment from a vision AI (the 'eyes')
2. Structured battle state data (team positions, unit counts, morale, actions, fortifications)

Your job: decide what flow field operations to execute to improve the tactical situation. You can:
- block_path: Place a wall between two points to block direct movement (forces routing around)
- corridor: Create a corridor (two parallel walls) to funnel units through a specific path
- retreat_wave: Create a retreat wavefield that pushes a unit group backward away from enemies
- set_param: Adjust a mod parameter (dispersion 0-1, block_strength 0-1, retreat_threshold 0-0.5, rout_threshold 0-0.3, aggression true/false)

Coordinates are in world space (x, y, z). Use the coordinates from the battle state data.

Respond with ONLY a JSON object (no markdown fences, no explanation outside JSON):
{
  ""reasoning"": ""1-2 sentences explaining your tactical reasoning"",
  ""commands"": [
    {""action"": ""block_path"", ""from"": [x,y,z], ""to"": [x,y,z], ""width"": 60, ""strength"": 0.7},
    {""action"": ""set_param"", ""param"": ""dispersion"", ""value"": 0.7}
  ]
}

Limit to 5 commands per response. Be surgical — small targeted interventions are better than sweeping changes.";

        /// <summary>Represents one flow field command from the coder.</summary>
        internal struct FlowFieldCommand
        {
            internal string Action;      // block_path, corridor, retreat_wave, set_param
            internal Vector3 From;       // for block_path, corridor
            internal Vector3 To;         // for block_path, corridor
            internal Vector3 UnitPos;    // for retreat_wave
            internal Vector3 EnemyPos;   // for retreat_wave
            internal Vector3 RetreatDest;// for retreat_wave
            internal float Width;        // for block_path, corridor
            internal float Strength;     // for all
            internal string Param;       // for set_param
            internal float ParamValue;   // for set_param (float)
            internal bool ParamBool;     // for set_param (bool)
            internal string RawJson;     // original JSON for UI display
        }

        internal void Start()
        {
            LastAnalysisTime = Time.time;
        }

        internal void Update()
        {
            if (!Enabled) return;

            // Check if vision analysis completed → start coder analysis.
            if (_visionReady)
            {
                _visionReady = false;
                if (_threadError != null)
                {
                    LastError = $"Vision: {_threadError}";
                    State = AgentState.Idle;
                    return;
                }
                LastVisionAssessment = _visionResponse ?? "(empty response)";
                State = AgentState.CoderAnalyzing;
                // Launch coder on background thread.
                StartCoderAnalysis();
            }

            // Check if coder analysis completed → show for approval.
            if (_coderReady)
            {
                _coderReady = false;
                if (_threadError != null)
                {
                    LastError = $"Coder: {_threadError}";
                    State = AgentState.Idle;
                    return;
                }
                LastError = "";
                ParseCoderResponse(_coderResponse);
                State = PendingCommands.Count > 0 ? AgentState.AwaitingApproval : AgentState.Idle;
            }

            // Timer: trigger a new analysis cycle.
            if (State == AgentState.Idle && Time.time - LastAnalysisTime >= Interval)
            {
                StartCoroutine(CaptureAndAnalyze());
            }
        }

        /// <summary>Trigger a full analysis cycle (manual or automatic).</summary>
        internal void AnalyzeNow()
        {
            if (State != AgentState.Idle) return;
            StartCoroutine(CaptureAndAnalyze());
        }

        /// <summary>Approve and execute all pending commands.</summary>
        internal void ApproveCommands()
        {
            if (PendingCommands.Count == 0) return;
            State = AgentState.Applying;
            try
            {
                foreach (var cmd in PendingCommands)
                    ExecuteCommand(cmd);
            }
            catch (Exception e)
            {
                LastError = $"Execute: {e.Message}";
            }
            PendingCommands.Clear();
            State = AgentState.Idle;
            LastAnalysisTime = Time.time;
        }

        /// <summary>Reject all pending commands.</summary>
        internal void RejectCommands()
        {
            PendingCommands.Clear();
            State = AgentState.Idle;
            LastAnalysisTime = Time.time;
        }

        // ---- Stage 1: Capture screenshot + vision analysis ----

        private IEnumerator CaptureAndAnalyze()
        {
            State = AgentState.Capturing;
            LastAnalysisTime = Time.time;
            LastError = "";

            yield return new WaitForEndOfFrame();

            Texture2D fullTex = null;
            try
            {
                fullTex = ScreenCapture.CaptureScreenshotAsTexture(1);
            }
            catch (Exception e)
            {
                LastError = $"Capture: {e.Message}";
                State = AgentState.Idle;
                yield break;
            }
            if (fullTex == null)
            {
                LastError = "Capture returned null";
                State = AgentState.Idle;
                yield break;
            }

            // Downscale.
            int targetW = ScreenshotWidth;
            int targetH = Mathf.RoundToInt((float)fullTex.height / fullTex.width * targetW);
            var scaled = new Texture2D(targetW, targetH, TextureFormat.RGB24, false);
            var rt = RenderTexture.GetTemporary(targetW, targetH, 0, RenderTextureFormat.ARGB32);
            Graphics.Blit(fullTex, rt);
            RenderTexture.active = rt;
            scaled.ReadPixels(new Rect(0, 0, targetW, targetH), 0, 0);
            scaled.Apply();
            RenderTexture.active = null;
            RenderTexture.ReleaseTemporary(rt);

            byte[] pngBytes = scaled.EncodeToPNG();
            _capturedBase64 = Convert.ToBase64String(pngBytes);

            Destroy(fullTex);
            Destroy(scaled);

            // Build battle state JSON for the coder (on main thread — accesses Unity APIs).
            _battleStateJson = BuildBattleStateJson();

            // Launch vision analysis on background thread.
            State = AgentState.VisionAnalyzing;
            string base64 = _capturedBase64;
            _workerThread = new Thread(() => DoVisionCall(base64)) { IsBackground = true };
            _workerThread.Start();
        }

        private void DoVisionCall(string base64)
        {
            try
            {
                _threadError = null;
                _visionResponse = OllamaClient.ChatWithImage(VisionModel, VisionPrompt, base64);
            }
            catch (Exception e)
            {
                _threadError = e.Message;
                _visionResponse = null;
            }
            _visionReady = true;
        }

        // ---- Stage 2: Coder analysis ----

        private void StartCoderAnalysis()
        {
            string vision = LastVisionAssessment;
            string battleState = _battleStateJson;
            _workerThread = new Thread(() => DoCoderCall(vision, battleState)) { IsBackground = true };
            _workerThread.Start();
        }

        private void DoCoderCall(string visionAssessment, string battleStateJson)
        {
            try
            {
                _threadError = null;
                string prompt = CoderPromptHeader
                    + "\n\n--- VISION ASSESSMENT ---\n" + visionAssessment
                    + "\n\n--- BATTLE STATE ---\n" + battleStateJson
                    + "\n\nPropose your flow field commands now. Remember: ONLY JSON, no markdown.";
                _coderResponse = OllamaClient.Chat(CoderModel, prompt);
            }
            catch (Exception e)
            {
                _threadError = e.Message;
                _coderResponse = null;
            }
            _coderReady = true;
        }

        // ---- Battle state serialization ----

        /// <summary>
        /// Build a JSON snapshot of the current battle state for the coder.
        /// Includes team positions, unit counts, morale, actions, fortifications.
        /// </summary>
        private string BuildBattleStateJson()
        {
            var sb = new StringBuilder();
            sb.Append("{");

            // General info.
            int totalAi = PathingModPlugin.TotalAiCount;
            sb.Append("\"total_units\":").Append(totalAi).Append(",");
            sb.Append("\"current_params\":{");
            sb.Append("\"dispersion\":").Append(SubGroupFracture.DispersionFactor.ToString("F2", CultureInfo.InvariantCulture)).Append(",");
            sb.Append("\"block_strength\":").Append(FlowFieldModulator.BlockStrength.ToString("F2", CultureInfo.InvariantCulture)).Append(",");
            sb.Append("\"retreat_threshold\":").Append(SubGroupFracture.RetreatThreshold.ToString("F2", CultureInfo.InvariantCulture)).Append(",");
            sb.Append("\"rout_threshold\":").Append(SubGroupFracture.RoutThreshold.ToString("F2", CultureInfo.InvariantCulture)).Append(",");
            sb.Append("\"aggression\":").Append(SubGroupFracture.AggressionBoost ? "true" : "false");
            sb.Append("},");

            // Teams.
            sb.Append("\"teams\":[");
            var teams = ThreadManager.Teams;
            if (teams != null)
            {
                for (int ti = 0; ti < teams.Count; ti++)
                {
                    var team = teams[ti];
                    if (ti > 0) sb.Append(",");
                    sb.Append("{");
                    sb.Append("\"team\":").Append(team.TeamNumber).Append(",");
                    // Aggregate position and count.
                    Vector3 centroid = Vector3.zero;
                    int remaining = 0;
                    int armyCount = 0;
                    if (team.Armies != null)
                    {
                        foreach (var a in team.Armies)
                        {
                            if (a == null) continue;
                            centroid += a.transform.position * a.Remaining;
                            remaining += a.Remaining;
                            armyCount++;
                        }
                    }
                    if (remaining > 0) centroid /= remaining;
                    sb.Append("\"units\":").Append(remaining).Append(",");
                    sb.Append("\"armies\":").Append(armyCount).Append(",");
                    sb.Append("\"centroid\":").Append(VecToJson(centroid));
                    sb.Append("}");
                }
            }
            sb.Append("],");

            // Sub-group stats (morale, actions, engagement).
            sb.Append("\"subgroups\":[");
            var stats = SubGroupFracture.GetStats();
            if (stats != null)
            {
                for (int si = 0; si < stats.Count; si++)
                {
                    var st = stats[si];
                    if (si > 0) sb.Append(",");
                    sb.Append("{");
                    sb.Append("\"team\":").Append(st.Team).Append(",");
                    sb.Append("\"units\":").Append(st.Remaining).Append(",");
                    sb.Append("\"health\":").Append(st.HealthRatio.ToString("F2", CultureInfo.InvariantCulture)).Append(",");
                    sb.Append("\"morale\":").Append(st.Morale.ToString("F2", CultureInfo.InvariantCulture)).Append(",");
                    sb.Append("\"action\":\"").Append(st.Action.ToString()).Append("\",");
                    sb.Append("\"engaged\":").Append(st.Engaged ? "true" : "false").Append(",");
                    sb.Append("\"outnumbered\":").Append(st.Outnumbered ? "true" : "false").Append(",");
                    sb.Append("\"flanked\":").Append(st.Flanked ? "true" : "false").Append(",");
                    sb.Append("\"ranged\":").Append(st.Ranged ? "true" : "false").Append(",");
                    sb.Append("\"holding\":").Append(st.Holding ? "true" : "false");
                    sb.Append("}");
                }
            }
            sb.Append("],");

            // Fortifications.
            sb.Append("\"fortifications\":[");
            var forts = FortificationAnalysis.GetFortifications();
            if (forts != null)
            {
                for (int fi = 0; fi < forts.Count; fi++)
                {
                    var fort = forts[fi];
                    if (fi > 0) sb.Append(",");
                    sb.Append("{");
                    sb.Append("\"team\":").Append(fort.DefendingTeam).Append(",");
                    sb.Append("\"center\":").Append(VecToJson(fort.Center)).Append(",");
                    sb.Append("\"radius\":").Append(fort.Radius.ToString("F0", CultureInfo.InvariantCulture));
                    sb.Append("}");
                }
            }
            sb.Append("]");

            sb.Append("}");
            return sb.ToString();
        }

        private static string VecToJson(Vector3 v)
        {
            return "[" + v.x.ToString("F1", CultureInfo.InvariantCulture) + ","
                + v.y.ToString("F1", CultureInfo.InvariantCulture) + ","
                + v.z.ToString("F1", CultureInfo.InvariantCulture) + "]";
        }

        // ---- Coder response parsing ----

        private void ParseCoderResponse(string response)
        {
            PendingCommands.Clear();
            if (string.IsNullOrEmpty(response))
            {
                LastError = "Empty coder response";
                return;
            }

            // Extract JSON (strip markdown fences if present).
            string json = ExtractJson(response);
            if (json == null)
            {
                LastCoderReasoning = response.Length > 300 ? response.Substring(0, 300) + "..." : response;
                LastError = "No JSON found in coder response";
                return;
            }

            // Extract reasoning.
            LastCoderReasoning = ExtractString(json, "reasoning", "(no reasoning provided)");

            // Extract commands array.
            int cmdsStart = json.IndexOf("\"commands\"", StringComparison.Ordinal);
            if (cmdsStart < 0) return;
            int arrStart = json.IndexOf('[', cmdsStart);
            if (arrStart < 0) return;

            // Find matching ].
            int depth = 0;
            int arrEnd = arrStart;
            for (int i = arrStart; i < json.Length; i++)
            {
                if (json[i] == '[') depth++;
                else if (json[i] == ']')
                {
                    depth--;
                    if (depth == 0) { arrEnd = i; break; }
                }
            }
            if (arrEnd <= arrStart) return;

            string cmdsJson = json.Substring(arrStart + 1, arrEnd - arrStart - 1);

            // Split by top-level commas (between { } objects).
            var cmdObjs = SplitJsonObjects(cmdsJson);
            foreach (var cmdJson in cmdObjs)
            {
                var cmd = ParseCommand(cmdJson);
                if (cmd.Action != null)
                {
                    cmd.RawJson = cmdJson;
                    PendingCommands.Add(cmd);
                }
            }

            UnityEngine.Debug.Log($"[BattleAgent] Parsed {PendingCommands.Count} commands. Reasoning: {LastCoderReasoning}");
        }

        private FlowFieldCommand ParseCommand(string json)
        {
            var cmd = new FlowFieldCommand();
            cmd.Action = ExtractString(json, "action", null);
            if (cmd.Action == null) return cmd;

            cmd.Strength = ExtractFloat(json, "strength", 0.7f);
            cmd.Width = ExtractFloat(json, "width", 60f);

            switch (cmd.Action)
            {
                case "block_path":
                    cmd.From = ExtractVec(json, "from", Vector3.zero);
                    cmd.To = ExtractVec(json, "to", Vector3.zero);
                    break;
                case "corridor":
                    cmd.From = ExtractVec(json, "from", Vector3.zero);
                    cmd.To = ExtractVec(json, "to", Vector3.zero);
                    break;
                case "retreat_wave":
                    cmd.UnitPos = ExtractVec(json, "unit_pos", Vector3.zero);
                    cmd.EnemyPos = ExtractVec(json, "enemy_pos", Vector3.zero);
                    cmd.RetreatDest = ExtractVec(json, "retreat_dest", Vector3.zero);
                    break;
                case "set_param":
                    cmd.Param = ExtractString(json, "param", null);
                    // Try float first, then bool.
                    cmd.ParamValue = ExtractFloat(json, "value", float.NaN);
                    if (float.IsNaN(cmd.ParamValue))
                    {
                        cmd.ParamBool = ExtractBool(json, "value", false);
                        cmd.ParamValue = 0f; // mark as parsed
                    }
                    break;
            }
            return cmd;
        }

        // ---- Command execution ----

        private void ExecuteCommand(FlowFieldCommand cmd)
        {
            switch (cmd.Action)
            {
                case "block_path":
                    FlowFieldModulator.BlockDirectPath(cmd.From, cmd.To, cmd.Width, cmd.Strength);
                    Debug.Log($"[BattleAgent] Executed block_path {cmd.From} -> {cmd.To} w={cmd.Width} s={cmd.Strength}");
                    break;

                case "corridor":
                    FlowFieldModulator.CreateCorridor(cmd.From, cmd.To, cmd.Width, cmd.Strength);
                    Debug.Log($"[BattleAgent] Executed corridor {cmd.From} -> {cmd.To} w={cmd.Width} s={cmd.Strength}");
                    break;

                case "retreat_wave":
                    FlowFieldModulator.CreateRetreatWavefield(cmd.UnitPos, cmd.EnemyPos, cmd.RetreatDest, cmd.Strength);
                    Debug.Log($"[BattleAgent] Executed retreat_wave from {cmd.UnitPos} away from {cmd.EnemyPos}");
                    break;

                case "set_param":
                    ExecuteSetParam(cmd);
                    break;

                default:
                    Debug.LogWarning($"[BattleAgent] Unknown command action: {cmd.Action}");
                    break;
            }
        }

        private void ExecuteSetParam(FlowFieldCommand cmd)
        {
            var i = PathingModPlugin.Instance;
            switch (cmd.Param)
            {
                case "dispersion":
                    SubGroupFracture.DispersionFactor = Mathf.Clamp01(cmd.ParamValue);
                    if (i != null) i.FractureDispersion.Value = SubGroupFracture.DispersionFactor;
                    break;
                case "block_strength":
                    FlowFieldModulator.BlockStrength = Mathf.Clamp01(cmd.ParamValue);
                    if (i != null) i.FlowFieldBlockStrength.Value = FlowFieldModulator.BlockStrength;
                    break;
                case "retreat_threshold":
                    SubGroupFracture.RetreatThreshold = Mathf.Clamp(cmd.ParamValue, 0f, 0.5f);
                    if (i != null) i.FractureRetreatThreshold.Value = SubGroupFracture.RetreatThreshold;
                    break;
                case "rout_threshold":
                    SubGroupFracture.RoutThreshold = Mathf.Clamp(cmd.ParamValue, 0f, 0.3f);
                    if (i != null) i.FractureRoutThreshold.Value = SubGroupFracture.RoutThreshold;
                    break;
                case "aggression":
                    SubGroupFracture.AggressionBoost = cmd.ParamBool;
                    if (i != null) i.FractureAggressionBoost.Value = cmd.ParamBool;
                    break;
            }
            Debug.Log($"[BattleAgent] Set param {cmd.Param} = {cmd.ParamValue}");
        }

        // ---- JSON helpers ----

        private static string ExtractJson(string text)
        {
            text = text.Trim();
            if (text.StartsWith("```"))
            {
                int firstNl = text.IndexOf('\n');
                if (firstNl >= 0) text = text.Substring(firstNl + 1);
                int lastFence = text.LastIndexOf("```");
                if (lastFence >= 0) text = text.Substring(0, lastFence);
                text = text.Trim();
            }
            int start = text.IndexOf('{');
            if (start < 0) return null;
            int depth = 0;
            for (int i = start; i < text.Length; i++)
            {
                if (text[i] == '{') depth++;
                else if (text[i] == '}')
                {
                    depth--;
                    if (depth == 0) return text.Substring(start, i - start + 1);
                }
            }
            return null;
        }

        private static List<string> SplitJsonObjects(string json)
        {
            var result = new List<string>();
            int depth = 0;
            int start = -1;
            for (int i = 0; i < json.Length; i++)
            {
                if (json[i] == '{')
                {
                    if (depth == 0) start = i;
                    depth++;
                }
                else if (json[i] == '}')
                {
                    depth--;
                    if (depth == 0 && start >= 0)
                    {
                        result.Add(json.Substring(start, i - start + 1));
                        start = -1;
                    }
                }
            }
            return result;
        }

        private static float ExtractFloat(string json, string key, float defaultVal)
        {
            string pattern = "\"" + key + "\"";
            int idx = json.IndexOf(pattern, StringComparison.Ordinal);
            if (idx < 0) return defaultVal;
            int colon = json.IndexOf(':', idx + pattern.Length);
            if (colon < 0) return defaultVal;
            int start = colon + 1;
            while (start < json.Length && (json[start] == ' ' || json[start] == '\t' || json[start] == '\n')) start++;
            int end = start;
            while (end < json.Length && (char.IsDigit(json[end]) || json[end] == '.' || json[end] == '-' || json[end] == '+' || json[end] == 'e' || json[end] == 'E'))
                end++;
            if (end <= start) return defaultVal;
            float result;
            if (float.TryParse(json.Substring(start, end - start), NumberStyles.Float, CultureInfo.InvariantCulture, out result))
                return result;
            return defaultVal;
        }

        private static bool ExtractBool(string json, string key, bool defaultVal)
        {
            string pattern = "\"" + key + "\"";
            int idx = json.IndexOf(pattern, StringComparison.Ordinal);
            if (idx < 0) return defaultVal;
            int colon = json.IndexOf(':', idx + pattern.Length);
            if (colon < 0) return defaultVal;
            int start = colon + 1;
            while (start < json.Length && json[start] == ' ') start++;
            if (start + 4 <= json.Length && json.Substring(start, 4) == "true") return true;
            if (start + 5 <= json.Length && json.Substring(start, 5) == "false") return false;
            return defaultVal;
        }

        private static string ExtractString(string json, string key, string defaultVal)
        {
            string pattern = "\"" + key + "\"";
            int idx = json.IndexOf(pattern, StringComparison.Ordinal);
            if (idx < 0) return defaultVal;
            int colon = json.IndexOf(':', idx + pattern.Length);
            if (colon < 0) return defaultVal;
            int q1 = json.IndexOf('"', colon + 1);
            if (q1 < 0) return defaultVal;
            int q2 = q1 + 1;
            while (q2 < json.Length)
            {
                if (json[q2] == '\\' && q2 + 1 < json.Length) { q2 += 2; continue; }
                if (json[q2] == '"') break;
                q2++;
            }
            if (q2 >= json.Length) return defaultVal;
            return json.Substring(q1 + 1, q2 - q1 - 1).Replace("\\n", "\n").Replace("\\\"", "\"");
        }

        private static Vector3 ExtractVec(string json, string key, Vector3 defaultVal)
        {
            string pattern = "\"" + key + "\"";
            int idx = json.IndexOf(pattern, StringComparison.Ordinal);
            if (idx < 0) return defaultVal;
            int arrStart = json.IndexOf('[', idx + pattern.Length);
            if (arrStart < 0) return defaultVal;
            int arrEnd = json.IndexOf(']', arrStart);
            if (arrEnd < 0) return defaultVal;
            string arrStr = json.Substring(arrStart + 1, arrEnd - arrStart - 1);
            string[] parts = arrStr.Split(',');
            if (parts.Length < 3) return defaultVal;
            float x, y, z;
            if (!float.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out x)) return defaultVal;
            if (!float.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out y)) return defaultVal;
            if (!float.TryParse(parts[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out z)) return defaultVal;
            return new Vector3(x, y, z);
        }
    }
}
