using System;
using System.Collections.Generic;
using ImGuiNET;
using PlayerViewer.Shaders;
using Vector2 = System.Numerics.Vector2;

namespace PlayerViewer.UI
{
    // Writing the edited model out. Always Save As, and the generated bfsha only goes in
    // when the edits actually produced a variation the shipped archives do not have.
    public partial class ViewerWindow
    {
        BundleSaveReport _saveReport;
        DateTime _saveReportTime;
        bool _saveProblemsOpen;

        //The path the user picked while guarded splices were still outstanding. The save runs
        //once they land: a bfsha assembled now would be missing exactly the programs the edit
        //was made for, and would say so in its own report rather than failing.
        string _savePendingPath;

        void DrawSaveSection()
        {
            Widgets.DisabledButton(
                _savePendingPath == null ? "Save model as..." : "Waiting for shaders...",
                _savePendingPath == null,
                SaveStandaloneAs
            );
            Widgets.ItemTooltip(
                "Writes the edited model with any generated shaders. Waits for splices."
            );

            if (_savePendingPath != null)
            {
                ImGui.PushTextWrapPos();
                Widgets.ColoredText(
                    Theme.Gold,
                    (
                        UberLoading()
                            ? "waiting for the ubershader before writing "
                            : $"waiting for {SaveWorkOutstanding()} shader(s) before writing "
                    ) + System.IO.Path.GetFileName(_savePendingPath)
                );
                ImGui.PopTextWrapPos();
                if (Widgets.SmallButton("Cancel save"))
                    _savePendingPath = null;
            }

            if (_saveReport == null)
                return;

            ImGui.PushTextWrapPos();
            var r = _saveReport;
            if (!r.Ok)
                Widgets.ErrorText("Save failed: " + r.Error);
            else
            {
                //Name and time; what went into the file is the tooltip.
                Widgets.SuccessText(
                    $"{System.IO.Path.GetFileName(r.Path)}, saved {_saveReportTime:HH:mm}"
                );
                Widgets.ItemTooltip(Summary(r));
                if (r.VerifyFailed > 0)
                    Widgets.ErrorText($"{r.VerifyFailed} material pass(es) do not resolve");
            }

            if (r.Problems.Count > 0)
            {
                if (Widgets.SmallButton(_saveProblemsOpen ? "Hide details" : "Details"))
                    _saveProblemsOpen = !_saveProblemsOpen;
                ImGui.SameLine();
                Widgets.ErrorText($"{r.Problems.Count} problem(s)");
                if (_saveProblemsOpen)
                    foreach (var problem in r.Problems)
                        Widgets.DimText("  " + problem);
            }
            ImGui.PopTextWrapPos();
        }

        static string Summary(BundleSaveReport r)
        {
            var parts = new List<string>
            {
                $"{r.Materials} material(s)",
                $"{r.FileBytes / 1024} KB in {r.Seconds:0.0}s",
            };
            if (r.WroteArchive)
            {
                parts.Insert(
                    1,
                    $"{r.ArchiveFile}: {r.ProgramsGenerated} generated + {r.ProgramsCopied} copied "
                        + $"+ {r.ProgramsCarried} carried = {r.ProgramsInArchive} program(s) over "
                        + $"{r.MaterialsServed} material(s), {r.ArchiveBytes / 1024} KB"
                );
                parts.Insert(2, $"{r.Verified} pass(es) verified from disk");
            }
            else if (r.ArchivesRemoved > 0)
                parts.Insert(1, "the file's own archive served nothing any more and was removed");
            else
                parts.Insert(1, "no generated archive, nothing needed one");
            return string.Join("\n", parts);
        }

