using System;
using System.Collections.Generic;
using System.Linq;
using BfresEditor;
using ImGuiNET;
using PlayerViewer.Shaders;
using ShaderBundler;
using Vector2 = System.Numerics.Vector2;
using Vector4 = System.Numerics.Vector4;

namespace PlayerViewer.UI
{
    // Pipeline stage section of the material editor: which gsys_assign_type stages already
    // have a program for this material's exact option key and which would have to be
    // generated. The queue that generates them is SpliceQueue; this holds the wrappers the
    // other partials call and the drawing.
    public partial class ViewerWindow
    {
        SpliceQueue _queue;

        bool _bundlePruned;
        string _bundleNote;

        //Created on first use, since a field initialiser cannot name instance methods.
        SpliceQueue Queue => _queue ??= new SpliceQueue(StandaloneModels, WeightsFor);

        //The names the other partials read.
        UberContext _uber => _queue?.Uber;
        CompileScheduler _splicer => _queue?.Scheduler;
        LivePreview _preview => Queue.Preview;

        /// <summary>Starts the ubershader when the splicer is on. The switch is read here,
        /// the one door into the load, the queue and the preview.</summary>
        void EnsureUberContext()
        {
            if (!_config.UseSplicer || _romfs == null)
                return;
            Queue.EnsureContext(_romfs);
        }

        /// <summary>
        /// The switch. Off hands every material back and forgets the grids and the queue,
        /// keeping the loaded ubershader, which belongs to the romfs. Opening a model throws
        /// it off, because on it splices the whole model.
        /// </summary>
        void SetSplicer(bool on)
        {
            if (_config.UseSplicer != on)
            {
                _config.UseSplicer = on;
                _config.Save();
            }
            if (!on)
                ResetVariations();
        }

        /// <summary>Hands every material back to its shipped archive and forgets the grids
        /// and the demand. The ubershader stays.</summary>
        void ResetVariations()
        {
            _savePendingPath = null;
            _saveReport = null;
            _queue?.Reset();
        }

        /// <summary>Drops the ubershader and its scheduler as well, for a romfs change.</summary>
        void DisposeVariations()
        {
            ResetVariations();
            _queue?.DropContext();
        }

        //Called right after the widget that changed, so it reads that widget: active means
        //the user has not let go of it yet.
        void InvalidateVariations(FMAT material) =>
            Queue.Invalidate(material, ImGui.IsAnyItemActive());

        /// <summary>Starts the ubershader load if needed and returns this material's grid, or
        /// null while the archive is not ready. Runs from the moment a material is selected,
        /// since the queue and the preview are keyed off it.</summary>
        MaterialVariations EnsureVariations(FMAT material)
        {
            if (material == null || _romfs == null || _standalone == null)
                return null;
            EnsureUberContext();
            return Queue.Ready ? Queue.Get(material) : null;
        }

        MaterialVariations GetVariations(FMAT material) => Queue.Get(material);

        MaterialVariations ExistenceVariations(FMAT material) =>
            material == null || _standalone == null ? null : Queue.Existence(material);

        //Pumped once a frame after the UI has drawn, whichever panel is open.
        void PumpVariations()
        {
            if (_standalone != null)
                EnsureUberContext();
            bool enabled = _config.UseSplicer && _standalone != null;
            if (enabled && Queue.Ready)
            {
                PruneStaleBundle();
                MigrateForeignMaterials();
            }
            Queue.Pump(_selectedMaterial, _savePendingPath != null, _standalone?.Name, enabled);
            PumpSave();
        }

        /// <summary>
        /// Drops the model's own shader archive when an older specialiser built it, so the
        /// splicer generates its programs again.
        /// </summary>
        void PruneStaleBundle()
        {
            if (_bundlePruned)
                return;
            _bundlePruned = true;

            var dropped = BundleFreshness.Prune(_standalone?.Bfres, _uber?.CodeHashes);
            if (dropped.Count == 0)
                return;

            BundleFreshness.Reprobe(_standalone?.Bfres, StandaloneModels());

            Queue.Reset();
            _bundleNote =
                $"{dropped.Count} embedded archive(s) came from an older specialiser and "
                + "were dropped; the passes are being generated again.";
        }

