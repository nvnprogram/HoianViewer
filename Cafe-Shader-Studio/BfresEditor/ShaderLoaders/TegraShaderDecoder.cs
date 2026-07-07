using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Diagnostics;
using System.Security.Cryptography;
using Toolbox.Core;
using Toolbox.Core.IO;
using GLFrameworkEngine;
using System.Text;

namespace BfresEditor
{
    public class TegraShaderDecoder
    {
        public static Dictionary<string, ShaderProgram> GLShaderPrograms = new Dictionary<string, ShaderProgram>();
        static Dictionary<string, ShaderInfo> _shaderInfoCache = new Dictionary<string, ShaderInfo>();

        public static readonly System.Diagnostics.Stopwatch TotalTime = new System.Diagnostics.Stopwatch();
        public static int LoadCount = 0;

        //Fine-grained profiling of a shader load (all on the render thread).
        public static readonly Stopwatch DataTime = new Stopwatch();       //bfsha bytecode access
        public static readonly Stopwatch HashTime = new Stopwatch();       //SHA1 of bytecode
        public static readonly Stopwatch DecompileTime = new Stopwatch();  //bytecode -> GLSL
        public static readonly Stopwatch BinaryTime = new Stopwatch();     //progbin load
        public static readonly Stopwatch LinkTime = new Stopwatch();       //GL compile+link

        /// <summary>
        /// When true (interactive UI), programs missing from the progbin cache are
        /// prepared asynchronously: the Ryujinx bytecode decompile runs on a worker
        /// thread and the GL link runs non-blocking via KHR_parallel_shader_compile.
        /// Affected meshes stay invisible for a few frames instead of stalling the
        /// render thread ~0.5-1s per program.
        /// </summary>
        public static bool AllowDeferredCompile = false;

        //Deduped background decompiles (bytecode -> GLSL files in ShaderCache),
        //keyed by the program hash key.
        static readonly System.Collections.Concurrent.ConcurrentDictionary<string, System.Threading.Tasks.Task>
            _pendingPrep = new();

        /// <summary>
        /// Starts (or returns the in-flight) background preparation of the decompiled
        /// GLSL sources for a shader variation. Once the returned task completes,
        /// <see cref="LoadShaderProgram"/> will hit the file cache and only perform
        /// cheap GL-side work.
        /// </summary>
        public static System.Threading.Tasks.Task PrepareShaderAsync(BfshaLibrary.ShaderVariation variation)
        {
            TegraShaderTranslator.InitCaps();

            var shaderData = variation.BinaryProgram.ShaderInfoData;
            var vertexData = GetShaderData(shaderData.VertexShaderCode);
            var fragData = GetShaderData(shaderData.PixelShaderCode);
            string fragHash = GetHashSHA1(fragData);
            string vertHash = GetHashSHA1(vertexData);
            string key = $"{vertHash}_{fragHash}";

            return _pendingPrep.GetOrAdd(key, _ => System.Threading.Tasks.Task.Run(() =>
            {
                if (!Directory.Exists("ShaderCache"))
                    Directory.CreateDirectory("ShaderCache");

                void WriteIfMissing(string path, Func<string> generate)
                {
                    if (File.Exists(path))
                        return;
                    string tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    File.WriteAllText(tmp, generate());
                    try { File.Move(tmp, path); }
                    catch { File.Delete(tmp); } //another thread won the race
                }

                WriteIfMissing($"ShaderCache/{vertHash}.vert",
                    () => DecompileShader(BfshaLibrary.ShaderType.VERTEX, vertexData));
                WriteIfMissing($"ShaderCache/{fragHash}.frag",
                    () => DecompileShader(BfshaLibrary.ShaderType.PIXEL, fragData));
            }));
        }


        public static ShaderInfo LoadShaderProgram(BfshaLibrary.ShaderModel shaderModel, BfshaLibrary.ShaderVariation variation)
        {
            TotalTime.Start();
            try { return LoadShaderProgramInternal(shaderModel, variation); }
            finally { TotalTime.Stop(); LoadCount++; }
        }

