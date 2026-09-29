using System;
using System.Collections.Generic;
using ImGuiNET;
using OpenTK;
using PlayerViewer.Rigging;
using Vector2 = System.Numerics.Vector2;

namespace PlayerViewer.UI
{
    // A move, rotate and scale gizmo over the viewport for one transform: arrows along the
    // parent's axes, rings about them, and circles on the object's own axes for scale. R cycles
    // the three. Drags snap unless Shift is held; while a gizmo is shown Shift does not move the
    // camera.
    public partial class ViewerWindow
    {
        enum GizmoMode
        {
            Move,
            Rotate,
            Scale,
        }

        /// <summary>Translation and scale, and Euler XYZ rotation in degrees, all in the parent's space.</summary>
        readonly record struct GizmoSrt(Vector3 Translation, Vector3 Rotation, Vector3 Scale);

        const float GizmoPixels = 90;
        const float GizmoPickPixels = 8;
        const float GizmoTurnStep = 15;
        const float GizmoScaleStep = 0.2f;
        const float GizmoMinScale = 0.01f;
        const int GizmoRingSegments = 64;

        sealed class TransformGizmo
        {
            /// <summary>The step a move snaps by, from where the drag started.</summary>
            public float MoveStep;

            public GizmoMode Mode;
            public int Hover = -1;
            public int Axis = -1;
            public bool Shown;
            public GizmoSrt Start,
                Live;

            //The drag's grab: the distance along the axis, or the direction from the centre on
            //the ring's plane, or the ring's tangent on screen when its plane is seen edge on.
            public float GrabAlong;
            public Vector3 Grab;
            public bool OnPlane;
            public Vector2 GrabScreen,
                Tangent;
            public float RadiusPixels,
                HandleLength;

            public readonly List<Vector2[]> Shapes = new() { null, null, null };

            public bool Dragging => Axis >= 0;

            /// <summary>Over a handle or dragging one, so a click there is the gizmo's.</summary>
            public bool Busy => Shown && (Dragging || Hover >= 0);
        }

        readonly TransformGizmo _effectGizmo = new() { MoveStep = 0.5f };
        readonly TransformGizmo _boneGizmo = new() { MoveStep = 0.01f };

        bool GizmoDragging => _effectGizmo.Dragging || _boneGizmo.Dragging;
        bool GizmoShown => _effectGizmo.Shown || _boneGizmo.Shown;

        static string GizmoModeName(GizmoMode mode) =>
            mode switch
            {
                GizmoMode.Move => "Transform",
                GizmoMode.Rotate => "Rotation",
                _ => "Scale",
            };

        /// <summary>The rotation as a row vector matrix, X applied first, then Y, then Z.</summary>
        static Matrix4 GizmoRotation(Vector3 degrees) =>
            Matrix4.CreateRotationX(MathHelper.DegreesToRadians(degrees.X))
            * Matrix4.CreateRotationY(MathHelper.DegreesToRadians(degrees.Y))
            * Matrix4.CreateRotationZ(MathHelper.DegreesToRadians(degrees.Z));

        /// <summary>Euler degrees of a rotation, of its two solutions the one nearer <paramref name="near"/>.</summary>
        static Vector3 GizmoEuler(Matrix4 rotation, Vector3 near)
        {
            var e = ModelSkin.EulerXYZ(rotation);
            var a = new Vector3(
                MathHelper.RadiansToDegrees(e.X),
                MathHelper.RadiansToDegrees(e.Y),
                MathHelper.RadiansToDegrees(e.Z)
            );
            var b = new Vector3(a.X + 180, 180 - a.Y, a.Z + 180);
            static float Wrap(float v, float to) => to + ((v - to) % 360 + 540) % 360 - 180;
            Vector3 Near(Vector3 v) => new(Wrap(v.X, near.X), Wrap(v.Y, near.Y), Wrap(v.Z, near.Z));
            a = Near(a);
            b = Near(b);
            return (a - near).LengthSquared <= (b - near).LengthSquared ? a : b;
        }

        static float Snap(float value, float step) => MathF.Round(value / step) * step;

