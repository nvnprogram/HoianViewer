using System;
using System.Collections.Generic;
using System.Linq;
using BfresLibrary;
using Gsys;
using PlayerViewer.Materials;

namespace PlayerViewer.Conversion
{
    /// <summary>
    /// Shader options of a converted model's materials: values the two games give different ids
    /// for the same feature, mapped both ways. The data behind each entry is in raiders.md.
    /// </summary>
    public static class OptionMap
    {
        /// <summary>The shading model the map is for; other materials are left alone.</summary>
        public const string ShadingModel = "hoian_uber";

        //The same expression under each game's id: models both games ship differ only in this.
        static readonly (string Option, string Thunder, string Spiral)[] Values =
        {
            ("vertex_expression0", "3630540955", "3963149084"),
            ("vertex_expression0", "3890018906", "2067287268"),
        };

        /// <summary>Gives each material the Splatoon 3 ids of its options. Returns the materials changed.</summary>
        public static int ToThunder(ResFile res)
        {
            int changed = 0;
            foreach (var mat in Materials(res))
                if (MapValues(mat, toSpiral: false))
                    changed++;
            return changed;
        }

        /// <summary>
        /// Gives each material the Raiders ids of its options. <paramref name="served"/> says
        /// whether the game has a program for an option set; with it, returns the names of the
        /// materials it has none for, which draw only through the splicer.
        /// </summary>
        public static List<string> ToSpiral(
            ResFile res,
            Func<Dictionary<string, string>, bool> served
        )
        {
            var unserved = new List<string>();
            foreach (var model in res.Models.Values)
            {
                var skins = model
                    .Shapes.Values.GroupBy(s => (int)s.MaterialIndex)
                    .ToDictionary(
                        g => g.Key,
                        g => g.Select(s => (uint)s.VertexSkinCount).Distinct().ToArray()
                    );
                for (int i = 0; i < model.Materials.Count; i++)
                {
                    var mat = model.Materials[i];
                    if (mat.ShaderAssign?.ShadingModelName != ShadingModel)
                        continue;
                    MapValues(mat, toSpiral: true);
                    if (served == null || !skins.TryGetValue(i, out var counts))
                        continue;
                    if (!counts.All(n => Served(mat, n, served)))
                        unserved.Add(mat.Name);
                }
            }
            return unserved;
        }

        static bool Served(
            Material mat,
            uint skinCount,
            Func<Dictionary<string, string>, bool> served
        )
        {
            var options = GsysShaderOptions.BuildStaticOptions(mat);
            GsysShaderOptions.AddDynamicOptions(
                options,
                skinCount,
                GsysShaderOptions.AssignTypes[0]
            );
            return served(options);
        }

        static bool MapValues(Material mat, bool toSpiral)
        {
            var options = mat.ShaderAssign?.ShaderOptions;
            if (options == null)
                return false;
            bool changed = false;
            foreach (var (option, thunder, spiral) in Values)
            {
                if (!options.ContainsKey(option))
                    continue;
                string from = toSpiral ? thunder : spiral;
                if (options[option].String != from)
                    continue;
                options.Set(option, new ResString { String = toSpiral ? spiral : thunder });
                changed = true;
            }
            return changed;
        }

        static IEnumerable<Material> Materials(ResFile res) =>
            res
                .Models.Values.SelectMany(m => m.Materials.Values)
                .Where(m => m.ShaderAssign?.ShadingModelName == ShadingModel);
    }
}
