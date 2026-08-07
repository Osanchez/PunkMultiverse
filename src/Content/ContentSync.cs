using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using PunkMultiverse.Core;
using PunkMultiverse.Protocol;
using UnityEngine;

namespace PunkMultiverse.Content
{
    internal enum ContentState : byte
    {
        Idle = 0,
        Downloading = 1,
        Installing = 2,
        Satisfied = 3,
        Failed = 4,
    }

    /// <summary>
    /// Host-served content: the host publishes the set it is running, every client ends up with
    /// byte-identical files before anyone reaches ship selection, and a rejoin costs nothing.
    ///
    /// This exists because the go-live barrier can only REFUSE a divergent run. Refusing is the
    /// right floor — a silent BR drop-table desync is much worse — but it is not a feature. This
    /// is the half that makes divergence not happen in the first place.
    ///
    /// Shape, and why:
    ///   * Transfers ride NetChannel.Events, the reliable bulk-tolerant lane, never Control.
    ///     A content stream must never be able to queue in front of a Welcome.
    ///   * Sends go straight through Transport.Send with backpressure off its bool return, the
    ///     same way WorldSync streams terrain — NOT through SendReliable, which copies every
    ///     payload and caps its outbox at 8192 with drop-oldest.
    ///   * The budget is bytes per SECOND, not per frame. ServerFrameRateCap deliberately caps a
    ///     dedicated server's frame rate, so a per-frame budget would quietly halve its
    ///     throughput.
    ///   * Hashing and disk IO run on a worker thread; NetWriter/NetReader/NetSession are
    ///     main-thread only, so the hand-off is byte[] through a queue drained under a budget.
    /// </summary>
    internal static class ContentSync
    {
        private const int ChunkBytes = 8192;
        private const int OfferChunkFiles = 96;
        private const int NeedChunkDigests = 192;

        // ---- local state -------------------------------------------------------------------

        internal static ContentState LocalState { get; private set; } = ContentState.Idle;
        internal static byte[] LocalSetHash { get; private set; }
        internal static string LocalFailure { get; private set; }
        internal static byte[] HostSetHash { get; private set; }
        /// <summary>Where the active set was laid out, for whoever consumes it.</summary>
        internal static string ActiveContentPath { get; private set; }

        private static List<ContentHash.Entry> _hostEntries;      // host: what we serve
        private static byte[] _hostSetHash;
        private static bool _hostReady;

        private static readonly Dictionary<byte, ContentState> PeerState = new Dictionary<byte, ContentState>();
        private static readonly Dictionary<byte, byte> PeerPercent = new Dictionary<byte, byte>();

        // client: assembling an offer
        private static List<ContentHash.Entry> _incoming;
        private static uint _incomingTotal;
        private static byte[] _incomingHash;

        // host: per-peer send queues
        private sealed class Stream
        {
            internal readonly Queue<byte[]> Wanted = new Queue<byte[]>();
            internal byte[] Current;
            internal long Offset;
            internal FileStream Open;
            internal double Allowance;
        }
        private static readonly Dictionary<ulong, Stream> Streams = new Dictionary<ulong, Stream>();

        // ---- worker ------------------------------------------------------------------------

        private abstract class Job { }
        private sealed class ScanJob : Job { internal string Root; internal long MaxFile; }
        private sealed class InstallJob : Job { internal byte[] SetHash; internal List<ContentHash.Entry> Entries; }
        private sealed class CommitJob : Job { internal byte[] Digest; }
        private sealed class SweepJob : Job { internal long Budget; internal byte[] Keep; }

        private abstract class Result { }
        private sealed class Scanned : Result { internal List<ContentHash.Entry> Entries; internal byte[] Hash; internal List<string> Problems; }
        private sealed class Installed : Result { internal byte[] SetHash; internal bool Ok; internal string Error; internal string Path; }
        private sealed class Committed : Result { internal byte[] Digest; internal bool Ok; internal string Error; }

        private static readonly ConcurrentQueue<Job> Jobs = new ConcurrentQueue<Job>();
        private static readonly ConcurrentQueue<Result> Results = new ConcurrentQueue<Result>();
        private static readonly AutoResetEvent Wake = new AutoResetEvent(false);
        private static Thread _worker;
        private static volatile bool _running;

