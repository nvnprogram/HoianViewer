using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ImGuiNET;
using OpenTK.Input;
using PlayerViewer.Phive;
using PlayerViewer.Physics;
using Vector2 = System.Numerics.Vector2;

namespace PlayerViewer.UI
{
    // The Physics tab: the cloth of the standalone model, simulated on its skeleton, with the
    // element tree, the simulation controls and the save. The inspector, the viewport overlay and
    // the authoring dialogs are in the files beside this one.
    public partial class ViewerWindow
    {
        enum ClothSelKind
        {
            None,
            File,
            Piece,
            Particles,
            Particle,
            Set,
            Collidable,
        }

        ClothDocument _cloth;
        readonly ClothRuntime _clothRuntime = new();
        readonly ClothMotion _clothMotion = new();
        ClothPacks _clothPacks;
        Core.Romfs _clothPacksRomfs;
        bool _clothSearched;
        string _clothError;
        string _clothNote;
        bool _physicsTabActive;

        ClothSelKind _clothSel;
        int _clothSelPiece = -1;
        int _clothSelIndex = -1;

        bool _clothShowParticles = true;
        bool _clothShowLinks = true;
        bool _clothShowShapes = true;
        bool _clothShowRanges;
        bool _clothOnlySelected;

        List<string> _clothSaveReport;
        bool _clothSaveFailed;
        string _clothSaveActors = "";
        string _clothLastRoot;

        ClothPacks Packs
        {
            get
            {
                if (_clothPacks == null || _clothPacksRomfs != _romfs)
                {
                    _clothPacks = _romfs != null ? new ClothPacks(_romfs) : null;
                    _clothPacksRomfs = _romfs;
                }
                return _clothPacks;
            }
        }

        /// <summary>The standalone model's actor: its file name without the bfres extensions.</summary>
        string StandaloneActor => _standalone?.Name;

        /// <summary>The bone names of every skeleton of the standalone model.</summary>
        HashSet<string> StandaloneBoneNames() =>
            _standalone.Skeletons().SelectMany(s => s.Bones.Select(b => b.Name)).ToHashSet();

        /// <summary>Everything the Physics tab holds goes with the model; called from the standalone teardown.</summary>
        void ResetClothEditor()
        {
            _cloth = null;
            _clothRuntime.Sync(null, Array.Empty<Toolbox.Core.STSkeleton>());
            _clothSearched = false;
            _clothError = null;
            _clothNote = null;
            _clothSaveReport = null;
            _clothSel = ClothSelKind.None;
            _clothSelPiece = _clothSelIndex = -1;
            _clothPendingDelete = null;
            _pendingNewSource = null;
            _authorPieceOpen = _authorCollidableOpen = false;
            ResetHairGen();
            ResetAuthorUndo();
            ResetProvenance();
            _clearAsked = _clearModal = false;
            CancelDeferred(Deferred.Clear);
        }

        /// <summary>
        /// Advances the standalone model with its cloth: the test motion on the root, the pose,
        /// then the cloth on top of it. Also the export path, so a capture carries the cloth.
        /// </summary>
        void UpdateStandalone(float dt)
        {
            if (_standalone == null)
                return;
            if (_paint != null)
            {
                HoldRestPose(dt);
                return;
            }
            //Paused, the motion holds its last transform rather than dropping it.
            bool motion = _cloth != null && _physicsTabActive;
            if (motion && _clothRuntime.Enabled)
                _clothMotion.Advance(dt);
            _standalone.RootMotion = motion ? _clothMotion.Current() : OpenTK.Matrix4.Identity;
            _standalone.RefreshPose = _cloth != null;
            _standalone.Update(dt);
            if (_cloth == null || (_clothFaulted && _cloth.Version == _clothFaultVersion))
                return;
            _clothFaulted = false;
            try
            {
                //A hair on its own has no body, so its chest bone is placed as the player's.
                if (_scene?.Human != null && AuthorHair)
                    HairSpine.Place(_scene.Human.Skeleton, _standalone.Skeletons());
                _clothRuntime.Sync(_cloth, _standalone.Skeletons());
                _clothRuntime.Update(dt);
            }
            catch (Exception ex)
            {
                _clothError = "Simulation stopped: " + ex.Message;
                _clothFaulted = true;
                _clothFaultVersion = _cloth.Version;
                _clothRuntime.Invalidate();
                Console.WriteLine($"[Cloth] {ex}");
            }
        }

