using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using BfresLibrary;
using PlayerViewer.Core;
using PlayerViewer.Core.Formats;

namespace PlayerViewer.Env
{
    /// <summary>A Raiders scene the Stage choice offers, with its lighting variants.</summary>
    public sealed class RaidersScene
    {
        public string Name;

        /// <summary>Variant names, such as Normal or Night, and the rendering param group each uses.</summary>
        public List<(string Variant, string Group)> Variants = new();
    }

    /// <summary>
    /// The Raiders scenes whose lighting the viewer can stand in for: each scene's
    /// <c>FieldEnvSetParam</c> names a rendering param group per variant, and the screens drawn
    /// outside a scene pack (the customise screen, the photo mode) add their own groups.
    /// </summary>
    public static class RaidersEnvCatalog
    {
        //Groups no scene pack names: the screens the game sets up in code.
        static readonly string[] ScreenGroups = { "Customize", "Snapshot", "General", "RelicResult" };

        static readonly ConditionalWeakTable<Romfs, List<RaidersScene>> _cache = new();

        //Below this ambient luminance a scene relies on exposure the viewer does not apply.
        const float MinAmbient = 0.02f;

        /// <summary>
        /// The scenes, the customise screen first. A variant is left out when its group has no
        /// env map and a sky without a panorama, since its ambient comes from the stage itself,
        /// or when its ambient is near black. Reads every scene pack; call it off the UI thread.
        /// </summary>
        public static List<RaidersScene> ListScenes(Romfs romfs)
        {
            lock (_cache)
                if (_cache.TryGetValue(romfs, out var cached))
                    return cached;

            var usable = new Dictionary<string, bool>(StringComparer.Ordinal);
            var skyHasPanorama = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            bool Usable(string group)
            {
                if (!usable.TryGetValue(group, out bool ok))
                {
                    var p = RaidersEnv.Load(romfs, group);
                    ok =
                        p != null
                        && (p.EnvMapTexture != null || SkyHasPanorama(romfs, p, skyHasPanorama))
                        && !TooDark(romfs, group);
                    usable[group] = ok;
                }
                return ok;
            }

            var scenes = new List<RaidersScene>();
            foreach (var group in ScreenGroups)
                if (Usable(group))
                    scenes.Add(new RaidersScene { Name = group, Variants = { ("Normal", group) } });

            var named = new List<RaidersScene>();
            foreach (var path in romfs.FindFiles("Pack/Scene", "*.pack.zs"))
            {
                string name = Path.GetFileName(path)[..^".pack.zs".Length];
                var variants = ReadVariants(romfs, name);
                if (variants == null)
                    continue;
                var scene = new RaidersScene { Name = name };
                foreach (var (variant, group) in variants)
                    if (Usable(group))
                        scene.Variants.Add((variant, group));
                if (scene.Variants.Count > 0)
                    named.Add(scene);
            }
            var byName = named.ToDictionary(s => s.Name, StringComparer.Ordinal);
            scenes.AddRange(
                named
                    .Where(s => !ScreenGroups.Contains(s.Name) && !NumberedCopy(s, byName))
                    .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            );

            lock (_cache)
                _cache.AddOrUpdate(romfs, scenes);
            return scenes;
        }

        //Camp01 to Camp03 are the hub at each point of the story, lit exactly as Camp, the
        //scene the game enters and routes from.
        static bool NumberedCopy(RaidersScene scene, Dictionary<string, RaidersScene> byName)
        {
            string stem = scene.Name.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
            return stem.Length < scene.Name.Length
                && byName.TryGetValue(stem, out var original)
                && original.Variants.SequenceEqual(scene.Variants);
        }

        //Normal first, the rest as the file lists them.
        static List<(string, string)> ReadVariants(Romfs romfs, string scene)
        {
            var data = romfs.ReadFile($"Pack/Scene/{scene}.pack");
            if (data == null)
                return null;
            var sarc = new Sarc(data);
            string file = sarc.FindFile(n =>
                n.StartsWith("Gyml/") && n.EndsWith(".game__gfx__parameter__FieldEnvSetParam.bgyml")
            );
            if (file == null)
                return null;
            var dict = Byml.AsHash(
                Byml.AsHash(new Byml(sarc.GetFile(file)).Root).GetValueOrDefault("FieldEnvDict")
            );
            if (dict == null)
                return null;
            var variants = new List<(string, string)>();
            foreach (var (variant, value) in dict)
            {
                string work = value as string;
                if (string.IsNullOrEmpty(work))
                    continue;
                string group = Path.GetFileName(work).Split('.')[0];
                variants.Add((variant, group));
            }
            return variants
                .OrderBy(v => v.Item1 == "Normal" ? 0 : 1)
                .ThenBy(v => v.Item1, StringComparer.Ordinal)
                .ToList();
        }

        static bool TooDark(Romfs romfs, string group)
        {
            using var frame = RaidersFrame.Load(romfs, group, reflections: false);
            if (frame?.AmbientSH == null)
                return true;
            var mean = SkyAmbient.Mean(frame.AmbientSH);
            return 0.2126f * mean.X + 0.7152f * mean.Y + 0.0722f * mean.Z < MinAmbient;
        }

        //Whether the group's sky model has a texture to light the scene with.
        static bool SkyHasPanorama(Romfs romfs, EnvParams p, Dictionary<string, bool> known)
        {
            if (!p.SkyEnable || string.IsNullOrEmpty(p.SkyActor))
                return false;
            string actor = Path.GetFileName(p.SkyActor).Split('.')[0];
            if (known.TryGetValue(actor, out bool has))
                return has;
            has = false;
            try
            {
                var data = romfs.ReadModel(actor);
                if (data != null)
                {
                    var model = new ResFile(new MemoryStream(data)).Models.Values.FirstOrDefault();
                    var shape = model
                        ?.Shapes.Values.OrderByDescending(s =>
                            model.VertexBuffers[s.VertexBufferIndex].VertexCount
                        )
                        .FirstOrDefault();
                    has = shape != null && model.Materials[shape.MaterialIndex].TextureRefs.Count > 0;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Env] Sky {actor}: {ex.Message}");
            }
            known[actor] = has;
            return has;
        }
    }
}
