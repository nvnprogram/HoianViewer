using System;
using System.Collections.Generic;
using System.Linq;
using ImGuiNET;
using OpenTK;
using PlayerViewer.HairGen;
using PlayerViewer.Physics;
using PlayerViewer.Rigging;
using Vector2 = System.Numerics.Vector2;

namespace PlayerViewer.UI
{
    // Painting a limb on the standalone model: the session a new limb or a repaint runs in, the
    // brush under the mouse (picked on the rest surface, marking the welded nodes inside its sphere,
    // or with the surface brush those reached along the mesh within its radius), the rest pose held
    // while it runs, and the painted surface drawn over the model.
    public partial class ViewerWindow
    {
        /// <summary>A limb being painted or having its bones picked, and for a repaint what Cancel puts back.</summary>
        sealed class LimbPaintSession
        {
            public PaintedLimb Limb;
            public bool IsNew;

            /// <summary>Picking the model's bones rather than painting; the chains picked so far, as bone indices.</summary>
            public bool Bones;
            public List<List<int>> Chains;

            public byte[] ModelBytes;
            public ClothDocument Cloth;
            public int ClothVersion;
            public int GenClothVersion;
            public HashSet<int> Nodes;
            public StrandStyle Style;

            /// <summary>Nodes the other limbs have, which this one's brush leaves alone.</summary>
            public HashSet<int> Claimed = new();

            //The paint or picks before each stroke or pick of this session, and those undone.
            public readonly List<(HashSet<int> Nodes, List<List<int>> Chains)> Undo = new(),
                Redo = new();

            //The animation taken off for the rest pose, put back when painting ends.
            public string AnimName;
            public float AnimFrame;
            public bool AnimPaused;
        }

        LimbPaintSession _paint;
        LimbPaintOverlay _paintOverlay;
        float _brushRadius = 0.055f;
        int _paintVersion;
        int _paintShownVersion = -1;

        bool _brushHit;
        Vector3 _brushCentre;
        bool _paintStroke;
        Vector3? _strokeLast;

        //The mesh piece a stroke started on; the stroke picks and paints only that piece.
        int _strokeIsland = -1;

        //The surface brush spreads along the mesh from where it touches rather than through space.
        bool _surfaceBrush = true;
        Vector2? _strokeLastMouse;
        int _brushTriangle = -1;

        //The surface brush's reach under the cursor as last worked out, and what it was for.
        Dictionary<int, float> _surfaceReach;
        (Vector3 Centre, float Radius, bool Erase, int Version) _surfaceReachFor;

        //The last surface pick and what it was for, kept while the mouse, camera and model stand still.
        (
            (MeshGraph Graph, Vector2 Mouse, Matrix4 ToClip, int Only)? For,
            bool Hit,
            Vector3 Point,
            int Island,
            int Triangle
        ) _pick;

        const float BrushMin = 0.005f;
        const float BrushMax = 0.25f;

        static readonly Vector3 BrushRgb = Vector3.One;
        static readonly Vector3 EraseRgb = new(1, 0.3f, 0.25f);

        void StartNewLimb()
        {
            if (!EnsureGenerator())
                return;
            BeginPaint(
                new LimbPaintSession
                {
                    Limb = new PaintedLimb { Name = NewLimbName() },
                    IsNew = true,
                }
            );
        }

        /// <summary>
        /// Repaints a limb, or picks a bone limb's bones again: a painted limb's bones go while it
        /// is painted, and a snapshot of everything before is kept for Cancel.
        /// </summary>
        void StartEditLimb(PaintedLimb limb)
        {
            if (limb.IsBoneLimb)
            {
                StartEditBoneLimb(limb);
                return;
            }
            var session = new LimbPaintSession
            {
                Limb = limb,
                ModelBytes = _standalone.SourceData,
                Cloth = _cloth,
                ClothVersion = _cloth?.Version ?? -1,
                GenClothVersion = _genClothVersion,
                Nodes = new HashSet<int>(limb.Nodes),
                Style = limb.Style,
            };
            BeginPaint(session);
            try
            {
                _gen.BuildFromLimbs(limb);
                ShowGeneratedModel();
            }
            catch (Exception ex)
            {
                CancelPaint();
                _genError = "Repainting failed: " + ex.Message;
                Console.WriteLine($"[HairGen] {ex}");
            }
        }

