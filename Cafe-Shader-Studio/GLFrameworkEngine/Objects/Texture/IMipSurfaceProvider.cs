namespace GLFrameworkEngine
{
    /// <summary>
    /// Provides direct access to deswizzled mip level surfaces.
    /// Used to upload real mip chains instead of GL.GenerateMipmap,
    /// bypassing Toolbox.Core's unreliable mip offset recomputation.
    /// </summary>
    public interface IMipSurfaceProvider
    {
        byte[] GetMipSurface(int arrayLevel, int mipLevel);
    }
}
