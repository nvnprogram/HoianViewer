using OpenTK;

namespace PlayerViewer.Phive
{
    /// <summary>
    /// What a cloth instance needs of the skeleton it drives: one scene bone per entry of the
    /// cloth's transform set, read before a step and written after it, and the hierarchy the
    /// bone write back aims along. Indices are transform set (cloth bone) indices.
    /// </summary>
    public interface IClothBinding
    {
        int BoneCount { get; }

        /// <summary>The bone's current world matrix, scale included.</summary>
        Matrix4 GetWorld(int bone);

        void SetWorld(int bone, Matrix4 world);

        /// <summary>The cloth index of the bone's scene parent, -1 when that parent is not a cloth bone.</summary>
        int ParentOf(int bone);

        /// <summary>The cloth index of the bone's first scene child that is a cloth bone, -1 when none is.</summary>
        int FirstChildOf(int bone);

        /// <summary>The bone's rest translation relative to its scene parent.</summary>
        Vector3 RestLocalTranslation(int bone);

        /// <summary>The bone's bind (rest) world matrix, for diagnostics.</summary>
        Matrix4 BindWorld(int bone);
    }
}
