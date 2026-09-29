using System;
using System.Collections.Generic;
using System.Linq;
using OpenTK;

namespace PlayerViewer.Rigging
{
    /// <summary>
    /// The visible shapes of a mesh welded into one surface: vertices at the same rest position
    /// become one node, so seams between shapes and split normals do not cut the surface apart.
    /// </summary>
    public class MeshGraph
    {
        public readonly List<Vector3> Nodes = new();
        public readonly List<(int A, int B, int C)> Triangles = new();

        /// <summary>The node of each vertex, per part; -1 for a hidden part.</summary>
        public readonly int[][] NodeOf;

        /// <summary>
        /// The node whose weights each vertex takes, per part: its own, or for a vertex only a
        /// lower detail level draws, the nearest node of its part, so that level moves with it.
        /// -1 for a hidden part.
        /// </summary>
        public readonly int[][] WeightNodeOf;

        /// <summary>The first vertex welded into each node, which speaks for the node's weights.</summary>
        public readonly List<(int Part, int Vertex)> FirstVertex = new();

        /// <summary>Each node's neighbours with the edge length.</summary>
        public readonly List<(int Node, float Length)>[] Adjacent;

        /// <summary>The triangles on each edge, keyed by its sorted node pair.</summary>
        public readonly Dictionary<(int, int), List<int>> EdgeTriangles = new();

        /// <summary>The connected piece of surface each node is on.</summary>
        public readonly int[] Island;
        public readonly int IslandCount;

        const float WeldTolerance = 1e-4f;

        public MeshGraph(SkinnedMesh mesh)
        {
            var index = new Dictionary<(long, long, long), int>();
            NodeOf = new int[mesh.Parts.Count][];
            WeightNodeOf = new int[mesh.Parts.Count][];
            for (int s = 0; s < mesh.Parts.Count; s++)
            {
                var part = mesh.Parts[s];
                NodeOf[s] = new int[part.Rest.Length];
                //Lower detail levels share the buffer; only what the full detail draws is welded.
                var drawn = new bool[part.Rest.Length];
                foreach (int v in part.Triangles)
                    drawn[v] = true;
                for (int v = 0; v < part.Rest.Length; v++)
                {
                    if (part.Hidden || !drawn[v] || part.Bones[v].Length == 0)
                    {
                        NodeOf[s][v] = -1;
                        continue;
                    }
                    var p = part.Rest[v];
                    var key = (
                        (long)MathF.Round(p.X / WeldTolerance),
                        (long)MathF.Round(p.Y / WeldTolerance),
                        (long)MathF.Round(p.Z / WeldTolerance)
                    );
                    if (!index.TryGetValue(key, out int node))
                    {
                        node = Nodes.Count;
                        index[key] = node;
                        Nodes.Add(p);
                        FirstVertex.Add((s, v));
                    }
                    NodeOf[s][v] = node;
                }
                WeightNodeOf[s] = part.Hidden ? NodeOf[s] : NearestNodes(part, NodeOf[s]);
                for (int t = 0; t + 2 < part.Triangles.Length; t += 3)
                {
                    int a = NodeOf[s][part.Triangles[t]],
                        b = NodeOf[s][part.Triangles[t + 1]],
                        c = NodeOf[s][part.Triangles[t + 2]];
                    if (a < 0 || b < 0 || c < 0 || a == b || b == c || a == c)
                        continue;
                    Triangles.Add((a, b, c));
                }
            }

            Adjacent = new List<(int, float)>[Nodes.Count];
            for (int i = 0; i < Nodes.Count; i++)
                Adjacent[i] = new List<(int, float)>();
            for (int t = 0; t < Triangles.Count; t++)
            {
                var (a, b, c) = Triangles[t];
                foreach (var (x, y) in new[] { (a, b), (b, c), (c, a) })
                {
                    var key = (Math.Min(x, y), Math.Max(x, y));
                    if (!EdgeTriangles.TryGetValue(key, out var list))
                    {
                        EdgeTriangles[key] = list = new List<int>();
                        float length = (Nodes[x] - Nodes[y]).Length;
                        Adjacent[x].Add((y, length));
                        Adjacent[y].Add((x, length));
                    }
                    list.Add(t);
                }
            }

            var sets = new UnionFind(Nodes.Count);
            foreach (var (a, b, c) in Triangles)
            {
                sets.Union(a, b);
                sets.Union(b, c);
            }
            Island = new int[Nodes.Count];
            var ids = new Dictionary<int, int>();
            for (int i = 0; i < Nodes.Count; i++)
            {
                int root = sets.Find(i);
                if (!ids.TryGetValue(root, out int id))
                    ids[root] = id = ids.Count;
                Island[i] = id;
            }
            IslandCount = ids.Count;
        }

