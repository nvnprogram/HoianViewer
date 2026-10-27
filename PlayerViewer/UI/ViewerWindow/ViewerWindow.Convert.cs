using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ImGuiNET;
using PlayerViewer.Conversion;
using PlayerViewer.Core;
using PlayerViewer.Core.Formats;
using PlayerViewer.Phive;
using PlayerViewer.Player;
using PlayerViewer.Textures;
using Vector2 = System.Numerics.Vector2;

namespace PlayerViewer.UI
{
    // A model or Phive file of the other game, converted to the current one.
    public partial class ViewerWindow
    {
        string _convertNotice;
        string _convertClash;
        Romfs _otherRomfs;

        /// <summary>
        /// Opens a model of the other game converted to this one. Nothing of either game is looked
        /// up by the model's name: the converted textures and animations are the model's own.
        /// </summary>
        StandaloneScene OpenConverted(string file, byte[] raw, GameKind from)
        {
            ConvertedModel model;
            if (from == GameKind.Spiral)
            {
                var other = OtherRomfs();
                model = ModelConvert.ToThunder(raw, ModelConvert.SourceFiles(file, other));
                _convertNotice =
                    "Converted from Raiders. Use splicer in Materials to fit its shaders.";
                var missing = new List<string>();
                if (model.MissingTextures.Count > 0)
                    missing.Add($"{model.MissingTextures.Count} texture(s)");
                if (model.MissingAnimFile)
                    missing.Add("its animation file");
                if (missing.Count > 0)
                    _convertNotice +=
                        $" {string.Join(" and ", missing)} not found"
                        + (other == null ? "; no Raiders romfs is set." : ".");
            }
            else
            {
                string stem = ModelStem(file);
                var other = OtherRomfs();
                model = ModelConvert.ToSpiral(
                    raw,
                    stem,
                    stem,
                    other == null ? null : other.ReadModel,
                    SpiralServed()
                );
                _convertNotice = "Converted from Splatoon 3.";
                if (model.MissingTextures.Count > 0)
                    _convertNotice +=
                        $" {model.MissingTextures.Count} texture(s) from other models not found"
                        + (other == null ? "; no Splatoon 3 romfs is set." : ".");
                if (model.Unserved.Count > 0)
                    _convertNotice +=
                        $" {model.Unserved.Count} material(s) need Use splicer in Materials to draw.";
            }
            _convertClash = StockClash(model.Archive, model.Model);
            return StandaloneScene.FromConverted(model, file, _romfs);
        }

        //Whether Raiders ships a program for an option set, from its product archive, or null.
        Func<Dictionary<string, string>, bool> SpiralServed()
        {
            if (_romfs?.Game != GameKind.Spiral)
                return null;
            try
            {
                var bytes = _romfs.ReadProduct("Shader", "Hoian_UBER", "product.*.bfsha*");
                if (bytes == null)
                    return null;
                var product = new ShaderLibrary.BfshaFile(new MemoryStream(bytes));
                if (!product.ShaderModels.ContainsKey(OptionMap.ShadingModel))
                    return null;
                var model = product.ShaderModels[OptionMap.ShadingModel];
                return options => model.GetProgramIndex(options) != -1;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Convert] product archive: {ex.Message}");
                return null;
            }
        }

        //The other game's romfs, if one is set, for a converted model's textures and animations.
        Romfs OtherRomfs()
        {
            var game = _config.CurrentGame == GameKind.Spiral ? GameKind.Thunder : GameKind.Spiral;
            string root = _config.Of(game).RomfsPath;
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
                return null;
            if (_otherRomfs?.Root != root)
                _otherRomfs = new Romfs(
                    root,
                    _config.Of(game).LayeredFsPath,
                    _config.Of(game).UseLayeredFs,
                    null,
                    game
                );
            return _otherRomfs;
        }

