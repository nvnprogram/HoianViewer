using System;
using System.Collections.Generic;
using System.Linq;
using ImGuiNET;
using OpenTK;
using PlayerViewer.HairGen;
using PlayerViewer.Phive;
using Vector2 = System.Numerics.Vector2;

namespace PlayerViewer.UI
{
    // The cloth drawn over the viewport while the Physics tab is open: particles, links, the
    // collision shapes and the local ranges, the selection highlighted, and a click on a particle
    // or a shape selects it. Screen space ImGui lines over the rendered image, so an export never
    // carries them.
    public partial class ViewerWindow
    {
        readonly List<(Vector2 Screen, int Piece, int Particle)> _clothPickParticles = new();
        readonly List<(Vector2 Screen, float Radius, int Piece, int Collidable)> _clothPickShapes =
            new();
        readonly ViewportClick _clothClick = new();
        int _clothOverlayFault = -1;

        static readonly uint ColFree = Color(0.35f, 0.85f, 1.0f, 1);
        static readonly uint ColFixed = Color(1.0f, 0.45f, 0.2f, 1);
        static readonly uint ColSelect = Color(1.0f, 0.83f, 0.36f, 1);
        static readonly uint ColLink = Color(0.9f, 0.9f, 0.9f, 0.55f);
        static readonly uint ColBend = Color(0.5f, 0.6f, 1.0f, 0.35f);
        static readonly uint ColStretch = Color(0.6f, 1.0f, 0.6f, 0.35f);
        static readonly uint ColShape = Color(1.0f, 0.4f, 0.7f, 0.8f);
        static readonly uint ColShapeOff = Color(0.6f, 0.4f, 0.5f, 0.45f);
        static readonly uint ColRange = Color(0.4f, 1.0f, 0.5f, 0.35f);

        //Packed by hand: these are built before there is an ImGui context to ask.
        static uint Color(float r, float g, float b, float a) =>
            (uint)(a * 255 + 0.5f) << 24
            | (uint)(b * 255 + 0.5f) << 16
            | (uint)(g * 255 + 0.5f) << 8
            | (uint)(r * 255 + 0.5f);

        static uint Color(Vector3 rgb, float a) => Color(rgb.X, rgb.Y, rgb.Z, a);

        /// <summary>The posed bone of this name, from the first of the skeletons that has one.</summary>
        static Toolbox.Core.STBone PosedBone(
            IEnumerable<Toolbox.Core.STSkeleton> skeletons,
            string name
        )
        {
            foreach (var skeleton in skeletons)
                if (skeleton.SearchBone(name) is Toolbox.Core.STBone bone)
                    return bone;
            return null;
        }

        void DrawClothOverlay(Vector2 pos, Vector2 size, Vector2 uv0, Vector2 uv1, bool hovered)
        {
            _clothPickParticles.Clear();
            _clothPickShapes.Clear();
            if (
                _cloth == null
                || _standalone == null
                || !_physicsTabActive
                || _animExporting
                || _paint != null
            )
                return;
            var cam = _pipeline.Camera;
            var map = ViewMap.Of(cam, pos, size, uv0, uv1);
            Vector3 right = cam.InverseRotationMatrix.Row0;
            var dl = ImGui.GetWindowDrawList();
            dl.PushClipRect(pos, pos + size);
            var datas = _cloth.File.Container.ClothDatas;
            try
            {
                for (int i = 0; i < datas.Count; i++)
                {
                    if (_clothOnlySelected && _clothSelPiece >= 0 && i != _clothSelPiece)
                        continue;
                    DrawPieceOverlay(dl, map, right, datas[i], i);
                }
                if (_clothShowShapes)
                {
                    DrawLooseCollidables(dl, map, right);
                    DrawFocusedColliderGhost(dl, map, right);
                }
            }
            catch (Exception ex)
            {
                //A cloth file the overlay cannot read throws every frame, so each version logs once.
                if (_clothOverlayFault != _cloth.Version)
                {
                    _clothOverlayFault = _cloth.Version;
                    Console.WriteLine($"[Cloth] overlay: {ex}");
                }
            }
            dl.PopClipRect();
            UpdateClothPicking(hovered);
        }

