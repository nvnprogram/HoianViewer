using System;
using System.Numerics;
using GLFrameworkEngine;
using OpenTK.Graphics.OpenGL;

namespace PlayerViewer.Effects.Gl
{
    /// <summary>
    /// The seven layer sphere map array the game binds for effects where the models get their
    /// cube array: the environment as a chrome ball, layer 0 sharp, layers 1 to 5 blurred by
    /// roughness, layer 6 sharp, and every layer but 0 with the main light's highlight added. The
    /// ball is seen from a fixed direction relative to the light rather than from the camera, so
    /// the highlight sits at the same spot on every ball the programs shade. Drawn from the
    /// models' cube array, again when the light changes.
    /// </summary>
    sealed class SphereMaps : IDisposable
    {
        const int Size = 256;
        const int Layers = 7;
        const int Levels = 3;

        //The highlight in each layer: a GGX lobe on the half angle between the reflected direction
        //and the light, its width and its strength against layer 6's, fitted per layer.
        static readonly float[] LobeAlpha = { 0, 0.05f, 0.133f, 0.23f, 0.26f, 0.3f, 0.05f };
        static readonly float[] LobeWeight = { 0, 0.92f, 0.87f, 0.36f, 0.063f, 0.008f, 1 };

        //How far the light's horizontal direction is turned from the ball's back axis, to the left.
        const float LightYaw = 53 * MathF.PI / 180;

        ShaderProgram _shader;
        int _texture,
            _fbo,
            _vao;
        int _source;
        Vector3 _toLight,
            _lobe;

        public GlTexture? Texture =>
            _texture != 0 ? new GlTexture(_texture, TextureTarget.Texture2DArray) : null;

        /// <summary>
        /// Redraws the layers when the cube array, the direction towards the light or the strength
        /// of layer 6's highlight changed. Leaves the bound frame buffer and viewport as it found
        /// them.
        /// </summary>
        public void Update(GlTexture cube, Vector3 toLight, Vector3 lobe)
        {
            if (_texture != 0 && _source == cube.Id && toLight == _toLight && lobe == _lobe)
                return;
            (_source, _toLight, _lobe) = (cube.Id, toLight, lobe);
            Create();
            var (right, up, back) = Frame(toLight);

            GL.GetInteger(GetPName.DrawFramebufferBinding, out int drawFbo);
            GL.GetInteger(GetPName.ReadFramebufferBinding, out int readFbo);
            var viewport = new int[4];
            GL.GetInteger(GetPName.Viewport, viewport);
            GL.GetInteger(GetPName.CurrentProgram, out int program);
            GL.GetInteger(GetPName.ActiveTexture, out int activeUnit);
            GL.ActiveTexture(TextureUnit.Texture0);
            GL.GetInteger((GetPName)All.TextureBindingCubeMapArray, out int boundCube);
            GL.GetInteger(GetPName.TextureBinding2DArray, out int boundArray);
            GL.GetInteger(GetPName.SamplerBinding, out int boundSampler);
            bool blend = GL.IsEnabled(EnableCap.Blend),
                depth = GL.IsEnabled(EnableCap.DepthTest),
                cull = GL.IsEnabled(EnableCap.CullFace),
                scissor = GL.IsEnabled(EnableCap.ScissorTest);
            GL.GetBoolean(GetPName.DepthWritemask, out bool depthMask);
            var colorMask = new bool[4];
            GL.GetBoolean(GetPName.ColorWritemask, colorMask);

            GL.Disable(EnableCap.Blend);
            GL.Disable(EnableCap.DepthTest);
            GL.Disable(EnableCap.CullFace);
            GL.Disable(EnableCap.ScissorTest);
            GL.DepthMask(false);
            GL.ColorMask(true, true, true, true);
            GL.Viewport(0, 0, Size, Size);
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);

