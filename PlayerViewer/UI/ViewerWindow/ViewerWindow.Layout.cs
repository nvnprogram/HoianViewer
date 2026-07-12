using System;
using Vector2 = System.Numerics.Vector2;
using Vector4 = System.Numerics.Vector4;
using ImGuiNET;
using PlayerViewer.Core;

namespace PlayerViewer.UI
{
    // Top-level window layout: host window, menu bar, and the romfs-setup screen.
    public partial class ViewerWindow
    {
        void DrawUI()
        {
            var viewport = ImGui.GetMainViewport();
            ImGui.SetNextWindowPos(viewport.Pos);
            ImGui.SetNextWindowSize(viewport.Size);
            ImGui.Begin("##host",
                ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove |
                ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoBringToFrontOnFocus | ImGuiWindowFlags.NoNavFocus |
                ImGuiWindowFlags.MenuBar);

            DrawMenuBar();

            if (_scene == null)
            {
                DrawRomfsSetup();
                ImGui.End();
                return;
            }

            float leftWidth = 330;
            float rightWidth = 300;

            ImGui.BeginChild("##left", new Vector2(leftWidth, 0), true);
            if (_standalone != null)
                DrawStandalonePanel();
            else
                DrawPlayerPanel();
            ImGui.EndChild();

            ImGui.SameLine();
            ImGui.BeginChild("##center", new Vector2(-rightWidth - 8, 0), false,
                ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
            DrawViewport();
            ImGui.EndChild();

            ImGui.SameLine();
            ImGui.BeginChild("##right", new Vector2(0, 0), true);
            DrawRightSidebar();
            ImGui.EndChild();

            DrawSettingsWindow();

            ImGui.End();
        }

        void DrawMenuBar()
        {
            if (!ImGui.BeginMenuBar())
                return;

            ImGui.PushStyleColor(ImGuiCol.Text, Theme.Gold);
            ImGui.Text("PLAYER VIEWER");
            ImGui.PopStyleColor();
            ImGui.Separator();

            if (ImGui.BeginMenu("File"))
            {
                if (ImGui.MenuItem("Change romfs path..."))
                {
                    _scene?.Dispose();
                    _scene = null;
                    _romfsInput = _config.RomfsPath ?? "";
                    _sdodrInput = _config.SdodrRomfsPath ?? "";
                }
                if (ImGui.MenuItem("View model file... (or drag && drop)"))
                {
                    string file = NativeFolderPicker.OpenFile("Open Model", "BFRES models (*.bfres;*.zs)", "*.bfres;*.zs");
                    if (!string.IsNullOrEmpty(file))
                        OpenStandalone(file);
                }
                if (_standalone != null && ImGui.MenuItem("Back to player"))
                    CloseStandalone();
                ImGui.Separator();
                if (ImGui.MenuItem("Exit"))
                    Close();
                ImGui.EndMenu();
            }

            if (ImGui.MenuItem("Settings"))
                _showSettings = true;

            if (_romfs != null)
            {
                ImGui.SameLine(ImGui.GetWindowWidth() - 320);
                ImGui.TextColored(Theme.TextDim, TruncatePath(_config.RomfsPath, 42));
            }

            ImGui.EndMenuBar();
        }

        static string TruncatePath(string path, int max)
        {
            if (string.IsNullOrEmpty(path) || path.Length <= max)
                return path ?? "";
            return "..." + path.Substring(path.Length - max + 3);
        }

        void DrawRomfsSetup()
        {
            var avail = ImGui.GetContentRegionAvail();
            ImGui.SetCursorPos(new Vector2(avail.X / 2 - 260, avail.Y / 2 - 90));
            ImGui.BeginChild("##setup", new Vector2(520, 210), true);

            ImGui.PushStyleColor(ImGuiCol.Text, Theme.Gold);
            ImGui.Text("Splatoon 3 romfs path");
            ImGui.PopStyleColor();
            ImGui.Spacing();

            ImGui.SetNextItemWidth(-90);
            ImGui.InputText("##romfs", ref _romfsInput, 512);
            ImGui.SameLine();
            if (ImGui.Button("Browse..."))
            {
                string folder = NativeFolderPicker.SelectFolder("Select romfs folder", _romfsInput);
                if (!string.IsNullOrEmpty(folder))
                    _romfsInput = folder;
            }

            ImGui.Spacing();
            bool valid = Romfs.IsValidRoot(_romfsInput);
            if (!valid && !string.IsNullOrEmpty(_romfsInput))
                ImGui.TextColored(new Vector4(0.9f, 0.35f, 0.3f, 1), "Not a valid romfs (needs Model/ + RSDB/)");
            if (_romfsError != null)
                ImGui.TextColored(new Vector4(0.9f, 0.35f, 0.3f, 1), _romfsError);

            ImGui.Spacing();
            ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1), "Side Order DLC romfs (optional)");
            ImGui.SetNextItemWidth(-90);
            ImGui.InputText("##sdodr", ref _sdodrInput, 512);
            ImGui.SameLine();
            if (ImGui.Button("Browse...##sdodr"))
            {
                string folder = NativeFolderPicker.SelectFolder("Select Side Order romfs folder", _sdodrInput);
                if (!string.IsNullOrEmpty(folder))
                    _sdodrInput = folder;
            }

            ImGui.Spacing();
            if (!valid) ImGui.PushStyleVar(ImGuiStyleVar.Alpha, 0.45f);
            if (ImGui.Button("Load", new Vector2(120, 0)) && valid)
            {
                _config.RomfsPath = _romfsInput;
                _config.SdodrRomfsPath = _sdodrInput;
                _config.Save();
                _needsLoad = true;
            }
            if (!valid) ImGui.PopStyleVar();

            ImGui.EndChild();
        }
    }
}
