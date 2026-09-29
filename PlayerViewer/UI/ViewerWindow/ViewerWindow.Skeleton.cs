using System;
using System.Collections.Generic;
using System.Linq;
using BfresLibrary;
using ImGuiNET;
using PlayerViewer.HairGen;
using PlayerViewer.Rigging;
using Vector2 = System.Numerics.Vector2;
using Vector3 = OpenTK.Vector3;

namespace PlayerViewer.UI
{
    // The Skeleton section of the standalone model: the bone tree, the selected bone's weights
    // tinted on the mesh and its joint in the viewport, and its name and rest transform edited
    // into the model bytes the viewer shows and Save writes. Each commit is a step of the
    // authoring history.
    public partial class ViewerWindow
    {
        sealed class BoneView
        {
            public string Name;
            public int Parent;
            public BoneSrt Srt;
            public Vector3 World;
            public int Smooth,
                Rigid,
                Vertices,
                Shapes;
            public bool Euler;
            public List<int> Children = new();
        }

        sealed class SkeletonModelView
        {
            public string Name;
            public List<BoneView> Bones = new();
        }

        //The shown model's skeletons as the file holds them, read again when the bytes change.
        byte[] _skelSource;
        List<SkeletonModelView> _skelModels = new();

        bool _skeletonTabActive;
        int _skelModel;
        int _skelBone = -1;
        string _skelBoneName;
        string _skelFilter = "";
        bool _skelMoveMesh;
        bool _skelJoints = true;
        bool _skelTint = true;
        bool _skelGizmo;
        bool _skelReveal;
        string _skelError;
        string _skelNote;
        int _skelHoverJoint = -1;
        readonly ViewportClick _skelClick = new();

        //The values in the boxes, rotation in degrees, for the bone they were read from.
        (byte[] Source, int Model, string Bone) _skelEditFor;
        Vector3 _skelT,
            _skelR,
            _skelS;
        string _skelName = "";

        //The bone whose Delete was pressed, waiting for the second press.
        object _skelPendingDelete;

        static readonly OpenTK.Vector4 SkelTint = new(1f, 0.42f, 0.12f, 1);

        void ResetSkeletonTab()
        {
            _skelSource = null;
            _skelModels = new();
            _skelModel = 0;
            _skelBone = -1;
            _skelBoneName = null;
            _skelError = _skelNote = null;
            CancelDeferred(Deferred.Skeleton);
            _skelPendingDelete = null;
            _skelEditFor = default;
        }

        List<SkeletonModelView> SkeletonViews()
        {
            var source = _standalone?.SourceData;
            if (source == null)
                return new();
            if (ReferenceEquals(source, _skelSource))
                return _skelModels;
            _skelSource = source;
            _skelModels = new();
            try
            {
                var res = Core.BfresBytes.Read(source);
                foreach (var model in res.Models.Values)
                {
                    var bones = model.Skeleton.Bones.Values.ToList();
                    var world = ModelSkin.RestWorlds(bones);
                    var counts = BoneEdit.VertexCounts(model);
                    var view = new SkeletonModelView { Name = model.Name };
                    for (int i = 0; i < bones.Count; i++)
                        view.Bones.Add(
                            new BoneView
                            {
                                Name = bones[i].Name,
                                Parent = bones[i].ParentIndex,
                                Srt = BoneEdit.Read(bones[i]),
                                World = world[i].ExtractTranslation(),
                                Smooth = bones[i].SmoothMatrixIndex,
                                Rigid = bones[i].RigidMatrixIndex,
                                Vertices = i < counts.Length ? counts[i] : 0,
                                Shapes = model.Shapes.Values.Count(s =>
                                    s.BoneIndex == i && s.VertexSkinCount == 0
                                ),
                                Euler = bones[i].FlagsRotation == BoneFlagsRotation.EulerXYZ,
                            }
                        );
                    foreach (var (bone, i) in view.Bones.Select((b, i) => (b, i)))
                        if (bone.Parent >= 0 && bone.Parent < view.Bones.Count)
                            view.Bones[bone.Parent].Children.Add(i);
                    _skelModels.Add(view);
                }
            }
            catch (Exception ex)
            {
                _skelError = "Reading the skeleton failed: " + ex.Message;
                Console.WriteLine($"[Skeleton] {ex}");
            }
            return _skelModels;
        }

