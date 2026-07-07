using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using GLFrameworkEngine;
using System.IO;

namespace BfresEditor
{
    public class ShaderInfo
    {
        public string FragPath;
        public string VertPath;

        public byte[] VertexConstants;
        public byte[] PixelConstants;

        public ShaderProgram Program;

        //Scanning the decompiled GLSL for used uniforms is only needed by the material
        //parameter editor UI, and is expensive (full source scan), so it is done lazily.
        private BfshaLibrary.UniformBlock _vertexUniformBlock;
        private string _vertexUniformBlockName;
        private List<string> _usedVertexStageUniforms;

        private BfshaLibrary.UniformBlock _pixelUniformBlock;
        private string _pixelUniformBlockName;
        private List<string> _usedPixelStageUniforms;

        public List<string> UsedVertexStageUniforms
        {
            get
            {
                if (_usedVertexStageUniforms == null)
                {
                    _usedVertexStageUniforms = _vertexUniformBlock != null
                        ? FindUniforms(File.ReadAllText(VertPath), _vertexUniformBlock, _vertexUniformBlockName)
                        : new List<string>();
                }
                return _usedVertexStageUniforms;
            }
        }

        public List<string> UsedPixelStageUniforms
        {
            get
            {
                if (_usedPixelStageUniforms == null)
                {
                    _usedPixelStageUniforms = _pixelUniformBlock != null
                        ? FindUniforms(File.ReadAllText(FragPath), _pixelUniformBlock, _pixelUniformBlockName)
                        : new List<string>();
                }
                return _usedPixelStageUniforms;
            }
        }

        public void Reload()
        {
            Program = new ShaderProgram(
                   new FragmentShader(File.ReadAllText(FragPath)),
                   new VertexShader(File.ReadAllText(VertPath)));
        }


        public void CreateUsedUniformListVertex(BfshaLibrary.UniformBlock block, string uniform) {
            _vertexUniformBlock = block;
            _vertexUniformBlockName = uniform;
            _usedVertexStageUniforms = null;
        }

        public void CreateUsedUniformListPixel(BfshaLibrary.UniformBlock block, string uniform) {
            _pixelUniformBlock = block;
            _pixelUniformBlockName = uniform;
            _usedPixelStageUniforms = null;
        }

        private List<string> FindUniforms(string shaderCode, BfshaLibrary.UniformBlock block, string blockName)
        {
            string swizzle = "x";

            Dictionary<string, string> UniformMapping = new Dictionary<string, string>();
            for (int i = 0; i < block.Uniforms.Count; i++)
            {
                string name = block.Uniforms.GetKey(i);
                int size = 16;
                if (i < block.Uniforms.Count - 1)
                    size = block.Uniforms[i + 1].Offset - block.Uniforms[i].Offset;

                //
                int startIndex = (block.Uniforms[i].Offset - 1) / 16;
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
