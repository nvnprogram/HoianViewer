using System;
using System.Collections.Generic;
using System.Numerics;
using ImGuiNET;

namespace PlayerViewer.UI
{
    /// <summary>The colours the Side Order controls are drawn in, derived per theme and accent.</summary>
    public sealed class SideOrderControlColours
    {
        public Vector4 FrameTop { get; init; }
        public Vector4 FrameBottom { get; init; }
        public Vector4 FrameHotTop { get; init; }
        public Vector4 FrameHotBottom { get; init; }
        public Vector4 FrameHeldTop { get; init; }
        public Vector4 FrameHeldBottom { get; init; }
        public Vector4 AccentTop { get; init; }
        public Vector4 AccentBottom { get; init; }
        public Vector4 AccentHotTop { get; init; }
        public Vector4 AccentText { get; init; }

        /// <summary>The soft shadow under a raised control, alpha included.</summary>
        public Vector4 Shadow { get; init; }

        /// <summary>The light line along a raised control's top, alpha included.</summary>
        public Vector4 Highlight { get; init; }

        public Vector4 Text { get; init; }

        /// <summary>The sunken bed of a tab strip or a timeline.</summary>
        public Vector4 Well { get; init; }

        /// <summary>The flat pill a slider's track sits in.</summary>
        public Vector4 SliderWell { get; init; }
        public Vector4 Track { get; init; }
        public Vector4 TrackFill { get; init; }
        public Vector4 TrackFillEnd { get; init; }
        public Vector4 Thumb { get; init; }

        public Vector4 ListBg { get; init; }
        public Vector4 RowTint { get; init; }
        public Vector4 RowHover { get; init; }
        public Vector4 RowSelected { get; init; }
        public Vector4 RowSelectedText { get; init; }

        /// <summary>The scrollbar thumb's gradient at rest, hovered and dragged.</summary>
        public Vector4 ThumbTop { get; init; }
        public Vector4 ThumbBottom { get; init; }
        public Vector4 ThumbHotTop { get; init; }
        public Vector4 ThumbHotBottom { get; init; }
        public Vector4 ThumbHeldTop { get; init; }
        public Vector4 ThumbHeldBottom { get; init; }

        /// <summary>The check and the radio dot, drawn on the accent fill.</summary>
        public Vector4 Mark { get; init; }
        public Vector4 Icon { get; init; }
    }

    /// <summary>
    /// The Side Order controls as draw list primitives: raised pills with a vertical gradient, a
    /// soft shadow below and a light top edge; the accent pill; checkbox, radio, slider track,
    /// chevron and the round play button. The widgets in <see cref="Widgets"/> draw these under
    /// or over ImGui's own items, whose colours they make transparent.
    /// </summary>
    public static class SideOrderControls
    {
        const ImDrawCornerFlags All = ImDrawCornerFlags.All;

        public static SideOrderControlColours Colours => SideOrderTheme.Current?.Controls;

        /// <summary>The Side Order look is on, so the widgets draw themselves.</summary>
        public static bool On => SideOrderTheme.Current?.Controls != null && SideOrderAssets.Ready;

        /// <summary>A control's state as the pill shows it.</summary>
        public readonly record struct State(
            bool Hot,
            bool Held,
            bool Accent = false,
            bool Danger = false
        );

        //Whether an item was held on the last frame it was drawn, so a pill drawn before its item
        //can show the press. Pruned of items not drawn for a while.
        static readonly Dictionary<uint, int> _held = new();
        static int _frame;

        /// <summary>
        /// The state of an item about to be drawn over <paramref name="min"/>..<paramref name="max"/>:
        /// hover from the mouse, press from the last frame.
        /// </summary>
        public static State Predict(uint id, Vector2 min, Vector2 max, bool accent = false)
        {
            bool hot = Hovering(min, max);
            bool held = _held.TryGetValue(id, out int f) && f >= ImGui.GetFrameCount() - 1;
            return new State(hot || held, held, accent);
        }

        /// <summary>A combo's state: lit while hovered or while its list was open last frame.</summary>
        public static State PredictCombo(uint id, Vector2 min, Vector2 max)
        {
            bool hot = Hovering(min, max);
            bool open = _open.TryGetValue(id, out int f) && f >= ImGui.GetFrameCount() - 1;
            return new State(hot || open, false);
        }

        /// <summary>The mouse is over a control about to be drawn in the current window, which takes input.</summary>
        public static bool Hovering(Vector2 min, Vector2 max) =>
            !Widgets.Disabled && ImGui.IsWindowHovered() && ImGui.IsMouseHoveringRect(min, max);

