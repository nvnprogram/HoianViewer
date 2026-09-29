using System;
using System.Collections.Generic;
using System.Linq;
using ImGuiNET;
using OpenTK;
using PlayerViewer.HairGen;
using Vector2 = System.Numerics.Vector2;

namespace PlayerViewer.UI
{
    // A painted limb's frame: a rotate gizmo in the viewport at the focused limb's root, and the
    // same as numbers in its window.
    public partial class ViewerWindow
    {
        enum AimPart
        {
            None,
            Roll,
            Tilt,
            Swing,
        }

        const float AimStepDegrees = 15;
        const float AimRingPixels = 7;
        const int AimRingSegments = 64;

        PaintedLimb _aimLimb;
        AimPart _aimDrag;
        AimPart _aimHover;
        LimbAim _aimStart,
            _aimLive;
        string _aimSnapped;
        List<AimSnap> _aimSnaps = new();
        Action _aimCommit;

        //The drag's axis, and how the mouse turns about it: on the ring's plane, or along the
        //ring's tangent on screen when the plane is seen edge on.
        Vector3 _aimAxis,
            _aimGrab;
        bool _aimOnPlane;
        Vector2 _aimGrabScreen,
            _aimTangent;
        float _aimRadiusPixels;

        //Where the gizmo was drawn last frame, so a click on it does not also pick a particle.
        bool _aimShown;
        Vector2 _aimRootScreen;
        readonly Dictionary<AimPart, Vector2[]> _aimRings = new();

        //An aim being typed or slid in a limb window, shown in the viewport before it commits.
        readonly Dictionary<PaintedLimb, LimbAim> _aimPreview = new();

        bool AimDragging => _aimDrag != AimPart.None;

        //The limb window's Edit rotation pill: the rings show only while it is on.
        bool _aimEdit;

        //Shift was down over the gizmo or during its drag, so it does not move the camera till let go.
        bool _aimShiftHeld;

        bool AimHandleNear(Vector2 mouse) =>
            _aimShown && (AimDragging || RingUnder(mouse) != AimPart.None);

        /// <summary>The aim change the viewport let go of, for the authoring pass to run.</summary>
        Action TakeAimCommit()
        {
            var commit = _aimCommit;
            _aimCommit = null;
            return commit;
        }

        void SetLimbAim(PaintedLimb limb, LimbAim aim, string label)
        {
            if (_gen == null || !_gen.Limbs.Contains(limb) || limb.Aim == aim)
                return;
            limb.Aim = aim;
            AuthorStep(label, RebuildLimbs);
        }

        /// <summary>
        /// Rest model space to the viewport for a limb: through the pose of the bone its chain
        /// hangs from, as the limb bones overlay draws its tip.
        /// </summary>
        Matrix4 AimFrame(HairChain chain)
        {
            var render = _standalone.Render.Transform.TransformMatrix;
            var anchor = _gen.Rig.Bones[chain.Anchor];
            return PosedBone(_standalone.Skeletons(), anchor.Name) is Toolbox.Core.STBone posed
                ? Matrix4.Invert(anchor.World) * posed.Transform * render
                : render;
        }

        static float AngleBetween(Vector3 a, Vector3 b) =>
            MathHelper.RadiansToDegrees(
                MathF.Acos(Math.Clamp(Vector3.Dot(a.Normalized(), b.Normalized()), -1, 1))
            );

        //Roll, Tilt and Swing take the X, Y and Z colours.
        static Vector3 PartColour(AimPart part) => AxisColour((int)part - 1);

        static Vector3 SideOf(LimbAim aim) => aim.Side ?? LimbAim.LevelSide(aim.Direction);

        static Vector3 NormalOf(LimbAim aim) =>
            Vector3.Cross(aim.Direction, SideOf(aim)).Normalized();

        /// <summary>A ring's axis and the first of the two vectors spanning it, the second being their cross product.</summary>
        static (Vector3 Axis, Vector3 U) Ring(LimbAim aim, AimPart part) =>
            part switch
            {
                AimPart.Roll => (aim.Direction, SideOf(aim)),
                AimPart.Tilt => (SideOf(aim), NormalOf(aim)),
                _ => (NormalOf(aim), aim.Direction),
            };

