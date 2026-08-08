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

        /// <summary>Bytes fetched / bytes this machine actually has to fetch. The denominator is
        /// what we NEED, not what the set weighs: a player who already has nine of ten files
        /// should see a bar that fills, not one that stops at 10%.</summary>
        internal static long BytesDone => _gotBytes;
        internal static long BytesNeeded => _wantedBytes;
        /// <summary>The whole set's size, for "of a 7.9 MB pack" context.</summary>
        internal static long BytesInSet => _incomingBytes;

        internal static byte LocalPercent
        {
            get
            {
                if (LocalState == ContentState.Satisfied) return 100;
                if (_wantedBytes <= 0) return LocalState == ContentState.Installing ? (byte)99 : (byte)0;
                // Capped at 99 while installing: 100 belongs to "you can play", and a bar that
                // reads 100% while the game is still busy is the other classic progress-bar lie.
                var pct = (int)(_gotBytes * 100 / _wantedBytes);
                return (byte)Mathf.Clamp(pct, 0, 99);
            }
        }

        /// <summary>True when this machine is holding the player up. The UI shows a modal for
        /// exactly this condition and nothing else.</summary>
        internal static bool Busy =>
            LocalState == ContentState.Downloading || LocalState == ContentState.Installing;

        private static List<ContentHash.Entry> _hostEntries;      // host: what we serve
        private static byte[] _hostSetHash;
        private static bool _hostReady;
        // The folder the host serves, kept so WeaponForge can be pointed at the same set the
        // clients are being given. Without this the host would keep its OWN weapons while
        // everyone else switched to the served ones -- and the digest would refuse the run.
        private static string _hostRoot;

        private static readonly Dictionary<byte, ContentState> PeerState = new Dictionary<byte, ContentState>();
        private static readonly Dictionary<byte, byte> PeerPercent = new Dictionary<byte, byte>();

        // client: assembling an offer
        private static List<ContentHash.Entry> _incoming;
        private static uint _incomingTotal;
        private static byte[] _incomingHash;
        // Progress is measured in BYTES, not blobs. A ten-blob set where one file is 2 MB and the
        // rest are 2 KB would make a blob-counting bar sit at 90% for the entire download and then
        // jump -- the single most common way a progress bar lies.
        private static long _incomingBytes;     // what the whole set weighs
        private static long _wantedBytes;       // what WE still had to fetch (cache hits excluded)
        private static long _gotBytes;          // fetched so far this session
        private static float _nextStatusAt;

        // host: per-peer send queues
        /// <summary>A blob a peer asked for, and where it wants the stream to START. The offset
        /// was previously dropped on the floor here, which meant resume never worked: the client
        /// asked to continue from N, the host sent from 0, and the client rejected every chunk.</summary>
        private struct Want { internal byte[] Digest; internal long From; }

        private sealed class Stream
        {
            internal readonly Queue<Want> Wanted = new Queue<Want>();
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
                            var scanned = ContentStore.ScanDirectory(s.Root, s.MaxFile, out var skipped);
                            // Drop what cannot be published rather than offering it: a set with
                            // one bad path is refused by every client IN FULL, so a single stray
                            // file in ContentRoot would otherwise take the whole session down.
                            var entries = ContentHash.Publishable(scanned, out var problems);
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
            _hostRoot = null;
            // The progress counters, which used to survive a session and lie about the next one.
            // LocalPercent is _gotBytes/_wantedBytes clamped to 99, so a second join in the SAME
            // process opened the modal already reading "99%" with the PREVIOUS transfer's byte
            // totals under it -- before a single byte of this session had moved. A cache hit then
            // finishes in milliseconds and the modal vanishes, which is why it reads as a flicker
            // for most people and as "stuck at 99%" for anyone whose install is a few frames
            // slower. Stale _expected/_received are worse than cosmetic: a late Committed result
            // from the old session can push _received past _expected and enqueue an InstallJob for
            // content this session never asked for.
            _expected = 0;
            _received = 0;
            _wantedBytes = 0;
            _gotBytes = 0;
            // Whatever the session swapped in, the player gets their own content back. A no-op
            // when nothing was ever swapped, which is the common case.
            ForgeContentSwap.Restore();
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
            _hostRoot = Path.IsPathRooted(root) ? root : Path.Combine(ModFolder.Dir, root);
            Jobs.Enqueue(new ScanJob
            {
                Root = _hostRoot,
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
            int queued = 0, resumed = 0;
            for (int i = 0; i < msg.Digests.Length; i++)
            {
                long from = i < msg.HaveBytes.Length ? (long)msg.HaveBytes[i] : 0;
                if (from < 0) from = 0;
                if (from > 0) resumed++;
                s.Wanted.Enqueue(new Want { Digest = msg.Digests[i], From = from });
                queued++;
            }
            Plugin.Log.LogInfo($"[Content] peer {peer} needs {queued} blob(s)" +
                (resumed > 0 ? $" ({resumed} resuming)" : ""));
        }

        // ---- host: streaming -------------------------------------------------------------------

        internal static void Tick(NetSession session)
        {
            DrainResults(session);
            // Before the Streams early-out: a CLIENT has no streams (those are the host's send
            // queues), and the client is the only machine with progress worth reporting.
            ReportProgress(session);
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
                        var want = s.Wanted.Dequeue();
                        s.Current = want.Digest;
                        s.Offset = 0;
                        try
                        {
                            s.Open = (FileStream)ContentStore.OpenBlob(s.Current);
                            // Honour the resume point. Clamped to the real length: a peer claiming
                            // to hold more than exists gets the whole blob rather than an
                            // exception, and the digest check at commit still has the last word.
                            if (want.From > 0)
                            {
                                s.Offset = Math.Min(want.From, s.Open.Length);
                                s.Open.Seek(s.Offset, SeekOrigin.Begin);
                            }
                        }
                        catch (Exception e)
                        {
                            Plugin.Log.LogWarning($"[Content] cannot serve a blob: {e.Message}");
                            s.Current = null;
                            try { s.Open?.Dispose(); } catch { }
                            s.Open = null;
                            continue;
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
                _incomingBytes = (long)msg.TotalBytes;
                _wantedBytes = 0;
                _gotBytes = 0;
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
                // Nothing to transfer, so the byte counters must read empty rather than carry
                // whatever the last offer left behind -- the modal derives its percentage from
                // them and would otherwise show a number describing a different download.
                _expected = 0;
                _received = 0;
                _wantedBytes = 0;
                _gotBytes = 0;
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
                _expected = 0;
                _received = 0;
                _wantedBytes = 0;
                _gotBytes = 0;
                LocalState = ContentState.Installing;
                Jobs.Enqueue(new InstallJob { SetHash = _incomingHash, Entries = _incoming });
                Wake.Set();
                return;
            }

            LocalState = ContentState.Downloading;
            _expected = wanted.Count;
            _received = 0;
            // Resume means part of a blob is already on disk, so the work remaining is the blob
            // sizes MINUS what each .part already holds -- otherwise a resumed download shows a
            // bar that starts at zero and finishes early.
            _wantedBytes = 0;
            for (int i = 0; i < wanted.Count; i++)
            {
                long len = LengthOf(wanted[i]);
                _wantedBytes += Math.Max(0, len - (long)haves[i]);
            }
            if (_wantedBytes <= 0) _wantedBytes = 1;   // never divide by zero
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
            _gotBytes += msg.Data.Length;
            if (!msg.Last) return;
            Jobs.Enqueue(new CommitJob { Digest = msg.Digest });
            Wake.Set();
        }

        private static long LengthOf(byte[] digest)
        {
            if (_incoming == null) return 0;
            foreach (var e in _incoming)
                if (ContentHash.SameDigest(e.Digest, digest)) return e.Length;
            return 0;
        }

        /// <summary>
        /// Tell the host how far along we are, so the lobby can show a per-player figure instead
        /// of a binary "not ready". Rate-limited: this is cosmetic traffic and must never compete
        /// with the transfer it is describing.
        /// </summary>
        private static void ReportProgress(NetSession session)
        {
            if (session == null || session.IsHost || !Busy) return;
            if (Time.unscaledTime < _nextStatusAt) return;
            _nextStatusAt = Time.unscaledTime + 0.5f;
            session.SendContent(0, new ContentStatusMsg
            {
                State = (byte)LocalState,
                Percent = LocalPercent,
                BytesDone = (ulong)Math.Max(0, _gotBytes),
                BytesTotal = (ulong)Math.Max(0, _wantedBytes),
            }, toHost: true);
        }

        /// <summary>
        /// The player pressed CANCEL. Stop wanting the host's content and say so — the host marks
        /// the slot failed, which keeps the go-live gate shut rather than letting a run start
        /// without them. The caller then leaves the session; this method deliberately does NOT,
        /// because "abandon the download" and "leave the lobby" are separate decisions and only
        /// the UI knows the player made both.
        /// </summary>
        internal static void CancelLocal(NetSession session)
        {
            if (!Busy) return;
            Plugin.Log.LogInfo($"[Content] cancelled by the player at {LocalPercent}%");
            _incoming = null;
            _expected = 0;
            _received = 0;
            _wantedBytes = 0;
            _gotBytes = 0;
            LocalState = ContentState.Failed;
            LocalFailure = "cancelled";
            // Partial blobs stay on disk on purpose: they are .part files keyed by digest, so
            // rejoining later resumes from where this left off instead of starting again.
            SendDone(session, _incomingHash ?? ContentHash.EmptySet(), false, "cancelled by the player");
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
                            Plugin.Log.LogWarning($"[Content] {s.Problems.Count} file(s) skipped, not served:");
                            for (int i = 0; i < Math.Min(6, s.Problems.Count); i++)
                                Plugin.Log.LogWarning($"[Content]   {s.Problems[i]}");
                        }
                        Plugin.Log.LogInfo($"[Content] serving {s.Entries.Count} file(s), " +
                            $"set {ContentHash.ToHex(s.Hash).Substring(0, 12)}");
                        // The host runs the set it serves. Only meaningful when ContentRoot is
                        // NOT already WeaponForge's own folder -- but that is the case worth
                        // supporting, because it lets a host curate what it serves without
                        // disturbing what it plays with solo.
                        if (s.Entries.Count > 0) ForgeContentSwap.SwapTo(_hostRoot);
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
                            // Apply it before telling the host we have it: Done is what releases
                            // the go-live gate, and the gate must not open on content that is on
                            // disk but not loaded.
                            ForgeContentSwap.SwapTo(i.Path);
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

        /// <summary>
        /// Returns true when the lobby should be rebroadcast. Not on every status: clients report
        /// twice a second each, and re-sending the whole roster to everyone six times a second so
        /// a number can tick by one is a poor trade. A 5% step is well under one bar-width and
        /// the state change itself is always sent.
        /// </summary>
        internal static bool HandleStatus(byte slot, ContentStatusMsg msg)
        {
            var state = (ContentState)msg.State;
            bool changed = !PeerState.TryGetValue(slot, out var was) || was != state;
            if (!changed && PeerPercent.TryGetValue(slot, out var pct))
                changed = msg.Percent >= pct + 5 || msg.Percent < pct;   // < = a restart or resume
            else changed = true;
            PeerState[slot] = state;
            PeerPercent[slot] = msg.Percent;
            return changed;
        }
    }
}
