using ShaderLibrary.IO;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace ShaderLibrary.Switch
{
    internal class BfshaLoader
    {
        internal static void Read(Stream stream, BfshaFile bfsha)
        {
            stream.Position = 0;

            var reader = new BinaryDataReader(stream);
            stream.Read(Utils.AsSpan(ref bfsha.BinHeader));

            reader.Header = bfsha.BinHeader;

            ulong unk = reader.ReadUInt64();
            ulong stringPoolOffset = reader.ReadUInt64();
            ulong shaderModelOffset = reader.ReadUInt64();
            bfsha.Name = reader.LoadString();
            bfsha.Path = reader.LoadString();
            bfsha.ShaderModels = LoadDictionary(reader, ReadShaderModel);
            reader.ReadUInt32();
            reader.ReadUInt32();
            reader.ReadUInt32();
            reader.ReadUInt32();
            reader.ReadUInt64();
            if (reader.Header.VersionMajor >= 7)
            {
                if (reader.ReadUInt64() != 0) //padding
                    throw new Exception();
            }

            ushort ModelCount = reader.ReadUInt16();
            ushort flag = reader.ReadUInt16();
            reader.ReadUInt16();
        }

        private static ShaderModel ReadShaderModel(BinaryDataReader reader)
        {
            ShaderModel shaderModel = new ShaderModel();
            shaderModel.Name = reader.LoadString();
            shaderModel.StaticOptions = LoadDictionary(reader, ReadShaderOption);
            shaderModel.DynamicOptions = LoadDictionary(reader, ReadShaderOption);
            shaderModel.Attributes = LoadDictionary(reader, ReadAttribute);
            shaderModel.Samplers = LoadDictionary(reader, ReadSampler);
            if (reader.Header.VersionMajor >= 8)
                shaderModel.Images = LoadDictionary(reader, ReadImage);
            shaderModel.UniformBlocks = LoadDictionary(reader, ReadBfshaUniformBlock);
            long uniformArrayOffset = reader.ReadInt64();

            if (reader.Header.VersionMajor >= 7)
            {
                shaderModel.StorageBuffers = LoadDictionary(reader, ReadStorageBuffer);
                reader.ReadUInt64(); //unknown offset. Not used in files tested
            }

            long shaderProgramArrayOffset = reader.ReadInt64();
            long keyTableOffset = reader.ReadInt64();
            long shaderArchiveOffset = reader.ReadInt64();
            long symbolInfoOffset = reader.ReadInt64();
            long shaderFileOffset = reader.ReadInt64();
            reader.ReadInt64(); //0
            reader.ReadInt64(); //0
            reader.ReadInt64(); //0

            if (reader.Header.VersionMajor >= 7)
            {
                //padding
                reader.ReadInt64(); //0
                reader.ReadInt64(); //0
            }

            uint uniformCount = reader.ReadUInt32();
            if (reader.Header.VersionMajor >= 7)
                reader.ReadUInt32(); //shaderStorageCount
            shaderModel.DefaultProgramIndex = reader.ReadInt32();
            ushort staticOptionCount = reader.ReadUInt16();
            ushort dynamicOptionCount = reader.ReadUInt16();
            ushort programCount = reader.ReadUInt16();
            if (reader.Header.VersionMajor < 7)
                reader.ReadUInt16(); //unk 

            shaderModel.StaticKeyLength = reader.ReadByte();
            shaderModel.DynamicKeyLength = reader.ReadByte();

            byte attribCount = reader.ReadByte();
            byte samplerCount = reader.ReadByte();

            if (reader.Header.VersionMajor >= 8)
                reader.ReadByte(); //image count

            byte uniformBlockCount = reader.ReadByte();
            shaderModel.Unknown2 = reader.ReadByte();
            shaderModel.BlockIndices = reader.ReadBytes(4);

            if (reader.Header.VersionMajor >= 8)
                shaderModel.UnknownIndices2 = reader.ReadBytes(11);
            else if (reader.Header.VersionMajor >= 7)
                reader.ReadBytes(4);
            else
                reader.ReadBytes(6);

            long pos = reader.Position;

            //Go into the bnsh file and get the file size
            reader.SeekBegin((long)shaderFileOffset + 0x1C);
            var bnshSize = (int)reader.ReadUInt32();

            var bnshFileStream = new SubStream(reader.BaseStream, shaderFileOffset, bnshSize);
            shaderModel.BnshFile = new BnshFile(bnshFileStream);

            shaderModel.Programs = ReadArray(reader,
                 (ulong)shaderProgramArrayOffset,
                 programCount, ReadBfshaShaderProgram);

            foreach (var program in shaderModel.Programs)
                program.ParentShader = shaderModel;

            shaderModel.KeyTable = reader.ReadCustom(() =>
            {
                int numKeysPerProgram = shaderModel.StaticKeyLength + shaderModel.DynamicKeyLength;

                return reader.ReadInt32s(numKeysPerProgram * shaderModel.Programs.Count);
            }, (ulong)keyTableOffset);

            if (symbolInfoOffset != 0)
            {
                reader.SeekBegin(symbolInfoOffset);
                shaderModel.SymbolData = ReadSymbolTable(reader, shaderModel);
            }

            //Compute variation index for saving
            foreach (var program in shaderModel.Programs)
            {
                var variationStartOffset = shaderFileOffset + 192;
                var relative_offset = (long)program.VariationOffset - variationStartOffset;
                program.VariationIndex = (int)(relative_offset / 64);
            }

            reader.SeekBegin(pos);
            return shaderModel;
        }

        static BfshaSampler ReadSampler(BinaryDataReader reader)
        {
            var annotation = reader.LoadString();
            var index = reader.ReadByte();
            reader.ReadBytes(7); // padding
            return new BfshaSampler() { Annotation = annotation, Index = index };
        }

        static BfshaAttribute ReadAttribute(BinaryDataReader reader)
        {
            var index = reader.ReadByte();
            var location = reader.ReadSByte();
            return new BfshaAttribute() { Location = location, Index = index };
        }

        static ShaderOption ReadShaderOption(BinaryDataReader reader)
        {
            ShaderOption option = new ShaderOption();

            option.Name = reader.LoadString();
            option.Choices = LoadDictionary(reader, reader.ReadUInt64(), 0, ReadResUint);
            var choiceValuesOffset = reader.ReadUInt64();
            ushort choiceCount = reader.ReadUInt16();
            if (reader.Header.VersionMajor >= 9)
            {
                option.DefaultChoiceIdx = reader.ReadInt16();
                option.Padding = reader.ReadUInt16();
                option.BlockOffset = reader.ReadByte();
                option.KeyOffset = reader.ReadByte();
                option.Bit32Mask = reader.ReadUInt32();
                option.Bit32Index = reader.ReadByte();
                option.Bit32Shift = reader.ReadByte();
                uint padding2 = reader.ReadUInt16();
            }
            else
            {
                option.DefaultChoiceIdx = reader.ReadByte();
                option.BlockOffset = reader.ReadUInt16(); // Uniform block offset.
                option.KeyOffset = reader.ReadByte();
                option.Bit32Index = reader.ReadByte();
                option.Bit32Shift = reader.ReadByte();
                option.Bit32Mask = reader.ReadUInt32();
                uint padding = reader.ReadUInt32();
            }

            option.ChoiceValues = reader.ReadCustom(() =>
            {
                return reader.ReadUInt32s(choiceCount);
            }, choiceValuesOffset);
            return option;
        }

        static internal ResUint32 ReadResUint(BinaryDataReader reader) => new ResUint32(reader.ReadUInt32());

        static BfshaStorageBuffer ReadStorageBuffer(BinaryDataReader reader)
        {
            BfshaStorageBuffer block = new BfshaStorageBuffer();
            block.Unknowns = reader.ReadUInt32s(8);
            return block;
        }

        static BfshaImageBuffer ReadImage(BinaryDataReader reader)
        {
            return new BfshaImageBuffer();
        }

        static BfshaUniformBlock ReadBfshaUniformBlock(BinaryDataReader reader)
        {
            BfshaUniformBlock block = new BfshaUniformBlock();
            reader.BaseStream.Read(Utils.AsSpan(ref block.header));

            long pos = reader.BaseStream.Position;

            block.Uniforms = LoadDictionary(reader,
                       block.header.UniformDictionaryOffset,
                       block.header.UniformArrayOffset,
                       ReadBfshaUniform);

            // Read default buffer
            block.DefaultBuffer = reader.ReadCustom(() =>
                reader.ReadBytes(block.header.Size), (uint)block.header.DefaultOffset);

            reader.SeekBegin(pos);
            return block;
        }

        static BfshaUniform ReadBfshaUniform(BinaryDataReader reader)
        {
            return new BfshaUniform()
            {
                Name = reader.LoadString(),
                Index = reader.ReadInt32(),
                DataOffset = reader.ReadUInt16(),
                BlockIndex = reader.ReadByte(),
                Padding = reader.ReadByte(), //padding
            };
        }

        static BfshaShaderProgram ReadBfshaShaderProgram(BinaryDataReader reader)
        {
            BfshaShaderProgram prog = new BfshaShaderProgram();

            if (reader.Header.VersionMajor >= 8)
            {
                var header = new ShaderProgramHeaderV8();
                reader.BaseStream.Read(Utils.AsSpan(ref header));

                prog.VariationOffset = header.VariationOffset;
                prog.UsedAttributeFlags = header.UsedAttributeFlags;
                prog.Flags = header.Flags;

                prog.UniformBlockIndices = ReadArray(reader, header.UniformIndexTableBlockOffset, header.NumBlocks, ReadShaderLocations);
                prog.SamplerIndices = ReadArray(reader, header.SamplerIndexTableOffset, header.NumSamplers, ReadShaderLocations);
                prog.ImageIndices = ReadArray(reader, header.ImageIndexTableOffset, header.NumImages, ReadShaderLocations);
                prog.StorageBufferIndices = ReadArray(reader, header.StorageBufferIndexTableOffset, header.NumStorageBuffers, ReadShaderLocations);
            }
            else if (reader.Header.VersionMajor >= 7)
            {
                var header = new ShaderProgramHeaderV7();
                reader.BaseStream.Read(Utils.AsSpan(ref header));

                prog.VariationOffset = header.VariationOffset;
                prog.UsedAttributeFlags = header.UsedAttributeFlags;
                prog.Flags = header.Flags;

                prog.UniformBlockIndices = ReadArray(reader, header.UniformIndexTableBlockOffset, header.NumBlocks, ReadShaderLocations);
                prog.SamplerIndices = ReadArray(reader, header.SamplerIndexTableOffset, header.NumSamplers, ReadShaderLocations);
                prog.StorageBufferIndices = ReadArray(reader, header.StorageBufferIndexTableOffset, header.NumStorageBuffers, ReadShaderLocations);
            }
            else if (reader.Header.VersionMajor >= 5)
            {
                var header = new ShaderProgramHeaderV5();
                reader.BaseStream.Read(Utils.AsSpan(ref header));

                prog.VariationOffset = header.VariationOffset;
                prog.UsedAttributeFlags = header.UsedAttributeFlags;
                prog.Flags = header.Flags;
                prog.UniformBlockIndices = ReadArray(reader, header.UniformIndexTableBlockOffset, header.NumBlocks, ReadShaderLocations);
                prog.SamplerIndices = ReadArray(reader, header.SamplerIndexTableOffset, header.NumSamplers, ReadShaderLocations);
            }
            else
            {
                var header = new ShaderProgramHeaderV4();
                reader.BaseStream.Read(Utils.AsSpan(ref header));

                prog.VariationOffset = header.VariationOffset;
                prog.UsedAttributeFlags = header.UsedAttributeFlags;
                prog.Flags = header.Flags;

                prog.UniformBlockIndices = ReadArray(reader, header.UniformIndexTableBlockOffset, header.NumBlocks, ReadShaderLocations);
                prog.SamplerIndices = ReadArray(reader, header.SamplerIndexTableOffset, header.NumSamplers, ReadShaderLocations);
            }
            return prog;
        }

        static ShaderIndexHeader ReadShaderLocations(BinaryDataReader reader)
        {
            ShaderIndexHeader header = new ShaderIndexHeader();
            if (reader.Header.VersionMajor >= 9)
            {
                header.VertexLocation = reader.ReadInt32();
                header.FragmentLocation = reader.ReadInt32();
            }
            else
            {
                header.VertexLocation = reader.ReadInt32();
                header.GeoemetryLocation = reader.ReadInt32();
                header.FragmentLocation = (sbyte)reader.ReadInt32();
                header.ComputeLocation = (sbyte)reader.ReadInt32();

                if (reader.Header.VersionMajor == 8)
                {
                    reader.ReadInt32();
                    reader.ReadInt32();
                }
            }
            return header;
        }

        static SymbolData ReadSymbolTable(BinaryDataReader reader, ShaderModel shaderModel)
        {
            SymbolData symbolTable = new();

            if (reader.Header.VersionMajor >= 8)
            {
                symbolTable.Samplers = ReadArray(reader, reader.ReadUInt64(), shaderModel.Samplers.Count, ReadSymbol);
                symbolTable.Images = ReadArray(reader, reader.ReadUInt64(), shaderModel.Images.Count, ReadSymbol);
                symbolTable.UniformBlocks = ReadArray(reader, reader.ReadUInt64(), shaderModel.UniformBlocks.Count, ReadSymbol);
                symbolTable.StorageBuffers = ReadArray(reader, reader.ReadUInt64(), shaderModel.StorageBuffers.Count, ReadSymbol);
            }
            else if (reader.Header.VersionMajor >= 7)
            {
                symbolTable.Samplers = ReadArray(reader, reader.ReadUInt64(), shaderModel.Samplers.Count, ReadSymbol);
                symbolTable.UniformBlocks = ReadArray(reader, reader.ReadUInt64(), shaderModel.UniformBlocks.Count, ReadSymbol);
                symbolTable.StorageBuffers = ReadArray(reader, reader.ReadUInt64(), shaderModel.StorageBuffers.Count, ReadSymbol);
                reader.ReadUInt64();
            }
            else
            {
                symbolTable.Samplers = ReadArray(reader, reader.ReadUInt64(), shaderModel.Samplers.Count, ReadSymbol);
                symbolTable.UniformBlocks = ReadArray(reader, reader.ReadUInt64(), shaderModel.UniformBlocks.Count, ReadSymbol);
                reader.ReadUInt64();
                reader.ReadUInt64();
            }
            return symbolTable;
        }

        static SymbolData.SymbolEntry ReadSymbol(BinaryDataReader reader)
        {
            SymbolData.SymbolEntry symbol = new();
            symbol.Name1 = reader.LoadString();
            if (reader.Header.VersionMajor <= 8)
            {
                symbol.Value1 = reader.LoadString();
                symbol.Name2 = reader.LoadString();
                symbol.Value2 = reader.LoadString();
            }
            if (reader.Header.VersionMajor == 8)
            {
                symbol.Name3 = reader.LoadString();
                symbol.Value3 = reader.LoadString();
            }
            if (reader.Header.VersionMajor == 9)
            {
                symbol.Value1 = reader.LoadString();
            }
            return symbol;
        }

        static List<T> ReadArray<T>(BinaryDataReader reader, ulong offset, int count,
            Func<BinaryDataReader, T> load_section) where T : class, new()
        {
            var start = reader.Position;
            reader.SeekBegin((long)offset);

            T[] values = new T[count];
            for (int i = 0; i < count; i++)
                values[i] = load_section.Invoke(reader);

            reader.SeekBegin(start);
            return values.ToList();
        }

        static internal ResDict<T> LoadDictionary<T>(BinaryDataReader reader, Func<BinaryDataReader, T> load_section) where T : class, IResData, new()
        {
            var valuesOffset = reader.ReadUInt64();
            var dictOffset = reader.ReadUInt64();
            return LoadDictionary(reader, dictOffset, valuesOffset, load_section);
        }

        static internal ResDict<T> LoadDictionary<T>(BinaryDataReader reader,
                  ulong dictOffset, ulong valuesOffset,  Func<BinaryDataReader, T> load_section) where T : class, IResData, new()
        {
            ResDict<T> dict = new ResDict<T>();
            if (dictOffset == 0)
                return dict;

            var start = reader.Position;

            reader.SeekBegin(dictOffset);
            reader.ReadUInt32(); // magic
            int numNodes = reader.ReadInt32(); // Excludes root node.
            for (int i = 0; i < numNodes + 1; i++)
            {
                var refe = reader.ReadUInt32();
                var idxLeft = reader.ReadUInt16();
                var idxRight = reader.ReadUInt16();
                var key = reader.LoadString(reader.ReadUInt64());

                dict._nodes.Add(new ResDict<T>.Node()
                {
                    Reference = refe,
                    IdxLeft = idxLeft,
                    IdxRight = idxRight,
                    Key = key,
                });

                if (i == 0)
                    continue;

                dict.Add(key, null);
            }

            if (valuesOffset != 0)
            {
                reader.SeekBegin(valuesOffset);
                T[] values = new T[numNodes];
                for (int i = 0; i < numNodes; i++)
                    values[i] = load_section.Invoke(reader);

                for (int i = 0; i < values.Length; i++)
                {
                    string key = dict.GetKey(i);
                    dict[key] = values[i];
                }
            }
            reader.SeekBegin(start);
            return dict;
        }
    }
}
