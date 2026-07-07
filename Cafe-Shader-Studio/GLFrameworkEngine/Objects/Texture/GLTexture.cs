using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using OpenTK.Graphics.OpenGL;
using Toolbox.Core;

namespace GLFrameworkEngine
{
    public class GLTexture : GLObject, IFramebufferAttachment, IRenderableTexture
    {
        public string Name { get; set; }

        public TextureTarget Target { get; set; }

        public TextureMagFilter MagFilter { get; set; }
        public TextureMinFilter MinFilter { get; set; }

        public TextureWrapMode WrapS { get; set; }
        public TextureWrapMode WrapT { get; set; }
        public TextureWrapMode WrapR { get; set; }

        public int Width { get; set; }
        public int Height { get; set; }
        public int MipCount
        {
            get { return _mipCount; }
            set { _mipCount = Math.Max(value, 1); }
        }
        private int _mipCount = 1;

        public int ArrayCount
        {
            get { return _arrayCount; }
            set { _arrayCount = Math.Max(value, 1); }
        }
        private int _arrayCount = 1;

        public PixelInternalFormat PixelInternalFormat { get; internal set; }
        public PixelFormat PixelFormat { get; internal set; }
        public PixelType PixelType { get; internal set; }

        public static readonly System.Diagnostics.Stopwatch CtorTime = new System.Diagnostics.Stopwatch();
        public static readonly System.Diagnostics.Stopwatch GenTime = new System.Diagnostics.Stopwatch();
        public static readonly System.Diagnostics.Stopwatch LoadImageTime = new System.Diagnostics.Stopwatch();

        public GLTexture() : base(CreateTextureTimed())
        {
            CtorTime.Start();
            Target = TextureTarget.Texture2D;
            WrapS = TextureWrapMode.ClampToEdge;
            WrapT = TextureWrapMode.ClampToEdge;
            WrapR = TextureWrapMode.ClampToEdge;
            MinFilter = TextureMinFilter.Linear;
            MagFilter = TextureMagFilter.Linear;
            PixelInternalFormat = PixelInternalFormat.Rgba;

            //NOTE: no GL.TexParameter here. GL.TexParameter affects the texture
            //bound to Target (not this new id), and binding here would lock the
            //object to Texture2D before subclasses set their real Target.
            //Parameters are applied in LoadImage/Create* which bind properly.
            CtorTime.Stop();
        }

        static int CreateTextureTimed()
        {
            GenTime.Start();
            int id = GL.GenTexture();
            GenTime.Stop();
            return id;
        }

        public void GenerateMipmaps() {
            Bind();
            GL.GenerateMipmap((GenerateMipmapTarget)Target);
        }

        public void Bind() {
            GL.BindTexture(Target, ID);
        }

        public void Unbind() {
            GL.BindTexture(Target, 0);
        }

        public void Dispose() {
            GL.DeleteTexture(ID);
        }

        public virtual void Attach(FramebufferAttachment attachment, Framebuffer target) {
            target.Bind();
            GL.FramebufferTexture(target.Target, attachment, ID, 0);
        }

        public void UpdateParameters()
        {
            GL.TexParameter(Target, TextureParameterName.TextureMagFilter, (int)MagFilter);
            GL.TexParameter(Target, TextureParameterName.TextureMinFilter, (int)MinFilter);
            GL.TexParameter(Target, TextureParameterName.TextureWrapS, (int)WrapS);
            GL.TexParameter(Target, TextureParameterName.TextureWrapT, (int)WrapT);
            GL.TexParameter(Target, TextureParameterName.TextureWrapR, (int)WrapR);
        }

        public virtual void SaveDDS(string fileName)
        {

        }

        public static GLTexture FromGenericTexture(STGenericTexture texture, ImageParameters parameters = null)
        {
            if (parameters == null) parameters = new ImageParameters();

            switch (texture.SurfaceType)
            {
                case STSurfaceType.Texture2D_Array:
                 return GLTexture2DArray.FromGeneric(texture, parameters);
                case STSurfaceType.Texture3D:
                    return GLTexture3D.FromGeneric(texture, parameters);
                case STSurfaceType.TextureCube:
                    return GLTextureCube.FromGeneric(texture, parameters);
                default:
                    return GLTexture2D.FromGeneric(texture, parameters);
            }
        }

        public static readonly System.Diagnostics.Stopwatch DecodeTime = new System.Diagnostics.Stopwatch();
        public static readonly System.Diagnostics.Stopwatch UploadTime = new System.Diagnostics.Stopwatch();
        public static readonly System.Diagnostics.Stopwatch MipmapTime = new System.Diagnostics.Stopwatch();

        public void LoadImage(STGenericTexture texture, ImageParameters parameters)
        {
            LoadImageTime.Start();
            try { LoadImageInternal(texture, parameters); }
            finally { LoadImageTime.Stop(); }
        }

