using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using BfresEditor;
using BfresLibrary;
using Gsys;
using PlayerViewer.Core;
using PlayerViewer.Textures;
using ShaderBundler;
using ShaderLibrary;
using ShaderLibrary.Helpers;

namespace PlayerViewer.Shaders
{
    public sealed class BundleSaveReport
    {
        public string Path;

        /// <summary>Set when nothing was written. Everything else is then meaningless.</summary>
        public string Error;

        public bool WroteArchive;
        public string ArchiveFile;
        public int ArchiveBytes;
        public long FileBytes;

        public int Materials;
        public int MaterialsServed;
        public int ProgramsGenerated;
        public int ProgramsCopied;
        public int ProgramsCarried;
        public int ProgramsInArchive;
        public int ArchivesRemoved;

        public int Verified;
        public int VerifyFailed;

        /// <summary>Anything the saved file is worse for. Not fatal, but the file is not
        /// what the editor was asked for.</summary>
        public readonly List<string> Problems = new();

        public readonly List<string> Notes = new();

        public double Seconds;

        public bool Ok => Error == null;
    }

    /// <summary>
    /// Writes an edited model out, with the variations the editor generated assembled into a
    /// bfsha embedded in it.
    /// </summary>
    public static class ModelBundle
    {
        /// <summary>Suggested name for the save dialog. Never the name the file was opened
        /// under, and it keeps the input's compression.</summary>
        public static string SuggestName(string sourcePath)
        {
            string name = System.IO.Path.GetFileName(sourcePath ?? "");
            bool zs = name.EndsWith(".zs", StringComparison.OrdinalIgnoreCase);
            if (zs)
                name = name[..^3];
            if (name.EndsWith(".bfres", StringComparison.OrdinalIgnoreCase))
                name = name[..^6];
            if (name.Length == 0)
                name = "model";
            return name + "_edit.bfres" + (zs ? ".zs" : "");
        }

        /// <summary>
        /// Writes the model as edited, with a generated archive embedded for the materials
        /// whose drawn passes no longer resolve. Without a ready ubershader the edits are
        /// written as they are and the materials that lost their program are reported. A
        /// failed write undoes what the save changed on the model in memory.
        /// </summary>
        public static BundleSaveReport Save(
            BFRES bfres,
            string outPath,
            UberContext uber,
            Func<FMAT, MaterialVariations> variationsOf,
            TextureStore textures
        )
        {
            var report = new BundleSaveReport { Path = outPath };
            var watch = Stopwatch.StartNew();
            var undo = new List<Action>();
            try
            {
                if (uber != null && uber.State != UberState.Ready)
                    uber = null;
                var embedded = EmbeddedArchives(bfres);
                var template = ChooseTemplate(bfres, embedded);
                var plans = PlanMaterials(bfres, uber, variationsOf, template, report);
                var carried = new List<BundleRequest>();
                var sources = new List<Embedded>();
                if (uber != null && template.Model != null)
                    carried = CarryRows(bfres, embedded, template, plans, sources, report);

                //An archive is rebuilt when there is something new to put in it, and also when
                //one the file carries has rows nothing references any more, or is not the one
                //file the save writes.
                string fileName = template.ArchiveName + ".bfsha";
                bool rebuild =
                    plans.Count > 0
                    || sources.Count > 1
                    || sources.Any(e => e.FileName != fileName || e.Kept != e.Model.Programs.Count);

                Build build = null;
                if (rebuild && (plans.Count > 0 || carried.Count > 0))
                {
                    build = BuildArchive(plans, carried, template, report);
                    report.WroteArchive = true;
                    report.ArchiveFile = build.FileName;
                    report.ArchiveBytes = build.Bytes.Length;
                    report.ProgramsInArchive = build.ProgramCount;
                    undo.Add(SetExternalFile(bfres.ResFile, build.FileName, build.Bytes));
                }
                if (rebuild)
                    foreach (var source in sources)
                        if (build == null || source.FileName != build.FileName)
                        {
                            undo.Add(RemoveExternalFile(bfres.ResFile, source.FileName));
                            report.ArchivesRemoved++;
                            report.Notes.Add(
                                build == null
                                    ? $"{source.FileName}: no material uses it any more, removed"
                                    : $"{source.FileName}: merged into {build.FileName}"
                            );
                        }

                undo.Add(SyncGeneratedTextures(bfres, textures, report));

                bool compress = outPath.EndsWith(".zs", StringComparison.OrdinalIgnoreCase);
                report.FileBytes = Write(bfres, outPath, compress);

                Verify(outPath, build, plans, carried, uber, report);
            }
            catch (Exception ex)
            {
                report.Error = ex.Message;
                Console.WriteLine($"[Save] {outPath} failed: {ex}");
                for (int i = undo.Count - 1; i >= 0; i--)
                {
                    try
                    {
                        undo[i]?.Invoke();
                    }
                    catch (Exception undoEx)
                    {
                        Console.WriteLine($"[Save] could not undo a change: {undoEx.Message}");
                    }
                }
            }
            report.Seconds = watch.Elapsed.TotalSeconds;
            return report;
        }