        internal static void StartWorker()
        {
            if (_worker != null) return;
            _running = true;
            _worker = new Thread(WorkerLoop) { Name = "PunkMV Content", IsBackground = true };
            _worker.Start();
        }

        internal static void StopWorker()
        {
            if (_worker == null) return;
            _running = false;
            Wake.Set();
            if (!_worker.Join(1000))
                Plugin.Log.LogWarning("[Content] worker did not stop within 1s");
            else
                Plugin.Log.LogInfo("[Content] worker stopped");
            _worker = null;
        }

        private static void WorkerLoop()
        {
            while (_running)
            {
                if (!Jobs.TryDequeue(out var job)) { Wake.WaitOne(200); continue; }
                try
                {
                    switch (job)
                    {
                        case ScanJob s:
                        {
                            var entries = ContentStore.ScanDirectory(s.Root, s.MaxFile, out var skipped);
                            var problems = ContentHash.Validate(entries);
                            problems.AddRange(skipped);
                            // Host side: its own files become blobs, so serving reads the cache
                            // exactly the way a client's install does. One path, not two.
                            foreach (var e in entries)
                            {
                                var src = Path.Combine(s.Root, e.Path.Replace('/', Path.DirectorySeparatorChar));
                                if (!ContentStore.ImportFile(src, e.Digest, out var err))
                                    problems.Add($"{e.Path}: {err}");
                            }
                            Results.Enqueue(new Scanned
                            {
                                Entries = entries,
                                Hash = ContentHash.SetDigest(entries),
                                Problems = problems,
                            });
                            break;
                        }
                        case CommitJob c:
                        {
                            bool ok = ContentStore.CommitBlob(c.Digest, out var err);
                            Results.Enqueue(new Committed { Digest = c.Digest, Ok = ok, Error = err });
                            break;
                        }
                        case InstallJob i:
                        {
                            string error = null;
                            bool ok = ContentStore.WriteSet(i.SetHash, i.Entries, out error)
                                   && ContentStore.Materialise(i.SetHash, out error);
                            Results.Enqueue(new Installed
                            {
                                SetHash = i.SetHash,
                                Ok = ok,
                                Error = error,
                                Path = ContentStore.ActivePathFor(ContentHash.ToHex(i.SetHash)),
                            });
                            break;
                        }
                        case SweepJob s:
                            ContentStore.Evict(s.Budget, s.Keep);
                            break;
                    }
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"[Content] worker job failed: {e.Message}");
                }
            }
        }

        // ---- lifecycle ---------------------------------------------------------------------

        internal static void Reset()
        {
            foreach (var s in Streams.Values) { try { s.Open?.Dispose(); } catch { } }
            Streams.Clear();
            PeerState.Clear();
            PeerPercent.Clear();
            _incoming = null;
            _incomingHash = null;
            _incomingTotal = 0;
            LocalState = ContentState.Idle;
            LocalFailure = null;
            HostSetHash = null;
        }

        internal static void CancelFor(ulong peer)
        {
            if (!Streams.TryGetValue(peer, out var s)) return;
            try { s.Open?.Dispose(); } catch { }
            Streams.Remove(peer);
        }

        /// <summary>Host: hash the configured content root and stand ready to serve it.</summary>
        internal static void BeginHosting()
        {
            _hostReady = false;
            _hostEntries = null;
            _hostSetHash = null;
            var root = NetConfig.ContentRoot != null ? NetConfig.ContentRoot.Value : "";
            if (string.IsNullOrWhiteSpace(root))
            {
                _hostEntries = new List<ContentHash.Entry>();
                _hostSetHash = ContentHash.EmptySet();
                _hostReady = true;
                LocalSetHash = _hostSetHash;
                LocalState = ContentState.Satisfied;
                return;
            }
            ContentStore.EnsureDirectories();
            StartWorker();
            Jobs.Enqueue(new ScanJob
            {
                Root = Path.IsPathRooted(root) ? root : Path.Combine(ModFolder.Dir, root),
                MaxFile = (long)NetConfig.ContentMaxFileMB.Value * 1024 * 1024,
            });
            Wake.Set();
            Plugin.Log.LogInfo($"[Content] hashing content root '{root}'");
        }

        internal static bool HostHasContent => _hostReady && _hostEntries != null && _hostEntries.Count > 0;
        internal static byte[] HostSetDigest => _hostSetHash;

