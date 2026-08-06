using System;
using System.Collections.Generic;
using System.Linq;

namespace GameScan
{
    public enum Tier
    {
        /// <summary>Signature changed or member removed, and the mod depends on it. Compilation
        /// or Harmony patching breaks — loudly, at load.</summary>
        Breaking,
        /// <summary>Body changed under an unchanged signature, and the mod depends on it. Nothing
        /// will report this at runtime; the mod keeps loading and behaves differently.</summary>
        Behavioural,
        /// <summary>Changed, but nothing in the mod references it.</summary>
        Unused,
    }

    public sealed class Finding
    {
        public Tier Tier { get; set; }
        public string Type { get; set; }
        public string Member { get; set; }      // null for type-level findings
        public string Change { get; set; }      // removed | signature | body | type-removed | type-shape
        public string Before { get; set; }
        public string After { get; set; }
        public List<ContractUse> Uses { get; set; } = new();
    }

    public sealed class DiffResult
    {
        public string FromVersion { get; set; }
        public string ToVersion { get; set; }
        public string FromMvid { get; set; }
        public string ToMvid { get; set; }
        public bool AssemblyIdentical { get; set; }

        public List<Finding> Findings { get; set; } = new();
        public List<string> NewTypes { get; set; } = new();
        public List<string> RemovedTypes { get; set; } = new();

        /// <summary>Contract keys that matched nothing in the manifest. A non-trivial count here
        /// means the extractor is mis-keying and the report is under-reporting risk.</summary>
        public List<string> UnresolvedContractKeys { get; set; } = new();

        public int TotalChangedMembers { get; set; }

        public IEnumerable<Finding> Breaking => Findings.Where(f => f.Tier == Tier.Breaking);
        public IEnumerable<Finding> Behavioural => Findings.Where(f => f.Tier == Tier.Behavioural);
        public IEnumerable<Finding> Unused => Findings.Where(f => f.Tier == Tier.Unused);
    }

    public static class Differ
    {
        public static DiffResult Diff(Manifest before, Manifest after, Contract contract)
        {
            var result = new DiffResult
            {
                FromVersion = before.Assembly.GameVersion,
                ToVersion = after.Assembly.GameVersion,
                FromMvid = before.Assembly.Mvid,
                ToMvid = after.Assembly.Mvid,
                AssemblyIdentical = before.Assembly.FileSha256 == after.Assembly.FileSha256,
            };

            var index = BuildContractIndex(contract, before, after, result);

            foreach (var typeName in before.Types.Keys.Where(t => !after.Types.ContainsKey(t)).OrderBy(x => x, StringComparer.Ordinal))
            {
                result.RemovedTypes.Add(typeName);
                var uses = index.ForType(typeName);
                result.Findings.Add(new Finding
                {
                    Tier = uses.Count > 0 ? Tier.Breaking : Tier.Unused,
                    Type = typeName,
                    Change = "type-removed",
                    Uses = uses,
                });
            }

            foreach (var typeName in after.Types.Keys.Where(t => !before.Types.ContainsKey(t)).OrderBy(x => x, StringComparer.Ordinal))
                result.NewTypes.Add(typeName);

            foreach (var (typeName, oldType) in before.Types.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                if (!after.Types.TryGetValue(typeName, out var newType)) continue;
                if (oldType.ShapeHash == newType.ShapeHash && SameHeader(oldType, newType) && SameBodies(oldType, newType))
                    continue;

                if (!SameHeader(oldType, newType))
                {
                    var uses = index.ForType(typeName);
                    result.Findings.Add(new Finding
                    {
                        Tier = uses.Count > 0 ? Tier.Breaking : Tier.Unused,
                        Type = typeName,
                        Change = "type-shape",
                        Before = Header(oldType),
                        After = Header(newType),
                        Uses = uses,
                    });
                }

                foreach (var (memberKey, oldMember) in oldType.Members.OrderBy(k => k.Key, StringComparer.Ordinal))
                {
                    var uses = index.ForMember(typeName, memberKey);

                    if (!newType.Members.TryGetValue(memberKey, out var newMember))
                    {
                        result.TotalChangedMembers++;
                        result.Findings.Add(new Finding
                        {
                            Tier = uses.Count > 0 ? Tier.Breaking : Tier.Unused,
                            Type = typeName,
                            Member = memberKey,
                            Change = "removed",
                            Before = oldMember.SigHash,
                            Uses = uses,
                        });
                        continue;
                    }

                    if (oldMember.SigHash != newMember.SigHash)
                    {
                        result.TotalChangedMembers++;
                        result.Findings.Add(new Finding
                        {
                            Tier = uses.Count > 0 ? Tier.Breaking : Tier.Unused,
                            Type = typeName,
                            Member = memberKey,
                            Change = "signature",
                            Before = oldMember.SigHash,
                            After = newMember.SigHash,
                            Uses = uses,
                        });
                        continue;
                    }

                    if (oldMember.BodyHash != newMember.BodyHash)
                    {
                        result.TotalChangedMembers++;
                        result.Findings.Add(new Finding
                        {
                            Tier = uses.Count > 0 ? Tier.Behavioural : Tier.Unused,
                            Type = typeName,
                            Member = memberKey,
                            Change = "body",
                            Before = $"{oldMember.IlCount} IL",
                            After = $"{newMember.IlCount} IL",
                            Uses = uses,
                        });
                    }
                }
            }

            // Most severe first, then by how many places in the mod touch it — the report should
            // lead with the thing most likely to be the reason something broke.
            result.Findings = result.Findings
                .OrderBy(f => (int)f.Tier)
                .ThenByDescending(f => f.Uses.Count)
                .ThenBy(f => f.Type, StringComparer.Ordinal)
                .ThenBy(f => f.Member, StringComparer.Ordinal)
                .ToList();

            return result;
        }

