using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using OpenTK.Graphics.OpenGL;
using Toolbox.Core;

namespace GLFrameworkEngine
{
    public class GLTextureCubeArray : GLTexture
    {
        public GLTextureCubeArray() : base()
        {
            Target = TextureTarget.TextureCubeMapArray;
        }

        public static GLTextureCubeArray CreateEmptyCubemap(int size, int arrayCount, int mipCount,
            PixelInternalFormat pixelInternalFormat = PixelInternalFormat.Rgba8,
            PixelFormat pixelFormat = PixelFormat.Rgba,
            PixelType pixelType = PixelType.UnsignedByte)
        {
            GLTextureCubeArray texture = new GLTextureCubeArray();
            texture.PixelFormat = pixelFormat;
            texture.PixelType = pixelType;
            texture.PixelInternalFormat = pixelInternalFormat;
            texture.Width = size; texture.Height = size;
            texture.Target = TextureTarget.TextureCubeMapArray;
            texture.MinFilter = TextureMinFilter.LinearMipmapLinear;
            texture.MagFilter = TextureMagFilter.Linear;
            texture.MipCount = mipCount;
            texture.ArrayCount = arrayCount;
            texture.Bind();

            //Allocate mip data
            if (texture.MipCount > 1)
                texture.GenerateMipmaps();

            for (int mip = 0; mip < texture.MipCount; mip++)
            {
                int mipWidth = (int)(texture.Width * Math.Pow(0.5, mip));
                int mipHeight = (int)(texture.Height * Math.Pow(0.5, mip));

                GL.TexImage3D(texture.Target, 0, texture.PixelInternalFormat,
                    mipWidth, mipHeight, texture.ArrayCount * 6, mip,
                      texture.PixelFormat, texture.PixelType, IntPtr.Zero);
            }

            texture.UpdateParameters();
            texture.Unbind();
            return texture;
        }

        public static GLTextureCubeArray FromGeneric(STGenericTexture texture, ImageParameters parameters)
        {
            GLTextureCubeArray glTexture = new GLTextureCubeArray();
            glTexture.Target = TextureTarget.Texture2D;
            glTexture.Width = (int)texture.Width;
            glTexture.Height = (int)texture.Height;
            glTexture.LoadImage(texture, parameters);
            return glTexture;
        }

