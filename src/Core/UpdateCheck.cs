using System;
using System.Collections;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.Networking;

namespace PunkMultiverse.Core
{
    /// <summary>
    /// In-game update check — no companion app needed. Queries the latest GitHub release once per
    /// session and compares the zip asset's version against this build. Result feeds the lobby
    /// screen banner; version-mismatch rejects point players here too. Fail-open: no network, no
    /// banner, nothing else changes.
    /// </summary>
    internal static class UpdateCheck
    {
        public const string ReleasesUrl = "https://github.com/Osanchez/PunkMultiverse/releases";
        private const string ApiUrl = "https://api.github.com/repos/Osanchez/PunkMultiverse/releases/latest";

        /// <summary>Newest published version, when it's newer than this build; else null.</summary>
        public static Version UpdateAvailable { get; private set; }
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
            using (var request = UnityWebRequest.Get(ApiUrl))
            {
                request.timeout = 10;
                request.SetRequestHeader("User-Agent", "PunkMultiverse");
                yield return request.SendWebRequest();
                if (request.result != UnityWebRequest.Result.Success) yield break;

                try
                {
                    // The release asset is PunkMultiverse-vX.Y.Z.zip — the mod version lives there.
                    var match = Regex.Match(request.downloadHandler.text, @"PunkMultiverse-v([0-9]+(?:\.[0-9]+)+)\.zip");
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
                }
            }
        }
    }
}
