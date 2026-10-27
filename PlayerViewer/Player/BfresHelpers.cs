using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BfresEditor;
using PlayerViewer.Core;
using PlayerViewer.Textures;

namespace PlayerViewer.Player
{
    /// <summary>
    /// Static helpers for BFRES asset resolution shared by PlayerScene and StandaloneScene.
    /// </summary>
    public static class BfresHelpers
    {
        /// <summary>
        /// Resolves shared assets for a BFRES: share_tex_list.txt (textures + anims
        /// from referenced models), ExternalTextureResource material user data, and for a
        /// Raiders model its textures in Tex/ and, with <paramref name="withAnimArchive"/>, the
        /// animations of its archive.
        /// </summary>
        public static void ResolveSharedAssets(
            BFRES bfres,
            byte[] rawData,
            Romfs romfs,
            string modelName = null,
            bool withAnimArchive = true
        )
        {
            try
            {
                var resFile = bfres.ResFile;

                if (modelName != null && romfs.ArchiveOf(modelName) is string archive)
                {
                    AttachTextures(bfres, resFile, archive, romfs);
                    if (withAnimArchive)
                        AttachAnimArchive(bfres, archive, romfs);
                }

                if (resFile.ExternalFiles.ContainsKey("share_tex_list.txt"))
                {
                    string content = System
                        .Text.Encoding.UTF8.GetString(
                            resFile.ExternalFiles["share_tex_list.txt"].Data
                        )
                        .Trim();
                    var refs = content.Split(
                        new[] { '\r', '\n' },
                        StringSplitOptions.RemoveEmptyEntries
                    );
                    foreach (var refModel in refs)
                        MergeSharedAssets(bfres, refModel.Trim(), romfs);
                }

                ResolveExternalTextureResources(bfres, resFile, romfs);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Scene] ResolveSharedAssets error: {ex.Message}");
            }
        }

        /// <summary>
        /// Scans all materials for ExternalTextureResource user data and loads
        /// the referenced BFRES textures into the render's texture cache.
        /// Value format: "_rp0/ModelName" or "_re0,ModelName".
        /// </summary>
        static void ResolveExternalTextureResources(
            BFRES bfres,
            BfresLibrary.ResFile resFile,
            Romfs romfs
        )
        {
            var render = (BfresRender)bfres.Renderer;
            var loaded = new HashSet<string>();

            foreach (var model in resFile.Models.Values)
            foreach (var mat in model.Materials.Values)
            {
                if (!mat.UserData.ContainsKey("ExternalTextureResource"))
                    continue;
                var strings = mat.UserData["ExternalTextureResource"].GetValueStringArray();
                if (strings.Length == 0)
                    continue;

                string raw = strings[0];
                int sep = Math.Max(raw.LastIndexOf('/'), raw.LastIndexOf(','));
                string refName = sep >= 0 ? raw.Substring(sep + 1) : raw;
                if (!loaded.Add(refName))
                    continue;

                var refData = romfs.ReadModel(refName);
                if (refData == null)
                {
                    Console.WriteLine(
                        $"[Scene] ExternalTextureResource not found: {refName} (from {raw})"
                    );
                    continue;
                }
                var refRes = new BfresLibrary.ResFile(new MemoryStream(refData));
                int added = 0;
                foreach (var tex in refRes.Textures.Values)
                {
                    if (render.Textures.ContainsKey(tex.Name))
                        continue;
                    if (tex is BfresLibrary.Switch.SwitchTexture st)
                    {
                        render.Textures.Add(tex.Name, new BntxTexture(st.BntxFile, st.Texture));
                        added++;
                    }
                }
                if (added > 0)
                    Console.WriteLine(
                        $"[Scene] Merged {added} textures from ExternalTextureResource {refName}"
                    );
            }
        }

        /// <summary>
        /// Adds the textures a Raiders file keeps in Tex/ to the model's render. With an
        /// <paramref name="archive"/>, names its table lacks are looked up in that archive.
        /// Returns how many were added.
        /// </summary>
        public static int AttachTextures(
            BFRES bfres,
            BfresLibrary.ResFile res,
            string archive,
            Romfs romfs
        ) => AttachTextures(bfres, res, archive, romfs.ReadFile);

