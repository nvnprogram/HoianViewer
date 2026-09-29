using System;
using System.Collections.Generic;
using EffectLibrary;
using OpenTK.Graphics.OpenGL;

namespace PlayerViewer.Effects.Gl
{
    /// <summary>A texture the host binds to a sampler it owns, with the sampler object to read
    /// it through (0 for the texture's own parameters).</summary>
    public readonly record struct HostTexture(GlTexture Texture, int Sampler);

    /// <summary>
    /// Draws one emitter: its program for the pass, the uniform blocks, the textures, the per
    /// particle streams as instanced float4 attributes, and the pass's fixed function state.
    /// Holds the GPU side of every effect file it has drawn from.
    /// </summary>
    public sealed class EffectRenderer : IDisposable
    {
        /// <summary>Screen space samplers read V flipped: set when the render targets are stored
        /// bottom row first, as the viewer's are. Must be set before the first draw.</summary>
        public bool FlipScreenSpace { get; init; } = true;

        /// <summary>Textures for the samplers the host owns, by reflection name. An entry for an
        /// emitter texture slot overrides the emitter's own texture.</summary>
        public Dictionary<string, HostTexture> HostTextures { get; } = new(StringComparer.Ordinal);

        /// <summary>
        /// For a target that keeps coverage in alpha: only normal blending writes alpha, and
        /// every other blend leaves it, so their coverage can be read from their brightness.
        /// </summary>
        public bool CoverageAlpha { get; set; }

        /// <summary>Asked for a host sampler <see cref="HostTextures"/> has no entry for, with the
        /// target the program declares; null leaves it blank.</summary>
        public Func<string, TextureTarget, HostTexture?> HostSource { get; set; }

        sealed class FileResources : IDisposable
        {
            public EffectProgramCache Programs;
            public EffectTextures Textures;
            public readonly Dictionary<ulong, GlMesh> Meshes = new();
            public readonly Dictionary<Emitter, byte[]> Static = new();

            public void Dispose()
            {
                Programs.Dispose();
                Textures.Dispose();
                foreach (var m in Meshes.Values)
                    m?.Dispose();
            }
        }

        readonly Dictionary<VfxFile, FileResources> _files = new();
        readonly int[] _blockBuffers = new int[2 + 16];
        int _particleBuffer,
            _vao,
            _storageBuffer,
            _stripeIndices;

        /// <summary>
        /// The runtime's two stripe index buffers, one after the other: even vertex ids for the
        /// strip, odd ones for the crossed strip. The program reads point id / 4 and the side
        /// from the next bit.
        /// </summary>
        const int StripeIndexCount = 0x15EC;
        readonly Dictionary<TextureTarget, int> _blank = new();

        /// <summary>The game binds nothing for an empty texture slot, so the emitter reads what
        /// the last emitter to fill that slot left there.</summary>
        readonly (GlTexture Texture, int Sampler)?[] _lastInSlot = new (GlTexture, int)?[
            EffectBindings.TextureSlotCount
        ];

        /// <summary>Binding points: 0 and 1 for the stages' constant banks, 2 + slot for blocks.</summary>
        const int ConstantsVertexBinding = 0;
        const int ConstantsFragmentBinding = 1;

        static readonly byte[] ZeroBlock = new byte[0x4000];

        FileResources Resources(VfxFile file)
        {
            if (!_files.TryGetValue(file, out var r))
                _files[file] = r = new FileResources
                {
                    Programs = new EffectProgramCache(file.Shaders, FlipScreenSpace),
                    Textures = new EffectTextures(file.Textures),
                };
            return r;
        }

