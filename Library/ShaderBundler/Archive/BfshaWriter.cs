using System;
using System.Collections.Generic;
using System.Linq;
using ShaderLibrary;

namespace ShaderBundler.Archive
{
    /// <summary>
    /// Writes a bfsha with each model's BNSH in the layout the shipped files of its version have.
    /// </summary>
    public static class BfshaWriter
    {
        const int ModelSize = 0x100;
        const int OptionSize = 0x28;
        const int SamplerSize = 0x10;
        const int BlockSize = 0x20;
        const int StorageSize = 0x20;
        const int UniformSize = 0x10;
        const int ProgramSize = 0x40;
        const int BnshAlign = 0x1000;

        /// <summary>Where the archive's own fields start; the models point back at it.</summary>
        const int ArchiveAt = 0x38;

        /// <summary>The first variation header in a BNSH.</summary>
        const int VariationStart = 0xc0;

        /// <param name="placements">Per model, where its BNSH puts shared blobs. Null, or a
        /// null entry, uses the first user of each.</param>
        public static byte[] Write(BfshaFile file, IReadOnlyList<BnshPlacement> placements = null)
        {
            var bh = file.BinHeader;
            if (bh.VersionMajor != 9 || bh.VersionMinor > 1)
                throw new NotSupportedException(
                    $"bfsha {bh.VersionMajor}.{bh.VersionMinor}.{bh.VersionMicro}"
                );
            bool v91 = bh.VersionMinor >= 1;

            var models = Enumerable
                .Range(0, file.ShaderModels.Count)
                .Select(i => file.ShaderModels[i])
                .ToList();
            foreach (var m in models)
                Check(m);

            var bnsh = models
                .Select(
                    (m, i) =>
                        BnshWriter.Write(
                            m.BnshFile,
                            placements != null && i < placements.Count ? placements[i] : null
                        )
                )
                .ToList();

            var img = new NwImage();
            var pool = new NwStringPool();
            pool.Add(file.Name);
            pool.Add(file.Path);

            int fileAt = img.Alloc(0x80);
            int modelsAt = img.Alloc(ModelSize * models.Count, 8);
            int modelDict = NwDict.Write(img, pool, models.Select(m => m.Name).ToList());

            var bnshAt = new long[models.Count];
            for (int mi = 0; mi < models.Count; mi++)
            {
                int mi_ = mi;
                WriteModel(
                    img,
                    pool,
                    models[mi],
                    modelsAt + mi * ModelSize,
                    v91,
                    () => bnshAt[mi_]
                );
            }

            int str = pool.Write(img);
            int strEnd = img.Length;

            int bnshStart = NwImage.AlignUp(img.Length, BnshAlign);
            img.Alloc(0, BnshAlign);
            for (int mi = 0; mi < models.Count; mi++)
                bnshAt[mi] = img.Append(bnsh[mi], BnshAlign);
            img.Align(BnshAlign);
            int bnshEnd = img.Length;

            img.Put(0, "FSHA    "u8);
            img.U8(8, bh.VersionMicro);
            img.U8(9, bh.VersionMinor);
            img.U16(10, bh.VersionMajor);
            img.U16(12, 0xfeff);
            img.U8(14, bh.Alignment);
            img.U8(15, bh.TargetAddressSize);
            img.U16(0x14, bh.Flag);
            img.U16(0x16, (ushort)str);

            img.Ptr(fileAt + 0x20, ArchiveAt);
            img.Ptr(fileAt + 0x28, str);
            img.U32(fileAt + 0x30, (uint)(strEnd - str));
            img.Ptr(fileAt + 0x38, () => pool[file.Name]);
            img.Ptr(fileAt + 0x40, () => pool[file.Path]);
            img.Ptr(fileAt + 0x48, modelsAt);
            img.Ptr(fileAt + 0x50, modelDict);
            img.Ptr(fileAt + 0x58, 0);
            img.Ptr(fileAt + 0x60, 0);
            img.Ptr(fileAt + 0x68, 0);
            img.U16(fileAt + 0x78, (ushort)models.Count);
            img.U16(fileAt + 0x7a, file.Flags);

            var pointers = img.Resolve();
            img.U32(0x10, (uint)(pool[file.Name] + 2));

            var sections = new List<(int, int)> { (0, strEnd), (bnshStart, bnshEnd - bnshStart) };
            int rlt = img.WriteRelocationTable(sections, pointers);
            img.U32(str + 8, (uint)(rlt - str));
            img.U32(0x18, (uint)rlt);
            img.U32(0x1c, (uint)img.Length);
            return img.ToArray();
        }

