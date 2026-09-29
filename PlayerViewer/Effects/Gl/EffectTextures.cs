using System;
using System.Collections.Generic;
using EffectLibrary;
using OpenTK.Graphics.OpenGL;
using Syroot.NintenTools.NSW.Bntx;
using Syroot.NintenTools.NSW.Bntx.GFX;
using Toolbox.Core.Switch;

namespace PlayerViewer.Effects.Gl
{
    /// <summary>A GL texture and the target it binds to.</summary>
    public readonly record struct GlTexture(int Id, TextureTarget Target);

    /// <summary>
    /// The textures of one effect file on the GPU, uploaded on first use with every mip as stored
    /// (block compressed data stays compressed) and the BNTX channel swizzle applied, and the
    /// sampler objects the emitters' sampler slots describe.
    /// </summary>
    public sealed class EffectTextures : IDisposable
    {
        readonly TextureArchive _archive;
        readonly Dictionary<ulong, GlTexture> _textures = new();
        readonly Dictionary<(byte, byte, byte, float), int> _samplers = new();

        public EffectTextures(TextureArchive archive) => _archive = archive;

        /// <summary>The texture an emitter slot names, or null when the slot is empty or the
        /// texture cannot be uploaded.</summary>
        public GlTexture? Get(Emitter emitter, int slot)
        {
            var entry = emitter.ResolveTexture(slot);
            if (entry == null)
                return null;
            if (_textures.TryGetValue(entry.Id, out var cached))
                return cached.Id == 0 ? null : cached;
            var texture = _archive?.GetTexture(entry);
            var gl = texture == null ? default : Upload(texture);
            _textures[entry.Id] = gl;
            return gl.Id == 0 ? null : gl;
        }

        /// <summary>A sampler object for an emitter slot's wrap and filter settings. Wrap 0 is
        /// mirror, 1 repeat, 2 clamp to edge; filter 0 is trilinear, 1 point. The captured draws
        /// bind no LOD bias whatever the slot says, so none is applied.</summary>
        public int Sampler(TextureSampler s)
        {
            var key = (s.WrapU, s.WrapV, s.Filter, s.MaxLod);
            if (_samplers.TryGetValue(key, out int id))
                return id;
            id = GL.GenSampler();
            GL.SamplerParameter(id, SamplerParameterName.TextureWrapS, (int)Wrap(s.WrapU));
            GL.SamplerParameter(id, SamplerParameterName.TextureWrapT, (int)Wrap(s.WrapV));
            bool point = s.Filter == 1;
            GL.SamplerParameter(
                id,
                SamplerParameterName.TextureMinFilter,
                (int)(
                    point
                        ? TextureMinFilter.NearestMipmapNearest
                        : TextureMinFilter.LinearMipmapLinear
                )
            );
            GL.SamplerParameter(
                id,
                SamplerParameterName.TextureMagFilter,
                (int)(point ? TextureMagFilter.Nearest : TextureMagFilter.Linear)
            );
            GL.SamplerParameter(id, SamplerParameterName.TextureMinLod, 0f);
            GL.SamplerParameter(id, SamplerParameterName.TextureMaxLod, s.MaxLod);
            _samplers[key] = id;
            return id;
        }

        static TextureWrapMode Wrap(byte mode) =>
            mode switch
            {
                0 => TextureWrapMode.MirroredRepeat,
                2 => TextureWrapMode.ClampToEdge,
                _ => TextureWrapMode.Repeat,
            };