        /// <summary>The selected bone of the chosen model, kept by name across reloads, else by index.</summary>
        BoneView SelectedBone(SkeletonModelView model)
        {
            if (model == null)
                return null;
            if (_skelBoneName != null)
            {
                int byName = model.Bones.FindIndex(b => b.Name == _skelBoneName);
                if (byName >= 0)
                    _skelBone = byName;
            }
            if (_skelBone < 0 || _skelBone >= model.Bones.Count)
            {
                _skelBone = -1;
                _skelBoneName = null;
                return null;
            }
            _skelBoneName = model.Bones[_skelBone].Name;
            return model.Bones[_skelBone];
        }

        void SelectSkeletonBone(int bone, bool reveal)
        {
            _skelBone = bone;
            var views = SkeletonViews();
            _skelBoneName =
                _skelModel < views.Count && bone >= 0 && bone < views[_skelModel].Bones.Count
                    ? views[_skelModel].Bones[bone].Name
                    : null;
            _skelReveal = reveal;
            _skelNote = null;
        }

        void DrawSkeletonTab()
        {
            _skeletonTabActive = true;
            var views = SkeletonViews();
            if (views.Count == 0)
            {
                Widgets.DimText("This model has no skeleton to show.");
                return;
            }
            _skelModel = Math.Clamp(_skelModel, 0, views.Count - 1);
            if (views.Count > 1)
            {
                ImGui.SetNextItemWidth(-1);
                if (Widgets.BeginCombo("##skelmodel", views[_skelModel].Name))
                {
                    for (int m = 0; m < views.Count; m++)
                        if (ImGui.Selectable($"{views[m].Name}##skm{m}", m == _skelModel))
                        {
                            _skelModel = m;
                            _skelBone = -1;
                            _skelBoneName = null;
                        }
                    ImGui.EndCombo();
                }
            }
            var model = views[_skelModel];
            var selected = SelectedBone(model);
            _pipeline.BoneTints =
                _skelTint && selected != null
                    ? new Dictionary<string, OpenTK.Vector4> { [selected.Name] = SkelTint }
                    : null;

            ImGui.AlignTextToFramePadding();
            Widgets.Text($"{model.Bones.Count} bones");
            ImGui.SameLine();
            Widgets.SearchBox("##skelfilter", ref _skelFilter, "filter bones", -1);
            float rowHeight = ImGui.GetTextLineHeightWithSpacing();
            float listHeight = Math.Min(model.Bones.Count, 8) * rowHeight + 12;
            Widgets.BeginList("##skeltree", new Vector2(0, listHeight));
            if (string.IsNullOrEmpty(_skelFilter))
            {
                var reveal = new HashSet<int>();
                if (_skelReveal && selected != null)
                    for (int p = selected.Parent; p >= 0; p = model.Bones[p].Parent)
                        reveal.Add(p);
                for (int i = 0; i < model.Bones.Count; i++)
                    if (model.Bones[i].Parent < 0 || model.Bones[i].Parent >= model.Bones.Count)
                        DrawBoneNode(model, i, 0, reveal);
            }
            else
                for (int i = 0; i < model.Bones.Count; i++)
                {
                    if (!Widgets.Matches(model.Bones[i].Name, _skelFilter))
                        continue;
                    var parent = model.Bones[i].Parent;
                    string under = parent >= 0 ? "   in " + model.Bones[parent].Name : "";
                    if (Widgets.ListRow($"{model.Bones[i].Name}{under}##skf{i}", i == _skelBone))
                        SelectSkeletonBone(i, true);
                }
            _skelReveal = false;
            Widgets.EndList();

            if (selected == null)
            {
                ImGui.PushTextWrapPos();
                Widgets.DimText(
                    "Pick a bone in the list or its joint in the viewport to see the surface it carries and edit it."
                );
                ImGui.PopTextWrapPos();
            }
            else
                DrawBoneDetails(model, selected);
            ImGui.PushTextWrapPos();
            if (_skelError != null)
                Widgets.ErrorText(_skelError);
            if (_skelNote != null)
                Widgets.ColoredText(Theme.Cyan, _skelNote);
            ImGui.PopTextWrapPos();
            Widgets.CheckboxControl("Joints", ref _skelJoints);
            Widgets.ItemTooltip(
                "Draws every joint in the viewport; a click on one selects its bone."
            );
            ImGui.SameLine();
            Widgets.CheckboxControl("Tint weights", ref _skelTint);
            Widgets.ItemTooltip("Colours the surface by the selected bone's skinning weight.");
            DrawUndoButtons();
        }