        static bool SameHeader(TypeEntry a, TypeEntry b) =>
            a.Kind == b.Kind && a.BaseType == b.BaseType && a.IsAbstract == b.IsAbstract &&
            a.IsSealed == b.IsSealed && a.IsPublic == b.IsPublic &&
            a.Interfaces.SequenceEqual(b.Interfaces);

        static string Header(TypeEntry t) =>
            $"{(t.IsPublic ? "public " : "")}{(t.IsAbstract ? "abstract " : "")}{(t.IsSealed ? "sealed " : "")}" +
            $"{t.Kind} : {t.BaseType ?? "-"}" +
            (t.Interfaces.Count > 0 ? " + " + string.Join(", ", t.Interfaces) : "");

        static bool SameBodies(TypeEntry a, TypeEntry b)
        {
            foreach (var (k, v) in a.Members)
            {
                if (!b.Members.TryGetValue(k, out var other)) return false;
                if (v.BodyHash != other.BodyHash) return false;
            }
            return true;
        }

        // ---- contract index ---------------------------------------------------------------

        sealed class ContractIndex
        {
            public Dictionary<string, List<ContractUse>> ByType = new(StringComparer.Ordinal);
            public Dictionary<string, List<ContractUse>> ByMember = new(StringComparer.Ordinal);

            public List<ContractUse> ForType(string type) =>
                ByType.TryGetValue(type, out var v) ? v : new List<ContractUse>();

            public List<ContractUse> ForMember(string type, string memberKey)
            {
                var uses = new List<ContractUse>();
                if (ByMember.TryGetValue($"{type}::{memberKey}", out var exact)) uses.AddRange(exact);
                // A member is also covered when the whole type is patched by a class-level
                // [HarmonyPatch(typeof(X))] with no member named — the patch could target it.
                if (ByType.TryGetValue(type, out var t))
                    uses.AddRange(t.Where(u => u.Via == "harmony-patch"));
                return uses;
            }
        }

