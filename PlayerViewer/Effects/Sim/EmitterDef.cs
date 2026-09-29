using System;
using System.Numerics;
using EffectLibrary;

namespace PlayerViewer.Effects.Sim
{
    /// <summary>The fourteen emitter keyframe animation slots, in the runtime's order.</summary>
    public enum EmitterAnim
    {
        Scale,
        Rotate,
        Translate,
        Color0,
        Color1,
        EmissionRate,
        ParticleLife,
        Alpha0,
        Alpha1,
        AllDirectionVelocity,
        DirectionalVelocity,
        ParticleScale,
        VolumeScale,
        GravityScale,
    }

    /// <summary>One emitter keyframe track: keys of xyz and a frame, stepped or linear, looped or not.</summary>
    public sealed class AnimTrack
    {
        public bool Enabled;
        public bool Loop;

        /// <summary>1 holds each key until the next, 0 interpolates linearly, anything else keeps the current value.</summary>
        public byte Interpolation;
        public Vector4[] Keys;

        public float LastFrame => Keys.Length == 0 ? 0 : Keys[^1].W;

        public static AnimTrack From(EmitterAnimation a)
        {
            var t = new AnimTrack
            {
                Enabled = a.Enabled,
                Loop = a.Loop,
                Interpolation = a.Interpolation,
                Keys = new Vector4[Math.Max(0, a.KeyCount)],
            };
            for (int i = 0; i < t.Keys.Length; i++)
                t.Keys[i] = a.GetKey(i);
            return t;
        }

        /// <summary>The value at <paramref name="time"/>; sets <paramref name="ended"/> once past the last key.</summary>
        public Vector3 Evaluate(float time, ref bool ended, Vector3 current)
        {
            int n = Keys.Length;
            if (n == 0)
                return current;
            if (Loop)
                time = FMod(time, Keys[n - 1].W);
            return Keyframes.Evaluate(Keys, Interpolation, time, current, ref ended);
        }

        /// <summary>C's fmodf: the remainder with the dividend's sign.</summary>
        public static float FMod(float x, float y) => y == 0 ? float.NaN : x % y;
    }

    /// <summary>
    /// Everything the simulation reads from one ResEmitter, at the offsets the vfx2 runtime
    /// reads them. Built once per emitter resource.
    /// </summary>
    public sealed class EmitterDef
    {
        public Emitter Source { get; }
        public EmitterDef Parent { get; }
        public EmitterDef[] Children { get; }

        public EmitterCalcType CalcType;
        public byte FollowType;
        public byte SeedType;
        public uint RandomSeed;
        public bool UpdateMatrixByEmit;
        public bool StopEmitOnFade;
        public byte FadeInAlphaCurve,
            FadeOutAlphaCurve;
        public bool FadeInScale,
            FadeOutScale;
        public int FadeOutTime,
            FadeInTime;
        public float FadeInScaleMin,
            FadeOutScaleMin;
        public uint DrawPath;

        public Vector3 Translate,
            TranslateRandom,
            Rotate,
            RotateRandom,
            Scale;
        public Vector4 Color0,
            Color1;

        public bool InheritAlpha0,
            InheritAlpha1;
        public bool EmitterPerParticle;

        public bool OneTime;
        public bool WorldGravity;
        public bool DistanceEmission;
        public bool WorldOrientedVelocity;
        public int StartFrame,
            StartLifePercent,
            EmissionFrames;

        /// <summary>A child's emission start as a share of its parent particle's life.</summary>
        public float StartLifeShare => StartLifePercent / 100f;
        public float Rate;
        public int RateRandom;
        public int Interval,
            IntervalRandom;
        public float PositionRandom;
        public float GravityScale;
        public Vector3 GravityAxis;
        public float DistUnit,
            DistMin,
            DistMax,
            DistMargin;
        public int DistMaxParticles;

        public byte VolumeType;
        public bool SweepStartRandom;
        public byte ArcType;
        public byte SphereTableIndex;
        public byte Sphere64Count;
        public byte PoleAxis;
        public float SweepLongitude,
            SweepLatitude,
            SweepStart,
            SurfacePosRandom,
            Caliber,
            LineCenter,
            LineLength;
        public Vector3 Radius,
            FormScale;
        public int PrimEmitType;
        public ulong ShapePrimitiveId;
        public int DivideCircle,
            DivideCircleRandom,
            DivideLine,
            DivideLineRandom;

        public bool InfiniteLife;
        public int Life,
            LifeRandom;
        public float MomentumRandom;

