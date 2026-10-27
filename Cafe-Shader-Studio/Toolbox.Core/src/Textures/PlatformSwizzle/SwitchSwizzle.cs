using System;
using System.Collections.Generic;
using System.Linq;
using Toolbox.Core.Switch;

namespace Toolbox.Core.Imaging
{
    public class SwitchSwizzle : IPlatformSwizzle
    {
        public TexFormat OutputFormat { get; set; } = TexFormat.RGBA8_UNORM;

        //Required settings
        public uint BlockHeightLog2;
        public uint Alignment;
        public uint TileMode;
        public int Target = 1; //Platform PC or NX
        public bool XbarGob;

        //Adjusted on encode
        public uint ReadTextureLayout;
        public uint ImageSize;
        public uint[] MipOffsets;

        //Quick check for linear tiling
        public bool LinearMode => TileMode == 1;

        public SwitchSwizzle(TexFormat format) {
            OutputFormat = format;
        }

        public override string ToString() {
            return OutputFormat.ToString();
        }

        public byte[] DecodeImage(STGenericTexture texture, byte[] data, uint width, uint height, int array, int mip) {

            if (data.Length == 0)
                throw new Exception("Data is empty! Failed to swizzle image!");

            if (BlockHeightLog2 == 0)
            {
                uint blkHeight = TextureFormatHelper.GetBlockHeight(OutputFormat);
                uint blockHeight = TegraX1Swizzle.GetBlockHeight(TegraX1Swizzle.DIV_ROUND_UP(texture.Height, blkHeight));
                BlockHeightLog2 = (uint)Convert.ToString(blockHeight, 2).Length ;

                if (OutputFormat != TexFormat.ASTC_8x5_UNORM)
                    BlockHeightLog2 -= 1;
            }

            return TegraX1Swizzle.GetImageData(texture, data, array, mip, 0, BlockHeightLog2, Target, LinearMode, XbarGob);
        }

        public byte[] EncodeImage(STGenericTexture texture, byte[] data, uint width, uint height, int array, int mip) {
            uint imageOffset = 0;
            List<byte[]> mipmaps = SwizzleSurfaceMipMaps(data, texture.Width, texture.Height, texture.Depth, texture.MipCount, ref imageOffset);
            //Combine mip map data
            return ByteUtils.CombineArray(mipmaps.ToArray());
        }

        public uint[] GenerateMipOffsets(STGenericTexture tex, uint imageSize)
        {
            return TegraX1Swizzle.GenerateMipSizes(OutputFormat,
                 tex.Width, tex.Height, tex.Depth, tex.ArrayCount, tex.MipCount, imageSize)[0]; 
        }

        public List<byte[]> SwizzleSurfaceMipMaps(byte[] data, uint width, uint height, uint depth, uint MipCount, ref uint imageOffset)
        {
            return SwizzleSurfaceMipMaps(data, width, height, depth, MipCount, ref imageOffset, 1);
        }