        void SaveStandaloneAs()
        {
            _saveReport = null;
            if (_standalone?.Bfres == null)
                return;

            //Every material has to be examined, not only the one on screen, and the ubershader
            //is what says which of them still resolve.
            EnsureUberContext();

            string path = NativeFolderPicker.SaveFile(
                "Save Model As",
                ModelBundle.SuggestName(_standalone.SourcePath),
                "BFRES models (*.bfres;*.zs)",
                "*.bfres.zs;*.bfres"
            );
            if (string.IsNullOrEmpty(path))
                return;
            SaveStandaloneTo(path);
        }

        /// <summary>The save after the dialog: the name completed, then written once the queue allows.</summary>
        void SaveStandaloneTo(string path)
        {
            _saveReport = null;
            if (_standalone?.Bfres == null)
                return;
            EnsureUberContext();
            if (
                path.EndsWith(".zs", StringComparison.OrdinalIgnoreCase)
                && !path.EndsWith(".bfres.zs", StringComparison.OrdinalIgnoreCase)
            )
                path = path[..^3] + ".bfres.zs";
            else if (
                !path.EndsWith(".bfres", StringComparison.OrdinalIgnoreCase)
                && !path.EndsWith(".zs", StringComparison.OrdinalIgnoreCase)
            )
                path += ".bfres";

            //Nothing unguarded gets written, so a splice of a material this file will embed
            //has to finish first. The rest of the model's queue is not waited on, because it
            //is not going into the file. An ubershader still being read is waited on the same
            //way, or the save would go out before anything could be examined.
            if (UberLoading() || SaveWorkOutstanding() > 0)
            {
                _savePendingPath = path;
                return;
            }
            WriteStandalone(path);
        }

        /// <summary>Pumped once a frame; runs a save that was waiting on the queue.</summary>
        void PumpSave()
        {
            if (_savePendingPath == null)
                return;
            if (UberLoading() || SaveWorkOutstanding() > 0)
                return;
            string path = _savePendingPath;
            _savePendingPath = null;
            WriteStandalone(path);
        }

        bool UberLoading() => Queue.Loading;

        /// <summary>
        /// Whether the save can plan against the ubershader. Off, still loading, or failed
        /// all mean the model is written as edited with no generated archive, and the
        /// existence grids are what the report is drawn from.
        /// </summary>
        bool SavingWithSplicer() => _config.UseSplicer && Queue.Ready;

        void WriteStandalone(string path)
        {
            (Action Undo, string Note) meta = (null, null);
            try
            {
                meta = EmbedProvenance(_standalone.Bfres.ResFile);
            }
            catch (Exception ex)
            {
                meta.Note = "physics metadata not written: " + ex.Message;
                Console.WriteLine($"[Meta] {ex}");
            }
            _saveReportTime = DateTime.Now;
            _saveReport = ModelBundle.Save(
                _standalone.Bfres,
                path,
                SavingWithSplicer() ? _uber : null,
                SavingWithSplicer() ? GetVariations : ExistenceVariations,
                Textures
            );
            _saveProblemsOpen = _saveReport.VerifyFailed > 0 || !_saveReport.Ok;
            if (!_saveReport.Ok)
                meta.Undo?.Invoke();
            else if (meta.Note != null)
                _saveReport.Notes.Add(meta.Note);

            var r = _saveReport;
            Console.WriteLine(
                r.Ok
                    ? $"[Save] {r.Path}: {r.FileBytes} B, archive {(r.WroteArchive ? r.ArchiveBytes + " B, " + r.ProgramsInArchive + " program(s)" : "none")}, "
                        + $"{r.ProgramsGenerated} generated, {r.ProgramsCopied} copied, {r.ProgramsCarried} carried, "
                        + $"{r.ArchivesRemoved} archive(s) removed, "
                        + $"{r.Verified} verified, {r.VerifyFailed} failed, {r.Problems.Count} problem(s) in {r.Seconds:0.00}s"
                    : $"[Save] {r.Path}: {r.Error}"
            );
            foreach (var problem in r.Problems)
                Console.WriteLine("[Save]   " + problem);
            foreach (var note in r.Notes)
                Console.WriteLine("[Save]   " + note);
        }
    }
}
