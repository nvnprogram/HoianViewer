using System;
using System.Numerics;
using PlayerViewer.Textures;
using SixLabors.ImageSharp.PixelFormats;
using BntxFile = Syroot.NintenTools.NSW.Bntx.BntxFile;
using ChannelType = Syroot.NintenTools.NSW.Bntx.GFX.ChannelType;
using ResTexture = Syroot.NintenTools.NSW.Bntx.Texture;

namespace PlayerViewer.Icons
{
    /// <summary>
    /// A float RGBA image in linear space, straight alpha: what a decoded texture becomes
    /// before it is composited, and what a composite is built in.
    /// </summary>
    public sealed class Surface
    {
        public readonly int Width;
        public readonly int Height;
        public readonly Vector4[] Pixels;

        public Surface(int width, int height)
        {
            Width = width;
            Height = height;
            Pixels = new Vector4[width * height];
        }

        /// <summary>
        /// Mip 0 of a texture with its channel selectors applied, the way a sampler sees it:
        /// an sRGB format comes back linear, and a single channel mask comes back as whatever
        /// its selectors route that channel to.
        /// </summary>
        public static Surface Decode(BntxFile bntx, ResTexture texture)
        {
            var wrapped = new BfresEditor.BntxTexture(bntx, texture);
            using var image = TextureStore.Decode(wrapped);
            bool srgb = texture.Format.ToString().Contains("SRGB");
            var surface = new Surface(image.Width, image.Height);
            var selectors = new[]
            {
                texture.ChannelRed,
                texture.ChannelGreen,
                texture.ChannelBlue,
                texture.ChannelAlpha,
            };
            image.ProcessPixelRows(rows =>
            {
                for (int y = 0; y < rows.Height; y++)
                {
                    var row = rows.GetRowSpan(y);
                    for (int x = 0; x < row.Length; x++)
                    {
                        var p = row[x];
                        var raw = new Vector4(p.R, p.G, p.B, p.A) / 255f;
                        if (srgb)
                            raw = new Vector4(
                                ToLinear(raw.X),
                                ToLinear(raw.Y),
                                ToLinear(raw.Z),
                                raw.W
                            );
                        surface.Pixels[y * surface.Width + x] = new Vector4(
                            Select(selectors[0], raw),
                            Select(selectors[1], raw),
                            Select(selectors[2], raw),
                            Select(selectors[3], raw)
                        );
                    }
                }
            });
            return surface;
        }

        /// <summary>
        /// A texture straight to display values, for one used as it is. A plain colour
        /// texture skips the linear round trip.
        /// </summary>
        public static IconImage DecodeImage(BntxFile bntx, ResTexture texture)
        {
            bool identity =
                texture.ChannelRed == ChannelType.Red
                && texture.ChannelGreen == ChannelType.Green
                && texture.ChannelBlue == ChannelType.Blue
                && texture.ChannelAlpha == ChannelType.Alpha;
            if (!identity)
                return Decode(bntx, texture).ToImage();
            using var image = TextureStore.Decode(new BfresEditor.BntxTexture(bntx, texture));
            var rgba = new byte[image.Width * image.Height * 4];
            image.CopyPixelDataTo(rgba);
            return new IconImage
            {
                Width = image.Width,
                Height = image.Height,
                Rgba = rgba,
            };
        }

        static float Select(ChannelType channel, Vector4 raw) =>
            channel switch
            {
                ChannelType.Zero => 0,
                ChannelType.One => 1,
                ChannelType.Red => raw.X,
                ChannelType.Green => raw.Y,
                ChannelType.Blue => raw.Z,
                _ => raw.W,
            };

        public static float ToLinear(float c) =>
            c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);

        public static float ToSrgb(float c) =>
            c <= 0.0031308f ? c * 12.92f : 1.055f * MathF.Pow(c, 1 / 2.4f) - 0.055f;

        /// <summary>Nearest sample at a texture coordinate, clamped to the edge.</summary>
        public Vector4 Sample(float u, float v)
        {
            int x = Math.Clamp((int)(u * Width), 0, Width - 1);
            int y = Math.Clamp((int)(v * Height), 0, Height - 1);
            return Pixels[y * Width + x];
        }

        /// <summary>Straight alpha source over this surface.</summary>
        public void Over(int index, Vector3 color, float alpha)
        {
            if (alpha <= 0)
                return;
            var dst = Pixels[index];
            float outA = alpha + dst.W * (1 - alpha);
            var rgb =
                (color * alpha + new Vector3(dst.X, dst.Y, dst.Z) * dst.W * (1 - alpha)) / outA;
            Pixels[index] = new Vector4(rgb, outA);
        }

        /// <summary>
        /// The surface cropped to what is visible in it, plus a margin. Layout parts are drawn
        /// on canvases with a lot of empty space around them, which a small icon slot would
        /// otherwise spend on nothing.
        /// </summary>
        public Surface Trimmed(int margin)
        {
            int x0 = Width,
                y0 = Height,
                x1 = -1,
                y1 = -1;
            for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
                if (Pixels[y * Width + x].W > 1 / 255f)
                {
                    x0 = Math.Min(x0, x);
                    y0 = Math.Min(y0, y);
                    x1 = Math.Max(x1, x);
                    y1 = Math.Max(y1, y);
                }
            if (x1 < 0)
                return this;
            x0 = Math.Max(0, x0 - margin);
            y0 = Math.Max(0, y0 - margin);
            x1 = Math.Min(Width - 1, x1 + margin);
            y1 = Math.Min(Height - 1, y1 + margin);
            var trimmed = new Surface(x1 - x0 + 1, y1 - y0 + 1);
            for (int y = 0; y < trimmed.Height; y++)
                Array.Copy(
                    Pixels,
                    (y0 + y) * Width + x0,
                    trimmed.Pixels,
                    y * trimmed.Width,
                    trimmed.Width
                );
            return trimmed;
        }

        public IconImage ToImage()
        {
            var rgba = new byte[Width * Height * 4];
            for (int i = 0; i < Pixels.Length; i++)
            {
                var p = Pixels[i];
                rgba[i * 4] = ToByte(ToSrgb(p.X));
                rgba[i * 4 + 1] = ToByte(ToSrgb(p.Y));
                rgba[i * 4 + 2] = ToByte(ToSrgb(p.Z));
                rgba[i * 4 + 3] = ToByte(p.W);
            }
            return new IconImage
            {
                Width = Width,
                Height = Height,
                Rgba = rgba,
            };
        }

        static byte ToByte(float v) => (byte)Math.Clamp(MathF.Round(v * 255), 0, 255);
    }
}
