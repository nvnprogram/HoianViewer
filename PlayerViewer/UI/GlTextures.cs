using OpenTK.Graphics.OpenGL;

namespace PlayerViewer.UI
{
    /// <summary>Texture uploads for the interface's own images.</summary>
    static class GlTextures
    {
        /// <summary>
        /// An RGBA8 texture from tightly packed pixels, filtered linearly, with mipmaps when asked.
        /// Returns the texture id.
        /// </summary>
        public static int UploadRgba<T>(
            T[] pixels,
            int width,
            int height,
            bool mips,
            TextureWrapMode wrap
        )
            where T : struct
        {
            int id = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, id);
            GL.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
            GL.TexImage2D(
                TextureTarget.Texture2D,
                0,
                PixelInternalFormat.Rgba8,
                width,
                height,
                0,
                PixelFormat.Rgba,
                PixelType.UnsignedByte,
                pixels
            );
            GL.PixelStore(PixelStoreParameter.UnpackAlignment, 4);
            if (mips)
                GL.GenerateMipmap(GenerateMipmapTarget.Texture2D);
            GL.TexParameter(
                TextureTarget.Texture2D,
                TextureParameterName.TextureMinFilter,
                (int)(mips ? TextureMinFilter.LinearMipmapLinear : TextureMinFilter.Linear)
            );
            GL.TexParameter(
                TextureTarget.Texture2D,
                TextureParameterName.TextureMagFilter,
                (int)TextureMagFilter.Linear
            );
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)wrap);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)wrap);
            GL.BindTexture(TextureTarget.Texture2D, 0);
            return id;
        }
    }
}
