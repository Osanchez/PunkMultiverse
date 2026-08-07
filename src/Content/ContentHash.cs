using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace PunkMultiverse.Content
{
    /// <summary>
    /// Identity for a set of content files, agreed byte-for-byte between a Windows client and a
    /// Wine/Linux dedicated server.
    ///
    /// This is the one piece whose correctness the whole feature rests on: if two machines can
    /// disagree about the hash of identical bytes, clients re-download forever; if they can agree
    /// on the hash of DIFFERENT bytes, the module registry silently diverges and BR's drop table
    /// goes with it. Everything here is chosen to remove a way for those to happen.
    ///
    /// Deliberately SHA-256 rather than the repo's usual FNV-1a 64. The claim being made is
    /// "these files are byte-identical", and a 64-bit non-cryptographic digest cannot back it —
    /// a collision means a client plays with different weapon data and nothing notices. FNV stays
    /// for the short display ids in log lines. Do not "fix" this back.
    /// </summary>
    internal static class ContentHash
    {
        /// <summary>Bump when the hashing rule changes; it invalidates every cached set rather
        /// than silently comparing incomparable digests.</summary>
        private const string SetPrefix = "PMVCONTENT1\n";
        private const string FilePrefix = "PMVFILE1\n";

        internal const int DigestBytes = 16;

        internal sealed class Entry
        {
            internal string Path;        // canonical, '/'-separated, case preserved
            internal long Length;
            internal byte[] Digest;      // DigestBytes long
        }

        // ---- canonical paths --------------------------------------------------------------

        /// <summary>
        /// The path as it travels: separators normalised, case PRESERVED.
        ///
        /// Case is not folded on purpose. Folding would let two files differing only in case
        /// collapse into one entry, and a Linux client has to open the exact name it was given.
        /// Portability is enforced by refusing to publish a bad set (see <see cref="Validate"/>),
        /// which fails once on the host rather than on every client.
        /// </summary>
        internal static string Canonical(string relative)
        {
            if (string.IsNullOrEmpty(relative)) return "";
            var s = relative.Replace('\\', '/').TrimStart('/');
            // Form C so a decomposed and a precomposed spelling of the same name agree. Mono
            // throws on lone surrogates, so a failure falls back to the raw string rather than
            // taking the whole set down.
            try { s = s.Normalize(NormalizationForm.FormC); } catch { }
            return s;
        }

        private static readonly string[] ReservedNames =
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        };

        /// <summary>
        /// Never transferred, in either direction. Two separate reasons, and either alone would
        /// be enough:
        ///
        /// A content channel that carries executables is a code-delivery channel. Joining a
        /// stranger's server must not be a way to receive a DLL. Today nothing loads out of the
        /// materialised tree except WeaponForge's own JSON/PNG/WAV readers — but that is a
        /// property of today's code, and this feature deliberately points another plugin's
        /// loader at that tree.
        ///
        /// And the obvious thing for a host to set ContentRoot to is WeaponForge's own plugin
        /// folder, which contains WeaponForge.dll. Publishing that would redistribute a
        /// third-party binary that carries no licence granting it, to every player who joins.
        ///
        /// This is a denylist rather than an allowlist on purpose: an allowlist would have to
        /// guess which formats a content mod we do not control considers valid, and would refuse
        /// legitimate content the first time one added a format. A denylist only has to know what
        /// is dangerous, which is a much smaller and more stable set.
        /// </summary>
        private static readonly string[] ExecutableExtensions =
        {
            ".dll", ".exe", ".so", ".dylib", ".msi", ".com", ".scr", ".jar",
            ".bat", ".cmd", ".ps1", ".psm1", ".sh", ".vbs", ".js", ".lnk",
        };

        /// <summary>
        /// Why a path cannot be published, or null if it is fine. The host refuses a set that
        /// contains one of these instead of handing every client something it cannot materialise
        /// — and a receiver applies the same rules to bytes it was sent, because the transfer
        /// writes host-chosen filenames to disk and that is a traversal surface, not a nicety.
        /// </summary>
        internal static string Reject(string canonical)
        {
            if (string.IsNullOrEmpty(canonical)) return "empty path";
            if (canonical.Length > 200) return "path longer than 200 characters";
            foreach (var ext in ExecutableExtensions)
                if (canonical.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                    return $"'{ext}' files are never transferred";
            if (canonical.StartsWith("/", StringComparison.Ordinal)) return "absolute path";
            if (canonical.IndexOf(':') >= 0) return "drive or stream separator";
            foreach (var segment in canonical.Split('/'))
            {
                if (segment.Length == 0) return "empty path segment";
                if (segment == "." || segment == "..") return "relative path segment";
                if (segment.EndsWith(".", StringComparison.Ordinal)
                    || segment.EndsWith(" ", StringComparison.Ordinal))
                    return "segment ends with a dot or space (unopenable on Windows)";
                var bare = segment;
                int dot = bare.IndexOf('.');
                if (dot > 0) bare = bare.Substring(0, dot);
                foreach (var reserved in ReservedNames)
                    if (string.Equals(bare, reserved, StringComparison.OrdinalIgnoreCase))
                        return $"reserved Windows device name '{reserved}'";
                foreach (var ch in segment)
                {
                    if (ch < 32) return "control character";
                    if (ch == '*' || ch == '?' || ch == '"' || ch == '<' || ch == '>' || ch == '|')
                        return $"character '{ch}' is not legal in a Windows filename";
                }
            }
            return null;
        }

        /// <summary>
        /// Check a whole set for portability. Returns the problems, empty when publishable.
        /// Case-only duplicates are caught here because they are legal on Linux and impossible on
        /// Windows — exactly the asymmetry that would make a dedicated server serve a set no
        /// player could ever install.
        /// </summary>
        /// <summary>
        /// Host side: split a scanned folder into what will actually be published and the reasons
        /// the rest will not be.
        ///
        /// The host must DROP what it cannot publish, not merely report it. <see cref="Validate"/>
        /// on its own is advisory, and a set that carries one bad path is refused by every client
        /// in full — so a single stray file in ContentRoot would take the whole session down with
        /// a message about the file rather than about the run. Dropping it means the set the host
        /// offers is always installable, and the client-side Validate is what it is documented to
        /// be: defence in depth against a hand-crafted offer, not the primary check.
        /// </summary>
        internal static List<Entry> Publishable(IEnumerable<Entry> entries, out List<string> problems)
        {
            problems = new List<string>();
            var kept = new List<Entry>();
            var byFolded = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in entries)
            {
                var why = Reject(e.Path);
                if (why != null) { problems.Add($"{e.Path}: {why}"); continue; }
                if (byFolded.TryGetValue(e.Path, out var other) && !string.Equals(other, e.Path, StringComparison.Ordinal))
                {
                    problems.Add($"{e.Path}: differs from '{other}' only by case");
                    continue;
                }
                byFolded[e.Path] = e.Path;
                kept.Add(e);
            }
            return kept;
        }

        /// <summary>
        /// Receive side: is this offer installable, in full? A client does not get to drop the
        /// parts it dislikes — the set hash covers every file, so an offer with a bad path is
        /// refused whole. The host has already dropped anything unpublishable
        /// (<see cref="Publishable"/>), so reaching here means a hand-crafted or corrupted offer.
        /// </summary>
        internal static List<string> Validate(IEnumerable<Entry> entries)
        {
            Publishable(entries, out var problems);
            return problems;
        }

        // ---- digests ----------------------------------------------------------------------

        /// <summary>Digest of one file's bytes. Length is folded in so a truncation cannot
        /// collide with the untruncated original under the same prefix.</summary>
        internal static byte[] FileDigest(Stream data, long length)
        {
            using (var sha = SHA256.Create())
            {
                var header = Encoding.UTF8.GetBytes(FilePrefix + length.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n");
                sha.TransformBlock(header, 0, header.Length, null, 0);
                var buffer = new byte[64 * 1024];
                int read;
                while ((read = data.Read(buffer, 0, buffer.Length)) > 0)
                    sha.TransformBlock(buffer, 0, read, null, 0);
                sha.TransformFinalBlock(new byte[0], 0, 0);
                return Truncate(sha.Hash);
            }
        }

        internal static byte[] FileDigest(string path)
        {
            using (var fs = File.OpenRead(path))
                return FileDigest(fs, fs.Length);
        }

        /// <summary>
        /// Digest of the whole set. Entries are ordered by the UTF-8 BYTES of their path, not by
        /// StringComparer.Ordinal: Ordinal compares UTF-16 code units, which disagrees with UTF-8
        /// byte order across the surrogate range. Both sides run the same build so Ordinal would
        /// in practice agree, but byte order is self-evidently right and removes the question.
        /// The entry count is appended, not prepended, as a length-extension guard.
        /// </summary>
        internal static byte[] SetDigest(List<Entry> entries)
        {
            var ordered = new List<Entry>(entries);
            ordered.Sort((a, b) => CompareUtf8(a.Path, b.Path));
            using (var sha = SHA256.Create())
            {
                Feed(sha, Encoding.UTF8.GetBytes(SetPrefix));
                foreach (var e in ordered)
                {
                    var path = Encoding.UTF8.GetBytes(e.Path);
                    Feed(sha, BitConverter.GetBytes(path.Length));
                    Feed(sha, path);
                    Feed(sha, BitConverter.GetBytes(e.Length));
                    Feed(sha, e.Digest ?? new byte[DigestBytes]);
                }
                Feed(sha, BitConverter.GetBytes(ordered.Count));
                sha.TransformFinalBlock(new byte[0], 0, 0);
                return Truncate(sha.Hash);
            }
        }

        private static void Feed(SHA256 sha, byte[] data) => sha.TransformBlock(data, 0, data.Length, null, 0);

        internal static int CompareUtf8(string a, string b)
        {
            var x = Encoding.UTF8.GetBytes(a ?? "");
            var y = Encoding.UTF8.GetBytes(b ?? "");
            int n = Math.Min(x.Length, y.Length);
            for (int i = 0; i < n; i++)
                if (x[i] != y[i]) return x[i].CompareTo(y[i]);
            return x.Length.CompareTo(y.Length);
        }

        private static byte[] Truncate(byte[] full)
        {
            var result = new byte[DigestBytes];
            Buffer.BlockCopy(full, 0, result, 0, DigestBytes);
            return result;
        }

        internal static string ToHex(byte[] digest)
        {
            if (digest == null) return "";
            var sb = new StringBuilder(digest.Length * 2);
            foreach (var b in digest) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        internal static byte[] FromHex(string hex)
        {
            if (string.IsNullOrEmpty(hex) || (hex.Length & 1) != 0) return null;
            var result = new byte[hex.Length / 2];
            for (int i = 0; i < result.Length; i++)
            {
                if (!byte.TryParse(hex.Substring(i * 2, 2), System.Globalization.NumberStyles.HexNumber,
                        System.Globalization.CultureInfo.InvariantCulture, out result[i]))
                    return null;
            }
            return result;
        }

        internal static bool SameDigest(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        /// <summary>The empty set's digest — what a host with no content offers, and what a client
        /// compares against to know it needs nothing.</summary>
        internal static byte[] EmptySet() => SetDigest(new List<Entry>());
    }
}
