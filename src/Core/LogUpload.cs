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
    /// reports, and `uploadlogs` ships this machine's BepInEx log (gzipped) to a write-only S3
    /// prefix, grouped by that id:
    ///     {LogUploadBase}/PunkMultiverse/logs/{runId}/{player}-P{slot}-{utc}.log.gz
    /// The bucket policy is anonymous PUT-only on that prefix (no reads, no listing), so the mod
    /// ships no credentials; a lifecycle rule expires objects. Empty LogUploadBase disables.
    /// </summary>
    internal static class LogUpload
    {
        /// <summary>Same on every machine of a run: seed hex + low 16 bits of the host identity.
        /// Survives StopSession deliberately — crash/quit is exactly when logs get uploaded.</summary>
        internal static string RunId { get; private set; } = "no-run";

        internal static void SetRun(int seed, ulong hostIdentity)
        {
            RunId = $"{(uint)seed:X8}-{(hostIdentity & 0xFFFF):X4}";
            Plugin.Log.LogInfo($"[Diag] run id {RunId} — quote this in reports; `uploadlogs` sends this machine's log");
        }

        private static bool _uploading;

        /// <summary>Gzip the live BepInEx log and PUT it under this run's folder. Runs as a
        /// coroutine on the session behaviour; reports through <paramref name="output"/> (devout)
        /// and a toast, and logs the object key so a report can reference the exact file.</summary>
        internal static void Upload(NetSession session, Action<string> output)
        {
            string baseUrl = NetConfig.LogUploadBase != null ? NetConfig.LogUploadBase.Value?.Trim() : "";
            if (string.IsNullOrEmpty(baseUrl)) { output("uploadlogs: disabled ([Diag] LogUploadBase is empty)"); return; }
            if (_uploading) { output("uploadlogs: an upload is already in flight"); return; }

            byte[] gz;
            string logPath = Path.Combine(BepInEx.Paths.BepInExRootPath, "LogOutput.log");
            try
            {
                // BepInEx holds the file open for writing — share-everything read, snapshot now.
                using (var src = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var mem = new MemoryStream())
                {
                    using (var zip = new GZipStream(mem, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
                        src.CopyTo(zip);
                    gz = mem.ToArray();
                }
            }
            catch (Exception e)
            {
                output($"uploadlogs: could not read log ({e.Message})");
                return;
            }

            string who = Sanitize(session?.LocalPlayer?.Name ?? Environment.UserName);
            int slot = session != null ? session.LocalSlot : -1;
            string key = $"PunkMultiverse/logs/{RunId}/{who}-P{slot + 1}-{DateTime.UtcNow:yyyyMMdd-HHmmss}.log.gz";
            string url = $"{baseUrl.TrimEnd('/')}/{key}";
            _uploading = true;
            session.StartCoroutine(Put(url, key, gz, output));
        }

        private static IEnumerator Put(string url, string key, byte[] body, Action<string> output)
        {
            using (var req = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPUT))
            {
                req.uploadHandler = new UploadHandlerRaw(body) { contentType = "application/gzip" };
                req.downloadHandler = new DownloadHandlerBuffer();
                req.timeout = 60;
                yield return req.SendWebRequest();
                _uploading = false;
                if (req.result == UnityWebRequest.Result.Success)
                {
                    Plugin.Log.LogInfo($"[Diag] log uploaded ({body.Length / 1024} KiB) -> {key}");
                    output($"uploadlogs: OK ({body.Length / 1024} KiB) -> {key}");
                    UI.Toast.Show("LOG UPLOADED — THANK YOU", 4f);
                }
                else
                {
                    Plugin.Log.LogWarning($"[Diag] log upload failed: {req.responseCode} {req.error}");
                    output($"uploadlogs: FAILED ({req.responseCode} {req.error})");
                    UI.Toast.Show("LOG UPLOAD FAILED — SEE LOG", 4f);
                }
            }
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
