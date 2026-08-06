using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace GameScan
{
    /// <summary>
    /// Extracts the set of game members the mod depends on, by reading the COMPILED mod
    /// assembly rather than its source.
    ///
    /// Why the DLL and not a regex over src/: the IL records every call, field access and type
    /// reference as resolved metadata. That catches ordinary direct calls into game code — the
    /// large majority of the dependency surface — which a scan for [HarmonyPatch] and
    /// AccessTools would miss entirely. The only thing IL cannot express is a member named by
    /// string, so those two forms (Harmony attribute arguments and AccessTools lookups) are
    /// recovered separately below.
    /// </summary>
    public static class ContractBuilder
    {
        const string GameAssembly = "Punk.Main";

        public static Contract Build(string modDllPath)
        {
            var readerParams = new ReaderParameters
            {
                ReadingMode = ReadingMode.Immediate,
                AssemblyResolver = new NullResolver(),
                ReadSymbols = false,
            };

            // Symbols give us src/File.cs:line for every use, which is what makes the update
            // report actionable. They are optional — a CI build without a PDB still works.
            AssemblyDefinition asm;
            try
            {
                asm = AssemblyDefinition.ReadAssembly(modDllPath, new ReaderParameters
                {
                    ReadingMode = ReadingMode.Immediate,
                    AssemblyResolver = new NullResolver(),
                    ReadSymbols = true,
                });
            }
            catch
            {
                Console.WriteLine("  (no PDB alongside the mod DLL — uses will have no source locations)");
                asm = AssemblyDefinition.ReadAssembly(modDllPath, readerParams);
            }

            using (asm)
            {
                var contract = new Contract
                {
                    ModAssembly = asm.Name.Name,
                    ModVersion = asm.Name.Version.ToString(),
                    CapturedAtUtc = DateTime.UtcNow.ToString("O"),
                };

                foreach (var type in ManifestBuilder.AllTypes(asm.MainModule))
                {
                    // A [HarmonyPatch(typeof(X))] on the class sets the target for every patch
                    // method inside it; method-level attributes then name the member.
                    var classTargets = HarmonyTargets(type);

                    foreach (var method in type.Methods)
                    {
                        var from = $"{type.FullName}::{method.Name}";

                        RecordHarmony(contract, classTargets, HarmonyTargets(method), from, FirstPoint(method));
                        if (method.HasBody) WalkBody(contract, method, from);
                    }
                }

                return contract;
            }
        }

        // ---- Harmony attributes -----------------------------------------------------------

        readonly struct HarmonyTarget
        {
            public readonly string TypeName;
            public readonly string MemberName;
            public HarmonyTarget(string t, string m) { TypeName = t; MemberName = m; }
        }

        static List<HarmonyTarget> HarmonyTargets(ICustomAttributeProvider p)
        {
            var result = new List<HarmonyTarget>();
            if (!p.HasCustomAttributes) return result;

            foreach (var attr in p.CustomAttributes)
            {
                if (attr.AttributeType.Name != "HarmonyPatch") continue;

                string typeName = null, memberName = null;
                foreach (var arg in attr.ConstructorArguments)
                {
                    if (arg.Type.FullName == "System.Type" && arg.Value is TypeReference tr)
                        typeName = tr.FullName;
                    else if (arg.Type.FullName == "System.String" && arg.Value is string s && s.Length > 0)
                        memberName = s;
                }
                if (typeName != null || memberName != null)
                    result.Add(new HarmonyTarget(typeName, memberName));
            }
            return result;
        }

        static void RecordHarmony(Contract c, List<HarmonyTarget> classTargets,
                                  List<HarmonyTarget> methodTargets, string from, SequencePoint point)
        {
            // Class-level type + method-level member name is the common split, so merge the two
            // levels rather than treating them independently.
            var typeName = methodTargets.Select(t => t.TypeName).FirstOrDefault(x => x != null)
                        ?? classTargets.Select(t => t.TypeName).FirstOrDefault(x => x != null);
            var memberName = methodTargets.Select(t => t.MemberName).FirstOrDefault(x => x != null)
                          ?? classTargets.Select(t => t.MemberName).FirstOrDefault(x => x != null);

            if (typeName == null) return;
            if (!IsGameType(typeName)) return;

            var key = memberName == null ? typeName : $"{typeName}::#{memberName}";
            Add(c, key, new ContractUse
            {
                Via = memberName == null ? "harmony-patch" : "harmony-target-string",
                FromMember = from,
                SourceFile = point?.Document?.Url,
                SourceLine = point?.StartLine ?? 0,
            });
        }

        // ---- IL walk ----------------------------------------------------------------------

        static void WalkBody(Contract c, MethodDefinition method, string from)
        {
            var instrs = method.Body.Instructions;

            for (int i = 0; i < instrs.Count; i++)
            {
                var ins = instrs[i];
                var point = NearestPoint(method, instrs, i);

                switch (ins.Operand)
                {
                    case MethodReference mr when InGame(mr.DeclaringType):
                        // AccessTools.Method(typeof(X), "Name") names its target with a string
                        // literal, which is invisible as a metadata reference. Recovered below.
                        Add(c, $"{Root(mr.DeclaringType).FullName}::{Key(mr)}", new ContractUse
                        {
                            Via = "call",
                            FromMember = from,
                            SourceFile = point?.Document?.Url,
                            SourceLine = point?.StartLine ?? 0,
                        });
                        break;

                    case FieldReference fr when InGame(fr.DeclaringType):
                        Add(c, $"{Root(fr.DeclaringType).FullName}::{fr.FieldType.FullName} {fr.Name}", new ContractUse
                        {
                            Via = "field",
                            FromMember = from,
                            SourceFile = point?.Document?.Url,
                            SourceLine = point?.StartLine ?? 0,
                        });
                        break;

                    case TypeReference tr when InGame(tr):
                        Add(c, Root(tr).FullName, new ContractUse
                        {
                            Via = "type-ref",
                            FromMember = from,
                            SourceFile = point?.Document?.Url,
                            SourceLine = point?.StartLine ?? 0,
                        });
                        break;
                }

                // String-named reflection: AccessTools.Method/Field/Property/TypeByName(...).
                // None of this is expressible as a metadata reference, so it has to be
                // reconstructed from the literals feeding the call.
                if (ins.Operand is MethodReference call &&
                    call.DeclaringType?.Name == "AccessTools")
                {
                    var (typeName, memberName) = ResolveAccessTools(call, instrs, i);
                    if (typeName != null && IsGameType(typeName))
                    {
                        var key = memberName == null ? typeName : $"{typeName}::#{memberName}";
                        Add(c, key, new ContractUse
                        {
                            Via = "accesstools",
                            FromMember = from,
                            SourceFile = point?.Document?.Url,
                            SourceLine = point?.StartLine ?? 0,
                        });
                    }
                }
            }
        }

        /// <summary>
        /// Work out what an AccessTools call is asking for, from the literals feeding it.
        ///
        /// Three shapes occur in this codebase and they must be told apart, because guessing
        /// wrong silently mis-attributes the dependency to whatever type happened to be loaded
        /// nearby:
        ///
        ///   AccessTools.Field(typeof(DebugMenu), "timeManager")     ldtoken + 1 literal
        ///   AccessTools.TypeByName("TimeManager")                   0 ldtoken + 1 literal
        ///   AccessTools.Method(AccessTools.TypeByName("T"), "M")    0 ldtoken + 2 literals
        ///
        /// The nested form is the one that matters most: the type itself is resolved by name at
        /// runtime, so a rename in the game fails as a null at load rather than as a compile error.
        /// </summary>
        static (string TypeName, string MemberName) ResolveAccessTools(
            MethodReference call, Mono.Collections.Generic.Collection<Instruction> instrs, int callIndex)
        {
            // 12 instructions is well past the longest real argument sequence for these calls,
            // and short enough not to bleed into an unrelated statement.
            const int Window = 12;
            var floor = Math.Max(0, callIndex - Window);

            // Locate a nested TypeByName first. Its position is what disambiguates the operands:
            // the literal before it names the type, the literal after it names the member.
            // Without that split, an unrelated string earlier in the same statement gets read as
            // the type name.
            var nestedAt = -1;
            for (int i = callIndex - 1; i >= floor; i--)
            {
                if (instrs[i].Operand is MethodReference inner &&
                    inner.DeclaringType?.Name == "AccessTools" && inner.Name == "TypeByName")
                {
                    nestedAt = i;
                    break;
                }
            }

            if (call.Name == "TypeByName")
                return (Normalize(LastLiteral(instrs, floor, callIndex)), null);

            if (nestedAt >= 0)
            {
                var typeName = LastLiteral(instrs, Math.Max(0, nestedAt - Window), nestedAt);
                var memberName = LastLiteral(instrs, nestedAt + 1, callIndex);
                return (Normalize(typeName), memberName);
            }

            for (int i = callIndex - 1; i >= floor; i--)
            {
                if (instrs[i].OpCode == OpCodes.Ldtoken && instrs[i].Operand is TypeReference tr)
                    return (Root(tr).FullName, LastLiteral(instrs, floor, callIndex));
            }

            return (null, null);
        }

        /// <summary>Most recent string literal in [from, before).</summary>
        static string LastLiteral(Mono.Collections.Generic.Collection<Instruction> instrs, int from, int before)
        {
            for (int i = before - 1; i >= from; i--)
                if (instrs[i].OpCode == OpCodes.Ldstr && instrs[i].Operand is string s)
                    return s;
            return null;
        }

        /// <summary>
        /// Harmony spells nested types with '+' (the reflection convention); Cecil — and so the
        /// manifest — uses '/'. Without this, every nested-type lookup misses.
        /// </summary>
        static string Normalize(string typeName) => typeName?.Replace('+', '/');

        // ---- helpers ----------------------------------------------------------------------

        static string Key(MethodReference mr)
        {
            var ps = string.Join(",", mr.Parameters.Select(p => p.ParameterType.FullName));
            var generic = mr.HasGenericParameters ? "`" + mr.GenericParameters.Count : "";
            return $"{mr.ReturnType.FullName} {mr.Name}{generic}({ps})";
        }

        /// <summary>Strip arrays/byref/generic instantiation down to the underlying type.</summary>
        static TypeReference Root(TypeReference t)
        {
            while (true)
            {
                if (t is TypeSpecification spec && spec.ElementType != null) { t = spec.ElementType; continue; }
                return t;
            }
        }

        static bool InGame(TypeReference t)
        {
            if (t == null) return false;
            var root = Root(t);
            return root.Scope?.Name == GameAssembly
                || root.Scope?.Name == GameAssembly + ".dll";
        }

        /// <summary>
        /// Used for names recovered from attributes, where we have a string and no scope.
        /// The game's types are overwhelmingly in the global namespace, so anything without a
        /// dotted framework prefix is treated as game-owned; the differ discards keys that do
        /// not resolve against the manifest anyway.
        /// </summary>
        static bool IsGameType(string fullName) =>
            !fullName.StartsWith("System.", StringComparison.Ordinal) &&
            !fullName.StartsWith("UnityEngine.", StringComparison.Ordinal) &&
            !fullName.StartsWith("HarmonyLib.", StringComparison.Ordinal) &&
            !fullName.StartsWith("BepInEx.", StringComparison.Ordinal) &&
            !fullName.StartsWith("PunkMultiverse.", StringComparison.Ordinal) &&
            !fullName.StartsWith("LiteNetLib.", StringComparison.Ordinal);

        static SequencePoint FirstPoint(MethodDefinition m)
        {
            if (m.DebugInformation == null || !m.DebugInformation.HasSequencePoints) return null;
            return m.DebugInformation.SequencePoints.FirstOrDefault();
        }

        static SequencePoint NearestPoint(MethodDefinition m,
            Mono.Collections.Generic.Collection<Instruction> instrs, int i)
        {
            if (m.DebugInformation == null || !m.DebugInformation.HasSequencePoints) return null;
            // Most instructions have no sequence point of their own; the enclosing statement's
            // point is the preceding one.
            for (int j = i; j >= 0; j--)
            {
                var p = m.DebugInformation.GetSequencePoint(instrs[j]);
                if (p != null && !p.IsHidden) return p;
            }
            return null;
        }

        static void Add(Contract c, string key, ContractUse use)
        {
            if (!c.Uses.TryGetValue(key, out var list))
                c.Uses[key] = list = new List<ContractUse>();

            // The same call in the same statement can appear many times across a build; keep the
            // list to distinct sites so the report does not repeat itself.
            if (list.Any(u => u.FromMember == use.FromMember && u.SourceLine == use.SourceLine && u.Via == use.Via))
                return;
            list.Add(use);
        }

        sealed class NullResolver : IAssemblyResolver
        {
            public void Dispose() { }
            public AssemblyDefinition Resolve(AssemblyNameReference name) => null;
            public AssemblyDefinition Resolve(AssemblyNameReference name, ReaderParameters parameters) => null;
        }
    }
}
