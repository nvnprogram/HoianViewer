using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using ShaderLibrary;

namespace ShaderBundler.Archive
{
    /// <summary>
    /// Where a BNSH puts its shared blobs.
    /// </summary>
    public sealed class BnshPlacement
    {
        /// <summary>One user of each code blob, in the order the blobs are stored.</summary>
        public readonly List<(int Variation, int Stage)> CodeOrder = new();

        /// <summary>The program stages whose block holds the control they use.</summary>
        public readonly HashSet<(int Variation, int Stage)> ControlOwners = new();

        /// <summary>Reads the placement out of a BNSH.</summary>
        public static BnshPlacement Read(ReadOnlySpan<byte> bnsh)
        {
            var p = new BnshPlacement();
            int n = (int)U32(bnsh, 0x7c);
            long vars = (long)U64(bnsh, 0x80);
            var progStarts = new List<(long Start, int Variation)>();
            var codeUser = new Dictionary<ulong, (int, int)>();
            var ctlUsers = new Dictionary<ulong, List<(int, int)>>();
            for (int i = 0; i < n; i++)
            {
                long prog = (long)U64(bnsh, (int)(vars + i * 0x40 + 0x10));
                progStarts.Add((prog, i));
                for (int s = 0; s < 6; s++)
                {
                    long st = (long)U64(bnsh, (int)(prog + 8 + s * 8));
                    if (st == 0)
                        continue;
                    ulong ctl = U64(bnsh, (int)st + 8);
                    ulong code = U64(bnsh, (int)st + 0x10);
                    codeUser.TryAdd(code, (i, s));
                    if (!ctlUsers.TryGetValue(ctl, out var users))
                        ctlUsers[ctl] = users = new List<(int, int)>();
                    users.Add((i, s));
                }
            }
            foreach (var kv in codeUser.OrderBy(kv => kv.Key))
                p.CodeOrder.Add(kv.Value);

            progStarts.Sort((a, b) => a.Start.CompareTo(b.Start));
            var starts = progStarts.Select(x => x.Start).ToArray();
            foreach (var kv in ctlUsers)
            {
                int k = Array.BinarySearch(starts, (long)kv.Key);
                if (k < 0)
                    k = ~k - 1;
                int owner = progStarts[k].Variation;
                var user = kv.Value.First(u => u.Item1 == owner);
                p.ControlOwners.Add(user);
            }
            return p;
        }

        static uint U32(ReadOnlySpan<byte> d, int o) =>
            BinaryPrimitives.ReadUInt32LittleEndian(d[o..]);

        static ulong U64(ReadOnlySpan<byte> d, int o) =>
            BinaryPrimitives.ReadUInt64LittleEndian(d[o..]);
    }

    /// <summary>
    /// Writes a BNSH, the container a bfsha model keeps its programs in, in the layout the
    /// shipped files of its version have: 2.1 (NVN) or 2.4 (NVN2).
    /// </summary>
    public static class BnshWriter
    {
        const int CodeAlign = 0x100;
        const int PoolAlign = 0x1000;

        const int StageHeaderSize = 0x40;
        const int ProgramHeaderSize = 0xa0;
        const int ReflectionSize = 0x40;
        const int StageReflectionSize = 0x60;
        const int StageInfoSize = 0x18;
        const int MemoryPoolSize = 0x50;
        const int MemoryPoolStorageSize = 0x140;

        /// <summary>What the layout of a version depends on.</summary>
        /// <param name="StageInfo">Whether each stage points at a block of its own.</param>
        /// <param name="PoolFlags">The memory pool's flags.</param>
        /// <param name="PoolSlack">Spare room the code pool keeps past the last program.</param>
        /// <param name="PoolSizeTwice">Whether the pool repeats its size after the pointer.</param>
        readonly record struct Layout(
            bool StageInfo,
            uint PoolFlags,
            int PoolSlack,
            bool PoolSizeTwice
        );

        //The 2.1 slack is a value inside the range every shipped file allows.
        static readonly Layout V21 = new(false, 0x61, 0x500, false);
        static readonly Layout V24 = new(true, 0x62, 0x800, true);

        static Layout LayoutOf(BinaryHeader h)
        {
            if (h.VersionMajor == 2 && h.VersionMinor >= 4)
                return V24;
            if (h.VersionMajor == 2 && h.VersionMinor == 1)
                return V21;
            throw new NotSupportedException(
                $"BNSH {h.VersionMajor}.{h.VersionMinor}.{h.VersionMicro}"
            );
        }

