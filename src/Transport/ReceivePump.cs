using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;

namespace PunkMultiverse.Transport
{
    /// <summary>
    /// Shared threaded-receive mechanism for transports: a background thread pulls datagrams
    /// from the wire into pooled buffers; the main thread drains within NetConfig.ReceiveBudgetMs
    /// per frame so an inbound burst (baseline storm, dormancy avalanche on a player death)
    /// amortizes across frames instead of spiking one. Used by BOTH the Steam and the loopback
    /// transports — loopback is the local test bed for shipping behavior, so the mechanisms
    /// must match (NetConfig.ThreadedReceive governs both).
    /// </summary>
    internal sealed class ReceivePump
    {
        // Past this queue depth the drain ignores the per-frame budget and catches up hard —
        // falling steadily behind is worse than a few long frames.
        private const int ForceDrainDepth = 1024;
        // But catch-up must still be BOUNDED: it processes at most the backlog present at entry
        // and at most this many ms, then yields the frame. "Drain until empty" against a live
        // producer thread never exits when arrival outpaces dispatch — observed live as a 55+ s
        // main-thread freeze inside one drain while a damage-claim storm arrived faster than its
        // own synchronous log lines could be written.
        private const float CatchUpBudgetMs = 100f;

        public struct Item
        {
            public ulong Peer;
            public byte Channel; // transport-specific meaning (Steam: NetChannel; loopback: unused — the frame byte is in Buf)
            public byte[] Buf;   // rented from the shared pool; null when Len == 0
            public int Len;
            public object Tag;   // transport-specific (loopback: sender IPEndPoint)
        }

        public delegate void Dispatcher(in Item item);

        private readonly ConcurrentQueue<Item> _rx = new ConcurrentQueue<Item>();
        private readonly Stopwatch _drainWatch = new Stopwatch();
        private Thread _thread;
        private volatile bool _running;

        public bool ThreadRunning => _thread != null;

        public byte[] Rent(int size) => ArrayPool<byte>.Shared.Rent(size);

        public void Enqueue(in Item item) => _rx.Enqueue(item);

        /// <summary>Start the receive thread. <paramref name="receiveOnce"/> pulls everything
        /// currently available from the wire into the queue and returns how many datagrams it
        /// moved; the pump sleeps ~1ms between empty polls and backs off after an exception.</summary>
        public void Start(string name, Func<int> receiveOnce)
        {
            _running = true;
            _thread = new Thread(() => Loop(receiveOnce)) { Name = name, IsBackground = true };
            _thread.Start();
        }

        private void Loop(Func<int> receiveOnce)
        {
            while (_running)
            {
                int moved = 0;
                try { moved = receiveOnce(); }
                catch (Exception e)
                {
                    if (_running) Plugin.Log.LogWarning($"[Net] receive thread error: {e.Message}");
                    Thread.Sleep(50); // errors are unusual — back off instead of spinning on them
                }
                if (moved == 0) Thread.Sleep(1); // ~1ms cadence; these APIs have no blocking receive
            }
        }

        /// <summary>Main thread: dispatch queued datagrams within the frame budget; leftovers
        /// carry into the next frame. Buffers return to the pool right after dispatch, so
        /// handlers must consume the payload synchronously (the same contract the old shared
        /// receive buffer imposed).</summary>
        public void Drain(Dispatcher dispatch)
        {
            if (_rx.IsEmpty) return;
            float budgetMs = NetConfig.ReceiveBudgetMs.Value;
            int backlog = _rx.Count;
            bool catchUp = budgetMs <= 0f || backlog > ForceDrainDepth;
            long budgetTicks = (long)((catchUp ? CatchUpBudgetMs : budgetMs) / 1000f * Stopwatch.Frequency);
            // Catch-up drains only the items that were queued when it started — never the live
            // tail the pump thread keeps appending — so it always terminates.
            int remaining = catchUp ? backlog : int.MaxValue;
            _drainWatch.Restart();
            while (remaining-- > 0 && _rx.TryDequeue(out var item))
            {
                try { dispatch(in item); }
                finally { if (item.Buf != null) ArrayPool<byte>.Shared.Return(item.Buf); }
                if (budgetTicks > 0 && _drainWatch.ElapsedTicks >= budgetTicks) break;
            }
        }

        /// <summary>Stop the thread and discard anything still queued.</summary>
        public void Stop(int joinMs = 500)
        {
            _running = false;
            if (_thread != null)
            {
                if (!_thread.Join(joinMs))
                    Plugin.Log.LogWarning($"[Net] receive thread '{_thread.Name}' did not stop within {joinMs}ms");
                _thread = null;
            }
            while (_rx.TryDequeue(out var item))
                if (item.Buf != null) ArrayPool<byte>.Shared.Return(item.Buf);
        }
    }
}
