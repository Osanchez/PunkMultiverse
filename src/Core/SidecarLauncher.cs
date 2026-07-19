using System;
using System.Diagnostics;

namespace PunkMultiverse.Core
{
    /// <summary>
    /// Spawns and owns the local dedicated-coordinator process (server sidecar): this install's
    /// own Punk.exe, headless, with PUNKMV_COORDINATOR=1. The hosting player's game then JOINS it
    /// as a regular client, so their game crashing or stalling no longer takes the session down —
    /// the coordinator survives and they reconnect like anyone else.
    ///
    /// Local/LAN only until the direct-UDP transport lands (a sidecar cannot be a Steam-networking
    /// endpoint: one SteamID per account, no second appid instance). The sidecar's lifetime is
    /// bound to this process: killed on application quit; reused if still alive on a re-host.
    /// </summary>
    internal static class SidecarLauncher
    {
        private static Process _sidecar;
        private static bool _quitHooked;

        internal static bool IsRunning
        {
            get { try { return _sidecar != null && !_sidecar.HasExited; } catch { return false; } }
        }

        /// <summary>Launch the coordinator process (or reuse a live one). False = could not start;
        /// the caller should fall back to classic in-process hosting.</summary>
        internal static bool LaunchIfNeeded(out string error)
        {
            error = null;
            if (IsRunning) return true;
            try
            {
                string exe = Process.GetCurrentProcess().MainModule?.FileName;
                if (string.IsNullOrEmpty(exe))
                {
                    error = "own executable path unavailable";
                    return false;
                }
                var psi = new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = "-batchmode -nographics",
                    WorkingDirectory = System.IO.Path.GetDirectoryName(exe) ?? ".",
                    UseShellExecute = false, // required for environment variables
                };
                psi.EnvironmentVariables["PUNKMV_COORDINATOR"] = "1";
                // CRITICAL: the child inherits THIS game's environment, and Unity Doorstop marks
                // itself initialized there (DOORSTOP_INITIALIZED etc.) — inherited, the child's
                // injector thinks it already ran, skips BepInEx entirely, and the sidecar boots as
                // a VANILLA zombie: cores pegged on an uncapped headless menu loop, no mod, no port
                // (caught live on the first spawn test). Scrub every DOORSTOP_* key so injection
                // runs fresh in the child.
                var doorstopKeys = new System.Collections.Generic.List<string>();
                foreach (System.Collections.DictionaryEntry kv in psi.EnvironmentVariables)
                    if (kv.Key is string k && k.StartsWith("DOORSTOP", StringComparison.OrdinalIgnoreCase))
                        doorstopKeys.Add(k);
                foreach (var k in doorstopKeys) psi.EnvironmentVariables.Remove(k);
                _sidecar = Process.Start(psi);
                if (_sidecar == null)
                {
                    error = "Process.Start returned null";
                    return false;
                }
                if (!_quitHooked)
                {
                    _quitHooked = true;
                    UnityEngine.Application.quitting += Kill;
                }
                Plugin.Log.LogInfo($"[Sidecar] coordinator spawned pid={_sidecar.Id} ({exe})");
                return true;
            }
            catch (Exception e)
            {
                error = e.Message;
                _sidecar = null;
                return false;
            }
        }

        internal static void Kill()
        {
            try
            {
                if (IsRunning)
                {
                    Plugin.Log.LogInfo($"[Sidecar] stopping coordinator pid={_sidecar.Id}");
                    _sidecar.Kill();
                }
            }
            catch { }
            finally { _sidecar = null; }
        }
    }
}
