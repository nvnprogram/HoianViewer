using System;
using System.Collections.Generic;
using System.Linq;
using PlayerViewer.Rigging;

namespace PlayerViewer.HairGen
{
    /// <summary>
    /// Carries physics authoring to another model of the same hair, as a gendered hair's _F and
    /// _M: settings, bone limbs, collidables and aims as they are, and paint through the nearest
    /// surface point. <see cref="SkeletonEdits"/> carries the skeleton edits made on the source.
    /// </summary>
    public static class PhysicsPort
    {
        /// <summary>How far a surface point may lie from its match on the other model and still take its paint.</summary>
        public const float Tolerance = 0.04f;

        /// <summary>Share of a limb's painted points that must find a match for the limb to be carried.</summary>
        public const float MinCoverage = 0.9f;

        /// <summary>Metres of distance a whole difference in bone weights counts as when picking a match.</summary>
        const float WeightMetres = 0.02f;

        /// <summary>
        /// For each node of <paramref name="to"/>, the node of <paramref name="from"/> it
        /// corresponds to, or -1 when none lies within the tolerance: the nearest, unless one
        /// nearly as near carries bone weights more like its own. Where the two surfaces differ,
        /// as along the edge of a reshaped fringe, the nearest point can lie across the edge of the
        /// part while the weights say which part the point belongs to.
        /// </summary>
        public static int[] Nearest(
            MeshGraph from,
            SkinnedMesh fromMesh,
            MeshGraph to,
            SkinnedMesh toMesh,
            float tolerance = Tolerance
        )
        {
            var grid = new SpatialGrid(tolerance);
            for (int i = 0; i < from.Nodes.Count; i++)
                grid.Add(i, from.Nodes[i]);
            var fromWeights = NodeWeights(from, fromMesh);
            var toWeights = NodeWeights(to, toMesh);
            var result = new int[to.Nodes.Count];
            var near = new List<(int Node, float Distance)>();
            for (int n = 0; n < result.Length; n++)
            {
                var p = to.Nodes[n];
                near.Clear();
                float nearest = tolerance;
                foreach (int i in grid.Near(p))
                {
                    float d = (from.Nodes[i] - p).Length;
                    if (d > tolerance)
                        continue;
                    near.Add((i, d));
                    nearest = Math.Min(nearest, d);
                }
                int best = -1;
                float bestScore = float.MaxValue;
                foreach (var (i, d) in near)
                {
                    //A point both surfaces share is its own match.
                    if (nearest < 1e-6f && d > 1e-6f)
                        continue;
                    if (d > nearest + WeightMetres)
                        continue;
                    float score = d + WeightMetres * Difference(fromWeights[i], toWeights[n]);
                    if (score < bestScore)
                        (best, bestScore) = (i, score);
                }
                result[n] = best;
            }
            return result;
        }

        /// <summary>Each node's bone weights by bone name, from the first vertex welded into it.</summary>
        static Dictionary<string, float>[] NodeWeights(MeshGraph graph, SkinnedMesh mesh)
        {
            var weights = new Dictionary<string, float>[graph.Nodes.Count];
            for (int node = 0; node < weights.Length; node++)
            {
                var (p, v) = graph.FirstVertex[node];
                var part = mesh.Parts[p];
                var w = new Dictionary<string, float>();
                if (part.Bones[v] != null)
                    for (int k = 0; k < part.Bones[v].Length; k++)
                    {
                        int b = part.Bones[v][k];
                        if (b >= 0 && b < mesh.Bones.Count && part.Weights[v][k] > 0)
                            w[mesh.Bones[b].Name] =
                                w.GetValueOrDefault(mesh.Bones[b].Name) + part.Weights[v][k];
                    }
                weights[node] = w;
            }
            return weights;
        }

        /// <summary>Half the summed absolute difference of two weightings: 0 alike, 1 disjoint.</summary>
        static float Difference(Dictionary<string, float> a, Dictionary<string, float> b)
        {
            float sum = 0;
            foreach (var (k, v) in a)
                sum += MathF.Abs(v - b.GetValueOrDefault(k));
            foreach (var (k, v) in b)
                if (!a.ContainsKey(k))
                    sum += v;
            return sum / 2;
        }