        /// <summary>
        /// Each limb's bones as the cloth poses them, in the limb's colour: a line from the bone
        /// it hangs from through every chain bone to the tip. Drawn for every limb, whether it has
        /// a piece of its own or moves in another's.
        /// </summary>
        void DrawLimbBones(Vector2 pos, Vector2 size, Vector2 uv0, Vector2 uv1)
        {
            if (
                !_showBones
                || _gen?.Rig == null
                || _standalone == null
                || !_physicsTabActive
                || _animExporting
                || _paint != null
            )
                return;
            var map = ViewMap.Of(_pipeline.Camera, pos, size, uv0, uv1);
            var render = _standalone.Render.Transform.TransformMatrix;
            var skeletons = _standalone.Skeletons();
            Toolbox.Core.STBone Find(string name) => PosedBone(skeletons, name);
            var rig = _gen.Rig;
            int focus = _genHovered >= 0 ? _genHovered : _genSelected;
            var dl = ImGui.GetWindowDrawList();
            dl.PushClipRect(pos, pos + size);
            foreach (
                var (i, c) in _gen.Limbs.SelectMany((l, i) => _gen.ChainsOf(l).Select(c => (i, c)))
            )
            {
                if (rig.Chains[c].Bones.Length == 0)
                    continue;
                var chain = rig.Chains[c];
                var hue = StrandHue(i);
                float alpha = focus < 0 || focus == i ? 1 : 0.45f;
                uint line = Color(hue.X, hue.Y, hue.Z, alpha);
                uint joint = Color(
                    Math.Min(1, hue.X + 0.35f),
                    Math.Min(1, hue.Y + 0.35f),
                    Math.Min(1, hue.Z + 0.35f),
                    alpha
                );
                var points = new List<Vector2>();
                var first = rig.Bones[chain.Bones[0]];
                if (
                    first.Parent >= 0
                    && Find(rig.Bones[first.Parent].Name) is Toolbox.Core.STBone parent
                    && map.Project(
                        Vector3.TransformPosition(Vector3.Zero, parent.Transform * render),
                        out var ps
                    )
                )
                    points.Add(ps);
                int firstBone = points.Count;
                Toolbox.Core.STBone last = null;
                foreach (int b in chain.Bones)
                {
                    last = Find(rig.Bones[b].Name);
                    if (
                        last != null
                        && map.Project(
                            Vector3.TransformPosition(Vector3.Zero, last.Transform * render),
                            out var s
                        )
                    )
                        points.Add(s);
                }
                //The tip rides on the last bone as it rests there.
                if (last != null)
                {
                    var tipLocal = Vector3.TransformPosition(
                        chain.At(chain.Length),
                        Matrix4.Invert(rig.Bones[chain.Bones[^1]].World)
                    );
                    if (
                        map.Project(
                            Vector3.TransformPosition(tipLocal, last.Transform * render),
                            out var ts
                        )
                    )
                        points.Add(ts);
                }
                for (int p = 1; p < points.Count; p++)
                {
                    //The link to the parent bone is faint: it is not part of the limb.
                    if (p <= firstBone)
                    {
                        dl.AddLine(
                            points[p - 1],
                            points[p],
                            Color(hue.X, hue.Y, hue.Z, 0.35f * alpha),
                            1
                        );
                        continue;
                    }
                    dl.AddLine(points[p - 1], points[p], Color(0, 0, 0, 0.5f * alpha), 4);
                    dl.AddLine(points[p - 1], points[p], line, 2);
                }
                //The last point is the tip; the joints are the bones.
                for (int p = firstBone; p < points.Count - 1; p++)
                {
                    dl.AddCircleFilled(points[p], 4.5f, Color(0, 0, 0, 0.5f * alpha), 12);
                    dl.AddCircleFilled(points[p], 3.5f, joint, 12);
                }
            }
            dl.PopClipRect();
        }