        /// <summary>
        /// Draws <paramref name="emitter"/> for <paramref name="pass"/> into the bound framebuffer.
        /// False when its program is not linked yet or it has none.
        /// </summary>
        public bool Draw(Emitter emitter, EffectPass pass, EmitterDrawInputs inputs)
        {
            var res = Resources(emitter.Set.File);
            int index = EffectPasses.ProgramIndex(emitter, pass);
            var program = res.Programs.Get(index);
            if (program == null && res.Programs.Pending(index))
                PendingDraws++;
            if (program == null || inputs.InstanceCount <= 0)
                return false;

            EnsureObjects();
            GL.UseProgram(program.Id);
            Apply(EmitterDrawState.For(emitter, pass));
            BindBlocks(program, inputs, res, emitter);
            BindTextures(program, res, emitter);

            GL.BindVertexArray(_vao);
            for (int i = 0; i < 16; i++)
                GL.DisableVertexAttribArray(i);
            if (inputs.Stripes != null)
            {
                DrawStripes(program, inputs);
                GL.BindVertexArray(0);
                return true;
            }
            BindParticles(program, inputs);
            var mesh = Mesh(res, emitter);
            mesh?.Bind(program.Program);
            var (pluginVs, pluginFs) = BlockSlots(program, EffectBlock.EmitterPlugin);
            int repeats = inputs.PluginBlocks?.Count ?? 1;
            for (int r = 0; r < repeats; r++)
            {
                if (inputs.PluginBlocks != null)
                    Upload(
                        program.Id,
                        pluginVs,
                        pluginFs,
                        2 + (int)EffectBlock.EmitterPlugin,
                        inputs.PluginBlocks[r]
                    );
                if (mesh != null)
                    GL.DrawElementsInstanced(
                        PrimitiveType.Triangles,
                        mesh.IndexCount,
                        DrawElementsType.UnsignedInt,
                        IntPtr.Zero,
                        inputs.InstanceCount
                    );
                else
                    GL.DrawArraysInstanced(PrimitiveType.TriangleStrip, 0, 4, inputs.InstanceCount);
            }
            GL.BindVertexArray(0);
            return true;
        }

        /// <summary>Puts back the state a draw changes that the rest of the viewer assumes.</summary>
        public void EndDraws()
        {
            GL.UseProgram(0);
            GL.Disable(EnableCap.Blend);
            GL.ColorMask(true, true, true, true);
            GL.DepthMask(true);
            GL.DepthFunc(DepthFunction.Lequal);
            GL.Enable(EnableCap.DepthTest);
            GL.Disable(EnableCap.CullFace);
            GL.Disable(EnableCap.PolygonOffsetFill);
            GL.Disable(EnableCap.DepthClamp);
            for (int unit = 0; unit < 32; unit++)
                GL.BindSampler(unit, 0);
            GL.ActiveTexture(TextureUnit.Texture0);
        }

        void EnsureObjects()
        {
            if (_vao != 0)
                return;
            _vao = GL.GenVertexArray();
            _particleBuffer = GL.GenBuffer();
            GL.GenBuffers(_blockBuffers.Length, _blockBuffers);
        }

        void Apply(EmitterDrawState s)
        {
            if (s.Blend)
            {
                bool over =
                    s.ColorSrc == BlendFactor.SrcAlpha
                    && s.ColorDst == BlendFactor.OneMinusSrcAlpha;
                bool keepAlpha = CoverageAlpha && !over;
                GL.Enable(EnableCap.Blend);
                GL.BlendFuncSeparate(
                    Factor(s.ColorSrc),
                    (BlendingFactorDest)Factor(s.ColorDst),
                    keepAlpha ? BlendingFactorSrc.Zero : Factor(s.AlphaSrc),
                    keepAlpha ? BlendingFactorDest.One : (BlendingFactorDest)Factor(s.AlphaDst)
                );
                GL.BlendEquationSeparate(Op(s.ColorOp), Op(s.AlphaOp));
            }
            else
                GL.Disable(EnableCap.Blend);
            GL.ColorMask(s.ColorWrite, s.ColorWrite, s.ColorWrite, s.ColorWrite);
            if (s.DepthTest)
            {
                GL.Enable(EnableCap.DepthTest);
                GL.DepthFunc(DepthFunction.Never + (int)s.DepthFunc);
            }
            else
                GL.Disable(EnableCap.DepthTest);
            GL.DepthMask(s.DepthWrite);
            if (s.Cull == CullMode.None)
                GL.Disable(EnableCap.CullFace);
            else
            {
                GL.Enable(EnableCap.CullFace);
                GL.CullFace(s.Cull == CullMode.Back ? CullFaceMode.Back : CullFaceMode.Front);
            }
            GL.FrontFace(FrontFaceDirection.Ccw);
            if (s.DepthBiasSlope != 0 || s.DepthBiasConstant != 0)
            {
                GL.Enable(EnableCap.PolygonOffsetFill);
                GL.PolygonOffset(s.DepthBiasSlope, s.DepthBiasConstant);
            }
            else
                GL.Disable(EnableCap.PolygonOffsetFill);
            if (s.DepthClamp)
                GL.Enable(EnableCap.DepthClamp);
            else
                GL.Disable(EnableCap.DepthClamp);
        }