        void BeginPaint(LimbPaintSession session)
        {
            //The state before the session is what an undo of its Done goes back to.
            EnsureUndoBase();
            _paint = session;
            //Before a repaint swaps the model, so the animation is kept for EndPaint.
            TakeAnimationOff();
            session.Claimed = new HashSet<int>(
                _gen?.Limbs.Where(l => l != session.Limb).SelectMany(l => l.Nodes)
                    ?? Enumerable.Empty<int>()
            );
            _authoringOpen = true;
            _genError = null;
            _genNotice = null;
            _bonePickNote = null;
            _bonePickHover = -1;
            _paintStroke = false;
            _strokeLast = null;
            _paintVersion++;
            _pipeline.BoneTints = null;
            if (session.Bones)
            {
                _pipeline.LimbPaint = null;
                return;
            }
            _paintOverlay ??= new LimbPaintOverlay();
            _paintOverlay.SetGraph(_gen.Graph);
            _pipeline.LimbPaint = _paintOverlay;
        }

        /// <summary>
        /// Done for a new limb, Save for a repaint: the limb is built into the rig and the cloth.
        /// The notice says which of the model's bones the build removed or brought back.
        /// </summary>
        void FinishPaint()
        {
            var session = _paint;
            if (session == null)
                return;
            string step =
                session.Bones && session.IsNew ? $"add limb {session.Limb.Name} from bones"
                : session.Bones ? $"pick the bones of {session.Limb.Name} again"
                : session.IsNew ? $"paint limb {session.Limb.Name}"
                : $"repaint {session.Limb.Name}";
            AuthorStep(step, () => FinishPaintStep(session));
        }

        void FinishPaintStep(LimbPaintSession session)
        {
            if (session.Bones)
            {
                if (session.Chains.Count == 0)
                    return;
                session.Limb.BoneChains = session
                    .Chains.Select(c => c.Select(b => PickBones[b].Name).ToArray())
                    .ToList();
            }
            else if (session.Limb.Nodes.Count == 0)
                return;
            else
                session.Limb.Hang = null;
            var before = RemovedBoneNames();
            if (session.IsNew)
                _gen.Limbs.Add(session.Limb);
            EndPaint();
            RebuildLimbs();
            OpenLimbWindow(session.Limb);
            var after = RemovedBoneNames();
            var replaced = after.Where(n => !before.Contains(n)).ToList();
            var back = before.Where(n => !after.Contains(n)).ToList();
            if (session.Bones)
                _genNotice = back.Count > 0 ? $"Brought back {BoneList(back)}." : null;
            else
                _genNotice =
                    (
                        replaced.Count > 0
                            ? $"{session.Limb.Name} replaced {BoneList(replaced)}."
                            : $"{session.Limb.Name} replaced none of the model's bones: none served only this part."
                    ) + (back.Count > 0 ? $" Brought back {BoneList(back)}." : "");
        }

        /// <summary>The model's bones the current rig removes, by name.</summary>
        List<string> RemovedBoneNames() =>
            _gen?.Rig == null
                ? new List<string>()
                : _gen.Rig.Removed.OrderBy(b => b).Select(b => _gen.Rig.Bones[b].Name).ToList();

        /// <summary>"5 bones: A, B, C, D, E" with the rest counted past six, or "1 bone: A".</summary>
        static string BoneList(List<string> names)
        {
            if (names.Count == 1)
                return "1 bone: " + names[0];
            var shown = names.Take(6).ToList();
            return $"{names.Count} bones: {string.Join(", ", shown)}"
                + (names.Count > shown.Count ? $" and {names.Count - shown.Count} more" : "");
        }

