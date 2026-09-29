using System;
using System.Collections.Generic;
using System.Linq;
using PlayerViewer.Phive;

namespace PlayerViewer.Rigging
{
    /// <summary>
    /// The skeleton a model ends up with and the weights that change, as <see cref="RigWriter"/>
    /// writes them: the model's bones with any added ones, new weights per welded node, and the
    /// bones that go.
    /// </summary>
    public class SkinRig
    {
        /// <summary>The model's bones followed by any added ones, at rest.</summary>
        public List<AuthorBone> Bones = new();

        /// <summary>How many of <see cref="Bones"/> the model already had.</summary>
        public int OriginalBoneCount;

        /// <summary>New influences for welded nodes whose weights change, as (rig bone, weight) summing to one.</summary>
        public Dictionary<int, (int Bone, float Weight)[]> NodeWeights = new();

        /// <summary>Bones the rig adds without skinning anything, such as the chest bone a body collider rides on.</summary>
        public HashSet<int> Unskinned = new();

        public MeshGraph Graph;

        /// <summary>The model's bones the written model leaves out.</summary>
        public HashSet<int> Removed = new();

        public int BoneIndex(string name) => BoneTree.IndexOf(Bones, name);

        public bool AddsBones => Bones.Count > OriginalBoneCount;

        /// <summary>The bone a shape the rig makes smooth falls back on: Head_Root when there is one, else the model's root.</summary>
        public int ShapeAnchor => Math.Max(0, BoneIndex("Head_Root"));

        /// <summary>Whether the rig gives any vertex of a part new weights.</summary>
        public bool Rewrites(int part) =>
            Graph.NodeOf[part].Any(n => n >= 0 && NodeWeights.ContainsKey(n));

        /// <summary>
        /// A vertex's influences once the rig is written, as rig bones: the rig's weights for its
        /// node, or its own. A rigid shape's influences off the anchor go to the anchor, since the
        /// model's root is not posed with the head; at most four, never none.
        /// </summary>
        public (int Bone, float Weight)[] Influences(
            MeshPart part,
            int v,
            int node,
            int anchor,
            bool wasRigid
        )
        {
            var influences =
                node >= 0 && NodeWeights.TryGetValue(node, out var w)
                    ? w
                    : part.Bones[v].Zip(part.Weights[v], (b, x) => (b, x)).ToArray();
            if (wasRigid)
                influences = influences
                    .Select(x => BoneTree.Under(Bones, x.Item1, anchor) ? x : (anchor, x.Item2))
                    .ToArray();
            if (influences.Length > 4)
                influences = influences.OrderByDescending(x => x.Item2).Take(4).ToArray();
            if (influences.Length == 0)
                influences = new[] { (anchor, 1f) };
            return influences;
        }
    }
}
