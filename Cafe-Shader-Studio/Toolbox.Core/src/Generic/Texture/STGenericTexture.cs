using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Drawing;
using Toolbox.Core.Imaging;
using Toolbox.Core.Switch;

namespace Toolbox.Core
{
    [Serializable]
    public abstract class STGenericTexture
    {
        public IRenderableTexture RenderableTex;

        public virtual string Name { get; set; }

        public bool IsSRGB { get; set; }

        public EventHandler DisplayPropertiesChanged = null;

        public STGenericTexture()
        {
            RedChannel = STChannelType.Red;
            GreenChannel = STChannelType.Green;
            BlueChannel = STChannelType.Blue;
            AlphaChannel = STChannelType.Alpha;
            DisplayProperties = new DefaultTextureProperties(this); 
        }

        /// <summary>
        /// The properties to display on the texture editor.
        /// </summary>
        public object DisplayProperties { get; set; }

        /// <summary>
        /// A Surface contains mip levels of compressed/uncompressed texture data
        /// </summary>
        public class Surface
        {
            public List<byte[]> mipmaps = new List<byte[]>();
        }

        /// <summary>
        /// The type of surface the texture uses which can determine how to use array levels and cubemaps.
        /// </summary>
        public STSurfaceType SurfaceType = STSurfaceType.Texture2D;

        /// <summary>
        /// Is the texture edited or not. Used for the image editor for saving changes.
        /// </summary>
        public bool IsEdited { get; set; } = false;

        /// <summary>
        /// The width of the image in pixels.
        /// </summary>
        public uint Width { get; set; }

        /// <summary>
        /// The height of the image in pixels.
        /// </summary>
        public uint Height { get; set; }

        /// <summary>
        /// The depth of the image in pixels. Used for 3D types.
        /// </summary>
        public uint Depth { get; set; }

        /// <summary>
        /// The total amount of surfaces for the texture.
        /// </summary>
        public uint ArrayCount { get; set; } = 1;

        /// <summary>
        /// The total amount of mipmaps for the texture.
        /// </summary>
        public uint MipCount
        {
            get { return mipCount; }
            set
            {
                if (value == 0)
                    mipCount = 1;
                else if (value > 17)
                    throw new Exception($"Invalid mip map count! Texture: {Name} Value: {value}");
                else
                    mipCount = value;
            }
        }

        /// <summary>
        /// The <see cref="PaletteFormat"/> which controls palette information.
        /// </summary>
        public PaletteFormat PaletteFormat { get; set; } = PaletteFormat.None;

        /// <summary>
        /// Determines if the image supports replacing and editing.
        /// </summary>
        public bool CanEdit { get; set; }

        /// <summary>
        /// Parameters on how to replace and save back the texture.
        /// </summary>
        public ImageParameters Parameters = new ImageParameters();

        /// <summary>
        /// The swizzle method to use when decoding or encoding back a texture.
        /// </summary>
        public IPlatformSwizzle Platform = new Imaging.DefaultSwizzle();

        /// <summary>
        /// Gets the image size from bytes into a string format.
        /// </summary>
        public string DataSize { get { return STMath.GetFileSize(DataSizeInBytes, 5); } }

        /// <summary>
        /// A list of all the formats supported by the texture.
        /// </summary>
        public virtual TexFormat[] SupportedFormats => new TexFormat[0];

        /// <summary>
        /// A list of external references of things like materials or animations.
        /// </summary>
        public virtual string[] ExternalReferences { get; }

        /// <summary>
        /// Determines which component to use for the red channel.
        /// </summary>
        public STChannelType RedChannel = STChannelType.Red;


        /// <summary>
        /// Determines which component to use for the green channel.
        /// </summary>
        public STChannelType GreenChannel = STChannelType.Green;

        /// <summary>
        /// Determines which component to use for the blue channel.
        /// </summary>
        public STChannelType BlueChannel = STChannelType.Blue;

        /// <summary>
        /// Determines which component to use for the alpha channel.
        /// </summary>
        public STChannelType AlphaChannel = STChannelType.Alpha;

