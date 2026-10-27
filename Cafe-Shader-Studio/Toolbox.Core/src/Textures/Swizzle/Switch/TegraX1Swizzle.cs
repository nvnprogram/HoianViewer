using System;
using System.Collections.Generic;

namespace Toolbox.Core.Switch
{
    public class TegraX1Swizzle
    {
        public static List<uint[]> GenerateMipSizes(TexFormat Format, uint Width, uint Height, uint Depth, uint SurfaceCount, uint MipCount, uint ImageSize)
        {
            List<uint[]> MipMapSizes = new List<uint[]>();

            uint bpp = TextureFormatHelper.GetBytesPerPixel(Format);
            uint blkWidth = TextureFormatHelper.GetBlockWidth(Format);
            uint blkHeight = TextureFormatHelper.GetBlockHeight(Format);
            uint blkDepth = TextureFormatHelper.GetBlockDepth(Format);

            uint blockHeight = TegraX1Swizzle.GetBlockHeight(TegraX1Swizzle.DIV_ROUND_UP(Height, blkHeight));
            uint BlockHeightLog2 = (uint)Convert.ToString(blockHeight, 2).Length - 1;

            uint Pitch = 0;
            uint DataAlignment = 512;

            int linesPerBlockHeight = (1 << (int)BlockHeightLog2) * 8;

            uint ArrayCount = SurfaceCount;

            uint ArrayOffset = 0;
            for (int arrayLevel = 0; arrayLevel < ArrayCount; arrayLevel++)
            {
                uint SurfaceSize = 0;
                int blockHeightShift = 0;

                uint[] MipOffsets = new uint[MipCount];

                for (int mipLevel = 0; mipLevel < MipCount; mipLevel++)
                {
                    uint width = (uint)Math.Max(1, Width >> mipLevel);
                    uint height = (uint)Math.Max(1, Height >> mipLevel);
                    uint depth = (uint)Math.Max(1, Depth >> mipLevel);

                    uint size = TegraX1Swizzle.DIV_ROUND_UP(width, blkWidth) * TegraX1Swizzle.DIV_ROUND_UP(height, blkHeight) * bpp;

                    if (TegraX1Swizzle.pow2_round_up(TegraX1Swizzle.DIV_ROUND_UP(height, blkWidth)) < linesPerBlockHeight)
                        blockHeightShift += 1;

                    uint width__ = TegraX1Swizzle.DIV_ROUND_UP(width, blkWidth);
                    uint height__ = TegraX1Swizzle.DIV_ROUND_UP(height, blkHeight);

                    //Calculate the mip size instead
                    byte[] AlignedData = new byte[(TegraX1Swizzle.round_up(SurfaceSize, DataAlignment) - SurfaceSize)];
                    SurfaceSize += (uint)AlignedData.Length;
                    MipOffsets[mipLevel] = (SurfaceSize);

                    //Get the first mip offset and current one and the total image size
                    int msize = (int)((MipOffsets[0] + ImageSize - MipOffsets[mipLevel]) / ArrayCount);

                    Pitch = TegraX1Swizzle.round_up(width__ * bpp, 64);
                    SurfaceSize += Pitch * TegraX1Swizzle.round_up(height__, Math.Max(1, blockHeight >> blockHeightShift) * 8);
                }
                ArrayOffset += (uint)(ImageSize / ArrayCount);

                MipMapSizes.Add(MipOffsets);
            }

            return MipMapSizes;
        }

        public static byte[] GetImageData(STGenericTexture texture, Span<byte> ImageData, int ArrayLevel, int MipLevel, int DepthLevel, int target = 1, bool LinearTileMode = false, bool xbarGob = false)
        {
            var format = texture.Platform.OutputFormat;
            uint blkHeight = texture.GetBlockHeight();
            uint blkDepth = texture.GetBlockDepth();
            uint blockHeight = TegraX1Swizzle.GetBlockHeight(TegraX1Swizzle.DIV_ROUND_UP(texture.Height, blkHeight));
            uint BlockHeightLog2 = (uint)Convert.ToString(blockHeight, 2).Length - 1;

            return GetImageData(texture, ImageData, ArrayLevel, MipLevel, DepthLevel, BlockHeightLog2, target, LinearTileMode, xbarGob);
        }

