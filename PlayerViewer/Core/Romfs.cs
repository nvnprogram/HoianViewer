using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PlayerViewer.Core.Formats;

namespace PlayerViewer.Core
{
    /// <summary>
    /// Access to a Splatoon 3 romfs dump with optional LayeredFS overlay:
    /// if a file exists under the layered root it wins over the base romfs,
    /// mirroring atmosphere mods. Lookup also handles .zs decompression.
    /// </summary>
    public class Romfs
    {
        public string Root { get; }

        /// <summary>Optional mod overlay root (same layout as romfs). Null = none.</summary>
        public string LayeredRoot { get; }

        /// <summary>Whether the overlay participates in lookups.</summary>
        public bool UseLayered { get; }

        /// <summary>Optional Side Order DLC romfs root. Null = none.</summary>
        public string SdodrRoot { get; }

        readonly Dictionary<string, Sarc> _packCache = new(StringComparer.OrdinalIgnoreCase);

        public Romfs(
            string root,
            string layeredRoot = null,
            bool useLayered = false,
            string sdodrRoot = null
        )
        {
            Root = root;
            LayeredRoot = layeredRoot;
            UseLayered =
                useLayered && !string.IsNullOrEmpty(layeredRoot) && Directory.Exists(layeredRoot);
            SdodrRoot =
                !string.IsNullOrEmpty(sdodrRoot) && Directory.Exists(sdodrRoot) ? sdodrRoot : null;
        }

        public static bool IsValidRoot(string root)
        {
            return !string.IsNullOrEmpty(root)
                && Directory.Exists(Path.Combine(root, "Model"))
                && Directory.Exists(Path.Combine(root, "RSDB"))
                && File.Exists(Path.Combine(root, "Model", "Player00.bfres.zs"));
        }

        /// <summary>
        /// Resolves a romfs-relative path (also trying + ".zs") to a full path,
        /// checking the layered root first. Null if the file exists nowhere.
        /// </summary>
        public string Resolve(string relativePath)
        {
            if (UseLayered)
            {
                string layered = Path.Combine(LayeredRoot, relativePath);
                if (File.Exists(layered))
                    return layered;
                if (File.Exists(layered + ".zs"))
                    return layered + ".zs";
            }
            if (SdodrRoot != null)
            {
                string sdodr = Path.Combine(SdodrRoot, relativePath);
                if (File.Exists(sdodr))
                    return sdodr;
                if (File.Exists(sdodr + ".zs"))
                    return sdodr + ".zs";
            }
            string path = Path.Combine(Root, relativePath);
            if (File.Exists(path))
                return path;
            if (File.Exists(path + ".zs"))
                return path + ".zs";
            return null;
        }

        /// <summary>
        /// Reads a file relative to the romfs root; tries the exact path then path + ".zs".
        /// Returns decompressed bytes or null.
        /// </summary>
        public byte[] ReadFile(string relativePath)
        {
            string path = Resolve(relativePath);
            if (path == null)
                return null;
            var data = File.ReadAllBytes(path);
            return path.EndsWith(".zs") ? Decompress(data) : data;
        }

        public bool FileExists(string relativePath) => Resolve(relativePath) != null;

        /// <summary>True when the layered overlay provides this file.</summary>
        public bool IsLayered(string relativePath)
        {
            if (!UseLayered)
                return false;
            string layered = Path.Combine(LayeredRoot, relativePath);
            return File.Exists(layered) || File.Exists(layered + ".zs");
        }

        public static byte[] Decompress(byte[] data)
        {
            //zstd magic
            if (
                data.Length >= 4
                && data[0] == 0x28
                && data[1] == 0xB5
                && data[2] == 0x2F
                && data[3] == 0xFD
            )
            {
                using var decompressor = new ZstdSharp.Decompressor();
                return decompressor.Unwrap(data).ToArray();
            }
            return data;
        }

        /// <summary>Model bfres by name (e.g. "Hed_ACC003"), decompressed. Null if missing.</summary>
        public byte[] ReadModel(string modelName) => ReadFile($"Model/{modelName}.bfres");

        /// <summary>Alias of ReadModel (used by animation loading).</summary>
        public byte[] ReadModelFile(string modelName) => ReadModel(modelName);

        public bool ModelExists(string modelName) => FileExists($"Model/{modelName}.bfres");

        /// <summary>
        /// Lists files in a romfs directory matching a pattern, unioned across the
        /// layered and base roots (layered wins for identical relative names).
        /// Returns full paths.
        /// </summary>
        public List<string> FindFiles(string relativeDir, string pattern)
        {
            var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            string baseDir = Path.Combine(Root, relativeDir);
            if (Directory.Exists(baseDir))
                foreach (var file in Directory.EnumerateFiles(baseDir, pattern))
                    byName[Path.GetFileName(file)] = file;

            if (SdodrRoot != null)
            {
                string sdodrDir = Path.Combine(SdodrRoot, relativeDir);
                if (Directory.Exists(sdodrDir))
                    foreach (var file in Directory.EnumerateFiles(sdodrDir, pattern))
                        byName[Path.GetFileName(file)] = file;
            }

            if (UseLayered)
            {
                string layeredDir = Path.Combine(LayeredRoot, relativeDir);
                if (Directory.Exists(layeredDir))
                    foreach (var file in Directory.EnumerateFiles(layeredDir, pattern))
                        byName[Path.GetFileName(file)] = file;
            }

            var results = byName.Values.ToList();
            results.Sort(StringComparer.OrdinalIgnoreCase);
            return results;
        }

        /// <summary>Lists model names (without extension) starting with the given prefix.</summary>
        public List<string> ListModelFiles(string prefix)
        {
            var results = new List<string>();
            foreach (var file in FindFiles("Model", prefix + "*.bfres.zs"))
            {
                string name = Path.GetFileName(file);
                name = name.Substring(0, name.Length - ".bfres.zs".Length);
                results.Add(name);
            }
            results.Sort(StringComparer.OrdinalIgnoreCase);
            return results;
        }

        /// <summary>Actor pack by name (e.g. "Hed_ACC003"), cached. Null if missing.</summary>
        public Sarc GetActorPack(string actorName)
        {
            if (_packCache.TryGetValue(actorName, out var cached))
                return cached;

            var data = ReadFile($"Pack/Actor/{actorName}.pack");
            Sarc sarc = data != null ? new Sarc(data) : null;
            _packCache[actorName] = sarc;
            return sarc;
        }

        /// <summary>Parses a byml file from the romfs (path may omit .zs).</summary>
        public Byml ReadByml(string relativePath)
        {
            var data = ReadFile(relativePath);
            return data != null ? new Byml(data) : null;
        }

        /// <summary>
        /// Resolves a "Work/..." gyml reference inside an actor pack to its compiled bgyml bytes.
        /// e.g. "Work/Gyml/HairArrange/X.spl__HairArrangeParam.gyml" -> "Gyml/HairArrange/X.spl__HairArrangeParam.bgyml"
        /// </summary>
        public static byte[] ResolveWorkPath(Sarc pack, string workPath)
        {
            if (pack == null || string.IsNullOrEmpty(workPath))
                return null;

            string rel = workPath.StartsWith("Work/") ? workPath.Substring(5) : workPath;
            if (rel.EndsWith(".gyml"))
                rel = rel.Substring(0, rel.Length - 5) + ".bgyml";
            return pack.GetFile(rel);
        }
    }
}