        int SaveWorkOutstanding() => Queue.SaveWorkOutstanding();

        //--- Drawing

        void DrawPipelineStages(FMAT material)
        {
            if (!_config.UseSplicer)
            {
                DrawExistenceOnlyStages(material);
                return;
            }
            if (_romfs == null)
            {
                Widgets.DimText("No romfs, so no ubershader to specialise from.");
                return;
            }

            EnsureUberContext();
            if (_uber.State == UberState.Loading)
            {
                Widgets.DimText("Reading the ubershader out of the romfs...");
                return;
            }
            if (_uber.State == UberState.Failed)
            {
                ImGui.PushTextWrapPos();
                Widgets.ErrorText(_uber.Error);
                ImGui.PopTextWrapPos();
                return;
            }

            var v = GetVariations(material);
            if (v.Error != null)
            {
                ImGui.PushTextWrapPos();
                Widgets.ErrorText(v.Error);
                ImGui.PopTextWrapPos();
                return;
            }

            DrawVariationWarnings(v);
            DrawStageMode(v);
            DrawStageRows(v);
            DrawSpliceProgress();
        }

        /// <summary>
        /// Which stages already have a shipped program, with the splicer off. Existence is a
        /// question about the serving archive alone, so none of the ubershader, the option
        /// table or the splice cache is needed to answer it; only generating is unavailable.
        /// </summary>
        void DrawExistenceOnlyStages(FMAT material)
        {
            var v = ExistenceVariations(material);
            if (v == null)
                return;
            if (v.Error != null)
            {
                ImGui.PushTextWrapPos();
                Widgets.ErrorText(v.Error);
                ImGui.PopTextWrapPos();
                return;
            }

            ImGui.PushTextWrapPos();
            Widgets.DimText(
                "The splicer is off, so this is what the shipped archive already has. Turn it "
                    + "on at the top of the Materials tab to generate the missing ones."
            );
            ImGui.PopTextWrapPos();
            ImGui.Spacing();
            DrawStageRows(v);
        }

        void DrawVariationWarnings(MaterialVariations v)
        {
            ImGui.PushTextWrapPos();
            if (v.Unsupported.Count > 0)
            {
                Widgets.ErrorText("The ubershader has no code for this material:");
                foreach (var u in v.Unsupported)
                    Widgets.ErrorText("  " + u);
                Widgets.DimText(
                    "Those stages are not queued. A splice made from this vector would compile "
                        + "cleanly and silently be a different shader."
                );
            }
            foreach (var d in v.Dropped)
                Widgets.DimText("dropped: " + d);

            foreach (uint w in v.Weights)
                if (w > PassPolicy.MaxShippedWeight)
                    Widgets.ErrorText(
                        $"a shape asks for gsys_weight {w}; neither product archive compiles "
                            + $"past {PassPolicy.MaxShippedWeight}."
                    );
            ImGui.PopTextWrapPos();
        }

        void DrawStageMode(MaterialVariations v)
        {
            ImGui.AlignTextToFramePadding();
            Widgets.DimText("Stages");
            ImGui.SameLine(52);
            if (ImGui.RadioButton("Auto", !v.Manual))
                v.Manual = false;
            Widgets.ItemTooltip(
                "The measured pass policy decides. Its zero omission guarantee is measured on "
                    + "stock content and says nothing about a material stock never shipped, "
                    + "which is what this editor makes."
            );
            ImGui.SameLine();
            if (ImGui.RadioButton("Manual", v.Manual))
                v.Manual = true;
            Widgets.ItemTooltip("Tick the stages to compile yourself.");

            if (v.Manual)
            {
                if (ImGui.SmallButton("All 15"))
                    foreach (var pass in _uber.AssignTypes)
                        v.ManualPasses.Add(pass);
                ImGui.SameLine();
                if (ImGui.SmallButton("None"))
                    v.ManualPasses.Clear();
                ImGui.SameLine();
                if (ImGui.SmallButton("Policy"))
                    v.ResetManualToPolicy();
            }
            ImGui.Spacing();
        }