        /// <summary>
        /// Determines if the texture is a cubemap based on the surface type.
        /// </summary>
        public bool IsCubemap
        {
            get { 
                return SurfaceType == STSurfaceType.TextureCube || 
                    SurfaceType == STSurfaceType.TextureCube_Array; }
        }

        public abstract void SetImageData(List<byte[]> imageData, uint width, uint height, int arrayLevel = 0);

        public abstract byte[] GetImageData(int ArrayLevel = 0, int MipLevel = 0, int DepthLevel = 0);

        public virtual byte[] GetPaletteData() { return paletteData; }
        private byte[] paletteData = new byte[0];
        private uint mipCount = 1;

        public void LoadRenderableTexture()
        {
            RenderableTex = RenderResourceCreator.CreateTexture(this);
        }

        public virtual void SetPaletteData(byte[] data, PaletteFormat format)
        {
            paletteData = data;
            PaletteFormat = format;
        }

        public List<Surface> Get3DSurfaces(int IndexStart = 0, bool GetAllSurfaces = true, int GetSurfaceAmount = 1)
        {
            if (GetAllSurfaces)
                GetSurfaceAmount = (int)Depth;

            var surfaces = new List<Surface>();
            for (int depthLevel = 0; depthLevel < Depth; depthLevel++)
            {
                bool IsLower = depthLevel < IndexStart;
                bool IsHigher = depthLevel >= (IndexStart + GetSurfaceAmount);
                if (!IsLower && !IsHigher)
                {
                    List<byte[]> mips = new List<byte[]>();
                    for (int mipLevel = 0; mipLevel < MipCount; mipLevel++)
                    {
                        mips.Add(GetSurface(0, mipLevel, depthLevel));
                    }

                    surfaces.Add(new Surface() { mipmaps = mips });
                }
            }

            return surfaces;
        }

        /// <summary>
        ///Gets a list of surfaces given the start index of the array and the amount of arrays to obtain
        /// </summary>
        /// <returns></returns>
        public List<Surface> GetSurfaces(int ArrayIndexStart = 0, bool GetAllSurfaces = true, int GetSurfaceAmount = 1)
        {
            if (GetAllSurfaces)
                GetSurfaceAmount = (int)ArrayCount;

            var surfaces = new List<Surface>();
            for (int arrayLevel = 0; arrayLevel < ArrayCount; arrayLevel++)
            {
                bool IsLower = arrayLevel < ArrayIndexStart;
                bool IsHigher = arrayLevel >= (ArrayIndexStart + GetSurfaceAmount);
                if (!IsLower && !IsHigher)
                {
                    List<byte[]> mips = new List<byte[]>();
                    for (int mipLevel = 0; mipLevel < MipCount; mipLevel++) {
                        mips.Add(GetDeswizzledSurface(arrayLevel, mipLevel));
                    }

                    surfaces.Add(new Surface() { mipmaps = mips });
                }
            }

            return surfaces;
        }

        public void Export(string filePath, TextureExportSettings settings)
        {
            foreach (var format in FileManager.GetExportableTextures())
            {
              if (format.IdentifyExport(Utils.GetExtension(filePath))) {
                    format.Export(this, settings, filePath);
                    break;
                }
            }
        }

        public void Replace(string filePath)
        {
            foreach (var format in FileManager.GetImportableTextures())
            {
                if (format.IdentifyImport(Utils.GetExtension(filePath))) {
                    format.Import(filePath);
                    break;
                }
            }
        }

        public void SaveBitmap(string filePath, TextureExportSettings settings = null)
        {
            var bitmap = GetBitmap(settings.ArrayLevel, settings.MipLevel);
            bitmap.Save(filePath);
        }

        public void SaveDDS(string filePath, TextureExportSettings settings = null) {
            var surfaces = GetSurfaces();

            DDS dds = new DDS();
            dds.MainHeader.Width = this.Width;
            dds.MainHeader.Height = this.Height;
            dds.MainHeader.Depth = this.Depth;
            dds.MainHeader.MipCount = this.MipCount;
            dds.MainHeader.PitchOrLinearSize = (uint)surfaces[0].mipmaps[0].Length;

            bool isCubeMap = ArrayCount == 6;
            if (surfaces.Count > 1) //Use DX10 format for array surfaces as it can do custom amounts
                dds.SetFlags(Platform.OutputFormat, true, isCubeMap);
            else
                dds.SetFlags(Platform.OutputFormat, false, isCubeMap);

            if (dds.IsDX10)
            {
                if (dds.Dx10Header == null)
                    dds.Dx10Header = new DDS.DX10Header();

                dds.Dx10Header.ResourceDim = 3;

                if (isCubeMap)
                    dds.Dx10Header.ArrayCount = (uint)(ArrayCount / 6);
                else
                    dds.Dx10Header.ArrayCount = (uint)ArrayCount;
            }
            dds.Save(filePath, surfaces);
        }

