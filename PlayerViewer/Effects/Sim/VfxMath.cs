using System;
using System.Numerics;

namespace PlayerViewer.Effects.Sim
{
    /// <summary>
    /// A row major 4x3 transform as the effect runtime stores it: three axis rows and a
    /// translation row. A point maps as p' = p.x * R0 + p.y * R1 + p.z * R2 + T.
    /// </summary>
    public struct Matrix34
    {
        public Vector3 R0,
            R1,
            R2,
            T;

        public static readonly Matrix34 Identity = new()
        {
            R0 = Vector3.UnitX,
            R1 = Vector3.UnitY,
            R2 = Vector3.UnitZ,
        };

        public Vector3 GetRow(int i) =>
            i switch
            {
                0 => R0,
                1 => R1,
                2 => R2,
                _ => T,
            };

        /// <summary>A direction through the 3x3 part, accumulated with fused multiply adds as the runtime does.</summary>
        public readonly Vector3 TransformNormal(Vector3 v) =>
            VfxMath.Fma(R2, v.Z, VfxMath.Fma(R1, v.Y, R0 * v.X));

        public readonly Vector3 Transform(Vector3 v) => TransformNormal(v) + T;

        /// <summary>a then b, for row vectors.</summary>
        public static Matrix34 Multiply(in Matrix34 a, in Matrix34 b) =>
            new()
            {
                R0 = b.TransformNormal(a.R0),
                R1 = b.TransformNormal(a.R1),
                R2 = b.TransformNormal(a.R2),
                T = b.TransformNormal(a.T) + b.T,
            };

        /// <summary>Scale times rotation (x, then y, then z, in radians), then translation.</summary>
        public static Matrix34 CreateSrtXyz(Vector3 scale, Vector3 rotate, Vector3 translate)
        {
            VfxMath.FastSinCosVector(rotate.X, out float sx, out float cx);
            VfxMath.FastSinCosVector(rotate.Y, out float sy, out float cy);
            VfxMath.FastSinCosVector(rotate.Z, out float sz, out float cz);
            float cxcz = cx * cz;
            float sxsy = sx * sy;
            float cxsz = cx * sz;
            return new Matrix34
            {
                R0 = new Vector3(cy * cz, cy * sz, -sy) * scale.X,
                R1 = new Vector3(sxsy * cz - cxsz, sxsy * sz + cxcz, sx * cy) * scale.Y,
                R2 = new Vector3(cxcz * sy + sx * sz, cxsz * sy - sx * cz, cx * cy) * scale.Z,
                T = translate,
            };
        }

        /// <summary>The same axes normalised, the translation kept.</summary>
        public readonly Matrix34 WithoutScale()
        {
            static Vector3 N(Vector3 v)
            {
                float l = MathF.Sqrt(VfxMath.Dot(v, v));
                return l > 0 ? v * (1f / l) : Vector3.Zero;
            }
            return new Matrix34
            {
                R0 = N(R0),
                R1 = N(R1),
                R2 = N(R2),
                T = T,
            };
        }

        /// <summary>The inverse of an affine transform; zero when singular.</summary>
        public readonly Matrix34 Inverse()
        {
            var m = new Matrix4x4(
                R0.X,
                R0.Y,
                R0.Z,
                0,
                R1.X,
                R1.Y,
                R1.Z,
                0,
                R2.X,
                R2.Y,
                R2.Z,
                0,
                T.X,
                T.Y,
                T.Z,
                1
            );
            if (!Matrix4x4.Invert(m, out var inv))
                return default;
            return new Matrix34
            {
                R0 = new Vector3(inv.M11, inv.M12, inv.M13),
                R1 = new Vector3(inv.M21, inv.M22, inv.M23),
                R2 = new Vector3(inv.M31, inv.M32, inv.M33),
                T = new Vector3(inv.M41, inv.M42, inv.M43),
            };
        }

        public readonly bool Equals(in Matrix34 o) =>
            R0 == o.R0 && R1 == o.R1 && R2 == o.R2 && T == o.T;
    }

