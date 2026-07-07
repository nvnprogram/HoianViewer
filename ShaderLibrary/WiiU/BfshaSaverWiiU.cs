using BfshaLibrary.WiiU;
using Microsoft.VisualBasic.FileIO;
using ShaderLibrary.IO;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Threading.Tasks;

namespace ShaderLibrary.WiiU
{
    public class BfshaSaverWiiU
    {
        private long _fileSizePos;
        private long _stringPoolOfsPos;

        private Dictionary<object, List<long>> _savedOffsets = new Dictionary<object, List<long>>();
        private Dictionary<object, long> _savedRefs = new Dictionary<object, long>();

        private Dictionary<string, List<long>> _savedStrings = new Dictionary<string, List<long>>();

        public void Save(BfshaFile bfsha, BinaryDataWriter writer)
        {
            foreach (var str in bfsha.StringPool.Strings)
                _savedStrings.Add(str, new List<long>());

            writer.WriteSignature("FSHA");
            writer.Write((byte)bfsha.BinHeader.VersionMajor);
            writer.Write((byte)bfsha.BinHeader.VersionMicro);
            writer.Write((byte)0);
            writer.Write((byte)bfsha.BinHeader.VersionMinor);

            writer.Write((ushort)bfsha.BinHeader.ByteOrder);
            writer.Write((ushort)0x10);

            //file size for later
            _fileSizePos = writer.Position;
            writer.Write(0);

            writer.Write(bfsha.DataAlignment);
            SaveString(writer, bfsha.Name);

            _stringPoolOfsPos = writer.Position;
            writer.Write(0); //stringPoolSize
            writer.Write(0); //stringPoolOffset

            SaveString(writer, bfsha.Path);

            writer.Write((ushort)bfsha.ShaderModels.Count);
            writer.Write((ushort)bfsha.Flags);
            writer.Write(0);

            SaveDataPointer(writer, bfsha.ShaderModels);
            writer.Write(0);
            writer.Write(0);

            WriteDictionary(writer, bfsha.ShaderModels);

            Dictionary<byte[], List<long>> data_blocks = new Dictionary<byte[], List<long>>();

            //here we try to write in a similar order to original
            foreach (var shaderModel in bfsha.ShaderModels.Values)
            {
                writer.AlignBytes(4);
                WriteShaderModel(writer, shaderModel);

                foreach (var op in shaderModel.StaticOptions.Values)
                    WriteShaderOption(writer, op);
                foreach (var op in shaderModel.DynamicOptions.Values)
                    WriteShaderOption(writer, op);

                foreach (var c in shaderModel.StaticOptions.Values.SelectMany(x => x.Choices.Values))
                    WriteOptionValue(writer, c);
                foreach (var c in shaderModel.DynamicOptions.Values.SelectMany(x => x.Choices.Values))
                    WriteOptionValue(writer, c);

                foreach (var attr in shaderModel.Attributes.Values)
                    WriteShaderAttribute(writer, attr);
                foreach (var sampler in shaderModel.Samplers.Values)
                    WriteShaderSampler(writer, sampler);
                foreach (var block in shaderModel.UniformBlocks.Values)
                    WriteShaderUniformBlock(writer, block);
                foreach (var uniform in shaderModel.UniformBlocks.Values.SelectMany(x => x.Uniforms.Values))
                    WriteShaderUniform(writer, uniform);

                SaveReference(writer, shaderModel.Programs);
                foreach (var prog in shaderModel.Programs)
                    WriteShaderProgram(writer, shaderModel,  prog);

                SaveReference(writer, shaderModel.KeyTable);
                for (int i = 0; i < shaderModel.KeyTable.Length; i++)
                    writer.Write(shaderModel.KeyTable[i]);

                foreach (var block in shaderModel.UniformBlocks.Values)
                {
                    if (block.DefaultBuffer  != null)
                    {
                        SaveReference(writer, block.DefaultBuffer);
                        writer.Write(block.DefaultBuffer);
                    }
                }

                WriteDictionary(writer, shaderModel.StaticOptions);
                WriteDictionary(writer, shaderModel.DynamicOptions);

                WriteDictionary(writer, shaderModel.Attributes);
                WriteDictionary(writer, shaderModel.Samplers);
                WriteDictionary(writer, shaderModel.UniformBlocks);
                foreach (var block in shaderModel.UniformBlocks.Values.Where(x => x.Uniforms.Count > 0))
                    WriteDictionary(writer, block.Uniforms);

                foreach (var op in shaderModel.StaticOptions.Values)
                    WriteDictionary(writer, op.Choices);
                foreach (var op in shaderModel.DynamicOptions.Values)
                    WriteDictionary(writer, op.Choices);

                Dictionary<long, BfshaGX2Header> gx2HeaderLoopOffsets = new Dictionary<long, BfshaGX2Header>();

                void WriteShaderHeader(BfshaGX2Header header)
                {
                    if (header == null) return;

                    writer.AlignBytes(4);
                    var pos = writer.BaseStream.Position;

                    header.UnusedHeader = new uint[header.UnusedHeader.Length];
                    var data = header.ToArray();

                    var data_ofs_pos = 52 * sizeof(uint) + 4;
                    if (header is BfshaGX2PixelHeader)
                        data_ofs_pos = 41 * sizeof(uint) + 4;
                    if (header is BfshaGX2GeometryHeader)
                        data_ofs_pos = 19 * sizeof(uint) + 4;

                    SaveReference(writer, header);
                    writer.Write(data);

                    if (!data_blocks.ContainsKey(header.Data))
                        data_blocks.Add(header.Data, new List<long>());

                    data_blocks[header.Data].Add(pos + data_ofs_pos);

                    gx2HeaderLoopOffsets.Add(pos + data_ofs_pos, header);
                }

                foreach (var program in shaderModel.Programs)
                {
                    WriteLocationList(writer, program.SamplerIndices);
                    WriteLocationList(writer, program.UniformBlockIndices);

                    WriteShaderHeader(program.GX2VertexData);
                    WriteShaderHeader(program.GX2GeometryData);
                    WriteShaderHeader(program.GX2PixelData);

                    foreach (var ofs in gx2HeaderLoopOffsets)
                    {
                        writer.WriteOffset(ofs.Key + 40);
                        foreach (var loop in ofs.Value.Loops)
                        {
                            writer.Write(loop.Offset);
                            writer.Write(loop.Value);
                        }
                    }
                }
            }

            SaveStringTable(writer);

            foreach (var block in data_blocks)
            {
                writer.AlignBytes((int)bfsha.DataAlignment);
                foreach (var ofs in block.Value)
                    writer.WriteOffset(ofs);
                writer.Write(block.Key);
            }

            WriteOffsets(writer);

            writer.AlignBytes((int)bfsha.DataAlignment);

            //file size
            using (writer.BaseStream.TemporarySeek(_fileSizePos, SeekOrigin.Begin)) {
                writer.Write((uint)writer.BaseStream.Length);
            }
        }

