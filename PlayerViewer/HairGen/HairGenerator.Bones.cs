using System.Linq;
using PlayerViewer.Rigging;

namespace PlayerViewer.HairGen
{
    // The authoring following an edit of the model's own skeleton.
    public partial class HairGenerator
    {
        /// <summary>The bone limbs' chains, the limbs' hang bones and the collidables' bones follow a renamed bone.</summary>
        public void RenameBone(string old, string name)
        {
            foreach (var limb in Limbs.Where(l => l.IsBoneLimb))
            foreach (var chain in limb.BoneChains)
                for (int i = 0; i < chain.Length; i++)
                    if (chain[i] == old)
                        chain[i] = name;
            foreach (var limb in Limbs.Where(l => l.Hang == old))
                limb.Hang = name;
            foreach (var collider in UserColliders.Concat(ColliderEdits.Values))
                if (collider.Bone == old)
                    collider.Bone = name;
        }

        /// <summary>
        /// Bone limbs drop a deleted bone from their chains, and go with their last chain; what
        /// hung from it or rode on it moves to <paramref name="heir"/>.
        /// </summary>
        public void DeleteBone(string name, string heir)
        {
            foreach (var limb in Limbs.Where(l => l.IsBoneLimb).ToList())
            {
                limb.BoneChains = limb
                    .BoneChains.Select(c => c.Where(b => b != name).ToArray())
                    .Where(c => c.Length > 0)
                    .ToList();
                if (limb.BoneChains.Count == 0)
                    Limbs.Remove(limb);
            }
            foreach (var limb in Limbs.Where(l => l.Hang == name))
                limb.Hang = heir;
            foreach (var collider in UserColliders.Concat(ColliderEdits.Values))
                if (collider.Bone == name && heir != null)
                    collider.Bone = heir;
        }

        /// <summary>
        /// The head for this generator rebased on <paramref name="mesh"/>, an edit of its model:
        /// a hair's head is read again when Head_Root moved, else the head is kept.
        /// </summary>
        public HeadShape HeadFor(SkinnedMesh mesh, Core.Romfs romfs, string actor)
        {
            int oldRoot = Mesh.BoneIndex("Head_Root");
            int newRoot = mesh.BoneIndex("Head_Root");
            bool reread =
                (oldRoot < 0) == (newRoot < 0)
                && (oldRoot < 0 || Mesh.Bones[oldRoot].World != mesh.Bones[newRoot].World);
            return Hair && reread ? HeadShape.ForHair(romfs, mesh, actor) : Head;
        }
    }
}