        /// <summary>The focused painted limb whose window is open, with its chain, or null.</summary>
        (PaintedLimb Limb, HairChain Chain)? AimTarget()
        {
            if (
                _gen?.Rig == null
                || _paint != null
                || _standalone == null
                || !_physicsTabActive
                || _animExporting
                || !_aimEdit && !AimDragging
            )
                return null;
            int index = AimDragging ? _gen.Limbs.IndexOf(_aimLimb) : _genSelected;
            if (index < 0 || index >= _gen.Limbs.Count)
                return null;
            var limb = _gen.Limbs[index];
            if (limb.IsBoneLimb || !_limbWindows.Contains(limb))
                return null;
            int chain = _gen.ChainOf(limb);
            return chain < 0 ? null : (limb, _gen.Rig.Chains[chain]);
        }

        AimPart RingUnder(Vector2 mouse)
        {
            var best = AimPart.None;
            float bestDistance = AimRingPixels;
            foreach (var (part, points) in _aimRings)
            {
                float d = RingDistance(mouse, points);
                if (d < bestDistance)
                {
                    bestDistance = d;
                    best = part;
                }
            }
            return best;
        }

        //The viewport as last drawn, for placing a limb's window clear of its gizmo.
        ViewMap? _aimView;

        const float AimRingMinPixels = 80;

        /// <summary>The rings' radius in rest space: near half the limb's length, and never under a size on screen.</summary>
        static float AimRadius(HairChain chain, LimbAim aim, ViewMap map, Matrix4 frame)
        {
            float radius = Math.Clamp(
                Math.Max(0.02f, (chain.Path[^1] - chain.Path[0]).Length) * 0.45f,
                0.03f,
                0.12f
            );
            if (!map.Project(Vector3.TransformPosition(aim.Root, frame), out var at))
                return radius;
            float perUnit = 0;
            foreach (var axis in new[] { aim.Direction, SideOf(aim), NormalOf(aim) })
                if (
                    map.Project(Vector3.TransformPosition(aim.Root + axis * 0.1f, frame), out var s)
                )
                    perUnit = Math.Max(perUnit, (s - at).Length() / 0.1f);
            return perUnit > 1 ? Math.Max(radius, AimRingMinPixels / perUnit) : radius;
        }

        /// <summary>Where a painted limb's gizmo is on screen with room around it, or null.</summary>
        (Vector2 Min, Vector2 Max)? AimRect(PaintedLimb limb)
        {
            if (_aimView is not ViewMap map || _gen?.Rig == null || limb.IsBoneLimb)
                return null;
            int c = _gen.ChainOf(limb);
            if (c < 0 || _gen.AimOf(limb) is not LimbAim aim)
                return null;
            var chain = _gen.Rig.Chains[c];
            var frame = AimFrame(chain);
            float radius = AimRadius(chain, aim, map, frame);
            float length = Math.Max(0.02f, (chain.Path[^1] - chain.Path[0]).Length);
            var min = new Vector2(float.MaxValue);
            var max = new Vector2(float.MinValue);
            var side = SideOf(aim);
            var normal = NormalOf(aim);
            foreach (
                var p in new[]
                {
                    aim.Root + aim.Direction * Math.Max(radius, length),
                    aim.Root - aim.Direction * radius,
                    aim.Root + side * radius,
                    aim.Root - side * radius,
                    aim.Root + normal * radius,
                    aim.Root - normal * radius,
                }
            )
                if (map.Project(Vector3.TransformPosition(p, frame), out var s))
                {
                    min = Vector2.Min(min, s);
                    max = Vector2.Max(max, s);
                }
            if (min.X > max.X)
                return null;
            return (min - new Vector2(40), max + new Vector2(40));
        }

