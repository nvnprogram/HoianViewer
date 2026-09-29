using System;
using ImGuiNET;
using PlayerViewer.Core;
using Vector2 = System.Numerics.Vector2;
using Vector4 = System.Numerics.Vector4;

namespace PlayerViewer.UI
{
    // The Settings window: appearance, lists and the export preferences.
    public partial class ViewerWindow
    {
        void DrawSettingsWindow()
        {
            if (!_showSettings)
                return;

            ImGui.SetNextWindowSize(new Vector2(420, 340), ImGuiCond.FirstUseEver);
            if (
                !BeginCardWindow(
                    "Settings",
                    ref _showSettings,
                    ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoDocking
                )
            )
            {
                ImGui.End();
                return;
            }

            bool dirty = false;

            DrawAppearanceSettings();
            DrawListSettings();

            Widgets.SectionHeader("Trim deadspace");
            Widgets.WrappedText("Crops empty space off exports. Applies to every format.");
            ImGui.Spacing();

            Widgets.Checkbox(
                "Enable",
                _config.TrimDeadspace,
                v => _config.TrimDeadspace = v,
                () => dirty = true
            );

            ImGui.SetNextItemWidth(160);
            Widgets.InputInt(
                "Margin (px)",
                _config.TrimMarginPx,
                v => _config.TrimMarginPx = Math.Max(0, v),
                () => dirty = true
            );
            ImGui.SameLine();
            Widgets.DimText("kept around the content");

            if (_config.TrimDeadspace)
                Widgets.ColoredText(
                    Theme.Gold,
                    "Trimmed animations encode twice and use temporary disk space."
                );

            Widgets.SectionHeader("WebP / WebM quality");
            Widgets.WrappedText("100 is lossless. Lower is smaller and faster to encode.");
            ImGui.Spacing();

            ImGui.SetNextItemWidth(-1);
            string qLabel = _config.WebpQuality >= 100 ? "Lossless" : "Lossy %d";
            Widgets.SliderInt(
                "##webpq",
                _config.WebpQuality,
                0,
                100,
                v => _config.WebpQuality = Math.Clamp(v, 0, 100),
                () => dirty = true,
                qLabel
            );

            if (Widgets.Button("Lossless"))
            {
                _config.WebpQuality = 100;
                dirty = true;
            }
            ImGui.SameLine();
            if (Widgets.Button("Near-lossless"))
            {
                _config.WebpQuality = 90;
                dirty = true;
            }
            ImGui.SameLine();
            if (Widgets.Button("Lossy"))
            {
                _config.WebpQuality = 75;
                dirty = true;
            }

            Widgets.SectionHeader("Supersample (export quality)");
            Widgets.WrappedText("Renders exports at this multiple of the capture size.");
            ImGui.Spacing();

            ImGui.SetNextItemWidth(-1);
            Widgets.SliderInt(
                "##supersample",
                _config.ExportSupersample,
                1,
                8,
                v => _config.ExportSupersample = Math.Clamp(v, 1, 8),
                () => dirty = true,
                "%dx"
            );

            //The render target has to fit the driver's size limit.
            int wantSs = _config.ExportSupersample;
            var (_, capW, capH) = CaptureSizes[_captureRes];
            int ss = ScenePipeline.ClampSupersample(wantSs, capW, capH);
            Widgets.DimText($"Renders {capW * ss}x{capH * ss} per frame, saves {capW}x{capH}.");
            if (_config.TrimDeadspace)
                Widgets.DimText("Trim is on, so that saved size is an upper bound.");

            if (ss < wantSs)
                Widgets.ErrorText(
                    $"This GPU caps render targets at {ScenePipeline.MaxTargetSize}px, so a "
                        + $"{capW}x{capH} export uses supersample {ss}x."
                );
            else if (ss >= 8)
                Widgets.ErrorText("8x is extreme: may exhaust GPU memory at 4K");
            else if (ss > 4)
                Widgets.ColoredText(
                    Theme.Gold,
                    "Large GPU memory use (grows with the square of the factor)"
                );

            Widgets.SectionHeader("Physics warm-up");
            Widgets.WrappedText("Extra playthroughs before recording, so hair starts settled.");
            ImGui.Spacing();

            ImGui.SetNextItemWidth(-1);
            string plLabel = _config.PrerollLoops <= 0 ? "Off" : "%d loops";
            Widgets.SliderInt(
                "##preroll",
                _config.PrerollLoops,
                0,
                PrerollMaxLoops,
                v => _config.PrerollLoops = Math.Clamp(v, 0, PrerollMaxLoops),
                () => dirty = true,
                plLabel
            );

            ImGui.Spacing();
            Widgets.Checkbox(
                "Physics convergence",
                _config.PhysicsConverge,
                v => _config.PhysicsConverge = v,
                () => dirty = true
            );
            Widgets.WrappedText("Eases hair back to its first pose so loops don't jump.");

            Widgets.SectionHeader("Data folder");
            Widgets.WrappedText(
                "Holds settings.json. An ffmpeg here is used over the one on PATH."
            );
            Widgets.DimText(AppPaths.DataDir);
            if (Widgets.Button("Open data folder"))
                AppPaths.OpenDataDir();

            if (dirty)
                _config.Save();

            ImGui.End();
        }
    }
}
