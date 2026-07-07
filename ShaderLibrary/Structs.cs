using Microsoft.VisualBasic;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace ShaderLibrary
{
    [StructLayout(LayoutKind.Sequential, Size = 0x10)]
    public struct BinaryHeader //A header shared between bntx and other formats
    {
        public ulong Magic; //MAGIC + padding

        public byte VersionMicro;
        public byte VersionMinor;
        public ushort VersionMajor;

        public ushort ByteOrder;
        public byte Alignment;
        public byte TargetAddressSize;
        public uint NameOffset;
        public ushort Flag;
        public ushort BlockOffset;
        public uint RelocationTableOffset;
        public uint FileSize;
    }

    [StructLayout(LayoutKind.Sequential, Size = 0x10)]
    public struct BufferMemoryPool
    {
        public uint Flag;
        public uint Size;
        public ulong Offset;

        public ulong Reserved1;
        public ulong Reserved2;
    }

    [StructLayout(LayoutKind.Sequential, Size = 0x10)]
    public struct BfshaHeader
    {
        public ulong ShaderArchiveOffset; 
        public ulong StringPoolOffset;
        public uint StringPoolSize;
        public uint Padding;

        public ulong NameOffset;
        public ulong PathOffset;
        public ulong ShaderModelOffset;
        public ulong ShaderModelDictionaryOffset;
        public ulong UserPointer;
        public ulong Unknown2;
        public ulong Unknown3;
        public ushort NumShaderModels;
        public ushort Flags;
        public uint Padding2;
    }

    [StructLayout(LayoutKind.Sequential, Size = 0x10)]
    public struct ShaderModelHeader
    {
        public ulong NameOffset;
        public ulong StaticOptionsArrayOffset;
        public ulong StaticOptionsDictionaryOffset;
        public ulong DynamicOptionsArrayOffset;
        public ulong DynamicOptionsDictionaryOffset;
        public ulong AttributesArrayOffset;
        public ulong AttributesDictionaryOffset;
        public ulong SamplerArrayOffset;
        public ulong SamplerDictionaryOffset;

        //Guess? Haven't seen image types used yet
        //For version >= 8
        //
        public ulong ImageArrayOffset;
        public ulong ImageDictionaryOffset;
        //

        public ulong UniformBlockArrayOffset;
        public ulong UniformBlockDictionaryOffset;
        public ulong UniformArrayOffset;

        //For version >= 7
        //
        public ulong StorageBlockArrayOffset;
        public ulong StorageBlockDictionaryOffset;
        public ulong Unknown0;
        //

        public ulong ShaderProgramArrayOffset;
        public ulong KeyTableOffset;

        public ulong ParentArchiveOffset;

        public ulong Unknown1;
        public ulong BnshOffset;

        public ulong Unknown2;
        public ulong Unknown3;
        public ulong Unknown4;

        //For version >= 7
        //
        public ulong Unknown5;
        public ulong Unknown6;
        //

        public uint NumUniforms;
        public uint NumStorageBlocks; //version >= 7

        public int DefaultProgramIndex;

        public ushort NumStaticOptions;
        public ushort NumDynamicOptions;
        public ushort NumShaderPrograms;

        public byte StaticKeyLength;
        public byte DynamicKeyLength;

        public byte NumAttributes;
        public byte NumSamplers;
        public byte NumImages; //version >= 8
        public byte NumUniformBlocks;
        public byte Unknown;

        public uint Unknown8;

        public ulong Padding1;
    }

    [StructLayout(LayoutKind.Sequential, Size = 0x10)]
    public struct ShaderOptionHeader
    {
        public ulong NameOffset;
        public ulong ChoiceDictionaryOffset;
        public ulong ChoiceArrayOffset;

        public ushort NumChoices;
        public ushort BlockOffset;
        public ushort Padding;
        public byte Flag;
        public byte KeyOffset;

        public uint Bit32Mask;
        public byte Bit32Index;
        public byte Bit32Shift;
        public ushort Padding2;
    }

    [StructLayout(LayoutKind.Sequential, Size = 0x10)]
    public struct ShaderProgramHeaderV4
    {
        public ulong SamplerIndexTableOffset;
        public ulong UniformIndexTableBlockOffset;
        public ulong VariationOffset;
        public ulong ParentModelOffset;

        public uint UsedAttributeFlags; //bits for what is used
        public ushort Flags;
        public ushort NumSamplers;

        public ushort NumBlocks;
        public ushort Padding1;
        public ushort Padding2;
        public ushort Padding3;
    }

    [StructLayout(LayoutKind.Sequential, Size = 0x10)]
    public struct ShaderProgramHeaderV5
    {
        public ulong SamplerIndexTableOffset;
        public ulong UniformIndexTableBlockOffset;
        public ulong VariationOffset;
        public ulong ParentModelOffset;

        public uint UsedAttributeFlags; //bits for what is used
        public ushort Flags;
        public ushort NumSamplers;

        public ushort NumBlocks;
        public ushort Padding1;
        public ushort Padding2;
        public ushort Padding3;

        public ulong UnknownOffset;
    }

    [StructLayout(LayoutKind.Sequential, Size = 0x10)]
    public struct ShaderProgramHeaderV7
    {
        public ulong SamplerIndexTableOffset;
        public ulong UniformIndexTableBlockOffset;
        public ulong StorageBufferIndexTableOffset;
        public ulong VariationOffset;
        public ulong ParentModelOffset;

        public uint UsedAttributeFlags; //bits for what is used
        public ushort Flags;
        public ushort NumSamplers;

        public ushort NumBlocks;
        public ushort NumStorageBuffers;
        public ushort Padding1;
        public ushort Padding2;
    }

    [StructLayout(LayoutKind.Sequential, Size = 0x10)]
    public struct ShaderProgramHeaderV8
    {
        public ulong SamplerIndexTableOffset;
        public ulong ImageIndexTableOffset;
        public ulong UniformIndexTableBlockOffset;
        public ulong StorageBufferIndexTableOffset;
        public ulong VariationOffset;
        public ulong ParentModelOffset;

        public uint UsedAttributeFlags; //bits for what is used
        public ushort Flags;
        public ushort NumSamplers;

        public ushort NumImages;
        public ushort NumBlocks;
        public ushort NumStorageBuffers;
        public ushort Padding;
    }

    [StructLayout(LayoutKind.Sequential, Size = 0x10)]
    public struct ShaderUniformBlockHeader
    {
        public ulong UniformArrayOffset;
        public ulong UniformDictionaryOffset;
        public ulong DefaultOffset;

        public byte Index;
        public byte Type;
        public ushort Size;
        public ushort NumUniforms;
        public ushort Padding;
    }

    [StructLayout(LayoutKind.Sequential, Size = 0x10)]
    public struct BnshHeader
    {
        public uint Magic;
        public uint BlockOffset;
        public uint BlockSize;
        public uint Padding;

        public ushort ApiType;
        public ushort ApiVersion;

        public uint CodeTarget;
        public uint CompilerVersion;

        public uint NumVariation;
        public ulong VariationStartOffset;
        public ulong MemoryPoolOffset;

        public ulong Unknown2;
    }

    [StructLayout(LayoutKind.Sequential, Size = 0x10)]
    public struct VariationHeader
    {
        public ulong Offset1;
        public ulong Offset2;
        public ulong BinaryOffset;
        public ulong ParentBnshOffset;

        public ulong Padding1;
        public ulong Padding2;
        public ulong Padding3;
        public ulong Padding4;
    }

    [StructLayout(LayoutKind.Sequential, Size = 0x10)]
    public struct BnshShaderProgramHeader
    {
        public byte Flags;
        public byte CodeType;
        public byte Format;
        public byte Padding;
        public uint BinaryFormat;

        public ulong VertexShaderOffset;
        public ulong TessellationControlShaderOffset;
        public ulong TessellationEvalShaderOffset;
        public ulong GeometryShaderOffset;
        public ulong FragmentShaderOffset;
        public ulong ComputeShaderOffset;

        public ulong Reserved0;
        public ulong Reserved1;
        public ulong Reserved2;
        public ulong Reserved3;
        public ulong Reserved4;

        public uint ObjectSize;
        public uint Padding1;

        public ulong ObjectOffset;
        public ulong ParentVariationOffset;
        public ulong ShaderReflectionOffset;

        public ulong BinaryOffset;
        public ulong ParentBnshOffset;

        public ulong Reserved5;
        public ulong Reserved6;
        public ulong Reserved7;
        public ulong Reserved8;
    }

    [StructLayout(LayoutKind.Sequential, Size = 0x10)]
    public struct BnshShaderReflectionHeader
    {
        public ulong InputDictionaryOffset;
        public ulong OutputDictionaryOffset;
        public ulong SamplerDictionaryOffset;
        public ulong UniformBufferDictionaryOffset;
        public ulong StorageBufferDictionaryOffset;

        public int OutputIdx; //id in slot list
        public int SamplerIdx;//id in slot list
        public int UniformBufferIdx;//id in slot list
        public int StorageBufferIdx;//id in slot list

        public int SlotOffset;

        public int ComputeWorkGroupX;
        public int ComputeWorkGroupY;
        public int ComputeWorkGroupZ;
        public int ImageIdx;

        public int SlotCount;

        public ulong Unknown1;
        public ulong Unknown2;
    }
}
