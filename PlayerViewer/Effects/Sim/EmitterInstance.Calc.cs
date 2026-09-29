using System;
using System.Buffers.Binary;
using System.Numerics;

namespace PlayerViewer.Effects.Sim
{
    public sealed partial class EmitterInstance
    {
        /// <summary>
        /// CalculateParticle for a CPU emitter: kills expired particles, integrates the rest,
        /// runs the fields, keeps the position delta the shaders read, and lets light children
        /// emit from each particle.
        /// </summary>
        void CalculateParticles()
        {
            var ps = Particles;
            int live = 0;
            for (int slot = 0; slot < ps.Count; slot++)
            {
                if (ps.CreateId[slot] == 0)
                    continue;
                float ptclTime = Time - ps.CreateTime[slot];
                if (ptclTime >= ps.Life[slot])
                {
                    Stripes?.Remove(slot, ptclTime, ps.Life[slot]);
                    ps.CreateId[slot] = 0;
                    continue;
                }
                var oldPos = ps.Get3(ParticleStream.LocalPos, slot);
                var oldVec = ps.Get3(ParticleStream.LocalVec, slot);
                var oldDiff = ps.Get3(ParticleStream.LocalDiff, slot);
                Behaviour(slot, ptclTime, oldPos, oldVec, out var pos, out var vec);
                ps.Set3(ParticleStream.LocalPos, slot, pos);
                ps.Set3(ParticleStream.LocalVec, slot, vec);
                Stripes?.Calculate(slot, ptclTime, ps.Life[slot]);
                ps.SetW(ParticleStream.LocalPos, slot, ps.Life[slot]);
                ps.SetW(ParticleStream.LocalVec, slot, ps.CreateTime[slot]);
                ps.Set3(
                    ParticleStream.LocalDiff,
                    slot,
                    PositionDelta(slot, ptclTime, pos, oldPos, vec, oldDiff)
                );
                if (Def.Children.Length > 0)
                    EmitLightChildren(slot);
                live++;
                UpdateParticleChildren(slot);
            }
            ps.LiveCount = live;
            if (live == 0)
            {
                ps.Count = 0;
                ps.Fill = 0;
            }
        }

        /// <summary>Integration, air resistance, gravity, then the fields in the runtime's order.</summary>
        void Behaviour(
            int slot,
            float ptclTime,
            Vector3 pos,
            Vector3 vec,
            out Vector3 newPos,
            out Vector3 newVec
        )
        {
            var ps = Particles;
            float fr = FrameRate;
            float dyn = ps.Streams[(int)ParticleStream.Scale][slot * 4 + 3];
            newPos = pos + vec * (fr * dyn);
            float air = Def.AirRegist;
            newVec = air == 1f ? vec : vec * MathF.Pow(air, fr);
            float gs = AnimValues[(int)EmitterAnim.GravityScale].X;
            if (gs > 0)
            {
                var g = new Vector3(
                    gs * Def.GravityAxis.X,
                    gs * Def.GravityAxis.Y,
                    gs * Def.GravityAxis.Z
                );
                if (!Def.WorldGravity)
                    newVec = new Vector3(
                        newVec.X + fr * g.X,
                        newVec.Y + fr * g.Y,
                        newVec.Z + fr * g.Z
                    );
                else
                {
                    Matrix34 m = Def.FollowType != 1 ? Rt : EmitterMatrixAt(slot).WithoutScale();
                    var local = new Vector3(
                        VfxMath.Dot(m.R0, g),
                        VfxMath.Dot(m.R1, g),
                        VfxMath.Dot(m.R2, g)
                    );
                    newVec += local * fr;
                }
            }
            if (Def.Fields != null)
                Def.Fields.Apply(this, slot, ptclTime, ref newPos, ref newVec);
        }

        /// <summary>The emitter transform a particle was born with, or the current one when it follows.</summary>
        internal Matrix34 EmitterMatrixAt(int slot)
        {
            var ps = Particles;
            if (ps[ParticleStream.EmtMat0] == null)
                return Srt;
            var m0 = ps.Get(ParticleStream.EmtMat0, slot);
            var m1 = ps.Get(ParticleStream.EmtMat1, slot);
            var m2 = ps.Get(ParticleStream.EmtMat2, slot);
            return new Matrix34
            {
                R0 = new Vector3(m0.X, m1.X, m2.X),
                R1 = new Vector3(m0.Y, m1.Y, m2.Y),
                R2 = new Vector3(m0.Z, m1.Z, m2.Z),
                T = new Vector3(m0.W, m1.W, m2.W),
            };
        }

