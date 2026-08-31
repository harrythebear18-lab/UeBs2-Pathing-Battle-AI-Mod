using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;

namespace UEBS2PathingMod
{
    /// <summary>
    /// Minimal HTTP client for the Ollama REST API (localhost:11434).
    ///
    /// Uses synchronous HttpWebRequest on a background thread — no async/await
    /// needed, no extra dependencies. The BattleAnalyzer calls these from a
    /// worker thread so the game thread is never blocked.
    /// </summary>
    internal static class OllamaClient
    {
        internal const string DefaultUrl = "http://localhost:11434";
        internal static string BaseUrl = DefaultUrl;
        internal static int TimeoutMs = 30000;

        /// <summary>Get the list of installed models.</summary>
        internal static List<string> ListModels()
        {
            var models = new List<string>();
            try
            {
                var resp = HttpGet($"{BaseUrl}/api/tags");
                // Parse "model":"xxx" entries from the JSON.
                int idx = 0;
                while ((idx = resp.IndexOf("\"model\"", idx, StringComparison.Ordinal)) >= 0)
                {
                    int colon = resp.IndexOf(':', idx);
                    if (colon < 0) break;
                    int q1 = resp.IndexOf('"', colon + 1);
                    if (q1 < 0) break;
                    int q2 = resp.IndexOf('"', q1 + 1);
                    if (q2 < 0) break;
                    string name = resp.Substring(q1 + 1, q2 - q1 - 1);
                    if (!models.Contains(name))
                        models.Add(name);
                    idx = q2 + 1;
                }
            }
            catch { }
            return models;
        }

        /// <summary>
        /// Send a vision chat request: prompt + base64 image.
        /// Returns the assistant's text response.
        /// </summary>
        internal static string ChatWithImage(string model, string prompt, string base64Image)
        {
            // Build JSON manually (no Newtonsoft dependency).
            // Ollama /api/chat format:
            // {
            //   "model": "qwen2.5vl:7b",
            //   "messages": [{"role":"user","content":"...","images":["base64..."]}],
            //   "stream": false
            // }
            string escapedPrompt = EscapeJson(prompt);
            string json = "{\"model\":\"" + EscapeJson(model) + "\","
                + "\"messages\":[{\"role\":\"user\",\"content\":\"" + escapedPrompt + "\","
                + "\"images\":[\"" + base64Image + "\"]}],"
                + "\"stream\":false}";

            string resp = HttpPost($"{BaseUrl}/api/chat", json);
            // Extract "content":"..." from the response.
            return ExtractContent(resp);
        }

        /// <summary>Send a text-only chat request.</summary>
        internal static string Chat(string model, string prompt)
        {
            string escapedPrompt = EscapeJson(prompt);
            string json = "{\"model\":\"" + EscapeJson(model) + "\","
                + "\"messages\":[{\"role\":\"user\",\"content\":\"" + escapedPrompt + "\"}],"
                + "\"stream\":false}";

            string resp = HttpPost($"{BaseUrl}/api/chat", json);
            return ExtractContent(resp);
        }

        // ---- HTTP helpers ----

        private static string HttpGet(string url)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "GET";
            req.Timeout = TimeoutMs;
            using (var resp = req.GetResponse())
            using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                return sr.ReadToEnd();
        }

        private static string HttpPost(string url, string jsonBody)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "POST";
            req.ContentType = "application/json";
            req.Timeout = TimeoutMs;
            var bytes = Encoding.UTF8.GetBytes(jsonBody);
            req.ContentLength = bytes.Length;
            using (var stream = req.GetRequestStream())
                stream.Write(bytes, 0, bytes.Length);
            using (var resp = req.GetResponse())
            using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                return sr.ReadToEnd();
        }

        // ---- JSON helpers (dependency-free) ----

        private static string EscapeJson(string s)
        {
            var sb = new StringBuilder(s.Length + 8);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append($"\\u{(int)c:X4}");
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        /// <summary>Extract the "content" field from an Ollama chat response.</summary>
        private static string ExtractContent(string json)
        {
            // Look for "content":"..." in the message object.
            int idx = json.IndexOf("\"content\"", StringComparison.Ordinal);
            if (idx < 0) return "";
            int colon = json.IndexOf(':', idx);
            if (colon < 0) return "";
            int q1 = json.IndexOf('"', colon + 1);
            if (q1 < 0) return "";
            // Find the closing quote, handling escaped quotes.
            int q2 = q1 + 1;
            while (q2 < json.Length)
            {
                if (json[q2] == '\\' && q2 + 1 < json.Length) { q2 += 2; continue; }
                if (json[q2] == '"') break;
                q2++;
            }
            if (q2 >= json.Length) return "";
            string content = json.Substring(q1 + 1, q2 - q1 - 1);
            // Unescape.
            return content.Replace("\\n", "\n").Replace("\\\"", "\"").Replace("\\\\", "\\");
        }
    }
}