        void DrawPieceOverlay(
            ImDrawListPtr dl,
            ViewMap map,
            Vector3 right,
            ClothData piece,
            int index
        )
        {
            var runtime = _clothRuntime.Pieces.FirstOrDefault(p => p.Index == index);
            var sim = piece.SimClothDatas[0];
            Vector3[] positions = runtime?.Sim?.RenderPositions ?? ClothEdit.RestPositions(sim);
            if (runtime?.Sim != null && !runtime.Sim.Primed)
                positions = ClothEdit.RestPositions(sim);
            bool selectedPiece = _clothSelPiece == index;
            var screen = new Vector2[positions.Length];
            var visible = new bool[positions.Length];
            for (int p = 0; p < positions.Length; p++)
                visible[p] = map.Project(positions[p], out screen[p]);

            if (_clothShowShapes && runtime?.Sim != null)
            {
                for (int c = 0; c < runtime.Compiled.Collidables.Count; c++)
                {
                    var col = runtime.Compiled.Collidables[c];
                    bool selected =
                        _clothSel == ClothSelKind.Collidable && SelectedCollidableName() == col.Name
                        || IsFocusedCollidable(col.Name);
                    uint color =
                        selected ? ColSelect
                        : runtime.Sim.IsCollided(c) ? ColShape
                        : ColShapeOff;
                    DrawShape(
                        dl,
                        map,
                        right,
                        col.Shape,
                        runtime.Sim.CollidableWorld(c),
                        col.Start,
                        col.End,
                        col.Radius,
                        color,
                        (index, c)
                    );
                }
            }

            //A limb's piece in the limb's colour, anything else white.
            int limb = PieceLimb(piece.Name);
            var hue = limb >= 0 ? StrandHue(limb) : Vector3.One;
            var fixedSet = sim.FixedParticles.Ints().ToHashSet();
            if (_clothShowLinks)
            {
                uint fill = Color(hue.X, hue.Y, hue.Z, 0.07f);
                foreach (var (a, b, c) in LinkTriangles(sim, fixedSet))
                    if (
                        a < screen.Length
                        && b < screen.Length
                        && c < screen.Length
                        && visible[a]
                        && visible[b]
                        && visible[c]
                    )
                        dl.AddTriangleFilled(screen[a], screen[b], screen[c], fill);
                for (int s = 0; s < sim.ConstraintSets.Count; s++)
                {
                    var set = sim.ConstraintSets[s];
                    bool selectedSet =
                        _clothSel == ClothSelKind.Set && selectedPiece && _clothSelIndex == s;
                    uint color = set.Kind switch
                    {
                        ConstraintSetKind.StandardLink => limb >= 0
                            ? Color(hue.X, hue.Y, hue.Z, 0.7f)
                            : ColLink,
                        ConstraintSetKind.BendLink => ColBend,
                        ConstraintSetKind.StretchLink => ColStretch,
                        _ => 0,
                    };
                    if (color == 0 || (set.Kind != ConstraintSetKind.StandardLink && !selectedSet))
                        continue;
                    int r = 0;
                    foreach (var link in set.Records.Objects)
                    {
                        int a = link.Int("particleA"),
                            b = link.Int("particleB");
                        if (a < screen.Length && b < screen.Length && visible[a] && visible[b])
                        {
                            bool hover = selectedSet && _clothHoverRecord == r;
                            dl.AddLine(
                                screen[a],
                                screen[b],
                                hover ? ColSelect
                                    : selectedSet ? Color(1, 0.83f, 0.36f, 0.7f)
                                    : color,
                                hover ? 3
                                    : selectedSet ? 2
                                    : 1
                            );
                        }
                        r++;
                    }
                }
            }

            if (_clothShowRanges && runtime?.Sim != null)
            {
                foreach (var set in sim.ConstraintSets.OfType<LocalRangeSet>())
                {
                    int r = 0;
                    bool selectedSet =
                        _clothSel == ClothSelKind.Set
                        && selectedPiece
                        && sim.ConstraintSets.IndexOf(set) == _clothSelIndex;
                    foreach (var entry in set.Records.Objects)
                    {
                        int v = entry.Int("referenceVertex");
                        var skinned = runtime.Sim.Skinned;
                        if (
                            v < skinned.Length
                            && map.Project(skinned[v], out var centre)
                            && map.Project(
                                skinned[v] + right * entry.Float("shapeRadius"),
                                out var edge
                            )
                        )
                        {
                            bool hover = selectedSet && _clothHoverRecord == r;
                            dl.AddCircle(
                                centre,
                                (edge - centre).Length(),
                                hover ? ColSelect : ColRange,
                                24,
                                hover ? 2 : 1
                            );
                        }
                        r++;
                    }
                }
            }

            if (!_clothShowParticles)
                return;
            for (int p = 0; p < positions.Length; p++)
            {
                if (!visible[p])
                    continue;
                _clothPickParticles.Add((screen[p], index, p));
                bool isFixed = fixedSet.Contains(p);
                float size = selectedPiece || _clothSelPiece < 0 ? 3.5f : 2.5f;
                if (isFixed)
                    dl.AddRectFilled(
                        screen[p] - new Vector2(size),
                        screen[p] + new Vector2(size),
                        ColFixed
                    );
                else
                    dl.AddCircleFilled(screen[p], size, ColFree, 12);
                bool selected =
                    selectedPiece && _clothSel == ClothSelKind.Particle && _clothSelIndex == p;
                bool hover =
                    selectedPiece && _clothSel == ClothSelKind.Particles && _clothHoverRecord == p;
                if (selected || hover)
                    dl.AddCircle(screen[p], 8, ColSelect, 16, 2);
            }
        }

