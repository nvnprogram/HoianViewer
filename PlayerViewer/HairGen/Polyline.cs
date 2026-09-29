using System;
using System.Collections.Generic;
using OpenTK;

namespace PlayerViewer.HairGen
{
    /// <summary>A line through points, measured by the arc length along it at each point.</summary>
    static class Polyline
    {
        /// <summary>The arc length at each point, from 0 at the first.</summary>
        public static List<float> Arcs(IReadOnlyList<Vector3> points)
        {
            var arcs = new List<float>(points.Count);
            float arc = 0;
            for (int i = 0; i < points.Count; i++)
            {
                if (i > 0)
                    arc += (points[i] - points[i - 1]).Length;
                arcs.Add(arc);
            }
            return arcs;
        }

        /// <summary>
        /// Where an arc falls among the first <paramref name="count"/> points: the point it reaches
        /// and the share of the way there from the one before, 0 before the start and
        /// <paramref name="count"/> past the end.
        /// </summary>
        static (int Index, float T) Find(IReadOnlyList<float> arcs, int count, float arc)
        {
            if (arc <= 0)
                return (0, 0);
            for (int i = 1; i < count; i++)
                if (arcs[i] >= arc)
                    return (i, (arc - arcs[i - 1]) / Math.Max(arcs[i] - arcs[i - 1], 1e-6f));
            return (count, 0);
        }

        /// <summary>The point at an arc length, clamped to the ends.</summary>
        public static Vector3 At(
            IReadOnlyList<Vector3> points,
            IReadOnlyList<float> arcs,
            float arc
        )
        {
            var (i, t) = Find(arcs, points.Count, arc);
            return i == 0 ? points[0]
                : i == points.Count ? points[^1]
                : Vector3.Lerp(points[i - 1], points[i], t);
        }

        /// <summary>A value given at each of <paramref name="count"/> points, at an arc length, clamped to the ends.</summary>
        public static float At(
            IReadOnlyList<float> values,
            IReadOnlyList<float> arcs,
            int count,
            float arc
        )
        {
            var (i, t) = Find(arcs, count, arc);
            return i == 0 ? values[0]
                : i == count ? values[^1]
                : values[i - 1] + (values[i] - values[i - 1]) * t;
        }
    }
}