        private void WriteLocationList(BinaryDataWriter writer, List<ShaderIndexHeader> indices)
        {
            writer.AlignBytes(4);
            SaveReference(writer, indices);
            foreach (var index in indices)
            {
                writer.Write((sbyte)index.VertexLocation);
                writer.Write((sbyte)index.GeoemetryLocation);
                writer.Write((sbyte)index.FragmentLocation);
            }
            writer.AlignBytes(4);
        }

        private void WriteOffsets(BinaryDataWriter writer)
        {
            foreach (var v in _savedOffsets)
            {
                var target = _savedRefs[v.Key];
                foreach (var ofsPos in v.Value)
                    writer.WriteOffset(ofsPos, target);
            }
        }

        private void WriteShaderModel(BinaryDataWriter writer, ShaderModel shaderModel)
        {
            SaveReference(writer, shaderModel);
            writer.Write((byte)shaderModel.StaticKeyLength);
            writer.Write((byte)shaderModel.DynamicKeyLength);
            writer.Write((ushort)shaderModel.StaticOptions.Count);
            writer.Write((ushort)shaderModel.DynamicOptions.Count);
            writer.Write((ushort)shaderModel.Programs.Count);
            writer.Write((byte)shaderModel.Attributes.Count);
            writer.Write((byte)shaderModel.Samplers.Count);
            writer.Write((byte)shaderModel.UniformBlocks.Count);
            writer.Write((byte)0);
            writer.Write((ushort)shaderModel.MaxVSRingItemSize);
            writer.Write((ushort)shaderModel.MaxRingItemSize);

            writer.Write(shaderModel.UnknownIndices2);
            writer.Write((uint)shaderModel.UniformBlocks.Sum(x => x.Value.Uniforms?.Count));
            writer.Write(shaderModel.BlockIndices);
            writer.Write(shaderModel.DefaultProgramIndex);

            SaveString(writer, shaderModel.Name);
            writer.Write(0);
            writer.Write(0);

            void WriteDictValueOffsets<T>(ResDict<T> value) where T : IResData, new()
            {
                SaveDataPointer(writer, value.Values.FirstOrDefault()); //value offset
                SaveDataPointer(writer, value); //dict offset
            }

            WriteDictValueOffsets(shaderModel.StaticOptions);
            WriteDictValueOffsets(shaderModel.DynamicOptions);
            WriteDictValueOffsets(shaderModel.Attributes);
            WriteDictValueOffsets(shaderModel.Samplers);
            WriteDictValueOffsets(shaderModel.UniformBlocks);

            SaveDataPointer(writer, shaderModel.UniformBlocks.Values.FirstOrDefault(x
                => x.Uniforms?.Count > 0).Uniforms.Values.FirstOrDefault()); //uniform start offset

            SaveDataPointer(writer, shaderModel.Programs); //Program offset
            SaveDataPointer(writer, shaderModel.KeyTable); //KeyTable offset
            writer.Write((int)-writer.BaseStream.Position); //Shader Archive offset
            writer.Write(0);
            writer.Write(0);

            writer.Write(0); //todo symbol data
        }