        /// <summary>Everyone the host is waiting on, for the lobby line.</summary>
        internal static ContentState StateOf(byte slot) =>
            PeerState.TryGetValue(slot, out var s) ? s : ContentState.Idle;

        internal static byte PercentOf(byte slot) =>
            PeerPercent.TryGetValue(slot, out var p) ? p : (byte)0;

        /// <summary>Host-side gate: is this peer allowed to be Ready?</summary>
        internal static bool Satisfied(byte slot)
        {
            if (!_hostReady) return true;             // nothing to be out of sync WITH yet
            if (!HostHasContent) return true;
            return StateOf(slot) == ContentState.Satisfied;
        }

        /// <summary>Local gate for the run seam.</summary>
        internal static bool SatisfiedLocally =>
            LocalState == ContentState.Satisfied || LocalState == ContentState.Idle;

        // ---- host: offering ------------------------------------------------------------------

        internal static void OfferTo(NetSession session, ulong peer)
        {
            if (session == null || !_hostReady) return;
            var entries = _hostEntries ?? new List<ContentHash.Entry>();
            bool empty = entries.Count == 0;
            ulong totalBytes = 0;
            foreach (var e in entries) totalBytes += (ulong)e.Length;

            for (uint start = 0; start == 0 || start < entries.Count; start += OfferChunkFiles)
            {
                int count = Math.Min(OfferChunkFiles, Math.Max(0, entries.Count - (int)start));
                var paths = new string[count];
                var lengths = new ulong[count];
                var digests = new byte[count][];
                for (int i = 0; i < count; i++)
                {
                    var e = entries[(int)start + i];
                    paths[i] = e.Path; lengths[i] = (ulong)e.Length; digests[i] = e.Digest;
                }
                var msg = new ContentOfferMsg
                {
                    SetHash = _hostSetHash, Empty = empty,
                    TotalFiles = (uint)entries.Count, TotalBytes = totalBytes,
                    StartIndex = start, Paths = paths, Lengths = lengths, Digests = digests,
                };
                session.SendContent(peer, msg);
                if (entries.Count == 0) break;
            }
            Plugin.Log.LogInfo($"[Content] offering set {ContentHash.ToHex(_hostSetHash).Substring(0, 12)} " +
                $"({entries.Count} file(s), {totalBytes / 1024} KB) to peer {peer}");
        }

        internal static void HandleNeed(NetSession session, ulong peer, ContentNeedMsg msg)
        {
            if (!_hostReady) return;
            if (!Streams.TryGetValue(peer, out var s)) Streams[peer] = s = new Stream();
            int queued = 0;
            for (int i = 0; i < msg.Digests.Length; i++)
            {
                s.Wanted.Enqueue(msg.Digests[i]);
                queued++;
            }
            Plugin.Log.LogInfo($"[Content] peer {peer} needs {queued} blob(s)");
        }

        // ---- host: streaming -------------------------------------------------------------------

        internal static void Tick(NetSession session)
        {
            DrainResults(session);
            if (session == null || Streams.Count == 0) return;

            double perPeer = Math.Max(16, NetConfig.ContentRateKBps.Value) * 1024.0;
            float dt = Mathf.Clamp(Time.unscaledDeltaTime, 0f, 0.1f);
            var done = new List<ulong>();

            foreach (var kv in Streams)
            {
                var peer = kv.Key; var s = kv.Value;
                s.Allowance = Math.Min(s.Allowance + perPeer * dt, perPeer);

                while (s.Allowance >= ChunkBytes || (s.Current != null && s.Allowance > 0))
                {
                    if (s.Current == null)
                    {
                        if (s.Wanted.Count == 0) { done.Add(peer); break; }
                        s.Current = s.Wanted.Dequeue();
                        s.Offset = 0;
                        try { s.Open = (FileStream)ContentStore.OpenBlob(s.Current); }
                        catch (Exception e)
                        {
                            Plugin.Log.LogWarning($"[Content] cannot serve a blob: {e.Message}");
                            s.Current = null; continue;
                        }
                    }

                    var buffer = new byte[ChunkBytes];
                    int read = s.Open.Read(buffer, 0, ChunkBytes);
                    bool last = read < ChunkBytes || s.Open.Position >= s.Open.Length;
                    var data = new byte[read];
                    Buffer.BlockCopy(buffer, 0, data, 0, read);

                    var msg = new ContentChunkMsg
                    { Digest = s.Current, Offset = (ulong)s.Offset, Last = last, Data = data };
                    // Backpressure, exactly as the terrain streamer does it: a refused send is not
                    // an error, it is "try again next frame".
                    if (!session.SendContent(peer, msg, bulk: true)) break;

                    s.Offset += read;
                    s.Allowance -= read;
                    if (last)
                    {
                        try { s.Open.Dispose(); } catch { }
                        s.Open = null; s.Current = null;
                    }
                }
            }
            foreach (var peer in done)
                if (Streams.TryGetValue(peer, out var s) && s.Current == null && s.Wanted.Count == 0)
                { try { s.Open?.Dispose(); } catch { } Streams.Remove(peer); }
        }