        /// <summary>
        /// Resolves contract keys against the manifest. String-named keys ("Type::#Name") are
        /// expanded to every member of that type with that name, since a name alone cannot
        /// distinguish overloads — and if any overload changed, the lookup is at risk.
        /// </summary>
        static ContractIndex BuildContractIndex(Contract contract, Manifest before, Manifest after, DiffResult result)
        {
            var index = new ContractIndex();

            foreach (var (key, uses) in contract.Uses)
            {
                var sep = key.IndexOf("::", StringComparison.Ordinal);
                if (sep < 0)
                {
                    if (!before.Types.ContainsKey(key) && !after.Types.ContainsKey(key))
                    {
                        result.UnresolvedContractKeys.Add(key);
                        continue;
                    }
                    Merge(index.ByType, key, uses);
                    continue;
                }

                var typeName = key.Substring(0, sep);
                var memberPart = key.Substring(sep + 2);

                if (!before.Types.ContainsKey(typeName) && !after.Types.ContainsKey(typeName))
                {
                    result.UnresolvedContractKeys.Add(key);
                    continue;
                }

                // A patch declared against a subclass usually resolves to a method the subclass
                // inherits — ModulePickup.Update actually lives on InteractiblePickup`1. Attribute
                // the use to whichever ancestor really declares it, or a change there is missed
                // entirely.
                var wanted = memberPart.StartsWith("#", StringComparison.Ordinal)
                    ? memberPart.Substring(1)
                    : MemberName(memberPart);

                var matched = 0;
                foreach (var (ownerName, owner) in Ancestry(typeName, before, after))
                {
                    if (memberPart.StartsWith("#", StringComparison.Ordinal) is false &&
                        owner.Members.ContainsKey(memberPart))
                    {
                        Merge(index.ByMember, $"{ownerName}::{memberPart}", uses);
                        matched++;
                        break;
                    }

                    foreach (var mk in owner.Members.Keys.Where(mk => MemberName(mk) == wanted))
                    {
                        Merge(index.ByMember, $"{ownerName}::{mk}", uses);
                        matched++;
                    }

                    // Harmony property patches name the property; the IL members are get_/set_.
                    if (matched == 0)
                    {
                        foreach (var mk in owner.Members.Keys)
                        {
                            var n = MemberName(mk);
                            if (n != "get_" + wanted && n != "set_" + wanted) continue;
                            Merge(index.ByMember, $"{ownerName}::{mk}", uses);
                            matched++;
                        }
                    }

                    if (matched > 0) break;
                }

                if (matched == 0) result.UnresolvedContractKeys.Add(key);
            }

            return index;
        }

        /// <summary>
        /// The type itself, then each ancestor that the game assembly declares. Stops at the
        /// first base it does not own (UnityEngine.MonoBehaviour and friends).
        /// </summary>
        static IEnumerable<(string Name, TypeEntry Entry)> Ancestry(string typeName, Manifest before, Manifest after)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var current = typeName;

            while (current != null && seen.Add(current))
            {
                if (!before.Types.TryGetValue(current, out var entry) &&
                    !after.Types.TryGetValue(current, out entry))
                    yield break;

                yield return (current, entry);
                // BaseType is a closed instantiation ("InteractiblePickup`1<ModulePickup/Data>");
                // manifest keys are the open definition, so drop the type arguments.
                current = StripGenericArgs(entry.BaseType);
            }
        }

        static string StripGenericArgs(string typeName)
        {
            if (typeName == null) return null;
            var lt = typeName.IndexOf('<');
            return lt < 0 ? typeName : typeName.Substring(0, lt);
        }

        static void Merge(Dictionary<string, List<ContractUse>> map, string key, List<ContractUse> uses)
        {
            if (!map.TryGetValue(key, out var list)) map[key] = list = new List<ContractUse>();
            list.AddRange(uses);
        }

        /// <summary>Member keys are "Type Name(params)" / "Type Name" — the name sits after the
        /// first space and ends at the parameter list.</summary>
        public static string MemberName(string memberKey)
        {
            var space = memberKey.IndexOf(' ');
            if (space < 0) return memberKey;
            var rest = memberKey.Substring(space + 1);
            var cut = rest.IndexOfAny(new[] { '(', '[', '`' });
            return cut < 0 ? rest : rest.Substring(0, cut);
        }
    }
}