        //Set when a step threw; the next edit or a restart runs it again.
        bool _clothFaulted;
        int _clothFaultVersion;

        /// <summary>
        /// The tab holds the engine side: the simulation switch, the data of the open cloth file
        /// and the save. Authoring (limbs, display, test motion) is in its own windows.
        /// </summary>
        void DrawPhysicsTab()
        {
            _physicsTabActive = true;
            if (!_clothSearched)
                FindStandaloneCloth();
            _pipeline.BoneTints =
                _gen == null ? null
                : _paint?.Bones == true ? PickTints()
                : _genTint && _paint == null ? GenTints()
                : null;

            //While a limb is painted the cloth is held and the model has lost that limb's bones.
            if (_paint != null)
            {
                ImGui.PushTextWrapPos();
                Widgets.ColoredText(
                    Theme.GoldBright,
                    (_paint.Bones ? "Picking bones for " : "Painting ") + _paint.Limb.Name
                );
                Widgets.DimText(
                    _paint.Bones
                        ? "The cloth is held while you pick. The bone list, Done and Cancel are in the Physics Maker window."
                        : "The cloth is held while you paint. The brush, Done and Cancel are in the Physics Maker window."
                );
                ImGui.PopTextWrapPos();
                return;
            }

            DrawPhysicsTopRow();
            Widgets.SectionHeader("Cloth data");
            DrawClothSourceSection();
            if (_cloth == null)
                return;

            DrawClothUndoRow();
            float tree = Math.Max(
                VisibleHeightBelowCursor() - _clothSaveHeight - ImGui.GetStyle().ItemSpacing.Y - 4,
                160
            );
            Widgets.BeginList("##clothtree", new Vector2(0, tree));
            DrawClothTree();
            Widgets.EndList();

            float saveTop = ImGui.GetCursorPosY();
            DrawClothSaveSection();
            _clothSaveHeight = ImGui.GetCursorPosY() - saveTop;
        }

        //Last frame's height of the save section, which the data tree leaves room for.
        float _clothSaveHeight = 120;

        /// <summary>Simulate, Restart and what runs, then the button that opens the authoring window.</summary>
        void DrawPhysicsTopRow()
        {
            if (_cloth != null)
            {
                bool enabled = _clothRuntime.Enabled;
                if (Widgets.CheckboxControl("Simulate", ref enabled))
                    _clothRuntime.Enabled = enabled;
                Widgets.ItemTooltip(
                    "Pauses the cloth and test motion so you can inspect it. F toggles."
                );
                ImGui.SameLine();
                if (Widgets.Button("Restart"))
                    RestartCloth();
                Widgets.ItemTooltip("Restarts the test motion and the cloth.");
                //The Side Order card is too narrow for the status beside the switch.
                if (!SideOrderControls.On)
                    ImGui.SameLine();
                int running = _clothRuntime.Pieces.Count(p => p.Sim != null);
                int problems = _clothRuntime.Pieces.Count(p => p.Problem != null);
                ImGui.AlignTextToFramePadding();
                if (problems > 0)
                    Widgets.ColoredText(Theme.Error, $"{running} running, {problems} not");
                else
                    Widgets.DimText($"{running} piece(s) running");
            }
            if (
                Widgets.Button(
                    _authoringOpen ? "Physics Maker (open)" : "Physics Maker...",
                    new Vector2(-1, 0),
                    accent: !_authoringOpen
                )
            )
                OpenPhysicsMaker();
            DrawCopyFromPartnerButton();
        }

        void RestartCloth()
        {
            _clothFaulted = false;
            _clothError = null;
            _clothMotion.Reset();
            _clothRuntime.Invalidate();
        }

        /// <summary>F pauses or resumes the simulation: the standalone model's cloth, else the player's hair.</summary>
        void HandleSimulationKey()
        {
            var io = ImGui.GetIO();
            if (
                io.KeyCtrl
                || io.KeyAlt
                || io.KeyShift
                || io.WantTextInput
                || Widgets.Typing
                || ImGui.IsAnyItemActive()
                || _animExporting
                || !ImGui.IsKeyPressed((int)Key.F, false)
            )
                return;
            if (_standalone != null)
            {
                if (_cloth != null)
                    _clothRuntime.Enabled = !_clothRuntime.Enabled;
            }
            else if (_scene != null)
            {
                _scene.HairPhysicsEnabled = !_scene.HairPhysicsEnabled;
                if (_scene.HairPhysicsEnabled)
                    _scene.ResetHairPhysics();
            }
        }

