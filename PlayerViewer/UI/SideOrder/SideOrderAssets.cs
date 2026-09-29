using System;
using System.Numerics;
using OpenTK.Graphics.OpenGL;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace PlayerViewer.UI
{
    /// <summary>
    /// Side Order UI's textures. Glow and the button base are alpha masks, uploaded white
    /// so ImGui's colour times texture tints them; floral and noise repeat with mipmaps,
    /// since the floral's grain depends on the mip the noise lands on.
    /// </summary>
    public static class SideOrderAssets
    {
        public static int Floral { get; private set; }
        public static int Glow { get; private set; }
        public static int Noise { get; private set; }
        public static int ButtonBase { get; private set; }
        public static int ButtonSymbol { get; private set; }

        /// <summary>White with alpha falling linearly from 1 at the top to 0 at the bottom.</summary>
        public static int Ramp { get; private set; }

        public static IntPtr GlowId => (IntPtr)Glow;
        public static IntPtr RampId => (IntPtr)Ramp;
        public static IntPtr ButtonBaseId => (IntPtr)ButtonBase;
        public static IntPtr ButtonSymbolId => (IntPtr)ButtonSymbol;

        public static bool Ready => Glow != 0;

        //The noise on the CPU, for the per panel wobble.
        static Rgba32[] _noise;
        static int _noiseSize;

        public static void Load()
        {
            if (Ready)
                return;
            Floral = Upload(Read("floral"), mask: false, repeat: true);
            var noise = Read("noise");
            (_noise, _noiseSize) = KeepPixels(noise);
            Noise = Upload(noise, mask: false, repeat: true);
            var glow = Read("glow");
            (var glowPixels, _glowSize) = KeepPixels(glow);
            _glowAlpha = Array.ConvertAll(glowPixels, p => p.A);
            Glow = Upload(glow, mask: true, repeat: false);
            ButtonBase = Upload(Read("buttonBase"), mask: true, repeat: false);
            ButtonSymbol = Upload(Read("buttonSymbol"), mask: false, repeat: false);

            const int rampH = 64;
            var ramp = new Image<Rgba32>(4, rampH);
            for (int y = 0; y < rampH; y++)
            for (int x = 0; x < 4; x++)
                ramp[x, y] = new Rgba32(255, 255, 255, (byte)(255 - y * 255 / (rampH - 1)));
            Ramp = Upload(ramp, mask: false, repeat: false);

            //Never sampled: a texture of its own so the renderer knows a rim's commands.
            Rim = Upload(
                new Image<Rgba32>(1, 1, new Rgba32(255, 255, 255, 255)),
                mask: false,
                repeat: false
            );
            _rimProgram = UiShaders.Program("SideOrderRim.vert", "SideOrderRim.frag");
            CafeStudio.UI.ImGuiController.TexturePrograms[RimId] = _rimProgram;
            RimShade = _rimShade;
        }

        static int _rimProgram;
        static float _rimShade = 0.6f;

        /// <summary>How dark the rim's line down the sides is, at full light.</summary>
        public static float RimShade
        {
            get => _rimShade;
            set
            {
                _rimShade = value;
                if (_rimProgram == 0)
                    return;
                int previous = GL.GetInteger(GetPName.CurrentProgram);
                GL.UseProgram(_rimProgram);
                GL.Uniform1(GL.GetUniformLocation(_rimProgram, "shadeLevel"), value);
                GL.UseProgram(previous);
            }
        }

        /// <summary>The texture <see cref="SideOrderSurface.RimLight"/> draws with, whose commands use the rim program.</summary>
        public static int Rim { get; private set; }

        public static IntPtr RimId => (IntPtr)Rim;

        /// <summary>Bilinear, wrapping sample of noise.png's red and green at a uv.</summary>
        public static Vector2 SampleNoise(float u, float v)
        {
            if (_noise == null)
                return new Vector2(0.5f);
            int n = _noiseSize;
            float x = u * n - 0.5f,
                y = v * n - 0.5f;
            int x0 = (int)MathF.Floor(x),
                y0 = (int)MathF.Floor(y);
            float fx = x - x0,
                fy = y - y0;
            Vector2 At(int ix, int iy)
            {
                var p = _noise[Wrap(iy, n) * n + Wrap(ix, n)];
                return new Vector2(p.R, p.G) / 255f;
            }
            var top = Vector2.Lerp(At(x0, y0), At(x0 + 1, y0), fx);
            var bottom = Vector2.Lerp(At(x0, y0 + 1), At(x0 + 1, y0 + 1), fx);
            return Vector2.Lerp(top, bottom, fy);
        }

        static int Wrap(int i, int n) => ((i % n) + n) % n;

        //glow.png's alpha on the CPU, for the background's colour along the window edge.
        static byte[] _glowAlpha;
        static int _glowSize;

        /// <summary>Bilinear, clamped sample of glow.png's alpha at a uv, 0 to 1.</summary>
        public static float SampleGlow(float u, float v)
        {
            if (_glowAlpha == null)
                return 0;
            int n = _glowSize;
            float x = Math.Clamp(u * n - 0.5f, 0, n - 1),
                y = Math.Clamp(v * n - 0.5f, 0, n - 1);
            int x0 = (int)x,
                y0 = (int)y,
                x1 = Math.Min(x0 + 1, n - 1),
                y1 = Math.Min(y0 + 1, n - 1);
            float fx = x - x0,
                fy = y - y0;
            float top =
                _glowAlpha[y0 * n + x0] + (_glowAlpha[y0 * n + x1] - _glowAlpha[y0 * n + x0]) * fx;
            float bottom =
                _glowAlpha[y1 * n + x0] + (_glowAlpha[y1 * n + x1] - _glowAlpha[y1 * n + x0]) * fx;
            return (top + (bottom - top) * fy) / 255f;
        }

        static Image<Rgba32> Read(string name)
        {
            using var stream =
                typeof(SideOrderAssets).Assembly.GetManifestResourceStream(
                    $"PlayerViewer.UI.SideOrder.Resources.{name}.png"
                ) ?? throw new InvalidOperationException($"missing embedded texture {name}");
            return Image.Load<Rgba32>(stream);
        }

        //A copy of an image's pixels for sampling on the CPU, and its width.
        static (Rgba32[] Pixels, int Width) KeepPixels(Image<Rgba32> image)
        {
            var pixels = new Rgba32[image.Width * image.Height];
            image.CopyPixelDataTo(pixels);
            return (pixels, image.Width);
        }

        //Repeating textures get mipmaps; a mask is uploaded white with its alpha.
        static int Upload(Image<Rgba32> image, bool mask, bool repeat)
        {
            using (image)
            {
                var pixels = new Rgba32[image.Width * image.Height];
                image.CopyPixelDataTo(pixels);
                if (mask)
                    for (int i = 0; i < pixels.Length; i++)
                        pixels[i] = new Rgba32(255, 255, 255, pixels[i].A);
                return GlTextures.UploadRgba(
                    pixels,
                    image.Width,
                    image.Height,
                    mips: repeat,
                    repeat ? TextureWrapMode.Repeat : TextureWrapMode.ClampToEdge
                );
            }
        }
    }
}
