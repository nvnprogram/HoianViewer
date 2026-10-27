using System;
using System.Collections.Generic;
using System.IO.Hashing;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using ShaderLibrary;

namespace ShaderBundler
{
    /// <summary>
    /// What an ubershader can express, read off the ubershader itself: the options its code
    /// reads from the option block and the samplers its programs bind. An option it never
    /// reads is fixed at its key row value, so a splice that sets it otherwise lacks that
    /// feature; a sampler no program binds is never sampled.
    /// </summary>
    public sealed class UberReach
    {
        /// <summary>Bumped when what is measured changes, so a stored result is redone.</summary>
        public const int Version = 1;

        //The uniform block location tables are relative to the first user bank, c[3].
        const int FirstUserBank = 3;

        /// <summary>Every static option at the value the ubershader's key row gives it.</summary>
        public Dictionary<string, string> KeyRow { get; } = new(StringComparer.Ordinal);

        /// <summary>Static options no program reads: their option block slot is never loaded,
        /// or they have none.</summary>
        public SortedSet<string> Unread { get; } = new(StringComparer.Ordinal);

        /// <summary>Samplers the ubershader declares and no program binds.</summary>
        public SortedSet<string> UnboundSamplers { get; } = new(StringComparer.Ordinal);

        /// <summary>Identity of the ubershader the result was measured on.</summary>
        public string Fingerprint { get; private set; }

        /// <summary>The programs whose two stages, translated, cover every distinct vertex and
        /// fragment code of the model once.</summary>
        public static List<int> CoveringPrograms(ShaderModel uber)
        {
            var seen = new HashSet<ulong>();
            var picked = new List<int>();
            for (int p = 0; p < uber.Programs.Count; p++)
            {
                var bp = uber.GetVariation(p)?.BinaryProgram;
                bool fresh = false;
                foreach (var code in new[] { bp?.VertexShader, bp?.FragmentShader })
                    if (code?.ByteCode != null && seen.Add(XxHash64.HashToUInt64(code.ByteCode)))
                        fresh = true;
                if (fresh)
                    picked.Add(p);
            }
            return picked;
        }

        /// <summary>Identity of an ubershader's code and of this measurement.</summary>
        public static string FingerprintOf(ShaderModel uber)
        {
            var h = new XxHash128();
            h.Append(BitConverter.GetBytes(Version));
            for (int p = 0; p < uber.Programs.Count; p++)
            {
                var bp = uber.GetVariation(p)?.BinaryProgram;
                foreach (var code in new[] { bp?.VertexShader, bp?.FragmentShader })
                    h.Append(BitConverter.GetBytes(code?.ByteCode == null ? 0UL : XxHash64.HashToUInt64(code.ByteCode)));
            }
            return Convert.ToHexString(h.GetCurrentHash()).ToLowerInvariant();
        }

        /// <summary>
        /// Measures an ubershader. <paramref name="translate"/> turns a program into its two
        /// stages' translated GLSL, in which the option block at location L reads as
        /// <c>vp_c{L+3}</c> or <c>fp_c{L+3}</c>. Returns null when a stage indexes the option
        /// block at run time, since what it reads cannot then be told.
        /// </summary>
        public static UberReach Measure(
            ShaderModel uber,
            Func<int, (string Vertex, string Fragment)> translate,
            Action<int, int> progress = null
        )
        {
            var r = new UberReach { Fingerprint = FingerprintOf(uber) };
            ReadKeyRow(uber, r.KeyRow);

            var slotOf = new Dictionary<int, string>();
            int block = uber.UniformBlocks.GetIndex(UberOptionTable.OptionBlockName);
            if (block >= 0)
            {
                var b = uber.UniformBlocks[block];
                for (int i = 0; i < b.Uniforms.Count; i++)
                    slotOf[b.Uniforms[i].DataOffset - 1] = b.Uniforms.GetKey(i);
            }

            var read = new HashSet<string>(StringComparer.Ordinal);
            var programs = CoveringPrograms(uber);
            for (int k = 0; k < programs.Count; k++)
            {
                int p = programs[k];
                var (vs, fs) = translate(p);
                var loc = block >= 0 ? uber.Programs[p].UniformBlockIndices[block] : null;
                foreach (var (src, prefix, location) in new[]
                {
                    (vs, "vp", loc?.VertexLocation ?? -1),
                    (fs, "fp", loc?.FragmentLocation ?? -1),
                })
                {
                    if (location < 0 || src == null)
                        continue;
                    var slots = Slots(src, prefix, location + FirstUserBank);
                    if (slots == null)
                        return null;
                    foreach (int s in slots)
                        if (slotOf.TryGetValue(s, out var name))
                            read.Add(name);
                }
                progress?.Invoke(k + 1, programs.Count);
            }

            foreach (var name in r.KeyRow.Keys)
                if (!read.Contains(name))
                    r.Unread.Add(name);

            for (int s = 0; s < uber.Samplers.Count; s++)
            {
                bool bound = false;
                for (int p = 0; p < uber.Programs.Count && !bound; p++)
                {
                    var l = uber.Programs[p].SamplerIndices[s];
                    bound = l.VertexLocation >= 0 || l.FragmentLocation >= 0;
                }
                if (!bound)
                    r.UnboundSamplers.Add(uber.Samplers.GetKey(s));
            }
            return r;
        }