    /// <summary>
    /// The arithmetic the effect runtime does, reproduced where the order or the approximation
    /// changes the bits: its sine and cosine polynomial, its vector normalise through the
    /// reciprocal square root estimate, and the order it sums a dot product in.
    /// </summary>
    public static class VfxMath
    {
        public const float Pi = 3.14159265358979323846f;
        public const float TwoPi = 6.28318530717958647692f;
        public const float HalfPi = 1.57079632679489661923f;
        public const float OneOverTwoPi = 0.159154943091895335768f;

        static readonly float[] SinCoefficients =
        {
            2.3889859e-08f,
            2.7525562e-06f,
            0.00019840874f,
            0.0083333310f,
            0.16666667f,
        };

        static readonly float[] CosCoefficients =
        {
            2.6051615e-07f,
            2.4760495e-05f,
            0.0013888378f,
            0.041666638f,
            0.5f,
        };

        /// <summary>The runtime's sine and cosine: reduced to [-pi/2, pi/2] and a polynomial.</summary>
        public static void FastSinCos(float x, out float sin, out float cos)
        {
            float y = MathF.FusedMultiplyAdd(
                -TwoPi,
                MathF.Round(x * OneOverTwoPi, MidpointRounding.AwayFromZero),
                x
            );
            float sign = 1f;
            if (y > HalfPi)
            {
                y = Pi - y;
                sign = -1f;
            }
            else if (y < -HalfPi)
            {
                y = -Pi - y;
                sign = -1f;
            }
            float yy = y * y;
            var s = SinCoefficients;
            var c = CosCoefficients;
            float sp = MathF.FusedMultiplyAdd(-s[0], yy, s[1]);
            sp = MathF.FusedMultiplyAdd(sp, yy, -s[2]);
            sp = MathF.FusedMultiplyAdd(sp, yy, s[3]);
            sp = MathF.FusedMultiplyAdd(sp, yy, -s[4]);
            sin = y * MathF.FusedMultiplyAdd(sp, yy, 1f);
            float cp = MathF.FusedMultiplyAdd(-c[0], yy, c[1]);
            cp = MathF.FusedMultiplyAdd(cp, yy, -c[2]);
            cp = MathF.FusedMultiplyAdd(cp, yy, c[3]);
            cp = MathF.FusedMultiplyAdd(cp, yy, -c[4]);
            cos = sign * MathF.FusedMultiplyAdd(cp, yy, 1f);
        }

        /// <summary>
        /// The vectorised form the matrix builders use: round half to even, and every multiply
        /// add fused.
        /// </summary>
        public static void FastSinCosVector(float x, out float sin, out float cos)
        {
            float q = MathF.Round(x * OneOverTwoPi, MidpointRounding.ToEven);
            float y = MathF.FusedMultiplyAdd(-q, TwoPi, x);
            bool neg = false;
            if (y > HalfPi)
            {
                y = Pi - y;
                neg = true;
            }
            if (-HalfPi > y)
            {
                y = -Pi - y;
                neg = true;
            }
            float yy = y * y;
            var s = SinCoefficients;
            var c = CosCoefficients;
            float cp = MathF.FusedMultiplyAdd(-yy, c[0], c[1]);
            cp = MathF.FusedMultiplyAdd(yy, cp, -c[2]);
            cp = MathF.FusedMultiplyAdd(yy, cp, c[3]);
            cp = MathF.FusedMultiplyAdd(yy, cp, -c[4]);
            cp = MathF.FusedMultiplyAdd(yy, cp, 1f);
            float sp = MathF.FusedMultiplyAdd(-yy, s[0], s[1]);
            sp = MathF.FusedMultiplyAdd(yy, sp, -s[2]);
            sp = MathF.FusedMultiplyAdd(yy, sp, s[3]);
            sp = MathF.FusedMultiplyAdd(yy, sp, -s[4]);
            sin = y * MathF.FusedMultiplyAdd(yy, sp, 1f);
            cos = neg ? -cp : cp;
        }

        public static Vector3 Fma(Vector3 a, float b, Vector3 c) =>
            new(
                MathF.FusedMultiplyAdd(a.X, b, c.X),
                MathF.FusedMultiplyAdd(a.Y, b, c.Y),
                MathF.FusedMultiplyAdd(a.Z, b, c.Z)
            );

        public static float FastSin(float x)
        {
            FastSinCos(x, out float s, out _);
            return s;
        }

        public static float FastCos(float x)
        {
            FastSinCos(x, out _, out float c);
            return c;
        }

