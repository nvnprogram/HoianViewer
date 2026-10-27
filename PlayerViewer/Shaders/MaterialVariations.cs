using System;
using System.Collections.Generic;
using System.Linq;
using BfresEditor;
using Gsys;
using PlayerViewer.Core;
using ShaderBundler;
using ShaderLibrary;
using ShaderLibrary.Helpers;

namespace PlayerViewer.Shaders
{
    public enum CellState
    {
        None,
        Queued,
        Running,
        Ready,
        Failed,
    }

    /// <summary>One (pipeline stage, weight) cell of a material.</summary>
    public sealed class VariationCell
    {
        public string Pass;
        public uint Weight;
        public OptionVector Vector;

        /// <summary>Program index in the archive that serves the material, -1 when it has
        /// none. (the exact hashed key lookup the engine does)</summary>
        public int ExistingProgram = -1;

        /// <summary>Why the ubershader cannot express this vector, null when it can.</summary>
        public string Refusal;

        public CellState State;
        public string Failure;
        public double Seconds;

        /// <summary>A quick splice of this cell is in the cache, so it can be drawn now.</summary>
        public bool PreviewReady;

        public CellState PreviewState;

        public bool Exists => ExistingProgram >= 0;

        //What a splice of this cell leaves out, for the reach version it was worked out against.
        internal List<string> Lost;
        internal int LostVersion = -1;

        /// <summary>Identity of the splice, shared by any cell that would produce the same
        /// binaries. Default on an existence only cell.</summary>
        public SpliceKey Key;
    }

    /// <summary>
    /// Which pipeline stages one material already has a program for and which would have to
    /// be generated, plus the stage selection the compile scheduler works from.
    /// </summary>
    public sealed class MaterialVariations
    {
        public FMAT Material { get; }

        public MaterialVariations(FMAT material)
        {
            Material = material;
        }

        /// <summary>Distinct vertex skin counts of the shapes drawn with this material. The
        /// weight is part of the key, so each one is its own set of cells.</summary>
        public uint[] Weights { get; private set; } = { 0 };

        public ShaderModel Serving { get; private set; }
        public string Error { get; private set; }

        /// <summary>The gsys_assign_type choices the cells were built over, which is the
        /// ubershader's list when there is one and the serving archive's when there is not.
        /// </summary>
        public IReadOnlyList<string> AssignTypes { get; private set; } = Array.Empty<string>();

        /// <summary>Built without the ubershader, so the cells answer whether a shipped
        /// program exists and nothing else. Neither carving nor previewing can read them.
        /// </summary>
        public bool ExistenceOnly { get; private set; }

        public bool Manual;
        public readonly HashSet<string> ManualPasses = new(StringComparer.Ordinal);
        bool _manualSeeded;

        public PassPolicy.Result Policy { get; private set; }

        public readonly List<VariationCell> Cells = new();

        /// <summary>Options the material states that the serving archive does not know. They
        /// stay at that archive's default, which is usually not what the author meant.</summary>
        public readonly List<string> Dropped = new();

        /// <summary>Values the ubershader has no code for. A splice made from one of these is
        /// silently a different shader, so the cells carrying them are refused.</summary>
        public readonly List<string> Unsupported = new();

        /// <summary>What stands in for the features the ubershader lacks. Null outside Raiders,
        /// and when the shipped programs serve the material as edited.</summary>
        public MissingFeatures.Layer Layer { get; private set; }

        /// <summary>What the splices a save would embed leave out, each feature once. Empty when
        /// the shipped programs serve the material and nothing is spliced for it.</summary>
        public List<string> Lost
        {
            get
            {
                var lost = new List<string>();
                if (_uber?.Reach == null || !Spliced)
                    return lost;
                foreach (var c in Cells)
                    if (DrawnPasses.IsDrawn(c.Pass) || IsSelected(c.Pass))
                        Note(lost, LostOf(c));
                return lost;
            }
        }

        /// <summary>What the splices a save would embed rebuild from what the ubershader can do.</summary>
        public IReadOnlyList<string> Approximated =>
            Layer != null && Spliced ? Layer.Approximated : NoLoss;

        bool Spliced => ModelBundle.NeedsArchive(this, _uber, null, null, countPending: true);

        /// <summary>What a splice of this cell leaves out, in words. Empty when it needs none or
        /// the reach is not known yet.</summary>
        public List<string> LostOf(VariationCell cell)
        {
            if (_uber == null || cell.Exists || cell.Refusal != null)
                return NoLoss;
            int version = _uber.ReachVersion;
            if (cell.LostVersion != version)
            {
                cell.Lost = SpliceLoss.Of(_uber, cell.Vector.Full, Material.Drawn, Layer);
                cell.LostVersion = version;
            }
            return cell.Lost;
        }

        static readonly List<string> NoLoss = new();
        UberContext _uber;

        public bool Ok => Error == null && Cells.Count > 0;

        public IEnumerable<VariationCell> CellsOf(string pass)
        {
            foreach (var c in Cells)
                if (c.Pass == pass)
                    yield return c;
        }

        public void ResetManualToPolicy()
        {
            ManualPasses.Clear();
            if (Policy == null)
                return;
            foreach (var pass in Policy.Passes)
                ManualPasses.Add(pass);
        }

        public bool IsSelected(string pass) =>
            Manual ? ManualPasses.Contains(pass) : Policy != null && Policy.Passes.Contains(pass);

        /// <summary>
        /// The existence half of the grid, from the serving archive alone. It is what the
        /// Stages tab shows with the splicer off.
        /// </summary>
        public void RebuildExistence(IEnumerable<uint> weights) => Build(null, weights, false);

