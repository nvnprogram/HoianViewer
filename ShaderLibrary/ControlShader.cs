using Microsoft.VisualBasic;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Threading.Tasks;

namespace ShaderLibrary
{
    public class ControlShader
    {
        private uint Magic = 2557874740;
        public uint MajorVer = 1;
        public uint MinorVer = 9;
        private uint val_0xC = 120;
        private uint val_0x10 = 0xb;
        public uint GlasmOffset = 2000; //pointer to byte at the end
        public uint GlasmSize = 0;
        public uint GlasmUnk0 = 0;
        public uint GlasmUnk1 = 2001; //size?

        public uint ProgramSize;
        private uint ConstBufSize;
        private uint ConstBufOffset;
        private uint ShaderSize;

        public uint ProgramOffset;
        public uint ProgramRegNum;
        public uint PerWarpScratchSize;

        public NVNshaderStage ShaderStage;
        public byte EarlyFragmnetTests;
        public byte PostDepthCoverage;
        public ushort Padding2;

        public byte WriteDepth;
        public byte[] Padding3 = new byte[15];
        public uint NumFragmentOutputs;
        public byte[] Padding4 = new byte[16];

        public byte FragmentUnk;
        public byte PerSamplerInvocation;

        private uint[] Unknowns = new uint[50];

        private byte val_0x2000;

        public Comp ShaderComp = new Comp();

        public struct Comp
        {
            public uint[] BlockDims;
            public uint SharedMemSz;
            public uint LocalPosMemSz;
            public uint LocalNegMemSz;
            public uint CrsSz;
            public uint NumBarriers;
        }

        public byte[] EndPadding = new byte[0x60];
        public byte[] GLAssembly = new byte[0];

        public ControlShader()
        {
            Unknowns[0] = 48;
            Unknowns[1] = 32; //todo this one varies
            Unknowns[28] = 2011069788;
            Unknowns[29] = 1721049022;
            Unknowns[30] = 3;
            Unknowns[32] = 1;
            Unknowns[34] = 2228756;
            Unknowns[48] = 2001;
        }

        public ControlShader(byte[] data)
        {
            using (var reader = new BinaryReader(new MemoryStream(data))) {
                Read(reader);
            }
        }

        public ControlShader(string filePath)
        {
            using (var reader = new BinaryReader(File.OpenRead(filePath)))
            {
                Read(reader);
            }
        }

        private void Read(BinaryReader reader)
        {
            Magic = reader.ReadUInt32();
            MajorVer = reader.ReadUInt32();
            MinorVer = reader.ReadUInt32();
            val_0xC = reader.ReadUInt32();
            val_0x10 = reader.ReadUInt32();
            GlasmOffset = reader.ReadUInt32();
            GlasmSize = reader.ReadUInt32();
            GlasmUnk0 = reader.ReadUInt32();
            GlasmUnk1 = reader.ReadUInt32();

            reader.BaseStream.Seek(1776, SeekOrigin.Begin);
            reader.ReadUInt64(); //points to end of file
            ProgramSize = reader.ReadUInt32();
            ConstBufSize = reader.ReadUInt32();
            ConstBufOffset = reader.ReadUInt32();
            ShaderSize = reader.ReadUInt32();
            ProgramOffset = reader.ReadUInt32(); // 0x30
            ProgramRegNum = reader.ReadUInt32();

            // Structure made around v1.5 (lowest support version supported for max compatibility)
            // Referenced https://github.com/nvnprogram/uam-nvn/blob/master/source/nvn_control.h
            PerWarpScratchSize = reader.ReadUInt32();
            ShaderStage = (NVNshaderStage)reader.ReadUInt32();
            EarlyFragmnetTests = reader.ReadByte();
            PostDepthCoverage = reader.ReadByte();
            Padding2 = reader.ReadUInt16();
            WriteDepth = reader.ReadByte();
            Padding3 = reader.ReadBytes(15);
            NumFragmentOutputs = reader.ReadUInt32();
            Padding4 = reader.ReadBytes(16);

            FragmentUnk = reader.ReadByte();
            PerSamplerInvocation = reader.ReadByte();

            ShaderComp = new Comp()
            {
                BlockDims = new uint[3]
                {
                    reader.ReadUInt32(),
                    reader.ReadUInt32(),
                    reader.ReadUInt32()
                },
                SharedMemSz = reader.ReadUInt32(),
                LocalPosMemSz = reader.ReadUInt32(),
                LocalNegMemSz = reader.ReadUInt32(),
                CrsSz = reader.ReadUInt32(),
                NumBarriers = reader.ReadUInt32(),
            };

            EndPadding = reader.ReadBytes((int)(GlasmOffset - reader.BaseStream.Position));
            GLAssembly = reader.ReadBytes((int)GlasmSize);

            var end = reader.BaseStream.Position;
        }

        public byte[] ToBytes()
        {
            var mem = new MemoryStream();
            Save(mem);
            return mem.ToArray();
        }

        public void Save(string filePath)
        {
            using (var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write))
            {
                Save(fs);
            }
        }

        public void Save(Stream stream)
        {
            using (var writer = new BinaryWriter(stream)) {
                Write(writer);
            }
        }

