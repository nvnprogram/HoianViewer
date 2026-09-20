using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BfresEditor;
using ShaderBundler;
using ShaderLibrary;

namespace PlayerViewer.Shaders
{
    /// <summary>
    /// Drops an embedded shader archive that an older specialiser built at load
    /// </summary>
    public static class BundleFreshness
    {
        /// <summary>
        /// Removes every embedded archive that is not on the current codegen and reports
        /// their names.
        /// </summary>
        public static List<string> Prune(BFRES bfres, ISet<ulong> uberCodeHashes)
        {
            var dropped = new List<string>();
            var res = bfres?.ResFile;
            if (res == null || BundleStamp.Codegen == BundleStamp.Unstamped)
                return dropped;

            foreach (var name in res.ExternalFiles.Keys.ToList())
            {
                if (!name.EndsWith(".bfsha", StringComparison.Ordinal))
                    continue;

                BundleAge age;
                try
                {
                    var archive = new BfshaFile(new MemoryStream(res.ExternalFiles[name].Data));
                    age = BundleStamp.Age(archive, uberCodeHashes);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Bundle] embedded {name}: {ex.Message}");
                    age = BundleAge.Stale;
                }

                if (age != BundleAge.Stale)
                    continue;

                res.ExternalFiles.RemoveKey(name);
                dropped.Add(name);
            }

            if (dropped.Count > 0)
            {
                bfres.ShaderFiles.Clear();
                Console.WriteLine(
                    $"[Bundle] dropped {dropped.Count} archive(s) from an older specialiser "
                        + $"(codegen {BundleStamp.Codegen} is current): {string.Join(", ", dropped)}"
                );
            }
            return dropped;
        }

        /// <summary>
        /// Runs every mesh's archive probe again, to drop the cached archives
        /// </summary>
        public static void Reprobe(BFRES bfres, IReadOnlyList<BfresModelAsset> models)
        {
            if (bfres == null || models == null)
                return;
            foreach (var model in models)
            {
                var fmdl = model.ResModel;
                if (fmdl == null)
                    continue;
                foreach (var mesh in model.Meshes)
                {
                    if (mesh.MaterialAsset is not BfshaRenderer renderer || mesh.Shape == null)
                        continue;
                    try
                    {
                        renderer.TryLoadShader(bfres, fmdl, mesh.Shape, mesh);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[Bundle] reprobe {mesh.Name}: {ex.Message}");
                    }
                }
            }
        }
    }
}
