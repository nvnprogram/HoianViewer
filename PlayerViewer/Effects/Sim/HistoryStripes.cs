using System;
using System.Collections.Generic;
using System.Numerics;
using EffectLibrary;

namespace PlayerViewer.Effects.Sim
{
    /// <summary>The history stripe plugin's parameters (EP02).</summary>
    public sealed class StripeParams
    {
        /// <summary>0 billboard, 1 emitter matrix, 2 emitter upright, others as the shader reads them.</summary>
        public int CalcType;
        public bool FollowEmitter;

        /// <summary>1 draws a second, crossed strip.</summary>
        public int Option;

        /// <summary>How V runs along the stripe: 0 over the points it has, 1 over the full history.</summary>
        public int Texturing;
        public float Divide,
            History,
            StartAlpha,
            EndAlpha,
            DirectionSmoothing;

        /// <summary>The two words after the alphas the plugin block carries, meaning unknown.</summary>
        public float Param2C,
            Param30;

        public static StripeParams Read(ReadOnlySpan<byte> p) =>
            new()
            {
                CalcType = BitConverter.ToInt32(p.Slice(0x00, 4)),
                FollowEmitter = BitConverter.ToInt32(p.Slice(0x04, 4)) != 0,
                Option = BitConverter.ToInt32(p.Slice(0x08, 4)),
                Texturing = BitConverter.ToInt32(p.Slice(0x0C, 4)),
                Divide = BitConverter.ToSingle(p.Slice(0x10, 4)),
                History = BitConverter.ToSingle(p.Slice(0x14, 4)),
                StartAlpha = BitConverter.ToSingle(p.Slice(0x1C, 4)),
                EndAlpha = BitConverter.ToSingle(p.Slice(0x20, 4)),
                DirectionSmoothing = BitConverter.ToSingle(p.Slice(0x28, 4)),
                Param2C = BitConverter.ToInt32(p.Slice(0x2C, 4)),
                Param30 = BitConverter.ToSingle(p.Slice(0x30, 4)),
            };

        public int HistoryCount => (int)History;

        /// <summary>Points a stripe can write: its history plus the subdivisions between.</summary>
        public int PointsPerStripe => HistoryCount + (HistoryCount - 1) * (int)Divide;
    }

    /// <summary>
    /// One stripe: the ring of positions its particle left, newest first, and the points it
    /// hands the shader. A dead particle's stripe stays behind and shortens a point a frame.
    /// </summary>
    public sealed class StripeInstance
    {
        public readonly Vector4[] Pos,
            Dir,
            Emat;

        /// <summary>The ring slot the next history point goes to; the newest is the one after it.</summary>
        public int Head;
        public int Count;

        /// <summary>Vertices to draw, two per point; below three points none.</summary>
        public int VertexCount;
        public float Time,
            Life,
            EmitFraction;
        public Vector4 Color0,
            Color1,
            Random,
            Interpolated,
            HeadPos;

        /// <summary>The shader's points: position and scale, direction and V, the up axis.</summary>
        public readonly float[] Points;

        public StripeInstance(int history, int points)
        {
            Pos = new Vector4[history];
            Dir = new Vector4[history];
            Emat = new Vector4[history];
            Points = new float[points * HistoryStripes.FloatsPerPoint];
        }

        public int Next(int i) => i + 1 < Pos.Length ? i + 1 : 0;

        public int Prev(int i) => i > 0 ? i - 1 : Pos.Length - 1;

        public int Newest => Next(Head);
    }

    /// <summary>
    /// The history stripe plugin of one CPU emitter, as the 11.3.0 runtime runs it: a stripe
    /// per particle, history pushed each particle step, subdivided with Hermite curves when asked,
    /// and a dead particle's stripe kept on a delayed list until it has shrunk away.
    /// </summary>
    public sealed class HistoryStripes
    {
        public const int FloatsPerPoint = 12;

