using System;
using System.Collections;
using System.IO;
using System.IO.Compression;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.Networking;

namespace PunkMultiverse.Core
{
    /// <summary>
    /// In-game update check and (opt-out) auto-update — no companion app needed. Queries the
    /// latest GitHub release once per session; when a newer build exists and AutoUpdate is on,
    /// downloads the release zip and stages the new DLL in place. The running assembly is
    /// already loaded, so the swap takes effect on the NEXT launch; the replaced build stays
    /// beside it as PunkMultiverse.dll.bak for manual rollback. Fail-open at every step: any
    /// error just leaves the current build in place with the manual releases link logged.
    /// </summary>
    internal static class UpdateCheck
    {
        public const string ReleasesUrl = "https://github.com/Osanchez/PunkMultiverse/releases";
        private const string ApiUrl = "https://api.github.com/repos/Osanchez/PunkMultiverse/releases/latest";

        /// <summary>Newest published version, when it's newer than this build; else null.</summary>
        public static Version UpdateAvailable { get; private set; }
        /// <summary>Version staged on disk, waiting for a restart; else null.</summary>
        public static Version UpdateStaged { get; private set; }
        /// <summary>True once the GitHub query returned a definitive answer (up to date OR update).</summary>
        public static bool Resolved { get; private set; }
        private static bool _checked;

        public static void Kick(MonoBehaviour runner)
        {
            if (_checked) return;
            _checked = true;
            runner.StartCoroutine(Check());
        }

        private static IEnumerator Check()
        {
            string json;
            using (var request = UnityWebRequest.Get(ApiUrl))
            {
                request.timeout = 10;
                request.SetRequestHeader("User-Agent", "PunkMultiverse");
                yield return request.SendWebRequest();
                if (request.result != UnityWebRequest.Result.Success) yield break;
                json = request.downloadHandler.text;
            }

            try
            {
                // The release asset is PunkMultiverse-vX.Y.Z.zip — the mod version lives there.
                var match = Regex.Match(json, @"PunkMultiverse-v([0-9]+(?:\.[0-9]+)+)\.zip");
                if (!match.Success) yield break;
                var latest = Version.Parse(match.Groups[1].Value);
                var current = Version.Parse(Plugin.Version);
                if (latest > current)
                {
                    UpdateAvailable = latest;
                    Plugin.Log.LogWarning($"[Update] v{latest} is available (you run v{current}) — {ReleasesUrl}");
                }
                else
                {
                    Plugin.Log.LogInfo($"[Update] up to date (v{current})");
                }
                Resolved = true;
            }
            catch (Exception e)
            {
                Plugin.Log.LogDebug($"[Update] check failed: {e.Message}");
                yield break;
            }

            if (UpdateAvailable == null || !NetConfig.AutoUpdate.Value) yield break;

            string url = null;
            try
            {
                var urlMatch = Regex.Match(json,
                    "\"browser_download_url\"\\s*:\\s*\"([^\"]*PunkMultiverse-v[0-9.]+\\.zip)\"");
                if (urlMatch.Success) url = urlMatch.Groups[1].Value;
            }
            catch { }
            if (url == null) yield break;

            Plugin.Log.LogInfo($"[Update] downloading v{UpdateAvailable}…");
            using (var request = UnityWebRequest.Get(url))
            {
                request.timeout = 120;
                request.SetRequestHeader("User-Agent", "PunkMultiverse");
                yield return request.SendWebRequest();
                if (request.result != UnityWebRequest.Result.Success)
                {
                    Plugin.Log.LogWarning($"[Update] download failed ({request.error}) — update manually: {ReleasesUrl}");
                    yield break;
                }
                TryStage(request.downloadHandler.data, UpdateAvailable);
            }
        }

        private static void TryStage(byte[] zipBytes, Version version)
        {
            string live = Path.Combine(ModFolder.Dir, "PunkMultiverse.dll");
            string bak = live + ".bak";
            try
            {
                byte[] dll = null;
                using (var zip = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read))
                {
                    foreach (var entry in zip.Entries)
                    {
                        if (entry.Name != "PunkMultiverse.dll") continue;
                        using (var stream = entry.Open())
                        using (var ms = new MemoryStream())
                        {
                            stream.CopyTo(ms);
                            dll = ms.ToArray();
                        }
                        break;
                    }
                }
                // ZipArchive verifies CRCs on read; still sanity-check what we're about to
                // install — a bad DLL here means the plugin never loads again.
                if (dll == null || dll.Length < 50_000 || dll[0] != (byte)'M' || dll[1] != (byte)'Z')
                {
                    Plugin.Log.LogWarning("[Update] release zip had no valid PunkMultiverse.dll — not staging");
                    return;
                }

                // A mapped assembly's file can be RENAMED (unlike overwritten): move the live
                // build aside as the rollback and write the new one under the live name.
                if (File.Exists(bak)) File.Delete(bak);
                File.Move(live, bak);
                try
                {
                    File.WriteAllBytes(live, dll);
                }
                catch
                {
                    File.Move(bak, live); // never leave the plugin missing
                    throw;
                }
                UpdateStaged = version;
                Plugin.Log.LogWarning($"[Update] v{version} staged — restart to apply (rollback: PunkMultiverse.dll.bak)");
                try { UI.Toast.Show($"MOD UPDATED TO v{version} — RESTART TO APPLY", 8f); } catch { }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[Update] auto-update failed: {e.Message} — update manually: {ReleasesUrl}");
            }
        }
    }
}