        /// <summary>Byte offsets of a bank a translated stage loads, or null when it indexes the
        /// bank at run time.</summary>
        public static HashSet<int> Slots(string glsl, string prefix, int bank)
        {
            if (Regex.IsMatch(glsl, $@"\b{prefix}_c{bank}\.data\[(?!\d+\])"))
                return null;
            var set = new HashSet<int>();
            foreach (Match m in Regex.Matches(glsl, $@"\b{prefix}_c{bank}\.data\[(\d+)\]\.([xyzw])\b"))
                set.Add(int.Parse(m.Groups[1].Value) * 16 + "xyzw".IndexOf(m.Groups[2].Value[0]) * 4);
            return set;
        }

        /// <summary>The options of a vector that a splice cannot honour: never read by the
        /// ubershader and set away from its key row.</summary>
        public List<(string Option, string Value)> Lost(IReadOnlyDictionary<string, string> full)
        {
            var lost = new List<(string, string)>();
            foreach (var name in Unread)
                if (full.TryGetValue(name, out var v) && KeyRow.TryGetValue(name, out var k) && v != k)
                    lost.Add((name, v));
            return lost;
        }

        /// <summary>
        /// The unread options a shipped archive shows to change nothing: wherever two of its
        /// programs differ in the option alone, their code is the same. Such an option is
        /// handled outside the shader, so a splice loses nothing by fixing it.
        /// </summary>
        public HashSet<string> Inert(ShaderModel serving)
        {
            var inert = new HashSet<string>(StringComparer.Ordinal);
            int stride = serving.StaticKeyLength + serving.DynamicKeyLength;
            int count = serving.Programs.Count;
            if (stride == 0 || serving.KeyTable == null || serving.KeyTable.Length < count * stride)
                return inert;

            var code = new ulong[count];
            for (int p = 0; p < count; p++)
            {
                var bp = serving.GetVariation(p)?.BinaryProgram;
                ulong v = bp?.VertexShader?.ByteCode == null ? 0 : XxHash64.HashToUInt64(bp.VertexShader.ByteCode);
                ulong f = bp?.FragmentShader?.ByteCode == null ? 0 : XxHash64.HashToUInt64(bp.FragmentShader.ByteCode);
                code[p] = v * 31 + f;
            }

            var row = new int[stride];
            foreach (var name in Unread)
            {
                if (!serving.StaticOptions.ContainsKey(name))
                    continue;
                var o = serving.StaticOptions[name];
                //Programs keyed the same apart from this option, by choice.
                var groups = new Dictionary<UInt128, Dictionary<int, ulong>>();
                bool differs = false, paired = false;
                for (int p = 0; p < count && !differs; p++)
                {
                    Array.Copy(serving.KeyTable, p * stride, row, 0, stride);
                    int choice = o.GetChoiceIndex(row[o.Bit32Index]);
                    row[o.Bit32Index] &= (int)~o.Bit32Mask;
                    var key = XxHash128.HashToUInt128(System.Runtime.InteropServices.MemoryMarshal.AsBytes(row.AsSpan()));
                    if (!groups.TryGetValue(key, out var g))
                        groups[key] = g = new Dictionary<int, ulong>();
                    foreach (var kv in g)
                        if (kv.Key != choice)
                        {
                            paired = true;
                            if (kv.Value != code[p])
                                differs = true;
                        }
                    g.TryAdd(choice, code[p]);
                }
                if (paired && !differs)
                    inert.Add(name);
            }
            return inert;
        }

        static void ReadKeyRow(ShaderModel uber, Dictionary<string, string> into)
        {
            //Every program carries the same static row; program 0 stands for all of them.
            var options = uber.Programs.Count == 0 ? null : KeyRows.ReadOptions(uber, 0);
            if (options == null)
                return;
            for (int i = 0; i < uber.StaticOptions.Count; i++)
            {
                string name = uber.StaticOptions[i].Name;
                if (options.TryGetValue(name, out var v))
                    into[name] = v;
            }
        }

        public string ToText()
        {
            var sb = new StringBuilder();
            sb.Append("reach ").Append(Version).Append(' ').Append(Fingerprint).Append('\n');
            foreach (var kv in KeyRow)
                sb.Append("key ").Append(kv.Key).Append('=').Append(kv.Value).Append('\n');
            foreach (var n in Unread)
                sb.Append("unread ").Append(n).Append('\n');
            foreach (var n in UnboundSamplers)
                sb.Append("sampler ").Append(n).Append('\n');
            return sb.ToString();
        }

        /// <summary>A stored result, or null when it is for another ubershader or version.</summary>
        public static UberReach Parse(string text, string fingerprint)
        {
            var lines = text.Split('\n');
            if (lines.Length == 0 || lines[0] != $"reach {Version} {fingerprint}")
                return null;
            var r = new UberReach { Fingerprint = fingerprint };
            foreach (var line in lines.Skip(1))
            {
                int sp = line.IndexOf(' ');
                if (sp < 0)
                    continue;
                string kind = line.Substring(0, sp), rest = line.Substring(sp + 1);
                if (kind == "key")
                {
                    int eq = rest.IndexOf('=');
                    if (eq > 0)
                        r.KeyRow[rest.Substring(0, eq)] = rest.Substring(eq + 1);
                }
                else if (kind == "unread")
                    r.Unread.Add(rest);
                else if (kind == "sampler")
                    r.UnboundSamplers.Add(rest);
            }
            return r;
        }
    }
}
