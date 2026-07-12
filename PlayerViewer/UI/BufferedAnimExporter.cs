using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Vector3 = System.Numerics.Vector3;

namespace PlayerViewer.UI
{
    /// <summary>
    /// Multiphase animation exporter used when "trim deadspace" is on. Because the crop
    /// rectangle can't be known until every frame has been seen, frames can't be streamed
    /// straight to ffmpeg the way <see cref="VideoRecorder"/> does. Instead:
    ///
    ///   Pass 1 (capture): each transparent RGBA frame is handed to a worker thread that
    ///   appends it to a temp file on disk and expands a running content bounding box over
    ///   non-zero-alpha pixels. Only the bbox is retained in memory; the pixels live on disk.
    ///
    ///   Pass 2 (encode): once capture ends, a second worker computes the final crop rect
    ///   (bbox + margin, clamped), then streams each frame back from disk, crops it, composites
    ///   the greenscreen background for MP4 (WebP keeps alpha), and pipes it into ffmpeg.
    ///
    /// The transparent render doubles as the alpha oracle for MP4, so a single render pass
    /// serves all formats. Disk cost is the full raw animation (W*H*4*frames) but transient.
    /// </summary>
    public class BufferedAnimExporter : IDisposable
    {
        public bool IsCapturing { get; private set; }
        public bool IsEncoding { get; private set; }
        public int EncodeProgress { get; private set; }
        public int EncodeTotal { get; private set; }
        public string OutputPath { get; private set; }
        public string Error { get; private set; }

        int _width, _height, _fps;
        string _tempPath;
        FileStream _tempWrite;
        BlockingCollection<byte[]> _writeQueue;
        Thread _writer;
        Thread _encoder;

        //Content bounding box in buffer (bottom-up) coordinates; expanded by the writer.
        int _minX, _minY, _maxX, _maxY;

        public bool StartCapture(int width, int height, int fps, string outputPath)
        {
            if (IsCapturing || IsEncoding)
                return false;

            _width = width;
            _height = height;
            _fps = fps;
            OutputPath = outputPath;
            Error = null;
            EncodeProgress = 0;
            EncodeTotal = 0;
            _minX = width; _minY = height; _maxX = -1; _maxY = -1;

            try
            {
                string dir = Path.Combine(Path.GetTempPath(), "PlayerViewerExport");
                Directory.CreateDirectory(dir);
                _tempPath = Path.Combine(dir, $"anim_{Guid.NewGuid():N}.raw");
                _tempWrite = new FileStream(_tempPath, FileMode.Create, FileAccess.Write,
                    FileShare.None, 1 << 20, FileOptions.SequentialScan);
            }
            catch (Exception ex)
            {
                Error = ex.Message;
                return false;
            }

            _writeQueue = new BlockingCollection<byte[]>(8);
            _writer = new Thread(WriteLoop) { IsBackground = true, Name = "AnimExportWrite" };
            _writer.Start();
            IsCapturing = true;
            return true;
        }

        /// <summary>Queues one bottom-up transparent RGBA frame (ArrayPool-rented; ownership transfers).</summary>
        public void PushFrame(byte[] rgba)
        {
            if (!IsCapturing)
            {
                ArrayPool<byte>.Shared.Return(rgba);
                return;
            }
            try { _writeQueue.Add(rgba); }
            catch { ArrayPool<byte>.Shared.Return(rgba); }
        }

        void WriteLoop()
        {
            int frameBytes = _width * _height * 4;
            try
            {
                foreach (var buf in _writeQueue.GetConsumingEnumerable())
                {
                    ScanBbox(buf);
                    _tempWrite.Write(buf, 0, frameBytes);
                    ArrayPool<byte>.Shared.Return(buf);
                }
            }
            catch (Exception ex)
            {
                Error ??= ex.Message;
                while (_writeQueue.TryTake(out var leftover))
                    ArrayPool<byte>.Shared.Return(leftover);
            }
        }

        //Expands the bounding box to include every pixel with alpha != 0. Bails early once
        //the box already spans the whole frame (nothing left to trim).
        void ScanBbox(byte[] buf)
        {
            if (_minX == 0 && _minY == 0 && _maxX == _width - 1 && _maxY == _height - 1)
                return;

            int w = _width, h = _height;
            for (int y = 0; y < h; y++)
            {
                int row = y * w * 4;
                for (int x = 0; x < w; x++)
                {
                    if (buf[row + x * 4 + 3] != 0)
                    {
                        if (x < _minX) _minX = x;
                        if (x > _maxX) _maxX = x;
                        if (y < _minY) _minY = y;
                        if (y > _maxY) _maxY = y;
                    }
                }
            }
        }

        /// <summary>
        /// Ends capture and kicks off the encode pass on a worker thread. Returns immediately;
        /// poll <see cref="IsEncoding"/> / <see cref="EncodeProgress"/> for progress.
        /// </summary>
        public void FinishCapture(VideoRecorder.OutputFormat format, Vector3 greenColor,
            int webpQuality, int marginPx)
        {
            if (!IsCapturing)
                return;
            IsCapturing = false;

            try
            {
                _writeQueue.CompleteAdding();
                _writer.Join(15000);
                _tempWrite.Flush();
                _tempWrite.Dispose();
                _tempWrite = null;
            }
            catch (Exception ex) { Error ??= ex.Message; }

            IsEncoding = true;
            _encoder = new Thread(() => EncodeLoop(format, greenColor, webpQuality, marginPx))
            { IsBackground = true, Name = "AnimExportEncode" };
            _encoder.Start();
        }