        /// <summary>Drops a new limb, or puts a repainted one back as it was: its paint, the model with its bones and the cloth with its edits.</summary>
        void CancelPaint()
        {
            var session = _paint;
            if (session == null)
                return;
            //A bone limb's chains are only written on Save, so there is nothing to put back.
            if (!session.IsNew && !session.Bones)
            {
                session.Limb.Nodes = session.Nodes;
                session.Limb.Style = session.Style;
                try
                {
                    _gen.BuildFromLimbs();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[HairGen] {ex}");
                }
                ShowModelBytes(session.ModelBytes);
                _cloth = session.Cloth;
                _genClothVersion = session.GenClothVersion;
                ValidateClothSelection();
            }
            EndPaint();
        }

        void EndPaint()
        {
            var session = _paint;
            _paint = null;
            _paintStroke = false;
            _brushHit = false;
            _pipeline.LimbPaint = null;
            _clothRuntime.Invalidate();
            if (
                session?.AnimName != null
                && _standalone != null
                && _standalone.AnimNames.Contains(session.AnimName)
            )
            {
                _standalone.PlayAnim(session.AnimName);
                _standalone.SetAnimFrame(session.AnimFrame);
                _standalone.AnimPaused = session.AnimPaused;
            }
        }

        /// <summary>
        /// The standalone model at its bind pose while a limb is painted, so the mesh on screen is
        /// the rest surface the paint is picked on: no test motion, no cloth, no animation.
        /// </summary>
        void HoldRestPose(float dt)
        {
            TakeAnimationOff();
            _standalone.RootMotion = Matrix4.Identity;
            _standalone.RefreshPose = true;
            _standalone.Update(dt);
        }

        /// <summary>Takes the playing animation off the model, keeping it in the session for <see cref="EndPaint"/>.</summary>
        void TakeAnimationOff()
        {
            if (_standalone?.CurrentAnimName == null)
                return;
            _paint.AnimName = _standalone.CurrentAnimName;
            _paint.AnimFrame = _standalone.AnimFrame;
            _paint.AnimPaused = _standalone.AnimPaused;
            _standalone.PlayAnim(null);
        }

        /// <summary>Rest model space to where the viewport draws the standalone model.</summary>
        Matrix4 PaintTransform() =>
            _standalone.RootMotion * _standalone.Render.Transform.TransformMatrix;

        /// <summary>Marks or clears every node within the radius of the segment between two brush centres.</summary>
        void ApplyBrush(Vector3 from, Vector3 to, float radius, bool erase, int island = -1)
        {
            if (
                LimbBrush.Paint(
                    _gen.Graph,
                    _paint.Limb.Nodes,
                    _paint.Claimed,
                    from,
                    to,
                    radius,
                    erase,
                    island
                )
            )
                _paintVersion++;
        }

        /// <summary>Marks or clears what the surface brush reaches from the seeds; painting stops at other limbs' nodes.</summary>
        void ApplySurfaceBrush(
            IEnumerable<(int Triangle, Vector3 At)> seeds,
            float radius,
            bool erase
        )
        {
            if (
                LimbBrush.PaintSurface(
                    _gen.Graph,
                    _paint.Limb.Nodes,
                    _paint.Claimed,
                    seeds,
                    radius,
                    erase
                )
            )
                _paintVersion++;
        }

        /// <summary>
        /// Where a surface stroke touched since last frame: the hit now, and hits picked along the
        /// mouse's path, so a fast drag leaves no gaps. Only on the stroke's mesh piece.
        /// </summary>
        List<(int Triangle, Vector3 At)> StrokeSeeds(ViewMap map, Matrix4 transform, Vector2 mouse)
        {
            var seeds = new List<(int, Vector3)> { (_brushTriangle, _brushCentre) };
            if (_strokeLastMouse is not Vector2 last)
                return seeds;
            float pixels = (mouse - last).Length();
            if (pixels < 4)
                return seeds;
            int steps = Math.Min(16, (int)MathF.Ceiling(pixels / 6));
            for (int k = 1; k < steps; k++)
                if (
                    map.Ray(
                        transform,
                        Vector2.Lerp(last, mouse, k / (float)steps),
                        out var origin,
                        out var ray
                    )
                    && LimbBrush.RaySurface(
                        _gen.Graph,
                        origin,
                        ray,
                        _strokeIsland,
                        out var at,
                        out _,
                        out int triangle
                    )
                )
                    seeds.Add((triangle, at));
            return seeds;
        }

