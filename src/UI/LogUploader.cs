using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using PunkMultiverse.Core;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Networking;

namespace PunkMultiverse.UI
{
    /// <summary>
    /// Log upload to a Discord webhook (URL from config, so it's never committed — Discord
    /// auto-revokes webhooks it finds in public repos). Reads the BepInEx log, gzips it, and POSTs
    /// it as a file with a one-line context header (player, version, session, reason) so host and
    /// client captures line up. Sends automatically when the game is closed (OnApplicationQuit) if
    /// a webhook is set — no button press; F8 sends manually mid-session.
    /// </summary>
    public sealed class LogUploader : MonoBehaviour
    {
        private bool _sending;
        private bool _quitSent;

        private void Update()
        {
            var kb = Keyboard.current;
            if (kb != null && kb[Key.F8].wasPressedThisFrame && !_sending)
                StartCoroutine(SendLogs("manual (F8)"));
        }

        // Auto-send the whole session's log when the GAME IS CLOSED, if a webhook is configured —
        // no button press. Blocking POST (not the coroutine path) because the process is tearing
        // down and a UnityWebRequest can't complete across frames here. F8 stays for manual sends.
        private void OnApplicationQuit()
        {
            if (_quitSent || !HasWebhook()) return;
            _quitSent = true;
            try { SendSync("game closed (auto)"); }
            catch (Exception e) { Plugin.Log.LogWarning($"[Logs] auto-send on quit failed: {e.Message}"); }
        }

        private static bool HasWebhook() =>
            NetConfig.LogWebhookUrl != null && !string.IsNullOrWhiteSpace(NetConfig.LogWebhookUrl.Value);

