using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using BfresEditor;
using PlayerViewer.Core;
using ShaderBundler;
using ShaderLibrary;

namespace PlayerViewer.Shaders
{
    public enum UberState
    {
        Idle,
        Loading,
        Ready,
        Failed,
    }

    /// <summary>
    /// The ubershader side of the variation pipeline: the archive a specialisation is spliced
    /// from, the option table that drives the specialiser, the cache of finished splices and
    /// the native tool itself.
    /// </summary>
    public sealed class UberContext
    {
        readonly Romfs _romfs;
        readonly SplicePlatform _platform;
        readonly object _gate = new();

        //Serving archive to why it cannot be paired with this ubershader, null when it can.
        readonly Dictionary<ShaderModel, string> _generation = new();

        UberReach _reach;
        ISet<string> _noEffect;
        int _reachVersion;

        Task _load;
        UberState _state = UberState.Idle;
        string _error;

        public UberContext(Romfs romfs)
        {
            _romfs = romfs ?? throw new ArgumentNullException(nameof(romfs));
            _platform = romfs.Game == GameKind.Spiral ? SplicePlatform.Nvn2 : SplicePlatform.Nvn;
            Cache = new UberSliceCache(
                AppPaths.CacheDir(UberSliceCache.RootName),
                _platform.SliceCacheName,
                _platform.Codegen
            );
        }

        /// <summary>Where this game keeps its ubershader.</summary>
        public UberSource Source => _platform.Uber;

        public GameKind Game => _romfs.Game;

        public UberState State
        {
            get
            {
                lock (_gate)
                    return _state;
            }
        }

        public string Error
        {
            get
            {
                lock (_gate)
                    return _error;
            }
        }

        public ShaderModel Model { get; private set; }
        public UberOptionTable Table { get; private set; }
        public string TableJson { get; private set; }

        /// <summary>The gsys_assign_type choices the ubershader declares, in archive order.
        /// Nothing outside this set can ever be generated.</summary>
        public IReadOnlyList<string> AssignTypes { get; private set; } = Array.Empty<string>();

        public UberSliceCache Cache { get; }

        /// <summary>The specialiser beside the exe, or null. Everything except compiling
        /// works without it.</summary>
        public string SpecialiserPath { get; } =
            UberspecRunner.FindExecutable(AppContext.BaseDirectory);

        /// <summary>The codegen version that specialiser stamps, or
        /// <see cref="BundleStamp.Unstamped"/> when there is none.</summary>
        public int Codegen => _platform.Codegen;

        HashSet<ulong> _codeHashes;

        /// <summary>
        /// The code hash of every ubershader program, built on first use
        /// Needed to find programs our old splicer generated before stamps were added
        /// </summary>
        public ISet<ulong> CodeHashes
        {
            get
            {
                lock (_gate)
                {
                    if (_codeHashes != null || Model?.BnshFile == null)
                        return _codeHashes;
                    var set = new HashSet<ulong>();
                    foreach (var variation in Model.BnshFile.Variations)
                    {
                        var bp = variation.BinaryProgram;
                        foreach (var code in new[] { bp?.VertexShader, bp?.FragmentShader })
                            if (code?.ControlCode != null)
                                set.Add(BundleStamp.CodeHash(code.ControlCode));
                    }
                    _codeHashes = set;
                    return _codeHashes;
                }
            }
        }

        public bool CanCompile => State == UberState.Ready && SpecialiserPath != null;

        /// <summary>What the ubershader can express, measured off its code. Raiders only, and
        /// null until measured.</summary>
        public UberReach Reach
        {
            get
            {
                lock (_gate)
                    return _reach;
            }
        }

        /// <summary>Changes when <see cref="Reach"/> does, so grids built before it can be
        /// rebuilt.</summary>
        public int ReachVersion
        {
            get
            {
                lock (_gate)
                    return _reachVersion;
            }
        }

        /// <summary>
        /// The options a splice cannot honor as it is unread by the ubershader
        /// </summary>
        public ISet<string> NoEffect
        {
            get
            {
                lock (_gate)
                    return _noEffect ?? EmptySet;
            }
        }

        static readonly ISet<string> EmptySet = new HashSet<string>();

        /// <summary>Starts the load if it has not started. Safe to call every frame.</summary>
        public void Ensure()
        {
            lock (_gate)
            {
                if (_load != null)
                    return;
                _state = UberState.Loading;
                //The reach is measured by translating the ubershader, which needs the GL
                //capabilities read on this thread first.
                if (_platform.ReportsSpliceLoss)
                    TegraShaderTranslator.InitCaps();
                _load = Task.Run(Load);
            }
        }