        /// <summary>
        /// <see cref="AttachTextures(BFRES, BfresLibrary.ResFile, string, Romfs)"/> reading each
        /// file through <paramref name="readFile"/>, which takes a romfs relative path.
        /// </summary>
        public static int AttachTextures(
            BFRES bfres,
            BfresLibrary.ResFile res,
            string archive,
            Func<string, byte[]> readFile
        )
        {
            var render = (BfresRender)bfres.Renderer;
            int added = 0;
            foreach (var (name, stem) in ExternalTextures.Stems(res, archive))
            {
                if (render.Textures.ContainsKey(name))
                    continue;
                try
                {
                    var tex = ExternalTextures.Read(stem, name, readFile);
                    if (tex == null)
                        continue;
                    render.Textures.Add(name, new BntxTexture(tex.BntxFile, tex.Texture));
                    added++;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Scene] Texture {stem}: {ex.Message}");
                }
            }
            return added;
        }

        /// <summary>
        /// A Raiders model's textures and, when it has one, its archive's animation file, all
        /// read through <paramref name="readFile"/>.
        /// </summary>
        public static void AttachSpiralFiles(
            BFRES bfres,
            BfresLibrary.ResFile res,
            string archive,
            Func<string, byte[]> readFile
        )
        {
            try
            {
                AttachTextures(bfres, res, archive, readFile);
                if (!res.ExternalFiles.ContainsKey(Conversion.ModelConvert.AttachFlagName))
                    return;
                var data = readFile($"Model/{archive}.anim.bfres");
                if (data == null)
                    return;
                var anims = new BfresLibrary.ResFile(new MemoryStream(data));
                AttachTextures(bfres, anims, null, readFile);
                MergeAnims(bfres, anims);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Scene] Files of {archive}: {ex.Message}");
            }
        }

        /// <summary>
        /// Merges a Raiders animation archive (archive.anim) into the model: its animations
        /// and the textures its texture pattern frames use.
        /// </summary>
        public static void AttachAnimArchive(BFRES bfres, string archive, Romfs romfs)
        {
            try
            {
                var data = romfs.ReadAnimArchive(archive);
                if (data == null)
                    return;
                var res = new BfresLibrary.ResFile(new MemoryStream(data));
                AttachTextures(bfres, res, null, romfs);
                MergeAnims(bfres, res);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Scene] Animation archive {archive}: {ex.Message}");
            }
        }

        static void MergeSharedAssets(BFRES bfres, string baseModelName, Romfs romfs)
        {
            try
            {
                var baseData = romfs.ReadModel(baseModelName);
                if (baseData == null)
                    return;
                var render = (BfresRender)bfres.Renderer;
                var res = new BfresLibrary.ResFile(new MemoryStream(baseData));
                int texAdded = 0;
                foreach (var tex in res.Textures.Values)
                {
                    if (render.Textures.ContainsKey(tex.Name))
                        continue;
                    if (tex is BfresLibrary.Switch.SwitchTexture st)
                    {
                        render.Textures.Add(tex.Name, new BntxTexture(st.BntxFile, st.Texture));
                        texAdded++;
                    }
                }

                int total = MergeAnims(bfres, res);
                if (texAdded > 0 || total > 0)
                    Console.WriteLine(
                        $"[Scene] Merged {texAdded} tex, {total} anims from {baseModelName}"
                    );
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"[Scene] Shared asset merge failed ({baseModelName}): {ex.Message}"
                );
            }
        }

        //Skeletal, material and bone visibility animations not already on the model.
        static int MergeAnims(BFRES bfres, BfresLibrary.ResFile res)
        {
            int skelAnims = 0;
            foreach (var anim in res.SkeletalAnims.Values)
            {
                if (bfres.SkeletalAnimations.Any(a => a.Name == anim.Name))
                    continue;
                bfres.SkeletalAnimations.Add(new BfresSkeletalAnim(res, anim, anim.Name));
                skelAnims++;
            }

            int matAnims = MergeMaterialAnims(bfres, res.ShaderParamAnims, res.Name);
            matAnims += MergeMaterialAnims(bfres, res.TexPatternAnims, res.Name);
            matAnims += MergeMaterialAnims(bfres, res.TexSrtAnims, res.Name);
            matAnims += MergeMaterialAnims(bfres, res.ColorAnims, res.Name);

            int visAnims = 0;
            foreach (var anim in res.BoneVisibilityAnims.Values)
            {
                if (bfres.VisibilityAnimations.Any(a => a.Name == anim.Name))
                    continue;
                bfres.VisibilityAnimations.Add(new BfresVisibilityAnim(anim, res.Name));
                visAnims++;
            }

            return skelAnims + matAnims + visAnims;
        }

        static int MergeMaterialAnims(
            BFRES bfres,
            BfresLibrary.ResDict<BfresLibrary.MaterialAnim> source,
            string resName
        )
        {
            int added = 0;
            foreach (var anim in source.Values)
            {
                if (bfres.MaterialAnimations.Any(a => a.Name == anim.Name))
                    continue;
                bfres.MaterialAnimations.Add(new BfresMaterialAnim(anim, resName));
                added++;
            }
            return added;
        }
    }
}
