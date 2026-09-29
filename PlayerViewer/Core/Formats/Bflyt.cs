using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;

namespace PlayerViewer.Core.Formats
{
    /// <summary>
    /// The parts of a little-endian BFLYT layout an icon composite needs: the texture list,
    /// each material's colours, texture maps and texture transforms, and each picture pane's
    /// vertex colours and material. Pane transforms and TEV stages are not read.
    /// </summary>
    public class Bflyt
    {
        public struct TexSrt
        {
            public Vector2 Translate;
            public float Rotate; //degrees
            public Vector2 Scale;
        }

        public class Material
        {
            public string Name;
            public Vector4 Black; //0 to 1, as stored
            public Vector4 White;
            public int[] Textures;
            public TexSrt[] Srts;
        }

        public class Picture
        {
            public string Name;
            public Vector4[] VertexColors; //top left, top right, bottom left, bottom right
            public Material Material;
        }

        public readonly List<string> Textures = new();
        public readonly List<Material> Materials = new();
        public readonly Dictionary<string, Picture> Pictures = new(StringComparer.Ordinal);

        readonly byte[] _data;

        public Bflyt(byte[] data)
        {
            _data = data;
            if (data.Length < 0x14 || Encoding.ASCII.GetString(data, 0, 4) != "FLYT")
                throw new InvalidOperationException("Not a BFLYT layout.");
            int sections = BitConverter.ToUInt16(data, 0x10);
            int pos = BitConverter.ToUInt16(data, 0x06);
            for (int i = 0; i < sections && pos + 8 <= data.Length; i++)
            {
                string magic = Encoding.ASCII.GetString(data, pos, 4);
                int size = BitConverter.ToInt32(data, pos + 4);
                switch (magic)
                {
                    case "txl1":
                        ReadNames(pos + 8, Textures);
                        break;
                    case "mat1":
                        ReadMaterials(pos);
                        break;
                    case "pic1":
                        ReadPicture(pos);
                        break;
                }
                pos += size;
            }
        }

        void ReadNames(int at, List<string> into)
        {
            int count = BitConverter.ToUInt16(_data, at);
            int table = at + 4;
            for (int i = 0; i < count; i++)
                into.Add(CString(table + BitConverter.ToInt32(_data, table + i * 4)));
        }

        string CString(int at)
        {
            int end = at;
            while (end < _data.Length && _data[end] != 0)
                end++;
            return Encoding.ASCII.GetString(_data, at, end - at);
        }

        Vector4 Color(int at) =>
            new(_data[at] / 255f, _data[at + 1] / 255f, _data[at + 2] / 255f, _data[at + 3] / 255f);

        //Name, flags, a word this version adds, the two colours, then the texture maps and
        //texture transforms whose counts the flags give.
        void ReadMaterials(int section)
        {
            int count = BitConverter.ToUInt16(_data, section + 8);
            for (int i = 0; i < count; i++)
            {
                int at = section + BitConverter.ToInt32(_data, section + 12 + i * 4);
                uint flags = BitConverter.ToUInt32(_data, at + 0x1C);
                var mat = new Material
                {
                    Name = CString(at),
                    Black = Color(at + 0x24),
                    White = Color(at + 0x28),
                    Textures = new int[flags & 3],
                    Srts = new TexSrt[(flags >> 2) & 3],
                };
                int p = at + 0x2C;
                for (int t = 0; t < mat.Textures.Length; t++, p += 4)
                    mat.Textures[t] = BitConverter.ToUInt16(_data, p);
                for (int t = 0; t < mat.Srts.Length; t++, p += 20)
                    mat.Srts[t] = new TexSrt
                    {
                        Translate = new Vector2(
                            BitConverter.ToSingle(_data, p),
                            BitConverter.ToSingle(_data, p + 4)
                        ),
                        Rotate = BitConverter.ToSingle(_data, p + 8),
                        Scale = new Vector2(
                            BitConverter.ToSingle(_data, p + 12),
                            BitConverter.ToSingle(_data, p + 16)
                        ),
                    };
                Materials.Add(mat);
            }
        }

        void ReadPicture(int section)
        {
            int pane = section + 8;
            var pic = new Picture { Name = CString(pane + 4), VertexColors = new Vector4[4] };
            for (int v = 0; v < 4; v++)
                pic.VertexColors[v] = Color(pane + 0x4C + v * 4);
            int material = BitConverter.ToUInt16(_data, pane + 0x5C);
            pic.Material = material < Materials.Count ? Materials[material] : null;
            Pictures[pic.Name] = pic;
        }
    }
}