        void EncodeLoop(VideoRecorder.OutputFormat format, Vector3 greenColor, int webpQuality, int marginPx)
        {
            int frameBytes = _width * _height * 4;
            byte[] frame = null;
            try
            {
                long total = new FileInfo(_tempPath).Length / frameBytes;
                EncodeTotal = (int)total;

                //Compute crop rect (buffer coords). Empty box (nothing drawn) = whole frame.
                int x0, y0, cw, ch;
                if (_maxX < 0)
                {
                    x0 = 0; y0 = 0; cw = _width; ch = _height;
                }
                else
                {
                    x0 = Math.Max(0, _minX - marginPx);
                    y0 = Math.Max(0, _minY - marginPx);
                    int x1 = Math.Min(_width - 1, _maxX + marginPx);
                    int y1 = Math.Min(_height - 1, _maxY + marginPx);
                    cw = x1 - x0 + 1;
                    ch = y1 - y0 + 1;
                }
                //Even dimensions: required by MP4's yuv420p, harmless for WebP. Trimming a
                //column/row keeps the crop inside the original frame.
                cw &= ~1; ch &= ~1;
                if (cw < 2) cw = 2;
                if (ch < 2) ch = 2;

                var psi = new ProcessStartInfo
                {
                    FileName = ExportUtil.ResolveFfmpeg(),
                    Arguments = ExportUtil.RawInputArgs(cw, ch, _fps) +
                                ExportUtil.CodecArgs(format, webpQuality, OutputPath),
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                using var proc = Process.Start(psi);
                proc.BeginErrorReadLine();
                var stdin = proc.StandardInput.BaseStream;

                bool mp4 = format != VideoRecorder.OutputFormat.WebpTransparent;
                byte gr = (byte)(Math.Clamp(greenColor.X, 0f, 1f) * 255);
                byte gg = (byte)(Math.Clamp(greenColor.Y, 0f, 1f) * 255);
                byte gb = (byte)(Math.Clamp(greenColor.Z, 0f, 1f) * 255);

                frame = ArrayPool<byte>.Shared.Rent(frameBytes);
                var outBuf = new byte[cw * ch * 4];

                using var read = new FileStream(_tempPath, FileMode.Open, FileAccess.Read,
                    FileShare.None, 1 << 20, FileOptions.SequentialScan);

                for (long f = 0; f < total; f++)
                {
                    ReadFull(read, frame, frameBytes);
                    for (int ry = 0; ry < ch; ry++)
                    {
                        int srcRow = ((y0 + ry) * _width + x0) * 4;
                        int dstRow = ry * cw * 4;
                        if (mp4)
                        {
                            //Composite straight-alpha source over the greenscreen background.
                            for (int rx = 0; rx < cw; rx++)
                            {
                                int s = srcRow + rx * 4, d = dstRow + rx * 4;
                                byte a = frame[s + 3];
                                int ia = 255 - a;
                                outBuf[d] = (byte)((frame[s] * a + gr * ia) / 255);
                                outBuf[d + 1] = (byte)((frame[s + 1] * a + gg * ia) / 255);
                                outBuf[d + 2] = (byte)((frame[s + 2] * a + gb * ia) / 255);
                                outBuf[d + 3] = 255;
                            }
                        }
                        else
                        {
                            Buffer.BlockCopy(frame, srcRow, outBuf, dstRow, cw * 4);
                        }
                    }
                    stdin.Write(outBuf, 0, outBuf.Length);
                    EncodeProgress = (int)(f + 1);
                }

                stdin.Flush();
                stdin.Close();
                proc.WaitForExit(30000);
            }
            catch (Exception ex)
            {
                Error ??= ex.Message;
            }
            finally
            {
                if (frame != null)
                    ArrayPool<byte>.Shared.Return(frame);
                TryDeleteTemp();
                IsEncoding = false;
            }
        }

        static void ReadFull(Stream s, byte[] buf, int count)
        {
            int read = 0;
            while (read < count)
            {
                int n = s.Read(buf, read, count - read);
                if (n <= 0) throw new EndOfStreamException();
                read += n;
            }
        }

        /// <summary>Aborts capture without encoding (cancel button). Safe if idle.</summary>
        public void Abort()
        {
            if (IsCapturing)
            {
                IsCapturing = false;
                try
                {
                    _writeQueue.CompleteAdding();
                    _writer?.Join(5000);
                    _tempWrite?.Dispose();
                    _tempWrite = null;
                }
                catch { }
                TryDeleteTemp();
            }
        }

        void TryDeleteTemp()
        {
            try { if (_tempPath != null && File.Exists(_tempPath)) File.Delete(_tempPath); }
            catch { }
        }

        public void Dispose()
        {
            Abort();
            _writeQueue?.Dispose();
            _writeQueue = null;
        }
    }
}
