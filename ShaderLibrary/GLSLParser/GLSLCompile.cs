using Silk.NET.Core.Native;
using Silk.NET.OpenGL;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace ShaderLibrary
{
    public class GLSLCompile
    {
        private GL _gl;

        public Dictionary<string, int> Inputs = new Dictionary<string, int>();
        public Dictionary<string, int> Outputs = new Dictionary<string, int>();
        public Dictionary<string, int> Samplers = new Dictionary<string, int>();
        public Dictionary<string, int> UniformBlocks = new Dictionary<string, int>();
        public Dictionary<string, int> StorageBuffers = new Dictionary<string, int>();

        public uint ShaderProgram { get; private set; }

        // bfsha to glsl symbol
        public Dictionary<string, string> UniformBlockSymbols = new Dictionary<string, string>();
        public Dictionary<string, string> SamplerSymbols = new Dictionary<string, string>();
        public Dictionary<string, string> AttributeSymbols = new Dictionary<string, string>();

        private IntermediateShader.ShaderModelInfo _shader;

        public GLSLCompile(GL gl, IntermediateShader.ShaderModelInfo shader)
        {
            _shader = shader;
            _gl = gl;

            foreach (var b in shader.UniformBlocks)
                UniformBlockSymbols.TryAdd(b.ID, b.Symbol);
            foreach (var b in shader.Samplers)
                SamplerSymbols.TryAdd(b.ID, b.Symbol);
            foreach (var b in shader.Attributes)
                AttributeSymbols.TryAdd(b.ID, b.Symbol);
        }

        public bool HasAttribute(string name)
        {
            if (AttributeSymbols.ContainsKey(name))
                return this.Inputs.ContainsKey(AttributeSymbols[name]);
            return false;
        }

        public int GetSamplerLocation(string name)
        {
            name = SamplerSymbols.ContainsKey(name) ? SamplerSymbols[name] : name;
            return this.Samplers.ContainsKey(name) ? this.Samplers[name] : -1; 
        }
            
        public int GetUniformBlockLocation(string name)
        {
            name = UniformBlockSymbols.ContainsKey(name) ? UniformBlockSymbols[name] : name;
            return this.UniformBlocks.ContainsKey(name) ? this.UniformBlocks[name] : -1;
        }

        public int GetStorageBufferLocation(string name)
            => this.StorageBuffers.ContainsKey(name) ? this.StorageBuffers[name] : -1;
        public int GetOutputLocation(string name)
            => this.Outputs.ContainsKey(name) ? this.Outputs[name] : -1;

        public unsafe void CompileVert(string vertexShaderSource)
        {
            const string dummyFragShader = "#version 330 core\r\n\r\nout vec4 FragColor;\r\n\r\nvoid main()\r\n{\r\n    FragColor = vec4(1.0); // Output solid white\r\n}";
            Compile(vertexShaderSource, dummyFragShader);
        }
        public unsafe void CompileFrag(string fragmentShaderSource)
        {
            const string dummyVertexShader = "#version 330 core\nvoid main() { gl_Position = vec4(0.0); }";
            Compile(dummyVertexShader, fragmentShaderSource);
        }

        public unsafe void Compile(string vertexShaderSource, string fragmentShaderSource)
        {
            Inputs.Clear();
            Outputs.Clear();
            Samplers.Clear();
            UniformBlocks.Clear();
            StorageBuffers.Clear();

            uint vertexShader = CompileShader(ShaderType.VertexShader, vertexShaderSource);
            uint fragmentShader = CompileShader(ShaderType.FragmentShader, fragmentShaderSource);

            ShaderProgram = _gl.CreateProgram();
            _gl.AttachShader(ShaderProgram, vertexShader);
            _gl.AttachShader(ShaderProgram, fragmentShader);
            _gl.LinkProgram(ShaderProgram);

            // Check link status
            _gl.GetProgram(ShaderProgram, GLEnum.LinkStatus, out var linkStatus);
            if (linkStatus == 0)
            {
                string log = _gl.GetProgramInfoLog(ShaderProgram);
                throw new Exception($"Shader link failed:\n{log}");
            }

            // Clean up shaders
            _gl.DetachShader(ShaderProgram, vertexShader);
            _gl.DetachShader(ShaderProgram, fragmentShader);
            _gl.DeleteShader(vertexShader);
            _gl.DeleteShader(fragmentShader);

            // Query attributes
            _gl.GetProgram(ShaderProgram, GLEnum.ActiveAttributes, out int numAttribs);
            for (int i = 0; i < numAttribs; i++)
            {
                string name = _gl.GetActiveAttrib(ShaderProgram, (uint)i, out _, out _);
                int location = _gl.GetAttribLocation(ShaderProgram, name);

                var attr = _shader.Attributes.FirstOrDefault(x => x.Symbol == name);
                if (attr == null)
                    continue;

                Inputs[name] = attr.Location;
            }
            // Query uniforms
            _gl.GetProgram(ShaderProgram, GLEnum.ActiveUniforms, out int numUniforms);
            for (int i = 0; i < numUniforms; i++)
            {
                string name = _gl.GetActiveUniform(ShaderProgram, (uint)i, out _, out UniformType type);
                int location = _gl.GetUniformLocation(ShaderProgram, name);

                if (type.ToString().Contains("Sampler"))
                {
                    var samp = _shader.Samplers.FirstOrDefault(x => x.Symbol == name);
                    if (samp == null)
                        continue;

                    Samplers[name] = samp.Location;
                }

                /*    var samp = _shader.Samplers[name];

                    if (type.ToString().Contains("Sampler"))
                    {
                        Samplers[name] = location;
                    }*/
            }

            // Query uniform blocks
            _gl.GetProgram(ShaderProgram, ProgramPropertyARB.ActiveUniformBlocks, out int numBlocks);
            for (int i = 0; i < numBlocks; i++)
            {
                Span<byte> nameBuffer = stackalloc byte[256];
                unsafe
                {
                    _gl.GetActiveUniformBlock(ShaderProgram, (uint)i, 
                        GLEnum.UniformBlockReferencedByVertexShader, out int isVertexUsed);
                    _gl.GetActiveUniformBlock(ShaderProgram, (uint)i,
                        GLEnum.UniformBlockReferencedByFragmentShader, out int isFragmentUsed);

                    _gl.GetActiveUniformBlockName(ShaderProgram, (uint)i,
                        (uint)nameBuffer.Length, null, out nameBuffer[0]);

                    if (isVertexUsed == 0 && isFragmentUsed == 0)
                        continue; // Not used, skip
                }

                _gl.GetActiveUniformBlock(ShaderProgram, (uint)i, GLEnum.UniformBlockBinding, out int binding);

                string name = SilkMarshal.PtrToString((nint)Unsafe.AsPointer(ref nameBuffer[0]))!;
                UniformBlocks[name] = binding;
            }

            // Query shader storage blocks
            _gl.GetProgramInterface(ShaderProgram, GLEnum.ShaderStorageBlock, GLEnum.ActiveResources, out int numSSBOs);
            for (int i = 0; i < numSSBOs; i++)
            {
                Span<byte> nameBuffer = stackalloc byte[256];
                unsafe
                {
                    _gl.GetProgramResourceName(ShaderProgram, ProgramInterface.ShaderStorageBlock, (uint)i,
                        (uint)nameBuffer.Length, out uint len, out nameBuffer[0]);
                }
                string name = SilkMarshal.PtrToString((nint)Unsafe.AsPointer(ref nameBuffer[0]))!;
                StorageBuffers[name] = i;
            }
        }

        private uint CompileShader(ShaderType type, string source)
        {
            uint shader = _gl.CreateShader(type);
            _gl.ShaderSource(shader, source);
            _gl.CompileShader(shader);

            _gl.GetShader(shader, ShaderParameterName.CompileStatus, out int success);
            if (success == 0)
            {
                string infoLog = _gl.GetShaderInfoLog(shader);
                throw new Exception($"Failed to compile {type} shader:\n{infoLog}");
            }

            return shader;
        }
    }
}