        public List<byte[]> SwizzleSurfaceMipMaps(byte[] data, uint width, uint height, uint depth, uint MipCount, ref uint imageOffset, uint arrayCount, int forceBlockHeightLog2 = -1)
        {
            uint blkWidth = TextureFormatHelper.GetBlockWidth(this.OutputFormat);
            uint blkHeight = TextureFormatHelper.GetBlockHeight(this.OutputFormat);
            uint blkDepth = TextureFormatHelper.GetBlockDepth(this.OutputFormat);
            uint bpp = TextureFormatHelper.GetBytesPerPixel(this.OutputFormat);

            if (arrayCount == 0)
                arrayCount = 1;

            if (LinearMode)
            {
                BlockHeightLog2 = 0;
                Alignment = 1;
                ReadTextureLayout = 0;
            }
            else
            {
                BlockHeightLog2 = forceBlockHeightLog2 >= 0
                    ? (uint)forceBlockHeightLog2
                    : TegraX1Swizzle.GetBlockHeightLog2(TegraX1Swizzle.DIV_ROUND_UP(height, blkHeight));
                Alignment = 512;
                ReadTextureLayout = 1;
            }

            uint linearLayerSize = 0;
            for (int mipLevel = 0; mipLevel < MipCount; mipLevel++)
            {
                uint w = Math.Max(1, width >> mipLevel);
                uint h = Math.Max(1, height >> mipLevel);
                linearLayerSize += TegraX1Swizzle.DIV_ROUND_UP(w, blkWidth)
                    * TegraX1Swizzle.DIV_ROUND_UP(h, blkHeight) * bpp;
            }

            uint baseRows = TegraX1Swizzle.DIV_ROUND_UP(height, blkHeight);
            uint layerAlignment = LinearMode
                ? 1
                : 512u << (int)TegraX1Swizzle.ShrinkBlockHeightLog2(BlockHeightLog2, baseRows);

            MipOffsets = new uint[MipCount];
            List<byte[]> mipmaps = new List<byte[]>();
            uint layerSize = 0;

            for (int arrayLevel = 0; arrayLevel < arrayCount; arrayLevel++)
            {
                uint SurfaceSize = 0;
                for (int mipLevel = 0; mipLevel < MipCount; mipLevel++)
                {
                    var result = WiiU.TextureHelper.GetCurrentMipSize(width, height, blkWidth, blkHeight, bpp, mipLevel);
                    uint offset = result.Item1 + (uint)arrayLevel * linearLayerSize;
                    uint size = result.Item2;
                    byte[] data_ = ByteUtils.SubArray(data, offset, size);

                    uint width_ = Math.Max(1, width >> mipLevel);
                    uint height_ = Math.Max(1, height >> mipLevel);
                    uint depth_ = Math.Max(1, depth >> mipLevel);

                    uint width__ = TegraX1Swizzle.DIV_ROUND_UP(width_, blkWidth);
                    uint height__ = TegraX1Swizzle.DIV_ROUND_UP(height_, blkHeight);

                    byte[] AlignedData = new byte[TegraX1Swizzle.round_up(SurfaceSize, Alignment) - SurfaceSize];
                    SurfaceSize += (uint)AlignedData.Length;
                    if (arrayLevel == 0)
                        MipOffsets[mipLevel] = SurfaceSize;

                    uint mipBlockHeightLog2 = LinearMode
                        ? 0
                        : TegraX1Swizzle.ShrinkBlockHeightLog2(BlockHeightLog2, height__);

                    uint Pitch;
                    if (LinearMode)
                    {
                        Pitch = width__ * bpp;
                        if (Target == 1)
                            Pitch = TegraX1Swizzle.round_up(Pitch, 32);

                        SurfaceSize += Pitch * height__;
                    }
                    else
                    {
                        Pitch = TegraX1Swizzle.round_up(width__ * bpp, 64);
                        SurfaceSize += Pitch * TegraX1Swizzle.round_up(height__, (1u << (int)mipBlockHeightLog2) * 8);
                    }

                    Span<byte> SwizzledData = TegraX1Swizzle.swizzle(width_, height_, depth_, blkWidth, blkHeight, blkDepth, Target, bpp, (uint)TileMode, (int)mipBlockHeightLog2, data_, XbarGob);
                    mipmaps.Add(AlignedData.Concat(SwizzledData.ToArray()).ToArray());
                }

                if (arrayCount > 1)
                {
                    uint padded = TegraX1Swizzle.round_up(SurfaceSize, layerAlignment);
                    if (padded != SurfaceSize)
                    {
                        int last = mipmaps.Count - 1;
                        mipmaps[last] = mipmaps[last]
                            .Concat(new byte[padded - SurfaceSize])
                            .ToArray();
                        SurfaceSize = padded;
                    }
                }
                layerSize = SurfaceSize;
            }

            ImageSize = layerSize * arrayCount;
            imageOffset = ImageSize;

            return mipmaps;
        }
    }
}