        void DrawBoneNode(SkeletonModelView model, int index, int depth, HashSet<int> reveal)
        {
            var bone = model.Bones[index];
            var flags =
                ImGuiTreeNodeFlags.OpenOnArrow
                | ImGuiTreeNodeFlags.OpenOnDoubleClick
                | ImGuiTreeNodeFlags.SpanAvailWidth;
            if (bone.Children.Count == 0)
                flags |= ImGuiTreeNodeFlags.Leaf | ImGuiTreeNodeFlags.NoTreePushOnOpen;
            if (index == _skelBone)
                flags |= ImGuiTreeNodeFlags.Selected;
            if (reveal.Contains(index))
                ImGui.SetNextItemOpen(true);
            else if (depth < 3)
                ImGui.SetNextItemOpen(true, ImGuiCond.Once);
            Widgets.RowFill(index == _skelBone);
            bool open = ImGui.TreeNodeEx($"{bone.Name}##skb{index}", flags);
            if (ImGui.IsItemClicked() && !ImGui.IsItemToggledOpen())
                SelectSkeletonBone(index, false);
            if (index == _skelBone && _skelReveal)
                ImGui.SetScrollHereY(0.5f);
            if (!open || bone.Children.Count == 0)
                return;
            foreach (int child in bone.Children)
                DrawBoneNode(model, child, depth + 1, reveal);
            ImGui.TreePop();
        }

        /// <summary>Whether a painted limb made this bone, so it is not in the model as opened.</summary>
        bool GeneratedBone(string name) =>
            _gen != null && _gen.ChangesModel && _gen.Mesh.BoneIndex(name) < 0;