        /// <summary>Looks for the model's cloth once: the actor named after the model, through its ClothList.</summary>
        void FindStandaloneCloth()
        {
            _clothSearched = true;
            _clothError = null;
            _pendingNewSource = null;
            if (Packs == null || StandaloneActor == null)
                return;
            try
            {
                var source = Packs.FindFor(StandaloneActor);
                if (source == null)
                {
                    _clothNote =
                        $"No actor pack named {StandaloneActor}. Open a .bphcl to edit one.";
                    return;
                }
                _clothSaveActors = string.Join(", ", source.Actors);
                if (source.IsNew)
                {
                    _clothNote = $"{StandaloneActor} has no cloth. Create one to add pieces to it.";
                    _pendingNewSource = source;
                    return;
                }
                OpenClothDocument(Packs.ReadCloth(source), source);
            }
            catch (Exception ex)
            {
                _clothError = ex.Message;
                Console.WriteLine($"[Cloth] {ex}");
            }
        }

        //The model's pack when it was found with no cloth, which New cloth writes into.
        ClothSource _pendingNewSource;

        void OpenClothDocument(byte[] bytes, ClothSource source)
        {
            //The authoring history keeps the document's steps, in order with the limbs'.
            _cloth = new ClothDocument(bytes, source) { KeepHistory = false };
            _cloth.Committed += OnClothCommitted;
            _clothRuntime.Invalidate();
            _clothRuntime.Enabled = true;
            _clothRuntime.Options.KeepSegmentLength = AuthorHair;
            _clothNote = null;
            _clothError = null;
            _clothSaveReport = null;
            _clothSel = ClothSelKind.None;
            _clothSelPiece = _clothSelIndex = -1;
            //Not the generator's until a rebuild says so; the generated cloth marks it after this.
            UnmarkGeneratedCloth();
            Console.WriteLine($"[Cloth] opened {source} ({bytes.Length} B)");
        }

        void DrawClothSourceSection()
        {
            ImGui.PushTextWrapPos();
            if (_cloth != null)
            {
                Widgets.ColoredText(Theme.GoldBright, _cloth.Source.FileName);
                Widgets.DimText(
                    _cloth.Source.IsNew ? "new, not saved yet" : _cloth.Source.ToString()
                );
            }
            if (_clothNote != null)
                Widgets.DimText(_clothNote);
            if (_clothError != null)
                Widgets.ErrorText(_clothError);
            ImGui.PopTextWrapPos();

            float w = (ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X) / 2;
            if (Widgets.Button("Open .bphcl...", new Vector2(w, 0)))
                OpenClothFromDisk();
            Widgets.ItemTooltip("Edits a loose cloth file on this model's skeleton.");
            ImGui.SameLine();
            Widgets.DisabledButton(
                "New cloth",
                Packs != null,
                new Vector2(w, 0),
                () => NewClothForModel()
            );
            Widgets.ItemTooltip("Starts an empty cloth for this model.");
        }

        void OpenClothFromDisk()
        {
            string path = NativeFolderPicker.OpenFile(
                "Open Cloth",
                "Havok cloth (*.bphcl)",
                "*.bphcl;*.bphcl.zs"
            );
            if (string.IsNullOrEmpty(path))
                return;
            try
            {
                var bytes = Core.Romfs.Decompress(File.ReadAllBytes(path));
                string name = Path.GetFileName(path);
                if (name.EndsWith(".zs", StringComparison.OrdinalIgnoreCase))
                    name = name[..^3];
                var source = new ClothSource { DiskPath = path, Entry = "Phive/Cloth/" + name };
                var found = StandaloneActor != null ? Packs?.FindFor(StandaloneActor) : null;
                if (found != null)
                    source.Actors.AddRange(found.Actors);
                _clothSaveActors = string.Join(", ", source.Actors);
                AuthorStep("open " + source.FileName, () => OpenClothDocument(bytes, source));
            }
            catch (Exception ex)
            {
                _clothError = "Not a cloth file: " + ex.Message;
            }
        }

