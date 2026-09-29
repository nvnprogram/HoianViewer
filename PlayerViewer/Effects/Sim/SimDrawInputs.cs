using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace PlayerViewer.Effects.Sim
{
    /// <summary>Turns a simulated emitter into the per draw inputs <see cref="EmitterDrawInputs"/> the renderer takes.</summary>
    public static class SimDrawInputs
    {
        /// <summary>
        /// The emitter's draws as the game issues them: one per slot range, in slot order. Each
        /// holds the dynamic block and the range's particle streams, instance 0 first.
        /// </summary>
        public static List<EmitterDrawInputs> Build(EmitterInstance e, float timeOffset = 0) =>
            Build(e, timeOffset, timeOffset);

        /// <summary>
        /// As <see cref="Build(EmitterInstance, float)"/>, with infinite life particles made
        /// <paramref name="ageOffset"/> younger instead of <paramref name="timeOffset"/>.
        /// </summary>
        public static List<EmitterDrawInputs> Build(
            EmitterInstance e,
            float timeOffset,
            float ageOffset
        )
        {
            if (e.Stripes != null)
                return BuildStripes(e, timeOffset, ageOffset);
            var list = new List<EmitterDrawInputs>();
            foreach (var (first, count) in e.Particles.DrawRanges())
                list.Add(Build(e, first, count, timeOffset, ageOffset));
            return list;
        }

        /// <summary>
        /// The draws of an emitter with a particle alive, for a loop: an emitter whose particles
        /// all died keeps drawing them unseen, and their birth times would not repeat.
        /// </summary>
        public static List<EmitterDrawInputs> BuildVisible(EmitterInstance e, float timeOffset) =>
            BuildVisible(e, timeOffset, timeOffset);

        public static List<EmitterDrawInputs> BuildVisible(
            EmitterInstance e,
            float timeOffset,
            float ageOffset
        ) =>
            e.Stripes != null
            || e.Particles.AnyAlive(
                BinaryPrimitives.ReadSingleLittleEndian(e.DynamicBlock.AsSpan(0x20))
            )
                ? Build(e, timeOffset, ageOffset)
                : new List<EmitterDrawInputs>();

        /// <summary>
        /// A stripe emitter's one draw: every stripe with vertices, their points packed in draw
        /// order so the inputs do not depend on where the runtime would have placed them.
        /// </summary>
        static List<EmitterDrawInputs> BuildStripes(
            EmitterInstance e,
            float timeOffset,
            float ageOffset
        )
        {
            var list = new List<EmitterDrawInputs>();
            var stripes = new List<StripeInstance>(e.Stripes.Drawn());
            if (stripes.Count == 0)
                return list;
            int points = Math.Max(1, e.Stripes.Params.PointsPerStripe);
            int stride = points * HistoryStripes.FloatsPerPoint;
            var storage = new float[stripes.Count * stride];
            var draws = new List<StripeDraw>(stripes.Count);
            bool cross = e.Stripes.Params.Option == 1;
            for (int i = 0; i < stripes.Count; i++)
            {
                var s = stripes[i];
                Array.Copy(s.Points, 0, storage, i * stride, stride);
                var block = new byte[HistoryStripes.BlockSize];
                e.Stripes.WriteBlock(s, block, i);
                if (
                    ageOffset != 0
                    && e.Def.InfiniteLife
                    && !(e.IsChild && e.Def.EmitterPerParticle)
                )
                    BinaryPrimitives.WriteSingleLittleEndian(
                        block.AsSpan(0x40),
                        s.Time - ageOffset
                    );
                draws.Add(new StripeDraw(block, s.VertexCount, cross));
            }
            var inputs = new EmitterDrawInputs
            {
                InstanceCount = draws.Count,
                PluginStorage = storage,
                Stripes = draws,
            };
            inputs[EffectBlock.EmitterDynamic] = Dynamic(e, timeOffset);
            list.Add(inputs);
            return list;
        }

        static byte[] Dynamic(EmitterInstance e, float timeOffset)
        {
            byte[] dyn = (byte[])e.DynamicBlock.Clone();
            if (timeOffset != 0 && !(e.IsChild && e.Def.EmitterPerParticle))
            {
                float t = BinaryPrimitives.ReadSingleLittleEndian(dyn.AsSpan(0x20));
                BinaryPrimitives.WriteSingleLittleEndian(dyn.AsSpan(0x20), t - timeOffset);
            }
            return dyn;
        }

        /// <summary>
        /// One draw of slots [first, first + count). A nonzero <paramref name="timeOffset"/> is
        /// taken off the emitter time and every birth time, which leaves each particle's age
        /// unchanged; a loop uses it to hand the renderer the same inputs every period. Infinite
        /// life particles are made <paramref name="ageOffset"/> younger instead, by default the
        /// same, so their age comes back each period too.
        /// </summary>
        public static EmitterDrawInputs Build(
            EmitterInstance e,
            int first,
            int count,
            float timeOffset = 0,
            float? ageOffset = null
        )
        {
            float age = ageOffset ?? timeOffset;
            // An emitter per particle child keeps its own clock from its parent's birth.
            if (e.IsChild && e.Def.EmitterPerParticle)
                timeOffset = age = 0;
            float shift = e.Def.InfiniteLife ? timeOffset - age : timeOffset;
            var inputs = new EmitterDrawInputs { InstanceCount = count };
            inputs[EffectBlock.EmitterDynamic] = Dynamic(e, timeOffset);
            var ps = e.Particles;
            for (int s = 0; s < ps.Streams.Length; s++)
            {
                var src = ps.Streams[s];
                if (src == null)
                    continue;
                var data = new float[count * 4];
                Array.Copy(src, first * 4, data, 0, count * 4);
                if (shift != 0 && s == (int)ParticleStream.LocalVec)
                    for (int i = 0; i < count; i++)
                        data[i * 4 + 3] -= shift;
                inputs.SetParticles(EffectBindings.ParticleAttributes[s], data);
            }
            return inputs;
        }
    }
}
