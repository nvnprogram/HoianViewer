using System;
using System.Numerics;

namespace PlayerViewer.Effects.Sim
{
    public sealed partial class EmitterInstance
    {
        /// <summary>
        /// The runtime's time or distance emission for one frame. The counter, the carried
        /// fraction of a particle, the interval and the emitted flag are the emitter's own, or
        /// for a light child the parent particle's.
        /// </summary>
        void TryEmit(
            ref float counter,
            ref float carry,
            ref float interval,
            ref bool hasEmitted,
            float frameRate,
            float windowStart
        )
        {
            if (Def.DistanceEmission)
            {
                TryEmitByDistance(ref carry, ref hasEmitted, frameRate);
                return;
            }
            float end = Def.EmissionFrames + windowStart;
            float time = Time;
            float c = counter;
            float newInterval = interval;
            if (c < interval)
            {
                counter = c + ClampStep(frameRate, end, time);
                return;
            }
            float rem = AnimTrack.FMod(c, interval);
            bool divided = Def.UsesResourceRate;
            float rate =
                (divided ? Def.Rate : AnimValues[(int)EmitterAnim.EmissionRate].X)
                * RateScale
                * Set.EmissionRatioScale;
            int n = (int)(c / interval);
            float add = 0;
            if (n >= 1)
            {
                float rr = divided ? 0 : Def.RateRandom;
                SeedEmitterLevel(0);
                for (; n > 0; n--)
                {
                    float r = Random.GetFloat();
                    add += ((-rr * r + 100f) / 100f) * rate;
                }
            }
            if (!hasEmitted)
            {
                if (interval >= frameRate)
                    add = MathF.Max(add, 1f);
                else
                {
                    float a = time + frameRate;
                    if (Def.OneTime)
                        a = MathF.Min(end, time + frameRate);
                    float k = (a - windowStart) / interval;
                    float whole = MathF.Floor(k);
                    add = rate * whole;
                    rem += k - whole;
                }
            }
            carry += add;
            int count = (int)MathF.Floor(carry);
            if (count >= 1)
            {
                Emit(ref hasEmitted, count);
                SeedEmitterLevel(1);
                UpdateByEmit(ref newInterval);
                carry -= count;
                _lastEmitTime = Time;
            }
            counter = rem + ClampStep(frameRate, end, time);
            interval = newInterval;
        }

        float ClampStep(float frameRate, float end, float time)
        {
            if (!Def.OneTime)
                return frameRate;
            float left = MathF.Max(0, end - (time + frameRate));
            return left <= frameRate ? left : frameRate;
        }

        void TryEmitByDistance(ref float travelled, ref bool hasEmitted, float frameRate)
        {
            float length = VfxMath.Length(LocalVec);
            float min = Def.DistMin * frameRate;
            if (length < Def.DistMargin * frameRate)
                length = 0;
            if (length < min)
                length = min;
            else if (Def.DistMax * frameRate < length)
                length = Def.DistMax * frameRate;
            float v = travelled + length;
            if (Def.DistUnit != 0)
            {
                int count = (int)(v / Def.DistUnit);
                if (count >= 1)
                {
                    var cur = Rt.T;
                    var delta = _prevPos - cur;
                    var savedSrt = Srt;
                    var savedRt = Rt;
                    for (; count > 0; count--)
                    {
                        v -= Def.DistUnit;
                        var p = length == 0 ? cur : cur + delta * (v / length);
                        Rt.T = p;
                        Srt.T = p;
                        Emit(ref hasEmitted, 1);
                        Srt = savedSrt;
                        Rt = savedRt;
                    }
                    _lastEmitTime = Time;
                }
            }
            travelled = v;
        }