        void NewClothForModel()
        {
            try
            {
                var source =
                    _pendingNewSource
                    ?? (StandaloneActor != null ? Packs.FindFor(StandaloneActor) : null)
                    ?? new ClothSource();
                source.IsNew = true;
                if (source.Entry == null || _cloth != null)
                    source.Entry = $"Phive/Cloth/{StandaloneActor}.bphcl";
                var empty = EmptyGeneratedCloth();
                _clothSaveActors = string.Join(", ", source.Actors);
                AuthorStep("new cloth", () => OpenClothDocument(empty.Snapshot(), source));
                _clothNote = "Add a collidable, then a piece from a bone chain.";
            }
            catch (Exception ex)
            {
                _clothError = ex.Message;
                Console.WriteLine($"[Cloth] {ex}");
            }
        }

        /// <summary>Undo and Redo walk the one authoring history, hand edits and limb changes alike.</summary>
        void DrawClothUndoRow()
        {
            float w = DrawUndoButtons(3);
            ImGui.SameLine();
            Widgets.DisabledButton(
                "Reset all",
                _cloth.IsModified,
                new Vector2(w, 0),
                () =>
                {
                    AuthorStep("reset the cloth data", _cloth.ResetAll);
                    ValidateClothSelection();
                }
            );
            Widgets.ItemTooltip("Back to the file as opened. Ctrl+Z undoes it.");
        }

        /// <summary>The save buttons and the packs they write, at the bottom of the tab.</summary>
        void DrawClothSaveSection()
        {
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();
            if (Widgets.Button("Save to mod romfs...", new Vector2(-1, 0)))
                SaveClothToRomfs();
            Widgets.ItemTooltip(
                "Writes the pack(s) with this cloth into a mod's romfs folder, on top of any pack already there."
            );
            if (Widgets.Button("Export .bphcl...", new Vector2(-1, 0)))
                ExportClothFile();
            ImGui.AlignTextToFramePadding();
            Widgets.DimText("Packs");
            ImGui.SameLine(Widgets.Column(64));
            ImGui.SetNextItemWidth(-1);
            Widgets.InputText("##clothactors", ref _clothSaveActors, 256);
            Widgets.ItemTooltip("Actors whose packs get this file, comma separated.");
            if (_cloth.HasUnsavedChanges)
                Widgets.ColoredText(Theme.Cyan, "Unsaved changes");

            //One status line and any warnings; the whole report is the tooltip and the log.
            if (_clothSaveReport is { Count: > 0 } report)
            {
                ImGui.PushTextWrapPos();
                if (_clothSaveFailed)
                    Widgets.ErrorText(report[^1]);
                else
                    Widgets.DimText(report[0]);
                Widgets.ItemTooltip(string.Join("\n", report));
                foreach (var line in report.Where(l => l.StartsWith("warning")))
                    Widgets.ErrorText(line);
                ImGui.PopTextWrapPos();
            }
        }

        /// <summary>Gives every piece and collidable its AAMP entry, as one undo step, and warns of each piece that does not compile.</summary>
        List<string> PrepareClothForSave()
        {
            var notes = ClothSave.AddMissingParams(_cloth.File);
            if (notes.Count > 0)
                AuthorStep("add parameters for the save", CommitCloth);
            notes.AddRange(ClothSave.CompileWarnings(_cloth.File));
            return notes;
        }

        void SaveClothToRomfs()
        {
            _clothSaveReport = null;
            var actors = _clothSaveActors
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();
            if (actors.Count == 0)
            {
                _clothSaveFailed = true;
                _clothSaveReport = new List<string>
                {
                    "No actor to write: name at least one pack.",
                };
                return;
            }
            string start =
                _clothLastRoot ?? (_config.LayeredFsPath is { Length: > 0 } l ? l : null);
            string root = NativeFolderPicker.SelectFolder(
                "Mod romfs folder (Pack/Actor goes inside)",
                start
            );
            if (string.IsNullOrEmpty(root))
                return;
            WriteClothPacks(root, actors);
        }