        /// <summary>
        /// One material's archive, assembled in memory for the live preview. Same planning and
        /// the same assembly the save uses, scoped to the passes the viewer draws, so what the
        /// preview shows is what the saved file would contain. A cell takes its guarded splice
        /// when it has one and, if allowed, its quick one otherwise.
        ///
        /// Returns null when the material has nothing to preview: every drawn pass is served by
        /// the shipped archive already, or a splice is still missing.
        /// </summary>
        public static byte[] BuildPreview(
            FMAT mat,
            MaterialVariations v,
            UberContext uber,
            string modelName,
            bool allowQuick,
            out string archiveName,
            out string error
        )
        {
            archiveName = null;
            error = null;
            try
            {
                if (v == null || !v.Ok)
                    return null;

                var serving = Serving(mat);
                if (serving.Model == null || serving.Archive == null)
                    return null;

                var drawn = v.Cells.Where(c => DrawnPasses.IsDrawn(c.Pass)).ToList();
                if (drawn.Count == 0 || drawn.All(c => c.Exists))
                    return null;
                if (
                    drawn.Any(c =>
                        !c.Exists
                        && !Spliced(uber, c)
                        && !(allowQuick && Spliced(uber, c, preview: true))
                    )
                )
                    return null;

                var report = new BundleSaveReport();
                var plan = new Plan
                {
                    Material = mat,
                    ModelName = modelName,
                    Variations = v,
                };
                foreach (var cell in drawn)
                {
                    var emit = Resolve(
                        mat,
                        cell,
                        serving.Model,
                        serving.Model,
                        uber,
                        report,
                        allowQuick
                    );
                    if (emit != null)
                        plan.Emits.Add(emit);
                }
                if (plan.Emits.Count != drawn.Count)
                {
                    error = report.Problems.FirstOrDefault() ?? "a drawn pass could not be built";
                    return null;
                }

                var build = BuildArchive(
                    new List<Plan> { plan },
                    new List<BundleRequest>(),
                    new Template(serving.Archive, serving.Model, mat.ShaderArchive),
                    report
                );
                archiveName = build.FileName;
                return build.Bytes;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return null;
            }
        }

        //--- Planning

        sealed class Emit
        {
            public VariationCell Cell;
            public bool Generated;
            public ShaderBinary Vertex,
                Fragment;
            public int TemplateProgram = -1;
            public int BindingProgram = -1;
            public ShaderModel BindingModel;
            public ShaderModel TemplateModel;
        }

        /// <summary>The archive a build is cloned from and keyed against.</summary>
        sealed record Template(
            ShaderLibrary.BfshaFile Archive,
            ShaderModel Model,
            string ArchiveName
        );

