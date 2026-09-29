using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using ImGuiNET;
using PlayerViewer.Core;

namespace PlayerViewer.UI
{
    /// <summary>
    /// A Blitz font in the atlas, with the game's CJK font filling what it lacks, laid out on the
    /// main font's line and baseline. Only the characters asked for are rasterised.
    /// </summary>
    public sealed class GameFont : IDisposable
    {
        //BlitzMain's em in pixels, so its capitals are a little over the main font's.
        const float EmPixels = 13.5f;

        //Windows fonts for a romfs without its font archives.
        static readonly Dictionary<string, string[]> SystemFonts = new()
        {
            ["KRko"] = new[] { "malgun.ttf", "gulim.ttc" },
            ["CNzh"] = new[] { "msyh.ttc", "simsun.ttc" },
            ["TWzh"] = new[] { "msjh.ttc", "mingliu.ttc" },
            [""] = new[] { "YuGothM.ttc", "meiryo.ttc", "msgothic.ttc" },
        };

        /// <summary>The font to push around localized text; null when there is none to push.</summary>
        public static ImFontPtr? Current { get; set; }

        /// <summary>This font once the atlas holds it.</summary>
        public ImFontPtr? Font { get; private set; }

        readonly List<IntPtr> _native = new();
        readonly List<(IntPtr Data, int Size, float Pixels, float Ascent)> _faces = new();
        IntPtr _ranges;
        float _lineHeight;

        /// <summary>The characters this was built for, to tell whether a rebuild changes anything.</summary>
        public string Signature { get; private set; } = "";

        GameFont() { }

        /// <summary>
        /// Prepares the font for a set of strings, or returns null when there are none or the
        /// romfs and the system have no font for them.
        /// </summary>
        public static GameFont Build(Romfs romfs, string language, IEnumerable<string> texts)
        {
            var needed = new SortedSet<char>();
            for (char c = (char)0x20; c <= 0xFF; c++)
                needed.Add(c);
            if (!AddTexts(needed, texts))
                return null;

            var faces = new List<RomfsFonts.Face>
            {
                RomfsFonts.Blitz(romfs),
                RomfsFonts.Fallback(romfs, language),
            }
                .Where(f => f != null)
                .ToList();
            if (faces.Count < 2)
            {
                var system = SystemFont(language);
                if (system != null)
                    faces.Add(system);
            }
            return Create(faces, needed, language, EmPixels);
        }

        /// <summary>
        /// A font on a face the caller supplies, at <paramref name="emPixels"/>, holding
        /// <paramref name="chars"/> and whatever <paramref name="texts"/> use. The language's
        /// fallback is merged in only when there are texts, which there are once a romfs is loaded.
        /// </summary>
        public static GameFont Build(
            RomfsFonts.Face primary,
            float emPixels,
            IEnumerable<char> chars,
            Romfs romfs,
            string language,
            IEnumerable<string> texts
        )
        {
            var needed = new SortedSet<char>(chars);
            var faces = new List<RomfsFonts.Face> { primary };
            if (texts != null && AddTexts(needed, texts))
                faces.Add(RomfsFonts.Fallback(romfs, language) ?? SystemFont(language));
            return Create(faces.Where(f => f != null).ToList(), needed, language, emPixels);
        }

        static bool AddTexts(SortedSet<char> needed, IEnumerable<string> texts)
        {
            bool any = false;
            foreach (var text in texts)
            {
                if (text == null)
                    continue;
                any = true;
                foreach (char c in text)
                    if (c >= 0x20 && !char.IsSurrogate(c))
                        needed.Add(c);
            }
            return any;
        }