        static void Check(ShaderModel m)
        {
            if (m.Images.Count > 0)
                throw new NotSupportedException($"model '{m.Name}': image resources");
            if (m.BnshFile.Variations.Any(v => v.BinaryProgram.ComputeShader != null))
                throw new NotSupportedException($"model '{m.Name}': compute programs");
            bool g = HasGeometry(m);
            if (m.BnshFile.Variations.Any(v => (v.BinaryProgram.GeometryShader != null) != g))
                throw new NotSupportedException(
                    $"model '{m.Name}': programs disagree on the geometry stage"
                );
        }

        /// <summary>A model whose programs have a geometry stage keeps a third location and
        /// symbol per resource, between the vertex and fragment ones.</summary>
        static bool HasGeometry(ShaderModel m) =>
            m.BnshFile.Variations.Count > 0
            && m.BnshFile.Variations[0].BinaryProgram.GeometryShader != null;

        static IEnumerable<T> Values<T>(ResDict<T> d)
            where T : IResData, new()
        {
            for (int i = 0; i < d.Count; i++)
                yield return d[i];
        }

        static List<string> Keys<T>(ResDict<T> d)
            where T : IResData, new() => Enumerable.Range(0, d.Count).Select(d.GetKey).ToList();

        /// <param name="v91">Whether the archive is 9.1, which differs from 9.0 in which empty
        /// tables it writes and which null pointers its relocation table lists.</param>
        static void WriteModel(
            NwImage img,
            NwStringPool pool,
            ShaderModel m,
            int at,
            bool v91,
            Func<long> bnsh
        )
        {
            bool geometry = HasGeometry(m);
            //9.0 leaves an empty table out, and lists its null pointer unless it is an image one.
            int Table(int size) => size == 0 && !v91 ? 0 : img.Alloc(size, 8);
            int Dict<T>(ResDict<T> d)
                where T : IResData, new() => d.Count == 0 ? 0 : NwDict.Write(img, pool, Keys(d));
            pool.Add(m.Name);
            img.Ptr(at, () => pool[m.Name]);

            var options = Values(m.StaticOptions).Concat(Values(m.DynamicOptions)).ToList();
            int sopt = Table(OptionSize * m.StaticOptions.Count);
            int dopt = Table(OptionSize * m.DynamicOptions.Count);
            var optionAt = new List<int>();
            for (int i = 0; i < options.Count; i++)
                optionAt.Add(
                    (i < m.StaticOptions.Count ? sopt : dopt - OptionSize * m.StaticOptions.Count)
                        + i * OptionSize
                );

            int attr = Table(2 * m.Attributes.Count);
            int k = 0;
            foreach (var a in Values(m.Attributes))
            {
                img.U8(attr + k * 2, a.Index);
                img.U8(attr + k * 2 + 1, (byte)a.Location);
                k++;
            }

            int samp = Table(SamplerSize * m.Samplers.Count);
            k = 0;
            foreach (var s in Values(m.Samplers))
            {
                pool.Add(s.Annotation);
                string ann = s.Annotation ?? "";
                img.Ptr(samp + k * SamplerSize, () => pool[ann]);
                img.U8(samp + k * SamplerSize + 8, s.Index);
                k++;
            }

            var blocks = Values(m.UniformBlocks).ToList();
            int ub = Table(BlockSize * blocks.Count);
            var storage = Values(m.StorageBuffers).ToList();
            int sb = Table(StorageSize * storage.Count);
            for (int i = 0; i < storage.Count; i++)
            {
                var u = storage[i].Unknowns ?? new uint[8];
                for (int w = 0; w < 8; w++)
                    img.U32(sb + i * StorageSize + w * 4, u[w]);
                for (int p = 0; p < 3; p++)
                    img.Ptr(sb + i * StorageSize + p * 8, 0);
            }

            int uniformCount = blocks.Sum(b => b.Uniforms.Count);
            int unif = Table(UniformSize * uniformCount);
            int u0 = 0;
            for (int i = 0; i < blocks.Count; i++)
            {
                var b = blocks[i];
                int bo = ub + i * BlockSize;
                img.Ptr(bo, unif + u0 * UniformSize);
                img.U8(bo + 0x18, b.header.Index);
                img.U8(bo + 0x19, b.header.Type);
                img.U16(bo + 0x1a, b.header.Size);
                img.U16(bo + 0x1c, (ushort)b.Uniforms.Count);
                img.U16(bo + 0x1e, b.header.Padding);
                for (int j = 0; j < b.Uniforms.Count; j++)
                {
                    var un = b.Uniforms[j];
                    int uo = unif + (u0 + j) * UniformSize;
                    string name = un.Name ?? "";
                    pool.Add(name);
                    img.Ptr(uo, () => pool[name]);
                    img.U32(uo + 8, (uint)un.Index);
                    img.U16(uo + 12, un.DataOffset);
                    img.U8(uo + 14, un.BlockIndex);
                    img.U8(uo + 15, un.Padding);
                }
                u0 += b.Uniforms.Count;
            }

            var progs = m.Programs;
            int progAt = img.Alloc(ProgramSize * progs.Count, 8);
            int keyLen = m.StaticKeyLength + m.DynamicKeyLength;
            if (m.KeyTable.Length != keyLen * progs.Count)
                throw new InvalidOperationException(
                    $"model '{m.Name}': key table does not match the program count"
                );
            int keys = img.Alloc(4 * m.KeyTable.Length, 8);
            for (int i = 0; i < m.KeyTable.Length; i++)
                img.U32(keys + i * 4, (uint)m.KeyTable[i]);

            for (int i = 0; i < blocks.Count; i++)
            {
                int bo = ub + i * BlockSize;
                if (blocks[i].DefaultBuffer != null)
                    img.Ptr(bo + 0x10, img.Append(blocks[i].DefaultBuffer, 8));
                else
                    img.Ptr(bo + 0x10, 0);
            }

            for (int i = 0; i < options.Count; i++)
            {
                var o = options[i];
                int oo = optionAt[i];
                pool.Add(o.Name);
                img.Ptr(oo, () => pool[o.Name]);
                if (o.ChoiceValues != null && o.ChoiceValues.Length > 0)
                {
                    int cv = img.Alloc(4 * o.ChoiceValues.Length, 4);
                    for (int j = 0; j < o.ChoiceValues.Length; j++)
                        img.U32(cv + j * 4, o.ChoiceValues[j]);
                    img.Ptr(oo + 0x10, cv);
                }
                else
                    img.Ptr(oo + 0x10, 0);
                img.U16(oo + 0x18, (ushort)o.Choices.Count);
                img.U16(oo + 0x1a, (ushort)o.DefaultChoiceIdx);
                img.U16(oo + 0x1c, o.Padding);
                img.U8(oo + 0x1e, (byte)o.BlockOffset);
                img.U8(oo + 0x1f, o.KeyOffset);
                img.U32(oo + 0x20, o.Bit32Mask);
                img.U8(oo + 0x24, o.Bit32Index);
                img.U8(oo + 0x25, o.Bit32Shift);
                img.U16(oo + 0x26, o.Padding2);
            }

            int stagesPer = geometry ? 3 : 2;
            int symbolSize = 8 * stagesPer;
            int sym = img.Alloc(0x20, 8);
            var sd = m.SymbolData ?? new SymbolData();
            var symTables = new[]
            {
                (sd.Samplers, m.Samplers.Count),
                (sd.Images, m.Images.Count),
                (sd.UniformBlocks, blocks.Count),
                (sd.StorageBuffers, storage.Count),
            };
            for (int t = 0; t < 4; t++)
            {
                var (list, count) = symTables[t];
                if (count == 0)
                {
                    img.Ptr(sym + t * 8, 0, registerNull: !v91 && t != 1);
                    continue;
                }
                int so = img.Alloc(symbolSize * count, 8);
                img.Ptr(sym + t * 8, so);
                for (int i = 0; i < count; i++)
                {
                    var e = list != null && i < list.Count ? list[i] : null;
                    int eo = so + i * symbolSize;
                    SymbolString(img, pool, eo, e?.Name1);
                    if (geometry)
                        SymbolString(img, pool, eo + 8, e?.Name2);
                    SymbolString(img, pool, eo + 8 * (stagesPer - 1), e?.Value1);
                }
            }

            int table = 0;
            if (m.UnknownTable != null)
                table = img.Append(m.UnknownTable, 8);

            var locKinds = new Func<BfshaShaderProgram, List<ShaderIndexHeader>>[]
            {
                p => p.SamplerIndices,
                p => p.ImageIndices,
                p => p.UniformBlockIndices,
                p => p.StorageBufferIndices,
            };
            for (int i = 0; i < progs.Count; i++)
            {
                var p = progs[i];
                int po = progAt + i * ProgramSize;
                for (int t = 0; t < 4; t++)
                {
                    var list = locKinds[t](p) ?? new List<ShaderIndexHeader>();
                    if (list.Count == 0)
                    {
                        img.Ptr(po + t * 8, 0);
                        continue;
                    }
                    int lo = img.Alloc(4 * stagesPer * list.Count, 8);
                    for (int j = 0; j < list.Count; j++)
                    {
                        int eo = lo + j * 4 * stagesPer;
                        img.U32(eo, (uint)list[j].VertexLocation);
                        if (geometry)
                            img.U32(eo + 4, (uint)list[j].GeoemetryLocation);
                        img.U32(eo + 4 * (stagesPer - 1), (uint)list[j].FragmentLocation);
                    }
                    img.Ptr(po + t * 8, lo);
                }
                int vi = p.VariationIndex;
                img.Ptr(po + 0x20, () => bnsh() + VariationStart + vi * 0x40);
                img.Ptr(po + 0x28, at);
                img.U32(po + 0x30, p.UsedAttributeFlags);
                img.U16(po + 0x34, (ushort)p.Flags);
                img.U16(po + 0x36, (ushort)p.SamplerIndices.Count);
                img.U16(po + 0x38, (ushort)(p.ImageIndices?.Count ?? 0));
                img.U16(po + 0x3a, (ushort)p.UniformBlockIndices.Count);
                img.U16(po + 0x3c, (ushort)p.StorageBufferIndices.Count);
            }

            //9.1 writes the option dictionaries even when empty.
            int sdict = v91
                ? NwDict.Write(img, pool, Keys(m.StaticOptions))
                : Dict(m.StaticOptions);
            int ddict = v91
                ? NwDict.Write(img, pool, Keys(m.DynamicOptions))
                : Dict(m.DynamicOptions);
            int adict = Dict(m.Attributes);
            int sadict = Dict(m.Samplers);
            int ubdict = Dict(m.UniformBlocks);
            int sbdict = Dict(m.StorageBuffers);
            for (int i = 0; i < blocks.Count; i++)
            {
                int bo = ub + i * BlockSize;
                if (blocks[i].Uniforms.Count > 0)
                    img.Ptr(bo + 8, NwDict.Write(img, pool, Keys(blocks[i].Uniforms)));
                else
                    img.Ptr(bo + 8, 0);
            }
            for (int i = 0; i < options.Count; i++)
                img.Ptr(optionAt[i] + 8, NwDict.Write(img, pool, Keys(options[i].Choices)));

            img.Ptr(at + 0x08, sopt);
            img.Ptr(at + 0x10, sdict, registerNull: !v91);
            img.Ptr(at + 0x18, dopt);
            img.Ptr(at + 0x20, ddict, registerNull: !v91);
            img.Ptr(at + 0x28, attr);
            img.Ptr(at + 0x30, adict, registerNull: !v91);
            img.Ptr(at + 0x38, samp);
            img.Ptr(at + 0x40, sadict, registerNull: !v91);
            img.Ptr(at + 0x48, 0);
            img.Ptr(at + 0x50, 0, registerNull: false);
            img.Ptr(at + 0x58, ub);
            img.Ptr(at + 0x60, ubdict, registerNull: !v91);
            img.Ptr(at + 0x68, unif);
            img.Ptr(at + 0x70, sb);
            img.Ptr(at + 0x78, sbdict, registerNull: !v91);
            img.Ptr(at + 0x80, 0);
            img.Ptr(at + 0x88, progAt);
            img.Ptr(at + 0x90, keys);
            img.Ptr(at + 0x98, ArchiveAt);
            img.Ptr(at + 0xa0, sym);
            img.Ptr(at + 0xa8, bnsh);
            img.Ptr(at + 0xb0, 0);
            img.Ptr(at + 0xb8, table, registerNull: v91);
            img.U32(at + 0xc0, (uint)(m.UnknownTable?.Length ?? 0));
            img.Ptr(at + 0xc8, 0);
            img.Ptr(at + 0xd0, 0);

            img.U32(at + 0xd8, (uint)uniformCount);
            img.U32(at + 0xe0, (uint)m.DefaultProgramIndex);
            img.U16(at + 0xe4, (ushort)m.StaticOptions.Count);
            img.U16(at + 0xe6, (ushort)m.DynamicOptions.Count);
            img.U16(at + 0xe8, (ushort)progs.Count);
            img.U8(at + 0xea, m.StaticKeyLength);
            img.U8(at + 0xeb, m.DynamicKeyLength);
            img.U8(at + 0xec, (byte)m.Attributes.Count);
            img.U8(at + 0xed, (byte)m.Samplers.Count);
            img.U8(at + 0xee, (byte)m.Images.Count);
            img.U8(at + 0xef, (byte)blocks.Count);
            img.U8(at + 0xf0, m.Unknown2);
            if (m.BlockIndices != null)
                img.Put(at + 0xf1, m.BlockIndices.AsSpan(0, 4));
            if (m.UnknownIndices2 != null)
                img.Put(at + 0xf5, m.UnknownIndices2.AsSpan(0, 11));
        }

        static void SymbolString(NwImage img, NwStringPool pool, int at, string s)
        {
            //The loader reads a null name as an empty one; no symbol is really empty.
            if (string.IsNullOrEmpty(s))
            {
                img.Ptr(at, 0, registerNull: false);
                return;
            }
            pool.Add(s);
            img.Ptr(at, () => pool[s]);
        }
    }
}
