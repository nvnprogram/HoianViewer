using System.Collections.Generic;
using OpenTK;

namespace PlayerViewer.Core
{
    /// <summary>A bone's rest local transform and where it hangs, for <see cref="BoneMath.RestWorlds"/>.</summary>
    public readonly record struct BoneRest(
        Vector3 Scale,
        Quaternion Rotation,
        Vector3 Translation,
        int Parent,
        bool SegmentScaleCompensate
    );

    /// <summary>Skeleton rest pose math shared by the bfres model code and the cloth layer.</summary>
    public static class BoneMath
    {
        /// <summary>
        /// The rotation of a quaternion, normalised first. Not through an axis and angle: a
        /// stored identity such as (0, 0, 0, 0.99999994) has no axis and would give NaN.
        /// </summary>
        public static Matrix4 Rotation(Quaternion q)
        {
            float len = q.Length;
            if (len <= 0)
                return Matrix4.Identity;
            float x = q.X / len,
                y = q.Y / len,
                z = q.Z / len,
                w = q.W / len;
            return new Matrix4(
                1 - 2 * (y * y + z * z),
                2 * (x * y + z * w),
                2 * (x * z - y * w),
                0,
                2 * (x * y - z * w),
                1 - 2 * (x * x + z * z),
                2 * (y * z + x * w),
                0,
                2 * (x * z + y * w),
                2 * (y * z - x * w),
                1 - 2 * (x * x + y * y),
                0,
                0,
                0,
                0,
                1
            );
        }

        /// <summary>
        /// The rest world matrix of every bone, composed as the engine does: scale, rotation, the
        /// inverse parent scale for segment scale compensate, translation, then the parent.
        /// </summary>
        public static Matrix4[] RestWorlds(IReadOnlyList<BoneRest> bones)
        {
            var world = new Matrix4[bones.Count];
            for (int i = 0; i < bones.Count; i++)
            {
                var b = bones[i];
                var local = Matrix4.CreateScale(b.Scale) * Rotation(b.Rotation);
                int parent = b.Parent;
                if (b.SegmentScaleCompensate && parent >= 0)
                {
                    var ps = bones[parent].Scale;
                    local *= Matrix4.CreateScale(1 / ps.X, 1 / ps.Y, 1 / ps.Z);
                }
                local *= Matrix4.CreateTranslation(b.Translation);
                world[i] = parent >= 0 && parent < i ? local * world[parent] : local;
            }
            return world;
        }
    }
}
