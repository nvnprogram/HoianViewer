using System;
using System.Numerics;

namespace PlayerViewer.UI
{
    /// <summary>
    /// The Side Order layout's metrics and its clock. The window's client area is the floral
    /// background edge to edge; the menu row and the cards sit directly on it.
    /// </summary>
    public static class SideOrderLayout
    {
        /// <summary>The window fills the screen: maximised, or snapped or sized over the whole work area.</summary>
        public static bool Filled;

        /// <summary>Between the window's left edge and the cards, and between cards side by side.</summary>
        public const float Gap = 12;

        public const float RightEdgeGap = 16;
        public const float BottomGap = 13;

        /// <summary>Between the cards stacked on the right.</summary>
        public const float RightStackGap = 20;

        /// <summary>
        /// The top row is the window's title bar (the custom title bar mode), tall with the title
        /// font. Otherwise the OS title bar is above it and the row is as thin as the classic menu bar.
        /// </summary>
        public static bool TitleBar = true;

        public const float TitleRowHeight = 46;

        /// <summary>The classic menu bar's height: its 16 px font and 5 px frame padding.</summary>
        public const float ThinRowHeight = 26;

        /// <summary>The row the scene title and the menus sit in, at the top of the window.</summary>
        public static float MenuRow => TitleBar ? TitleRowHeight : ThinRowHeight;

        /// <summary>Where the cards start below the window's top.</summary>
        public static float CardsTop => TitleBar ? TitleRowHeight : ThinRowHeight + 6;

        public const float CardRounding = 18;
        public const float ViewportRounding = 24;

        /// <summary>How far a card's scrolling child sits in from its right edge and from its top and bottom.</summary>
        public static readonly Vector2 ScrollInset = new(3, 6);

        /// <summary>How far headers, labels and checkboxes sit in from the frames' left edge.</summary>
        public const float TextIndent = 8;

        /// <summary>Where a labelled row's control starts, from the card's left edge.</summary>
        public const float LabelColumn = 108;

        /// <summary>How much wider a fixed label column is than the classic look's, for the text indent and the wider body font.</summary>
        public const float ColumnWiden = 22;

        /// <summary>The space above a section header that follows other content.</summary>
        public const float HeaderGap = 4;

        /// <summary>The top of a card's first header line, from the card's top edge.</summary>
        public const float FirstHeaderTop = 9;

        //The card widths at a 1672 px wide window, scaled with the window within bounds.
        public static float LeftCardWidth(float windowWidth) =>
            MathF.Round(Math.Clamp(windowWidth * (373f / 1672f), 340, 440));

        public static float RightCardWidth(float windowWidth) =>
            MathF.Round(Math.Clamp(windowWidth * (393f / 1672f), 350, 460));

        /// <summary>
        /// Kept clear at the top right for the custom title bar's window buttons, less the
        /// right edge gap the path keeps anyway; 0 while they are not drawn.
        /// </summary>
        public static float WindowButtonsWidth;

        /// <summary>A window button's hover pill; the buttons sit this far apart.</summary>
        public const float WindowButtonWidth = 40;
        public const float WindowButtonHeight = 30;
        public const float WindowButtonGap = 4;

        /// <summary>From the window's right edge to the close button.</summary>
        public const float WindowButtonsRight = 10;

        /// <summary>Between the romfs path and the minimise button.</summary>
        public const float WindowButtonsPathGap = 14;

        /// <summary>60 Hz frames, a long stall counted as a tenth of a second.</summary>
        public static double Time { get; private set; }

        public static void Tick(float seconds) => Time += Math.Min(0.1, Math.Max(0, seconds)) * 60;

        /// <summary>
        /// Wobble gain: small elements move proportionally more, and nothing
        /// taller than its reference moves less than one.
        /// </summary>
        public static float WobbleGain(float height) =>
            MathF.Max(1, MathF.Pow(640 / MathF.Max(1, height), 0.25f));

        /// <summary>How far the noise under the sheen scrolls per frame, in uv.</summary>
        public const double NoiseScroll = 0.001000001;

        /// <summary>The sheen's glow lookup across the width, in uv per pixel; the background shader has it too.</summary>
        public const float SheenUvPerPixel = 0.00031147541f;

        /// <summary>The fractional part, so a rate times the clock stays precise in a long session.</summary>
        public static float Wrap(double v) => (float)(v - Math.Floor(v));
    }
}
