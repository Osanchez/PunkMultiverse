using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;

namespace PunkMultiverse.Core
{
    /// <summary>Background observer for a Unity main thread that stops advancing. It never calls
    /// Unity APIs; the main thread publishes only primitive snapshots.</summary>
    internal static class MainThreadWatchdog
    {
        private const long MaxFallbackBytes = 1024 * 1024;
        private const int PollMs = 25;
        private const int MaxStackChars = 16 * 1024;

        private static Thread _thread;
        private static Thread _mainThread;
        private static StreamWriter _fallback;
        private static ConstructorInfo _threadStackCtor;
        private static long _lastActivityTicks;
        private static long _heartbeat;
        private static int _phase;
        private static int _sessionState;
        private static int _enabled;
        private static int _thresholdMs = 250;
        private static int _repeatMs = 2000;
        private static int _captureStack = 1;
        private static int _shutdown;
        private static int _hitchId;
        private static int _handler = -1;
        private static long _dispatchSeq;

        private static Thread _captureWorker;
        private static volatile bool _captureDone;
        private static string _captureResult;
        private static string _captureError;
        private static int _captureHitchId;
        private static long _captureStartedTicks;
        private static bool _captureTimeoutReported;

        internal static string FallbackPath => Path.Combine(ModFolder.Dir, "PunkMultiverse.hitch.log");
        internal static bool StackCaptureSupported => _threadStackCtor != null;

        internal static void Initialize(Thread mainThread)
        {
            if (_thread != null) return;
            _mainThread = mainThread;
            Interlocked.Exchange(ref _lastActivityTicks, Stopwatch.GetTimestamp());
            try
            {
                _threadStackCtor = typeof(StackTrace).GetConstructor(
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                    null, new[] { typeof(Thread), typeof(bool) }, null);
            }
            catch { _threadStackCtor = null; }

            try
            {
                Directory.CreateDirectory(ModFolder.Dir);
                var path = FallbackPath;
                var mode = File.Exists(path) && new FileInfo(path).Length < MaxFallbackBytes
                    ? FileMode.Append : FileMode.Create;
                _fallback = new StreamWriter(new FileStream(path, mode, FileAccess.Write, FileShare.Read),
                    new UTF8Encoding(false)) { AutoFlush = true };
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[Hitch] fallback log unavailable: {e.Message}");
            }

            _thread = new Thread(Run) { IsBackground = true, Name = "PunkMV Hitch Watchdog" };
            _thread.Start();
        }

        internal static void Configure(bool enabled, int thresholdMs, int repeatMs, bool captureStack)
        {
            _thresholdMs = Math.Max(100, Math.Min(5000, thresholdMs));
            _repeatMs = Math.Max(500, Math.Min(30000, repeatMs));
            Volatile.Write(ref _captureStack, captureStack ? 1 : 0);
            Volatile.Write(ref _enabled, enabled ? 1 : 0);
        }

        internal static void PublishState(SessionState state)
        {
            Volatile.Write(ref _sessionState, (int)state);
        }

        internal static void Beat(int phase)
        {
            Volatile.Write(ref _phase, phase);
            Interlocked.Increment(ref _heartbeat);
            Interlocked.Exchange(ref _lastActivityTicks, Stopwatch.GetTimestamp());
        }

        internal static void Phase(int phase)
        {
            Volatile.Write(ref _phase, phase);
            Interlocked.Exchange(ref _lastActivityTicks, Stopwatch.GetTimestamp());
        }

        internal static int CurrentPhase => Volatile.Read(ref _phase);

        // The message-dispatch loop publishes the handler it's about to run plus a monotonic counter,
        // so a hitch inside Transport.Poll names its handler. A disp# frozen across "ongoing" lines is
        // the wedged handler; an advancing disp# means the stall is elsewhere.
        internal static void SetHandler(int msgType)
        {
            Volatile.Write(ref _handler, msgType);
            Interlocked.Increment(ref _dispatchSeq);
        }

        internal static void Shutdown()
        {
            Volatile.Write(ref _shutdown, 1);
            try { _thread?.Join(250); } catch { }
            _thread = null;
            try { _fallback?.Dispose(); } catch { }
            _fallback = null;
        }

