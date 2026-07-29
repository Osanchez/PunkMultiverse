using System;
using System.Diagnostics;
using System.Threading;
using UnityEngine;

namespace PunkMultiverse.Core
{
    /// <summary>
    /// Guarantees the process actually dies when the player quits.
    ///
    /// The mod self-initializes the Steam API on direct (non-Steam) launches, and an
    /// un-shut-down steamclient intermittently deadlocks process exit — the "windowless zombie
    /// Punk.exe". <c>Plugin.OnDestroy</c> already shuts the API down for exactly this reason, and
    /// that is still the right thing to do, but it is not sufficient: measured 2026-07-29, a main
    /// install quit cleanly (BepInEx log ends `[Steam] self-initialized API shut down` /
    /// `[Session] stopped (plugin unloaded)`, Unity got as far as `Input System module state
    /// changed to: Shutdown`) and the process then sat for five and a half hours with its window
    /// destroyed, 57 threads, every one of them in Wait, and zero CPU. Nothing of ours was running:
    /// every mod thread is IsBackground, and the hitch watchdog's stack capture — the one component
    /// that could suspend a thread — had already degraded to `main-stack-failed=The method or
    /// operation is not implemented` on this Mono runtime, so it never suspended anything.
    ///
    /// Omar could not see it (no window) but it kept the game "running" for Steam and blocked the
    /// test harness, which refuses to start while a Punk.exe is alive.
    ///
    /// The deadlock is inside a native library we do not control, so this does the one thing that
    /// always works: once Unity says it is quitting, wait a generous grace period for the normal
    /// shutdown to finish, and if the process is somehow still here, kill it outright.
    ///
    /// Safety: it arms ONLY on <c>Application.quitting</c> — the player has already asked to exit
    /// and Unity has begun teardown, with saves (`options.json`, the run stash) written before this
    /// point. The timer thread is a background thread, so it can never be the thing keeping the
    /// process alive, and if the normal shutdown completes first the whole process is gone and the
    /// timer dies with it. <c>Process.Kill</c> rather than <c>Environment.Exit</c> deliberately:
    /// Environment.Exit runs finalizers and would queue behind the very deadlock we are escaping.
    /// </summary>
    internal static class ExitWatchdog
    {
        private static int _armed;

        internal static void Install()
        {
            try { Application.quitting += Arm; }
            catch (Exception e) { Plugin.Log.LogWarning($"[Exit] watchdog not installed: {e.Message}"); }
        }

        /// <summary>Also callable from the plugin's own teardown, so a quit path that somehow never
        /// raises Application.quitting still gets the guarantee.</summary>
        internal static void Arm()
        {
            if (Interlocked.Exchange(ref _armed, 1) != 0) return; // already counting
            int seconds = NetConfig.ExitWatchdogSeconds != null ? NetConfig.ExitWatchdogSeconds.Value : 10;
            if (seconds <= 0)
            {
                Plugin.Log.LogInfo("[Exit] watchdog disabled by config — a hung shutdown will zombie the process");
                return;
            }
            Plugin.Log.LogInfo($"[Exit] quit started — process will be force-closed if shutdown hangs past {seconds}s");
            StartTimer(seconds);
        }

        /// <summary>Runs the kill path on demand (`exitkill &lt;seconds&gt;` devcmd) WITHOUT quitting, so
        /// the mechanism can be verified. A real shutdown always beats the timer on a healthy exit —
        /// which is the correct outcome, but it means the branch that actually matters never runs in
        /// a normal test. This is how it gets exercised: arm the timer while the game keeps running,
        /// and the process must die with a non-zero exit code and leave exit-watchdog.log behind.</summary>
        internal static void ForceTest(int seconds)
        {
            Plugin.Log.LogWarning($"[Exit] TEST: arming the force-close path for {seconds}s with no quit in progress");
            StartTimer(Math.Max(1, seconds));
        }

        private static void StartTimer(int seconds)
        {
            var timer = new Thread(() =>
            {
                try
                {
                    Thread.Sleep(seconds * 1000);
                    // Still alive this far past the quit request: the normal path is wedged.
                    // Nothing below can block — no managed cleanup, no finalizers, no flush.
                    string note = $"[Exit] shutdown did not complete within {seconds}s — " +
                        "force-closing (steamclient deadlock on exit; see Core/ExitWatchdog.cs)";
                    // Synchronous append FIRST. Kill() is abrupt by design, so the BepInEx log —
                    // buffered, and already shutting down — loses this line: measured, a forced exit
                    // left no trace at all in LogOutput.log. A watchdog whose only evidence is the
                    // absence of a process is one nobody can debug later, so the record goes to its
                    // own file with a write that has already hit disk before the process dies.
                    try
                    {
                        System.IO.File.AppendAllText(
                            System.IO.Path.Combine(ModFolder.Dir, "exit-watchdog.log"),
                            $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {note}{Environment.NewLine}");
                    }
                    catch { }
                    try { Plugin.Log.LogWarning(note); } catch { }
                    Process.GetCurrentProcess().Kill();
                }
                catch { }
            })
            { IsBackground = true, Name = "PunkMV Exit Watchdog" };
            timer.Start();
        }
    }
}
