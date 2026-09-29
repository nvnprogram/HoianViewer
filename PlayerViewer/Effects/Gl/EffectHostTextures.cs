using System;
using System.Collections.Generic;
using OpenTK.Graphics.OpenGL;

namespace PlayerViewer.Effects.Gl
{
    /// <summary>
    /// What the host binds at the samplers on the game's custom shader callback
    /// </summary>
    public sealed class EffectHostTextures : IDisposable
    {
        /// <summary>The environment cube array, owned by the caller. Null binds a blank one.</summary>
        public Func<GlTexture?> EnvironmentCubeArray { get; set; }

        readonly Dictionary<(string, TextureTarget), GlTexture> _constants = new();
        readonly List<int> _owned = new();
        int _shadowSampler;
        readonly SphereMaps _sphereMaps = new();

        int _colorCopy,
            _depthCopy,
            _copyFbo,
            _depthFbo,
            _copyWidth,
            _copyHeight;

        /// <summary>The texture to bind at a host sampler, or null to leave it blank.</summary>
        public HostTexture? Get(string name, TextureTarget target)
        {
            switch (name)
            {
                case EffectBindings.FrameBufferTexture:
                    return Copy(_colorCopy, target);
                case "sysCustomShaderTextureSampler6":
                    //The stage's screen space shadow mask, not its colour: 1 is unshadowed.
                    return Constant(name, target, PixelInternalFormat.Rgba8, 1, 1, 1, 1);
                case EffectBindings.DepthBufferTexture:
                case "sysCustomShaderTextureSampler5":
                    return Copy(_depthCopy, target);
                case "sysCustomShaderCubeArraySampler0":
                case "sysCustomShaderTextureArraySampler1":
                    return Environment(target);
                case "sysCustomShaderShadowArraySampler0":
                case "sysCustomShaderShadowSampler0":
                    return Shadow(name, target);
                case "sysCustomShaderTextureSampler1":
                    //A variance shadow map: (1, 1) is nothing in front.
                    return Constant(name, target, PixelInternalFormat.Rg32f, 1, 1, 0, 0);
                case "sysCustomShaderTextureSampler2":
                    //The stage's ink map: (1, 1) is no ink.
                    return Constant(name, target, PixelInternalFormat.Rg8, 1, 1, 0, 0);
                case "sysCustomShaderTextureSampler4":
                    return Constant(name, target, PixelInternalFormat.Rgba8, 0, 0, 0, 1);
                case "sysCustomShaderTextureArraySampler0":
                    return Constant(
                        name,
                        target,
                        PixelInternalFormat.Rgba32f,
                        0.055f,
                        0.055f,
                        0.055f,
                        3f
                    );
            }
            return null;
        }

        static HostTexture? Copy(int id, TextureTarget target) =>
            id != 0 && target == TextureTarget.Texture2D
                ? new HostTexture(new GlTexture(id, target), 0)
                : null;

        HostTexture? Environment(TextureTarget target)
        {
            var cube = EnvironmentCubeArray?.Invoke();
            if (cube is not { } c)
                return null;
            if (target == TextureTarget.TextureCubeMapArray)
                return new HostTexture(c, 0);
            if (target != TextureTarget.Texture2DArray)
                return null;
            //The 2D array programs sample is the sphere maps, drawn from the cube array.
            return _sphereMaps.Texture is { } a ? new HostTexture(a, 0) : null;
        }

        /// <summary>
        /// Brings the sphere maps up to date for the main light: the direction towards it and the
        /// strength of its sharp highlight. Call before the frame's first effect draw.
        /// </summary>
        public void UpdateSphereMaps(System.Numerics.Vector3 toLight, System.Numerics.Vector3 lobe)
        {
            if (EnvironmentCubeArray?.Invoke() is { } cube)
                _sphereMaps.Update(cube, toLight, lobe);
        }

        HostTexture? Shadow(string name, TextureTarget target)
        {
            if (target is not (TextureTarget.Texture2D or TextureTarget.Texture2DArray))
                return null;
            if (_shadowSampler == 0)
            {
                _shadowSampler = GL.GenSampler();
                GL.SamplerParameter(
                    _shadowSampler,
                    SamplerParameterName.TextureCompareMode,
                    (int)TextureCompareMode.CompareRefToTexture
                );
                GL.SamplerParameter(
                    _shadowSampler,
                    SamplerParameterName.TextureCompareFunc,
                    (int)All.Lequal
                );
                GL.SamplerParameter(
                    _shadowSampler,
                    SamplerParameterName.TextureMinFilter,
                    (int)TextureMinFilter.Linear
                );
                GL.SamplerParameter(
                    _shadowSampler,
                    SamplerParameterName.TextureMagFilter,
                    (int)TextureMagFilter.Linear
                );
            }
            //Depth at the far plane everywhere: every compare passes, so nothing is shadowed.
            var tex = Constant(name, target, PixelInternalFormat.DepthComponent32f, 1, 0, 0, 0);
            return tex is { } t ? new HostTexture(t.Texture, _shadowSampler) : null;
        }