        /// <summary>The brush in the viewport: picked on the rest surface, painting on a left drag, drawn as its sphere's outline.</summary>
        void DrawLimbBrush(Vector2 pos, Vector2 size, Vector2 uv0, Vector2 uv1, bool hovered)
        {
            _brushHit = false;
            bool active =
                _paint != null
                && _standalone != null
                && _gen != null
                && !_animExporting
                && _physicsTabActive;
            if (active && _paint.Bones)
            {
                _pipeline.LimbPaint = null;
                DrawBonePickViewport(pos, size, uv0, uv1, hovered);
                return;
            }
            _pipeline.LimbPaint = active ? _paintOverlay : null;
            if (!active)
            {
                _paintStroke = false;
                return;
            }
            var io = ImGui.GetIO();
            var transform = PaintTransform();
            var cam = _pipeline.Camera;
            var map = ViewMap.Of(cam, pos, size, uv0, uv1);
            if (
                !io.WantTextInput
                && !Widgets.Typing
                && !io.KeyCtrl
                && !io.KeyAlt
                && ImGui.IsKeyPressed((int)OpenTK.Input.Key.R, false)
            )
                _surfaceBrush = !_surfaceBrush;
            if (hovered && io.KeyShift && io.MouseWheel != 0)
                _brushRadius = Math.Clamp(
                    _brushRadius * (1 + 0.1f * io.MouseWheel),
                    BrushMin,
                    BrushMax
                );

            if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left) && !io.KeyShift)
            {
                _paintStroke = true;
                _strokeLast = null;
                _strokeIsland = -1;
                PushPaintUndo();
            }
            if (!ImGui.IsMouseDown(ImGuiMouseButton.Left))
            {
                if (_paintStroke)
                    DropPaintUndoIfSame();
                _paintStroke = false;
                _strokeIsland = -1;
            }

            int filter = _paintStroke ? _strokeIsland : -1;
            if (
                (hovered || _paintStroke)
                && PickSurface(
                    map,
                    io.MousePos,
                    transform,
                    filter,
                    out var hit,
                    out int island,
                    out _brushTriangle
                )
            )
            {
                _brushHit = true;
                _brushCentre = hit;
                if (_paintStroke && _strokeIsland < 0)
                    _strokeIsland = island;
            }
            bool erase = io.KeyCtrl;
            if (_paintStroke && _brushHit)
            {
                if (_surfaceBrush)
                    ApplySurfaceBrush(
                        StrokeSeeds(map, transform, io.MousePos),
                        _brushRadius,
                        erase
                    );
                else
                    ApplyBrush(
                        _strokeLast ?? _brushCentre,
                        _brushCentre,
                        _brushRadius,
                        erase,
                        _strokeIsland
                    );
                _strokeLast = _brushCentre;
                _strokeLastMouse = io.MousePos;
            }
            else
            {
                _strokeLast = null;
                _strokeLastMouse = null;
            }

