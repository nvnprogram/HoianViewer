using System;
using System.Numerics;
using ImGuiNET;

namespace PlayerViewer.UI
{
    public enum SurfaceKind
    {
        /// <summary>A panel on the background.</summary>
        Card,

        /// <summary>The 3D viewport, whose image is the fill; only the shadow and the rim draw.</summary>
        Viewport,
    }

    /// <summary>
    /// The Side Order panel stack as ImGui draw list primitives: shadow, fill, a lighter top, glow
    /// bloom, the wobbling frost sheen and a rim light, blended in sRGB with no blur.
    /// </summary>
    public static class SideOrderSurface
    {
        //A rim light's strength along the top and elsewhere on the light and the dark themes,
        //and how far it lightens the frost colour toward white.
        readonly record struct RimStyle(
            float Lighten,
            float LightTop,
            float LightRest,
            float DarkTop,
            float DarkRest
        );

        static readonly RimStyle CardRim = new(0.5f, 1f, 0.25f, 0.75f, 0.06f);
        static readonly RimStyle ViewportRim = new(0.6f, 0.9f, 0.15f, 0.65f, 0.05f);

        /// <summary>Draws the whole surface. <paramref name="seed"/> keeps panels from wobbling in step.</summary>
        public static void Draw(
            ImDrawListPtr dl,
            Vector2 min,
            Vector2 max,
            float radius,
            int seed,
            SurfaceKind kind
        )
        {
            var theme = SideOrderTheme.Current;
            if (theme == null || !SideOrderAssets.Ready || max.X - min.X < 2 || max.Y - min.Y < 2)
                return;
            radius = Math.Min(radius, Math.Min(max.X - min.X, max.Y - min.Y) / 2);
            dl.PushClipRectFullScreen();
            Shadow(dl, min, max, radius, theme, kind);
            if (kind != SurfaceKind.Viewport)
            {
                Fill(dl, min, max, radius, theme);
                Sheen(dl, min, max, radius, seed, theme);
                Rim(dl, min, max, radius, theme, CardRim);
            }
            dl.PopClipRect();
        }

        //The edge colour falling off outside the edge, in two pixel rings.
        static void Shadow(
            ImDrawListPtr dl,
            Vector2 min,
            Vector2 max,
            float radius,
            SideOrderTheme theme,
            SurfaceKind kind
        )
        {
            //Deeper and dropped a little below a card; the viewport keeps the centred rim shadow.
            bool card = kind == SurfaceKind.Card;
            float peak = theme.IsLight ? (card ? 0.34f : 0.30f) : (card ? 0.5f : 0.45f);
            float reach = card ? 6f : 4f;
            var drop = new Vector2(0, card ? 2 : 0);
            for (float o = 1; o < 18; o += 2)
            {
                float a = peak * MathF.Exp(-(o - 1) / reach);
                var grow = new Vector2(o);
                dl.AddRect(
                    min - grow + drop * (o / 18),
                    max + grow + drop * (o / 9),
                    SideOrderControls.Col(theme.Edge, a),
                    radius + o,
                    ImDrawCornerFlags.All,
                    2
                );
            }
        }

        static void Fill(
            ImDrawListPtr dl,
            Vector2 min,
            Vector2 max,
            float radius,
            SideOrderTheme theme
        )
        {
            dl.AddRectFilled(min, max, FillColour(theme), radius, ImDrawCornerFlags.All);

            var frost = theme.Frost;
            dl.AddImageRounded(
                SideOrderAssets.RampId,
                min,
                max,
                Vector2.Zero,
                Vector2.One,
                SideOrderControls.Col(frost, theme.IsLight ? 0.22f : 0.12f),
                radius,
                ImDrawCornerFlags.All
            );

            //The lower half darkens toward the bottom edge.
            dl.AddImageRounded(
                SideOrderAssets.RampId,
                new Vector2(min.X, (min.Y + max.Y) / 2),
                max,
                new Vector2(0, 1),
                new Vector2(1, 0),
                SideOrderControls.Col(
                    theme.IsLight ? theme.Edge : Vector4.Zero,
                    theme.IsLight ? 0.13f : 0.2f
                ),
                radius,
                Widgets.Corners(ImDrawCornerFlags.Bot)
            );

            //The glow bloom: glow.png stretched so the box spans the middle 80% of it.
            const float bloom = 0.35f;
            dl.AddImageRounded(
                SideOrderAssets.GlowId,
                min,
                max,
                new Vector2(0.09f, 0.1f),
                new Vector2(0.89f, 0.9f),
                SideOrderControls.Col(theme.Glow, 0.7843f * bloom),
                radius,
                ImDrawCornerFlags.All
            );
        }

