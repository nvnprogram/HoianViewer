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

        /// <summary>The GRSR section, the render programs, or null.</summary>
        public VfxSection RenderSection { get; }

        /// <summary>Whether the programs are BNSH containers rather than BFSHA.</summary>
        public bool IsBnsh => RenderSection != null;

        /// <summary>The BNSH of the render programs, empty when there is none.</summary>
        public Memory<byte> RenderBinary => RenderSection?.Payload ?? Memory<byte>.Empty;

        /// <summary>The compute programs' archive bytes (BFSHA in v46, BNSH in v54).</summary>
        public Memory<byte> ComputeBinary => ComputeSection?.Payload ?? Memory<byte>.Empty;

        BfshaFile _general,
            _compute;
        Exception _generalError,
            _computeError;

        ShaderArchive(VfxSection section)
        {
            Section = section;
            ComputeSection = section.FindChild("GRSC");
            RenderSection = section.FindChild("GRSR");
            if (IsBnsh)
                _generalError = _computeError = new NotSupportedException(
                    "The programs are BNSH, not BFSHA"
                );
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

        /// <summary>The render programs of a BNSH archive, parsed on first use; null for BFSHA.</summary>
        public BnshFile Render =>
            IsBnsh ? LoadBnsh(RenderSection, ref _render, ref _renderError) : null;

        public Exception RenderError => _renderError;

        BnshFile _render;
        Exception _renderError;

        public BnshFile.ShaderVariation GetRenderVariation(int index)
        {
            if (IsBnsh)
            {
                var bnsh = Render;
                return bnsh == null || index < 0 || index >= bnsh.Variations.Count
                    ? null
                    : bnsh.Variations[index];
            }
            var model = GeneralModel;
            return model == null || index < 0 || index >= model.Programs.Count
                ? null
                : model.GetVariation(model.Programs[index]);
        }

        static BnshFile LoadBnsh(VfxSection section, ref BnshFile file, ref Exception error)
        {
            if (file != null || error != null)
                return file;
            try
            {
                file = new BnshFile(Streams.Open(section.Payload));
            }
            catch (Exception e)
            {
                error = e;
            }
            return file;
        }

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
