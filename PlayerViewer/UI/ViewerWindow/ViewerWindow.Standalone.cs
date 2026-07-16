using System;
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

            ImGui.TextColored(Theme.GoldBright, _standalone.Name);
            ImGui.PushTextWrapPos();
            Widgets.DimText(_standalone.SourcePath);
            ImGui.PopTextWrapPos();
            if (_standaloneError != null)
                Widgets.ErrorText(_standaloneError);

            ImGui.Spacing();
            if (ImGui.Button("Back to player", new Vector2(-1, 0)))
            {
                CloseStandalone();
                return;
            }
            if (ImGui.Button("Frame model", new Vector2(-1, 0)))
                _pipeline.FrameSphere(_standalone.GetBounding());

            var models = _standalone.Render.Models.OfType<BfresEditor.BfresModelAsset>().ToList();
            Widgets.SectionHeader("Models");

            float spacing = ImGui.GetStyle().ItemSpacing.Y;
            float avail = ImGui.GetContentRegionAvail().Y;
            float listH = Math.Max(avail - _measuredStandaloneTailHeight - spacing, 90);
            ImGui.BeginChild("##models", new Vector2(0, listH), true);
            for (int mi = 0; mi < models.Count; mi++)
            {
                var model = models[mi];
                bool visible = model.IsVisible;
                if (ImGui.Checkbox($"##{mi}_vis", ref visible))
                    model.IsVisible = visible;
                ImGui.SameLine();
                if (ImGui.TreeNode($"{model.ModelData.Name}##{mi}"))
                {
                    foreach (var mesh in model.Meshes)
                    {
                        bool meshVis = mesh.Shape.IsVisible;
                        if (ImGui.Checkbox($"{mesh.Name}##{mi}_{mesh.Name}", ref meshVis))
                            mesh.Shape.IsVisible = meshVis;
                    }
                    ImGui.TreePop();
                }
            }
            ImGui.EndChild();

            float tailY0 = ImGui.GetCursorPosY();
            DrawLightingSection();
            DrawTeamColorSection();
            DrawViewSection();
            _measuredStandaloneTailHeight = ImGui.GetCursorPosY() - tailY0;
        }
    }
}