        /// <summary>The full grid: existence, the option vectors and what the cache holds.
        /// With approximate, a material the shipped programs do not serve is spliced with
        /// stand-ins for what the ubershader lacks.</summary>
        public void Rebuild(UberContext uber, IEnumerable<uint> weights, bool approximate) =>
            Build(uber ?? throw new ArgumentNullException(nameof(uber)), weights, approximate);

        /// <summary>Marks the grid unusable, for a failure raised outside its own build.</summary>
        public void Fail(string message)
        {
            Cells.Clear();
            Error = message;
            Layer = null;
            Material.DrawnMaterial = null;
        }

        //One loop for both grids: without a context a cell only carries whether a shipped
        //program exists, with one it carries the vector and the cache state as well.
        void Build(UberContext uber, IEnumerable<uint> weights, bool approximate)
        {
            Cells.Clear();
            Dropped.Clear();
            Unsupported.Clear();
            Error = null;
            Layer = null;
            Material.DrawnMaterial = null;
            ExistenceOnly = uber == null;
            _uber = uber;
            AssignTypes = Array.Empty<string>();

            try
            {
                var distinct = new SortedSet<uint>(weights ?? Array.Empty<uint>());
                if (distinct.Count == 0)
                    distinct.Add(0);
                Weights = distinct.ToArray();

                //The probed archive, never the preview one the viewer may have bound:
                //existence is a question about what shipped.
                Serving = Material.GetBaseShaderModel()?.Inner;
                if (Serving == null)
                {
                    Error = "no shader archive resolved for this material";
                    return;
                }

                if (uber == null)
                    AssignTypes = UberSelect.AssignTypes(Serving);
                else
                {
                    AssignTypes = uber.AssignTypes;
                    string generation = uber.GenerationProblem(Serving);
                    if (generation != null)
                    {
                        Error = generation;
                        return;
                    }
                    Policy = PassPolicy.Decide(
                        PassPolicy.Facts.From(Material.Material),
                        new PassPolicy.Opts(),
                        uber.AssignTypes
                    );
                    //Seeded from the policy once: a rebuild after an edit keeps the ticks.
                    if (!_manualSeeded)
                    {
                        _manualSeeded = true;
                        ResetManualToPolicy();
                    }
                }

                AddCells(uber, Material.Material);
                if (uber == null || !NeedsLayer(uber))
                    return;
                Layer = MissingFeatures.Derive(Material.Material, approximate);
                if (Layer.Drawn == null)
                    return;
                Cells.Clear();
                Dropped.Clear();
                Unsupported.Clear();
                AddCells(uber, Layer.Drawn);
                Material.DrawnMaterial = Layer.Drawn;
            }
            catch (Exception ex)
            {
                Cells.Clear();
                Error = ex.Message;
                Layer = null;
                Material.DrawnMaterial = null;
            }
        }

        void AddCells(UberContext uber, BfresLibrary.Material source)
        {
            var derived = GsysShaderOptions.BuildStaticOptions(source);
            foreach (string pass in AssignTypes)
            foreach (uint weight in Weights)
            {
                GsysShaderOptions.AddDynamicOptions(derived, weight, pass);
                Cells.Add(
                    uber == null
                        ? ExistenceCell(derived, pass, weight)
                        : FullCell(uber, derived, pass, weight)
                );
            }
        }

        bool NeedsLayer(UberContext uber) =>
            uber.Game == GameKind.Spiral
            && Material.Material.ShaderAssign?.ShadingModelName == MissingFeatures.ShadingModel
            && Cells.Any(c => c.Pass == PassPolicy.Material && !c.Exists);

        VariationCell ExistenceCell(Dictionary<string, string> derived, string pass, uint weight)
        {
            var full = new Dictionary<string, string>(StringComparer.Ordinal);
            var dropped = new List<string>();
            OptionVector.Complete(Serving, derived, full, dropped);
            Note(Dropped, dropped);
            return new VariationCell
            {
                Pass = pass,
                Weight = weight,
                ExistingProgram = ShaderOptionSearcher.GetProgramIndex(Serving, full),
            };
        }

        VariationCell FullCell(
            UberContext uber,
            Dictionary<string, string> derived,
            string pass,
            uint weight
        )
        {
            var vector = OptionVector.Build(Serving, uber.Table, derived, weight, pass);
            var cell = new VariationCell
            {
                Pass = pass,
                Weight = weight,
                Vector = vector,
                ExistingProgram = ShaderOptionSearcher.GetProgramIndex(
                    Serving,
                    new Dictionary<string, string>(vector.Full)
                ),
                Refusal =
                    vector.Unsupported.Count == 0 ? null : string.Join("; ", vector.Unsupported),
                Key = new SpliceKey(vector.Hash, pass, weight),
            };
            if (cell.Refusal == null)
            {
                string kv = cell.Key.Cache(ShaderStage.Vertex);
                string kf = cell.Key.Cache(ShaderStage.Fragment);
                if (uber.Cache.Has(kv) && uber.Cache.Has(kf))
                    cell.State = CellState.Ready;
                cell.PreviewReady =
                    uber.Cache.Has(kv, preview: true) && uber.Cache.Has(kf, preview: true);
                if (cell.PreviewReady)
                    cell.PreviewState = CellState.Ready;
            }
            Note(Dropped, vector.Dropped);
            Note(Unsupported, vector.Unsupported);
            return cell;
        }

        //Every cell drops the same names, and the panel wants each said once.
        static void Note(List<string> into, IEnumerable<string> names)
        {
            foreach (string name in names)
                if (!into.Contains(name))
                    into.Add(name);
        }
    }
}