        /// <summary>A bfsha the file itself carries, with how many of its rows survive.</summary>
        sealed class Embedded
        {
            public string FileName;
            public ShaderLibrary.BfshaFile Archive;
            public ShaderModel Model;
            public int Kept;
        }

        sealed class Plan
        {
            public FMAT Material;
            public string ModelName;
            public MaterialVariations Variations;
            public readonly List<Emit> Emits = new();
        }

        sealed class Build
        {
            public string FileName;
            public string ModelName;
            public byte[] Bytes;
            public int ProgramCount;
        }

        //The archive is picked per material by probing it for the material pass, so an
        //embedded archive that cannot serve that pass is skipped and everything else in it
        //goes unread. Both passes the viewer draws are therefore emitted for any material the
        //archive serves, whether or not the user ticked them and whether or not they had to
        //be generated.
        static bool Wanted(MaterialVariations v, string pass) =>
            DrawnPasses.IsDrawn(pass) || v.IsSelected(pass);

        /// <summary>
        /// Whether this material is a reason to embed an archive: a drawn pass no longer
        /// served by what shipped, or a pass the user ticked. With countPending a splice
        /// still to come counts too, which is what a save waits on; without it only a finished
        /// splice does, which is what a save embeds.
        /// </summary>
        internal static bool NeedsArchive(
            MaterialVariations v,
            UberContext uber,
            string name,
            BundleSaveReport report,
            bool countPending = false
        )
        {
            bool drawn = v.Cells.Any(c => DrawnPasses.IsDrawn(c.Pass) && !c.Exists);
            bool asked =
                v.Manual
                && v.Cells.Any(c =>
                    v.ManualPasses.Contains(c.Pass)
                    && (Spliced(uber, c) || (countPending && !c.Exists && c.Refusal == null))
                );
            if (!drawn && !asked)
                return false;
            if (countPending)
                return true;

            if (v.Cells.Any(c => Wanted(v, c.Pass) && Spliced(uber, c)))
                return true;

            if (drawn)
                report?.Problems.Add(
                    $"{name}: no shipped program for a pass the viewer draws, and nothing was "
                        + "compiled for it, so the saved file cannot serve it either"
                );
            return false;
        }

        static List<Plan> PlanMaterials(
            BFRES bfres,
            UberContext uber,
            Func<FMAT, MaterialVariations> variationsOf,
            Template template,
            BundleSaveReport report
        )
        {
            var plans = new List<Plan>();
            int unexamined = 0;
            string unexaminedWhy = null;

            foreach (var model in bfres.Models)
            foreach (var mat in model.Materials.OfType<FMAT>())
            {
                report.Materials++;

                var v = variationsOf(mat);
                if (v == null || !v.Ok)
                {
                    unexamined++;
                    unexaminedWhy ??= v?.Error ?? "no variation state";
                    continue;
                }

                //Without the ubershader the grid still says which shipped programs exist.
                if (uber == null)
                {
                    if (v.Cells.Any(c => DrawnPasses.IsDrawn(c.Pass) && !c.Exists))
                        report.Problems.Add(
                            $"{mat.Name}: no shipped program for a pass the viewer draws, and "
                                + "the splicer is off so nothing was generated for it"
                        );
                    continue;
                }

                if (!NeedsArchive(v, uber, mat.Name, report))
                    continue;

                var serving = Serving(mat);
                if (serving.Model == null || serving.Archive == null)
                {
                    report.Problems.Add(
                        $"{mat.Name}: needs a generated program but its shader archive is not known"
                    );
                    continue;
                }
                //A program is re-keyed against the template, so every archive involved has to
                //declare the same options. The shipped archive and anything cloned from it do.
                if (!SameOptions(template.Model, serving.Model))
                {
                    report.Problems.Add(
                        $"{mat.Name}: served by an archive of another generation than the rest "
                            + "of the file, so its variations were left out"
                    );
                    continue;
                }

                var plan = new Plan
                {
                    Material = mat,
                    ModelName = model.Name,
                    Variations = v,
                };
                foreach (var cell in v.Cells)
                {
                    if (!Wanted(v, cell.Pass))
                        continue;
                    var emit = Resolve(mat, cell, serving.Model, template.Model, uber, report);
                    if (emit != null)
                        plan.Emits.Add(emit);
                }

                if (plan.Emits.Count == 0)
                    continue;
                plans.Add(plan);
                report.MaterialsServed++;
                report.ProgramsGenerated += plan.Emits.Count(x => x.Generated);
                report.ProgramsCopied += plan.Emits.Count(x => !x.Generated);
            }

            if (unexamined > 0)
                report.Problems.Add(
                    $"{unexamined} material(s) were not checked for missing programs: {unexaminedWhy}"
                );
            if (uber == null)
                report.Notes.Add(
                    "the splicer is off: the file carries the material edits and no generated archive"
                );
            return plans;
        }

