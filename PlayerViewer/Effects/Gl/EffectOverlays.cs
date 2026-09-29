using System;
using System.Collections.Generic;
using GLFrameworkEngine;
using OpenTK.Graphics.OpenGL;

namespace PlayerViewer.Effects.Gl
{
    /// <summary>A faint ground grid on y = 0, depth tested and never written to depth.</summary>
    sealed class EffectGrid : IDisposable
    {
        const float Spacing = 0.5f;
        const int Lines = 40;
        const float Extent = Spacing * Lines / 2;

        readonly ShaderProgram _shader;
        readonly int _vao,
            _vbo,
            _count;

        public EffectGrid()
        {
            _shader = new ShaderProgram(
                new FragmentShader(EffectShaders.Load("EffectGrid.frag")),
                new VertexShader(EffectShaders.Load("EffectGrid.vert"))
            );
            var data = new List<float>();
            for (int i = -Lines / 2; i <= Lines / 2; i++)
            {
                float at = i * Spacing;
                float strength =
                    i == 0 ? 0.3f
                    : i % 2 == 0 ? 0.12f
                    : 0.06f;
                data.AddRange(new[] { at, 0, -Extent, strength, at, 0, Extent, strength });
                data.AddRange(new[] { -Extent, 0, at, strength, Extent, 0, at, strength });
            }
            _count = data.Count / 4;
            _vao = GL.GenVertexArray();
            _vbo = GL.GenBuffer();
            GL.BindVertexArray(_vao);
            GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
            GL.BufferData(
                BufferTarget.ArrayBuffer,
                data.Count * 4,
                data.ToArray(),
                BufferUsageHint.StaticDraw
            );
            GL.EnableVertexAttribArray(0);
            GL.VertexAttribPointer(0, 4, VertexAttribPointerType.Float, false, 16, 0);
            GL.BindVertexArray(0);
            GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
        }

        public void Draw(Camera camera)
        {
            GL.UseProgram(_shader.program);
            var viewProj = camera.ViewMatrix * camera.ProjectionMatrix;
            GL.UniformMatrix4(
                GL.GetUniformLocation(_shader.program, "uViewProj"),
                false,
                ref viewProj
            );
            GL.Uniform3(GL.GetUniformLocation(_shader.program, "uColor"), 0.75f, 0.75f, 0.8f);
            GL.Uniform1(GL.GetUniformLocation(_shader.program, "uExtent"), Extent);
            GL.Enable(EnableCap.Blend);
            GL.BlendFuncSeparate(
                BlendingFactorSrc.SrcAlpha,
                BlendingFactorDest.OneMinusSrcAlpha,
                BlendingFactorSrc.Zero,
                BlendingFactorDest.One
            );
            GL.BlendEquation(BlendEquationMode.FuncAdd);
            GL.Enable(EnableCap.DepthTest);
            GL.DepthFunc(DepthFunction.Lequal);
            GL.DepthMask(false);
            GL.Disable(EnableCap.CullFace);
            GL.BindVertexArray(_vao);
            GL.DrawArrays(PrimitiveType.Lines, 0, _count);
            GL.BindVertexArray(0);
            GL.DepthMask(true);
            GL.Disable(EnableCap.Blend);
            GL.UseProgram(0);
        }

        public void Dispose()
        {
            GL.DeleteVertexArray(_vao);
            GL.DeleteBuffer(_vbo);
            _shader.Dispose();
        }
    }

    /// <summary>
    /// Rewrites the target's colour from premultiplied to straight alpha in place, through a copy
    /// of it. See Unpremultiply.frag.
    /// </summary>
    sealed class Unpremultiply : IDisposable
    {
        readonly ShaderProgram _shader;
        readonly int _vao;
        int _copy,
            _copyFbo,
            _width,
            _height;

        public Unpremultiply()
        {
            _shader = new ShaderProgram(
                new FragmentShader(EffectShaders.Load("Unpremultiply.frag")),
                new VertexShader(EffectShaders.Load("Fullscreen.vert"))
            );
            _vao = GL.GenVertexArray();
        }

        public void Apply(int targetFbo, int width, int height)
        {
            if (_copy == 0 || width != _width || height != _height)
            {
                DeleteCopy();
                _width = width;
                _height = height;
                _copy = GL.GenTexture();
                GL.BindTexture(TextureTarget.Texture2D, _copy);
                GL.TexImage2D(
                    TextureTarget.Texture2D,
                    0,
                    PixelInternalFormat.Rgba16f,
                    width,
                    height,
                    0,
                    PixelFormat.Rgba,
                    PixelType.HalfFloat,
                    IntPtr.Zero
                );
                GL.TexParameter(
                    TextureTarget.Texture2D,
                    TextureParameterName.TextureMinFilter,
                    (int)TextureMinFilter.Nearest
                );
                GL.TexParameter(
                    TextureTarget.Texture2D,
                    TextureParameterName.TextureMagFilter,
                    (int)TextureMagFilter.Nearest
                );
                GL.BindTexture(TextureTarget.Texture2D, 0);
                _copyFbo = GL.GenFramebuffer();
                GL.BindFramebuffer(FramebufferTarget.Framebuffer, _copyFbo);
                GL.FramebufferTexture2D(
                    FramebufferTarget.Framebuffer,
                    FramebufferAttachment.ColorAttachment0,
                    TextureTarget.Texture2D,
                    _copy,
                    0
                );
            }

            GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, targetFbo);
            GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, _copyFbo);
            GL.BlitFramebuffer(
                0,
                0,
                width,
                height,
                0,
                0,
                width,
                height,
                ClearBufferMask.ColorBufferBit,
                BlitFramebufferFilter.Nearest
            );
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, targetFbo);

            GL.Disable(EnableCap.Blend);
            GL.Disable(EnableCap.DepthTest);
            GL.Disable(EnableCap.CullFace);
            GL.DepthMask(false);
            GL.ColorMask(true, true, true, true);
            GL.UseProgram(_shader.program);
            GL.ActiveTexture(TextureUnit.Texture0);
            GL.BindTexture(TextureTarget.Texture2D, _copy);
            GL.BindSampler(0, 0);
            GL.Uniform1(GL.GetUniformLocation(_shader.program, "uColorTex"), 0);
            GL.BindVertexArray(_vao);
            GL.DrawArrays(PrimitiveType.Triangles, 0, 3);
            GL.BindVertexArray(0);
            GL.BindTexture(TextureTarget.Texture2D, 0);
            GL.UseProgram(0);
            GL.DepthMask(true);
            GL.Enable(EnableCap.DepthTest);
        }

        void DeleteCopy()
        {
            if (_copyFbo != 0)
                GL.DeleteFramebuffer(_copyFbo);
            if (_copy != 0)
                GL.DeleteTexture(_copy);
            _copy = _copyFbo = 0;
        }

        public void Dispose()
        {
            DeleteCopy();
            GL.DeleteVertexArray(_vao);
            _shader.Dispose();
        }
    }
}
