using System;
using System.Collections.Generic;
using System.Linq;
using BfresEditor;
using BfresLibrary;
using ImGuiNET;
using PlayerViewer.Materials;
using PlayerViewer.Textures;
using Vector2 = System.Numerics.Vector2;

namespace PlayerViewer.UI
{
    // The material list, the editor window and what its tabs share; each tab is in the file
    // beside this one. Edits go to the BfresLibrary Material and are mirrored back into the
    // FMAT wrapper.
    public partial class ViewerWindow
    {
        FMAT _selectedMaterial;
        string _materialSearch = "";
        string _textureSearch = "";
        string _selectedTexture;

        static readonly string[] SelectionModeLabels =
        {
            "None",
            "Outline selected",
            "Isolate selected",
        };

        void DrawMaterialsTab(List<BfresModelAsset> models)
        {
            DrawSplicerToggle();
            DrawMigrationNote();

            ImGui.AlignTextToFramePadding();
            Widgets.DimText("Selection");
            ImGui.SameLine(Widgets.Column(72));
            int mode = (int)_pipeline.MaterialViewMode;
            ImGui.SetNextItemWidth(-1);
            if (
                Widgets.Combo(
                    "##selmode",
                    ref mode,
                    SelectionModeLabels,
                    SelectionModeLabels.Length
                )
            )
                _pipeline.MaterialViewMode = (ScenePipeline.MaterialView)mode;
            Widgets.ItemTooltip("How the selected material shows in the viewport");

            EnsureUberContext();

            FilterRow("##matsearch", ref _materialSearch);

            Widgets.BeginList("##matlist", new Vector2(0, 0));
            //Collected as they are drawn rather than up front, so a collapsed model's
            //materials are not rows the arrows can land on.
            var rows = new List<FMAT>();
            for (int mi = 0; mi < models.Count; mi++)
            {
                var materials = models[mi]
                    .ResModel.Materials.OfType<FMAT>()
                    .Where(x => Widgets.Matches(x.Name, _materialSearch))
                    .ToList();
                if (materials.Count == 0)
                    continue;

                if (models.Count > 1)
                {
                    Widgets.RowFill(false);
                    if (
                        !ImGui.TreeNodeEx(
                            $"{models[mi].ModelData.Name}##m{mi}",
                            ImGuiTreeNodeFlags.DefaultOpen
                        )
                    )
                        continue;
                }

                foreach (var material in materials)
                {
                    rows.Add(material);
                    DrawMaterialRow(mi, material);
                }

                if (models.Count > 1)
                    ImGui.TreePop();
            }

            int move = Widgets.ListNav(MaterialListId, rows.Count, rows.IndexOf(_selectedMaterial));
            if (move >= 0)
                SelectMaterial(rows[move]);
            Widgets.EndList();
        }

        const string MaterialListId = "matlist";

        /// <summary>
        /// The specialiser switch, at the top of the Materials tab.
        /// </summary>
        void DrawSplicerToggle()
        {
            bool use = _config.UseSplicer;
            if (Widgets.CheckboxControl("Use splicer", ref use))
                SetSplicer(use);
            Widgets.ItemTooltip(
                "Generates the shader variations the game does not ship, by splicing the "
                    + "ubershader's bytecode, and packs them into the model on save.\n\nUnsupported materials "
                    + "are automatically normalized to conform and render(i.e., a Splatoon 2 material).\n\n"
                    + "A generated variation has ~1.1x instructions of an equivalent shipping variation, "
                    + "which is negligible next to the fallback ubershader(which is 25x more instructions)."
            );

            if (_config.UseSplicer && _uber != null && _uber.SpecialiserPath == null)
            {
                ImGui.PushTextWrapPos();
                Widgets.ErrorText(
                    "No uberslicer executable beside the viewer, so nothing can be generated. Which "
                        + "programs a material already has is still reported exactly."
                );
                ImGui.PopTextWrapPos();
            }
            ImGui.Separator();
        }

