using System;
using System.Collections.Generic;
using System.Linq;
using ImGuiNET;
using Vector2 = System.Numerics.Vector2;
using Vector4 = System.Numerics.Vector4;

namespace PlayerViewer.UI
{
    // Left-hand panel shown when viewing a loose (dropped/browsed) BFRES model.
    public partial class ViewerWindow
    {
        void DrawStandalonePanel()
        {
            Widgets.SectionHeader("Standalone Model");

            Widgets.ColoredText(Theme.GoldBright, _standalone.Name);
            ImGui.PushTextWrapPos();
            Widgets.DimText(_standalone.SourcePath);
            ImGui.PopTextWrapPos();
            if (_standaloneError != null)
                Widgets.ErrorText(_standaloneError);
            DrawConvertNotice();

            ImGui.Spacing();
            bool back = false;
            Widgets.DisabledButton(
                "Back to player",
                !ExportBusy,
                new Vector2(-1, 0),
                () => back = true
            );
            if (back)
            {
                CloseStandalone();
                return;
            }
            if (Widgets.Button("Frame model", new Vector2(-1, 0)))
                _pipeline.FrameSphere(_standalone.GetBounding());
            DrawSaveSection();

            var models = _standalone.Render.Models.OfType<BfresEditor.BfresModelAsset>().ToList();

            float spacing = ImGui.GetStyle().ItemSpacing.Y;
            float avail = VisibleHeightBelowCursor();
            //A tab asks for more than the lighting and view tail leaves, and the panel scrolls
            //instead. The request is last frame's, as the tab is known only once drawn.
            float fits = avail - _measuredStandaloneTailHeight - spacing;
            float bodyHeight = _standaloneBodyFixed ? 1 : Math.Max(fits, _standaloneBodyMin);
            //Once the panel itself scrolls, the body grows to its content and leaves the
            //scrolling to the panel, so there are never two bars side by side. A tab of fixed
            //height is always just its content.
            var bodyFlags = ImGuiWindowFlags.None;
            if (_standaloneBodyFixed || fits < _standaloneBodyMin)
            {
                bodyHeight = Math.Max(bodyHeight, _standaloneBodyContent);
                bodyFlags = ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;
            }
            float requestedMin = 160;
            bool fixedHeight = false;
            float bodyPad = 0;

            if (SideOrderControls.On)
            {
                //Padding for the tab well to reach into, the child widened so the well lines up
                //with the buttons above.
                float pad = Widgets.TabWellPad;
                bodyPad = pad * 2;
                ImGui.SetCursorPosX(ImGui.GetCursorPosX() - pad);
                ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(bodyPad));
                //No fill of its own: it would show as a frame round the well.
                ImGui.PushStyleColor(ImGuiCol.ChildBg, Vector4.Zero);
                int hidden = Widgets.HideGrab();
                ImGui.BeginChild(
                    "##standalonebody",
                    new Vector2(ImGui.GetContentRegionAvail().X + pad, bodyHeight),
                    false,
                    ImGuiWindowFlags.AlwaysUseWindowPadding | bodyFlags
                );
                ImGui.PopStyleColor(hidden);
                ImGui.PopStyleColor();
                ImGui.PopStyleVar();
            }
            else
                ImGui.BeginChild("##standalonebody", new Vector2(0, bodyHeight), false, bodyFlags);
            if ((bodyFlags & ImGuiWindowFlags.NoScrollbar) != 0)
                ImGui.SetScrollY(0);
            Widgets.DecorateScrollbar();
            _physicsTabActive = false;
            _skeletonTabActive = false;
            if (_selectPhysicsTab)
                _standaloneTab = StandaloneTab.Physics;
            _selectPhysicsTab = false;
            _standaloneTab = (StandaloneTab)
                Widgets.BeginSectionTabs(
                    "##standalonetabs",
                    StandaloneTabNames,
                    StandaloneTabRows,
                    (int)_standaloneTab
                );
            switch (_standaloneTab)
            {
                case StandaloneTab.Models:
                    DrawModelsTab(models);
                    break;
                case StandaloneTab.Materials:
                    requestedMin = 380;
                    DrawMaterialsTab(models);
                    break;
                case StandaloneTab.Textures:
                    requestedMin = 380;
                    DrawTexturesTab();
                    break;
                case StandaloneTab.Skeleton:
                    requestedMin = 640;
                    fixedHeight = true;
                    DrawSkeletonTab();
                    break;
                case StandaloneTab.Physics:
                    requestedMin = 640;
                    DrawPhysicsTab();
                    fixedHeight = _cloth == null;
                    break;
            }
            Widgets.EndSectionTabs();
            _standaloneBodyContent = MathF.Ceiling(
                ImGui.GetCursorPosY() - ImGui.GetStyle().ItemSpacing.Y + bodyPad
            );
            ImGui.EndChild();
            if (!_physicsTabActive && !_skeletonTabActive)
                _pipeline.BoneTints = null;
            _standaloneBodyMin = requestedMin;
            _standaloneBodyFixed = fixedHeight;

