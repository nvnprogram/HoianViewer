using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace ShaderBundler.Archive
{
    /// <summary>
    /// A NintendoWare binary under construction: the bytes, every pointer slot in them, and the
    /// relocation table the runtime patches those slots from. The one builder the container
    /// writers share, with <see cref="NwStringPool"/> and <see cref="NwDict"/>.
    /// </summary>
    public sealed class NwImage
    {
        byte[] _buf = new byte[1 << 16];
        readonly List<Slot> _slots = new();

        public int Length { get; private set; }

        readonly struct Slot
        {
            public readonly int At;
            public readonly Func<long> Target;
            public readonly bool RegisterNull;

            public Slot(int at, Func<long> target, bool registerNull)
            {
                At = at;
                Target = target;
                RegisterNull = registerNull;
            }
        }

        public static int AlignUp(int v, int a) => (v + a - 1) / a * a;

        public void Align(int a) => Grow(AlignUp(Length, a) - Length);

        /// <summary>Appends zeroed space and returns where it starts.</summary>
        public int Alloc(int size, int align = 1)
        {
            Align(align);
            int at = Length;
            Grow(size);
            return at;
        }

        public int Append(ReadOnlySpan<byte> data, int align = 1)
        {
            int at = Alloc(data.Length, align);
            data.CopyTo(_buf.AsSpan(at));
            return at;
        }

        void Grow(int n)
        {
            if (n <= 0)
                return;
            int need = Length + n;
            if (need > _buf.Length)
                Array.Resize(ref _buf, Math.Max(need, _buf.Length * 2));
            Length = need;
        }

        public void Put(int at, ReadOnlySpan<byte> data) => data.CopyTo(_buf.AsSpan(at));

        public void U8(int at, byte v) => _buf[at] = v;

        public void U16(int at, ushort v) =>
            BinaryPrimitives.WriteUInt16LittleEndian(_buf.AsSpan(at), v);

        public void U32(int at, uint v) =>
            BinaryPrimitives.WriteUInt32LittleEndian(_buf.AsSpan(at), v);

        public void U64(int at, ulong v) =>
            BinaryPrimitives.WriteUInt64LittleEndian(_buf.AsSpan(at), v);

        /// <summary>A pointer slot whose target is known once the layout is done. A null
        /// target is listed in the relocation table only when the format lists it.</summary>
        public void Ptr(int at, Func<long> target, bool registerNull = true) =>
            _slots.Add(new Slot(at, target, registerNull));

        public void Ptr(int at, long target, bool registerNull = true) =>
            Ptr(at, () => target, registerNull);

        /// <summary>Writes every pointer and returns the slots the relocation table lists, with
        /// their targets.</summary>
        public List<(int At, long Target)> Resolve()
        {
            var listed = new List<(int, long)>(_slots.Count);
            foreach (var s in _slots)
            {
                long t = s.Target();
                U64(s.At, (ulong)t);
                if (t != 0 || s.RegisterNull)
                    listed.Add((s.At, t));
            }
            return listed;
        }

        /// <summary>
        /// Appends the relocation table. A pointer belongs to the section its target lies in,
        /// a null one to the first section, and each section's slots are grouped into entries.
        /// </summary>
        public int WriteRelocationTable(
            IReadOnlyList<(int Offset, int Size)> sections,
            List<(int At, long Target)> pointers
        )
        {
            var perSection = new List<int>[sections.Count];
            for (int i = 0; i < sections.Count; i++)
                perSection[i] = new List<int>();
            foreach (var (at, target) in pointers)
                perSection[SectionOf(sections, target)].Add(at);

            var entries = new List<(int Offset, int Structs, int Offsets, int Pads)>();
            var ranges = new List<(int First, int Count)>();
            foreach (var list in perSection)
            {
                list.Sort();
                int first = entries.Count;
                entries.AddRange(Group(list));
                ranges.Add((first, entries.Count - first));
            }

            int at0 = Alloc(0x10 + sections.Count * 0x18 + entries.Count * 8, 8);
            Put(at0, "_RLT"u8);
            U32(at0 + 4, (uint)at0);
            U32(at0 + 8, (uint)sections.Count);
            for (int i = 0; i < sections.Count; i++)
            {
                int s = at0 + 0x10 + i * 0x18;
                U32(s + 8, (uint)sections[i].Offset);
                U32(s + 12, (uint)sections[i].Size);
                U32(s + 16, (uint)ranges[i].First);
                U32(s + 20, (uint)ranges[i].Count);
            }
            int e = at0 + 0x10 + sections.Count * 0x18;
            foreach (var en in entries)
            {
                U32(e, (uint)en.Offset);
                U16(e + 4, (ushort)en.Structs);
                U8(e + 6, (byte)en.Offsets);
                U8(e + 7, (byte)en.Pads);
                e += 8;
            }
            return at0;
        }

        static int SectionOf(IReadOnlyList<(int Offset, int Size)> sections, long target)
        {
            if (target == 0)
                return 0;
            for (int i = 0; i < sections.Count; i++)
                if (target >= sections[i].Offset && target < sections[i].Offset + sections[i].Size)
                    return i;
            //A pointer one past a section's end, at an empty array, still belongs to it.
            for (int i = sections.Count - 1; i >= 0; i--)
                if (target == sections[i].Offset + sections[i].Size && sections[i].Size > 0)
                    return i;
            throw new InvalidOperationException($"pointer target 0x{target:x} lies in no section");
        }

        /// <summary>
        /// Groups sorted slots the way the shipped files do: runs of adjacent slots, and a run
        /// joined by the next free run of the same length at a fixed stride, for as long as
        /// that stride keeps finding one.
        /// </summary>
        static List<(int, int, int, int)> Group(List<int> slots)
        {
            var runs = new List<(int Start, int Len)>();
            foreach (int p in slots)
            {
                if (runs.Count > 0 && runs[^1].Start + runs[^1].Len * 8 == p && runs[^1].Len < 255)
                    runs[^1] = (runs[^1].Start, runs[^1].Len + 1);
                else
                    runs.Add((p, 1));
            }

            var byStart = new Dictionary<int, int>(runs.Count);
            for (int i = 0; i < runs.Count; i++)
                byStart[runs[i].Start] = i;
            var used = new bool[runs.Count];
            var outList = new List<(int, int, int, int)>();
            for (int i = 0; i < runs.Count; i++)
            {
                if (used[i])
                    continue;
                used[i] = true;
                var (p, n) = runs[i];
                int structs = 1,
                    pads = 0;
                int j = i + 1;
                while (j < runs.Count && (used[j] || runs[j].Len != n))
                    j++;
                if (j < runs.Count)
                {
                    int stride = (runs[j].Start - p) / 8;
                    if (stride - n <= 255)
                    {
                        pads = stride - n;
                        int q = runs[j].Start;
                        while (
                            structs < 0xffff
                            && byStart.TryGetValue(q, out int k)
                            && !used[k]
                            && runs[k].Len == n
                        )
                        {
                            used[k] = true;
                            structs++;
                            q += stride * 8;
                        }
                    }
                }
                outList.Add((p, structs, n, structs > 1 ? pads : 0));
            }
            outList.Sort((a, b) => a.Item1.CompareTo(b.Item1));
            return outList;
        }

        public byte[] ToArray() => _buf.AsSpan(0, Length).ToArray();
    }

    /// <summary>The order a string pool keeps its strings in.</summary>
    public enum NwStringOrder
    {
        /// <summary>The key the dictionaries sort by, the string read backwards bit by bit, as
        /// bfsha and BNSH files have it.</summary>
        KeyOrder,

        /// <summary>The order the strings were first added in, after the empty one, as BNTX
        /// files have it.</summary>
        FirstAdded,
    }

    /// <summary>
    /// The string pool block: every name the file points at, once each, the empty string first.
    /// </summary>
    public sealed class NwStringPool
    {
        readonly NwStringOrder _order;
        readonly HashSet<string> _seen = new(StringComparer.Ordinal) { "" };
        readonly List<string> _strings = new() { "" };
        readonly Dictionary<string, int> _offsets = new(StringComparer.Ordinal);

        public NwStringPool(NwStringOrder order = NwStringOrder.KeyOrder) => _order = order;

        public void Add(string s)
        {
            if (_seen.Add(s ?? ""))
                _strings.Add(s ?? "");
        }

        /// <summary>Where the string's length prefix is, once the pool is written.</summary>
        public long this[string s] => _offsets[s ?? ""];

        /// <summary>Appends the pool and returns the block's offset. Its size field is patched
        /// by the caller, since it runs to the relocation table.</summary>
        public int Write(NwImage img)
        {
            var ordered = _strings;
            if (_order == NwStringOrder.KeyOrder)
            {
                var keyed = _strings.Select(s => (s, Key(s))).ToList();
                keyed.Sort((a, b) => a.Item2.AsSpan().SequenceCompareTo(b.Item2));
                ordered = keyed.Select(k => k.s).ToList();
            }
            int at = img.Alloc(0x14, 8);
            img.Put(at, "_STR"u8);
            img.U32(at + 0x10, (uint)(ordered.Count - 1));
            foreach (var s in ordered)
            {
                var bytes = Encoding.UTF8.GetBytes(s);
                int o = img.Alloc(2 + bytes.Length + 1, 2);
                img.U16(o, (ushort)bytes.Length);
                img.Put(o + 2, bytes);
                _offsets[s] = o;
            }
            img.Align(2);
            return at;
        }

        static readonly byte[] Rev = Enumerable
            .Range(0, 256)
            .Select(b =>
            {
                int r = 0;
                for (int i = 0; i < 8; i++)
                    r |= (b >> i & 1) << (7 - i);
                return (byte)r;
            })
            .ToArray();

        static byte[] Key(string s)
        {
            var b = Encoding.UTF8.GetBytes(s);
            var k = new byte[b.Length];
            for (int i = 0; i < b.Length; i++)
                k[i] = Rev[b[b.Length - 1 - i]];
            return k;
        }
    }

    /// <summary>The Patricia trie dictionary the format indexes names with.</summary>
    public static class NwDict
    {
        public static int Size(int count) => 8 + (count + 1) * 16;

        /// <summary>Writes a dictionary of the keys in their order and returns its offset. Some
        /// formats start it with its signature and some leave the field zero.</summary>
        public static int Write(
            NwImage img,
            NwStringPool pool,
            IReadOnlyList<string> keys,
            bool signed = false
        )
        {
            foreach (var k in keys)
                pool.Add(k);
            var nodes = ShaderLibrary.ResDictUpdate.UpdateNodes(keys.ToList());
            int at = img.Alloc(Size(keys.Count), 8);
            if (signed)
                img.Put(at, "_DIC"u8);
            img.U32(at + 4, (uint)keys.Count);
            for (int i = 0; i < nodes.Length; i++)
            {
                int n = at + 8 + i * 16;
                img.U32(n, nodes[i].Reference);
                img.U16(n + 4, nodes[i].IdxLeft);
                img.U16(n + 6, nodes[i].IdxRight);
                string key = i == 0 ? "" : nodes[i].Key;
                img.Ptr(n + 8, () => pool[key]);
            }
            return at;
        }
    }
}