        /// <summary>Bytes of the plugin block a stripe draw binds.</summary>
        public const int BlockSize = 96;

        public StripeParams Params { get; }
        readonly EmitterInstance _e;

        /// <summary>The stripe of each particle slot.</summary>
        public readonly StripeInstance[] BySlot;

        /// <summary>Stripes of dead particles, oldest death first.</summary>
        public readonly List<StripeInstance> Delayed = new();

        public HistoryStripes(EmitterInstance e, StripeParams p, int capacity)
        {
            _e = e;
            Params = p;
            BySlot = new StripeInstance[capacity];
        }

        /// <summary>On emit: a fresh stripe for the slot. One still attached is dropped undrawn.</summary>
        internal void Emit(int slot)
        {
            int history = Math.Max(1, Params.HistoryCount);
            var s = new StripeInstance(history, Math.Max(1, Params.PointsPerStripe))
            {
                EmitFraction = _e.Time - MathF.Floor(_e.Time),
                Random = _e.Particles.Get(ParticleStream.Random, slot),
            };
            BySlot[slot] = s;
        }

        /// <summary>On removal: the stripe's final colours, then onto the delayed list.</summary>
        internal void Remove(int slot, float time, float life)
        {
            var s = BySlot[slot];
            if (s == null)
                return;
            BySlot[slot] = null;
            UpdateColors(s, s.Random, life, LookTime(time));
            Delayed.Add(s);
        }

        /// <summary>Each step: pushes the particle's position into its history and rebuilds the points.</summary>
        internal void Calculate(int slot, float time, float life)
        {
            var s = BySlot[slot];
            if (s == null)
                return;
            var p = Params;
            var ps = _e.Particles;
            var d = _e.Def;
            s.Time = time;
            s.Life = life;
            s.VertexCount = 0;
            float look = LookTime(time);
            UpdateColors(s, s.Random, life, look);

            int w = s.Head;
            s.Head = s.Prev(s.Head);
            var local = ps.Get3(ParticleStream.LocalPos, slot);
            var localVec = ps.Get3(ParticleStream.LocalVec, slot);
            var baseScale = ps.Get3(ParticleStream.Scale, slot);
            var random = ps.Get(ParticleStream.Random, slot);
            var scale = ParticleAnim.Scale(d, baseScale, random, life, look);
            Vector3 pos,
                vec;
            if (p.FollowEmitter)
            {
                pos = local;
                vec = localVec;
            }
            else
            {
                scale = scale * _e.FadeScale * _e.Set.ParticleScaleForCalc;
                var m = _e.WorldMatrixAt(slot);
                pos = m.Transform(local);
                vec = m.TransformNormal(localVec);
            }
            s.HeadPos = new Vector4(pos, 0);
            s.Pos[w] = new Vector4(pos, scale.X);
            s.Dir[w] = new Vector4(vec, 0);
            s.Emat[w] = p.FollowEmitter ? new Vector4(0, 1, 0, 0) : new Vector4(_e.Srt.R1, 0);
            if (p.CalcType is 1 or 2 && d.Source.Section.GetU8(0xBF2) != 0)
                RollZ(s, w, slot, look);
            if (p.History > s.Count)
                s.Count++;
            if (s.Count >= 2)
                Link(s, time);
        }

        /// <summary>
        /// The newest point's direction, from the step since the last one, eased toward it when
        /// asked; the first pair shares it. Then the points are written.
        /// </summary>
        void Link(StripeInstance s, float time)
        {
            int n = s.Newest;
            int o = s.Next(n);
            var d = s.Pos[n] - s.Pos[o];
            d.W = 0;
            float k = Params.DirectionSmoothing;
            if (k <= 0)
                s.Dir[n] = d;
            else
            {
                if (s.Time >= 2)
                    d = s.Interpolated + (Normalize(d) - s.Interpolated) * k;
                s.Interpolated = Normalize(d);
                s.Dir[n] = s.Interpolated;
            }
            if (s.Count == 2)
                s.Dir[o] = s.Dir[n];
            WritePoints(s);
        }

