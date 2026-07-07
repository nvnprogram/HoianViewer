using System;
using System.Collections.Generic;
using OpenTK.Graphics.OpenGL;
using OpenTK;

namespace GLFrameworkEngine
{
    public class ShaderProgram : IDisposable
    {
        public int program;

        private Dictionary<string, int> attributes = new Dictionary<string, int>();
        private Dictionary<string, int> uniforms = new Dictionary<string, int>();
        private int activeAttributeCount;
        private HashSet<Shader> shaders = new HashSet<Shader>();

        // This isn't in OpenTK's enums for some reason.
        // https://www.khronos.org/registry/OpenGL/api/GL/glcorearb.h
        private static readonly int GL_PROGRAM_BINARY_MAX_LENGTH = 0x8741;

        public ShaderProgram(Shader[] shaders) {
            foreach (Shader shader in shaders)
            {
                if (!this.shaders.Contains(shader))
                    this.shaders.Add(shader);
            }
            program = CompileShaders();
        }

        public ShaderProgram(Shader vertexShader, Shader fragmentShader) {
            if (!this.shaders.Contains(vertexShader))
                this.shaders.Add(vertexShader);
            if (!this.shaders.Contains(fragmentShader))
                this.shaders.Add(fragmentShader);
            program = CompileShaders();
        }

        public ShaderProgram(byte[] binaryData, BinaryFormat format)
        {
            GL.ProgramBinary(program, format, binaryData, binaryData.Length);
        }

        private ShaderProgram(int existingProgram)
        {
            program = existingProgram;
            LoadAttributes(program);
            LoadUniorms(program);
        }

        private ShaderProgram() { }

        #region deferred (non-blocking) compilation

        /// <summary>
        /// True while a deferred program is still compiling in driver threads.
        /// Callers should poll <see cref="PollReady"/> and skip drawing meanwhile.
        /// </summary>
        public bool IsPending { get; private set; }

        /// <summary>Invoked once when a deferred program finishes linking.</summary>
        public Action<ShaderProgram> OnLinked;

        const int GL_COMPLETION_STATUS = 0x91B1; //KHR_parallel_shader_compile

        [System.Runtime.InteropServices.UnmanagedFunctionPointer(
            System.Runtime.InteropServices.CallingConvention.StdCall)]
        delegate void MaxShaderCompilerThreadsDel(uint count);

        static bool? _supportsParallelCompile;
        public static bool SupportsParallelCompile
        {
            get
            {
                if (_supportsParallelCompile == null)
                {
                    _supportsParallelCompile = false;
                    try
                    {
                        GL.GetInteger(GetPName.NumExtensions, out int count);
                        for (int i = 0; i < count; i++)
                        {
                            string ext = GL.GetString(StringNameIndexed.Extensions, i);
                            if (ext == "GL_KHR_parallel_shader_compile" ||
                                ext == "GL_ARB_parallel_shader_compile")
                            {
                                _supportsParallelCompile = true;
                                break;
                            }
                        }

                        //Ask the driver to spin up its compiler thread pool; without
                        //this some drivers compile lazily on the status query, which
                        //blocks and defeats the whole point.
                        if (_supportsParallelCompile.Value)
                        {
                            var ctx = OpenTK.Graphics.GraphicsContext.CurrentContext as OpenTK.Graphics.IGraphicsContextInternal;
                            IntPtr fn = ctx?.GetAddress("glMaxShaderCompilerThreadsKHR") ?? IntPtr.Zero;
                            if (fn == IntPtr.Zero)
                                fn = ctx?.GetAddress("glMaxShaderCompilerThreadsARB") ?? IntPtr.Zero;
                            if (fn != IntPtr.Zero)
                            {
                                var del = System.Runtime.InteropServices.Marshal
                                    .GetDelegateForFunctionPointer<MaxShaderCompilerThreadsDel>(fn);
                                del(0xFFFFFFFF);
                            }
                        }
                    }
                    catch { }
                }
                return _supportsParallelCompile.Value;
            }
        }

        /// <summary>
        /// Creates a program whose compile+link runs asynchronously in driver
        /// threads (KHR_parallel_shader_compile). No status queries are made here,
        /// so this returns immediately; the caller must poll <see cref="PollReady"/>
        /// before using the program.
        /// </summary>
        public static ShaderProgram CreateDeferred(Shader[] shaders)
        {
            var prog = new ShaderProgram();
            foreach (var shader in shaders)
                prog.shaders.Add(shader);

            prog.program = GL.CreateProgram();
            foreach (var shader in prog.shaders)
                GL.AttachShader(prog.program, shader.id);
            try { GL.ProgramParameter(prog.program, ProgramParameterName.ProgramBinaryRetrievableHint, 1); } catch { }
            GL.LinkProgram(prog.program);
            prog.IsPending = true;
            return prog;
        }