        void DrawStageRows(MaterialVariations v)
        {
            //The rows are checkbox, name, status. Measure the first two rather than padding by
            //eye: depth_silhouette is the longest name and ran into its status at a guess of 40.
            float statusColumn =
                ImGui.GetFrameHeight()
                + ImGui.GetStyle().ItemSpacing.X * 2
                + ImGui.CalcTextSize("depth_silhouette").X
                + ImGui.GetStyle().ItemSpacing.X;

            foreach (string pass in v.AssignTypes)
            {
                ImGui.PushID(pass);
                //With no policy there is nothing to select, so every row reports plainly.
                bool selected = !v.ExistenceOnly && v.IsSelected(pass);

                //In auto mode the tick still shows what the policy chose, dimmed, and the
                //result is discarded rather than making it look editable.
                bool ticked = v.Manual ? v.ManualPasses.Contains(pass) : selected;
                if (!v.Manual)
                    ImGui.PushStyleVar(ImGuiStyleVar.Alpha, 0.5f);
                if (ImGui.Checkbox("##sel", ref ticked) && v.Manual && !v.ExistenceOnly)
                {
                    if (ticked)
                        v.ManualPasses.Add(pass);
                    else
                        v.ManualPasses.Remove(pass);
                }
                if (!v.Manual)
                    ImGui.PopStyleVar();

                ImGui.SameLine();
                ImGui.AlignTextToFramePadding();
                bool lit = v.ExistenceOnly ? v.CellsOf(pass).Any(c => c.Exists) : selected;
                ImGui.TextColored(lit ? Theme.TextMain : Theme.TextDim, DrawnPasses.Short(pass));
                Widgets.ItemTooltip(PassTooltip(v, pass, selected));

                ImGui.SameLine(statusColumn);
                DrawStageStatus(v, pass, selected);
                ImGui.PopID();
            }
        }

        void DrawStageStatus(MaterialVariations v, string pass, bool selected)
        {
            var cells = v.CellsOf(pass).ToList();
            if (cells.Count == 0)
            {
                Widgets.DimText("-");
                return;
            }

            bool perWeight = v.Weights.Length > 1;
            for (int i = 0; i < cells.Count; i++)
            {
                if (i > 0)
                    ImGui.SameLine(0, 6);
                var (text, colour) = v.ExistenceOnly
                    ? cells[i].Exists
                        ? ($"in archive ({cells[i].ExistingProgram})", Theme.Success)
                        : ("missing", Theme.Error)
                    : CellStatus(cells[i], selected);
                ImGui.TextColored(colour, perWeight ? $"w{cells[i].Weight} {text}" : text);
                if (cells[i].Failure != null)
                    Widgets.ItemTooltip(cells[i].Failure);
            }

            var failed = v.ExistenceOnly
                ? null
                : cells.FirstOrDefault(c => c.State == CellState.Failed);
            if (failed == null)
                return;
            ImGui.SameLine(0, 8);
            if (!ImGui.SmallButton("retry"))
                return;
            foreach (var c in cells.Where(x => x.State == CellState.Failed))
                Queue.Forget(c);
        }