            float tailY0 = ImGui.GetCursorPosY();
            DrawLightingSection();
            DrawTeamColorSection();
            DrawViewSection();
            _measuredStandaloneTailHeight = ImGui.GetCursorPosY() - tailY0;
        }

        /// <summary>
        /// Room left below the cursor in the current child, measured so it does not move when
        /// the child is scrolled.
        /// </summary>
        static float VisibleHeightBelowCursor() =>
            ImGui.GetWindowHeight() - ImGui.GetStyle().WindowPadding.Y - ImGui.GetCursorPosY();

        void DrawModelsTab(List<BfresEditor.BfresModelAsset> models)
        {
            if (!ReferenceEquals(_renameScene, _standalone))
            {
                _renameScene = _standalone;
                _renameRow = NoRow;
                _renameError = null;
            }
            Widgets.BeginList("##models", new Vector2(0, 0));
            for (int mi = 0; mi < models.Count; mi++)
            {
                var model = models[mi];
                Widgets.RowFill(false);
                bool visible = model.IsVisible;
                if (Widgets.CheckboxControl($"##{mi}_vis", ref visible))
                    model.IsVisible = visible;
                var row = (mi, -1);
                float buttonX = BeginNameRow();
                bool typing = _renameRow == row;
                //The ids follow the row, not the name, so a rename keeps the node's open state.
                bool open = Widgets.ListTreeNode(
                    typing ? $"###model{mi}" : $"{model.ModelData.Name}###model{mi}",
                    sameLine: true
                );
                if (typing)
                {
                    ImGui.SameLine();
                    ImGui.SetCursorScreenPos(
                        new Vector2(Widgets.ListTreeLabelX(), ImGui.GetCursorScreenPos().Y)
                    );
                    DrawRenameField(model, null, buttonX);
                }
                EndNameRow(row, buttonX, model.ModelData.Name);
                if (open)
                {
                    for (int si = 0; si < model.Meshes.Count; si++)
                    {
                        var mesh = model.Meshes[si];
                        row = (mi, si);
                        typing = _renameRow == row;
                        Widgets.RowFill(false);
                        buttonX = BeginNameRow();
                        bool meshVis = mesh.Shape.IsVisible;
                        string label = typing ? "" : mesh.Name;
                        if (Widgets.CheckboxControl($"{label}###shape{mi}_{si}", ref meshVis))
                            mesh.Shape.IsVisible = meshVis;
                        if (typing)
                        {
                            ImGui.SameLine(0, ImGui.GetStyle().ItemInnerSpacing.X);
                            DrawRenameField(model, mesh, buttonX);
                        }
                        EndNameRow(row, buttonX, mesh.Name);
                    }
                    ImGui.TreePop();
                }
            }
            Widgets.EndList();
        }

        static readonly (int Model, int Shape) NoRow = (-1, -1);

        //The row whose name is being typed (a model index, and a shape index or -1 for the model),
        //and the scene it belongs to.
        (int Model, int Shape) _renameRow = NoRow;
        object _renameScene;
        string _renameText = "";

        //A refused rename's message, shown under its row for a few seconds.
        string _renameError;
        (int Model, int Shape) _renameErrorRow = NoRow;
        DateTime _renameErrorTime;

        //Room between a row's name and its rename button, and between the button and the edge.
        const float RenameGap = 6,
            RenameEdgePad = 4;

        /// <summary>
        /// Starts a list row that ends in a rename button: returns the button's x in window space
        /// and clips the row's name short of it. Pair with <see cref="EndNameRow"/>.
        /// </summary>
        float BeginNameRow()
        {
            float size = ImGui.GetFrameHeight();
            float x = ImGui.GetWindowContentRegionMax().X - size - RenameEdgePad;
            float clip = ImGui.GetWindowPos().X + x - RenameGap;
            ImGui.PushClipRect(new Vector2(-1e6f), new Vector2(clip, 1e6f), true);
            return x;
        }

        void EndNameRow((int Model, int Shape) row, float buttonX, string name)
        {
            ImGui.PopClipRect();
            ImGui.SameLine(buttonX);
            Widgets.BeginDisabled(_paint != null);
            if (Widgets.PencilButton($"##rename{row.Model}_{row.Shape}") && _renameRow != row)
            {
                _renameRow = row;
                _renameText = name;
                _renameError = null;
            }
            Widgets.ItemTooltip(_paint != null ? "Rename (not while painting)" : "Rename");
            Widgets.EndDisabled();
            if (
                _renameError != null
                && _renameErrorRow == row
                && (DateTime.Now - _renameErrorTime).TotalSeconds < 5
            )
            {
                ImGui.PushTextWrapPos();
                Widgets.ErrorText(_renameError);
                ImGui.PopTextWrapPos();
            }
        }

