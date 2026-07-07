using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace ShaderLibrary.IO
{
    public class BinaryDataReader : BinaryReader
    {
        public BinaryHeader Header;

        public long Position => this.BaseStream.Position;

        private bool IsBigEndian = false;
        private bool IsWiiU = false;

        public BinaryDataReader(Stream input, bool is_big_endian = false, bool leaveOpen = false) : base(input, Encoding.UTF8, leaveOpen)
        {
            IsBigEndian = is_big_endian;
            IsWiiU |= is_big_endian;
        }

        public override uint ReadUInt32()
        {
            var bytes = base.ReadBytes(4);
            if (IsBigEndian) Array.Reverse(bytes);

            return BitConverter.ToUInt32(bytes);
        }

        public override int ReadInt32()
        {
            var bytes = base.ReadBytes(4);
            if (IsBigEndian) Array.Reverse(bytes);

            return BitConverter.ToInt32(bytes);
        }

        public override short ReadInt16()
        {
            var bytes = base.ReadBytes(2);
            if (IsBigEndian) Array.Reverse(bytes);

            return BitConverter.ToInt16(bytes);
        }

        public override ushort ReadUInt16()
        {
            var bytes = base.ReadBytes(2);
            if (IsBigEndian) Array.Reverse(bytes);

            return BitConverter.ToUInt16(bytes);
        }

        public long ReadOffset()
        {
            if (IsWiiU) //Wii u
            {
                var startpos = this.BaseStream.Position;

                var offset = this.ReadInt32();

                return offset != 0 ? startpos + offset : 0;
            }
            return ReadInt64();
        }

        public T ReadCustom<T>(Func<T> value, ulong offset)
        {
            if (offset == 0) return default(T);

            using (this.BaseStream.TemporarySeek((long)offset, SeekOrigin.Begin)) {
                return value.Invoke();
            }
        }


        public sbyte[] ReadSbytes(int count)
        {
            sbyte[] values = new sbyte[count];
            for (int i = 0; i < count; i++)
                values[i] = this.ReadSByte();
            return values;
        }

        public bool[] ReadBooleans(int count)
        {
            bool[] values = new bool[count];
            for (int i = 0; i < count; i++)
                values[i] = this.ReadBoolean();
            return values;
        }

        public float[] ReadSingles(int count)
        {
            float[] values = new float[count];
            for (int i = 0; i < count; i++)
                values[i] = this.ReadSingle();
            return values;
        }

        public ushort[] ReadUInt16s(int count)
        {
            ushort[] values = new ushort[count];
            for (int i = 0; i < count; i++)
                values[i] = this.ReadUInt16();
            return values;
        }

        public int[] ReadInt32s(int count)
        {
            int[] values = new int[count];
            for (int i = 0; i < count; i++)
                values[i] = this.ReadInt32();
            return values;
        }

        public uint[] ReadUInt32s(int count)
        {
            uint[] values = new uint[count];
            for (int i = 0; i < count; i++)
                values[i] = this.ReadUInt32();
            return values;
        }

        public long[] ReadInt64s(int count)
        {
            long[] values = new long[count];
            for (int i = 0; i < count; i++)
                values[i] = this.ReadInt64();
            return values;
        }

        public ulong[] ReadUInt64s(int count)
        {
            ulong[] values = new ulong[count];
            for (int i = 0; i < count; i++)
                values[i] = this.ReadUInt64();
            return values;
        }

        public string LoadString()
        {
            return LoadString((uint)this.ReadOffset());
        }

        public string LoadString(ulong offset)
        {
            if (offset == 0) return "";

            var shift = IsWiiU ? 0 : 2; //switch shifts by 2 due to string length

            using (this.BaseStream.TemporarySeek((long)offset + shift, SeekOrigin.Begin)) {
                return ReadZeroTerminatedString();
            }
        }

        public void SeekBegin(long offset)
        {
            this.BaseStream.Seek(offset, SeekOrigin.Begin);
        }

        public void SeekBegin(ulong offset)
        {
            this.BaseStream.Seek((long)offset, SeekOrigin.Begin);
        }

        public T ReadStruct<T>() => Utils.BytesToStruct<T>(ReadBytes(Marshal.SizeOf<T>()), IsBigEndian);
        public List<T> ReadMultipleStructs<T>(int count) => Enumerable.Range(0, count).Select(_ => ReadStruct<T>()).ToList();

        public string ReadZeroTerminatedString(int maxLength = int.MaxValue)
        {
            long start = this.Position;
            int size = 0;

            // Read until we hit the end of the stream (-1) or a zero
            while (this.ReadByte() - 1 > 0 && size < maxLength)
            {
                size++;
            }

            this.BaseStream.Position = start;
            string text = Encoding.UTF8.GetString(this.ReadBytes(size), 0, size);
            this.BaseStream.Position++; // Skip the null byte
            return text;
        }

        public void Align(int align)
        {
            var startPos = this.BaseStream.Position;
             this.BaseStream.Seek((int)(-this.BaseStream.Position % align + align) % align, SeekOrigin.Current);
        }
    }
}
