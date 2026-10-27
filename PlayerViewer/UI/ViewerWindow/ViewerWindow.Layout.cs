using System;
using ImGuiNET;
using PlayerViewer.Core;
using Vector2 = System.Numerics.Vector2;

namespace PlayerViewer.UI
{
    // Top-level window layout: host window, menu bar, and the romfs-setup screen.
    public partial class ViewerWindow
    {
        const float LeftPanelWidth = 330;

        //The classic right panel, wider while it holds the Physics Maker.
        float ClassicRightWidth => AuthoringShown ? 350 : 300;

        void DrawUI()
        {
            if (SideOrderControls.On)
            {
                DrawSideOrderUI();
                return;
            }
            var viewport = ImGui.GetMainViewport();
            ImGui.SetNextWindowPos(viewport.Pos);
            ImGui.SetNextWindowSize(viewport.Size);
            //The host is the window's backdrop; other windows keep the style's WindowBg.
            ImGui.PushStyleColor(ImGuiCol.WindowBg, Theme.Bg);
            ImGui.Begin(
                "##host",
                ImGuiWindowFlags.NoTitleBar
                    | ImGuiWindowFlags.NoResize
                    | ImGuiWindowFlags.NoMove
                    | ImGuiWindowFlags.NoCollapse
                    | ImGuiWindowFlags.NoBringToFrontOnFocus
                    | ImGuiWindowFlags.NoNavFocus
                    | ImGuiWindowFlags.MenuBar
            );
            ImGui.PopStyleColor();

            DrawMenuBar();

            if (_scene == null)
            {
                DrawRomfsSetup();
                DrawSettingsWindow();
                ImGui.End();
                return;
            }

            float rightWidth = ClassicRightWidth;

            ImGui.BeginChild("##left", new Vector2(LeftPanelWidth, 0), true);
            DrawLeftPanel();
            ImGui.EndChild();

            _pipeline.SelectedMaterial = _standalone != null ? _selectedMaterial : null;

            ImGui.SameLine();
            ImGui.BeginChild(
                "##center",
                new Vector2(-rightWidth - 8, 0),
                false,
                ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse
            );
            DrawViewport();
            ImGui.EndChild();

            ImGui.SameLine();
            ImGui.BeginChild("##right", new Vector2(0, 0), true);
            if (_effect != null)
                DrawEffectSidebar();
            else
                DrawRightSidebar();
            ImGui.EndChild();

            DrawFloatingWindows();
            ImGui.End();
        }

        void DrawLeftPanel()
        {
            if (_effect != null)
                DrawEffectPanel();
            else if (_standalone != null)
                DrawStandalonePanel();
            else
                DrawPlayerPanel();
        }

        void DrawFloatingWindows()
        {
            HandleSimulationKey();
            DrawSettingsWindow();
            DrawMaterialEditorWindow();
            DrawClothInspectorWindow();
            DrawClothAuthorWindows();
            DrawPhysicsAuthoringWindows();
            DrawTextureWindow();
            DrawProvenanceModal();
            DrawCopyModal();
            DrawPhiveResultModal();
            DrawSpiralSaveModal();
            DrawClearPhysicsModal();
            RunDeferred();
            SyncMakerPose();
        }

