using ShaderLibrary.IO;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ShaderLibrary.Common
{
    public class RelocationTable
    {
        RelocationSection[] Sections = new RelocationSection[1];

        private long _ofsRelocationTable;

        public RelocationTable(int num_sections = 6)
        {
            Sections = new RelocationSection[num_sections];
            for (int i = 0; i < num_sections; i++)
                Sections[i] = new RelocationSection();
        }

        public void SaveHeaderOffset(BinaryWriter writer)
        {
            _ofsRelocationTable = writer.BaseStream.Position;
            writer.Write(0);
        }

        public void Write(BinaryDataWriter writer)
        {
            writer.AlignBytes(8);

            //write header offset
            long pos = writer.BaseStream.Position;
            using (writer.BaseStream.TemporarySeek(_ofsRelocationTable, SeekOrigin.Begin))
                writer.Write((uint)pos);

            writer.WriteSignature("_RLT");
            writer._ofsEndOfBlock = writer.Position;

            writer.Write((uint)pos); //rlt pos
            writer.Write(Sections.Length);
            writer.Write(0); //empty

            int idx = 0;
            foreach (RelocationSection section in Sections)
            {
                writer.Write(0L); //padding
                writer.Write(section.Position);
                writer.Write(section.Size);
                writer.Write(idx);
                writer.Write(section.Entries.Count);

                idx += section.Entries.Count;
            }

            foreach (RelocationSection section in Sections)
            {
                foreach (RelocationEntry entry in section.Entries)
                {
                    writer.Write(entry.Position);
                    writer.Write((ushort)entry.StructCount);
                    writer.Write((byte)entry.OffsetCount);
                    writer.Write((byte)entry.PadingCount);
                }
            }
        }

        internal void SetRelocationSection(int section_idx, uint section_offset, uint section_size)
        {
            Sections[section_idx].Position = section_offset;
            Sections[section_idx].Size = section_size;
        }

        public void SaveEntry(BinaryWriter writer, uint offsetCount, uint structCount, uint paddingCount, uint section_idx, string hint)
        {
            if (section_idx > Sections.Length)
                section_idx = 0;

            Sections[section_idx].Entries.Add(new RelocationEntry((uint)writer.BaseStream.Position, 
               offsetCount, structCount, paddingCount, hint));
        }

        public void SaveEntry(BinaryWriter writer, long pos, uint offsetCount, uint structCount, uint paddingCount, uint section_idx, string hint)
        {
            Sections[section_idx].Entries.Add(new RelocationEntry((uint)pos,
                offsetCount, structCount, paddingCount, hint));
        }

        private class RelocationSection
        {
            internal List<RelocationEntry> Entries = new List<RelocationEntry>();
            internal uint Size;
            internal uint Position;

            public RelocationSection() { }

            internal RelocationSection(uint position, uint size, List<RelocationEntry> entries)
            {
                Position = position;
                Size = size;
                Entries = entries;
            }
        }

        private class RelocationEntry
        {
            internal uint Position;
            internal uint PadingCount;
            internal uint StructCount;
            internal uint OffsetCount;
            internal string Hint;
            internal int SectionIdx;

            internal RelocationEntry(uint position, uint offsetCount, uint structCount, uint padingCount, string hint)
            {
                Position = position;
                StructCount = structCount;
                OffsetCount = offsetCount;
                PadingCount = padingCount;
                Hint = hint;
            }
        }
    }
}