        /// <summary>
        /// The per-material editor, in its own window beside the left panel. It is far taller
        /// than the panel can give it, and sharing that space made both halves unusable.
        /// </summary>
        void DrawMaterialEditorWindow()
        {
            if (_standalone == null || _selectedMaterial == null)
                return;

            var viewport = ImGui.GetMainViewport();
            ImGui.SetNextWindowPos(
                new Vector2(viewport.Pos.X + LeftPanelWidth + 16, viewport.Pos.Y + 64),
                ImGuiCond.FirstUseEver
            );
            ImGui.SetNextWindowSize(new Vector2(520, 640), ImGuiCond.FirstUseEver);
            ImGui.SetNextWindowSizeConstraints(new Vector2(320, 200), new Vector2(900, 4000));

            bool open = true;
            if (
                BeginCardWindow(
                    $"{_selectedMaterial.Name}###mateditor",
                    ref open,
                    ImGuiWindowFlags.NoFocusOnAppearing
                )
            )
                DrawMaterialEditor(_selectedMaterial);
            ImGui.End();

            if (!open)
                _selectedMaterial = null;
        }

        void DrawMaterialRow(int modelIndex, FMAT material)
        {
            Widgets.RowFill(material == _selectedMaterial);
            bool visible = material.IsVisible;
            if (Widgets.CheckboxControl($"##vis{modelIndex}_{material.Name}", ref visible))
                material.IsVisible = visible;
            ImGui.SameLine();

            if (!visible)
                ImGui.PushStyleColor(ImGuiCol.Text, Theme.TextDim);
            if (
                ImGui.Selectable(
                    $"{material.Name}##sel{modelIndex}_{material.Name}",
                    material == _selectedMaterial
                )
            )
                SelectMaterial(material);
            Widgets.KeepRowVisible(MaterialListId, material == _selectedMaterial);
            if (!visible)
                ImGui.PopStyleColor();
        }

        void SelectMaterial(FMAT material)
        {
            _selectedMaterial = material;
            _optionSearch = "";
            _paramSearch = "";
            _samplerSearch = "";
            _renderInfoSearch = "";
            ClearImport();
            _transferError = null;
            _transferNote = null;
        }

        void DrawMaterialEditor(FMAT material)
        {
            Widgets.ColoredText(Theme.GoldBright, material.Name);
            ImGui.PushTextWrapPos();
            Widgets.DimText($"{material.ShaderArchive} / {material.ShaderModel}");

            var shaderModel = material.GetShaderModel();
            if (shaderModel == null)
                Widgets.ErrorText("No shader archive resolved for this material.");
            Widgets.DimText("Edits are in memory. Re-open the file to discard them.");
            ImGui.PopTextWrapPos();

            //Before the tab bar, so the compile queue and the live preview run whichever tab
            //is open rather than only while Stages is. The sampler a map option needs is
            //created here for the same reason: the option is switched on from the Options tab.
            EnsureVariations(material);
            Bindings(material).AutoBindMapSamplers();
            DrawCompileStatus();
            DrawMaterialTransfer(material);

            //Tabs rather than stacked headers: the stage list alone is 15 rows, and anything
            //under it was reachable only by scrolling past the whole thing.
            if (!Widgets.BeginTabBar("##matedit"))
                return;

            if (Widgets.BeginTabItem("Stages"))
            {
                DrawPipelineStages(material);
                ImGui.EndTabItem();
            }
            if (Widgets.BeginTabItem("Options"))
            {
                DrawShaderOptions(material, shaderModel);
                ImGui.EndTabItem();
            }
            if (Widgets.BeginTabItem("Params"))
            {
                DrawShaderParams(material);
                ImGui.EndTabItem();
            }
            if (Widgets.BeginTabItem("Samplers"))
            {
                DrawSamplers(material, shaderModel);
                ImGui.EndTabItem();
            }
            if (Widgets.BeginTabItem("Render"))
            {
                DrawRenderInfo(material);
                ImGui.EndTabItem();
            }
            if (Widgets.BeginTabItem("User"))
            {
                DrawUserData(material);
                ImGui.EndTabItem();
            }
            Widgets.EndTabBar();
        }