        /// <summary>
        /// Loads a DX10 DDS containing a cubemap array in raw R11G11B10_FLOAT
        /// (DXGI 26) with full mip chains (e.g. RenderDoc dumps of prefiltered
        /// env cubemaps). DDS stores mips per layer; GL wants layers per mip.
        /// </summary>
        public static GLTextureCubeArray FromDX10ArrayDDS(string filePath)
        {
            byte[] data = System.IO.File.ReadAllBytes(filePath);
            if (data.Length < 148 || data[0] != 'D' || data[1] != 'D' || data[2] != 'S')
                throw new Exception("Not a DDS file");

            int height = BitConverter.ToInt32(data, 12);
            int width = BitConverter.ToInt32(data, 16);
            int mipCount = Math.Max(1, BitConverter.ToInt32(data, 28));
            string fourCC = System.Text.Encoding.ASCII.GetString(data, 84, 4);
            if (fourCC != "DX10")
                throw new Exception("Expected DX10 header");
            int dxgiFormat = BitConverter.ToInt32(data, 128);
            int arraySize = BitConverter.ToInt32(data, 140);
            if (dxgiFormat != 26) //DXGI_FORMAT_R11G11B10_FLOAT
                throw new Exception($"Unsupported DXGI format {dxgiFormat}");
            if (arraySize % 6 != 0)
                throw new Exception($"Array size {arraySize} is not a multiple of 6");

            var texture = new GLTextureCubeArray();
            texture.Width = width;
            texture.Height = height;
            texture.MipCount = mipCount;
            texture.ArrayCount = arraySize / 6;
            texture.Bind();

            //Per-layer offsets of each mip within a layer's data block.
            int[] mipOffsets = new int[mipCount];
            int[] mipSizes = new int[mipCount];
            int layerSize = 0;
            for (int m = 0; m < mipCount; m++)
            {
                int w = Math.Max(1, width >> m);
                int h = Math.Max(1, height >> m);
                mipOffsets[m] = layerSize;
                mipSizes[m] = w * h * 4;
                layerSize += mipSizes[m];
            }

            const int headerSize = 148;
            for (int m = 0; m < mipCount; m++)
            {
                int w = Math.Max(1, width >> m);
                int h = Math.Max(1, height >> m);
                byte[] mipData = new byte[mipSizes[m] * arraySize];
                for (int layer = 0; layer < arraySize; layer++)
                {
                    int srcOffset = headerSize + layer * layerSize + mipOffsets[m];
                    System.Buffer.BlockCopy(data, srcOffset, mipData, layer * mipSizes[m], mipSizes[m]);
                }
                GL.TexImage3D(TextureTarget.TextureCubeMapArray, m,
                    PixelInternalFormat.R11fG11fB10f, w, h, arraySize, 0,
                    PixelFormat.Rgb, PixelType.UnsignedInt10F11F11FRev, mipData);
            }

            GL.TexParameter(texture.Target, TextureParameterName.TextureBaseLevel, 0);
            GL.TexParameter(texture.Target, TextureParameterName.TextureMaxLevel, mipCount - 1);
            GL.TexParameter(texture.Target, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.LinearMipmapLinear);
            GL.TexParameter(texture.Target, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            //Match the game's sampler (clamp to edge, full mip range).
            GL.TexParameter(texture.Target, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(texture.Target, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(texture.Target, TextureParameterName.TextureWrapR, (int)TextureWrapMode.ClampToEdge);
            texture.Unbind();
            return texture;
        }

        public static GLTextureCubeArray FromDDS(DDS dds)
        {
            int size = (int)dds.Width;

            GLTextureCubeArray texture = new GLTextureCubeArray();
            texture.Width = size;
            texture.Height = size;
            texture.Bind();

            var format = dds.Platform.OutputFormat;

            var surfaces = dds.GetSurfaces();
            List<byte[]> cubemapSurfaces = new List<byte[]>();
            for (int a = 0; a < surfaces.Count; a++)
                cubemapSurfaces.Add(surfaces[a].mipmaps[0]);

            int depth = surfaces.Count;

            byte[] buffer = ByteUtils.CombineArray(cubemapSurfaces.ToArray());

            for (int j = 0; j < dds.MipCount; j++)
            {
                int mipWidth = CalculateMipDimension(texture.Width, j);
                int mipHeight = CalculateMipDimension(texture.Height, j);

                if (dds.IsBCNCompressed())
                {
                    var internalFormat = GLFormatHelper.ConvertCompressedFormat(format, true);
                    GLTextureDataLoader.LoadCompressedImage(texture.Target, mipWidth, mipHeight, depth, internalFormat, buffer, j);
                }
                else
                {
                    var formatInfo = GLFormatHelper.ConvertPixelFormat(format);
                    if (dds.Platform.OutputFormat == TexFormat.RGBA8_UNORM)
                        formatInfo.Format = PixelFormat.Rgba;

                    GLTextureDataLoader.LoadImage(texture.Target, mipWidth, mipHeight, depth, formatInfo, buffer, j);
                }
            }

            GL.TexParameter(texture.Target, TextureParameterName.TextureBaseLevel, 0);
            GL.TexParameter(texture.Target, TextureParameterName.TextureMaxLevel, 6);
            GL.GenerateMipmap(GenerateMipmapTarget.TextureCubeMapArray);
            GL.TexParameter(texture.Target, TextureParameterName.TextureSwizzleR, (int)All.Red);
            GL.TexParameter(texture.Target, TextureParameterName.TextureSwizzleG, (int)All.Green);
            GL.TexParameter(texture.Target, TextureParameterName.TextureSwizzleB, (int)All.Blue);
            GL.TexParameter(texture.Target, TextureParameterName.TextureSwizzleA, (int)All.One);

            texture.Unbind();
            return texture;
        }

        public override void SaveDDS(string fileName)
        {
            List<STGenericTexture.Surface> surfaces = new List<STGenericTexture.Surface>();

            Bind();

            int size = this.Width;
            for (int i = 0; i < 6 * ArrayCount; i++)
            {
                var surface = new STGenericTexture.Surface();
                surfaces.Add(surface);

                for (int m = 0; m < MipCount; m++)
                {
                    int mipSize = (int)(size * Math.Pow(0.5, m));
                    byte[] outputRaw = new byte[mipSize * mipSize * 4];
                    GL.GetTextureSubImage(this.ID, m, 0, 0, i, Width, Height, 1,
                      PixelFormat.Rgba, PixelType.UnsignedByte, outputRaw.Length, outputRaw);

                    surface.mipmaps.Add(outputRaw);
                }
            }

            var dds = new DDS();
            dds.MainHeader.Width = (uint)this.Width;
            dds.MainHeader.Height = (uint)this.Height;
            dds.MainHeader.Depth = 1;
            dds.MainHeader.MipCount = (uint)this.MipCount;
            dds.MainHeader.PitchOrLinearSize = (uint)surfaces[0].mipmaps[0].Length;
            dds.ArrayCount = (uint)this.ArrayCount * 6;

            dds.SetFlags(TexFormat.RGBA8_UNORM, ArrayCount > 6, true);

            if (dds.IsDX10)
            {
                if (dds.Dx10Header == null)
                    dds.Dx10Header = new DDS.DX10Header();

                dds.Dx10Header.ResourceDim = 3;
                dds.Dx10Header.ArrayCount = (uint)ArrayCount * 6;
            }

            dds.Save(fileName, surfaces);

            Unbind();
        }

        public void Save(string fileName)
        {
            Bind();
            for (int i = 0; i < 6; i++)
            {
                byte[] output = new byte[Width * Height * 4];
                GL.GetTextureSubImage((int)this.Target, 0, 0, 0, i, Width, Height, 1,
                PixelFormat.Bgra, PixelType.UnsignedByte, output.Length, output);

                //Remove alpha
                output = SetImageData(output, true, true);

                var bitmap = BitmapImageHelper.CreateBitmap(output, Width, Height);
                bitmap.Save(fileName + $"_{i}.png");
            }
            Unbind();
        }

        public byte[] GetImage(int index)
        {
            Bind();
            byte[] output = new byte[Width * Height * 4];

            GL.GetTextureSubImage((int)this.Target, 0, 0, 0, index, Width, Height, 1,
              PixelFormat.Bgra, PixelType.UnsignedByte, output.Length, output);

            output = SetImageData(output, true, true);

            Unbind();
            return output;
        }

        private byte[] SetImageData(byte[] input, bool flipImage, bool removeAlpha)
        {
            byte[] output = new byte[Width * Height * 4];
            int stride = Width * 4;

            if (flipImage)
            {
                for (int y = 0; y < Height; y++)
                {
                    int IOffs = stride * y;
                    int OOffs = stride * (Height - 1 - y);

                    for (int x = 0; x < Width; x++)
                    {
                        output[OOffs + 0] = input[IOffs + 0];
                        output[OOffs + 1] = input[IOffs + 1];
                        output[OOffs + 2] = input[IOffs + 2];
                        output[OOffs + 3] = removeAlpha ? (byte)255 : input[IOffs + 3];

                        IOffs += 4;
                        OOffs += 4;
                    }
                }
            }
            else if (removeAlpha)
            {
                for (int x = 0; x < Width; x++)
                {
                    for (int y = 0; y < Height; y++)
                    {
                        int pixelIndex = x + (y * Width);
                        output[pixelIndex * 4 + 3] = 255;
                    }
                }
            }
            return output;
        }
    }
}
