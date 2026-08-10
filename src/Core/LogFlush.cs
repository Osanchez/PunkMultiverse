using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace PunkMultiverse.Core
{
    /// <summary>
    /// Push BepInEx's disk log to disk while the game is still running.
    ///
    /// Every incident this project has had to diagnose from a player's machine ran into the same
    /// wall: <c>BepInEx\LogOutput.log</c> is 0 bytes for the whole session and only materialises
    /// when the process exits cleanly. A hung game closed through Task Manager therefore takes the
    /// mod's entire log with it — during the 2026-08-08 black-screen night we had 642 MB of
    /// Unity's Player.log and not one line of ours until the game was closed properly.
    ///
    /// The listener's writer is found by TYPE, not by name: which member holds it has moved
    /// between BepInEx versions, and a name lookup that silently finds nothing is exactly the kind
    /// of diagnostic that lies.
    /// </summary>
    internal sealed class LogFlush : MonoBehaviour
    {
        private const float IntervalSeconds = 3f;

        private static readonly List<TextWriter> Writers = new List<TextWriter>();
        private static bool _reported;
        private float _next;

        private void Update()
        {
            if (Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + IntervalSeconds;
            Flush();
        }

        /// <summary>Flush now. Cheap enough to call from a shutdown path too.</summary>
        internal static void Flush()
        {
            try
            {
                if (Writers.Count == 0) Scan();   // listeners can appear after our Awake
                for (int i = 0; i < Writers.Count; i++)
                {
                    try { Writers[i].Flush(); } catch { }
                }
            }
            catch { }
        }

        private static void Scan()
        {
            const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            try
            {
                foreach (var listener in BepInEx.Logging.Logger.Listeners)
                {
                    if (listener == null) continue;
                    var type = listener.GetType();
                    foreach (var prop in type.GetProperties(Flags))
                    {
                        if (!typeof(TextWriter).IsAssignableFrom(prop.PropertyType)) continue;
                        try { Add(prop.GetValue(listener) as TextWriter); } catch { }
                    }
                    foreach (var field in type.GetFields(Flags))
                    {
                        if (!typeof(TextWriter).IsAssignableFrom(field.FieldType)) continue;
                        try { Add(field.GetValue(listener) as TextWriter); } catch { }
                    }
                }
                if (!_reported)
                {
                    _reported = true;
                    Plugin.Log.LogInfo(Writers.Count > 0
                        ? $"[Log] flushing {Writers.Count} disk writer(s) every {IntervalSeconds:0}s — " +
                          "a hung session's log survives a kill from now on"
                        : "[Log] no disk log writer found to flush; a hung session's mod log will still " +
                          "be lost unless the game is closed normally");
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[Log] listener scan failed: {e.Message}"); }
        }

        private static void Add(TextWriter writer)
        {
            if (writer == null || Writers.Contains(writer)) return;
            Writers.Add(writer);
        }
    }
}