        /// <summary>
        /// Every row of the file's own archives that a material still hits, keyed by the
        /// static half of the key, so a material that resolves keeps all its passes and
        /// weights and one that no longer resolves takes them all with it. Rows a plan
        /// rebuilds are left to the plan.
        /// </summary>
        static List<BundleRequest> CarryRows(
            BFRES bfres,
            List<Embedded> embedded,
            Template template,
            List<Plan> plans,
            List<Embedded> sources,
            BundleSaveReport report
        )
        {
            var carried = new List<BundleRequest>();
            string modelName = template.Model.Name;

            var planned = new HashSet<string>(StringComparer.Ordinal);
            foreach (var plan in plans)
            foreach (var emit in plan.Emits)
            {
                var key = ShaderOptionSearcher.WriteOptionKeys(
                    template.Model,
                    new Dictionary<string, string>(emit.Cell.Vector.Full)
                );
                if (key != null)
                    planned.Add(KeyRows.Signature(key));
            }

            var materials = bfres
                .Models.SelectMany(m => m.Materials.OfType<FMAT>())
                .Where(m => m.Material.ShaderAssign?.ShadingModelName == modelName)
                .ToList();

            foreach (var source in embedded)
            {
                if (
                    source.Archive.ShaderModels.Count != 1
                    || !source.Archive.ShaderModels.ContainsKey(modelName)
                )
                {
                    report.Notes.Add(
                        $"{source.FileName}: not a '{modelName}' archive, left as it is"
                    );
                    continue;
                }
                source.Model = source.Archive.ShaderModels[modelName];
                if (!SameOptions(template.Model, source.Model))
                {
                    report.Notes.Add(
                        $"{source.FileName}: another archive generation, left as it is"
                    );
                    continue;
                }
                sources.Add(source);

                var hit = new HashSet<string>(StringComparer.Ordinal);
                foreach (var mat in materials)
                {
                    var full = new Dictionary<string, string>();
                    OptionVector.Complete(
                        source.Model,
                        GsysShaderOptions.BuildStaticOptions(mat.Material),
                        full,
                        null
                    );
                    string sig = KeyRows.StaticSignature(
                        source.Model,
                        ShaderOptionSearcher.WriteOptionKeys(source.Model, full)
                    );
                    if (sig != null)
                        hit.Add(sig);
                }

                for (int i = 0; i < source.Model.Programs.Count; i++)
                {
                    if (!hit.Contains(KeyRows.StaticSignature(source.Model, i)))
                        continue;
                    source.Kept++;

                    string label = $"{source.FileName} program {i}";
                    var options = KeyRows.ReadOptions(source.Model, i);
                    var key =
                        options == null
                            ? null
                            : ShaderOptionSearcher.WriteOptionKeys(template.Model, options);
                    if (key == null)
                    {
                        report.Problems.Add(
                            $"{label}: its key row could not be read back, dropped"
                        );
                        continue;
                    }
                    if (planned.Contains(KeyRows.Signature(key)))
                        continue;

                    var bp = source.Model.GetVariation(i)?.BinaryProgram;
                    var vertex = ShaderBinary.From(bp?.VertexShader);
                    var fragment = ShaderBinary.From(bp?.FragmentShader);
                    if (vertex == null || fragment == null)
                    {
                        report.Problems.Add($"{label}: has no usable binaries, dropped");
                        continue;
                    }

                    var req = new BundleRequest
                    {
                        Label = label,
                        KeyOptions = options,
                        TemplateProgramIndex = i,
                        TemplateModel = source.Model,
                        BindingProgramIndex = i,
                        BindingModel = source.Model,
                    };
                    req.Binaries[ShaderStage.Vertex] = vertex;
                    req.Binaries[ShaderStage.Fragment] = fragment;
                    carried.Add(req);
                }
            }

            report.ProgramsCarried = carried.Count;
            return carried;
        }

