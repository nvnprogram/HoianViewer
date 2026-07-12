using System;
using System.IO;

namespace PlayerViewer.UI
{
    /// <summary>
    /// Shared helpers for the capture/export paths: ffmpeg discovery, codec argument
    /// building (so streaming and buffered exporters stay in sync), and filename
    /// stamping. Kept static and stateless.
    /// </summary>
    public static class ExportUtil
    {
        public static string ResolveFfmpeg()
        {
            string local = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
            return File.Exists(local) ? local : "ffmpeg";
        }

        /// <summary>
        /// Builds the ffmpeg output-codec arguments for the given format. For WebP the
        /// quality knob (0-100) selects lossless vs lossy: 100 keeps the bit-exact
        /// lossless encoder (compression_level 4, q:v 75 = entropy-search effort, not
        /// quality); below 100 switches to lossy where q:v IS the visual quality.
        /// </summary>
        public static string CodecArgs(VideoRecorder.OutputFormat format, int quality, string outputPath)
        {
            if (format == VideoRecorder.OutputFormat.WebpTransparent)
            {
                if (quality >= 100)
                    return $"-c:v libwebp_anim -lossless 1 -compression_level 4 -q:v 75 -loop 0 -an \"{outputPath}\"";
                int q = Math.Clamp(quality, 0, 100);
                return $"-c:v libwebp_anim -lossless 0 -compression_level 4 -q:v {q} -loop 0 -an \"{outputPath}\"";
            }
            return $"-c:v libx264 -preset veryfast -pix_fmt yuv420p -crf 16 \"{outputPath}\"";
        }

        /// <summary>Raw-RGBA input args for a pipe of the given size and rate.</summary>
        public static string RawInputArgs(int width, int height, int fps) =>
            $"-y -f rawvideo -pixel_format rgba -video_size {width}x{height} " +
            $"-framerate {fps} -i pipe:0 -vf vflip ";

        /// <summary>Appends _&lt;unix seconds&gt; before the extension so saves never collide.</summary>
        public static string Timestamped(string baseName, string ext)
        {
            long unix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (!ext.StartsWith('.')) ext = "." + ext;
            return $"{baseName}_{unix}{ext}";
        }
    }
}
