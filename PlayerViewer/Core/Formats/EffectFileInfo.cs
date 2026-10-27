using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PlayerViewer.Core.Formats
{
    /// <summary>
    /// <c>Effect/EffectFileInfo.Product.*.byml</c>: which esetb an effect user loads. A hash of
    /// <c>BinaryDict</c> (ELink user name to esetb name), <c>EsetbList</c> (every esetb) and
    /// <c>StaticEsetb</c> (the ones always resident). Splatoon 3 points most users at a few
    /// shared files; Raiders gives nearly every user a file of its own name. The esetb is
    /// <c>Effect/&lt;name&gt;.&lt;platform&gt;.esetb.byml.zs</c>, the platform taken from this
    /// file's own name.
    /// </summary>
    public sealed class EffectFileInfo
    {
        public IReadOnlyList<string> Files { get; }
        public IReadOnlyList<string> StaticFiles { get; }

        /// <summary>"Nin_NX_NVN" on Switch, "Nin_Ounce_NVN2" on Ounce.</summary>
        public string Platform { get; }

        EffectFileInfo(List<string> files, List<string> statics, string platform)
        {
            Files = files;
            StaticFiles = statics;
            Platform = platform;
        }

        /// <param name="fileName">The table's own file name, which carries the platform.</param>
        public static EffectFileInfo Read(byte[] byml, string fileName)
        {
            var root = Byml.AsHash(new Byml(byml).Root);
            return new EffectFileInfo(
                Strings(root, "EsetbList"),
                Strings(root, "StaticEsetb"),
                PlatformOf(fileName)
            );
        }

        /// <summary>The table in a romfs, or null when there is none.</summary>
        public static EffectFileInfo Find(Romfs romfs)
        {
            string path = romfs
                .FindFiles("Effect", "EffectFileInfo.*.byml*")
                .OrderBy(x => x, StringComparer.Ordinal)
                .LastOrDefault();
            if (path == null)
                return null;
            string name = Path.GetFileName(path);
            var data = File.ReadAllBytes(path);
            return Read(path.EndsWith(".zs") ? Romfs.Decompress(data) : data, name);
        }

        /// <summary>The romfs relative path of an esetb named in the table.</summary>
        public string PathOf(string esetbName) => $"Effect/{esetbName}.{Platform}.esetb.byml.zs";

        /// <summary>The esetb name of a path: the file name up to the platform.</summary>
        public static string NameOf(string esetbPath)
        {
            string file = Path.GetFileName(esetbPath);
            int at = file.IndexOf(".Nin_", StringComparison.Ordinal);
            return at > 0 ? file[..at] : file.Split(".esetb")[0];
        }

        static string PlatformOf(string fileName)
        {
            string file = Path.GetFileName(fileName ?? "");
            int at = file.IndexOf(".Nin_", StringComparison.Ordinal);
            if (at < 0)
                return "Nin_NX_NVN";
            string rest = file[(at + 1)..];
            int end = rest.IndexOf('.');
            return end > 0 ? rest[..end] : rest;
        }

        static List<string> Strings(Dictionary<string, object> root, string key) =>
            Byml.AsArray(root?.GetValueOrDefault(key))?.OfType<string>().ToList()
            ?? new List<string>();
    }
}