        public static byte[] GetImageData(STGenericTexture texture, Span<byte> ImageData, int ArrayLevel, int MipLevel, int DepthLevel, uint BlockHeightLog2, int target = 1, bool LinearTileMode = false, bool xbarGob = false)
        {
            var format = texture.Platform.OutputFormat;
            uint bpp = TextureFormatHelper.GetBytesPerPixel(format);
            uint blkWidth = texture.GetBlockWidth();
            uint blkHeight = texture.GetBlockHeight();
            uint blkDepth = texture.GetBlockDepth();
            uint blockHeight = TegraX1Swizzle.GetBlockHeight(TegraX1Swizzle.DIV_ROUND_UP(texture.Height, blkHeight));


            uint Pitch = 0;
            uint DataAlignment = 512;
            uint TileMode = 0;
            if (LinearTileMode)
                TileMode = 1;
            uint numDepth = 1;
            if (texture.Depth > 1)
                numDepth = texture.Depth;

            int linesPerBlockHeight = (1 << (int)BlockHeightLog2) * 8;

            uint ArrayOffset = 0;
            for (int depthLevel = 0; depthLevel < numDepth; depthLevel++)
            {
                for (int arrayLevel = 0; arrayLevel < texture.ArrayCount; arrayLevel++)
                {
                    uint SurfaceSize = 0;
                    int blockHeightShift = 0;

                    List<uint> MipOffsets = new List<uint>();

                    for (int mipLevel = 0; mipLevel < texture.MipCount; mipLevel++)
                    {
                        uint width = (uint)Math.Max(1, texture.Width >> mipLevel);
                        uint height = (uint)Math.Max(1, texture.Height >> mipLevel);
                        uint depth = (uint)Math.Max(1, texture.Depth >> mipLevel);

                        uint size = TegraX1Swizzle.DIV_ROUND_UP(width, blkWidth) * TegraX1Swizzle.DIV_ROUND_UP(height, blkHeight) * bpp;


                        uint width__ = TegraX1Swizzle.DIV_ROUND_UP(width, blkWidth);
                        uint height__ = TegraX1Swizzle.DIV_ROUND_UP(height, blkHeight);

                        //Calculate the mip size instead
                        byte[] AlignedData = new byte[(TegraX1Swizzle.round_up(SurfaceSize, DataAlignment) - SurfaceSize)];
                        SurfaceSize += (uint)AlignedData.Length;
                        MipOffsets.Add(SurfaceSize);

                        uint mipBlockHeightLog2 = TegraX1Swizzle.ShrinkBlockHeightLog2(BlockHeightLog2, height__);
                        int msize = (int)(ImageData.Length / texture.ArrayCount - MipOffsets[mipLevel]);

                        var data_ = ImageData.Slice((int)(ArrayOffset + MipOffsets[mipLevel]), msize);

                        try
                        {
                            Pitch = TegraX1Swizzle.round_up(width__ * bpp, 64);
                            SurfaceSize += Pitch * TegraX1Swizzle.round_up(height__, (1u << (int)mipBlockHeightLog2) * 8);

                            Span<byte> result = TegraX1Swizzle.deswizzle(width, height, depth, blkWidth, blkHeight, blkDepth, target, bpp, TileMode, (int)mipBlockHeightLog2, data_, xbarGob);
                            //Create a copy and use that to remove uneeded data
                            byte[] result_ = new byte[size];
                            Array.Copy(result.ToArray(), 0, result_, 0, size);
                            result = null;

                            if (ArrayLevel == arrayLevel && MipLevel == mipLevel && DepthLevel == depthLevel)
                                return result_;
                        }
                        catch (Exception e)
                        {
                            Console.WriteLine($"Failed to swizzle texture {texture.Name}!");
                            Console.WriteLine(e);

                            return new byte[0];
                        }
                    }
                    ArrayOffset += (uint)(ImageData.Length / texture.ArrayCount);
                }
            }
            return new byte[0];
        }

        /*---------------------------------------
         * 
         * Code ported from AboodXD's BNTX Extractor https://github.com/aboood40091/BNTX-Extractor/blob/master/swizzle.py
         * 
         *---------------------------------------*/

        public static uint GetBlockHeight(uint height)
        {
            uint blockHeight = pow2_round_up(height / 8);
            if (blockHeight > 16)
                blockHeight = 16;

            return blockHeight;
        }

        /// <summary>
        /// Gobs per block in Y for a surface whose base level is <paramref name="height"/> rows
        /// of block linear elements.
        /// </summary>
        public static uint GetBlockHeightLog2(uint height)
        {
            uint log2 = 4;
            while (log2 > 0 && 8u * (1u << (int)log2) > height + height / 2)
                log2--;

            return log2;
        }