        void Load()
        {
            try
            {
                var src = Source;
                string sarc =
                    _romfs.Resolve("Shader/" + src.SarcName + ".zs")
                    ?? _romfs.Resolve("Shader/" + src.SarcName);
                if (sarc == null)
                    throw new FileNotFoundException(
                        $"no Shader/{src.SarcName} in the romfs; the ubershader lives "
                            + "inside it and nothing ships as data."
                    );

                var archive = UberArchive.LoadFile(sarc, src.EntryName);
                var model = archive.ShaderModels.ContainsKey(src.ModelName)
                    ? archive.ShaderModels[src.ModelName]
                    : null;
                if (model == null)
                    throw new InvalidOperationException(
                        $"{src.EntryName} has no '{src.ModelName}' shader model."
                    );

                var table = UberOptionTable.Build(model);
                var assignTypes = UberSelect.AssignTypes(model);
                string json = table.ToJson();

                //The reach is measured on this thread after the model is published. The programs
                //load lazily from one shared stream, so they are all loaded before that.
                if (_platform.ReportsSpliceLoss && model.BnshFile != null)
                    foreach (var variation in model.BnshFile.Variations)
                        _ = variation.BinaryProgram;

                lock (_gate)
                {
                    Model = model;
                    Table = table;
                    TableJson = json;
                    AssignTypes = assignTypes;
                    _state = UberState.Ready;
                }
                Console.WriteLine(
                    $"[Uber] {src.EntryName} {src.ModelName}: {model.Programs.Count} programs, "
                        + $"{assignTypes.Count} assign types, {table.Rows.Count} options"
                );
                if (_platform.ReportsSpliceLoss)
                    MeasureReach(model);
            }
            catch (Exception ex)
            {
                lock (_gate)
                {
                    _error = ex.Message;
                    _state = UberState.Failed;
                }
                Console.WriteLine($"[Uber] load failed: {ex}");
            }
        }

        //Read back from the last measurement of the same ubershader, else measured and kept.
        void MeasureReach(ShaderModel model)
        {
            var thread = Thread.CurrentThread;
            var priority = thread.Priority;
            try
            {
                thread.Priority = ThreadPriority.BelowNormal;
                string path = AppPaths.RaidersUberReachPath;
                string fingerprint = UberReach.FingerprintOf(model);
                UberReach reach =
                    Bundled(fingerprint)
                    ?? (File.Exists(path) ? UberReach.Parse(File.ReadAllText(path), fingerprint) : null);
                if (reach == null)
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    reach = UberReach.Measure(
                        model,
                        p =>
                        {
                            var bp = model.GetVariation(p).BinaryProgram;
                            return TegraShaderTranslator.TranslatePair(
                                bp.VertexShader.ByteCode,
                                NvnPlatform.EmbeddedConstants(
                                    bp.VertexShader.ControlCode,
                                    bp.VertexShader.ByteCode
                                ),
                                bp.FragmentShader.ByteCode,
                                NvnPlatform.EmbeddedConstants(
                                    bp.FragmentShader.ControlCode,
                                    bp.FragmentShader.ByteCode
                                ),
                                GpuIsa.Ampere
                            );
                        }
                    );
                    if (reach == null)
                    {
                        Console.WriteLine(
                            "[Uber] reach: the option block is indexed at run time, so what a splice loses is not known"
                        );
                        return;
                    }
                    File.WriteAllText(path, reach.ToText());
                    Console.WriteLine($"[Uber] reach measured in {sw.Elapsed.TotalSeconds:F0} s");
                }
                var noEffect = new HashSet<string>(reach.Unread, StringComparer.Ordinal);
                noEffect.ExceptWith(ProductInert(reach));
                lock (_gate)
                {
                    _reach = reach;
                    _noEffect = noEffect;
                    _reachVersion++;
                }
                Console.WriteLine(
                    $"[Uber] reach: {reach.Unread.Count} options unread ({noEffect.Count} that matter), "
                        + $"{reach.UnboundSamplers.Count} samplers bound by no program"
                );
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Uber] reach failed: {ex}");
            }
            finally
            {
                thread.Priority = priority;
            }
        }

        //The measurement of the stock ubershader ships with the viewer; any other is measured.
        static UberReach Bundled(string fingerprint)
        {
            using var stream = typeof(UberContext).Assembly.GetManifestResourceStream(
                "PlayerViewer.Shaders.Resources.UberReachSpiral.txt"
            );
            if (stream == null)
                return null;
            using var reader = new StreamReader(stream);
            return UberReach.Parse(reader.ReadToEnd().Replace("\r\n", "\n"), fingerprint);
        }

        //Unread options the product's programs show to change nothing. Its own copy, since the
        //renderer reads its cached one on the GL thread and programs load from a shared stream.
        HashSet<string> ProductInert(UberReach reach)
        {
            var bytes = _romfs.ReadProduct("Shader", "Hoian_UBER", "product.*.bfsha*");
            if (bytes == null)
                return new HashSet<string>();
            var product = new BfshaFile(new MemoryStream(bytes));
            return product.ShaderModels.ContainsKey(Source.ModelName)
                ? reach.Inert(product.ShaderModels[Source.ModelName])
                : new HashSet<string>();
        }

        /// <summary>
        /// Why the archive serving a material cannot be paired with this ubershader, or null
        /// when it can.
        /// </summary>
        public string GenerationProblem(ShaderModel serving)
        {
            lock (_gate)
            {
                if (Model == null)
                    return "the ubershader is not loaded";
                if (_generation.TryGetValue(serving, out string cached))
                    return cached;

                string problem = null;
                try
                {
                    UberOptionTable.Build(serving).RequireSameGeneration(Model);
                }
                catch (Exception ex)
                {
                    problem = ex.Message;
                }
                _generation[serving] = problem;
                return problem;
            }
        }

        /// <summary>The ubershader grid cell a splice starts from. Serialised because worker
        /// threads share one archive.</summary>
        public UberSelect.Selection Resolve(string assignType, string weight)
        {
            lock (_gate)
                return UberSelect.Resolve(Model, assignType, weight);
        }
    }
}
