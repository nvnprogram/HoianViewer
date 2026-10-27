using BCnEncoder.Decoder;
using BCnEncoder.Shared;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Toolbox.Core;

namespace PlayerViewer.Textures
{
    /// <summary>
    /// The formats Toolbox has no decoder for: BC6 and BC7, and the float formats R11G11B10 and
    /// RGBA16F. The HDR ones are clamped to the 8 bit range an export can hold.
    /// </summary>
    public static class BlockDecoder
    {
        /// <summary>Mip 0 as an image, or null when the ordinary decoder handles the format.</summary>
        public static Image<Rgba32> Decode(STGenericTexture texture)
        {
            var format = texture.Platform.OutputFormat;
            if (format == TexFormat.RG11B10_FLOAT || format == TexFormat.RGBA16_FLOAT)
                return DecodeFloat(texture);
            CompressionFormat compression;
            bool hdr = false;
            switch (format)
            {
                case TexFormat.BC6H_UF16:
                    compression = CompressionFormat.Bc6U;
                    hdr = true;
                    break;
                case TexFormat.BC6H_SF16:
                    compression = CompressionFormat.Bc6S;
                    hdr = true;
                    break;
                case TexFormat.BC7_UNORM:
                case TexFormat.BC7_SRGB:
                    compression = CompressionFormat.Bc7;
                    break;
                default:
                    return null;
            }

            int width = (int)texture.Width;
            int height = (int)texture.Height;
            int alignedWidth = (width + 3) / 4 * 4;
            int alignedHeight = (height + 3) / 4 * 4;

            //Deswizzled but still compressed, which is what the block decoder wants.
            byte[] blocks = texture.GetDeswizzledSurface();
            var decoder = new BcDecoder();
            var image = new Image<Rgba32>(alignedWidth, alignedHeight);

            if (hdr)
            {
                var pixels = decoder.DecodeRawHdr(blocks, alignedWidth, alignedHeight, compression);
                image.ProcessPixelRows(rows =>
                {
                    for (int y = 0; y < rows.Height; y++)
                    {
                        var row = rows.GetRowSpan(y);
                        for (int x = 0; x < row.Length; x++)
                        {
                            var p = pixels[y * alignedWidth + x];
                            row[x] = new Rgba32(Unit(p.r), Unit(p.g), Unit(p.b), 255);
                        }
                    }
                });
            }
            else
            {
                var pixels = decoder.DecodeRaw(blocks, alignedWidth, alignedHeight, compression);
                image.ProcessPixelRows(rows =>
                {
                    for (int y = 0; y < rows.Height; y++)
                    {
                        var row = rows.GetRowSpan(y);
                        for (int x = 0; x < row.Length; x++)
                        {
                            var p = pixels[y * alignedWidth + x];
                            row[x] = new Rgba32(p.r, p.g, p.b, p.a);
                        }
                    }
                });
            }

            if (alignedWidth != width || alignedHeight != height)
                image.Mutate(x => x.Crop(new Rectangle(0, 0, width, height)));
            return image;
        }

        static Image<Rgba32> DecodeFloat(STGenericTexture texture)
        {
            int width = (int)texture.Width;
            int height = (int)texture.Height;
            var texels = FloatTexels.Decode(
                texture.GetDeswizzledSurface(),
                texture.Platform.OutputFormat,
                width * height
            );
            if (texels == null)
                return null;
            var image = new Image<Rgba32>(width, height);
            image.ProcessPixelRows(rows =>
            {
                for (int y = 0; y < rows.Height; y++)
                {
                    var row = rows.GetRowSpan(y);
                    for (int x = 0; x < row.Length; x++)
                    {
                        var p = texels[y * width + x];
                        row[x] = new Rgba32(Unit(p.X), Unit(p.Y), Unit(p.Z), Unit(p.W));
                    }
                }
            });
            return image;
        }

        static byte Unit(float value) =>
            (byte)System.Math.Clamp(System.MathF.Round(value * 255f), 0f, 255f);
    }
}
