using System.Collections.Generic;
using System.Linq;
using OpenTK;
using PlayerViewer.Phive;
using PlayerViewer.Rigging;

namespace PlayerViewer.Physics
{
    /// <summary>Edits of where a cloth file's pieces carry a collidable, kept consistent with its rest transform.</summary>
    public static class ClothCollidables
    {
        /// <summary>
        /// Every piece's offset for the collidable, from its rest transform and the rest pose of
        /// the bone the piece moves it with.
        /// </summary>
        public static void RefreshOffsets(ClothFile file, Collidable col, List<AuthorBone> bones)
        {
            foreach (var piece in file.Container.ClothDatas)
            {
                var sim = piece.SimClothDatas[0];
                int slot = sim.PerInstanceCollidables.FindIndex(c => c.Source == col.Source);
                if (slot < 0)
                    continue;
                var names = file.SkeletonFor(piece)?.BoneNames;
                string boneName = names?.ElementAtOrDefault(
                    sim.CollidableTransformIndices.ElementAtOrDefault(slot)
                );
                int model = boneName != null ? BoneTree.IndexOf(bones, boneName) : -1;
                if (model < 0)
                    continue;
                var offset = HkValue.Affine(col.Transform) * Matrix4.Invert(bones[model].World);
                sim.CollidableTransformMap.Array("offsets")
                    .SetValue(slot, HkValue.FromMatrix(offset));
            }
        }

        /// <summary>Moves a piece's collidable in <paramref name="slot"/> with another of the piece's skeleton bones.</summary>
        public static void SetBone(
            ClothFile file,
            ClothData piece,
            Collidable col,
            int slot,
            int bone,
            List<AuthorBone> bones
        )
        {
            piece
                .SimClothDatas[0]
                .CollidableTransformMap.Array("transformIndices")
                .SetValue(slot, bone);
            RefreshOffsets(file, col, bones);
            ClothEdit.RecomputeTransformUsage(piece);
        }
    }
}
