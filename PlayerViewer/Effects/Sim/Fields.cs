using System;
using System.Collections.Generic;
using System.Numerics;
using EffectLibrary;

namespace PlayerViewer.Effects.Sim
{
    /// <summary>
    /// A field's keyframed strength: up to eight xyz keys over the particle's life, or over a
    /// fixed loop with an optional per particle start offset.
    /// </summary>
    public sealed class FieldAnim
    {
        public bool Enabled,
            Loop,
            RandomStart;
        public int Count,
            Frames,
            Interpolation;
        public Vector4[] Keys = new Vector4[8];

        public static FieldAnim Read(ReadOnlySpan<byte> p, int o)
        {
            var a = new FieldAnim
            {
                Enabled = I(p, o) != 0,
                Loop = I(p, o + 4) != 0,
                RandomStart = I(p, o + 8) != 0,
                Count = I(p, o + 12),
                Frames = I(p, o + 16),
                Interpolation = I(p, o + 20),
            };
            for (int k = 0; k < 8; k++)
                a.Keys[k] = new Vector4(
                    F(p, o + 24 + 16 * k),
                    F(p, o + 28 + 16 * k),
                    F(p, o + 32 + 16 * k),
                    F(p, o + 36 + 16 * k)
                );
            return a;
        }

        /// <summary>The value at <paramref name="ptclTime"/>, or <paramref name="constant"/> when the animation is off.</summary>
        public Vector3 Evaluate(float ptclTime, float life, float random, Vector3 constant)
        {
            if (!Enabled)
                return constant;
            if (Count == 0)
                return Vector3.Zero;
            float t;
            if (Loop)
            {
                float frames = Frames;
                t =
                    AnimTrack.FMod(
                        MathF.FusedMultiplyAdd(random * (RandomStart ? 1f : 0f), frames, ptclTime),
                        frames
                    ) / frames;
            }
            else
                t = ptclTime / life;
            return Keyframes.Evaluate(
                Keys.AsSpan(0, Math.Clamp(Count, 1, 8)),
                Interpolation,
                t,
                Vector3.Zero
            );
        }

        internal static int I(ReadOnlySpan<byte> p, int o) => BitConverter.ToInt32(p.Slice(o, 4));

        internal static float F(ReadOnlySpan<byte> p, int o) =>
            BitConverter.ToSingle(p.Slice(o, 4));

        internal static Vector3 V3(ReadOnlySpan<byte> p, int o) =>
            new(F(p, o), F(p, o + 4), F(p, o + 8));
    }

    /// <summary>
    /// The emitter's fields as the runtime applies them to a CPU particle each frame, in its
    /// order: collision, random (FRND), simple random (FRN1), magnet, spin, convergence, curl
    /// noise, position add, then the host's custom callbacks. The stream out compute programs
    /// are assumed to run the same fields.
    /// </summary>
    public sealed class FieldSet
    {
        sealed class Collision
        {
            public bool Kill,
                World;
            public float Height,
                Bounce,
                Friction;
            public int Limit;
        }

        sealed class Random
        {
            public bool UnifiedPhase,
                Waves,
                AirRegist;
            public Vector3 Power;
            public int Interval;
            public float PhaseRandom,
                PhaseSpread;
            public float[] Amplitude = new float[4],
                Period = new float[4];
            public FieldAnim Anim;
        }

        sealed class SimpleRandom
        {
            public Vector3 Power;
            public int Interval;
            public FieldAnim Anim;
        }

        sealed class Magnet
        {
            public bool FollowEmitter,
                HostTarget;
            public bool[] Axis = new bool[3];
            public float Power;
            public Vector3 Target;
            public FieldAnim Anim;
        }

        sealed class Spin
        {
            public float Power,
                Diffusion;
            public int Axis;
            public FieldAnim PowerAnim,
                DiffusionAnim;
        }

        sealed class Convergence
        {
            public bool FollowEmitter,
                HostTarget;
            public Vector3 Target;
            public float Ratio;
            public FieldAnim Anim;
        }

        sealed class PositionAdd
        {
            public bool World;
            public Vector3 Add;
            public FieldAnim Anim;
        }

        sealed class CurlNoise
        {
            public bool Interpolate,
                RandomOffset,
                World;
            public Vector3 Speed,
                Power;
            public float Scale,
                Base;
        }

