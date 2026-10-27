using System;
using System.Numerics;
using Toolbox.Core;

namespace PlayerViewer.Textures
{
    /// <summary>Unpacks the uncompressed float formats to linear RGBA.</summary>
    public static class FloatTexels
    {
        /// <summary>
        /// The first <paramref name="count"/> texels of a deswizzled surface, or null when the
        /// format is not one of these or the surface is short.
        /// </summary>
        public static Vector4[] Decode(byte[] data, TexFormat format, int count)
        {
            if (data == null)
                return null;
            var texels = new Vector4[count];
            if (format == TexFormat.RG11B10_FLOAT)
            {
                if (data.Length < count * 4)
                    return null;
                for (int i = 0; i < count; i++)
                    texels[i] = new Vector4(R11G11B10(BitConverter.ToUInt32(data, i * 4)), 1);
                return texels;
            }
            if (format == TexFormat.RGBA16_FLOAT)
            {
                if (data.Length < count * 8)
                    return null;
                for (int i = 0; i < count; i++)
                {
                    int o = i * 8;
                    texels[i] = new Vector4(
                        (float)BitConverter.ToHalf(data, o),
                        (float)BitConverter.ToHalf(data, o + 2),
                        (float)BitConverter.ToHalf(data, o + 4),
                        (float)BitConverter.ToHalf(data, o + 6)
                    );
                }
                return texels;
            }
            return null;
        }

        /// <summary>Red in bits 0 to 10, green 11 to 21, blue 22 to 31; no sign bits.</summary>
        public static Vector3 R11G11B10(uint packed) =>
            new(
                SmallFloat(packed & 0x7FF, 6),
                SmallFloat((packed >> 11) & 0x7FF, 6),
                SmallFloat(packed >> 22, 5)
            );

        /// <summary>The inverse of <see cref="R11G11B10(uint)"/>; negatives and NaN become 0.</summary>
        public static uint PackR11G11B10(Vector3 c) =>
            ToSmallFloat(c.X, 6) | (ToSmallFloat(c.Y, 6) << 11) | (ToSmallFloat(c.Z, 5) << 22);

        //A half's exponent and top mantissa bits, rounded to nearest.
        static uint ToSmallFloat(float value, int mantissaBits)
        {
            if (!(value > 0))
                return 0;
            int drop = 10 - mantissaBits;
            uint half = BitConverter.HalfToUInt16Bits((Half)MathF.Min(value, 65000f)) & 0x7FFFu;
            uint rounded = (half + (1u << (drop - 1))) >> drop;
            return Math.Min(rounded, (30u << mantissaBits) | ((1u << mantissaBits) - 1));
        }

        //An unsigned float with a five bit exponent, biased 15 like a half.
        static float SmallFloat(uint bits, int mantissaBits)
        {
            uint mantissa = bits & ((1u << mantissaBits) - 1);
            uint exponent = bits >> mantissaBits;
            float m = mantissa / (float)(1 << mantissaBits);
            if (exponent == 0)
                return m * (1f / 16384f);
            if (exponent == 31)
                return mantissa == 0 ? float.PositiveInfinity : float.NaN;
            return MathF.ScaleB(1 + m, (int)exponent - 15);
        }
    }
}
