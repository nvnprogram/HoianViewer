using System;
using System.Globalization;
using System.Numerics;
using ImGuiNET;
using PlayerViewer.Core;

namespace PlayerViewer.UI
{
    /// <summary>The accent colour sets each Side Order theme offers.</summary>
    public enum SideOrderAccentName
    {
        Sdodr,
        Neon,
        Lime,
        Rose,
        Violet,
        Teal,
        Magenta,
        Cyan,
        Gold,
    }

    /// <summary>
    /// One of the Side Order looks: the preset colours, the scalars its renderer
    /// uses with them, the accents per theme, and the ImGui style built from all
    /// of it. Colours are sRGB.
    /// </summary>
    public sealed class SideOrderTheme
    {
        public InterfaceTheme Id { get; private init; }
        public bool IsLight { get; private init; }

        public Vector4 Bg { get; private init; }
        public Vector4 Panel { get; private init; }
        public Vector4 Glow { get; private init; }
        public Vector4 Frost { get; private init; }
        public Vector4 Floral { get; private init; }
        public Vector4 Floral2 { get; private init; }
        public Vector4 Text { get; private init; }
        public Vector4 TextPanel { get; private init; }
        public Vector4 Button { get; private init; }
        public Vector4 Icon { get; private init; }

        public float BgDarken { get; private init; }
        public float BgSheenAlpha { get; private init; }
        public float PanelMix { get; private init; }
        public float BlurDarken { get; private init; }
        public float PanelSheenAlpha { get; private init; }
        public float BgFloralAlpha { get; private init; }

        /// <summary>How dark the rim's line down a shape's sides is (<see cref="SideOrderAssets.RimShade"/>).</summary>
        public float RimShade { get; private init; }

        /// <summary>Scales the card fill, so the light cards sit a little under their controls.</summary>
        public float CardShade { get; private init; } = 1;

        //Bg times BgDarken, the colour the background fill actually shows.
        public Vector4 BgShown { get; private init; }

        //Panel rim and the round button halo and drop shadow, all tinted by Button.
        public Vector4 Edge { get; private init; }
        public Vector4 ButtonHalo { get; private init; }
        public Vector4 ButtonShadow { get; private init; }

        /// <summary>The 3D viewport's background while this theme is on. Never exported.</summary>
        public Vector4 Viewport { get; private init; }

        /// <summary>The light behind the model, fading out to <see cref="Viewport"/> at the edges.</summary>
        public Vector4 ViewportGlow { get; private init; }

        /// <summary>The control colours for the accent last applied.</summary>
        public SideOrderControlColours Controls { get; private set; }

        /// <summary>The Light theme's body text, darker than the preset's text on panels.</summary>
        static readonly Vector4 LightText = Hex("#33282a");

        //Each accent's glow, the colour it swaps into the theme, sRGB. Indexed by
        //SideOrderAccentName, Sdodr first.
        Vector4[] _accentGlows;

        public Vector4 AccentGlow(SideOrderAccentName name) =>
            _accentGlows[Math.Clamp((int)name, 0, _accentGlows.Length - 1)];

        /// <summary>The accent the look starts with, the preset closest to its orange.</summary>
        public const SideOrderAccentName DefaultAccent = SideOrderAccentName.Sdodr;

        //The Sdodr accent's selected row and slider fill, brighter than any preset's glow.
        static readonly Vector4 SdodrHighlight = Hex("#f1c19e");
        static readonly Vector4 SdodrFill = Hex("#e5b07d");

        public static readonly SideOrderTheme Light = new()
        {
            Id = InterfaceTheme.Light,
            IsLight = true,
            Bg = Hex("#a4978f"),
            Panel = Hex("#efe5e0"),
            Glow = Hex("#c4a07e"),
            Frost = Hex("#fffbfa"),
            Floral = Hex("#ffe6d4"),
            Floral2 = Hex("#ff9700"),
            Text = Hex("#5a4f4f"),
            TextPanel = Hex("#5a4f4f"),
            Button = Hex("#c9b0ac"),
            Icon = Hex("#5a4f4f"),
            BgDarken = 0.85f,
            BgSheenAlpha = 0.4f,
            PanelMix = 0.7f,
            BlurDarken = 0.45f,
            PanelSheenAlpha = 0.62f,
            BgFloralAlpha = 0.7f,
            CardShade = 0.8f,
            RimShade = 0.3f,
            BgShown = Hex("#988c85"),
            Edge = Hex("#443d3d"),
            ButtonHalo = Hex("#6b5858"),
            ButtonShadow = Hex("#814445"),
            Viewport = Hex("#a59c98"),
            ViewportGlow = Hex("#c3b9b4"),
            _accentGlows = Hexes(
                "#c4a07e",
                "#6daad5",
                "#99d072",
                "#d56d87",
                "#a16dd5",
                "#72d0c8",
                "#dda2c2",
                "#6d90d5",
                "#ceaa56"
            ),
        };