        static Emit Resolve(
            FMAT mat,
            VariationCell cell,
            ShaderModel serving,
            ShaderModel template,
            UberContext uber,
            BundleSaveReport report,
            bool allowQuick = false
        )
        {
            string what = $"{mat.Name} {DrawnPasses.Short(cell.Pass)} w{cell.Weight}";

            if (cell.Exists)
            {
                var bp = serving.GetVariation(cell.ExistingProgram)?.BinaryProgram;
                var vertex = ShaderBinary.From(bp?.VertexShader);
                var fragment = ShaderBinary.From(bp?.FragmentShader);
                if (vertex == null || fragment == null)
                {
                    report.Problems.Add($"{what}: the shipped program has no usable binaries");
                    return null;
                }
                //Copied so the material is whole in the one archive the engine picks for it.
                return new Emit
                {
                    Cell = cell,
                    Vertex = vertex,
                    Fragment = fragment,
                    TemplateProgram = cell.ExistingProgram,
                    TemplateModel = serving,
                    BindingProgram = cell.ExistingProgram,
                    BindingModel = serving,
                };
            }

            if (cell.Refusal != null)
            {
                report.Problems.Add($"{what}: cannot be expressed ({cell.Refusal})");
                return null;
            }

            //The guarded splice when it is there, else the quick one when allowed.
            bool quick = !Spliced(uber, cell) && allowQuick;
            var cache = uber.Cache;
            if (
                !cache.TryGet(cell.Key.Cache(ShaderStage.Vertex), out var cv, quick)
                || !cache.TryGet(cell.Key.Cache(ShaderStage.Fragment), out var cf, quick)
            )
            {
                //The passes the viewer draws break the material when they are missing. The
                //rest are as absent from the saved archive as from the shipped one.
                string line = $"{what}: not compiled, so the saved file has no program for it";
                if (DrawnPasses.IsDrawn(cell.Pass))
                    report.Problems.Add(line);
                else
                    report.Notes.Add(line);
                return null;
            }

            var selection = uber.Resolve(cell.Pass, cell.Weight.ToString());
            return new Emit
            {
                Cell = cell,
                Generated = true,
                Vertex = cv,
                Fragment = cf,
                TemplateProgram = UberSelect.FindProgram(
                    template,
                    cell.Pass,
                    cell.Weight.ToString(),
                    exact: false
                ),
                BindingProgram = selection.ProgramIndex,
                BindingModel = uber.Model,
            };
        }

        //The save path never passes preview: an unguarded program must not reach a file.
        static bool Spliced(UberContext uber, VariationCell cell, bool preview = false) =>
            !cell.Exists
            && cell.Refusal == null
            && uber.Cache.Has(cell.Key.Cache(ShaderStage.Vertex), preview)
            && uber.Cache.Has(cell.Key.Cache(ShaderStage.Fragment), preview);