        /// <summary>
        /// Fills up to <paramref name="count"/> slots in ring order, stopping at a live one. A
        /// loop sized ring overwrites instead, so each period's births land in the same slots.
        /// </summary>
        void Emit(ref bool hasEmitted, int count)
        {
            var ps = Particles;
            SeedEmitterLevel(2);
            float random = Random.GetFloat();
            if (Def.IsUnisonDivided)
            {
                int div = Def.VolumeType == 13 ? Def.DivideLine : Def.DivideCircle;
                int divRandom =
                    Def.VolumeType == 13 ? Def.DivideLineRandom : Def.DivideCircleRandom;
                count = (div - (int)(random * divRandom * 0.01f * div)) * count;
            }
            if (ps.Capacity == 0)
                return;
            for (int i = 0; i < count; i++)
            {
                int slot = ps.Fill;
                if (!_loopRing && Time - ps.CreateTime[slot] < ps.Life[slot])
                    break;
                hasEmitted = true;
                if (!InitParticle(slot, i, count, random))
                    continue;
                Stripes?.Emit(slot);
                ps.Set3(ParticleStream.LocalDiff, slot, ps.Get3(ParticleStream.LocalVec, slot));
                if (Parent != null)
                    InheritFromParent(slot);
                if ((int)ps.Life[slot] < 1)
                    ps.Life[slot] = 1;
                ps.Fill = slot + 1 < ps.Capacity ? slot + 1 : 0;
                if (ps.Count < ps.Capacity)
                    ps.Count++;
                else if (_loopRing && slot == ps.Oldest)
                    ps.Oldest = ps.Fill;
            }
        }