        private void WriteShaderOption(BinaryDataWriter writer, ShaderOption option)
        {
            SaveReference(writer, option);

            writer.Write((byte)option.Choices.Count);
            writer.Write((byte)option.DefaultChoiceIdx);
            writer.Write((ushort)option.BlockOffset);
            writer.Write((byte)option.Flags);
            writer.Write((byte)option.KeyOffset);
            writer.Write((byte)option.Bit32Index);
            writer.Write((byte)option.Bit32Shift);
            writer.Write((uint)option.Bit32Mask);

            this.SaveString(writer, option.Name);
            SaveDataPointer(writer, option.Choices);
            SaveDataPointer(writer, option.Choices.Values.FirstOrDefault());
        }

        private void WriteOptionValue(BinaryDataWriter writer, ResUint32 value)
        {
            if (value != null)
            {
                SaveReference(writer, value);
                writer.Write(value.Value);
            }
        }

        private void WriteShaderUniformBlock(BinaryDataWriter writer, BfshaUniformBlock block)
        {
            SaveReference(writer, block);

            writer.Write((byte)block.header.Index);
            writer.Write((byte)block.header.Type);
            writer.Write((ushort)block.header.Size);
            writer.Write((ushort)block.Uniforms.Count);
            writer.Write((ushort)0);

            SaveDataPointer(writer, block.Uniforms);
            SaveDataPointer(writer, block.DefaultBuffer);
        }

        private void WriteShaderUniform(BinaryDataWriter writer, BfshaUniform uniform)
        {
            SaveReference(writer, uniform);

            writer.Write((uint)uniform.Index);
            writer.Write((ushort)uniform.GX2Count);
            writer.Write((byte)uniform.GX2Type);
            writer.Write((byte)uniform.BlockIndex);
            writer.Write((ushort)uniform.DataOffset);
            writer.Write((byte)uniform.GX2ParamType);
            writer.Write((byte)0);
            writer.Write(0);
        }

        private void WriteShaderSampler(BinaryDataWriter writer, BfshaSampler sampler)
        {
            SaveReference(writer, sampler);

            writer.Write((byte)sampler.Index);
            writer.Write((byte)sampler.GX2Type);
            writer.Write((byte)sampler.GX2Count);
            writer.Write((byte)0);
            writer.Write(0);
            //SaveString(writer, sampler.Annotation);
        }

        private void WriteShaderAttribute(BinaryDataWriter writer, BfshaAttribute attr)
        {
            SaveReference(writer, attr);

            writer.Write((byte)attr.Index);
            writer.Write((byte)attr.GX2Type);
            writer.Write((byte)attr.GX2Count);
            writer.Write((byte)attr.Location);
        }