        void DrawBoneDetails(SkeletonModelView model, BoneView bone)
        {
            var key = (_standalone.SourceData, _skelModel, bone.Name);
            if (_skelEditFor != key && !Widgets.Typing && !ImGui.IsAnyItemActive())
            {
                _skelEditFor = key;
                _skelT = bone.Srt.Translation;
                _skelR = Degrees(bone.Srt.Rotation);
                _skelS = bone.Srt.Scale;
                _skelName = bone.Name;
            }
            bool generated = GeneratedBone(bone.Name);
            bool locked = generated || _paint != null;

            float label = Widgets.Column(78);
            Widgets.LabelRow("Name", label);
            Widgets.BeginDisabled(locked);
            Widgets.InputText("##skelname", ref _skelName, 64);
            bool nameDone = ImGui.IsItemDeactivated() && !locked;
            Widgets.EndDisabled();
            string problem = null;
            if (_skelName != bone.Name)
                problem = BoneNameProblem(model, _skelBone, _skelName);
            if (nameDone && _skelName != bone.Name)
            {
                if (problem == null)
                {
                    string name = _skelName;
                    int index = _skelBone;
                    Defer(
                        Deferred.Skeleton,
                        () => RenameSkeletonBone(_skelModel, index, name),
                        replace: true
                    );
                }
                else
                    _skelName = bone.Name;
            }
            ImGui.PushTextWrapPos();
            if (problem != null)
                Widgets.ErrorText(problem);
            else if (_skelName != bone.Name && RenameWarning(bone.Name) is string warning)
                Widgets.ColoredText(Theme.GoldBright, warning);

            Widgets.LabelRow("Parent", label, fill: false);
            if (bone.Parent >= 0 && bone.Parent < model.Bones.Count)
            {
                if (Widgets.SmallButton($"{model.Bones[bone.Parent].Name}##skparent"))
                    SelectSkeletonBone(bone.Parent, true);
                Widgets.ItemTooltip("Selects the parent.");
            }
            else
                Widgets.DimText("none, a root");
            if (!locked)
            {
                int index = _skelBone;
                Widgets.ConfirmButton(
                    "Delete bone",
                    $"Really delete {bone.Name}",
                    bone.Name,
                    ref _skelPendingDelete,
                    () =>
                        Defer(
                            Deferred.Skeleton,
                            () => DeleteSkeletonBone(_skelModel, index),
                            replace: true
                        )
                );
                Widgets.ItemTooltip("Its children and the surface it moves go to its parent.");
            }
            string matrix =
                bone.Smooth >= 0 && bone.Rigid >= 0 ? $"smooth {bone.Smooth} and rigid {bone.Rigid}"
                : bone.Smooth >= 0 ? $"smooth skinning (matrix {bone.Smooth})"
                : bone.Rigid >= 0 ? $"rigid skinning (matrix {bone.Rigid})"
                : "no skinning matrix";
            Widgets.DimText(
                $"Bone {_skelBone}, {matrix}, weights {bone.Vertices} vertices"
                    + (bone.Shapes > 0 ? $", {bone.Shapes} shape(s) bound to it" : "")
            );
            if (generated)
                Widgets.ColoredText(
                    Theme.Cyan,
                    "A painted limb made this bone; edit the limb instead. It is made again on every rebuild."
                );
            else if (_paint != null)
                Widgets.ColoredText(Theme.Cyan, "Finish or cancel the limb being painted first.");
            ImGui.PopTextWrapPos();

            Widgets.BeginDisabled(locked);
            Widgets.ToggleButton("Edit transform##skelgizmo", ref _skelGizmo);
            Widgets.ItemTooltip("R -> cycle Transform/Rotation/Scale. Shift for no snapping.");
            bool t = VectorRow("Translate", "skt", ref _skelT, 0.001f, "%.4f", label);
            bool r = VectorRow("Rotate", "skr", ref _skelR, 0.5f, "%.2f", label);
            Widgets.ItemTooltip("X, Y, Z in degrees.");
            bool s = VectorRow("Scale", "sks", ref _skelS, 0.01f, "%.4f", label);
            Widgets.EndDisabled();
            if (!locked && (t || r || s))
            {
                var srt = EditedSrt(bone.Srt);
                if (srt != bone.Srt)
                {
                    int index = _skelBone;
                    string what =
                        t ? "translation"
                        : r ? "rotation"
                        : "scale";
                    Defer(
                        Deferred.Skeleton,
                        () =>
                            SetSkeletonBoneTransform(
                                _skelModel,
                                index,
                                srt,
                                $"{what} of {bone.Name}"
                            ),
                        replace: true
                    );
                }
            }
            Widgets.LabelRow("World", label, fill: false);
            Widgets.DimText($"{bone.World.X:0.####}, {bone.World.Y:0.####}, {bone.World.Z:0.####}");
            Widgets.ItemTooltip("The bone's position at rest in model space.");

            Widgets.CheckboxControl("Move mesh with bone", ref _skelMoveMesh);
            Widgets.ItemTooltip("Moves the mesh this bone and its children carry.");
            DrawWeightLegend(bone);
        }

        /// <summary>A labelled row of three click to type boxes; true on the frame an edit of any finished.</summary>
        static bool VectorRow(
            string label,
            string id,
            ref Vector3 value,
            float speed,
            string format,
            float column,
            string tip = null
        )
        {
            Widgets.LabelRow(label, column, tip, fill: false);
            float spacing = ImGui.GetStyle().ItemInnerSpacing.X;
            float width = (ImGui.GetContentRegionAvail().X - 2 * spacing) / 3;
            bool done = false;
            for (int k = 0; k < 3; k++)
            {
                if (k > 0)
                    ImGui.SameLine(0, spacing);
                ImGui.SetNextItemWidth(width);
                float v = value[k];
                Widgets.ValueBox($"##{id}{k}", ref v, speed, 0, 0, format, out bool finished);
                value[k] = v;
                done |= finished;
            }
            return done;
        }

        static Vector3 Degrees(Vector3 radians) =>
            new(
                (float)(radians.X * 180 / Math.PI),
                (float)(radians.Y * 180 / Math.PI),
                (float)(radians.Z * 180 / Math.PI)
            );

