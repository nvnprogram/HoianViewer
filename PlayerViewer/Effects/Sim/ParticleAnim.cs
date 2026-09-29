using System;
using System.Numerics;
using System.Runtime.InteropServices;
using EffectLibrary;

namespace PlayerViewer.Effects.Sim
{
    /// <summary>
    /// The runtime's CPU copies of the per particle look the shaders compute: scale, the two
    /// colours and the current rotation at a particle's age.  Keys and wave parameters come from
    /// the rewritten static block, the flags and fixed colours from the raw resource.
    /// </summary>
    public static class ParticleAnim
    {
        /// <summary>Static block offsets are taken from here: the runtime keeps a pointer 0x70 into the block.</summary>
        const int S = 0x70;

        /// <summary>Scale at <paramref name="time"/>: the particle's base scale, the scale keys, then the fluctuation.</summary>
        public static Vector3 Scale(
            EmitterDef d,
            Vector3 baseScale,
            Vector4 random,
            float life,
            float time
        )
        {
            var b = d.StaticBlock;
            var r = d.Source.Section;
            var scale = baseScale;
            int keys = I(b, S + 0x20);
            if (keys < 2)
                scale *= V3(b, EmitterLayout.ScaleKeys);
            else
                scale *= Key8(
                    b,
                    EmitterLayout.ScaleKeys,
                    keys,
                    r.GetU8(0xC3C),
                    KeyTime(r, 0xC1C, 0xC21, 0xC34, random.X, life, time)
                );
            if (r.GetU8(0xD85) == 0)
                return scale;
            int wave = r.GetU8(0xD87) >> 4;
            float x = Wave(
                wave,
                F(b, 0x100),
                F(b, 0x108),
                F(b, 0x110),
                F(b, 0x118),
                random.X,
                time
            );
            float y =
                r.GetU8(0xD86) != 0
                    ? Wave(wave, F(b, 0x104), F(b, 0x10C), F(b, 0x114), F(b, 0x11C), random.X, time)
                    : x;
            return new Vector3(scale.X * x, scale.Y * y, scale.Z);
        }

        /// <summary>Colour 0 (rgb, alpha in w) at <paramref name="time"/>.</summary>
        public static Vector4 Color0(EmitterDef d, Vector4 random, float life, float time) =>
            AlphaWave(d, BaseColor(d, 0, random, life, time), random, time);

        /// <summary>Colour 1 (rgb, alpha in w) at <paramref name="time"/>.</summary>
        public static Vector4 Color1(EmitterDef d, Vector4 random, float life, float time) =>
            AlphaWave(d, BaseColor(d, 1, random, life, time), random, time);

        /// <summary>
        /// A colour as a child inherits it: the particle colour times the static colour scale and
        /// the emitter's colour scale and animated colour, then the alpha wave.
        /// </summary>
        public static Vector4 EmitterColor(
            EmitterDef d,
            int which,
            Vector4 random,
            Vector4 emitterScale,
            Vector3 animColor,
            float animAlpha,
            float life,
            float time
        )
        {
            var c = BaseColor(d, which, random, life, time);
            float k = F(d.StaticBlock, 0x670);
            c = new Vector4(
                (k * (emitterScale.X * animColor.X)) * c.X,
                (k * (emitterScale.Y * animColor.Y)) * c.Y,
                (k * (emitterScale.Z * animColor.Z)) * c.Z,
                (emitterScale.W * animAlpha) * c.W
            );
            return AlphaWave(d, c, random, time);
        }

