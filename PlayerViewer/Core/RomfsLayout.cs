using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PlayerViewer.Core
{
    /// <summary>
    /// How a game lays out its romfs: where its model and animation files are, the platform name
    /// its packs and loose files carry, and what it keeps outside a model's bfres.
    /// </summary>
    public abstract class RomfsLayout
    {
        protected readonly Romfs Romfs;

        protected RomfsLayout(Romfs romfs) => Romfs = romfs;

        public static RomfsLayout For(Romfs romfs) =>
            romfs.Game == GameKind.Spiral ? new SpiralLayout(romfs) : new ThunderLayout(romfs);

        /// <summary>Whether a folder is the game's romfs.</summary>
        public static bool IsRoot(string root, GameKind game) =>
            game == GameKind.Spiral ? SpiralLayout.IsRoot(root) : ThunderLayout.IsRoot(root);

        /// <summary>The game's romfs a chosen folder names, or null.</summary>
        public static string FindRoot(string path, GameKind game) =>
            game == GameKind.Spiral ? SpiralLayout.FindRoot(path)
            : ThunderLayout.IsRoot(path) ? path
            : null;

        /// <summary>The platform part of pack, shader and loose file names.</summary>
        public abstract string Platform { get; }

        /// <summary>Whether a model keeps its textures and animations in files beside its bfres.</summary>
        public abstract bool SplitsModelFiles { get; }

        /// <summary>Whether a ClothList cloth may live outside the pack, its platform in the name.</summary>
        public abstract bool KeepsClothLoose { get; }

        /// <summary>Model bfres by name, decompressed, or null.</summary>
        public abstract byte[] ReadModel(string modelName);

        public abstract bool ModelExists(string modelName);

        /// <summary>The animation file of a model archive, or null when models keep their own.</summary>
        public abstract byte[] ReadAnimArchive(string archive);

        /// <summary>The archive a model is in, or null.</summary>
        public abstract string ArchiveOf(string modelName);

        /// <summary>The model file a model's animations are read from.</summary>
        public abstract string AnimFileOf(string modelName);

        /// <summary>Model names starting with a prefix, sorted.</summary>
        public abstract List<string> ListModelFiles(string prefix);

        /// <summary>The player cutscene animation files, such as <c>D4_02_PL</c>: one per
        /// cutscene, bound by bone name to whichever player body the event puts in it.</summary>
        public abstract List<string> CutsceneFiles();
    }

    /// <summary>Splatoon 3: one bfres per model, named by the model, textures and animations inside.</summary>
    sealed class ThunderLayout : RomfsLayout
    {
        public const string PlatformName = "Nin_NX_NVN";

        public ThunderLayout(Romfs romfs)
            : base(romfs) { }

        public static bool IsRoot(string root) =>
            !string.IsNullOrEmpty(root)
            && Directory.Exists(Path.Combine(root, "Model"))
            && Directory.Exists(Path.Combine(root, "RSDB"))
            && File.Exists(Path.Combine(root, "Model", "Player00.bfres.zs"));

        public override string Platform => PlatformName;

        public override bool SplitsModelFiles => false;

        public override bool KeepsClothLoose => false;

        public override byte[] ReadModel(string modelName) =>
            Romfs.ReadFile($"Model/{modelName}.bfres");

        public override bool ModelExists(string modelName) =>
            Romfs.FileExists($"Model/{modelName}.bfres");

        public override byte[] ReadAnimArchive(string archive) => null;

        public override string ArchiveOf(string modelName) => null;

        public override string AnimFileOf(string modelName) => modelName;

        public override List<string> ListModelFiles(string prefix)
        {
            var results = new List<string>();
            foreach (var file in Romfs.FindFiles("Model", prefix + "*.bfres.zs"))
            {
                string name = Path.GetFileName(file);
                results.Add(name.Substring(0, name.Length - ".bfres.zs".Length));
            }
            results.Sort(StringComparer.OrdinalIgnoreCase);
            return results;
        }

        public override List<string> CutsceneFiles() => new List<string>();
    }

    /// <summary>
    /// Splatoon Raiders: model files named archive.model, the archive's animations in
    /// archive.anim, textures in <c>Tex</c>, and cloth beside the packs.
    /// </summary>
    sealed class SpiralLayout : RomfsLayout
    {
        public const string PlatformName = "Nin_Ounce_NVN2";

        Dictionary<string, string> _models;
        readonly object _modelsLock = new();

        public SpiralLayout(Romfs romfs)
            : base(romfs) { }

        /// <summary>A Raiders romfs: archive.model bfres names and NVN2 shaders.</summary>
        public static bool IsRoot(string root)
        {
            try
            {
                return !string.IsNullOrEmpty(root)
                    && Directory.Exists(Path.Combine(root, "RSDB"))
                    && File.Exists(Path.Combine(root, "Model", "Player00.Player00.bfres.zs"))
                    && Directory
                        .EnumerateFiles(
                            Path.Combine(root, "Shader"),
                            $"Hoian_UBER.*{PlatformName}.bfsha*"
                        )
                        .Any();
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// The romfs a chosen folder names: the romfs itself, or the dump above it, whose newest
        /// update carries the whole tree. Null when there is none.
        /// </summary>
        public static string FindRoot(string path)
        {
            if (string.IsNullOrEmpty(path))
                return null;
            var candidates = new List<string> { path, Path.Combine(path, "romfs") };
            try
            {
                string updates = Path.Combine(path, "updates");
                if (Directory.Exists(updates))
                    candidates.AddRange(
                        Directory
                            .EnumerateDirectories(updates)
                            .OrderByDescending(d =>
                                long.TryParse(Path.GetFileName(d), out long v) ? v : -1
                            )
                            .Select(d => Path.Combine(d, "romfs"))
                    );
            }
            catch (Exception) { }
            candidates.Add(Path.Combine(path, "base", "romfs"));
            return candidates.FirstOrDefault(IsRoot);
        }

        public override string Platform => PlatformName;

        public override bool SplitsModelFiles => true;

        public override bool KeepsClothLoose => true;

        //A plain name finds the model in whichever archive holds it; "archive.model" or
        //"archive.anim" names the file.
        public override byte[] ReadModel(string modelName)
        {
            string path = ModelPath(modelName);
            return path != null ? Romfs.Decompress(File.ReadAllBytes(path)) : null;
        }

        public override bool ModelExists(string modelName) => ModelPath(modelName) != null;

        public override byte[] ReadAnimArchive(string archive) => ReadModel(archive + ".anim");

        public override string ArchiveOf(string modelName)
        {
            if (ModelPath(modelName) is not string path)
                return null;
            string file = Path.GetFileName(path);
            return file.Substring(0, file.IndexOf('.'));
        }

        public override string AnimFileOf(string modelName) =>
            (ArchiveOf(modelName) ?? modelName) + ".anim";

        public override List<string> ListModelFiles(string prefix)
        {
            var results = Index()
                .Keys.Where(k =>
                    !k.Contains('.') && k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                )
                .ToList();
            results.Sort(StringComparer.OrdinalIgnoreCase);
            return results;
        }

        public override List<string> CutsceneFiles() =>
            Romfs
                .FindFiles("Model", "*_PL.anim.bfres.zs")
                .Select(f => Path.GetFileName(f).Split('.')[0])
                .Distinct()
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToList();

        string ModelPath(string name) =>
            string.IsNullOrEmpty(name) ? null : Index().GetValueOrDefault(name);

        Dictionary<string, string> Index()
        {
            lock (_modelsLock)
                return _models ??= IndexModels();
        }

        //Model file names: "archive.model.bfres.zs", the animations "archive.anim.bfres.zs".
        Dictionary<string, string> IndexModels()
        {
            var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var byModel = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in Romfs.FindFiles("Model", "*.bfres.zs"))
            {
                string stem = Path.GetFileName(file)[..^".bfres.zs".Length];
                index[stem] = file;
                int dot = stem.IndexOf('.');
                if (dot <= 0)
                    continue;
                string archive = stem[..dot],
                    model = stem[(dot + 1)..];
                if (model == "anim")
                    continue;
                //A model named after its archive wins a name two archives share.
                if (!byModel.ContainsKey(model) || archive == model)
                    byModel[model] = file;
            }
            foreach (var (model, file) in byModel)
                index.TryAdd(model, file);
            return index;
        }
    }
}
