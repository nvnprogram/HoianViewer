using System;
using System.Collections.Generic;
using System.Linq;
using ImGuiNET;
using OpenTK;
using PlayerViewer.Phive;
using PlayerViewer.Physics;
using PlayerViewer.Rigging;
using Vector2 = System.Numerics.Vector2;

namespace PlayerViewer.UI
{
    // The cloth file's own dialogs: a new piece generated from bone chains, and a new collidable
    // on a bone.
    public partial class ViewerWindow
    {
        bool _authorPieceOpen;
        bool _authorCollidableOpen;
        StripSpec _authorSpec;
        readonly HashSet<int> _authorChainStarts = new();
        readonly HashSet<int> _authorCollidables = new();
        string _authorError;
        string _boneFilter = "";

        string _newColName = "Collidable_New";
        int _newColShape;
        int _newColBone = -1;
        Vector3 _newColStart = new(0, -0.05f, 0);
        Vector3 _newColEnd = new(0, 0.05f, 0);
        float _newColRadius = 0.08f;
        readonly HashSet<int> _newColPieces = new();

        List<AuthorBone> _clothModelBones;
        object _clothModelBonesFor;

        /// <summary>The standalone model's bones at rest, what authoring places things against.</summary>
        List<AuthorBone> ClothModelBones()
        {
            if (!ReferenceEquals(_clothModelBonesFor, _standalone))
            {
                _clothModelBones =
                    _standalone != null
                        ? ModelBones.FromSkeletons(_standalone.Skeletons())
                        : new List<AuthorBone>();
                _clothModelBonesFor = _standalone;
            }
            return _clothModelBones;
        }

        void OpenAuthorPiece()
        {
            _authorPieceOpen = true;
            _authorError = null;
            _authorChainStarts.Clear();
            _authorCollidables.Clear();
            int count = _cloth.File.Container.Collidables.Count;
            for (int i = 0; i < count; i++)
                _authorCollidables.Add(i);
            _authorSpec = new StripSpec { Name = UniquePieceName("Cloth_New") };
        }

        void OpenAuthorCollidable()
        {
            _authorCollidableOpen = true;
            _authorError = null;
            var bones = ClothModelBones();
            if (_newColBone < 0 || _newColBone >= bones.Count)
                _newColBone = Math.Max(0, BoneTree.IndexOf(bones, "Head_Root"));
            _newColName = UniqueCollidableName(
                "Collidable_" + bones.ElementAtOrDefault(_newColBone)?.Name
            );
            _newColPieces.Clear();
            for (int i = 0; i < _cloth.File.Container.ClothDatas.Count; i++)
                _newColPieces.Add(i);
        }

        string UniquePieceName(string stem)
        {
            var names = _cloth.File.Container.ClothDatas.Select(d => d.Name).ToHashSet();
            string name = stem;
            for (int i = 1; names.Contains(name); i++)
                name = $"{stem}_{i}";
            return name;
        }

        string UniqueCollidableName(string stem)
        {
            var names = _cloth.File.Container.Collidables.Select(c => c.Name).ToHashSet();
            string name = stem;
            for (int i = 1; names.Contains(name); i++)
                name = $"{stem}_{i}";
            return name;
        }

        void DrawClothAuthorWindows()
        {
            if (_standalone == null || _cloth == null)
            {
                _authorPieceOpen = _authorCollidableOpen = false;
                return;
            }
            if (_paint != null || !_physicsTabActive)
                return;
            if (_authorPieceOpen)
                DrawAuthorPieceWindow();
            if (_authorCollidableOpen)
                DrawAuthorCollidableWindow();
        }

        /// <summary>The bone list with a filter; returns the index clicked, -1 for none. Marked rows draw checked.</summary>
        int BonePicker(string id, Func<int, bool> marked, float height)
        {
            var bones = ClothModelBones();
            Widgets.SearchBox("##bonefilter" + id, ref _boneFilter, "filter bones", -1);
            int clicked = -1;
            Widgets.BeginList("##bones" + id, new Vector2(0, height));
            for (int i = 0; i < bones.Count; i++)
            {
                if (!Widgets.Matches(bones[i].Name, _boneFilter))
                    continue;
                ImGui.SetCursorPosX(ImGui.GetCursorPosX() + BoneDepth(bones, i) * 10);
                if (Widgets.ListRow($"{bones[i].Name}##b{i}", marked(i)))
                    clicked = i;
            }
            Widgets.EndList();
            return clicked;
        }

