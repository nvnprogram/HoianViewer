using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BfresEditor;
using GLFrameworkEngine;
using PlayerViewer.Core;
using Toolbox.Core;

namespace PlayerViewer.Player
{
    /// <summary>
    /// Body skin hiding driven by equipped gear (the game's PlayerAlphaMaskMgr):
    /// gear RSDB rows name a mask texture inside Model/GearAlphaMask.bfres
    /// (AlphaMaskF/AlphaMaskM, per-variation AlphaMaskV1). Up to four masks
    /// (head, clothes, bottom, shoes) are unioned (min) into the body's own
    /// opacity texture; M_Body alpha-tests (gequal 0.5) so masked skin under
    /// clothing is discarded instead of clipping through.
    /// </summary>
    public class AlphaMaskSystem : IDisposable
    {
        public const string CompositeTextureName = "__BodyAlphaMask";

        //A decoded mask as tightly-packed RGBA8 bytes (the BC4 value lands in R/G/B).
        readonly record struct MaskImage(byte[] Rgba, int Width, int Height);

        readonly Dictionary<string, MaskImage> _masks = new(StringComparer.OrdinalIgnoreCase);
        bool _loaded;

        RuntimeTexture _composite;
        string _lastKey;

        /// <summary>Decodes all mask textures from Model/GearAlphaMask.bfres.zs.</summary>
        public void Load(Romfs romfs)
        {
            if (_loaded)
                return;
            _loaded = true;

            var data = romfs.ReadModel("GearAlphaMask");
            if (data == null)
            {
                Console.WriteLine("[AlphaMask] GearAlphaMask.bfres not found");
                return;
            }

            int bntxOffset = FindBntx(data);
            if (bntxOffset < 0)
                return;

            var bntx = new Syroot.NintenTools.NSW.Bntx.BntxFile(
                new MemoryStream(data, bntxOffset, data.Length - bntxOffset)
            );
            foreach (var tex in bntx.Textures)
            {
                try
                {
                    //Texture names carry an _Opa suffix; RSDB mask names do not.
                    string name = tex.Name.EndsWith("_Opa") ? tex.Name[..^4] : tex.Name;
                    var bt = new BntxTexture(bntx, tex);
                    _masks[name] = new MaskImage(
                        bt.GetDecodedSurface(0, 0),
                        (int)bt.Width,
                        (int)bt.Height
                    );
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[AlphaMask] Failed to decode {tex.Name}: {ex.Message}");
                }
            }
            Console.WriteLine($"[AlphaMask] Loaded {_masks.Count} masks");
        }

        static int FindBntx(byte[] data)
        {
            for (int i = 0; i < data.Length - 4; i++)
                if (
                    data[i] == 'B'
                    && data[i + 1] == 'N'
                    && data[i + 2] == 'T'
                    && data[i + 3] == 'X'
                )
                    return i;
            return -1;
        }

        public bool HasMask(string name) => !string.IsNullOrEmpty(name) && _masks.ContainsKey(name);

        /// <summary>
        /// Rebuilds the composite opacity texture for the human body and registers it
        /// on the human render under CompositeTextureName. Returns true if a mask
        /// override is active (caller then redirects M_Body's _o0 sampler to it).
        /// </summary>
        public bool Compose(PartModel human, IEnumerable<string> maskNames)
        {
            var active = maskNames.Where(HasMask).Distinct().OrderBy(x => x).ToList();
            if (active.Count == 0)
            {
                _lastKey = null;
                human.Render.Textures.Remove(CompositeTextureName);
                return false;
            }

            string key = string.Join("|", active) + "@" + human.ModelName;
            if (key == _lastKey && human.Render.Textures.ContainsKey(CompositeTextureName))
                return true;
            _lastKey = key;

            //Start from the body's own opacity texture so authored holes survive.
            byte[] result = null;
            int rw = 256,
                rh = 256;
            if (human.Render.Textures.TryGetValue("M_Body_Opa", out var bodyOpa))
            {
                try
                {
                    var bt = (BntxTexture)bodyOpa;
                    result = bt.GetDecodedSurface(0, 0);
                    rw = (int)bt.Width;
                    rh = (int)bt.Height;
                }
                catch { }
            }
            result ??= Filled(rw, rh, 255);

            foreach (var name in active)
                MinBlend(result, rw, rh, _masks[name]);

            _composite?.Dispose();
            _composite = new RuntimeTexture(CompositeTextureName, result, rw, rh);
            human.Render.Textures[CompositeTextureName] = _composite;
            return true;
        }

        static byte[] Filled(int w, int h, byte value)
        {
            var buf = new byte[w * h * 4];
            Array.Fill(buf, value);
            return buf;
        }

        /// <summary>dst = min(dst, mask) per pixel (mask nearest-sampled to dst size).</summary>
        static void MinBlend(byte[] dst, int dw, int dh, MaskImage mask)
        {
            for (int y = 0; y < dh; y++)
            {
                int my = mask.Height == dh ? y : y * mask.Height / dh;
                for (int x = 0; x < dw; x++)
                {
                    int mx = mask.Width == dw ? x : x * mask.Width / dw;
                    //BC4 masks decode with the value in the color channels; take red (RGBA index 0).
                    byte mv = mask.Rgba[(my * mask.Width + mx) * 4];
                    int di = (y * dw + x) * 4;
                    byte v = Math.Min(dst[di], mv);
                    dst[di] = dst[di + 1] = dst[di + 2] = dst[di + 3] = v;
                }
            }
        }

        public void Dispose()
        {
            _composite?.Dispose();
            _composite = null;
            _masks.Clear();
            _loaded = false;
        }
    }

    /// <summary>
    /// An STGenericTexture wrapping a GL texture created at runtime (not from a
    /// file), so it can live in a render's texture dictionary and be bound by name.
    /// </summary>
    class RuntimeTexture : STGenericTexture, IDisposable
    {
        public RuntimeTexture(string name, byte[] rgba, int width, int height)
        {
            Name = name;
            Width = (uint)width;
            Height = (uint)height;
            MipCount = 1;
            RenderableTex = GLTexture2D.FromRgba(rgba, width, height);
        }

        public override void SetImageData(
            List<byte[]> imageData,
            uint width,
            uint height,
            int arrayLevel = 0
        ) { }

        public override byte[] GetImageData(
            int ArrayLevel = 0,
            int MipLevel = 0,
            int DepthLevel = 0
        ) => Array.Empty<byte>();

        public void Dispose()
        {
            (RenderableTex as GLTexture2D)?.Dispose();
            RenderableTex = null;
        }
    }
}
