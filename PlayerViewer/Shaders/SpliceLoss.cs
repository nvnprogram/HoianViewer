using System;
using System.Collections.Generic;
using BfresLibrary;
using PlayerViewer.Materials;

namespace PlayerViewer.Shaders
{
    /// <summary>
    /// What a splice of a material leaves out because the ubershader cannot express it, in
    /// words for the user.
    /// </summary>
    public static class SpliceLoss
    {
        static readonly Dictionary<string, string> Names = new(StringComparer.Ordinal)
        {
            ["enable_ao"] = "AO map",
            ["enable_emission_map"] = "emission map",
            ["enable_opacity_tex"] = "opacity map",
            ["enable_transmission_map"] = "transmission map",
            ["enable_sfxmask"] = "effect mask",
            ["disable_edge_light_sfx_mask"] = "edge light effect mask",
            ["texcoord_select_ao"] = "AO map UV set",
            ["texcoord_select_emmmap"] = "emission map UV set",
            ["texcoord_select_opacity"] = "opacity map UV set",
            ["texcoord_select_trsmap"] = "transmission map UV set",
            ["texcoord_select_sfxmask"] = "effect mask UV set",
            ["restex_id_thickness_map"] = "thickness map source",
            ["comp_paint_target"] = "paint target",
            ["disable_hue_peak_offset"] = "hue peak offset",
            ["pixel_expression0"] = "pixel expression",
            ["vertex_expression0"] = "vertex expression",
            ["is_enable_field_noise"] = "field noise",
            ["is_enable_field_noise_metalness"] = "field noise metalness",
            ["field_noise_material_type"] = "field noise material",
        };

        /// <summary>A feature an option value stands for, as a short phrase.</summary>
        public static string Feature(string option, string value)
        {
            if (!Names.TryGetValue(option, out var name))
            {
                name = option;
                foreach (var prefix in new[] { "is_enable_", "enable_", "is_" })
                    if (name.StartsWith(prefix, StringComparison.Ordinal))
                    {
                        name = name.Substring(prefix.Length);
                        break;
                    }
                name = name.Replace('_', ' ');
            }
            if (value == "1" || option.EndsWith("_expression0", StringComparison.Ordinal))
                return name;
            return value == "0" ? name + " off" : name + " " + value;
        }

        /// <summary>The feature a sampler is the map of.</summary>
        public static string SamplerFeature(string sampler)
        {
            string option = MaterialSamplers.OptionFor(sampler);
            return option != null ? Feature(option, "1") : sampler;
        }

        /// <summary>
        /// The features a splice of this vector leaves out
        /// </summary>
        public static List<string> Of(
            UberContext uber,
            IReadOnlyDictionary<string, string> full,
            Material material,
            MissingFeatures.Layer layer
        )
        {
            var lost = new List<string>();
            var reach = uber?.Reach;
            if (reach == null)
                return lost;
            bool Covered(string name) => layer != null && layer.Covered.Contains(name);
            var noEffect = uber.NoEffect;
            foreach (var (option, value) in reach.Lost(full))
                if (noEffect.Contains(option) && !Covered(option))
                    Add(lost, Feature(option, value));
            foreach (var sampler in reach.UnboundSamplers)
                if (
                    !MaterialSamplers.EngineProvided(sampler)
                    && !Covered(sampler)
                    && MaterialSamplers.Bound(material, sampler)
                )
                    Add(lost, SamplerFeature(sampler));
            if (layer != null)
                foreach (var feature in layer.Missing)
                    Add(lost, feature);
            return lost;
        }

        /// <summary>Whether binding this sampler changes nothing in a splice.</summary>
        public static bool SamplerHasNoEffect(UberContext uber, string sampler) =>
            uber?.Reach != null && uber.Reach.UnboundSamplers.Contains(sampler);

        static void Add(List<string> into, string feature)
        {
            if (!into.Contains(feature))
                into.Add(feature);
        }
    }
}
