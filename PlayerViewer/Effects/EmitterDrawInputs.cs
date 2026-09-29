using System;
using System.Collections.Generic;

namespace PlayerViewer.Effects
{
    /// <summary>
    /// Everything one emitter draw reads that the simulation and the host produce: the uniform
    /// blocks by kind, and the per particle attribute streams by reflection name, one float4 per
    /// particle each. The static block is the emitter's own and comes from
    /// <c>StaticUniformBlock.Build</c>; the rest change per frame.
    /// </summary>
    public sealed class EmitterDrawInputs
    {
        readonly byte[][] _blocks = new byte[EffectBindings.BlockCount][];

        public Dictionary<string, float[]> Particles { get; } = new(StringComparer.Ordinal);

        /// <summary>Instances drawn: particles, or one for an emitter that draws its mesh once.</summary>
        public int InstanceCount { get; set; }

        public byte[] this[EffectBlock block]
        {
            get => _blocks[(int)block];
            set => _blocks[(int)block] = value;
        }

        /// <summary>Sets a particle stream; <paramref name="data"/> holds four floats a particle.</summary>
        public void SetParticles(string attribute, float[] data) => Particles[attribute] = data;

        /// <summary>
        /// A stripe emitter's points, the storage buffer its vertex program reads: each stripe's
        /// points back to back, twelve floats a point. Null for any other emitter.
        /// </summary>
        public float[] PluginStorage { get; set; }

        /// <summary>A stripe emitter's draws in order; null for any other emitter.</summary>
        public List<StripeDraw> Stripes { get; set; }

        /// <summary>
        /// An area loop emitter's plugin blocks: the particles are drawn once with each. Null for
        /// any other emitter.
        /// </summary>
        public List<byte[]> PluginBlocks { get; set; }
    }

    /// <summary>
    /// One stripe: its plugin block, the vertices it draws (two a point) and whether a second
    /// strip crosses the first.
    /// </summary>
    public readonly record struct StripeDraw(byte[] Block, int VertexCount, bool Cross);
}