        public byte[] GetSurface(int ArrayLevel = 0, int MipLevel = 0, int DepthLevel = 0)
        {
            uint width = Math.Max(1, Width >> MipLevel);
            uint height = Math.Max(1, Height >> MipLevel);
            byte[] data = GetImageData(ArrayLevel, MipLevel, DepthLevel);
            return data;
        }

        /// <summary>
        /// Gets a <see cref="Bitmap"/> given an array and mip index.
        /// </summary>
        /// <param name="ArrayIndex">The index of the surface/array. Cubemaps will have 6</param>
        /// <param name="MipLevel">The index of the mip level.</param>
        /// <returns></returns>
        public Bitmap GetBitmap(int ArrayLevel = 0, int MipLevel = 0, int DepthLevel = 0)
        {
            //Get the mip width and height on the current mip level
            uint width = Math.Max(1, Width >> MipLevel);
            uint height = Math.Max(1, Height >> MipLevel);
            //Get the raw image data from the texture
            byte[] data = GetImageData(ArrayLevel, MipLevel, DepthLevel);

            //Deswizzle the image data by the platform being used
            data = Platform.DecodeImage(this, data, width, height, ArrayLevel, MipLevel);
                
            //Decompress the compressed/encoded data if not RGBA
            if (Platform.OutputFormat != TexFormat.RGBA8_UNORM)
                data = DecodeBlock(data, width, height, Platform.OutputFormat);
            else if (Platform is DefaultSwizzle || Platform is SwitchSwizzle || Platform is WiiUSwizzle)
                data = ImageUtility.ConvertBgraToRgba(data); //Swap Red/Blue channels

            //Adjust BCN textures with improper pow2 sizes
            if (IsBCNCompressed())
            {
                width = ((width + 3) / 4) * 4;
                height = ((height + 3) / 4) * 4;
            }
            //Create a new bitmap image from RGBA data and the width/height
            return BitmapExtension.CreateBitmap(data, (int)width, (int)height);
        }

        public byte[] GetDecodedSurface(int ArrayLevel = 0, int MipLevel = 0, int DepthLevel = 0)
        {
            //Get the mip width and height on the current mip level
            uint width = Math.Max(1, Width >> MipLevel);
            uint height = Math.Max(1, Height >> MipLevel);
            //Get the raw image data from the texture
            byte[] data = GetImageData(ArrayLevel, MipLevel, DepthLevel);

            //Deswizzle the image data by the platform being used
            data = Platform.DecodeImage(this, data, width, height, ArrayLevel, MipLevel);

            //Decompress the compressed/encoded data if not RGBA
            if (Platform.OutputFormat != TexFormat.RGBA8_UNORM)
                data = DecodeBlock(data, width, height, Platform.OutputFormat);
            else if (Platform is DefaultSwizzle || Platform is SwitchSwizzle || Platform is WiiUSwizzle)
                data = ImageUtility.ConvertBgraToRgba(data); //Swap Red/Blue channels
            return data;
        }

        public byte[] GetDeswizzledSurface(int ArrayLevel = 0, int MipLevel = 0, int DepthLevel = 0)
        {
            uint width = Math.Max(1, Width >> MipLevel);
            uint height = Math.Max(1, Height >> MipLevel);
            byte[] data = GetImageData(ArrayLevel, MipLevel, DepthLevel);
            data = Platform.DecodeImage(this, data, width, height, ArrayLevel, MipLevel);
            if (SwapsRedBlue())
                data = ImageUtility.ConvertBgraToRgba(data);
            return data;
        }

