namespace PlayerViewer.Effects.Sim
{
    public sealed partial class EmitterInstance
    {
        float _seedFrameTime = -1;
        uint _seedParentKey;
        int _seedCall;

        /// <summary>Particles this emitter has created; loop analysis counts births per period with it.</summary>
        public int Births => _createCounter;

        /// <summary>
        /// Loop mode: before each emitter level draw, the generator restarts from the emitter,
        /// the frame within the loop period and a call counter, so the draws repeat each period.
        /// A light child counts its calls per parent particle, whose order in the ring varies.
        /// </summary>
        void SeedEmitterLevel(uint purpose)
        {
            var sys = Set.System;
            if (sys.Mode != RandomMode.Loop)
                return;
            uint parentKey = EmitParentKey;
            if (_seedFrameTime != Time || _seedParentKey != parentKey)
            {
                _seedFrameTime = Time;
                _seedParentKey = parentKey;
                _seedCall = 0;
            }
            _seedCall++;
            Random.SetSeed(
                sys.Hash(Id, 0x100 + purpose, PeriodFrame(Time), (uint)_seedCall ^ parentKey)
            );
        }

        /// <summary>Loop mode: each particle's draws start from the emission frame and its index in the emission.</summary>
        void SeedParticle(int index)
        {
            var sys = Set.System;
            if (sys.Mode != RandomMode.Loop)
                return;
            Random.SetSeed(
                sys.Hash(Id, (uint)index + 1, PeriodFrame(Time), (uint)_seedCall ^ EmitParentKey)
            );
        }

        /// <summary>Loop mode: an emitter per particle child starts from its parent particle's slot and birth frame.</summary>
        internal void SeedChild()
        {
            var sys = Set.System;
            if (sys.Mode != RandomMode.Loop || Parent == null)
                return;
            uint key =
                ParentSlot >= 0 && Parent.Particles != null
                    ? Parent.Particles.LoopKey[ParentSlot]
                    : 0;
            Random.SetSeed(sys.Hash(Id, 0xC0DE, PeriodFrame(Parent.Time), key));
        }

        /// <summary>
        /// The random vector a field draws for a particle. Loop mode takes it from the particle's
        /// slot and the frame within the period rather than the emitter's running index.
        /// </summary>
        internal System.Numerics.Vector3 FieldVector(int slot)
        {
            var sys = Set.System;
            if (sys.Mode != RandomMode.Loop)
                return Random.GetTableVector();
            return RandomTables.Vec3[
                sys.Hash(Id, 0xF1E1D, PeriodFrame(Time), Particles.LoopKey[slot]) & 511
            ];
        }

        /// <summary>The key of the parent particle a light child is emitting from, 0 otherwise.</summary>
        uint EmitParentKey =>
            _emitParentSlot >= 0 ? Parent.Particles.LoopKey[_emitParentSlot] * 0x9E3779B1u : 0;

        /// <summary>
        /// Loop mode: names a new particle by its emission frame within the period, its index in
        /// the emission and its parent's key, never by its slot, which depends on ring sizes.
        /// </summary>
        void SetLoopKey(int slot, int index)
        {
            var sys = Set.System;
            if (sys.Mode == RandomMode.Loop)
                Particles.LoopKey[slot] = sys.Hash(
                    Id,
                    0x4B45,
                    PeriodFrame(Time),
                    ((uint)index | ((uint)_seedCall << 20)) ^ EmitParentKey
                );
        }

        uint PeriodFrame(float time)
        {
            int n = Set.System.LoopPeriod;
            int t = (int)System.MathF.Floor(time);
            return n > 0 ? (uint)(((t % n) + n) % n) : (uint)t;
        }

        /// <summary>The emitter level state a loop period starts from.</summary>
        internal sealed class LoopState
        {
            public float Counter,
                Carry,
                Interval;
            public bool HasEmitted;
            public int Sequence;
            public VfxRandom Random;
            public Matrix34 ResSrt,
                ResRt;
        }

        internal LoopState SaveLoopState() =>
            new()
            {
                Counter = _emitCounter,
                Carry = _emitCarry,
                Interval = _emitInterval,
                HasEmitted = _hasEmitted,
                Sequence = _sequence,
                Random = Random,
                ResSrt = ResSrt,
                ResRt = ResRt,
            };

        internal void RestoreLoopState(LoopState s)
        {
            _emitCounter = s.Counter;
            _emitCarry = s.Carry;
            _emitInterval = s.Interval;
            _hasEmitted = s.HasEmitted;
            _sequence = s.Sequence;
            Random = s.Random;
            ResSrt = s.ResSrt;
            ResRt = s.ResRt;
        }
    }
}