        /// <summary>
        /// Green already exists, cyan will be generated, red is missing and nothing is going
        /// to make it. The last is the one worth noticing: the material simply has no program
        /// for that pass.
        /// </summary>
        (string, Vector4) CellStatus(VariationCell cell, bool selected)
        {
            if (cell.Exists)
                return ($"in archive ({cell.ExistingProgram})", Theme.Success);
            if (cell.Refusal != null)
                return ("cannot express", Theme.Error);

            switch (cell.State)
            {
                case CellState.Running:
                    return (cell.PreviewReady ? "quick, splicing" : "splicing", Theme.Gold);
                case CellState.Queued:
                    return (cell.PreviewReady ? "quick, queued" : "queued", Theme.TextDim);
                case CellState.Ready:
                    return ($"generated ({cell.Seconds:0.0}s)", Theme.Cyan);
                case CellState.Failed:
                    return ("failed", Theme.Error);
                default:
                    if (cell.PreviewReady)
                        return ("quick", Theme.Gold);
                    if (cell.PreviewState == CellState.Running)
                        return ("quick splicing", Theme.Gold);
                    return selected ? ("new", Theme.Cyan) : ("missing", Theme.Error);
            }
        }

        string PassTooltip(MaterialVariations v, string pass, bool selected)
        {
            var why = selected ? v.Policy?.Why : v.Policy?.WhyNot;
            string reason = why != null && why.TryGetValue(pass, out string r) ? r : null;
            return reason == null ? pass : $"{pass}\n\n{reason}";
        }

        void DrawSpliceProgress()
        {
            ImGui.Spacing();
            if (_uber.SpecialiserPath == null)
            {
                ImGui.PushTextWrapPos();
                Widgets.ErrorText(
                    "No specialiser beside the exe, so nothing can be generated. Existence is "
                        + "still exact."
                );
                ImGui.PopTextWrapPos();
                return;
            }

            if (_bundleNote != null)
            {
                ImGui.PushTextWrapPos();
                ImGui.TextColored(Theme.Gold, _bundleNote);
                ImGui.PopTextWrapPos();
            }

            var active = _splicer.Active;
            int queued = _splicer.Queued;
            if (active.Length > 0 || queued > 0)
            {
                ImGui.TextColored(Theme.Gold, $"compiling {active.Length}, {queued} queued");
                foreach (var label in active)
                    Widgets.DimText("  " + label);
            }
            else
                Widgets.DimText("idle");

            if (Queue.SplicesDone > 0 || Queue.SplicesFailed > 0)
                Widgets.DimText(
                    $"{Queue.SplicesDone} generated, {Queue.SplicesFailed} failed this session"
                );
        }

        /// <summary>
        /// One line of compile state, drawn in the editor header so it is on every tab rather
        /// than only on Stages. Empty when there is nothing happening.
        /// </summary>
        void DrawCompileStatus()
        {
            if (!_config.UseSplicer)
            {
                Widgets.DimText("splicer off, so this material draws from what shipped");
                return;
            }
            if (_splicer == null)
                return;
            if (_uber != null && _uber.State == UberState.Loading)
            {
                Widgets.DimText("reading the ubershader...");
                return;
            }

            var (running, queued, pr, pq) = _splicer.Counts();
            var parts = new List<string>();
            if (pr > 0 || pq > 0)
                parts.Add($"quick {pr + pq}");
            if (running > 0 || queued > 0)
                parts.Add($"splicing {running}, {queued} queued");
            if (_preview.Upgrading)
                parts.Add("linking");

            ImGui.PushTextWrapPos();
            if (parts.Count > 0)
                ImGui.TextColored(Theme.Gold, string.Join("  ", parts));
            else if (_preview.IsQuick(_selectedMaterial))
                ImGui.TextColored(Theme.Cyan, "drawn from a quick splice");
            else if (_preview.IsPreviewing(_selectedMaterial))
                ImGui.TextColored(Theme.Cyan, "drawn from the generated programs");
            else
                Widgets.DimText("splicer idle");
            if (_preview.Error != null)
                Widgets.ErrorText("Preview: " + _preview.Error);
            ImGui.PopTextWrapPos();
        }
    }
}
