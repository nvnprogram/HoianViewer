using System;
using System.Numerics;
using BCnEncoder.Decoder;
using BCnEncoder.Shared;
using PlayerViewer.Core;
using PlayerViewer.Textures;
using Toolbox.Core;

namespace PlayerViewer.Env
{
    /// <summary>
    /// A scene's resource env map: a cube with its mip chain, deswizzled, and the ambient SH
    /// projected from it.
    /// </summary>
    public sealed class EnvMapCube
    {
        public int Size;
        public TexFormat Format;

        /// <summary>Per mip, the six faces one after another, each tightly packed.</summary>
        public byte[][] Mips;

        //Mip the SH is projected from; 32 texels a side for the stock 256 cubes.
        const int ShMip = 3;

        //The 8 bit env maps hold HDR as colour times alpha to this power times this range, the
        //decode the engine's cube map shaders use with the context's cube map HDR row.
        const float HdrPower = 4;
        const float HdrRange = 1024;

        /// <summary>
        /// The cube at a romfs path, or null when it is missing or not a cube this reads. 8 bit and
        /// BC3 cubes decode to RGBA16F.
        /// </summary>
        public static EnvMapCube Load(Romfs romfs, string path)
        {
            var bytes = path != null ? romfs.ReadFile(path) : null;
            if (bytes == null)
                return null;
            var switchTex = ExternalTextures.Load(bytes);
            if (switchTex == null)
                return null;
            var tex = new BfresEditor.BntxTexture(switchTex.BntxFile, switchTex.Texture);
            var format = tex.Platform.OutputFormat;
            if (
                tex.ArrayCount != 6
                || tex.Width != tex.Height
                || (
                    format != TexFormat.RG11B10_FLOAT
                    && format != TexFormat.RGBA16_FLOAT
                    && format != TexFormat.RGBA8_UNORM
                    && format != TexFormat.BC3_UNORM
                )
            )
                return null;

            bool encoded = format == TexFormat.RGBA8_UNORM || format == TexFormat.BC3_UNORM;
            var cube = new EnvMapCube
            {
                Size = (int)tex.Width,
                Format = encoded ? TexFormat.RGBA16_FLOAT : format,
                Mips = new byte[tex.MipCount][],
            };
            int bpp = encoded ? 8 : (int)tex.GetBytesPerPixel();
            for (int mip = 0; mip < cube.Mips.Length; mip++)
            {
                int s = Math.Max(1, cube.Size >> mip);
                int faceSize = s * s * bpp;
                var data = new byte[faceSize * 6];
                for (int face = 0; face < 6; face++)
                {
                    var surface = tex.GetMipSurface(face, mip);
                    if (encoded && surface != null)
                        surface = DecodeHdr(surface, format, s);
                    if (surface == null || surface.Length < faceSize)
                        return mip > 0 ? cube.Truncate(mip) : null;
                    Buffer.BlockCopy(surface, 0, data, face * faceSize, faceSize);
                }
                cube.Mips[mip] = data;
            }
            return cube;
        }

        //An 8 bit face, BGRA after the upload swap or BC3 blocks, as RGBA16F radiance.
        static byte[] DecodeHdr(byte[] surface, TexFormat format, int size)
        {
            int count = size * size;
            var rgba = new byte[count * 4];
            if (format == TexFormat.BC3_UNORM)
            {
                int aligned = (size + 3) / 4 * 4;
                if (surface.Length < aligned * aligned)
                    return null;
                var pixels = new BcDecoder().DecodeRaw(
                    surface,
                    aligned,
                    aligned,
                    CompressionFormat.Bc3
                );
                for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    var p = pixels[y * aligned + x];
                    int o = (y * size + x) * 4;
                    (rgba[o], rgba[o + 1], rgba[o + 2], rgba[o + 3]) = (p.r, p.g, p.b, p.a);
                }
            }
            else
            {
                if (surface.Length < count * 4)
                    return null;
                for (int i = 0; i < count; i++)
                {
                    int o = i * 4;
                    (rgba[o], rgba[o + 1], rgba[o + 2], rgba[o + 3]) = (
                        surface[o + 2],
                        surface[o + 1],
                        surface[o],
                        surface[o + 3]
                    );
                }
            }
            var result = new byte[count * 8];
            for (int i = 0; i < count; i++)
            {
                int o = i * 4;
                float scale = MathF.Pow(rgba[o + 3] / 255f, HdrPower) * HdrRange / 255f;
                var span = result.AsSpan(i * 8, 8);
                WriteHalf(span, 0, rgba[o] * scale);
                WriteHalf(span, 1, rgba[o + 1] * scale);
                WriteHalf(span, 2, rgba[o + 2] * scale);
                WriteHalf(span, 3, 1);
            }
            return result;
        }

