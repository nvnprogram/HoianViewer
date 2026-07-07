using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ShaderLibrary.Common
{
    public enum GX2FetchShaderType
    {
        TESSELLATION_NONE = 0,
        TESSELLATION_LINE = 1,
        TESSELLATION_TRIANGLE = 2,
        TESSELLATION_QUAD = 3
    }

    public enum GX2ShaderMode
    {
        UNIFORM_REGISTER = 0,
        UNIFORM_BLOCK = 1,
        GEOMETRY_SHADER = 2,
        COMPUTE_SHADER = 3
    }

    public enum GX2SamplerVarType
    {
        SAMPLER_1D = 0,
        SAMPLER_2D = 1,
        SAMPLER_3D = 3,
        SAMPLER_CUBE = 4,
        SAMPLER_2D_SHADOW = 6,
        SAMPLER_2D_ARRAY = 10,
        SAMPLER_2D_ARRAY_SHADOW = 12,
        SAMPLER_CUBE_ARRAY = 13,
    }

    public enum GX2ShaderVarType
    {
        VOID = 0,
        BOOL = 1,
        INT = 2,
        UINT = 3,
        FLOAT = 4,
        DOUBLE = 5,
        DOUBLE2 = 6,
        DOUBLE3 = 7,
        DOUBLE4 = 8,
        FLOAT2 = 9,
        FLOAT3 = 10,
        FLOAT4 = 11,
        BOOL2 = 12,
        BOOL3 = 13,
        BOOL4 = 14,
        INT2 = 15,
        INT3 = 16,
        INT4 = 17,
        UINT2 = 18,
        UINT3 = 19,
        UINT4 = 20,
        FLOAT2X2 = 21,
        FLOAT2X3 = 22,
        FLOAT2X4 = 23,
        FLOAT3X2 = 24,
        FLOAT3X3 = 25,
        FLOAT3X4 = 26,
        FLOAT4X2 = 27,
        FLOAT4X3 = 28,
        FLOAT4X4 = 29,
        DOUBLE2X2 = 30,
        DOUBLE2X3 = 31,
        DOUBLE2X4 = 32,
        DOUBLE3X2 = 33,
        DOUBLE3X3 = 34,
        DOUBLE3X4 = 35,
        DOUBLE4X2 = 36,
        DOUBLE4X3 = 37,
        DOUBLE4X4 = 38
    }

    /// <summary>
    /// BFRES fmat shader param type used for Wii U uniforms.
    /// </summary>
    public enum ShaderParamType : byte
    {
        Bool,
        Bool2,
        Bool3,
        Bool4,
        Int,
        Int2,
        Int3,
        Int4,
        UInt,
        UInt2,
        UInt3,
        UInt4,
        Float,
        Float2,
        Float3,
        Float4,
        Reserved2,
        Float2x2,
        Float2x3,
        Float2x4,
        Reserved3,
        Float3x2,
        Float3x3,
        Float3x4,
        Reserved4,
        Float4x2,
        Float4x3,
        Float4x4,
        Srt2D,
        Srt3D,
        TexSrt,
        TexSrtEx
    }
}
