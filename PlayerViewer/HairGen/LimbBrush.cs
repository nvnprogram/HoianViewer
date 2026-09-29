using System;
using System.Collections.Generic;
using OpenTK;
using PlayerViewer.Rigging;

namespace PlayerViewer.HairGen
{
    /// <summary>
    /// The limb paint brush on a mesh graph: the mouse ray and the triangle it hits, the volume
    /// brush marking nodes inside a swept sphere, and the surface brush's walk along the mesh.
    /// </summary>
    public static class LimbBrush
    {
        /// <summary>
        /// The ray through a point of the image, given in normalised device coordinates, in the
        /// space <paramref name="toClip"/> takes to clip space. Starts on the near plane; the
        /// direction is unit length.
        /// </summary>
        public static bool ClipRay(
            Matrix4 toClip,
            float x,
            float y,
            out Vector3 origin,
            out Vector3 direction
        )
        {
            origin = direction = default;
            var inverse = Matrix4.Invert(toClip);
            //Not the far plane: its W is below float precision and can come out zero or negative.
            var near = new Vector4(x, y, -1, 1) * inverse;
            var mid = new Vector4(x, y, 0, 1) * inverse;
            if (Math.Abs(near.W) < 1e-12f || Math.Abs(mid.W) < 1e-12f)
                return false;
            origin = near.Xyz / near.W;
            direction = mid.Xyz / mid.W - origin;
            if (direction.LengthSquared < 1e-20f || float.IsNaN(direction.X))
                return false;
            direction.Normalize();
            return true;
        }

        /// <summary>
        /// The nearest graph triangle the ray hits, either face, and the mesh piece it is on; only
        /// on piece <paramref name="only"/> when that is not -1.
        /// </summary>
        public static bool RaySurface(
            MeshGraph graph,
            Vector3 origin,
            Vector3 direction,
            int only,
            out Vector3 hit,
            out int island,
            out int triangle
        )
        {
            hit = default;
            island = -1;
            triangle = -1;
            var nodes = graph.Nodes;
            float best = float.MaxValue;
            for (int ti = 0; ti < graph.Triangles.Count; ti++)
            {
                var (a, b, c) = graph.Triangles[ti];
                if (only >= 0 && graph.Island[a] != only)
                    continue;
                //Moller and Trumbore.
                var p0 = nodes[a];
                var e1 = nodes[b] - p0;
                var e2 = nodes[c] - p0;
                var pv = Vector3.Cross(direction, e2);
                float det = Vector3.Dot(e1, pv);
                if (Math.Abs(det) < 1e-12f)
                    continue;
                float inv = 1 / det;
                var tv = origin - p0;
                float bu = Vector3.Dot(tv, pv) * inv;
                if (bu < 0 || bu > 1)
                    continue;
                var qv = Vector3.Cross(tv, e1);
                float bv = Vector3.Dot(direction, qv) * inv;
                if (bv < 0 || bu + bv > 1)
                    continue;
                float t = Vector3.Dot(e2, qv) * inv;
                if (t > 0 && t < best)
                {
                    best = t;
                    island = graph.Island[a];
                    triangle = ti;
                }
            }
            if (best == float.MaxValue)
                return false;
            hit = origin + direction * best;
            return true;
        }

        /// <summary>
        /// Marks or clears in <paramref name="painted"/> every node within the radius of the
        /// segment between two brush centres, on piece <paramref name="island"/> unless it is -1.
        /// Marking skips <paramref name="claimed"/>. Returns whether anything changed.
        /// </summary>
        public static bool Paint(
            MeshGraph graph,
            HashSet<int> painted,
            HashSet<int> claimed,
            Vector3 from,
            Vector3 to,
            float radius,
            bool erase,
            int island = -1
        )
        {
            var nodes = graph.Nodes;
            var islands = graph.Island;
            var segment = to - from;
            float lengthSq = segment.LengthSquared;
            //A jump longer than this left the surface between the two frames, so nothing is bridged.
            if (lengthSq > 36 * radius * radius)
            {
                from = to;
                segment = Vector3.Zero;
                lengthSq = 0;
            }
            float r2 = radius * radius;
            bool changed = false;
            for (int i = 0; i < nodes.Count; i++)
            {
                var p = nodes[i];
                var closest = from;
                if (lengthSq > 0)
                    closest =
                        from
                        + segment * Math.Clamp(Vector3.Dot(p - from, segment) / lengthSq, 0, 1);
                if ((p - closest).LengthSquared > r2 || (island >= 0 && islands[i] != island))
                    continue;
                if (!erase && claimed.Contains(i))
                    continue;
                changed |= erase ? painted.Remove(i) : painted.Add(i);
            }
            return changed;
        }

        /// <summary>
        /// Each node reached along the mesh from the seeds (a point on a triangle) within the
        /// radius, with that distance, never into a wall. The corner nearest a seed is taken even
        /// beyond the radius, so a small brush on a coarse mesh still marks something.
        /// </summary>
        public static Dictionary<int, float> SurfaceReach(
            MeshGraph graph,
            IEnumerable<(int Triangle, Vector3 At)> seeds,
            float radius,
            HashSet<int> walls
        )
        {
            var reached = new Dictionary<int, float>();
            var queue = new PriorityQueue<int, float>();
            void Reach(int node, float d)
            {
                if (walls != null && walls.Contains(node))
                    return;
                if (reached.TryGetValue(node, out float old) && old <= d)
                    return;
                reached[node] = d;
                queue.Enqueue(node, d);
            }
            foreach (var (triangle, at) in seeds)
            {
                if (triangle < 0 || triangle >= graph.Triangles.Count)
                    continue;
                var (a, b, c) = graph.Triangles[triangle];
                int nearest = -1;
                float nearestD = float.MaxValue;
                foreach (int node in new[] { a, b, c })
                {
                    float d = (graph.Nodes[node] - at).Length;
                    if (d <= radius)
                        Reach(node, d);
                    if (d < nearestD && (walls == null || !walls.Contains(node)))
                        (nearest, nearestD) = (node, d);
                }
                if (nearest >= 0 && nearestD > radius)
                    Reach(nearest, nearestD);
            }
            while (queue.TryDequeue(out int node, out float d))
            {
                if (reached[node] < d)
                    continue;
                foreach (var (next, length) in graph.Adjacent[node])
                    if (d + length <= radius)
                        Reach(next, d + length);
            }
            return reached;
        }

        /// <summary>
        /// Marks or clears what the surface brush reaches from the seeds; marking stops at
        /// <paramref name="claimed"/> nodes. Returns whether anything changed.
        /// </summary>
        public static bool PaintSurface(
            MeshGraph graph,
            HashSet<int> painted,
            HashSet<int> claimed,
            IEnumerable<(int Triangle, Vector3 At)> seeds,
            float radius,
            bool erase
        )
        {
            bool changed = false;
            foreach (int node in SurfaceReach(graph, seeds, radius, erase ? null : claimed).Keys)
                changed |= erase ? painted.Remove(node) : painted.Add(node);
            return changed;
        }
    }
}
