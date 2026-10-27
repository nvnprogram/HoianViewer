using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BfresLibrary;
using PlayerViewer.Core;
using PlayerViewer.Core.Formats;
using PlayerViewer.Textures;

namespace PlayerViewer.Conversion
{
    /// <summary>A model converted for one game, with the files that game keeps beside it.</summary>
    public sealed class ConvertedModel
    {
        public GameKind Game;
        public string Archive;
        public string Model;
        public byte[] Bfres;

        /// <summary>Raiders texture files by stem, each the bytes of <c>Tex/&lt;stem&gt;.bntx</c>.</summary>
        public readonly Dictionary<string, byte[]> Textures = new(StringComparer.Ordinal);

        /// <summary>The Raiders animation archive, <c>Model/&lt;archive&gt;.anim.bfres</c>, or null.</summary>
        public byte[] AnimArchive;

        /// <summary>Textures a Raiders model binds that none of the given places had.</summary>
        public readonly List<string> MissingTextures = new();

        /// <summary>Materials Raiders ships no program for, which draw only through the splicer.</summary>
        public readonly List<string> Unserved = new();

        /// <summary>Whether the model names an animation file that could not be found.</summary>
        public bool MissingAnimFile;

        public int Animations;

        /// <summary>The model file's name in the target game's <c>Model</c> folder, without <c>.zs</c>.</summary>
        public string FileName =>
            Game == GameKind.Spiral ? $"{Archive}.{Model}.bfres" : $"{Model}.bfres";

        public string AnimFileName => $"{Archive}.anim.bfres";

        /// <summary>A converted Raiders file by its romfs path, as <c>Romfs.ReadFile</c> takes it, or null.</summary>
        public byte[] ReadFile(string path)
        {
            if (path == $"Model/{AnimFileName}")
                return AnimArchive;
            string file = Path.GetFileName(path);
            return file.EndsWith(".bntx", StringComparison.Ordinal)
                ? Textures.GetValueOrDefault(file[..^".bntx".Length])
                : null;
        }
    }

    /// <summary>
    /// Converts a model between Splatoon 3 and Raiders. The structures are the same; what differs
    /// is the header version, where the textures and animations are and the order of bytes
    /// inside a GOB.
    /// </summary>
    public static class ModelConvert
    {
        public const string AttachFlagName = "LoadAttachAnim.flag";
        const string EmbeddedName = "textures.bntx";

        /// <summary>The game a bfres was written for, by its header version, or null.</summary>
        public static GameKind? GameOf(byte[] bfres)
        {
            if (
                bfres == null
                || bfres.Length < 0x10
                || bfres[0] != 'F'
                || bfres[1] != 'R'
                || bfres[2] != 'E'
                || bfres[3] != 'S'
                || bfres[0x0A] != 10
                || bfres[0x0B] != 0
            )
                return null;
            if (bfres[8] == 0 && bfres[9] == 0)
                return GameKind.Thunder;
            if (bfres[8] == 2 && bfres[9] == 3)
                return GameKind.Spiral;
            return null;
        }

        /// <summary>Archive and model of a Raiders model by its internal name, <c>archive.model</c>.</summary>
        public static (string Archive, string Model) SplitName(string name)
        {
            int dot = name.IndexOf('.');
            return dot > 0 ? (name[..dot], name[(dot + 1)..]) : (name, name);
        }

        public static ConvertedModel ToThunder(
            byte[] raiders,
            Func<string, byte[]> readFile,
            string model = null
        )
        {
            var res = new ResFile(new MemoryStream(raiders));
            var (archive, ownModel) = SplitName(res.Name);
            var result = new ConvertedModel
            {
                Game = GameKind.Thunder,
                Archive = archive,
                Model = model ?? ownModel,
            };

            var stems = ExternalTextures.Stems(res, archive);
            if (res.ExternalFiles.ContainsKey(AttachFlagName))
            {
                var animBytes = readFile($"Model/{result.AnimFileName}");
                if (animBytes != null)
                {
                    var anims = new ResFile(new MemoryStream(animBytes));
                    MergeAnims(res, anims);
                    foreach (var (name, stem) in ExternalTextures.ReadTable(anims) ?? new())
                        stems.TryAdd(name, stem);
                }
                else
                    result.MissingAnimFile = true;
            }
            result.Animations = AnimCount(res);
            OptionMap.ToThunder(res);

            var textures = new List<PackedTexture>();
            foreach (var (name, stem) in stems.OrderBy(x => x.Key, StringComparer.Ordinal))
            {
                var bytes = readFile(ExternalTextures.PathOf(stem));
                var tex = bytes != null ? BntxPack.Read(bytes).FirstOrDefault() : null;
                if (tex == null)
                {
                    result.MissingTextures.Add(name);
                    continue;
                }
                tex.Name = name;
                Retarget(tex, BntxPlatform.Ounce, BntxPlatform.Switch);
                textures.Add(tex);
            }

            var externals = new ResDict<ExternalFile>();
            if (textures.Count > 0)
                externals.Add(
                    EmbeddedName,
                    new ExternalFile
                    {
                        Data = BntxPack.Write("textures", BntxPlatform.Switch, textures),
                    }
                );
            foreach (var (name, file) in res.ExternalFiles)
                if (name != ExternalTextures.TableName && name != AttachFlagName)
                    externals.Add(name, file);
            res.ExternalFiles = externals;

            res.Name = result.Model;
            res.VersionMinor = 0;
            res.VersionMinor2 = 0;
            res.Alignment = 0xC;
            SetBufferAlignment(res, 8);
            result.Bfres = Save(res);
            return result;
        }