        void DrawMenuBar()
        {
            if (!ImGui.BeginMenuBar())
                return;

            //The title bar row is tall, with the title font and larger menus; the thin row under
            //an OS title bar takes the header font and the body.
            var menuFont = SideOrderLayout.TitleBar ? UiFonts.Menu : null;
            if ((SideOrderLayout.TitleBar ? UiFonts.Title : UiFonts.Header) is { } title)
                SceneTitle(title, SceneName);
            else
            {
                ImGui.PushStyleColor(ImGuiCol.Text, Theme.Gold);
                ImGui.Text("PLAYER VIEWER");
                ImGui.PopStyleColor();
                ImGui.Separator();
            }

            //Pushed across the File menu and popped only once it has ended: a font popped inside
            //the menu's popup would come off the popup's draw list instead of this one.
            if (menuFont is { } pushed)
                ImGui.PushFont(pushed);
            float menusLeft = ImGui.GetCursorScreenPos().X;
            if (ImGui.BeginMenu("File"))
            {
                NoteBarWindow();
                //An export drives its scene until it ends, so the scene items wait for it. The
                //setup screen has no scene to open anything over.
                bool ready = !ExportBusy && _scene != null;
                if (ImGui.MenuItem("Change romfs path...", null, false, ready))
                    ShowRomfsSetup();
                if (ImGui.MenuItem("View model file... (or drag && drop)", null, false, ready))
                {
                    string file = NativeFolderPicker.OpenFile(
                        "Open Model",
                        "BFRES models (*.bfres;*.zs)",
                        "*.bfres;*.zs"
                    );
                    if (!string.IsNullOrEmpty(file))
                        OpenStandalone(file);
                }
                if (ImGui.MenuItem("View effect file... (or drag && drop)", null, false, ready))
                    BrowseEffectFile();
                DrawPhiveConvertMenu(!ExportBusy);
                var effects = RomfsEffectFiles();
                if (_romfs?.Game == GameKind.Spiral)
                {
                    if (ImGui.MenuItem("Effect Viewer", null, false, ready && effects.Count > 0))
                        OpenEffect(effects[0]);
                }
                else if (effects.Count > 0 && ImGui.BeginMenu("Game effects", ready))
                {
                    NoteBarWindow();
                    foreach (string file in effects)
                        if (ImGui.MenuItem(RomfsEffectLabel(file)))
                            OpenEffect(file);
                    ImGui.EndMenu();
                }
                if (_standalone != null && ImGui.MenuItem("Back to player", null, false, ready))
                    CloseStandalone();
                if (_effect != null && ImGui.MenuItem("Back to player##effect", null, false, ready))
                    CloseEffect();
                ImGui.Separator();
                if (ImGui.MenuItem("Exit"))
                    Close();
                ImGui.EndMenu();
            }

            if (ImGui.MenuItem("Settings"))
                _showSettings = true;
            if (ImGui.BeginMenu("Game"))
            {
                NoteBarWindow();
                foreach (var game in new[] { GameKind.Spiral, GameKind.Thunder })
                    if (
                        ImGui.MenuItem(
                            GameNames.Label(game),
                            null,
                            _config.CurrentGame == game,
                            !ExportBusy
                        )
                    )
                        SwitchGame(game);
                ImGui.EndMenu();
            }
            //The menus take clicks in the custom title bar; the title and path drag it.
            NoteBarItem(new Vector2(menusLeft, ImGui.GetItemRectMin().Y), ImGui.GetItemRectMax());
            if (menuFont != null)
                ImGui.PopFont();

            if (_romfs != null)
                MenuPath(_romfs.Root);

            ImGui.EndMenuBar();
        }

        /// <summary>
        /// Back to the setup screen, for another romfs. Every scene closes and the app returns to
        /// the player view, so a load starts as on launch.
        /// </summary>
        void ShowRomfsSetup()
        {
            if (ExportBusy)
                return;
            StopAnimChain();
            TearDownEffect();
            TearDownStandalone();
            _pipeline.SelectedMaterial = null;
            _scene?.Dispose();
            _scene = null;
            ReleaseShaderPrograms();
            CompactHeap();
            _pipeline.FramePlayer();
            _romfsInput = _config.Thunder.RomfsPath ?? "";
            _sdodrInput = _config.SdodrRomfsPath ?? "";
            _raidersInput = _config.Spiral.RomfsPath ?? "";
            _thunderLayeredInput = _config.Thunder.LayeredFsPath ?? "";
            _raidersLayeredInput = _config.Spiral.LayeredFsPath ?? "";
        }

        string _thunderLayeredInput = "",
            _raidersLayeredInput = "";

        //A LayeredFS folder field on the setup screen.
        static void LayeredField(string title, string id, ref string input, float fieldWidth)
        {
            Widgets.SectionHeader(title);
            ImGui.SetNextItemWidth(fieldWidth);
            Widgets.PathInput("##" + id, ref input, 512);
            ImGui.SameLine();
            if (Widgets.Button("Browse...##" + id))
            {
                string folder = NativeFolderPicker.SelectFolder(
                    "Select LayeredFS (mod) folder",
                    input
                );
                if (!string.IsNullOrEmpty(folder))
                    input = folder;
            }
        }