        GlTexture Upload(Texture t)
        {
            string name = TextureArchive.FormatName(_archive.GetFileFormat(t));
            if (!Formats.TryGetValue(name, out var f))
                return default;
            bool array = t.ArrayLength > 1 || t.SurfaceDim == SurfaceDim.Dim2DArray;
            var target = array ? TextureTarget.Texture2DArray : TextureTarget.Texture2D;
            int layers = Math.Max(1, (int)t.ArrayLength);
            int mips = Math.Max(1, (int)t.MipCount);

            int id = GL.GenTexture();
            GL.BindTexture(target, id);
            //Surfaces are tightly packed; a narrow R8 or RG8 row is not a multiple of four bytes.
            GL.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
            try
            {
                for (int mip = 0; mip < mips; mip++)
                {
                    int w = Math.Max(1, (int)t.Width >> mip);
                    int h = Math.Max(1, (int)t.Height >> mip);
                    byte[] data = new byte[SurfaceSize(f, w, h) * layers];
                    int size = SurfaceSize(f, w, h);
                    for (int layer = 0; layer < layers; layer++)
                    {
                        var surface = Deswizzle(t, f, layer, mip);
                        if (surface == null)
                        {
                            GL.DeleteTexture(id);
                            return default;
                        }
                        System.Buffer.BlockCopy(
                            surface,
                            0,
                            data,
                            layer * size,
                            Math.Min(size, surface.Length)
                        );
                    }
                    if (f.Compressed != 0 && array)
                        GL.CompressedTexImage3D(
                            target,
                            mip,
                            (InternalFormat)f.Compressed,
                            w,
                            h,
                            layers,
                            0,
                            data.Length,
                            data
                        );
                    else if (f.Compressed != 0)
                        GL.CompressedTexImage2D(
                            target,
                            mip,
                            (InternalFormat)f.Compressed,
                            w,
                            h,
                            0,
                            data.Length,
                            data
                        );
                    else if (array)
                        GL.TexImage3D(
                            target,
                            mip,
                            f.Internal,
                            w,
                            h,
                            layers,
                            0,
                            f.Pixel,
                            f.Type,
                            data
                        );
                    else
                        GL.TexImage2D(target, mip, f.Internal, w, h, 0, f.Pixel, f.Type, data);
                }
            }
            finally
            {
                GL.PixelStore(PixelStoreParameter.UnpackAlignment, 4);
            }
            GL.TexParameter(target, TextureParameterName.TextureBaseLevel, 0);
            GL.TexParameter(target, TextureParameterName.TextureMaxLevel, mips - 1);
            GL.TexParameter(target, TextureParameterName.TextureSwizzleR, Swizzle(t.ChannelRed));
            GL.TexParameter(target, TextureParameterName.TextureSwizzleG, Swizzle(t.ChannelGreen));
            GL.TexParameter(target, TextureParameterName.TextureSwizzleB, Swizzle(t.ChannelBlue));
            GL.TexParameter(target, TextureParameterName.TextureSwizzleA, Swizzle(t.ChannelAlpha));
            GL.BindTexture(target, 0);
            return new GlTexture(id, target);
        }

        static int Swizzle(ChannelType c) =>
            (int)(
                c switch
                {
                    ChannelType.Zero => All.Zero,
                    ChannelType.One => All.One,
                    ChannelType.Red => All.Red,
                    ChannelType.Green => All.Green,
                    ChannelType.Blue => All.Blue,
                    _ => All.Alpha,
                }
            );

        /// <summary>One mip of one layer in linear order, with the per mip block height rule of
        /// <c>BntxTexture.GetMipSurface</c>. Written out here because that wrapper cannot be built
        /// for the half float vertex animation tables.</summary>
        static byte[] Deswizzle(Texture t, GlFormat f, int layer, int mip)
        {
            if (layer >= t.TextureData.Count || mip >= t.TextureData[layer].Count)
                return null;
            uint block = f.Compressed != 0 ? 4u : 1u;
            uint w = Math.Max(1u, t.Width >> mip);
            uint h = Math.Max(1u, t.Height >> mip);
            uint heightInBlocks = (h + block - 1) / block;
            uint blockHeight = 1u << (int)t.BlockHeightLog2;
            while (heightInBlocks <= blockHeight / 2 * 8 && blockHeight > 1)
                blockHeight /= 2;
            var linear = TegraX1Swizzle.deswizzle(
                w,
                h,
                1,
                block,
                block,
                1,
                1,
                (uint)f.Bpp,
                0,
                (int)Math.Log2(blockHeight),
                t.TextureData[layer][mip]
            );
            return linear.ToArray();
        }