        static string ModelStem(string file)
        {
            string stem = Path.GetFileName(file);
            foreach (var suffix in new[] { ".zs", ".bfres" })
                if (stem.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    stem = stem[..^suffix.Length];
            return stem;
        }

        //Note for a name the current game already uses, which a mod saved under it replaces.
        string StockClash(string archive, string model)
        {
            bool taken =
                _romfs.Game == GameKind.Spiral
                    ? _romfs.ArchiveOf(model) != null
                        || _romfs.Resolve($"Model/{archive}.{model}.bfres") != null
                    : _romfs.Resolve($"Model/{model}.bfres") != null;
            return taken
                ? $"The game has its own {model}; saving under that name replaces it."
                : null;
        }

        void DrawConvertNotice()
        {
            if (_standalone?.Converted == null || _convertNotice == null)
                return;
            ImGui.PushTextWrapPos();
            Widgets.ColoredText(Theme.Gold, _convertNotice);
            if (_convertClash != null)
                Widgets.DimText(_convertClash);
            ImGui.PopTextWrapPos();
        }

        string ConvertedSuggestName() =>
            _standalone?.Converted is { } m ? m.FileName + ".zs" : null;

        //Texture files a Raiders save could not find to copy, reported once the save is written.
        int _saveMissingTextures;

        /// <summary>The files a Raiders model needs beside it: the animation file, and the
        /// texture files by stem, which go under <c>Tex</c> of the romfs root.</summary>
        sealed class SpiralCompanions
        {
            public readonly List<(string Path, byte[] Data)> Files = new();
            public readonly Dictionary<string, byte[]> Textures = new(StringComparer.Ordinal);
            public string Root;
            public string Suffix;
        }

        /// <summary>
        /// Names a converted model, or any Raiders model, after the file it is saved as, before
        /// the save. Returns the files a Raiders model needs beside it, written once the model is.
        /// </summary>
        SpiralCompanions PrepareConvertedSave(string path)
        {
            _saveMissingTextures = 0;
            var m = _standalone?.Converted;
            var res = _standalone?.Bfres?.ResFile;
            if (res == null || (m == null && _romfs?.Game != GameKind.Spiral))
                return null;
            string stem = ModelStem(path);
            if (m?.Game == GameKind.Thunder)
            {
                res.Name = stem;
                return null;
            }

            //A Raiders model's own files come from beside it or the romfs, under the names it had
            //when opened, which an earlier save has renamed in memory.
            var edits = Textures?.External;
            Func<string, byte[]> read =
                m != null ? m.ReadFile : ModelConvert.SourceFiles(_standalone.SourcePath, _romfs);
            string oldArchive =
                m?.Archive ?? edits?.SourceArchive ?? ModelConvert.SplitName(res.Name).Archive;
            byte[] animFile =
                m != null ? m.AnimArchive
                : res.ExternalFiles.ContainsKey(ModelConvert.AttachFlagName)
                    ? read($"Model/{oldArchive}.anim.bfres")
                : null;

            var (archive, model) = ModelConvert.SplitName(stem);
            res.Name = $"{archive}.{model}";
            var companions = new SpiralCompanions
            {
                Root = ModelConvert.SpiralRootFor(path),
                Suffix = path.EndsWith(".zs", StringComparison.OrdinalIgnoreCase) ? ".zs" : "",
            };

            //Each texture under the new archive's name, so none is shared with a stock model's.
            var anims =
                animFile != null ? new BfresLibrary.ResFile(new MemoryStream(animFile)) : null;
            var missing = ModelConvert.RetableSpiral(
                res,
                anims,
                archive,
                read,
                edits,
                companions.Textures
            );
            if (anims != null)
            {
                anims.Name = $"{archive}.anim";
                var ms = new MemoryStream();
                anims.Save(ms, true);
                companions.Files.Add(
                    (
                        Path.Combine(
                            Path.GetDirectoryName(Path.GetFullPath(path)),
                            $"{archive}.anim.bfres{companions.Suffix}"
                        ),
                        ms.ToArray()
                    )
                );
            }
            _saveMissingTextures = missing.Count;
            return companions;
        }

        void FinishConvertedSave(SpiralCompanions companions)
        {
            if (companions == null || _saveReport == null || !_saveReport.Ok)
                return;
            //The save may have generated placeholder textures, listed but not yet collected.
            ModelConvert.AddEditedFiles(
                _standalone.Bfres.ResFile,
                Textures?.External,
                companions.Textures
            );
            var files = companions.Files.ToList();
            foreach (var (stem, data) in companions.Textures)
                files.Add(
                    (
                        Path.Combine(
                            companions.Root,
                            ExternalTextures.PathOf(stem) + companions.Suffix
                        ),
                        data
                    )
                );
            if (_saveMissingTextures > 0)
                _saveReport.Problems.Add(
                    $"{_saveMissingTextures} texture file(s) not found, so not written"
                );
            try
            {
                foreach (var (path, data) in files)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    AtomicFile.Write(
                        path,
                        data,
                        path.EndsWith(".zs", StringComparison.OrdinalIgnoreCase)
                    );
                }
                int textures = files.Count(f => f.Path.Contains(".bntx"));
                _saveReport.Notes.Add(
                    $"{textures} texture file(s)"
                        + (files.Count > textures ? " and the animation file" : "")
                        + " written beside it"
                );
            }
            catch (Exception ex)
            {
                _saveReport.Problems.Add("Companion files not written: " + ex.Message);
            }
        }