        public static readonly SideOrderTheme Dark = new()
        {
            Id = InterfaceTheme.Dark,
            Bg = Hex("#2a2624"),
            Panel = Hex("#4d4644"),
            Glow = Hex("#b79880"),
            Frost = Hex("#a89a94"),
            Floral = Hex("#c9b3a4"),
            Floral2 = Hex("#e0895a"),
            Text = Hex("#f1e9e4"),
            TextPanel = Hex("#f4ede8"),
            Button = Hex("#9c8c86"),
            Icon = Hex("#f1e9e4"),
            BgDarken = 1.0f,
            BgSheenAlpha = 0.35f,
            PanelMix = 0.55f,
            BlurDarken = 0.3f,
            PanelSheenAlpha = 0.35f,
            BgFloralAlpha = 0.55f,
            BgShown = Hex("#2a2624"),
            Edge = Hex("#332f2e"),
            ButtonHalo = Hex("#524543"),
            ButtonShadow = Hex("#633534"),
            RimShade = 0.28f,
            Viewport = Hex("#2f2a28"),
            ViewportGlow = Hex("#4d4543"),
            _accentGlows = Hexes(
                "#b79880",
                "#65a5d2",
                "#93cd6a",
                "#d26580",
                "#9c65d2",
                "#6acdc5",
                "#da9abc",
                "#6589d2",
                "#cba54d"
            ),
        };

        public static readonly SideOrderTheme YetDarker = new()
        {
            Id = InterfaceTheme.YetDarker,
            Bg = Hex("#14110f"),
            Panel = Hex("#2d2725"),
            Glow = Hex("#b79880"),
            Frost = Hex("#6f635e"),
            Floral = Hex("#a08d80"),
            Floral2 = Hex("#c47a50"),
            Text = Hex("#f1e9e4"),
            TextPanel = Hex("#f4ede8"),
            Button = Hex("#7d6f6a"),
            Icon = Hex("#f1e9e4"),
            BgDarken = 1.0f,
            BgSheenAlpha = 0.35f,
            PanelMix = 0.55f,
            BlurDarken = 0.3f,
            PanelSheenAlpha = 0.35f,
            BgFloralAlpha = 0.55f,
            BgShown = Hex("#14110f"),
            Edge = Hex("#272323"),
            ButtonHalo = Hex("#413534"),
            ButtonShadow = Hex("#4e2827"),
            RimShade = 0.32f,
            Viewport = Hex("#1a1615"),
            ViewportGlow = Hex("#352e2b"),
            _accentGlows = Hexes(
                "#b79880",
                "#65a5d2",
                "#93cd6a",
                "#d26580",
                "#9c65d2",
                "#6acdc5",
                "#da9abc",
                "#6589d2",
                "#cba54d"
            ),
        };

        public static SideOrderTheme For(InterfaceTheme id) =>
            id switch
            {
                InterfaceTheme.Light => Light,
                InterfaceTheme.Dark => Dark,
                _ => YetDarker,
            };

        /// <summary>The theme last applied, or null while the classic look is on.</summary>
        public static SideOrderTheme Current { get; private set; }

        /// <summary>Leaves the Side Order look; the caller applies the classic one.</summary>
        public static void Clear() => Current = null;

