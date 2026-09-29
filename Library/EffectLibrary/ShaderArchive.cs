using System;
using ShaderLibrary;

namespace EffectLibrary
{
    /// <summary>
    /// GRSN and its GRSC child: two BFSHA archives, the general one (model VfxGeneralShader) that
    /// every emitter's three program indices point into, and the compute one (model
    /// ComputeShader) for stream out emitters. Some files carry no compute archive. Both are
    /// parsed on first use and saved as their original bytes.
    /// </summary>
    public sealed class ShaderArchive
    {
        public VfxSection Section { get; }

        /// <summary>The GRSC section, or null.</summary>
        public VfxSection ComputeSection { get; }

        BfshaFile _general,
            _compute;
        Exception _generalError,
            _computeError;

        ShaderArchive(VfxSection section)
        {
            Section = section;
            ComputeSection = section.FindChild("GRSC");
        }

        internal static ShaderArchive From(VfxSection section) =>
            section == null ? null : new ShaderArchive(section);

        public Exception GeneralError => _generalError;
        public Exception ComputeError => _computeError;

        public BfshaFile General => Load(Section, ref _general, ref _generalError);

        public BfshaFile Compute =>
            ComputeSection == null ? null : Load(ComputeSection, ref _compute, ref _computeError);

        /// <summary>The general archive's one shader model.</summary>
        public ShaderModel GeneralModel => General?.ShaderModels[0];

        public BfshaShaderProgram GetProgram(int index)
        {
            var model = GeneralModel;
            return model == null || index < 0 || index >= model.Programs.Count
                ? null
                : model.Programs[index];
        }

        static BfshaFile Load(VfxSection section, ref BfshaFile file, ref Exception error)
        {
            if (file != null || error != null)
                return file;
            try
            {
                file = new BfshaFile(Streams.Open(section.Payload));
            }
            catch (Exception e)
            {
                error = e;
            }
            return file;
        }
    }
}