        /// <summary>
        /// Paint on <c>from</c> carried to <c>to</c>: a node takes it when its nearest node on the
        /// source is painted. <paramref name="covered"/> counts the painted source nodes some node
        /// of the target lies near.
        /// </summary>
        public static HashSet<int> MapPaint(
            IReadOnlySet<int> painted,
            int[] toFrom,
            int[] fromTo,
            out int covered
        )
        {
            var mapped = new HashSet<int>();
            for (int n = 0; n < toFrom.Length; n++)
                if (toFrom[n] >= 0 && painted.Contains(toFrom[n]))
                    mapped.Add(n);
            covered = painted.Count(s => s < fromTo.Length && fromTo[s] >= 0);
            return mapped;
        }

        /// <summary>
        /// The metadata of <paramref name="source"/> for another model: paint in the target's
        /// vertices, nothing kept of the source's mesh. A painted limb whose paint the target
        /// cannot match is left out, and said so in <paramref name="report"/>.
        /// </summary>
        public static PhysicsProvenance ForModel(
            PhysicsProvenance source,
            SkinnedMesh sourceMesh,
            SkinnedMesh targetMesh,
            List<string> report
        )
        {
            var meta = source.Clone();
            meta.Rest = new();
            meta.GeneratedBones = new();
            meta.Shapes = targetMesh
                .Parts.Select(p => new PhysicsProvenance.ShapeRecord
                {
                    Name = p.Name,
                    Vertices = p.Rest.Length,
                })
                .ToList();
            var painted = meta.Limbs.Where(l => l.Kind == "painted").ToList();
            if (painted.Count == 0)
                return meta;

            var fromGraph = new MeshGraph(sourceMesh);
            var toGraph = new MeshGraph(targetMesh);
            var toFrom = Nearest(fromGraph, sourceMesh, toGraph, targetMesh);
            var fromTo = Nearest(toGraph, targetMesh, fromGraph, sourceMesh);
            var dropped = new HashSet<PhysicsProvenance.LimbRecord>();
            foreach (var record in painted)
            {
                var problems = new List<string>();
                var nodes = source.PaintedNodes(sourceMesh, fromGraph, record, problems);
                report.AddRange(problems);
                var mapped = MapPaint(nodes, toFrom, fromTo, out int covered);
                if (nodes.Count == 0 || mapped.Count == 0 || covered < MinCoverage * nodes.Count)
                {
                    report.Add(
                        $"{record.Name} left out: {covered} of its {nodes.Count} painted points have a match on this model"
                    );
                    dropped.Add(record);
                    continue;
                }
                record.Paint = PhysicsProvenance.PaintOf(targetMesh, toGraph, mapped);
                record.Bones = null;
                record.Replaced = null;
                if (covered < nodes.Count)
                    report.Add($"{record.Name}: {covered} of {nodes.Count} painted points matched");
            }
            if (dropped.Count > 0)
                DropLimbs(meta, dropped);
            return meta;
        }

        /// <summary>Removes limbs, renumbering the collidables' limb lists.</summary>
        static void DropLimbs(PhysicsProvenance meta, HashSet<PhysicsProvenance.LimbRecord> dropped)
        {
            var map = new Dictionary<int, int>();
            var kept = new List<PhysicsProvenance.LimbRecord>();
            for (int i = 0; i < meta.Limbs.Count; i++)
                if (!dropped.Contains(meta.Limbs[i]))
                {
                    map[i] = kept.Count;
                    kept.Add(meta.Limbs[i]);
                }
            meta.Limbs = kept;
            foreach (var c in meta.Colliders.Concat(meta.ColliderEdits))
                if (c.Limbs != null)
                    c.Limbs = c.Limbs.Where(map.ContainsKey).Select(i => map[i]).ToList();
        }
    }
}