        /// <summary>
        /// Applies this theme to ImGui's style and to <see cref="Theme"/>'s palette. Expects the
        /// style at ImGui's defaults, so nothing a previous look set lingers.
        /// </summary>
        public void Apply(SideOrderAccentName accentName = DefaultAccent)
        {
            Current = this;
            SideOrderAssets.RimShade = RimShade;
            var glow = AccentGlow(accentName);
            bool sdodr = accentName == SideOrderAccentName.Sdodr;

            var fill = sdodr ? SdodrFill : glow;
            var highlight = IsLight
                ? (sdodr ? SdodrHighlight : Mix(glow, Panel, 0.35f))
                : Mix(fill, Panel, 0.45f);
            var text = IsLight ? LightText : TextPanel;
            var dim = Mix(text, Panel, IsLight ? 0.25f : 0.45f);
            Controls = BuildControls(fill, highlight, text);
            var frame = Mix(Controls.FrameTop, Controls.FrameBottom, 0.5f);
            var frameHover = Mix(frame, fill, 0.3f);
            var frameActive = Mix(frame, fill, 0.55f);
            var line = Mix(TextPanel, Panel, 0.72f);

            Theme.Use(
                new ThemePalette
                {
                    Gold = IsLight ? Saturate(Scale(fill, 0.44f), 1.4f) : Mix(fill, White, 0.1f),
                    GoldDim = IsLight ? Mix(fill, Panel, 0.3f) : Mix(fill, Panel, 0.4f),
                    GoldBright = IsLight
                        ? Saturate(Scale(fill, 0.44f), 1.7f)
                        : Mix(fill, White, 0.3f),
                    Bg = BgShown,
                    BgPanel = Panel,
                    BgItem = frame,
                    BgItemHover = frameHover,
                    BgItemActive = frameActive,
                    TextMain = text,
                    TextDim = dim,
                    Error = IsLight ? Hex("#9b3024") : Hex("#f07b6b"),
                    Success = IsLight ? Hex("#2e6127") : Hex("#8fd487"),
                    Cyan = IsLight ? Hex("#185d6f") : Hex("#7fcfe0"),
                    RedButtonBg = IsLight ? Hex("#d06a5c") : Hex("#8e3a30"),
                    RedButtonHover = IsLight ? Hex("#dc7b6d") : Hex("#a8463a"),
                }
            );

            //The geometry: frames 30 px tall on a 35 px row pitch, drawn as pills
            //(rounding past half the height clamps to it), panels with large radii, a round
            //slider thumb as tall as the grab.
            var style = ImGui.GetStyle();
            style.WindowRounding = SideOrderLayout.CardRounding;
            style.ChildRounding = 16;
            style.FrameRounding = 16;
            style.PopupRounding = 14;
            style.GrabRounding = 13;
            style.TabRounding = 12;
            //ImGui insets the grab 3 px each side, so a 12 px bar is a 6 px thumb, which
            //Widgets.DecorateScrollbar draws over as a raised pill.
            style.ScrollbarRounding = 8;
            style.ScrollbarSize = 12;
            style.WindowBorderSize = 0;
            style.ChildBorderSize = 0;
            style.PopupBorderSize = 1;
            style.FrameBorderSize = 0;
            style.WindowPadding = new Vector2(14, 12);
            style.FramePadding = new Vector2(14, 5);
            style.ItemSpacing = new Vector2(8, 5);
            style.ItemInnerSpacing = new Vector2(6, 4);
            style.GrabMinSize = 26;
            style.WindowTitleAlign = new Vector2(0.5f, 0.5f);

            var c = style.Colors;
            c[(int)ImGuiCol.Text] = text;
            c[(int)ImGuiCol.TextDisabled] = dim;
            //Floating windows are opaque cards, the tone a card shows over the background. The
            //host window pushes Theme.Bg for itself.
            var card = WithAlpha(Mix(Panel, BgShown, IsLight ? 0.15f : 0.3f), 1);
            c[(int)ImGuiCol.WindowBg] = card;
            c[(int)ImGuiCol.ChildBg] = WithAlpha(Panel, IsLight ? 0.7f : 0.35f);
            c[(int)ImGuiCol.PopupBg] = WithAlpha(
                IsLight ? Mix(Panel, Controls.FrameTop, 0.5f) : Mix(Panel, TextPanel, 0.04f),
                1
            );
            c[(int)ImGuiCol.Border] = WithAlpha(Edge, IsLight ? 0.3f : 0.6f);
            c[(int)ImGuiCol.BorderShadow] = Vector4.Zero;
            c[(int)ImGuiCol.FrameBg] = frame;
            c[(int)ImGuiCol.FrameBgHovered] = frameHover;
            c[(int)ImGuiCol.FrameBgActive] = frameActive;
            c[(int)ImGuiCol.TitleBg] = card;
            c[(int)ImGuiCol.TitleBgActive] = card;
            c[(int)ImGuiCol.TitleBgCollapsed] = card;
            c[(int)ImGuiCol.MenuBarBg] = WithAlpha(Panel, 0.5f);
            //A thumb with no track, muted until it is used.
            c[(int)ImGuiCol.ScrollbarBg] = Vector4.Zero;
            c[(int)ImGuiCol.ScrollbarGrab] = Mix(Controls.ThumbTop, Controls.ThumbBottom, 0.5f);
            c[(int)ImGuiCol.ScrollbarGrabHovered] = Mix(
                Controls.ThumbHotTop,
                Controls.ThumbHotBottom,
                0.5f
            );
            c[(int)ImGuiCol.ScrollbarGrabActive] = Mix(
                Controls.ThumbHeldTop,
                Controls.ThumbHeldBottom,
                0.5f
            );
            c[(int)ImGuiCol.CheckMark] = IsLight ? text : fill;
            c[(int)ImGuiCol.SliderGrab] = fill;
            c[(int)ImGuiCol.SliderGrabActive] = Mix(fill, White, 0.25f);
            c[(int)ImGuiCol.Button] = frame;
            c[(int)ImGuiCol.ButtonHovered] = frameHover;
            c[(int)ImGuiCol.ButtonActive] = frameActive;
            c[(int)ImGuiCol.Header] = highlight;
            c[(int)ImGuiCol.HeaderHovered] = WithAlpha(highlight, 0.55f);
            c[(int)ImGuiCol.HeaderActive] = highlight;
            c[(int)ImGuiCol.Separator] = line;
            c[(int)ImGuiCol.SeparatorHovered] = fill;
            c[(int)ImGuiCol.SeparatorActive] = fill;
            c[(int)ImGuiCol.ResizeGrip] = WithAlpha(dim, 0.4f);
            c[(int)ImGuiCol.ResizeGripHovered] = fill;
            c[(int)ImGuiCol.ResizeGripActive] = fill;
            c[(int)ImGuiCol.Tab] = Mix(Panel, BgShown, 0.25f);
            c[(int)ImGuiCol.TabHovered] = frameHover;
            c[(int)ImGuiCol.TabActive] = frame;
            c[(int)ImGuiCol.TabUnfocused] = Mix(Panel, BgShown, 0.25f);
            c[(int)ImGuiCol.TabUnfocusedActive] = Mix(frame, Panel, 0.4f);
            c[(int)ImGuiCol.PlotLines] = fill;
            c[(int)ImGuiCol.PlotHistogram] = fill;
            c[(int)ImGuiCol.TextSelectedBg] = WithAlpha(highlight, 0.6f);
            c[(int)ImGuiCol.DragDropTarget] = fill;
            c[(int)ImGuiCol.NavHighlight] = fill;
            c[(int)ImGuiCol.ModalWindowDimBg] = new Vector4(0, 0, 0, 0.45f);
        }

