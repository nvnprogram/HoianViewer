using System;
using System.Collections.Generic;
using BfresEditor;
using ImGuiNET;
using PlayerViewer.Materials;

namespace PlayerViewer.UI
{
    // Every material is brought into the ubershader's key space as soon as the splicer is on,
    // so a foreign shading model, an option the archive does not declare or a value it has
    // no choice for never reaches the splicer as a key it cannot look up.
    public partial class ViewerWindow
    {
        object _normalisedFor;
        readonly List<string> _migrationNotes = new();

        /// <summary>Called by the splice pump once the ubershader is ready. Runs once per model.</summary>
        void MigrateForeignMaterials()
        {
            if (_standalone == null || ReferenceEquals(_normalisedFor, _standalone))
                return;
            var target = _uber?.Model;
            if (target == null)
                return;
            _normalisedFor = _standalone;
            _migrationNotes.Clear();

            foreach (var material in StandaloneMaterials())
            {
                //A key that resolves to a shipped program is valid as it is, whatever
                //shading model it names. Stock materials never get here.
                if (material.MaterialAsset is BfshaRenderer renderer && renderer.HasValidProgram)
                    continue;

                MigrationReport report;
                try
                {
                    report = MaterialMigration.Normalise(
                        material.Material,
                        target,
                        MaterialMigration.ArchiveName
                    );
                }
                catch (Exception ex)
                {
                    _migrationNotes.Add($"{material.Name}: {ex.Message}");
                    Console.WriteLine($"[Material] normalising {material.Name} failed: {ex}");
                    continue;
                }
                if (report == null)
                    continue;
                //The normalised shape is what a reset goes back to.
                _materialBaselines.Remove(material);
                MaterialEdited(material);
                _migrationNotes.Add($"{material.Name}: {report}");
                Console.WriteLine(
                    $"[Material] {material.Name} normalised for {target.Name}: {report}"
                );
            }
        }

        void DrawMigrationNote()
        {
            if (_migrationNotes.Count == 0)
                return;
            ImGui.PushTextWrapPos();
            Widgets.ColoredText(
                Theme.Cyan,
                $"{_migrationNotes.Count} material(s) normalised for the ubershader"
            );
            ImGui.PopTextWrapPos();
            Widgets.ItemTooltip(string.Join("\n", _migrationNotes));
        }
    }
}
