using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ImGuiNET;
using PlayerViewer.Core;

namespace PlayerViewer.UI
{
    /// <summary>
    /// The fonts the atlas holds beyond the built in ones, for one look. Classic adds the game
    /// font the gear names are pushed in. Side Order adds the bundled BlitzMain as the default
    /// font, with the names' glyphs and their CJK fallback merged in, and BlitzBold for headers
    /// and the scene title.
    /// </summary>
    public sealed class UiFonts : IDisposable
    {
        //Blitz faces are two em tall ascent to descent, so these are ems, not atlas sizes. The
        //body em sets the capital height of the Side Order body text.
        const float BodyEm = 16,
            BodyLine = 20;
        const float HeaderEm = 18,
            HeaderLine = 22;
        const float TitleEm = 22,
            TitleLine = 26;

        //The title bar's File and Settings, a fifth larger than the body.
        const float MenuEm = 19,
            MenuLine = 22;

        //Capital height over the em, for centring capitals in a line.
        const float MainCaps = 0.767f,
            BoldCaps = 0.78f;

        /// <summary>BlitzBold for section titles; null in the classic look.</summary>
        public static ImFontPtr? Header { get; private set; }

        /// <summary>BlitzBold for the scene title; null in the classic look.</summary>
        public static ImFontPtr? Title { get; private set; }

        /// <summary>BlitzMain for the title bar's menus; null in the classic look.</summary>
        public static ImFontPtr? Menu { get; private set; }

        readonly GameFont _names,
            _body,
            _header,
            _title,
            _menu;

        /// <summary>What was built, to tell whether a rebuild changes anything.</summary>
        public string Signature { get; }

        UiFonts(GameFont names, GameFont body, GameFont header, GameFont title, GameFont menu)
        {
            _names = names;
            _body = body;
            _header = header;
            _title = title;
            _menu = menu;
            Signature = string.Join(
                "|",
                new[] { names, body, header, title, menu }.Select(f => f?.Signature ?? "")
            );
        }

        GameFont[] All => new[] { _names, _body, _header, _title, _menu };

        /// <summary>
        /// The fonts for a look. <paramref name="names"/> are the texts the gear pickers show, or
        /// null before a romfs is loaded.
        /// </summary>
        public static UiFonts Build(
            bool sideOrder,
            Romfs romfs,
            string language,
            IReadOnlyCollection<string> names
        )
        {
            if (!sideOrder)
                return new UiFonts(
                    names != null ? GameFont.Build(romfs, language, names) : null,
                    null,
                    null,
                    null,
                    null
                );
            var main = Embedded("BlitzMain");
            var bold = Embedded("BlitzBold");
            var chars = UiChars().ToArray();
            return new UiFonts(
                null,
                GameFont.Build(main, BodyEm, chars, romfs, language, names),
                GameFont.Build(bold, HeaderEm, chars, null, language, null),
                GameFont.Build(bold, TitleEm, chars, null, language, null),
                GameFont.Build(main, MenuEm, chars, null, language, null)
            );
        }

        //Latin-1, Latin Extended-A and general punctuation cover every string the panels draw.
        static IEnumerable<char> UiChars()
        {
            for (char c = (char)0x20; c <= 0x17F; c++)
                if (c < 0x7F || c >= 0xA0)
                    yield return c;
            for (char c = (char)0x2010; c <= 0x205E; c++)
                yield return c;
        }

        static readonly Dictionary<string, byte[]> _embedded = new();

        static RomfsFonts.Face Embedded(string name)
        {
            if (!_embedded.TryGetValue(name, out var data))
            {
                using var stream =
                    typeof(UiFonts).Assembly.GetManifestResourceStream(
                        $"PlayerViewer.UI.Fonts.{name}.otf"
                    ) ?? throw new InvalidOperationException($"missing embedded font {name}");
                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                _embedded[name] = data = ms.ToArray();
            }
            return new RomfsFonts.Face { Name = name, Data = data };
        }

        //Where the baseline goes so capitals sit centred in the line.
        static float Baseline(float em, float line, float caps) =>
            MathF.Round((line + em * caps) / 2);

        /// <summary>
        /// Adds the fonts to the atlas. The classic name font is aligned to the main font, whose
        /// ascent and line height are given.
        /// </summary>
        public void AddTo(ImFontAtlasPtr atlas, float mainAscent, float mainLineHeight)
        {
            _names?.AddTo(atlas, mainAscent, mainLineHeight);
            _body?.AddTo(atlas, Baseline(BodyEm, BodyLine, MainCaps), BodyLine);
            _header?.AddTo(atlas, Baseline(HeaderEm, HeaderLine, BoldCaps), HeaderLine);
            _title?.AddTo(atlas, Baseline(TitleEm, TitleLine, BoldCaps), TitleLine);
            _menu?.AddTo(atlas, Baseline(MenuEm, MenuLine, MainCaps), MenuLine);
        }

        /// <summary>Sets the built fonts' line heights and makes them the ones in use.</summary>
        public void AfterBuild()
        {
            foreach (var font in All)
                font?.AfterBuild();
            GameFont.Current = _names?.Font;
            Header = _header?.Font;
            Title = _title?.Font;
            Menu = _menu?.Font;
            SetDefaultFont(_body?.Font);
        }

        /// <summary>Stops handing out fonts, for a rebuild that frees them.</summary>
        public static void Clear()
        {
            GameFont.Clear();
            Header = null;
            Title = null;
            Menu = null;
            SetDefaultFont(null);
        }

        //Null leaves the atlas's first font, the main one, as the default.
        static unsafe void SetDefaultFont(ImFontPtr? font) =>
            ImGui.GetIO().NativePtr->FontDefault = font is { } f ? f.NativePtr : null;

        /// <summary>Frees the buffers the atlas read from. Only after the atlas moved on.</summary>
        public void Dispose()
        {
            foreach (var font in All)
                font?.Dispose();
        }
    }
}
