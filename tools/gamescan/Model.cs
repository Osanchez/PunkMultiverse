using System.Collections.Generic;

namespace GameScan
{
    /// <summary>
    /// On-disk manifest of the game assembly. One of these is committed as the baseline;
    /// a fresh one is produced after every game update and diffed against it.
    /// </summary>
    public sealed class Manifest
    {
        /// <summary>Bumped when the hashing scheme changes, which invalidates old baselines.</summary>
        public int FormatVersion { get; set; } = 1;

        public AssemblyInfo Assembly { get; set; }
        public Dictionary<string, TypeEntry> Types { get; set; } = new();
    }

    public sealed class AssemblyInfo
    {
        public string Name { get; set; }
        public string Version { get; set; }
        /// <summary>Module version id — changes on every recompile, so it is a cheap "is this the same build" check.</summary>
        public string Mvid { get; set; }
        public string FileSha256 { get; set; }
        public long FileSize { get; set; }
        /// <summary>Game build string, supplied by the caller (Steam does not put it in the assembly).</summary>
        public string GameVersion { get; set; }
        public string CapturedAtUtc { get; set; }
    }

    public sealed class TypeEntry
    {
        public string Kind { get; set; }            // class | struct | interface | enum | delegate
        public string BaseType { get; set; }
        public List<string> Interfaces { get; set; } = new();
        public bool IsAbstract { get; set; }
        public bool IsSealed { get; set; }
        public bool IsPublic { get; set; }
        /// <summary>True for lambda closures, iterator state machines, etc. Their names churn between
        /// compiles without meaning anything, so the report de-prioritises them.</summary>
        public bool CompilerGenerated { get; set; }

        /// <summary>Hash of the type header plus every member signature hash — a fast "did anything
        /// about this type's shape change" check.</summary>
        public string ShapeHash { get; set; }

        public Dictionary<string, MemberEntry> Members { get; set; } = new();
    }

    public sealed class MemberEntry
    {
        public string Kind { get; set; }            // method | field | property | event

        /// <summary>Covers name, parameter and return types, static/virtual/abstract, visibility,
        /// and — critically — the constant value of literal fields, so a silently renumbered enum
        /// is caught. A change here breaks compilation or Harmony patching outright.</summary>
        public string SigHash { get; set; }

        /// <summary>Normalized IL opcode+operand stream. Null for members without a body.
        /// A change here with an unchanged SigHash is a BEHAVIOUR change under a stable
        /// signature — the failure mode Harmony cannot warn about.</summary>
        public string BodyHash { get; set; }

        public int IlCount { get; set; }
        public bool CompilerGenerated { get; set; }
    }

    /// <summary>
    /// What the mod actually depends on, extracted from the compiled mod assembly's IL.
    /// </summary>
    public sealed class Contract
    {
        public int FormatVersion { get; set; } = 1;
        public string ModAssembly { get; set; }
        public string ModVersion { get; set; }
        public string CapturedAtUtc { get; set; }

        /// <summary>Key: "TypeName" or "TypeName::memberKey". Value: where in the mod it is used.</summary>
        public Dictionary<string, List<ContractUse>> Uses { get; set; } = new();
    }

    public sealed class ContractUse
    {
        /// <summary>harmony-patch | harmony-target-string | accesstools | call | field | type-ref</summary>
        public string Via { get; set; }
        public string FromMember { get; set; }
        public string SourceFile { get; set; }
        public int SourceLine { get; set; }
    }
}