        /// <summary>InitializeParticle, in the runtime's order of random draws.</summary>
        bool InitParticle(int slot, int index, int count, float random)
        {
            var ps = Particles;
            var set = Set;
            SeedParticle(index);
            if (!EmitShape(index, count, random, out var pos, out var vec))
                return false;

            SetLoopKey(slot, index);
            ps.SetW(ParticleStream.InitRotate, slot, _createCounter % ps.Capacity);
            _createCounter++;
            ps.CreateId[slot] = _createCounter;

            if (Def.XzDiffusion != 0)
            {
                var xz = new Vector3(pos.X, 0, pos.Z);
                if (VfxMath.Dot(xz, xz) <= 1.1920929e-07f)
                {
                    float a = Random.GetFloat() * 2f - 1f;
                    float b = Random.GetFloat() * 2f - 1f;
                    xz = new Vector3(a, 0, b);
                }
                vec += VfxMath.Normalize(xz) * Def.XzDiffusion;
            }

            uint velDraw = Random.Next();
            float velocityRandomScale = set.RandomVelocityScale;
            float axisSpeedScale = set.DirectionalVelocityScale;
            float angle = Def.DiffusionAngle;
            if (Def.PositionRandom != 0)
                pos += Random.GetTableNormal() * Def.PositionRandom;
            float axisSpeedAnim = AnimValues[(int)EmitterAnim.DirectionalVelocity].X;

            var emt = Srt;
            if (ps[ParticleStream.EmtMat0] != null)
            {
                ps.Set(
                    ParticleStream.EmtMat0,
                    slot,
                    new Vector4(emt.R0.X, emt.R1.X, emt.R2.X, emt.T.X)
                );
                ps.Set(
                    ParticleStream.EmtMat1,
                    slot,
                    new Vector4(emt.R0.Y, emt.R1.Y, emt.R2.Y, emt.T.Y)
                );
                ps.Set(
                    ParticleStream.EmtMat2,
                    slot,
                    new Vector4(emt.R0.Z, emt.R1.Z, emt.R2.Z, emt.T.Z)
                );
            }
            ps.Set3(ParticleStream.LocalPos, slot, pos);

            var dir = Def.Direction;
            if (Def.WorldOrientedVelocity)
            {
                float len = VfxMath.Length(dir);
                var local = emt.Inverse().TransformNormal(dir);
                dir = VfxMath.Normalize(local) * len;
            }
            float velRandom = velDraw * (-1f / 4294967296f) * (Def.VelocityRandom / 100f);
            float velScale = velRandom * velocityRandomScale + 1f;
            float axisSpeed = axisSpeedAnim * axisSpeedScale;
            if (angle == 0)
                vec = VfxMath.Fma(dir, axisSpeed, vec) * velScale;
            else
            {
                float rot = VfxMath.Pi * (Random.GetFloat() * 2f);
                VfxMath.FastSinCos(rot, out float s, out float c);
                float a = angle / -90f + 1f;
                float y = a + Random.GetFloat() * (1f - a);
                float r2 = 1f - y * y;
                float r = r2 > 0 ? MathF.Sqrt(r2) : 0;
                var cone = VfxMath.RotateFromUp(new Vector3(c * r, y, s * r), dir);
                vec = (vec + cone * axisSpeed) * velScale;
            }

            var d = Random.GetTableVector();
            vec = new Vector3(
                MathF.FusedMultiplyAdd(d.X, Def.Diffusion.X, vec.X),
                MathF.FusedMultiplyAdd(d.Y, Def.Diffusion.Y, vec.Y),
                MathF.FusedMultiplyAdd(d.Z, Def.Diffusion.Z, vec.Z)
            );

            if (FrameRate > 0)
            {
                var inherit = LocalVec * (1f / FrameRate) * Def.EmitterVelocityInherit;
                float len = VfxMath.Length(inherit);
                if (len > Def.EmitterVelocityInheritMax)
                    inherit = VfxMath.Normalize(inherit) * Def.EmitterVelocityInheritMax;
                vec += inherit;
            }
            if (set.AdditionalVelocity != Vector3.Zero)
                vec += Rt.Inverse().TransformNormal(set.AdditionalVelocity);
            ps.Set3(ParticleStream.LocalVec, slot, vec);

            var baseScale = AnimValues[(int)EmitterAnim.ParticleScale];
            var sr = Def.ParticleScaleRandom;
            Vector3 scale;
            if (sr.X != sr.Y || sr.Y != sr.Z)
            {
                float x = baseScale.X * MathF.FusedMultiplyAdd(sr.X / -100f, Random.GetFloat(), 1f);
                float yy =
                    baseScale.Y * MathF.FusedMultiplyAdd(sr.Y / -100f, Random.GetFloat(), 1f);
                float z = baseScale.Z * MathF.FusedMultiplyAdd(sr.Z / -100f, Random.GetFloat(), 1f);
                scale = set.BirthScale * new Vector3(x, yy, z);
            }
            else
            {
                float f = MathF.FusedMultiplyAdd(sr.X / -100f, Random.GetFloat(), 1f);
                scale =
                    new Vector3(baseScale.X * f, baseScale.Y * f, baseScale.Z * f) * set.BirthScale;
            }
            ps.Set3(ParticleStream.Scale, slot, scale);
            ps.Bounces[slot] = 0;

            float dyn = (Def.MomentumRandom + 1f) + (Random.GetFloat() * Def.MomentumRandom) * -2f;
            ps.SetW(ParticleStream.Scale, slot, dyn);

            ps.CreateTime[slot] = Time;
            ps.SetW(ParticleStream.LocalVec, slot, Time);

            float life;
            if (!Def.InfiniteLife)
            {
                float lifeAnim = AnimValues[(int)EmitterAnim.ParticleLife].X;
                int lr = Random.GetInteger(Def.LifeRandom);
                life = set.ParticleLifeScale * (LifeScale * (lifeAnim + lifeAnim * (lr * -0.01f)));
            }
            else
                life = 268435456f;
            ps.Life[slot] = life;
            ps.SetW(ParticleStream.LocalPos, slot, life);
            if (ps.ChildClock != null)
            {
                ps.ChildClock[slot] = 0;
                for (int i = 0; i < Def.Children.Length; i++)
                {
                    ps.ChildCounter[slot, i] = 1;
                    ps.ChildCarry[slot, i] = 0;
                    ps.ChildInterval[slot, i] = 1;
                    ps.ChildHasEmitted[slot, i] = false;
                }
            }

            ps.RandomDraw[slot] = Random.Draws;
            ps.Set(
                ParticleStream.Random,
                slot,
                new Vector4(
                    Random.GetFloat(),
                    Random.GetFloat(),
                    Random.GetFloat(),
                    Random.GetFloat()
                )
            );
            ps.Set3(ParticleStream.InitRotate, slot, Def.InitialRotation + set.InitRotate);
            if (ps[ParticleStream.Color0] != null)
            {
                ps.Set(ParticleStream.Color0, slot, Vector4.One);
                ps.Set(ParticleStream.Color1, slot, Vector4.One);
            }
            SpawnChildren(slot, life);
            return true;
        }