        // ---- client: receiving -------------------------------------------------------------------

        internal static void HandleOffer(NetSession session, ContentOfferMsg msg)
        {
            if (msg.StartIndex == 0)
            {
                _incoming = new List<ContentHash.Entry>();
                _incomingTotal = msg.TotalFiles;
                _incomingHash = msg.SetHash;
                HostSetHash = msg.SetHash;
            }
            if (_incoming == null) return;
            for (int i = 0; i < msg.Paths.Length; i++)
                _incoming.Add(new ContentHash.Entry
                { Path = msg.Paths[i], Length = (long)msg.Lengths[i], Digest = msg.Digests[i] });

            if (msg.Empty || _incomingTotal == 0)
            {
                LocalState = ContentState.Satisfied;
                LocalSetHash = msg.SetHash;
                SendDone(session, msg.SetHash, true, "");
                return;
            }
            if (_incoming.Count < _incomingTotal) return;   // more chunks coming

            // Recompute the hash from what arrived. If the two implementations disagree, that is
            // caught here — before a byte of content moves — rather than after a download.
            var computed = ContentHash.SetDigest(_incoming);
            if (!ContentHash.SameDigest(computed, _incomingHash))
            {
                Fail(session, _incomingHash, "the host's file list does not hash to the set it named " +
                    $"(theirs {ContentHash.ToHex(_incomingHash).Substring(0, 12)}, " +
                    $"ours {ContentHash.ToHex(computed).Substring(0, 12)})");
                return;
            }

            var problems = ContentHash.Validate(_incoming);
            if (problems.Count > 0)
            {
                Fail(session, _incomingHash, $"the host's content is not installable here: {problems[0]}");
                return;
            }

            ContentStore.EnsureDirectories();
            StartWorker();

            // Already have this exact set? Then a rejoin costs one file-exists check and nothing
            // else. That IS the "rejoin re-downloads nothing" requirement.
            if (ContentStore.HasSet(_incomingHash))
            {
                LocalState = ContentState.Installing;
                Jobs.Enqueue(new InstallJob { SetHash = _incomingHash, Entries = _incoming });
                Wake.Set();
                Plugin.Log.LogInfo($"[Content] cache hit for set {ContentHash.ToHex(_incomingHash).Substring(0, 12)}");
                return;
            }

            var wanted = new List<byte[]>();
            var haves = new List<ulong>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in _incoming)
            {
                var hex = ContentHash.ToHex(e.Digest);
                if (!seen.Add(hex)) continue;              // dedup: one blob, one download
                if (ContentStore.HasBlob(e.Digest)) continue;
                wanted.Add(e.Digest);
                haves.Add((ulong)ContentStore.PartialLength(e.Digest));
            }

            if (wanted.Count == 0)
            {
                LocalState = ContentState.Installing;
                Jobs.Enqueue(new InstallJob { SetHash = _incomingHash, Entries = _incoming });
                Wake.Set();
                return;
            }

            LocalState = ContentState.Downloading;
            _expected = wanted.Count;
            _received = 0;
            Plugin.Log.LogInfo($"[Content] need {wanted.Count}/{_incoming.Count} blob(s) from the host");
            UI.Toast.Show("DOWNLOADING CUSTOM CONTENT", 4f);

