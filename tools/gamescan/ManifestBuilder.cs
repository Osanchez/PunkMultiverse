using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Mono.Cecil;

namespace GameScan
{
    public static class ManifestBuilder
    {
        public static Manifest Build(string dllPath, string gameVersion)
        {
            using var asm = AssemblyDefinition.ReadAssembly(dllPath, new ReaderParameters
            {
                ReadingMode = ReadingMode.Immediate,
                // No symbols and no resolution: we hash what is written in this file, and never
                // need to follow a reference out of it. Resolution would also fail here, since
                // the Unity assemblies are not on the tool's probing path.
                ReadSymbols = false,
                AssemblyResolver = new NullResolver(),
            });

            var module = asm.MainModule;
            var fi = new FileInfo(dllPath);

            var manifest = new Manifest
            {
                Assembly = new AssemblyInfo
                {
                    Name = asm.Name.Name,
                    Version = asm.Name.Version.ToString(),
                    Mvid = module.Mvid.ToString(),
                    FileSha256 = FileHash(dllPath),
                    FileSize = fi.Length,
                    GameVersion = string.IsNullOrWhiteSpace(gameVersion) ? "unknown" : gameVersion,
                    CapturedAtUtc = DateTime.UtcNow.ToString("O"),
                },
            };

            foreach (var type in AllTypes(module))
            {
                // <Module> is a synthetic container, never something a mod patches.
                if (type.FullName == "<Module>") continue;

                var entry = new TypeEntry
                {
                    Kind = Signatures.KindOf(type),
                    BaseType = type.BaseType?.FullName,
                    Interfaces = type.Interfaces.Select(i => i.InterfaceType.FullName).OrderBy(x => x, StringComparer.Ordinal).ToList(),
                    IsAbstract = type.IsAbstract,
                    IsSealed = type.IsSealed,
                    IsPublic = type.IsPublic || type.IsNestedPublic,
                    CompilerGenerated = Signatures.IsCompilerGenerated(type, type.Name),
                };

                foreach (var m in type.Methods)
                {
                    var body = Signatures.BodyHash(m, out var il);
                    entry.Members[Signatures.MemberKey(m)] = new MemberEntry
                    {
                        Kind = "method",
                        SigHash = Signatures.SigHash(m),
                        BodyHash = body,
                        IlCount = il,
                        CompilerGenerated = Signatures.IsCompilerGenerated(m, m.Name),
                    };
                }

                foreach (var f in type.Fields)
                {
                    entry.Members[Signatures.MemberKey(f)] = new MemberEntry
                    {
                        Kind = "field",
                        SigHash = Signatures.SigHash(f),
                        CompilerGenerated = Signatures.IsCompilerGenerated(f, f.Name),
                    };
                }

                // Properties and events carry no IL of their own — their accessors are already
                // recorded as methods above. They are listed so the API index can render them
                // as properties rather than as get_/set_ pairs.
                foreach (var p in type.Properties)
                {
                    entry.Members[Signatures.MemberKey(p)] = new MemberEntry
                    {
                        Kind = "property",
                        SigHash = Signatures.SigHash(p),
                        CompilerGenerated = Signatures.IsCompilerGenerated(p, p.Name),
                    };
                }

                foreach (var e in type.Events)
                {
                    entry.Members[Signatures.MemberKey(e)] = new MemberEntry
                    {
                        Kind = "event",
                        SigHash = Signatures.SigHash(e),
                        CompilerGenerated = Signatures.IsCompilerGenerated(e, e.Name),
                    };
                }

                entry.ShapeHash = ShapeHash(entry);
                manifest.Types[Signatures.TypeKey(type)] = entry;
            }

            return manifest;
        }

        static string ShapeHash(TypeEntry t)
        {
            var sb = new StringBuilder();
            sb.Append(t.Kind).Append('|').Append(t.BaseType).Append('|')
              .Append(string.Join(",", t.Interfaces)).Append('|')
              .Append(t.IsAbstract).Append(t.IsSealed).Append(t.IsPublic).Append('\n');
            foreach (var kv in t.Members.OrderBy(k => k.Key, StringComparer.Ordinal))
                sb.Append(kv.Key).Append('=').Append(kv.Value.SigHash).Append('\n');
            return Signatures.Sha256(sb.ToString());
        }

        /// <summary>Nested types are real patch targets (Unit.Data, Enemy.Data …), so recurse.</summary>
        public static IEnumerable<TypeDefinition> AllTypes(ModuleDefinition module)
        {
            foreach (var t in module.Types)
                foreach (var x in Walk(t))
                    yield return x;
        }

        static IEnumerable<TypeDefinition> Walk(TypeDefinition t)
        {
            yield return t;
            foreach (var n in t.NestedTypes)
                foreach (var x in Walk(n))
                    yield return x;
        }

        static string FileHash(string path)
        {
            using var fs = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
        }

        /// <summary>
        /// Cecil resolves references lazily; anything that triggers a resolve on a Unity type we
        /// do not have would throw. Nothing in the manifest path needs resolution, so refuse it
        /// loudly-but-harmlessly rather than dragging in the whole Managed folder.
        /// </summary>
        sealed class NullResolver : IAssemblyResolver
        {
            public void Dispose() { }
            public AssemblyDefinition Resolve(AssemblyNameReference name) => null;
            public AssemblyDefinition Resolve(AssemblyNameReference name, ReaderParameters parameters) => null;
        }
    }
}
