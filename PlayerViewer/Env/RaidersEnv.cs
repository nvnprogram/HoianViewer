using System;
using System.Collections.Generic;
using System.Numerics;
using PlayerViewer.Core;
using PlayerViewer.Core.Formats;

namespace PlayerViewer.Env
{
    /// <summary>
    /// Raiders rendering param group
    /// </summary>
    public class RaidersEnvParams : EnvParams
    {
        public float IrradianceIntensity = 1;
        public float CubeMapEmissionScale = 1;
        public float CubeMapDirectLightScale = 1;

        /// <summary>The env map texture's romfs path, such as <c>Tex/X.Y.bntx</c>, or null.</summary>
        public string EnvMapTexture;

        public float RadialFogShadowInfluence = 0;

        public float BakeShadowFldBGIntensOffset = 0.5f;
        public float BakeShadowFldBGIntensScale = 2.5f;
        public float BakeAOFldBGIntensOffset = 0;
        public float BakeAOFldBGIntensScale = 1;
        public float BakeAOFldBGMainLightOcclude = 0;

        public float ExpFogBaseDensity = 1;
        public float ExpFogStartHeight = 0;
        public float ExpFogStartDensity = 0.5f;
        public float ExpFogEndHeight = 400;
        public float ExpFogEndDensity = 0;
        public Vector4 ExpFogColor = new(0.3f, 0.6f, 1, 0);
        public float ExpFogIntensity = 1;
        public float ExpFogStartColorDistance = 0;
        public float ExpFogStartColorScale = 1;
        public float ExpFogEndColorDistance = 500;
        public float ExpFogEndColorScale = 1;

        public RaidersEnvParams()
        {
            MainLightLatitude = 80;
            MainLightLongitude = 0;
        }
    }

    /// <summary>
    /// Reads Raiders rendering param groups out of the Bootup pack, following each group's
    /// <c>$parent</c> chain.
    /// </summary>
    public static class RaidersEnv
    {
        /// <summary>The scene the character customise screen uses, the default for the viewer.</summary>
        public const string DefaultScene = "Customize";

        const string GroupSuffix = ".game__gfx__parameter__RenderingParamGroup.bgyml";

        //The packs the base groups are read from, once per romfs.
        static readonly System.Runtime.CompilerServices.ConditionalWeakTable<
            Romfs,
            List<Sarc>
        > _packs = new();

        /// <summary>
        /// The params of a group such as <c>Customize</c>, or null when it is missing. Scene groups
        /// are loose under <c>Gyml/</c>, the bases they inherit from are in the Bootup pack; a
        /// pack's copy wins over a loose file.
        /// </summary>
        public static RaidersEnvParams Load(Romfs romfs, string name)
        {
            List<Sarc> packs;
            lock (_packs)
                if (!_packs.TryGetValue(romfs, out packs))
                {
                    packs = new List<Sarc>();
                    foreach (var pattern in new[] { "Params.pack.zs", "Bootup.*.pack.zs" })
                    foreach (var path in romfs.FindFiles("Pack", pattern))
                        if (
                            romfs.ReadFile("Pack/" + System.IO.Path.GetFileName(path))
                            is byte[] data
                        )
                            packs.Add(new Sarc(data));
                    _packs.Add(romfs, packs);
                }

            Dictionary<string, object> merged = null;
            string file = $"Gyml/{name}{GroupSuffix}";
            for (int depth = 0; depth < 8 && file != null; depth++)
            {
                byte[] bytes = null;
                foreach (var pack in packs)
                    if ((bytes = pack.GetFile(file)) != null)
                        break;
                bytes ??= romfs.ReadFile(file);
                if (bytes == null)
                    break;
                var root = Byml.AsHash(new Byml(bytes).Root);
                merged = merged == null ? root : Merge(root, merged);
                string parent = Byml.GetString(root, "$parent");
                file = parent.Length > 0 ? WorkToPack(parent) : null;
            }
            return merged == null ? null : FromByml(merged, name);
        }

        //The child's values over the parent's, hash by hash.
        static Dictionary<string, object> Merge(
            Dictionary<string, object> parent,
            Dictionary<string, object> child
        )
        {
            var result = new Dictionary<string, object>(parent);
            foreach (var (key, value) in child)
            {
                if (
                    value is Dictionary<string, object> c
                    && result.TryGetValue(key, out var p)
                    && p is Dictionary<string, object> ph
                )
                    result[key] = Merge(ph, c);
                else
                    result[key] = value;
            }
            return result;
        }

        static string WorkToPack(string work)
        {
            string rel = work.StartsWith("Work/") ? work.Substring(5) : work;
            return rel.EndsWith(".gyml") ? rel.Substring(0, rel.Length - 5) + ".bgyml" : rel;
        }

