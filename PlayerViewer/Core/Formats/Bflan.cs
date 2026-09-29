using System;
using System.Collections.Generic;
using System.Text;

namespace PlayerViewer.Core.Formats
{
    /// <summary>
    /// The curves of a little-endian BFLAN layout animation, keyed by pane or material name,
    /// tag (FLTP texture pattern, FLMC material colour, FLTS texture transform, ...), index
    /// and target, plus the texture names a pattern curve indexes into.
    /// </summary>
    public class Bflan
    {
        public class Curve
        {
            public string Owner;
            public string Tag;
            public int Index;
            public int Target;
            public (float Frame, float Value)[] Keys;

            /// <summary>
            /// The value in force at a frame. The layouts author their per frame choices as
            /// steps, a pair of keys at each change, so the last key at or before the frame is
            /// the one that holds.
            /// </summary>
            public float Evaluate(float frame)
            {
                float value = Keys.Length > 0 ? Keys[0].Value : 0;
                foreach (var (f, v) in Keys)
                    if (f <= frame)
                        value = v;
                return value;
            }
        }

        public readonly List<string> Textures = new();
        public readonly List<Curve> Curves = new();

        readonly byte[] _data;

        public Bflan(byte[] data)
        {
            _data = data;
            if (data.Length < 0x14 || Encoding.ASCII.GetString(data, 0, 4) != "FLAN")
                throw new InvalidOperationException("Not a BFLAN animation.");
            int sections = BitConverter.ToUInt16(data, 0x10);
            int pos = BitConverter.ToUInt16(data, 0x06);
            for (int i = 0; i < sections && pos + 8 <= data.Length; i++)
            {
                int size = BitConverter.ToInt32(data, pos + 4);
                if (Encoding.ASCII.GetString(data, pos, 4) == "pai1")
                    ReadInfo(pos);
                pos += size;
            }
        }

        public Curve Find(string owner, string tag, int index, int target) =>
            Curves.Find(c =>
                c.Owner == owner && c.Tag == tag && c.Index == index && c.Target == target
            );

        string CString(int at)
        {
            int end = at;
            while (end < _data.Length && _data[end] != 0)
                end++;
            return Encoding.ASCII.GetString(_data, at, end - at);
        }

        void ReadInfo(int section)
        {
            int textures = BitConverter.ToUInt16(_data, section + 0x0C);
            int contents = BitConverter.ToUInt16(_data, section + 0x0E);
            int contentTable = section + BitConverter.ToInt32(_data, section + 0x10);
            int textureTable = section + 0x14;
            for (int i = 0; i < textures; i++)
                Textures.Add(
                    CString(textureTable + BitConverter.ToInt32(_data, textureTable + i * 4))
                );

            for (int c = 0; c < contents; c++)
            {
                int content = section + BitConverter.ToInt32(_data, contentTable + c * 4);
                string owner = CString(content);
                int tags = _data[content + 0x1C];
                for (int t = 0; t < tags; t++)
                {
                    int tag = content + BitConverter.ToInt32(_data, content + 0x20 + t * 4);
                    string magic = Encoding.ASCII.GetString(_data, tag, 4);
                    int entries = _data[tag + 4];
                    for (int e = 0; e < entries; e++)
                    {
                        int entry = tag + BitConverter.ToInt32(_data, tag + 8 + e * 4);
                        Curves.Add(ReadCurve(owner, magic, entry));
                    }
                }
            }
        }

        //Hermite keys are frame, value, slope. Step keys hold a 16 bit value in place of the
        //float, which is how pattern and visibility curves store their index.
        Curve ReadCurve(string owner, string tag, int entry)
        {
            int curveType = _data[entry + 2];
            int count = BitConverter.ToUInt16(_data, entry + 4);
            int keys = entry + BitConverter.ToInt32(_data, entry + 8);
            var curve = new Curve
            {
                Owner = owner,
                Tag = tag,
                Index = _data[entry],
                Target = _data[entry + 1],
                Keys = new (float, float)[count],
            };
            int stride = curveType == 2 ? 12 : 8;
            for (int k = 0; k < count; k++)
            {
                int at = keys + k * stride;
                float frame = BitConverter.ToSingle(_data, at);
                float value =
                    curveType == 2
                        ? BitConverter.ToSingle(_data, at + 4)
                        : BitConverter.ToUInt16(_data, at + 4);
                curve.Keys[k] = (frame, value);
            }
            return curve;
        }
    }
}
