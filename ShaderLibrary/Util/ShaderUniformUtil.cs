using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ShaderLibrary
{
    public class ShaderUniformUtil
    {
        public static List<string> FindUniformsVertex(string shaderCode, BfshaShaderProgram program, BfshaUniformBlock block)
        {
            var locationInfo = program.UniformBlockIndices[block.Index];
            return FindUniforms(shaderCode, block, $"vp_c{locationInfo.FragmentLocation + 3}.data");
        }

        public static List<string> FindUniformsFragment(string shaderCode, BfshaShaderProgram program, BfshaUniformBlock block)
        {
            var locationInfo = program.UniformBlockIndices[block.Index];
            return FindUniforms(shaderCode, block, $"fp_c{locationInfo.FragmentLocation + 3}.data");
        }

        public static List<string> FindUniforms(string shaderCode, BfshaUniformBlock block, string blockName)
        {
            string swizzle = "x";

            Dictionary<string, string> UniformMapping = new Dictionary<string, string>();
            for (int i = 0; i < block.Uniforms.Count; i++)
            {
                string name = block.Uniforms.GetKey(i);
                int size = 16;
                if (i < block.Uniforms.Count - 1)
                    size = block.Uniforms[i + 1].DataOffset - block.Uniforms[i].DataOffset;

                //
                int startIndex = (block.Uniforms[i].DataOffset - 1) / 16;
                int amount = size / 4;

                int index = 0;
                for (int j = 0; j < amount; j++)
                {
                    UniformMapping.Add($"{blockName}[{startIndex + index}].{swizzle}", name);
                    if (swizzle == "w")
                        index++;

                    swizzle = SwizzleShift(swizzle);
                }
            }

            List<string> loadedUniforms = new List<string>();
            foreach (var line in shaderCode.Split('\n'))
            {
                //Uniforms are packed into 16 byte blocks
                //Check for the uniform block
                foreach (var val in UniformMapping)
                {
                    if (line.Contains(val.Key) && !loadedUniforms.Contains(val.Value))
                        loadedUniforms.Add(val.Value);
                }
            }
            return loadedUniforms;
        }

        static string SwizzleShift(string swizzle)
        {
            if (swizzle == "x") return "y";
            if (swizzle == "y") return "z";
            if (swizzle == "z") return "w";
            return "x";
        }
    }
}
