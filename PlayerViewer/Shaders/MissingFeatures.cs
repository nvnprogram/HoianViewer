using System;
using System.Collections.Generic;
using System.Linq;
using BfresLibrary;
using Gsys;
using PlayerViewer.Materials;

namespace PlayerViewer.Shaders
{
    /// <summary>
    /// Features a material uses that the Raiders ubershader has no code for (later fix with inserted bytecode)
    /// </summary>
    public static class MissingFeatures
    {
        /// <summary>The shading model the layer is for.</summary>
        public const string ShadingModel = "hoian_uber";

        public const string TeamColourMask = "team colour mask";
        public const string OpacityMap = "opacity map";
        public const string EmissionMap = "emission map";

        public sealed class Layer
        {
            /// <summary>The material as spliced, drawn and saved, or null when it is the one
            /// edited.</summary>
            public Material Drawn;

            /// <summary>Features rebuilt from what the ubershader can do.</summary>
            public readonly List<string> Approximated = new();

            /// <summary>Features left out: approximation off, or no free slot for one.</summary>
            public readonly List<string> Missing = new();

            /// <summary>Shader samplers and options whose feature the layer covers, so a splice
            /// is not reported as losing them.</summary>
            public readonly HashSet<string> Covered = new(StringComparer.Ordinal);
        }

        //A feature: the shader sampler of its map, the option the ubershader ignores (or null),
        //whether a material uses it, and the stand-in that rebuilds it on a copy.
        sealed record Feature(
            string Name,
            string Sampler,
            string Option,
            Func<Material, bool> Uses,
            Func<Material, bool> StandIn
        );

        static readonly Feature[] Features =
        {
            new(TeamColourMask, "_su0", null, UsesTeamMask, StandInTeamMask),
            new(OpacityMap, "_op0", "enable_opacity_tex", UsesOpacity, StandInOpacity),
            new(EmissionMap, "_e0", "enable_emission_map", UsesEmission, StandInEmission),
        };

        /// <summary>
        /// The layer for a material the shipped programs do not serve. Each feature it uses
        /// takes a stand-in when <paramref name="approximate"/>, else is left out.
        /// </summary>
        public static Layer Derive(Material edited, bool approximate)
        {
            var layer = new Layer();
            Material drawn = null;
            bool changed = false;
            foreach (var feature in Features)
            {
                if (!feature.Uses(edited))
                    continue;
                if (approximate && feature.StandIn(drawn ??= Copy(edited)))
                {
                    changed = true;
                    layer.Approximated.Add(feature.Name);
                    Cover(layer, feature);
                }
                else
                    layer.Missing.Add(feature.Name);
            }
            layer.Drawn = changed ? drawn : null;
            return layer;
        }

        static void Cover(Layer layer, Feature feature)
        {
            layer.Covered.Add(feature.Sampler);
            if (feature.Option != null)
                layer.Covered.Add(feature.Option);
        }

        //A copy whose options and sampler assigns can change; everything else is shared.
        static Material Copy(Material m)
        {
            var assign = m.ShaderAssign;
            return new Material
            {
                Name = m.Name,
                Flags = m.Flags,
                RenderInfos = m.RenderInfos,
                RenderState = m.RenderState,
                ShaderInfoV10 = m.ShaderInfoV10,
                TextureRefs = m.TextureRefs,
                Samplers = m.Samplers,
                ShaderParams = m.ShaderParams,
                ShaderParamData = m.ShaderParamData,
                UserData = m.UserData,
                VolatileFlags = m.VolatileFlags,
                ShaderAssign = new ShaderAssign
                {
                    ShaderArchiveName = assign.ShaderArchiveName,
                    ShadingModelName = assign.ShadingModelName,
                    Revision = assign.Revision,
                    AttribAssigns = assign.AttribAssigns,
                    SamplerAssigns = Copy(assign.SamplerAssigns),
                    ShaderOptions = Copy(assign.ShaderOptions),
                },
            };
        }

        static ResDict<ResString> Copy(ResDict<ResString> from)
        {
            var to = new ResDict<ResString>();
            foreach (var (key, value) in from)
                to.Add(key, new ResString { String = value?.String });
            return to;
        }