        /// <summary>The focused limb's rotate gizmo over the viewport, and its drag.</summary>
        void DrawAimHandle(Vector2 pos, Vector2 size, Vector2 uv0, Vector2 uv1, bool hovered)
        {
            _aimShown = false;
            _aimView = ViewMap.Of(_pipeline.Camera, pos, size, uv0, uv1);
            var target = AimTarget();
            if (target == null)
            {
                _aimDrag = AimPart.None;
                _aimRings.Clear();
                return;
            }
            var (limb, chain) = target.Value;
            var shownAim = AimDragging
                ? _aimLive
                : _aimPreview.GetValueOrDefault(limb) ?? _gen.AimOf(limb);
            if (shownAim == null)
                return;
            float length = Math.Max(0.02f, (chain.Path[^1] - chain.Path[0]).Length);
            var map = _aimView.Value;
            var frame = AimFrame(chain);
            bool Screen(Vector3 rest, out Vector2 s) =>
                map.Project(Vector3.TransformPosition(rest, frame), out s);

            var io = ImGui.GetIO();
            var mouse = io.MousePos;
            if (AimDragging)
                UpdateAimDrag(map, frame, mouse, io.KeyShift);
            var aim = AimDragging ? _aimLive : shownAim;
            if (!Screen(aim.Root, out var rs))
                return;
            float radius = AimRadius(chain, aim, map, frame);

            _aimRings.Clear();
            foreach (var part in new[] { AimPart.Roll, AimPart.Tilt, AimPart.Swing })
            {
                var (axis, u) = Ring(aim, part);
                var w = Vector3.Cross(axis, u);
                if (RingPoints(map, frame, aim.Root, u, w, radius, AimRingSegments) is { } points)
                    _aimRings[part] = points;
            }
            _aimShown = true;
            _aimRootScreen = rs;
            _aimHover = hovered && !AimDragging ? RingUnder(mouse) : AimPart.None;

            var dl = ImGui.GetWindowDrawList();
            dl.PushClipRect(pos, pos + size);

            //The plate the strip lies in: it flaps most freely across it.
            var side = SideOf(aim);
            float half = Math.Clamp(chain.Thickness, 0.015f, radius * 0.6f);
            if (
                Screen(aim.Root - side * half, out var p0)
                && Screen(aim.Root + side * half, out var p1)
                && Screen(aim.Root + aim.Direction * length + side * half, out var p2)
                && Screen(aim.Root + aim.Direction * length - side * half, out var p3)
                && Screen(aim.Root + aim.Direction * length, out var tip)
            )
            {
                dl.AddQuadFilled(p0, p1, p2, p3, Color(1, 1, 1, 0.22f));
                dl.AddQuad(p0, p1, p2, p3, Color(0, 0, 0, 0.5f), 3.5f);
                dl.AddQuad(p0, p1, p2, p3, Color(1, 1, 1, 0.9f), 1.5f);
                dl.AddLine(rs, tip, Color(0, 0, 0, 0.55f), 4);
                dl.AddLine(rs, tip, Color(PartColour(AimPart.Roll), 1), 2);
            }

            foreach (var (part, points) in _aimRings)
            {
                bool active = _aimDrag == part || _aimHover == part;
                DrawRing(dl, points, active ? ColSelect : Color(PartColour(part), 0.9f), active);
            }
            dl.AddCircleFilled(rs, 4, Color(1, 1, 1, 0.9f), 12);

            var shownPart = AimDragging ? _aimDrag : _aimHover;
            if (shownPart != AimPart.None)
            {
                var (yaw, pitch) = aim.Angles();
                string text = shownPart switch
                {
                    AimPart.Roll => $"roll {aim.Roll():0.0}: turns the flat side",
                    AimPart.Tilt => $"yaw {yaw:0.0}, pitch {pitch:0.0}: tilts across the flat side",
                    _ => $"yaw {yaw:0.0}, pitch {pitch:0.0}: swings along the flat side",
                };
                if (_aimSnapped != null)
                    text += $"\nsnapped: {_aimSnapped}";
                else if (!AimDragging)
                    text += "\ndrag to turn, Shift for no snapping";
                DrawCursorLabel(dl, map, mouse, text);
            }
            dl.PopClipRect();

            if (
                !AimDragging
                && _aimHover != AimPart.None
                && ImGui.IsMouseClicked(ImGuiMouseButton.Left)
            )
                BeginAimDrag(limb, shownAim, _aimHover, map, frame, mouse, radius);
            if (AimDragging && !ImGui.IsMouseDown(ImGuiMouseButton.Left))
            {
                var done = _aimLive;
                string snapped = _aimSnapped;
                string what = _aimDrag == AimPart.Roll ? "roll" : "aim";
                _aimDrag = AimPart.None;
                _aimSnapped = null;
                if (done != _aimStart)
                    _aimCommit = () =>
                        SetLimbAim(
                            limb,
                            done,
                            $"{what} of {limb.Name}{(snapped != null ? ", " + snapped : "")}"
                        );
            }
        }