        //--- Assembly

        static Build BuildArchive(
            List<Plan> plans,
            List<BundleRequest> carried,
            Template template,
            BundleSaveReport report
        )
        {
            var serving = template;
            string archiveName = template.ArchiveName;
            string modelName = template.Model.Name;

            var requests = new List<BundleRequest>(carried);
            foreach (var plan in plans)
            foreach (var emit in plan.Emits)
            {
                var req = new BundleRequest
                {
                    Label =
                        $"{plan.Material.Name} {DrawnPasses.Short(emit.Cell.Pass)} w{emit.Cell.Weight}",
                    KeyOptions = emit.Cell.Vector.Full,
                    TemplateProgramIndex = emit.TemplateProgram,
                    TemplateModel = emit.TemplateModel,
                    BindingProgramIndex = emit.BindingProgram,
                    BindingModel = emit.BindingModel,
                };
                req.Binaries[ShaderStage.Vertex] = emit.Vertex;
                req.Binaries[ShaderStage.Fragment] = emit.Fragment;
                requests.Add(req);
            }

            var builder = new BfshaBundleBuilder(
                serving.Archive,
                modelName,
                new BuildSettings
                {
                    ArchiveName = archiveName,
                    OptionModel = serving.Model,
                    BindingModel = serving.Model,
                    DefaultProgramIndex = -1,
                }
            );
            var built = builder.Build(requests);
            report.Notes.AddRange(built.Notes);

            using var ms = new MemoryStream();
            built.Bfsha.Save(ms);
            return new Build
            {
                FileName = archiveName + ".bfsha",
                ModelName = modelName,
                Bytes = ms.ToArray(),
                ProgramCount = built.Model.Programs.Count,
            };
        }

        /// <summary>
        /// One texture per <see cref="TextureImport.Generated"/> name a slot still carries,
        /// and none for a name nothing carries any more. Goes through the store so the loaded
        /// model sees what the file has.
        /// </summary>
        static Action SyncGeneratedTextures(
            BFRES bfres,
            TextureStore textures,
            BundleSaveReport report
        )
        {
            var refs = bfres
                .Models.SelectMany(m => m.Materials.OfType<FMAT>())
                .SelectMany(m => m.Material.TextureRefs)
                .Select(t => t.Name)
                .Where(TextureImport.IsGenerated)
                .GroupBy(x => x)
                .ToDictionary(g => g.Key, g => g.Count());

            var bntx = textures?.Bntx;
            if (bntx == null)
            {
                if (refs.Count > 0)
                    report.Problems.Add(
                        $"{refs.Values.Sum()} sampler(s) have no texture and this model carries "
                            + "no texture container to generate one in"
                    );
                return null;
            }

            var undo = new List<Action>();
            foreach (var (name, _) in TextureImport.Generated)
            {
                bool carried = bntx.Textures.Any(x => x.Name == name);
                if (refs.TryGetValue(name, out int slots))
                {
                    if (!carried)
                    {
                        textures.Install(
                            TextureImport.Generate(name, bntx.Textures.FirstOrDefault())
                        );
                        undo.Add(() => textures.Delete(name));
                    }
                    report.Notes.Add(
                        $"{slots} sampler(s) left without a texture are bound to a generated {name}"
                    );
                }
                else if (carried)
                {
                    var kept = bntx.Textures.First(x => x.Name == name);
                    textures.Delete(name);
                    undo.Add(() => textures.Install(kept));
                    report.Notes.Add($"removed {name}, which nothing binds any more");
                }
            }
            return () =>
            {
                for (int i = undo.Count - 1; i >= 0; i--)
                    undo[i]();
            };
        }