        static int SurfaceSize(GlFormat f, int w, int h) =>
            f.Compressed != 0 ? (w + 3) / 4 * ((h + 3) / 4) * f.Bpp : w * h * f.Bpp;

        /// <summary>A format: its compressed internal format (0 when uncompressed) and bytes per
        /// block, or its uncompressed triple and bytes per texel.</summary>
        readonly record struct GlFormat(
            int Compressed,
            int Bpp,
            PixelInternalFormat Internal = 0,
            PixelFormat Pixel = 0,
            PixelType Type = 0
        );

        static readonly Dictionary<string, GlFormat> Formats = new()
        {
            ["BC1_UNORM"] = new((int)InternalFormat.CompressedRgbaS3tcDxt1Ext, 8),
            ["BC1_SRGB"] = new((int)InternalFormat.CompressedSrgbAlphaS3tcDxt1Ext, 8),
            ["BC2_UNORM"] = new((int)InternalFormat.CompressedRgbaS3tcDxt3Ext, 16),
            ["BC2_SRGB"] = new((int)InternalFormat.CompressedSrgbAlphaS3tcDxt3Ext, 16),
            ["BC3_UNORM"] = new((int)InternalFormat.CompressedRgbaS3tcDxt5Ext, 16),
            ["BC3_SRGB"] = new((int)InternalFormat.CompressedSrgbAlphaS3tcDxt5Ext, 16),
            ["BC4_UNORM"] = new((int)InternalFormat.CompressedRedRgtc1, 8),
            ["BC4_SNORM"] = new((int)InternalFormat.CompressedSignedRedRgtc1, 8),
            ["BC5_UNORM"] = new((int)InternalFormat.CompressedRgRgtc2, 16),
            ["BC5_SNORM"] = new((int)InternalFormat.CompressedSignedRgRgtc2, 16),
            ["BC6H_UF16"] = new((int)InternalFormat.CompressedRgbBptcUnsignedFloat, 16),
            ["BC6H_SF16"] = new((int)InternalFormat.CompressedRgbBptcSignedFloat, 16),
            ["BC7_UNORM"] = new((int)InternalFormat.CompressedRgbaBptcUnorm, 16),
            ["BC7_SRGB"] = new((int)InternalFormat.CompressedSrgbAlphaBptcUnorm, 16),
            ["R8_UNORM"] = new(
                0,
                1,
                PixelInternalFormat.R8,
                PixelFormat.Red,
                PixelType.UnsignedByte
            ),
            ["R8_G8_UNORM"] = new(
                0,
                2,
                PixelInternalFormat.Rg8,
                PixelFormat.Rg,
                PixelType.UnsignedByte
            ),
            ["R8_G8_B8_A8_UNORM"] = new(
                0,
                4,
                PixelInternalFormat.Rgba8,
                PixelFormat.Rgba,
                PixelType.UnsignedByte
            ),
            ["R8_G8_B8_A8_SRGB"] = new(
                0,
                4,
                PixelInternalFormat.Srgb8Alpha8,
                PixelFormat.Rgba,
                PixelType.UnsignedByte
            ),
            ["R16_UINT"] = new(
                0,
                2,
                PixelInternalFormat.R16ui,
                PixelFormat.RedInteger,
                PixelType.UnsignedShort
            ),
            ["R16_G16_B16_A16_FLOAT"] = new(
                0,
                8,
                PixelInternalFormat.Rgba16f,
                PixelFormat.Rgba,
                PixelType.HalfFloat
            ),
        };

        public void Dispose()
        {
            foreach (var t in _textures.Values)
                if (t.Id != 0)
                    GL.DeleteTexture(t.Id);
            foreach (int s in _samplers.Values)
                GL.DeleteSampler(s);
            _textures.Clear();
            _samplers.Clear();
        }
    }
}