        HostTexture? Constant(
            string name,
            TextureTarget target,
            PixelInternalFormat format,
            float r,
            float g,
            float b,
            float a
        )
        {
            if (target is not (TextureTarget.Texture2D or TextureTarget.Texture2DArray))
                return null;
            if (!_constants.TryGetValue((name, target), out var tex))
            {
                int id = GL.GenTexture();
                _owned.Add(id);
                GL.BindTexture(target, id);
                bool depth = format == PixelInternalFormat.DepthComponent32f;
                var pixelFormat = depth ? PixelFormat.DepthComponent : PixelFormat.Rgba;
                var data = depth ? new[] { r } : new[] { r, g, b, a };
                if (target == TextureTarget.Texture2DArray)
                    GL.TexImage3D(
                        target,
                        0,
                        format,
                        1,
                        1,
                        1,
                        0,
                        pixelFormat,
                        PixelType.Float,
                        data
                    );
                else
                    GL.TexImage2D(target, 0, format, 1, 1, 0, pixelFormat, PixelType.Float, data);
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
                _constants[(name, target)] = tex = new GlTexture(id, target);
            }
            return new HostTexture(tex, 0);
        }

        /// <summary>
        /// Copies the bound draw target's colour, at half size as the game does, and its depth,
        /// into the textures the frame buffer and depth samplers read. The target must have a
        /// DepthComponent24 depth attachment.
        /// </summary>
        public void CopyFrame(int sourceFbo, int width, int height)
        {
            EnsureCopies(width, height);
            int halfW = Math.Max(width / 2, 1),
                halfH = Math.Max(height / 2, 1);
            GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, sourceFbo);
            GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, _copyFbo);
            GL.BlitFramebuffer(
                0,
                0,
                width,
                height,
                0,
                0,
                halfW,
                halfH,
                ClearBufferMask.ColorBufferBit,
                BlitFramebufferFilter.Linear
            );
            CopyDepth(sourceFbo, width, height);
        }

        /// <summary>
        /// Copies the bound draw target's depth alone, for the opaque colour draws, which read
        /// the depth their own prepass left.
        /// </summary>
        public void CopyDepth(int sourceFbo, int width, int height)
        {
            EnsureCopies(width, height);
            GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, sourceFbo);
            GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, _depthFbo);
            GL.BlitFramebuffer(
                0,
                0,
                width,
                height,
                0,
                0,
                width,
                height,
                ClearBufferMask.DepthBufferBit,
                BlitFramebufferFilter.Nearest
            );
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, sourceFbo);
        }

        void EnsureCopies(int width, int height)
        {
            if (_colorCopy != 0 && _copyWidth == width && _copyHeight == height)
                return;
            DisposeCopies();
            _copyWidth = width;
            _copyHeight = height;

            _colorCopy = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, _colorCopy);
            GL.TexImage2D(
                TextureTarget.Texture2D,
                0,
                PixelInternalFormat.R11fG11fB10f,
                Math.Max(width / 2, 1),
                Math.Max(height / 2, 1),
                0,
                PixelFormat.Rgb,
                PixelType.Float,
                IntPtr.Zero
            );
            SetClampLinear(TextureMinFilter.Linear, TextureMagFilter.Linear);

            _depthCopy = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, _depthCopy);
            GL.TexImage2D(
                TextureTarget.Texture2D,
                0,
                PixelInternalFormat.DepthComponent24,
                width,
                height,
                0,
                PixelFormat.DepthComponent,
                PixelType.Float,
                IntPtr.Zero
            );
            SetClampLinear(TextureMinFilter.Nearest, TextureMagFilter.Nearest);
            GL.BindTexture(TextureTarget.Texture2D, 0);

            _copyFbo = GL.GenFramebuffer();
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, _copyFbo);
            GL.FramebufferTexture2D(
                FramebufferTarget.Framebuffer,
                FramebufferAttachment.ColorAttachment0,
                TextureTarget.Texture2D,
                _colorCopy,
                0
            );
            _depthFbo = GL.GenFramebuffer();
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, _depthFbo);
            GL.FramebufferTexture2D(
                FramebufferTarget.Framebuffer,
                FramebufferAttachment.DepthAttachment,
                TextureTarget.Texture2D,
                _depthCopy,
                0
            );
            GL.DrawBuffer(DrawBufferMode.None);
            GL.ReadBuffer(ReadBufferMode.None);
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        }

        static void SetClampLinear(TextureMinFilter min, TextureMagFilter mag)
        {
            GL.TexParameter(
                TextureTarget.Texture2D,
                TextureParameterName.TextureMinFilter,
                (int)min
            );
            GL.TexParameter(
                TextureTarget.Texture2D,
                TextureParameterName.TextureMagFilter,
                (int)mag
            );
            GL.TexParameter(
                TextureTarget.Texture2D,
                TextureParameterName.TextureWrapS,
                (int)TextureWrapMode.ClampToEdge
            );
            GL.TexParameter(
                TextureTarget.Texture2D,
                TextureParameterName.TextureWrapT,
                (int)TextureWrapMode.ClampToEdge
            );
        }

        void DisposeCopies()
        {
            if (_copyFbo != 0)
                GL.DeleteFramebuffer(_copyFbo);
            if (_depthFbo != 0)
                GL.DeleteFramebuffer(_depthFbo);
            if (_colorCopy != 0)
                GL.DeleteTexture(_colorCopy);
            if (_depthCopy != 0)
                GL.DeleteTexture(_depthCopy);
            _copyFbo = _depthFbo = _colorCopy = _depthCopy = 0;
            _copyWidth = _copyHeight = 0;
        }

        public void Dispose()
        {
            DisposeCopies();
            foreach (int id in _owned)
                GL.DeleteTexture(id);
            _owned.Clear();
            _constants.Clear();
            _sphereMaps.Dispose();
            if (_shadowSampler != 0)
                GL.DeleteSampler(_shadowSampler);
            _shadowSampler = 0;
        }
    }
}