        //Returns what puts the previous entry back.
        static Action SetExternalFile(ResFile res, string name, byte[] data)
        {
            var file = new ExternalFile { Name = name, Data = data };
            if (res.ExternalFiles.ContainsKey(name))
            {
                var previous = res.ExternalFiles[name];
                res.ExternalFiles[name] = file;
                return () => res.ExternalFiles[name] = previous;
            }
            res.ExternalFiles.Add(name, file);
            return () => res.ExternalFiles.RemoveKey(name);
        }

        //Written beside the target and swapped in, so a failed write cannot truncate the file
        //that was opened.
        static long Write(BFRES bfres, string outPath, bool compress)
        {
            byte[] data;
            using (var ms = new MemoryStream())
            {
                bfres.Save(ms);
                data = ms.ToArray();
            }
            if (compress)
            {
                using var compressor = new ZstdSharp.Compressor(12);
                compressor.SetParameter(
                    ZstdSharp.Unsafe.ZSTD_cParameter.ZSTD_c_nbWorkers,
                    Math.Clamp(Environment.ProcessorCount / 2, 1, 4)
                );
                data = compressor.Wrap(data).ToArray();
            }

            string dir = System.IO.Path.GetDirectoryName(outPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            AtomicFile.Write(outPath, data);
            return data.LongLength;
        }

        //--- Verification, from the bytes on disk rather than from what was just in memory

        static void Verify(
            string outPath,
            Build build,
            List<Plan> plans,
            List<BundleRequest> carried,
            UberContext uber,
            BundleSaveReport report
        )
        {
            var raw = Romfs.Decompress(File.ReadAllBytes(outPath));
            var res = new ResFile(new MemoryStream(raw));

            if (build == null)
            {
                report.Notes.Add(
                    $"reloaded: {res.Models.Count} model(s), no generated archive to check"
                );
                return;
            }

            if (!res.ExternalFiles.ContainsKey(build.FileName))
            {
                report.Problems.Add($"the saved file carries no {build.FileName}");
                return;
            }

            var archive = new ShaderLibrary.BfshaFile(
                new MemoryStream(res.ExternalFiles[build.FileName].Data)
            );
            if (!archive.ShaderModels.ContainsKey(build.ModelName))
            {
                report.Problems.Add($"the saved archive has no '{build.ModelName}' shader model");
                return;
            }
            var model = archive.ShaderModels[build.ModelName];
            foreach (var problem in KeyOrder.Check(model, "the saved archive"))
                report.Problems.Add(problem);

            foreach (var req in carried)
            {
                var options = new Dictionary<string, string>(req.KeyOptions);
                int hashed = ShaderOptionSearcher.GetProgramIndex(model, options);
                var key = ShaderOptionSearcher.WriteOptionKeys(model, options);
                int engine = key == null ? -1 : KeyOrder.EngineFindProgram(model, key, out _);
                if (hashed >= 0 && engine == hashed)
                    report.Verified++;
                else
                {
                    report.VerifyFailed++;
                    report.Problems.Add(
                        $"{req.Label}: the saved archive does not serve it "
                            + $"(lookup {hashed}, range search {engine})"
                    );
                }
            }

            foreach (var plan in plans)
            {
                var saved = Find(res, plan.ModelName, plan.Material.Name);
                if (saved == null)
                {
                    report.Problems.Add($"{plan.Material.Name}: absent from the saved file");
                    report.VerifyFailed += plan.Emits.Count;
                    continue;
                }

                //Derived from the material as it came back off disk, so this checks the
                //bfres write path as well as the archive.
                var derived = GsysShaderOptions.BuildStaticOptions(saved);
                foreach (var emit in plan.Emits)
                {
                    var cell = emit.Cell;
                    GsysShaderOptions.AddDynamicOptions(derived, cell.Weight, cell.Pass);
                    var vector = OptionVector.Build(
                        model,
                        uber.Table,
                        derived,
                        cell.Weight,
                        cell.Pass
                    );
                    var options = new Dictionary<string, string>(vector.Full);

                    int hashed = ShaderOptionSearcher.GetProgramIndex(model, options);
                    var key = ShaderOptionSearcher.WriteOptionKeys(model, options);
                    int engine = key == null ? -1 : KeyOrder.EngineFindProgram(model, key, out _);

                    if (hashed >= 0 && engine == hashed)
                        report.Verified++;
                    else
                    {
                        report.VerifyFailed++;
                        report.Problems.Add(
                            $"{plan.Material.Name} {DrawnPasses.Short(cell.Pass)} w{cell.Weight}: the saved "
                                + $"archive does not serve it (lookup {hashed}, range search {engine})"
                        );
                    }
                }
            }
        }

        static Material Find(ResFile res, string modelName, string materialName)
        {
            if (!res.Models.ContainsKey(modelName))
                return null;
            var model = res.Models[modelName];
            return model.Materials.ContainsKey(materialName) ? model.Materials[materialName] : null;
        }

        //--- Helpers

        /// <summary>Every bfsha the file carries, in file order.</summary>
        static List<Embedded> EmbeddedArchives(BFRES bfres)
        {
            var found = new List<Embedded>();
            bfres.UpdateExternalShaderFiles();
            int next = 0;
            foreach (var name in bfres.ResFile.ExternalFiles.Keys.ToList())
            {
                if (!name.EndsWith(".bfsha") && !name.EndsWith(".sharcfb"))
                    continue;
                object file = next < bfres.ShaderFiles.Count ? bfres.ShaderFiles[next] : null;
                next++;
                if (file is BfshaLibrary.BfshaFile bfsha && name.EndsWith(".bfsha"))
                    found.Add(new Embedded { FileName = name, Archive = bfsha.Inner });
            }
            return found;
        }

        /// <summary>
        /// The archive the build is cloned from: what serves the file's materials from
        /// outside it when anything does, else the file's own archive. Independent of what
        /// is being planned, so a file with nothing new to embed is re-keyed against the same
        /// archive as one with.
        /// </summary>
        static Template ChooseTemplate(BFRES bfres, List<Embedded> embedded)
        {
            Template own = null;
            foreach (var mat in bfres.Models.SelectMany(m => m.Materials.OfType<FMAT>()))
            {
                var serving = Serving(mat);
                if (serving.Model == null || serving.Archive == null)
                    continue;
                var template = new Template(serving.Archive, serving.Model, mat.ShaderArchive);
                if (!embedded.Any(e => ReferenceEquals(e.Archive, serving.Archive)))
                    return template;
                own ??= template;
            }
            return own ?? new Template(null, null, null);
        }

        static bool SameOptions(ShaderModel a, ShaderModel b)
        {
            if (ReferenceEquals(a, b))
                return true;
            if (a == null || b == null || a.Name != b.Name)
                return false;
            if (
                a.StaticOptions.Count != b.StaticOptions.Count
                || a.DynamicOptions.Count != b.DynamicOptions.Count
            )
                return false;
            for (int i = 0; i < a.StaticOptions.Count; i++)
                if (a.StaticOptions[i].Name != b.StaticOptions[i].Name)
                    return false;
            for (int i = 0; i < a.DynamicOptions.Count; i++)
                if (a.DynamicOptions[i].Name != b.DynamicOptions[i].Name)
                    return false;
            return true;
        }

        static Action RemoveExternalFile(ResFile res, string name)
        {
            if (!res.ExternalFiles.ContainsKey(name))
                return null;
            var previous = res.ExternalFiles[name];
            res.ExternalFiles.RemoveKey(name);
            return () => res.ExternalFiles.Add(name, previous);
        }

        static (ShaderLibrary.BfshaFile Archive, ShaderModel Model) Serving(FMAT mat)
        {
            var renderer = mat.MaterialAsset as BfshaRenderer;
            return (renderer?.BaseShaderArchiveFile?.Inner, renderer?.BaseShaderModel?.Inner);
        }
    }
}
