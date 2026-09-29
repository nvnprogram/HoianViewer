using System;
using System.Collections.Generic;
using System.Linq;
using PlayerViewer.Phive;

namespace PlayerViewer.Rigging
{
    /// <summary>Lookups and walks over a skeleton's parent links.</summary>
    public static class BoneTree
    {
        /// <summary>The first bone of the name, or -1.</summary>
        public static int IndexOf(IReadOnlyList<AuthorBone> bones, string name)
        {
            for (int i = 0; i < bones.Count; i++)
                if (bones[i].Name == name)
                    return i;
            return -1;
        }

        /// <summary>Whether a bone is the anchor or below it.</summary>
        public static bool Under(IReadOnlyList<AuthorBone> bones, int bone, int anchor)
        {
            for (int b = bone; b >= 0; b = bones[b].Parent)
                if (b == anchor)
                    return true;
            return false;
        }

        /// <summary>A chain from a bone down through first children to a leaf.</summary>
        public static int[] ChainFrom(IReadOnlyList<AuthorBone> bones, int start)
        {
            var chain = new List<int> { start };
            while (true)
            {
                int current = chain[^1];
                int child = -1;
                for (int i = 0; i < bones.Count; i++)
                    if (bones[i].Parent == current)
                    {
                        child = i;
                        break;
                    }
                if (child < 0 || chain.Contains(child))
                    break;
                chain.Add(child);
            }
            return chain.ToArray();
        }

        /// <summary>
        /// Joins each chain whose first bone is the first child of another chain's last bone onto
        /// that chain, in place, so chains picked from the tip up become one.
        /// </summary>
        public static bool JoinChains(IReadOnlyList<AuthorBone> bones, List<List<int>> chains)
        {
            bool any = false;
            for (bool joined = true; joined; )
            {
                joined = false;
                for (int i = 0; i < chains.Count && !joined; i++)
                for (int j = 0; j < chains.Count && !joined; j++)
                {
                    if (i == j || chains[i].Count == 0 || chains[j].Count == 0)
                        continue;
                    var below = ChainFrom(bones, chains[i][^1]);
                    if (below.Length < 2 || below[1] != chains[j][0])
                        continue;
                    chains[i].AddRange(chains[j]);
                    chains.RemoveAt(j);
                    joined = any = true;
                }
            }
            return any;
        }

        /// <summary><see cref="JoinChains(IReadOnlyList{AuthorBone}, List{List{int}})"/> on bone names; a chain naming a bone the model lacks is left as it is.</summary>
        public static List<string[]> JoinChains(
            IReadOnlyList<AuthorBone> bones,
            IEnumerable<string[]> chains,
            out bool joined
        )
        {
            var named = chains.ToList();
            var known = named
                .Where(c => c.Length > 0 && c.All(n => IndexOf(bones, n) >= 0))
                .Select(c => c.Select(n => IndexOf(bones, n)).ToList())
                .ToList();
            joined = JoinChains(bones, known);
            if (!joined)
                return named;
            return known
                .Select(c => c.Select(b => bones[b].Name).ToArray())
                .Concat(named.Where(c => c.Length == 0 || c.Any(n => IndexOf(bones, n) < 0)))
                .ToList();
        }

        /// <summary>Adds, down the tree, every child of a bone in the set that <paramref name="joins"/> accepts.</summary>
        public static void AddChildren(
            ISet<int> set,
            int count,
            Func<int, int> parent,
            Func<int, bool> joins
        )
        {
            for (bool grew = true; grew; )
            {
                grew = false;
                for (int b = 0; b < count; b++)
                    if (!set.Contains(b) && parent(b) >= 0 && set.Contains(parent(b)) && joins(b))
                        grew |= set.Add(b);
            }
        }

        /// <summary>Takes out of the set every bone above one that is not in it.</summary>
        public static void KeepAncestors(ISet<int> set, int count, Func<int, int> parent)
        {
            for (bool shrank = true; shrank; )
            {
                shrank = false;
                for (int b = 0; b < count; b++)
                    if (!set.Contains(b) && parent(b) >= 0)
                        shrank |= set.Remove(parent(b));
            }
        }
    }
}
