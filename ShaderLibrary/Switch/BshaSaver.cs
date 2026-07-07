using ShaderLibrary.Common;
using ShaderLibrary.IO;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace ShaderLibrary
{
    internal class BfshaSaver
    {
        public string Name { get; set; }

        private const string _bfshasignature = "FSHA";

        public RelocationTable RelocationTable = new RelocationTable(2);
        public StringTable StringTable = new StringTable();

        private long _fileSizePos;

        public void Save(BfshaFile bfsha, BinaryDataWriter writer)
        {
            writer.RelocationTable = this.RelocationTable;
            writer.StringTable = this.StringTable;

            writer.WriteSignature(_bfshasignature);
            writer.Write(0x20202020);
            writer.Write(bfsha.BinHeader.VersionMicro);
            writer.Write(bfsha.BinHeader.VersionMinor);
            writer.Write(bfsha.BinHeader.VersionMajor);
            writer.Write(bfsha.BinHeader.ByteOrder);
            writer.Write((byte)bfsha.BinHeader.Alignment);
            writer.Write((byte)bfsha.BinHeader.TargetAddressSize);
            SaveFileNameString(writer, bfsha.Name);
            writer.Write(2324692992); //unk
            RelocationTable.SaveHeaderOffset(writer);
            _fileSizePos = writer.BaseStream.Position;
            writer.Write(0u);

            RelocationTable.SaveEntry(writer, 2, 1, 0, 0, "String Pool");

            writer.Write((ulong)56); //offset to header
            var string_pool_offset = writer.SaveOffset();
            writer.Write(0UL); //string size

            RelocationTable.SaveEntry(writer, 4, 1, 0, 0, "Header");

            writer.SaveString(bfsha.Name);
            writer.SaveString(bfsha.Path);

            var shader_model_offset = writer.SaveOffset();
            var shader_dict_offset = writer.SaveOffset();
            writer.Write(new byte[24]); //0
            if (bfsha.BinHeader.VersionMajor >= 7) //padding
                writer.Write(0UL); //padding

            writer.Write((ushort)bfsha.ShaderModels.Count);
            writer.Write((ushort)0x1A);
            writer.Write(0); //padding

            List<ShaderModelOffset> saved_models = new List<ShaderModelOffset>();

            writer.WriteOffset(shader_model_offset);
            foreach (var model in bfsha.ShaderModels.Values)
            {
                var start_pos = writer.Position;

                ShaderModelOffset sm = new ShaderModelOffset();
                saved_models.Add(sm);

                sm.Position = start_pos;

                writer.SaveString(model.Name);
                sm.static_option_offset = writer.SaveOffset();
                sm.static_dict_option_offset = writer.SaveOffset();
                sm.dynamic_option_offset = writer.SaveOffset();
                sm.dynamic_dict_option_offset = writer.SaveOffset();
                sm.attribute_offset = writer.SaveOffset();
                sm.attribute_dict_offset = writer.SaveOffset();
                sm.sampler_offset = writer.SaveOffset();
                sm.sampler_dict_offset = writer.SaveOffset();

                if (bfsha.BinHeader.VersionMajor >= 8) //image section
                {
                    sm.image_offset = writer.SaveOffset();
                    sm.image_dict_offset = writer.SaveOffset();
                }

                sm.uniform_block_offset = writer.SaveOffset();
                sm.uniform_block_dict_offset = writer.SaveOffset();
                sm.uniform_offset = writer.SaveOffset();

                if (bfsha.BinHeader.VersionMajor >= 7) //storage blocks used by TOTK
                {
                    sm.storage_block_offset = writer.SaveOffset();
                    sm.storage_block_dict_offset = writer.SaveOffset();
                    writer.SaveOffset(); //unk
                }

                sm.shader_program_offset = writer.SaveOffset();
                sm.key_searcher_offset = writer.SaveOffset();
                writer.Write((ulong)56); //archive_offset
                sm.symbols_offset = writer.SaveOffset();

                var num_ofs = (uint)(writer.Position - start_pos) / 8;
                RelocationTable.SaveEntry(writer, (uint)start_pos, num_ofs, 1, 0, 0, "ShaderModel");

                RelocationTable.SaveEntry(writer, 1, 1, 0, 1, "BNSH Offset");

                sm.bnsh_offset = writer.SaveOffset();
                writer.Write((ulong)0); //unk
                writer.Write((ulong)0); //unk
                writer.Write((ulong)0); //unk

                if (bfsha.BinHeader.VersionMajor >= 7)
                {
                    writer.Write((ulong)0); //unk
                    writer.Write((ulong)0); //unk
                }

                writer.Write(model.UniformBlocks.Values.Sum(x => x.Uniforms.Count));

                if (bfsha.BinHeader.VersionMajor >= 7)
                    writer.Write(model.StorageBuffers.Count);

                writer.Write(model.DefaultProgramIndex);

                writer.Write((ushort)model.StaticOptions.Count);
                writer.Write((ushort)model.DynamicOptions.Count);
                writer.Write((ushort)model.Programs.Count);

                if (bfsha.BinHeader.VersionMajor < 7)
                    writer.Write((ushort)0);

                writer.Write((byte)model.StaticKeyLength);
                writer.Write((byte)model.DynamicKeyLength);
                writer.Write((byte)model.Attributes.Count);
                writer.Write((byte)model.Samplers.Count);

                if (bfsha.BinHeader.VersionMajor >= 8)
                    writer.Write((byte)model.Images.Count); //image count

                writer.Write((byte)model.UniformBlocks.Count);
                writer.Write((byte)model.Unknown2);
                writer.Write(model.BlockIndices); //4 bytes

                if (bfsha.BinHeader.VersionMajor >= 8)
                    writer.Write(model.UnknownIndices2); //padding
                else if (bfsha.BinHeader.VersionMajor >= 7)
                    writer.Write(uint.MaxValue); //padding
                else
                    writer.Write(new byte[6]); //padding
            }

            writer.WriteDictionary(bfsha.ShaderModels, shader_dict_offset);

            for (int i = 0; i < bfsha.ShaderModels.Count; i++)
            {
                var model = bfsha.ShaderModels[i];
                var ofs_list = saved_models[i];

                RelocationTable.SaveEntry(writer, 3,
                    (uint)model.StaticOptions.Count, 2, 0, "Static Options");

                writer.AlignBytes(8);
                writer.WriteOffset(ofs_list.static_option_offset);

                void SaveOption(ShaderOption op)
                {
                    writer.SaveString(op.Name);
                    op._choiceDictOfsPos = writer.SaveOffset();
                    op._choiceValuesOfsPos = writer.SaveOffset();

                    writer.Write((ushort)op.Choices.Count);

                    if (bfsha.BinHeader.VersionMajor >= 9)
                    {
                        writer.Write((ushort)op.DefaultChoiceIdx);
                        writer.Write((ushort)0);

                        writer.Write((byte)op.BlockOffset);
                        writer.Write((byte)op.KeyOffset);
                        writer.Write(op.Bit32Mask);

                        writer.Write((byte)op.Bit32Index);
                        writer.Write((byte)op.Bit32Shift);

                        writer.Write((ushort)0); //padding
                    }
                    else
                    {
                        writer.Write((ushort)op.BlockOffset);

                        writer.Write((byte)op.DefaultChoiceIdx);
                        writer.Write((byte)op.KeyOffset);
                        writer.Write((byte)op.Bit32Index);
                        writer.Write((byte)op.Bit32Shift);

                        writer.Write(op.Bit32Mask);
                        writer.Write(0); //padding
                    }
                }

                foreach (var op in model.StaticOptions.Values)
                    SaveOption(op);

                writer.AlignBytes(8);
                writer.WriteOffset(ofs_list.dynamic_option_offset);

                RelocationTable.SaveEntry(writer, 3,
                      (uint)model.DynamicOptions.Count, 2, 0, "Dynamic Options");

                foreach (var op in model.DynamicOptions.Values)
                    SaveOption(op);

                writer.AlignBytes(8);
                writer.WriteOffset(ofs_list.attribute_offset);

                foreach (var att in model.Attributes.Values)
                {
                    writer.Write((byte)att.Index);
                    writer.Write((byte)att.Location);
                }

                writer.AlignBytes(8);
                writer.WriteOffset(ofs_list.sampler_offset);

                RelocationTable.SaveEntry(writer, 1,
                    (uint)model.Samplers.Count, 1, 0, "Samplers");

                foreach (var sampler in model.Samplers.Values)
                {
                    writer.SaveString(sampler.Annotation);
                    writer.Write((byte)sampler.Index);
                    writer.Write(new byte[7]);
                }

                writer.AlignBytes(8);
                writer.WriteOffset(ofs_list.uniform_block_offset);

                RelocationTable.SaveEntry(writer, 3,
                   (uint)model.UniformBlocks.Count, 1, 0, "Uniform Blocks");

                foreach (var uniformBlocks in model.UniformBlocks.Values)
                {
                    uniformBlocks._uniformVarOfsPos = writer.SaveOffset();
                    uniformBlocks._uniformVarDictOfsPos = writer.SaveOffset();
                    uniformBlocks._defaultDataOfsPos = writer.SaveOffset();

                    writer.Write((byte)uniformBlocks.Index);
                    writer.Write((byte)uniformBlocks.Type);
                    writer.Write((ushort)uniformBlocks.Size);
                    writer.Write((ushort)uniformBlocks.Uniforms.Count);
                    writer.Write((ushort)0);
                }

                if (bfsha.BinHeader.VersionMajor >= 7)
                {
                    writer.AlignBytes(8);
                    writer.WriteOffset(ofs_list.storage_block_offset);

                    foreach (var storageBuffer in model.StorageBuffers?.Values)
                    {
                        writer.Write(storageBuffer.Unknowns);
                    }
                }


                writer.AlignBytes(8);
                writer.WriteOffset(ofs_list.uniform_offset);

                var num_uniforms =
                   (uint)model.UniformBlocks.Values.Sum(x => x.Uniforms.Count);

                RelocationTable.SaveEntry(writer, 1, num_uniforms, 1, 0, "Uniform Vars");

                foreach (var uniformBlocks in model.UniformBlocks.Values)
                {
                    writer.WriteOffset(uniformBlocks._uniformVarOfsPos);
                    foreach (var uniform in uniformBlocks.Uniforms.Values)
                    {
                        writer.SaveString(uniform.Name);
                        writer.Write(uniform.Index);
                        writer.Write((ushort)uniform.DataOffset);
                        writer.Write((byte)uniform.BlockIndex);
                        writer.Write((byte)0);
                    }
                }

                writer.AlignBytes(8);
                writer.WriteOffset(ofs_list.shader_program_offset);

                uint num_offsets = 2;
                if (bfsha.BinHeader.VersionMajor >= 8)
                    num_offsets = 4;
                else if (bfsha.BinHeader.VersionMajor >= 7)
                    num_offsets = 3;

                uint num_padding = 5;
                if (bfsha.BinHeader.VersionMajor >= 8)
                    num_padding = 7;
                else if (bfsha.BinHeader.VersionMajor >= 7)
                    num_padding = 6;
                else if (bfsha.BinHeader.VersionMajor == 5)
                    num_padding = 6;

                RelocationTable.SaveEntry(writer, num_offsets,
                          (uint)model.Programs.Count, 4, 0, "Shader Programs");

                RelocationTable.SaveEntry(writer, (uint)writer.Position + (num_offsets * 8), 1,
                          (uint)model.Programs.Count, num_padding, 1, "Shader Variations");

                RelocationTable.SaveEntry(writer, (uint)writer.Position + (num_offsets * 8) + 8, 1,
                        (uint)model.Programs.Count, num_padding, 0, "Shader Program model pos");

                foreach (var prog in model.Programs)
                {
                    if (bfsha.BinHeader.VersionMajor >= 8)
                    {
                        prog._samplerTableOfsPos = writer.SaveOffset();
                        prog._imageTableOfsPos = writer.SaveOffset();
                        prog._uniformBlockTableOfsPos = writer.SaveOffset();
                        prog._storageBlockTableOfsPos = writer.SaveOffset();
                        prog._shaderVariationOfsPos = writer.SaveOffset(); //from bnsh
                        writer.Write(ofs_list.Position); //shader model pos
                        writer.Write(prog.UsedAttributeFlags);
                        writer.Write((ushort)prog.Flags);
                        writer.Write((ushort)prog.SamplerIndices.Count);
                        writer.Write((ushort)prog.ImageIndices.Count);
                        writer.Write((ushort)prog.UniformBlockIndices.Count);
                        writer.Write((ushort)prog.UniformBlockIndices.Count);
                        writer.Write(new byte[2]);
                    }
                    else if (bfsha.BinHeader.VersionMajor >= 7)
                    {
                        prog._samplerTableOfsPos = writer.SaveOffset();
                        prog._uniformBlockTableOfsPos = writer.SaveOffset();
                        prog._storageBlockTableOfsPos = writer.SaveOffset();
                        prog._shaderVariationOfsPos = writer.SaveOffset(); //from bnsh
                        writer.Write(ofs_list.Position); //shader model pos
                        writer.Write(prog.UsedAttributeFlags);
                        writer.Write((ushort)prog.Flags);
                        writer.Write((ushort)prog.SamplerIndices.Count);
                        writer.Write((ushort)prog.UniformBlockIndices.Count);
                        writer.Write((ushort)prog.StorageBufferIndices.Count);
                        writer.Write(new byte[4]);
                    }
                    else if (bfsha.BinHeader.VersionMajor >= 5)
                    {
                        prog._samplerTableOfsPos = writer.SaveOffset();
                        prog._uniformBlockTableOfsPos = writer.SaveOffset();
                        prog._shaderVariationOfsPos = writer.SaveOffset(); //from bnsh
                        writer.Write(ofs_list.Position); //shader model pos
                        writer.Write(prog.UsedAttributeFlags);
                        writer.Write((ushort)prog.Flags);
                        writer.Write((ushort)prog.SamplerIndices.Count);
                        writer.Write((ushort)prog.UniformBlockIndices.Count);
                        writer.Write(new byte[6]);

                        RelocationTable.SaveEntry(writer, (uint)writer.Position, 1, 1, 0, 0, "Shader Program v5 unk offset");
                        writer.Write(new byte[8]); //version 5 is weird, write this unused pointer 
                    }
                    else
                    {
                        prog._samplerTableOfsPos = writer.SaveOffset();
                        prog._uniformBlockTableOfsPos = writer.SaveOffset();
                        prog._shaderVariationOfsPos = writer.SaveOffset(); //from bnsh
                        writer.Write(ofs_list.Position); //shader model pos
                        writer.Write(prog.UsedAttributeFlags);
                        writer.Write((ushort)prog.Flags);
                        writer.Write((ushort)prog.SamplerIndices.Count);
                        writer.Write((ushort)prog.UniformBlockIndices.Count);
                        writer.Write(new byte[6]);
                    }
                }

                writer.AlignBytes(8);
                writer.WriteOffset(ofs_list.key_searcher_offset);
                writer.Write(model.KeyTable);

                foreach (var uniformBlocks in model.UniformBlocks.Values)
                {
                    if (uniformBlocks.DefaultBuffer?.Length > 0)
                    {
                        writer.AlignBytes(8);
                        writer.WriteOffset(uniformBlocks._defaultDataOfsPos);
                        writer.Write(uniformBlocks.DefaultBuffer);
                    }
                }

                if (model.SymbolData != null)
                {
                    writer.AlignBytes(8);
                    writer.WriteOffset(ofs_list.symbols_offset);

                    RelocationTable.SaveEntry(writer, 2, 1, 0, 0, "Symbol List");

                    long samplerSymbolsOfsPos = 0;
                    long imageSymbolsOfsPos = 0;
                    long uniformBlockSymbolsOfsPos = 0;
                    long storageBlockSymbolsOfsPos = 0;

                    if (bfsha.BinHeader.VersionMajor >= 8)
                    {
                        samplerSymbolsOfsPos = writer.SaveOffset();
                        imageSymbolsOfsPos = writer.SaveOffset();
                        uniformBlockSymbolsOfsPos = writer.SaveOffset();
                        storageBlockSymbolsOfsPos = writer.SaveOffset();
                    }
                    else if (bfsha.BinHeader.VersionMajor >= 7)
                    {
                        samplerSymbolsOfsPos = writer.SaveOffset();
                        uniformBlockSymbolsOfsPos = writer.SaveOffset();
                        storageBlockSymbolsOfsPos = writer.SaveOffset();
                        writer.Write(0UL);
                    }
                    else
                    {
                        samplerSymbolsOfsPos = writer.SaveOffset();
                        uniformBlockSymbolsOfsPos = writer.SaveOffset();
                        writer.Write(0UL);
                        writer.Write(0UL);
                    }
                
                    uint num_symbol_offsets = 4;
                    if (bfsha.BinHeader.VersionMajor > 8)
                        num_symbol_offsets = 1;
                    else if (bfsha.BinHeader.VersionMajor == 8)
                        num_symbol_offsets = 6;

                    void SaveSymbol(SymbolData.SymbolEntry entry)
                    {
                         writer.SaveString(entry.Name1);
                        if (bfsha.BinHeader.VersionMajor <= 8)
                        {
                            writer.SaveString(entry.Value1);
                            writer.SaveString(entry.Name2);
                            writer.SaveString(entry.Value2);
                        }
                        if (bfsha.BinHeader.VersionMajor == 8)
                        {
                            writer.SaveString(entry.Value3);
                            writer.SaveString(entry.Name3);
                        }
                        if (bfsha.BinHeader.VersionMajor == 9)
                        {
                            writer.SaveString(entry.Value1);
                        }
                    }

                    if (model.SymbolData.Samplers.Count > 0)
                    {
                        writer.WriteOffset(samplerSymbolsOfsPos);

                        RelocationTable.SaveEntry(writer, (uint)writer.Position, num_symbol_offsets,
                                (uint)model.SymbolData.Samplers.Count, 0, 0, "Samplers Symbols");

                        foreach (var symbol in model.SymbolData.Samplers)
                            SaveSymbol(symbol);
                    }

                    if (model.SymbolData.UniformBlocks.Count > 0)
                    {
                        writer.WriteOffset(uniformBlockSymbolsOfsPos);

                        RelocationTable.SaveEntry(writer, (uint)writer.Position, num_symbol_offsets,
                            (uint)model.SymbolData.UniformBlocks.Count, 0, 0, "UniformBlocks Symbols");

                        foreach (var symbol in model.SymbolData.UniformBlocks)
                            SaveSymbol(symbol);
                    }

                    if (bfsha.BinHeader.VersionMajor >= 7 && model.SymbolData.StorageBuffers.Count > 0)
                    {
                        writer.WriteOffset(storageBlockSymbolsOfsPos);

                        RelocationTable.SaveEntry(writer, (uint)writer.Position, num_symbol_offsets,
                           (uint)model.SymbolData.StorageBuffers.Count, 0, 0, "StorageBuffer Symbols");

                        foreach (var symbol in model.SymbolData.StorageBuffers)
                            SaveSymbol(symbol);
                    }
                }

                writer.WriteDictionary(model.StaticOptions, ofs_list.static_dict_option_offset);
                writer.WriteDictionary(model.DynamicOptions, ofs_list.dynamic_dict_option_offset);
                writer.WriteDictionary(model.Attributes, ofs_list.attribute_dict_offset);
                writer.WriteDictionary(model.Samplers, ofs_list.sampler_dict_offset);
                writer.WriteDictionary(model.UniformBlocks, ofs_list.uniform_block_dict_offset);

                if (bfsha.BinHeader.VersionMajor >= 7)
                    writer.WriteDictionary(model.StorageBuffers, ofs_list.storage_block_dict_offset);

                foreach (var block in model.UniformBlocks.Values)
                    writer.WriteDictionary(block.Uniforms, block._uniformVarDictOfsPos);

                foreach (var op in model.StaticOptions.Values)
                    writer.WriteDictionary(op.Choices, op._choiceDictOfsPos);

                foreach (var op in model.DynamicOptions.Values)
                    writer.WriteDictionary(op.Choices, op._choiceDictOfsPos);

                foreach (var prog in model.Programs)
                {
                    void SaveLocations(List<ShaderIndexHeader> locations, long ofs)
                    {
                        if (locations.Count == 0)
                            return;

                        writer.AlignBytes(8);
                        writer.WriteOffset(ofs);
                        foreach (var loc in locations)
                        {
                            if (bfsha.BinHeader.VersionMajor >= 9)
                            {
                                writer.Write((int)loc.VertexLocation);
                                writer.Write((int)loc.FragmentLocation);
                            }
                            else
                            {
                                writer.Write((int)loc.VertexLocation);
                                writer.Write((int)loc.GeoemetryLocation);
                                writer.Write((int)loc.FragmentLocation);
                                writer.Write((int)loc.ComputeLocation);

                                if (bfsha.BinHeader.VersionMajor >= 8)
                                {
                                    writer.Write((int)-1); //tess shader?
                                    writer.Write((int)-1); //tess shader?
                                }
                            }
                        }
                    }

                    SaveLocations(prog.SamplerIndices, prog._samplerTableOfsPos);
                    if (bfsha.BinHeader.VersionMajor >= 8)
                        SaveLocations(prog.ImageIndices, prog._imageTableOfsPos);
                    SaveLocations(prog.UniformBlockIndices, prog._uniformBlockTableOfsPos);
                    if (bfsha.BinHeader.VersionMajor >= 7)
                        SaveLocations(prog.StorageBufferIndices, prog._storageBlockTableOfsPos);
                }
            }

            var pos = writer.Position;

            writer.WriteOffset(string_pool_offset);
            StringTable.Write(writer);

            var size = writer.Position - pos;

            using (writer.BaseStream.TemporarySeek(string_pool_offset + 8, System.IO.SeekOrigin.Begin))
            {
                writer.Write((uint)size);
            }

            //BNSH file
            writer.AlignBytes(4096);

            var pos2 = writer.BaseStream.Position;

            for (int i = 0; i < bfsha.ShaderModels.Count; i++)
            {
                var model = bfsha.ShaderModels[i];
                var ofs_list = saved_models[i];

                //BNSH file
                writer.AlignBytes(4096);

                if (i == 0)
                {
                    RelocationTable.SetRelocationSection(0, 0, (uint)writer.BaseStream.Position);
                    pos2 = writer.BaseStream.Position;
                }

                writer.WriteOffset(ofs_list.bnsh_offset);

                var shader_pos = writer.BaseStream.Position;

                var mem = new MemoryStream();
                model.BnshFile.Save(mem);
                writer.Write(mem.ToArray());

                var variationStartOffset = 192;

                //Adjust all variation offsets
                for (int j = 0; j < model.Programs.Count; j++)
                {
                    var program = model.Programs[j];
                    using (writer.BaseStream.TemporarySeek(shader_pos + variationStartOffset + (program.VariationIndex * 64), System.IO.SeekOrigin.Begin))
                    {
                        writer.WriteOffset(program._shaderVariationOfsPos);
                    }
                }
            }

            writer.AlignBytes(1024);

            RelocationTable.SetRelocationSection(1, (uint)pos2, (uint)(writer.BaseStream.Position - pos2));

            RelocationTable.Write(writer);
            writer.WriteHeaderBlocks();

            //file size
            using (writer.BaseStream.TemporarySeek(this._fileSizePos, SeekOrigin.Begin))
            {
                writer.Write((uint)writer.BaseStream.Length);
            }
        }

        private void SaveFileNameString(BinaryWriter writer, string name)
        {
            var pos = writer.BaseStream.Position;

            writer.Write(0u); //uint size
            StringTable.AddFileNameEntry(pos, name);
        }


        class ShaderModelOffset
        {
            public long Position;

            public long static_option_offset;
            public long static_dict_option_offset;
            public long dynamic_option_offset;
            public long dynamic_dict_option_offset;
            public long attribute_offset;
            public long attribute_dict_offset;
            public long image_offset;
            public long image_dict_offset;
            public long sampler_offset;
            public long sampler_dict_offset;
            public long uniform_block_offset;
            public long uniform_block_dict_offset;
            public long uniform_offset;
            public long storage_block_offset;
            public long storage_block_dict_offset;
            public long shader_program_offset;
            public long key_searcher_offset;
            public long symbols_offset;
            public long bnsh_offset;
        }
    }
}