        void LoadImageInternal(STGenericTexture texture, ImageParameters parameters)
        {
            if (parameters == null) parameters = new ImageParameters();

            Bind();

            var format = texture.Platform.OutputFormat;
            var width = CalculateMipDimension((int)texture.Width, 0);
            var height = CalculateMipDimension((int)texture.Height, 0);

            int numSurfaces = 1;
            if (Target == TextureTarget.Texture3D || Target == TextureTarget.Texture2DArray) {
                numSurfaces = (int)Math.Max(1, texture.Depth);
            }
            if (Target == TextureTarget.TextureCubeMap || Target == TextureTarget.TextureCubeMapArray) {
                numSurfaces = (int)Math.Max(1, texture.ArrayCount);
            }

            var prefetched = TextureDataPrefetch.TakeAll(texture);

            for (int i = 0; i < numSurfaces; i++)
            {
                int depth = 1;

                bool loadAsBitmap = !IsPower2(width, height) && texture.IsBCNCompressed() && false;
                if (texture.IsASTC() || parameters.FlipY)
                    loadAsBitmap = true;

                //Upload the file's real mip chain for BC compressed 2D textures.
                //GL.GenerateMipmap on compressed formats is extremely slow in drivers
                //(decompress + downscale + recompress per texture).
                //Requires an IMipSurfaceProvider; Toolbox's generic mip deswizzle
                //recomputes offsets incorrectly and returns corrupt mip data.
                bool uploadFullMipChain = Target == TextureTarget.Texture2D &&
                    texture.IsBCNCompressed() && !loadAsBitmap &&
                    !parameters.UseSoftwareDecoder && texture.MipCount > 1 &&
                    format != TexFormat.BC5_SNORM &&
                    texture is IMipSurfaceProvider;

                int numMips = uploadFullMipChain ? (int)texture.MipCount : 1;
                int uploadedMips = 0;

                for (int mipLevel = 0; mipLevel < numMips; mipLevel++)
                {
                    int mipWidth = Math.Max(1, width >> mipLevel);
                    int mipHeight = Math.Max(1, height >> mipLevel);

                    DecodeTime.Start();
                    byte[] surface = null;
                    if (prefetched != null && i < prefetched.Length && mipLevel < prefetched[i].Length)
                        surface = prefetched[i][mipLevel];
                    if (surface == null && !loadAsBitmap && !parameters.UseSoftwareDecoder)
                    {
                        if (uploadFullMipChain)
                            surface = ((IMipSurfaceProvider)texture).GetMipSurface(i, mipLevel);
                        if (surface == null && mipLevel == 0)
                            surface = texture.GetDeswizzledSurface(i, mipLevel);
                    }
                    DecodeTime.Stop();

                    //Mip data unavailable; stop and cap the mip range at what we uploaded.
                    if (surface == null && mipLevel > 0)
                        break;

                    UploadTime.Start();
                    if (loadAsBitmap || parameters.UseSoftwareDecoder)
                    {
                        var rgbaData = texture.GetDecodedSurface(i, mipLevel);
                        if (parameters.FlipY)
                            rgbaData = FlipVertical(width, height, rgbaData);

                        var formatInfo = GLFormatHelper.ConvertPixelFormat(TexFormat.RGBA8_UNORM);
                        if (texture.IsSRGB) formatInfo.InternalFormat = PixelInternalFormat.Srgb8Alpha8;

                        GLTextureDataLoader.LoadImage(Target, width, height, depth, formatInfo, rgbaData, mipLevel);
                    }
                    else if (texture.IsBCNCompressed())
                    {
                        var internalFormat = GLFormatHelper.ConvertCompressedFormat(format, true);
                        GLTextureDataLoader.LoadCompressedImage(Target, mipWidth, mipHeight, depth, internalFormat, surface, mipLevel);
                    }
                    else
                    {
                        var formatInfo = GLFormatHelper.ConvertPixelFormat(format);
                        GLTextureDataLoader.LoadImage(Target, width, height, depth, formatInfo, surface, mipLevel);
                    }
                    UploadTime.Stop();
                    uploadedMips++;
                }

                MipmapTime.Start();
                if (uploadFullMipChain)
                {
                    GL.TexParameter(Target, TextureParameterName.TextureBaseLevel, 0);
                    GL.TexParameter(Target, TextureParameterName.TextureMaxLevel, uploadedMips - 1);
                }
                else if (texture.MipCount > 1 && texture.Platform.OutputFormat != TexFormat.BC5_SNORM)
                    GL.GenerateMipmap((GenerateMipmapTarget)Target);
                else
                {
                    //Set level to base only
                    GL.TexParameter(Target, TextureParameterName.TextureBaseLevel, 0);
                    GL.TexParameter(Target, TextureParameterName.TextureMaxLevel, 0);
                }
                MipmapTime.Stop();
            }

            Unbind();
        }

        public static bool IsPower2(int width, int height) {
            return IsPow2(width) && IsPow2(height);
        }

        public virtual System.Drawing.Bitmap ToBitmap(bool saveAlpha = false)
        {
            return null;
        }

        internal static int CalculateMipDimension(int baseLevelDimension, int mipLevel) {
            return baseLevelDimension / (int)Math.Pow(2, mipLevel);
        }

        internal static bool IsPow2(int Value){
            return Value != 0 && (Value & (Value - 1)) == 0;
        }

        private static byte[] FlipVertical(int Width, int Height, byte[] Input)
        {
            byte[] FlippedOutput = new byte[Width * Height * 4];

            int Stride = Width * 4;
            for (int Y = 0; Y < Height; Y++)
            {
                int IOffs = Stride * Y;
                int OOffs = Stride * (Height - 1 - Y);

                for (int X = 0; X < Width; X++)
                {
                    FlippedOutput[OOffs + 0] = Input[IOffs + 0];
                    FlippedOutput[OOffs + 1] = Input[IOffs + 1];
                    FlippedOutput[OOffs + 2] = Input[IOffs + 2];
                    FlippedOutput[OOffs + 3] = Input[IOffs + 3];

                    IOffs += 4;
                    OOffs += 4;
                }
            }
            return FlippedOutput;
        }
    }
}
