using OpenTK;

namespace PlayerViewer.Phive
{
    /// <summary>Segment math shared by the cloth solver and the model code above it.</summary>
    public static class Geometry
    {
        /// <summary>The point of segment ab closest to p; a when the segment has no length.</summary>
        public static Vector3 ClosestOnSegment(Vector3 p, Vector3 a, Vector3 b)
        {
            Vector3 ab = b - a;
            float t =
                ab.LengthSquared > 1e-9f
                    ? MathHelper.Clamp(Vector3.Dot(p - a, ab) / ab.LengthSquared, 0, 1)
                    : 0;
            return a + ab * t;
        }
    }
}