        public bool SwapsRedBlue()
        {
            if (Parameters.DontSwapRG)
                return false;
            string name = Platform.OutputFormat.ToString();
            return name.StartsWith("RGBA8_") || name.StartsWith("BGRA8_") || name.StartsWith("BGRX8_");
        }

        public static byte[] DecodeBlock(byte[] data, uint width, uint height, TexFormat format) {
            byte[] output = new byte[0];
            //Search for decoders. Plugins can have custom external decoders, some optimized for certain platforms
            foreach (var decoder in FileManager.GetTextureDecoders())
            {
                bool isDecoded = decoder.Decode(format, data, (int)width, (int)height, out output);
                if (isDecoded)
                    return output != null ? ImageUtility.ConvertBgraToRgba(output) : new byte[0];
            }

            return output != null ? ImageUtility.ConvertBgraToRgba(output) : new byte[0];
        }

        public uint GetBlockWidth()
        {
            var format = Platform.OutputFormat;
            return TextureFormatHelper.GetBlockWidth(format);
        }

        public uint GetBlockHeight()
        {
            var format = Platform.OutputFormat;
            return TextureFormatHelper.GetBlockHeight(format);
        }

        public uint GetBlockDepth()
        {
            var format = Platform.OutputFormat;
            return TextureFormatHelper.GetBlockDepth(format);
        }

        public uint GetBytesPerPixel()
        {
            var format = Platform.OutputFormat;
            return TextureFormatHelper.GetBytesPerPixel(format);
        }

        public bool IsBCNCompressed()
        {
            var format = Platform.OutputFormat;
            return TextureFormatHelper.IsBCNCompressed(format);
        }

        public bool IsASTC()
        {
            var format = Platform.OutputFormat;
            return format.ToString().StartsWith("ASTC");
        }

        /// <summary>
        /// The total length of all the bytes given from GetImageData.
        /// </summary>
        public long DataSizeInBytes
        {
            get
            {
                if (Platform is Imaging.CTRSwizzle)
                    return GetImageSize3DS();
                if (Platform is Imaging.GamecubeSwizzle)
                    return GetImageSizeGCN();
                else
                    return GetImageSizeDefault();
            }
        }

        private int GetImageSizeDefault()
        {
            var format = Platform.OutputFormat;

            int totalSize = 0;
            uint bpp = TextureFormatHelper.GetBytesPerPixel(format);

            for (int arrayLevel = 0; arrayLevel < ArrayCount; arrayLevel++)
            {
                for (int mipLevel = 0; mipLevel < MipCount; mipLevel++)
                {
                    uint width = (uint)Math.Max(1, Width >> mipLevel);
                    uint height = (uint)Math.Max(1, Height >> mipLevel);

                    uint size = width * height * bpp;
                    if (IsBCNCompressed())
                    {
                        size = ((width + 3) >> 2) * ((Height + 3) >> 2) * bpp;
                        if (size < bpp)
                            size = bpp;
                    }

                    totalSize += (int)size;
                }
            }
            return totalSize;
        }

        private int GetImageSizeGCN()
        {
            var platform = (GamecubeSwizzle)Platform;

            int totalSize = 0;
            for (int arrayLevel = 0; arrayLevel < ArrayCount; arrayLevel++)
            {
                for (int mipLevel = 0; mipLevel < MipCount; mipLevel++)
                {
                    uint width = (uint)Math.Max(1, Width >> mipLevel);
                    uint height = (uint)Math.Max(1, Height >> mipLevel);

                    totalSize += Decode_Gamecube.GetDataSize((uint)platform.Format, width, height);
                }
            }

            return totalSize;
        }

        private int GetImageSize3DS()
        {
            var platform = (CTRSwizzle)Platform;

            int totalSize = 0;
            for (int arrayLevel = 0; arrayLevel < ArrayCount; arrayLevel++)
            {
                for (int mipLevel = 0; mipLevel < MipCount; mipLevel++)
                {
                    uint width = (uint)Math.Max(1, Width >> mipLevel);
                    uint height = (uint)Math.Max(1, Height >> mipLevel);
                    totalSize += CTR_3DS.CalculateLength((int)width, (int)height, platform.Format);
                }
            }
            return totalSize;
        }
    }
}