        void BeginAimDrag(
            PaintedLimb limb,
            LimbAim aim,
            AimPart part,
            ViewMap map,
            Matrix4 frame,
            Vector2 mouse,
            float radius
        )
        {
            _aimLimb = limb;
            _aimDrag = part;
            _aimStart = aim;
            _aimLive = aim;
            _aimSnapped = null;
            _aimSnaps = LimbAimMath.Snaps(_gen, limb, aim);
            _aimAxis = Ring(aim, part).Axis;
            _aimGrabScreen = mouse;
            _aimOnPlane = RingGrab(map, frame, mouse, aim.Root, _aimAxis, out var grab);
            if (_aimOnPlane)
                _aimGrab = grab;
            //Edge on: the ring's tangent on screen where it was grabbed, a turn per radius dragged.
            var points = _aimRings.GetValueOrDefault(part);
            _aimTangent = Vector2.Zero;
            _aimRadiusPixels = 60;
            if (points != null)
            {
                _aimTangent = RingTangent(points, mouse);
                _aimRadiusPixels = RingRadiusPixels(points, _aimRootScreen);
            }
        }

        void UpdateAimDrag(ViewMap map, Matrix4 frame, Vector2 mouse, bool free)
        {
            var start = _aimStart;
            float angle;
            if (_aimOnPlane)
            {
                if (RingAngle(map, frame, mouse, start.Root, _aimAxis, _aimGrab) is not float a)
                    return;
                angle = a;
            }
            else
                angle = Vector2.Dot(mouse - _aimGrabScreen, _aimTangent) / _aimRadiusPixels;

            LimbAim Turned(float radians)
            {
                var turned = start.Rotated(Quaternion.FromAxisAngle(_aimAxis, radians));
                //A tilt moves the chain off its traced line, so it is built straight from the root.
                return _aimDrag == AimPart.Roll
                    ? turned with
                    {
                        Direction = start.Direction,
                    }
                    : turned with
                    {
                        Straight = true,
                    };
            }

            _aimSnapped = null;
            var dragged = Turned(angle);
            var live = dragged;
            if (!free)
            {
                //Every candidate is measured from where the mouse put the frame; the nearest wins.
                float best = float.MaxValue;
                foreach (var snap in _aimSnaps)
                {
                    if (_aimDrag == AimPart.Roll)
                    {
                        //The side only, square to this limb's direction; a strip's side has no sense.
                        var s = SideOf(snap.Frame);
                        s -= dragged.Direction * Vector3.Dot(s, dragged.Direction);
                        if (
                            s.LengthSquared < 1e-6f
                            || AngleBetween(dragged.Direction, snap.Frame.Direction) > 30
                        )
                            continue;
                        s.Normalize();
                        if (Vector3.Dot(s, SideOf(dragged)) < 0)
                            s = -s;
                        float a = AngleBetween(SideOf(dragged), s);
                        if (a < snap.Degrees && a < best)
                        {
                            best = a;
                            _aimSnapped = snap.Name;
                            live = dragged with { Side = s };
                        }
                    }
                    else
                    {
                        float a = AngleBetween(dragged.Direction, snap.Frame.Direction);
                        if (a < snap.Degrees && a < best)
                        {
                            best = a;
                            _aimSnapped = snap.Name;
                            live = dragged with
                            {
                                Direction = snap.Frame.Direction.Normalized(),
                                Side = SideOf(snap.Frame),
                            };
                        }
                    }
                }
                if (_aimSnapped == null)
                {
                    float step = MathHelper.DegreesToRadians(AimStepDegrees);
                    float stepped = MathF.Round(angle / step) * step;
                    if (MathF.Abs(stepped - angle) < MathHelper.DegreesToRadians(3))
                    {
                        live = Turned(stepped);
                        if (MathF.Abs(stepped) > 1e-4f)
                            _aimSnapped = $"{MathHelper.RadiansToDegrees(stepped):0} degrees";
                    }
                }
            }
            _aimLive = live;
        }

