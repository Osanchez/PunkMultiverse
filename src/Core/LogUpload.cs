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
            // Date prefix makes the S3 console navigable ("which run was last night's?") — the
            // hash alone wasn't (Omar, 2026-07-23). UTC date at go-live: every machine of a run
            // stamps the same folder except the midnight-straddle edge, which is acceptable.
            string next = $"{DateTime.UtcNow:yyyy-MM-dd}_{(uint)seed:X8}-{(hostIdentity & 0xFFFF):X4}";
            if (next != RunId) { _sendsThisRun = 0; _nextAllowedSendAt = 0f; } // fresh run, fresh budget
            RunId = next;
            Plugin.Log.LogInfo($"[Diag] run id {RunId} — quote this in reports; SEND LOGS / F8 uploads this machine's log");
        }

        // Anti-spam. The STABLE key means a re-send overwrites this player's single object, so
        // spamming can never grow a run's folder — these bounds exist to stop repeat *requests*
        // (a stuck key, an impatient player) from costing anything.
        private const float SendCooldownSeconds = 30f;
        private const int MaxSendsPerRun = 5;
        private static float _nextAllowedSendAt;
        private static int _sendsThisRun;
        private static bool _busy;

        /// <summary>Why a send would be refused right now, or null if it's allowed. Lets the UI
        /// grey the button out and say why instead of failing after the click.</summary>
        internal static string BlockedReason()
        {
            if (_busy) return "already sending";
            if (_sendsThisRun >= MaxSendsPerRun) return $"limit reached ({MaxSendsPerRun} per run)";
            float wait = _nextAllowedSendAt - Time.unscaledTime;
            if (wait > 0f) return $"wait {Mathf.CeilToInt(wait)}s";
            return null;
        }

        /// <summary>UI entry (pause-menu button / F8). Returns a toast line ONLY when the send was
        /// refused up front; otherwise null, because the outcome is asynchronous and Upload /
        /// SignAndPut raise their own toast when it's actually known. (Returning "sending…" here
        /// produced two toasts and reported success before the PUT had happened.)</summary>
        internal static string UploadFromUi(NetSession session)
        {
            string blocked = BlockedReason();
            if (blocked != null) return $"LOGS: {blocked.ToUpperInvariant()}";
            UI.Toast.Show("SENDING LOG…", 2f);
            Upload(session, line => Plugin.Log.LogInfo($"[Diag] {line}"));
            return null;
        }

        internal static void Upload(NetSession session, Action<string> output)
        {
            string blocked = BlockedReason();
            if (blocked != null) { output($"uploadlogs: {blocked}"); return; }
            _nextAllowedSendAt = Time.unscaledTime + SendCooldownSeconds;
            _sendsThisRun++;

            // ---- 1. snapshot + gzip the live log, and SAVE IT LOCALLY no matter what ----
            string player = Sanitize(session?.LocalPlayer?.Name ?? Environment.UserName);
            // Stable, collision-safe per-player suffix: the display name alone can repeat between
            // players, and a timestamp would make every send a NEW object. Identity hash does both.
            string pid = ShortId(session?.LocalPlayer?.IdentityId ?? 0);
            string name = $"{player}-{pid}.log.gz";
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
            session.StartCoroutine(SignAndPut(endpoint, player, pid, gz, localPath, kib, output));
        }

        private static IEnumerator SignAndPut(string endpoint, string player, string pid, byte[] body,
            string localPath, int kib, Action<string> output)
        {
            string signUrl = $"{endpoint.TrimEnd('/')}?runId={UnityWebRequest.EscapeURL(RunId)}" +
                             $"&player={UnityWebRequest.EscapeURL(player)}" +
                             $"&pid={UnityWebRequest.EscapeURL(pid)}&size={body.Length}";
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

        /// <summary>8 hex chars derived from the player's stable identity (SteamID / install id).
        /// Distinguishes same-named players without putting a raw SteamID in the object key.</summary>
        private static string ShortId(ulong identity)
        {
            if (identity == 0) return "LOCAL000";
            ulong h = identity * 1099511628211UL ^ 14695981039346656037UL;
            return ((uint)(h ^ (h >> 32))).ToString("X8");
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
