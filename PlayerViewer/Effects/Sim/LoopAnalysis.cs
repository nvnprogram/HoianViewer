using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using EffectLibrary;

namespace PlayerViewer.Effects.Sim
{
    public enum LoopKind
    {
        /// <summary>Starts and ends empty; the whole lifetime is the clip.</summary>
        OneShot,

        /// <summary>Settles into a steady state that <see cref="RandomMode.Loop"/> makes exactly periodic.</summary>
        Periodic,

        /// <summary>Neither; exported over a chosen duration.</summary>
        NotLoopable,
    }

    /// <summary>
    /// Whether and how an emitter set loops. A one-shot plays <see cref="Length"/> frames from 0.
    /// A periodic set plays [<see cref="Start"/>, Start + <see cref="Period"/>): its regime
    /// begins at <see cref="RegimeStart"/>, and <see cref="WarmPeriods"/> periods later every
    /// particle alive was born inside it, so the state at Start and at Start + Period is the
    /// same. Children take a second period, for the ones emitted from particles born before.
    /// The ring capacities hold for loop seed <see cref="Seed"/>: with random emission timing,
    /// how many particles a period makes depends on it. <see cref="Unsure"/> names what the
    /// resource cannot tell about a periodic set, which only rendering the loop can settle.
    /// </summary>
    public sealed record LoopInfo(
        LoopKind Kind,
        int Length,
        int Period,
        int RegimeStart,
        int WarmPeriods,
        string Reason,
        IReadOnlyDictionary<uint, int> Capacities,
        uint Seed = EffectSimulation.DefaultLoopSeed,
        string Unsure = null
    )
    {
        public int Start => RegimeStart + WarmPeriods * Period;

        public static LoopInfo NotLoopable(string reason) =>
            new(LoopKind.NotLoopable, 0, 0, 0, 0, reason, null);

        public override string ToString() =>
            Kind switch
            {
                LoopKind.OneShot => $"one-shot, {Length} frames",
                LoopKind.Periodic => $"periodic, {Period} frames after {Start} frames of warm-up"
                    + (Unsure != null ? $", unsure: {Unsure}" : ""),
                _ => $"not loopable: {Reason}",
            };
    }

    /// <summary>
    /// Classifies an emitter set for a looping export as a pure function of the resource and the
    /// simulation: one-shot, periodic with its period and warm-up, or not loopable with a reason.
    /// </summary>
    public static class LoopAnalysis
    {
        /// <summary>The longest clip considered, in frames.</summary>
        public const int MaxFrames = 60 * 60;