        /// <summary>The name field of the row being renamed; a shape's when <paramref name="mesh"/> is set.</summary>
        void DrawRenameField(
            BfresEditor.BfresModelAsset model,
            BfresEditor.BfresMeshAsset mesh,
            float buttonX
        )
        {
            float width =
                ImGui.GetWindowPos().X + buttonX - RenameGap - ImGui.GetCursorScreenPos().X;
            var state = Widgets.InlineField(
                $"##renamefield{_renameRow.Model}_{_renameRow.Shape}",
                ref _renameText,
                128,
                width
            );
            if (state == Widgets.FieldState.Typing)
                return;
            var row = _renameRow;
            _renameRow = NoRow;
            if (state == Widgets.FieldState.Cancel)
                return;
            string problem = RenameRow(model, mesh, _renameText);
            if (problem != null)
            {
                _renameError = problem;
                _renameErrorRow = row;
                _renameErrorTime = DateTime.Now;
            }
        }

        /// <summary>
        /// Renames a model or one of its shapes, in the scene and in the bytes a reload starts
        /// from, and in the generator when the model is the one its limbs are on. Returns why a
        /// name was refused, or null.
        /// </summary>
        string RenameRow(
            BfresEditor.BfresModelAsset model,
            BfresEditor.BfresMeshAsset mesh,
            string name
        )
        {
            var res = _standalone.Bfres.ResFile;
            string modelName = model.ModelData.Name;
            string old = mesh?.Name ?? modelName;
            if (name == old || _paint != null)
                return null;
            string problem =
                mesh == null
                    ? Core.BfresNames.ModelProblem(res, old, name)
                    : Core.BfresNames.ShapeProblem(model.ResModel.Model, old, name);
            if (problem != null)
                return problem;
            try
            {
                bool first = res.Models.Keys.FirstOrDefault() == modelName;
                if (mesh == null)
                    _standalone.RenameModel(model, name);
                else
                    _standalone.RenameShape(model, mesh, name);
                if (_genOriginal != null && first)
                {
                    _genOriginal =
                        mesh == null
                            ? Core.BfresNames.RenameModel(_genOriginal, old, name)
                            : Core.BfresNames.RenameShape(_genOriginal, modelName, old, name);
                    if (mesh != null && _gen != null)
                        foreach (var part in _gen.Mesh.Parts)
                            if (part.Name == old)
                                part.Name = name;
                }
                Console.WriteLine(
                    $"[Standalone] Renamed {(mesh == null ? "model" : "shape")} {old} to {name}"
                );
                return null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Standalone] Rename of {old} failed: {ex}");
                return "The rename failed: " + ex.Message;
            }
        }

        //Every model of the loaded scene.
        IReadOnlyList<BfresEditor.BfresModelAsset> StandaloneModels() =>
            _standalone?.Render == null
                ? Array.Empty<BfresEditor.BfresModelAsset>()
                : _standalone.Render.Models.OfType<BfresEditor.BfresModelAsset>().ToList();

        //Every material of the loaded model, in model order.
        IEnumerable<BfresEditor.FMAT> StandaloneMaterials()
        {
            foreach (var model in StandaloneModels())
            foreach (var material in model.ResModel.Materials.OfType<BfresEditor.FMAT>())
                yield return material;
        }

        //The weight is part of the key, so a material drawn by shapes with different skin
        //counts needs a variation per count.
        IEnumerable<uint> WeightsFor(BfresEditor.FMAT material)
        {
            foreach (var model in StandaloneModels())
            foreach (var mesh in model.Meshes)
                if (mesh.Shape.Material == material)
                    yield return mesh.Shape.VertexSkinCount;
        }

        //Minimum height the tab body asks for, set by whichever tab drew last frame.
        float _standaloneBodyMin = 120;

        //Whether the tab drawn last frame has a height of its own rather than filling the body.
        //A list filling the body measures as the body, so it cannot be sized to its content.
        bool _standaloneBodyFixed;

        //Last frame's height of the tab body's content, which it grows to when the panel scrolls.
        float _standaloneBodyContent;

        enum StandaloneTab
        {
            Models,
            Materials,
            Textures,
            Skeleton,
            Physics,
        }

        static readonly string[] StandaloneTabNames =
        {
            "Models",
            "Materials",
            "Textures",
            "Skeleton",
            "Physics",
        };

        //Five sections do not fit one row of the left card: three above, two below.
        static readonly int[] StandaloneTabRows = { 3, 2 };

        StandaloneTab _standaloneTab;
    }
}