        /// <summary>Emitter matrix and upright stripes: rolls the newest up axis about the stripe by the particle's Z rotation.</summary>
        void RollZ(StripeInstance s, int w, int slot, float time)
        {
            var ps = _e.Particles;
            Vector3 axis;
            if (s.Count >= 1)
            {
                var diff = s.Pos[w] - s.Pos[s.Next(w)];
                axis = new Vector3(diff.X, diff.Y, diff.Z) * (1f / _e.FrameRate);
            }
            else
                axis = Xyz(s.Dir[w]);
            if (VfxMath.Dot(axis, axis) < 0.00001f)
                axis = _e.Srt.R2;
            axis = VfxMath.Normalize(axis);
            var rot = ParticleAnim.Rotation(
                _e.Def,
                ps.Get(ParticleStream.InitRotate, slot),
                ps.Get(ParticleStream.Random, slot),
                time
            );
            var up = VfxMath.Normalize(ParticleAnim.RotateAxis(Xyz(s.Emat[w]), axis, rot.Z));
            s.Emat[w] = new Vector4(up, 0);
            if (s.Count == 1)
                s.Emat[s.Next(w)] = s.Emat[w];
        }

        /// <summary>emitterPostCalc: each delayed stripe ages, loses its oldest point, and is freed below two.</summary>
        internal void PostCalculate()
        {
            float fr = _e.FrameRate;
            for (int i = 0; i < Delayed.Count; i++)
            {
                var s = Delayed[i];
                float a = (s.Time + 0.0005f) + s.EmitFraction;
                float b = a + fr;
                bool shrink = true;
                if (b - a < 0.9995f)
                {
                    float fa = MathF.Floor(a);
                    float fb = MathF.Floor(b);
                    if (!(fb - fa > 0 && b - fb >= 0.001f))
                        shrink = a - fa <= 0.001f && !(b - a < 0.0005f);
                }
                s.Time += fr;
                if (s.Count < 2)
                {
                    Delayed.RemoveAt(i--);
                    continue;
                }
                if (shrink)
                    s.Count--;
                WritePoints(s);
            }
        }

        /// <summary>
        /// The age the CPU copies of the particle look are evaluated at. In loop mode an infinite
        /// life particle's age is taken back by whole periods, the same reduction the draw applies
        /// to its birth time, so a periodic set evaluates the same floats every period.
        /// </summary>
        float LookTime(float time)
        {
            var sys = _e.Set.System;
            int n = sys.LoopPeriod;
            if (!_e.Def.InfiniteLife || sys.Mode != RandomMode.Loop || n <= 0)
                return time;
            float since = _e.Set.Time - sys.LoopStart;
            return since < 0 ? time : time - MathF.Floor(since / n) * n;
        }

        void UpdateColors(StripeInstance s, Vector4 random, float life, float time)
        {
            var d = _e.Def;
            float colorScale = BitConverter.ToSingle(d.StaticBlock, 0x670);
            var c0 = ParticleAnim.Color0(d, random, life, time);
            var c1 = ParticleAnim.Color1(d, random, life, time);
            s.Color0 = new Vector4(c0.X * colorScale, c0.Y * colorScale, c0.Z * colorScale, c0.W);
            s.Color1 = new Vector4(c1.X * colorScale, c1.Y * colorScale, c1.Z * colorScale, c1.W);
        }

        /// <summary>The V span: the points present, the full history while alive (texturing 1), or none.</summary>
        float Span(StripeInstance s)
        {
            if (s.Time >= s.Life)
                return s.Count;
            return Params.Texturing switch
            {
                0 => s.Count,
                1 => Params.History,
                _ => 0,
            };
        }

        void WritePoints(StripeInstance s)
        {
            if (Params.Divide > 0)
                WriteDivided(s);
            else
                WriteDirect(s);
        }

