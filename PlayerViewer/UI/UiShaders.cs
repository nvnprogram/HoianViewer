using System;
using System.IO;
using OpenTK.Graphics.OpenGL;

namespace PlayerViewer.UI
{
    /// <summary>The viewer's own GLSL under UI/Shaders, embedded in the assembly.</summary>
    static class UiShaders
    {
        //Embedded rather than copied next to the exe: the single file bundler has already been
        //caught swallowing loose runtime files, and these are code, not user editable content.
        public static string Load(string name)
        {
            string resource = $"PlayerViewer.UI.Shaders.{name}";
            using var stream =
                typeof(UiShaders).Assembly.GetManifestResourceStream(resource)
                ?? throw new InvalidOperationException($"missing embedded shader {resource}");
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }

        /// <summary>A linked GL program from two embedded shaders, for a pass that drives GL itself.</summary>
        public static int Program(string vertex, string fragment)
        {
            int vs = Compile(ShaderType.VertexShader, vertex);
            int fs = Compile(ShaderType.FragmentShader, fragment);
            int program = GL.CreateProgram();
            GL.AttachShader(program, vs);
            GL.AttachShader(program, fs);
            GL.LinkProgram(program);
            GL.GetProgram(program, GetProgramParameterName.LinkStatus, out int linked);
            GL.DeleteShader(vs);
            GL.DeleteShader(fs);
            if (linked == 0)
                throw new InvalidOperationException(
                    $"{vertex}, {fragment}: {GL.GetProgramInfoLog(program)}"
                );
            return program;
        }

        static int Compile(ShaderType type, string name)
        {
            int shader = GL.CreateShader(type);
            GL.ShaderSource(shader, Load(name));
            GL.CompileShader(shader);
            GL.GetShader(shader, ShaderParameter.CompileStatus, out int ok);
            if (ok == 0)
                throw new InvalidOperationException($"{name}: {GL.GetShaderInfoLog(shader)}");
            return shader;
        }
    }
}
