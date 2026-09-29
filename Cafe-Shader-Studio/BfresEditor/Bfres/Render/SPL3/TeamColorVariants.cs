using System;
using System.Collections.Generic;
using System.Numerics;

namespace BfresEditor
{
    /// <summary>
    /// The team colour variants the game derives from a linear team colour: HSV offsets from the
    /// TeamColorOffset table, the hue direction peaks, and the darkened model colour.
    /// </summary>
    public static class TeamColorVariants
    {
        /// <summary>The game's variant list, in its order.</summary>
        public enum Kind
        {
            Original, Pale, Bright, Dark, HueBright, HueBrightHalf, HueDark, HueDarkHalf, Model,
            Ink, InkBright, InkLame, InkLameRare, Silhouette, LuminanceNormalized, MiniMapInk, UIEffect,
        }

        public const int KindCount = 17;

        /// <summary>One TeamColorOffset row.</summary>
        public readonly record struct Offset(float Hue, float Saturation, float Brightness);

        /// <summary>
        /// A colour set's hue settings for one team: HueOffset widens every nonzero hue offset,
        /// and with the detail flag on the four hue variants take their hue from DetailBright
        /// and DetailDark instead. All zero when the set's HueOffsetEnable is off.
        /// </summary>
        public readonly record struct HueOffset(bool Detail, float Offset, float DetailBright, float DetailDark);

        /// <summary>TeamColorOffset rows by name. The 11.3.0 table; the app loads the romfs one over it.</summary>
        public static Dictionary<string, Offset> Offsets = new Dictionary<string, Offset>
        {
            ["Bright"] = new Offset(0f, 0f, 0.1f),
            ["Dark"] = new Offset(0f, 0.5f, -0.2f),
            ["HueBright"] = new Offset(0.1f, 0f, 0.05f),
            ["HueBrightHalf"] = new Offset(0.05f, 0f, 0f),
            ["HueDark"] = new Offset(-0.1f, 0f, 0.05f),
            ["HueDarkHalf"] = new Offset(-0.05f, 0f, 0f),
            ["Ink"] = new Offset(0f, 0f, -0.25f),
            ["InkBright"] = new Offset(0f, 0f, 0f),
            ["InkLame"] = new Offset(0f, 0f, 0.1f),
            ["InkLameRare"] = new Offset(0.1f, 0f, 0.05f),
            ["Pale"] = new Offset(0f, -0.05f, 0.1f),
            ["Silhouette"] = new Offset(0f, -0.6f, 0f),
        };

        //TeamColorHueDirPeak: a hue offset is negated for hues strictly between the two peaks.
        const float BrightPeak = 0.2f;
        const float DarkPeak = 0.72f;

        //Defaults of the ink colour correction the model colour applies, as the binary registers them.
        const float DownBrightnessLuminanceRate = 0.35f;
        const float DownBrightnessRate1 = 0.1f;
        const float DownBrightnessRate6 = 0.5f;
        const float MinBright = 0.01f;

        /// <summary>
        /// One variant of a linear team colour. Ink and InkBright depend on the scene's ink
        /// parameters and are not derived here; they come back unchanged.
        /// </summary>
        public static Vector3 Get(Vector3 linear, Kind kind, HueOffset hue)
        {
            switch (kind)
            {
                case Kind.Original:
                case Kind.UIEffect:
                case Kind.Ink:
                case Kind.InkBright:
                    return linear;
                case Kind.Model:
                    return ModelColor(linear);
            }
            if (!Offsets.TryGetValue(kind.ToString(), out var row))
                return linear;

            float h = row.Hue;
            if (hue.Detail)
            {
                h = kind switch
                {
                    Kind.HueBright => hue.DetailBright,
                    Kind.HueBrightHalf => hue.DetailBright * 0.5f,
                    Kind.HueDark => hue.DetailDark,
                    Kind.HueDarkHalf => hue.DetailDark * 0.5f,
                    _ => h,
                };
            }
            else if (MathF.Abs(h) > 1.1920929e-7f)
                h = h > 0 ? h + hue.Offset : h - hue.Offset;
            return Shift(linear, true, h, row.Saturation, row.Brightness);
        }

