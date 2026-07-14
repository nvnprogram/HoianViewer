using System;
using ImGuiNET;
using PlayerViewer.Core;
using Vector2 = System.Numerics.Vector2;
using Vector4 = System.Numerics.Vector4;

namespace PlayerViewer.UI
{
    // Settings window (opened from the menu bar): export/capture preferences persisted in
    // AppConfig: deadspace trimming and WebP encode quality.
    public partial class ViewerWindow
    {
        void DrawSettingsWindow()
        {
            if (!_showSettings)
                return;

            ImGui.SetNextWindowSize(new Vector2(420, 340), ImGuiCond.FirstUseEver);
            if (
                !ImGui.Begin(
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

            Widgets.SectionHeader("Trim deadspace");
            ImGui.TextWrapped(
                "Crops fully-transparent space off exported frames, shrinking "
                    + "file size. Uses the transparent render to find the content, so it crops the "
                    + "color/image background too. Applies to WebP, WebM, MP4, and PNG."
            );
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
            Widgets.DimText("transparent padding kept around the content");

            if (_config.TrimDeadspace)
                ImGui.TextColored(
                    Theme.Gold,
                    "Note: trimmed animation export buffers every frame "
                        + "to a temp file on disk first; transiently uses ~width×height×4×frames of space, "
                        + "times the supersample factor squared (several GB at 4K, tens of GB with high supersample)."
                );

            Widgets.SectionHeader("WebP / WebM quality");
            ImGui.TextWrapped(
                "Quality for the transparent WebP and WebM (VP9) exports. 100 = "
                    + "lossless (largest). Lower = lossy: smaller and faster to encode, with some quality loss."
            );
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

            if (ImGui.Button("Lossless"))
            {
                _config.WebpQuality = 100;
                dirty = true;
            }
            ImGui.SameLine();
            if (ImGui.Button("Near-lossless"))
            {
                _config.WebpQuality = 90;
                dirty = true;
            }
            ImGui.SameLine();
            if (ImGui.Button("Lossy"))
            {
                _config.WebpQuality = 75;
                dirty = true;
            }

            Widgets.SectionHeader("Supersample (export quality)");
            ImGui.TextWrapped(
                "Renders exports at this multiple of the capture size. With trim on, "
                    + "the crop keeps that full internal resolution, so you only need the camera angle "
                    + "right; a small or loosely-framed subject still exports sharp."
            );
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

            int ss = _config.ExportSupersample;
            Widgets.DimText(
                $"A 1080p export renders {1920 * ss}x{1080 * ss} internally ({ss * ss}x the pixels)."
            );
            if (ss >= 8)
                Widgets.ErrorText("8x is extreme: may exhaust GPU memory at 4K");
            else if (ss > 4)
                ImGui.TextColored(
                    Theme.Gold,
                    "High supersample: large GPU memory & temp-disk use (grows with the square of the factor)"
                );

            Widgets.SectionHeader("Physics warm-up");
            ImGui.TextWrapped(
                "Plays the animation (/ first animation in the sequence) through "
                    + "this many extra times before recording starts without capturing. Physics reset"
                    + "whenever an animation loads, so frame 0 has a twitch each time the exported "
                    + "WebP/WebM loops. A warm-up lets the sim settle first."
            );
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

            Widgets.SectionHeader("Data folder");
            ImGui.TextWrapped(
                "settings.json lives here. Drop an ffmpeg binary here to use it "
                    + "instead of one on PATH."
            );
            Widgets.DimText(AppPaths.DataDir);
            if (ImGui.Button("Open data folder"))
                AppPaths.OpenDataDir();

            if (dirty)
                _config.Save();

            ImGui.End();
        }
    }
}