        /// <param name="sampled">Whether an emitter's programs sample a texture slot; without it
        /// every slot counts. An empty slot still counts when sampled, since it reads whatever the
        /// last emitter left bound there.</param>
        /// <param name="seed">The loop seed the ring capacities are counted for; count them for
        /// the seed the set will play with, or <see cref="Reseed"/> recounts them.</param>
        /// <param name="cancel">Checked every frame of the dry runs.</param>
        /// <param name="vertexSampled">Whether an emitter's vertex stage samples a texture slot;
        /// without it none is taken to.</param>
        public static LoopInfo Analyze(
            EmitterSet set,
            int minimumPeriod = 0,
            Func<Emitter, ulong, IEmissionPrimitive> primitives = null,
            Func<Emitter, int, bool> sampled = null,
            uint seed = EffectSimulation.DefaultLoopSeed,
            CancellationToken cancel = default,
            Func<Emitter, int, bool> vertexSampled = null
        )
        {
            var sim = new EffectSimulation { PrimitiveSource = primitives };
            var defs = set.AllEmitters.Select(sim.GetDef).ToList();
            var blockers = new List<string>();
            var unsure = new List<string>();
            var periods = new List<int>();
            int settle = 0;
            int maxLife = 0;
            int gap = 1;
            bool anyContinuous = false;
            bool anyInfinite = false;
            int ageSettle = 0;
            var measured = new HashSet<Emitter>();
            var still = new HashSet<Emitter>();

            foreach (var d in defs)
            {
                var reasons = new List<string>();
                bool continuous = Continuous(d);
                anyContinuous |= continuous;
                if (d.InfiniteLife)
                {
                    anyInfinite = true;
                    var doubts = new List<string>();
                    AgeTerms(d, sampled, vertexSampled, periods, reasons, doubts, ref ageSettle);
                    unsure.AddRange(doubts.Select(r => $"{d.Source.Name}: {r}"));
                    if (Moves(d))
                        Motion(d, measured, still, reasons, ref ageSettle);
                }
                else
                    maxLife = Math.Max(maxLife, Span(d));

                foreach (var a in d.Anims)
                {
                    if (a == null || !a.Enabled || a.Keys.Length < 2)
                        continue;
                    if (!a.Loop)
                        settle = Math.Max(settle, (int)MathF.Ceiling(a.LastFrame));
                    else if (!Whole(a.LastFrame, out int p) || p <= 0)
                        reasons.Add($"a looping emitter animation lasts {a.LastFrame} frames");
                    else
                        periods.Add(p);
                }
                if (d.FadeInAlphaCurve != 0 || d.FadeInScale)
                    settle = Math.Max(settle, d.FadeInTime);
                settle = Math.Max(settle, d.StartFrame + 1);
                if (d.OneTime && !d.IsChild)
                {
                    // A one time emitter is gone before the regime starts.
                    int fadeOut = d.FadeOutAlphaCurve != 0 || d.FadeOutScale ? d.FadeOutTime : 0;
                    settle = Math.Max(
                        settle,
                        d.StartFrame + d.EmissionFrames + d.MaxLife + fadeOut + 1
                    );
                }
                if (continuous && !d.OneTime && !d.IsChild)
                {
                    // A period must hold at least one emission, so every slot is rewritten.
                    float rate = d.UsesResourceRate ? d.Rate : d.MaxRate;
                    int every = rate > 0 && rate < 1 ? (int)MathF.Ceiling(1 / rate) : 1;
                    gap = Math.Max(gap, (d.Interval + d.IntervalRandom + 1) * every + 1);
                }
                if (
                    continuous
                    && !d.OneTime
                    && !d.IsChild
                    && d.IntervalRandom == 0
                    && d.RateRandom == 0
                )
                {
                    // Steady emission repeats once the fractional carry comes back to zero.
                    int q = RateCycle(d.UsesResourceRate ? d.Rate : d.MaxRate);
                    if (q > 0)
                        periods.Add((d.Interval + 1) * q);
                }
                if (d.DistanceEmission)
                    reasons.Add("it emits by distance travelled");
                if (d.PluginType is 1 or 3)
                    reasons.Add("connection and super stripes are not simulated");
                if (d.Fields != null)
                    reasons.AddRange(d.Fields.LoopBlockers());
                blockers.AddRange(reasons.Select(r => $"{d.Source.Name}: {r}"));
            }

            if (blockers.Count > 0)
                return LoopInfo.NotLoopable(string.Join("; ", blockers));

            if (!anyContinuous && !anyInfinite)
            {
                int length = RunToDeath(set, settle, primitives, cancel);
                return length < 0
                    ? LoopInfo.NotLoopable($"still alive after {MaxFrames} frames")
                    : new LoopInfo(LoopKind.OneShot, length, 0, 0, 0, null, null);
            }

            int unit = Lcm(periods);
            if (unit > MaxFrames)
                return LoopInfo.NotLoopable(
                    $"its periods only align after more than {MaxFrames} frames"
                );
            int n = Math.Max(Math.Max(minimumPeriod, maxLife + 1), gap);
            n = (n + unit - 1) / unit * unit;
            if (n > MaxFrames)
                return LoopInfo.NotLoopable($"a loop would be longer than {MaxFrames} frames");

            if (anyInfinite)
            {
                int fill = FillTime(set, primitives, measured, still, cancel, out string reason);
                if (fill < 0)
                    return LoopInfo.NotLoopable(reason);
                settle = Math.Max(settle, fill + ageSettle);
            }
            int warm = defs.Any(d => d.IsChild) ? 2 : 1;
            var caps = Capacities(
                set,
                n,
                settle + (warm - 1) * n,
                primitives,
                settle,
                seed,
                cancel
            );
            return new LoopInfo(
                LoopKind.Periodic,
                n,
                n,
                settle,
                warm,
                null,
                caps,
                seed,
                unsure.Count > 0 ? string.Join("; ", unsure) : null
            );
        }

        /// <summary>
        /// <paramref name="info"/> with its ring capacities counted for loop seed
        /// <paramref name="seed"/>; the same record when they already are.
        /// </summary>
        public static LoopInfo Reseed(
            EmitterSet set,
            LoopInfo info,
            uint seed,
            Func<Emitter, ulong, IEmissionPrimitive> primitives = null
        )
        {
            if (info.Kind != LoopKind.Periodic || info.Seed == seed)
                return info;
            int start = info.RegimeStart + (info.WarmPeriods - 1) * info.Period;
            var caps = Capacities(set, info.Period, start, primitives, info.RegimeStart, seed);
            return info with { Capacities = caps, Seed = seed };
        }

