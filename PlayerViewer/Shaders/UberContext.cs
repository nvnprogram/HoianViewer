using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
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
        readonly object _gate = new();

        //Serving archive to why it cannot be paired with this ubershader, null when it can.
        readonly Dictionary<ShaderModel, string> _generation = new();

        Task _load;
        UberState _state = UberState.Idle;
        string _error;

        public UberContext(Romfs romfs)
        {
            _romfs = romfs ?? throw new ArgumentNullException(nameof(romfs));
            Cache = new UberSliceCache(AppPaths.UberSliceCacheDir, BundleStamp.Codegen);
        }

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
        public int Codegen => BundleStamp.Codegen;

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

        /// <summary>Starts the load if it has not started. Safe to call every frame.</summary>
        public void Ensure()
        {
            lock (_gate)
            {
                if (_load != null)
                    return;
                _state = UberState.Loading;
                _load = Task.Run(Load);
            }
        }

        void Load()
        {
            try
            {
                string sarc =
                    _romfs.Resolve("Shader/" + UberArchive.SarcName + ".zs")
                    ?? _romfs.Resolve("Shader/" + UberArchive.SarcName);
                if (sarc == null)
                    throw new FileNotFoundException(
                        $"no Shader/{UberArchive.SarcName} in the romfs; the ubershader lives "
                            + "inside it and nothing ships as data."
                    );

                var archive = UberArchive.LoadFile(sarc);
                var model = archive.ShaderModels[UberArchive.ModelName];
                if (model == null)
                    throw new InvalidOperationException(
                        $"{UberArchive.EntryName} has no '{UberArchive.ModelName}' shader model."
                    );

                var table = UberOptionTable.Build(model);
                var assignTypes = UberSelect.AssignTypes(model);
                string json = table.ToJson();

                lock (_gate)
                {
                    Model = model;
                    Table = table;
                    TableJson = json;
                    AssignTypes = assignTypes;
                    _state = UberState.Ready;
                }
                Console.WriteLine(
                    $"[Uber] {UberArchive.ModelName}: {model.Programs.Count} programs, "
                        + $"{assignTypes.Count} assign types, {table.Rows.Count} options"
                );
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
