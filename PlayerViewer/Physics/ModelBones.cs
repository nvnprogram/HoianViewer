using System.Collections.Generic;
using System.Linq;
using OpenTK;
using PlayerViewer.Core;
using PlayerViewer.Phive;
using Toolbox.Core;

namespace PlayerViewer.Physics
{
    /// <summary>A model skeleton's rest pose as the authoring code wants it: names, parents and model space world matrices.</summary>
    public static class ModelBones
    {
        /// <summary>The rest world matrix of every bone, independent of any pose or root motion.</summary>
        public static List<AuthorBone> FromSkeleton(STSkeleton skeleton)
        {
            var bones = skeleton.Bones;
            var world = BoneMath.RestWorlds(
                bones
                    .Select(b => new BoneRest(
                        b.Scale,
                        b.Rotation,
                        b.Position,
                        b.ParentIndex,
                        b.UseSegmentScaleCompensate
                    ))
                    .ToList()
            );
            var result = new List<AuthorBone>(bones.Count);
            for (int i = 0; i < bones.Count; i++)
                result.Add(new AuthorBone(bones[i].Name, bones[i].ParentIndex, world[i]));
            return result;
        }

        /// <summary>
        /// Every skeleton of the models, concatenated; a bone name found twice keeps the first,
        /// and a later skeleton's bone whose parent was skipped hangs from that first one.
        /// </summary>
        public static List<AuthorBone> FromSkeletons(IEnumerable<STSkeleton> skeletons)
        {
            var all = new List<AuthorBone>();
            var byName = new Dictionary<string, int>();
            foreach (var s in skeletons)
            {
                var bones = FromSkeleton(s);
                var at = new int[bones.Count];
                var added = new List<int>();
                for (int i = 0; i < bones.Count; i++)
                {
                    if (byName.TryGetValue(bones[i].Name, out at[i]))
                        continue;
                    at[i] = byName[bones[i].Name] = all.Count;
                    all.Add(bones[i]);
                    added.Add(i);
                }
                foreach (int i in added)
                {
                    int parent = bones[i].Parent;
                    all[at[i]] = bones[i] with
                    {
                        Parent = parent >= 0 && parent < at.Length ? at[parent] : -1,
                    };
                }
            }
            return all;
        }

        public static IEnumerable<int> Children(IReadOnlyList<AuthorBone> bones, int parent) =>
            Enumerable.Range(0, bones.Count).Where(i => bones[i].Parent == parent);
    }
}