        /// <summary>Creates the emitter per particle children of a new particle.</summary>
        void SpawnChildren(int slot, float life)
        {
            var ps = Particles;
            for (int i = 0; i < Def.Children.Length; i++)
            {
                var cd = Def.Children[i];
                if (!cd.EmitterPerParticle)
                {
                    ps.Children[slot, i] = LightChildren[i];
                    continue;
                }
                var child = new EmitterInstance(Set, cd, this, slot) { AnchorLife = life };
                child.InheritPos = ps.Get(ParticleStream.LocalPos, slot);
                child.InheritVec = ps.Get(ParticleStream.LocalVec, slot);
                child.InheritScale = ps.Get(ParticleStream.Scale, slot);
                child.InheritRotate = ps.Get(ParticleStream.InitRotate, slot);
                child.InheritRandom = ps.Get(ParticleStream.Random, slot);
                child.AnchorAge = 0;
                ps.Children[slot, i] = child;
                ChildLists[i].Add(child);
            }
        }

        /// <summary>The shape functions: a local position and the all direction velocity along the shape normal.</summary>
        bool EmitShape(int index, int count, float random, out Vector3 pos, out Vector3 vec)
        {
            var d = Def;
            var vs = AnimValues[(int)EmitterAnim.VolumeScale];
            float normalSpeed = AnimValues[(int)EmitterAnim.AllDirectionVelocity].X;
            pos = vec = Vector3.Zero;
            switch (d.VolumeType)
            {
                case 0:
                    vec = Random.GetTableNormal() * normalSpeed;
                    return true;
                case 1:
                case 8:
                {
                    float start = d.SweepStartRandom
                        ? VfxMath.Pi * random + VfxMath.Pi * random
                        : d.SweepStart;
                    float rot = MathF.FusedMultiplyAdd(
                        d.SweepLongitude,
                        -0.5f,
                        MathF.FusedMultiplyAdd(Random.GetFloat(), d.SweepLongitude, start)
                    );
                    VfxMath.FastSinCos(rot, out float s, out float c);
                    pos = new Vector3(s * (d.Radius.X * vs.X), 0, c * (d.Radius.Z * vs.Z));
                    vec = new Vector3(s * normalSpeed, 0, c * normalSpeed);
                    if (d.VolumeType == 8)
                        pos.Y = d.Radius.Y * vs.Y * (Random.GetFloat() * 2f - 1f);
                    return true;
                }
                case 2:
                    return CircleDivided(index, random, vs, normalSpeed, out pos, out vec);
                case 3:
                case 9:
                {
                    uint r0 = Random.Next();
                    float start = d.SweepStartRandom
                        ? VfxMath.Pi * random + VfxMath.Pi * random
                        : d.SweepStart;
                    float rot = MathF.FusedMultiplyAdd(
                        d.SweepLongitude,
                        -0.5f,
                        MathF.FusedMultiplyAdd(r0 * (1f / 4294967296f), d.SweepLongitude, start)
                    );
                    VfxMath.FastSinCos(rot, out float s, out float c);
                    float r1 = Random.GetFloat();
                    float inner = 1f - d.Caliber;
                    float f = MathF.FusedMultiplyAdd(inner * inner, 1f - r1, r1);
                    float rr = f > 0 ? MathF.Sqrt(f) : 0;
                    float sx = s * rr;
                    float cz = c * rr;
                    float rx = d.Radius.X * vs.X;
                    float rz = d.Radius.Z * vs.Z;
                    pos = new Vector3(sx * rx, 0, cz * rz);
                    var dir = new Vector3(rr * sx, 0, rr * cz);
                    if (rx <= rz)
                        dir.X *= rx / rz;
                    else
                        dir.Z *= rz / rx;
                    vec =
                        VfxMath.Dot(dir, dir) > 0
                            ? VfxMath.Normalize(dir) * normalSpeed
                            : new Vector3(0, 0, normalSpeed);
                    if (d.VolumeType == 9)
                        pos.Y = d.Radius.Y * vs.Y * (Random.GetFloat() * 2f - 1f);
                    return true;
                }
                case 4:
                case 7:
                    return Sphere(d.VolumeType == 7, random, vs, normalSpeed, out pos, out vec);
                case 5:
                case 6:
                    return SphereDivided(index, vs, normalSpeed, out pos, out vec);
                case 10:
                    return Box(vs, normalSpeed, out pos, out vec);
                case 11:
                    return BoxFill(vs, normalSpeed, out pos, out vec);
                case 12:
                {
                    float len = d.LineLength * vs.Z;
                    pos = new Vector3(
                        0,
                        0,
                        (len + d.LineCenter * len) * -0.5f + Random.GetFloat() * len
                    );
                    vec = new Vector3(0, 0, normalSpeed);
                    return true;
                }
                case 13:
                {
                    int div = d.DivideLine;
                    int i = PickDivided(index, div);
                    if (d.PrimEmitType == 0)
                        div -= (int)(d.DivideLineRandom * random * 0.01f * div);
                    int span = div - 1;
                    float half = span == 0 ? 0.5f : 0f;
                    float step = span == 0 ? 0 : 1f / span;
                    float len = d.LineLength * vs.Z;
                    pos = new Vector3(
                        0,
                        0,
                        (len + d.LineCenter * len) * -0.5f + (half + step * i) * len
                    );
                    vec = new Vector3(0, 0, normalSpeed);
                    return true;
                }
                case 14:
                    return Rectangle(vs, normalSpeed, out pos, out vec);
                case 15:
                    return Primitive(index, vs, normalSpeed, out pos, out vec);
            }
            return true;
        }

