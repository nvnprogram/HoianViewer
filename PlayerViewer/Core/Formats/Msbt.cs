using System;
using System.Collections.Generic;
using System.Text;

namespace PlayerViewer.Core.Formats
{
    /// <summary>
    /// Minimal MSBT message reader: the label and text sections only, with control tags
    /// stripped so what is left is the displayed text.
    /// </summary>
    public class Msbt
    {
        public readonly Dictionary<string, string> Messages = new(StringComparer.Ordinal);

        readonly byte[] _data;
        readonly bool _bigEndian;
        readonly int _unitSize;

        public Msbt(byte[] data)
        {
            _data = data;
            if (data.Length < 0x20 || Encoding.ASCII.GetString(data, 0, 8) != "MsgStdBn")
                throw new InvalidOperationException("Not an MSBT file.");
            _bigEndian = data[8] == 0xFE && data[9] == 0xFF;
            _unitSize = data[0x0C] switch
            {
                0 => 1,
                1 => 2,
                2 => 4,
                _ => throw new InvalidOperationException($"Unknown MSBT encoding {data[0x0C]}."),
            };
            int sections = U16(0x0E);

            var labels = new List<(string Name, int Index)>();
            var texts = new List<string>();
            int pos = 0x20;
            for (int i = 0; i < sections && pos + 0x10 <= data.Length; i++)
            {
                string magic = Encoding.ASCII.GetString(data, pos, 4);
                int size = (int)U32(pos + 4);
                int body = pos + 0x10;
                if (magic == "LBL1")
                    ReadLabels(body, labels);
                else if (magic == "TXT2")
                    ReadTexts(body, size, texts);
                pos = (body + size + 0xF) & ~0xF;
            }

            foreach (var (name, index) in labels)
                if (index >= 0 && index < texts.Count)
                    Messages[name] = texts[index];
        }

        ushort U16(int offset) =>
            _bigEndian
                ? (ushort)(_data[offset] << 8 | _data[offset + 1])
                : BitConverter.ToUInt16(_data, offset);

        uint U32(int offset) =>
            _bigEndian
                ? (uint)(
                    _data[offset] << 24
                    | _data[offset + 1] << 16
                    | _data[offset + 2] << 8
                    | _data[offset + 3]
                )
                : BitConverter.ToUInt32(_data, offset);

        //A hash table of buckets, each a run of length prefixed names with their text index.
        void ReadLabels(int body, List<(string, int)> labels)
        {
            int buckets = (int)U32(body);
            for (int b = 0; b < buckets; b++)
            {
                int count = (int)U32(body + 4 + b * 8);
                int at = body + (int)U32(body + 8 + b * 8);
                for (int i = 0; i < count; i++)
                {
                    int length = _data[at];
                    string name = Encoding.ASCII.GetString(_data, at + 1, length);
                    labels.Add((name, (int)U32(at + 1 + length)));
                    at += 5 + length;
                }
            }
        }

        void ReadTexts(int body, int size, List<string> texts)
        {
            int count = (int)U32(body);
            for (int i = 0; i < count; i++)
            {
                int start = body + (int)U32(body + 4 + i * 4);
                int end = i + 1 < count ? body + (int)U32(body + 8 + i * 4) : body + size;
                texts.Add(ReadText(start, Math.Min(end, _data.Length)));
            }
        }

        uint Unit(int offset) =>
            _unitSize switch
            {
                1 => _data[offset],
                2 => U16(offset),
                _ => U32(offset),
            };

        //0x0E opens a tag (group, type, parameter byte count, parameters) and 0x0F closes
        //one (group, type). Both carry markup rather than text, so neither is kept.
        string ReadText(int start, int end)
        {
            var sb = new StringBuilder();
            var utf8 = new List<byte>();
            int at = start;
            while (at + _unitSize <= end)
            {
                uint unit = Unit(at);
                if (unit == 0)
                    break;
                if (unit == 0x0E)
                {
                    int paramBytes = U16(at + _unitSize + 4);
                    at += _unitSize + 6 + paramBytes;
                    continue;
                }
                if (unit == 0x0F)
                {
                    at += _unitSize + 4;
                    continue;
                }
                if (_unitSize == 1)
                    utf8.Add((byte)unit);
                else if (_unitSize == 2)
                    sb.Append((char)unit);
                else
                    sb.Append(char.ConvertFromUtf32((int)unit));
                at += _unitSize;
            }
            return _unitSize == 1 ? Encoding.UTF8.GetString(utf8.ToArray()) : sb.ToString();
        }
    }
}
