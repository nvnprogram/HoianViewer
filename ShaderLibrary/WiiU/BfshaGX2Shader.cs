using ShaderLibrary;
using ShaderLibrary.IO;
using ShaderLibrary.WiiU;
using System;
using System.Collections.Generic;
using System.IO;

namespace BfshaLibrary.WiiU
{
    public interface BfshaGX2Header
    {
        byte[] Data { get; set; }
        byte[] Regs { get; set; }
        uint Mode { get; set; }
        List<GSHFile.GX2LoopVar> Loops { get; set; }
        uint[] UnusedHeader { get; set; }

        void Write(BinaryDataWriter writer);

        byte[] ToArray();
    }

    public class BfshaGX2VertexHeader : BfshaGX2Header
    {
        public byte[] Data { get; set; }
        public byte[] Regs { get; set; }
        public uint Mode { get; set; }

        public List<GSHFile.GX2LoopVar> Loops { get; set; } = new List<GSHFile.GX2LoopVar>();

        public uint[] UnusedHeader { get; set; } = new uint[22];

        public byte[] ToArray()
        {
            var mem = new MemoryStream();
            using (var writer = new BinaryDataWriter(mem, true))
            {
                Write(writer);
            }
            return mem.ToArray();
        }
        public BfshaGX2VertexHeader() { }

        public BfshaGX2VertexHeader(BinaryDataReader reader)
        {
            Regs = reader.ReadBytes(52 * sizeof(uint));
            uint size = reader.ReadUInt32();
            var data_offset = reader.ReadOffset();
            Mode = reader.ReadUInt32();

            uint uniformBlockCount = reader.ReadUInt32();
            uint uniformBlocksOffset = reader.ReadUInt32();
            uint uniformVarCount = reader.ReadUInt32();
            uint uniformVarsOffset = reader.ReadUInt32();
            uint padding1 = reader.ReadUInt32();
            uint padding2 = reader.ReadUInt32();

            uint loopVarCount = reader.ReadUInt32();
            uint loopVarsOffset = reader.ReadUInt32();
            uint samplerVarCount = reader.ReadUInt32();
            uint samplerVarsOffset = reader.ReadUInt32();
            uint attribVarCount = reader.ReadUInt32();
            uint attribVarsOffset = reader.ReadUInt32();
            byte hasStreamOut = reader.ReadByte();
            uint streamOutStride = reader.ReadUInt32();

            UnusedHeader = reader.ReadUInt32s(22);
            Data = reader.ReadCustom(() => reader.ReadBytes((int)size), (uint)data_offset);
        }

        internal long _ofs_pos;
        public void Write(BinaryDataWriter writer)
        {
            UnusedHeader[7] = (uint)this.Loops.Count;

            writer.Write(Regs);
            writer.Write(Data.Length);
            _ofs_pos = writer.Position;
            writer.Write(0); //offset for later
            writer.Write(Mode);
            writer.Write(UnusedHeader);
        }

        public void WriteData(BfshaFile bfsha, BinaryDataWriter writer)
        {
            writer.AlignBytes((int)bfsha.DataAlignment);
            writer.WriteOffset(_ofs_pos);
            writer.Write(Data);
        }
    }

    public class BfshaGX2PixelHeader : BfshaGX2Header
    {
        public byte[] Data { get; set; }
        public byte[] Regs { get; set; }
        public uint Mode { get; set; }
        public uint[] UnusedHeader { get; set; } = new uint[14];

        public List<GSHFile.GX2LoopVar> Loops { get; set; } = new List<GSHFile.GX2LoopVar>();

        public byte[] ToArray()
        {
            var mem = new MemoryStream();
            using (var writer = new BinaryDataWriter(mem, true))
            {
                Write(writer);
            }
            return mem.ToArray();
        }

        public BfshaGX2PixelHeader() { }

        public BfshaGX2PixelHeader(BinaryDataReader reader)
        {
            Regs = reader.ReadBytes(41 * sizeof(uint));
            uint size = reader.ReadUInt32();
            var data_offset = reader.ReadOffset();
            Mode = reader.ReadUInt32();
            UnusedHeader = reader.ReadUInt32s(14);
            Data = reader.ReadCustom(() => reader.ReadBytes((int)size), (uint)data_offset);
        }

        public void Write(BinaryDataWriter writer)
        {
            UnusedHeader[7] = (uint)this.Loops.Count;

            writer.Write(Regs);
            writer.Write(Data.Length);
            writer.Write(0); //offset for later
            writer.Write(Mode);
            writer.Write(UnusedHeader);
        }
    }

    public class BfshaGX2GeometryHeader : BfshaGX2Header
    {
        public byte[] Data { get; set; }
        public byte[] Regs { get; set; }
        public uint Mode { get; set; }
        public uint[] UnusedHeader { get; set; }
        public List<GSHFile.GX2LoopVar> Loops { get; set; } = new List<GSHFile.GX2LoopVar>();

        public byte[] ToArray()
        {
            var mem = new MemoryStream();
            using (var writer = new BinaryDataWriter(mem, true))
            {
                Write(writer);
            }
            return mem.ToArray();
        }

        public BfshaGX2GeometryHeader(BinaryDataReader reader)
        {
            Regs = reader.ReadBytes(19 * 4);
            uint size = reader.ReadUInt32();
            var data_offset = reader.ReadOffset();
            Mode = reader.ReadUInt32();

            UnusedHeader = reader.ReadUInt32s(14);
            Data = reader.ReadCustom(() => reader.ReadBytes((int)size), (uint)data_offset);
        }

        private long _ofs_pos;
        public void Write(BinaryDataWriter writer)
        {
            UnusedHeader[7] = (uint)this.Loops.Count;

            writer.Write(Regs);
            writer.Write(Data.Length);
            _ofs_pos = writer.Position;
            writer.Write(0); //offset for later
            writer.Write(Mode);
            writer.Write(UnusedHeader);
        }

        public void WriteData(BfshaFile bfsha, BinaryDataWriter writer)
        {
            writer.AlignBytes((int)bfsha.DataAlignment);
            writer.WriteOffset(_ofs_pos);
            writer.Write(Data);
        }
    }
}
