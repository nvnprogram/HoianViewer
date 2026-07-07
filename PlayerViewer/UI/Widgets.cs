using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ImGuiNET;
using PlayerViewer.Core;

namespace PlayerViewer.UI
{
    /// <summary>
    /// Reusable themed widgets: searchable gear combos, section headers.
    /// </summary>
    public static class Widgets
    {
        static readonly Dictionary<string, string> _searches = new();

        public static void SectionHeader(string text)
        {
            ImGui.Spacing();
            ImGui.TextColored(Theme.Gold, text.ToUpperInvariant());
            ImGui.PushStyleColor(ImGuiCol.Separator, Theme.GoldDim);
            ImGui.Separator();
            ImGui.PopStyleColor();
            ImGui.Spacing();
        }

        /// <summary>
        /// Searchable combo for a gear list. Returns true when the selection changed
        /// (selected receives the new entry, null = none).
        /// </summary>
        public static bool GearCombo(string label, List<GearEntry> entries, GearEntry current,
            out GearEntry selected, bool allowNone = true, string noneLabel = "Blank")
        {
            selected = current;
            bool changed = false;

            string preview = current?.DisplayName ?? noneLabel;
            ImGui.SetNextItemWidth(-1);
            if (!ImGui.BeginCombo("##" + label, preview, ImGuiComboFlags.HeightLarge))
                return false;

            string search = _searches.GetValueOrDefault(label, "");
            bool justOpened = ImGui.IsWindowAppearing();
            if (justOpened)
                ImGui.SetKeyboardFocusHere();
            ImGui.SetNextItemWidth(-1);
            if (ImGui.InputText("##search" + label, ref search, 64))
                _searches[label] = search;

            ImGui.Separator();
            if (ImGui.BeginChild("##list" + label, new Vector2(0, 300)))
            {
                if (allowNone && MatchesSearch(noneLabel, search))
                {
                    if (ImGui.Selectable(noneLabel, current == null))
                    {
                        selected = null;
                        changed = true;
                    }
                }

                //Custom (dropped-in) entries float to the top and get gold text.
                foreach (var group in new[] { true, false })
                {
                    foreach (var entry in entries)
                    {
                        if (entry.IsCustom != group)
                            continue;
                        if (!MatchesSearch(entry.DisplayName, search) && !MatchesSearch(entry.Label ?? "", search))
                            continue;

                        if (entry.IsCustom)
                            ImGui.PushStyleColor(ImGuiCol.Text, Theme.GoldBright);
                        bool isSelected = entry == current;
                        if (ImGui.Selectable($"{entry.DisplayName}##{entries.IndexOf(entry)}", isSelected))
                        {
                            selected = entry;
                            changed = true;
                        }
                        if (entry.IsCustom)
                            ImGui.PopStyleColor();
                        //Scroll to the selection only when the popup opens, so the
                        //user can still scroll the list freely afterwards.
                        if (isSelected && justOpened)
                            ImGui.SetScrollHereY();
                        if (!string.IsNullOrEmpty(entry.Label) && ImGui.IsItemHovered())
                            ImGui.SetTooltip(entry.Label);
                    }
                }
            }
            ImGui.EndChild();
            if (changed)
                ImGui.CloseCurrentPopup();
            ImGui.EndCombo();
            return changed;
        }

        static bool MatchesSearch(string text, string search)
        {
            return string.IsNullOrEmpty(search) ||
                text.Contains(search, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Label + combo on one line with fixed label column.</summary>
        public static void LabeledRow(string label, Action drawControl)
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(Theme.TextDim, label);
            ImGui.SameLine(92);
            drawControl();
        }
    }
}
