using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BfresLibrary;
using PlayerViewer.Conversion;
using Syroot.NintenTools.NSW.Bntx;
using BntxTextureData = Syroot.NintenTools.NSW.Bntx.Texture;

namespace PlayerViewer.Textures
{
    /// <summary>
    /// Texture edits on a model that keeps its textures in <c>Tex/</c>, one file each. Each
    /// edited name holds its texture in memory until a save writes the files, and the model's
    /// <c>textureTable.byml</c> is kept in step as the edits are made.
    /// </summary>
    public sealed class ExternalTextureEdits
    {
        readonly ResFile _res;
        readonly Dictionary<string, string> _sourceStems;

        //A null container is a name a delete or a rename took away.
        readonly Dictionary<string, BntxFile> _edits = new(StringComparer.Ordinal);

        public ExternalTextureEdits(ResFile res, BntxPlatform platform)
        {
            _res = res;
            Platform = platform;
            _sourceStems =
                ExternalTextures.ReadTable(res)
                ?? new Dictionary<string, string>(StringComparer.Ordinal);
            SourceArchive = ModelConvert.SplitName(res.Name).Archive;
        }

        public BntxPlatform Platform { get; }

        /// <summary>The archive the model was named for when opened. A save renames it in memory.</summary>
        public string SourceArchive { get; }

        /// <summary>The stem a texture's file had when the model was opened, where a save reads an
        /// unedited texture from; a save renames the stems in memory.</summary>
        public string SourceStem(string name, string stem) =>
            _sourceStems.TryGetValue(name, out var source) ? source : stem;

        /// <summary>Whether a model keeps its textures outside the bfres: it carries a texture
        /// table, or is a bfres 10.3 file, which never embeds them.</summary>
        public static bool Applies(ResFile res) =>
            res != null
            && (
                res.ExternalFiles.ContainsKey(ExternalTextures.TableName)
                || (res.VersionMajor2 == 10 && res.VersionMinor == 3)
            );

        public bool Removed(string name) => _edits.TryGetValue(name, out var file) && file == null;

        public bool Edited(string name) => _edits.TryGetValue(name, out var file) && file != null;

        /// <summary>Names whose texture is held here rather than in a file.</summary>
        public IEnumerable<string> Live => _edits.Where(x => x.Value != null).Select(x => x.Key);

        /// <summary>Whether the model's own table lists the name.</summary>
        public bool Listed(string name) => Table().ContainsKey(name);

        /// <summary>
        /// Holds a texture under its name and returns the one texture container it is drawn
        /// from. A name the table lacks is listed when <paramref name="list"/> is set; a texture
        /// shown through the animation file's table is replaced without one.
        /// </summary>
        public BntxFile Put(BntxTextureData texture, bool list)
        {
            var container = NewContainer(Platform, texture.Name);
            container.Textures.Add(texture);
            container.TextureDict.Add(texture.Name);
            _edits[texture.Name] = container;
            if (list && !Listed(texture.Name))
                EditTable(t => t[texture.Name] = StemOf(texture.Name));
            return container;
        }

        public void Remove(string name)
        {
            _edits[name] = null;
            if (Listed(name))
                EditTable(t => t.Remove(name));
        }

        /// <summary>Moves a texture to its new name, which <paramref name="texture"/> already
        /// carries. The new name is always listed, under a file of its own.</summary>
        public BntxFile Rename(string oldName, BntxTextureData texture)
        {
            Remove(oldName);
            return Put(texture, true);
        }

        /// <summary>The texture file of an edited name, written as <paramref name="stem"/>, or
        /// null for a name with no edit.</summary>
        public byte[] File(string name, string stem)
        {
            if (!_edits.TryGetValue(name, out var container) || container == null)
                return null;
            return Pack(container, stem, Platform);
        }

        /// <summary>One texture container as its own BNTX file, the way the romfs keeps it: the
        /// texture under its own name, the file under the stem.</summary>
        public static byte[] Pack(BntxFile container, string stem, BntxPlatform platform)
        {
            var ms = new MemoryStream();
            container.Save(ms);
            return BntxPack.Write(stem, platform, BntxPack.Read(ms.ToArray()));
        }

        //A new name's stem, under the archive the model is named for now. A save renames every
        //stem after the archive it is saved as.
        string StemOf(string name) => $"{ModelConvert.SplitName(_res.Name).Archive}.{name}";

        Dictionary<string, string> Table() =>
            ExternalTextures.ReadTable(_res)
            ?? new Dictionary<string, string>(StringComparer.Ordinal);

        void EditTable(Action<Dictionary<string, string>> edit)
        {
            var table = Table();
            edit(table);
            ExternalTextures.WriteTable(_res, table);
        }

        static BntxFile NewContainer(BntxPlatform platform, string name) =>
            new()
            {
                Target = platform.Tag.ToCharArray(),
                PlatformTarget = platform.Tag,
                Name = name,
                Alignment = 0xC,
                TargetAddressSize = 0x40,
                VersionMajor = 0,
                VersionMajor2 = 4,
                VersionMinor = 1,
                VersionMinor2 = platform.Revision,
                Textures = new List<BntxTextureData>(),
                TextureDict = new Syroot.NintenTools.NSW.Bntx.ResDict(),
                RelocationTable = new Syroot.NintenTools.NSW.Bntx.RelocationTable(),
                Flag = 0,
            };
    }
}