        public static float DegToRad(float d) => d * (Pi / 180f);

        /// <summary>A three component dot product summed as the runtime's NEON code does.</summary>
        public static float Dot(Vector3 a, Vector3 b) => (a.X * b.X + a.Z * b.Z) + a.Y * b.Y;

        public static float Length(Vector3 v) => MathF.Sqrt(Dot(v, v));

        /// <summary>The runtime's normalise: estimate, two Newton steps, zero for a zero vector.</summary>
        public static Vector3 Normalize(Vector3 v)
        {
            float d = Dot(v, v);
            if (d == 0)
                return Vector3.Zero;
            float e = Frsqrte(d);
            e *= Frsqrts(e, e * d);
            e *= Frsqrts(e, d * e);
            return v * e;
        }

        /// <summary>FRSQRTS: (3 - a * b) / 2 with one rounding.</summary>
        public static float Frsqrts(float a, float b) => MathF.FusedMultiplyAdd(-a, b, 3f) * 0.5f;

        /// <summary>FRSQRTE of the console's CPU: an 8 bit reciprocal square root estimate.</summary>
        public static float Frsqrte(float x)
        {
            uint bits = BitConverter.SingleToUInt32Bits(x);
            if (float.IsNaN(x) || (x < 0 && x != 0))
                return float.NaN;
            if (x == 0)
                return (bits & 0x80000000) != 0 ? float.NegativeInfinity : float.PositiveInfinity;
            if (float.IsPositiveInfinity(x))
                return 0f;
            int exp = (int)((bits >> 23) & 0xFF);
            ulong fraction = (ulong)(bits & 0x7FFFFF) << 29;
            if (exp == 0)
            {
                while ((fraction & (1UL << 51)) == 0)
                {
                    fraction <<= 1;
                    exp--;
                }
                fraction = (fraction << 1) & ((1UL << 52) - 1);
            }
            int scaled =
                (exp & 1) == 0
                    ? (int)(0x100 | ((fraction >> 44) & 0xFF))
                    : (int)(0x80 | ((fraction >> 45) & 0x7F));
            int resultExp = (380 - exp) / 2;
            int estimate = RecipSqrtEstimate(scaled);
            uint result = ((uint)(resultExp & 0xFF) << 23) | ((uint)(estimate & 0xFF) << 15);
            return BitConverter.UInt32BitsToSingle(result);
        }

        static int RecipSqrtEstimate(int a)
        {
            if (a < 256)
                a = a * 2 + 1;
            else
            {
                a = (a >> 1) << 1;
                a = (a + 1) * 2;
            }
            long b = 512;
            while (a * (b + 1) * (b + 1) < (1L << 28))
                b++;
            return (int)((b + 1) / 2);
        }

        /// <summary>
        /// Rotates <paramref name="v"/> by the rotation taking +Y onto <paramref name="dir"/>,
        /// built as the runtime builds it: an unnormalised quaternion, then the matrix with
        /// 2/|q|^2. A half turn about X when the two are opposite.
        /// </summary>
        public static Vector3 RotateFromUp(Vector3 v, Vector3 dir)
        {
            float d = dir.Y + 1f;
            float qx,
                qy,
                qz,
                qw;
            if (d <= 1.1920929e-07f)
            {
                qx = 1;
                qy = qz = qw = 0;
            }
            else
            {
                float s = MathF.Sqrt(d + d);
                qx = dir.Z / s;
                qy = 0;
                qz = -dir.X / s;
                qw = s * 0.5f;
            }
            float n = qx * qx + qy * qy + qz * qz + qw * qw;
            float k = 2f / n;
            float xx = qx * qx * k,
                yy = qy * qy * k,
                zz = qz * qz * k;
            float xy = qx * qy * k,
                xz = qx * qz * k,
                yz = qy * qz * k;
            float wx = qw * qx * k,
                wy = qw * qy * k,
                wz = qw * qz * k;
            var r0 = new Vector3(1 - (yy + zz), xy + wz, xz - wy);
            var r1 = new Vector3(xy - wz, 1 - (xx + zz), yz + wx);
            var r2 = new Vector3(xz + wy, yz - wx, 1 - (xx + yy));
            return r0 * v.X + r1 * v.Y + r2 * v.Z;
        }
    }
}
