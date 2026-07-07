using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using Toolbox.Core;

namespace GLFrameworkEngine
{
    /// <summary>
    /// Pre-deswizzles texture surfaces on background threads so the GL upload
    /// during rendering doesn't stall on the (single threaded) CPU decode.
    /// Prefetched data is consumed (and removed) by GLTexture.LoadImage.
    /// </summary>
    public static class TextureDataPrefetch
    {
        //[texture] -> [surface][mip] deswizzled data
        static readonly ConcurrentDictionary<STGenericTexture, byte[][][]> _cache =
            new ConcurrentDictionary<STGenericTexture, byte[][][]>();

        /// <summary>
        /// Deswizzles all surfaces of the given textures in parallel.
        /// </summary>
        public static void PrefetchAll(System.Collections.Generic.IEnumerable<STGenericTexture> textures)
        {
            Parallel.ForEach(textures, texture =>
            {
                try { Prefetch(texture); }
                catch { } //Fall back to the normal decode path on failure.
            });
        }

        public static void Prefetch(STGenericTexture texture)
        {
            //ASTC images go through a different decode path; skip them.
            if (texture.IsASTC())
                return;

            int numSurfaces = GetSurfaceCount(texture);
            int numMips = GetMipCount(texture);

            var mipProvider = texture as IMipSurfaceProvider;

            var surfaces = new byte[numSurfaces][][];
            for (int i = 0; i < numSurfaces; i++)
            {
                surfaces[i] = new byte[numMips][];
                for (int mip = 0; mip < numMips; mip++)
                {
                    byte[] data = null;
                    if (mipProvider != null)
                        data = mipProvider.GetMipSurface(i, mip);
                    //Only trust the generic deswizzler for the base level;
                    //its mip offset math is unreliable for mip levels.
                    if (data == null && mip == 0)
                        data = texture.GetDeswizzledSurface(i, mip);
                    surfaces[i][mip] = data;
                }
            }

            _cache[texture] = surfaces;
        }

        /// <summary>
        /// The number of mips GLTexture.LoadImage will upload for this texture.
        /// Full mip chains are only uploaded for 2D BCn textures (see LoadImage).
        /// </summary>
        public static int GetMipCount(STGenericTexture texture)
        {
            bool is2D = texture.SurfaceType != STSurfaceType.Texture3D &&
                        texture.SurfaceType != STSurfaceType.Texture2D_Array &&
                        texture.SurfaceType != STSurfaceType.TextureCube &&
                        texture.SurfaceType != STSurfaceType.TextureCube_Array;
            if (is2D && texture.IsBCNCompressed())
                return (int)Math.Max(1, texture.MipCount);
            return 1;
        }

        public static int GetSurfaceCount(STGenericTexture texture)
        {
            switch (texture.SurfaceType)
            {
                case STSurfaceType.Texture3D:
                case STSurfaceType.Texture2D_Array:
                    return (int)Math.Max(1, texture.Depth);
                case STSurfaceType.TextureCube:
                case STSurfaceType.TextureCube_Array:
                    return (int)Math.Max(1, texture.ArrayCount);
                default:
                    return 1;
            }
        }

        /// <summary>
        /// Takes all prefetched data for a texture ([surface][mip]), or null.
        /// </summary>
        internal static byte[][][] TakeAll(STGenericTexture texture)
        {
            _cache.TryRemove(texture, out var surfaces);
            return surfaces;
        }

        public static void Clear() => _cache.Clear();
    }
}
