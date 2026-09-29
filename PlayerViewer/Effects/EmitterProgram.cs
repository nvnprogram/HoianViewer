using System.Collections.Generic;
using EffectLibrary;
using ShaderLibrary;

namespace PlayerViewer.Effects
{
    /// <summary>A binding's slot in the vertex and the fragment stage, -1 where it is unused.</summary>
    public readonly record struct StageSlots(int Vertex, int Fragment)
    {
        public static readonly StageSlots None = new(-1, -1);

        public bool Used => Vertex >= 0 || Fragment >= 0;
    }

    /// <summary>
    /// One program of an effect file's general archive and what it binds, from the per stage
    /// reflection its variation carries. The shading model itself declares no samplers or vertex
    /// inputs, so this is the only place the binding map comes from.
    /// </summary>
    public sealed class EmitterProgram
    {
        public int Index { get; }

        public BnshFile.ShaderVariation Variation { get; }

        /// <summary>NVN uniform buffer slot per block, per stage.</summary>
        public IReadOnlyDictionary<EffectBlock, StageSlots> Blocks { get; }

        /// <summary>NVN sampler slot per reflection name, per stage.</summary>
        public IReadOnlyDictionary<string, StageSlots> Samplers { get; }

        /// <summary>Vertex input location per reflection name.</summary>
        public IReadOnlyDictionary<string, int> Attributes { get; }

        /// <summary>Storage buffers the vertex stage reads (stripe plugins, custom attributes).</summary>
        public IReadOnlyList<string> StorageBuffers { get; }

        EmitterProgram(
            int index,
            BnshFile.ShaderVariation variation,
            Dictionary<EffectBlock, StageSlots> blocks,
            Dictionary<string, StageSlots> samplers,
            Dictionary<string, int> attributes,
            List<string> storage
        )
        {
            Index = index;
            Variation = variation;
            Blocks = blocks;
            Samplers = samplers;
            Attributes = attributes;
            StorageBuffers = storage;
        }

        public StageSlots Block(EffectBlock block) =>
            Blocks.GetValueOrDefault(block, StageSlots.None);

        public int Attribute(string name) => Attributes.TryGetValue(name, out int at) ? at : -1;

        /// <summary>
        /// Whether any program the emitter draws with samples texture slot
        /// <paramref name="slot"/>; true when a program cannot be read.
        /// </summary>
        public static bool SamplesTexture(Emitter emitter, int slot)
        {
            var archive = emitter.Set.File.Shaders;
            string name = "sysTextureSampler" + slot;
            foreach (
                int index in new[]
                {
                    emitter.ShaderIndex,
                    emitter.DepthModeShaderIndex,
                    emitter.PassShaderIndex,
                }
            )
            {
                if (index < 0)
                    continue;
                var program = Load(archive, index);
                if (program == null || program.Samplers.ContainsKey(name))
                    return true;
            }
            return false;
        }

        /// <summary>Whether the vertex stage of the emitter's draw program samples texture slot <paramref name="slot"/>.</summary>
        public static bool SamplesTextureInVertex(Emitter emitter, int slot)
        {
            var program = Load(emitter.Set.File.Shaders, emitter.ShaderIndex);
            return program != null
                && program.Samplers.TryGetValue("sysTextureSampler" + slot, out var at)
                && at.Vertex >= 0;
        }

        /// <summary>The program at <paramref name="index"/> of the file's general archive, or null.
        /// Safe from any thread: the archive's programs are read on first use from one stream.</summary>
        public static EmitterProgram Load(ShaderArchive archive, int index)
        {
            if (archive == null)
                return null;
            lock (archive)
                return LoadLocked(archive, index);
        }

        static EmitterProgram LoadLocked(ShaderArchive archive, int index)
        {
            var model = archive.GeneralModel;
            if (model == null || index < 0 || index >= model.Programs.Count)
                return null;
            var variation = model.GetVariation(model.Programs[index]);
            var program = variation?.BinaryProgram;
            if (program == null)
                return null;

            var blocks = new Dictionary<EffectBlock, StageSlots>();
            var samplers = new Dictionary<string, StageSlots>();
            var attributes = new Dictionary<string, int>();
            var storage = new List<string>();
            Collect(program.VertexShaderReflection, true, blocks, samplers, attributes, storage);
            Collect(program.FragmentShaderReflection, false, blocks, samplers, null, storage);
            return new EmitterProgram(index, variation, blocks, samplers, attributes, storage);
        }

        static void Collect(
            BnshFile.ShaderReflectionData r,
            bool vertex,
            Dictionary<EffectBlock, StageSlots> blocks,
            Dictionary<string, StageSlots> samplers,
            Dictionary<string, int> attributes,
            List<string> storage
        )
        {
            if (r == null)
                return;
            for (int i = 0; i < r.UniformBuffers.Count; i++)
                if (
                    EffectBindings.BlockNames.TryGetValue(r.UniformBuffers.GetKey(i), out var block)
                )
                    blocks[block] = With(
                        blocks.GetValueOrDefault(block, StageSlots.None),
                        vertex,
                        (int)r.UniformBuffers[i].Value
                    );
            for (int i = 0; i < r.Samplers.Count; i++)
            {
                string name = r.Samplers.GetKey(i);
                samplers[name] = With(
                    samplers.GetValueOrDefault(name, StageSlots.None),
                    vertex,
                    (int)r.Samplers[i].Value
                );
            }
            if (attributes != null)
                for (int i = 0; i < r.Inputs.Count; i++)
                    attributes[r.Inputs.GetKey(i)] = (int)r.Inputs[i].Value;
            for (int i = 0; i < r.StorageBuffers.Count; i++)
                if (!storage.Contains(r.StorageBuffers.GetKey(i)))
                    storage.Add(r.StorageBuffers.GetKey(i));
        }

        static StageSlots With(StageSlots s, bool vertex, int slot) =>
            vertex ? s with { Vertex = slot } : s with { Fragment = slot };
    }
}