        /// <summary>
        /// Configures <paramref name="sim"/> to play <paramref name="info"/>: loop randoms, its
        /// period and regime, and the particle slots that keep each ring periodic. The capacities
        /// hold for <see cref="LoopInfo.Seed"/> only.
        /// </summary>
        public static void Apply(EffectSimulation sim, EmitterSet set, LoopInfo info)
        {
            sim.Mode = RandomMode.Loop;
            sim.LoopPeriod = info.Period;
            sim.LoopStart = info.RegimeStart;
            if (info.Capacities != null)
                foreach (var (id, cap) in info.Capacities)
                    sim.LoopCapacities[(set.Name, id)] = cap;
        }

        /// <summary>
        /// The offset to hand <see cref="SimDrawInputs"/> at simulation time
        /// <paramref name="time"/>: whole periods past the clip start, so every period draws
        /// with the same inputs.
        /// </summary>
        public static float TimeOffset(LoopInfo info, float time) =>
            info.Kind != LoopKind.Periodic || time < info.Start
                ? 0
                : MathF.Floor((time - info.Start) / info.Period) * info.Period;

        /// <summary>
        /// The longest a particle of <paramref name="d"/> can be around after the emission that
        /// led to it: its life, plus for a child its ancestors' lives and emission windows.
        /// </summary>
        static int Span(EmitterDef d) =>
            d.MaxLife + Trail(d) + (d.IsChild ? Span(d.Parent) + Math.Max(d.EmissionFrames, 0) : 0);

        /// <summary>Frames a dead particle's stripe stays on screen, shrinking a point a frame.</summary>
        static int Trail(EmitterDef d) =>
            d.Stripe != null ? (int)MathF.Ceiling(d.Stripe.History) : 0;

        /// <summary>Whether a particle's position can change with age: velocity, gravity or a field.</summary>
        static bool Moves(EmitterDef d) =>
            Max(d, EmitterAnim.AllDirectionVelocity, d.AllDirectionVelocity) != 0
            || Max(d, EmitterAnim.DirectionalVelocity, d.DirectionalScale) * d.Direction.Length()
                != 0
            || d.XzDiffusion != 0
            || d.Diffusion != System.Numerics.Vector3.Zero
            || Gravity(d)
            || d.Fields != null;

        static bool Gravity(EmitterDef d) =>
            Max(d, EmitterAnim.GravityScale, d.GravityScale) * d.GravityAxis.Length() != 0;

        /// <summary>The largest magnitude a term takes: its animation's keys, or its value
        /// when it has no keys, which leave the value as it is.</summary>
        static float Max(EmitterDef d, EmitterAnim a, float v) =>
            d.Anims[(int)a] is { Keys.Length: > 0 } t
                ? t.Keys.Max(k => MathF.Abs(k.X))
                : MathF.Abs(v);

        /// <summary>
        /// Infinite life particles that may move can still come to rest. A GPU particle's position
        /// is its birth velocity times <c>(1 - air^age) / (1 - air)</c>, still once the power is
        /// too small to change the difference, or without air resistance when it has no velocity
        /// at all. A CPU particle's position is in the streams, so the dry run watches them.
        /// </summary>
        static void Motion(
            EmitterDef d,
            HashSet<Emitter> measured,
            HashSet<Emitter> still,
            List<string> reasons,
            ref int ageSettle
        )
        {
            const string moving = "infinite life particles keep moving";
            if (d.CalcType != EmitterCalcType.Gpu)
            {
                if (Gravity(d) && d.Fields == null)
                    reasons.Add(moving);
                else
                    measured.Add(d.Source);
                return;
            }
            float air = d.AirRegist;
            if (d.Fields != null || Gravity(d))
                reasons.Add(moving);
            else if (air == 1)
                still.Add(d.Source);
            else if (Converges(air, out int frames))
                ageSettle = Math.Max(ageSettle, frames);
            else
                reasons.Add(moving);
        }