        //Light's frames, accent pill and list are fixed colours; the dark themes lift the panel
        //colour toward the text colour the same way, so frames stand out of their cards alike.
        SideOrderControlColours BuildControls(Vector4 fill, Vector4 highlight, Vector4 text)
        {
            var dark = Hex("#2a2021");
            if (IsLight)
            {
                var top = Hex("#faf0eb");
                var bottom = Hex("#eadbd3");
                return new SideOrderControlColours
                {
                    FrameTop = top,
                    FrameBottom = bottom,
                    FrameHotTop = Mix(top, White, 0.6f),
                    FrameHotBottom = Mix(bottom, White, 0.35f),
                    FrameHeldTop = Mix(bottom, Edge, 0.03f),
                    FrameHeldBottom = Mix(bottom, Edge, 0.06f),
                    AccentTop = Hex("#fedcbb"),
                    AccentBottom = Hex("#f2bf97"),
                    AccentHotTop = Hex("#ffe8d2"),
                    AccentText = text,
                    Shadow = WithAlpha(Edge, 0.34f),
                    Highlight = WithAlpha(White, 0.85f),
                    Text = text,
                    Well = WithAlpha(Mix(bottom, Panel, 0.2f), 0.8f),
                    SliderWell = WithAlpha(Mix(bottom, top, 0.6f), 0.9f),
                    Track = Hex("#dccdc6"),
                    TrackFill = Hex("#ebbb8c"),
                    TrackFillEnd = Hex("#dc9f6b"),
                    Thumb = Hex("#fefbfa"),
                    ListBg = WithAlpha(Hex("#f5ece7"), 0.92f),
                    //A shade darker than the card, warm, so it reads as a control on it.
                    ThumbTop = Mix(bottom, Edge, 0.14f),
                    ThumbBottom = Mix(bottom, Edge, 0.28f),
                    ThumbHotTop = Mix(bottom, Edge, 0.22f),
                    ThumbHotBottom = Mix(bottom, Edge, 0.36f),
                    ThumbHeldTop = Mix(Mix(bottom, Edge, 0.22f), fill, 0.35f),
                    ThumbHeldBottom = Mix(Mix(bottom, Edge, 0.36f), fill, 0.35f),
                    RowTint = WithAlpha(Edge, 0.04f),
                    RowHover = WithAlpha(Edge, 0.07f),
                    RowSelected = highlight,
                    RowSelectedText = text,
                    Mark = dark,
                    Icon = Icon,
                };
            }
            var p = Panel;
            var t = TextPanel;
            var frameTop = Mix(p, t, 0.19f);
            var frameBottom = Mix(p, t, 0.10f);
            return new SideOrderControlColours
            {
                FrameTop = frameTop,
                FrameBottom = frameBottom,
                FrameHotTop = Mix(p, t, 0.26f),
                FrameHotBottom = Mix(p, t, 0.15f),
                FrameHeldTop = Mix(p, t, 0.08f),
                FrameHeldBottom = Mix(p, t, 0.11f),
                AccentTop = Mix(fill, White, 0.1f),
                AccentBottom = Mix(fill, p, 0.22f),
                AccentHotTop = Mix(fill, White, 0.25f),
                AccentText = dark,
                Shadow = new Vector4(0, 0, 0, 0.5f),
                Highlight = WithAlpha(White, Id == InterfaceTheme.Dark ? 0.13f : 0.1f),
                Text = text,
                Well = WithAlpha(Mix(p, t, 0.05f), 0.85f),
                SliderWell = WithAlpha(Mix(p, BgShown, 0.2f), 0.85f),
                Track = Mix(p, BgShown, 0.55f),
                TrackFill = fill,
                TrackFillEnd = Mix(fill, p, 0.2f),
                Thumb = Mix(t, White, 0.3f),
                ListBg = WithAlpha(Mix(p, t, 0.05f), 0.85f),
                //The frame colours: a dark card needs its thumb lighter than itself to show.
                ThumbTop = Mix(p, t, 0.22f),
                ThumbBottom = Mix(p, t, 0.12f),
                ThumbHotTop = Mix(p, t, 0.30f),
                ThumbHotBottom = Mix(p, t, 0.18f),
                ThumbHeldTop = Mix(Mix(p, t, 0.30f), fill, 0.35f),
                ThumbHeldBottom = Mix(Mix(p, t, 0.18f), fill, 0.35f),
                RowTint = WithAlpha(White, 0.025f),
                RowHover = WithAlpha(White, 0.06f),
                RowSelected = highlight,
                RowSelectedText = t,
                Mark = dark,
                Icon = Icon,
            };
        }

