using System;
using System.Collections.Generic;
using System.Numerics;
using EffectLibrary;

namespace PlayerViewer.Effects.Sim
{
    /// <summary>
    /// One running emitter: its clock, emission state, matrices, animation values and particle
    /// ring. The calculation order is its CalculateEmitter.
    /// </summary>
    public sealed partial class EmitterInstance
    {
        public EmitterSetInstance Set { get; }
        public EmitterDef Def { get; }
        public EmitterInstance Parent { get; }

        /// <summary>For an emitter per particle child, the parent's particle slot.</summary>
        public int ParentSlot { get; private set; }

        /// <summary>Stable within the set: the emitter's path of indices, for loop seeding.</summary>
        public uint Id { get; }

        public float Time;
        public float FrameRate;
        float _frameAccum;

        float _emitCounter,
            _emitCarry,
            _emitInterval;
        bool _hasEmitted;
        public float RateScale = 1,
            IntervalRatio = 1,
            LifeScale = 1;
        float _lastEmitTime;

        public float FadeOut = 1;
        public float FadeIn;

        public VfxRandom Random;
        int _createCounter;
        bool _loopRing;
        int _sequence;

        /// <summary>The life of the parent particle this child emits from, which bounds its emission.</summary>
        public float AnchorLife;
        internal bool ParentKilled;
        internal bool HasLiveChildren;
        bool _lastKill;

        public Matrix34 ResSrt,
            ResRt,
            AnimSrt,
            AnimRt,
            ParentSrt,
            ParentRt,
            Srt,
            Rt;
        Vector3 _prevPos;
        public Vector3 LocalVec;
        public Vector4 ColorScale0 = Vector4.One,
            ColorScale1 = Vector4.One;

        /// <summary>The emitter animation values by <see cref="EmitterAnim"/>.</summary>
        public readonly Vector3[] AnimValues = new Vector3[14];
        readonly bool[] _animEnded = new bool[14];

        /// <summary>Child emitters per child resource: the one shared emitter of a light child,
        /// or one per parent particle for an emitter per particle child.</summary>
        public readonly List<EmitterInstance>[] ChildLists;

        /// <summary>The shared emitter of each light child, null for the others.</summary>
        public readonly EmitterInstance[] LightChildren;

        public ParticleStore Particles { get; private set; }

        /// <summary>The history stripes of a CPU emitter with the stripe plugin, else null.</summary>
        public HistoryStripes Stripes { get; private set; }

        /// <summary>The dynamic uniform block of the last calculation.</summary>
        public readonly byte[] DynamicBlock = new byte[EffectBindings.DynamicSize];

        // Set while a light child emits from one parent particle.
        Vector3 _emitParentPos,
            _emitParentVec;
        int _emitParentSlot = -1;

        // Emitter per particle children: what they inherit from the parent particle.
        internal Vector4 InheritPos,
            InheritVec,
            InheritScale,
            InheritRotate,
            InheritRandom;
        internal Vector3 AnchorPos,
            AnchorVelocity;
        internal float AnchorAge;

        public EmitterInstance(
            EmitterSetInstance set,
            EmitterDef def,
            EmitterInstance parent,
            int parentSlot
        )
        {
            Set = set;
            Def = def;
            Parent = parent;
            ParentSlot = parentSlot;
            Id = (uint)(def.Source.Index + 1) | (parent == null ? 0 : (parent.Id << 8));
            ChildLists = new List<EmitterInstance>[def.Children.Length];
            LightChildren = new EmitterInstance[def.Children.Length];
            for (int i = 0; i < ChildLists.Length; i++)
                ChildLists[i] = new List<EmitterInstance>();
            Initialize();
        }

        public bool IsChild => Parent != null;

        /// <summary>Whether a particle is alive or a stripe still has something to draw.</summary>
        public bool ShowsAnything => Particles.AnyAlive(Time) || Stripes is { AnyDrawn: true };

        /// <summary>A stream out emitter whose compute programs the CPU stands in for.</summary>
        public bool CpuFallback =>
            Def.CalcType == EmitterCalcType.GpuCompute && Set.System.StreamOutOnCpu;

        /// <summary>A CPU stepped emitter with a field the simulation does not run.</summary>
        public bool FieldsIncomplete =>
            (Def.CalcType == EmitterCalcType.Cpu || CpuFallback)
            && Def.Fields is { FullySimulated: false };

