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
    public partial class TegraShaderDecoder
    {
        public static Dictionary<string, ShaderProgram> GLShaderPrograms = new Dictionary<string, ShaderProgram>();
        static Dictionary<string, ShaderInfo> _shaderInfoCache = new Dictionary<string, ShaderInfo>();

        //How many renderers hold each program. The disk cache keeps the sources and the
        //program binary, so a program nobody holds can be dropped and reloaded later.
        static readonly Dictionary<string, int> _holders = new Dictionary<string, int>();

        public static void ClearInfoCache() => _shaderInfoCache.Clear();
        public static int ShaderInfoCacheCount => _shaderInfoCache.Count;

        /// <summary>Hands a program back; see <see cref="ReleaseUnused"/>.</summary>
        public static void Release(ShaderInfo info)
        {
            if (info?.Key == null || !_holders.TryGetValue(info.Key, out int n))
                return;
            _holders[info.Key] = n - 1;
        }

        /// <summary>
        /// Deletes every GL program no renderer holds and forgets the decompile tasks that
        /// have finished. Called when a scene goes away; a later load comes back off disk.
        /// Must be called on the render thread.
        /// </summary>
        public static int ReleaseUnused()
        {
            int freed = 0;
            foreach (var key in new List<string>(GLShaderPrograms.Keys))
            {
                if (_holders.TryGetValue(key, out int n) && n > 0)
                    continue;
                if (DebugLog)
                    Console.WriteLine($"[GL] free {key}");
                GLShaderPrograms[key].Dispose();
                GLShaderPrograms.Remove(key);
                _shaderInfoCache.Remove(key);
                _holders.Remove(key);
                freed++;
            }
            foreach (var key in new List<string>(_pendingPrep.Keys))
                if (_pendingPrep.TryGetValue(key, out var task) && task.IsCompleted)
                    _pendingPrep.TryRemove(key, out _);
            return freed;
        }

        static void Hold(string key)
        {
            _holders.TryGetValue(key, out int n);
            _holders[key] = n + 1;
        }

        public static readonly System.Diagnostics.Stopwatch TotalTime = new System.Diagnostics.Stopwatch();
        public static int LoadCount = 0;

        const int CacheVersion = 7;
        static bool _cacheVersionChecked;

        public static string CacheDir = "ShaderCache";

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
            EnsureCacheVersion();

            var shaderData = variation.BinaryProgram.ShaderInfoData;
            string vertHash = GetStageHash(shaderData.VertexShaderCode);
            string fragHash = GetStageHash(shaderData.PixelShaderCode);
            string key = $"{vertHash}_{fragHash}";

            string vertPath = Path.Combine(CacheDir, $"{key}.vert");
            string fragPath = Path.Combine(CacheDir, $"{fragHash}.frag");
            if (File.Exists(vertPath) && File.Exists(fragPath))
                return System.Threading.Tasks.Task.CompletedTask;

            return _pendingPrep.GetOrAdd(key, _ => System.Threading.Tasks.Task.Run(() =>
            {
                if (!Directory.Exists(CacheDir))
                    Directory.CreateDirectory(CacheDir);

                WriteDecompiled(vertPath, fragPath,
                    GetShaderData(shaderData.VertexShaderCode),
                    GetShaderData(shaderData.PixelShaderCode));
            }));
        }


        static void EnsureCacheVersion()
        {
            if (_cacheVersionChecked) return;
            _cacheVersionChecked = true;

            string versionPath = Path.Combine(CacheDir, "version.txt");

            if (!Directory.Exists(CacheDir))
            {
                Directory.CreateDirectory(CacheDir);
                File.WriteAllText(versionPath, CacheVersion.ToString());
                return;
            }

            int existing = 1;
            if (File.Exists(versionPath))
                int.TryParse(File.ReadAllText(versionPath).Trim(), out existing);

            if (existing == CacheVersion)
                return;

            Console.WriteLine($"[ShaderCache] Version mismatch ({existing} -> {CacheVersion}), purging cache.");
            foreach (var f in Directory.GetFiles(CacheDir))
                try { File.Delete(f); } catch { }
            File.WriteAllText(versionPath, CacheVersion.ToString());
        }

        /// <summary>
        /// Walks the source a line at a time without normalising it into a second string and
        /// then splitting that into one string per line.
        /// </summary>
        static void ForEachLine(string source, LineAction body)
        {
            int i = 0;
            while (true)
            {
                int nl = source.IndexOf('\n', i);
                int end = nl < 0 ? source.Length : nl;
                int trimmed = end > i && source[end - 1] == '\r' ? end - 1 : end;
                body(source.AsSpan(i, trimmed - i));
                if (nl < 0)
                    return;
                i = nl + 1;
            }
        }

        delegate void LineAction(ReadOnlySpan<char> line);

        static int CountLines(string source)
        {
            int count = 1;
            for (int i = source.IndexOf('\n'); i >= 0; i = source.IndexOf('\n', i + 1))
                count++;
            return count;
        }

        /// <summary>
        /// Patches a decompiled stage for framebuffer samplers:
        /// - Y-flip UV: UV -> UV * vec2(1,-1) + vec2(0,1)  (OpenGL bottom-up -> NX top-down)
        /// </summary>
        internal static string PatchFramebufferSamplers(string fragSource,
            HashSet<string> yFlipSamplers)
        {
            if (yFlipSamplers == null || yFlipSamplers.Count == 0) return fragSource;

            var sb = new StringBuilder(fragSource.Length + 256);
            ForEachLine(fragSource, line =>
            {
                if (!line.Contains("texture", StringComparison.Ordinal))
                {
                    sb.Append(line).Append(Environment.NewLine);
                    return;
                }
                string patched = line.ToString();
                foreach (var sampler in yFlipSamplers)
                    patched = PatchLineTexture(patched, sampler);
                sb.Append(patched).Append(Environment.NewLine);
            });
            return sb.ToString();
        }

        static string PatchLineTexture(string line, string samplerName)
        {
            int searchStart = 0;
            while (true)
            {
                int texIdx = line.IndexOf("texture", searchStart, StringComparison.Ordinal);
                if (texIdx < 0) break;

                int parenIdx = line.IndexOf('(', texIdx);
                if (parenIdx < 0) break;

                int cursor = parenIdx + 1;
                while (cursor < line.Length && char.IsWhiteSpace(line[cursor])) cursor++;

                if (cursor + samplerName.Length > line.Length ||
                    line.Substring(cursor, samplerName.Length) != samplerName)
                {
                    searchStart = texIdx + 7;
                    continue;
                }

                int afterName = cursor + samplerName.Length;
                while (afterName < line.Length && char.IsWhiteSpace(line[afterName])) afterName++;
                if (afterName >= line.Length || line[afterName] != ',')
                {
                    searchStart = texIdx + 7;
                    continue;
                }

                int uvStart = afterName + 1;
                while (uvStart < line.Length && char.IsWhiteSpace(line[uvStart])) uvStart++;

                int depth = 0;
                int uvEnd = uvStart;
                while (uvEnd < line.Length)
                {
                    char c = line[uvEnd];
                    if (c == '(') depth++;
                    else if (c == ')') { if (depth == 0) break; depth--; }
                    else if (c == ',' && depth == 0) break;
                    uvEnd++;
                }

                string uvExpr = line.Substring(uvStart, uvEnd - uvStart).Trim();
                string flipped = $"({uvExpr}) * vec2(1.0, -1.0) + vec2(0.0, 1.0)";
                line = line.Substring(0, uvStart) + flipped + line.Substring(uvEnd);
                searchStart = uvStart + flipped.Length;
            }
            return line;
        }

        public static readonly bool DebugLog = ReadDebugSwitch();

        static bool ReadDebugSwitch()
        {
            bool on = false;
            DebugSwitch(ref on);
            return on;
        }

        static partial void DebugSwitch(ref bool on);

        public static string TimingReport() =>
            $"[ShaderCache] {LoadCount} load(s) in {TotalTime.Elapsed.TotalMilliseconds:0.0}ms"
            + $"  hash {HashTime.Elapsed.TotalMilliseconds:0.0}"
            + $"  data {DataTime.Elapsed.TotalMilliseconds:0.0}"
            + $"  decompile {DecompileTime.Elapsed.TotalMilliseconds:0.0}"
            + $"  progbin {BinaryTime.Elapsed.TotalMilliseconds:0.0}"
            + $"  link {LinkTime.Elapsed.TotalMilliseconds:0.0}"
            + $"  ({_shaderInfoCache.Count} cached)";

        /// <summary>
        /// Loads or fetches the program. With <paramref name="hold"/> the caller counts as a
        /// holder and must <see cref="Release"/> it; without, the program is only warmed and
        /// goes on the next <see cref="ReleaseUnused"/> unless something holds it by then.
        /// </summary>
        public static ShaderInfo LoadShaderProgram(BfshaLibrary.ShaderModel shaderModel,
            BfshaLibrary.ShaderVariation variation,
            HashSet<string> yFlipSamplers = null, bool hold = true)
        {
            TotalTime.Start();
            try
            {
                var info = LoadShaderProgramInternal(shaderModel, variation, yFlipSamplers);
                if (hold && info?.Key != null)
                    Hold(info.Key);
                return info;
            }
            finally
            {
                TotalTime.Stop();
                LoadCount++;
                if (LoadCount % 10 == 0 && DebugLog)
                    Console.WriteLine(TimingReport());
            }
        }

        static ShaderInfo LoadShaderProgramInternal(BfshaLibrary.ShaderModel shaderModel,
            BfshaLibrary.ShaderVariation variation,
            HashSet<string> yFlipSamplers)
        {
            EnsureCacheVersion();

            var shaderData = variation.BinaryProgram.ShaderInfoData;
            HashTime.Start();
            string vertHash = GetStageHash(shaderData.VertexShaderCode);
            string fragHash = GetStageHash(shaderData.PixelShaderCode);
            HashTime.Stop();

            bool hasPatch = yFlipSamplers != null && yFlipSamplers.Count > 0;
            string programKey = $"{vertHash}_{fragHash}";
            string key = hasPatch ? programKey + "_fbpatch" : programKey;

            string vertPath = Path.Combine(CacheDir, $"{programKey}.vert");
            string fragPath = Path.Combine(CacheDir, $"{fragHash}.frag");

            if (_shaderInfoCache.TryGetValue(key, out var cached))
                return cached;

            if (GLShaderPrograms.TryGetValue(key, out var existing))
            {
                var info = new ShaderInfo()
                {
                    Program = existing,
                    Key = key,
                    VertexConstants = GetConstants(shaderData.VertexShaderCode),
                    PixelConstants = GetConstants(shaderData.PixelShaderCode),
                    FragPath = fragPath,
                    VertPath = vertPath,
                };
                _shaderInfoCache[key] = info;
                return info;
            }

            if (!Directory.Exists(CacheDir))
                Directory.CreateDirectory(CacheDir);

            DecompileTime.Start();
            var (freshVert, freshFrag) = WriteDecompiled(
                vertPath, fragPath,
                () => GetShaderData(shaderData.VertexShaderCode),
                () => GetShaderData(shaderData.PixelShaderCode));
            DecompileTime.Stop();

            //Try the driver program binary cache first, which skips the costly GL compile/link.
            string binaryPath = Path.Combine(CacheDir, $"{key}.progbin");
            BinaryTime.Start();
            ShaderProgram program = TryLoadProgramBinary(binaryPath);
            BinaryTime.Stop();

            if (program == null)
            {
                string fragSource = freshFrag ?? File.ReadAllText(fragPath);
                string vertSource = freshVert ?? File.ReadAllText(vertPath);

                if (hasPatch)
                {
                    fragSource = PatchFramebufferSamplers(fragSource, yFlipSamplers);
                    vertSource = PatchFramebufferSamplers(vertSource, yFlipSamplers);
                }

                LinkTime.Start();
                if (AllowDeferredCompile && ShaderProgram.SupportsParallelCompile)
                {
                    program = ShaderProgram.CreateDeferred(new Shader[]
                    {
                        new FragmentShader(fragSource),
                        new VertexShader(vertSource),
                    });
                    program.OnLinked = p => SaveProgramBinary(p, binaryPath);
                }
                else
                {
                    program = new ShaderProgram(
                        new FragmentShader(fragSource),
                        new VertexShader(vertSource));

                    SaveProgramBinary(program, binaryPath);
                }
                LinkTime.Stop();
            }

            GLShaderPrograms.Add(key, program);

            var result = new ShaderInfo()
            {
                Program = program,
                Key = key,
                VertexConstants = GetConstants(shaderData.VertexShaderCode),
                PixelConstants = GetConstants(shaderData.PixelShaderCode),
                FragPath = fragPath,
                VertPath = vertPath,
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

            var builder = new StringBuilder(code.Length + 1024);
            int total = CountLines(code);
            int numLines = 0;
            ForEachLine(code, line => {
                if (!writtenExtraUniforms && line.Contains("const int undef = 0;", StringComparison.Ordinal)) {
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

                if (writtenExtraUniforms && numLines >= total - 5
                    && line.Contains("    return;", StringComparison.Ordinal)) {
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

                builder.Append(line).Append(Environment.NewLine);
                numLines++;
            });
            return builder.ToString();
        }

        //Hash algorithm for cached shaders. Make sure to only decompile unique/new shaders
        static string GetHashSHA1(byte[] data) =>
            Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(data));

        static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, string>
            _stageHash = new();

        static string GetStageHash(BfshaLibrary.ShaderCodeData shaderData)
        {
            if (_stageHash.TryGetValue(shaderData, out string hash))
                return hash;

            hash = GetHashSHA1(GetShaderData(shaderData));
            _stageHash.AddOrUpdate(shaderData, hash);
            return hash;
        }

        //Gets the raw byte data and splits off uneeded parts
        static byte[] GetShaderData(BfshaLibrary.ShaderCodeData shaderData)
        {
            DataTime.Start();
            try { return GetShaderDataInternal(shaderData); }
            finally { DataTime.Stop(); }
        }

        static byte[] GetShaderDataInternal(BfshaLibrary.ShaderCodeData shaderData)
        {
            var data = ((BfshaLibrary.ShaderCodeDataBinary)shaderData).BinaryData;
            var stream = data[1];
            if (!stream.CanSeek)
            {
                byte[] whole = stream.ToArray();
                return ByteUtils.SubArray(whole, 48, (uint)whole.Length - 48);
            }

            int length = (int)(stream.Length - 48);
            if (length <= 0)
                return Array.Empty<byte>();

            byte[] result = new byte[length];
            stream.Position = 48;
            stream.ReadExactly(result, 0, length);
            return result;
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

        //Decompiles a vertex/pixel pair. The two stages have to go through the translator
        //together, see TegraShaderTranslator.TranslatePair, which is why the decompiled
        //vertex source is cached per program rather than per vertex bytecode hash.
        static (string Vertex, string Pixel) DecompilePair(byte[] vertexData, byte[] pixelData)
        {
            var (vertex, pixel) = TegraShaderTranslator.TranslatePair(vertexData, pixelData);
            return (ReduceTrig(StripSamplerBindings(vertex)),
                AppendPixelShaderCode(ReduceTrig(StripSamplerBindings(pixel))));
        }

        static readonly System.Text.RegularExpressions.Regex _trigCall =
            new(@"\b(sin|cos)\(", System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>
        /// The console reduces a sin or cos argument to turns before the lookup, so a large
        /// argument still gives a usable result. Some drivers lose all precision there instead,
        /// so the argument is reduced the same way first.
        /// </summary>
        static string ReduceTrig(string source)
        {
            int main = source.IndexOf("void main()", StringComparison.Ordinal);
            if (main < 0 || !_trigCall.IsMatch(source, main))
                return source;
            const string helpers =
                "float pv_sin(float x) { return sin(fract(x * 0.159154937) * 6.28318548); }\n"
                + "float pv_cos(float x) { return cos(fract(x * 0.159154937) * 6.28318548); }\n\n";
            string body = _trigCall.Replace(source.Substring(main), "pv_$1(");
            return source.Substring(0, main) + helpers + body;
        }

        //Writes the decompiled sources for a program if they are not cached yet.
        //The vertex source is keyed by the program (vertex + pixel hash), the pixel
        //source only by its own hash since it does not depend on the vertex shader.
        static void WriteDecompiled(string vertPath, string fragPath, byte[] vertexData, byte[] fragData) =>
            WriteDecompiled(vertPath, fragPath, () => vertexData, () => fragData);

        /// <summary>
        /// The bytecode is fetched through the callbacks so a pair that is already on disk
        /// costs two File.Exists and nothing else. Returns the sources it produced, or nulls
        /// when it produced none.
        /// </summary>
        static (string Vertex, string Pixel) WriteDecompiled(
            string vertPath, string fragPath,
            Func<byte[]> vertexData, Func<byte[]> fragData)
        {
            bool needVert = !File.Exists(vertPath);
            bool needFrag = !File.Exists(fragPath);
            if (!needVert && !needFrag)
                return (null, null);

            var (vertex, pixel) = DecompilePair(vertexData(), fragData());

            if (needVert) WriteAtomic(vertPath, vertex);
            if (needFrag) WriteAtomic(fragPath, pixel);
            return (vertex, pixel);
        }

        static void WriteAtomic(string path, string contents)
        {
            string tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(tmp, contents);
            try { File.Move(tmp, path); }
            catch { try { File.Delete(tmp); } catch { } } //another thread won the race
        }

        static readonly System.Text.RegularExpressions.Regex _bindingLayout =
            new(@"layout\s*\(binding\s*=\s*\d+\)\s*",
                System.Text.RegularExpressions.RegexOptions.Compiled);

        static string StripSamplerBindings(string translated)
        {
            // Strip layout(binding=N) from sampler declarations so glUniform1i can assign texture units.
            var sb = new StringBuilder(translated.Length + 64);
            ForEachLine(translated, line =>
            {
                if (line.Contains("uniform sampler", StringComparison.Ordinal)
                    || line.Contains("uniform usampler", StringComparison.Ordinal)
                    || line.Contains("uniform isampler", StringComparison.Ordinal))
                    sb.Append(_bindingLayout.Replace(line.ToString(), ""));
                else
                    sb.Append(line);
                sb.Append(Environment.NewLine);
            });
            return sb.ToString();
        }

    }
}