        static GameFont Create(
            List<RomfsFonts.Face> faces,
            SortedSet<char> needed,
            string language,
            float emPixels
        )
        {
            if (faces.Count == 0)
                return null;
            string names = string.Join(",", faces.Select(f => f.Name));
            var font = new GameFont
            {
                Signature = $"{names}@{emPixels}:{language}:" + new string(needed.ToArray()),
            };
            font._ranges = font.Ranges(needed);
            foreach (var face in faces)
            {
                var metrics = RomfsFonts.Metrics(face.Data);
                if (metrics == null)
                    continue;
                var (upm, ascent, descent) = metrics.Value;
                //The atlas sizes a font by ascent to descent, not by the em.
                float em = emPixels * face.Scale;
                float pixels = em * (ascent - descent) / upm;
                float ascentPx = MathF.Floor(ascent * pixels / (ascent - descent) + 1);
                var copy = Marshal.AllocHGlobal(face.Data.Length);
                Marshal.Copy(face.Data, 0, copy, face.Data.Length);
                font._native.Add(copy);
                font._faces.Add((copy, face.Data.Length, pixels, ascentPx));
            }
            return font._faces.Count > 0 ? font : null;
        }

        static RomfsFonts.Face SystemFont(string language)
        {
            if (!SystemFonts.TryGetValue(language ?? "", out var names))
                names = SystemFonts[""];
            string dir = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
            foreach (var name in names)
            {
                string path = Path.Combine(dir, name);
                if (File.Exists(path))
                    return new RomfsFonts.Face { Name = name, Data = File.ReadAllBytes(path) };
            }
            return null;
        }

        //Pairs of inclusive ranges, zero terminated, in the atlas's 16 bit character type.
        IntPtr Ranges(SortedSet<char> chars)
        {
            var ranges = new List<ushort>();
            int start = -1,
                prev = -1;
            foreach (char c in chars)
            {
                if (start < 0)
                    start = c;
                else if (c != prev + 1)
                {
                    ranges.Add((ushort)start);
                    ranges.Add((ushort)prev);
                    start = c;
                }
                prev = c;
            }
            ranges.Add((ushort)start);
            ranges.Add((ushort)prev);
            ranges.Add(0);
            var block = Marshal.AllocHGlobal(ranges.Count * sizeof(ushort));
            Marshal.Copy(ranges.Select(r => (short)r).ToArray(), 0, block, ranges.Count);
            _native.Add(block);
            return block;
        }

        /// <summary>
        /// Adds the font to the atlas. Every face is placed so its baseline falls
        /// <paramref name="mainAscent"/> below the top of a line, where the main font's does
        /// for the classic name font.
        /// </summary>
        public unsafe void AddTo(ImFontAtlasPtr atlas, float mainAscent, float mainLineHeight)
        {
            _lineHeight = mainLineHeight;
            ImFontPtr font = default;
            float dstAscent = 0;
            for (int i = 0; i < _faces.Count; i++)
            {
                var (data, size, pixels, ascentPx) = _faces[i];
                if (i == 0)
                    dstAscent = ascentPx;
                var config = ImGuiNative.ImFontConfig_ImFontConfig();
                config->MergeMode = (byte)(i == 0 ? 0 : 1);
                config->OversampleH = 3;
                config->OversampleV = 1;
                config->FontDataOwnedByAtlas = 0;
                config->GlyphOffset = new System.Numerics.Vector2(0, mainAscent - dstAscent);
                var added = atlas.AddFontFromMemoryTTF(data, size, pixels, config, _ranges);
                if (i == 0)
                    font = added;
                ImGuiNative.ImFontConfig_destroy(config);
            }
            Font = font;
        }

        /// <summary>
        /// Gives the built font the line height it was added with, so a row laid out with it is
        /// as tall as any other; its glyphs keep the size they were rasterised at.
        /// </summary>
        public void AfterBuild()
        {
            if (Font is { } font)
                font.FontSize = _lineHeight;
        }

        /// <summary>Stops handing the font out, for a rebuild without it.</summary>
        public static void Clear() => Current = null;

        /// <summary>Frees the buffers the atlas read from. Only after the atlas moved on.</summary>
        public void Dispose()
        {
            foreach (var block in _native)
                Marshal.FreeHGlobal(block);
            _native.Clear();
            _faces.Clear();
        }
    }
}
