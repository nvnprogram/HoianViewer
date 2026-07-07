using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ShaderLibrary.WiiU
{
    public class BfshaGX2ShaderImporter
    {
        public static void Import(ShaderModel shadermodel, BfshaShaderProgram program,
            GSHFile.GX2Shader shader, IntermediateShader.ShaderModelInfo intermediate)
        {
            program.GX2VertexData = new BfshaLibrary.WiiU.BfshaGX2VertexHeader();
            program.GX2PixelData = new BfshaLibrary.WiiU.BfshaGX2PixelHeader();

            program.GX2VertexData.Data = shader.VertexData;
            program.GX2VertexData.Regs = shader.VertexHeader.GetRegs();
            program.GX2VertexData.Mode = shader.VertexHeader.Mode;
            program.GX2VertexData.Loops = shader.VertexHeader.Loops;

            program.ResetLocations(shadermodel);

            SetLocations(shadermodel, program, shader.VertexHeader, intermediate);

            program.GX2PixelData.Data = shader.PixelData;
            program.GX2PixelData.Regs = shader.PixelHeader.GetRegs();
            program.GX2PixelData.Mode = shader.PixelHeader.Mode;
            program.GX2PixelData.Loops = shader.PixelHeader.Loops;

            SetLocations(shadermodel, program, shader.PixelHeader, intermediate);
/*
            program.GX2GeometryData.Data = shader.GeometryShData;
            program.GX2GeometryData.Regs = shader.GeometryHeader.GetRegs();
            program.GX2GeometryData.Mode = shader.GeometryHeader.Mode;
            program.GX2GeometryData.Loops = shader.GeometryHeader.Loops;

            SetLocations(shadermodel, program, shader.GeometryHeader, intermediate);*/
        }

        static void SetLocations(ShaderModel shadermodel, BfshaShaderProgram program,
            GSHFile.GX2VertexHeader shader, IntermediateShader.ShaderModelInfo intermediate)
        {
            foreach (var uniformBlock in shader.UniformBlocks)
            {
                string target = intermediate.GetUniformBlockBfshaName(uniformBlock.Name);
                if (!shadermodel.UniformBlocks.ContainsKey(target))
                    return;

                var bind_info = program.UniformBlockIndices[shadermodel.UniformBlocks[target].Index];
                bind_info.VertexLocation = (int)uniformBlock.Offset;
            }
            foreach (var samplerVar in shader.Samplers)
            {
                string target = intermediate.GetSamplerBfshaName(samplerVar.Name);
                if (!shadermodel.Samplers.ContainsKey(target))
                    return;

                var bind_info = program.SamplerIndices[shadermodel.Samplers[target].Index];

                bind_info.VertexLocation = (int)samplerVar.Location;
            }

            program.UsedAttributeFlags = 0;
            foreach (var attributeVar in shader.Attributes)
            {
                string target = intermediate.GetAttributeBfshaName(attributeVar.Name);
                if (shadermodel.Attributes.ContainsKey(target))
                    program.SetAttribute(shadermodel.Attributes[target].Index, true);
            }
        }

        static void SetLocations(ShaderModel shadermodel, BfshaShaderProgram program,
            GSHFile.GX2PixelHeader shader, IntermediateShader.ShaderModelInfo intermediate)
        {
            foreach (var uniformBlock in shader.UniformBlocks)
            {
                string target = intermediate.GetUniformBlockBfshaName(uniformBlock.Name);
                if (!shadermodel.UniformBlocks.ContainsKey(target))
                    return;

                var bind_info = program.UniformBlockIndices[shadermodel.UniformBlocks[target].Index];
                bind_info.FragmentLocation = (int)uniformBlock.Offset;
            }
            foreach (var samplerVar in shader.Samplers)
            {
                string target = intermediate.GetSamplerBfshaName(samplerVar.Name);
                if (!shadermodel.Samplers.ContainsKey(target))
                    return;

                var bind_info = program.SamplerIndices[shadermodel.Samplers[target].Index];
                bind_info.FragmentLocation = (int)samplerVar.Location;
            }
        }
        static void SetLocations(ShaderModel shadermodel, BfshaShaderProgram program,
            GSHFile.GX2GeometryShaderHeader shader, IntermediateShader.ShaderModelInfo intermediate)
        {
            foreach (var uniformBlock in shader.UniformBlocks)
            {
                string target = intermediate.GetUniformBlockBfshaName(uniformBlock.Name);
                if (!shadermodel.UniformBlocks.ContainsKey(target))
                    return;

                var bind_info = program.UniformBlockIndices[shadermodel.UniformBlocks[target].Index];
                bind_info.GeoemetryLocation = (int)uniformBlock.Offset;
            }
            foreach (var samplerVar in shader.Samplers)
            {
                string target = intermediate.GetSamplerBfshaName(samplerVar.Name);
                if (!shadermodel.Samplers.ContainsKey(target))
                    return;

                var bind_info = program.SamplerIndices[shadermodel.Samplers[target].Index];
                bind_info.GeoemetryLocation = (int)samplerVar.Location;
            }
        }
    }
}