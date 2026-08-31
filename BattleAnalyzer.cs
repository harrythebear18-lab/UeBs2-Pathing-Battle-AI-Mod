using System;
using System.Collections;
using System.Text;
using System.Threading;
using UnityEngine;

namespace UEBS2PathingMod
{
    /// <summary>
    /// AI battle analyst: captures periodic screenshots of the battlefield,
    /// sends them to a local Qwen vision model via Ollama, and translates
    /// the model's tactical assessment into flow-field parameter adjustments.
    ///
    /// The analyzer does NOT directly control units — it shapes the wave field
    /// parameters (dispersion, obstacle strength, morale thresholds, aggression)
    /// that the GPU flow field uses to drive team movement. This is the right
    /// level of abstraction: the LLM sees the big picture, the flow field
    /// handles the micropathing.
    ///
    /// Threading:
    ///   - Screenshot capture happens on the main thread (Unity requirement).
    ///   - Ollama HTTP call happens on a background thread (no game stutter).
    ///   - Parameter application happens on the main thread (Unity thread-safe).
    /// </summary>
    internal class BattleAnalyzer : MonoBehaviour
    {
        // ---- Config ----
        internal bool Enabled = false;
        internal float Interval = 15f;           // seconds between analyses
        internal string ModelName = "qwen2.5vl:7b";
        internal int ScreenshotWidth = 640;      // downscale for faster LLM processing
        internal bool AutoApply = true;          // apply parameter changes automatically

        // ---- State ----
        internal string LastAssessment = "(no analysis yet)";
        internal string LastError = "";
        internal float LastAnalysisTime;
        internal bool IsAnalyzing;

        // Parsed parameters from the last analysis (for UI display).
        internal float SuggestedDispersion = -1f;
        internal float SuggestedBlockStrength = -1f;
        internal float SuggestedRetreatThreshold = -1f;
        internal float SuggestedRoutThreshold = -1f;
        internal bool SuggestedAggression = true;

        // Background thread result.
        private string _pendingResponse;
        private string _pendingError;
        private bool _responseReady;
        private Thread _workerThread;

        // The prompt sent to Qwen vision. Asks for structured JSON output.
        private const string AnalysisPrompt = @"You are a tactical AI advisor for Ultimate Epic Battle Simulator 2. Analyze this battlefield screenshot and assess the tactical situation.

Consider: unit formations, team balance, terrain, spacing, flanking opportunities, frontline integrity, and overall battle flow.

Respond with ONLY a JSON object (no markdown, no explanation outside the JSON) with these fields:
{
  ""dispersion"": <0.0 to 1.0>,  // Formation width: 0.0=tight column, 0.5=moderate spread, 1.0=very wide front. Increase when units are clumped in narrow channels, decrease when too spread thin.
  ""block_strength"": <0.0 to 1.0>,  // Flow field obstacle strength: higher=stronger tactical walls, lower=more fluid movement. Increase for chokepoint-heavy terrain, decrease for open fields.
  ""retreat_threshold"": <0.0 to 0.5>,  // Morale level (fraction of full morale) below which units retreat. Lower=more stubborn, higher=more cautious.
  ""rout_threshold"": <0.0 to 0.3>,  // Morale level below which units rout in panic. Must be lower than retreat_threshold.
  ""aggression"": <true or false>,  // Should winning units pursue retreating enemies? true=aggressive exploitation, false=hold position.
  ""assessment"": ""<one sentence tactical summary>""
}

Base your adjustments on what you see. If the battle looks well-balanced, keep parameters near their current values.";

        /// <summary>Start the analyzer. Called by the plugin.</summary>
        internal void Start()
        {
            LastAnalysisTime = Time.time;
        }

        internal void Update()
        {
            if (!Enabled) return;

            // Check if a background analysis has completed.
            if (_responseReady)
            {
                if (_pendingError != null)
                {
                    LastError = _pendingError;
                    UnityEngine.Debug.LogWarning($"[BattleAnalyzer] Error: {_pendingError}");
                }
                else if (_pendingResponse != null)
                {
                    ParseAndApply(_pendingResponse);
                }
                _responseReady = false;
                IsAnalyzing = false;
            }

            // Timer: trigger a new analysis if enough time has passed.
            if (!IsAnalyzing && Time.time - LastAnalysisTime >= Interval)
            {
                StartCoroutine(CaptureAndAnalyze());
            }
        }