        /// <summary>
        /// Frames of age after which <c>k^age</c> is below 2^-30, far under what one minus it can
        /// resolve, so a term decaying at <paramref name="k"/> is constant.
        /// </summary>
        static bool Converges(float k, out int frames)
        {
            frames = 0;
            if (!(k >= 0 && k < 1))
                return false;
            frames = k == 0 ? 1 : (int)MathF.Ceiling(-30 / MathF.Log2(k));
            return frames <= MaxFrames;
        }

        /// <summary>An emitter that keeps emitting for as long as the set lives.</summary>
        static bool Continuous(EmitterDef d)
        {
            if (d.IsChild)
                return Continuous(d.Parent);
            var rate = d.Anims[(int)EmitterAnim.EmissionRate];
            bool stops =
                rate is { Enabled: true, Loop: false }
                && rate.Keys.Length > 0
                && rate.Keys[^1].X == 0;
            return !d.OneTime && !stops;
        }

        /// <summary>
        /// Terms that change with particle age for as long as it lives. An infinite life
        /// particle is still there a period later, so each must have a whole period.
        /// </summary>
        static void AgeTerms(
            EmitterDef d,
            Func<Emitter, int, bool> sampled,
            Func<Emitter, int, bool> vertexSampled,
            List<int> periods,
            List<string> reasons,
            List<string> unsure,
            ref int ageSettle
        )
        {
            var b = d.StaticBlock;
            var data = d.Source.Section;
            float F(int o) => BitConverter.ToSingle(b, o);
            // Rotation advances by the add rate times age, or its decaying sum, or not at all.
            float regist = F(0xA2C);
            bool linear = regist == 1;
            if (regist != 0 && !linear)
            {
                if (Converges(regist, out int rest))
                    ageSettle = Math.Max(ageSettle, rest);
                else
                    reasons.Add("infinite life particle rotation never settles");
            }
            for (int axis = 0; axis < 3 && linear; axis++)
            {
                if (F(0xA30 + 4 * axis) != 0)
                    reasons.Add("infinite life particles rotate at a random speed");
                else if (F(0xA20 + 4 * axis) != 0)
                    Cycle(
                        F(0xA20 + 4 * axis) / VfxMath.TwoPi,
                        "infinite life particles rotate",
                        periods,
                        reasons
                    );
            }
            for (int axis = 0; axis < 3; axis++)
                if (data.GetU8(0xC40 + axis) != 0 && F(0xA50 + 4 * axis) != 0)
                {
                    if (Whole(F(0xA60 + 4 * axis), out int p) && p > 0)
                        periods.Add(p);
                    else
                        reasons.Add("a rotation wave on infinite particle age has no whole period");
                }
            for (int t = 0; t < EmitterLayout.SamplerCount; t++)
            {
                if (sampled?.Invoke(d.Source, t) == false)
                    continue;
                int shift = EmitterLayout.TexShiftAnims + EmitterLayout.TexShiftAnimStride * t;
                int anim = EmitterLayout.TextureAnims + EmitterLayout.TextureAnimStride * t;
                var sampler = d.Source.GetSampler(t);
                for (int k = 0; k < 2; k++)
                {
                    if (F(shift + 4 * k) == 0)
                        continue;
                    // A mirrored axis repeats every two texture widths; a clamped one never.
                    byte wrap = k == 0 ? sampler.WrapU : sampler.WrapV;
                    if (wrap is not (0 or 1))
                        reasons.Add(
                            $"texture {t} scrolls a clamped axis with infinite particle age"
                        );
                    else
                        Cycle(
                            F(shift + 4 * k) / (wrap == 0 ? 2 : 1),
                            $"texture {t} scrolls with infinite particle age",
                            periods,
                            reasons
                        );
                }
                if (F(shift + 0x30) != 0)
                    Cycle(
                        F(shift + 0x30) / VfxMath.TwoPi,
                        $"texture {t} rotates with infinite particle age",
                        periods,
                        reasons
                    );
                if (F(shift + 0x18) != 0 || F(shift + 0x1C) != 0)
                    reasons.Add($"texture {t} scales with infinite particle age");
                if (data.GetU8(anim) is 2 or 3)
                    PatternTerm(b, t, data.GetU8(anim) == 3, periods, reasons, ref ageSettle);
                //A slot the vertex stage reads with nothing of the resource moving it is indexed
                //by the program itself: a vertex animation texture read by age may or may not
                //repeat with the period, and only the program knows.
                bool still =
                    F(shift) == 0
                    && F(shift + 4) == 0
                    && F(shift + 0x30) == 0
                    && F(shift + 0x18) == 0
                    && F(shift + 0x1C) == 0
                    && data.GetU8(anim) is not (2 or 3);
                if (still && vertexSampled?.Invoke(d.Source, t) == true)
                    unsure.Add(
                        $"its vertex program reads texture {t} by its own clock on particles that never die"
                    );
            }
            // Alpha (0xD84) and scale x (0xD85) run wave x; scale y (0xD86) its own wave y.
            bool waveX = data.GetU8(0xD84) != 0 || data.GetU8(0xD85) != 0;
            bool waveY = data.GetU8(0xD85) != 0 && data.GetU8(0xD86) != 0;
            for (int k = 0; k < 2; k++)
                if ((k == 0 ? waveX : waveY) && F(0x100 + 4 * k) != 0)
                {
                    if (!Whole(F(0x108 + 4 * k), out int p) || p <= 0)
                        reasons.Add("fluctuation on infinite particle age has no whole period");
                    else
                        periods.Add(p);
                }
            for (int k = 0; k < 5; k++)
                if (F(0xA0 + 4 * k) > 0)
                    periods.Add((int)F(0xA0 + 4 * k));
        }

