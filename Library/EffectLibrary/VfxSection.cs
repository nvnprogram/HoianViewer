using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace EffectLibrary
{
    /// <summary>How a section lays out its payload and children; decided by the tag.</summary>
    public enum SectionKind
    {
        /// <summary>No payload of its own, only children (ESTA, PRMA, TRMA).</summary>
        Container,

        /// <summary>Payload after the header, then sub-sections, then children (ESET, EMTR, the
        /// emitter sub-sections).</summary>
        Record,

        /// <summary>An embedded file: child headers first, the payload 4096 aligned after them
        /// (GRTF, G3PR, GRSN, GRSC).</summary>
        Blob,

        /// <summary>A plain payload after the header whose size field excludes the header (GTNT,
        /// G3NT, PRIM).</summary>
        Table,
    }

    /// <summary>
    /// One node of the VFXB section tree. The payload is kept as the file's own bytes, so what is
    /// not modelled goes back out unchanged; the offsets, sizes and padding are recomputed on
    /// write.
    /// </summary>
    public sealed class VfxSection
    {
        public const int HeaderSize = 0x20;
        public const uint None = 0xFFFFFFFF;

        public string Tag { get; }

        /// <summary>The payload bytes. Writes through this land in the loaded buffer.</summary>
        public Memory<byte> Payload { get; set; }

        /// <summary>Whether the header points at a payload at all. Empty containers do not.</summary>
        public bool HasPayload { get; set; }

        public List<VfxSection> Children { get; } = new();

        /// <summary>The emitter's attribute chain (fields, anims, custom params).</summary>
        public List<VfxSection> SubSections { get; } = new();

        public VfxSection Parent { get; internal set; }

        /// <summary>Header word at 0x18, zero in every S3 file.</summary>
        public uint Reserved18 { get; set; }

        /// <summary>Header bytes at 0x1E, zero in every S3 file.</summary>
        public ushort Reserved1E { get; set; }

        /// <summary>Where the header sat in the file it was read from, for diagnostics.</summary>
        public int SourceOffset { get; internal set; } = -1;

        public VfxSection(string tag)
        {
            if (tag.Length != 4)
                throw new ArgumentException("A section tag is four characters", nameof(tag));
            Tag = tag;
        }

        public SectionKind Kind => KindOf(Tag);

        public static SectionKind KindOf(string tag) =>
            tag switch
            {
                "ESTA" or "PRMA" or "TRMA" => SectionKind.Container,
                "GRTF" or "G3PR" or "GRSN" or "GRSC" => SectionKind.Blob,
                "GTNT" or "G3NT" or "PRIM" => SectionKind.Table,
                _ => SectionKind.Record,
            };

        public Span<byte> Data => Payload.Span;

        public VfxSection FindChild(string tag) => Children.Find(x => x.Tag == tag);

        public VfxSection FindSub(string tag) => SubSections.Find(x => x.Tag == tag);

        public void AddChild(VfxSection child)
        {
            child.Parent = this;
            Children.Add(child);
        }

        public void AddSub(VfxSection sub)
        {
            sub.Parent = this;
            SubSections.Add(sub);
        }

        public override string ToString() => $"{Tag} ({Payload.Length} bytes)";

        #region payload access

        public byte GetU8(int offset) => Data[offset];

        public void SetU8(int offset, byte value) => Data[offset] = value;

        public ushort GetU16(int offset) => BinaryPrimitives.ReadUInt16LittleEndian(Data[offset..]);

        public void SetU16(int offset, ushort value) =>
            BinaryPrimitives.WriteUInt16LittleEndian(Data[offset..], value);

        public int GetS32(int offset) => BinaryPrimitives.ReadInt32LittleEndian(Data[offset..]);

        public void SetS32(int offset, int value) =>
            BinaryPrimitives.WriteInt32LittleEndian(Data[offset..], value);

        public uint GetU32(int offset) => BinaryPrimitives.ReadUInt32LittleEndian(Data[offset..]);

        public void SetU32(int offset, uint value) =>
            BinaryPrimitives.WriteUInt32LittleEndian(Data[offset..], value);

        public ulong GetU64(int offset) => BinaryPrimitives.ReadUInt64LittleEndian(Data[offset..]);

        public void SetU64(int offset, ulong value) =>
            BinaryPrimitives.WriteUInt64LittleEndian(Data[offset..], value);

        public float GetF32(int offset) => BinaryPrimitives.ReadSingleLittleEndian(Data[offset..]);

        public void SetF32(int offset, float value) =>
            BinaryPrimitives.WriteSingleLittleEndian(Data[offset..], value);

        /// <summary>A zero terminated string in a fixed field. The bytes after the terminator
        /// are left alone on write, since some fields keep data there.</summary>
        public string GetString(int offset, int length) =>
            FixedString.Read(Data.Slice(offset, length));

        public void SetString(int offset, int length, string value) =>
            FixedString.Write(Data.Slice(offset, length), value);

        #endregion
    }

    static class FixedString
    {
        public static string Read(ReadOnlySpan<byte> field)
        {
            int end = field.IndexOf((byte)0);
            return System.Text.Encoding.UTF8.GetString(end < 0 ? field : field[..end]);
        }

        /// <summary>Writes the string and one terminator; throws when it does not fit.</summary>
        public static void Write(Span<byte> field, string value)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(value);
            if (bytes.Length >= field.Length)
                throw new ArgumentException(
                    $"'{value}' needs {bytes.Length + 1} bytes, the field holds {field.Length}"
                );
            int old = field.IndexOf((byte)0);
            bytes.CopyTo(field);
            field[bytes.Length] = 0;
            //Clear the rest of the old string so no tail of it survives past the new terminator.
            if (old > bytes.Length)
                field[(bytes.Length + 1)..old].Clear();
        }
    }
}
