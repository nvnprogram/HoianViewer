using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace EffectLibrary
{
    /// <summary>
    /// Reads/Writes the VFXB section tree.
    /// </summary>
    static class VfxLayout
    {
        const int BlobAlignment = 4096;
        const int EmitterAlignment = 256;
        const int TopAlignment = 16;
        const int ChainAlignment = 4;

        #region read

        //Far above any stock file; a tree past these is malformed.
        const int MaxDepth = 64;
        const int MaxSections = 1 << 20;

        public static List<VfxSection> ReadTop(Memory<byte> file, int first)
        {
            var list = new List<VfxSection>();
            int offset = first;
            int sections = 0;
            while (true)
            {
                var section = Read(file, offset, 0, ref sections);
                list.Add(section);
                uint next = U32(file.Span, offset + 0x0C);
                if (next == VfxSection.None)
                    break;
                offset = Step(file, offset, next, "next");
            }
            return list;
        }

        /// <summary>
        /// The header a relative offset points at. It must move forward past the current header and
        /// stay in the file, which also keeps every chain and the recursion finite.
        /// </summary>
        static int Step(Memory<byte> file, int from, uint relative, string what)
        {
            long to = from + (long)relative;
            if (relative < VfxSection.HeaderSize || to + VfxSection.HeaderSize > file.Length)
                throw new InvalidOperationException(
                    $"Section at 0x{from:X} has a {what} offset of 0x{relative:X}, which does not lead forward to a header in the file"
                );
            return (int)to;
        }

        static uint U32(ReadOnlySpan<byte> s, int at) =>
            BinaryPrimitives.ReadUInt32LittleEndian(s[at..]);

        static VfxSection Read(Memory<byte> file, int offset, int depth, ref int sections)
        {
            if (depth > MaxDepth)
                throw new InvalidOperationException(
                    $"Section at 0x{offset:X} is nested deeper than {MaxDepth}"
                );
            if (++sections > MaxSections)
                throw new InvalidOperationException($"More than {MaxSections} sections");
            var s = file.Span;
            if (offset < 0 || offset + VfxSection.HeaderSize > s.Length)
                throw new InvalidOperationException(
                    $"Section header at 0x{offset:X} is outside the file"
                );
            string tag = Encoding.ASCII.GetString(s.Slice(offset, 4));
            uint size = U32(s, offset + 0x04);
            uint child = U32(s, offset + 0x08);
            uint sub = U32(s, offset + 0x10);
            uint bin = U32(s, offset + 0x14);
            ushort count = BinaryPrimitives.ReadUInt16LittleEndian(s[(offset + 0x1C)..]);

            var section = new VfxSection(tag)
            {
                SourceOffset = offset,
                Reserved18 = U32(s, offset + 0x18),
                Reserved1E = BinaryPrimitives.ReadUInt16LittleEndian(s[(offset + 0x1E)..]),
            };

            switch (section.Kind)
            {
                case SectionKind.Container:
                    break;
                case SectionKind.Record:
                {
                    //The payload runs to whichever of the sub chain, the children or the end of
                    //the section comes first.
                    uint end = size;
                    if (sub != VfxSection.None)
                        end = Math.Min(end, sub);
                    if (child != VfxSection.None && count > 0)
                        end = Math.Min(end, child);
                    section.HasPayload = true;
                    section.Payload = Slice(file, offset, bin, end - bin);
                    break;
                }
                case SectionKind.Blob:
                case SectionKind.Table:
                    section.HasPayload = true;
                    section.Payload = Slice(file, offset, bin, size);
                    break;
            }

            if (sub != VfxSection.None)
            {
                int at = Step(file, offset, sub, "sub");
                while (true)
                {
                    section.AddSub(Read(file, at, depth + 1, ref sections));
                    uint next = U32(s, at + 0x0C);
                    if (next == VfxSection.None)
                        break;
                    at = Step(file, at, next, "next");
                }
            }

            //GRSN stores a zero count with its GRSC child, and a zero offset when it has none.
            int children = tag == "GRSN" ? (child != VfxSection.None && child != 0 ? 1 : 0) : count;
            if (children > 0)
            {
                int at = Step(file, offset, child, "child");
                for (int i = 0; i < children; i++)
                {
                    section.AddChild(Read(file, at, depth + 1, ref sections));
                    uint next = U32(s, at + 0x0C);
                    if (i + 1 < children)
                    {
                        if (next == VfxSection.None)
                            throw new InvalidOperationException(
                                $"{tag} at 0x{offset:X} counts {children} children but the chain ends at {i + 1}"
                            );
                        at = Step(file, at, next, "next");
                    }
                }
            }
            return section;
        }

        static Memory<byte> Slice(Memory<byte> file, int section, uint offset, uint length)
        {
            if (offset == VfxSection.None)
                return Memory<byte>.Empty;
            long start = section + (long)offset;
            if (start + length > file.Length)
                throw new InvalidOperationException(
                    $"Payload at 0x{start:X} of 0x{length:X} bytes runs past the file"
                );
            return file.Slice((int)start, (int)length);
        }

        #endregion

        #region write

        public static void WriteTop(ByteSink sink, List<VfxSection> sections)
        {
            for (int i = 0; i < sections.Count; i++)
            {
                int start = sink.Position;
                Write(sink, sections[i]);
                if (i + 1 < sections.Count)
                {
                    sink.Align(TopAlignment);
                    sink.PatchU32(start + 0x0C, (uint)(sink.Position - start));
                }
            }
        }

        static void WriteHeader(ByteSink sink, VfxSection section, ushort count)
        {
            sink.Write(Encoding.ASCII.GetBytes(section.Tag));
            sink.WriteU32(0);
            sink.WriteU32(VfxSection.None);
            sink.WriteU32(VfxSection.None);
            sink.WriteU32(VfxSection.None);
            sink.WriteU32(VfxSection.None);
            sink.WriteU32(section.Reserved18);
            sink.WriteU32((uint)(count | section.Reserved1E << 16));
        }

        /// <summary>Writes the section; its next field is left for the caller.</summary>
        static void Write(ByteSink sink, VfxSection section)
        {
            switch (section.Kind)
            {
                case SectionKind.Container:
                    WriteContainer(sink, section);
                    break;
                case SectionKind.Record:
                    WriteRecord(sink, section);
                    break;
                case SectionKind.Blob:
                    WriteBlob(sink, section);
                    break;
                case SectionKind.Table:
                    WriteTable(sink, section);
                    break;
            }
        }

        static void WriteContainer(ByteSink sink, VfxSection section)
        {
            int start = sink.Position;
            WriteHeader(sink, section, (ushort)section.Children.Count);
            if (section.Children.Count == 0)
                return;
            uint first = (uint)(sink.Position - start);
            sink.PatchU32(start + 0x08, first);
            sink.PatchU32(start + 0x14, first);
            int last = WriteChain(sink, section.Children);
            //The packager takes the last child's own size field, which for a PRIM leaves out its
            //header.
            sink.PatchU32(start + 0x04, (uint)(last - start) + sink.ReadU32(last + 0x04));
        }

        static void WriteRecord(ByteSink sink, VfxSection section)
        {
            int start = sink.Position;
            WriteHeader(sink, section, (ushort)section.Children.Count);
            if (section.Tag == "EMTR")
                sink.Align(EmitterAlignment);
            if (section.HasPayload)
            {
                sink.PatchU32(start + 0x14, (uint)(sink.Position - start));
                sink.Write(section.Payload.Span);
            }
            if (section.SubSections.Count > 0)
            {
                sink.Align(ChainAlignment);
                sink.PatchU32(start + 0x10, (uint)(sink.Position - start));
                WriteChain(sink, section.SubSections);
            }
            sink.PatchU32(start + 0x04, (uint)(sink.Position - start));
            if (section.Children.Count > 0)
            {
                sink.Align(ChainAlignment);
                sink.PatchU32(start + 0x08, (uint)(sink.Position - start));
                WriteChain(sink, section.Children);
                if (section.Tag == "ESET")
                    sink.PatchU32(start + 0x04, (uint)(sink.Position - start));
            }
        }

        static void WriteTable(ByteSink sink, VfxSection section)
        {
            int start = sink.Position;
            WriteHeader(sink, section, (ushort)section.Children.Count);
            sink.PatchU32(start + 0x04, (uint)section.Payload.Length);
            sink.PatchU32(start + 0x14, (uint)(sink.Position - start));
            sink.Write(section.Payload.Span);
            //The name tables of GRTF and G3PR point next at their own end rounded to 16, though
            //they are the only child.
            if (section.Tag is "GTNT" or "G3NT")
                sink.PatchU32(start + 0x0C, (uint)Align(sink.Position - start, TopAlignment));
        }

        static void WriteBlob(ByteSink sink, VfxSection section)
        {
            int start = sink.Position;
            bool grsn = section.Tag == "GRSN";
            WriteHeader(sink, section, grsn ? (ushort)0 : (ushort)section.Children.Count);
            sink.PatchU32(start + 0x04, (uint)section.Payload.Length);

            var deferred = new List<(int Start, VfxSection Section)>();
            if (section.Children.Count > 0)
            {
                sink.PatchU32(start + 0x08, (uint)(sink.Position - start));
                int previous = -1;
                foreach (var child in section.Children)
                {
                    sink.Align(ChainAlignment);
                    int at = sink.Position;
                    if (previous >= 0)
                        sink.PatchU32(previous + 0x0C, (uint)(at - previous));
                    if (child.Kind == SectionKind.Blob)
                    {
                        //A nested blob has only its header here; its payload follows ours.
                        WriteHeader(sink, child, 0);
                        sink.PatchU32(at + 0x04, (uint)child.Payload.Length);
                        deferred.Add((at, child));
                    }
                    else
                        Write(sink, child);
                    previous = at;
                }
            }
            else if (grsn)
                sink.PatchU32(start + 0x08, 0);

            sink.Align(BlobAlignment);
            sink.PatchU32(start + 0x14, (uint)(sink.Position - start));
            sink.Write(section.Payload.Span);
            foreach (var (at, child) in deferred)
            {
                sink.Align(BlobAlignment);
                sink.PatchU32(at + 0x14, (uint)(sink.Position - at));
                sink.Write(child.Payload.Span);
            }
            //Without a compute archive the packager still aligns for its empty payload.
            if (grsn && deferred.Count == 0)
                sink.Align(BlobAlignment);
        }

        static int Align(int value, int alignment) =>
            (value + alignment - 1) / alignment * alignment;

        /// <summary>Writes siblings linked by their next fields and returns the last one's
        /// start.</summary>
        static int WriteChain(ByteSink sink, List<VfxSection> list)
        {
            int previous = -1;
            foreach (var item in list)
            {
                sink.Align(ChainAlignment);
                int at = sink.Position;
                if (previous >= 0)
                    sink.PatchU32(previous + 0x0C, (uint)(at - previous));
                Write(sink, item);
                previous = at;
            }
            return previous;
        }

        #endregion
    }
}