        /// <summary>Marks a combo's list as open this frame.</summary>
        public static void MarkOpen(uint id) => _open[id] = ImGui.GetFrameCount();

        static readonly Dictionary<uint, int> _open = new();

        /// <summary>Call right after the item, so the next frame's pill knows it is held.</summary>
        public static void Remember(uint id)
        {
            int frame = ImGui.GetFrameCount();
            if (ImGui.IsItemActive())
                _held[id] = frame;
            if (frame - _frame > 600)
            {
                _frame = frame;
                var stale = new List<uint>();
                foreach (var (key, f) in _held)
                    if (frame - f > 2)
                        stale.Add(key);
                foreach (var key in stale)
                    _held.Remove(key);
            }
        }

        /// <summary>Pushes transparent fills for an item whose look is drawn here instead.</summary>
        public static void PushClearFrame()
        {
            var clear = Vector4.Zero;
            ImGui.PushStyleColor(ImGuiCol.FrameBg, clear);
            ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, clear);
            ImGui.PushStyleColor(ImGuiCol.FrameBgActive, clear);
            ImGui.PushStyleColor(ImGuiCol.Button, clear);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, clear);
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, clear);
        }

        public static void PopClearFrame() => ImGui.PopStyleColor(6);

        /// <summary>
        /// A raised pill: a soft shadow under it, a vertical gradient and a light top edge. A held
        /// pill shrinks a little about its centre.
        /// </summary>
        public static void Pill(
            ImDrawListPtr dl,
            Vector2 min,
            Vector2 max,
            State state,
            float radius = -1
        )
        {
            var c = Colours;
            if (c == null || max.X - min.X < 1 || max.Y - min.Y < 1)
                return;
            if (state.Held)
            {
                var centre = (min + max) / 2;
                var half = (max - min) / 2;
                half = new Vector2(Math.Max(half.X - 3, half.X * 0.97f), half.Y * 0.94f);
                min = centre - half;
                max = centre + half;
            }
            float r = radius < 0 ? (max.Y - min.Y) / 2 : Math.Min(radius, (max.Y - min.Y) / 2);

            Shadow(dl, min, max, r, 1);

            Vector4 top,
                bottom;
            if (state.Danger)
            {
                top = state.Hot && !state.Held ? Theme.RedButtonHover : Theme.RedButtonBg;
                top = Vector4.Lerp(top, Vector4.One, 0.12f);
                bottom = Theme.RedButtonBg;
            }
            else if (state.Accent)
            {
                top = state.Hot && !state.Held ? c.AccentHotTop : c.AccentTop;
                bottom = c.AccentBottom;
            }
            else if (state.Held)
            {
                top = c.FrameHeldTop;
                bottom = c.FrameHeldBottom;
            }
            else if (state.Hot)
            {
                top = c.FrameHotTop;
                bottom = c.FrameHotBottom;
            }
            else
            {
                top = c.FrameTop;
                bottom = c.FrameBottom;
            }
            Gradient(dl, min, max, top, bottom, r);
            TopEdge(dl, min, max, r, c.Highlight);
        }

        /// <summary>The soft shadow a raised shape casts, mostly below it.</summary>
        public static void Shadow(
            ImDrawListPtr dl,
            Vector2 min,
            Vector2 max,
            float r,
            float strength
        )
        {
            var s = Colours.Shadow;
            float a = s.W * strength;
            dl.AddRectFilled(
                min + new Vector2(-0.5f, 1.5f),
                max + new Vector2(0.5f, 3f),
                Col(s, a * 0.35f),
                r + 1,
                All
            );
            dl.AddRectFilled(
                min + new Vector2(0, 0.5f),
                max + new Vector2(0, 1.5f),
                Col(s, a * 0.6f),
                r,
                All
            );
        }

        /// <summary>A rounded rect shading from <paramref name="top"/> to <paramref name="bottom"/>.</summary>
        public static void Gradient(
            ImDrawListPtr dl,
            Vector2 min,
            Vector2 max,
            Vector4 top,
            Vector4 bottom,
            float r,
            ImDrawCornerFlags corners = All
        )
        {
            corners = Widgets.Corners(corners);
            dl.AddRectFilled(min, max, ImGui.GetColorU32(bottom), r, corners);
            dl.AddImageRounded(
                SideOrderAssets.RampId,
                min,
                max,
                Vector2.Zero,
                Vector2.One,
                ImGui.GetColorU32(top),
                r,
                corners
            );
        }

        //The cards' rim light, at the strength the colour's alpha gives.
        static void TopEdge(ImDrawListPtr dl, Vector2 min, Vector2 max, float r, Vector4 colour) =>
            SideOrderSurface.RimLight(
                dl,
                min,
                max,
                r,
                colour with
                {
                    W = 1,
                },
                Math.Min(1, colour.W * 3.5f),
                colour.W * 0.3f
            );

        /// <summary>The combo's open arrow: a chevron in two strokes.</summary>
        public static void Chevron(ImDrawListPtr dl, Vector2 centre, float size, uint colour)
        {
            float h = size * 0.5f;
            dl.PathLineTo(centre + new Vector2(-size, -h));
            dl.PathLineTo(centre + new Vector2(0, h));
            dl.PathLineTo(centre + new Vector2(size, -h));
            dl.PathStroke(colour, false, 2.7f);
        }

        /// <summary>The chevron at the right end of a frame, where ImGui puts the arrow button.</summary>
        public static void FrameChevron(ImDrawListPtr dl, Vector2 min, Vector2 max)
        {
            float h = max.Y - min.Y;
            Chevron(
                dl,
                new Vector2(
                    MathF.Round(max.X - h * 0.62f),
                    MathF.Round((min.Y + max.Y) / 2) + 0.5f
                ),
                h * 0.18f,
                ImGui.GetColorU32(Colours.Text)
            );
        }

        /// <summary>A checkbox square: raised when off, the accent with a check when on.</summary>
        public static void CheckBox(ImDrawListPtr dl, Vector2 min, float side, bool on, State state)
        {
            var c = Colours;
            var max = min + new Vector2(side);
            const float r = 7;
            if (!on)
            {
                Pill(dl, min, max, state, r);
                return;
            }
            AccentShape(dl, min, max, r, state.Hot);
            float s = side;
            dl.PathLineTo(min + new Vector2(s * 0.27f, s * 0.52f));
            dl.PathLineTo(min + new Vector2(s * 0.43f, s * 0.68f));
            dl.PathLineTo(min + new Vector2(s * 0.73f, s * 0.35f));
            dl.PathStroke(ImGui.GetColorU32(c.Mark), false, MathF.Max(2.2f, s * 0.1f));
        }

        /// <summary>A radio circle: raised when off, filled with the accent when on.</summary>
        public static void Radio(
            ImDrawListPtr dl,
            Vector2 centre,
            float radius,
            bool on,
            State state
        )
        {
            var min = centre - new Vector2(radius);
            var max = centre + new Vector2(radius);
            if (!on)
            {
                Pill(dl, min, max, state with { Held = false }, radius);
                return;
            }
            AccentShape(dl, min, max, radius, state.Hot);
        }

        //A checked box or chosen radio: the accent, raised, at its own rounding.
        static void AccentShape(ImDrawListPtr dl, Vector2 min, Vector2 max, float r, bool hot)
        {
            var c = Colours;
            Shadow(dl, min, max, r, 1);
            Gradient(dl, min, max, hot ? c.AccentHotTop : c.AccentTop, c.AccentBottom, r);
            TopEdge(dl, min, max, r, c.Highlight);
        }

        /// <summary>
        /// The scrollbar thumb as a slim raised pill: a soft shadow, the gradient in the thumb
        /// colours and a light cap along its top.
        /// </summary>
        public static void ScrollThumb(ImDrawListPtr dl, Vector2 min, Vector2 max, State state)
        {
            var c = Colours;
            float r = MathF.Min(max.X - min.X, max.Y - min.Y) / 2;
            Shadow(dl, min, max, r, 0.8f);
            var top =
                state.Held ? c.ThumbHeldTop
                : state.Hot ? c.ThumbHotTop
                : c.ThumbTop;
            var bottom =
                state.Held ? c.ThumbHeldBottom
                : state.Hot ? c.ThumbHotBottom
                : c.ThumbBottom;
            Gradient(dl, min, max, top, bottom, r);
            TopEdge(dl, min, max, r, c.Highlight);
        }

        /// <summary>A sunken rounded well, the bed of a tab strip, a timeline or a progress bar.</summary>
        public static void Well(ImDrawListPtr dl, Vector2 min, Vector2 max, float radius = -1)
        {
            var c = Colours;
            float h = max.Y - min.Y;
            float r = radius < 0 ? h / 2 : Math.Min(radius, h / 2);
            dl.AddRectFilled(min, max, ImGui.GetColorU32(c.Well), r, All);
            //A darker upper edge sinks it, as the slider groove.
            Widgets.PushClip(
                dl,
                min - Vector2.One,
                new Vector2(max.X + 1, min.Y + Math.Min(h * 0.4f, 12))
            );
            dl.AddRect(
                min,
                max,
                ImGui.GetColorU32(c.Shadow * new Vector4(1, 1, 1, 0.45f)),
                r,
                All,
                1
            );
            dl.PopClipRect();
        }

        /// <summary>
        /// A slider: the flat well, the groove, the accent fill up to <paramref name="t"/> and a round
        /// white thumb whose centre is at <paramref name="thumbX"/>.
        /// </summary>
        public static void Slider(
            ImDrawListPtr dl,
            Vector2 wellMin,
            Vector2 wellMax,
            float trackMinX,
            float trackMaxX,
            float thumbX,
            State state,
            bool drawWell = true
        )
        {
            var c = Colours;
            float h = wellMax.Y - wellMin.Y;
            if (drawWell)
            {
                dl.AddRectFilled(wellMin, wellMax, ImGui.GetColorU32(c.SliderWell), h / 2, All);
                TopEdge(dl, wellMin, wellMax, h / 2, c.Highlight * new Vector4(1, 1, 1, 0.6f));
            }

            float cy = MathF.Round((wellMin.Y + wellMax.Y) / 2);
            const float groove = 7;
            var gMin = new Vector2(trackMinX, cy - groove / 2);
            var gMax = new Vector2(trackMaxX, cy + groove / 2);
            dl.AddRectFilled(gMin, gMax, ImGui.GetColorU32(c.Track), groove / 2, All);
            //A darker upper edge sinks the groove.
            Widgets.PushClip(dl, gMin - Vector2.One, new Vector2(gMax.X + 1, cy));
            dl.AddRect(
                gMin,
                gMax,
                ImGui.GetColorU32(c.Shadow * new Vector4(1, 1, 1, 0.5f)),
                groove / 2,
                All,
                1
            );
            dl.PopClipRect();

            float fillEnd = Math.Clamp(thumbX, trackMinX, trackMaxX);
            if (fillEnd > trackMinX + 1)
            {
                var fMax = new Vector2(fillEnd, gMax.Y);
                dl.AddRectFilledMultiColor(
                    new Vector2(gMin.X + groove / 2, gMin.Y),
                    new Vector2(Math.Max(gMin.X + groove / 2, fMax.X - groove / 2), fMax.Y),
                    ImGui.GetColorU32(c.TrackFillEnd),
                    ImGui.GetColorU32(c.TrackFill),
                    ImGui.GetColorU32(c.TrackFill),
                    ImGui.GetColorU32(c.TrackFillEnd)
                );
                dl.AddRectFilled(
                    gMin,
                    new Vector2(Math.Min(gMin.X + groove, fMax.X), fMax.Y),
                    ImGui.GetColorU32(c.TrackFillEnd),
                    groove / 2,
                    Widgets.Corners(ImDrawCornerFlags.Left)
                );
                dl.AddRectFilled(
                    new Vector2(Math.Max(gMin.X, fMax.X - groove), gMin.Y),
                    fMax,
                    ImGui.GetColorU32(c.TrackFill),
                    groove / 2,
                    Widgets.Corners(ImDrawCornerFlags.Right)
                );
            }

            float tr = MathF.Min(11, h / 2 - 3);
            if (state.Held)
                tr *= 0.94f;
            var tc = new Vector2(thumbX, cy);
            var tMin = tc - new Vector2(tr);
            var tMax = tc + new Vector2(tr);
            Shadow(dl, tMin, tMax, tr, 1.4f);
            var thumbBottom = Vector4.Lerp(c.Thumb, c.FrameBottom, state.Hot ? 0.25f : 0.5f);
            Gradient(dl, tMin, tMax, c.Thumb, thumbBottom, tr);
            TopEdge(dl, tMin, tMax, tr, c.Highlight);
        }

        /// <summary>
        /// The round play button: Halo, drop shadow and bevelled plate, with the
        /// play or pause symbol on it.
        /// </summary>
        public static void RoundButton(
            ImDrawListPtr dl,
            Vector2 min,
            Vector2 max,
            bool playing,
            State state
        )
        {
            var theme = SideOrderTheme.Current;
            var c = Colours;
            var size = max - min;
            //The disc a little smaller than the box, and the texture scaled so the disc, not the
            //image, has that size and sits on the box's centre.
            float disc = MathF.Min(size.X, size.Y) * 0.86f;
            if (state.Held)
                disc *= 0.94f;
            float k = disc / PlateDiameter;
            var centre = (min + max) / 2;
            var plateMin = centre - PlateCentre * k;
            var plateMax = plateMin + PlateTexture * k;
            dl.PushClipRectFullScreen();
            dl.AddImage(
                SideOrderAssets.GlowId,
                centre - new Vector2(HaloHalfSize * k) + new Vector2(0, 1.2f * k),
                centre + new Vector2(HaloHalfSize * k) + new Vector2(0, 1.2f * k),
                Vector2.Zero,
                Vector2.One,
                Col(theme.ButtonHalo, theme.IsLight ? 0.35f : 0.6f)
            );
            dl.AddImage(
                SideOrderAssets.ButtonBaseId,
                plateMin + new Vector2(0, 2 * k),
                plateMax + new Vector2(0, 2 * k),
                Vector2.Zero,
                Vector2.One,
                Col(theme.ButtonShadow, 0.8f)
            );
            //The bevelled plate texture tinted so its mean is the theme's button colour, a
            //touch lighter under the mouse.
            var tint = PlateTint(theme);
            if (state.Hot)
                tint = Vector4.Lerp(tint, Vector4.One, 0.08f);
            dl.AddImage(
                SideOrderAssets.ButtonSymbolId,
                plateMin,
                plateMax,
                Vector2.Zero,
                Vector2.One,
                ImGui.GetColorU32(tint)
            );
            dl.PopClipRect();

            //The icons, drawn from their view box over the disc's centre.
            float u = disc * 0.82f / IconBox;
            var origin = centre - new Vector2(IconBox / 2 - IconNudge, IconBox / 2) * u;
            DrawIcon(dl, origin, u, playing, ImGui.GetColorU32(c.Icon));
        }

        //Pause is two rounded bars; play a triangle rounded at its corners.
        static void DrawIcon(ImDrawListPtr dl, Vector2 origin, float u, bool pause, uint colour)
        {
            Vector2 P(float x, float y) => origin + new Vector2(x, y) * u;
            if (pause)
            {
                dl.AddRectFilled(P(5, 4), P(10, 20), colour, 1.5f * u);
                dl.AddRectFilled(P(14, 4), P(19, 20), colour, 1.5f * u);
                return;
            }
            dl.PathLineTo(P(5.5f, 4.6f));
            dl.PathLineTo(P(5.5f, 19.4f));
            dl.PathBezierCubicCurveTo(P(5.5f, 20.6f), P(6.8f, 21.3f), P(7.8f, 20.7f));
            dl.PathLineTo(P(19.2f, 13.3f));
            dl.PathBezierCubicCurveTo(P(20.1f, 12.7f), P(20.1f, 11.4f), P(19.2f, 10.8f));
            dl.PathLineTo(P(7.8f, 3.3f));
            dl.PathBezierCubicCurveTo(P(6.8f, 2.7f), P(5.5f, 3.4f), P(5.5f, 4.6f));
            dl.PathFillConvex(colour);
        }

        //buttonSymbol.png's size and its disc's; the disc is measured from the texture's alpha.
        static readonly Vector2 PlateTexture = new(100, 90);
        static readonly Vector2 PlateCentre = new(49.5f, 44f);
        const float PlateDiameter = 51;

        //The halo's half size in plate texels.
        const float HaloHalfSize = 64;

        //The icons' view box in units, and how far right of its centre they sit.
        const float IconBox = 24;
        const float IconNudge = 0.19f;

        static Vector4 PlateTint(SideOrderTheme theme)
        {
            var mean = new Vector3(200.1f, 175.0f, 170.7f) / 255f;
            var b = theme.Button;
            return new Vector4(
                Math.Min(1, b.X / mean.X),
                Math.Min(1, b.Y / mean.Y),
                Math.Min(1, b.Z / mean.Z),
                1
            );
        }

        /// <summary>A magnifying glass, for a search field.</summary>
        public static void Magnifier(ImDrawListPtr dl, Vector2 centre, float r, uint colour)
        {
            var c = centre + new Vector2(-r * 0.2f, -r * 0.2f);
            dl.AddCircle(c, r * 0.62f, colour, 16, 2.2f);
            dl.AddLine(c + new Vector2(r * 0.45f), c + new Vector2(r * 1.05f), colour, 2.6f);
        }

        /// <summary>An x, for clearing a field.</summary>
        public static void Cross(ImDrawListPtr dl, Vector2 centre, float r, uint colour)
        {
            dl.AddLine(centre - new Vector2(r), centre + new Vector2(r), colour, 2.2f);
            dl.AddLine(centre + new Vector2(-r, r), centre + new Vector2(r, -r), colour, 2.2f);
        }

        public static uint Col(Vector4 c, float alpha) =>
            ImGui.GetColorU32(new Vector4(c.X, c.Y, c.Z, Math.Clamp(alpha, 0, 1)));
    }
}