        private void Write(BinaryWriter writer)
        {
            writer.Write(this.Magic);
            writer.Write(MajorVer);
            writer.Write(MinorVer);
            writer.Write(val_0xC);
            writer.Write(val_0x10);
            writer.Write(GlasmOffset);
            writer.Write((uint)GLAssembly.Length);
            writer.Write(GlasmUnk0);
            writer.Write(GlasmUnk1);

            writer.BaseStream.Seek(1776, SeekOrigin.Begin);
            writer.Write((ulong)2001);
            writer.Write(ProgramSize);
            writer.Write(ConstBufSize);
            writer.Write(ConstBufOffset);
            writer.Write(ShaderSize);
            writer.Write(ProgramOffset);
            writer.Write(ProgramRegNum);
            writer.Write(PerWarpScratchSize);
            writer.Write((uint)ShaderStage);
            writer.Write((byte)EarlyFragmnetTests);
            writer.Write((byte)PostDepthCoverage);
            writer.Write((ushort)Padding2);
            writer.Write((byte)WriteDepth);
            writer.Write(Padding3);
            writer.Write(NumFragmentOutputs);
            writer.Write(Padding4);
            writer.Write(FragmentUnk);
            writer.Write(PerSamplerInvocation);
            writer.Write(ShaderComp.BlockDims[0]);
            writer.Write(ShaderComp.BlockDims[1]);
            writer.Write(ShaderComp.BlockDims[2]);
            writer.Write(ShaderComp.SharedMemSz);
            writer.Write(ShaderComp.LocalPosMemSz);
            writer.Write(ShaderComp.LocalNegMemSz);
            writer.Write(ShaderComp.CrsSz);
            writer.Write(ShaderComp.NumBarriers);
            writer.Write(EndPadding);

            writer.Write(GLAssembly);
            writer.Write(0);
        }

        public float[] GetConstantsAsFloats(byte[] shader_code)
        {
            if (shader_code.Length < this.ConstBufOffset + this.ConstBufSize)
                return new float[0];

            using (var reader = new BinaryReader(new MemoryStream(shader_code)))
            {
                reader.BaseStream.Seek(this.ConstBufOffset, SeekOrigin.Begin);

                float[] values = new float[this.ConstBufSize / 4];
                for (int i = 0; i < values.Length; i++)
                    values[i] = reader.ReadSingle();
                return values;
            }
        }

        public byte[] GetConstants(byte[] shader_code)
        {
            if (shader_code.Length < this.ConstBufOffset + this.ConstBufSize)
                return new byte[0];

            using (var reader = new BinaryReader(new MemoryStream(shader_code)))
            {
                reader.BaseStream.Seek(this.ConstBufOffset, SeekOrigin.Begin);
                return reader.ReadBytes((int)this.ConstBufSize); 
            }
        }

        public void SetConstants(byte[] shader_code, byte[] constants, out byte[] new_shader_code)
        {
            this.ConstBufSize = (uint)constants.Length; //constants size
            this.ProgramSize = GetBytecodeLength(shader_code);  //shader code without the header

            //save to output shader bytecode
            var mem = new MemoryStream();
            using (var writer = new BinaryWriter(mem))
            {
                //shader code
                writer.Write(shader_code);

                AlignBytes(writer, 256);

                //constants (don't write atm, buggy alignment issues)
                this.ConstBufOffset = (uint)writer.BaseStream.Position;

                if (constants.Length > 0)
                {
                    writer.Write(constants);
                    AlignBytes(writer, 256);
                }

                this.ShaderSize = (uint)writer.BaseStream.Position;
            }

            //save output
            new_shader_code = mem.ToArray();
        }

        private uint GetBytecodeLength(byte[] code)
        {
            using (var reader = new BinaryReader(new MemoryStream(code)))
            {
                reader.ReadBytes(48); //start
                reader.ReadBytes(0x50); //nvn header
                //byte code here
                int bytecode_size = 0;
                while (reader.BaseStream.Position < reader.BaseStream.Length)
                {
                    ulong cmd = reader.ReadUInt64();
                    if (cmd == 0)
                        break;

                    bytecode_size += 8;
                }
                return (uint)bytecode_size + 0x50;
            }
        }

        static void AlignBytes(BinaryWriter wr, int align, byte pad_val = 0)
        {
            var startPos = wr.BaseStream.Position;
            long position = wr.Seek((int)(-wr.BaseStream.Position % align + align) % align, SeekOrigin.Current);

            wr.Seek((int)startPos, System.IO.SeekOrigin.Begin);
            while (wr.BaseStream.Position != position)
            {
                wr.Write((byte)pad_val);
            }
        }

        static int GetAlignPos(int startPos, int align)
        {
            return (-startPos % align + align) % align;
        }

        public enum NVNshaderStage
        {
            NVN_SHADER_STAGE_VERTEX = 0,
            NVN_SHADER_STAGE_FRAGMENT = 1,
            NVN_SHADER_STAGE_GEOMETRY = 2,
            NVN_SHADER_STAGE_TESS_CONTROL = 3,
            NVN_SHADER_STAGE_TESS_EVALUATION = 4,
            NVN_SHADER_STAGE_COMPUTE = 5
        }
    }
}