        public static byte[] Write(BnshFile bnsh, BnshPlacement placement = null)
        {
            var layout = LayoutOf(bnsh.BinHeader);
            var programs = bnsh.Variations.Select(v => v.BinaryProgram).ToList();
            int n = programs.Count;
            var img = new NwImage();
            var strings = new NwStringPool();

            int hdr = img.Alloc(0x60);
            int grsc = img.Alloc(0x60);
            int vars = img.Alloc(n * 0x40);

            //Unique blobs, and who stores each.
            var codeBlobs = new BlobSet();
            var ctlBlobs = new BlobSet();
            var stages = new BnshFile.ShaderCode[n, 6];
            for (int i = 0; i < n; i++)
            {
                var sc = Stages(programs[i]);
                for (int s = 0; s < 6; s++)
                {
                    stages[i, s] = sc[s];
                    if (sc[s] == null)
                        continue;
                    if (sc[s].ByteCode == null || sc[s].ControlCode == null)
                        throw new InvalidOperationException(
                            $"variation {i} stage {s}: missing code or control"
                        );
                    codeBlobs.Use(sc[s].ByteCode, (i, s));
                    ctlBlobs.Use(sc[s].ControlCode, (i, s));
                }
            }

            var ctlOwner = new HashSet<(int, int)>();
            foreach (var blob in ctlBlobs.All)
            {
                var owner =
                    placement != null
                        ? blob.Users.FirstOrDefault(
                            u => placement.ControlOwners.Contains(u),
                            blob.Users[0]
                        )
                        : blob.Users[0];
                ctlOwner.Add(owner);
            }

            var ctlAt = new Dictionary<Blob, int>();
            var progAt = new int[n];
            var stageAt = new int[n, 6];
            for (int i = 0; i < n; i++)
            {
                var bp = programs[i];
                int prog = img.Alloc(ProgramHeaderSize, 8);
                progAt[i] = prog;
                WriteProgramHeader(img, prog, bp.header);

                for (int s = 0; s < 6; s++)
                {
                    if (stages[i, s] == null)
                        continue;
                    int st = img.Alloc(StageHeaderSize, 8);
                    stageAt[i, s] = st;
                    img.Ptr(prog + 8 + s * 8, st);
                }
                for (int s = 0; s < 6; s++)
                    if (stages[i, s] == null)
                        img.Ptr(prog + 8 + s * 8, 0, registerNull: false);

                var reflections = Reflections(bp);
                int refl = 0;
                if (bp.header.ShaderReflectionOffset != 0 || reflections.Any(r => r != null))
                {
                    refl = img.Alloc(ReflectionSize, 8);
                    img.Ptr(prog + 0x78, refl);
                }
                else
                    img.Ptr(prog + 0x78, 0, registerNull: false);

                for (int s = 0; s < 6; s++)
                {
                    var sc = stages[i, s];
                    if (sc == null)
                        continue;
                    int st = stageAt[i, s];
                    var ctl = ctlBlobs.Find(sc.ControlCode);
                    if (ctlOwner.Contains((i, s)))
                        ctlAt[ctl] = img.Append(sc.ControlCode, 8);
                    WriteStageHeader(img, st, sc);
                    if (layout.StageInfo)
                    {
                        int info = img.Alloc(StageInfoSize, 8);
                        if (sc.StageInfo != null)
                            img.Put(info, sc.StageInfo.AsSpan(0, StageInfoSize));
                        img.Ptr(st + 0x30, info);
                    }
                }

                if (refl != 0)
                    WriteReflection(img, strings, refl, reflections, i);
            }

            int pool = img.Alloc(MemoryPoolSize, 8);
            int storage = img.Alloc(MemoryPoolStorageSize, 8);
            int section0End = img.Length;

            int objStart = img.Length;
            var objAt = new int[n];
            for (int i = 0; i < n; i++)
            {
                var mem = programs[i].MemoryData ?? new byte[programs[i].header.ObjectSize];
                objAt[i] = img.Append(mem);
            }
            int objEnd = img.Length;

            int codeStart = img.Alloc(0, PoolAlign);
            var codeOrder = new List<Blob>();
            if (placement != null)
                foreach (var (v, s) in placement.CodeOrder)
                    if (v < n && stages[v, s] != null)
                        codeOrder.Add(codeBlobs.Find(stages[v, s].ByteCode));
            foreach (var b in codeBlobs.All)
                if (!codeOrder.Contains(b))
                    codeOrder.Add(b);
            var codeAt = new Dictionary<Blob, int>();
            foreach (var b in codeOrder.Distinct())
                codeAt[b] = img.Append(b.Data, CodeAlign);
            int used = img.Length - codeStart;
            int poolSize = NwImage.AlignUp(used + layout.PoolSlack, PoolAlign);
            img.Alloc(codeStart + poolSize - img.Length);

            //Pointers that need the layout.
            for (int i = 0; i < n; i++)
            {
                int prog = progAt[i];
                img.Ptr(vars + i * 0x40 + 0x10, prog);
                img.Ptr(vars + i * 0x40 + 0x18, grsc);
                img.Ptr(prog + 0x68, objAt[i]);
                img.Ptr(prog + 0x70, vars + i * 0x40);
                for (int s = 0; s < 6; s++)
                {
                    var sc = stages[i, s];
                    if (sc == null)
                        continue;
                    int st = stageAt[i, s];
                    var ctl = ctlBlobs.Find(sc.ControlCode);
                    var code = codeBlobs.Find(sc.ByteCode);
                    img.Ptr(st + 8, ctlAt[ctl]);
                    img.Ptr(st + 0x10, codeAt[code]);
                }
            }

            img.U32(pool, layout.PoolFlags);
            img.U32(pool + 4, (uint)poolSize);
            img.Ptr(pool + 8, codeStart);
            if (layout.PoolSizeTwice)
                img.U64(pool + 0x10, (ulong)poolSize);
            img.Ptr(pool + 0x20, storage);

            strings.Add(bnsh.Name ?? "dummy");
            int str = strings.Write(img);
            int strEnd = img.Length;

            //Header and the grsc block.
            img.Put(hdr, "BNSH\0\0\0\0"u8);
            img.U8(hdr + 8, bnsh.BinHeader.VersionMicro);
            img.U8(hdr + 9, bnsh.BinHeader.VersionMinor);
            img.U16(hdr + 10, bnsh.BinHeader.VersionMajor);
            img.U16(hdr + 12, 0xfeff);
            img.U8(hdr + 14, bnsh.BinHeader.Alignment);
            img.U8(hdr + 15, bnsh.BinHeader.TargetAddressSize);
            img.U16(hdr + 0x14, bnsh.BinHeader.Flag);
            img.U16(hdr + 0x16, (ushort)grsc);

            var h = bnsh.Header;
            img.Put(grsc, "grsc"u8);
            img.U32(grsc + 4, (uint)(str - grsc));
            img.U32(grsc + 8, (uint)(str - grsc));
            img.U16(grsc + 0x10, h.ApiType);
            img.U16(grsc + 0x12, h.ApiVersion);
            img.U32(grsc + 0x14, h.CodeTarget);
            img.U32(grsc + 0x18, h.CompilerVersion);
            img.U32(grsc + 0x1c, (uint)n);
            img.Ptr(grsc + 0x20, vars);
            img.Ptr(grsc + 0x28, pool);
            img.U64(grsc + 0x30, h.Unknown2);

            var pointers = img.Resolve();
            long nameAt = strings[bnsh.Name ?? "dummy"] + 2;
            img.U32(hdr + 0x10, (uint)nameAt);

            var sections = new List<(int, int)>
            {
                (0, section0End),
                (objStart, objEnd - objStart),
                (objEnd, 0),
                (objEnd, 0),
                (codeStart, poolSize),
                (str, strEnd - str),
            };
            int rlt = img.WriteRelocationTable(sections, pointers);
            img.U32(str + 8, (uint)(rlt - str));
            img.U32(hdr + 0x18, (uint)rlt);
            img.U32(hdr + 0x1c, (uint)img.Length);
            return img.ToArray();
        }

