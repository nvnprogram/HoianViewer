using System;
using System.Numerics;
using OpenTK.Graphics.OpenGL;

namespace PlayerViewer.UI
{
    /// <summary>
    /// The Side Order window background, drawn into the default framebuffer before ImGui. The
    /// ImGui renderer takes no draw callbacks, so this is its own pass: one full window triangle
    /// with the fused shader, which writes presented sRGB.
    /// </summary>
    public sealed class SideOrderBackground : IDisposable
    {
        int _program,
            _vao;
        int _uRes,
            _uBg,
            _uFrost,
            _uFloral0,
            _uFloral1,
            _uSheenAlpha,
            _uFloralAlpha,
            _uDrift0,
            _uDrift1,
            _uNoiseScroll,
            _uWobble;

        void Init()
        {
            if (_program != 0)
                return;
            SideOrderAssets.Load();
            _program = UiShaders.Program("SideOrderBackground.vert", "SideOrderBackground.frag");

            GL.UseProgram(_program);
            GL.Uniform1(GL.GetUniformLocation(_program, "uNoise"), 0);
            GL.Uniform1(GL.GetUniformLocation(_program, "uGlow"), 1);
            GL.Uniform1(GL.GetUniformLocation(_program, "uFloral"), 2);
            GL.UseProgram(0);
            _uRes = GL.GetUniformLocation(_program, "uRes");
            _uBg = GL.GetUniformLocation(_program, "uBg");
            _uFrost = GL.GetUniformLocation(_program, "uFrost");
            _uFloral0 = GL.GetUniformLocation(_program, "uFloral0");
            _uFloral1 = GL.GetUniformLocation(_program, "uFloral1");
            _uSheenAlpha = GL.GetUniformLocation(_program, "uSheenAlpha");
            _uFloralAlpha = GL.GetUniformLocation(_program, "uFloralAlpha");
            _uDrift0 = GL.GetUniformLocation(_program, "uDrift0");
            _uDrift1 = GL.GetUniformLocation(_program, "uDrift1");
            _uNoiseScroll = GL.GetUniformLocation(_program, "uNoiseScroll");
            _uWobble = GL.GetUniformLocation(_program, "uWobble");
            //Core profile draws need a vertex array even with no attributes.
            _vao = GL.GenVertexArray();
        }

        /// <summary>Fills the bound framebuffer, <paramref name="width"/> by <paramref name="height"/>.</summary>
        public void Draw(SideOrderTheme theme, int width, int height)
        {
            Init();
            double t = SideOrderLayout.Time;

            GL.Viewport(0, 0, width, height);
            GL.Disable(EnableCap.Blend);
            GL.Disable(EnableCap.ScissorTest);
            GL.Disable(EnableCap.DepthTest);
            GL.Disable(EnableCap.CullFace);
            GL.Disable(EnableCap.FramebufferSrgb);
            GL.UseProgram(_program);
            GL.Uniform2(_uRes, (float)width, (float)height);
            Set(_uBg, ColourSpace.Lin(theme.Bg) * theme.BgDarken);
            Set(_uFrost, ColourSpace.Lin(theme.Frost));
            Set(_uFloral0, ColourSpace.Lin(theme.Floral));
            Set(_uFloral1, ColourSpace.Lin(theme.Floral2));
            GL.Uniform1(_uSheenAlpha, theme.BgSheenAlpha);
            GL.Uniform1(_uFloralAlpha, theme.BgFloralAlpha);
            GL.Uniform2(
                _uDrift0,
                SideOrderLayout.Wrap(-0.0005 * t),
                SideOrderLayout.Wrap(0.0005 * t)
            );
            GL.Uniform2(
                _uDrift1,
                SideOrderLayout.Wrap(-0.001 * t),
                SideOrderLayout.Wrap(0.001 * t)
            );
            GL.Uniform2(_uNoiseScroll, 0f, SideOrderLayout.Wrap(SideOrderLayout.NoiseScroll * t));
            GL.Uniform1(_uWobble, SideOrderLayout.WobbleGain(height));

            GL.ActiveTexture(TextureUnit.Texture0);
            GL.BindTexture(TextureTarget.Texture2D, SideOrderAssets.Noise);
            GL.ActiveTexture(TextureUnit.Texture1);
            GL.BindTexture(TextureTarget.Texture2D, SideOrderAssets.Glow);
            GL.ActiveTexture(TextureUnit.Texture2);
            GL.BindTexture(TextureTarget.Texture2D, SideOrderAssets.Floral);

            GL.BindVertexArray(_vao);
            GL.DrawArrays(PrimitiveType.Triangles, 0, 3);
            GL.BindVertexArray(0);

            GL.BindTexture(TextureTarget.Texture2D, 0);
            GL.ActiveTexture(TextureUnit.Texture1);
            GL.BindTexture(TextureTarget.Texture2D, 0);
            GL.ActiveTexture(TextureUnit.Texture0);
            GL.BindTexture(TextureTarget.Texture2D, 0);
            GL.UseProgram(0);
        }

        static void Set(int location, Vector3 v) => GL.Uniform3(location, v.X, v.Y, v.Z);

        /// <summary>
        /// The colour this pass shows along the edge of a window this size, in sRGB: the fill
        /// and its sheen averaged in linear light over five points on each side. The florals
        /// are left out.
        /// </summary>
        public static Vector4 EdgeMean(SideOrderTheme theme, float width, float height)
        {
            if (width < 1 || height < 1)
                return theme.BgShown;
            var bg = ColourSpace.Lin(theme.Bg) * theme.BgDarken;
            var frost = ColourSpace.Lin(theme.Frost);
            var sum = Vector3.Zero;
            void Point(float x, float y)
            {
                float lobe = SideOrderAssets.SampleGlow(
                    0.5f + SideOrderLayout.SheenUvPerPixel * (x - width / 2),
                    0.5f + 0.38f / height * (y - height / 2)
                );
                sum += Vector3.Lerp(bg, frost, Math.Clamp(lobe * theme.BgSheenAlpha, 0, 1));
            }
            for (int i = 0; i < 5; i++)
            {
                float t = (i + 0.5f) / 5;
                Point(t * width, 0);
                Point(t * width, height);
                Point(0, t * height);
                Point(width, t * height);
            }
            var mean = sum / 20;
            return new Vector4(
                ColourSpace.ToSrgb(mean.X),
                ColourSpace.ToSrgb(mean.Y),
                ColourSpace.ToSrgb(mean.Z),
                1
            );
        }

        public void Dispose()
        {
            if (_program != 0)
                GL.DeleteProgram(_program);
            if (_vao != 0)
                GL.DeleteVertexArray(_vao);
            _program = _vao = 0;
        }
    }
}
