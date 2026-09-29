using System;
using System.Collections.Generic;
using System.Linq;
using PlayerViewer.Core;
using PlayerViewer.Phive;
using PlayerViewer.Rigging;

namespace PlayerViewer.HairGen
{
    /// <summary>
    /// The cloth side of the physics metadata: the record a save writes of the cloth, and on load
    /// the rig built again compared with the saved model and the saved cloth brought back.
    /// </summary>
    public static class ProvenanceRestore
    {
        public static string Sha(byte[] data) =>
            data == null
                ? null
                : Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data));

        /// <summary>
        /// The record of a committed cloth. <paramref name="generated"/> is the generator's own
        /// build of it, null when the cloth is not the generated one; the hand edits are the
        /// difference between the two.
        /// </summary>
        public static PhysicsProvenance.ClothRecord Record(
            string entry,
            string packs,
            byte[] committed,
            ClothFile generated
        )
        {
            var record = new PhysicsProvenance.ClothRecord
            {
                Entry = entry,
                Packs = packs,
                Generated = generated != null,
            };
            var saved = ClothFile.Load(committed);
            record.Hash = Sha(saved.Save());
            record.KeepSaved(committed);
            if (generated != null)
            {
                var built = ClothFile.Load(generated.Snapshot());
                record.GeneratedHash = Sha(built.Save());
                var diff = ClothDiff.Compare(built, saved);
                record.Edits = diff.Edits;
                record.Structural = diff.Structural;
            }
            return record;
        }

        /// <summary>A model's bytes without the metadata file, saved again; the same array when it has none.</summary>
        public static byte[] WithoutProvenance(byte[] model)
        {
            var res = BfresBytes.Read(model);
            if (!res.ExternalFiles.ContainsKey(PhysicsProvenance.FileName))
                return model;
            res.ExternalFiles.RemoveKey(PhysicsProvenance.FileName);
            return BfresBytes.ToBytes(res);
        }

        /// <summary>How two models differ in what the rig writes: the skeleton, and the weights per vertex.</summary>
        public readonly record struct RigComparison(
            bool Identical,
            int Bones,
            int OtherBones,
            bool SameBones,
            float Worst,
            int Differ,
            int Total
        )
        {
            public bool BonesDiffer => !Identical && !SameBones;

            public override string ToString()
            {
                if (Identical)
                    return "identical";
                string bones = SameBones
                    ? $"same {Bones} bones ({(Worst > 0 ? $"worst world difference {Worst:0.#e0}" : "exactly")})"
                    : $"bones differ ({Bones} against {OtherBones})";
                return $"{bones}, {Differ} of {Total} vertices weighted differently";
            }
        }

        public static RigComparison CompareModels(byte[] a, byte[] b)
        {
            if (a.AsSpan().SequenceEqual(b))
                return new RigComparison(true, 0, 0, true, 0, 0, 0);
            var x = SkinnedMesh.FromModel(BfresBytes.FirstModel(a));
            var y = SkinnedMesh.FromModel(BfresBytes.FirstModel(b));
            bool sameBones = x.Bones.Select(o => o.Name).SequenceEqual(y.Bones.Select(o => o.Name));
            float worst = 0;
            if (sameBones)
                for (int i = 0; i < x.Bones.Count; i++)
                for (int r = 0; r < 4; r++)
                for (int c = 0; c < 4; c++)
                    worst = Math.Max(
                        worst,
                        Math.Abs(x.Bones[i].World[r, c] - y.Bones[i].World[r, c])
                    );
            int differ = 0,
                total = 0;
            for (int p = 0; p < Math.Min(x.Parts.Count, y.Parts.Count); p++)
            {
                var pa = x.Parts[p];
                var pb = y.Parts[p];
                for (int v = 0; v < Math.Min(pa.Rest.Length, pb.Rest.Length); v++)
                {
                    total++;
                    var wa = Influence(x, pa, v);
                    var wb = Influence(y, pb, v);
                    if (
                        wa
                            .Keys.Union(wb.Keys)
                            .Any(k =>
                                Math.Abs(wa.GetValueOrDefault(k) - wb.GetValueOrDefault(k))
                                > 0.5f / 255
                            )
                    )
                        differ++;
                }
            }
            return new RigComparison(
                false,
                x.Bones.Count,
                y.Bones.Count,
                sameBones,
                worst,
                differ,
                total
            );
        }

        static Dictionary<string, float> Influence(SkinnedMesh mesh, MeshPart part, int v)
        {
            var map = new Dictionary<string, float>();
            for (int k = 0; k < part.Bones[v].Length; k++)
            {
                string name = mesh.Bones[part.Bones[v][k]].Name;
                map[name] = map.GetValueOrDefault(name) + part.Weights[v][k];
            }
            return map;
        }

        /// <summary>Applies the record's hand edits to the file, a line for each that fails; returns how many applied.</summary>
        public static int ApplyEdits(
            PhysicsProvenance.ClothRecord cloth,
            ClothFile file,
            List<string> report
        )
        {
            var failed = ClothDiff.Apply(file, cloth.Edits);
            report.AddRange(failed.Select(f => "edit not applied: " + f));
            return cloth.Edits.Count - failed.Count;
        }

        /// <summary>
        /// What <see cref="RestoreCloth"/> leaves to the document: commit the edits made to its
        /// file, then take <see cref="Take"/> in its place when set, as hand edited or not.
        /// </summary>
        public readonly record struct ClothRestore(
            bool Commit,
            byte[] Take,
            bool HandEdited,
            bool Identical
        );

        /// <summary>
        /// The saved cloth brought back onto <paramref name="file"/>, the cloth just generated,
        /// through its hand edits, else through the saved copy. See hair-gen.md for when the copy
        /// is taken. <paramref name="rebuilt"/>: the limbs were rebuilt on load.
        /// <paramref name="rigDiffers"/>: the rig built again has other bones than the shown
        /// model, whose <paramref name="shownBones"/> the saved copy must move.
        /// </summary>
        public static ClothRestore RestoreCloth(
            PhysicsProvenance.ClothRecord cloth,
            ClothFile file,
            string entry,
            bool rebuilt,
            bool rigDiffers,
            ISet<string> shownBones,
            List<string> report
        )
        {
            if (cloth.Entry != null && cloth.Entry != entry)
                report.Add($"the saved cloth was {cloth.Entry}, the generated one is {entry}");
            if (!cloth.Generated)
            {
                report.Add(
                    "the saved session's cloth was not the generated one; the generated cloth is shown"
                );
                return default;
            }
            if (
                rigDiffers
                && cloth.Saved != null
                && cloth.SavedBytes() is var fits
                && Fits(fits, shownBones)
            )
            {
                report.Add(
                    "the rig built again has other bones than the saved model, so the saved cloth is shown; "
                        + "changing a limb builds the model and its cloth again"
                );
                return new ClothRestore(false, fits, false, false);
            }
            int applied = ApplyEdits(cloth, file, report);
            report.AddRange(cloth.Structural.Select(s => "not restored (structural): " + s));
            bool commit = applied > 0;
            if (Sha(file.Save()) == cloth.Hash)
            {
                report.Add($"cloth identical to the saved one, {applied} hand edit(s) applied");
                return new ClothRestore(commit, null, false, true);
            }
            if (cloth.Saved == null || rebuilt)
            {
                report.Add(
                    $"cloth differs from the saved one, {applied} of {cloth.Edits.Count} hand edit(s) applied"
                );
                return new ClothRestore(commit, null, false, false);
            }
            var savedBytes = cloth.SavedBytes();
            var noise = ClothDiff.Compare(
                ClothFile.Load(file.Snapshot()),
                ClothFile.Load(savedBytes)
            );
            if (noise.Structural.Count > 0)
            {
                report.Add(
                    $"cloth differs from the saved one in structure ({noise.Structural[0]}), the generated cloth with {applied} hand edit(s) is shown"
                );
                return new ClothRestore(commit, null, false, false);
            }
            bool edited = cloth.Edits.Count + cloth.Structural.Count > 0;
            report.Add(
                $"cloth restored from the saved copy{(Sha(ClothFile.Load(savedBytes).Save()) == cloth.Hash ? " (identical)" : " (hash differs)")}: "
                    + $"built again with the hand edits it differed in {noise.Edits.Count} value(s), by at most {noise.Largest:0.#e0}"
            );
            return new ClothRestore(commit, savedBytes, edited, false);
        }

        /// <summary>Whether every bone the cloth moves is in <paramref name="bones"/>; false for a cloth that does not load.</summary>
        static bool Fits(byte[] bytes, ISet<string> bones)
        {
            try
            {
                return ClothFile
                    .Load(bytes)
                    .Skeletons.SelectMany(s => s.BoneNames)
                    .All(bones.Contains);
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