        void Initialize()
        {
            Srt = Rt = AnimSrt = AnimRt = ParentSrt = ParentRt = Matrix34.Identity;
            Srt.T = Rt.T = Vector3.Zero;
            FadeIn = Def.FadeInAlphaCurve != 0 || Def.FadeInScale ? 0 : 1;
            FadeOut = 1;
            switch (Def.SeedType)
            {
                case 2:
                    Random.SetSeed(unchecked(Def.RandomSeed * 3755744309u));
                    break;
                case 1:
                    Random.SetSeed(Set.RandomSeed);
                    break;
                case 0:
                    uint seed = Set.System.Global.Next();
                    if (Set.System.Mode == RandomMode.Loop)
                        seed = Set.System.Hash(Id, 0x5EED, 0, 0);
                    if (Set.System.EmitterSeedOverrides.TryGetValue(Def.Source.Name, out uint o))
                        seed = o;
                    Random.SetSeed(seed);
                    break;
            }
            if (Set.System.Mode == RandomMode.Loop && Def.SeedType != 2)
                Random.SetSeed(Set.System.Hash(Id, 0x5EED, 0, 0));
            SeedChild();
            ResourceUpdate();
            _emitInterval = Def.Interval + 1;
            _emitCounter = _emitInterval;
            _prevPos = Set.WorldSrt.T;
            LocalVec = Vector3.Zero;
            Set.EmitterCounter++;
        }

        /// <summary>Particle slots, the animation defaults and the resource matrix.</summary>
        void ResourceUpdate()
        {
            int parentCapacity = Parent?.Particles?.Capacity ?? 0;
            int capacity = Def.RequiredCapacity(parentCapacity);
            _loopRing = false;
            if (
                Set.System.Mode == RandomMode.Loop
                && !Def.InfiniteLife
                && !(IsChild && Def.EmitterPerParticle)
            )
            {
                if (Set.System.LoopCapacity(this) is int lc and > 0)
                {
                    capacity = lc;
                    _loopRing = true;
                }
                else if (Set.System.LoopOverwriteAll)
                {
                    capacity = capacity * 4 + 16;
                    _loopRing = true;
                }
            }
            Particles = new ParticleStore(
                capacity,
                IsChild,
                Def.FollowType != 0,
                Def.Children.Length
            );
            Stripes =
                Def.Stripe != null && Def.CalcType == EmitterCalcType.Cpu
                    ? new HistoryStripes(this, Def.Stripe, Particles.Capacity)
                    : null;
            CreateResMatrix();
            AnimValues[(int)EmitterAnim.Scale] = Def.Scale;
            AnimValues[(int)EmitterAnim.Rotate] = Def.Rotate;
            AnimValues[(int)EmitterAnim.Translate] = Def.Translate;
            AnimValues[(int)EmitterAnim.Color0] = new Vector3(
                Def.Color0.X,
                Def.Color0.Y,
                Def.Color0.Z
            );
            AnimValues[(int)EmitterAnim.Color1] = new Vector3(
                Def.Color1.X,
                Def.Color1.Y,
                Def.Color1.Z
            );
            AnimValues[(int)EmitterAnim.EmissionRate] = new Vector3(Def.MaxRate, 0, 0);
            AnimValues[(int)EmitterAnim.ParticleLife] = new Vector3(Def.MaxLife, 0, 0);
            AnimValues[(int)EmitterAnim.Alpha0] = new Vector3(Def.Color0.W, 0, 0);
            AnimValues[(int)EmitterAnim.Alpha1] = new Vector3(Def.Color1.W, 0, 0);
            AnimValues[(int)EmitterAnim.AllDirectionVelocity] = new Vector3(
                Def.AllDirectionVelocity,
                0,
                0
            );
            AnimValues[(int)EmitterAnim.DirectionalVelocity] = new Vector3(
                Def.DirectionalScale,
                0,
                0
            );
            AnimValues[(int)EmitterAnim.ParticleScale] = Def.ParticleScale;
            AnimValues[(int)EmitterAnim.VolumeScale] = Def.FormScale;
            AnimValues[(int)EmitterAnim.GravityScale] = new Vector3(Def.GravityScale, 0, 0);
        }

        /// <summary>The resource transform with its random rotation and translation.</summary>
        void CreateResMatrix()
        {
            float R(ref VfxRandom r) => r.GetFloat() * 2f - 1f;
            var rot = new Vector3(
                Def.Rotate.X + R(ref Random) * Def.RotateRandom.X,
                Def.Rotate.Y + R(ref Random) * Def.RotateRandom.Y,
                Def.Rotate.Z + R(ref Random) * Def.RotateRandom.Z
            );
            var trans = new Vector3(
                Def.Translate.X + R(ref Random) * Def.TranslateRandom.X,
                Def.Translate.Y + R(ref Random) * Def.TranslateRandom.Y,
                Def.Translate.Z + R(ref Random) * Def.TranslateRandom.Z
            );
            ResSrt = Matrix34.CreateSrtXyz(Def.Scale, rot, trans);
            ResRt = Matrix34.CreateSrtXyz(Vector3.One, rot, trans);
        }

