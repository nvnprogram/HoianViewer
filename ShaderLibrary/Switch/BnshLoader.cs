using ShaderLibrary.IO;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ShaderLibrary.Switch
{
    internal class BnshLoader
    {
        internal static void Read(Stream stream, BnshFile bnsh)
        {
            bnsh._stream = stream;
            var reader = new BinaryDataReader(stream, false, true);

            stream.Read(Utils.AsSpan(ref bnsh.BinHeader));
            reader.ReadBytes(64); //padding

            // Apply name offset (- 2 due to string length)
            if (bnsh.BinHeader.NameOffset != 0)
                bnsh.Name = reader.LoadString(bnsh.BinHeader.NameOffset - 2);

            //GRSC header
            reader.BaseStream.Read(Utils.AsSpan(ref bnsh.Header));

            // Variations
            reader.SeekBegin(bnsh.Header.VariationStartOffset);
            for (int i = 0; i < bnsh.Header.NumVariation; i++)
                bnsh.Variations.Add(ReadShaderVariation(reader));
        }

        private static BnshFile.ShaderVariation ReadShaderVariation(BinaryDataReader reader)
        {
            BnshFile.ShaderVariation variation = new BnshFile.ShaderVariation();
            variation._stream = reader.BaseStream;
            // Read only header. Data is read only as needed
            reader.BaseStream.Read(Utils.AsSpan(ref variation.header));
            return variation;
        }

        internal static BnshFile.BnshShaderProgram ReadBnshShaderProgram(BnshFile.ShaderVariation variation)
        {
            BnshFile.BnshShaderProgram program = new();

            var reader = new BinaryDataReader(variation._stream, false, true);
            reader.SeekBegin((int)variation.header.BinaryOffset);

            reader.BaseStream.Read(Utils.AsSpan(ref program.header));
            var pos = reader.BaseStream.Position;

            program.VertexShader = ReadShaderCode(reader, program.header.VertexShaderOffset);
            program.FragmentShader = ReadShaderCode(reader, program.header.FragmentShaderOffset);
            program.GeometryShader = ReadShaderCode(reader, program.header.GeometryShaderOffset);
            program.ComputeShader = ReadShaderCode(reader, program.header.ComputeShaderOffset);
            program.TessellationControlShader = ReadShaderCode(reader, program.header.TessellationControlShaderOffset);
            program.TessellationEvalShader = ReadShaderCode(reader, program.header.TessellationEvalShaderOffset);

            reader.SeekBegin(program.header.ObjectOffset);
            program.MemoryData = reader.ReadBytes((int)program.header.ObjectSize);

            if (program.header.ShaderReflectionOffset != 0)
            {
                reader.SeekBegin(program.header.ShaderReflectionOffset);
                //offsets
                var offsets = reader.ReadUInt64s(6);
                program.VertexShaderReflection = ReadReflectionData(reader, offsets[0]);
                program.TessellationControlShaderReflection = ReadReflectionData(reader, offsets[1]);
                program.TessellationEvalShaderReflection = ReadReflectionData(reader, offsets[2]);
                program.GeometryShaderReflection = ReadReflectionData(reader, offsets[3]);
                program.FragmentShaderReflection = ReadReflectionData(reader, offsets[4]);
                program.ComputeShaderReflection = ReadReflectionData(reader, offsets[5]);
            }

            reader.SeekBegin(pos);

            return program;
        }

        private static BnshFile.ShaderCode ReadShaderCode(BinaryDataReader reader, ulong offset)
        {
            if (offset == 0) return null;
            reader.SeekBegin(offset);

            BnshFile.ShaderCode code = new();
            reader.ReadBytes(8); //always empty
            ulong controlCodeOffset = reader.ReadUInt64();
            ulong byteCodeOffset = reader.ReadUInt64();
            uint byteCodeSize = reader.ReadUInt32();
            uint controlCodeSize = reader.ReadUInt32();
            code.Reserved = reader.ReadBytes(32); //padding

            code.ControlCode = reader.ReadCustom(() =>
            {
                return reader.ReadBytes((int)controlCodeSize);
            }, controlCodeOffset);

            code.ByteCode = reader.ReadCustom(() =>
            {
                return reader.ReadBytes((int)byteCodeSize);
            }, byteCodeOffset);

            return code;
        }

        private static BnshFile.ShaderReflectionData ReadReflectionData(BinaryDataReader reader, ulong offset)
        {
            if (offset == 0) return null;
            reader.SeekBegin(offset);

            BnshFile.ShaderReflectionData reflect = new();

            reader.BaseStream.Read(Utils.AsSpan(ref reflect.header));
            var pos = reader.BaseStream.Position;

            reflect.Inputs = BfshaLoader.LoadDictionary(reader, reflect.header.InputDictionaryOffset, 0, BfshaLoader.ReadResUint);
            reflect.Outputs = BfshaLoader.LoadDictionary(reader, reflect.header.OutputDictionaryOffset, 0, BfshaLoader.ReadResUint);
            reflect.Samplers = BfshaLoader.LoadDictionary(reader, reflect.header.SamplerDictionaryOffset, 0, BfshaLoader.ReadResUint);
            reflect.UniformBuffers = BfshaLoader.LoadDictionary(reader, reflect.header.UniformBufferDictionaryOffset, 0, BfshaLoader.ReadResUint);
            reflect.StorageBuffers = BfshaLoader.LoadDictionary(reader, reflect.header.StorageBufferDictionaryOffset, 0, BfshaLoader.ReadResUint);

            if (reflect.header.SlotCount > 0)
            {
                reflect.Slots = reader.ReadCustom(() => reader.ReadInt32s((int)reflect.header.SlotCount), (uint)reflect.header.SlotOffset);
                reflect.AssignSlots(reflect.Slots);
            }
            reader.SeekBegin(pos);
            return reflect;
        }


    }
}