        int PickDivided(int index, int count)
        {
            if (count <= 0)
                return 0;
            switch (Def.PrimEmitType)
            {
                case 2:
                    int i = _sequence % count;
                    _sequence = _sequence + 1 < count ? _sequence + 1 : 0;
                    return i;
                case 1:
                    return (int)(Random.GetFloat() * count);
                default:
                    return index % count;
            }
        }

        bool CircleDivided(
            int index,
            float random,
            Vector3 vs,
            float normalSpeed,
            out Vector3 pos,
            out Vector3 vec
        )
        {
            var d = Def;
            float start = d.SweepStartRandom
                ? VfxMath.Pi * random + VfxMath.Pi * random
                : d.SweepStart;
            int div = d.DivideCircle;
            int steps,
                i;
            bool open = d.SweepLongitude != VfxMath.Pi + VfxMath.Pi;
            if (d.PrimEmitType != 0)
            {
                steps = div - (open && div > 1 ? 1 : 0);
                if (d.PrimEmitType == 2)
                {
                    i = _sequence % div;
                    _sequence = _sequence + 1 < div ? _sequence + 1 : 0;
                }
                else
                    i = (int)(Random.GetFloat() * div);
            }
            else
            {
                int n = div - (int)(d.DivideCircleRandom * random * 0.01f * div);
                steps = n - (open && n > 1 ? 1 : 0);
                i = index % (steps + 1);
            }
            float stepAngle = d.SweepLongitude / steps;
            float r = Random.GetFloat();
            float rot =
                start
                + MathF.FusedMultiplyAdd(
                    (r + -0.5f) + (r + -0.5f),
                    d.SurfacePosRandom,
                    MathF.FusedMultiplyAdd(i, stepAngle, d.SweepLongitude * -0.5f)
                );
            VfxMath.FastSinCos(rot, out float s, out float c);
            pos = new Vector3(s * (d.Radius.X * vs.X), 0, c * (d.Radius.Z * vs.Z));
            vec = new Vector3(s * normalSpeed, 0, c * normalSpeed);
            return true;
        }