        Collision _collision;
        Random _random;
        SimpleRandom _simpleRandom;
        Magnet _magnet;
        Spin _spin;
        Convergence _convergence;
        PositionAdd _posAdd;
        CurlNoise _curlNoise;
        bool _custom;

        public static FieldSet From(Emitter e)
        {
            var f = new FieldSet();
            bool any = false;
            var s = e.Section;
            if (s.FindSub("FCOL") is { } col)
            {
                var p = col.Payload.Span;
                f._collision = new Collision
                {
                    Kill = p[0] == 1,
                    World = p[1] != 0,
                    Height = FieldAnim.F(p, 4),
                    Bounce = FieldAnim.F(p, 8),
                    Limit = FieldAnim.I(p, 12),
                    Friction = FieldAnim.F(p, 16),
                };
                any = true;
            }
            if (s.FindSub("FRND") is { } rnd)
            {
                var p = rnd.Payload.Span;
                var r = new Random
                {
                    UnifiedPhase = p[0] != 0,
                    Waves = p[1] != 0,
                    AirRegist = p[2] != 0,
                    Power = FieldAnim.V3(p, 4),
                    Interval = FieldAnim.I(p, 16),
                    PhaseRandom = FieldAnim.F(p, 20),
                    PhaseSpread = FieldAnim.F(p, 24),
                    Anim = FieldAnim.Read(p, 60),
                };
                for (int k = 0; k < 4; k++)
                {
                    r.Amplitude[k] = FieldAnim.F(p, 28 + 4 * k);
                    r.Period[k] = FieldAnim.F(p, 44 + 4 * k);
                }
                f._random = r;
                any = true;
            }
            if (s.FindSub("FRN1") is { } rn1)
            {
                var p = rn1.Payload.Span;
                f._simpleRandom = new SimpleRandom
                {
                    Power = FieldAnim.V3(p, 0),
                    Interval = FieldAnim.I(p, 12),
                    Anim = FieldAnim.Read(p, 16),
                };
                any = true;
            }
            if (s.FindSub("FMAG") is { } mag)
            {
                var p = mag.Payload.Span;
                f._magnet = new Magnet
                {
                    FollowEmitter = p[0] != 0,
                    Axis = new[] { p[1] != 0, p[2] != 0, p[3] != 0 },
                    Power = FieldAnim.F(p, 4),
                    Target = FieldAnim.V3(p, 8),
                    Anim = FieldAnim.Read(p, 20),
                    HostTarget = p[172] != 0,
                };
                any = true;
            }
            if (s.FindSub("FSPN") is { } spn)
            {
                var p = spn.Payload.Span;
                f._spin = new Spin
                {
                    Power = FieldAnim.F(p, 0),
                    Axis = FieldAnim.I(p, 4),
                    Diffusion = FieldAnim.F(p, 8),
                    PowerAnim = FieldAnim.Read(p, 12),
                    DiffusionAnim = FieldAnim.Read(p, 164),
                };
                any = true;
            }
            if (s.FindSub("FCOV") is { } cov)
            {
                var p = cov.Payload.Span;
                f._convergence = new Convergence
                {
                    FollowEmitter = p[0] == 1,
                    HostTarget = p[1] != 0,
                    Target = FieldAnim.V3(p, 4),
                    Ratio = FieldAnim.F(p, 16),
                    Anim = FieldAnim.Read(p, 20),
                };
                any = true;
            }
            if (s.FindSub("FCLN") is { } cln)
            {
                var p = cln.Payload.Span;
                f._curlNoise = new CurlNoise
                {
                    Interpolate = (p[0] & 1) != 0,
                    RandomOffset = (p[1] & 1) != 0,
                    World = p[2] != 0,
                    Speed = FieldAnim.V3(p, 4),
                    Power = FieldAnim.V3(p, 16),
                    Scale = FieldAnim.F(p, 28),
                    Base = FieldAnim.F(p, 32),
                };
                any = true;
            }
            if (s.FindSub("FPAD") is { } pad)
            {
                var p = pad.Payload.Span;
                f._posAdd = new PositionAdd
                {
                    World = p[0] != 0,
                    Add = FieldAnim.V3(p, 4),
                    Anim = FieldAnim.Read(p, 16),
                };
                any = true;
            }
            if (s.FindSub("FCSF") != null)
            {
                f._custom = true;
                any = true;
            }
            return any ? f : null;
        }

        /// <summary>Whether the simulation runs every field this emitter has.</summary>
        public bool FullySimulated => !_custom;

