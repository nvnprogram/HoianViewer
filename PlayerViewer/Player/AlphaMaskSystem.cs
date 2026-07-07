using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
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

        readonly Dictionary<string, Bitmap> _masks = new(StringComparer.OrdinalIgnoreCase);
        bool _loaded;

        RuntimeTexture _composite;
        string _lastKey;

        /// <summary>Decodes all mask textures from Model/GearAlphaMask.bfres.zs.</summary>
        public void Load(Romfs romfs)
        {
            if (_loaded) return;
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
                new MemoryStream(data, bntxOffset, data.Length - bntxOffset));
            foreach (var tex in bntx.Textures)
            {
                try
                {
                    //Texture names carry an _Opa suffix; RSDB mask names do not.
                    string name = tex.Name.EndsWith("_Opa") ? tex.Name[..^4] : tex.Name;
                    _masks[name] = new BntxTexture(bntx, tex).GetBitmap();
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
                if (data[i] == 'B' && data[i + 1] == 'N' && data[i + 2] == 'T' && data[i + 3] == 'X')
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
            Bitmap result = null;
            if (human.Render.Textures.TryGetValue("M_Body_Opa", out var bodyOpa))
            {
                try { result = ((BntxTexture)bodyOpa).GetBitmap(); }
                catch { }
            }
            result ??= FilledBitmap(256, 256, 255);

            foreach (var name in active)
                MinBlend(result, _masks[name]);

            _composite?.Dispose();
            _composite = new RuntimeTexture(CompositeTextureName, result);
            human.Render.Textures[CompositeTextureName] = _composite;
            result.Dispose();
            return true;
        }

        static Bitmap FilledBitmap(int w, int h, byte value)
        {
            var bmp = new Bitmap(w, h);
            using var g = Graphics.FromImage(bmp);
            g.Clear(Color.FromArgb(255, value, value, value));
            return bmp;
        }

        /// <summary>dst = min(dst, mask) per pixel (mask scaled to dst size).</summary>
        static void MinBlend(Bitmap dst, Bitmap mask)
        {
            using Bitmap scaled = mask.Width == dst.Width && mask.Height == dst.Height
                ? new Bitmap(mask)
                : new Bitmap(mask, dst.Width, dst.Height);

            var rect = new Rectangle(0, 0, dst.Width, dst.Height);
            var d = dst.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
            var s = scaled.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            unsafe
            {
                byte* dp = (byte*)d.Scan0;
                byte* sp = (byte*)s.Scan0;
                int count = dst.Width * dst.Height;
                for (int i = 0; i < count; i++, dp += 4, sp += 4)
                {
                    //BC4 masks decode with the value in the color channels; take red.
                    byte v = Math.Min(dp[2], sp[2]);
                    dp[0] = dp[1] = dp[2] = dp[3] = v;
                }
            }
            dst.UnlockBits(d);
            scaled.UnlockBits(s);
        }

        public void Dispose()
        {
            _composite?.Dispose();
            _composite = null;
            foreach (var bmp in _masks.Values)
                bmp.Dispose();
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
        public RuntimeTexture(string name, Bitmap bitmap)
        {
            Name = name;
            Width = (uint)bitmap.Width;
            Height = (uint)bitmap.Height;
            MipCount = 1;
            RenderableTex = GLTexture2D.FromBitmap(bitmap);
        }

        public override void SetImageData(List<byte[]> imageData, uint width, uint height, int arrayLevel = 0) { }
        public override byte[] GetImageData(int ArrayLevel = 0, int MipLevel = 0, int DepthLevel = 0) => Array.Empty<byte>();

        public void Dispose()
        {
            (RenderableTex as GLTexture2D)?.Dispose();
            RenderableTex = null;
        }
    }
}