        /// <summary>The unit direction of a latitude direction setting.</summary>
        static Vector3 LatitudeAxis(byte dir) =>
            dir switch
            {
                0 => Vector3.UnitX,
                1 => -Vector3.UnitX,
                2 => Vector3.UnitY,
                3 => -Vector3.UnitY,
                4 => Vector3.UnitZ,
                5 => -Vector3.UnitZ,
                _ => Vector3.Zero,
            };

        Vector3 ApplyLatitudeDir(Vector3 v)
        {
            var axis = LatitudeAxis(Def.PoleAxis);
            if (axis == Vector3.UnitY || axis == Vector3.Zero && Def.PoleAxis > 5)
                return v;
            return VfxMath.RotateFromUp(v, axis);
        }

        bool Sphere(
            bool fill,
            float random,
            Vector3 vs,
            float normalSpeed,
            out Vector3 pos,
            out Vector3 vec
        )
        {
            var d = Def;
            bool latitude = d.ArcType == 1;
            float sweep = latitude ? d.SweepLatitude : d.SweepLongitude;
            float r0 = Random.GetFloat();
            float rot;
            if (latitude)
                rot = r0 * VfxMath.Pi + r0 * VfxMath.Pi;
            else
            {
                float start = d.SweepStartRandom
                    ? VfxMath.Pi * random + VfxMath.Pi * random
                    : d.SweepStart;
                rot = MathF.FusedMultiplyAdd(
                    sweep,
                    -0.5f,
                    MathF.FusedMultiplyAdd(r0, sweep, start)
                );
            }
            VfxMath.FastSinCos(rot, out float s, out float c);
            float y;
            if (!latitude)
                y = Random.GetFloat() * 2f - 1f;
            else
            {
                float cl = VfxMath.FastCos(d.SweepLatitude);
                y = MathF.FusedMultiplyAdd(1f - cl, Random.Next() * (-1f / 4294967296f), 1f);
            }
            float h = MathF.FusedMultiplyAdd(-y, y, 1f);
            float rr = h > 0 ? MathF.Sqrt(h) : 0;
            var dir = new Vector3(s * rr, y, c * rr);
            float fillScale = 1;
            if (fill)
            {
                float rf = Random.GetFloat();
                float sq = rf > 0 ? MathF.Sqrt(rf) : 0;
                fillScale = (sq * d.Caliber + 1f) - d.Caliber;
            }
            if (latitude)
                dir = ApplyLatitudeDir(dir);
            var radius = new Vector3(d.Radius.X * vs.X, d.Radius.Y * vs.Y, d.Radius.Z * vs.Z);
            pos = radius * dir * fillScale;
            vec = dir * normalSpeed;
            return true;
        }

        bool SphereDivided(
            int index,
            Vector3 vs,
            float normalSpeed,
            out Vector3 pos,
            out Vector3 vec
        )
        {
            var d = Def;
            float[] table =
                d.VolumeType == 5
                    ? SphereTables.GetSmall(d.SphereTableIndex)
                    : SphereTables.GetLarge(d.Sphere64Count);
            int n = table.Length / 3;
            int i = PickDivided(index, n);
            var dir = new Vector3(table[i * 3], table[i * 3 + 1], table[i * 3 + 2]);
            pos = vec = Vector3.Zero;
            if (
                VfxMath.Pi + -0.0001f >= d.SweepLatitude
                && VfxMath.FastCos(d.SweepLatitude) >= dir.Y
            )
                return false;
            dir = ApplyLatitudeDir(dir);
            var radius = new Vector3(d.Radius.X * vs.X, d.Radius.Y * vs.Y, d.Radius.Z * vs.Z);
            pos = radius * dir;
            vec = dir * normalSpeed;
            return true;
        }