        public static ConvertedModel ToSpiral(
            byte[] s3,
            string archive,
            string model,
            Func<string, byte[]> readModel = null,
            Func<Dictionary<string, string>, bool> served = null
        )
        {
            var res = new ResFile(new MemoryStream(s3));
            var result = new ConvertedModel
            {
                Game = GameKind.Spiral,
                Archive = archive,
                Model = model,
            };

            var stems = new Dictionary<string, string>(StringComparer.Ordinal);
            void Add(PackedTexture tex)
            {
                string stem = $"{archive}.{tex.Name}";
                Retarget(tex, BntxPlatform.Switch, BntxPlatform.Ounce);
                result.Textures[stem] = BntxPack.Write(stem, BntxPlatform.Ounce, new[] { tex });
                stems[tex.Name] = stem;
            }
            foreach (var (extName, file) in res.ExternalFiles)
            {
                if (!extName.EndsWith(".bntx", StringComparison.Ordinal) || file.Data == null)
                    continue;
                foreach (var tex in BntxPack.Read(file.Data))
                    Add(tex);
            }
            CarryReferencedTextures(res, readModel, stems, Add, result.MissingTextures);
            result.Unserved.AddRange(OptionMap.ToSpiral(res, served));

            result.Animations = AnimCount(res);
            var bound = res
                .Models.Values.SelectMany(m => m.Materials.Values)
                .SelectMany(m => m.TextureRefs)
                .Select(t => t.Name)
                .ToHashSet(StringComparer.Ordinal);
            var patterns = res
                .TexPatternAnims.Values.Where(a => a.TextureNames != null)
                .SelectMany(a => a.TextureNames.Keys)
                .ToHashSet(StringComparer.Ordinal);
            if (result.Animations > 0)
            {
                var anims = new ResFile(new MemoryStream(s3));
                anims.Models.Clear();
                anims.Textures = new ResDict<TextureShared>();
                anims.ExternalFiles = new ResDict<ExternalFile>();
                var animTable = Table(stems.Where(x => patterns.Contains(x.Key)));
                if (animTable.Count > 0)
                    anims.ExternalFiles.Add(
                        ExternalTextures.TableName,
                        new ExternalFile { Data = BymlWriter.Write(animTable) }
                    );
                anims.Name = $"{archive}.anim";
                SetSpiralVersion(anims);
                //Animation archives keep their data at the smaller alignment.
                anims.Alignment = 3;
                result.AnimArchive = Save(anims);
            }

            //Textures only the animations show are listed in the animation file's table.
            var table = Table(stems.Where(x => bound.Contains(x.Key) || !patterns.Contains(x.Key)));
            var externals = new ResDict<ExternalFile>();
            if (result.AnimArchive != null)
                externals.Add(AttachFlagName, new ExternalFile { Data = Array.Empty<byte>() });
            externals.Add(
                ExternalTextures.TableName,
                new ExternalFile { Data = BymlWriter.Write(table) }
            );
            foreach (var (extName, file) in res.ExternalFiles)
                if (!extName.EndsWith(".bntx", StringComparison.Ordinal))
                    externals.Add(extName, file);
            res.ExternalFiles = externals;
            res.Textures = new ResDict<TextureShared>();
            ClearAnims(res);

            res.Name = $"{archive}.{model}";
            SetSpiralVersion(res);
            res.Alignment = 0xC;
            SetBufferAlignment(res, 256);
            result.Bfres = Save(res);
            return result;
        }

        const string ExternalResourceKey = "ExternalTextureResource";