        /// <summary>
        /// The transform from a particle's local space to the world: the emitter's own when it
        /// follows fully, else the birth rotation with the birth translation (follow type 1) or
        /// the emitter's current one (follow type 2).
        /// </summary>
        internal Matrix34 WorldMatrixAt(int slot)
        {
            var m = Srt;
            if (Def.FollowType == 0)
                return m;
            var birth = EmitterMatrixAt(slot);
            m.R0 = birth.R0;
            m.R1 = birth.R1;
            m.R2 = birth.R2;
            if (Def.FollowType == 1)
                m.T = birth.T;
            return m;
        }

        /// <summary>
        /// The position delta velocity look billboards read. A particle that barely moved keeps
        /// a tiny delta along its velocity, position or gravity so the look stays defined.
        /// </summary>
        Vector3 PositionDelta(
            int slot,
            float ptclTime,
            Vector3 pos,
            Vector3 oldPos,
            Vector3 vec,
            Vector3 oldDiff
        )
        {
            var d = pos - oldPos;
            float dl = VfxMath.Length(d);
            float vl = VfxMath.Length(vec);
            if (dl >= 1e-6f)
            {
                if (FrameRate * vl < 0.001f && dl < 0.001f && FrameRate * vl > 0)
                    d = vec * FrameRate;
                return d;
            }
            if (ptclTime != 0)
                return oldDiff;
            if (Def.StaticGravity.W <= 0)
            {
                if (vl >= 1e-6f)
                    return VfxMath.Normalize(vec) * 1e-6f;
                if (VfxMath.Length(pos) >= 1e-6f)
                    return VfxMath.Normalize(pos) * 1e-6f;
                return new Vector3(0, 1e-6f, 0);
            }
            var g = new Vector3(Def.StaticGravity.X, Def.StaticGravity.Y, Def.StaticGravity.Z);
            if (Def.WorldGravity)
                g = (Def.FollowType != 0 ? EmitterMatrixAt(slot) : Srt).TransformNormal(g);
            return g * 1e-6f;
        }

        /// <summary>
        /// Lets each light child emit from this particle: the child's emission state for the
        /// particle lives with the particle, and it emits at the particle's position.
        /// </summary>
        void EmitLightChildren(int slot)
        {
            var ps = Particles;
            for (int i = 0; i < Def.Children.Length; i++)
            {
                var child = ps.Children[slot, i];
                if (child == null)
                    break;
                var cd = child.Def;
                if (cd.EmitterPerParticle || ps.CreateId[slot] == 0)
                    continue;
                float childClock = ps.ChildClock[slot];
                if (child.Time == 0)
                {
                    child.UpdateMatrix();
                }
                float life = ps.Life[slot];
                float windowStart = life * cd.StartLifeShare;
                float windowEnd = life;
                if (cd.OneTime)
                    windowEnd = windowStart + cd.EmissionFrames;
                if (windowStart > childClock)
                    continue;
                if (!(windowEnd > childClock || !ps.ChildHasEmitted[slot, i]))
                    continue;
                var pos = ps.Get3(ParticleStream.LocalPos, slot);
                var vec = ps.Get3(ParticleStream.LocalVec, slot);
                if (Def.FollowType == 1)
                {
                    var m = EmitterMatrixAt(slot);
                    pos = m.Transform(pos);
                    vec = m.TransformNormal(vec);
                }
                child._emitParentSlot = slot;
                child._emitParentPos = pos;
                child._emitParentVec = vec;
                if (cd.DistanceEmission && childClock != 0)
                    child.LocalVec = ps.Get3(ParticleStream.LocalDiff, slot);
                var savedSrt = child.Srt;
                var savedRt = child.Rt;
                if (cd.FollowType == 1 && Def.FollowType != 1)
                {
                    child.Srt = Matrix34.Multiply(child.ResSrt, Srt);
                    child.Rt = Matrix34.Multiply(child.ResRt, Rt);
                }
                float counter = ps.ChildCounter[slot, i];
                float carry = ps.ChildCarry[slot, i];
                float interval = ps.ChildInterval[slot, i];
                bool hasEmitted = ps.ChildHasEmitted[slot, i];
                child.TryEmit(
                    ref counter,
                    ref carry,
                    ref interval,
                    ref hasEmitted,
                    FrameRate,
                    windowStart
                );
                ps.ChildCounter[slot, i] = counter;
                ps.ChildCarry[slot, i] = carry;
                ps.ChildInterval[slot, i] = interval;
                ps.ChildHasEmitted[slot, i] = hasEmitted;
                child._emitParentSlot = -1;
            }
        }

