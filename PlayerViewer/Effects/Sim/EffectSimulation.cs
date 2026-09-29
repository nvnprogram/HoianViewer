using System;
using System.Collections.Generic;
using System.Numerics;
using EffectLibrary;

namespace PlayerViewer.Effects.Sim
{
    /// <summary>Where per particle randomness comes from.</summary>
    public enum RandomMode
    {
        /// <summary>The runtime's own generators and seeding, for comparing with the game.</summary>
        Game,

        /// <summary>
        /// Seeds derived from the emission frame modulo the loop period, so a periodic effect
        /// repeats exactly. See <see cref="LoopAnalysis"/>.
        /// </summary>
        Loop,
    }

    /// <summary>
    /// The effect runtime's System, GL free: plays emitter sets, advances them a frame at a time
    /// in the runtime's order and produces each emitter's draw inputs. One per scene.
    /// </summary>
    public sealed class EffectSimulation
    {
        readonly Dictionary<Emitter, EmitterDef> _defs = new();

        public RandomMode Mode { get; set; } = RandomMode.Game;

        /// <summary>
        /// The runtime's global xorshift, which seeds emitter sets and random seed type emitters.
        /// The game seeds it once at boot and draws from it for every set it ever creates.
        /// </summary>
        public XorShift128 Global { get; } = new(0);

        public const uint DefaultLoopSeed = 0x9E3779B9;

        /// <summary>In <see cref="RandomMode.Loop"/>, the seed every derived seed hashes in.</summary>
        public uint LoopSeed { get; set; } = DefaultLoopSeed;

        /// <summary>In <see cref="RandomMode.Loop"/>, the period in frames the randomness repeats with.</summary>
        public int LoopPeriod { get; set; }

        /// <summary>In <see cref="RandomMode.Loop"/>, the set time the periodic regime starts at.</summary>
        public int LoopStart { get; set; }

        /// <summary>Resolves an emission primitive for the primitive volume shape; null draws none.</summary>
        public Func<Emitter, ulong, IEmissionPrimitive> PrimitiveSource { get; set; }

        public List<EmitterSetInstance> Sets { get; } = new();

        /// <summary>
        /// Replaces the seeds the global generator would give: the set's, and each random seed
        /// type emitter's by name. For reproducing a capture whose seeds were fitted.
        /// </summary>
        public uint? SetSeedOverride { get; set; }
        public Dictionary<string, uint> EmitterSeedOverrides { get; } = new();

        /// <summary>
        /// Integrates stream out emitters on the CPU with the same fields, standing in for the
        /// compute programs the game runs for them.
        /// </summary>
        public bool StreamOutOnCpu { get; set; } = true;

        /// <summary>In <see cref="RandomMode.Loop"/>, the particle slots per emitter id that make the ring periodic.</summary>
        public Dictionary<(string Set, uint Emitter), int> LoopCapacities { get; } = new();

        /// <summary>
        /// Loop analysis: every finite ring is oversized and overwrites, so births do not depend
        /// on ring sizes and can be counted.
        /// </summary>
        internal bool LoopOverwriteAll { get; set; }

        internal int? LoopCapacity(EmitterInstance e) =>
            LoopCapacities.TryGetValue((e.Set.Resource.Name, e.Id), out int c) ? c : null;

        public EmitterDef GetDef(Emitter e)
        {
            var root = e;
            while (root.Parent != null)
                root = root.Parent;
            if (!_defs.TryGetValue(root, out var def))
            {
                def = new EmitterDef(root, null, PrimitiveSource);
                _defs[root] = def;
            }
            return Find(def, e);
        }

        static EmitterDef Find(EmitterDef d, Emitter e)
        {
            if (d.Source == e)
                return d;
            foreach (var c in d.Children)
                if (Find(c, e) is { } r)
                    return r;
            return null;
        }

        /// <summary>Creates a set the way CreateEmitterSetId does, with its matrix set once.</summary>
        public EmitterSetInstance Play(EmitterSet set, in Matrix34 matrix)
        {
            var inst = new EmitterSetInstance(this, set);
            inst.SetMatrix(matrix);
            inst.Initialize();
            Sets.Add(inst);
            return inst;
        }

        /// <summary>
        /// One frame: the deferred kills, then every set in creation order. A frame rate of zero
        /// rebuilds matrices and blocks without advancing anything, as the game does paused.
        /// </summary>
        public void Calculate(float frameRate)
        {
            Sets.RemoveAll(s => s.KillRequested);
            foreach (var s in Sets.ToArray())
                s.Calculate(frameRate);
            Sets.RemoveAll(s => !s.Alive);
        }

        /// <summary>A seed derived from its parts for loop mode.</summary>
        internal uint Hash(uint a, uint b, uint c, uint d)
        {
            ulong h = LoopSeed * 0x9E3779B97F4A7C15UL;
            foreach (uint v in new[] { a, b, c, d })
            {
                h ^= v + 0x9E3779B97F4A7C15UL + (h << 6) + (h >> 2);
                h *= 0xBF58476D1CE4E5B9UL;
                h ^= h >> 31;
            }
            return (uint)(h ^ (h >> 32));
        }
    }

    /// <summary>A mesh the primitive volume shape emits from: a position and a normal per vertex.</summary>
    public interface IEmissionPrimitive
    {
        int Count { get; }
        Vector3 Position(int index);
        Vector3 Normal(int index);
    }
}
