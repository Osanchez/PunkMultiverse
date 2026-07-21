using System;
using System.Collections;
using System.IO;
using System.IO.Compression;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace PunkMultiverse.Core
{
    /// <summary>
    /// Field log diagnostics (tester pipeline, 2026-07-20). Every net run gets a RUN ID that is
    /// IDENTICAL on every machine without any wire change — derived from the run seed + the host's
    /// stable identity, both of which every player already shares at go-live. Players quote it in
    /// reports, and `uploadlogs` collects this machine's BepInEx log for that run.
    ///
    /// The mod carries NO AWS credentials (a shipped DLL is trivially decompiled) and the bucket
    /// allows no anonymous access. Instead `uploadlogs`:
    ///   1. ALWAYS gzips the log to  &lt;mod folder&gt;/diagnostics/&lt;runId&gt;/&lt;player&gt;-&lt;utc&gt;.log.gz
    ///   2. asks the signer endpoint for a short-lived presigned PUT URL for exactly that object
    ///   3. PUTs the bytes to it
    /// Step 1 is the point: the upload is best-effort, so a missing/unreachable/erroring endpoint
    /// (or no network at all) still leaves the player a single file to send manually, and the
    /// command tells them its exact path. There is no failure mode where the log is simply lost.
    /// </summary>
    internal static class LogUpload
    {
        /// <summary>Same on every machine of a run: seed hex + low 16 bits of the host identity.
        /// Survives StopSession deliberately — crash/quit is exactly when logs get uploaded.</summary>
        internal static string RunId { get; private set; } = "no-run";

        internal static void SetRun(int seed, ulong hostIdentity)
        {
            RunId = $"{(uint)seed:X8}-{(hostIdentity & 0xFFFF):X4}";
            Plugin.Log.LogInfo($"[Diag] run id {RunId} — quote this in reports; `uploadlogs` collects this machine's log");
        }

        private static bool _busy;

        internal static void Upload(NetSession session, Action<string> output)
        {
            if (_busy) { output("uploadlogs: already collecting"); return; }

            // ---- 1. snapshot + gzip the live log, and SAVE IT LOCALLY no matter what ----
            string player = Sanitize(session?.LocalPlayer?.Name ?? Environment.UserName);
            int slot = session != null ? session.LocalSlot : -1;
            string stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
            string name = $"{player}-P{slot + 1}-{stamp}.log.gz";
            string localPath;
            byte[] gz;
            try
            {
                string dir = Path.Combine(Path.Combine(ModFolder.Dir, "diagnostics"), RunId);
                Directory.CreateDirectory(dir);
                localPath = Path.Combine(dir, name);

                // BepInEx holds the log open for writing — share-everything read, snapshot now.
                string logPath = Path.Combine(BepInEx.Paths.BepInExRootPath, "LogOutput.log");
                using (var src = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var mem = new MemoryStream())
                {
                    using (var zip = new GZipStream(mem, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
                        src.CopyTo(zip);
                    gz = mem.ToArray();
                }
                File.WriteAllBytes(localPath, gz);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[Diag] could not collect log: {e.Message}");
                output($"uploadlogs: FAILED to read/write the log ({e.Message})");
                return;
            }

            int kib = gz.Length / 1024;
            Plugin.Log.LogInfo($"[Diag] log saved ({kib} KiB) -> {localPath}");

            // ---- 2. best-effort upload through the signer ----
            string endpoint = NetConfig.LogUploadEndpoint != null ? NetConfig.LogUploadEndpoint.Value?.Trim() : "";
            if (string.IsNullOrEmpty(endpoint))
            {
                output($"uploadlogs: saved locally ({kib} KiB) — no upload endpoint set. Send this file: {localPath}");
                UI.Toast.Show("LOG SAVED LOCALLY — SEE CONSOLE FOR PATH", 6f);
                return;
            }
            if (session == null)
            {
                output($"uploadlogs: saved locally ({kib} KiB) — no session to run the upload on. Send: {localPath}");
                return;
            }
            _busy = true;
            session.StartCoroutine(SignAndPut(endpoint, player, gz, localPath, kib, output));
        }

        private static IEnumerator SignAndPut(string endpoint, string player, byte[] body,
            string localPath, int kib, Action<string> output)
        {
            string signUrl = $"{endpoint.TrimEnd('/')}?runId={UnityWebRequest.EscapeURL(RunId)}" +
                             $"&player={UnityWebRequest.EscapeURL(player)}&size={body.Length}";
            string putUrl = null;
            string key = null;

            using (var sign = UnityWebRequest.Get(signUrl))
            {
                sign.timeout = 20;
                yield return sign.SendWebRequest();
                if (sign.result == UnityWebRequest.Result.Success)
                {
                    try
                    {
                        // Tiny hand-parse: the response is our own {"url":"...","key":"..."} and
                        // pulling in a JSON dependency for two fields isn't worth it.
                        putUrl = ExtractJsonString(sign.downloadHandler.text, "url");
                        key = ExtractJsonString(sign.downloadHandler.text, "key");
                    }
                    catch { }
                }
                else
                {
                    Plugin.Log.LogWarning($"[Diag] log signer unreachable: {sign.responseCode} {sign.error}");
                }
            }

            if (string.IsNullOrEmpty(putUrl))
            {
                _busy = false;
                output($"uploadlogs: upload unavailable — log saved locally ({kib} KiB). Send this file: {localPath}");
                UI.Toast.Show("LOG SAVED LOCALLY — SEE CONSOLE FOR PATH", 6f);
                yield break;
            }

            using (var put = new UnityWebRequest(putUrl, UnityWebRequest.kHttpVerbPUT))
            {
                put.uploadHandler = new UploadHandlerRaw(body) { contentType = "application/gzip" };
                put.downloadHandler = new DownloadHandlerBuffer();
                put.timeout = 120;
                yield return put.SendWebRequest();
                _busy = false;
                if (put.result == UnityWebRequest.Result.Success)
                {
                    Plugin.Log.LogInfo($"[Diag] log uploaded ({kib} KiB) -> {key}");
                    output($"uploadlogs: UPLOADED ({kib} KiB) as {key} (local copy: {localPath})");
                    UI.Toast.Show("LOG UPLOADED — THANK YOU", 4f);
                }
                else
                {
                    Plugin.Log.LogWarning($"[Diag] log upload failed: {put.responseCode} {put.error}");
                    output($"uploadlogs: upload FAILED ({put.responseCode} {put.error}) — " +
                           $"log saved locally ({kib} KiB). Send this file: {localPath}");
                    UI.Toast.Show("LOG SAVED LOCALLY — SEE CONSOLE FOR PATH", 6f);
                }
            }
        }

        /// <summary>Minimal `"field":"value"` reader for the signer's own small JSON response.</summary>
        private static string ExtractJsonString(string json, string field)
        {
            if (string.IsNullOrEmpty(json)) return null;
            string needle = "\"" + field + "\"";
            int i = json.IndexOf(needle, StringComparison.Ordinal);
            if (i < 0) return null;
            i = json.IndexOf(':', i + needle.Length);
            if (i < 0) return null;
            int open = json.IndexOf('"', i + 1);
            if (open < 0) return null;
            var sb = new StringBuilder(128);
            for (int p = open + 1; p < json.Length; p++)
            {
                char c = json[p];
                if (c == '\\' && p + 1 < json.Length) { sb.Append(json[++p]); continue; }
                if (c == '"') break;
                sb.Append(c);
            }
            return sb.Length > 0 ? sb.ToString() : null;
        }

        private static string Sanitize(string name)
        {
            if (string.IsNullOrEmpty(name)) return "player";
            var sb = new StringBuilder(name.Length);
            foreach (var c in name)
                if (char.IsLetterOrDigit(c) || c == '-' || c == '_') sb.Append(c);
            return sb.Length > 0 ? sb.ToString(0, Math.Min(sb.Length, 24)) : "player";
        }
    }
}