        /// <summary>Keeps an emitter per particle child on its parent particle.</summary>
        void UpdateParticleChildren(int slot)
        {
            var ps = Particles;
            if (ps.Children == null)
                return;
            for (int i = 0; i < Def.Children.Length; i++)
            {
                var child = ps.Children[slot, i];
                if (
                    child == null
                    || !Def.Children[i].EmitterPerParticle
                    || child.Parent != this
                    || child.ParentSlot != slot
                )
                    continue;
                child.AnchorAge = Time - ps.CreateTime[slot];
                child.AnchorLife = ps.Life[slot];
                var pos = ps.Get3(ParticleStream.LocalPos, slot);
                if (Def.FollowType == 1)
                    pos = EmitterMatrixAt(slot).Transform(pos);
                child.AnchorPos = pos;
                child.AnchorVelocity = Rt.TransformNormal(ps.Get3(ParticleStream.LocalVec, slot));
            }
        }

        /// <summary>
        /// InheritParentParticleInfo: a light child particle starts at its parent
        /// particle; by the flags at 0xB20 on, a child takes a share of the parent particle's
        /// velocity, its scale times a rate, its rotation and its colours at the parent's age.
        /// </summary>
        void InheritFromParent(int slot)
        {
            var ps = Particles;
            var s = Def.Source.Section;
            var pd = Parent.Def;
            Vector3 sourceVec;
            Vector4 sourceScale,
                sourceRotate,
                sourceRandom;
            float sourceLife,
                sourceAge;
            if (Def.EmitterPerParticle)
            {
                sourceVec = AnchorVelocity;
                sourceScale = InheritScale;
                sourceRotate = InheritRotate;
                sourceRandom = InheritRandom;
                sourceLife = AnchorLife;
                sourceAge = AnchorAge;
            }
            else
            {
                if (_emitParentSlot < 0)
                    return;
                var pps = Parent.Particles;
                int p = _emitParentSlot;
                sourceScale = pps.Get(ParticleStream.Scale, p);
                sourceRotate = pps.Get(ParticleStream.InitRotate, p);
                sourceRandom = pps.Get(ParticleStream.Random, p);
                sourceLife = pps.Life[p];
                sourceAge = (Parent.Time - pps.CreateTime[p]) - Parent.FrameRate;
                ps.Set3(
                    ParticleStream.LocalPos,
                    slot,
                    _emitParentPos + ps.Get3(ParticleStream.LocalPos, slot)
                );
                sourceVec = _emitParentVec;
            }
            if (s.GetU8(0xB20) != 0)
                ps.Set3(
                    ParticleStream.LocalVec,
                    slot,
                    sourceVec * s.GetF32(0xB30) + ps.Get3(ParticleStream.LocalVec, slot)
                );
            if (s.GetU8(0xB21) != 0)
            {
                var scale = ParticleAnim.Scale(
                    pd,
                    new Vector3(sourceScale.X, sourceScale.Y, sourceScale.Z),
                    sourceRandom,
                    sourceLife,
                    sourceAge
                );
                ps.Set3(ParticleStream.Scale, slot, scale * s.GetF32(0xB34));
            }
            if (s.GetU8(0xB22) != 0)
                ps.Set3(
                    ParticleStream.InitRotate,
                    slot,
                    ParticleAnim.Rotation(pd, sourceRotate, sourceRandom, sourceAge)
                );
            // Both colours inherit through the parent emitter's colour 0 and alpha 0 animation.
            var animColor = Parent.AnimValues[(int)EmitterAnim.Color0];
            float animAlpha = Parent.AnimValues[(int)EmitterAnim.Alpha0].X;
            for (int which = 0; which < 2; which++)
            {
                var stream = which == 0 ? ParticleStream.Color0 : ParticleStream.Color1;
                bool rgb = s.GetU8(0xB24 + which) != 0;
                bool alpha = s.GetU8(0xB26 + which) != 0;
                if (ps[stream] == null || !(rgb || alpha))
                    continue;
                var c = ParticleAnim.EmitterColor(
                    pd,
                    which,
                    sourceRandom,
                    which == 0 ? Parent.ColorScale0 : Parent.ColorScale1,
                    animColor,
                    animAlpha,
                    sourceLife,
                    sourceAge
                );
                if (rgb)
                    ps.Set3(stream, slot, new Vector3(c.X, c.Y, c.Z));
                if (alpha)
                    ps.SetW(stream, slot, c.W);
            }
        }