        static void WriteHalf(Span<byte> texel, int channel, float value) =>
            BitConverter.TryWriteBytes(texel.Slice(channel * 2, 2), (System.Half)value);

        /// <summary>A cube whose every texel is what the packed SH rows give in its direction.</summary>
        public static EnvMapCube FromSH(Vector4[] rows, int size = 16)
        {
            int mips = (int)Math.Log2(size) + 1;
            var cube = new EnvMapCube
            {
                Size = size,
                Format = TexFormat.RGBA16_FLOAT,
                Mips = new byte[mips][],
            };
            for (int mip = 0; mip < mips; mip++)
            {
                int s = Math.Max(1, size >> mip);
                var data = new byte[s * s * 8 * 6];
                for (int face = 0; face < 6; face++)
                for (int y = 0; y < s; y++)
                for (int x = 0; x < s; x++)
                {
                    float u = 2 * (x + 0.5f) / s - 1;
                    float v = 2 * (y + 0.5f) / s - 1;
                    var c = Vector3.Max(
                        Vector3.Zero,
                        SkyAmbient.Evaluate(rows, Vector3.Normalize(Direction(face, u, v)))
                    );
                    var span = data.AsSpan(((face * s + y) * s + x) * 8, 8);
                    WriteHalf(span, 0, c.X);
                    WriteHalf(span, 1, c.Y);
                    WriteHalf(span, 2, c.Z);
                    WriteHalf(span, 3, 1);
                }
                cube.Mips[mip] = data;
            }
            return cube;
        }

        EnvMapCube Truncate(int mips)
        {
            Array.Resize(ref Mips, mips);
            return this;
        }

        /// <summary>Linear radiance of one face of a mip, row by row.</summary>
        public Vector3[] Face(int mip, int face)
        {
            int s = Math.Max(1, Size >> mip);
            int count = s * s;
            int bpp = Mips[mip].Length / (6 * count);
            var faceData = new byte[count * bpp];
            Buffer.BlockCopy(Mips[mip], face * faceData.Length, faceData, 0, faceData.Length);
            var texels = new Vector3[count];
            var wide = FloatTexels.Decode(faceData, Format, count);
            for (int i = 0; i < count; i++)
                texels[i] = new Vector3(wide[i].X, wide[i].Y, wide[i].Z);
            return texels;
        }

        /// <summary>
        /// The seven packed ambient rows: the cube's radiance projected to order 2 SH, in the
        /// irradiance over pi form the shaders evaluate.
        /// </summary>
        public Vector4[] AmbientSH()
        {
            int mip = Math.Min(ShMip, Mips.Length - 1);
            int s = Math.Max(1, Size >> mip);
            var sh = new Vector3[9];
            for (int face = 0; face < 6; face++)
            {
                var texels = Face(mip, face);
                for (int y = 0; y < s; y++)
                for (int x = 0; x < s; x++)
                {
                    float u = 2 * (x + 0.5f) / s - 1;
                    float v = 2 * (y + 0.5f) / s - 1;
                    var dir = Direction(face, u, v);
                    float lengthSq = 1 + u * u + v * v;
                    float solidAngle = 4f / (s * s) / (lengthSq * MathF.Sqrt(lengthSq));
                    var radiance = Vector3.Max(Vector3.Zero, texels[y * s + x]);
                    if (float.IsFinite(radiance.X + radiance.Y + radiance.Z))
                        SkyAmbient.Accumulate(sh, Vector3.Normalize(dir), radiance * solidAngle);
                }
            }
            return SkyAmbient.Pack(sh);
        }

        //The direction through a texel of a face, in the GL cube map convention the console
        //shares: +X, -X, +Y, -Y, +Z, -Z, with u to the right and v down each face.
        internal static Vector3 Direction(int face, float u, float v) =>
            face switch
            {
                0 => new Vector3(1, -v, -u),
                1 => new Vector3(-1, -v, u),
                2 => new Vector3(u, 1, v),
                3 => new Vector3(u, -1, -v),
                4 => new Vector3(u, -v, 1),
                _ => new Vector3(-u, -v, -1),
            };
    }
}
