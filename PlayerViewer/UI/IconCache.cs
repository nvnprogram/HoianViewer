using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using OpenTK.Graphics.OpenGL;
using PlayerViewer.Icons;

namespace PlayerViewer.UI
{
    /// <summary>An uploaded icon, as ImGui draws it.</summary>
    public readonly struct IconTexture
    {
        public readonly IntPtr Id;
        public readonly int Width;
        public readonly int Height;

        public IconTexture(int id, int width, int height)
        {
            Id = (IntPtr)id;
            Width = width;
            Height = height;
        }
    }

    /// <summary>
    /// GL textures for the icons, decoded on first request. The decode runs on one worker
    /// thread, newest request first so whatever is on screen now comes before what scrolled
    /// past; the upload happens on the render thread in <see cref="Pump"/>.
    /// </summary>
    public sealed class IconCache : IDisposable
    {
        //Enough to fill a freshly opened grid within a few frames without one frame paying
        //for all of it.
        const int UploadsPerFrame = 24;

        readonly IconSource _source;
        readonly Dictionary<string, IconTexture> _ready = new(StringComparer.Ordinal);
        readonly HashSet<string> _missing = new(StringComparer.Ordinal);
        readonly HashSet<string> _requested = new(StringComparer.Ordinal);
        readonly List<string> _queue = new();
        readonly ConcurrentQueue<(string Key, IconImage Image)> _done = new();
        readonly SemaphoreSlim _signal = new(0);
        readonly Thread _worker;
        volatile bool _stopping;

        public IconCache(IconSource source)
        {
            _source = source;
            _worker = new Thread(Work)
            {
                IsBackground = true,
                Name = "Icon decode",
                Priority = ThreadPriority.BelowNormal,
            };
            _worker.Start();
        }

        /// <summary>
        /// The icon for a key when it is uploaded; otherwise queues it and answers false,
        /// which a caller draws as an empty slot until a later frame has it.
        /// </summary>
        public bool TryGet(string key, out IconTexture icon)
        {
            icon = default;
            if (key == null || _stopping)
                return false;
            if (_ready.TryGetValue(key, out icon))
                return true;
            if (_missing.Contains(key))
                return false;
            lock (_queue)
            {
                if (_requested.Add(key))
                {
                    _queue.Add(key);
                    _signal.Release();
                }
                else
                {
                    //Asked again while still waiting: move it to the front.
                    int at = _queue.IndexOf(key);
                    if (at >= 0 && at != _queue.Count - 1)
                    {
                        _queue.RemoveAt(at);
                        _queue.Add(key);
                    }
                }
            }
            return false;
        }

        /// <summary>Whether the romfs has no icon for the key, known once it has been tried.</summary>
        public bool IsMissing(string key) => key == null || _missing.Contains(key);

        void Work()
        {
            while (!_stopping)
            {
                _signal.Wait();
                if (_stopping)
                    return;
                string key;
                lock (_queue)
                {
                    if (_queue.Count == 0)
                        continue;
                    key = _queue[^1];
                    _queue.RemoveAt(_queue.Count - 1);
                }
                IconImage image = null;
                try
                {
                    image = _source.Load(key);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Icons] {key}: {ex.Message}");
                }
                _done.Enqueue((key, image));
            }
        }

        /// <summary>Uploads what the worker has finished. Render thread, once per frame.</summary>
        public void Pump()
        {
            for (int i = 0; i < UploadsPerFrame && _done.TryDequeue(out var item); i++)
            {
                lock (_queue)
                    _requested.Remove(item.Key);
                if (item.Image == null)
                {
                    _missing.Add(item.Key);
                    continue;
                }
                _ready[item.Key] = Upload(item.Image);
            }
        }

        static IconTexture Upload(IconImage image) =>
            new(
                GlTextures.UploadRgba(
                    image.Rgba,
                    image.Width,
                    image.Height,
                    mips: true,
                    TextureWrapMode.ClampToEdge
                ),
                image.Width,
                image.Height
            );

        /// <summary>The largest rectangle with the icon's aspect that fits a box, centred in it.</summary>
        public static (Vector2 Min, Vector2 Max) Fit(IconTexture icon, Vector2 min, Vector2 size)
        {
            float scale = MathF.Min(size.X / icon.Width, size.Y / icon.Height);
            var fitted = new Vector2(icon.Width, icon.Height) * scale;
            var at = min + (size - fitted) * 0.5f;
            return (at, at + fitted);
        }

        /// <summary>Stops the worker and deletes every texture. Needs the GL context current.</summary>
        public void Dispose()
        {
            _stopping = true;
            _signal.Release();
            _worker.Join(2000);
            foreach (var icon in _ready.Values)
                GL.DeleteTexture((int)icon.Id);
            _ready.Clear();
            _signal.Dispose();
        }
    }
}
