using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PlayerViewer.Core;
using PlayerViewer.Phive;

namespace PlayerViewer.Physics
{
    /// <summary>What a cloth save does to the file and to the mod's packs, each step a line of its report.</summary>
    public static class ClothSave
    {
        /// <summary>Adds the AAMP entry every piece and collidable needs, which one added from a loose file may lack.</summary>
        public static List<string> AddMissingParams(ClothFile file)
        {
            var notes = new List<string>();
            if (file.Params == null)
                return notes;
            foreach (var piece in file.Container.ClothDatas)
                if (file.Params.MeshFor(piece.Name) == null)
                {
                    string root = file.SkeletonFor(piece)?.BoneNames.FirstOrDefault() ?? "";
                    file.Params.AddMesh(piece.Name, root);
                    notes.Add($"added cloth_mesh parameters for {piece.Name}");
                }
            foreach (var col in file.Container.Collidables)
                if (file.Params.CollidableFor(col.Name) == null)
                {
                    file.Params.AddCollidable(col.Name);
                    notes.Add($"added collidable parameters for {col.Name}");
                }
            return notes;
        }

        /// <summary>A warning for each piece the solver cannot build.</summary>
        public static IEnumerable<string> CompileWarnings(ClothFile file) =>
            file
                .Container.ClothDatas.Select(data =>
                    ClothPiece.Compile(file, data, out string why) == null
                        ? $"warning: piece {data.Name} does not compile: {why}"
                        : null
                )
                .Where(w => w != null)
                .ToList();

        /// <summary>
        /// Writes the cloth as <paramref name="entry"/> into each actor's pack under a mod romfs
        /// root, wiring the pack when needed, then reads each back through its ClothList. With
        /// <paramref name="only"/> the ClothList names this cloth alone. Throws when a pack is
        /// missing or does not lead back to the cloth.
        /// </summary>
        public static List<string> WritePacks(
            ClothPacks packs,
            Romfs romfs,
            string root,
            IReadOnlyList<string> actors,
            string entry,
            byte[] bytes,
            bool only
        )
        {
            var report = new List<string>();
            var target = new ClothPacks(new Romfs(romfs.Root, root, true, romfs.SdodrRoot));
            foreach (var actor in actors)
            {
                //A pack already in the target folder is built on, so earlier writes there are kept.
                bool inTarget = File.Exists(
                    Path.Combine(root, "Pack", "Actor", actor + ".pack.zs")
                );
                var pack =
                    (inTarget ? target.ReadPack(actor) : null)
                    ?? packs.ReadPack(actor)
                    ?? throw new InvalidOperationException($"No actor pack named {actor}");
                var notes = packs.PutCloth(pack, entry, bytes, only);
                string path = packs.WritePack(root, pack);
                report.Add($"{actor}: {path}");
                report.AddRange(notes.Select(n => "  " + n));
            }
            //Read back through the same lookup the game uses.
            var check = new ClothPacks(new Romfs(romfs.Root, root, true, romfs.SdodrRoot));
            foreach (var actor in actors)
            {
                var src = check.FindFor(actor);
                bool ok =
                    src != null
                    && !src.IsNew
                    && check.ReadCloth(src)?.AsSpan().SequenceEqual(bytes) == true;
                if (!ok)
                    throw new InvalidOperationException(
                        $"{actor}: the written pack does not lead back to the cloth"
                    );
            }
            report.Add($"{bytes.Length} B, read back through each pack's ClothList");
            return report;
        }
    }
}