        /// <summary>The next emission interval, and the resource matrix again where asked.</summary>
        void UpdateByEmit(ref float interval)
        {
            int extra = Random.GetInteger(Def.IntervalRandom);
            interval = (Def.Interval + 1f) + extra;
            interval *= IntervalRatio;
            interval *= Set.EmissionIntervalScale;
            if (Def.UpdateMatrixByEmit)
                CreateResMatrix();
        }

        /// <summary>The per emitter step of the set calculation. Returns true to kill.</summary>
        internal bool CalculateInSet(float frameRate, bool fading)
        {
            if (frameRate <= 0)
            {
                UpdateMatrix();
                WriteDynamicBlock(0, 0);
                return false;
            }
            bool alive = CalculateEmitter(frameRate, fading, true);
            _lastKill = !alive;
            return !alive;
        }

        bool CalculateEmitter(float frameRate, bool fading, bool emitting)
        {
            float timeAtStart = Time;
            float windowStart = Def.StartFrame;
            float windowEnd = Def.StartFrame + Def.EmissionFrames;
            _frameAccum = frameRate;
            FrameRate = frameRate;

            if (IsChild && Def.EmitterPerParticle)
            {
                windowEnd = AnchorLife;
                windowStart = windowEnd * Def.StartLifeShare;
                if (Def.OneTime)
                    windowEnd = windowStart + Def.EmissionFrames;
            }

            bool animated = Def.HasAnims && (!IsChild || Def.EmitterPerParticle);
            if (animated)
            {
                for (int i = 0; i < 14; i++)
                {
                    var a = Def.Anims[i];
                    if (a != null && a.Enabled && (!_animEnded[i] || a.Loop))
                        AnimValues[i] = a.Evaluate(Time, ref _animEnded[i], AnimValues[i]);
                }
                var allDirAnim = Def.Anims[(int)EmitterAnim.AllDirectionVelocity];
                if (
                    allDirAnim != null
                    && allDirAnim.Enabled
                    && (!_animEnded[(int)EmitterAnim.AllDirectionVelocity] || allDirAnim.Loop)
                )
                    AnimValues[(int)EmitterAnim.AllDirectionVelocity].X *=
                        Set.AllDirectionVelocityScale;
                var vol = Def.Anims[(int)EmitterAnim.VolumeScale];
                if (
                    vol != null
                    && vol.Enabled
                    && (!_animEnded[(int)EmitterAnim.VolumeScale] || vol.Loop)
                )
                    AnimValues[(int)EmitterAnim.VolumeScale] *= Set.VolumeScale;
                else if (!_animEnded[(int)EmitterAnim.VolumeScale] || vol.Loop)
                    AnimValues[(int)EmitterAnim.VolumeScale] = Def.FormScale * Set.VolumeScale;
            }
            else
            {
                AnimValues[(int)EmitterAnim.AllDirectionVelocity].X =
                    Def.AllDirectionVelocity * Set.AllDirectionVelocityScale;
                AnimValues[(int)EmitterAnim.VolumeScale] = Def.FormScale * Set.VolumeScale;
            }

            UpdateMatrix();
            if (Time == 0)
                _prevPos = Srt.T;
            if (Time != 0)
            {
                var inv = Srt.Inverse();
                LocalVec = inv.Transform(Srt.T) - inv.Transform(_prevPos);
            }

            if ((Def.FadeInAlphaCurve != 0 || Def.FadeInScale) && FadeIn < 1)
            {
                if (Def.FadeInTime < 1 || (FadeIn += frameRate / Def.FadeInTime) > 1)
                    FadeIn = 1;
            }

            if (fading)
            {
                emitting &= !Def.StopEmitOnFade;
                if (Def.FadeOutAlphaCurve != 0 || Def.FadeOutScale)
                {
                    if (
                        Def.FadeOutTime <= 0
                        || (FadeOut -= frameRate / Def.FadeOutTime) <= 1.1920929e-07f
                    )
                    {
                        FadeOut = 0;
                        return false;
                    }
                }
            }

            // A loop ring overwrites, so its oldest index is found afresh from the ring order.
            if (_loopRing)
                Particles.Oldest = Particles.Count == Particles.Capacity ? Particles.Fill : 0;
            Particles.AdvanceOldest(Time);

            if (
                emitting
                && (!IsChild || Def.EmitterPerParticle)
                && windowStart <= Time
                && ((!Def.OneTime && !IsChild) || windowEnd > Time || !_hasEmitted)
            )
                TryEmit(
                    ref _emitCounter,
                    ref _emitCarry,
                    ref _emitInterval,
                    ref _hasEmitted,
                    frameRate,
                    windowStart
                );

            if (Def.Children.Length > 0)
                Particles.AdvanceChildTime(frameRate);

            if (Def.CalcType == EmitterCalcType.Cpu || CpuFallback)
                CalculateParticles();
            else
                Particles.LiveCount = Particles.Count;
            Stripes?.PostCalculate();

            if (Particles.Count > 0 || Def.PluginType != 0)
                WriteDynamicBlock(frameRate, _frameAccum);

            _prevPos = Srt.T;
            Time += frameRate;

            float life = Def.MaxLife;
            if (IsChild && !Def.EmitterPerParticle)
                return !ParentKilled || timeAtStart <= windowStart + _lastEmitTime + life;
            if (!Def.OneTime)
                return (!fading && !IsChild) || timeAtStart <= windowStart + _lastEmitTime + life;
            if (Def.InfiniteLife)
                return true;
            float end = windowEnd + life;
            if (Def.FadeOutAlphaCurve != 0 || Def.FadeOutScale)
                end += Def.FadeOutTime;
            if (Def.Stripe != null)
                end += (int)(Def.Stripe.History * MathF.Max(frameRate, 1f));
            return timeAtStart <= end;
        }