        /// <summary>
        /// A rotation edited in degrees, back in the file's radians; a component left as
        /// <paramref name="stored"/> shows it keeps its stored radians exactly.
        /// </summary>
        static Vector3 StoredRotation(Vector3 stored, Vector3 degrees)
        {
            var shown = Degrees(stored);
            var rotation = stored;
            for (int k = 0; k < 3; k++)
                if (degrees[k] != shown[k])
                    rotation[k] = (float)(degrees[k] * Math.PI / 180);
            return rotation;
        }

        /// <summary>The boxes as a transform in file terms.</summary>
        BoneSrt EditedSrt(BoneSrt stored) =>
            new(_skelT, StoredRotation(stored.Rotation, _skelR), _skelS);

        void DrawWeightLegend(BoneView bone)
        {
            if (!_skelTint)
                return;
            var dl = ImGui.GetWindowDrawList();
            float width = Math.Min(160, ImGui.GetContentRegionAvail().X * 0.5f);
            float h = ImGui.GetTextLineHeight() * 0.6f;
            ImGui.AlignTextToFramePadding();
            Widgets.DimText("Weight  0");
            ImGui.SameLine();
            var pos = ImGui.GetCursorScreenPos() + new Vector2(0, (ImGui.GetFrameHeight() - h) / 2);
            //Half the scene where the bone carries nothing, its tint over the scene where it carries all.
            uint none = ImGui.GetColorU32(new System.Numerics.Vector4(0.25f, 0.25f, 0.25f, 1));
            uint full = ImGui.GetColorU32(
                new System.Numerics.Vector4(SkelTint.X, SkelTint.Y, SkelTint.Z, 1)
            );
            dl.AddRectFilledMultiColor(pos, pos + new Vector2(width, h), none, full, full, none);
            ImGui.Dummy(new Vector2(width, ImGui.GetFrameHeight()));
            ImGui.SameLine();
            Widgets.DimText("1");
            if (bone.Vertices == 0)
                Widgets.DimText("This bone weights no vertex, so nothing is tinted.");
        }

        string BoneNameProblem(SkeletonModelView model, int index, string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return "A bone needs a name.";
            if (name != name.Trim())
                return "The name starts or ends with a space.";
            int other = model.Bones.FindIndex(b => b.Name == name);
            if (other >= 0 && other != index)
                return $"Bone {other} is already called {name}.";
            if (_gen != null && other < 0 && _gen.Mesh.BoneIndex(name) >= 0)
                return $"A bone a painted limb replaced is called {name}; undo or delete that limb first.";
            return null;
        }

        /// <summary>What else finds a bone by the name being changed, or null.</summary>
        string RenameWarning(string name)
        {
            var parts = new List<string>();
            if (name is "Head_Root" or "Skl_Root" or "Root")
                parts.Add("the game attaches the model by this name");
            if (
                _gen != null
                && (
                    name == _gen.RigOptions.AnchorBone
                    || name == _gen.ClothOptions.HeadBone
                    || name == _gen.ClothOptions.ChestBone
                    || _gen.ClothOptions.BodyColliders.Any(c => c.Bone == name)
                )
            )
                parts.Add("the physics generator looks it up by name");
            if (
                _cloth != null
                && _genClothVersion < 0
                && _cloth.File.Skeletons.Any(s => s.BoneNames.Contains(name))
            )
                parts.Add("the open cloth file names it, and would lose it");
            return parts.Count == 0
                ? null
                : "Careful: "
                    + string.Join("; ", parts)
                    + ". Animations and presets in other files keep the old name.";
        }

        /// <summary>
        /// Applies an edit to the model the skeleton comes from and shows the result. With a
        /// generator the edit goes into the model as opened, which the rig is written into, so
        /// the limbs are built again on the edited skeleton and keep it.
        /// </summary>
        void EditStandaloneSkeleton(
            int modelIndex,
            string boneName,
            Action<ResFile, Model, int> edit
        )
        {
            _skelError = null;
            byte[] source = _gen != null ? _genOriginal : _standalone.SourceData;
            var res = Core.BfresBytes.Read(source);
            var model = res.Models[modelIndex];
            int index = model.Skeleton.Bones.Values.ToList().FindIndex(b => b.Name == boneName);
            if (index < 0)
                throw new InvalidOperationException($"The model has no bone {boneName}.");
            edit(res, model, index);
            byte[] bytes = Core.BfresBytes.ToBytes(res);
            if (_gen == null)
            {
                ShowModelBytes(bytes);
                return;
            }
            if (_gen.Hair)
                bytes = HeadRoot.Ensure(bytes);
            var mesh = SkinnedMesh.FromModel(Core.BfresBytes.FirstModel(bytes));
            bool built = _gen.Rig != null;
            _gen = _gen.Rebased(mesh, _gen.HeadFor(mesh, _romfs, _genActor));
            _genOriginal = bytes;
            if (built)
                RebuildLimbs();
            else
                ShowModelBytes(bytes);
        }