        /// <summary>Writes the cloth into each actor's pack under the romfs root, and the shown model beside them.</summary>
        void WriteClothPacks(string root, List<string> actors)
        {
            var report = new List<string>();
            bool generated = GenOwnsCloth;
            if (generated && !_gen.HasCloth)
            {
                _clothSaveFailed = true;
                _clothSaveReport = new List<string>
                {
                    "Every strand is rigid, so nothing was written: the hair keeps the cloth it has.",
                };
                return;
            }
            try
            {
                report.AddRange(PrepareClothForSave());
                var bytes = _cloth.Save();
                string entry = _cloth.Source.Entry ?? $"Phive/Cloth/{StandaloneActor}.bphcl";
                report.AddRange(
                    ClothSave.WritePacks(
                        Packs,
                        _romfs,
                        root,
                        actors,
                        entry,
                        bytes,
                        only: generated || _cloth.Source.ReplacesList
                    )
                );
                string model = WriteGeneratedModel(root, report);
                if (model != null)
                    report.Add($"model: {model}");
                string folder = Path.GetFileName(root.TrimEnd('\\', '/'));
                report.Insert(
                    0,
                    $"Saved {string.Join(", ", actors)} to {folder}"
                        + (model != null ? " with the model." : ".")
                );
                _cloth.MarkSaved(bytes);
                _clothLastRoot = root;
                _clothSaveFailed = false;
                Console.WriteLine("[Cloth] saved: " + string.Join(" | ", report));
            }
            catch (Exception ex)
            {
                report.Add("Save failed: " + ex.Message);
                _clothSaveFailed = true;
                Console.WriteLine($"[Cloth] save failed: {ex}");
            }
            _clothSaveReport = report;
        }

        void ExportClothFile()
        {
            string path = NativeFolderPicker.SaveFile(
                "Export Cloth",
                _cloth.Source.FileName,
                "Havok cloth (*.bphcl)",
                "*.bphcl"
            );
            if (string.IsNullOrEmpty(path))
                return;
            try
            {
                var report = PrepareClothForSave();
                var bytes = _cloth.Save();
                Core.AtomicFile.Write(path, bytes);
                report.Add($"{path}: {bytes.Length} B");
                report.Insert(0, $"Exported {Path.GetFileName(path)}.");
                _clothSaveReport = report;
                _clothSaveFailed = false;
            }
            catch (Exception ex)
            {
                _clothSaveReport = new List<string> { "Export failed: " + ex.Message };
                _clothSaveFailed = true;
            }
        }

        void SelectCloth(ClothSelKind kind, int piece, int index)
        {
            _clothSel = kind;
            _clothSelPiece = piece;
            _clothSelIndex = index;
        }

        /// <summary>After an undo or reload the selection may point past what exists; it falls back to the piece, then the file.</summary>
        void ValidateClothSelection()
        {
            var datas = _cloth?.File.Container?.ClothDatas;
            if (datas == null)
            {
                _clothSel = ClothSelKind.None;
                return;
            }
            if (_clothSel == ClothSelKind.Collidable && _clothSelPiece < 0)
            {
                if (_clothSelIndex >= _cloth.File.Container.Collidables.Count)
                    SelectCloth(ClothSelKind.File, -1, -1);
                return;
            }
            if (_clothSel is ClothSelKind.None or ClothSelKind.File)
                return;
            if (_clothSelPiece < 0 || _clothSelPiece >= datas.Count)
            {
                SelectCloth(ClothSelKind.File, -1, -1);
                return;
            }
            var sim = datas[_clothSelPiece].SimClothDatas[0];
            bool valid = _clothSel switch
            {
                ClothSelKind.Particle => _clothSelIndex < sim.ParticleCount,
                ClothSelKind.Set => _clothSelIndex < sim.ConstraintSets.Count,
                ClothSelKind.Collidable => _clothSelIndex < sim.PerInstanceCollidables.Count,
                _ => true,
            };
            if (!valid)
                SelectCloth(ClothSelKind.Piece, _clothSelPiece, -1);
        }

