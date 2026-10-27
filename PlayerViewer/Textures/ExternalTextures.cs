using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BfresLibrary;
using BfresLibrary.Switch;
using PlayerViewer.Core.Formats;
using Syroot.NintenTools.NSW.Bntx;

namespace PlayerViewer.Textures
{
    /// <summary>
    /// Textures a Raiders model keeps outside its bfres. The model carries a
    /// <c>textureTable.byml</c> mapping each texture name its materials use to a file stem, and
    /// the texture is <c>Tex/&lt;stem&gt;.bntx</c>, one texture per file.
    /// </summary>
    public static class ExternalTextures
    {
        public const string TableName = "textureTable.byml";

        /// <summary>The romfs path of a texture file, without the compression suffix.</summary>
        public static string PathOf(string stem) => $"Tex/{stem}.bntx";

        /// <summary>Texture name to file stem, or null when the model carries no table.</summary>
        public static Dictionary<string, string> ReadTable(ResFile res)
        {
            if (res?.ExternalFiles == null || !res.ExternalFiles.ContainsKey(TableName))
                return null;
            var data = res.ExternalFiles[TableName].Data;
            if (data == null || data.Length == 0)
                return null;
            var root = Byml.AsHash(new Byml(data).Root);
            if (root == null)
                return null;
            var table = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var kv in root)
                if (kv.Value is string stem)
                    table[kv.Key] = stem;
            return table;
        }

        /// <summary>Writes the model's texture table, adding it when the model has none.</summary>
        public static void WriteTable(ResFile res, IReadOnlyDictionary<string, string> table)
        {
            var data = BymlWriter.Write(
                table.ToDictionary(x => x.Key, x => (object)x.Value, StringComparer.Ordinal)
            );
            if (res.ExternalFiles.ContainsKey(TableName))
                res.ExternalFiles[TableName].Data = data;
            else
                res.ExternalFiles.Add(TableName, new ExternalFile { Data = data });
        }

        /// <summary>
        /// Every texture name the model's materials bind, mapped to its file stem: the table's
        /// entry, else <c>&lt;archive&gt;.&lt;name&gt;</c>.
        /// </summary>
        public static Dictionary<string, string> Stems(ResFile res, string archive)
        {
            var table = ReadTable(res) ?? new Dictionary<string, string>(StringComparer.Ordinal);
            var stems = new Dictionary<string, string>(table, StringComparer.Ordinal);
            if (archive != null)
                foreach (var model in res.Models.Values)
                foreach (var mat in model.Materials.Values)
                foreach (var tex in mat.TextureRefs)
                    if (!string.IsNullOrEmpty(tex.Name) && !stems.ContainsKey(tex.Name))
                        stems[tex.Name] = $"{archive}.{tex.Name}";
            return stems;
        }

        /// <summary>
        /// Reads one texture file. The texture is filed under <paramref name="name"/>, the name the
        /// materials bind, whatever the file calls it.
        /// </summary>
        public static SwitchTexture Load(byte[] bntxBytes, string name = null)
        {
            var bntx = new BntxFile(new MemoryStream(bntxBytes));
            var tex = bntx.Textures.FirstOrDefault();
            if (tex == null)
                return null;
            if (name != null && tex.Name != name)
                tex.Name = name;
            return new SwitchTexture(bntx, tex);
        }

        /// <summary>
        /// The texture file of <paramref name="stem"/>, filed under <paramref name="name"/>.
        /// <paramref name="readFile"/> takes a romfs relative path and returns the decompressed
        /// bytes, or null when the file is not there. Null when there is no file or no texture in it.
        /// </summary>
        public static SwitchTexture Read(string stem, string name, Func<string, byte[]> readFile)
        {
            var bytes = readFile(PathOf(stem));
            return bytes == null ? null : Load(bytes, name);
        }
    }
}