        private void WriteShaderProgram(BinaryDataWriter writer, ShaderModel shaderModel, BfshaShaderProgram program)
        {
            if (writer.Header.VersionMajor >= 4)
            {
                throw new Exception($"Version {writer.Header.VersionMajor} not supported!");
            }
            else
            {
                writer.Write((ushort)program.Flags);
                writer.Write((byte)program.SamplerIndices.Count);
                writer.Write((byte)program.UniformBlockIndices.Count);
                writer.Write(program.UsedAttributeFlags);
                for (int i = 0; i < program.GX2Instructions.Length; i++)
                    writer.Write(program.GX2Instructions[i]);

                writer.Write(0); //always 0

                SaveDataPointer(writer, program.SamplerIndices);
                SaveDataPointer(writer, program.UniformBlockIndices);

                SaveDataPointer(writer, program.GX2VertexData);
                SaveDataPointer(writer, program.GX2GeometryData);
                SaveDataPointer(writer, program.GX2PixelData);

                SaveDataPointer(writer, shaderModel);
            }
        }

        private void WriteDictionary<T>(BinaryDataWriter writer, ResDict<T> dict) where T : IResData, new()
        {
            if (dict.Count == 0)
                return;

            writer.AlignBytes(4);
            SaveReference(writer, dict);

            dict.GenerateTreeWiiU();

            var values = dict.Values.ToList();
            var nodes = dict.GetNodes();

            writer.Write(nodes.Count * 16 + 8); //size
            writer.Write(dict.Count); //count

            for (int i = 0; i < nodes.Count; i++) 
            {
                writer.Write(nodes[i].Reference);
                writer.Write(nodes[i].IdxLeft);
                writer.Write(nodes[i].IdxRight);
                if (i > 0)
                {
                    SaveString(writer, nodes[i].Key);
                    SaveDataPointer(writer, values[i - 1]);
                }
                else
                {
                    writer.Write(0);
                    writer.Write(0);
                }
            }
        }

        private void SaveStringTable(BinaryDataWriter writer)
        {
            writer.AlignBytes(4);

            long table_start = writer.Position;
            writer.WriteOffset(_stringPoolOfsPos + 4);

           // var ordered = _savedStrings.OrderBy(x => x.Key).ToList();

            foreach (var str in _savedStrings)
            {
                writer.Write(str.Key.Length);

                foreach (var ofs in str.Value)
                    writer.WriteOffset(ofs);

                writer.Write(Encoding.UTF8.GetBytes(str.Key));
                writer.Write((byte)0);
                writer.AlignBytes(4);
            }

            long table_size = writer.Position - table_start;

            using (writer.BaseStream.TemporarySeek(_stringPoolOfsPos, SeekOrigin.Begin)) {
                writer.Write((uint)table_size);
            }
        }

        private void SaveDataPointer<T>(BinaryDataWriter writer, ResDict<T> value) where T : IResData, new()
        {
            var pos = writer.Position;
            writer.Write(0U);

            if (value != null && value.Count > 0)
            {
                if (!_savedOffsets.ContainsKey(value))
                    _savedOffsets.Add(value, new List<long>());

                _savedOffsets[value].Add(pos);
            }
        }

        private void SaveDataPointer(BinaryDataWriter writer, IEnumerable<object> value)
        {
            var pos = writer.Position;
            writer.Write(0U);

            if (value != null && value.ToList().Count > 0)
            {
                if (!_savedOffsets.ContainsKey(value))
                    _savedOffsets.Add(value, new List<long>());

                _savedOffsets[value].Add(pos);
            }
        }

        private void SaveDataPointer(BinaryDataWriter writer, object value)
        {
            var pos = writer.Position;
            writer.Write(0U);

            if (value != null)
            {
                if (!_savedOffsets.ContainsKey(value))
                    _savedOffsets.Add(value, new List<long>());

                _savedOffsets[value].Add(pos);
            }
        }

        private void SaveReference(BinaryDataWriter writer, object value)
        {
            if (value == null)
                return;

            if (!_savedRefs.ContainsKey(value))
                _savedRefs.Add(value, writer.Position);
        }

        private void SaveString( BinaryDataWriter writer, string value)
        {
            var pos = writer.Position;
            writer.Write(0U);
            if (value != null)
            {
                if (!_savedStrings.ContainsKey(value))
                    _savedStrings.Add(value, new List<long>());

                _savedStrings[value].Add(pos);
            }
        }

        class ByteArrayComparer : IEqualityComparer<byte[]>
        {
            public bool Equals(byte[] left, byte[] right)
            {
                if (left == null || right == null)
                {
                    return left == right;
                }
                return left.SequenceEqual(right);
            }
            public int GetHashCode(byte[] key)
            {
                if (key == null)
                    throw new ArgumentNullException("key");
                return key.Sum(b => b);
            }
        }
    }
}