        //A LayeredFS path typed on the setup screen; a new one is switched on.
        void SaveLayered(GameKind game, string path)
        {
            path ??= "";
            var settings = _config.Of(game);
            if (!PathsEqualOrEmpty(path, settings.LayeredFsPath))
                settings.UseLayeredFs = path.Length > 0;
            settings.LayeredFsPath = path;
        }

        static bool PathsEqualOrEmpty(string a, string b) =>
            string.IsNullOrEmpty(a)
                ? string.IsNullOrEmpty(b)
                : !string.IsNullOrEmpty(b) && PathsEqual(a, b);

        /// <summary>
        /// Makes another game current. Its romfs loads in place of this one when it is set,
        /// otherwise the setup screen asks for it.
        /// </summary>
        void SwitchGame(GameKind game)
        {
            if (ExportBusy || game == _config.CurrentGame)
                return;
            SavePlayerConfig();
            _config.CurrentGame = game;
            _config.Save();
            _layeredInput = _config.Active.LayeredFsPath ?? "";
            if (Romfs.IsValidRoot(_config.Active.RomfsPath, game))
            {
                StopAnimChain();
                _needsLoad = true;
            }
            else
                ShowRomfsSetup();
        }

        //The scene the Side Order title row names. Classic keeps its one title.
        string SceneName =>
            _effect != null ? "EFFECT VIEWER"
            : _standalone != null ? "MODEL VIEWER"
            : "PLAYER VIEWER";

        //The title font is taller than the bar's line, so it is drawn centred on the line's
        //capitals and only its width takes part in the layout.
        static void SceneTitle(ImFontPtr font, string text)
        {
            var pos = ImGui.GetCursorScreenPos();
            float line = ImGui.GetFrameHeight();
            ImGui.PushFont(font);
            var size = ImGui.CalcTextSize(text);
            Widgets.DrawText(
                ImGui.GetWindowDrawList(),
                new Vector2(pos.X, MathF.Round(pos.Y + (line - size.Y) / 2)),
                ImGui.GetColorU32(Theme.TextMain),
                text
            );
            ImGui.PopFont();
            ImGui.Dummy(new Vector2(size.X, line));
            ImGui.SameLine(0, 30);
        }

        //Room the classic menu bar keeps for the path at its right end.
        const float ClassicPathWidth = 320;

        //Cut from the left to what fits. Side Order right aligns it with the right card's edge,
        //or that far left of the window buttons when they are drawn.
        void MenuPath(string path)
        {
            if (string.IsNullOrEmpty(path))
                return;
            if (!SideOrderControls.On)
            {
                float start = ImGui.GetWindowWidth() - ClassicPathWidth;
                ImGui.SameLine(start);
                Widgets.DimText(FitPathLeft(path, ImGui.GetWindowContentRegionMax().X - start));
                return;
            }
            var viewport = ImGui.GetMainViewport();
            float right =
                viewport.Pos.X
                + viewport.Size.X
                - SideOrderLayout.RightEdgeGap
                - SideOrderLayout.WindowButtonsWidth
                - ImGui.GetWindowPos().X;
            float left = ImGui.GetCursorPosX() + 24;
            string shown = FitPathLeft(path, right - left);
            if (shown.Length == 0)
                return;
            ImGui.SameLine(right - ImGui.CalcTextSize(shown).X);
            Widgets.DimText(shown);
        }

        static string FitPathLeft(string path, float width)
        {
            if (width <= 0)
                return "";
            if (ImGui.CalcTextSize(path).X <= width)
                return path;
            int lo = 0,
                hi = path.Length;
            while (lo < hi)
            {
                int keep = (lo + hi + 1) / 2;
                if (ImGui.CalcTextSize("..." + path.Substring(path.Length - keep)).X <= width)
                    lo = keep;
                else
                    hi = keep - 1;
            }
            return lo == 0 ? "" : "..." + path.Substring(path.Length - lo);
        }

        //Each setup card is as tall as its content asked for last frame.
        readonly float[] _setupCardHeight = { 220, 160 };

        const float SetupMinHeight = 100;
        const float SetupCardGap = 12;