        private static void Run()
        {
            bool stalled = false;
            long stalledHeartbeat = 0;
            long detectedAt = 0;
            long nextRepeatAt = 0;
            int activeId = 0;

            while (Volatile.Read(ref _shutdown) == 0)
            {
                Thread.Sleep(PollMs);
                FlushStackResult();
                if (Volatile.Read(ref _enabled) == 0)
                {
                    stalled = false;
                    continue;
                }

                int state = Volatile.Read(ref _sessionState);
                if (state != (int)SessionState.Loading && state != (int)SessionState.InGame)
                {
                    stalled = false;
                    continue;
                }

                long now = Stopwatch.GetTimestamp();
                long beat = Interlocked.Read(ref _heartbeat);
                long last = Interlocked.Read(ref _lastActivityTicks);
                double ageMs = (now - last) * 1000.0 / Stopwatch.Frequency;

                if (!stalled)
                {
                    if (ageMs < _thresholdMs) continue;
                    stalled = true;
                    stalledHeartbeat = beat;
                    detectedAt = now;
                    nextRepeatAt = now + MsToTicks(_repeatMs);
                    activeId = Interlocked.Increment(ref _hitchId);
                    string line = Format(activeId, "detected", ageMs, state, beat);
                    Emit(line);
                    StartStackCapture(activeId);
                    continue;
                }

                if (beat != stalledHeartbeat)
                {
                    FlushStackResult();
                    double duration = (now - detectedAt) * 1000.0 / Stopwatch.Frequency + _thresholdMs;
                    Emit(string.Format(CultureInfo.InvariantCulture,
                        "[Hitch] id={0} recovered duration={1:0}ms mono={2:0.000}s",
                        activeId, duration, now / (double)Stopwatch.Frequency));
                    stalled = false;
                    continue;
                }

                FlushStackResult();
                if (!_captureDone && _captureWorker != null && _captureWorker.IsAlive
                    && !_captureTimeoutReported && now - _captureStartedTicks >= MsToTicks(500))
                {
                    _captureTimeoutReported = true;
                    Emit($"[Hitch] id={_captureHitchId} main-stack-timeout=500ms fallback=phase-marker");
                }
                if (now >= nextRepeatAt)
                {
                    nextRepeatAt = now + MsToTicks(_repeatMs);
                    Emit(Format(activeId, "ongoing", ageMs, state, beat));
                }
            }
        }

        private static string Format(int id, string kind, double ageMs, int state, long heartbeat)
        {
            long now = Stopwatch.GetTimestamp();
            int handler = Volatile.Read(ref _handler);
            string handlerStr = handler >= 0
                ? string.Format(CultureInfo.InvariantCulture, " handler={0} disp#={1}",
                    ((PunkMultiverse.Protocol.MsgType)handler).ToString(), Interlocked.Read(ref _dispatchSeq))
                : "";
            return string.Format(CultureInfo.InvariantCulture,
                "[Hitch] id={0} {1} age={2:0}ms mono={3:0.000}s state={4} phase={5} heartbeat={6}{7}",
                id, kind, ageMs, now / (double)Stopwatch.Frequency,
                ((SessionState)state).ToString(), RuntimeInstrumentation.PhaseName(Volatile.Read(ref _phase)), heartbeat, handlerStr);
        }

        private static void StartStackCapture(int hitchId)
        {
            if (Volatile.Read(ref _captureStack) == 0)
            {
                Emit($"[Hitch] id={hitchId} main-stack=disabled fallback=phase-marker");
                return;
            }
            if (_threadStackCtor == null)
            {
                Emit($"[Hitch] id={hitchId} main-stack-supported=false fallback=phase-marker");
                return;
            }
            if (_captureWorker != null && _captureWorker.IsAlive)
            {
                Emit($"[Hitch] id={hitchId} main-stack=busy fallback=phase-marker");
                return;
            }

            _captureDone = false;
            _captureResult = null;
            _captureError = null;
            _captureHitchId = hitchId;
            _captureStartedTicks = Stopwatch.GetTimestamp();
            _captureTimeoutReported = false;
            _captureWorker = new Thread(() =>
            {
                try
                {
                    var trace = _threadStackCtor.Invoke(new object[] { _mainThread, true }) as StackTrace;
                    string value = trace?.ToString() ?? "<empty>";
                    _captureResult = value.Length > MaxStackChars ? value.Substring(0, MaxStackChars) : value;
                }
                catch (Exception e) { _captureError = e.GetBaseException().Message; }
                finally { _captureDone = true; }
            }) { IsBackground = true, Name = "PunkMV Stack Capture" };
            _captureWorker.Start();
        }

        private static void FlushStackResult()
        {
            if (!_captureDone) return;
            _captureDone = false;
            if (_captureResult != null)
                Emit($"[Hitch] id={_captureHitchId} main-stack:\n{_captureResult}");
            else
                Emit($"[Hitch] id={_captureHitchId} main-stack-failed={_captureError ?? "unknown"} fallback=phase-marker");
        }

        private static void Emit(string line)
        {
            try { _fallback?.WriteLine(line); } catch { }
            try { Plugin.Log?.LogWarning(line); } catch { }
        }

        private static long MsToTicks(int ms) => (long)(ms / 1000.0 * Stopwatch.Frequency);
    }
}