        /// <summary>
        /// A combo over a known choice list that keeps a value outside the list rather than
        /// coercing it, since the measured lists are not all closed sets. When defaultLabel is
        /// given it becomes entry 0 and reports back as null. A zero width leaves room for
        /// the reset button when there is one.
        /// </summary>
        static bool ChoiceCombo(
            string id,
            string current,
            IReadOnlyList<string> choices,
            string defaultLabel,
            out string picked,
            Action reset = null,
            float width = 0
        )
        {
            var list = new List<string>();
            if (defaultLabel != null)
                list.Add(null);
            list.AddRange(choices);

            int index = list.IndexOf(current);
            if (index < 0)
            {
                list.Add(current);
                index = list.Count - 1;
            }

            var labels = new string[list.Count];
            for (int i = 0; i < list.Count; i++)
                labels[i] = list[i] ?? defaultLabel;
            if (index >= (defaultLabel != null ? 1 : 0) + choices.Count)
                labels[index] = $"{current}  (not a listed value)";

            ImGui.SetNextItemWidth(width != 0 ? width : ControlWidth(reset));
            int sel = index;
            picked = null;
            bool moved = Widgets.Combo(id, ref sel, labels, labels.Length) && sel != index;
            DrawResetButton(id, reset);
            if (!moved)
                return false;
            picked = list[sel];
            return true;
        }

        //--- Shared array editors. Length never changes here; nothing in this panel wants
        //a resize, and an entry's element count is part of what the archive expects.

        delegate bool ArrayInput<T>(string label, ref T value);

        //One input per element, the reset button beside the first, and a copy handed back on
        //an edit so the caller's array is never written in place.
        static void DrawArray<T>(
            string id,
            T[] values,
            ArrayInput<T> input,
            Action<T[]> set,
            Action reset
        )
        {
            if (values.Length == 0)
            {
                Widgets.DimText("(no values)");
                DrawResetButton(id, reset);
                return;
            }
            for (int i = 0; i < values.Length; i++)
            {
                T value = values[i];
                ImGui.SetNextItemWidth(i == 0 ? ControlWidth(reset) : -1);
                bool edited = input($"##{id}_{i}", ref value);
                if (i == 0)
                    DrawResetButton(id, reset);
                if (!edited)
                    continue;
                var copy = (T[])values.Clone();
                copy[i] = value;
                set(copy);
            }
        }

        static void DrawStringArray(
            string id,
            string[] values,
            Action<string[]> set,
            Action reset = null
        ) =>
            DrawArray(
                id,
                values,
                (string label, ref string value) =>
                {
                    value ??= "";
                    return ImGui.InputText(label, ref value, 256);
                },
                set,
                reset
            );

        static void DrawFloatArray(
            string id,
            float[] values,
            Action<float[]> set,
            Action reset = null
        ) =>
            DrawArray(
                id,
                values,
                (string label, ref float value) => ImGui.InputFloat(label, ref value),
                set,
                reset
            );

        static void DrawIntArray(string id, int[] values, Action<int[]> set, Action reset = null) =>
            DrawArray(
                id,
                values,
                (string label, ref int value) => ImGui.InputInt(label, ref value),
                set,
                reset
            );

        //--- Reset to what the file held

        //Width of the reset button and the gap in front of it, so a control that has one does
        //not run to the edge and push it off.
        const float ResetButtonWidth = 52;

        readonly Dictionary<FMAT, MaterialBaseline> _materialBaselines = new();

        /// <summary>
        /// What this material held before the editor touched it. Taken on first draw, which is
        /// the first moment anything in it can change, so it survives selecting another
        /// material and comes back only when the model is reloaded.
        /// </summary>
        MaterialBaseline Baseline(FMAT material)
        {
            if (!_materialBaselines.TryGetValue(material, out var baseline))
                _materialBaselines[material] = baseline = new MaterialBaseline(material.Material);
            return baseline;
        }

        static float ControlWidth(Action reset) =>
            reset == null ? -1 : -(ResetButtonWidth + ImGui.GetStyle().ItemSpacing.X);