        /// <summary>The limb a generated piece was built for, by the name its leading chain gave it, or -1.</summary>
        int PieceLimb(string name)
        {
            if (_gen?.Rig == null || name == null)
                return -1;
            for (int i = 0; i < _gen.Limbs.Count; i++)
                foreach (int c in _gen.ChainsOf(_gen.Limbs[i]))
                {
                    string own = "Cloth_" + _gen.Rig.Chains[c].Name;
                    if (
                        name == own
                        || name.StartsWith(own + "_")
                            && name.Length > own.Length + 1
                            && name[(own.Length + 1)..].All(char.IsDigit)
                    )
                        return i;
                }
            return -1;
        }

        /// <summary>
        /// The triangles whose three sides are standard links, two fixed particles counting as
        /// linked since they move together: the surface the links close.
        /// </summary>
        static List<(int A, int B, int C)> LinkTriangles(SimClothData sim, HashSet<int> fixedSet)
        {
            var adjacent = new Dictionary<int, HashSet<int>>();
            void Add(int a, int b)
            {
                if (!adjacent.TryGetValue(a, out var set))
                    adjacent[a] = set = new();
                set.Add(b);
            }
            foreach (var set in sim.ConstraintSets)
                if (set.Kind == ConstraintSetKind.StandardLink)
                    foreach (var link in set.Records.Objects)
                    {
                        int a = link.Int("particleA"),
                            b = link.Int("particleB");
                        Add(a, b);
                        Add(b, a);
                    }
            bool Joined(int a, int b) =>
                adjacent.TryGetValue(a, out var n) && n.Contains(b)
                || fixedSet.Contains(a) && fixedSet.Contains(b);
            var found = new HashSet<(int, int, int)>();
            foreach (var (a, near) in adjacent)
            foreach (int b in near)
            foreach (int c in near)
            {
                if (b >= c || !Joined(b, c))
                    continue;
                int[] t = { a, b, c };
                Array.Sort(t);
                found.Add((t[0], t[1], t[2]));
            }
            return found.ToList();
        }

        string SelectedCollidableName()
        {
            if (_clothSel != ClothSelKind.Collidable)
                return null;
            var file = _cloth.File;
            return _clothSelPiece >= 0
                ? file
                    .Container.ClothDatas.ElementAtOrDefault(_clothSelPiece)
                    ?.SimClothDatas[0]
                    .PerInstanceCollidables.ElementAtOrDefault(_clothSelIndex)
                    ?.Name
                : file.Container.Collidables.ElementAtOrDefault(_clothSelIndex)?.Name;
        }

