using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BfresEditor;
using ImGuiNET;
using PlayerViewer.Materials;
using PlayerViewer.Textures;

namespace PlayerViewer.UI
{
    // Export and import of one material as a folder: material.json plus a PNG per texture it
    // binds. It is how a material moves between models, and how one gets edited outside the
    // viewer and brought back.
    public partial class ViewerWindow
    {
        string _transferError;
        string _transferNote;

        //An import waiting on the texture question. The file is already read, so the prompt
        //can say how many textures came with it before anything is applied.
        string _importFolder;
        MaterialTransfer.MaterialFile _importFile;
        List<MaterialTransfer.TextureEntry> _importTextures;

        void DrawMaterialTransfer(FMAT material)
        {
            if (Widgets.Button("Export material..."))
                ExportMaterial(material);
            Widgets.ItemTooltip(
                "Writes material.json and a PNG for every texture this material binds into a "
                    + "folder you pick."
            );
            ImGui.SameLine();
            if (Widgets.Button("Replace material..."))
                StartImportMaterial();
            Widgets.ItemTooltip(
                "Reads a folder written by Export over THIS material. Its options, render "
                    + "info, parameters, samplers and user data are all replaced; the "
                    + "material keeps its own name and its place in the model."
            );

            if (_transferError != null)
            {
                ImGui.PushTextWrapPos();
                Widgets.ErrorText(_transferError);
                ImGui.PopTextWrapPos();
            }
            else if (_transferNote != null)
            {
                ImGui.PushTextWrapPos();
                Widgets.DimText(_transferNote);
                ImGui.PopTextWrapPos();
            }

            if (_importFile != null)
                DrawImportPrompt(material);
        }

        void DrawImportPrompt(FMAT material)
        {
            ImGui.Spacing();
            ImGui.PushTextWrapPos();
            Widgets.ColoredText(Theme.Gold, $"Import '{_importFile.Name}' over {material.Name}?");
            if (_importTextures.Count > 0)
                Widgets.DimText(
                    $"{_importTextures.Count} texture(s) sit beside it. Bringing them in adds "
                        + "them to this model in the format each was exported from, replacing "
                        + "anything that already has the name."
                );
            else
                Widgets.DimText(
                    "No texture files beside it, so the sampler names are taken as they are."
                );
            ImGui.PopTextWrapPos();

            if (_importTextures.Count > 0)
            {
                if (Widgets.Button("Import with textures"))
                    FinishImportMaterial(material, true);
                ImGui.SameLine();
                if (Widgets.Button("Leave textures unbound"))
                    FinishImportMaterial(material, false);
            }
            else if (Widgets.Button("Import"))
                FinishImportMaterial(material, false);

            ImGui.SameLine();
            if (Widgets.Button("Cancel##import"))
                ClearImport();
        }

        void ExportMaterial(FMAT material)
        {
            _transferError = null;
            _transferNote = null;
            string folder = NativeFolderPicker.SelectFolder(
                $"Export {material.Name} into a folder"
            );
            if (string.IsNullOrEmpty(folder))
                return;

            try
            {
                var problems = new List<string>();
                var store = Textures;
                int textures = MaterialTransfer.Export(
                    material.Material,
                    store == null ? null : store.Find,
                    folder,
                    problems
                );
                _transferNote =
                    $"wrote material.json and {textures} texture(s) to "
                    + Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar));
                if (problems.Count > 0)
                    _transferError = string.Join("; ", problems);
                Console.WriteLine(
                    $"[Material] Exported {material.Name} to {folder} ({textures} texture(s))"
                );
            }
            catch (Exception ex)
            {
                _transferError = ex.Message;
                Console.WriteLine($"[Material] Export of {material.Name} failed: {ex}");
            }
        }

        void StartImportMaterial()
        {
            _transferError = null;
            _transferNote = null;
            ClearImport();

            string folder = NativeFolderPicker.SelectFolder("Pick an exported material folder");
            if (string.IsNullOrEmpty(folder))
                return;
            if (!File.Exists(Path.Combine(folder, MaterialTransfer.FileName)))
            {
                _transferError = $"no {MaterialTransfer.FileName} in that folder";
                return;
            }

            try
            {
                _importFile = MaterialTransfer.Read(folder);
                _importFolder = folder;
                _importTextures = MaterialTransfer.AvailableTextures(_importFile, folder);
            }
            catch (Exception ex)
            {
                _transferError = ex.Message;
                ClearImport();
            }
        }

        void FinishImportMaterial(FMAT material, bool withTextures)
        {
            var file = _importFile;
            string folder = _importFolder;
            var entries = _importTextures;
            ClearImport();

            var problems = new List<string>();
            try
            {
                if (withTextures)
                    ImportTextures(folder, entries, problems);

                MaterialTransfer.Apply(material.Material, file, problems);
                MaterialEdited(material);
                _samplerBindings.Remove(material);

                _transferNote = $"imported '{file.Name}' onto {material.Name}";
                if (problems.Count > 0)
                    _transferError = string.Join("; ", problems);
                Console.WriteLine(
                    $"[Material] Imported {file.Name} onto {material.Name} from {folder}"
                        + (problems.Count > 0 ? $" ({problems.Count} problem(s))" : "")
                );
            }
            catch (Exception ex)
            {
                _transferError = ex.Message;
                Console.WriteLine($"[Material] Import onto {material.Name} failed: {ex}");
            }
        }

        void ImportTextures(
            string folder,
            List<MaterialTransfer.TextureEntry> entries,
            List<string> problems
        )
        {
            var store = Textures;
            var bntx = store?.Bntx;
            if (bntx == null)
            {
                problems.Add("this model has no texture container, so no texture was added");
                return;
            }

            bool replaced = false;
            foreach (var entry in entries)
            {
                try
                {
                    var format =
                        TextureFormats.FromTexName(entry.SourceFormat)
                        ?? TextureFormats.Find(
                            entry.Srgb
                                ? Syroot.NintenTools.NSW.Bntx.GFX.SurfaceFormat.BC1_SRGB
                                : Syroot.NintenTools.NSW.Bntx.GFX.SurfaceFormat.BC1_UNORM
                        );
                    replaced |= store.Has(entry.Name);
                    InstallImage(Path.Combine(folder, entry.File), entry.Name, format, null);
                }
                catch (Exception ex)
                {
                    problems.Add($"{entry.Name}: {ex.Message}");
                }
            }
            //A texture read over one that other materials bind has to reach them too.
            if (replaced)
                ReloadMaterialsAfterTextureChange();
        }

        void ClearImport()
        {
            _importFile = null;
            _importFolder = null;
            _importTextures = null;
        }
    }
}
