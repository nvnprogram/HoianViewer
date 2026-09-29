using System;
using System.Collections.Generic;
using System.Linq;
using BfresEditor;
using BfresLibrary;
using ImGuiNET;
using PlayerViewer.Materials;
using Vector2 = System.Numerics.Vector2;

namespace PlayerViewer.UI
{
    // Render tab of the material editor.
    public partial class ViewerWindow
    {
        bool _renderInfoRaw;
        string _renderInfoSearch = "";
        string _newRenderInfo = "";

        /// <summary>
        /// Every render info a Splatoon 3 material is expected to carry, whether or not this
        /// one does.
        /// </summary>
        void DrawRenderInfo(FMAT material)
        {
            var infos = material.Material.RenderInfos;
            var baseline = Baseline(material);

            DrawResetAll(
                "render info",
                baseline.ChangedRenderInfo(material.Material).ToList(),
                name => baseline.ResetRenderInfo(material.Material, name),
                material
            );

            Widgets.CheckboxControl("Edit as raw text", ref _renderInfoRaw);
            Widgets.ItemTooltip(
                "The choice lists are the engine's own where one exists and the values stock "
                    + "content uses everywhere else, so they are a suggestion rather than a "
                    + "closed set. This types any value."
            );

            //The add control sits above the list. It is one row and the list is 53, so below
            //it the panel does not scroll far enough to reach it.
            DrawAddRenderInfo(material);
            FilterRow("##risearch", ref _renderInfoSearch);

            Widgets.BeginScrollChild("##rilist", new Vector2(0, 0));
            foreach (var entry in infos.ToArray())
                if (Widgets.Matches(entry.Key, _renderInfoSearch))
                    DrawStoredRenderInfo(material, entry.Key, entry.Value);

            foreach (var known in RenderInfoCatalog.All)
            {
                if (
                    infos.ContainsKey(known.Name) || !Widgets.Matches(known.Name, _renderInfoSearch)
                )
                    continue;
                DrawAbsentRenderInfo(material, known);
            }
            ImGui.EndChild();
        }

        void DrawStoredRenderInfo(FMAT material, string name, RenderInfo info)
        {
            var baseline = Baseline(material);
            bool changed = baseline.RenderInfoChanged(material.Material, name);
            DrawEntryName($"{name}  ({info.Type})", changed);
            var reset = ResetAction(
                material,
                changed,
                () => baseline.ResetRenderInfo(material.Material, name)
            );

            switch (info.Type)
            {
                case RenderInfoType.String:
                {
                    var values = info.GetValueStrings();
                    var known = RenderInfoCatalog.Find(name);
                    if (!_renderInfoRaw && known?.Choices != null && values.Length == 1)
                    {
                        if (
                            ChoiceCombo(
                                $"##ri{name}",
                                values[0] ?? "",
                                known.Choices,
                                null,
                                out string picked,
                                reset
                            )
                        )
                        {
                            info.SetValue(new[] { picked });
                            MaterialEdited(material);
                        }
                        return;
                    }
                    DrawStringArray(
                        $"ri{name}",
                        values,
                        v =>
                        {
                            info.SetValue(v);
                            MaterialEdited(material);
                        },
                        reset
                    );
                    return;
                }
                case RenderInfoType.Single:
                    DrawFloatArray(
                        $"ri{name}",
                        info.GetValueSingles(),
                        v =>
                        {
                            info.SetValue(v);
                            MaterialEdited(material);
                        },
                        reset
                    );
                    return;
                case RenderInfoType.Int32:
                    DrawIntArray(
                        $"ri{name}",
                        info.GetValueInt32s(),
                        v =>
                        {
                            info.SetValue(v);
                            MaterialEdited(material);
                        },
                        reset
                    );
                    return;
            }
        }

        void DrawAbsentRenderInfo(FMAT material, RenderInfoCatalog.Entry known)
        {
            var baseline = Baseline(material);
            bool changed = baseline.RenderInfoChanged(material.Material, known.Name);
            DrawEntryName($"{known.Name}  ({known.Type})", changed, dim: !changed);
            var reset = ResetAction(
                material,
                changed,
                () => baseline.ResetRenderInfo(material.Material, known.Name)
            );

            string label =
                known.AbsentMeans == null ? "<Default>" : $"<Default> ({known.AbsentMeans})";

            if (known.Type == RenderInfoType.String && known.Choices != null && !_renderInfoRaw)
            {
                if (
                    ChoiceCombo(
                        $"##ri{known.Name}",
                        null,
                        known.Choices,
                        label,
                        out string picked,
                        reset
                    )
                    && picked != null
                )
                    AddRenderInfo(material, known, picked);
                return;
            }

            ImGui.AlignTextToFramePadding();
            Widgets.DimText(label);
            ImGui.SameLine();
            if (Widgets.SmallButton($"Add##ri{known.Name}"))
                AddRenderInfo(material, known, null);
            DrawResetButton($"ri{known.Name}", reset);
        }

        void AddRenderInfo(FMAT material, RenderInfoCatalog.Entry known, string value)
        {
            var info = new RenderInfo { Name = known.Name };
            switch (known.Type)
            {
                case RenderInfoType.Single:
                    info.SetValue(new float[known.Length]);
                    break;
                case RenderInfoType.Int32:
                    info.SetValue(new int[known.Length]);
                    break;
                default:
                    var strings = new string[known.Length];
                    for (int i = 0; i < strings.Length; i++)
                        strings[i] = value ?? (known.Choices != null ? known.Choices[0] : "");
                    info.SetValue(strings);
                    break;
            }
            material.Material.RenderInfos.Add(known.Name, info);
            MaterialEdited(material);
        }

        void DrawAddRenderInfo(FMAT material)
        {
            ImGui.SetNextItemWidth(-1);
            Widgets.InputText("##rinew", ref _newRenderInfo, 64);
            string newName = _newRenderInfo.Trim();
            bool taken = newName.Length > 0 && material.Material.RenderInfos.ContainsKey(newName);
            Widgets.DisabledButton(
                "Add a string render info",
                newName.Length > 0 && !taken,
                () =>
                {
                    AddRenderInfo(material, new RenderInfoCatalog.Entry { Name = newName }, null);
                    _newRenderInfo = "";
                }
            );
            Widgets.ItemTooltip(
                "For a name the list below does not have. Everything the engine reads is "
                    + "already there."
            );
            if (taken)
                Widgets.ErrorText("that name is already on this material");
        }
    }
}
