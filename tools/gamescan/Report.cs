using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace GameScan
{
    public static class Report
    {
        /// <summary>
        /// The point of this report is triage, not completeness: lead with the handful of
        /// changes that can actually break the mod, and collapse the hundreds that cannot.
        /// </summary>
        public static string Markdown(DiffResult d, string beforeCacheDir, string afterCacheDir)
        {
            var sb = new StringBuilder();

            sb.AppendLine("# Game update report");
            sb.AppendLine();
            sb.AppendLine($"- **Game version:** `{d.FromVersion}` → `{d.ToVersion}`");
            sb.AppendLine($"- **Module id:** `{Short(d.FromMvid)}` → `{Short(d.ToMvid)}`");
            sb.AppendLine();

            if (d.AssemblyIdentical)
            {
                sb.AppendLine("**The game assembly is byte-identical to the baseline.** Nothing changed.");
                return sb.ToString();
            }

            var breaking = d.Breaking.ToList();
            var behavioural = d.Behavioural.ToList();
            var unused = d.Unused.ToList();

            sb.AppendLine("## Summary");
            sb.AppendLine();
            sb.AppendLine("| Tier | Count | Meaning |");
            sb.AppendLine("|---|---:|---|");
            sb.AppendLine($"| 🔴 Breaking | {breaking.Count} | Signature changed or member removed, **and the mod uses it**. Harmony will throw at load. |");
            sb.AppendLine($"| 🟠 Behavioural | {behavioural.Count} | Body changed, signature stable, **and the mod uses it**. Nothing will warn you. |");
            sb.AppendLine($"| ⚪ Unused | {unused.Count} | Changed, but the mod does not reference it. |");
            sb.AppendLine();
            sb.AppendLine($"New types: {d.NewTypes.Count} · Removed types: {d.RemovedTypes.Count} · Total changed members: {d.TotalChangedMembers}");
            sb.AppendLine();

            if (breaking.Count == 0 && behavioural.Count == 0)
            {
                sb.AppendLine("> Nothing the mod depends on changed. The update is very unlikely to have broken it.");
                sb.AppendLine();
            }

            Section(sb, "🔴 Breaking — the mod depends on these and they changed shape", breaking, beforeCacheDir, afterCacheDir, true);
            Section(sb, "🟠 Behavioural — same signature, different code", behavioural, beforeCacheDir, afterCacheDir, true);

            if (unused.Count > 0)
            {
                sb.AppendLine("<details>");
                sb.AppendLine($"<summary>⚪ {unused.Count} changes the mod does not reference</summary>");
                sb.AppendLine();
                foreach (var g in unused.GroupBy(f => f.Type).OrderBy(g => g.Key, StringComparer.Ordinal))
                    sb.AppendLine($"- `{g.Key}` — {g.Count()} member(s)");
                sb.AppendLine();
                sb.AppendLine("</details>");
                sb.AppendLine();
            }

            if (d.NewTypes.Count > 0)
            {
                sb.AppendLine("<details>");
                sb.AppendLine($"<summary>New types ({d.NewTypes.Count})</summary>");
                sb.AppendLine();
                foreach (var t in d.NewTypes) sb.AppendLine($"- `{t}`");
                sb.AppendLine();
                sb.AppendLine("</details>");
                sb.AppendLine();
            }

            if (d.UnresolvedContractKeys.Count > 0)
            {
                sb.AppendLine("<details>");
                sb.AppendLine($"<summary>⚠ {d.UnresolvedContractKeys.Count} contract keys did not resolve against the manifest</summary>");
                sb.AppendLine();
                sb.AppendLine("These are members the mod appears to reference that no manifest entry matched.");
                sb.AppendLine("A handful is normal (generic instantiations, members the update deleted).");
                sb.AppendLine("A large number means the extractor is mis-keying and this report is under-reporting risk.");
                sb.AppendLine();
                foreach (var k in d.UnresolvedContractKeys.Distinct().OrderBy(x => x, StringComparer.Ordinal).Take(80))
                    sb.AppendLine($"- `{k}`");
                sb.AppendLine();
                sb.AppendLine("</details>");
                sb.AppendLine();
            }

            return sb.ToString();
        }

        static void Section(StringBuilder sb, string title, List<Finding> findings,
                            string beforeCacheDir, string afterCacheDir, bool showUses)
        {
            if (findings.Count == 0) return;

            sb.AppendLine($"## {title}");
            sb.AppendLine();

            foreach (var g in findings.GroupBy(f => f.Type).OrderByDescending(g => g.Sum(f => f.Uses.Count)))
            {
                sb.AppendLine($"### `{g.Key}`");
                sb.AppendLine();

                foreach (var f in g)
                {
                    var what = f.Member ?? "(type declaration)";
                    sb.AppendLine($"- **{f.Change}** — `{what}`");
                    if (f.Change == "type-shape")
                    {
                        sb.AppendLine($"  - was: `{f.Before}`");
                        sb.AppendLine($"  - now: `{f.After}`");
                    }
                    else if (f.Change == "body")
                    {
                        sb.AppendLine($"  - {f.Before} → {f.After}");
                    }

                    if (showUses && f.Uses.Count > 0)
                    {
                        foreach (var u in f.Uses.Take(6))
                        {
                            var loc = u.SourceFile != null
                                ? $"{Trim(u.SourceFile)}:{u.SourceLine}"
                                : u.FromMember;
                            sb.AppendLine($"  - used via *{u.Via}* at `{loc}`");
                        }
                        if (f.Uses.Count > 6)
                            sb.AppendLine($"  - …and {f.Uses.Count - 6} more use(s)");
                    }
                }

                sb.AppendLine();
                if (beforeCacheDir != null && afterCacheDir != null)
                {
                    // Delegating the actual text diff to git keeps this tool out of the business
                    // of implementing one, and the decompiled cache is already on disk.
                    var file = SimpleName(g.Key) + ".cs";
                    sb.AppendLine("  <sub>read the change:</sub>");
                    sb.AppendLine("  ```");
                    sb.AppendLine($"  git diff --no-index {beforeCacheDir}/{file} {afterCacheDir}/{file}");
                    sb.AppendLine("  ```");
                }
                sb.AppendLine();
            }
        }

        /// <summary>ilspycmd names files after the outermost type, without namespace.</summary>
        static string SimpleName(string fullName)
        {
            var slash = fullName.IndexOf('/');
            if (slash >= 0) fullName = fullName.Substring(0, slash);
            var dot = fullName.LastIndexOf('.');
            return dot >= 0 ? fullName.Substring(dot + 1) : fullName;
        }

        static string Trim(string path)
        {
            var i = path.Replace('\\', '/').IndexOf("/src/", StringComparison.OrdinalIgnoreCase);
            return i >= 0 ? path.Replace('\\', '/').Substring(i + 1) : path;
        }

        static string Short(string mvid) => mvid == null ? "?" : mvid.Substring(0, Math.Min(8, mvid.Length));
    }
}