        static Vector4 BaseColor(EmitterDef d, int which, Vector4 random, float life, float time)
        {
            var b = d.StaticBlock;
            var r = d.Source.Section;
            int fixedAt = 0xD40 + 0x10 * which;
            var c = new Vector4(
                r.GetF32(fixedAt),
                r.GetF32(fixedAt + 4),
                r.GetF32(fixedAt + 8),
                r.GetF32(fixedAt + 12)
            );
            int colorTable = which == 0 ? EmitterLayout.Color0Keys : EmitterLayout.Color1Keys;
            int alphaTable = which == 0 ? EmitterLayout.Alpha0Keys : EmitterLayout.Alpha1Keys;
            int colorKeys = I(b, EmitterLayout.KeyCounts + 8 * which);
            int alphaKeys = I(b, EmitterLayout.KeyCounts + 8 * which + 4);
            byte colorType = r.GetU8(0xD3C + which);
            if (colorType == 3)
            {
                int k = (int)(random.X * colorKeys);
                var v = V3(b, colorTable + 16 * k);
                c = new Vector4(v, c.W);
            }
            if (colorType == 2 && colorKeys >= 1)
            {
                var v = Key8(
                    b,
                    colorTable,
                    colorKeys,
                    r.GetU8(0xC38 + 2 * which),
                    KeyTime(
                        r,
                        0xC18 + 2 * which,
                        0xC1D + 2 * which,
                        0xC24 + 8 * which,
                        random.X,
                        life,
                        time
                    )
                );
                c = new Vector4(v, c.W);
            }
            if (r.GetU8(0xD3E + which) == 2 && alphaKeys >= 1)
                c.W = Key8(
                    b,
                    alphaTable,
                    alphaKeys,
                    r.GetU8(0xC39 + 2 * which),
                    KeyTime(
                        r,
                        0xC19 + 2 * which,
                        0xC1E + 2 * which,
                        0xC28 + 8 * which,
                        random.X,
                        life,
                        time
                    )
                ).X;
            return c;
        }

        /// <summary>The alpha fluctuation, clamped to [0, 1]; nothing when it is off.</summary>
        static Vector4 AlphaWave(EmitterDef d, Vector4 c, Vector4 random, float time)
        {
            var b = d.StaticBlock;
            var r = d.Source.Section;
            if (r.GetU8(0xD84) == 0)
                return c;
            int wave = r.GetU8(0xD87) >> 4;
            float a = wave is 0 or 1 or 2
                ? Wave(wave, F(b, 0x100), F(b, 0x108), F(b, 0x110), F(b, 0x118), random.X, time)
                    * c.W
                : c.W;
            c.W =
                a < 0 ? 0
                : a > 1 ? 1
                : a;
            return c;
        }

        /// <summary>
        /// The current rotation in radians: the particle's initial rotation, its fluctuations, the
        /// random initial spread and the rotation speed integrated against air resistance.
        /// </summary>
        public static Vector3 Rotation(EmitterDef d, Vector4 initRotate, Vector4 random, float time)
        {
            var b = d.StaticBlock;
            var r = d.Source.Section;
            var initRand = V3(b, 0xA10);
            var add = V3(b, 0xA20);
            float regist = F(b, 0xA2C);
            var addRand = V3(b, 0xA30);
            float spinAge;
            if (regist == 0)
                spinAge = 0;
            else if (regist == 1)
                spinAge = time;
            else
                spinAge = (1f - MathF.Pow(regist, time)) / (1f - regist);
            float x = initRotate.X,
                y = initRotate.Y,
                z = initRotate.Z;
            // The y fluctuation adds to x, as the runtime has it.
            if (r.GetU8(0xC40) != 0)
                x += RotateWave(r.GetU8(0xC43) >> 4, b, 0, random.X, time);
            if (r.GetU8(0xC41) != 0)
                x += RotateWave(r.GetU8(0xC44) >> 4, b, 1, random.X, time);
            if (r.GetU8(0xC42) != 0)
                z += RotateWave(r.GetU8(0xC45) >> 4, b, 2, random.X, time);

            float ax = add.X + ((random.X + random.Y) * 0.5f) * addRand.X;
            float ay = add.Y + ((random.Y + random.Z) * 0.5f) * addRand.Y;
            float az = add.Z + ((random.X + random.Z) * 0.5f) * addRand.Z;
            if (random.Z >= 0.5f && r.GetU8(0xBED) != 0)
            {
                ax = -ax;
                x = -x;
            }
            if (random.X >= 0.5f && r.GetU8(0xBEE) != 0)
            {
                ay = -ay;
                y = -y;
            }
            if (random.Y >= 0.5f && r.GetU8(0xBEF) != 0)
            {
                az = -az;
                z = -z;
            }
            return new Vector3(
                (x + (random.X - 0.5f) * initRand.X) + ax * spinAge,
                (y + (random.Y - 0.5f) * initRand.Y) + ay * spinAge,
                (z + (random.Z - 0.5f) * initRand.Z) + az * spinAge
            );
        }