        //Activating a freshly linked program (reflection + the material's first
        //render: UBO creation, sampler setup, driver specialization) costs
        //~10-40ms. When a model brings in many new programs at once, activating
        //them all in one frame produces a single very long frame, so activations
        //are capped per rendered frame; the remaining meshes stay hidden a few
        //frames longer. The render loop must advance FrameStamp once per frame.
        public static long FrameStamp;
        static long _activationFrame = -1;
        static int _activationCount;

        /// <summary>
        /// Returns true once the deferred link completed (and finalizes uniform /
        /// attribute reflection on first success). Never blocks when the driver
        /// supports parallel compile.
        /// </summary>
        public bool PollReady()
        {
            if (!IsPending)
                return true;

            if (SupportsParallelCompile)
            {
                GL.GetProgram(program, (GetProgramParameterName)GL_COMPLETION_STATUS, out int done);
                if (done == 0)
                    return false;
            }

            if (_activationFrame != FrameStamp) { _activationFrame = FrameStamp; _activationCount = 0; }
            if (_activationCount >= 1)
                return false;
            _activationCount++;

            IsPending = false;
            LoadAttributes(program);
            LoadUniorms(program);
            OnLinked?.Invoke(this);
            OnLinked = null;
            return true;
        }

        #endregion

        /// <summary>
        /// Tries to create a program from a driver program binary.
        /// Returns null if the driver rejects the binary (ie after a driver update).
        /// </summary>
        public static ShaderProgram TryFromBinary(byte[] binaryData, BinaryFormat format)
        {
            try
            {
                int prog = GL.CreateProgram();
                GL.ProgramBinary(prog, format, binaryData, binaryData.Length);
                GL.GetProgram(prog, GetProgramParameterName.LinkStatus, out int status);
                if (status != 1)
                {
                    GL.DeleteProgram(prog);
                    return null;
                }
                return new ShaderProgram(prog);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Gets the driver program binary for disk caching.
        /// Returns null if the driver does not support program binaries.
        /// </summary>
        public byte[] GetBinary(out BinaryFormat format)
        {
            format = 0;
            try
            {
                GL.GetProgram(program, (GetProgramParameterName)GL_PROGRAM_BINARY_MAX_LENGTH, out int size);
                if (size <= 0)
                    return null;

                byte[] binaryData = new byte[size];
                GL.GetProgramBinary(program, size, out int length, out format, binaryData);
                if (length <= 0)
                    return null;
                if (length != size)
                    Array.Resize(ref binaryData, length);
                return binaryData;
            }
            catch
            {
                return null;
            }
        }

        public void Link()
        {
            GL.LinkProgram(program);
        }

        public void Enable() {
            GL.UseProgram(program);
        }

        public void Disable() {
            GL.UseProgram(0);
        }

        public void Dispose() {
            foreach (var shader in shaders)
                shader.Dispose();

            GL.DeleteProgram(program);
        }

        public void SetTexture(GLTexture tex, string uniform, int id)
        {
            GL.ActiveTexture(TextureUnit.Texture0 + id);
            tex.Bind();
            this.SetInt(uniform, id);
        }

        public void SetVector4(string name, Vector4 value)
        {
            if (uniforms.ContainsKey(name))
                GL.Uniform4(uniforms[name], value);
        }

        public void SetVector3(string name, Vector3 value)
        {
            if (uniforms.ContainsKey(name))
                GL.Uniform3(uniforms[name], value);
        }

        public void SetVector2(string name, Vector2 value)
        {
            if (uniforms.ContainsKey(name))
                GL.Uniform2(uniforms[name], value);
        }

        public void SetFloat(string name, float value)
        {
            if (uniforms.ContainsKey(name))
                GL.Uniform1(uniforms[name], value);
        }

        public void SetInt(string name, int value)
        {
            if (uniforms.ContainsKey(name))
                GL.Uniform1(uniforms[name], value);
        }

        public void SetBool(string name, bool value)
        {
            int intValue = value == true ? 1 : 0;

            if (uniforms.ContainsKey(name))
                GL.Uniform1(uniforms[name], intValue);
        }

        public void SetBoolToInt(string name, bool value)
        {
            if (!uniforms.ContainsKey(name))
                return;

            if (value)
                GL.Uniform1(uniforms[name], 1);
            else
                GL.Uniform1(this[name], 0);
        }

        public void SetColor(string name, System.Drawing.Color color)
        {
            if (uniforms.ContainsKey(name))
                GL.Uniform4(uniforms[name], color.R, color.G, color.B, color.A);
        }

        public void SetMatrix4x4(string name, ref Matrix4 value, bool transpose = false)
        {
            if (uniforms.ContainsKey(name))
                GL.UniformMatrix4(uniforms[name], transpose, ref value);
        }

        public int this[string name]
        {
            get { return uniforms[name]; }
        }

        private void LoadUniorms(int program)
        {
            uniforms.Clear();

            GL.GetProgram(program, GetProgramParameterName.ActiveUniforms, out activeAttributeCount);
            for (int i = 0; i < activeAttributeCount; i++)
            {
                string name = GL.GetActiveUniform(program, i, out int size, out ActiveUniformType type);
                int location = GL.GetUniformLocation(program, name);

                // Overwrite existing vertex attributes.
                uniforms[name] = location;
            }
        }

        private void LoadAttributes(int program)
        {
            attributes.Clear();

            GL.GetProgram(program, GetProgramParameterName.ActiveAttributes, out activeAttributeCount);
            for (int i = 0; i < activeAttributeCount; i++)
            {
                string name = GL.GetActiveAttrib(program, i, out int size, out ActiveAttribType type);
                int location = GL.GetAttribLocation(program, name);

                // Overwrite existing vertex attributes.
                attributes[name] = location;
            }
        }

        public int GetAttribute(string name)
        {
            if (string.IsNullOrEmpty(name) || !attributes.ContainsKey(name))
                return -1;
            else
                return attributes[name];
        }


        public void EnableVertexAttributes()
        {
            foreach (KeyValuePair<string, int> attrib in attributes)
                GL.EnableVertexAttribArray(attrib.Value);
        }

        public void DisableVertexAttributes()
        {
            foreach (KeyValuePair<string, int> attrib in attributes)
                GL.DisableVertexAttribArray(attrib.Value);
        }

        public void Compile()
        {
            program = CompileShaders();

            LoadAttributes(program);
            LoadUniorms(program);
            OnCompiled();
        }

        public void SaveBinary(string fileName)
        {
            CreateBinary(out byte[] binaryData, out BinaryFormat format);
            System.IO.File.WriteAllBytes(fileName, binaryData);
        }

        private void CreateBinary(out byte[] binaryData, out BinaryFormat format)
        {
            GL.GetProgram(program, (GetProgramParameterName)GL_PROGRAM_BINARY_MAX_LENGTH, out int size);
            binaryData = new byte[size];
            GL.GetProgramBinary(program, size, out _, out format, binaryData);
        }

        public virtual void OnCompiled() { }

        private int CompileShaders()
        {
            int program = GL.CreateProgram();
            foreach (Shader shader in shaders) {
                GL.AttachShader(program, shader.id);
            }
            //Hint that the binary may be fetched for disk caching.
            try { GL.ProgramParameter(program, ProgramParameterName.ProgramBinaryRetrievableHint, 1); } catch { }
            GL.LinkProgram(program);
            foreach (var shader in shaders)
            {
                string log = GL.GetShaderInfoLog(shader.id);
                if (!string.IsNullOrWhiteSpace(log))
                    Console.WriteLine($"{shader.type.ToString("g")}:\n{log}");
            }
            LoadAttributes(program);
            LoadUniorms(program);
            return program;
        }
    }

    public class Shader : IDisposable
    {
        public Shader(string src, ShaderType type)
        {
            id = GL.CreateShader(type);
            GL.ShaderSource(id, src);
            GL.CompileShader(id);
            this.type = type;
        }

        public string GetShaderSource()
        {
            string source = "";

            GL.GetShader(id, ShaderParameter.ShaderSourceLength, out int length);
            if (length != 0)
                GL.GetShaderSource(id, length, out _, out source);
            return source;
        }

        public string GetInfoLog() {
            return GL.GetShaderInfoLog(id);
        }

        public void Dispose() {
            GL.DeleteShader(id);
        }

        public ShaderType type;

        public int id;
    }

    public class FragmentShader : Shader
    {
        public FragmentShader(string src)
            : base(src, ShaderType.FragmentShader)
        {

        }
    }

    public class VertexShader : Shader
    {
        public VertexShader(string src)
            : base(src, ShaderType.VertexShader)
        {

        }
    }

    public class GeomertyShader : Shader
    {
        public GeomertyShader(string src)
            : base(src, ShaderType.GeometryShader)
        {

        }
    }
}