        //The two games' cards, Splatoon 3 above Raiders, in a region that scrolls when the
        //window is too short for them.
        void DrawRomfsSetup()
        {
            if (SideOrderControls.On)
            {
                DrawRomfsSetupSideOrder();
                return;
            }
            var avail = ImGui.GetContentRegionAvail();
            ImGui.BeginChild("##setupscroll", avail, false);
            const float width = 520;
            float total = _setupCardHeight[0] + SetupCardGap + _setupCardHeight[1];
            float y = Math.Max(0, (avail.Y - total) / 2);
            float x = Math.Max(0, (ImGui.GetContentRegionAvail().X - width) / 2);
            var style = ImGui.GetStyle();
            for (int i = 0; i < 2; i++)
            {
                ImGui.SetCursorPos(new Vector2(x, y));
                ImGui.BeginChild(
                    i == 0 ? "##setup" : "##setupraiders",
                    new Vector2(width, _setupCardHeight[i]),
                    true
                );
                DrawSetupCard(i);
                _setupCardHeight[i] = Math.Max(
                    SetupMinHeight,
                    MathF.Ceiling(
                        ImGui.GetCursorPosY() - style.ItemSpacing.Y + style.WindowPadding.Y
                    )
                );
                ImGui.EndChild();
                y += _setupCardHeight[i] + SetupCardGap;
            }
            ImGui.EndChild();
        }

        //The cards in the middle of the window below the menu row.
        void DrawRomfsSetupSideOrder()
        {
            var viewport = ImGui.GetMainViewport();
            float top = viewport.Pos.Y + SideOrderLayout.CardsTop;
            var regionMin = new Vector2(viewport.Pos.X, top);
            var regionSize = new Vector2(viewport.Size.X, viewport.Pos.Y + viewport.Size.Y - top);
            ImGui.SetCursorScreenPos(regionMin);
            ImGui.PushStyleColor(ImGuiCol.ChildBg, System.Numerics.Vector4.Zero);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
            ImGui.BeginChild("##setupscroll", Vector2.Max(regionSize, Vector2.One), false);
            ImGui.PopStyleVar();
            ImGui.PopStyleColor();
            Widgets.DecorateScrollbar();

            const float width = 560;
            const float gap = SideOrderLayout.Gap;
            float total = _setupCardHeight[0] + SetupCardGap + _setupCardHeight[1];
            var origin = ImGui.GetCursorScreenPos();
            float y = Math.Max(gap, (regionSize.Y - total) / 2 - 40);
            float x = MathF.Round(Math.Max(gap, (regionSize.X - width) / 2));
            for (int i = 0; i < 2; i++)
            {
                var min = new Vector2(origin.X + x, MathF.Round(origin.Y + y));
                var max = min + new Vector2(width, _setupCardHeight[i]);
                int card = i;
                _setupCardHeight[i] = Math.Max(
                    SetupMinHeight,
                    Card(
                        i == 0 ? "##setup" : "##setupraiders",
                        min,
                        max,
                        7 + i,
                        () => DrawSetupCard(card)
                    )
                );
                y += _setupCardHeight[i] + SetupCardGap;
            }
            //The scroll extent: the cards and a gap below them.
            ImGui.SetCursorScreenPos(new Vector2(origin.X, origin.Y + y - SetupCardGap + gap));
            ImGui.Dummy(Vector2.One);
            ImGui.EndChild();
        }

        void DrawSetupCard(int card)
        {
            if (card == 0)
                DrawThunderSetup();
            else
                DrawRaidersSetup();
        }

        //The typed paths' checks, redone only when a path changes.
        string _checkedThunder,
            _checkedRaiders;
        bool _thunderValid;
        string _raidersRoot;

        static float SetupFieldWidth =>
            -(Widgets.ButtonWidth("Browse...") + ImGui.GetStyle().ItemSpacing.X);

        void CheckSetupPaths()
        {
            if (_checkedThunder != _romfsInput)
            {
                _checkedThunder = _romfsInput;
                _thunderValid = Romfs.IsValidRoot(_romfsInput, GameKind.Thunder);
            }
            if (_checkedRaiders != _raidersInput)
            {
                _checkedRaiders = _raidersInput;
                _raidersRoot = Romfs.FindRoot(_raidersInput, GameKind.Spiral);
            }
        }