            for (int start = 0; start < wanted.Count; start += NeedChunkDigests)
            {
                int count = Math.Min(NeedChunkDigests, wanted.Count - start);
                var d = new byte[count][]; var h = new ulong[count];
                for (int i = 0; i < count; i++) { d[i] = wanted[start + i]; h[i] = haves[start + i]; }
                session.SendContent(0, new ContentNeedMsg { SetHash = _incomingHash, Digests = d, HaveBytes = h },
                    toHost: true);
            }
        }

        private static int _expected, _received;

        internal static void HandleChunk(NetSession session, ContentChunkMsg msg)
        {
            if (LocalState != ContentState.Downloading) return;
            if (!ContentStore.AppendChunk(msg.Digest, (long)msg.Offset, msg.Data, msg.Data.Length, out var err))
            {
                Plugin.Log.LogWarning($"[Content] chunk rejected: {err}");
                return;
            }
            if (!msg.Last) return;
            Jobs.Enqueue(new CommitJob { Digest = msg.Digest });
            Wake.Set();
        }

        // ---- results ---------------------------------------------------------------------------

        private static void DrainResults(NetSession session)
        {
            // Bounded like ReceivePump's drain: take the backlog present at entry, never chase a
            // live producer. "Drain until empty" against a running worker was a 55-second freeze
            // once already on this project.
            int budget = Results.Count;
            while (budget-- > 0 && Results.TryDequeue(out var result))
            {
                switch (result)
                {
                    case Scanned s:
                        _hostEntries = s.Entries;
                        _hostSetHash = s.Hash;
                        _hostReady = true;
                        LocalSetHash = s.Hash;
                        LocalState = ContentState.Satisfied;
                        if (s.Problems.Count > 0)
                        {
                            Plugin.Log.LogWarning($"[Content] {s.Problems.Count} file(s) cannot be published:");
                            for (int i = 0; i < Math.Min(6, s.Problems.Count); i++)
                                Plugin.Log.LogWarning($"[Content]   {s.Problems[i]}");
                        }
                        Plugin.Log.LogInfo($"[Content] serving {s.Entries.Count} file(s), " +
                            $"set {ContentHash.ToHex(s.Hash).Substring(0, 12)}");
                        break;

                    case Committed c:
                        if (!c.Ok)
                        {
                            Plugin.Log.LogWarning($"[Content] blob failed verification ({c.Error}) — re-requesting");
                            break;
                        }
                        _received++;
                        if (_expected > 0 && _received >= _expected && _incoming != null)
                        {
                            LocalState = ContentState.Installing;
                            Jobs.Enqueue(new InstallJob { SetHash = _incomingHash, Entries = _incoming });
                            Wake.Set();
                        }
                        break;

                    case Installed i:
                        if (i.Ok)
                        {
                            LocalState = ContentState.Satisfied;
                            LocalSetHash = i.SetHash;
                            ActiveContentPath = i.Path;
                            Plugin.Log.LogInfo($"[Content] set {ContentHash.ToHex(i.SetHash).Substring(0, 12)} " +
                                $"installed at {i.Path}");
                            SendDone(session, i.SetHash, true, "");
                            Jobs.Enqueue(new SweepJob
                            {
                                Budget = (long)NetConfig.ContentCacheMaxMB.Value * 1024 * 1024,
                                Keep = i.SetHash,
                            });
                            Wake.Set();
                        }
                        else Fail(session, i.SetHash, i.Error);
                        break;
                }
            }
        }

        private static void Fail(NetSession session, byte[] setHash, string reason)
        {
            LocalState = ContentState.Failed;
            LocalFailure = reason;
            Plugin.Log.LogError($"[Content] failed: {reason}");
            UI.Toast.Show("CONTENT SYNC FAILED", 6f);
            SendDone(session, setHash, false, reason);
        }

        private static void SendDone(NetSession session, byte[] setHash, bool ok, string reason)
        {
            if (session == null || session.IsHost) return;
            session.SendContent(0, new ContentDoneMsg { SetHash = setHash, Ok = ok, Reason = reason },
                toHost: true);
        }

        internal static void HandleDone(byte slot, ContentDoneMsg msg)
        {
            PeerState[slot] = msg.Ok ? ContentState.Satisfied : ContentState.Failed;
            PeerPercent[slot] = msg.Ok ? (byte)100 : (byte)0;
            if (msg.Ok) Plugin.Log.LogInfo($"[Content] P{slot + 1} has the content");
            else Plugin.Log.LogWarning($"[Content] P{slot + 1} could not install the content: {msg.Reason}");
        }

        internal static void HandleStatus(byte slot, ContentStatusMsg msg)
        {
            PeerState[slot] = (ContentState)msg.State;
            PeerPercent[slot] = msg.Percent;
        }
    }
}