        /// <summary>Sits to the right of the control it undoes. A null action means the entry
        /// still holds what the file did, so there is nothing to draw.</summary>
        static void DrawResetButton(string id, Action reset)
        {
            if (reset == null)
                return;
            ImGui.SameLine();
            if (Widgets.Button($"Reset##reset{id}", new Vector2(ResetButtonWidth, 0)))
                reset();
        }

        //The per entry reset, or null while the entry still holds what the file did. The
        //reload is what puts the change on screen; a param needs none.
        Action ResetAction(FMAT material, bool changed, Action reset, bool reload = true) =>
            !changed
                ? null
                : () =>
                {
                    reset();
                    if (reload)
                        MaterialEdited(material);
                };

        /// <summary>An entry's name, in cyan when it no longer holds what the file did.</summary>
        static void DrawEntryName(string text, bool changed, bool dim = false)
        {
            ImGui.PushTextWrapPos();
            Widgets.ColoredText(
                changed ? Theme.Cyan
                    : dim ? Theme.TextDim
                    : Theme.TextMain,
                text
            );
            ImGui.PopTextWrapPos();
        }

        /// <summary>
        /// The header row of a tab that can be reset: how many of its entries have moved, and
        /// one button to put them all back.
        /// </summary>
        void DrawResetAll(
            string what,
            List<string> changed,
            Action<string> reset,
            FMAT material,
            bool reload = true
        )
        {
            if (changed.Count == 0)
            {
                Widgets.DimText($"no {what} differs from the file");
                ImGui.Separator();
                return;
            }

            ImGui.AlignTextToFramePadding();
            Widgets.ColoredText(
                Theme.Cyan,
                $"{changed.Count} {what}{(changed.Count == 1 ? "" : "s")} changed"
            );
            ImGui.SameLine();
            if (Widgets.Button($"Reset all##resetall{what}"))
            {
                foreach (string name in changed)
                    reset(name);
                if (reload)
                    MaterialEdited(material);
            }
            Widgets.ItemTooltip(
                "Puts every entry on this tab back to what the file held when it was opened. "
                    + "An entry the material did not originally carry is removed again."
            );
            ImGui.PushTextWrapPos();
            Widgets.DimText(
                string.Join(", ", changed.Take(8))
                    + (changed.Count > 8 ? $" and {changed.Count - 8} more" : "")
            );
            ImGui.PopTextWrapPos();
            ImGui.Separator();
        }

        //Resyncs the FMAT wrapper (texture maps, options, render state) from the material it
        //wraps, then re-resolves the shader for every mesh drawn with it. Both halves are
        //needed: Reload only refreshes the wrapper's own dictionaries, while ReloadShader is
        //what runs the program lookup again off the new option key and recompiles, which is
        //the mesh actually changing on screen.
        void MaterialEdited(FMAT material)
        {
            try
            {
                material.Reload(material.Material);
                foreach (FSHP mesh in material.GetMappedMeshes())
                    mesh.ReloadShader();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Material] Reload failed for {material.Name}: {ex.Message}");
            }
            InvalidateVariations(material);
        }

        static void FilterRow(string id, ref string filter)
        {
            if (SideOrderControls.On)
            {
                Widgets.SearchField(id, ref filter, "Filter", -1);
                return;
            }
            ImGui.AlignTextToFramePadding();
            Widgets.ColoredText(Theme.TextDim, "Filter");
            ImGui.SameLine(52);
            ImGui.SetNextItemWidth(-1);
            Widgets.InputText(id, ref filter, 64);
        }

        /// <summary>
        /// The material editor's own state, which belongs to the loaded model rather than to
        /// the splicer.
        /// </summary>
        void ResetMaterialEditor()
        {
            //The texture store caches the render and the wrapper of the model going away.
            _textures = null;
            _samplerBindings.Clear();
            _materialBaselines.Clear();
            _reencodeSelectedFor = null;
            ClearImport();
            _transferError = null;
            _transferNote = null;
            ClearTextureStatus();
            _pendingDelete = null;
        }
    }
}