        /// <summary>
        /// Capture a screenshot on the main thread (end of frame),
        /// then kick off the Ollama call on a background thread.
        /// </summary>
        private IEnumerator CaptureAndAnalyze()
        {
            IsAnalyzing = true;
            LastAnalysisTime = Time.time;

            // Wait for end of frame so the frame buffer is complete.
            yield return new WaitForEndOfFrame();

            // Capture screenshot at full res, then downscale.
            Texture2D fullTex = null;
            try
            {
                fullTex = ScreenCapture.CaptureScreenshotAsTexture(1);
            }
            catch (Exception e)
            {
                _pendingError = $"Capture failed: {e.Message}";
                _responseReady = true;
                yield break;
            }

            if (fullTex == null)
            {
                _pendingError = "Capture returned null";
                _responseReady = true;
                yield break;
            }

            // Downscale to target width for faster LLM processing.
            int targetW = ScreenshotWidth;
            int targetH = Mathf.RoundToInt((float)fullTex.height / fullTex.width * targetW);
            var scaled = new Texture2D(targetW, targetH, TextureFormat.RGB24, false);

            // Use RenderTexture for GPU-accelerated downscaling.
            var rt = RenderTexture.GetTemporary(targetW, targetH, 0, RenderTextureFormat.ARGB32);
            Graphics.Blit(fullTex, rt);
            RenderTexture.active = rt;
            scaled.ReadPixels(new Rect(0, 0, targetW, targetH), 0, 0);
            scaled.Apply();
            RenderTexture.active = null;
            RenderTexture.ReleaseTemporary(rt);

            // Encode to PNG and then base64.
            byte[] pngBytes = scaled.EncodeToPNG();
            string base64 = Convert.ToBase64String(pngBytes);

            // Clean up textures.
            Destroy(fullTex);
            Destroy(scaled);

            // Launch background thread for the Ollama HTTP call.
            string capturedBase64 = base64;
            _workerThread = new Thread(() => DoOllamaCall(capturedBase64))
            {
                IsBackground = true
            };
            _workerThread.Start();
        }

        /// <summary>Background thread: call Ollama and store the result.</summary>
        private void DoOllamaCall(string base64Image)
        {
            try
            {
                _pendingError = null;
                _pendingResponse = OllamaClient.ChatWithImage(ModelName, AnalysisPrompt, base64Image);
            }
            catch (Exception e)
            {
                _pendingError = $"Ollama call failed: {e.Message}";
                _pendingResponse = null;
            }
            // Signal main thread that the response is ready.
            _responseReady = true;
        }

        /// <summary>
        /// Parse the LLM response JSON and apply parameter adjustments
        /// to the mod's systems. Called on the main thread.
        /// </summary>
        private void ParseAndApply(string response)
        {
            LastError = "";
            LastAnalysisTime = Time.time;

            // Extract JSON from the response (the LLM may wrap it in markdown).
            string json = ExtractJson(response);
            if (string.IsNullOrEmpty(json))
            {
                LastError = "No JSON found in response";
                LastAssessment = response.Length > 200 ? response.Substring(0, 200) + "..." : response;
                return;
            }

            // Parse fields with simple string extraction (no JSON library).
            SuggestedDispersion = ExtractFloat(json, "dispersion", -1f);
            SuggestedBlockStrength = ExtractFloat(json, "block_strength", -1f);
            SuggestedRetreatThreshold = ExtractFloat(json, "retreat_threshold", -1f);
            SuggestedRoutThreshold = ExtractFloat(json, "rout_threshold", -1f);
            SuggestedAggression = ExtractBool(json, "aggression", true);
            LastAssessment = ExtractString(json, "assessment", "(no assessment)");

            if (AutoApply)
            {
                ApplyParameters();
            }

            UnityEngine.Debug.Log($"[BattleAnalyzer] Assessment: {LastAssessment}");
        }

        /// <summary>Apply the suggested parameters to the mod's systems.</summary>
        internal void ApplyParameters()
        {
            if (SuggestedDispersion >= 0f)
                SubGroupFracture.DispersionFactor = Mathf.Clamp01(SuggestedDispersion);
            if (SuggestedBlockStrength >= 0f)
                FlowFieldModulator.BlockStrength = Mathf.Clamp01(SuggestedBlockStrength);
            if (SuggestedRetreatThreshold >= 0f)
                SubGroupFracture.RetreatThreshold = Mathf.Clamp(SuggestedRetreatThreshold, 0f, 0.5f);
            if (SuggestedRoutThreshold >= 0f)
                SubGroupFracture.RoutThreshold = Mathf.Clamp(SuggestedRoutThreshold, 0f, 0.3f);
            SubGroupFracture.AggressionBoost = SuggestedAggression;
        }

        /// <summary>Trigger an immediate analysis (manual hotkey).</summary>
        internal void AnalyzeNow()
        {
            if (IsAnalyzing) return;
            StartCoroutine(CaptureAndAnalyze());
        }

        // ---- JSON extraction helpers (dependency-free) ----

        private static string ExtractJson(string text)
        {
            // Strip markdown code fences if present.
            text = text.Trim();
            if (text.StartsWith("```"))
            {
                // Remove first line (```json or ```).
                int firstNl = text.IndexOf('\n');
                if (firstNl >= 0) text = text.Substring(firstNl + 1);
                // Remove trailing ```.
                int lastFence = text.LastIndexOf("```");
                if (lastFence >= 0) text = text.Substring(0, lastFence);
                text = text.Trim();
            }
            // Find the first { and matching }.
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

        private static float ExtractFloat(string json, string key, float defaultVal)
        {
            string pattern = "\"" + key + "\"";
            int idx = json.IndexOf(pattern, StringComparison.Ordinal);
            if (idx < 0) return defaultVal;
            int colon = json.IndexOf(':', idx + pattern.Length);
            if (colon < 0) return defaultVal;
            // Skip whitespace after colon.
            int start = colon + 1;
            while (start < json.Length && (json[start] == ' ' || json[start] == '\t' || json[start] == '\n')) start++;
            // Find end of number.
            int end = start;
            while (end < json.Length && (char.IsDigit(json[end]) || json[end] == '.' || json[end] == '-' || json[end] == '+' || json[end] == 'e' || json[end] == 'E'))
                end++;
            if (end <= start) return defaultVal;
            float result;
            if (float.TryParse(json.Substring(start, end - start), out result))
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
    }
}