        /// <summary>
        /// A pattern animation shows cell <c>int(age / step)</c> of its table, wrapped for a loop
        /// and held on the last for a clamp: a loop repeats every step times the table count, a
        /// clamp is constant once it reaches the end.
        /// </summary>
        static void PatternTerm(
            byte[] b,
            int t,
            bool loop,
            List<int> periods,
            List<string> reasons,
            ref int ageSettle
        )
        {
            int ptn = EmitterLayout.TexPatternAnims + EmitterLayout.TexPatternAnimStride * t;
            int table = (int)BitConverter.ToSingle(b, ptn);
            int step = (int)BitConverter.ToSingle(b, ptn + 4);
            if (table <= 1)
                return;
            long length = (long)step * table;
            if (step <= 0 || length > MaxFrames)
                reasons.Add($"texture {t} pattern animation runs on infinite particle age");
            else if (loop)
                periods.Add((int)length);
            else
                ageSettle = Math.Max(ageSettle, (int)length);
        }

        /// <summary>
        /// A term advancing <paramref name="perFrame"/> turns a frame repeats after the first
        /// whole number of frames that make close to whole turns.
        /// </summary>
        static void Cycle(float perFrame, string what, List<int> periods, List<string> reasons)
        {
            float rate = MathF.Abs(perFrame);
            for (int p = 1; p <= MaxFrames; p++)
            {
                float turns = rate * p;
                if (MathF.Abs(turns - MathF.Round(turns)) < 1e-4f && turns >= 0.5f)
                {
                    periods.Add(p);
                    return;
                }
            }
            reasons.Add($"{what} and repeats after no whole number of frames");
        }

        static bool Whole(float v, out int n)
        {
            n = (int)MathF.Round(v);
            return MathF.Abs(v - n) <= 1e-4f * MathF.Max(1, MathF.Abs(v)) && n <= MaxFrames;
        }

        /// <summary>Emissions after which the fractional carry of <paramref name="rate"/> is back at zero, or 0.</summary>
        static int RateCycle(float rate)
        {
            for (int q = 1; q <= 64; q++)
                if (Whole(rate * q, out _))
                    return q;
            return 0;
        }

        static long Gcd(long a, long b) => b == 0 ? a : Gcd(b, a % b);

        static int Lcm(IEnumerable<int> values)
        {
            long l = 1;
            foreach (int v in values)
            {
                if (v <= 0)
                    continue;
                l = l / Gcd(l, v) * v;
                if (l > MaxFrames)
                    return int.MaxValue;
            }
            return (int)l;
        }

        /// <summary>
        /// Frames until the set dies, or past <paramref name="settle"/> has nothing alive left to
        /// show; -1 if it outlives <see cref="MaxFrames"/>.
        /// </summary>
        static int RunToDeath(
            EmitterSet set,
            int settle,
            Func<Emitter, ulong, IEmissionPrimitive> primitives,
            CancellationToken cancel
        )
        {
            var sim = new EffectSimulation { PrimitiveSource = primitives };
            var inst = sim.Play(set, Matrix34.Identity);
            for (int f = 1; f <= MaxFrames; f++)
            {
                cancel.ThrowIfCancellationRequested();
                sim.Calculate(1);
                if (!inst.Alive || (f > settle && !inst.AllEmitters().Any(e => e.ShowsAnything)))
                    return f;
            }
            return -1;
        }