        static BlendingFactorSrc Factor(BlendFactor f) =>
            f switch
            {
                BlendFactor.Zero => BlendingFactorSrc.Zero,
                BlendFactor.One => BlendingFactorSrc.One,
                BlendFactor.SrcColor => BlendingFactorSrc.SrcColor,
                BlendFactor.OneMinusSrcColor => BlendingFactorSrc.OneMinusSrcColor,
                BlendFactor.DstColor => BlendingFactorSrc.DstColor,
                BlendFactor.OneMinusDstColor => BlendingFactorSrc.OneMinusDstColor,
                BlendFactor.SrcAlpha => BlendingFactorSrc.SrcAlpha,
                _ => BlendingFactorSrc.OneMinusSrcAlpha,
            };

        static BlendEquationMode Op(BlendOp op) =>
            op switch
            {
                BlendOp.Subtract => BlendEquationMode.FuncSubtract,
                BlendOp.ReverseSubtract => BlendEquationMode.FuncReverseSubtract,
                _ => BlendEquationMode.FuncAdd,
            };

        void BindBlocks(
            GlEffectProgram program,
            EmitterDrawInputs inputs,
            FileResources res,
            Emitter emitter
        )
        {
            Upload(
                program.Id,
                program.VertexConstants,
                -1,
                ConstantsVertexBinding,
                program.Info.VertexConstants
            );
            Upload(
                program.Id,
                program.FragmentConstants,
                -1,
                ConstantsFragmentBinding,
                program.Info.PixelConstants
            );
            foreach (var (block, vs, fs) in program.Blocks)
            {
                var data = inputs[block];
                if (data == null && block == EffectBlock.EmitterStatic)
                {
                    if (!res.Static.TryGetValue(emitter, out data))
                        res.Static[emitter] = data = StaticUniformBlock.Build(emitter);
                }
                Upload(program.Id, vs, fs, 2 + (int)block, data ?? ZeroBlock);
            }
        }

        void Upload(int programId, int vsIndex, int fsIndex, int binding, byte[] data)
        {
            if (vsIndex < 0 && fsIndex < 0)
                return;
            data ??= ZeroBlock;
            int buffer = _blockBuffers[binding];
            GL.BindBuffer(BufferTarget.UniformBuffer, buffer);
            GL.BufferData(
                BufferTarget.UniformBuffer,
                data.Length,
                data,
                BufferUsageHint.StreamDraw
            );
            GL.BindBufferBase(BufferRangeTarget.UniformBuffer, binding, buffer);
            GLFrameworkEngine.UniformBlock.ForgetBindings();
            if (vsIndex >= 0)
                GL.UniformBlockBinding(programId, vsIndex, binding);
            if (fsIndex >= 0)
                GL.UniformBlockBinding(programId, fsIndex, binding);
        }

        void BindTextures(GlEffectProgram program, FileResources res, Emitter emitter)
        {
            //Resolved before any unit is bound: a first use uploads, which rebinds the active unit.
            var bindings = new (int Unit, TextureTarget Target, int Texture, int Sampler)[
                program.Samplers.Count
            ];
            for (int i = 0; i < bindings.Length; i++)
            {
                var (name, unit, target) = program.Samplers[i];
                GlTexture? texture = null;
                int sampler = 0;
                int slot = TextureSlot(name);
                if (HostTextures.TryGetValue(name, out var host))
                {
                    texture = host.Texture;
                    sampler = host.Sampler;
                }
                else if (
                    EffectBindings.IsHostSampler(name)
                    && HostSource?.Invoke(name, target) is { } supplied
                )
                {
                    texture = supplied.Texture;
                    sampler = supplied.Sampler;
                }
                else if (slot >= 0 && emitter.ResolveTexture(slot) == null)
                {
                    if (_lastInSlot[slot] is { } last)
                        (texture, sampler) = (last.Texture, last.Sampler);
                }
                else if (slot >= 0)
                {
                    texture = res.Textures.Get(emitter, slot);
                    sampler = res.Textures.Sampler(emitter.GetSampler(slot));
                    if (texture != null)
                        _lastInSlot[slot] = (texture.Value, sampler);
                }
                int id = texture is GlTexture t && t.Target == target ? t.Id : Blank(target);
                bindings[i] = (unit, target, id, sampler);
            }
            foreach (var (unit, target, id, sampler) in bindings)
            {
                GL.ActiveTexture(TextureUnit.Texture0 + unit);
                GL.BindTexture(target, id);
                GL.BindSampler(unit, sampler);
            }
            GL.ActiveTexture(TextureUnit.Texture0);
        }

