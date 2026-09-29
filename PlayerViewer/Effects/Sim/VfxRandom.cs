using System;
using System.Numerics;

namespace PlayerViewer.Effects.Sim
{
    /// <summary>
    /// The runtime's xorshift128. One instance is global: it seeds the emitter sets and the
    /// emitters whose seed type asks for a random seed. Another built from a fixed seed fills the
    /// two direction tables.
    /// </summary>
    public sealed class XorShift128
    {
        uint _x,
            _y,
            _z,
            _w;

        public XorShift128(uint seed) => SetSeed(seed);

        public void SetSeed(uint s)
        {
            _x = (s ^ (s >> 30)) * 1812433253u + 1;
            _y = (_x ^ (_x >> 30)) * 1812433253u + 2;
            _z = (_y ^ (_y >> 30)) * 1812433253u + 3;
            _w = (_z ^ (_z >> 30)) * 1812433253u + 4;
        }

        public uint Next()
        {
            uint t = _x ^ (_x << 11);
            _x = _y;
            _y = _z;
            _z = _w;
            _w = _w ^ (_w >> 19) ^ t ^ (t >> 8);
            return _w;
        }

        /// <summary>[0, 1) from the top 23 bits, as the runtime builds it.</summary>
        public float NextFloat() =>
            BitConverter.UInt32BitsToSingle((Next() >> 9) | 0x3F800000) - 1f;

        public (uint X, uint Y, uint Z, uint W) State => (_x, _y, _z, _w);
    }

    /// <summary>
    /// One emitter's random source: a linear congruential generator for scalars and two
    /// independent 16 bit cursors into the shared direction tables.
    /// </summary>
    public struct VfxRandom
    {
        public uint Value;
        public ushort Vec3Index;
        public ushort NormalIndex;

        /// <summary>Scalar draws since the last seed; where a value sits in the sequence.</summary>
        public int Draws;

        const float Scale = 1f / 4294967296f;

        /// <summary>The runtime's SetSeed: the table cursors come from the seed's halves.</summary>
        public void SetSeed(uint seed)
        {
            Value = seed;
            Draws = 0;
            Vec3Index = (ushort)seed;
            NormalIndex = (ushort)(seed >> 16);
        }

        public uint Next()
        {
            uint old = Value;
            Value = old * 1103515245u + 12345u;
            Draws++;
            return old;
        }

        /// <summary>The generator state <paramref name="steps"/> draws before <paramref name="value"/>.</summary>
        public static uint Rewind(uint value, int steps)
        {
            const uint inverse = 4005161829u;
            for (int i = 0; i < steps; i++)
                value = unchecked((value - 12345u) * inverse);
            return value;
        }

        /// <summary>[0, 1).</summary>
        public float GetFloat() => Next() * Scale;

        /// <summary>[0, n).</summary>
        public int GetInteger(int n) => (int)(((long)Next() * n) >> 32);

        /// <summary>Components in [-1, 1).</summary>
        public Vector3 GetTableVector() => RandomTables.Vec3[Vec3Index++ & 511];

        /// <summary>A unit vector.</summary>
        public Vector3 GetTableNormal() => RandomTables.Normal[NormalIndex++ & 511];
    }

    /// <summary>The two 512 entry direction tables, built once from a fixed xorshift seed.</summary>
    public static class RandomTables
    {
        public static readonly Vector3[] Vec3 = new Vector3[512];
        public static readonly Vector3[] Normal = new Vector3[512];

        static RandomTables()
        {
            var r = new XorShift128(12345679);
            static float Sym(float f) => (f * 2f) - 1f;
            for (int i = 0; i < 512; i++)
            {
                Vec3[i] = new Vector3(Sym(r.NextFloat()), Sym(r.NextFloat()), Sym(r.NextFloat()));
                var n = new Vector3(Sym(r.NextFloat()), Sym(r.NextFloat()), Sym(r.NextFloat()));
                Normal[i] = VfxMath.Normalize(n);
            }
        }
    }
}