        void WriteDirect(StripeInstance s)
        {
            int count = s.Count;
            if (count < 3)
            {
                s.VertexCount = 0;
                return;
            }
            float step = 1f / (Span(s) - 1f);
            var o = s.Points;
            int node = s.Head;
            for (int i = 0; i < count; i++)
            {
                node = s.Next(node);
                int at = i * FloatsPerPoint;
                Put(o, at, s.Pos[node]);
                Put3(o, at + 4, s.Dir[node]);
                o[at + 7] = step * i;
                Put3(o, at + 8, s.Emat[node]);
                o[at + 11] = 0;
            }
            s.VertexCount = 2 * count;
        }

        void WriteDivided(StripeInstance s)
        {
            int count = s.Count;
            if (count < 3)
            {
                s.VertexCount = 0;
                return;
            }
            int div = (int)Params.Divide;
            int between = (count - 1) * div;
            int total = between + count;
            float step = 1f / ((Span(s) + -1f) + between);
            float segment = 1f / (div + 1);
            var o = s.Points;
            int node = s.Newest;
            int at0 = 0;
            for (int i = 0; i < total; i++)
            {
                float u = segment * i;
                int k = (int)MathF.Floor(u);
                if (at0 < k)
                {
                    node = s.Next(node);
                    at0 = k;
                }
                Hermite(s, node, k, count, u - k, out var pos, out var dir);
                int at = i * FloatsPerPoint;
                o[at] = pos.X;
                o[at + 1] = pos.Y;
                o[at + 2] = pos.Z;
                o[at + 3] = s.Pos[node].W;
                o[at + 4] = dir.X;
                o[at + 5] = dir.Y;
                o[at + 6] = dir.Z;
                o[at + 7] = step * i;
            }
            s.VertexCount = 2 * total;
            if (Params.CalcType == 0)
                return;
            node = s.Newest;
            at0 = 0;
            for (int i = 0; i < total; i++)
            {
                float u = segment * i;
                int k = (int)MathF.Floor(u);
                if (at0 < k)
                {
                    node = s.Next(node);
                    at0 = k;
                }
                var a = Xyz(s.Emat[node]);
                var e = VfxMath.Normalize(a + (Xyz(s.Emat[s.Next(node)]) - a) * (u - k));
                Put3(o, i * FloatsPerPoint + 8, new Vector4(e, 0));
            }
        }

