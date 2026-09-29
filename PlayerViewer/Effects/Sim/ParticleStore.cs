using System;
using System.Numerics;

namespace PlayerViewer.Effects.Sim
{
    /// <summary>The per particle attribute streams, one float4 per slot each, named as the shaders read them.</summary>
    public enum ParticleStream
    {
        LocalPos,
        LocalVec,
        LocalDiff,
        Scale,
        Random,
        InitRotate,
        Color0,
        Color1,
        EmtMat0,
        EmtMat1,
        EmtMat2,
    }

    /// <summary>
    /// An emitter's particle ring: slots filled in order and reused once dead, the attribute
    /// streams, and the bookkeeping the runtime keeps per slot.
    /// </summary>
    public sealed class ParticleStore
    {
        public int Capacity { get; }

        /// <summary>Slots in use, up to the capacity; what the runtime reports as the particle count.</summary>
        public int Count;

        /// <summary>The next slot to fill.</summary>
        public int Fill;

        /// <summary>The oldest slot that may still be alive.</summary>
        public int Oldest;

        /// <summary>Particles the last CPU pass found alive.</summary>
        public int LiveCount;

        public readonly float[][] Streams = new float[11][];

        /// <summary>Per slot: nonzero while alive (CPU emitters clear it on death).</summary>
        public readonly int[] CreateId;
        public readonly float[] CreateTime;
        public readonly float[] Life;

        /// <summary>Per slot per child resource: the child emitter spawned from this particle.</summary>
        public readonly EmitterInstance[,] Children;

        /// <summary>Per slot: the time a light child has emitted from this particle.</summary>
        public readonly float[] ChildClock;

        /// <summary>Per slot per light child: its emission counter, carried fraction, interval and emitted flag.</summary>
        public readonly float[,] ChildCounter,
            ChildCarry,
            ChildInterval;
        public readonly bool[,] ChildHasEmitted;
        public readonly int[] Bounces;

        /// <summary>Per slot: the generator's draw count when the random attribute was drawn,
        /// for fitting a seed against a capture.</summary>
        public readonly int[] RandomDraw;

        /// <summary>Loop mode: a particle's identity within the period, which seeds what descends from it.</summary>
        public readonly uint[] LoopKey;

        public ParticleStore(int capacity, bool colors, bool emitterMatrices, int childCount)
        {
            Capacity = Math.Max(0, capacity);
            for (int i = 0; i < Streams.Length; i++)
            {
                var s = (ParticleStream)i;
                bool present =
                    s is ParticleStream.Color0 or ParticleStream.Color1 ? colors
                    : s
                        is ParticleStream.EmtMat0
                            or ParticleStream.EmtMat1
                            or ParticleStream.EmtMat2
                        ? emitterMatrices
                    : true;
                if (present)
                    Streams[i] = new float[Capacity * 4];
            }
            CreateId = new int[Capacity];
            CreateTime = new float[Capacity];
            Life = new float[Capacity];
            Bounces = new int[Capacity];
            RandomDraw = new int[Capacity];
            LoopKey = new uint[Capacity];
            if (childCount > 0)
            {
                Children = new EmitterInstance[Capacity, childCount];
                ChildClock = new float[Capacity];
                ChildCounter = new float[Capacity, childCount];
                ChildCarry = new float[Capacity, childCount];
                ChildInterval = new float[Capacity, childCount];
                ChildHasEmitted = new bool[Capacity, childCount];
            }
        }

        public float[] this[ParticleStream s] => Streams[(int)s];

        public Vector4 Get(ParticleStream s, int slot)
        {
            var a = Streams[(int)s];
            int i = slot * 4;
            return new Vector4(a[i], a[i + 1], a[i + 2], a[i + 3]);
        }

        public Vector3 Get3(ParticleStream s, int slot)
        {
            var a = Streams[(int)s];
            int i = slot * 4;
            return new Vector3(a[i], a[i + 1], a[i + 2]);
        }

        public void Set(ParticleStream s, int slot, Vector4 v)
        {
            var a = Streams[(int)s];
            if (a == null)
                return;
            int i = slot * 4;
            a[i] = v.X;
            a[i + 1] = v.Y;
            a[i + 2] = v.Z;
            a[i + 3] = v.W;
        }

        public void Set3(ParticleStream s, int slot, Vector3 v)
        {
            var a = Streams[(int)s];
            if (a == null)
                return;
            int i = slot * 4;
            a[i] = v.X;
            a[i + 1] = v.Y;
            a[i + 2] = v.Z;
        }

        public void SetW(ParticleStream s, int slot, float w)
        {
            var a = Streams[(int)s];
            if (a != null)
                a[slot * 4 + 3] = w;
        }

        public bool IsAlive(int slot, float time) => time - CreateTime[slot] < Life[slot];

        /// <summary>Whether any particle is alive; a GPU emitter's ring keeps its dead ones in the draw.</summary>
        public bool AnyAlive(float time)
        {
            for (int i = 0; i < Count; i++)
                if (CreateId[i] != 0 && IsAlive(i, time))
                    return true;
            return false;
        }

        /// <summary>Moves the oldest index past dead slots, stopping short of the fill index.</summary>
        public void AdvanceOldest(float time)
        {
            if (Capacity == 0)
                return;
            int old = Oldest;
            if (old == Fill && CreateId[old] == 0)
                return;
            int k = old;
            do
            {
                if (time - CreateTime[k] < Life[k])
                    break;
                if ((old + 1) % Capacity != Fill)
                    old++;
                k = (k + 1) % Capacity;
            } while (k != Fill);
            Oldest = old % Capacity;
        }

        public void AdvanceChildTime(float frameRate)
        {
            if (ChildClock == null)
                return;
            for (int i = 0; i < Count; i++)
                if (CreateId[i] != 0)
                    ChildClock[i] += frameRate;
        }

        /// <summary>
        /// The slot ranges the runtime draws, oldest to newest along the ring: one range, or
        /// the newest part from slot 0 and then the oldest part up to the capacity.
        /// </summary>
        public (int First, int Count)[] DrawRanges()
        {
            if (Count <= 0)
                return Array.Empty<(int, int)>();
            int last = (Fill + Capacity - 1) % Capacity;
            if (last >= Oldest)
                return new[] { (Oldest, last - Oldest + 1) };
            return new[] { (0, last + 1), (Oldest, Capacity - Oldest) };
        }
    }
}