        void DrawClothTree()
        {
            var file = _cloth.File;
            var datas = file.Container?.ClothDatas ?? new List<ClothData>();
            ValidateClothSelection();

            if (
                Widgets.ListRow(
                    $"{_cloth.Source.FileName}##clothfile",
                    _clothSel == ClothSelKind.File
                )
            )
                SelectCloth(ClothSelKind.File, -1, -1);

            for (int i = 0; i < datas.Count; i++)
            {
                var piece = datas[i];
                var sim = piece.SimClothDatas.FirstOrDefault();
                var runtime = _clothRuntime.Pieces.FirstOrDefault(p => p.Index == i);
                ImGui.PushID(i);
                var flags = ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.OpenOnDoubleClick;
                if (_clothSel == ClothSelKind.Piece && _clothSelPiece == i)
                    flags |= ImGuiTreeNodeFlags.Selected;
                if (_clothSelPiece == i && _clothSel != ClothSelKind.Piece)
                    ImGui.SetNextItemOpen(true, ImGuiCond.Once);
                Widgets.RowFill((flags & ImGuiTreeNodeFlags.Selected) != 0);
                bool open = ImGui.TreeNodeEx($"{piece.Name}##piece", flags);
                if (ImGui.IsItemClicked() && !ImGui.IsItemToggledOpen())
                    SelectCloth(ClothSelKind.Piece, i, -1);
                if (runtime?.Problem != null)
                {
                    ImGui.SameLine();
                    Widgets.ColoredText(Theme.Error, "(!)");
                    if (ImGui.IsItemHovered())
                        Widgets.PlainTooltip(runtime.Problem);
                }
                if (open && sim != null)
                {
                    bool particlesSelected =
                        _clothSelPiece == i
                        && _clothSel is ClothSelKind.Particles or ClothSelKind.Particle;
                    if (
                        Widgets.ListRow(
                            $"Particles ({sim.ParticleCount}, {sim.FixedParticles.Count} fixed)",
                            particlesSelected
                        )
                    )
                        SelectCloth(ClothSelKind.Particles, i, -1);
                    for (int s = 0; s < sim.ConstraintSets.Count; s++)
                    {
                        var set = sim.ConstraintSets[s];
                        bool selected =
                            _clothSel == ClothSelKind.Set
                            && _clothSelPiece == i
                            && _clothSelIndex == s;
                        if (
                            Widgets.ListRow(
                                $"{set.Name ?? set.TypeName} ({set.Records.Count})##set{s}",
                                selected
                            )
                        )
                            SelectCloth(ClothSelKind.Set, i, s);
                    }
                    for (int c = 0; c < sim.PerInstanceCollidables.Count; c++)
                    {
                        var col = sim.PerInstanceCollidables[c];
                        bool selected =
                            _clothSel == ClothSelKind.Collidable
                            && _clothSelPiece == i
                            && _clothSelIndex == c;
                        if (Widgets.ListRow($"{col.ShapeKind}: {col.Name}##col{c}", selected))
                            SelectCloth(ClothSelKind.Collidable, i, c);
                    }
                    ImGui.TreePop();
                }
                ImGui.PopID();
            }

            var collidables = file.Container?.Collidables ?? new List<Collidable>();
            Widgets.RowFill(false);
            if (
                ImGui.TreeNodeEx(
                    $"All collidables ({collidables.Count})##allcols",
                    ImGuiTreeNodeFlags.DefaultOpen
                )
            )
            {
                for (int c = 0; c < collidables.Count; c++)
                {
                    bool selected =
                        _clothSel == ClothSelKind.Collidable
                        && _clothSelPiece < 0
                        && _clothSelIndex == c;
                    if (
                        Widgets.ListRow(
                            $"{collidables[c].ShapeKind}: {collidables[c].Name}##fcol{c}",
                            selected
                        )
                    )
                        SelectCloth(ClothSelKind.Collidable, -1, c);
                }
                ImGui.TreePop();
            }

            //The list's rows are a text line tall with no gap; these get a little of a pill's height back.
            ImGui.Dummy(new Vector2(0, 4));
            ImGui.Separator();
            ImGui.Dummy(new Vector2(0, 4));
            ImGui.PushStyleVar(
                ImGuiStyleVar.FramePadding,
                new Vector2(ImGui.GetStyle().FramePadding.X, 3)
            );
            ImGui.PushStyleVar(
                ImGuiStyleVar.ItemSpacing,
                new Vector2(ImGui.GetStyle().ItemSpacing.X, 5)
            );
            if (Widgets.Button("+ Piece from bones...", new Vector2(-1, 0)))
                OpenAuthorPiece();
            if (GenOwnsCloth)
                Widgets.ItemTooltip(
                    "Adds a piece by hand; the next limb change rebuilds the cloth without it."
                );
            if (Widgets.Button("+ Collidable...", new Vector2(-1, 0)))
            {
                //On the generated cloth a collidable added to the file would go with the next rebuild.
                if (GenOwnsCloth)
                    AddCollider(NewCollider(CollidableShapeKind.Capsule));
                else
                    OpenAuthorCollidable();
            }
            if (GenOwnsCloth)
                Widgets.ItemTooltip(
                    "Adds a capsule to the Physics Maker, which keeps it when the limbs rebuild the cloth."
                );
            ImGui.PopStyleVar(2);
        }
    }
}