        //The frost sheen: glow.png's middle, nearly flat across and fading gently top and bottom.
        //The noise nudges its lookup and its silhouette, so the bright edge shimmers.
        static void Sheen(
            ImDrawListPtr dl,
            Vector2 min,
            Vector2 max,
            float radius,
            int seed,
            SideOrderTheme theme
        )
        {
            float hw = (max.X - min.X) / 2,
                h = max.Y - min.Y;
            double t = SideOrderLayout.Time;
            var n = SideOrderAssets.SampleNoise(
                0.5f + SideOrderLayout.Wrap(seed * 0.173),
                0.938f + SideOrderLayout.Wrap(SideOrderLayout.NoiseScroll * t)
            );
            var d = (n * 0.007f - new Vector2(0.0035f)) * SideOrderLayout.WobbleGain(h);
            var shift = new Vector2(d.X * 160, -d.Y * h);
            float u = SideOrderLayout.SheenUvPerPixel * hw;
            dl.AddImageRounded(
                SideOrderAssets.GlowId,
                min + shift,
                max + shift,
                new Vector2(0.5f - u, 0.31f) + d,
                new Vector2(0.5f + u, 0.69f) + d,
                SideOrderControls.Col(
                    theme.Frost,
                    theme.PanelSheenAlpha * (theme.IsLight ? 0.45f : 0.6f)
                ),
                radius,
                ImDrawCornerFlags.All
            );
        }

        //A rim light in the frost colour, strongest along the top.
        static void Rim(
            ImDrawListPtr dl,
            Vector2 min,
            Vector2 max,
            float radius,
            SideOrderTheme theme,
            RimStyle style
        )
        {
            var light = Vector4.Lerp(theme.Frost, Vector4.One, style.Lighten);
            if (theme.IsLight)
                RimLight(dl, min, max, radius, light, style.LightTop, style.LightRest);
            else
                RimLight(dl, min, max, radius, light, style.DarkTop, style.DarkRest);
        }

        /// <summary>
        /// A thin line just inside a rounded rect, lit as a glass edge catches the light:
        /// <paramref name="top"/> where the edge faces the light, <paramref name="rest"/> where it
        /// does not. One quad, shaded per pixel by the rim program (<see cref="SideOrderAssets"/>).
        /// </summary>
        public static void RimLight(
            ImDrawListPtr dl,
            Vector2 min,
            Vector2 max,
            float radius,
            Vector4 colour,
            float top,
            float rest
        )
        {
            if (!SideOrderAssets.Ready || max.X - min.X < 1 || max.Y - min.Y < 1)
                return;
            radius = Math.Max(0, Math.Min(radius, Math.Min(max.X - min.X, max.Y - min.Y) / 2));
            float restLevel = Math.Clamp(colour.W * rest * ImGui.GetStyle().Alpha, 0, 1);
            var code = new Vector2(MathF.Round(radius * 4), MathF.Round(restLevel * 255)) * 4;
            dl.AddImage(
                SideOrderAssets.RimId,
                min,
                max,
                code - Vector2.One,
                code + Vector2.One,
                ImGui.GetColorU32(colour with { W = Math.Clamp(colour.W * top, 0, 1) })
            );
        }

        /// <summary>
        /// A floating window or popup as a card: the card's shadow outside its edge and the rim
        /// on it. ImGui has drawn the fill, so nothing here covers the contents.
        /// </summary>
        public static void DrawWindowDecor(ImDrawListPtr dl, Vector2 min, Vector2 max, float radius)
        {
            var theme = SideOrderTheme.Current;
            if (theme == null || max.X - min.X < 2 || max.Y - min.Y < 2)
                return;
            radius = Math.Min(radius, Math.Min(max.X - min.X, max.Y - min.Y) / 2);
            dl.PushClipRectFullScreen();
            Shadow(dl, min, max, radius, theme, SurfaceKind.Card);
            Rim(dl, min, max, radius, theme, CardRim);
            dl.PopClipRect();
        }

        /// <summary>The viewport's edge over its image: a rim light, strongest along the top.</summary>
        public static void DrawBevel(ImDrawListPtr dl, Vector2 min, Vector2 max, float radius)
        {
            if (SideOrderTheme.Current is { } theme)
                Rim(dl, min, max, radius, theme, ViewportRim);
        }

        /// <summary>
        /// The panel fill: the panel colour blended over the darkened backdrop the way a
        /// blurred panel is, without the blur.
        /// </summary>
        static uint FillColour(SideOrderTheme theme)
        {
            float mixAlpha = theme.PanelMix * 0.9411766f;
            var bg = ColourSpace.Lin(theme.Bg) * theme.BgDarken;
            float lum = 0.2126f * bg.X + 0.7152f * bg.Y + 0.0722f * bg.Z;
            float alpha = 1 - (1 - mixAlpha) * (1 - theme.BlurDarken * lum);
            var panel = ColourSpace.Lin(theme.Panel) * (theme.CardShade * mixAlpha / alpha);
            return ImGui.GetColorU32(
                new Vector4(
                    ColourSpace.ToSrgb(panel.X),
                    ColourSpace.ToSrgb(panel.Y),
                    ColourSpace.ToSrgb(panel.Z),
                    alpha
                )
            );
        }
    }
}