        public float AllDirectionVelocity,
            DirectionalScale;
        public Vector3 Direction;
        public float DiffusionAngle,
            XzDiffusion;
        public Vector3 Diffusion;
        public float VelocityRandom,
            EmitterVelocityInherit,
            EmitterVelocityInheritMax;
        public Vector3 ParticleScale,
            ParticleScaleRandom;

        /// <summary>The static uniform block after the load time rewrite; the runtime reads some
        /// particle values from it rather than from the raw resource.</summary>
        public byte[] StaticBlock;

        public Vector3 InitialRotation;
        public float AirRegist;

        /// <summary>The static block's gravity, xyz and the scale in w.</summary>
        public Vector4 StaticGravity;

        /// <summary>The animation tracks by <see cref="EmitterAnim"/>; null where absent.</summary>
        public AnimTrack[] Anims = new AnimTrack[14];
        public bool HasAnims,
            HasSrtAnims;

        /// <summary>The longest particle life the life animation or the resource allows.</summary>
        public int MaxLife;

        /// <summary>The highest emission rate the rate animation or the resource allows.</summary>
        public float MaxRate;

        public FieldSet Fields;

        /// <summary>A stripe or area loop plugin (EP01..EP04), 0 for none.</summary>
        public int PluginType;

        /// <summary>The history stripe's parameters when <see cref="PluginType"/> is 2.</summary>
        public StripeParams Stripe;

        /// <summary>The area loop's parameters when <see cref="PluginType"/> is 4.</summary>
        public AreaLoopParams AreaLoop;

        /// <summary>The game callback set the particles go through (0xCF0), 0 for none.</summary>
        public int CustomAction;

        /// <summary>The light a particle becomes when <see cref="CustomAction"/> is the light
        /// action.</summary>
        public LightActionParams Light;

        /// <summary>The emission primitive of a primitive volume emitter, when the host resolved one.</summary>
        public IEmissionPrimitive ShapePrimitive;