        static BnshFile.ShaderCode[] Stages(BnshFile.BnshShaderProgram p) =>
            new[]
            {
                p.VertexShader,
                p.TessellationControlShader,
                p.TessellationEvalShader,
                p.GeometryShader,
                p.FragmentShader,
                p.ComputeShader,
            };

        static BnshFile.ShaderReflectionData[] Reflections(BnshFile.BnshShaderProgram p) =>
            new[]
            {
                p.VertexShaderReflection,
                p.TessellationControlShaderReflection,
                p.TessellationEvalShaderReflection,
                p.GeometryShaderReflection,
                p.FragmentShaderReflection,
                p.ComputeShaderReflection,
            };

        /// <summary>
        /// The program's reflection block, its per stage blocks after it, then each stage's
        /// dictionaries and slot array. The slots list every dictionary's values in turn, and
        /// each start index the stage block keeps follows from the dictionary sizes.
        /// </summary>
        static void WriteReflection(
            NwImage img,
            NwStringPool strings,
            int refl,
            BnshFile.ShaderReflectionData[] reflections,
            int variation
        )
        {
            var stageAt = new int[6];
            for (int k = 0; k < 6; k++)
            {
                if (reflections[k] == null)
                {
                    img.Ptr(refl + k * 8, 0, registerNull: false);
                    continue;
                }
                stageAt[k] = img.Alloc(StageReflectionSize, 8);
                img.Ptr(refl + k * 8, stageAt[k]);
            }

            for (int k = 0; k < 6; k++)
            {
                var r = reflections[k];
                if (r == null)
                    continue;
                int at = stageAt[k];
                var raw = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref r.header, 1));
                if (raw.Length != StageReflectionSize)
                    throw new InvalidOperationException("stage reflection header size");
                if (BinaryPrimitives.ReadUInt64LittleEndian(raw[0x50..]) != 0)
                    throw new NotSupportedException(
                        $"variation {variation}: stage reflection images"
                    );
                if (BinaryPrimitives.ReadUInt64LittleEndian(raw[0x58..]) != 0)
                    throw new NotSupportedException(
                        $"variation {variation}: separate texture and sampler reflection"
                    );
                img.Put(at + 0x40, raw.Slice(0x40, 0xc));