        /// <summary>Collidables no running piece draws (none uses them, or nothing runs), at their rest transform.</summary>
        void DrawLooseCollidables(ImDrawListPtr dl, ViewMap map, Vector3 right)
        {
            var drawn = _clothRuntime
                .Pieces.Where(p => p.Sim != null)
                .SelectMany(p => p.Compiled.Collidables.Select(c => c.Name))
                .ToHashSet();
            var collidables = _cloth.File.Container.Collidables;
            for (int c = 0; c < collidables.Count; c++)
            {
                var col = collidables[c];
                if (drawn.Contains(col.Name))
                    continue;
                Vector3 start = Vector3.Zero,
                    end = Vector3.Zero;
                float radius = 0;
                switch (col.ShapeKind)
                {
                    case CollidableShapeKind.Capsule:
                        (start, end, radius) = col.Capsule;
                        break;
                    case CollidableShapeKind.Sphere:
                        start = end = col.Sphere.Xyz;
                        radius = col.Sphere.W;
                        break;
                    default:
                        continue;
                }
                bool selected =
                    _clothSel == ClothSelKind.Collidable && SelectedCollidableName() == col.Name
                    || IsFocusedCollidable(col.Name);
                DrawShape(
                    dl,
                    map,
                    right,
                    col.ShapeKind,
                    HkValue.Affine(col.Transform) * _standalone.RootMotion,
                    start,
                    end,
                    radius,
                    selected ? ColSelect : ColShapeOff,
                    (-1, c)
                );
            }
        }

        /// <summary>
        /// A capsule as two end circles joined by their outline, a sphere as a circle, both at
        /// their projected size; a click picks it when <paramref name="pick"/> is given.
        /// </summary>
        void DrawShape(
            ImDrawListPtr dl,
            ViewMap map,
            Vector3 right,
            CollidableShapeKind kind,
            Matrix4 world,
            Vector3 start,
            Vector3 end,
            float radius,
            uint color,
            (int Piece, int Collidable)? pick
        )
        {
            if (kind is not (CollidableShapeKind.Capsule or CollidableShapeKind.Sphere))
                return;
            Vector3 a = Vector3.TransformPosition(start, world);
            Vector3 b =
                kind == CollidableShapeKind.Capsule ? Vector3.TransformPosition(end, world) : a;
            if (
                !map.Project(a, out var sa)
                || !map.Project(b, out var sb)
                || !map.Project(a + right * radius, out var ra)
            )
                return;
            float r = (ra - sa).Length();
            dl.AddCircle(sa, r, color, 32, 1.5f);
            if (kind == CollidableShapeKind.Capsule)
            {
                dl.AddCircle(sb, r, color, 32, 1.5f);
                var d = sb - sa;
                if (d.LengthSquared() > 1e-6f)
                {
                    var n = Vector2.Normalize(new Vector2(-d.Y, d.X)) * r;
                    dl.AddLine(sa + n, sb + n, color, 1.5f);
                    dl.AddLine(sa - n, sb - n, color, 1.5f);
                }
            }
            if (pick is var (piece, index))
                _clothPickShapes.Add(((sa + sb) * 0.5f, Math.Max(r, 6), piece, index));
        }

        /// <summary>A click that did not drag picks the particle under the cursor, else the shape.</summary>
        void UpdateClothPicking(bool hovered)
        {
            var mouse = ImGui.GetIO().MousePos;
            if (!_clothClick.Update(hovered && !AimHandleNear(mouse)))
                return;
            var hits = _clothPickParticles
                .Where(p => (p.Screen - mouse).Length() < 9)
                .OrderBy(p => (p.Screen - mouse).Length())
                .ToList();
            if (hits.Count > 0)
            {
                SelectCloth(ClothSelKind.Particle, hits[0].Piece, hits[0].Particle);
                return;
            }
            var shape = _clothPickShapes
                .Where(s => (s.Screen - mouse).Length() < s.Radius)
                .OrderBy(s => (s.Screen - mouse).Length())
                .Select(s => ((int Piece, int Collidable)?)(s.Piece, s.Collidable))
                .FirstOrDefault();
            if (!shape.HasValue)
                return;
            SelectCloth(ClothSelKind.Collidable, shape.Value.Piece, shape.Value.Collidable);
            //A generated collidable picked in the viewport is also picked in the authoring list.
            string name = SelectedCollidableName();
            if (_gen?.Colliders.FirstOrDefault(c => c.BuiltName == name) is LimbCollider picked)
                _colSelected = picked;
        }