        static readonly Vector4 White = Vector4.One;

        static Vector4[] Hexes(params string[] hex) => Array.ConvertAll(hex, Hex);

        public static Vector4 Hex(string hex)
        {
            int v = int.Parse(hex.AsSpan(1), NumberStyles.HexNumber);
            return new Vector4(
                (v >> 16 & 0xFF) / 255f,
                (v >> 8 & 0xFF) / 255f,
                (v & 0xFF) / 255f,
                1
            );
        }

        //Blends colours only; the result keeps a's alpha.
        static Vector4 Mix(Vector4 a, Vector4 b, float t) =>
            new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, a.Z + (b.Z - a.Z) * t, a.W);

        static Vector4 Scale(Vector4 c, float s) => new(c.X * s, c.Y * s, c.Z * s, c.W);

        //Pushes a colour away from its grey by k, so a darkened accent stays a colour.
        static Vector4 Saturate(Vector4 c, float k)
        {
            float grey = (c.X + c.Y + c.Z) / 3;
            return new(
                Math.Clamp(grey + (c.X - grey) * k, 0, 1),
                Math.Clamp(grey + (c.Y - grey) * k, 0, 1),
                Math.Clamp(grey + (c.Z - grey) * k, 0, 1),
                c.W
            );
        }

        static Vector4 WithAlpha(Vector4 c, float a) => new(c.X, c.Y, c.Z, a);
    }
}
