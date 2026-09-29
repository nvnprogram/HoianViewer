using System.IO;

namespace PlayerViewer.Core
{
    /// <summary>Writes a file beside its target and moves it over, so a failed write leaves the old file whole.</summary>
    public static class AtomicFile
    {
        /// <summary>
        /// Writes the bytes, zstd compressed at level 16 as the romfs keeps its files when
        /// <paramref name="compress"/> is set, to <c>path.tmp</c>, then moves that over the path.
        /// </summary>
        public static void Write(string path, byte[] data, bool compress = false)
        {
            if (compress)
            {
                using var compressor = new ZstdSharp.Compressor(16);
                data = compressor.Wrap(data).ToArray();
            }
            string temp = path + ".tmp";
            try
            {
                File.WriteAllBytes(temp, data);
                File.Move(temp, path, true);
            }
            finally
            {
                if (File.Exists(temp))
                    File.Delete(temp);
            }
        }
    }
}
