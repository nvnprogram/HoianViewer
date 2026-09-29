using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PlayerViewer.Core;
using PlayerViewer.Core.Formats;

namespace PlayerViewer.Env
{
    /// <summary>One lighting variant of a scene, such as a stage's Day or Night.</summary>
    public class SceneEnv
    {
        public string Scene;
        public string Variant;
        public EnvParams Params;

        /// <summary>
        /// Whether the variant names its own gsys environment set. The fog objects the
        /// environment block's fog rows come from live in that set, and the ones checked carry
        /// none, so such a variant is drawn without them.
        /// </summary>
        public bool NamedEnvSet;

        /// <summary>Whether the set is a RenderingNight gyml, the game's night time of day.</summary>
        public bool Night;
    }

    /// <summary>
    /// Finds the rendering params a scene uses, through its FieldEnv gyml: each <c>EnvSet*</c>
    /// key there names one RenderingDay, RenderingNight or similar gyml in the scene's pack.
    /// </summary>
    public static class EnvCatalog
    {
        //Versus and coop stages only. The lobbies, the plaza and the shop rely on exposure the
        //viewer does not apply, and read several times too bright without it.
        static readonly string[] ListedPrefixes = { "Vss_", "Cop_" };

        /// <summary>The stage scenes under Pack/Scene whose lighting the viewer can reproduce.</summary>
        public static List<string> ListScenes(Romfs romfs)
        {
            var names = new List<string>();
            foreach (var file in romfs.FindFiles("Pack/Scene", "*.pack.zs"))
            {
                string name = Path.GetFileName(file);
                name = name.Substring(0, name.Length - ".pack.zs".Length);
                if (ListedPrefixes.Any(name.StartsWith))
                    names.Add(name);
            }
            return names;
        }

        /// <summary>Every lighting variant of a scene, in FieldEnv order. Empty if it has none.</summary>
        public static List<SceneEnv> Load(Romfs romfs, string scene)
        {
            var result = new List<SceneEnv>();
            var data = romfs.ReadFile($"Pack/Scene/{scene}.pack");
            if (data == null)
                return result;
            var pack = new Sarc(data);

            string fieldEnvPath = pack.FindFile(n =>
                n.StartsWith("Gyml/") && n.Contains("__FieldEnv") && n.EndsWith(".bgyml")
            );
            if (fieldEnvPath == null)
                return result;

            var fieldEnv = Byml.AsHash(new Byml(pack.GetFile(fieldEnvPath)).Root);
            if (fieldEnv == null)
                return result;

            foreach (var (key, value) in fieldEnv)
            {
                if (!key.StartsWith("EnvSet"))
                    continue;
                string work = value as string;
                if (value is List<object> list)
                    work = list.OfType<string>().FirstOrDefault(s => s.Length > 0);
                if (string.IsNullOrEmpty(work))
                    continue;

                var bytes =
                    Romfs.ResolveWorkPath(pack, work)
                    ?? romfs.ReadFile("Gyml/" + Path.GetFileName(ToBgyml(work)));
                if (bytes == null)
                    continue;

                var root = Byml.AsHash(new Byml(bytes).Root);
                string variant = key.Length > "EnvSet".Length ? key.Substring(6) : "Default";
                result.Add(
                    new SceneEnv
                    {
                        Scene = scene,
                        Variant = variant,
                        Params = EnvParams.FromByml(root, $"{scene} {variant}"),
                        NamedEnvSet = NamesEnvSet(root),
                        Night = work.Contains("__RenderingNight.", StringComparison.Ordinal),
                    }
                );
            }
            return result;
        }

        static string ToBgyml(string work) =>
            work.EndsWith(".gyml") ? work.Substring(0, work.Length - 5) + ".bgyml" : work;

        static bool NamesEnvSet(Dictionary<string, object> root)
        {
            if (root == null)
                return false;
            if (Byml.GetString(root, "DefaultGSysEnvSet").Length > 0)
                return true;
            var custom = root.TryGetValue("CustomGSysEnvSet", out var c)
                ? c as Dictionary<string, object>
                : null;
            return Byml.GetString(custom, "GEnvName").Length > 0;
        }
    }
}