        /// <summary>How many parents a bone has, for indenting a flat bone list; deep chains stop at 12.</summary>
        static int BoneDepth(List<AuthorBone> bones, int i)
        {
            int depth = 0;
            for (int p = bones[i].Parent; p >= 0 && depth < 12; p = bones[p].Parent)
                depth++;
            return depth;
        }

        /// <summary>Where the authoring tool windows open the first time: right of the left panel.</summary>
        Vector2 ToolWindowPos()
        {
            var viewport = ImGui.GetMainViewport();
            return new Vector2(viewport.Pos.X + LeftPanelWidth + 16, viewport.Pos.Y + 64);
        }

        void DrawAuthorPieceWindow()
        {
            var bones = ClothModelBones();
            var spec = _authorSpec;
            ImGui.SetNextWindowPos(ToolWindowPos(), ImGuiCond.FirstUseEver);
            ImGui.SetNextWindowSize(new Vector2(440, 760), ImGuiCond.FirstUseEver);
            bool open = true;
            if (!BeginCardWindow("New cloth piece###authorpiece", ref open))
            {
                ImGui.End();
                _authorPieceOpen = open;
                return;
            }
            ImGui.PushTextWrapPos();
            Widgets.DimText(
                "Pick the first bone of each chain; it runs down first children to the end."
            );
            ImGui.PopTextWrapPos();
            int clicked = BonePicker("piece", i => _authorChainStarts.Contains(i), 170);
            if (clicked >= 0 && !_authorChainStarts.Remove(clicked))
                _authorChainStarts.Add(clicked);

            var chains = _authorChainStarts
                .OrderBy(i => i)
                .Select(i => BoneTree.ChainFrom(bones, i))
                .ToList();
            foreach (var chain in chains)
                Widgets.DimText(string.Join(" > ", chain.Select(b => bones[b].Name)));
            if (chains.Count == 0)
                Widgets.DimText("No chain picked yet.");

            int anchor =
                spec.Anchor >= 0 ? spec.Anchor
                : chains.Count > 0 ? bones[chains[0][0]].Parent
                : -1;
            Widgets.LabelRow("Anchor", ClothLabelWidth);
            if (
                Widgets.BeginCombo(
                    "##anchor",
                    anchor >= 0 ? bones[anchor].Name : "(parent of the first chain bone)"
                )
            )
            {
                for (int i = 0; i < bones.Count; i++)
                    if (ImGui.Selectable($"{bones[i].Name}##a{i}", i == anchor))
                        spec.Anchor = i;
                ImGui.EndCombo();
            }
            Widgets.ItemTooltip("The bone the fixed level is skinned to: the head for hair.");

            Widgets.LabelRow("Name", ClothLabelWidth);
            Widgets.InputText("##piecename", ref spec.Name, 64);

            Header("Shape");
            Slider("Width", ref spec.Width, 0.005f, 0.5f, "Strip width across each level.");
            Slider(
                "Root offset",
                ref spec.RootOffset,
                0,
                1.5f,
                "The fixed level sits this fraction of the first segment before the first bone."
            );
            Slider(
                "Tip length",
                ref spec.TipLength,
                0,
                1.5f,
                "A free level past the last bone, as a fraction of the last segment; 0 for none."
            );
            Slider("Roll", ref spec.Roll, -180, 180, "Turns the strip about the chain, degrees.");
            Header("Particles");
            Slider(
                "Total mass",
                ref spec.Mass,
                0.01f,
                20,
                "Spread evenly over the free particles. Stock pieces: 1 to 13."
            );
            Slider("Radius", ref spec.Radius, 0, 0.2f, "Each particle's collision radius.");
            Slider("Friction", ref spec.Friction, 0, 1, "Stock: 0.5.");
            Header("Constraints");
            Slider("Link stiffness", ref spec.LinkStiffness, 0, 2, "Link stiffness. Stock: 1.");
            Widgets.CheckboxControl("Stretch links", ref spec.Stretch);
            Widgets.ItemTooltip(
                "Each free particle can be at most its rest distance from the fixed particle of its side."
            );
            ImGui.SameLine();
            Widgets.CheckboxControl("Bend links", ref spec.Bend);
            ImGui.SameLine();
            Widgets.CheckboxControl("Local range", ref spec.LocalRange);
            if (spec.Bend)
                Slider(
                    "Bend min / max",
                    ref spec.BendRatio,
                    0,
                    1,
                    "The bend links' minimum as a fraction of the rest distance. Stock: 0.8."
                );
            if (spec.LocalRange)
            {
                Slider(
                    "Range at root",
                    ref spec.RangeRoot,
                    0,
                    2,
                    "Radius as a fraction of the distance to the fixed level, at the first free level. Stock long strands: 0.45."
                );
                Slider(
                    "Range at tip",
                    ref spec.RangeTip,
                    0,
                    2,
                    "The same at the last level. Stock: 0.95."
                );
                Slider("Range stiffness", ref spec.RangeSetStiffness, 0, 2, "Stock: 0.6 to 0.8.");
            }
            Header("Simulation");
            Slider(
                "Gravity",
                ref spec.Gravity,
                0,
                20,
                "The game runs 9.81 or 0; any other value it forces to 9.81."
            );
            Slider("Damping / s", ref spec.Damping, 0, 1, "Stock: 0.001.");
            Widgets.SliderInt("Iterations", ref spec.Iterations, 1, 8, "%d");

            Header("Collides with");
            var collidables = _cloth.File.Container.Collidables;
            if (collidables.Count == 0)
                Widgets.DimText(
                    "No collidables yet; add one first, or add the piece and attach them later."
                );
            for (int i = 0; i < collidables.Count; i++)
            {
                bool on = _authorCollidables.Contains(i);
                if (
                    Widgets.CheckboxControl(
                        $"{collidables[i].ShapeKind}: {collidables[i].Name}##ac{i}",
                        ref on
                    )
                )
                {
                    if (on)
                        _authorCollidables.Add(i);
                    else
                        _authorCollidables.Remove(i);
                }
            }

            if (_authorError != null)
                Widgets.ErrorText(_authorError);
            ImGui.Separator();
            Widgets.DisabledButton(
                "Create piece",
                chains.Count > 0 && anchor >= 0 && !string.IsNullOrWhiteSpace(spec.Name),
                () =>
                {
                    try
                    {
                        CreatePiece(spec, chains, anchor);
                        open = false;
                    }
                    catch (Exception ex)
                    {
                        _authorError = ex.Message;
                        Console.WriteLine($"[Cloth] {ex}");
                    }
                }
            );
            ImGui.End();
            _authorPieceOpen = open;
        }