        static float RotateWave(int wave, byte[] b, int axis, float random, float time)
        {
            float amp = F(b, 0xA50 + 4 * axis);
            float cycle = F(b, 0xA60 + 4 * axis);
            float phase = F(b, 0xA70 + 4 * axis);
            float phaseRandom = F(b, 0xA80 + 4 * axis);
            float t = (phase + time) / cycle + phaseRandom * random;
            float v;
            if (wave is 2 or 4)
            {
                float h = t - MathF.Floor(t) >= 0.5f ? -0.5f : 0.5f;
                v = amp * h + amp * h;
            }
            else
                v = amp * VfxMath.FastSin(VfxMath.Pi * t + VfxMath.Pi * t);
            return v * VfxMath.Pi;
        }

        /// <summary>
        /// One fluctuation factor: a raised cosine, a sawtooth or a square wave of the given
        /// amplitude, 1 at rest.
        /// </summary>
        static float Wave(
            int wave,
            float amp,
            float cycle,
            float phaseRandom,
            float phase,
            float random,
            float time
        )
        {
            float t = (phase + time) / cycle + phaseRandom * random;
            switch (wave)
            {
                case 0:
                    return (VfxMath.FastCos(VfxMath.Pi * t + VfxMath.Pi * t) + 1f) * -0.5f * amp
                        + 1f;
                case 1:
                    return MathF.Abs(1f - (t - MathF.Floor(t)) * amp);
                case 2:
                    return MathF.Abs(1f - (t - MathF.Floor(t) < 0.5f ? 1f : 0f) * amp);
                default:
                    return 1f;
            }
        }

        /// <summary>The time into an eight key table: the loop position with its random start, or age over life.</summary>
        static float KeyTime(
            VfxSection r,
            int loopAt,
            int randomAt,
            int rateAt,
            float random,
            float life,
            float time
        )
        {
            if (r.GetU8(loopAt) != 0)
            {
                int rate = r.GetS32(rateAt);
                if (rate >= 1)
                    return AnimTrack.FMod(time + (random * r.GetU8(randomAt)) * rate, rate) / rate;
            }
            return time / life;
        }

        /// <summary>
        /// An eight key table at <paramref name="t"/>: clamped at both ends, then stepped
        /// (interpolation 1) or linear (0). Any other interpolation leaves zero.
        /// </summary>
        public static Vector3 Key8(byte[] b, int table, int count, int interpolation, float t) =>
            Keyframes.Evaluate(
                MemoryMarshal.Cast<byte, Vector4>(b.AsSpan(table, 16 * count)),
                interpolation,
                t,
                Vector3.Zero
            );

        /// <summary><paramref name="v"/> rotated by <paramref name="angle"/> about the unit <paramref name="axis"/>.</summary>
        public static Vector3 RotateAxis(Vector3 v, Vector3 axis, float angle)
        {
            VfxMath.FastSinCos(angle, out float s, out float c);
            float omc = 1f - c;
            float x = axis.X,
                y = axis.Y,
                z = axis.Z;
            var r0 = new Vector3(c + omc * (x * x), x * y * omc - s * z, s * y + x * z * omc);
            var r1 = new Vector3(s * z + x * y * omc, c + y * y * omc, y * z * omc - s * x);
            var r2 = new Vector3(x * z * omc - s * y, s * x + y * z * omc, c + z * z * omc);
            return r0 * v.X + r1 * v.Y + r2 * v.Z;
        }

        static float F(byte[] b, int o) => BitConverter.ToSingle(b, o);

        static int I(byte[] b, int o) => BitConverter.ToInt32(b, o);

        static Vector3 V3(byte[] b, int o) => new(F(b, o), F(b, o + 4), F(b, o + 8));
    }
}