        /// <summary>R cycles the mode of a shown gizmo, unless a key is being typed somewhere.</summary>
        static void GizmoModeKey(TransformGizmo g)
        {
            var io = ImGui.GetIO();
            if (
                g.Dragging
                || io.KeyCtrl
                || io.KeyAlt
                || io.WantTextInput
                || Widgets.Typing
                || ImGui.IsAnyItemActive()
                || !ImGui.IsKeyPressed((int)OpenTK.Input.Key.R, false)
            )
                return;
            g.Mode = (GizmoMode)(((int)g.Mode + 1) % 3);
            g.Hover = -1;
        }

        /// <summary>
        /// Draws the gizmo for <paramref name="value"/>, whose parent space goes to the viewport
        /// through <paramref name="frame"/>, and runs its drag. <paramref name="live"/> is the
        /// value to show; true on the frame a drag that changed it lets go.
        /// </summary>
        bool DrawTransformGizmo(
            TransformGizmo g,
            ViewMap map,
            Matrix4 frame,
            GizmoSrt value,
            bool hovered,
            out GizmoSrt live
        )
        {
            live = value;
            g.Shown = false;
            GizmoModeKey(g);
            var io = ImGui.GetIO();
            var mouse = io.MousePos;
            bool Screen(Vector3 p, out Vector2 s) =>
                map.Project(Vector3.TransformPosition(p, frame), out s);

            if (g.Dragging)
                UpdateGizmoDrag(g, map, frame, mouse, io.KeyShift);
            var shown = g.Dragging ? g.Live : value;
            var origin = shown.Translation;
            if (!Screen(origin, out var o))
                return false;

            //Parent space units per pixel near the centre, so the gizmo keeps its size on screen.
            float perUnit = 0;
            for (int k = 0; k < 3; k++)
                if (Screen(origin + Unit(k) * 0.01f, out var s))
                    perUnit = Math.Max(perUnit, (s - o).Length() / 0.01f);
            if (perUnit < 1e-4f)
                return false;
            float length = GizmoPixels / perUnit;
            var rotation = GizmoRotation(shown.Rotation);

            //Each handle as points on screen: a stem for move and scale, a closed ring for rotate.
            for (int k = 0; k < 3; k++)
            {
                g.Shapes[k] = null;
                if (g.Mode == GizmoMode.Rotate)
                    g.Shapes[k] = RingPoints(
                        map,
                        frame,
                        origin,
                        Unit((k + 1) % 3),
                        Unit((k + 2) % 3),
                        length * 0.8f,
                        GizmoRingSegments
                    );
                else
                {
                    var axis = g.Mode == GizmoMode.Move ? Unit(k) : LocalAxis(rotation, k);
                    if (Screen(origin + axis * length, out var tip))
                        g.Shapes[k] = new[] { o, tip };
                }
            }
            g.Shown = true;
            g.Hover = hovered && !g.Dragging ? GizmoHandleUnder(g, mouse) : -1;

            var dl = ImGui.GetWindowDrawList();
            dl.PushClipRect(map.Pos, map.Pos + map.Size);
            for (int k = 0; k < 3; k++)
            {
                var points = g.Shapes[k];
                if (points == null)
                    continue;
                bool active = g.Axis == k || g.Hover == k;
                uint colour = active ? ColSelect : Color(AxisColour(k), 0.95f);
                uint shadow = Color(0, 0, 0, 0.5f);
                if (g.Mode == GizmoMode.Rotate)
                {
                    DrawRing(dl, points, colour, active);
                    continue;
                }
                var (a, b) = (points[0], points[1]);
                dl.AddLine(a, b, shadow, active ? 6 : 4.5f);
                dl.AddLine(a, b, colour, active ? 3.5f : 2.5f);
                if (g.Mode == GizmoMode.Scale)
                {
                    dl.AddCircleFilled(b, active ? 8 : 7, shadow, 20);
                    dl.AddCircleFilled(b, active ? 6.5f : 5.5f, colour, 20);
                    continue;
                }
                var dir = b - a;
                if (dir.Length() < 1)
                    continue;
                dir /= dir.Length();
                var side = new Vector2(-dir.Y, dir.X) * 6;
                var head = b + dir * 12;
                dl.AddTriangleFilled(head + dir, b + side * 1.25f, b - side * 1.25f, shadow);
                dl.AddTriangleFilled(head, b + side, b - side, colour);
            }
            dl.AddCircleFilled(o, 4, Color(1, 1, 1, 0.9f), 12);

            int shownAxis = g.Dragging ? g.Axis : g.Hover;
            if (shownAxis >= 0)
            {
                string text = $"R -> {GizmoModeName((GizmoMode)(((int)g.Mode + 1) % 3))}";
                if (g.Dragging)
                    text = GizmoDragText(g) + "\n" + text + ", Shift for no snapping";
                DrawCursorLabel(dl, map, mouse, text);
            }
            dl.PopClipRect();

            if (!g.Dragging && g.Hover >= 0 && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                BeginGizmoDrag(g, map, frame, value, mouse, length);
            if (g.Dragging)
            {
                live = g.Live;
                if (!ImGui.IsMouseDown(ImGuiMouseButton.Left))
                {
                    g.Axis = -1;
                    return live != g.Start;
                }
            }
            return false;
        }

        static Vector3 Unit(int k) =>
            k switch
            {
                0 => Vector3.UnitX,
                1 => Vector3.UnitY,
                _ => Vector3.UnitZ,
            };

        static Vector3 LocalAxis(Matrix4 rotation, int k) =>
            k switch
            {
                0 => rotation.Row0.Xyz,
                1 => rotation.Row1.Xyz,
                _ => rotation.Row2.Xyz,
            };

        static int GizmoHandleUnder(TransformGizmo g, Vector2 mouse)
        {
            int best = -1;
            float bestDistance = GizmoPickPixels;
            for (int k = 0; k < 3; k++)
            {
                var points = g.Shapes[k];
                if (points == null)
                    continue;
                float d = float.MaxValue;
                if (g.Mode == GizmoMode.Rotate)
                    d = RingDistance(mouse, points);
                else
                {
                    //The stem from a little out of the centre, where all three meet, to the head.
                    var dir = points[1] - points[0];
                    float len = dir.Length();
                    var from = len > 1 ? points[0] + dir * Math.Min(0.2f, 12 / len) : points[0];
                    var to =
                        g.Mode == GizmoMode.Move && len > 1
                            ? points[1] + dir / len * 12
                            : points[1];
                    d = DistanceToSegment(mouse, from, to);
                    if (g.Mode == GizmoMode.Scale)
                        d = Math.Min(d, Math.Max(0, (mouse - points[1]).Length() - 4));
                }
                if (d < bestDistance)
                {
                    bestDistance = d;
                    best = k;
                }
            }
            return best;
        }

        /// <summary>The axis a drag moves along or turns about, in the parent's space.</summary>
        static Vector3 GizmoDragAxis(TransformGizmo g) =>
            g.Mode == GizmoMode.Scale
                ? LocalAxis(GizmoRotation(g.Start.Rotation), g.Axis).Normalized()
                : Unit(g.Axis);

        /// <summary>How far along <paramref name="axis"/> through the start's centre the mouse ray passes nearest, or null edge on.</summary>
        static float? AlongAxis(
            ViewMap map,
            Matrix4 frame,
            Vector2 mouse,
            Vector3 centre,
            Vector3 axis
        )
        {
            if (!map.Ray(frame, mouse, out var origin, out var ray))
                return null;
            float b = Vector3.Dot(axis, ray);
            float denom = 1 - b * b;
            if (denom < 0.004f)
                return null;
            var w0 = centre - origin;
            return (b * Vector3.Dot(ray, w0) - Vector3.Dot(axis, w0)) / denom;
        }

        void BeginGizmoDrag(
            TransformGizmo g,
            ViewMap map,
            Matrix4 frame,
            GizmoSrt value,
            Vector2 mouse,
            float length
        )
        {
            g.Axis = g.Hover;
            g.Hover = -1;
            g.Start = value;
            g.Live = value;
            g.GrabScreen = mouse;
            g.HandleLength = length;
            var axis = GizmoDragAxis(g);
            var centre = value.Translation;
            g.OnPlane = false;
            if (g.Mode != GizmoMode.Rotate)
            {
                var along = AlongAxis(map, frame, mouse, centre, axis);
                g.OnPlane = along != null;
                g.GrabAlong = along ?? length;
            }
            else if (RingGrab(map, frame, mouse, centre, axis, out var grab))
            {
                g.Grab = grab;
                g.OnPlane = true;
            }

            //Edge on: along the handle on screen, a whole handle's length per its length dragged,
            //and for a ring along its tangent where it was grabbed, a turn per radius.
            var points = g.Shapes[g.Axis];
            g.Tangent = Vector2.Zero;
            g.RadiusPixels = GizmoPixels;
            if (points == null)
                return;
            if (g.Mode != GizmoMode.Rotate)
            {
                var d = points[1] - points[0];
                if (d.Length() > 1)
                {
                    g.Tangent = d / d.Length();
                    g.RadiusPixels = d.Length();
                }
                return;
            }
            g.Tangent = RingTangent(points, mouse);
            var centreScreen = Vector2.Zero;
            foreach (var p in points)
                centreScreen += p / points.Length;
            g.RadiusPixels = RingRadiusPixels(points, centreScreen);
        }

        static void UpdateGizmoDrag(
            TransformGizmo g,
            ViewMap map,
            Matrix4 frame,
            Vector2 mouse,
            bool free
        )
        {
            var start = g.Start;
            var axis = GizmoDragAxis(g);
            int k = g.Axis;
            float screenAlong = Vector2.Dot(mouse - g.GrabScreen, g.Tangent) / g.RadiusPixels;
            if (g.Mode == GizmoMode.Rotate)
            {
                float angle = screenAlong;
                if (g.OnPlane)
                {
                    if (
                        RingAngle(map, frame, mouse, start.Translation, axis, g.Grab) is not float a
                    )
                        return;
                    angle = a;
                }
                float degrees = MathHelper.RadiansToDegrees(angle);
                if (!free)
                    degrees = Snap(degrees, GizmoTurnStep);
                var turned =
                    GizmoRotation(start.Rotation)
                    * Matrix4.CreateFromAxisAngle(axis, MathHelper.DegreesToRadians(degrees));
                var euler = GizmoEuler(turned, start.Rotation);
                //A snapped turn about one axis leaves round numbers as round numbers.
                if (!free)
                    euler = new Vector3(
                        Snap(euler.X, 1e-3f),
                        Snap(euler.Y, 1e-3f),
                        Snap(euler.Z, 1e-3f)
                    );
                g.Live = start with { Rotation = degrees == 0 ? start.Rotation : euler };
                return;
            }

            float grabbed = g.GrabAlong;
            float along = grabbed + screenAlong * g.HandleLength;
            if (g.OnPlane && AlongAxis(map, frame, mouse, start.Translation, axis) is float at)
                along = at;
            if (g.Mode == GizmoMode.Move)
            {
                var t = start.Translation;
                t[k] += free ? along - grabbed : Snap(along - grabbed, g.MoveStep);
                g.Live = start with { Translation = t };
                return;
            }
            var s = start.Scale;
            float factor = MathF.Abs(grabbed) > 1e-8f ? along / grabbed : 1;
            s[k] = start.Scale[k] * factor;
            s[k] = free
                ? Math.Max(s[k], GizmoMinScale)
                : Math.Max(Snap(s[k], GizmoScaleStep), GizmoScaleStep);
            g.Live = start with { Scale = s };
        }

        static string GizmoDragText(TransformGizmo g)
        {
            string axis = "XYZ"[g.Axis].ToString();
            var live = g.Live;
            return g.Mode switch
            {
                GizmoMode.Move => $"{axis} {live.Translation[g.Axis]:0.###}",
                GizmoMode.Rotate =>
                    $"{live.Rotation.X:0.#}, {live.Rotation.Y:0.#}, {live.Rotation.Z:0.#} degrees",
                _ => $"{axis} scale {live.Scale[g.Axis]:0.###}",
            };
        }
    }
}
