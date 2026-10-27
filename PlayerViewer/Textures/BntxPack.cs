using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ShaderBundler.Archive;

namespace PlayerViewer.Textures
{
    /// <summary>One texture of a BNTX as stored: its info block and its whole image.</summary>
    public sealed class PackedTexture
    {
        public string Name;

        /// <summary>The fixed part of the texture's info block; pointers are rewritten on write.</summary>
        public byte[] Info;
        public byte[] Image;

        /// <summary>Each mip level's offset into <see cref="Image"/>.</summary>
        public long[] MipOffsets;

        public int Alignment => (int)BinaryPrimitives.ReadUInt32LittleEndian(Info.AsSpan(0x54));

        public uint AccessFlags
        {
            get => BinaryPrimitives.ReadUInt32LittleEndian(Info.AsSpan(0x20));
            set => BinaryPrimitives.WriteUInt32LittleEndian(Info.AsSpan(0x20), value);
        }

        public uint PackagerVersion
        {
            get => BinaryPrimitives.ReadUInt32LittleEndian(Info.AsSpan(0x38));
            set => BinaryPrimitives.WriteUInt32LittleEndian(Info.AsSpan(0x38), value);
        }

        public bool IsBlockLinear =>
            BinaryPrimitives.ReadUInt16LittleEndian(Info.AsSpan(0x12)) == 0;
    }

    /// <summary>
    /// Reads and writes a BNTX at the byte level, laid out the way the games' own files are, so
    /// a texture moved between files keeps every field it had. Written on the archive writers'
    /// <see cref="NwImage"/>, string pool and name dictionary.
    /// </summary>
    public static class BntxPack
    {
        const int InfoSize = 0xA0;

        //Two 0x100 byte areas the runtime fills, then the mip pointer array.
        const int InfoBlockFixed = 0x2A0;
        const int PoolOffset = 0x58;
        const int PoolSize = 0x140;
        const int FileAlignment = 0x1000;

        public static List<PackedTexture> Read(byte[] file)
        {
            var d = file.AsSpan();
            if (d.Length < 0x58 || Encoding.ASCII.GetString(file, 0, 4) != "BNTX")
                throw new FormatException("Not a BNTX file.");
            int count = (int)U32(d, 0x24);
            long infos = (long)U64(d, 0x28);
            var list = new List<PackedTexture>(count);
            for (int i = 0; i < count; i++)
            {
                int at = (int)U64(d, (int)infos + 8 * i);
                var info = d.Slice(at, InfoSize).ToArray();
                int mips = BinaryPrimitives.ReadUInt16LittleEndian(info.AsSpan(0x16));
                int imageSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(info.AsSpan(0x50));
                int mipArray = (int)U64(d, at + 0x70);
                var offsets = new long[mips];
                long first = (long)U64(d, mipArray);
                for (int m = 0; m < mips; m++)
                    offsets[m] = (long)U64(d, mipArray + 8 * m) - first;
                list.Add(
                    new PackedTexture
                    {
                        Name = ReadString(d, (int)U64(d, at + 0x60)),
                        Info = info,
                        Image = d.Slice((int)first, imageSize).ToArray(),
                        MipOffsets = offsets,
                    }
                );
            }
            return list;
        }

        /// <summary>A BNTX holding the given textures, for the platform.</summary>
        public static byte[] Write(
            string fileName,
            BntxPlatform platform,
            IReadOnlyList<PackedTexture> textures
        )
        {
            int n = textures.Count;
            var img = new NwImage();
            var pool = new NwStringPool(NwStringOrder.FirstAdded);
            foreach (var t in textures)
                pool.Add(t.Name);
            pool.Add(fileName);

            img.Alloc(PoolOffset + PoolSize);
            int infoArray = img.Alloc(8 * n, 8);
            int str = pool.Write(img);
            int dic = NwDict.Write(img, pool, textures.Select(t => t.Name).ToList(), signed: true);
            var infoAt = new int[n];
            for (int i = 0; i < n; i++)
                infoAt[i] = img.Alloc(InfoBlockFixed + 8 * textures[i].MipOffsets.Length);
            int firstInfo = n > 0 ? infoAt[0] : img.Length;
            int section0End = img.Length;
            int data = NwImage.AlignUp(section0End + 0x10, FileAlignment);
            int brtd = data - 0x10;
            img.Alloc(data - img.Length);
            var imageAt = new int[n];
            for (int i = 0; i < n; i++)
                imageAt[i] = img.Append(textures[i].Image, Math.Max(1, textures[i].Alignment));
            img.Align(FileAlignment);
            int rlt = img.Length;

            img.Put(0, "BNTX"u8);
            img.U32(8, 0x00040100u | platform.Revision);
            img.U16(0xC, 0xFEFF);
            img.U8(0xE, 12);
            img.U8(0xF, 64);
            img.U16(0x16, (ushort)str);
            img.U32(0x18, (uint)rlt);

            img.Put(0x20, Encoding.ASCII.GetBytes(platform.Tag));
            img.U32(0x24, (uint)n);
            img.Ptr(0x28, infoArray);
            img.Ptr(0x30, brtd);
            img.Ptr(0x38, dic);
            img.Ptr(0x40, PoolOffset);
            for (int i = 0; i < n; i++)
                img.Ptr(infoArray + 8 * i, infoAt[i]);

            img.U32(str + 4, (uint)(firstInfo - str));
            img.U32(str + 8, (uint)(firstInfo - str));

            for (int i = 0; i < n; i++)
            {
                var t = textures[i];
                int at = infoAt[i];
                int next = (i + 1 < n ? infoAt[i + 1] : brtd) - at;
                img.Put(at, t.Info);
                img.U32(at + 4, (uint)next);
                img.U32(at + 8, (uint)next);
                img.U32(at + 0xC, 0);
                int mipArray = at + InfoBlockFixed;
                string name = t.Name;
                img.Ptr(at + 0x60, () => pool[name]);
                img.Ptr(at + 0x68, 0x20);
                img.Ptr(at + 0x70, mipArray);
                img.U64(at + 0x78, 0);
                img.Ptr(at + 0x80, at + InfoSize);
                img.Ptr(at + 0x88, at + InfoSize + 0x100);
                img.U64(at + 0x90, 0);
                img.U64(at + 0x98, 0);
                for (int m = 0; m < t.MipOffsets.Length; m++)
                    img.Ptr(mipArray + 8 * m, imageAt[i] + (int)t.MipOffsets[m]);
            }

            img.Put(brtd, "BRTD"u8);
            img.U32(brtd + 8, (uint)(rlt - brtd));

            var pointers = img.Resolve();
            img.U32(0x10, (uint)(pool[fileName] + 2));
            var sections = new List<(int, int)> { (0, section0End), (brtd, rlt - brtd) };
            img.WriteRelocationTable(sections, pointers);
            img.U32(0x1C, (uint)img.Length);
            return img.ToArray();
        }

        static string ReadString(ReadOnlySpan<byte> d, int at) =>
            Encoding.UTF8.GetString(
                d.Slice(at + 2, BinaryPrimitives.ReadUInt16LittleEndian(d.Slice(at)))
            );

        static uint U32(ReadOnlySpan<byte> d, int at) =>
            BinaryPrimitives.ReadUInt32LittleEndian(d.Slice(at));

        static ulong U64(ReadOnlySpan<byte> d, int at) =>
            BinaryPrimitives.ReadUInt64LittleEndian(d.Slice(at));
    }
}