        /// <summary>
        /// Adds the textures a material takes from another model through its
        /// ExternalTextureResource user data, and drops the entry, which no Raiders model has.
        /// </summary>
        static void CarryReferencedTextures(
            ResFile res,
            Func<string, byte[]> readModel,
            Dictionary<string, string> stems,
            Action<PackedTexture> add,
            List<string> missing
        )
        {
            var sources = new Dictionary<string, List<PackedTexture>>(StringComparer.Ordinal);
            List<PackedTexture> TexturesOf(string name)
            {
                if (sources.TryGetValue(name, out var list))
                    return list;
                list = new List<PackedTexture>();
                if (readModel?.Invoke(name) is byte[] data)
                    foreach (var file in new ResFile(new MemoryStream(data)).ExternalFiles.Values)
                        if (
                            file.Data is { Length: > 4 } bytes
                            && bytes[0] == 'B'
                            && bytes[1] == 'N'
                        )
                            list.AddRange(BntxPack.Read(bytes));
                return sources[name] = list;
            }

            foreach (var mat in res.Models.Values.SelectMany(m => m.Materials.Values))
            {
                if (!mat.UserData.ContainsKey(ExternalResourceKey))
                    continue;
                //Each entry is "sampler,model" or "sampler/model".
                var models = mat.UserData[ExternalResourceKey]
                    .GetValueStringArray()
                    .Select(v => v[(Math.Max(v.LastIndexOf('/'), v.LastIndexOf(',')) + 1)..])
                    .Distinct()
                    .ToList();
                mat.UserData.RemoveKey(ExternalResourceKey);
                foreach (var name in mat.TextureRefs.Select(t => t.Name))
                {
                    if (string.IsNullOrEmpty(name) || stems.ContainsKey(name))
                        continue;
                    var tex = models.SelectMany(TexturesOf).FirstOrDefault(t => t.Name == name);
                    if (tex != null)
                        add(tex);
                    else if (!missing.Contains(name))
                        missing.Add(name);
                }
            }
        }

        /// <summary>
        /// Reads the files a loose Raiders model needs: from the folder layout a mod uses (the
        /// model in <c>Model</c>, its textures in <c>Tex</c> beside it), from beside the model,
        /// then from the given romfs.
        /// </summary>
        public static Func<string, byte[]> SourceFiles(string modelPath, Romfs romfs)
        {
            string dir = Path.GetDirectoryName(Path.GetFullPath(modelPath));
            var candidates = new List<Func<string, string>>();
            if (Path.GetDirectoryName(dir) is string parent)
                candidates.Add(relative => Path.Combine(parent, relative));
            candidates.Add(relative => Path.Combine(dir, relative));
            candidates.Add(relative => Path.Combine(dir, Path.GetFileName(relative)));
            return relative =>
            {
                foreach (var candidate in candidates)
                foreach (var suffix in new[] { "", ".zs" })
                {
                    string full = candidate(relative) + suffix;
                    if (File.Exists(full))
                        return Romfs.Decompress(File.ReadAllBytes(full));
                }
                return romfs?.ReadFile(relative);
            };
        }

        /// <summary>
        /// The romfs root a Raiders model's other files go under: the folder above its
        /// <c>Model</c> folder, else the folder it is saved in.
        /// </summary>
        public static string SpiralRootFor(string modelPath)
        {
            string dir = Path.GetDirectoryName(Path.GetFullPath(modelPath));
            return string.Equals(Path.GetFileName(dir), "Model", StringComparison.OrdinalIgnoreCase)
                ? Path.GetDirectoryName(dir)
                : dir;
        }

        /// <summary>
        /// Renames a Raiders model's texture tables, its own and its animation file's, for the
        /// archive it is saved as, each texture under the stem <c>archive.name</c>, and collects
        /// the files they list by stem: an edited texture from <paramref name="edits"/>, any
        /// other read from the stem it had when opened. A name the edits removed is dropped, and an edited one
        /// neither table lists joins the model's. Returns the names whose file was not found.
        /// </summary>
        public static HashSet<string> RetableSpiral(
            ResFile res,
            ResFile anims,
            string archive,
            Func<string, byte[]> read,
            ExternalTextureEdits edits,
            Dictionary<string, byte[]> files
        )
        {
            var missing = new HashSet<string>(StringComparer.Ordinal);
            Dictionary<string, string> Retable(Dictionary<string, string> table)
            {
                var renamed = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var (name, oldStem) in table)
                {
                    if (edits?.Removed(name) == true)
                        continue;
                    string stem = $"{archive}.{name}";
                    renamed[name] = stem;
                    string source = edits?.SourceStem(name, oldStem) ?? oldStem;
                    var bytes =
                        edits?.File(name, stem)
                        ?? (
                            read(ExternalTextures.PathOf(source)) is byte[] bntx
                                ? BntxPack.Write(stem, BntxPlatform.Ounce, BntxPack.Read(bntx))
                                : null
                        );
                    if (bytes != null)
                        files[stem] = bytes;
                    else
                        missing.Add(name);
                }
                return renamed;
            }