        static ShaderInfo LoadShaderProgramInternal(BfshaLibrary.ShaderModel shaderModel, BfshaLibrary.ShaderVariation variation)
        {
            DataTime.Start();
            var shaderData = variation.BinaryProgram.ShaderInfoData;

            var vertexData = GetShaderData(shaderData.VertexShaderCode);
            var fragData = GetShaderData(shaderData.PixelShaderCode);
            DataTime.Stop();
            HashTime.Start();
            string fragHash = GetHashSHA1(fragData);
            string vertHash = GetHashSHA1(vertexData);
            HashTime.Stop();

            string key = $"{vertHash}_{fragHash}";

            if (_shaderInfoCache.TryGetValue(key, out var cached))
                return cached;

            if (GLShaderPrograms.ContainsKey(key))
            {
                var info = new ShaderInfo()
                {
                    Program = GLShaderPrograms[key],
                    VertexConstants = GetConstants(shaderData.VertexShaderCode),
                    PixelConstants = GetConstants(shaderData.PixelShaderCode),
                    FragPath = $"ShaderCache/{fragHash}.frag",
                    VertPath = $"ShaderCache/{vertHash}.vert",
                };
                _shaderInfoCache[key] = info;
                return info;
            }

            if (!Directory.Exists($"ShaderCache"))
                Directory.CreateDirectory("ShaderCache");

            DecompileTime.Start();
            if (!File.Exists($"ShaderCache/{vertHash}.vert"))
            {
                File.WriteAllText($"ShaderCache/{vertHash}.vert",
                      DecompileShader(BfshaLibrary.ShaderType.VERTEX, vertexData));
            }
            if (!File.Exists($"ShaderCache/{fragHash}.frag"))
                File.WriteAllText($"ShaderCache/{fragHash}.frag",
                     DecompileShader(BfshaLibrary.ShaderType.PIXEL, fragData));
            DecompileTime.Stop();

            //Try the driver program binary cache first, which skips the costly GL compile/link.
            string binaryPath = $"ShaderCache/{key}.progbin";
            BinaryTime.Start();
            ShaderProgram program = TryLoadProgramBinary(binaryPath);
            BinaryTime.Stop();

            if (program == null)
            {
                LinkTime.Start();
                if (AllowDeferredCompile && ShaderProgram.SupportsParallelCompile)
                {
                    //Compile+link in driver threads; meshes poll readiness at draw.
                    program = ShaderProgram.CreateDeferred(new Shader[]
                    {
                        new FragmentShader(File.ReadAllText($"ShaderCache/{fragHash}.frag")),
                        new VertexShader(File.ReadAllText($"ShaderCache/{vertHash}.vert")),
                    });
                    program.OnLinked = p => SaveProgramBinary(p, binaryPath);
                }
                else
                {
                    //Load the source to opengl
                    program = new ShaderProgram(
                                    new FragmentShader(File.ReadAllText($"ShaderCache/{fragHash}.frag")),
                                    new VertexShader(File.ReadAllText($"ShaderCache/{vertHash}.vert")));

                    SaveProgramBinary(program, binaryPath);
                }
                LinkTime.Stop();
            }

            GLShaderPrograms.Add(key, program);

            var result = new ShaderInfo()
            {
                Program = program,
                VertexConstants = GetConstants(shaderData.VertexShaderCode),
                PixelConstants = GetConstants(shaderData.PixelShaderCode),
                FragPath = $"ShaderCache/{fragHash}.frag",
                VertPath = $"ShaderCache/{vertHash}.vert",
            };
            _shaderInfoCache[key] = result;
            return result;
        }

        static ShaderProgram TryLoadProgramBinary(string path)
        {
            if (!File.Exists(path))
                return null;

            try
            {
                using (var reader = new BinaryReader(File.OpenRead(path)))
                {
                    int format = reader.ReadInt32();
                    int length = reader.ReadInt32();
                    byte[] data = reader.ReadBytes(length);
                    return ShaderProgram.TryFromBinary(data, (OpenTK.Graphics.OpenGL.BinaryFormat)format);
                }
            }
            catch
            {
                return null;
            }
        }

        static void SaveProgramBinary(ShaderProgram program, string path)
        {
            try
            {
                var data = program.GetBinary(out OpenTK.Graphics.OpenGL.BinaryFormat format);
                if (data == null)
                    return;

                using (var writer = new BinaryWriter(File.Create(path)))
                {
                    writer.Write((int)format);
                    writer.Write(data.Length);
                    writer.Write(data);
                }
            }
            catch { }
        }