        static void Slider(string label, ref float value, float min, float max, string tip)
        {
            Widgets.LabelRow(label, ClothLabelWidth, tip);
            Widgets.SliderValue("##" + label, ref value, min, max, "%.3f", out _);
        }

        void CreatePiece(StripSpec spec, List<int[]> chains, int anchor)
        {
            if (_cloth.File.Container.ClothDatas.Any(d => d.Name == spec.Name))
                throw new InvalidOperationException($"A piece named {spec.Name} exists already");
            var file = _cloth.File.WithTypes(ClothAuthor.PieceTypes, Packs.DonorTypeSections());
            var collidables = file.Container.Collidables;
            spec.Chains = chains;
            spec.Anchor = anchor;
            spec.Collidables = _authorCollidables
                .Where(i => i < collidables.Count)
                .OrderBy(i => i)
                .Select(i => collidables[i])
                .ToList();
            ClothAuthor.AddStripPiece(file, spec, ClothModelBones());
            AuthorStep(
                "add piece " + spec.Name,
                () =>
                {
                    _cloth.Replace(file);
                    _cloth.Commit();
                }
            );
            int index = _cloth.File.Container.ClothDatas.FindIndex(d => d.Name == spec.Name);
            SelectCloth(ClothSelKind.Piece, index, -1);
            _clothNote = null;
            Console.WriteLine($"[Cloth] generated piece {spec.Name}: {chains.Count} chain(s)");
        }