        /// <summary>Why the fields keep an emitter from looping exactly: terms on absolute emitter time, or fields not simulated.</summary>
        public IEnumerable<string> LoopBlockers()
        {
            if (_random is { UnifiedPhase: true })
                yield return "the random field's unified phase runs on emitter time";
            if (_curlNoise is { } c && c.Speed != Vector3.Zero)
                yield return "the curl noise field scrolls with emitter time";
            if (_custom)
                yield return "a custom shader field is not simulated";
        }

        public void Apply(
            EmitterInstance e,
            int slot,
            float ptclTime,
            ref Vector3 pos,
            ref Vector3 vec
        )
        {
            var ps = e.Particles;
            float fr = e.FrameRate;
            float dyn = ps[ParticleStream.Scale][slot * 4 + 3];
            float random = ps[ParticleStream.Random][slot * 4];
            float life = ps.Life[slot];
            if (_collision != null)
                ApplyCollision(e, slot, ptclTime, ref pos, ref vec);
            if (_random != null)
                ApplyRandom(e, slot, ptclTime, life, random, ref pos);
            if (_simpleRandom is { } sr)
            {
                var power = sr.Anim.Evaluate(ptclTime, life, random, sr.Power);
                if (sr.Interval != 0 && (uint)ptclTime % (uint)sr.Interval == 0)
                {
                    var v = e.FieldVector(slot);
                    vec = new Vector3(
                        MathF.FusedMultiplyAdd(v.X, power.X, vec.X),
                        MathF.FusedMultiplyAdd(v.Y, power.Y, vec.Y),
                        MathF.FusedMultiplyAdd(v.Z, power.Z, vec.Z)
                    );
                }
            }
            if (_magnet is { HostTarget: false } mg)
            {
                float power = mg
                    .Anim.Evaluate(ptclTime, life, random, new Vector3(mg.Power, 0, 0))
                    .X;
                var target = mg.Target;
                if (mg.FollowEmitter && e.Def.FollowType == 1)
                    target += e.EmitterMatrixAt(slot).Inverse().Transform(e.Srt.T);
                for (int i = 0; i < 3; i++)
                    if (mg.Axis[i])
                        vec = With(
                            vec,
                            i,
                            Get(vec, i)
                                + power * ((Get(target, i) - Get(pos, i)) - Get(vec, i)) * fr
                        );
            }
            if (_spin is { } sp)
                ApplySpin(sp, fr, dyn, ptclTime, life, random, ref pos);
            if (_convergence is { HostTarget: false } cv)
            {
                float ratio = cv
                    .Anim.Evaluate(ptclTime, life, random, new Vector3(cv.Ratio, 0, 0))
                    .X;
                var target = cv.Target;
                if (cv.FollowEmitter && e.Def.FollowType == 1)
                    target += e.EmitterMatrixAt(slot).Inverse().Transform(e.Rt.T);
                float k = fr * (dyn * ratio);
                pos = new Vector3(
                    MathF.FusedMultiplyAdd(target.X - pos.X, k, pos.X),
                    MathF.FusedMultiplyAdd(target.Y - pos.Y, k, pos.Y),
                    MathF.FusedMultiplyAdd(target.Z - pos.Z, k, pos.Z)
                );
            }
            if (_curlNoise is { } cn)
                ApplyCurlNoise(cn, e, slot, random, pos, ref vec);
            if (_posAdd is { } pa)
            {
                var add = pa.Anim.Evaluate(ptclTime, life, random, pa.Add);
                if (!pa.World)
                    pos += new Vector3(dyn * add.X, dyn * add.Y, dyn * add.Z) * fr;
                else
                {
                    var m = e.Def.FollowType == 1 ? e.EmitterMatrixAt(slot).WithoutScale() : e.Rt;
                    var local = new Vector3(
                        Vector3.Dot(m.R0, add),
                        Vector3.Dot(m.R1, add),
                        Vector3.Dot(m.R2, add)
                    );
                    pos += local * (fr * dyn);
                }
            }
        }

