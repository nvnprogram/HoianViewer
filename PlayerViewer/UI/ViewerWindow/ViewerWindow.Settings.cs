using System;
using Vector2 = System.Numerics.Vector2;
using Vector4 = System.Numerics.Vector4;
using ImGuiNET;

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
            if (!ImGui.Begin("Settings", ref _showSettings, ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoDocking))
            {
                ImGui.End();
                return;
            }

            bool dirty = false;

            Widgets.SectionHeader("Trim deadspace");
            ImGui.TextWrapped("Crops fully-transparent space off exported frames, shrinking " +
                "file size. Uses the transparent render to find the content, so it also crops " +
                "greenscreen MP4s. Applies to WebP, MP4, and PNG screenshots.");
            ImGui.Spacing();

            bool trim = _config.TrimDeadspace;
            if (ImGui.Checkbox("Enable", ref trim))
            {
                _config.TrimDeadspace = trim;
                dirty = true;
            }

            int margin = _config.TrimMarginPx;
            ImGui.SetNextItemWidth(160);
            if (ImGui.InputInt("Margin (px)", ref margin))
            {
                _config.TrimMarginPx = Math.Max(0, margin);
                dirty = true;
            }
            ImGui.SameLine();
            ImGui.TextColored(Theme.TextDim, "transparent padding kept around the content");

            if (trim)
                ImGui.TextColored(Theme.Gold, "Note: trimmed animation export buffers every frame " +
                    "to a temp file on disk first; transiently uses ~width×height×4×frames of space, " +
                    "times the supersample factor squared (several GB at 4K, tens of GB with high supersample).");

            Widgets.SectionHeader("WebP quality");
            ImGui.TextWrapped("100 = lossless (bit-exact, largest). Lower = lossy: much faster " +
                "to encode and far smaller, with some quality loss.");
            ImGui.Spacing();

            int quality = _config.WebpQuality;
            ImGui.SetNextItemWidth(-1);
            string qLabel = quality >= 100 ? "Lossless" : "Lossy %d";
            if (ImGui.SliderInt("##webpq", ref quality, 0, 100, qLabel))
            {
                _config.WebpQuality = Math.Clamp(quality, 0, 100);
                dirty = true;
            }

            if (ImGui.Button("Lossless")) { _config.WebpQuality = 100; dirty = true; }
            ImGui.SameLine();
            if (ImGui.Button("Near-lossless")) { _config.WebpQuality = 90; dirty = true; }
            ImGui.SameLine();
            if (ImGui.Button("Lossy")) { _config.WebpQuality = 75; dirty = true; }

            Widgets.SectionHeader("Supersample (export quality)");
            ImGui.TextWrapped("Renders exports at this multiple of the capture size. With trim on, " +
                "the crop keeps that full internal resolution, so you only need the camera angle " +
                "right; a small or loosely-framed subject still exports sharp.");
            ImGui.Spacing();

            int ss = _config.ExportSupersample;
            ImGui.SetNextItemWidth(-1);
            if (ImGui.SliderInt("##supersample", ref ss, 1, 8, "%dx"))
            {
                _config.ExportSupersample = Math.Clamp(ss, 1, 8);
                dirty = true;
            }

            ss = _config.ExportSupersample;
            ImGui.TextColored(Theme.TextDim,
                $"A 1080p export renders {1920 * ss}x{1080 * ss} internally ({ss * ss}x the pixels).");
            if (ss >= 8)
                ImGui.TextColored(new Vector4(0.95f, 0.35f, 0.3f, 1),
                    "8x is extreme: may exhaust GPU memory at 4K");
            else if (ss > 4)
                ImGui.TextColored(Theme.Gold,
                    "High supersample: large GPU memory & temp-disk use (grows with the square of the factor)");

            if (dirty)
                _config.Save();

            ImGui.End();
        }
    }
}