        /// <summary>
        /// MakeDynamicConstantBuffer: colours, time and count, fades, the emitter matrices, and
        /// the animation and parent transforms, 0x160 bytes.
        /// </summary>
        void WriteDynamicBlock(float frameRate, float frameAccum)
        {
            var b = DynamicBlock.AsSpan();
            var set = Set;
            var c0 = AnimValues[(int)EmitterAnim.Color0];
            var c1 = AnimValues[(int)EmitterAnim.Color1];
            float a0 = AnimValues[(int)EmitterAnim.Alpha0].X * ColorScale0.W;
            float a1 = AnimValues[(int)EmitterAnim.Alpha1].X * ColorScale1.W;
            if (IsChild && Def.InheritAlpha0)
                a0 *= Parent.AnimValues[(int)EmitterAnim.Alpha0].X;
            if (IsChild && Def.InheritAlpha1)
                a1 *= Parent.AnimValues[(int)EmitterAnim.Alpha1].X;
            W4(
                b,
                0x00,
                new Vector4(
                    (c0.X * ColorScale0.X) * set.Color.X,
                    (c0.Y * ColorScale0.Y) * set.Color.Y,
                    (c0.Z * ColorScale0.Z) * set.Color.Z,
                    a0
                )
            );
            W4(
                b,
                0x10,
                new Vector4(
                    (c1.X * ColorScale1.X) * set.Color.X,
                    (c1.Y * ColorScale1.Y) * set.Color.Y,
                    (c1.Z * ColorScale1.Z) * set.Color.Z,
                    a1
                )
            );
            float fade = set.Color.W * FadeAlpha;
            if (IsChild && (Def.InheritAlpha0 || Def.InheritAlpha1))
                fade *= Parent.FadeAlpha;
            float time = Time;
            float w = frameRate;
            if (frameRate == 0 && Time > 0)
            {
                time = Time - FrameRate;
                w = FrameRate;
            }
            W4(b, 0x20, new Vector4(time, Particles.Count, frameAccum, w));
            float fs = FadeScale;
            W4(
                b,
                0x30,
                new Vector4(
                    fade,
                    fs * set.ParticleScaleForCalc.X,
                    fs * set.ParticleScaleForCalc.Y,
                    fs * set.ParticleScaleForCalc.Z
                )
            );
            Transposed(b, 0x40, Srt);
            Transposed(b, 0x80, Rt);
            W4(b, 0xC0, Vector4.Zero);
            BinaryPrimitives.WriteSingleLittleEndian(b[0xD0..], 0);
            BinaryPrimitives.WriteSingleLittleEndian(b[0xD4..], FadeInAlpha);
            BinaryPrimitives.WriteSingleLittleEndian(b[0xD8..], FadeOutAlpha);
            Rows(b, 0xE0, AnimSrt);
            Rows(b, 0x120, ParentSrt);
        }

        static void W4(Span<byte> b, int at, Vector4 v)
        {
            BinaryPrimitives.WriteSingleLittleEndian(b[at..], v.X);
            BinaryPrimitives.WriteSingleLittleEndian(b[(at + 4)..], v.Y);
            BinaryPrimitives.WriteSingleLittleEndian(b[(at + 8)..], v.Z);
            BinaryPrimitives.WriteSingleLittleEndian(b[(at + 12)..], v.W);
        }

        static void Transposed(Span<byte> b, int at, in Matrix34 m)
        {
            W4(b, at, new Vector4(m.R0.X, m.R1.X, m.R2.X, m.T.X));
            W4(b, at + 16, new Vector4(m.R0.Y, m.R1.Y, m.R2.Y, m.T.Y));
            W4(b, at + 32, new Vector4(m.R0.Z, m.R1.Z, m.R2.Z, m.T.Z));
            W4(b, at + 48, new Vector4(0, 0, 0, 1));
        }

        static void Rows(Span<byte> b, int at, in Matrix34 m)
        {
            W4(b, at, new Vector4(m.R0, 0));
            W4(b, at + 16, new Vector4(m.R1, 0));
            W4(b, at + 32, new Vector4(m.R2, 0));
            W4(b, at + 48, new Vector4(m.T, 1));
        }
    }
}