                var dicts = new[]
                {
                    r.Inputs,
                    r.Outputs,
                    r.Samplers,
                    r.UniformBuffers,
                    r.StorageBuffers,
                };
                var slots = new List<uint>();
                for (int d = 0; d < dicts.Length; d++)
                {
                    if (d > 0)
                        img.U32(at + 0x28 + (d - 1) * 4, (uint)slots.Count);
                    var dict = dicts[d];
                    if (dict == null || dict.Count == 0)
                    {
                        img.Ptr(at + d * 8, 0, registerNull: false);
                        continue;
                    }
                    var keys = Enumerable.Range(0, dict.Count).Select(dict.GetKey).ToList();
                    img.Ptr(at + d * 8, NwDict.Write(img, strings, keys, signed: true));
                    for (int j = 0; j < dict.Count; j++)
                        slots.Add(dict[j].Value);
                }
                img.U32(at + 0x4c, (uint)slots.Count);
                if (slots.Count == 0)
                    img.Ptr(at + 0x38, 0, registerNull: false);
                else
                {
                    int so = img.Alloc(4 * slots.Count, 8);
                    for (int j = 0; j < slots.Count; j++)
                        img.U32(so + j * 4, slots[j]);
                    img.Ptr(at + 0x38, so);
                }
                img.Ptr(at + 0x50, 0, registerNull: false);
                img.Ptr(at + 0x58, 0, registerNull: false);
            }
        }

        static void WriteProgramHeader(NwImage img, int at, BnshShaderProgramHeader h)
        {
            img.U8(at, h.Flags);
            img.U8(at + 1, h.CodeType);
            img.U8(at + 2, h.Format);
            img.U8(at + 3, h.Padding);
            img.U32(at + 4, h.BinaryFormat);
            img.U64(at + 0x38, h.Reserved0);
            img.U64(at + 0x40, h.Reserved1);
            img.U64(at + 0x48, h.Reserved2);
            img.U64(at + 0x50, h.Reserved3);
            img.U64(at + 0x58, h.Reserved4);
            img.U32(at + 0x60, h.ObjectSize);
            img.U32(at + 0x64, h.Padding1);
        }

        static void WriteStageHeader(NwImage img, int at, BnshFile.ShaderCode sc)
        {
            img.U32(at + 0x18, (uint)sc.ByteCode.Length);
            img.U32(at + 0x1c, (uint)sc.ControlCode.Length);
            if (sc.Reserved != null)
                img.Put(at + 0x20, sc.Reserved.AsSpan(0, 32));
        }

        sealed class Blob
        {
            public byte[] Data;
            public readonly List<(int, int)> Users = new();
        }

        /// <summary>Byte blobs stored once each, in the order of their first use.</summary>
        sealed class BlobSet
        {
            readonly Dictionary<UInt128, List<Blob>> _byHash = new();
            public readonly List<Blob> All = new();

            public void Use(byte[] data, (int, int) user)
            {
                var b = Find(data, create: true);
                b.Users.Add(user);
            }

            public Blob Find(byte[] data) => Find(data, create: false);

            Blob Find(byte[] data, bool create)
            {
                var h = System.IO.Hashing.XxHash128.HashToUInt128(data);
                if (!_byHash.TryGetValue(h, out var list))
                    _byHash[h] = list = new List<Blob>();
                foreach (var b in list)
                    if (b.Data.AsSpan().SequenceEqual(data))
                        return b;
                if (!create)
                    throw new InvalidOperationException("blob not registered");
                var nb = new Blob { Data = data };
                list.Add(nb);
                All.Add(nb);
                return nb;
            }
        }
    }
}
