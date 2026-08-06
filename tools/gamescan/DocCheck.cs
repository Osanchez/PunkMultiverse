using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace GameScan
{
    /// <summary>
    /// Checks the identifiers the curated docs claim against what the game assembly actually
    /// declares, so documentation rot shows up as a report instead of as a wrong answer six
    /// months later.
    ///
    /// Only identifiers inside backticks or fenced blocks are considered — prose is far too
    /// noisy to match against a type list. Even so this is a lead generator, not a proof: the
    /// docs legitimately mention prefab ids, config keys and types from other assemblies, none
    /// of which live in Punk.Main. Read the output, do not gate on it.
    /// </summary>
    public static class DocCheck
    {
        public static int Run(Manifest manifest, string docsDir, string srcDir)
        {
            var known = BuildKnownSet(manifest);
            var modSymbols = BuildModSymbols(srcDir);

            var files = Directory.GetFiles(docsDir, "*.md", SearchOption.TopDirectoryOnly)
                                 .OrderBy(f => f, StringComparer.Ordinal);

            var total = 0;
            var flagged = 0;

            foreach (var file in files)
            {
                var name = Path.GetFileName(file);
                total++;

                var text = File.ReadAllText(file);
                var candidates = ExtractIdentifiers(text);

                var unresolved = candidates
                    .Where(id => !known.Contains(id))
                    .Where(id => !modSymbols.Contains(id))
                    .Where(id => !IsKnownNonGame(id))
                    .OrderBy(x => x, StringComparer.Ordinal)
                    .ToList();

                if (unresolved.Count == 0) continue;

                flagged++;
                Console.WriteLine($"{name}  ({unresolved.Count})");
                Console.WriteLine("    " + string.Join(", ", unresolved));
            }

            Console.WriteLine();
            Console.WriteLine($"gamescan: {flagged} of {total} docs mention identifiers not found in the game assembly.");
            Console.WriteLine("gamescan: expect prefab ids, config keys and other assemblies' types here — review, do not gate.");
            return 0;
        }

        /// <summary>
        /// Every name a doc could legitimately use: type simple names, every segment of a nested
        /// name, generic definitions with the arity suffix stripped, and every member name.
        /// Missing the nested-name segments makes well-documented types like FogManager.FogVisual
        /// look absent, which drowns the real signal.
        /// </summary>
        static HashSet<string> BuildKnownSet(Manifest manifest)
        {
            var known = new HashSet<string>(StringComparer.Ordinal);

            void AddName(string s)
            {
                if (string.IsNullOrEmpty(s)) return;
                known.Add(s);
                var tick = s.IndexOf('`');
                if (tick > 0) known.Add(s.Substring(0, tick));
                var dot = s.LastIndexOf('.');
                if (dot >= 0 && dot < s.Length - 1) AddName(s.Substring(dot + 1));
            }

            foreach (var (full, type) in manifest.Types)
            {
                known.Add(full);
                foreach (var segment in full.Split('/')) AddName(segment);

                foreach (var memberKey in type.Members.Keys)
                    AddName(Differ.MemberName(memberKey));
            }

            return known;
        }

        /// <summary>The mod's own type and member names, so docs describing mod code do not flag.</summary>
        static HashSet<string> BuildModSymbols(string srcDir)
        {
            var symbols = new HashSet<string>(StringComparer.Ordinal);
            if (!Directory.Exists(srcDir)) return symbols;

            var declaration = new Regex(@"\b(?:class|struct|enum|interface)\s+([A-Za-z_]\w*)", RegexOptions.Compiled);
            var anyIdentifier = new Regex(@"\b([A-Z]\w{2,})\b", RegexOptions.Compiled);

            foreach (var file in Directory.EnumerateFiles(srcDir, "*.cs", SearchOption.AllDirectories))
            {
                var src = File.ReadAllText(file);
                foreach (Match m in declaration.Matches(src)) symbols.Add(m.Groups[1].Value);
                foreach (Match m in anyIdentifier.Matches(src)) symbols.Add(m.Groups[1].Value);
            }
            return symbols;
        }

        static readonly Regex Inline = new(@"`([^`\n]+)`", RegexOptions.Compiled);
        static readonly Regex Fenced = new(@"```[a-zA-Z]*\n(.*?)```", RegexOptions.Compiled | RegexOptions.Singleline);
        static readonly Regex Identifier = new(@"\b([A-Z][A-Za-z0-9_]{2,})\b", RegexOptions.Compiled);

        static HashSet<string> ExtractIdentifiers(string markdown)
        {
            var found = new HashSet<string>(StringComparer.Ordinal);

            void Scan(string s)
            {
                foreach (Match m in Identifier.Matches(s)) found.Add(m.Groups[1].Value);
            }

            foreach (Match m in Inline.Matches(markdown)) Scan(m.Groups[1].Value);
            foreach (Match m in Fenced.Matches(markdown)) Scan(m.Groups[1].Value);
            return found;
        }

        // Vocabulary that appears in code spans without being a game type: BCL and Unity types,
        // acronyms, doc-link anchors, and the game's own prefab id conventions.
        static readonly Regex NonGame = new(
            @"^(TODO|NOTE|IMPORTANT|PASS|FAIL|WARN|OK|YES|NO|HP|PvP|BR|UI|API|CI|CPU|GPU|RAM|IL|DLL|" +
            @"JSON|UDP|TCP|IP|SSH|AWS|S3|URL|HTTP|HTTPS|VM|OS|SDK|CLI|ID|GUID|FPS|" +
            @"VANILLA_GOTCHAS|GAMESCAN|README|MEMORY|CLAUDE|LICENSE|" +
            @"Box_\w*|Crate\w*|Unit_\w*|Enemy_\w*|Ally_\w*|Br[A-Z]\w*|" +
            @"[A-Z][A-Z_0-9]*|" +                                    // SHOUTING placeholders
            @"Vector\d\w*|Quaternion|Transform|GameObject|MonoBehaviour|Rigidbody2D|Collider2D|" +
            @"Color|Sprite|Texture2D|RawImage|Image|Canvas|RectTransform|Animator|Object|Time|" +
            @"Mathf|Math|Debug|Physics2D|LayerMask|Camera|ScriptableObject|SerializedScriptableObject|" +
            @"Dictionary|List|HashSet|Stack|Queue|Action|Func|Task|IEnumerator|IEnumerable|IDisposable|" +
            @"Native\w+|UniTask\w*|Allocator|String|Single|Int32|Boolean|Byte|Void|Type|" +
            @"PlayerInput|Audio\w+|EventSystem|VisualElement|VisualTreeAsset|Label|Button|Shader|" +
            @"Material|LayoutElement|InputAction\w*|BurstCompile|IJob\w*|IInitializable|IGameService|" +
            @"MinMax\w+|Seeker|Pathfinding|Harmony\w*|AccessTools|BepInEx|Odin|Sirenix|ProCamera2D|" +
            @"MyBox|Delaunay|Perlin)$",
            RegexOptions.Compiled);

        static bool IsKnownNonGame(string id) => NonGame.IsMatch(id);
    }
}