        void SetSkeletonBoneTransform(int modelIndex, int bone, BoneSrt srt, string label)
        {
            var views = SkeletonViews();
            if (modelIndex >= views.Count || bone < 0 || bone >= views[modelIndex].Bones.Count)
                return;
            string name = views[modelIndex].Bones[bone].Name;
            if (GeneratedBone(name) || _paint != null)
                return;
            bool move = _skelMoveMesh;
            try
            {
                AuthorStep(
                    label,
                    () =>
                        EditStandaloneSkeleton(
                            modelIndex,
                            name,
                            (res, model, index) => BoneEdit.SetTransform(model, index, srt, move)
                        )
                );
            }
            catch (Exception ex)
            {
                _skelError = "The edit failed: " + ex.Message;
                Console.WriteLine($"[Skeleton] {ex}");
            }
            _skelEditFor = default;
        }

        /// <summary>
        /// Deletes a bone: its children hang from its parent where they were and its weights go to
        /// the parent, or for a root to the first root left. Limbs and collidables on it follow.
        /// </summary>
        void DeleteSkeletonBone(int modelIndex, int bone)
        {
            var views = SkeletonViews();
            if (modelIndex >= views.Count || bone < 0 || bone >= views[modelIndex].Bones.Count)
                return;
            var list = views[modelIndex].Bones;
            string name = list[bone].Name;
            if (GeneratedBone(name) || _paint != null)
                return;
            int parent = list[bone].Parent;
            int heir =
                parent >= 0 && parent < list.Count
                    ? parent
                    : Enumerable
                        .Range(0, list.Count)
                        .FirstOrDefault(
                            i => i != bone && (list[i].Parent < 0 || list[i].Parent == bone),
                            -1
                        );
            string heirName = heir >= 0 ? list[heir].Name : null;
            try
            {
                AuthorStep(
                    $"delete {name}",
                    () =>
                    {
                        if (_gen != null && modelIndex == 0)
                            _gen.DeleteBone(name, heirName);
                        EditStandaloneSkeleton(
                            modelIndex,
                            name,
                            (res, model, index) =>
                            {
                                if (BoneEdit.Delete(model, index) is string why)
                                    throw new InvalidOperationException(why);
                            }
                        );
                    }
                );
                _skelBoneName = heirName;
                _skelNote = $"Deleted {name}.";
            }
            catch (Exception ex)
            {
                _skelError = $"{name} was not deleted: {ex.Message}";
                Console.WriteLine($"[Skeleton] {ex}");
            }
            _skelEditFor = default;
        }

        void RenameSkeletonBone(int modelIndex, int bone, string name)
        {
            var views = SkeletonViews();
            if (modelIndex >= views.Count || bone < 0 || bone >= views[modelIndex].Bones.Count)
                return;
            string old = views[modelIndex].Bones[bone].Name;
            if (old == name || GeneratedBone(old) || _paint != null)
                return;
            int curves = 0;
            try
            {
                AuthorStep(
                    $"rename {old} to {name}",
                    () =>
                    {
                        if (_gen != null && modelIndex == 0)
                            _gen.RenameBone(old, name);
                        EditStandaloneSkeleton(
                            modelIndex,
                            old,
                            (res, model, index) => curves = BoneEdit.Rename(res, model, index, name)
                        );
                    }
                );
                _skelBoneName = name;
                _skelNote =
                    curves > 0
                        ? $"Renamed {old} to {name}, and {curves} animation curve(s) of this file with it."
                        : $"Renamed {old} to {name}.";
            }
            catch (Exception ex)
            {
                _skelError = "The rename failed: " + ex.Message;
                Console.WriteLine($"[Skeleton] {ex}");
            }
            _skelEditFor = default;
        }