        bool Box(Vector3 vs, float normalSpeed, out Vector3 pos, out Vector3 vec)
        {
            var d = Def;
            uint r0 = Random.Next();
            uint r1 = Random.Next();
            uint r2 = Random.Next();
            uint r3 = Random.Next();
            uint r4 = Random.Next();
            const float S = 1f / 4294967296f;
            float a = (r2 * S) * 2f - 1f;
            float b = (r3 * S) * 2f - 1f;
            float c = (r4 * S) * 2f - 1f;
            float x = d.Radius.X * vs.X,
                y = d.Radius.Y * vs.Y,
                z = d.Radius.Z * vs.Z;
            bool positive = r1 < 0x7FFFFFFF;
            if (r0 > 0x55555554)
            {
                if (r0 > 0xAAAAAAA9)
                    pos = new Vector3(positive ? x : -x, y * b, z * c);
                else
                    pos = new Vector3(x * a, positive ? y : -y, z * c);
            }
            else
                pos = new Vector3(x * a, y * b, positive ? z : -z);
            vec = pos == Vector3.Zero ? Vector3.Zero : VfxMath.Normalize(pos) * normalSpeed;
            return true;
        }

        bool BoxFill(Vector3 vs, float normalSpeed, out Vector3 pos, out Vector3 vec)
        {
            var d = Def;
            float x = d.Radius.X * vs.X,
                y = d.Radius.Y * vs.Y,
                z = d.Radius.Z * vs.Z;
            float inner = 1f - d.Caliber != 1f ? 1f - d.Caliber : 0.999f;
            float outer = 1f - inner;
            float f0 = Random.GetFloat() * (1f - inner * (inner * inner));
            float f1 = Random.GetFloat();
            float f2 = Random.GetFloat();
            float f3 = Random.GetFloat();
            float px = f1,
                py,
                pz = f3;
            if (f0 >= outer)
            {
                py = inner * f2;
                if (f0 >= outer + inner * outer)
                {
                    pz = inner + f3 * outer;
                    px = inner * f1;
                }
                else
                    px = inner + f1 * outer;
            }
            else
                py = inner + f2 * outer;
            var p = new Vector3(px, py, pz);
            if (Random.GetFloat() < 0.5f)
                p.X = -p.X;
            if (Random.GetFloat() < 0.5f)
                p.Y = -p.Y;
            if (Random.GetFloat() < 0.5f)
                p.Z = -p.Z;
            pos = new Vector3(x, y, z) * p;
            vec = pos == Vector3.Zero ? Vector3.Zero : VfxMath.Normalize(pos) * normalSpeed;
            return true;
        }

        bool Rectangle(Vector3 vs, float normalSpeed, out Vector3 pos, out Vector3 vec)
        {
            var d = Def;
            uint r0 = Random.Next();
            uint r1 = Random.Next();
            uint r2 = Random.Next();
            uint r3 = Random.Next();
            const float S = 1f / 4294967296f;
            float x = d.Radius.X * vs.X;
            float z = d.Radius.Z * vs.Z;
            bool positive = r1 < 0x7FFFFFFF;
            if (r0 > 0x7FFFFFFE)
                pos = new Vector3(positive ? x : -x, 0, z * ((r3 * S) * 2f - 1f));
            else
                pos = new Vector3(x * ((r2 * S) * 2f - 1f), 0, positive ? z : -z);
            vec = pos == Vector3.Zero ? Vector3.Zero : VfxMath.Normalize(pos) * normalSpeed;
            return true;
        }

        bool Primitive(int index, Vector3 vs, float normalSpeed, out Vector3 pos, out Vector3 vec)
        {
            var p = Def.ShapePrimitive;
            if (p == null || p.Count == 0)
            {
                pos = Vector3.Zero;
                vec = Random.GetTableNormal() * normalSpeed;
                return true;
            }
            int i = index;
            if (Def.PrimEmitType == 2)
                i = _sequence++ % p.Count;
            else if (Def.PrimEmitType == 1)
                i = (int)(Random.GetFloat() * p.Count);
            if (i < p.Count)
            {
                pos = vs * p.Position(i);
                vec = p.Normal(i) * normalSpeed;
            }
            else
            {
                pos = Vector3.Zero;
                vec = Vector3.Zero;
            }
            return true;
        }
    }
}