        static string Get(Material mat, string name)
        {
            var options = mat.ShaderAssign.ShaderOptions;
            return options.ContainsKey(name) ? options[name].String : null;
        }

        const string TargetAlbedo = "0",
            TargetEmission = "2",
            TargetDiscard = "10";
        const string OpFirst = "0",
            OpMultiply = "2",
            OpLerp = "4";
        const string Translucent = "2";
        const string OperandAlbedo = "0",
            OperandEmissionColor = "3",
            OperandMyTeamColor = "50";
        const string ChannelRed = "10";
        const int CalcSlots = 4,
            ResourceSlots = 3;

        static bool UsesTeamMask(Material mat) =>
            Get(mat, "team_color_map_type") == "2" && MaterialSamplers.Target(mat, "_su0") != null;

        static bool StandInTeamMask(Material mat) =>
            AddCalc(
                mat,
                MaterialSamplers.Target(mat, "_su0"),
                Get(mat, "texcoord_select_teamcolormap"),
                TargetAlbedo,
                OpLerp,
                ("A", OperandAlbedo),
                ("B", OperandMyTeamColor),
                ("C", null),
                ("C_channel", ChannelRed)
            );

        static bool UsesOpacity(Material mat) =>
            Get(mat, "enable_opacity_tex") == "1" && MaterialSamplers.Target(mat, "_op0") != null;

        static bool StandInOpacity(Material mat)
        {
            if (GsysShaderOptions.BuildStaticOptions(mat)["gsys_renderstate"] == Translucent)
                return false;
            if (
                !AddCalc(
                    mat,
                    MaterialSamplers.Target(mat, "_op0"),
                    Get(mat, "texcoord_select_opacity"),
                    TargetDiscard,
                    OpFirst,
                    ("A", null),
                    ("A_channel", ChannelRed)
                )
            )
                return false;
            Set(mat, "enable_opacity_tex", "0");
            return true;
        }

        static bool UsesEmission(Material mat) =>
            Get(mat, "enable_emission") == "1"
            && Get(mat, "enable_emission_map") == "1"
            && MaterialSamplers.Target(mat, "_e0") != null;

        static bool StandInEmission(Material mat)
        {
            if ((Get(mat, "emission_color_type") ?? "0") is not ("0" or GsysShaderOptions.Unset))
                return false;
            if (
                !AddCalc(
                    mat,
                    MaterialSamplers.Target(mat, "_e0"),
                    Get(mat, "texcoord_select_emmmap"),
                    TargetEmission,
                    OpMultiply,
                    ("A", null),
                    ("B", OperandEmissionColor)
                )
            )
                return false;
            Set(mat, "enable_emission_map", "0");
            return true;
        }

        static void Set(Material mat, string name, string value) =>
            mat.ShaderAssign.ShaderOptions.Set(name, new ResString { String = value });

        static bool AddCalc(
            Material mat,
            string sampler,
            string texcoord,
            string target,
            string op,
            params (string Operand, string Value)[] operands
        )
        {
            int res = Enumerable
                .Range(0, ResourceSlots)
                .FirstOrDefault(i => MaterialSamplers.Target(mat, $"_re{i}") == null, -1);
            int slot = Enumerable
                .Range(0, CalcSlots)
                .FirstOrDefault(i => Get(mat, $"enable_calc_color{i}") != "1", -1);
            if (res < 0 || slot < 0)
                return false;

            mat.ShaderAssign.SamplerAssigns.Set($"_re{res}", new ResString { String = sampler });
            if (texcoord != null && texcoord != GsysShaderOptions.Unset)
                Set(mat, $"texcoord_select_res{res}", texcoord);
            string calc = $"blitz_calc_color{slot}_";
            Set(mat, $"enable_calc_color{slot}", "1");
            Set(mat, calc + "replace_color", target);
            Set(mat, calc + "calc_type", op);
            Set(mat, calc + "clamp01", "0");
            foreach (var operand in new[] { "A", "B", "C", "D" })
            {
                Set(mat, calc + operand, "0");
                Set(mat, calc + operand + "_channel", "0");
            }
            foreach (var (operand, value) in operands)
                Set(mat, calc + operand, value ?? (9 + res).ToString());
            return true;
        }
    }
}