        /// <summary>The GPU texture an emitter slot names, uploading it on first use; null when
        /// the slot is empty or will not upload.</summary>
        public GlTexture? Texture(Emitter emitter, int slot) =>
            Resources(emitter.Set.File).Textures.Get(emitter, slot);

        /// <summary>Forgets the last texture per slot, so a new frame does not inherit the
        /// previous one's.</summary>
        /// <summary>Draws skipped since <see cref="BeginFrame"/> because a program was still linking.</summary>
        public int PendingDraws { get; private set; }

        public void BeginFrame()
        {
            Array.Clear(_lastInSlot);
            PendingDraws = 0;
        }

        static int TextureSlot(string name)
        {
            const string prefix = "sysTextureSampler";
            if (
                !name.StartsWith(prefix, StringComparison.Ordinal)
                || name.Length != prefix.Length + 1
            )
                return -1;
            int slot = name[^1] - '0';
            return slot >= 0 && slot < EffectBindings.TextureSlotCount ? slot : -1;
        }

        /// <summary>A 1x1 zero texture of the target, so an unfed sampler never reads whatever the
        /// previous draw left on the unit.</summary>
        int Blank(TextureTarget target)
        {
            if (_blank.TryGetValue(target, out int id))
                return id;
            id = GL.GenTexture();
            GL.BindTexture(target, id);
            var zero = new byte[4 * 6];
            switch (target)
            {
                case TextureTarget.Texture2DArray:
                case TextureTarget.Texture3D:
                    GL.TexImage3D(
                        target,
                        0,
                        PixelInternalFormat.Rgba8,
                        1,
                        1,
                        1,
                        0,
                        PixelFormat.Rgba,
                        PixelType.UnsignedByte,
                        zero
                    );
                    break;
                case TextureTarget.TextureCubeMapArray:
                    GL.TexImage3D(
                        target,
                        0,
                        PixelInternalFormat.Rgba8,
                        1,
                        1,
                        6,
                        0,
                        PixelFormat.Rgba,
                        PixelType.UnsignedByte,
                        zero
                    );
                    break;
                case TextureTarget.TextureCubeMap:
                    for (int face = 0; face < 6; face++)
                        GL.TexImage2D(
                            TextureTarget.TextureCubeMapPositiveX + face,
                            0,
                            PixelInternalFormat.Rgba8,
                            1,
                            1,
                            0,
                            PixelFormat.Rgba,
                            PixelType.UnsignedByte,
                            zero
                        );
                    break;
                default:
                    GL.TexImage2D(
                        target,
                        0,
                        PixelInternalFormat.Rgba8,
                        1,
                        1,
                        0,
                        PixelFormat.Rgba,
                        PixelType.UnsignedByte,
                        zero
                    );
                    break;
            }
            GL.TexParameter(
                target,
                TextureParameterName.TextureMinFilter,
                (int)TextureMinFilter.Nearest
            );
            GL.TexParameter(
                target,
                TextureParameterName.TextureMagFilter,
                (int)TextureMagFilter.Nearest
            );
            GL.BindTexture(target, 0);
            _blank[target] = id;
            return id;
        }

        void BindParticles(GlEffectProgram program, EmitterDrawInputs inputs)
        {
            int total = 0;
            foreach (var (name, data) in inputs.Particles)
                if (program.Program.Attribute(name) >= 0)
                    total += data.Length * 4;
            GL.BindBuffer(BufferTarget.ArrayBuffer, _particleBuffer);
            GL.BufferData(
                BufferTarget.ArrayBuffer,
                Math.Max(total, 16),
                IntPtr.Zero,
                BufferUsageHint.StreamDraw
            );
            int offset = 0;
            foreach (var (name, data) in inputs.Particles)
            {
                int location = program.Program.Attribute(name);
                if (location < 0)
                    continue;
                GL.BufferSubData(BufferTarget.ArrayBuffer, (IntPtr)offset, data.Length * 4, data);
                GL.EnableVertexAttribArray(location);
                GL.VertexAttribPointer(
                    location,
                    4,
                    VertexAttribPointerType.Float,
                    false,
                    16,
                    offset
                );
                GL.VertexAttribDivisor(location, 1);
                offset += data.Length * 4;
            }
            GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
        }