        static string AppendPixelShaderCode(string code)
        {
            bool writtenExtraUniforms = false;

            var builder = new StringBuilder();

            var lines = code.Split('\n');
            int numLines = 0;
            foreach (var line in lines) {
                if (!writtenExtraUniforms && line.Contains("const int undef = 0;")) {
                    //Extra in tool uniforms for in tool functions (ie selection color)
                    builder.AppendLine("struct EXTRA_BLOCK");
                    builder.AppendLine("{");
                    builder.AppendLine("    vec4 selectionColor;");
                    builder.AppendLine("};");
                    builder.AppendLine("uniform EXTRA_BLOCK extraBlock;");

                    //Alpha test stage emulation. Switch games use the fixed function
                    //alpha test which core profile GL lacks; the decompiled shader only
                    //contains the discard for alpha == 0.
                    builder.AppendLine("uniform int css_alphaTest;");
                    builder.AppendLine("uniform int css_alphaFunc;");
                    builder.AppendLine("uniform float css_alphaRef;");

                    writtenExtraUniforms = true;
                }

                if (writtenExtraUniforms && line.Contains("    return;") && numLines >= lines.Length - 5) {
                    builder.AppendLine("    if (css_alphaTest != 0) {");
                    builder.AppendLine("        bool css_pass = true;");
                    builder.AppendLine("        if (css_alphaFunc == 0) css_pass = out_attr0.a >= css_alphaRef;");
                    builder.AppendLine("        else if (css_alphaFunc == 1) css_pass = out_attr0.a > css_alphaRef;");
                    builder.AppendLine("        else if (css_alphaFunc == 2) css_pass = out_attr0.a == css_alphaRef;");
                    builder.AppendLine("        else if (css_alphaFunc == 3) css_pass = out_attr0.a < css_alphaRef;");
                    builder.AppendLine("        else if (css_alphaFunc == 4) css_pass = out_attr0.a <= css_alphaRef;");
                    builder.AppendLine("        if (!css_pass) discard;");
                    builder.AppendLine("    }");
                    builder.AppendLine("    out_attr0.rgb = out_attr0.rgb * (1 - extraBlock.selectionColor.a) + extraBlock.selectionColor.rgb * extraBlock.selectionColor.a;");
                }

                builder.AppendLine(line);
                numLines++;
            }
            return builder.ToString();
        }

        //Hash algorithm for cached shaders. Make sure to only decompile unique/new shaders
        static string GetHashSHA1(byte[] data)
        {
            using (var sha1 = new System.Security.Cryptography.SHA1CryptoServiceProvider()) {
                return string.Concat(sha1.ComputeHash(data).Select(x => x.ToString("X2")));
            }
        }

        //Gets the raw byte data and splits off uneeded parts
        static byte[] GetShaderData(BfshaLibrary.ShaderCodeData shaderData)
        {
            var data = ((BfshaLibrary.ShaderCodeDataBinary)shaderData).BinaryData;
            byte[] data1 = data[1].ToArray();

            return ByteUtils.SubArray(data1, 48, (uint)data1.Length - 48);
        }

        static byte[] GetConstants(BfshaLibrary.ShaderCodeData shaderData)
        {
            var data = ((BfshaLibrary.ShaderCodeDataBinary)shaderData).BinaryData;

            //Bnsh has 2 shader code sections. The first section has block info for constants
            using (var reader = new Toolbox.Core.IO.FileReader(data[0])) {
                long ctrlLen = reader.BaseStream.Length;
                if (ctrlLen < 1800)
                    return null;
                reader.SeekBegin(1776);
                ulong ofsUnk = reader.ReadUInt64();
                uint lenByteCode = reader.ReadUInt32();
                uint lenConstData = reader.ReadUInt32();
                uint ofsConstBlockDataStart = reader.ReadUInt32();
                uint ofsConstBlockDataEnd = reader.ReadUInt32();

                long byteCodeLen = data[1].Length;

                if (lenConstData == 0)
                    return null;
                if (ofsConstBlockDataStart + lenConstData > byteCodeLen)
                    return null;
                return GetConstantsFromCode(data[1], ofsConstBlockDataStart, lenConstData);
            }
        }

        static byte[] GetConstantsFromCode(Stream shaderCode, uint offset, uint length)
        {
            using (var reader = new Toolbox.Core.IO.FileReader(shaderCode, true))
            {
                reader.SeekBegin(offset);
                return reader.ReadBytes((int)length);
            }
        }

        static string DecompileShader(BfshaLibrary.ShaderType shaderType, byte[] Data)
        {
            string translated = TegraShaderTranslator.Translate(Data);

            // Strip layout(binding=N) from sampler declarations so glUniform1i can assign texture units.
            var sb = new StringBuilder();
            foreach (var line in translated.Split('\n'))
            {
                if (line.Contains("uniform sampler") || line.Contains("uniform usampler") ||
                    line.Contains("uniform isampler"))
                {
                    sb.AppendLine(System.Text.RegularExpressions.Regex.Replace(
                        line, @"layout\s*\(binding\s*=\s*\d+\)\s*", ""));
                }
                else
                {
                    sb.AppendLine(line);
                }
            }
            translated = sb.ToString();

            if (shaderType == BfshaLibrary.ShaderType.PIXEL)
                translated = AppendPixelShaderCode(translated);

            return translated;
        }

    }
}