        /// <summary>
        /// The table sampled at the position scaled, scrolled by emitter time and
        /// offset, pushes velocity; world mode samples and pushes in world space.
        /// </summary>
        static void ApplyCurlNoise(
            CurlNoise c,
            EmitterInstance e,
            int slot,
            float random,
            Vector3 pos,
            ref Vector3 vec
        )
        {
            float offset = c.Base;
            if (c.RandomOffset)
                offset *= random;
            Matrix34 m = default;
            if (c.World)
            {
                m = e.WorldMatrixAt(slot);
                pos = m.Transform(pos);
            }
            var coord = new Vector3(offset) + (c.Speed * e.Time + pos * c.Scale);
            var noise = c.Interpolate
                ? CurlNoiseTable.Interpolated(coord)
                : CurlNoiseTable.Nearest(coord);
            if (c.World)
                noise = m.Inverse().TransformNormal(noise);
            vec += c.Power * noise * e.FrameRate;
        }

        void ApplyCollision(
            EmitterInstance e,
            int slot,
            float ptclTime,
            ref Vector3 pos,
            ref Vector3 vec
        )
        {
            var c = _collision;
            var ps = e.Particles;
            if (c.Limit != -1 && c.Limit <= ps.Bounces[slot])
                return;
            if (!c.World)
            {
                if (pos.Y >= c.Height)
                    return;
                pos.Y = c.Height;
                if (c.Kill)
                {
                    ps.Life[slot] = ptclTime;
                    return;
                }
                vec.Y = -(c.Bounce * vec.Y);
                vec *= c.Friction;
                ps.Bounces[slot]++;
                return;
            }
            var m = e.Def.FollowType != 0 ? e.EmitterMatrixAt(slot) : e.Srt;
            var world = m.Transform(pos);
            if (world.Y >= c.Height)
                return;
            if (c.Kill)
            {
                ps.Life[slot] = ptclTime;
                return;
            }
            var inv = m.Inverse();
            pos = inv.Transform(new Vector3(world.X, c.Height + 0.0001f, world.Z));
            var wv = m.TransformNormal(vec);
            vec = inv.TransformNormal(new Vector3(wv.X, -c.Bounce * wv.Y, wv.Z)) * c.Friction;
            ps.Bounces[slot]++;
        }

        void ApplyRandom(
            EmitterInstance e,
            int slot,
            float ptclTime,
            float life,
            float random,
            ref Vector3 pos
        )
        {
            var r = _random;
            var power = r.Anim.Evaluate(ptclTime, life, random, r.Power);
            float fr = e.FrameRate;
            float t = ptclTime;
            if (r.AirRegist)
            {
                float air = e.Def.AirRegist;
                float k = MathF.FusedMultiplyAdd(1f - air, 1f - fr, air);
                if (k != 1f)
                    t = (1f - MathF.Pow(k, ptclTime)) / (1f - k);
            }
            float n = r.Interval;
            float w = VfxMath.TwoPi / n;
            var rnd = e.Particles.Get(ParticleStream.Random, slot);
            Vector3 d;
            if (r.UnifiedPhase)
            {
                float spread = r.PhaseSpread / 100f * n;
                float ratio = r.PhaseRandom / 100f * n;
                float step = fr * ratio;
                float phase = e.Time * ratio - spread;
                float two = spread + spread;
                float px = MathF.FusedMultiplyAdd(two, rnd.X, 0 * n + phase);
                float py = MathF.FusedMultiplyAdd(two, rnd.Y, 0.31415f * n + phase);
                float pz = MathF.FusedMultiplyAdd(two, rnd.Z, 0.92653f * n + phase);
                d = new Vector3(
                    Unified(r, px, t, fr, step, w),
                    Unified(r, py, t, fr, step, w),
                    Unified(r, pz, t, fr, step, w)
                );
            }
            else
            {
                float px = MathF.FusedMultiplyAdd(rnd.X, n, t);
                float py = t + rnd.Y * n;
                float pz = t + rnd.Z * n;
                d = new Vector3(Plain(r, px, fr, w), Plain(r, py, fr, w), Plain(r, pz, fr, w));
            }
            pos = new Vector3(
                MathF.FusedMultiplyAdd(d.X, power.X, pos.X),
                MathF.FusedMultiplyAdd(d.Y, power.Y, pos.Y),
                MathF.FusedMultiplyAdd(d.Z, power.Z, pos.Z)
            );
        }

        /// <summary>One axis of the random field with a per particle phase: the step of a wave sum or of the noise.</summary>
        static float Plain(Random r, float p, float fr, float w)
        {
            if (r.Waves)
                return Waves(r, fr + p, w) - Waves(r, p, w);
            return Noise(p * w, MathF.FusedMultiplyAdd(fr, w, p * w));
        }