        /// <summary>
        /// The frame after which no emitter adds an infinite life particle and the
        /// <paramref name="measured"/> emitters' particles no longer move; -1 with the reason if
        /// that never comes, or if a <paramref name="still"/> emitter gives one a velocity.
        /// </summary>
        static int FillTime(
            EmitterSet set,
            Func<Emitter, ulong, IEmissionPrimitive> primitives,
            HashSet<Emitter> measured,
            HashSet<Emitter> still,
            CancellationToken cancel,
            out string reason
        )
        {
            var sim = new EffectSimulation { PrimitiveSource = primitives };
            var inst = sim.Play(set, Matrix34.Identity);
            var births = new Dictionary<EmitterInstance, int>();
            var motion = new Dictionary<EmitterInstance, float[][]>();
            int lastBirth = 0,
                lastMove = 0;
            reason = null;
            for (int f = 1; f <= MaxFrames; f++)
            {
                cancel.ThrowIfCancellationRequested();
                sim.Calculate(1);
                foreach (var e in inst.AllEmitters())
                {
                    if (!e.Def.InfiniteLife)
                        continue;
                    if (!births.TryGetValue(e, out int b) || b != e.Births)
                        lastBirth = f;
                    births[e] = e.Births;
                    if (measured.Contains(e.Def.Source) && Moved(e, motion))
                        lastMove = f;
                    if (still.Contains(e.Def.Source) && HasVelocity(e))
                    {
                        reason = $"{e.Def.Source.Name}: infinite life particles keep moving";
                        return -1;
                    }
                }
                if (f - Math.Max(lastBirth, lastMove) > MaxFrames / 4)
                    return Math.Max(lastBirth, lastMove);
            }
            reason =
                lastMove > lastBirth
                    ? "infinite life particles keep moving"
                    : "infinite life particles keep being added";
            return -1;
        }

        /// <summary>Whether the particles' positions or velocities changed since the last call.</summary>
        static bool Moved(EmitterInstance e, Dictionary<EmitterInstance, float[][]> last)
        {
            var ps = e.Particles;
            var pos = ps[ParticleStream.LocalPos];
            var vec = ps[ParticleStream.LocalVec];
            if (
                last.TryGetValue(e, out var was)
                && pos.AsSpan().SequenceEqual(was[0])
                && vec.AsSpan().SequenceEqual(was[1])
            )
                return false;
            last[e] = new[] { (float[])pos.Clone(), (float[])vec.Clone() };
            return true;
        }

        static bool HasVelocity(EmitterInstance e)
        {
            var ps = e.Particles;
            var vec = ps[ParticleStream.LocalVec];
            for (int i = 0; i < ps.Capacity; i++)
                if (
                    ps.IsAlive(i, e.Time)
                    && (vec[4 * i] != 0 || vec[4 * i + 1] != 0 || vec[4 * i + 2] != 0)
                )
                    return true;
            return false;
        }

        /// <summary>
        /// Particles each emitter creates in the period from <paramref name="start"/>: the ring
        /// size that puts every particle back in its slot one period later. Per particle child emitters live
        /// and die with their parent particle and keep the game's size.
        /// </summary>
        static Dictionary<uint, int> Capacities(
            EmitterSet set,
            int n,
            int start,
            Func<Emitter, ulong, IEmissionPrimitive> primitives,
            int regime,
            uint seed,
            CancellationToken cancel = default
        )
        {
            var sim = new EffectSimulation
            {
                PrimitiveSource = primitives,
                Mode = RandomMode.Loop,
                LoopPeriod = n,
                LoopStart = regime,
                LoopSeed = seed,
                LoopOverwriteAll = true,
            };
            var inst = sim.Play(set, Matrix34.Identity);
            var at = new Dictionary<uint, int>();
            for (int f = 0; f < start + n; f++)
            {
                cancel.ThrowIfCancellationRequested();
                if (f == start)
                    foreach (var e in inst.AllEmitters())
                        if (!e.Def.EmitterPerParticle)
                            at[e.Id] = e.Births;
                sim.Calculate(1);
            }
            var caps = new Dictionary<uint, int>();
            foreach (var e in inst.AllEmitters())
                if (at.TryGetValue(e.Id, out int b) && !e.Def.InfiniteLife && e.Births > b)
                    caps[e.Id] = e.Births - b;
            return caps;
        }
    }
}