        void DrawAuthorCollidableWindow()
        {
            var bones = ClothModelBones();
            ImGui.SetNextWindowPos(ToolWindowPos(), ImGuiCond.FirstUseEver);
            ImGui.SetNextWindowSize(new Vector2(420, 600), ImGuiCond.FirstUseEver);
            bool open = true;
            if (!BeginCardWindow("New collidable###authorcollidable", ref open))
            {
                ImGui.End();
                _authorCollidableOpen = open;
                return;
            }
            Widgets.DimText("The bone it follows:");
            int clicked = BonePicker("col", i => i == _newColBone, 160);
            if (clicked >= 0)
            {
                bool renamed = _newColName.StartsWith("Collidable_");
                _newColBone = clicked;
                if (renamed)
                    _newColName = UniqueCollidableName("Collidable_" + bones[clicked].Name);
            }
            Widgets.LabelRow("Name", ClothLabelWidth);
            Widgets.InputText("##colname", ref _newColName, 64);
            Widgets.LabelRow("Shape", ClothLabelWidth);
            Widgets.Combo("##colshape", ref _newColShape, new[] { "Capsule", "Sphere" }, 2);
            Widgets.DimText("In the bone's own space:");
            VectorRow("Start / centre", "ncs", ref _newColStart, 0.002f, "%.3f", ClothLabelWidth);
            if (_newColShape == 0)
                VectorRow("End", "nce", ref _newColEnd, 0.002f, "%.3f", ClothLabelWidth);
            Slider(
                "Radius",
                ref _newColRadius,
                0.001f,
                1,
                "The shape's radius; a particle is pushed out to this plus its own."
            );

            Header("Pieces that collide with it");
            var datas = _cloth.File.Container.ClothDatas;
            for (int i = 0; i < datas.Count; i++)
            {
                bool on = _newColPieces.Contains(i);
                if (Widgets.CheckboxControl($"{datas[i].Name}##np{i}", ref on))
                {
                    if (on)
                        _newColPieces.Add(i);
                    else
                        _newColPieces.Remove(i);
                }
            }
            if (_authorError != null)
                Widgets.ErrorText(_authorError);
            ImGui.Separator();
            Widgets.DisabledButton(
                "Create collidable",
                _newColBone >= 0 && !string.IsNullOrWhiteSpace(_newColName),
                () =>
                {
                    try
                    {
                        CreateCollidable();
                        open = false;
                    }
                    catch (Exception ex)
                    {
                        _authorError = ex.Message;
                        Console.WriteLine($"[Cloth] {ex}");
                    }
                }
            );
            ImGui.End();
            _authorCollidableOpen = open;
        }

        void CreateCollidable()
        {
            var bones = ClothModelBones();
            if (_cloth.File.Container.Collidables.Any(c => c.Name == _newColName))
                throw new InvalidOperationException(
                    $"A collidable named {_newColName} exists already"
                );
            var file = _cloth.File.WithTypes(
                ClothAuthor.CollidableTypes,
                Packs.DonorTypeSections()
            );
            var kind = _newColShape == 0 ? CollidableShapeKind.Capsule : CollidableShapeKind.Sphere;
            var col = ClothAuthor.AddCollidable(
                file,
                _newColName,
                kind,
                bones[_newColBone].World,
                _newColStart,
                _newColEnd,
                _newColRadius
            );
            var datas = file.Container.ClothDatas;
            foreach (int i in _newColPieces.Where(i => i < datas.Count))
                ClothAuthor.Attach(file, datas[i], col, bones, _newColBone);
            AuthorStep(
                "add collidable " + _newColName,
                () =>
                {
                    _cloth.Replace(file);
                    _cloth.Commit();
                }
            );
            int index = _cloth.File.Container.Collidables.FindIndex(c => c.Name == _newColName);
            SelectCloth(ClothSelKind.Collidable, -1, index);
            Console.WriteLine(
                $"[Cloth] added collidable {_newColName} on {bones[_newColBone].Name}"
            );
        }
    }
}
