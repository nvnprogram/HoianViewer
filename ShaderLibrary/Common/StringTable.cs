using ShaderLibrary.IO;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ShaderLibrary.Common
{
    public class StringTable
    {
        private Dictionary<string, StringEntry> _savedStrings = new Dictionary<string, StringEntry>();

        private long _ofsStringTable;
        private long _ofsFileName;
        private string fileName;

        public void SaveHeaderOffset(BinaryWriter writer)
        {
            _ofsStringTable = writer.BaseStream.Position;
            writer.Write(0); //offset
            writer.Write(0); //size
        }

        //File name is pointed directly and using unit offset
        public void AddFileNameEntry(long ofs, string str)
        {
            _ofsFileName = ofs;
            fileName = str;
        }

        public void AddEntry(long ofs, string str)
        {
            if (_savedStrings.ContainsKey(str))
                _savedStrings[str].Positions.Add(ofs);
            else
            {
                _savedStrings.Add(str, new StringEntry()
                {
                    Positions = new List<long>() { ofs },
                });
            }
        }

        public void Write(BinaryDataWriter writer)
        {
            writer.AlignBytes(8);

            // Sort the strings ordinally.
            SortedList<string, StringEntry> sorted = new SortedList<string, StringEntry>(ResStringComparer.Instance);
            foreach (KeyValuePair<string, StringEntry> entry in _savedStrings)
                sorted.Add(entry.Key, entry.Value);

            long start_pos = writer.BaseStream.Position;

            writer.WriteSignature("_STR");
            writer.SaveHeaderBlock();
            writer.Write(sorted.Count);

            //save file name from binary header 
            if (_ofsFileName != 0)
            {
                writer.Write((short)fileName.Length);

                long str_pos = writer.Position;
                using (writer.BaseStream.TemporarySeek(this._ofsFileName, SeekOrigin.Begin))
                    writer.Write((uint)str_pos);

                writer.Write(Encoding.UTF8.GetBytes(fileName));
                writer.Write((byte)0);
                writer.AlignBytes(4);
            }

            foreach (KeyValuePair<string, StringEntry> entry in sorted)
            {
                var pos = writer.BaseStream.Position;

                foreach (var p in entry.Value.Positions)
                {
                    using (writer.BaseStream.TemporarySeek(p, SeekOrigin.Begin))
                    {
                        writer.Write((uint)pos);
                    }
                }

                // Align and satisfy offsets.
                writer.Write((short)entry.Key.Length);

                // Write the name.
                writer.Write(Encoding.UTF8.GetBytes(entry.Key));
                writer.Write((byte)0);
                writer.AlignBytes(4);
            }

            long end_pos = writer.BaseStream.Position;
            long size = end_pos - start_pos;
        }

        class StringEntry
        {
            public List<long> Positions = new List<long>();
        }
    }
}