        /// <summary>
        /// A point on the segment from ring slot <paramref name="node"/>, the
        /// <paramref name="index"/>th newest, towards the next older one. The two ends take
        /// one sided tangents; the oldest index folds back onto the segment before it, as the
        /// runtime does.
        /// </summary>
        static void Hermite(
            StripeInstance s,
            int node,
            int index,
            int count,
            float t,
            out Vector3 pos,
            out Vector3 dir
        )
        {
            Vector3 P(int i) => Xyz(s.Pos[i]);
            Vector3 D(int i) => Xyz(s.Dir[i]);
            Vector3 p0,
                p1,
                m0,
                m1;
            if (index == 0)
            {
                int nx = s.Next(node);
                int nn = s.Next(nx);
                p0 = P(node);
                p1 = P(nx);
                m0 = ((P(nx) - P(node)) + (P(nx) - P(nn))) * 0.25f;
                m1 = (P(nn) - P(node)) * 0.5f;
                dir = D(node) * (1f - t) + D(nx) * t;
            }
            else if (index == count - 1)
            {
                int pv = s.Prev(node);
                int pp = s.Prev(pv);
                p0 = P(pv);
                p1 = P(node);
                m0 = (P(node) - P(pp)) * 0.5f;
                m1 = ((P(pv) - P(node)) + (P(pv) - P(pp))) * -0.25f;
                dir = D(pv) * 0f + D(node);
            }
            else if (index == count - 2)
            {
                int nx = s.Next(node);
                int pv = s.Prev(node);
                p0 = P(node);
                p1 = P(nx);
                m0 = (P(nx) - P(pv)) * 0.5f;
                m1 = ((P(node) - P(nx)) + (P(node) - P(pv))) * -0.25f;
                dir = D(node) * (1f - t) + D(nx) * t;
            }
            else
            {
                int nx = s.Next(node);
                int pv = s.Prev(node);
                int nn = s.Next(nx);
                p0 = P(node);
                p1 = P(nx);
                m0 = (P(nx) - P(pv)) * 0.5f;
                m1 = (P(nn) - P(node)) * 0.5f;
                dir = D(node) * (1f - t) + D(nx) * t;
            }
            float t2 = t * t;
            float t3 = t2 * t;
            float H(float a, float b, float c, float d)
            {
                // Coefficients of t^3, t^2, t and 1 for p0, p1, m0 and m1.
                float c3 = 2f * a - 2f * b + c + d;
                float c2 = -3f * a + 3f * b - 2f * c - d;
                float c1 = c;
                float c0 = a;
                return c0 + MathF.FusedMultiplyAdd(t, c1, MathF.FusedMultiplyAdd(t3, c3, t2 * c2));
            }
            pos = new Vector3(
                H(p0.X, p1.X, m0.X, m1.X),
                H(p0.Y, p1.Y, m0.Y, m1.Y),
                H(p0.Z, p1.Z, m0.Z, m1.Z)
            );
        }

        /// <summary>The 96 byte plugin block of one stripe draw, its points at <paramref name="index"/> times <see cref="StripeParams.PointsPerStripe"/>.</summary>
        public void WriteBlock(StripeInstance s, Span<byte> b, int index)
        {
            var p = Params;
            int points = Math.Max(1, p.PointsPerStripe);
            W(b, 0x00, s.Random);
            W(b, 0x10, new Vector4(p.StartAlpha, p.EndAlpha, p.Param2C, p.Param30));
            W(b, 0x20, s.Color0);
            W(b, 0x30, s.Color1);
            F(b, 0x40, s.Time);
            F(b, 0x44, s.Count + p.Divide * (s.Count - 1));
            F(b, 0x48, index);
            F(b, 0x4C, s.Life);
            F(b, 0x50, s.HeadPos.X);
            F(b, 0x54, s.HeadPos.Y);
            F(b, 0x58, s.HeadPos.Z);
            F(b, 0x5C, points);
        }

        public bool AnyDrawn
        {
            get
            {
                foreach (var _ in Drawn())
                    return true;
                return false;
            }
        }

        /// <summary>Stripes the runtime draws, in its order: the delayed list, then the live ones by slot.</summary>
        public IEnumerable<StripeInstance> Drawn()
        {
            foreach (var s in Delayed)
                if (s.VertexCount >= 1)
                    yield return s;
            foreach (var s in BySlot)
                if (s != null && s.VertexCount >= 1)
                    yield return s;
        }

        static Vector4 Normalize(Vector4 v) => new(VfxMath.Normalize(Xyz(v)), 0);

        static Vector3 Xyz(Vector4 v) => new(v.X, v.Y, v.Z);

        static void Put(float[] o, int at, Vector4 v)
        {
            o[at] = v.X;
            o[at + 1] = v.Y;
            o[at + 2] = v.Z;
            o[at + 3] = v.W;
        }

        static void Put3(float[] o, int at, Vector4 v)
        {
            o[at] = v.X;
            o[at + 1] = v.Y;
            o[at + 2] = v.Z;
        }

        static void W(Span<byte> b, int at, Vector4 v)
        {
            F(b, at, v.X);
            F(b, at + 4, v.Y);
            F(b, at + 8, v.Z);
            F(b, at + 12, v.W);
        }

        static void F(Span<byte> b, int at, float v) =>
            System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(b[at..], v);
    }
}
