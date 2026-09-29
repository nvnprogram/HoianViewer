using System.Collections.Generic;
using Toolbox.Core;

namespace PlayerViewer.Physics
{
    /// <summary>The chest bone a hair model's cloth collides with, placed as the player would place it.</summary>
    public static class HairSpine
    {
        /// <summary>
        /// Puts each hair skeleton's placeholder Spine_3 where the player's rest Spine_3 sits
        /// relative to its head, which is what the game gives it every frame.
        /// </summary>
        public static void Place(STSkeleton human, IEnumerable<STSkeleton> hair)
        {
            var head = human.SearchBone("Head");
            var spine = human.SearchBone("Spine_3");
            foreach (var skeleton in hair)
            {
                var headRoot = skeleton.SearchBone("Head_Root");
                var hairSpine = skeleton.SearchBone("Spine_3");
                if (head == null || spine == null || headRoot == null || hairSpine == null)
                    continue;
                var relative = OpenTK.Matrix4.Invert(spine.Inverse) * head.Inverse;
                hairSpine.Transform = relative * headRoot.Transform;
            }
        }
    }
}
