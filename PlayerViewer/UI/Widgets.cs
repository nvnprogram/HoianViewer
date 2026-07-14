using System;
using System.Collections.Generic;
using System.Numerics;
using ImGuiNET;
using PlayerViewer.Core;

namespace PlayerViewer.UI
{
    /// <summary>
    /// Reusable themed widgets: searchable gear combos, section headers, labeled rows,
    /// and bound controls.
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

        /// <summary>Label + combo on one line with fixed label column.</summary>
        public static void LabeledRow(string label, Action drawControl)
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(Theme.TextDim, label);
            ImGui.SameLine(92);
            drawControl();
        }

        /// <summary>
        /// Searchable combo for a gear list. Returns true when the selection changed
        /// (selected receives the new entry, null = none).
        /// </summary>
        public static bool GearCombo(
            string label,
            List<GearEntry> entries,
            GearEntry current,
            out GearEntry selected,
            bool allowNone = true,
            string noneLabel = "Blank"
        )
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
                        if (
                            !MatchesSearch(entry.DisplayName, search)
                            && !MatchesSearch(entry.Label ?? "", search)
                        )
                            continue;

                        if (entry.IsCustom)
                            ImGui.PushStyleColor(ImGuiCol.Text, Theme.GoldBright);
                        bool isSelected = entry == current;
                        if (
                            ImGui.Selectable(
                                $"{entry.DisplayName}##{entries.IndexOf(entry)}",
                                isSelected
                            )
                        )
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
            return string.IsNullOrEmpty(search)
                || text.Contains(search, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Full-width button that dims and no-ops when disabled.</summary>
        public static void DisabledButton(string label, bool enabled, Action onClick)
        {
            if (!enabled)
                ImGui.PushStyleVar(ImGuiStyleVar.Alpha, 0.45f);
            if (ImGui.Button(label, new Vector2(-1, 0)) && enabled)
                onClick();
            if (!enabled)
                ImGui.PopStyleVar();
        }

        /// <summary>Full-width red (destructive/cancel) button.</summary>
        public static void RedButton(string label, Action onClick)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, Theme.RedButtonBg);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Theme.RedButtonHover);
            if (ImGui.Button(label, new Vector2(-1, 0)))
                onClick();
            ImGui.PopStyleColor(2);
        }

        /// <summary>Muted caption/status text.</summary>
        public static void DimText(string text) => ImGui.TextColored(Theme.TextDim, text);

        /// <summary>Red error/warning text.</summary>
        public static void ErrorText(string text) => ImGui.TextColored(Theme.Error, text);

        /// <summary>Green success/active text.</summary>
        public static void SuccessText(string text) => ImGui.TextColored(Theme.Success, text);

        /// <summary>Tooltip shown when the last-drawn item is hovered.</summary>
        public static void ItemTooltip(string text)
        {
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(text);
        }

        //Read a value, draw the control, and on edit push the new value through <paramref name="set"/> then
        //run <paramref name="onChanged"/> (persist/side effects). Each returns true when the value changed.

        public static bool Checkbox(
            string label,
            bool value,
            Action<bool> set,
            Action onChanged = null
        )
        {
            bool v = value;
            if (!ImGui.Checkbox(label, ref v))
                return false;
            set(v);
            onChanged?.Invoke();
            return true;
        }

        public static bool SliderInt(
            string label,
            int value,
            int min,
            int max,
            Action<int> set,
            Action onChanged = null,
            string format = "%d"
        )
        {
            int v = value;
            if (!ImGui.SliderInt(label, ref v, min, max, format))
                return false;
            set(v);
            onChanged?.Invoke();
            return true;
        }

        public static bool SliderFloat(
            string label,
            float value,
            float min,
            float max,
            Action<float> set,
            Action onChanged = null,
            string format = "%.2f"
        )
        {
            float v = value;
            if (!ImGui.SliderFloat(label, ref v, min, max, format))
                return false;
            set(v);
            onChanged?.Invoke();
            return true;
        }

        public static bool InputInt(
            string label,
            int value,
            Action<int> set,
            Action onChanged = null
        )
        {
            int v = value;
            if (!ImGui.InputInt(label, ref v))
                return false;
            set(v);
            onChanged?.Invoke();
            return true;
        }

        public static bool Combo(
            string label,
            int value,
            string[] items,
            Action<int> set,
            Action onChanged = null
        )
        {
            int v = value;
            if (!ImGui.Combo(label, ref v, items, items.Length))
                return false;
            set(v);
            onChanged?.Invoke();
            return true;
        }

        public static bool ColorEdit3(
            string label,
            Vector3 value,
            Action<Vector3> set,
            ImGuiColorEditFlags flags = ImGuiColorEditFlags.None,
            Action onChanged = null
        )
        {
            Vector3 v = value;
            if (!ImGui.ColorEdit3(label, ref v, flags))
                return false;
            set(v);
            onChanged?.Invoke();
            return true;
        }
    }
}