        /// <summary>
        /// The selected bone's gizmo while Edit transform is on: its rest transform in its
        /// parent's space, placed through the parent as posed. It commits when a drag lets go.
        /// </summary>
        void DrawBoneGizmo(Vector2 pos, Vector2 size, Vector2 uv0, Vector2 uv1, bool hovered)
        {
            var g = _boneGizmo;
            var views = _standalone != null ? SkeletonViews() : null;
            var assets = _standalone?.Render.Models.OfType<BfresEditor.BfresModelAsset>().ToList();
            BoneView bone =
                views != null && _skelModel < views.Count ? SelectedBone(views[_skelModel]) : null;
            if (
                bone == null
                || !_skelGizmo
                || !_skeletonTabActive
                || _physicsTabActive
                || _animExporting
                || _paint != null
                || GeneratedBone(bone.Name)
                || _skelModel >= assets.Count
            )
            {
                g.Shown = false;
                g.Axis = -1;
                return;
            }
            var posed = assets[_skelModel].ModelData.Skeleton.Bones;
            var frame = _standalone.Render.Transform.TransformMatrix;
            if (bone.Parent >= 0 && bone.Parent < posed.Count)
                frame = posed[bone.Parent].Transform * frame;
            var map = ViewMap.Of(_pipeline.Camera, pos, size, uv0, uv1);
            var stored = bone.Srt;
            var value = new GizmoSrt(stored.Translation, Degrees(stored.Rotation), stored.Scale);
            bool done = DrawTransformGizmo(g, map, frame, value, hovered, out var live);
            if (g.Dragging || done)
            {
                _skelT = live.Translation;
                _skelR = live.Rotation;
                _skelS = live.Scale;
            }
            if (!done)
                return;
            var srt = new BoneSrt(
                live.Translation,
                StoredRotation(stored.Rotation, live.Rotation),
                live.Scale
            );
            int index = _skelBone;
            int modelIndex = _skelModel;
            string what =
                g.Mode == GizmoMode.Move ? "translation"
                : g.Mode == GizmoMode.Rotate ? "rotation"
                : "scale";
            Defer(
                Deferred.Skeleton,
                () => SetSkeletonBoneTransform(modelIndex, index, srt, $"{what} of {bone.Name}"),
                replace: true
            );
        }

        /// <summary>
        /// Every joint of the chosen model, the selected one ringed and named; a click without a
        /// drag selects the joint under the cursor.
        /// </summary>
        void DrawSkeletonViewport(Vector2 pos, Vector2 size, Vector2 uv0, Vector2 uv1, bool hovered)
        {
            if (
                _standalone == null
                || !_skeletonTabActive
                || _physicsTabActive
                || _animExporting
                || !_skelJoints
            )
                return;
            var assets = _standalone.Render.Models.OfType<BfresEditor.BfresModelAsset>().ToList();
            if (_skelModel >= assets.Count)
                return;
            var bones = assets[_skelModel].ModelData.Skeleton.Bones;
            var map = ViewMap.Of(_pipeline.Camera, pos, size, uv0, uv1);
            var screen = ProjectJoints(map, _standalone.Render.Transform.TransformMatrix, bones);
            var dl = ImGui.GetWindowDrawList();
            dl.PushClipRect(pos, pos + size);
            DrawJointLinks(
                dl,
                screen,
                bones.Select(b => b.ParentIndex).ToList(),
                Color(1, 1, 1, 0.25f),
                i => i == _skelBone || bones[i].ParentIndex == _skelBone
            );
            int near = DrawJointDots(dl, screen, i => i == _skelBone, ColSelect, hovered);
            TestHookNote("skeleton joints", screen);
            foreach (int i in new[] { _skelBone, near }.Distinct())
                if (i >= 0 && i < bones.Count && screen[i] is Vector2 h)
                    DrawJointName(dl, h, bones[i].Name);
            dl.PopClipRect();
            _skelHoverJoint = near;

            if (_skelClick.Update(hovered && !_boneGizmo.Busy) && near >= 0)
                SelectSkeletonBone(near, true);
        }
    }
}
