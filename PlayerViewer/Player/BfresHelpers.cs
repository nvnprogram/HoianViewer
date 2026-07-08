using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BfresEditor;
using PlayerViewer.Core;

namespace PlayerViewer.Player
{
    /// <summary>
    /// Static helpers for BFRES asset resolution shared by PlayerScene and StandaloneScene.
    /// </summary>
    public static class BfresHelpers
    {
        /// <summary>
        /// Resolves shared assets for a BFRES: share_tex_list.txt (textures + anims
        /// from referenced models) and ExternalTextureResource material user data.
        /// </summary>
        public static void ResolveSharedAssets(BFRES bfres, byte[] rawData, Romfs romfs)
        {
            try
            {
                var resFile = new BfresLibrary.ResFile(new MemoryStream(rawData));

                if (resFile.ExternalFiles.ContainsKey("share_tex_list.txt"))
                {
                    string content = System.Text.Encoding.UTF8
                        .GetString(resFile.ExternalFiles["share_tex_list.txt"].Data).Trim();
                    var refs = content.Split(new[] { '\r', '\n' },
                        StringSplitOptions.RemoveEmptyEntries);
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
        static void ResolveExternalTextureResources(BFRES bfres, BfresLibrary.ResFile resFile, Romfs romfs)
        {
            var render = (BfresRender)bfres.Renderer;
            var loaded = new HashSet<string>();

            foreach (var model in resFile.Models.Values)
            foreach (var mat in model.Materials.Values)
            {
                if (!mat.UserData.ContainsKey("ExternalTextureResource"))
                    continue;
                var strings = mat.UserData["ExternalTextureResource"].GetValueStringArray();
                if (strings.Length == 0) continue;

                string raw = strings[0];
                int sep = Math.Max(raw.LastIndexOf('/'), raw.LastIndexOf(','));
                string refName = sep >= 0 ? raw.Substring(sep + 1) : raw;
                if (!loaded.Add(refName)) continue;

                var refData = romfs.ReadModel(refName);
                if (refData == null)
                {
                    Console.WriteLine($"[Scene] ExternalTextureResource not found: {refName} (from {raw})");
                    continue;
                }
                var refRes = new BfresLibrary.ResFile(new MemoryStream(refData));
                int added = 0;
                foreach (var tex in refRes.Textures.Values)
                {
                    if (render.Textures.ContainsKey(tex.Name)) continue;
                    if (tex is BfresLibrary.Switch.SwitchTexture st)
                    {
                        render.Textures.Add(tex.Name, new BntxTexture(st.BntxFile, st.Texture));
                        added++;
                    }
                }
                if (added > 0)
                    Console.WriteLine($"[Scene] Merged {added} textures from ExternalTextureResource {refName}");
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

                int total = skelAnims + matAnims + visAnims;
                if (texAdded > 0 || total > 0)
                    Console.WriteLine($"[Scene] Merged {texAdded} tex, {total} anims " +
                        $"(skel={skelAnims} mat={matAnims} vis={visAnims}) from {baseModelName}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Scene] Shared asset merge failed ({baseModelName}): {ex.Message}");
            }
        }

        static int MergeMaterialAnims(BFRES bfres, BfresLibrary.ResDict<BfresLibrary.MaterialAnim> source, string resName)
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
