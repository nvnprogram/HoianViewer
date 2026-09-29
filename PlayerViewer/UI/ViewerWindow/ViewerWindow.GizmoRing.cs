using System;
using ImGuiNET;
using OpenTK;
using Vector2 = System.Numerics.Vector2;

namespace PlayerViewer.UI
{
    // What the transform gizmo's rings and the limb aim's rings share: building one on screen,
    // hitting and drawing it, and turning it with the mouse.
    public partial class ViewerWindow
    {
        /// <summary>The colour of the X, Y or Z handle, or of the first, second or third ring.</summary>
        static Vector3 AxisColour(int axis) =>
            axis switch
            {
                0 => new Vector3(1, 0.3f, 0.3f),
                1 => new Vector3(0.35f, 0.9f, 0.35f),
                _ => new Vector3(0.35f, 0.6f, 1),
            };

        /// <summary>A ring about a centre in the plane of <paramref name="u"/> and <paramref name="w"/>, on screen, or null when part of it does not project.</summary>
        static Vector2[] RingPoints(
            ViewMap map,
            Matrix4 frame,
            Vector3 centre,
            Vector3 u,
            Vector3 w,
            float radius,
            int segments
        )
        {
            var points = new Vector2[segments];
            for (int i = 0; i < segments; i++)
            {
                float t = i * MathF.Tau / segments;
                var p = centre + (u * MathF.Cos(t) + w * MathF.Sin(t)) * radius;
                if (!map.Project(Vector3.TransformPosition(p, frame), out points[i]))
                    return null;
            }
            return points;
        }

        static float DistanceToSegment(Vector2 p, Vector2 a, Vector2 b)
        {
            var ab = b - a;
            float t = Math.Clamp(
                Vector2.Dot(p - a, ab) / Math.Max(ab.LengthSquared(), 1e-6f),
                0,
                1
            );
            return (p - (a + ab * t)).Length();
        }

        static float RingDistance(Vector2 mouse, Vector2[] points)
        {
            float d = float.MaxValue;
            for (int i = 0; i < points.Length; i++)
                d = Math.Min(
                    d,
                    DistanceToSegment(mouse, points[i], points[(i + 1) % points.Length])
                );
            return d;
        }

        static void DrawRing(ImDrawListPtr dl, Vector2[] points, uint colour, bool active)
        {
            dl.AddPolyline(
                ref points[0],
                points.Length,
                Color(0, 0, 0, 0.5f),
                true,
                active ? 6 : 4.5f
            );
            dl.AddPolyline(ref points[0], points.Length, colour, true, active ? 3.5f : 2.5f);
        }

        /// <summary>
        /// Where the mouse grabs a ring, as the unit direction from its centre on its plane; false
        /// when the plane is seen too near edge on for that.
        /// </summary>
        static bool RingGrab(
            ViewMap map,
            Matrix4 frame,
            Vector2 mouse,
            Vector3 centre,
            Vector3 axis,
            out Vector3 grab
        )
        {
            grab = default;
            if (!map.Ray(frame, mouse, out var origin, out var ray))
                return false;
            float facing = Vector3.Dot(ray, axis);
            if (MathF.Abs(facing) <= 0.3f)
                return false;
            var hit = origin + ray * (Vector3.Dot(centre - origin, axis) / facing);
            var on = hit - centre;
            on -= axis * Vector3.Dot(on, axis);
            if (on.LengthSquared <= 1e-12f)
                return false;
            grab = on.Normalized();
            return true;
        }

        /// <summary>The turn in radians about the axis from the grab to where the mouse is on the ring's plane, or null.</summary>
        static float? RingAngle(
            ViewMap map,
            Matrix4 frame,
            Vector2 mouse,
            Vector3 centre,
            Vector3 axis,
            Vector3 grab
        )
        {
            if (!map.Ray(frame, mouse, out var origin, out var ray))
                return null;
            float facing = Vector3.Dot(ray, axis);
            if (MathF.Abs(facing) < 1e-4f)
                return null;
            var v = origin + ray * (Vector3.Dot(centre - origin, axis) / facing) - centre;
            v -= axis * Vector3.Dot(v, axis);
            if (v.LengthSquared < 1e-12f)
                return null;
            v.Normalize();
            return MathF.Atan2(Vector3.Dot(Vector3.Cross(grab, v), axis), Vector3.Dot(grab, v));
        }

        /// <summary>The ring's direction on screen at its point nearest the mouse, or zero.</summary>
        static Vector2 RingTangent(Vector2[] points, Vector2 mouse)
        {
            int nearest = 0;
            for (int i = 1; i < points.Length; i++)
                if ((points[i] - mouse).LengthSquared() < (points[nearest] - mouse).LengthSquared())
                    nearest = i;
            var t =
                points[(nearest + 1) % points.Length]
                - points[(nearest + points.Length - 1) % points.Length];
            return t.Length() > 1e-3f ? t / t.Length() : Vector2.Zero;
        }

        /// <summary>The ring's radius on screen about a centre, at least 20 pixels.</summary>
        static float RingRadiusPixels(Vector2[] points, Vector2 centre)
        {
            float radius = 0;
            foreach (var p in points)
                radius = Math.Max(radius, (p - centre).Length());
            return Math.Max(20, radius);
        }

        /// <summary>Text beside the cursor, on its left when the right would run out of the viewport.</summary>
        static void DrawCursorLabel(ImDrawListPtr dl, ViewMap map, Vector2 mouse, string text)
        {
            var size = ImGui.CalcTextSize(text);
            var at = mouse + new Vector2(16, 8);
            if (at.X + size.X > map.Pos.X + map.Size.X)
                at.X = mouse.X - 16 - size.X;
            Widgets.DrawText(dl, at, Color(1, 1, 1, 1), text);
        }
    }
}
