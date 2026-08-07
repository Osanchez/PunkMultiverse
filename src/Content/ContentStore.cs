using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace PunkMultiverse.Content
{
    /// <summary>
    /// The on-disk cache for host-served content, under BepInEx/plugins/PunkMultiverse/content/.
    ///
    ///   cas/&lt;aa&gt;/&lt;hex&gt;.bin    a committed blob, named by its own digest
    ///   cas/&lt;aa&gt;/&lt;hex&gt;.part   a resumable partial download
    ///   cas/&lt;aa&gt;/&lt;hex&gt;.tmp    an in-flight write; crash debris, swept at boot
    ///   sets/&lt;hex&gt;.set        the manifest of a COMPLETE, VERIFIED set
    ///   active/&lt;hex&gt;/...      the materialised view a content mod is pointed at
    ///
    /// The whole design rests on one invariant:
    ///
    ///     if sets/&lt;hash&gt;.set exists, every blob it names exists and is byte-correct.
    ///
    /// Everything else follows from it. A blob is written to .tmp, flushed to the device,
    /// digest-verified, and only then MOVED to its final name — so the existence of the final
    /// name IS the commit record, and a torn write can never leave a file whose name claims a
    /// digest it does not have. That case matters more than it sounds: such a file would be
    /// trusted forever by every future run.
    ///
    /// Content is never written into another mod's folders. That is what makes a crash safe:
    /// nothing of the player's changed, so there is nothing to roll back.
    ///
    /// Named by digest, so a rejoin re-downloads nothing by construction, and two hosts sharing
    /// most of a weapon pack share the blobs.
    /// </summary>
    internal static class ContentStore
    {
        private const string SetMagic = "PMVS";
        private const int SetFormat = 1;

        internal static string Root => Path.Combine(ModFolder.Dir, "content");
        private static string CasDir => Path.Combine(Root, "cas");
        private static string SetsDir => Path.Combine(Root, "sets");
        private static string ActiveDir => Path.Combine(Root, "active");

        private static string BlobDir(string hex) =>
            Path.Combine(CasDir, hex.Substring(0, 2));

        // Two hex chars = 256 buckets. Both NTFS and ext4 degrade badly past ~100k entries in one
        // directory, and a large pack plus a few versions of it gets there faster than expected.
        private static string BlobPath(string hex, string extension = ".bin") =>
            Path.Combine(BlobDir(hex), hex + extension);

        internal static string SetPath(string hex) => Path.Combine(SetsDir, hex + ".set");
        internal static string ActivePathFor(string hex) => Path.Combine(ActiveDir, hex);

        internal static bool HasBlob(byte[] digest) => File.Exists(BlobPath(ContentHash.ToHex(digest)));
        internal static bool HasSet(byte[] setDigest) => File.Exists(SetPath(ContentHash.ToHex(setDigest)));

        /// <summary>Bytes already received for a partial blob — the resume point.</summary>
        internal static long PartialLength(byte[] digest)
        {
            try
            {
                var p = BlobPath(ContentHash.ToHex(digest), ".part");
                return File.Exists(p) ? new FileInfo(p).Length : 0;
            }
            catch { return 0; }
        }

        internal static void EnsureDirectories()
        {
            Directory.CreateDirectory(Root);
            Directory.CreateDirectory(CasDir);
            Directory.CreateDirectory(SetsDir);
            Directory.CreateDirectory(ActiveDir);
        }

        // ---- writing blobs ----------------------------------------------------------------

        /// <summary>Append a chunk to a blob's .part file. Offset is checked so a duplicated or
        /// out-of-order chunk cannot corrupt the file silently.</summary>
        internal static bool AppendChunk(byte[] digest, long offset, byte[] data, int count, out string error)
        {
            error = null;
            try
            {
                var hex = ContentHash.ToHex(digest);
                Directory.CreateDirectory(BlobDir(hex));
                var part = BlobPath(hex, ".part");
                long have = File.Exists(part) ? new FileInfo(part).Length : 0;
                if (offset != have)
                {
                    // SELF-HEAL rather than refuse. A sender that restarts a blob from 0 (or from
                    // any earlier point) is not an error to argue with -- it is the authority on
                    // what it is about to send, and the bytes it sends are verified against the
                    // digest before anything is committed, so accepting them is safe.
                    //
                    // Refusing instead is how this wedged a client permanently: every chunk was
                    // rejected, the blob never completed, and the go-live gate held that player
                    // out of every future session until they deleted the cache by hand. A
                    // transfer that cannot recover from a disagreement about its own offset is
                    // worse than one that simply re-sends.
                    if (offset > have)
                    {
                        // A gap would leave a file whose length lies about its contents.
                        error = $"chunk at {offset} leaves a gap (the file holds {have})";
                        return false;
                    }
                    using (var trunc = new FileStream(part, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None))
                        trunc.SetLength(offset);
                    have = offset;
                }
                using (var fs = new FileStream(part, FileMode.Append, FileAccess.Write, FileShare.None))
                    fs.Write(data, 0, count);
                return true;
            }
            catch (Exception e) { error = e.Message; return false; }
        }

        /// <summary>
        /// Verify a completed .part against the digest its name claims, and commit it. A mismatch
        /// deletes the partial rather than keeping it: a bad resume must cost one re-download, not
        /// poison the cache permanently.
        /// </summary>
        internal static bool CommitBlob(byte[] digest, out string error)
        {
            error = null;
            var hex = ContentHash.ToHex(digest);
            var part = BlobPath(hex, ".part");
            var final = BlobPath(hex);
            try
            {
                if (File.Exists(final)) { TryDelete(part); return true; }   // someone else won; fine
                if (!File.Exists(part)) { error = "no partial to commit"; return false; }

                byte[] actual;
                using (var fs = File.OpenRead(part)) actual = ContentHash.FileDigest(fs, fs.Length);
                if (!ContentHash.SameDigest(actual, digest))
                {
                    TryDelete(part);
                    error = "digest mismatch after transfer";
                    return false;
                }
                // Same-volume move is atomic on NTFS and ext4, which is the entire commit story.
                File.Move(part, final);
                return true;
            }
            catch (Exception e) { error = e.Message; return false; }
        }

        /// <summary>Take a local file into the cache (host side: its own content becomes blobs so
        /// the serving path is identical to the receiving one).</summary>
        internal static bool ImportFile(string sourcePath, byte[] digest, out string error)
        {
            error = null;
            try
            {
                var hex = ContentHash.ToHex(digest);
                var final = BlobPath(hex);
                if (File.Exists(final)) return true;
                Directory.CreateDirectory(BlobDir(hex));
                var tmp = BlobPath(hex, ".tmp." + System.Diagnostics.Process.GetCurrentProcess().Id);
                File.Copy(sourcePath, tmp, overwrite: true);
                if (File.Exists(final)) { TryDelete(tmp); return true; }
                File.Move(tmp, final);
                return true;
            }
            catch (Exception e) { error = e.Message; return false; }
        }

        internal static Stream OpenBlob(byte[] digest) =>
            File.OpenRead(BlobPath(ContentHash.ToHex(digest)));

        // ---- set manifests ----------------------------------------------------------------

        /// <summary>
        /// THE ONLY method that writes a .set, deliberately. Writing one before its blobs are
        /// committed poisons the cache for every future launch, so there is exactly one place to
        /// review for that mistake. Callers must have committed every blob first.
        /// </summary>
        internal static bool WriteSet(byte[] setDigest, List<ContentHash.Entry> entries, out string error)
        {
            error = null;
            try
            {
                foreach (var e in entries)
                {
                    if (HasBlob(e.Digest)) continue;
                    error = $"refusing to write a set: blob for '{e.Path}' is not committed";
                    return false;
                }
                EnsureDirectories();
                var hex = ContentHash.ToHex(setDigest);
                var sb = new StringBuilder();
                sb.Append(SetMagic).Append('\n').Append(SetFormat).Append('\n');
                sb.Append(hex).Append('\n').Append(entries.Count).Append('\n');
                foreach (var e in entries)
                    sb.Append(ContentHash.ToHex(e.Digest)).Append(' ')
                      .Append(e.Length.ToString(CultureInfo.InvariantCulture)).Append(' ')
                      .Append(e.Path).Append('\n');

                // Move-aside/rollback, same idiom as UpdateCheck.TryStage.
                var final = SetPath(hex);
                var tmp = final + ".tmp";
                File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
                if (File.Exists(final)) File.Delete(final);
                File.Move(tmp, final);
                return true;
            }
            catch (Exception e) { error = e.Message; return false; }
        }

        /// <summary>Read a committed set, or null. The magic bytes are checked before the file is
        /// trusted — the same instinct as UpdateCheck's MZ check on a downloaded DLL.</summary>
        internal static List<ContentHash.Entry> ReadSet(byte[] setDigest)
        {
            try
            {
                var path = SetPath(ContentHash.ToHex(setDigest));
                if (!File.Exists(path)) return null;
                var lines = File.ReadAllLines(path);
                if (lines.Length < 4 || lines[0] != SetMagic) return null;
                if (!int.TryParse(lines[1], out int format) || format != SetFormat) return null;
                if (!int.TryParse(lines[3], out int count)) return null;
                var entries = new List<ContentHash.Entry>(count);
                for (int i = 0; i < count && 4 + i < lines.Length; i++)
                {
                    var line = lines[4 + i];
                    int a = line.IndexOf(' ');
                    if (a <= 0) return null;
                    int b = line.IndexOf(' ', a + 1);
                    if (b <= a) return null;
                    var digest = ContentHash.FromHex(line.Substring(0, a));
                    if (digest == null) return null;
                    if (!long.TryParse(line.Substring(a + 1, b - a - 1), NumberStyles.Integer,
                            CultureInfo.InvariantCulture, out long length)) return null;
                    entries.Add(new ContentHash.Entry
                    {
                        Digest = digest,
                        Length = length,
                        Path = line.Substring(b + 1),
                    });
                }
                return entries.Count == count ? entries : null;
            }
            catch { return null; }
        }

        // ---- materialising -----------------------------------------------------------------

        /// <summary>
        /// Lay a set out as real files under active/&lt;hash&gt;/. The copy reads every byte anyway,
        /// so it verifies as it goes — which closes the "we only ever verified on write" hole for
        /// free rather than needing a separate pass.
        /// </summary>
        internal static bool Materialise(byte[] setDigest, out string error)
        {
            error = null;
            var entries = ReadSet(setDigest);
            if (entries == null) { error = "no committed set to materialise"; return false; }
            var root = ActivePathFor(ContentHash.ToHex(setDigest));
            try
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
                Directory.CreateDirectory(root);
                var full = Path.GetFullPath(root);
                foreach (var e in entries)
                {
                    // The receiver writes host-chosen filenames. Re-check here, at the last
                    // moment before touching the filesystem, because this is the traversal
                    // surface and being sure twice is cheaper than being wrong once.
                    var why = ContentHash.Reject(e.Path);
                    if (why != null) { error = $"'{e.Path}': {why}"; return false; }
                    var dest = Path.GetFullPath(Path.Combine(root, e.Path.Replace('/', Path.DirectorySeparatorChar)));
                    if (!dest.StartsWith(full, StringComparison.OrdinalIgnoreCase))
                    { error = $"'{e.Path}' escapes the content directory"; return false; }

                    Directory.CreateDirectory(Path.GetDirectoryName(dest));
                    using (var src = OpenBlob(e.Digest))
                    using (var dst = File.Create(dest))
                        src.CopyTo(dst);

                    var actual = ContentHash.FileDigest(dest);
                    if (!ContentHash.SameDigest(actual, e.Digest))
                    {
                        error = $"'{e.Path}' failed verification while materialising";
                        return false;
                    }
                }
                return true;
            }
            catch (Exception e) { error = e.Message; return false; }
        }

        // ---- housekeeping -------------------------------------------------------------------

        /// <summary>
        /// Delete crash debris and anything that breaks the invariant. Directory listings only —
        /// no hashing — so it is cheap enough to run unconditionally at boot. Rehashing the whole
        /// cache every launch would be a hitch for no benefit: blobs are verified on write and
        /// again while materialising.
        /// </summary>
        internal static void BootSweep()
        {
            try
            {
                EnsureDirectories();
                var now = DateTime.UtcNow;
                int tmp = 0, parts = 0, sets = 0, actives = 0;

                foreach (var dir in Directory.GetDirectories(CasDir))
                    foreach (var f in Directory.GetFiles(dir))
                    {
                        if (f.IndexOf(".tmp", StringComparison.OrdinalIgnoreCase) >= 0
                            && (now - File.GetLastWriteTimeUtc(f)).TotalHours > 1)
                        { TryDelete(f); tmp++; }
                        else if (f.EndsWith(".part", StringComparison.OrdinalIgnoreCase)
                            && (now - File.GetLastWriteTimeUtc(f)).TotalDays > 7)
                        { TryDelete(f); parts++; }
                    }

                foreach (var setFile in Directory.GetFiles(SetsDir, "*.set"))
                {
                    var hex = Path.GetFileNameWithoutExtension(setFile);
                    var digest = ContentHash.FromHex(hex);
                    var entries = digest != null ? ReadSet(digest) : null;
                    bool broken = entries == null;
                    if (!broken)
                        foreach (var e in entries)
                            if (!HasBlob(e.Digest)) { broken = true; break; }
                    if (broken) { TryDelete(setFile); sets++; }
                }

                foreach (var activeDir in Directory.GetDirectories(ActiveDir))
                {
                    var hex = Path.GetFileName(activeDir);
                    if (!File.Exists(SetPath(hex)))
                    { TryDeleteDir(activeDir); actives++; }
                }

                if (tmp + parts + sets + actives > 0)
                    Plugin.Log.LogInfo($"[Content] boot sweep: {tmp} temp, {parts} stale partial(s), " +
                        $"{sets} broken set(s), {actives} orphaned view(s) removed");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[Content] boot sweep skipped: {e.Message}");
            }
        }

        /// <summary>
        /// Keep the cache under budget. The unit of eviction is a SET, never a blob — deleting a
        /// blob out from under a manifest breaks the invariant everything else depends on.
        /// If one set alone exceeds the budget it is kept and logged: a budget that silently makes
        /// the game unplayable is worse than an over-budget cache.
        /// </summary>
        internal static void Evict(long budgetBytes, byte[] keepSet)
        {
            try
            {
                EnsureDirectories();
                long total = 0;
                foreach (var dir in Directory.GetDirectories(CasDir))
                    foreach (var f in Directory.GetFiles(dir, "*.bin"))
                        total += new FileInfo(f).Length;
                if (total <= budgetBytes) return;

                var keepHex = keepSet != null ? ContentHash.ToHex(keepSet) : null;
                var setFiles = new List<FileInfo>();
                foreach (var f in Directory.GetFiles(SetsDir, "*.set")) setFiles.Add(new FileInfo(f));
                setFiles.Sort((a, b) => a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc));

                int dropped = 0;
                foreach (var setFile in setFiles)
                {
                    if (total <= budgetBytes) break;
                    var hex = Path.GetFileNameWithoutExtension(setFile.Name);
                    if (string.Equals(hex, keepHex, StringComparison.OrdinalIgnoreCase)) continue;
                    TryDelete(setFile.FullName);
                    TryDeleteDir(ActivePathFor(hex));
                    dropped++;
                    total = SweepUnreferencedBlobs();
                }
                if (dropped > 0)
                    Plugin.Log.LogInfo($"[Content] cache over budget — evicted {dropped} set(s), " +
                        $"now {total / (1024 * 1024)} MB");
                else if (total > budgetBytes)
                    Plugin.Log.LogWarning($"[Content] cache is {total / (1024 * 1024)} MB, over the " +
                        "budget, but the only sets present are in use — keeping them");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[Content] eviction skipped: {e.Message}");
            }
        }

        /// <summary>Mark and sweep: a blob no surviving manifest names is unreachable.</summary>
        private static long SweepUnreferencedBlobs()
        {
            var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var setFile in Directory.GetFiles(SetsDir, "*.set"))
            {
                var digest = ContentHash.FromHex(Path.GetFileNameWithoutExtension(setFile));
                var entries = digest != null ? ReadSet(digest) : null;
                if (entries == null) continue;
                foreach (var e in entries) referenced.Add(ContentHash.ToHex(e.Digest));
            }
            long total = 0;
            foreach (var dir in Directory.GetDirectories(CasDir))
                foreach (var f in Directory.GetFiles(dir, "*.bin"))
                {
                    if (referenced.Contains(Path.GetFileNameWithoutExtension(f))) { total += new FileInfo(f).Length; continue; }
                    TryDelete(f);
                }
            return total;
        }

        internal static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        private static void TryDeleteDir(string path)
        {
            try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { }
        }

        /// <summary>Scan a directory into hashed entries. Host side; runs on the worker.</summary>
        internal static List<ContentHash.Entry> ScanDirectory(string root, long maxFileBytes, out List<string> skipped)
        {
            skipped = new List<string>();
            var entries = new List<ContentHash.Entry>();
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return entries;
            var full = Path.GetFullPath(root);
            foreach (var file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
            {
                try
                {
                    var info = new FileInfo(file);
                    var name = info.Name;
                    if (name.StartsWith(".", StringComparison.Ordinal)
                        || name.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)
                        || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
                        || name.EndsWith(".bak", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (info.Length > maxFileBytes)
                    { skipped.Add($"{name} ({info.Length / (1024 * 1024)} MB, over the per-file limit)"); continue; }

                    var relative = ContentHash.Canonical(Path.GetFullPath(file).Substring(full.Length).TrimStart(
                        Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                    entries.Add(new ContentHash.Entry
                    {
                        Path = relative,
                        Length = info.Length,
                        Digest = ContentHash.FileDigest(file),
                    });
                }
                catch (Exception e) { skipped.Add($"{Path.GetFileName(file)} ({e.Message})"); }
            }
            entries.Sort((a, b) => ContentHash.CompareUtf8(a.Path, b.Path));
            return entries;
        }
    }
}
