using System;
using System.Numerics;

namespace PlayerViewer.UI
{
    /// <summary>sRGB and linear light, for the interface's colour math.</summary>
    static class ColourSpace
    {
        /// <summary>An sRGB colour in linear light.</summary>
        public static Vector3 Lin(Vector4 c) => new(ToLinear(c.X), ToLinear(c.Y), ToLinear(c.Z));

        public static float ToLinear(float c) =>
            c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);

        /// <summary>A linear value in sRGB, clamped to 0 to 1 first.</summary>
        public static float ToSrgb(float c)
        {
            c = Math.Clamp(c, 0, 1);
            return c <= 0.0031308f ? c * 12.92f : 1.055f * MathF.Pow(c, 1 / 2.4f) - 0.055f;
        }
    }
}
