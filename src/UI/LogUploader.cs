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
    /// F8 = send logs. Reads the BepInEx log, gzips it, and POSTs it as a file to a Discord
    /// webhook (URL from config, so it's never committed — Discord auto-revokes webhooks it finds
    /// in public repos). Zero backend: the log lands in a channel with a one-line context header
    /// (player, version, session) so host and client captures can be lined up.
    /// </summary>
    public sealed class LogUploader : MonoBehaviour
    {
        private bool _sending;

        private void Update()
        {
            var kb = Keyboard.current;
            if (kb != null && kb[Key.F8].wasPressedThisFrame && !_sending)
                StartCoroutine(SendLogs());
        }

        private IEnumerator SendLogs()
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
                byte[] raw;
                using (var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var ms = new MemoryStream())
                {
                    fs.CopyTo(ms);
                    raw = ms.ToArray();
                }
                using (var outMs = new MemoryStream())
                {
                    using (var gzs = new GZipStream(outMs, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
                        gzs.Write(raw, 0, raw.Length);
                    gz = outMs.ToArray();
                }
                string who = LocalName();
                fileName = $"punk-{Sanitize(who)}-v{Plugin.Version}-{DateTime.Now:yyyyMMdd-HHmmss}.log.gz";
                context = BuildContext(who, raw.Length, gz.Length);
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

        private static string BuildContext(string who, int rawBytes, int gzBytes)
        {
            var s = NetSession.Instance;
            string role = s == null || s.State == SessionState.Offline ? "menu"
                : (s.IsHost ? "host" : "client") + $"/{s.State}";
            return $"PUNK Multiverse logs — {who} ({role}), v{Plugin.Version}, " +
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