        /// <summary>
        /// A stripe emitter: its points go up as the storage buffer, then each stripe draws a
        /// triangle strip through the index buffer with its own plugin block.
        /// </summary>
        void DrawStripes(GlEffectProgram program, EmitterDrawInputs inputs)
        {
            if (_stripeIndices == 0)
            {
                var indices = new ushort[StripeIndexCount * 2];
                for (int i = 0; i < StripeIndexCount; i++)
                {
                    indices[i] = (ushort)(2 * i);
                    indices[StripeIndexCount + i] = (ushort)(2 * i + 1);
                }
                _stripeIndices = GL.GenBuffer();
                GL.BindBuffer(BufferTarget.ElementArrayBuffer, _stripeIndices);
                GL.BufferData(
                    BufferTarget.ElementArrayBuffer,
                    indices.Length * 2,
                    indices,
                    BufferUsageHint.StaticDraw
                );
            }
            GL.BindBuffer(BufferTarget.ElementArrayBuffer, _stripeIndices);
            if (_storageBuffer == 0)
                _storageBuffer = GL.GenBuffer();
            var storage = inputs.PluginStorage;
            GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _storageBuffer);
            GL.BufferData(
                BufferTarget.ShaderStorageBuffer,
                Math.Max(storage.Length * 4, 16),
                storage,
                BufferUsageHint.StreamDraw
            );
            GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 0, _storageBuffer);
            for (int i = 0; i < program.StorageBlocks; i++)
                GL.ShaderStorageBlockBinding(program.Id, i, 0);

            var (vs, fs) = BlockSlots(program, EffectBlock.EmitterPlugin);
            foreach (var d in inputs.Stripes)
            {
                int count = Math.Min(d.VertexCount, StripeIndexCount);
                if (count <= 0)
                    continue;
                Upload(program.Id, vs, fs, 2 + (int)EffectBlock.EmitterPlugin, d.Block);
                GL.DrawElements(
                    PrimitiveType.TriangleStrip,
                    count,
                    DrawElementsType.UnsignedShort,
                    0
                );
                if (d.Cross)
                    GL.DrawElements(
                        PrimitiveType.TriangleStrip,
                        count,
                        DrawElementsType.UnsignedShort,
                        StripeIndexCount * 2
                    );
            }
            GL.BindBuffer(BufferTarget.ShaderStorageBuffer, 0);
        }

        /// <summary>The program's vertex and fragment block indices for <paramref name="block"/>, -1 where unused.</summary>
        static (int Vertex, int Fragment) BlockSlots(GlEffectProgram program, EffectBlock block)
        {
            foreach (var (b, v, f) in program.Blocks)
                if (b == block)
                    return (v, f);
            return (-1, -1);
        }

        static GlMesh Mesh(FileResources res, Emitter emitter)
        {
            ulong id = emitter.ParticlePrimitiveId;
            if (id == 0 || id == ulong.MaxValue)
                return null;
            if (!res.Meshes.TryGetValue(id, out var mesh))
            {
                var data = EffectMesh.From(emitter.Set.File, emitter.ResolvePrimitive(id));
                res.Meshes[id] = mesh = data == null ? null : new GlMesh(data);
            }
            return mesh;
        }

        public void Dispose()
        {
            foreach (var r in _files.Values)
                r.Dispose();
            _files.Clear();
            if (_vao != 0)
            {
                GL.DeleteVertexArray(_vao);
                GL.DeleteBuffer(_particleBuffer);
                GL.DeleteBuffers(_blockBuffers.Length, _blockBuffers);
                _vao = 0;
            }
            if (_storageBuffer != 0)
                GL.DeleteBuffer(_storageBuffer);
            if (_stripeIndices != 0)
                GL.DeleteBuffer(_stripeIndices);
            _storageBuffer = _stripeIndices = 0;
            foreach (int t in _blank.Values)
                GL.DeleteTexture(t);
            _blank.Clear();
        }
    }
}