        /// <summary>
        /// The emitter's own transform (the animated or resource matrix) times its parent's: the
        /// set matrix, or for a child the parent emitter's with the parent particle's position.
        /// </summary>
        void UpdateMatrix()
        {
            if (Def.HasSrtAnims)
            {
                var rot = AnimValues[(int)EmitterAnim.Rotate];
                if (Def.Anims[(int)EmitterAnim.Rotate] != null)
                    rot *= VfxMath.Pi / 180f;
                AnimSrt = Matrix34.CreateSrtXyz(
                    AnimValues[(int)EmitterAnim.Scale],
                    rot,
                    AnimValues[(int)EmitterAnim.Translate]
                );
                AnimRt = Matrix34.CreateSrtXyz(
                    Vector3.One,
                    rot,
                    AnimValues[(int)EmitterAnim.Translate]
                );
            }
            else
            {
                AnimSrt = ResSrt;
                AnimRt = ResRt;
            }
            if (IsChild)
            {
                ParentSrt = ParentRt = Matrix34.Identity;
                var offset = Def.EmitterPerParticle ? AnchorPos : Vector3.Zero;
                ParentSrt.T = ParentRt.T = offset;
                if (Parent.Def.FollowType != 1)
                {
                    ParentSrt = Parent.Srt;
                    ParentSrt.T = Parent.Srt.T + Parent.Srt.TransformNormal(offset);
                    ParentRt = Parent.Rt;
                    ParentRt.T = Parent.Rt.T + Parent.Rt.TransformNormal(offset);
                }
            }
            else
            {
                ParentSrt = Set.WorldSrt;
                ParentRt = Set.WorldRt;
            }
            Srt = Matrix34.Multiply(AnimSrt, ParentSrt);
            Rt = Matrix34.Multiply(AnimRt, ParentRt);
        }

        /// <summary>The fade in alpha factor after its curve.</summary>
        public float FadeInAlpha => Curve(FadeIn, Def.FadeInAlphaCurve);

        /// <summary>The fade out alpha factor after its curve.</summary>
        public float FadeOutAlpha => Curve(FadeOut, Def.FadeOutAlphaCurve);

        static float Curve(float v, byte type)
        {
            if (v >= 1 || v < 0 || type == 0)
                return 1;
            return type switch
            {
                1 => v,
                2 => v * v,
                3 => (v * v) * (v * v),
                _ => 1,
            };
        }

        /// <summary>The alpha the fades leave, both curves applied.</summary>
        public float FadeAlpha => FadeInAlpha * FadeOutAlpha;

        /// <summary>The scale the fades leave.</summary>
        public float FadeScale
        {
            get
            {
                float a = 1,
                    b = 1;
                if (FadeIn < 1 && FadeIn >= 0 && Def.FadeInScale)
                    a = Def.FadeInScaleMin + FadeIn * (1 - Def.FadeInScaleMin);
                if (FadeOut < 1 && FadeOut >= 0 && Def.FadeOutScale)
                    b = Def.FadeOutScaleMin + FadeOut * (1 - Def.FadeOutScaleMin);
                return a * b;
            }
        }

        internal void DetachFromParent() { }

        public override string ToString() => $"{Def.Source.Name} t={Time} n={Particles.Count}";
    }
}
