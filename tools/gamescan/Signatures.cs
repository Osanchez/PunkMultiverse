using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace GameScan
{
    /// <summary>
    /// Stable keys and hashes for game members.
    ///
    /// Two rules drive everything here:
    ///   1. Keys must survive unrelated edits. We key on types and names, never on metadata
    ///      tokens or IL offsets, both of which shift when anything earlier in the file changes.
    ///   2. Hashes must ignore cosmetic churn but catch semantic change. Branch targets are
    ///      recorded as instruction INDEXES rather than byte offsets for exactly this reason.
    /// </summary>
    public static class Signatures
    {
        public static string Sha256(string s)
        {
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(s));
            // 16 hex chars is ample: this is change detection, not cryptographic commitment,
            // and short hashes keep the committed baseline readable in diffs.
            return Convert.ToHexString(bytes, 0, 8).ToLowerInvariant();
        }

        public static string TypeKey(TypeDefinition t) => t.FullName;

        public static string MemberKey(MethodDefinition m)
        {
            var ps = string.Join(",", m.Parameters.Select(p => p.ParameterType.FullName));
            var generic = m.HasGenericParameters ? "`" + m.GenericParameters.Count : "";
            return $"{m.ReturnType.FullName} {m.Name}{generic}({ps})";
        }

        public static string MemberKey(FieldDefinition f) => $"{f.FieldType.FullName} {f.Name}";

        public static string MemberKey(PropertyDefinition p)
        {
            var ps = p.HasParameters ? "[" + string.Join(",", p.Parameters.Select(x => x.ParameterType.FullName)) + "]" : "";
            return $"{p.PropertyType.FullName} {p.Name}{ps}";
        }

        public static string MemberKey(EventDefinition e) => $"{e.EventType.FullName} {e.Name}";

        // ---- signature hashes -------------------------------------------------------------

        public static string SigHash(MethodDefinition m)
        {
            var sb = new StringBuilder();
            sb.Append(MemberKey(m)).Append('|');
            sb.Append(m.IsStatic ? "static " : "");
            sb.Append(m.IsVirtual ? "virtual " : "");
            sb.Append(m.IsAbstract ? "abstract " : "");
            sb.Append(Visibility(m)).Append('|');
            // Parameter modifiers are part of the calling convention a patch must match.
            foreach (var p in m.Parameters)
                sb.Append(p.IsOut ? "out " : "").Append(p.ParameterType.IsByReference ? "ref " : "")
                  .Append(p.Name).Append(',');
            return Sha256(sb.ToString());
        }

        public static string SigHash(FieldDefinition f)
        {
            var sb = new StringBuilder();
            sb.Append(MemberKey(f)).Append('|');
            sb.Append(f.IsStatic ? "static " : "");
            sb.Append(f.IsInitOnly ? "readonly " : "");
            sb.Append(f.IsLiteral ? "const " : "");
            sb.Append(Visibility(f)).Append('|');
            // The constant value matters enormously: this is what makes a silently renumbered
            // enum member (CellType.Hazard 5 -> 6) show up as a change. Nothing else would catch it.
            if (f.HasConstant)
                sb.Append("=").Append(Convert.ToString(f.Constant, CultureInfo.InvariantCulture));
            return Sha256(sb.ToString());
        }

        public static string SigHash(PropertyDefinition p)
        {
            var sb = new StringBuilder();
            sb.Append(MemberKey(p)).Append('|');
            sb.Append(p.GetMethod != null ? "get;" : "").Append(p.SetMethod != null ? "set;" : "");
            return Sha256(sb.ToString());
        }

        public static string SigHash(EventDefinition e) => Sha256(MemberKey(e));

        static string Visibility(MethodDefinition m) =>
            m.IsPublic ? "public" : m.IsFamily ? "protected" : m.IsAssembly ? "internal" : "private";

        static string Visibility(FieldDefinition f) =>
            f.IsPublic ? "public" : f.IsFamily ? "protected" : f.IsAssembly ? "internal" : "private";

        // ---- IL body hash -----------------------------------------------------------------

        /// <summary>
        /// Hash of the normalized instruction stream. Returns null when there is no body
        /// (abstract, extern, interface members).
        /// </summary>
        public static string BodyHash(MethodDefinition m, out int ilCount)
        {
            ilCount = 0;
            if (!m.HasBody || m.Body == null) return null;

            var instrs = m.Body.Instructions;
            ilCount = instrs.Count;

            // Branch operands are Instruction references. Recording their byte offset would make
            // the hash change whenever anything earlier in the method grows or shrinks, even if
            // control flow is identical. Index is stable against that.
            var index = new Dictionary<Instruction, int>(instrs.Count);
            for (int i = 0; i < instrs.Count; i++) index[instrs[i]] = i;

            var sb = new StringBuilder(instrs.Count * 24);
            foreach (var ins in instrs)
            {
                sb.Append(ins.OpCode.Name);
                var op = OperandText(ins.Operand, index);
                if (op.Length > 0) sb.Append(' ').Append(op);
                sb.Append('\n');
            }

            // Exception handlers are real control flow; a try/catch appearing or vanishing is a
            // behaviour change even when the instruction stream is otherwise similar.
            foreach (var h in m.Body.ExceptionHandlers)
            {
                sb.Append("EH ").Append(h.HandlerType)
                  .Append(' ').Append(h.CatchType?.FullName ?? "-")
                  .Append(' ').Append(h.TryStart != null && index.TryGetValue(h.TryStart, out var ts) ? ts : -1)
                  .Append(' ').Append(h.TryEnd != null && index.TryGetValue(h.TryEnd, out var te) ? te : -1)
                  .Append(' ').Append(h.HandlerStart != null && index.TryGetValue(h.HandlerStart, out var hs) ? hs : -1)
                  .Append('\n');
            }

            return Sha256(sb.ToString());
        }

        static string OperandText(object operand, Dictionary<Instruction, int> index)
        {
            switch (operand)
            {
                case null:
                    return "";
                case Instruction target:
                    return "@" + (index.TryGetValue(target, out var i) ? i : -1);
                case Instruction[] targets:
                    return "@[" + string.Join(",", targets.Select(t => index.TryGetValue(t, out var j) ? j : -1)) + "]";
                case VariableDefinition v:
                    return "loc" + v.Index;
                case ParameterDefinition p:
                    return "arg" + p.Index;
                case MethodReference mr:
                    return mr.FullName;
                case FieldReference fr:
                    return fr.FullName;
                case TypeReference tr:
                    return tr.FullName;
                case CallSite cs:
                    return cs.FullName;
                case string s:
                    // String literals are behaviour: error text, prefab ids, config keys.
                    return "\"" + s.Replace("\n", "\\n") + "\"";
                case float f:
                    return f.ToString("R", CultureInfo.InvariantCulture);
                case double d:
                    return d.ToString("R", CultureInfo.InvariantCulture);
                default:
                    return Convert.ToString(operand, CultureInfo.InvariantCulture) ?? "";
            }
        }

        // ---- misc -------------------------------------------------------------------------

        public static bool IsCompilerGenerated(ICustomAttributeProvider p, string name)
        {
            if (name.Contains('<') || name.Contains('$')) return true;
            return p.HasCustomAttributes && p.CustomAttributes.Any(a =>
                a.AttributeType.FullName == "System.Runtime.CompilerServices.CompilerGeneratedAttribute");
        }

        public static string KindOf(TypeDefinition t)
        {
            if (t.IsEnum) return "enum";
            if (t.IsInterface) return "interface";
            if (t.BaseType?.FullName == "System.MulticastDelegate") return "delegate";
            if (t.IsValueType) return "struct";
            return "class";
        }
    }
}