        /// <summary>
        /// The same block shrunk for one mip level: halve while half the block would still
        /// cover the level on its own.
        /// </summary>
        public static uint ShrinkBlockHeightLog2(uint log2, uint height)
        {
            while (log2 > 0 && (8u << (int)(log2 - 1)) >= height)
                log2--;

            return log2;
        }

        public static uint DIV_ROUND_UP(uint n, uint d)
        {
            return (n + d - 1) / d;
        }
        public static uint round_up(uint x, uint y)
        {
            return ((x - 1) | (y - 1)) + 1;
        }
        public static uint pow2_round_up(uint x)
        {
            x -= 1;
            x |= x >> 1;
            x |= x >> 2;
            x |= x >> 4;
            x |= x >> 8;
            x |= x >> 16;
            return x + 1;
        }

        private static Span<byte> _swizzle(uint width, uint height, uint depth, uint blkWidth, uint blkHeight, uint blkDepth, int roundPitch, uint bpp, uint tileMode, int blockHeightLog2, Span<byte> data, int toSwizzle, bool xbarGob)
        {
            uint block_height = (uint)(1 << blockHeightLog2);

            width = DIV_ROUND_UP(width, blkWidth);
            height = DIV_ROUND_UP(height, blkHeight);
            depth = DIV_ROUND_UP(depth, blkDepth);

            uint pitch;
            uint surfSize;
            if (tileMode == 1)
            {
                pitch = width * bpp;

                if (roundPitch == 1)
                    pitch = round_up(pitch, 32);

                surfSize = pitch * height;
            }
            else
            {
                pitch = round_up(width * bpp, 64);
                surfSize = pitch * round_up(height, block_height * 8);
            }

            Span<byte> result = new byte[surfSize];

            for (uint y = 0; y < height; y++)
            {
                for (uint x = 0; x < width; x++)
                {
                    uint pos;
                    uint pos_;

                    if (tileMode == 1)
                        pos = y * pitch + x * bpp;
                    else
                        pos = getAddrBlockLinear(x, y, width, bpp, 0, block_height, xbarGob);

                    pos_ = (y * width + x) * bpp;

                    if (pos + bpp <= surfSize)
                    {
                        int srcOffset = toSwizzle == 0 ? (int)pos : (int)pos_;
                        int dstOffset = toSwizzle == 0 ? (int)pos_ : (int)pos;
                        var src = data.Slice(srcOffset, (int)bpp);
                        var dst = result.Slice(dstOffset, (int)bpp);
                        src.CopyTo(dst);
                    }
                }
            }
            return result;
        }

        public static Span<byte> deswizzle(uint width, uint height, uint depth, uint blkWidth, uint blkHeight, uint blkDepth, int roundPitch, uint bpp, uint tileMode, int size_range, Span<byte> data, bool xbarGob = false)
        {
            return _swizzle(width, height, depth, blkWidth, blkHeight, blkDepth, roundPitch, bpp, tileMode, size_range, data, 0, xbarGob);
        }

        public static Span<byte> swizzle(uint width, uint height, uint depth, uint blkWidth, uint blkHeight, uint blkDepth, int roundPitch, uint bpp, uint tileMode, int size_range, Span<byte> data, bool xbarGob = false)
        {
            return _swizzle(width, height, depth, blkWidth, blkHeight, blkDepth, roundPitch, bpp, tileMode, size_range, data, 1, xbarGob);
        }

        static uint getAddrBlockLinear(uint x, uint y, uint width, uint bytes_per_pixel, uint base_address, uint block_height, bool xbarGob)
        {
            /*
              From Tega X1 TRM 
                               */
            uint image_width_in_gobs = DIV_ROUND_UP(width * bytes_per_pixel, 64);


            uint GOB_address = (base_address
                                + (y / (8 * block_height)) * 512 * block_height * image_width_in_gobs
                                + (x * bytes_per_pixel / 64) * 512 * block_height
                                + (y % (8 * block_height) / 8) * 512);

            x *= bytes_per_pixel;

            if (xbarGob)
                return GOB_address + ((x % 64) / 32) * 256 + ((y % 8) / 4) * 128
                    + ((x % 32) / 16) * 64 + (y % 4) * 16 + (x % 16);

            uint Address = (GOB_address + ((x % 64) / 32) * 256 + ((y % 8) / 2) * 64
                            + ((x % 32) / 16) * 32 + (y % 2) * 16 + (x % 16));
            return Address;
        }
    }
}