using System;
using System.Diagnostics;
using System.IO;
using PlayerViewer.Core;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace PlayerViewer.UI
{
    public enum OutputFormat
    {
        /// <summary>H.264 MP4, opaque (yuv420p). Alpha is discarded.</summary>
        Mp4,

        /// <summary>Animated WebP that preserves the RGBA alpha channel.</summary>
        WebpTransparent,

        /// <summary>VP9 WebM that preserves the alpha channel (yuva420p).</summary>
        WebmTransparent,
    }

    /// <summary>
    /// Shared helpers for the export path: ffmpeg discovery/probe, codec argument
    /// building, and filename stamping. Kept static and stateless.
    /// </summary>
    public static class ExportUtil
    {
        //Prefer an ffmpeg dropped in the per-user data folder; otherwise fall back to PATH.
        public static string ResolveFfmpeg()
        {
            string name = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
            string local = Path.Combine(AppPaths.DataDir, name);
            return File.Exists(local) ? local : "ffmpeg";
        }

        static bool? _ffmpegAvailable;
        public static bool FfmpegAvailable
        {
            get
            {
                if (_ffmpegAvailable == null)
                {
                    try
                    {
                        using var probe = Process.Start(
                            new ProcessStartInfo
                            {
                                FileName = ResolveFfmpeg(),
                                Arguments = "-version",
                                UseShellExecute = false,
                                RedirectStandardOutput = true,
                                RedirectStandardError = true,
                                CreateNoWindow = true,
                            }
                        );
                        probe.WaitForExit(5000);
                        _ffmpegAvailable = true;
                    }
                    catch
                    {
                        _ffmpegAvailable = false;
                    }
                }
                return _ffmpegAvailable.Value;
            }
        }

        /// <summary>
        /// Builds the ffmpeg output-codec arguments for the given format. For WebP the
        /// quality knob (0-100) selects lossless vs lossy: 100 keeps the bit-exact
        /// lossless encoder (compression_level 4, q:v 75 = entropy-search effort, not
        /// quality); below 100 switches to lossy where q:v IS the visual quality.
        /// </summary>
        public static string CodecArgs(OutputFormat format, int quality, string outputPath)
        {
            if (format == OutputFormat.WebpTransparent)
            {
                if (quality >= 100)
                    return $"-c:v libwebp_anim -lossless 1 -compression_level 4 -q:v 75 -loop 0 -an \"{outputPath}\"";
                int q = Math.Clamp(quality, 0, 100);
                return $"-c:v libwebp_anim -lossless 0 -compression_level 4 -q:v {q} -loop 0 -an \"{outputPath}\"";
            }
            if (format == OutputFormat.WebmTransparent)
            {
                //VP9 with an alpha plane (yuva420p). quality 100 = lossless; below maps to a VP9
                //CRF (0 best .. 63 worst). cpu-used/deadline trade encode speed for size.
                if (quality >= 100)
                    return $"-c:v libvpx-vp9 -pix_fmt yuva420p -lossless 1 -cpu-used 4 -deadline good -an \"{outputPath}\"";
                int crf = Math.Clamp((100 - quality) * 63 / 100, 0, 63);
                return $"-c:v libvpx-vp9 -pix_fmt yuva420p -b:v 0 -crf {crf} -cpu-used 4 -deadline good -an \"{outputPath}\"";
            }
            return $"-c:v libx264 -preset veryfast -pix_fmt yuv420p -crf 16 \"{outputPath}\"";
        }

        /// <summary>Raw-RGBA input args for a pipe of the given size and rate.</summary>
        public static string RawInputArgs(int width, int height, int fps) =>
            $"-y -f rawvideo -pixel_format rgba -video_size {width}x{height} "
            + $"-framerate {fps} -i pipe:0 -vf vflip ";

        /// <summary>
        /// Builds a full-frame straight-RGBA background buffer for compositing at export time:
        /// Color = solid fill, Transparent = opaque black (only used as the MP4 no-alpha
        /// fallback; callers pass null when the format keeps alpha), Image = the imported image
        /// scaled (Fill/Fit/Stretch + zoom + offset) or tiled. bottomUp flips the vertical sample
        /// so a video buffer (composited before ffmpeg's -vf vflip) reads upright.
        /// </summary>
        public static byte[] BuildBackground(int fw, int fh, BackgroundConfig cfg, bool bottomUp)
        {
            if (fw <= 0 || fh <= 0)
                return null;
            var buf = new byte[fw * fh * 4];

            void Fill()
            {
                byte r = (byte)(Math.Clamp(cfg.Color[0], 0f, 1f) * 255);
                byte g = (byte)(Math.Clamp(cfg.Color[1], 0f, 1f) * 255);
                byte b = (byte)(Math.Clamp(cfg.Color[2], 0f, 1f) * 255);
                for (int i = 0; i < buf.Length; i += 4)
                {
                    buf[i] = r;
                    buf[i + 1] = g;
                    buf[i + 2] = b;
                    buf[i + 3] = 255;
                }
            }

            if (cfg.Mode == 1)
            {
                Fill();
                return buf;
            } //Color
            if (cfg.Mode != 2) //Transparent -> black
            {
                for (int i = 3; i < buf.Length; i += 4)
                    buf[i] = 255;
                return buf;
            }

            //Image mode. A bad/missing path falls back to the solid color so export never crashes.
            if (!TryDecodeBackground(cfg.ImagePath, out byte[] src, out int iw, out int ih))
            {
                Fill();
                return buf;
            }

            float zoom = Math.Max(cfg.Zoom, 1e-4f);

            //Placement/scale is constant across the whole frame; hoist it out of the pixel loops.
            int nx = Math.Max(cfg.TileX, 1),
                ny = Math.Max(cfg.TileY, 1);
            float offXfw = cfg.OffsetX * fw,
                offYfh = cfg.OffsetY * fh;
            float sx,
                sy;
            if (cfg.ScaleMode == 2)
            {
                sx = (float)fw / iw * zoom;
                sy = (float)fh / ih * zoom;
            }
            else
            {
                float s =
                    cfg.ScaleMode == 1
                        ? MathF.Min((float)fw / iw, (float)fh / ih) //Fit
                        : MathF.Max((float)fw / iw, (float)fh / ih); //Fill
                sx = sy = s * zoom;
            }
            float dispW = iw * sx,
                dispH = ih * sy;
            float originX = (fw - dispW) * 0.5f + offXfw;
            float originY = (fh - dispH) * 0.5f + offYfh;

            for (int dy = 0; dy < fh; dy++)
            {
                int dyEff = bottomUp ? fh - 1 - dy : dy;
                //v is constant across the row.
                float v;
                if (cfg.Tile)
                {
                    v = ((dyEff - offYfh) / fh) * ny / zoom;
                    v -= MathF.Floor(v);
                }
                else
                {
                    v = (dyEff - originY) / dispH;
                }

                for (int dx = 0; dx < fw; dx++)
                {
                    float u;
                    if (cfg.Tile)
                    {
                        u = ((dx - offXfw) / fw) * nx / zoom;
                        u -= MathF.Floor(u);
                    }
                    else
                    {
                        u = (dx - originX) / dispW;
                        if (u < 0 || u >= 1 || v < 0 || v >= 1)
                            continue; //off-frame / Fit letterbox stays transparent (0,0,0,0)
                    }
                    int sxp = Math.Clamp((int)(u * iw), 0, iw - 1);
                    int syp = Math.Clamp((int)(v * ih), 0, ih - 1);
                    int si = (syp * iw + sxp) * 4,
                        d = (dy * fw + dx) * 4;
                    buf[d] = src[si];
                    buf[d + 1] = src[si + 1];
                    buf[d + 2] = src[si + 2];
                    buf[d + 3] = src[si + 3];
                }
            }
            return buf;
        }

        //Decodes the background image to tightly-packed top-down RGBA, cached by path + write time.
        static string _bgPath;
        static long _bgStamp;
        static byte[] _bgRgba;
        static int _bgW,
            _bgH;

        static bool TryDecodeBackground(string path, out byte[] rgba, out int w, out int h)
        {
            rgba = null;
            w = h = 0;
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                    return false;
                long stamp = File.GetLastWriteTimeUtc(path).Ticks;
                if (_bgRgba == null || _bgPath != path || _bgStamp != stamp)
                {
                    using var img = Image.Load<Rgba32>(path);
                    var pixels = new byte[img.Width * img.Height * 4];
                    img.CopyPixelDataTo(pixels);
                    _bgRgba = pixels;
                    _bgW = img.Width;
                    _bgH = img.Height;
                    _bgPath = path;
                    _bgStamp = stamp;
                }
                if (_bgW <= 0 || _bgH <= 0)
                    return false;
                rgba = _bgRgba;
                w = _bgW;
                h = _bgH;
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Appends _&lt;unix seconds&gt; before the extension so saves never collide.</summary>
        public static string Timestamped(string baseName, string ext)
        {
            long unix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (!ext.StartsWith('.'))
                ext = "." + ext;
            return $"{baseName}_{unix}{ext}";
        }
    }
}