        /// <summary>A click in the viewport without a drag: pressed where allowed, let go within a few pixels.</summary>
        sealed class ViewportClick
        {
            bool _pressed;
            Vector2 _at;

            /// <summary>Called every frame; true on the release that ends such a click.</summary>
            public bool Update(bool canStart)
            {
                var io = ImGui.GetIO();
                if (canStart && ImGui.IsMouseClicked(ImGuiMouseButton.Left) && !io.KeyShift)
                {
                    _pressed = true;
                    _at = io.MousePos;
                }
                if (!_pressed || !ImGui.IsMouseReleased(ImGuiMouseButton.Left))
                    return false;
                _pressed = false;
                return (io.MousePos - _at).Length() <= 4;
            }
        }

        /// <summary>Each bone's joint on screen, null where the bone is missing or does not project.</summary>
        static Vector2?[] ProjectJoints(
            ViewMap map,
            Matrix4 render,
            IReadOnlyList<Toolbox.Core.STBone> bones
        )
        {
            var screen = new Vector2?[bones.Count];
            for (int i = 0; i < bones.Count; i++)
                if (
                    bones[i] != null
                    && map.Project(
                        Vector3.TransformPosition(Vector3.Zero, bones[i].Transform * render),
                        out var s
                    )
                )
                    screen[i] = s;
            return screen;
        }

        /// <summary>A line from each joint to its parent's; those <paramref name="strong"/> picks stand out.</summary>
        static void DrawJointLinks(
            ImDrawListPtr dl,
            Vector2?[] screen,
            IReadOnlyList<int> parents,
            uint colour,
            Func<int, bool> strong = null
        )
        {
            for (int i = 0; i < screen.Length; i++)
            {
                int parent = parents[i];
                if (
                    screen[i] is not Vector2 a
                    || parent < 0
                    || parent >= screen.Length
                    || screen[parent] is not Vector2 b
                )
                    continue;
                if (strong?.Invoke(i) == true)
                {
                    dl.AddLine(b, a, Color(0, 0, 0, 0.5f), 4);
                    dl.AddLine(b, a, ColSelect, 2);
                }
                else
                    dl.AddLine(b, a, colour, 1);
            }
        }

        /// <summary>
        /// A disc on each joint, the picked ones larger and in their colour. Returns the joint
        /// nearest the mouse within 9 pixels while hovered, else -1.
        /// </summary>
        static int DrawJointDots(
            ImDrawListPtr dl,
            Vector2?[] screen,
            Func<int, bool> picked,
            uint pickedColour,
            bool hovered
        )
        {
            var mouse = ImGui.GetIO().MousePos;
            int near = -1;
            float nearDistance = 9;
            for (int i = 0; i < screen.Length; i++)
            {
                if (screen[i] is not Vector2 s)
                    continue;
                bool on = picked(i);
                float r = on ? 4.5f : 3;
                dl.AddCircleFilled(s, r + 1, Color(0, 0, 0, 0.5f), 12);
                dl.AddCircleFilled(s, r, on ? pickedColour : Color(0.85f, 0.85f, 0.85f, 0.8f), 12);
                float d = (s - mouse).Length();
                if (hovered && d < nearDistance)
                {
                    nearDistance = d;
                    near = i;
                }
            }
            return near;
        }

        static void DrawJointName(ImDrawListPtr dl, Vector2 at, string name)
        {
            dl.AddCircle(at, 8, ColSelect, 16, 2);
            Widgets.DrawText(dl, at + new Vector2(10, -8), Color(1, 1, 1, 1), name);
        }
    }
}