        /// <summary>One axis of the random field with the phase shared through emitter time.</summary>
        static float Unified(Random r, float phase, float t, float fr, float step, float w)
        {
            float p = t + phase;
            if (r.Waves)
                return (Waves(r, step + (fr + p), w) - Waves(r, p, w))
                    - (Waves(r, step + phase, w) - Waves(r, phase, w));
            float a1 = p * w;
            float stepW = step * w;
            float a2 = stepW + MathF.FusedMultiplyAdd(fr, w, a1);
            float a3 = phase * w;
            float a4 = a3 + stepW;
            float sum = Octave(a1, a2, a3, a4, 5f / 3f, 4f);
            sum += Octave(a1, a2, a3, a4, 50f / 21f, 3f);
            sum += Octave(a1, a2, a3, a4, 50f / 21f, 2f);
            sum += Octave(a1, a2, a3, a4, 50f / 21f, 1.5f);
            return sum;
        }

        static float Waves(Random r, float t, float w)
        {
            float x = t * w;
            float s = r.Amplitude[0] * VfxMath.FastSin(x / r.Period[0]);
            s += VfxMath.FastSin(x / r.Period[1]) * r.Amplitude[1];
            s += VfxMath.FastSin(x / r.Period[2]) * r.Amplitude[2];
            s += VfxMath.FastSin(x / r.Period[3]) * r.Amplitude[3];
            return s;
        }

        static float Octave(float a1, float a2, float a3, float a4, float f, float amp) =>
            (
                ((VfxMath.FastSin(a2 * f) - VfxMath.FastSin(a1 * f)) + VfxMath.FastSin(a3 * f))
                - VfxMath.FastSin(a4 * f)
            ) * amp;

        static float Noise(float a, float b)
        {
            static float D(float a, float b, float f) =>
                VfxMath.FastSin(b * f) - VfxMath.FastSin(a * f);
            float d3 = D(a, b, 100f / 23f);
            return ((D(a, b, 5f / 3f) * 4f + D(a, b, 50f / 21f) * 3f) + (d3 + d3))
                + D(a, b, 20f / 3f) * 1.5f;
        }

        static void ApplySpin(
            Spin sp,
            float fr,
            float dyn,
            float ptclTime,
            float life,
            float random,
            ref Vector3 pos
        )
        {
            float power = sp.PowerAnim.Enabled
                ? sp.PowerAnim.Evaluate(ptclTime, life, random, default).X * (VfxMath.Pi / 180f)
                : sp.Power;
            float diffusion = sp
                .DiffusionAnim.Evaluate(ptclTime, life, random, new Vector3(sp.Diffusion, 0, 0))
                .X;
            VfxMath.FastSinCos(fr * (dyn * power), out float s, out float c);
            switch (sp.Axis)
            {
                case 0:
                {
                    float y = s * pos.Z + pos.Y * c;
                    float z = c * pos.Z - pos.Y * s;
                    Spread(ref y, ref z, fr, dyn, diffusion);
                    pos = new Vector3(pos.X, y, z);
                    break;
                }
                case 1:
                {
                    float z = s * pos.X + pos.Z * c;
                    float x = c * pos.X - pos.Z * s;
                    Spread(ref z, ref x, fr, dyn, diffusion);
                    pos = new Vector3(x, pos.Y, z);
                    break;
                }
                case 2:
                {
                    float x = s * pos.Y + c * pos.X;
                    float y = c * pos.Y - s * pos.X;
                    Spread(ref x, ref y, fr, dyn, diffusion);
                    pos = new Vector3(x, y, pos.Z);
                    break;
                }
            }
        }

        /// <summary>Pushes a point out from the spin axis, in the plane of the spin.</summary>
        static void Spread(ref float a, ref float b, float fr, float dyn, float diffusion)
        {
            if (diffusion == 0)
                return;
            float len = b * b + a * a;
            if (len <= 0)
                return;
            float k = fr * (dyn * (diffusion * (1f / MathF.Sqrt(len))));
            a += a * k;
            b += b * k;
        }

        static float Get(Vector3 v, int i) =>
            i switch
            {
                0 => v.X,
                1 => v.Y,
                _ => v.Z,
            };

        static Vector3 With(Vector3 v, int i, float x) =>
            i switch
            {
                0 => new Vector3(x, v.Y, v.Z),
                1 => new Vector3(v.X, x, v.Z),
                _ => new Vector3(v.X, v.Y, x),
            };
    }
}