        /// <summary>A painted limb's frame in its window: yaw, pitch, roll and root as numbers, straight or traced, and the ways back to its twin or its traced frame.</summary>
        void DrawLimbAimControls(PaintedLimb limb, float label)
        {
            if (limb.IsBoneLimb)
                return;
            var built = _gen.AimOf(limb);
            if (built == null)
                return;
            var aim =
                AimDragging && _aimLimb == limb
                    ? _aimLive
                    : _aimPreview.GetValueOrDefault(limb) ?? built;
            ImGui.Spacing();
            Widgets.ToggleButton("Edit rotation##aimedit", ref _aimEdit);
            Widgets.ItemTooltip(
                (
                    limb.Aim == null ? "Traced from the paint. "
                    : limb.Aim.Straight ? "Set by hand, straight. "
                    : "Turned by hand, traced line. "
                ) + "Red ring rolls, green and blue tilt. Shift for no snapping."
            );
            var (yaw, pitch) = aim.Angles();
            float roll = aim.Roll();
            bool edited = false,
                done = false;
            string what = null;
            float yaw0 = yaw,
                pitch0 = pitch;
            StyleValue(
                "Roll",
                ref roll,
                -180,
                180,
                "Degrees the flat side is turned about the limb, from lying level.",
                label,
                ref edited,
                ref done,
                ref what
            );
            StyleValue(
                "Yaw",
                ref yaw,
                -180,
                180,
                "Degrees about the vertical, 0 facing forward. Builds the limb straight.",
                label,
                ref edited,
                ref done,
                ref what
            );
            StyleValue(
                "Pitch",
                ref pitch,
                -90,
                90,
                "Degrees up from level. Changing it builds the limb straight.",
                label,
                ref edited,
                ref done,
                ref what
            );
            bool turned = yaw != yaw0 || pitch != pitch0;
            var root = aim.Root;
            bool rootDone = false;
            if (aim.Straight)
                rootDone = VectorRow(
                    "Root",
                    $"aimroot{LimbId(limb)}",
                    ref root,
                    0.002f,
                    "%.3f",
                    label,
                    "Where the straight chain starts, in the model's rest space."
                );
            if (edited || rootDone || root != aim.Root)
            {
                var direction = turned ? LimbAim.FromAngles(yaw, pitch) : aim.Direction;
                _aimPreview[limb] = aim = new LimbAim(
                    root,
                    direction,
                    LimbAim.SideAt(direction, roll),
                    aim.Straight || turned
                );
            }
            if (done || rootDone)
            {
                var set = aim;
                _aimPreview.Remove(limb);
                Defer(
                    Deferred.Author,
                    () => SetLimbAim(limb, set, $"{(rootDone ? "root" : what)} of {limb.Name}")
                );
            }

            var twin = _gen.MirrorOf(limb);
            var spacing = ImGui.GetStyle().ItemSpacing;
            float half = (ImGui.GetContentRegionAvail().X - spacing.X) / 2;
            var mirrored = LimbAimMath.Mirrored(_gen, limb, twin, built);
            string mirrorLabel =
                twin == limb ? "Centre it"
                : twin != null ? $"Mirror {twin.Name}"
                : "Mirror";
            Widgets.DisabledButton(
                mirrorLabel + $"##aimmirror{LimbId(limb)}",
                mirrored != null,
                new Vector2(half, 0),
                () =>
                    Defer(
                        Deferred.Author,
                        () =>
                            SetLimbAim(
                                limb,
                                mirrored,
                                $"{limb.Name} {mirrorLabel.ToLowerInvariant()}"
                            )
                    )
            );
            Widgets.ItemTooltip(
                twin == limb ? "Turns it square to the centre plane, lying flat across it."
                : twin != null ? $"Takes {twin.Name}'s frame, mirrored, so the two move alike."
                : "No limb's paint mirrors this one's."
            );
            ImGui.SameLine();
            Widgets.DisabledButton(
                $"Trace again##aimtrace{LimbId(limb)}",
                limb.Aim != null,
                new Vector2(-1, 0),
                () =>
                    Defer(
                        Deferred.Author,
                        () => SetLimbAim(limb, null, $"{limb.Name} traced from the paint again")
                    )
            );
            Widgets.ItemTooltip("Drops the hand set frame and traces it from the paint again.");
        }
    }
}