        /// <summary>Each vertex's node, or for one without a node but with bones, the node of the nearest vertex of the part that has one.</summary>
        static int[] NearestNodes(MeshPart part, int[] nodeOf)
        {
            var result = (int[])nodeOf.Clone();
            const float cell = 0.02f;
            var grid = new SpatialGrid(cell);
            for (int v = 0; v < nodeOf.Length; v++)
                if (nodeOf[v] >= 0)
                    grid.Add(v, part.Rest[v]);
            if (grid.IsEmpty)
                return result;
            for (int v = 0; v < nodeOf.Length; v++)
            {
                if (nodeOf[v] >= 0 || part.Bones[v].Length == 0)
                    continue;
                var p = part.Rest[v];
                int best = -1;
                float bestSq = float.MaxValue;
                void Consider(int w)
                {
                    float d = (part.Rest[w] - p).LengthSquared;
                    if (d < bestSq)
                    {
                        bestSq = d;
                        best = w;
                    }
                }
                //Rings of cells outward until the nearest found is closer than the next ring can be,
                //then every vertex when that is far.
                bool settled = false;
                for (int ring = 0; ring < 6 && !settled; ring++)
                {
                    foreach (int w in grid.Ring(p, ring))
                        Consider(w);
                    settled = best >= 0 && MathF.Sqrt(bestSq) <= ring * cell;
                }
                if (!settled)
                    foreach (int w in grid.All())
                        Consider(w);
                if (best >= 0)
                    result[v] = nodeOf[best];
            }
            return result;
        }

        /// <summary>
        /// Shortest path lengths along the edges from any of the sources; unreachable nodes stay at
        /// infinity. With <paramref name="inside"/> paths stay in that node set, and
        /// <paramref name="extra"/> adds edges. <paramref name="previous"/> gets each node's step
        /// toward its source and <paramref name="origin"/> the source itself, -1 where unreached.
        /// </summary>
        public float[] Distances(
            IEnumerable<int> sources,
            ISet<int> inside = null,
            IReadOnlyDictionary<int, List<(int Node, float Length)>> extra = null,
            int[] previous = null,
            int[] origin = null
        )
        {
            var distance = new float[Nodes.Count];
            Array.Fill(distance, float.PositiveInfinity);
            if (previous != null)
                Array.Fill(previous, -1);
            if (origin != null)
                Array.Fill(origin, -1);
            var queue = new PriorityQueue<int, float>();
            foreach (int s in sources)
            {
                distance[s] = 0;
                if (origin != null)
                    origin[s] = s;
                queue.Enqueue(s, 0);
            }
            while (queue.TryDequeue(out int node, out float d))
            {
                if (d > distance[node])
                    continue;
                var edges =
                    extra != null && extra.TryGetValue(node, out var joined)
                        ? Adjacent[node].Concat(joined)
                        : Adjacent[node];
                foreach (var (next, length) in edges)
                {
                    if (inside != null && !inside.Contains(next))
                        continue;
                    float nd = d + length;
                    if (nd < distance[next])
                    {
                        distance[next] = nd;
                        if (previous != null)
                            previous[next] = node;
                        if (origin != null)
                            origin[next] = origin[node];
                        queue.Enqueue(next, nd);
                    }
                }
            }
            return distance;
        }
    }
}
