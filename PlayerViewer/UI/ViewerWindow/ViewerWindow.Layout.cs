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

            float rightWidth = 300;

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
            DrawClearPhysicsModal();
            RunDeferred();
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
                var effects = RomfsEffectFiles();
                if (effects.Count > 0 && ImGui.BeginMenu("Game effects", ready))
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
            //File and Settings take clicks in the custom title bar; the title and path drag it.
            NoteBarItem(new Vector2(menusLeft, ImGui.GetItemRectMin().Y), ImGui.GetItemRectMax());
            if (menuFont != null)
                ImGui.PopFont();

            if (_romfs != null)
                MenuPath(_config.RomfsPath);

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
            _romfsInput = _config.RomfsPath ?? "";
            _sdodrInput = _config.SdodrRomfsPath ?? "";
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

        //The setup is as tall as its content asked for last frame.
        float _setupCardHeight = 220;

        const float SetupMinHeight = 120;

        void DrawRomfsSetup()
        {
            if (SideOrderControls.On)
            {
                DrawRomfsSetupSideOrder();
                return;
            }
            var avail = ImGui.GetContentRegionAvail();
            var size = new Vector2(520, _setupCardHeight);
            ImGui.SetCursorPos((avail - size) / 2);
            ImGui.BeginChild("##setup", size, true);
            DrawRomfsSetupFields();
            var style = ImGui.GetStyle();
            _setupCardHeight = Math.Max(
                SetupMinHeight,
                MathF.Ceiling(ImGui.GetCursorPosY() - style.ItemSpacing.Y + style.WindowPadding.Y)
            );
            ImGui.EndChild();
        }

        //A card in the middle of the window, below the menu row.
        void DrawRomfsSetupSideOrder()
        {
            var viewport = ImGui.GetMainViewport();
            var size = new Vector2(560, _setupCardHeight);
            float top = viewport.Pos.Y + SideOrderLayout.CardsTop;
            var min = new Vector2(
                MathF.Round(viewport.Pos.X + (viewport.Size.X - size.X) / 2),
                MathF.Round(
                    Math.Max(
                        top + SideOrderLayout.Gap,
                        top + (viewport.Size.Y - top - size.Y) / 2 - 40
                    )
                )
            );
            _setupCardHeight = Math.Max(
                SetupMinHeight,
                Card("##setup", min, min + size, 7, DrawRomfsSetupFields)
            );
        }

        void DrawRomfsSetupFields()
        {
            float fieldWidth = -(Widgets.ButtonWidth("Browse...") + ImGui.GetStyle().ItemSpacing.X);

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
            bool valid = Romfs.IsValidRoot(_romfsInput);
            if (!valid && !string.IsNullOrEmpty(_romfsInput))
                Widgets.ErrorText("Not a valid romfs (needs Model/ + RSDB/)");
            if (_romfsError != null)
                Widgets.ErrorText(_romfsError);

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

            ImGui.Dummy(new Vector2(0, 6));
            Widgets.AccentButton(
                "Load",
                valid,
                () =>
                {
                    _config.RomfsPath = _romfsInput;
                    _config.SdodrRomfsPath = _sdodrInput;
                    _config.Save();
                    _needsLoad = true;
                }
            );
        }
    }
}
