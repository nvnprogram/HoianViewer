using Microsoft.VisualBasic.FileIO;
using ShaderLibrary.IO;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace ShaderLibrary.WiiU
{
    public class BfshaLoaderWiiU
    {
        public static void Load(BfshaFile bfsha, BinaryDataReader reader)
        {
            reader.SeekBegin(0);

            bfsha.IsWiiU = true;
            bfsha.BinHeader = new BinaryHeader();
            bfsha.BinHeader.Magic = reader.ReadUInt32();
            bfsha.BinHeader.VersionMajor = reader.ReadByte();
            bfsha.BinHeader.VersionMicro = reader.ReadByte();
            reader.ReadByte();
            bfsha.BinHeader.VersionMinor = reader.ReadByte();

            reader.Header = bfsha.BinHeader;

            bfsha.BinHeader.ByteOrder = reader.ReadUInt16();
            ushort headerSize = reader.ReadUInt16(); //HeaderSize 0x10
            var fileSize = reader.ReadUInt32();
            bfsha.DataAlignment = reader.ReadUInt32();
            var nameOffset = reader.ReadOffset();
            var stringPoolSize = reader.ReadUInt32();
            var stringPoolOffset = reader.ReadOffset();
            var pathOffset = reader.ReadOffset();
            var numShaderModels = reader.ReadUInt16();
            bfsha.Flags = reader.ReadUInt16();
            var v = reader.ReadUInt32();
            var shaderModelOffset = reader.ReadOffset();
            var unknown1 = reader.ReadUInt32();
            var unknown2 = reader.ReadUInt32();

            bfsha.Name = reader.LoadString((uint)nameOffset);
            bfsha.Path = reader.LoadString((uint)pathOffset);

            reader.SeekBegin(stringPoolOffset);
            while (reader.BaseStream.Position < stringPoolOffset + stringPoolSize)
            {
                uint len = reader.ReadUInt32();
                string value = reader.ReadZeroTerminatedString();
                reader.Align(4);

                bfsha.StringPool.Strings.Add(value);
                Console.WriteLine($"Strings {value}");
            }

            reader.SeekBegin(shaderModelOffset);
            bfsha.ShaderModels = ReadDictionary(reader, ReadShaderModel);
        }

        static ShaderModel ReadShaderModel(BinaryDataReader reader)
        {
            ShaderModel shaderModel = new ShaderModel();
            shaderModel.StaticKeyLength = reader.ReadByte();
            shaderModel.DynamicKeyLength = reader.ReadByte();

            ushort staticOptionCount = reader.ReadUInt16();
            ushort dynamicOptionCount = reader.ReadUInt16();
            ushort programCount = reader.ReadUInt16();
            byte attributeCount = reader.ReadByte();
            byte samplerCount = reader.ReadByte();
            byte uniformBlockCount = reader.ReadByte();
            byte unknown = reader.ReadByte();
            shaderModel.MaxVSRingItemSize = reader.ReadUInt16();
            shaderModel.MaxRingItemSize = reader.ReadUInt16();
            shaderModel.UnknownIndices2 = reader.ReadBytes(4);
            uint uniformCount = reader.ReadUInt32();
            shaderModel.BlockIndices = reader.ReadBytes(4);
            shaderModel.DefaultProgramIndex = reader.ReadInt32();

            uint nameOffset = (uint)reader.ReadOffset();
            uint unknown6 = reader.ReadUInt32();
            uint unknown7 = reader.ReadUInt32();

            var staticOptionsOffset = reader.ReadOffset();
            var staticOptionsDictOffset = reader.ReadOffset();
            var dynamicOptionsOffset = reader.ReadOffset();
            var dynamicOptionsDictOffset = reader.ReadOffset();
            var attributesOffset = reader.ReadOffset();
            var attributesDictOffset = reader.ReadOffset();
            var samplersOffset = reader.ReadOffset();
            var samplersDictOffset = reader.ReadOffset();
            var uniformBlocksOffset = reader.ReadOffset();
            var uniformBlocksDictOffset = reader.ReadOffset();

            var uniformsOffset = reader.ReadOffset();
            var shaderProgramsOffset = reader.ReadOffset();
            var keyTableOffset = reader.ReadOffset();

            var shaderArchiveOffset = reader.ReadOffset();
            uint unknown8 = reader.ReadUInt32();
            uint unknown9 = reader.ReadUInt32();
            var symbolsOffset = (int)reader.ReadOffset();

            shaderModel.Name = reader.LoadString(nameOffset);

            if (staticOptionsDictOffset  != 0)
            {
                reader.SeekBegin(staticOptionsDictOffset);
                shaderModel.StaticOptions = ReadDictionary(reader, ReadShaderOption);
            }
            if (dynamicOptionsDictOffset != 0)
            {
                reader.SeekBegin(dynamicOptionsDictOffset);
                shaderModel.DynamicOptions = ReadDictionary(reader, ReadShaderOption);
            }
            if (samplersDictOffset != 0)
            {
                reader.SeekBegin(samplersDictOffset);
                shaderModel.Samplers = ReadDictionary(reader, ReadShaderSampler);
            }
            if (attributesDictOffset != 0)
            {
                reader.SeekBegin(attributesDictOffset);
                shaderModel.Attributes = ReadDictionary(reader, ReadShaderAttribute);
            }
            if (uniformBlocksDictOffset != 0)
            {
                reader.SeekBegin(uniformBlocksDictOffset);
                shaderModel.UniformBlocks = ReadDictionary(reader, ReadShaderUniformBlock);
            }
            if (keyTableOffset != 0)
            {
                reader.SeekBegin(keyTableOffset);
                shaderModel.KeyTable = reader.ReadInt32s((shaderModel.StaticKeyLength + shaderModel.DynamicKeyLength) * programCount);
            }

            reader.SeekBegin(shaderProgramsOffset);

            shaderModel.Programs = new List<BfshaShaderProgram>();
            for (int i = 0; i < programCount; i++)
                shaderModel.Programs.Add(ReadShaderProgram(reader));

            return shaderModel;
        }

        static ShaderOption ReadShaderOption(BinaryDataReader reader)
        {
            ShaderOption option = new ShaderOption();
            byte choiceCount = reader.ReadByte();
            option.DefaultChoiceIdx = reader.ReadByte();
            option.BlockOffset = reader.ReadUInt16();
            option.Flags = reader.ReadByte(); //flags
            option.KeyOffset = reader.ReadByte();
            option.Bit32Index = reader.ReadByte();
            option.Bit32Shift = reader.ReadByte();
            option.Bit32Mask = reader.ReadUInt32();
            option.Name = reader.LoadString((uint)reader.ReadOffset());
            uint choiceDict = (uint)reader.ReadOffset();
            uint choiceValuesOffset = (uint)reader.ReadOffset();

            using (reader.BaseStream.TemporarySeek(choiceDict, SeekOrigin.Begin))
            {
                option.Choices = ReadDictionary(reader, ReadOptionValue);
            }

            Console.WriteLine($"BfshaShaderOption {option.Name}");

            return option;
        }

        static ResUint32 ReadOptionValue(BinaryReader reader)
        {
            return new ResUint32()
            {
                Value = reader.ReadUInt32(),
            };
        }

        static BfshaSampler ReadShaderSampler(BinaryDataReader reader)
        {
            BfshaSampler sampler = new BfshaSampler();
            sampler.Index = reader.ReadByte();
            sampler.GX2Type = reader.ReadByte();
            sampler.GX2Count = reader.ReadByte();
            reader.ReadSByte(); //padding
            reader.ReadUInt32();
           // sampler.Annotation = reader.LoadString((uint)reader.ReadOffset());

            Console.WriteLine($"BfshaSampler {sampler.Index}");

            return sampler;
        }

        static BfshaAttribute ReadShaderAttribute(BinaryDataReader reader)
        {
            BfshaAttribute attr = new BfshaAttribute();
            attr.Index = reader.ReadByte();
            attr.GX2Type = reader.ReadByte();
            attr.GX2Count = reader.ReadByte();
            attr.Location = reader.ReadSByte();

            Console.WriteLine($"BfshaAttribute {attr.Index}");

            return attr;
        }

        static BfshaUniformBlock ReadShaderUniformBlock(BinaryDataReader reader)
        {
            BfshaUniformBlock block = new BfshaUniformBlock();
            block.header.Index = reader.ReadByte();
            block.header.Type = reader.ReadByte();
            block.header.Size = reader.ReadUInt16();
            uint uniformCount = reader.ReadUInt16();
            reader.ReadUInt16();

            var uniformOffset = reader.ReadOffset();
            var dataOffset = reader.ReadOffset();

            if (uniformOffset != 0)
            {
                using (reader.BaseStream.TemporarySeek(uniformOffset, SeekOrigin.Begin)) {
                    block.Uniforms = ReadDictionary(reader, ReadShaderUniform);
                }
            }
            if (dataOffset != 0)
            {
                block.DefaultBuffer = reader.ReadCustom(() =>
                    reader.ReadBytes(block.Size), (uint)dataOffset);
            }

            Console.WriteLine($"BfshaUniformBlock {block.Index}");

            return block;
        }

        static BfshaUniform ReadShaderUniform(BinaryDataReader reader)
        {
            BfshaUniform uniform = new BfshaUniform();
            uniform.Index = reader.ReadInt32();
            uniform.GX2Count = reader.ReadUInt16();
            uniform.GX2Type = reader.ReadByte();
            uniform.BlockIndex = reader.ReadByte();
            uniform.DataOffset = reader.ReadUInt16();
            uniform.GX2ParamType = reader.ReadByte();
            reader.ReadByte();
            reader.ReadInt32();

            Console.WriteLine($"BfshaUniform {uniform.Index}");

            return uniform;
        }

        static BfshaShaderProgram ReadShaderProgram(BinaryDataReader reader)
        {
            BfshaShaderProgram program = new BfshaShaderProgram();
            program.Flags = reader.ReadUInt16();
            var samplerCount = reader.ReadByte();
            var blockCount = reader.ReadByte();
            program.UsedAttributeFlags = reader.ReadUInt32();

            if (reader.Header.VersionMajor >= 3 && reader.Header.VersionMicro >= 5)
            {
                program.GX2Instructions = reader.ReadUInt16s(16);
                reader.ReadUInt32(); //always 0
                program.GX2Instructions = reader.ReadUInt16s(10);
                reader.ReadUInt32(); //always 0

                var samplerLocationsOffset = reader.ReadOffset();
                var uniformBlockLocationsOffset = reader.ReadOffset();

                var ShaderVertexDataOffset = reader.ReadOffset();
                var ShaderGeometryDataOffset = reader.ReadOffset();
                var ShaderGeometryCopyDataOffset = reader.ReadOffset();
                var ShaderFragmentDataOffset = reader.ReadOffset();
                var ShaderComputeDataOffset = reader.ReadOffset();

                var parentModelOffset = reader.ReadOffset();

                //Rather than GX2 shader headers, this version has unsupported headers that work differently
                //Unsure how to support these atm

               // throw new Exception($"Version {reader.Header.VersionMajor} not supported!");

              //  program.SamplerIndices = ReadLocationList(reader, samplerLocationsOffset, samplerCount);
              //  program.UniformBlockIndices = ReadLocationList(reader, uniformBlockLocationsOffset, blockCount);
            }
            else
            {
                program.GX2Instructions = reader.ReadUInt16s(10);
                reader.ReadUInt32(); //always 0

                var samplerLocationsOffset = reader.ReadOffset();
                var uniformBlockLocationsOffset = reader.ReadOffset();

                var GX2VertexDataOffset = reader.ReadOffset();
                var GX2GeometryDataOffset = reader.ReadOffset();
                var GX2FragmentDataOffset = reader.ReadOffset();

                var parentModelOffset = reader.ReadOffset();

                program.GX2VertexData = reader.ReadCustom(() =>
                {
                    return new BfshaLibrary.WiiU.BfshaGX2VertexHeader(reader);
                }, (uint)GX2VertexDataOffset);

                program.GX2GeometryData = reader.ReadCustom(() =>
                {
                    return new BfshaLibrary.WiiU.BfshaGX2GeometryHeader(reader);
                }, (uint)GX2GeometryDataOffset);

                program.GX2PixelData = reader.ReadCustom(() =>
                {
                    return new BfshaLibrary.WiiU.BfshaGX2PixelHeader(reader);
                }, (uint)GX2FragmentDataOffset);

                program.SamplerIndices = ReadLocationList(reader, samplerLocationsOffset, samplerCount);
                program.UniformBlockIndices = ReadLocationList(reader, uniformBlockLocationsOffset, blockCount);
            }
            return program;
        }

        static List<ShaderIndexHeader> ReadLocationList(BinaryDataReader reader, long offset, byte count)
        {
            List<ShaderIndexHeader> values = new List<ShaderIndexHeader>();
            using (reader.BaseStream.TemporarySeek(offset, SeekOrigin.Begin))
            {
                for (int i = 0; i < count; i++)
                {
                    values.Add(new ShaderIndexHeader()
                    {
                        VertexLocation = reader.ReadSByte(),
                        GeoemetryLocation = reader.ReadSByte(),
                        FragmentLocation = reader.ReadSByte(),
                    });
                    if (reader.Header.VersionMajor >= 4) //extra shader
                        values[i].ComputeLocation = reader.ReadSByte();
                }
            }return values;
        }

        static ResDict<T> ReadDictionary<T>(BinaryDataReader reader, Func<BinaryDataReader, T> load_section) where T : class, IResData, new()
        {
            ResDict<T> dict = new ResDict<T>();

            reader.ReadUInt32();
            int numNodes = reader.ReadInt32(); // Excludes root node.
            for (int i = 0; i < numNodes + 1; i++)
            {
                var refe = reader.ReadUInt32();
                var IdxLeft = reader.ReadUInt16();
                var IdxRight = reader.ReadUInt16();
                var Key = reader.LoadString((uint)reader.ReadOffset());
                var ValueOffset = reader.ReadOffset();

                dict._nodes.Add(new ResDict<T>.Node()
                {
                    Reference = refe,
                    IdxLeft = IdxLeft,
                    IdxRight = IdxRight,
                    Key = Key,
                });

                if (i == 0)
                    continue;

                if (ValueOffset != 0)
                {
                    using (reader.BaseStream.TemporarySeek(ValueOffset, SeekOrigin.Begin))
                    {
                        dict.Add(Key, load_section(reader));
                    }
                }
                else
                    dict.Add(Key, null);
            }
            return dict;
        }


        [StructLayout(LayoutKind.Sequential, Size = 0x10)]
        public struct BfshaHeader
        {
            public uint Magic;

            public byte VersionMajor;
            public byte VersionMajor2;
            public byte VersionMinor1;
            public byte VersionMinor2;

            public ushort ByteOrder;
            public ushort HeaderSize;
            public uint FileSize;
            public uint Alignment;
            public uint NameOffset;
            public uint StringPoolSize;
            public ulong StringPoolOffset;
            public ulong PathOffset;

            public ushort NumShaderModels;
            public ushort Flags;
            public uint Padding;

            public uint ShaderModelOffset;

            public uint Unknown1;
            public uint Unknown2;
        }



        [StructLayout(LayoutKind.Sequential, Size = 0x10)]
        public struct ShaderModelHeader
        {
            public byte StaticKeyLength;
            public byte DynamicKeyLength;
            public ushort StaticOptionCount;
            public ushort DynamicOptionCount;
            public ushort ProgramCount;
            public byte AttributeCount;
            public byte SamplerCount;
            public byte UniformBlockCount;
            public byte Unknown;

            public ushort Unknown2;
            public ushort Unknown3;
            public uint Unknown4;

            public uint UniformCount;
            public uint Unknown5;
            public uint DefaultProgramIndex;

            public uint NameOffset;
            public uint Unknown6;
            public uint Unknown7;

            public uint StaticOptionsOffset;
            public uint DynamicOptionsOffset;
            public uint AttributesOffset;
            public uint SamplersOffset;
            public uint UniformBlocksOffset;
            public uint UniformsOffset;
            public uint ShaderProgramsOffset;
            public uint KeyTableOffset;
            public uint ShaderArchiveOffset;
            public uint Unknown8;
            public uint Unknown9;
            public uint SymbolsOffset;
        }

        [StructLayout(LayoutKind.Sequential, Size = 0x10)]
        public struct ShaderOptionHeader
        {
            public byte ChoiceCount;
            public byte DefaultIndex;
            public ushort BlockOffset; // Uniform block offset.

            public byte flag;
            public byte KeyOffset;
            public byte Bit32Index;
            public byte Bit32Shift;

            public uint bit32Mask;
            public uint NameOffset;
            public uint ChoicesOffset;
            public uint ChoicesValuesOffset;
        }

        [StructLayout(LayoutKind.Sequential, Size = 0x10)]
        public struct AttributeHeader
        {
            public byte Index;
            public byte GX2Type;
            public byte GX2Count;
            public byte Location;
        }

        [StructLayout(LayoutKind.Sequential, Size = 0x10)]
        public struct SamplerHeader
        {
            public byte Index;
            public byte GX2Type;
            public byte GX2Count;
            public byte Padding;
            public uint AnnNameOffset;
        }

        [StructLayout(LayoutKind.Sequential, Size = 0x10)]
        public struct ShaderProgramHeader
        {
            public ushort Unknown;
            public byte SamplerCount;
            public byte BlockCount;
            public uint UsedAttributeFlags;

            public uint Unknown2;
            public uint Unknown3;
            public uint Unknown4;
            public uint Unknown5;
            public uint Unknown6;
            public uint Unknown7;

            public uint SamplerLocationsOffset;
            public uint UniformBlockLocationsOffset;

            public uint GX2VertexDataOffset;
            public uint GX2GeometryDataOffset;
            public uint GX2FragmentDataOffset;

            public uint ParentModelOffset;
        }
    }
}