        //--- Raiders save: a romfs folder and the archive and model names, laid out as the game's.

        bool _spiralSaveOpen;
        string _spiralSaveRoot = "";
        string _spiralSaveArchive = "";
        string _spiralSaveModel = "";
        const string SpiralSaveModalId = "Save Raiders model##spiralsave";

        void OpenSpiralSave()
        {
            var m = _standalone?.Converted;
            var (archive, model) =
                m != null
                    ? (m.Archive, m.Model)
                    : ModelConvert.SplitName(_standalone?.Bfres?.ResFile?.Name ?? "");
            _spiralSaveArchive = archive;
            _spiralSaveModel = model;
            string layered = _config.Spiral.LayeredFsPath;
            if (string.IsNullOrEmpty(_spiralSaveRoot) && !string.IsNullOrEmpty(layered))
                _spiralSaveRoot = layered;
            _spiralSaveOpen = true;
        }

        //A name part the game splits on or a path would.
        static bool ValidNamePart(string s) =>
            !string.IsNullOrWhiteSpace(s)
            && s.IndexOf('.') < 0
            && s.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

        void DrawSpiralSaveModal()
        {
            if (!_spiralSaveOpen || _standalone == null)
            {
                _spiralSaveOpen = false;
                return;
            }
            if (!ImGui.IsPopupOpen(SpiralSaveModalId))
                ImGui.OpenPopup(SpiralSaveModalId);
            if (!Widgets.BeginCenteredModal(SpiralSaveModalId, 560))
                return;

            Widgets.Text("Romfs folder");
            float browse = ImGui.CalcTextSize("Browse...").X + ImGui.GetStyle().FramePadding.X * 2;
            ImGui.SetNextItemWidth(-browse - ImGui.GetStyle().ItemSpacing.X);
            Widgets.PathInput("##spiralroot", ref _spiralSaveRoot, 512);
            ImGui.SameLine();
            if (Widgets.Button("Browse...", new Vector2(browse, 0)))
            {
                string picked = NativeFolderPicker.SelectFolder("Romfs folder", _spiralSaveRoot);
                if (!string.IsNullOrEmpty(picked))
                    _spiralSaveRoot = picked;
            }
            Widgets.ItemTooltip("Mod romfs; Model and Tex go under it.");

            Widgets.LabeledRow(
                "Archive",
                () =>
                {
                    ImGui.SetNextItemWidth(-1);
                    Widgets.InputText("##spiralarchive", ref _spiralSaveArchive, 128);
                }
            );
            Widgets.LabeledRow(
                "Model",
                () =>
                {
                    ImGui.SetNextItemWidth(-1);
                    Widgets.InputText("##spiralmodel", ref _spiralSaveModel, 128);
                }
            );

            bool namesOk = ValidNamePart(_spiralSaveArchive) && ValidNamePart(_spiralSaveModel);
            bool rootOk = !string.IsNullOrWhiteSpace(_spiralSaveRoot);
            ImGui.PushTextWrapPos();
            if (!namesOk)
                Widgets.ErrorText("Archive and model need a name without dots.");
            else
            {
                Widgets.DimText($"Model/{_spiralSaveArchive}.{_spiralSaveModel}.bfres.zs");
                if (StockClash(_spiralSaveArchive, _spiralSaveModel) is string clash)
                    Widgets.ColoredText(Theme.Gold, clash);
            }
            ImGui.PopTextWrapPos();

            ImGui.Spacing();
            float half = (ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X) / 2;
            Widgets.DisabledButton(
                "Save",
                namesOk && rootOk,
                new Vector2(half, 0),
                () =>
                {
                    _spiralSaveOpen = false;
                    ImGui.CloseCurrentPopup();
                    SaveStandaloneTo(
                        Path.Combine(
                            _spiralSaveRoot.Trim(),
                            "Model",
                            $"{_spiralSaveArchive.Trim()}.{_spiralSaveModel.Trim()}.bfres.zs"
                        )
                    );
                }
            );
            ImGui.SameLine();
            if (Widgets.Button("Cancel", new Vector2(-1, 0)))
            {
                _spiralSaveOpen = false;
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndPopup();
        }

        //--- Phive files, converted file to file from the File menu.

        string _phiveResult;
        bool _phiveResultOpen;
        const string PhiveModalId = "Convert##phive";

        void DrawPhiveConvertMenu(bool ready)
        {
            if (!ImGui.BeginMenu("Convert Phive file", ready))
                return;
            NoteBarWindow();
            if (ImGui.MenuItem("Cloth: Splatoon 3 -> Raiders"))
                ConvertPhiveFile("bphcl", true, PhiveConvert.ClothTo2025);
            if (ImGui.MenuItem("Cloth: Raiders -> Splatoon 3"))
                ConvertPhiveFile("bphcl", false, PhiveConvert.ClothToSplatoon3);
            if (ImGui.MenuItem("Shape: Splatoon 3 -> Raiders"))
                ConvertPhiveFile("bphsh", true, PhiveConvert.ShapeTo2025);
            if (ImGui.MenuItem("Shape: Raiders -> Splatoon 3"))
                ConvertPhiveFile("bphsh", false, PhiveConvert.ShapeToSplatoon3);
            ImGui.EndMenu();
        }

        delegate byte[] PhiveConverter(byte[] data, out string error);

        void ConvertPhiveFile(string ext, bool to2025, PhiveConverter convert)
        {
            string kind = ext == "bphcl" ? "cloth" : "shape";
            string source = NativeFolderPicker.OpenFile(
                $"Open {(to2025 ? "Splatoon 3" : "Raiders")} {kind}",
                $"Phive {kind} (*.{ext};*.zs)",
                $"*.{ext};*.{ext}.zs"
            );
            if (string.IsNullOrEmpty(source))
                return;
            try
            {
                var output = convert(Romfs.Decompress(File.ReadAllBytes(source)), out string error);
                if (output == null)
                {
                    ShowPhiveResult(error ?? "Conversion failed.");
                    return;
                }
                string target = NativeFolderPicker.SaveFile(
                    $"Save {(to2025 ? "Raiders" : "Splatoon 3")} {kind}",
                    PhiveConvert.SuggestName(Path.GetFileName(source), to2025),
                    $"Phive {kind} (*.{ext};*.zs)",
                    $"*.{ext}.zs;*.{ext}"
                );
                if (string.IsNullOrEmpty(target))
                    return;
                AtomicFile.Write(
                    target,
                    output,
                    target.EndsWith(".zs", StringComparison.OrdinalIgnoreCase)
                );
                ShowPhiveResult($"Saved {Path.GetFileName(target)}.");
            }
            catch (Exception ex)
            {
                ShowPhiveResult(ex.Message);
                Console.WriteLine($"[Phive] {source}: {ex}");
            }
        }

        void ShowPhiveResult(string text)
        {
            _phiveResult = text;
            _phiveResultOpen = true;
        }

        void DrawPhiveResultModal()
        {
            if (!_phiveResultOpen)
                return;
            if (!ImGui.IsPopupOpen(PhiveModalId))
                ImGui.OpenPopup(PhiveModalId);
            if (!Widgets.BeginCenteredModal(PhiveModalId, 420))
                return;
            ImGui.PushTextWrapPos();
            Widgets.Text(_phiveResult ?? "");
            ImGui.PopTextWrapPos();
            ImGui.Spacing();
            if (Widgets.Button("OK", new Vector2(-1, 0)))
            {
                _phiveResultOpen = false;
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndPopup();
        }
    }
}
