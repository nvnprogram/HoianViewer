using System;
using System.Collections.Generic;
using System.Linq;
using PlayerViewer.Phive;
using PlayerViewer.Rigging;

namespace PlayerViewer.Physics
{
    /// <summary>The rules a click on a bone follows while a limb's chains are picked from the model's bones.</summary>
    public static class BoneChainPick
    {
        /// <summary>
        /// A bone picked: the root of a chain removes it, a bone inside a chain or past its end
        /// along its first children ends it there, any other bone starts a chain, joined onto the
        /// chain it runs into. Returns why the bone was refused, or null.
        /// </summary>
        /// <param name="replacedBy">The limb whose bones replaced a bone of the model, or null.</param>
        /// <param name="takenBy">Bones other limbs' chains already move, by the limb's name.</param>
        public static string Pick(
            IReadOnlyList<AuthorBone> bones,
            List<List<int>> chains,
            int bone,
            Func<int, string> replacedBy,
            IReadOnlyDictionary<int, string> takenBy
        )
        {
            if (bone < 0 || bone >= bones.Count)
                return null;
            int rooted = chains.FindIndex(c => c[0] == bone);
            if (rooted >= 0)
            {
                chains.RemoveAt(rooted);
                return null;
            }
            foreach (var chain in chains)
            {
                var natural = BoneTree.ChainFrom(bones, chain[0]);
                int at = Array.IndexOf(natural, bone);
                if (at < 0)
                    continue;
                chain.Clear();
                chain.AddRange(natural.Take(at + 1));
                return null;
            }
            if (bones[bone].Parent < 0)
                return $"{bones[bone].Name} has no parent to hang from.";
            if (replacedBy(bone) is string by)
                return $"{bones[bone].Name} was replaced by {by}'s bones; it carries nothing now.";
            if (takenBy.TryGetValue(bone, out var owner))
                return $"{bones[bone].Name} already moves with {owner}.";
            //Stops before a bone another chain already has.
            var run = BoneTree
                .ChainFrom(bones, bone)
                .TakeWhile(b => !chains.Any(c => c.Contains(b)) && !takenBy.ContainsKey(b))
                .ToList();
            chains.Add(run);
            BoneTree.JoinChains(bones, chains);
            return null;
        }
    }
}