        public EmitterDef(
            Emitter e,
            EmitterDef parent = null,
            Func<Emitter, ulong, IEmissionPrimitive> primitives = null
        )
        {
            Source = e;
            Parent = parent;
            var s = e.Section;
            CalcType = (EmitterCalcType)s.GetU8(0xA92);
            FollowType = s.GetU8(0xA93);
            SeedType = s.GetU8(0xA94);
            UpdateMatrixByEmit = s.GetU8(0xA95) != 0;
            StopEmitOnFade = s.GetU8(0xA98) != 0;
            FadeInAlphaCurve = s.GetU8(0xA99);
            FadeInScale = s.GetU8(0xA9A) != 0;
            FadeOutAlphaCurve = s.GetU8(0xA9B);
            FadeOutScale = s.GetU8(0xA9C) != 0;
            RandomSeed = s.GetU32(0xAA0);
            DrawPath = s.GetU32(0xAA4);
            FadeOutTime = s.GetS32(0xAA8);
            FadeInTime = s.GetS32(0xAAC);
            Translate = V3(s, 0xAB0);
            TranslateRandom = V3(s, 0xABC);
            Rotate = V3(s, 0xAC8);
            RotateRandom = V3(s, 0xAD4);
            Scale = V3(s, 0xAE0);
            Color0 = V4(s, 0xAEC);
            Color1 = V4(s, 0xAFC);
            FadeInScaleMin = s.GetF32(0xB18);
            FadeOutScaleMin = s.GetF32(0xB1C);
            InheritAlpha0 = s.GetU8(0xB26) != 0 && s.GetU8(0xB2A) != 0;
            InheritAlpha1 = s.GetU8(0xB27) != 0 && s.GetU8(0xB2B) != 0;
            EmitterPerParticle = s.GetU8(0xB2C) != 0;

            OneTime = s.GetU8(0xB38) != 0;
            WorldGravity = s.GetU8(0xB39) != 0;
            DistanceEmission = s.GetU8(0xB3A) != 0;
            WorldOrientedVelocity = s.GetU8(0xB3B) != 0;
            StartFrame = s.GetS32(0xB3C);
            StartLifePercent = s.GetS32(0xB40);
            EmissionFrames = s.GetS32(0xB44);
            Rate = s.GetF32(0xB48);
            RateRandom = s.GetS32(0xB4C);
            Interval = s.GetS32(0xB50);
            IntervalRandom = s.GetS32(0xB54);
            PositionRandom = s.GetF32(0xB58);
            GravityScale = s.GetF32(0xB5C);
            GravityAxis = V3(s, 0xB60);
            DistUnit = s.GetF32(0xB6C);
            DistMin = s.GetF32(0xB70);
            DistMax = s.GetF32(0xB74);
            DistMargin = s.GetF32(0xB78);
            DistMaxParticles = s.GetS32(0xB7C);

            VolumeType = s.GetU8(0xB80);
            SweepStartRandom = s.GetU8(0xB81) != 0;
            ArcType = s.GetU8(0xB82);
            SphereTableIndex = s.GetU8(0xB83);
            Sphere64Count = s.GetU8(0xB84);
            PoleAxis = s.GetU8(0xB85);
            SweepLongitude = s.GetF32(0xB88);
            SweepLatitude = s.GetF32(0xB8C);
            SweepStart = s.GetF32(0xB90);
            SurfacePosRandom = s.GetF32(0xB94);
            Caliber = s.GetF32(0xB98);
            LineCenter = s.GetF32(0xB9C);
            LineLength = s.GetF32(0xBA0);
            Radius = V3(s, 0xBA4);
            FormScale = V3(s, 0xBB0);
            PrimEmitType = s.GetS32(0xBBC);
            ShapePrimitiveId = s.GetU64(0xBC0);
            DivideCircle = s.GetS32(0xBC8);
            DivideCircleRandom = s.GetS32(0xBCC);
            DivideLine = s.GetS32(0xBD0);
            DivideLineRandom = s.GetS32(0xBD4);

            InfiniteLife = s.GetU8(0xBE8) != 0;
            Life = s.GetS32(0xBF8);
            LifeRandom = s.GetS32(0xBFC);
            MomentumRandom = s.GetF32(0xC00);

            AllDirectionVelocity = s.GetF32(0xCF4);
            DirectionalScale = s.GetF32(0xCF8);
            Direction = V3(s, 0xCFC);
            DiffusionAngle = s.GetF32(0xD08);
            XzDiffusion = s.GetF32(0xD0C);
            Diffusion = V3(s, 0xD10);
            VelocityRandom = s.GetF32(0xD1C);
            EmitterVelocityInherit = s.GetF32(0xD20);
            EmitterVelocityInheritMax = s.GetF32(0xD24);
            ParticleScale = V3(s, 0xD60);
            ParticleScaleRandom = V3(s, 0xD6C);

            StaticBlock = StaticUniformBlock.Build(e);
            InitialRotation = StaticV3(0xA00);
            AirRegist = BitConverter.ToSingle(StaticBlock, 0x0E0);
            StaticGravity = new Vector4(StaticV3(0x0D0), BitConverter.ToSingle(StaticBlock, 0x0DC));

            foreach (var a in e.Animations)
            {
                int slot = a.Tag switch
                {
                    "EAES" => (int)EmitterAnim.Scale,
                    "EAER" => (int)EmitterAnim.Rotate,
                    "EAET" => (int)EmitterAnim.Translate,
                    "EAC0" => (int)EmitterAnim.Color0,
                    "EAC1" => (int)EmitterAnim.Color1,
                    "EATR" => (int)EmitterAnim.EmissionRate,
                    "EAPL" => (int)EmitterAnim.ParticleLife,
                    "EAA0" => (int)EmitterAnim.Alpha0,
                    "EAA1" => (int)EmitterAnim.Alpha1,
                    "EAOV" => (int)EmitterAnim.AllDirectionVelocity,
                    "EADV" => (int)EmitterAnim.DirectionalVelocity,
                    "EASL" => (int)EmitterAnim.ParticleScale,
                    "EASS" => (int)EmitterAnim.VolumeScale,
                    "EAGV" => (int)EmitterAnim.GravityScale,
                    _ => -1,
                };
                if (slot < 0)
                    continue;
                Anims[slot] = AnimTrack.From(a);
                HasAnims = true;
                if (slot <= (int)EmitterAnim.Translate)
                    HasSrtAnims = true;
            }

            // The load time rewrite: shapes that emit a fixed set of points emit all of them
            // once per emission, and every other shape has no emission type.
            if (VolumeType is not (2 or 5 or 6 or 13 or 15))
                PrimEmitType = -1;
            if (VolumeType == 15)
                ShapePrimitive = primitives?.Invoke(e, ShapePrimitiveId);
            if (PrimEmitType == 0)
            {
                if (VolumeType is 2 or 13)
                    Rate = 1;
                else if (VolumeType == 5)
                    Rate = SphereTables.SmallCounts[Math.Min((int)SphereTableIndex, 7)];
                else if (VolumeType == 6)
                    Rate = Sphere64Count;
                else if (VolumeType == 15 && ShapePrimitive != null)
                    Rate = ShapePrimitive.Count;
            }

            var lifeAnim = Anims[(int)EmitterAnim.ParticleLife];
            MaxLife = lifeAnim != null ? (int)MaxKeyX(lifeAnim) : Life;
            var rateAnim = Anims[(int)EmitterAnim.EmissionRate];
            MaxRate = rateAnim != null ? MaxKeyX(rateAnim) : Rate;

            Fields = FieldSet.From(e);
            PluginType =
                s.FindSub("EP01") != null ? 1
                : s.FindSub("EP02") != null ? 2
                : s.FindSub("EP03") != null ? 3
                : s.FindSub("EP04") != null ? 4
                : 0;
            if (s.FindSub("EP02") is { } ep02)
                Stripe = StripeParams.Read(ep02.Payload.Span);
            if (s.FindSub("EP04") is { } ep04 && ep04.Payload.Length >= 76)
                AreaLoop = AreaLoopParams.Read(ep04.Payload.Span);
            CustomAction = s.GetS32(EmitterLayout.CustomAction);
            if (
                CustomAction == EffectLights.LightAction
                && s.FindSub("CADP") is { } cadp
                && cadp.Payload.Length >= 20
            )
                Light = LightActionParams.Read(cadp.Payload.Span);

            Children = new EmitterDef[e.Children.Count];
            for (int i = 0; i < Children.Length; i++)
                Children[i] = new EmitterDef(e.Children[i], this, primitives);
        }

