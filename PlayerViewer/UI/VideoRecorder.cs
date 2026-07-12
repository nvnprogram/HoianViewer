using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace PlayerViewer.UI
{
    /// <summary>
    /// Records viewport frames to a video by piping raw RGBA into ffmpeg.
    /// Encoding runs on a worker thread with a bounded frame queue so pipe
    /// writes never block the render thread.
    /// </summary>
    public class VideoRecorder : IDisposable
    {
        public enum OutputFormat
        {
            /// <summary>H.264 MP4, opaque (yuv420p). Alpha is discarded.</summary>
            Mp4,
            /// <summary>Lossless animated WebP that preserves the RGBA alpha channel.</summary>
            WebpTransparent,
        }

        const int MaxQueuedFrames = 30;

        readonly record struct CapturedFrame(byte[] Pixels, long Slot);

        Process _ffmpeg;
        Stream _stdin;
        Thread _worker;
        BlockingCollection<CapturedFrame> _queue;
        int _width, _height;
        int _fps;
        int _droppedFrames;
        Stopwatch _recClock;
        long _lastCapturedSlot;

        //Lockstep mode: 1 PushFrame = 1 output frame per PushFrame call,
        // with no drops and deterministic timing.
        bool _lockstep;
        long _lockstepSlot;

        public bool IsRecording { get; private set; }
        public int FrameCount { get; private set; }
        public string OutputPath { get; private set; }

        static string ResolveFfmpeg() => ExportUtil.ResolveFfmpeg();

        static bool? _ffmpegAvailable;
        public static bool FfmpegAvailable
        {
            get
            {
                if (_ffmpegAvailable == null)
                {
                    try
                    {
                        using var probe = Process.Start(new ProcessStartInfo
                        {
                            FileName = ResolveFfmpeg(),
                            Arguments = "-version",
                            UseShellExecute = false,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            CreateNoWindow = true,
                        });
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

        public bool Start(int width, int height, string outputPath, int fps = 60,
            bool lockstep = false, OutputFormat format = OutputFormat.Mp4, int webpQuality = 100)
        {
            if (IsRecording || !FfmpegAvailable)
                return false;

            _width = width & ~1;
            _height = height & ~1;
            OutputPath = outputPath;
            FrameCount = 0;
            _droppedFrames = 0;
            _lockstep = lockstep;
            _lockstepSlot = 0;

            //All formats consume the same raw RGBA stream; only the output codec
            //differs. WebP keeps the alpha channel for a transparent background;
            //MP4 flattens it. -vf vflip corrects OpenGL's bottom-up row order.
            //Codec args (incl. the lossless/lossy WebP split) live in ExportUtil so the
            //streaming and buffered-trim exporters stay in sync.
            string input = ExportUtil.RawInputArgs(_width, _height, fps);
            string codec = ExportUtil.CodecArgs(format, webpQuality, outputPath);

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = ResolveFfmpeg(),
                    Arguments = input + codec,
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                _ffmpeg = Process.Start(psi);
                _ffmpeg.BeginErrorReadLine();
                _stdin = _ffmpeg.StandardInput.BaseStream;
            }
            catch
            {
                _ffmpeg = null;
                _stdin = null;
                _ffmpegAvailable = false;
                return false;
            }

            _fps = fps;
            _queue = new BlockingCollection<CapturedFrame>(MaxQueuedFrames);
            _worker = new Thread(EncodeLoop) { IsBackground = true, Name = "VideoEncode" };
            _worker.Start();

            _recClock = Stopwatch.StartNew();
            _lastCapturedSlot = -1;
            IsRecording = true;
            return true;
        }

        /// <summary>
        /// Call BEFORE doing the GPU readback. Returns true when the next
        /// timeline slot is due, meaning the caller should proceed with
        /// <see cref="ScenePipeline.ReadFinalPixelsAsync"/> and then
        /// <see cref="PushFrame"/>. When false, skip the readback entirely.
        /// </summary>
        public bool IsCaptureDue()
        {
            if (!IsRecording)
                return false;
            // If in lockstep, always capture the next frame for output
            if (_lockstep)
                return true;

            long slot = (long)Math.Floor(_recClock.Elapsed.TotalSeconds * _fps);
            return slot > _lastCapturedSlot;
        }

        void EncodeLoop()
        {
            int frameBytes = _width * _height * 4;
            byte[] previous = null;
            long nextOutputSlot = 0;
            try
            {
                foreach (var frame in _queue.GetConsumingEnumerable())
                {
                    while (previous != null && nextOutputSlot < frame.Slot)
                    {
                        _stdin.Write(previous, 0, frameBytes);
                        nextOutputSlot++;
                    }

                    _stdin.Write(frame.Pixels, 0, frameBytes);
                    nextOutputSlot = frame.Slot + 1;

                    if (previous != null)
                        ArrayPool<byte>.Shared.Return(previous);
                    previous = frame.Pixels;
                }

                if (previous != null)
                    ArrayPool<byte>.Shared.Return(previous);
            }
            catch
            {
                if (previous != null)
                    ArrayPool<byte>.Shared.Return(previous);
                while (_queue.TryTake(out var leftover))
                    ArrayPool<byte>.Shared.Return(leftover.Pixels);
            }
        }

        /// <summary>
        /// Queues one bottom-up RGBA frame. The buffer must come from
        /// ArrayPool; ownership transfers to the encoder thread.
        /// </summary>
        public void PushFrame(byte[] rgba, int width, int height)
        {
            if (!IsRecording)
            {
                ArrayPool<byte>.Shared.Return(rgba);
                return;
            }
            if ((width & ~1) != _width || (height & ~1) != _height)
            {
                ArrayPool<byte>.Shared.Return(rgba);
                return;
            }

            if (_lockstep)
            {
                //Never drop; block until the encoder drains a slot
                try
                {
                    _queue.Add(new CapturedFrame(rgba, _lockstepSlot++));
                    FrameCount++;
                }
                catch
                {
                    ArrayPool<byte>.Shared.Return(rgba);
                }
                return;
            }

            long slot = (long)Math.Floor(_recClock.Elapsed.TotalSeconds * _fps);
            if (slot <= _lastCapturedSlot)
            {
                ArrayPool<byte>.Shared.Return(rgba);
                return;
            }
            _lastCapturedSlot = slot;

            if (_queue.TryAdd(new CapturedFrame(rgba, slot)))
                FrameCount++;
            else
            {
                ArrayPool<byte>.Shared.Return(rgba);
                _droppedFrames++;
            }
        }

        public void Stop()
        {
            if (!IsRecording)
                return;
            IsRecording = false;

            try
            {
                _queue.CompleteAdding();
                _worker.Join(15000);
                _stdin?.Flush();
                _stdin?.Close();
                _ffmpeg?.WaitForExit(10000);
            }
            catch { }
            if (_droppedFrames > 0)
                Console.WriteLine($"[VideoRecorder] Encoder fell behind; dropped {_droppedFrames} frame(s).");
            _queue?.Dispose();
            _queue = null;
            _worker = null;
            _ffmpeg?.Dispose();
            _ffmpeg = null;
            _stdin = null;
        }

        public void Dispose() => Stop();
    }
}
