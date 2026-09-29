using System;
using System.Collections.Generic;
using System.Linq;
using BfresEditor;
using EffectLibrary;
using OpenTK.Graphics.OpenGL;

namespace PlayerViewer.Effects.Gl
{
    /// <summary>
    /// One effect program linked through the material renderer's translation path, with the GL
    /// side of its bindings resolved: uniform block indices per stage and a texture unit per
    /// sampler, already assigned to the sampler uniforms.
    /// </summary>
    public sealed class GlEffectProgram
    {
        public EmitterProgram Program { get; }
        public ShaderInfo Info { get; }
        public int Id => Info.Program.program;

        /// <summary>Each block the program reads with its GL block index per stage (-1 where the
        /// stage does not read it).</summary>
        public IReadOnlyList<(EffectBlock Block, int Vertex, int Fragment)> Blocks
        {
            get;
            private set;
        }

        /// <summary>Active storage blocks; the stripe programs read their points from the first.</summary>
        public int StorageBlocks { get; private set; }

        /// <summary>GL block index of each stage's constant bank, c1, or -1.</summary>
        public int VertexConstants { get; private set; }
        public int FragmentConstants { get; private set; }

        /// <summary>Each sampler with the texture unit it reads and the target it declares.</summary>
        public IReadOnlyList<(string Name, int Unit, TextureTarget Target)> Samplers
        {
            get;
            private set;
        }

        /// <summary>The first texture unit samplers are given; the units below stay free.</summary>
        public const int FirstUnit = 1;

        bool _resolved;

        GlEffectProgram(EmitterProgram program, ShaderInfo info)
        {
            Program = program;
            Info = info;
        }

        /// <summary>False while a deferred link is still running. The first call after the link
        /// resolves the bindings.</summary>
        public bool Ready()
        {
            if (_resolved)
                return true;
            if (Info.Program.IsPending && !Info.Program.PollReady())
                return false;
            Resolve();
            _resolved = true;
            return true;
        }

        void Resolve()
        {
            int id = Info.Program.program;
            var program = Program;

            var blocks = new List<(EffectBlock, int, int)>();
            foreach (var (block, slots) in program.Blocks)
            {
                int vs =
                    slots.Vertex < 0 ? -1 : GL.GetUniformBlockIndex(id, $"_vp_c{slots.Vertex + 3}");
                int fs =
                    slots.Fragment < 0
                        ? -1
                        : GL.GetUniformBlockIndex(id, $"_fp_c{slots.Fragment + 3}");
                if (vs >= 0 || fs >= 0)
                    blocks.Add((block, vs, fs));
            }
            Blocks = blocks;
            GL.GetProgramInterface(
                id,
                ProgramInterface.ShaderStorageBlock,
                ProgramInterfaceParameter.ActiveResources,
                out int storage
            );
            StorageBlocks = storage;
            VertexConstants = GL.GetUniformBlockIndex(id, "_vp_c1");
            FragmentConstants = GL.GetUniformBlockIndex(id, "_fp_c1");

            var types = ActiveSamplerTypes(id);
            var samplers = new List<(string, int, TextureTarget)>();
            foreach (
                var (name, slots) in program.Samplers.OrderBy(x => x.Key, StringComparer.Ordinal)
            )
            {
                int unit = FirstUnit + samplers.Count;
                var target = TextureTarget.Texture2D;
                bool bound = false;
                foreach (string uniform in UniformNames(slots))
                {
                    int loc = GL.GetUniformLocation(id, uniform);
                    if (loc < 0)
                        continue;
                    GL.ProgramUniform1(id, loc, unit);
                    target = types.GetValueOrDefault(uniform, target);
                    bound = true;
                }
                if (bound)
                    samplers.Add((name, unit, target));
            }
            Samplers = samplers;
        }

        static IEnumerable<string> UniformNames(StageSlots slots)
        {
            if (slots.Vertex >= 0)
                yield return "vp_tex_tcb_" + (slots.Vertex * 2 + 8).ToString("X1");
            if (slots.Fragment >= 0)
                yield return "fp_tex_tcb_" + (slots.Fragment * 2 + 8).ToString("X1");
        }

        static Dictionary<string, TextureTarget> ActiveSamplerTypes(int program)
        {
            var result = new Dictionary<string, TextureTarget>(StringComparer.Ordinal);
            GL.GetProgram(program, GetProgramParameterName.ActiveUniforms, out int count);
            for (int i = 0; i < count; i++)
            {
                string name = GL.GetActiveUniform(program, i, out _, out var type);
                var target = type switch
                {
                    ActiveUniformType.Sampler2DArray
                    or ActiveUniformType.Sampler2DArrayShadow
                    or ActiveUniformType.IntSampler2DArray
                    or ActiveUniformType.UnsignedIntSampler2DArray => TextureTarget.Texture2DArray,
                    ActiveUniformType.SamplerCube or ActiveUniformType.SamplerCubeShadow =>
                        TextureTarget.TextureCubeMap,
                    ActiveUniformType.SamplerCubeMapArray
                    or ActiveUniformType.SamplerCubeMapArrayShadow =>
                        TextureTarget.TextureCubeMapArray,
                    ActiveUniformType.Sampler3D => TextureTarget.Texture3D,
                    _ => TextureTarget.Texture2D,
                };
                result[name] = target;
            }
            return result;
        }

        /// <summary>Loads program <paramref name="index"/> of the archive, or null when it has none.
        /// With <paramref name="flipScreenSpace"/> the screen space samplers read V flipped, for
        /// render targets stored bottom row first.</summary>
        public static GlEffectProgram Load(ShaderArchive archive, int index, bool flipScreenSpace)
        {
            var program = EmitterProgram.Load(archive, index);
            if (program == null)
                return null;
            HashSet<string> flip = null;
            if (flipScreenSpace)
            {
                flip = new HashSet<string>(StringComparer.Ordinal);
                foreach (string name in EffectBindings.ScreenSpaceSamplers)
                    if (program.Samplers.TryGetValue(name, out var slots))
                        flip.UnionWith(UniformNames(slots));
            }
            var info = TegraShaderDecoder.LoadShaderProgram(
                null,
                new BfshaLibrary.ShaderVariation(program.Variation),
                flip
            );
            if (info?.Program == null)
                return null;
            return new GlEffectProgram(program, info);
        }
    }

    /// <summary>The linked programs of one effect file's general archive, loaded on first use.</summary>
    public sealed class EffectProgramCache : IDisposable
    {
        readonly ShaderArchive _archive;
        readonly bool _flipScreenSpace;
        readonly Dictionary<int, GlEffectProgram> _programs = new();

        public EffectProgramCache(ShaderArchive archive, bool flipScreenSpace)
        {
            _archive = archive;
            _flipScreenSpace = flipScreenSpace;
        }

        /// <summary>The program, or null when the archive has none or its link is still running.</summary>
        public GlEffectProgram Get(int index)
        {
            if (!_programs.TryGetValue(index, out var program))
                _programs[index] = program = GlEffectProgram.Load(
                    _archive,
                    index,
                    _flipScreenSpace
                );
            return program != null && program.Ready() ? program : null;
        }

        /// <summary>Whether program <paramref name="index"/> exists and its link is still running.</summary>
        public bool Pending(int index) =>
            _programs.TryGetValue(index, out var program) && program != null && !program.Ready();

        public void Dispose()
        {
            foreach (var p in _programs.Values)
                if (p != null)
                    TegraShaderDecoder.Release(p.Info);
            _programs.Clear();
        }
    }
}