        /// <summary>
        /// Offsets a colour in HSV. Saturation also loses the size of the value offset. With
        /// <paramref name="huePeak"/> the hue offset runs backwards between the peaks, so the
        /// bright hue variants turn towards the bright peak's hue and the dark ones towards the other.
        /// </summary>
        public static Vector3 Shift(Vector3 c, bool huePeak, float dh, float ds, float dv)
        {
            ToHsv(c, out float h, out float s, out float v);
            if (huePeak && BrightPeak < h && h < DarkPeak)
                dh = -dh;
            float s2 = Math.Clamp(ds - MathF.Abs(dv) + s, 0f, 1f);
            float v2 = MathF.Max(v + dv, 0f);
            return s2 == 0f ? new Vector3(v2) : FromHsv(h + dh, s2, v2);
        }

        /// <summary>Adds to a colour's HSV value, clamped to 0 to 1.</summary>
        public static Vector3 AddValue(Vector3 c, float dv)
        {
            ToHsv(c, out float h, out float s, out float v);
            float v2 = Math.Clamp(v + dv, 0f, 1f);
            return s == 0f ? new Vector3(v2) : FromHsv(h, s, v2);
        }

        //The model colour: the value drops by a fixed amount scaled down for a bright colour.
        static Vector3 ModelColor(Vector3 c)
        {
            ToHsv(c, out float h, out float s, out float v);
            float rate = Math.Clamp((6f * DownBrightnessRate1 - DownBrightnessRate6) / 5f, 0f, DownBrightnessRate6);
            float luminance = Math.Clamp(1f - (1f - LStar(c) / 100f) * DownBrightnessLuminanceRate, 0f, 1f);
            float v2 = Math.Clamp(v - MathF.Min(rate * luminance, v), 0f, 1f);
            v2 = MathF.Max(v2, MinBright);
            return s == 0f ? new Vector3(v2) : FromHsv(h, s, v2);
        }

        static float LStar(Vector3 c)
        {
            float y = c.Y * 0.7152f + c.X * 0.2126f + c.Z * 0.0722f;
            float f = y >= 0.0088565f ? MathF.Cbrt(y) : y * 7.787f + 0.13793f;
            return f * 116f - 16f;
        }

        static void ToHsv(Vector3 c, out float h, out float s, out float v)
        {
            float hi, lo, k;
            if (c.Y >= c.Z) { hi = c.Y; lo = c.Z; k = 0f; }
            else { hi = c.Z; lo = c.Y; k = -1f; }
            float mid, max, k2;
            if (c.X >= hi) { mid = hi; max = c.X; k2 = k; }
            else { mid = c.X; max = hi; k2 = -1f / 3f - k; }
            float d = max - MathF.Min(mid, lo);
            h = Math.Clamp(MathF.Abs(k2 + (mid - lo) / (d * 6f + 1e-20f)), 0f, 1f);
            s = Math.Clamp(d / (max + 1e-20f), 0f, 1f);
            v = max;
        }

        static Vector3 FromHsv(float h, float s, float v)
        {
            float x = (h - MathF.Floor(h)) / (1f / 6f);
            int sector = (int)x;
            float f = x - sector;
            float p = v * (1f - s);
            float q = v * (1f - s * f);
            float t = v * (1f - s * (1f - f));
            var c = sector switch
            {
                0 => new Vector3(v, t, p),
                1 => new Vector3(q, v, p),
                2 => new Vector3(p, v, t),
                3 => new Vector3(p, q, v),
                4 => new Vector3(t, p, v),
                _ => new Vector3(v, p, q),
            };
            return Vector3.Clamp(c, Vector3.Zero, Vector3.One);
        }
    }
}