            var own = ExternalTextures.ReadTable(res) is { } table ? Retable(table) : null;
            var animTable =
                anims != null && ExternalTextures.ReadTable(anims) is { } at ? Retable(at) : null;
            foreach (var name in edits?.Live ?? Enumerable.Empty<string>())
            {
                if ((own?.ContainsKey(name) ?? false) || (animTable?.ContainsKey(name) ?? false))
                    continue;
                own ??= new Dictionary<string, string>(StringComparer.Ordinal);
                string stem = $"{archive}.{name}";
                own[name] = stem;
                files[stem] = edits.File(name, stem);
            }
            if (own != null)
                ExternalTextures.WriteTable(res, own);
            if (animTable != null)
                ExternalTextures.WriteTable(anims, animTable);
            return missing;
        }

        /// <summary>Adds the file of every edited texture the model's table lists that
        /// <paramref name="files"/> lacks, which is a texture the save itself generated.</summary>
        public static void AddEditedFiles(
            ResFile res,
            ExternalTextureEdits edits,
            Dictionary<string, byte[]> files
        )
        {
            if (edits == null)
                return;
            foreach (var (name, stem) in ExternalTextures.ReadTable(res) ?? new())
                if (!files.ContainsKey(stem) && edits.Edited(name))
                    files[stem] = edits.File(name, stem);
        }

        static Dictionary<string, object> Table(IEnumerable<KeyValuePair<string, string>> stems) =>
            stems.ToDictionary(x => x.Key, x => (object)x.Value, StringComparer.Ordinal);

        static void MergeAnims(ResFile into, ResFile from)
        {
            static void Add<T>(ResDict<T> to, ResDict<T> source)
                where T : BfresLibrary.Core.IResData, new()
            {
                foreach (var (name, anim) in source)
                    if (!to.ContainsKey(name))
                        to.Add(name, anim);
            }
            Add(into.SkeletalAnims, from.SkeletalAnims);
            Add(into.ShaderParamAnims, from.ShaderParamAnims);
            Add(into.TexSrtAnims, from.TexSrtAnims);
            Add(into.ColorAnims, from.ColorAnims);
            Add(into.TexPatternAnims, from.TexPatternAnims);
            Add(into.MatVisibilityAnims, from.MatVisibilityAnims);
            Add(into.BoneVisibilityAnims, from.BoneVisibilityAnims);
            Add(into.ShapeAnims, from.ShapeAnims);
            Add(into.SceneAnims, from.SceneAnims);
        }

        static int AnimCount(ResFile res) =>
            res.SkeletalAnims.Count
            + res.ShaderParamAnims.Count
            + res.TexSrtAnims.Count
            + res.ColorAnims.Count
            + res.TexPatternAnims.Count
            + res.MatVisibilityAnims.Count
            + res.BoneVisibilityAnims.Count
            + res.ShapeAnims.Count
            + res.SceneAnims.Count;

        static void ClearAnims(ResFile res)
        {
            res.SkeletalAnims.Clear();
            res.ShaderParamAnims.Clear();
            res.TexSrtAnims.Clear();
            res.ColorAnims.Clear();
            res.TexPatternAnims.Clear();
            res.MatVisibilityAnims.Clear();
            res.BoneVisibilityAnims.Clear();
            res.ShapeAnims.Clear();
            res.SceneAnims.Clear();
        }

        //Ounce aligns every vertex and index buffer to 256 bytes, Switch to 8.
        static void SetBufferAlignment(ResFile res, ushort align)
        {
            foreach (var model in res.Models.Values)
            foreach (var buffer in model.VertexBuffers)
                buffer.GPUBufferAlignent = align;
        }

        static void SetSpiralVersion(ResFile res)
        {
            res.VersionMinor = 3;
            res.VersionMinor2 = 2;
        }

        static void Retarget(PackedTexture tex, BntxPlatform from, BntxPlatform to)
        {
            if (tex.IsBlockLinear)
                GobOrder.Reorder(tex.Image, from.Gob, to.Gob);
            tex.PackagerVersion = to.PackagerVersion;
            tex.AccessFlags = to.AccessFlags;
        }

        static byte[] Save(ResFile res)
        {
            var ms = new MemoryStream();
            res.Save(ms, true);
            return ms.ToArray();
        }
    }
}