        /// <summary>
        /// <c>Work/Model/Etc/A/output/textures/B.ftxb</c> is the texture <c>Tex/A.B.bntx</c>.
        /// </summary>
        public static string TexturePath(string work)
        {
            const string prefix = "Work/Model/Etc/";
            if (string.IsNullOrEmpty(work) || !work.StartsWith(prefix))
                return null;
            string rest = work.Substring(prefix.Length);
            int slash = rest.IndexOf('/');
            if (slash <= 0)
                return null;
            string stem = System.IO.Path.GetFileNameWithoutExtension(rest);
            return $"Tex/{rest.Substring(0, slash)}.{stem}.bntx";
        }

        static RaidersEnvParams FromByml(Dictionary<string, object> root, string name)
        {
            var p = new RaidersEnvParams { Name = name };
            var group = Hash(root, "Group");

            var lighting = Hash(Hash(group, "ライティング"), "Group");
            p.ReadMainLight(Hash(lighting, "メインライト"), "Intensity");

            var envMap = Hash(lighting, "環境マップ");
            p.IrradianceIntensity = Byml.GetFloat(
                envMap,
                "IrradianceIntensity",
                p.IrradianceIntensity
            );
            p.CubeMapEmissionScale = Byml.GetFloat(
                envMap,
                "CubeMapEmissionScale",
                p.CubeMapEmissionScale
            );
            p.CubeMapDirectLightScale = Byml.GetFloat(
                envMap,
                "CubeMapDirectLightScale",
                p.CubeMapDirectLightScale
            );
            p.EnvMapTexture = TexturePath(Byml.GetString(Hash(envMap, "ResrouceTexture"), "Path"));

            p.ReadSky(Hash(lighting, "天球"));

            var fog = Hash(Hash(group, "フォグ"), "Group");
            p.ReadDepthFog(Hash(fog, "デプスフォグ"));
            p.ReadHeightFog(Hash(fog, "高さフォグ"));

            var radial = Hash(fog, "メインライト散乱フォグ");
            p.ReadRadialFog(radial);
            p.RadialFogShadowInfluence = Byml.GetFloat(
                radial,
                "ShadowInfluence",
                p.RadialFogShadowInfluence
            );

            var exp = Hash(fog, "指数関数的高さフォグ");
            p.ExpFogBaseDensity = Byml.GetFloat(exp, "BaseDensity", p.ExpFogBaseDensity);
            p.ExpFogStartHeight = Byml.GetFloat(exp, "StartHeight", p.ExpFogStartHeight);
            p.ExpFogStartDensity = Byml.GetFloat(exp, "StartDensity", p.ExpFogStartDensity);
            p.ExpFogEndHeight = Byml.GetFloat(exp, "EndHeight", p.ExpFogEndHeight);
            p.ExpFogEndDensity = Byml.GetFloat(exp, "EndDensity", p.ExpFogEndDensity);
            p.ExpFogColor = Color(exp, "Color", p.ExpFogColor);
            p.ExpFogIntensity = Byml.GetFloat(exp, "Intensity", p.ExpFogIntensity);
            p.ExpFogStartColorDistance = Byml.GetFloat(
                exp,
                "StartColorDistance",
                p.ExpFogStartColorDistance
            );
            p.ExpFogStartColorScale = Byml.GetFloat(
                exp,
                "StartColorScale",
                p.ExpFogStartColorScale
            );
            p.ExpFogEndColorDistance = Byml.GetFloat(
                exp,
                "EndColorDistance",
                p.ExpFogEndColorDistance
            );
            p.ExpFogEndColorScale = Byml.GetFloat(exp, "EndColorScale", p.ExpFogEndColorScale);

            var bake = Hash(Hash(Hash(group, "影"), "Group"), "ベイク");
            p.ReadBake(bake);
            p.BakeShadowFldBGIntensOffset = Byml.GetFloat(
                bake,
                "BakeShadowFldBGIntensOffset",
                p.BakeShadowFldBGIntensOffset
            );
            p.BakeShadowFldBGIntensScale = Byml.GetFloat(
                bake,
                "BakeShadowFldBGIntensScale",
                p.BakeShadowFldBGIntensScale
            );
            p.BakeAOFldBGIntensOffset = Byml.GetFloat(
                bake,
                "BakeAOFldBGIntensOffset",
                p.BakeAOFldBGIntensOffset
            );
            p.BakeAOFldBGIntensScale = Byml.GetFloat(
                bake,
                "BakeAOFldBGIntensScale",
                p.BakeAOFldBGIntensScale
            );
            p.BakeAOFldBGMainLightOcclude = Byml.GetFloat(
                bake,
                "BakeAOFldBGMainLightOcclude",
                p.BakeAOFldBGMainLightOcclude
            );
            return p;
        }

        static Dictionary<string, object> Hash(Dictionary<string, object> hash, string key) =>
            EnvParams.Hash(hash, key);

        static Vector4 Color(Dictionary<string, object> hash, string key, Vector4 fallback) =>
            EnvParams.Color(hash, key, fallback);
    }
}