        public bool IsChild => Parent != null;

        /// <summary>The circle or line divided shapes emit their division count per emission.</summary>
        public bool IsUnisonDivided => PrimEmitType == 0 && VolumeType is 2 or 13;

        /// <summary>The shapes that take the resource rate rather than the rate animation.</summary>
        public bool UsesResourceRate => VolumeType is 2 or 5 or 6 or 13 && PrimEmitType == 0;

        static float MaxKeyX(AnimTrack t)
        {
            float m = 0;
            foreach (var k in t.Keys)
                m = MathF.Max(m, k.X);
            return m;
        }

        /// <summary>The particle slots the runtime allocates, before rounding.</summary>
        public int RequiredCapacity(int parentCapacity)
        {
            int n;
            if (DistanceEmission)
                n = DistMaxParticles;
            else
            {
                float rate = MaxRate;
                if (Anims[(int)EmitterAnim.EmissionRate] != null && rate < 1)
                    rate = 1;
                float duration = EmissionFrames != 0 ? EmissionFrames : 1;
                int intervalP1 = Interval + 1;
                int a = (int)MathF.Ceiling(duration / intervalP1);
                int b = (int)MathF.Ceiling(MaxLife / (float)intervalP1);
                int count = (a >= b && !InfiniteLife) || !OneTime ? b : a;
                float ceilRate = MathF.Ceiling(rate);
                n = (int)(MathF.Ceiling(rate * count) + ceilRate * 2);
                bool extra = CalcType == EmitterCalcType.GpuCompute && !OneTime;
                if (PrimEmitType != 0)
                {
                    if (extra)
                        n += (int)ceilRate;
                }
                else
                {
                    int divide =
                        VolumeType == 13 ? DivideLine
                        : VolumeType == 2 ? DivideCircle
                        : 1;
                    n *= divide;
                    if (extra)
                        n += (int)ceilRate * divide;
                }
            }
            if (IsChild && !EmitterPerParticle)
                n *= parentCapacity;
            return n;
        }

        /// <summary>The capacity after the runtime's rounding: even, and a multiple of 32 for stream out.</summary>
        public static int RoundCapacity(int n, EmitterCalcType calc)
        {
            if (n > 0 && (n & 1) == 1)
                n++;
            if (calc == EmitterCalcType.GpuCompute)
                n = (n + 31) & ~31;
            return n;
        }

        Vector3 StaticV3(int o) =>
            new(
                BitConverter.ToSingle(StaticBlock, o),
                BitConverter.ToSingle(StaticBlock, o + 4),
                BitConverter.ToSingle(StaticBlock, o + 8)
            );

        static Vector3 V3(VfxSection s, int o) =>
            new(s.GetF32(o), s.GetF32(o + 4), s.GetF32(o + 8));

        static Vector4 V4(VfxSection s, int o) =>
            new(s.GetF32(o), s.GetF32(o + 4), s.GetF32(o + 8), s.GetF32(o + 12));
    }
}