        void DrawThunderSetup()
        {
            float fieldWidth = SetupFieldWidth;

            Widgets.SectionHeader("Splatoon 3 romfs");
            ImGui.SetNextItemWidth(fieldWidth);
            Widgets.PathInput("##romfs", ref _romfsInput, 512);
            ImGui.SameLine();
            if (Widgets.Button("Browse..."))
            {
                string folder = NativeFolderPicker.SelectFolder("Select romfs folder", _romfsInput);
                if (!string.IsNullOrEmpty(folder))
                    _romfsInput = folder;
            }
            CheckSetupPaths();
            if (!_thunderValid && !string.IsNullOrEmpty(_romfsInput))
                Widgets.ErrorText("Not a valid romfs (needs Model/ + RSDB/)");

            Widgets.SectionHeader("Side Order DLC romfs (optional)");
            ImGui.SetNextItemWidth(fieldWidth);
            Widgets.PathInput("##sdodr", ref _sdodrInput, 512);
            ImGui.SameLine();
            if (Widgets.Button("Browse...##sdodr"))
            {
                string folder = NativeFolderPicker.SelectFolder(
                    "Select Side Order romfs folder",
                    _sdodrInput
                );
                if (!string.IsNullOrEmpty(folder))
                    _sdodrInput = folder;
            }
            LayeredField(
                "Splatoon 3 LayeredFS (optional)",
                "thunderlayered",
                ref _thunderLayeredInput,
                fieldWidth
            );
            DrawSetupLoad(GameKind.Thunder, _thunderValid);
        }

        void DrawRaidersSetup()
        {
            float fieldWidth = SetupFieldWidth;

            Widgets.SectionHeader("Splatoon Raiders romfs");
            ImGui.SetNextItemWidth(fieldWidth);
            Widgets.PathInput("##raiders", ref _raidersInput, 512);
            ImGui.SameLine();
            if (Widgets.Button("Browse...##raiders"))
            {
                string folder = NativeFolderPicker.SelectFolder(
                    "Select Splatoon Raiders romfs folder",
                    _raidersInput
                );
                if (!string.IsNullOrEmpty(folder))
                    _raidersInput = folder;
            }
            CheckSetupPaths();
            if (_raidersRoot == null && !string.IsNullOrEmpty(_raidersInput))
                Widgets.ErrorText("Not a Raiders romfs (needs Model/Player00.Player00.bfres.zs)");
            else if (_raidersRoot != null && !PathsEqual(_raidersRoot, _raidersInput))
                Widgets.DimText(_raidersRoot);
            LayeredField(
                "Splatoon Raiders LayeredFS (optional)",
                "raiderslayered",
                ref _raidersLayeredInput,
                fieldWidth
            );
            DrawSetupLoad(GameKind.Spiral, _raidersRoot != null);
        }

        //A card's load button. It needs only its own game's romfs; the other card's paths are
        //kept when they are valid.
        void DrawSetupLoad(GameKind game, bool ready)
        {
            if (_romfsError != null && game == _config.CurrentGame)
                Widgets.ErrorText(_romfsError);
            ImGui.Dummy(new Vector2(0, 6));
            Widgets.AccentButton(
                $"Load {GameNames.Title(game)}##load{game}",
                ready,
                () =>
                {
                    if (game == GameKind.Thunder || _thunderValid)
                    {
                        _config.Thunder.RomfsPath = _romfsInput;
                        _config.SdodrRomfsPath = _sdodrInput;
                        SaveLayered(GameKind.Thunder, _thunderLayeredInput);
                    }
                    if (game == GameKind.Spiral || _raidersRoot != null)
                    {
                        _config.Spiral.RomfsPath = _raidersRoot ?? _raidersInput;
                        _raidersInput = _config.Spiral.RomfsPath;
                        SaveLayered(GameKind.Spiral, _raidersLayeredInput);
                    }
                    _config.CurrentGame = game;
                    _config.Save();
                    _needsLoad = true;
                }
            );
        }

        static bool PathsEqual(string a, string b)
        {
            try
            {
                return string.Equals(
                    System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(a)),
                    System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(b)),
                    StringComparison.OrdinalIgnoreCase
                );
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
