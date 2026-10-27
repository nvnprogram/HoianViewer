using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Hashing;
using System.Text;

namespace ShaderBundler
{
    /// <summary>
    /// On disk cache of specialised stage binaries, keyed by the option vector the splice was
    /// made from. A fragment splice costs about 3s seconds, so a hit is the difference between a
    /// responsive editor and an unusable one.
    ///
    /// One root holds a folder per game, and a version.txt at the root with a line per game.
    /// A game whose line does not match has its folder purged; the other game keeps its own.
    ///
    /// Preview splices live in a `preview` sub-directory of the game's folder. Same keys, so a
    /// pair is addressed by (key, preview) and nothing that walks the full splices can pick one
    /// up: a preview program has had none of the specialiser's acceptance guards run over it
    /// and must never reach a saved archive. The sub-directory is inside so one version purge
    /// covers both.
    /// </summary>
    public sealed class UberSliceCache
    {
        public const int FormatVersion = 3;

        /// <summary>The root folder's name.</summary>
        public const string RootName = "UberSliceCache";

        //One lock for every instance, since the games share the root's version.txt.
        static readonly object RootLock = new();

        readonly string _root;
        readonly string _name;
        readonly string _dir;
        readonly string _previewDir;
        readonly string _version;
        bool _versionChecked;

        public UberSliceCache(string root, string name, int codegenVersion)
        {
            _root = Path.TrimEndingDirectorySeparator(
                root ?? throw new ArgumentNullException(nameof(root))
            );
            _name = name ?? throw new ArgumentNullException(nameof(name));
            _dir = Path.Combine(_root, _name);
            _previewDir = Path.Combine(_dir, "preview");
            _version = $"{FormatVersion}.{codegenVersion}";
        }

        public string Directory => _dir;

        string Dir(bool preview) => preview ? _previewDir : _dir;

        /// <summary>
        /// Identity of one splice: the option vector, the stage, and the grid cell it was
        /// spliced from. The vector already carries the assign type and the weight, but they
        /// are named here too so a key is readable and a change to the derivation cannot
        /// quietly collide with an old entry.
        /// </summary>
        public static string MakeKey(
            string optionVectorHash,
            ShaderStage stage,
            string assignType,
            string weight
        )
        {
            string material = $"{optionVectorHash}|{stage}|{assignType}|{weight}";
            return XxHash128.HashToUInt128(Encoding.UTF8.GetBytes(material)).ToString("x32");
        }

        public bool Has(SpliceKey key, ShaderStage stage, bool preview = false) =>
            Has(key.Cache(stage), preview);

        public bool TryGet(
            SpliceKey key,
            ShaderStage stage,
            out ShaderBinary binary,
            bool preview = false
        ) => TryGet(key.Cache(stage), out binary, preview);

        public void Put(
            SpliceKey key,
            ShaderStage stage,
            ShaderBinary binary,
            bool preview = false
        ) => Put(key.Cache(stage), binary, preview);

        /// <summary>Whether both blobs of an entry are present, without reading them.</summary>
        public bool Has(string key, bool preview = false)
        {
            EnsureVersion();
            return File.Exists(Path.Combine(Dir(preview), key + ".bytecode"))
                && File.Exists(Path.Combine(Dir(preview), key + ".control"));
        }

        public bool TryGet(string key, out ShaderBinary binary, bool preview = false)
        {
            binary = null;
            EnsureVersion();
            string code = Path.Combine(Dir(preview), key + ".bytecode");
            string ctrl = Path.Combine(Dir(preview), key + ".control");
            if (!File.Exists(code) || !File.Exists(ctrl))
                return false;
            try
            {
                binary = new ShaderBinary(File.ReadAllBytes(code), File.ReadAllBytes(ctrl));
                return true;
            }
            catch (IOException)
            {
                return false;
            }
        }

        public void Put(string key, ShaderBinary binary, bool preview = false)
        {
            if (binary == null)
                throw new ArgumentNullException(nameof(binary));
            EnsureVersion();
            //Write beside the entry and swap in, so a torn write cannot be read back as a
            //shader.
            WriteAtomic(Path.Combine(Dir(preview), key + ".bytecode"), binary.ByteCode);
            WriteAtomic(Path.Combine(Dir(preview), key + ".control"), binary.ControlCode);
        }

        static void WriteAtomic(string path, byte[] data)
        {
            string temp = Path.Combine(
                Path.GetDirectoryName(path),
                Path.GetRandomFileName() + ".tmp"
            );
            File.WriteAllBytes(temp, data);
            File.Move(temp, path, true);
        }

        void EnsureVersion()
        {
            lock (RootLock)
            {
                if (_versionChecked)
                    return;
                _versionChecked = true;

                string versionPath = Path.Combine(_root, "version.txt");
                var versions = ReadVersions(versionPath);
                if (versions == null)
                {
                    ClearLegacy();
                    versions = new Dictionary<string, string>();
                }

                bool fresh = !System.IO.Directory.Exists(_dir);
                System.IO.Directory.CreateDirectory(_previewDir);
                if (!fresh && versions.TryGetValue(_name, out var existing) && existing == _version)
                    return;

                //Both directories, or a purge would leave the preview splices of the previous
                //uberspec behind and they are the ones drawn first.
                if (!fresh)
                    DeleteFiles(_dir, _previewDir);
                versions[_name] = _version;
                var lines = new List<string>();
                foreach (var entry in versions)
                    lines.Add($"{entry.Key} {entry.Value}");
                File.WriteAllLines(versionPath, lines);
            }
        }

        //Null when the file is missing or in the single version form of the old layout.
        static Dictionary<string, string> ReadVersions(string path)
        {
            if (!File.Exists(path))
                return null;
            var versions = new Dictionary<string, string>();
            foreach (var line in File.ReadAllLines(path))
            {
                if (line.Trim().Length == 0)
                    continue;
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 2)
                    return null;
                versions[parts[0]] = parts[1];
            }
            return versions;
        }

        //Before format 3 the entries sat loose in the root.
        void ClearLegacy()
        {
            string preview = Path.Combine(_root, "preview");
            DeleteFiles(_root, preview);
            TryDeleteDirectory(preview);
        }

        static void DeleteFiles(params string[] dirs)
        {
            foreach (var dir in dirs)
            {
                if (!System.IO.Directory.Exists(dir))
                    continue;
                foreach (var f in System.IO.Directory.GetFiles(dir))
                    try
                    {
                        File.Delete(f);
                    }
                    catch { }
            }
        }

        static void TryDeleteDirectory(string dir)
        {
            try
            {
                if (System.IO.Directory.Exists(dir))
                    System.IO.Directory.Delete(dir, true);
            }
            catch { }
        }
    }
}
