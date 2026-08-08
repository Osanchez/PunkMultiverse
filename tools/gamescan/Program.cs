using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace GameScan
{
    public static class Program
    {
        static readonly JsonSerializerOptions Json = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        };

        public static int Main(string[] args)
        {
            if (args.Length == 0) { Usage(); return 1; }

            try
            {
                return args[0] switch
                {
                    "manifest" => CmdManifest(Parse(args)),
                    "contract" => CmdContract(Parse(args)),
                    "diff"     => CmdDiff(Parse(args)),
                    "index"    => CmdIndex(Parse(args)),
                    "guard"    => CmdGuard(Parse(args)),
                    "doccheck" => CmdDocCheck(Parse(args)),
                    _          => Unknown(args[0]),
                };
            }
            catch (Exception ex)
            {
                Console.WriteLine($"gamescan: {ex.Message}");
                return 2;
            }
        }

        static int Unknown(string cmd)
        {
            Console.WriteLine($"gamescan: unknown command '{cmd}'");
            Usage();
            return 1;
        }

        static void Usage()
        {
            Console.WriteLine("""
                gamescan — game-update change detector

                  manifest --dll <Punk.Main.dll> --out <manifest.json> [--game-version <v>]
                      Hash every type and member of the game assembly.

                  contract --mod <PunkMultiverse.dll> --out <contract.json>
                      Extract the game members the mod depends on, from the compiled mod IL.

                  diff --before <manifest.json> --after <manifest.json> --contract <contract.json>
                       --out-md <report.md> [--out-json <report.json>]
                       [--cache-before <dir>] [--cache-after <dir>]
                      Tiered report: what changed that the mod actually uses.

                  index --manifest <manifest.json> --out-dir <dir>
                      Generate per-area API index markdown.

                  guard --manifest <manifest.json> --contract <contract.json> --out <GameBaseline.g.cs>
                      Generate the compact baseline the mod checks at boot.

                  doccheck --manifest <manifest.json> --docs-dir <docs> --src-dir <src>
                      Report identifiers the docs claim that the game assembly does not declare.
                """);
        }

        // ---- commands ---------------------------------------------------------------------

        static int CmdManifest(Dictionary<string, string> a)
        {
            var dll = Require(a, "dll");
            var outPath = Require(a, "out");
            a.TryGetValue("game-version", out var version);

            Console.WriteLine($"gamescan: reading {dll}");
            var manifest = ManifestBuilder.Build(dll, version);

            var members = manifest.Types.Sum(t => t.Value.Members.Count);
            Console.WriteLine($"gamescan: {manifest.Types.Count} types, {members} members");

            Write(outPath, JsonSerializer.Serialize(manifest, Json));
            Console.WriteLine($"gamescan: wrote {outPath}");
            return 0;
        }

        static int CmdContract(Dictionary<string, string> a)
        {
            var mod = Require(a, "mod");
            var outPath = Require(a, "out");

            Console.WriteLine($"gamescan: reading {mod}");
            var contract = ContractBuilder.Build(mod);

            var byVia = contract.Uses
                .SelectMany(kv => kv.Value.Select(u => u.Via))
                .GroupBy(v => v)
                .OrderByDescending(g => g.Count());
            Console.WriteLine($"gamescan: {contract.Uses.Count} game members referenced");
            foreach (var g in byVia) Console.WriteLine($"          {g.Count(),6}  via {g.Key}");

            Write(outPath, JsonSerializer.Serialize(contract, Json));
            Console.WriteLine($"gamescan: wrote {outPath}");
            return 0;
        }

        static int CmdDiff(Dictionary<string, string> a)
        {
            var before = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(Require(a, "before")), Json);
            var after = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(Require(a, "after")), Json);
            var contract = JsonSerializer.Deserialize<Contract>(File.ReadAllText(Require(a, "contract")), Json);

            if (before.FormatVersion != after.FormatVersion)
                throw new InvalidOperationException(
                    $"manifest format {before.FormatVersion} vs {after.FormatVersion} — regenerate the baseline, " +
                    "hashes from different schemes are not comparable");

            var result = Differ.Diff(before, after, contract);

            a.TryGetValue("cache-before", out var cb);
            a.TryGetValue("cache-after", out var ca);
            var md = Report.Markdown(result, cb, ca);

            Write(Require(a, "out-md"), md);
            if (a.TryGetValue("out-json", out var oj))
                Write(oj, JsonSerializer.Serialize(result, Json));

            var breaking = result.Breaking.Count();
            var behavioural = result.Behavioural.Count();
            Console.WriteLine($"gamescan: {breaking} breaking, {behavioural} behavioral, {result.Unused.Count()} unused");

            // Exit code carries the verdict so CI can gate on it: 0 clean, 3 behavioural only,
            // 4 breaking.
            if (breaking > 0) return 4;
            if (behavioural > 0) return 3;
            return 0;
        }

        static int CmdIndex(Dictionary<string, string> a)
        {
            var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(Require(a, "manifest")), Json);
            var outDir = Require(a, "out-dir");
            Directory.CreateDirectory(outDir);

            var grouped = manifest.Types
                .Where(t => !t.Value.CompilerGenerated)
                .GroupBy(t => Areas.Classify(t.Key).Slug)
                .ToDictionary(g => g.Key, g => g.OrderBy(t => t.Key, StringComparer.Ordinal).ToList());

            var areas = Areas.All.ToList();
            var written = 0;

            foreach (var area in areas)
            {
                if (!grouped.TryGetValue(area.Slug, out var types) || types.Count == 0) continue;

                var sb = new StringBuilder();
                sb.AppendLine($"# API index — {area.Title}");
                sb.AppendLine();
                sb.AppendLine($"Generated by `tools/gamescan` from `Punk.Main.dll` " +
                              $"(game `{manifest.Assembly.GameVersion}`, module `{manifest.Assembly.Mvid[..8]}`).");
                sb.AppendLine("**Do not edit** — regenerate with `tools/gamescan.ps1 -Index`.");
                if (area.Doc != null) sb.AppendLine($"Prose for this area: [`{area.Doc}`]({area.Doc.Replace(".md", ".md")})");
                sb.AppendLine();
                sb.AppendLine($"{types.Count} types.");
                sb.AppendLine();

                foreach (var (name, t) in types)
                {
                    sb.AppendLine($"## `{name}`");
                    sb.AppendLine();
                    var header = $"{t.Kind}";
                    if (t.BaseType != null && t.BaseType != "System.Object") header += $" : {Simple(t.BaseType)}";
                    if (t.Interfaces.Count > 0) header += (t.BaseType != null && t.BaseType != "System.Object" ? ", " : " : ") + string.Join(", ", t.Interfaces.Select(Simple));
                    sb.AppendLine($"`{header}`");
                    sb.AppendLine();

                    foreach (var (kind, label) in new[]
                             {
                                 ("field", "fields"), ("property", "properties"),
                                 ("event", "events"), ("method", "methods"),
                             })
                    {
                        var members = t.Members
                            .Where(m => m.Value.Kind == kind && !m.Value.CompilerGenerated)
                            .OrderBy(m => Differ.MemberName(m.Key), StringComparer.Ordinal)
                            .ToList();
                        if (members.Count == 0) continue;

                        sb.AppendLine($"<sub>{label}</sub>");
                        sb.AppendLine();
                        sb.AppendLine("```csharp");
                        foreach (var (key, _) in members) sb.AppendLine(Simplify(key));
                        sb.AppendLine("```");
                        sb.AppendLine();
                    }
                }

                var path = Path.Combine(outDir, area.Slug + ".md");
                Write(path, sb.ToString());
                written++;
            }

            // A landing page, so the index is navigable rather than a folder of slugs.
            var toc = new StringBuilder();
            toc.AppendLine("# API index");
            toc.AppendLine();
            toc.AppendLine($"Mechanically generated from `Punk.Main.dll` — {manifest.Types.Count} types, " +
                           $"{manifest.Types.Sum(t => t.Value.Members.Count)} members. **Do not edit.**");
            toc.AppendLine();
            toc.AppendLine("This is a lookup table, not an explanation. For how a system actually works,");
            toc.AppendLine("read the curated doc for its area (linked below) — and `../VANILLA_GOTCHAS.md`");
            toc.AppendLine("before changing anything.");
            toc.AppendLine();
            toc.AppendLine("| Area | Types | Prose |");
            toc.AppendLine("|---|---:|---|");
            foreach (var area in areas)
            {
                if (!grouped.TryGetValue(area.Slug, out var types) || types.Count == 0) continue;
                var prose = area.Doc != null ? $"[`{area.Doc}`](../{area.Doc})" : "—";
                toc.AppendLine($"| [{area.Title}]({area.Slug}.md) | {types.Count} | {prose} |");
            }
            Write(Path.Combine(outDir, "README.md"), toc.ToString());

            Console.WriteLine($"gamescan: wrote {written} area index files to {outDir}");
            return 0;
        }

        static int CmdGuard(Dictionary<string, string> a)
        {
            var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(Require(a, "manifest")), Json);
            var contract = JsonSerializer.Deserialize<Contract>(File.ReadAllText(Require(a, "contract")), Json);
            var outPath = Require(a, "out");

            var source = GuardBuilder.Build(manifest, contract);
            Write(outPath, source);
            Console.WriteLine($"gamescan: wrote {outPath}");
            return 0;
        }

        static int CmdDocCheck(Dictionary<string, string> a)
        {
            var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(Require(a, "manifest")), Json);
            return DocCheck.Run(manifest, Require(a, "docs-dir"), Require(a, "src-dir"));
        }

        // ---- helpers ----------------------------------------------------------------------

        /// <summary>Member keys carry fully-qualified type names; the index is far easier to scan
        /// with the namespaces dropped.</summary>
        static string Simplify(string memberKey)
        {
            var sb = new StringBuilder(memberKey.Length);
            var token = new StringBuilder();
            foreach (var ch in memberKey)
            {
                if (char.IsLetterOrDigit(ch) || ch == '.' || ch == '_' || ch == '`')
                {
                    token.Append(ch);
                }
                else
                {
                    sb.Append(Simple(token.ToString()));
                    token.Clear();
                    sb.Append(ch);
                }
            }
            sb.Append(Simple(token.ToString()));
            return sb.ToString();
        }

        static string Simple(string typeName)
        {
            if (string.IsNullOrEmpty(typeName)) return typeName;
            var slash = typeName.LastIndexOf('/');
            if (slash >= 0) typeName = typeName.Substring(slash + 1);
            var dot = typeName.LastIndexOf('.');
            // Keep a trailing dot (it was punctuation, not a namespace separator).
            if (dot == typeName.Length - 1) return typeName;
            return dot >= 0 ? typeName.Substring(dot + 1) : typeName;
        }

        static Dictionary<string, string> Parse(string[] args)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 1; i < args.Length; i++)
            {
                if (!args[i].StartsWith("--", StringComparison.Ordinal)) continue;
                var key = args[i].Substring(2);
                var value = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal)
                    ? args[++i] : "true";
                map[key] = value;
            }
            return map;
        }

        static string Require(Dictionary<string, string> a, string key) =>
            a.TryGetValue(key, out var v)
                ? v
                : throw new ArgumentException($"missing required argument --{key}");

        static void Write(string path, string content)
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, content, new UTF8Encoding(false));
        }
    }
}