            int p = _shader.program;
            GL.UseProgram(p);
            GL.BindTexture(TextureTarget.TextureCubeMapArray, cube.Id);
            GL.BindSampler(0, 0);
            GL.Uniform1(GL.GetUniformLocation(p, "uCube"), 0);
            GL.Uniform1(GL.GetUniformLocation(p, "uSize"), (float)Size);
            GL.Uniform3(GL.GetUniformLocation(p, "uRight"), right.X, right.Y, right.Z);
            GL.Uniform3(GL.GetUniformLocation(p, "uUp"), up.X, up.Y, up.Z);
            GL.Uniform3(GL.GetUniformLocation(p, "uBack"), back.X, back.Y, back.Z);
            GL.Uniform3(GL.GetUniformLocation(p, "uToLight"), toLight.X, toLight.Y, toLight.Z);
            int cubeIndex = GL.GetUniformLocation(p, "uCubeIndex"),
                lobeColor = GL.GetUniformLocation(p, "uLobe"),
                lobeAlpha = GL.GetUniformLocation(p, "uAlpha");
            GL.BindVertexArray(_vao);
            for (int layer = 0; layer < Layers; layer++)
            {
                GL.FramebufferTextureLayer(
                    FramebufferTarget.Framebuffer,
                    FramebufferAttachment.ColorAttachment0,
                    _texture,
                    0,
                    layer
                );
                //Layer 6 is the sharp one; the rest are the roughness steps of the cube array.
                GL.Uniform1(cubeIndex, (float)(layer == 6 ? 0 : layer));
                var c = lobe * LobeWeight[layer];
                GL.Uniform3(lobeColor, c.X, c.Y, c.Z);
                GL.Uniform1(lobeAlpha, Math.Max(LobeAlpha[layer], 1e-3f));
                GL.DrawArrays(PrimitiveType.Triangles, 0, 3);
            }
            GL.BindVertexArray(0);
            GL.BindTexture(TextureTarget.Texture2DArray, _texture);
            GL.GenerateMipmap(GenerateMipmapTarget.Texture2DArray);

            GL.BindTexture(TextureTarget.TextureCubeMapArray, boundCube);
            GL.BindTexture(TextureTarget.Texture2DArray, boundArray);
            GL.BindSampler(0, boundSampler);
            GL.ActiveTexture((TextureUnit)activeUnit);

            GL.UseProgram(program);
            GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, drawFbo);
            GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, readFbo);
            GL.Viewport(viewport[0], viewport[1], viewport[2], viewport[3]);
            SetCap(EnableCap.Blend, blend);
            SetCap(EnableCap.DepthTest, depth);
            SetCap(EnableCap.CullFace, cull);
            SetCap(EnableCap.ScissorTest, scissor);
            GL.DepthMask(depthMask);
            GL.ColorMask(colorMask[0], colorMask[1], colorMask[2], colorMask[3]);
        }

        /// <summary>
        /// The ball's right, up and back axes in world space: up is world up, and back is the
        /// light's horizontal direction turned by <see cref="LightYaw"/>, so the light shows on
        /// the upper left of the ball at its own elevation.
        /// </summary>
        static (Vector3 Right, Vector3 Up, Vector3 Back) Frame(Vector3 toLight)
        {
            var h = new Vector3(toLight.X, 0, toLight.Z);
            h = h.LengthSquared() > 1e-8f ? Vector3.Normalize(h) : Vector3.UnitZ;
            var side = new Vector3(h.Z, 0, -h.X);
            float c = MathF.Cos(LightYaw),
                s = MathF.Sin(LightYaw);
            return (c * side - s * h, Vector3.UnitY, c * h + s * side);
        }

        static void SetCap(EnableCap cap, bool on)
        {
            if (on)
                GL.Enable(cap);
            else
                GL.Disable(cap);
        }

        void Create()
        {
            if (_texture != 0)
                return;
            _shader = new ShaderProgram(
                new FragmentShader(EffectShaders.Load("SphereMap.frag")),
                new VertexShader(EffectShaders.Load("Fullscreen.vert"))
            );
            _vao = GL.GenVertexArray();
            _texture = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2DArray, _texture);
            GL.TexStorage3D(
                TextureTarget3d.Texture2DArray,
                Levels,
                (SizedInternalFormat)All.R11fG11fB10f,
                Size,
                Size,
                Layers
            );
            GL.TexParameter(
                TextureTarget.Texture2DArray,
                TextureParameterName.TextureMinFilter,
                (int)TextureMinFilter.LinearMipmapLinear
            );
            GL.TexParameter(
                TextureTarget.Texture2DArray,
                TextureParameterName.TextureMagFilter,
                (int)TextureMagFilter.Linear
            );
            GL.TexParameter(
                TextureTarget.Texture2DArray,
                TextureParameterName.TextureWrapS,
                (int)TextureWrapMode.ClampToEdge
            );
            GL.TexParameter(
                TextureTarget.Texture2DArray,
                TextureParameterName.TextureWrapT,
                (int)TextureWrapMode.ClampToEdge
            );
            GL.BindTexture(TextureTarget.Texture2DArray, 0);
            _fbo = GL.GenFramebuffer();
        }

        public void Dispose()
        {
            if (_texture != 0)
                GL.DeleteTexture(_texture);
            if (_fbo != 0)
                GL.DeleteFramebuffer(_fbo);
            if (_vao != 0)
                GL.DeleteVertexArray(_vao);
            _shader?.Dispose();
            _shader = null;
            _texture = _fbo = _vao = 0;
        }
    }
}