        /// <summary>Synchronous log upload for OnApplicationQuit — blocks (up to the timeout) so the
        /// send finishes before the process dies. Mirrors <see cref="SendLogs"/> but uses
        /// HttpWebRequest instead of UnityWebRequest.</summary>
        private void SendSync(string reason)
        {
            string url = NetConfig.LogWebhookUrl.Value;
            string logPath = Path.Combine(BepInEx.Paths.BepInExRootPath, "LogOutput.log");
            if (!File.Exists(logPath)) return;

            byte[] raw = ReadCombinedLog(logPath);
            byte[] gz;
            using (var outMs = new MemoryStream())
            {
                using (var gzs = new GZipStream(outMs, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
                    gzs.Write(raw, 0, raw.Length);
                gz = outMs.ToArray();
            }
            string who = LocalName();
            string fileName = $"punk-{Sanitize(who)}-v{Plugin.Version}-{DateTime.Now:yyyyMMdd-HHmmss}.log.gz";
            string context = BuildContext(who, raw.Length, gz.Length, reason);

            string boundary = "----PunkMV" + Guid.NewGuid().ToString("N");
            var body = new MemoryStream();
            void Write(string s) { var b = Encoding.UTF8.GetBytes(s); body.Write(b, 0, b.Length); }
            Write($"--{boundary}\r\nContent-Disposition: form-data; name=\"payload_json\"\r\n\r\n");
            Write("{\"content\":\"" + JsonEscape(context) + "\"}\r\n");
            Write($"--{boundary}\r\nContent-Disposition: form-data; name=\"file\"; filename=\"{fileName}\"\r\nContent-Type: application/gzip\r\n\r\n");
            body.Write(gz, 0, gz.Length);
            Write($"\r\n--{boundary}--\r\n");
            byte[] payload = body.ToArray();

            // Mono's default TLS/cert handling is flaky for outbound HTTPS; force 1.2 and accept the
            // webhook's cert (best-effort upload of our own log to a user-set URL — no secrets at risk).
            System.Net.ServicePointManager.SecurityProtocol = System.Net.SecurityProtocolType.Tls12;
            var prevCertCb = System.Net.ServicePointManager.ServerCertificateValidationCallback;
            System.Net.ServicePointManager.ServerCertificateValidationCallback = (a, b, c, d) => true;
            try
            {
                var req = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(url);
                req.Method = "POST";
                req.ContentType = "multipart/form-data; boundary=" + boundary;
                req.Timeout = 10000;
                req.ReadWriteTimeout = 10000;
                req.ContentLength = payload.Length;
                using (var rs = req.GetRequestStream()) rs.Write(payload, 0, payload.Length);
                using (var resp = (System.Net.HttpWebResponse)req.GetResponse())
                    Plugin.Log.LogInfo($"[Logs] auto-sent {fileName} on quit ({gz.Length / 1024}KB) -> {(int)resp.StatusCode}");
            }
            finally
            {
                System.Net.ServicePointManager.ServerCertificateValidationCallback = prevCertCb;
            }
        }

        private IEnumerator SendLogs(string reason)
        {
            string url = NetConfig.LogWebhookUrl != null ? NetConfig.LogWebhookUrl.Value : "";
            if (string.IsNullOrWhiteSpace(url))
            {
                Toast.Show("NO LOG WEBHOOK SET ([Diag] LogWebhookUrl in config.cfg)", 5f);
                yield break;
            }

            _sending = true;
            Toast.Show("SENDING LOGS…", 3f);

            byte[] gz;
            string fileName;
            string context;
            try
            {
                string logPath = Path.Combine(BepInEx.Paths.BepInExRootPath, "LogOutput.log");
                if (!File.Exists(logPath))
                {
                    Toast.Show("LOG FILE NOT FOUND", 4f);
                    _sending = false;
                    yield break;
                }
                // The log is being written live — open shared-read and copy before compressing.
                byte[] raw = ReadCombinedLog(logPath);
                using (var outMs = new MemoryStream())
                {
                    using (var gzs = new GZipStream(outMs, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
                        gzs.Write(raw, 0, raw.Length);
                    gz = outMs.ToArray();
                }
                string who = LocalName();
                fileName = $"punk-{Sanitize(who)}-v{Plugin.Version}-{DateTime.Now:yyyyMMdd-HHmmss}.log.gz";
                context = BuildContext(who, raw.Length, gz.Length, reason);
            }
            catch (Exception e)
            {
                Toast.Show("LOG READ FAILED", 4f);
                Plugin.Log.LogWarning($"[Logs] read/gzip failed: {e.Message}");
                _sending = false;
                yield break;
            }

            var form = new List<IMultipartFormSection>
            {
                new MultipartFormDataSection("payload_json", "{\"content\":\"" + JsonEscape(context) + "\"}"),
                new MultipartFormFileSection("file", gz, fileName, "application/gzip"),
            };
            using (var req = UnityWebRequest.Post(url, form))
            {
                req.timeout = 30;
                yield return req.SendWebRequest();
                bool ok = req.result == UnityWebRequest.Result.Success;
                if (ok)
                {
                    Toast.Show("LOGS SENT ✓", 4f);
                    Plugin.Log.LogInfo($"[Logs] uploaded {fileName} ({gz.Length / 1024}KB) to webhook");
                }
                else
                {
                    Toast.Show($"LOG SEND FAILED ({req.responseCode})", 5f);
                    Plugin.Log.LogWarning($"[Logs] upload failed: {req.responseCode} {req.error}");
                }
            }
            _sending = false;
        }

        private static string LocalName()
        {
            try
            {
                var s = NetSession.Instance;
                if (s != null && s.Players != null)
                    foreach (var p in s.Players)
                        if (p != null && p.IsLocal) return p.Name;
            }
            catch { }
            return Environment.UserName;
        }

        /// <summary>Copy the live BepInEx log and append the watchdog's independent fallback. The
        /// latter is the only evidence guaranteed to advance while Unity's main thread is stuck.</summary>
        private static byte[] ReadCombinedLog(string logPath)
        {
            using (var ms = new MemoryStream())
            {
                using (var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    fs.CopyTo(ms);
                string hitchPath = MainThreadWatchdog.FallbackPath;
                if (File.Exists(hitchPath))
                {
                    byte[] separator = Encoding.UTF8.GetBytes(
                        "\r\n\r\n===== PunkMultiverse watchdog fallback =====\r\n");
                    ms.Write(separator, 0, separator.Length);
                    using (var hs = new FileStream(hitchPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        hs.CopyTo(ms);
                }
                return ms.ToArray();
            }
        }

        private static string BuildContext(string who, int rawBytes, int gzBytes, string reason)
        {
            var s = NetSession.Instance;
            string role = s == null || s.State == SessionState.Offline ? "menu"
                : (s.IsHost ? "host" : "client") + $"/{s.State}";
            return $"PUNK Multiverse logs — {who} ({role}), v{Plugin.Version}, {reason}, " +
                   $"raw {rawBytes / 1024}KB → gz {gzBytes / 1024}KB";
        }

        private static string Sanitize(string s)
        {
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
                sb.Append(char.IsLetterOrDigit(c) ? c : '_');
            return sb.Length == 0 ? "player" : sb.ToString();
        }

        private static string JsonEscape(string s)
        {
            var sb = new StringBuilder(s.Length + 8);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': break;
                    case '\t': sb.Append(' '); break;
                    default: sb.Append(c); break;
                }
            }
            return sb.ToString();
        }
    }
}
