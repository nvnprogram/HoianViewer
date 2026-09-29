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
            Widgets.BeginList("##models", new Vector2(0, 0));
            for (int mi = 0; mi < models.Count; mi++)
            {
                var model = models[mi];
                Widgets.RowFill(false);
                bool visible = model.IsVisible;
                if (Widgets.CheckboxControl($"##{mi}_vis", ref visible))
                    model.IsVisible = visible;
                ImGui.SameLine();
                if (ImGui.TreeNode($"{model.ModelData.Name}##{mi}"))
                {
                    foreach (var mesh in model.Meshes)
                    {
                        Widgets.RowFill(false);
                        bool meshVis = mesh.Shape.IsVisible;
                        if (Widgets.CheckboxControl($"{mesh.Name}##{mi}_{mesh.Name}", ref meshVis))
                            mesh.Shape.IsVisible = meshVis;
                    }
                    ImGui.TreePop();
                }
            }
            Widgets.EndList();
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