            if (_brushHit)
            {
                var centre = Vector3.TransformPosition(_brushCentre, transform);
                var right = cam.InverseRotationMatrix.Row0;
                //The transform may scale; the radius is in rest space.
                float scale = Vector3.TransformVector(Vector3.UnitX, transform).Length;
                if (
                    map.Project(centre, out var sc)
                    && map.Project(centre + right * _brushRadius * scale, out var se)
                )
                {
                    var dl = ImGui.GetWindowDrawList();
                    dl.PushClipRect(pos, pos + size);
                    var rgb = erase ? EraseRgb : BrushRgb;
                    uint colour = Color(rgb, 0.9f);
                    //The surface brush's reach is lit on the mesh; its ring only gives the size.
                    if (_surfaceBrush)
                        dl.AddCircle(sc, (se - sc).Length(), Color(rgb, 0.375f), 48, 1);
                    else
                        dl.AddCircle(sc, (se - sc).Length(), colour, 48, 1.5f);
                    dl.AddCircleFilled(sc, 2, colour);
                    dl.PopClipRect();
                }
            }
            UpdatePaintOverlay(transform, erase);
        }

        /// <summary>
        /// The closest graph triangle under the mouse, in rest model space, and the mesh piece it
        /// is on; only on piece <paramref name="only"/> when that is not -1.
        /// </summary>
        bool PickSurface(
            ViewMap map,
            Vector2 mouse,
            Matrix4 transform,
            int only,
            out Vector3 hit,
            out int island,
            out int triangle
        )
        {
            var graph = _gen.Graph;
            var key = (graph, mouse, transform * map.ViewProjection, only);
            if (_pick.For != key)
            {
                _pick.For = key;
                _pick.Point = default;
                _pick.Island = _pick.Triangle = -1;
                _pick.Hit =
                    map.Ray(transform, mouse, out var origin, out var ray)
                    && LimbBrush.RaySurface(
                        graph,
                        origin,
                        ray,
                        only,
                        out _pick.Point,
                        out _pick.Island,
                        out _pick.Triangle
                    );
            }
            hit = _pick.Point;
            island = _pick.Island;
            triangle = _pick.Triangle;
            return _pick.Hit;
        }

        /// <summary>Hands the overlay this frame's placement and brush, and the node colours (premultiplied) when the paint has changed.</summary>
        void UpdatePaintOverlay(Matrix4 transform, bool erase)
        {
            if (_paintOverlay == null)
                return;
            _paintOverlay.Model = transform;
            _paintOverlay.Brush = _brushHit
                ? new Vector4(_brushCentre, _brushRadius)
                : Vector4.Zero;
            _paintOverlay.BrushColour = erase ? EraseRgb : BrushRgb;
            _paintOverlay.SurfaceBrush = _surfaceBrush;
            if (_surfaceBrush && _brushHit)
            {
                var key = (_brushCentre, _brushRadius, erase, _paintVersion);
                if (_surfaceReach == null || _surfaceReachFor != key)
                {
                    _surfaceReachFor = key;
                    _surfaceReach = LimbBrush.SurfaceReach(
                        _gen.Graph,
                        new[] { (_brushTriangle, _brushCentre) },
                        _brushRadius,
                        erase ? null : _paint.Claimed
                    );
                    _paintOverlay.SetReach(
                        _surfaceReach.ToDictionary(e => e.Key, e => e.Value / _brushRadius)
                    );
                }
            }
            else if (_surfaceReach != null)
            {
                _surfaceReach = null;
                _paintOverlay.SetReach(null);
            }
            if (_paintShownVersion == _paintVersion)
                return;
            _paintShownVersion = _paintVersion;
            var colours = new Dictionary<int, Vector4>();
            for (int i = 0; i < _gen.Limbs.Count; i++)
            {
                if (_gen.Limbs[i] == _paint.Limb)
                    continue;
                var hue = StrandHue(i);
                foreach (int node in _gen.Limbs[i].Nodes)
                    colours[node] = new Vector4(hue * 0.8f * 0.55f, 0.55f);
            }
            int index = _gen.Limbs.IndexOf(_paint.Limb);
            var own = StrandHue(index < 0 ? _gen.Limbs.Count : index);
            own = Vector3.Lerp(own, Vector3.One, 0.2f);
            foreach (int node in _paint.Limb.Nodes)
                colours[node] = new Vector4(own, 1);
            _paintOverlay.SetColours(colours);
        }
    }
}
