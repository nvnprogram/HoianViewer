using System;
using System.Collections.Generic;
using System.Linq;
using BfresLibrary;
using OpenTK;

namespace PlayerViewer.Rigging
{
    /// <summary>A skeleton edit: a rename, a new rest transform (with the one it replaced), or both.</summary>
    public sealed record BoneChange(string Bone, string Rename, BoneSrt? Srt, BoneSrt Was);

    /// <summary>
    /// The skeleton edits between two versions of a model, and their replay on another model of
    /// the same skeleton, as a gendered hair's _F and _M.
    /// </summary>
    public static class SkeletonEdits
    {
        /// <summary>
        /// The edits that turned <paramref name="original"/>'s skeleton into
        /// <paramref name="edited"/>'s: bones renamed (a bone gone and a new one under the same
        /// parent at the same rest) and bones given another rest transform. Bones named in
        /// <paramref name="removed"/> are gone on purpose.
        /// </summary>
        public static List<BoneChange> Diff(
            Model original,
            Model edited,
            IReadOnlySet<string> removed
        )
        {
            var changes = new List<BoneChange>();
            var before = original.Skeleton.Bones.Values.ToList();
            var after = edited.Skeleton.Bones.Values.ToList();
            var afterNames = after.Select(b => b.Name).ToHashSet();
            var beforeNames = before.Select(b => b.Name).ToHashSet();
            string Parent(IList<Bone> list, Bone b) =>
                b.ParentIndex >= 0 && b.ParentIndex < list.Count ? list[b.ParentIndex].Name : null;
            var renamed = new Dictionary<string, string>();
            var added = after.Where(b => !beforeNames.Contains(b.Name)).ToList();
            //A renamed bone may also have been moved: among new bones under the same parent, the
            //one alone there, else the nearest within a few centimetres.
            foreach (
                var gone in before.Where(b =>
                    !afterNames.Contains(b.Name) && !removed.Contains(b.Name)
                )
            )
            {
                var at = BoneEdit.Read(gone).Translation;
                string parent = Parent(before, gone) is string q
                    ? renamed.GetValueOrDefault(q, q)
                    : null;
                var siblings = added
                    .Where(b =>
                        !renamed.ContainsValue(b.Name)
                        && (Parent(after, b) is string p ? renamed.GetValueOrDefault(p, p) : null)
                            == parent
                    )
                    .ToList();
                var match =
                    siblings.Count == 1
                        ? siblings[0]
                        : siblings
                            .OrderBy(b => (BoneEdit.Read(b).Translation - at).Length)
                            .FirstOrDefault(b =>
                                (BoneEdit.Read(b).Translation - at).Length < 0.03f
                            );
                if (match != null)
                    renamed[gone.Name] = match.Name;
            }
            foreach (var bone in before)
            {
                string name = renamed.GetValueOrDefault(bone.Name, bone.Name);
                var now = after.FirstOrDefault(b => b.Name == name);
                if (now == null)
                    continue;
                var a = BoneEdit.Read(bone);
                var b = BoneEdit.Read(now);
                bool moved = !SameRest(a, b);
                if (moved || name != bone.Name)
                    changes.Add(
                        new BoneChange(
                            bone.Name,
                            name != bone.Name ? name : null,
                            moved ? b : null,
                            a
                        )
                    );
            }
            return changes;
        }

        static bool Same(Vector3 a, Vector3 b) => (a - b).Length < 1e-5f;

        /// <summary>The same rest transform; rotations compared as rotations, since -180 and 180 degrees are one.</summary>
        static bool SameRest(BoneSrt a, BoneSrt b)
        {
            if (!Same(a.Translation, b.Translation) || !Same(a.Scale, b.Scale))
                return false;
            var ra = ModelSkin.FromEulerXYZ(a.Rotation);
            var rb = ModelSkin.FromEulerXYZ(b.Rotation);
            for (int r = 0; r < 3; r++)
            for (int c = 0; c < 3; c++)
                if (MathF.Abs(ra[r, c] - rb[r, c]) > 1e-5f)
                    return false;
            return true;
        }

        /// <summary>
        /// Applies <paramref name="changes"/> to a model whose skeleton was
        /// <paramref name="original"/>'s: a bone is changed only where it still has the source's
        /// original rest, so a model with its own skeleton keeps it. The mesh stays in place.
        /// </summary>
        public static void Apply(
            ResFile res,
            Model model,
            Model original,
            List<BoneChange> changes,
            List<string> report
        )
        {
            var originals = original.Skeleton.Bones.Values.ToDictionary(b => b.Name);
            foreach (var change in changes)
            {
                var bones = model.Skeleton.Bones.Values.ToList();
                int index = bones.FindIndex(b => b.Name == change.Bone);
                if (index < 0)
                {
                    report.Add($"{change.Bone} is not on this model; its edit is skipped");
                    continue;
                }
                if (change.Srt is BoneSrt srt)
                {
                    var have = BoneEdit.Read(bones[index]);
                    if (!SameRest(have, BoneEdit.Read(originals[change.Bone])))
                        report.Add($"{change.Bone} rests differently here; its transform is kept");
                    else
                    {
                        //Only what the edit changed, so this model keeps its own way of writing the rest.
                        var was = change.Was;
                        var set = new BoneSrt(
                            Same(srt.Translation, was.Translation)
                                ? have.Translation
                                : srt.Translation,
                            SameRest(was with { Rotation = srt.Rotation }, was)
                                ? have.Rotation
                                : srt.Rotation,
                            Same(srt.Scale, was.Scale) ? have.Scale : srt.Scale
                        );
                        BoneEdit.SetTransform(model, index, set, false);
                        report.Add($"{change.Bone} moved as on the source");
                    }
                }
                if (change.Rename is string name)
                {
                    if (BoneEdit.NameProblem(model, index, name) is string problem)
                        report.Add($"{change.Bone} not renamed to {name}: {problem}");
                    else
                    {
                        BoneEdit.Rename(res, model, index, name);
                        report.Add($"{change.Bone} renamed to {name}");
                    }
                }
            }
        }
    }
}
