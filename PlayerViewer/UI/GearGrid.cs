using System;
using System.Collections.Generic;
using System.Numerics;
using ImGuiNET;
using PlayerViewer.Core;
using PlayerViewer.Icons;

namespace PlayerViewer.UI
{
    /// <summary>
    /// A gear picker that opens a grid of tiles beside the panel instead of a dropdown: a
    /// search bar over tiles of icon and name. It follows the list protocol of the combos: an
    /// arrow step applies the pick and leaves the grid up, a click picks and closes.
    /// </summary>
    public static class GearGrid
    {
        const float OpenSeconds = 0.14f;
        const float OpenSlide = 14;
        const float TileWidth = 96;
        const float IconSide = 64;
        const float Spacing = 6;
        const float Columns = 6;

        //Width to spare so rounding never drops the last column.
        const float FitSlack = 4;

        //The grid's tallest, how far above its row it opens, and its least gap to the window's edges.
        const float MaxHeight = 620;
        const float Rise = 50;
        const float EdgeMargin = 30;

        static readonly Dictionary<string, string> _searches = new(StringComparer.Ordinal);
        static readonly Dictionary<(string, float, bool), (string, string)> _lines = new();
        static double _openedAt;
        static bool _scrollToCurrent;
        static bool _navScroll;
        static int _columns = 1;

        /// <summary>
        /// The closed picker as a combo sized to the rest of the row, and the grid when it is
        /// open. The grid opens at <paramref name="anchorX"/>, level with the row where it fits.
        /// Returns true when the selection changed.
        /// </summary>
        public static bool Draw(
            string id,
            IReadOnlyList<GearEntry> entries,
            GearEntry current,
            out GearEntry selected,
            bool allowNone,
            string noneLabel,
            IconCache icons,
            float anchorX
        )
        {
            selected = current;
            string popupId = "##grid" + id;
            bool isOpen = ImGui.IsPopupOpen(popupId);

            var min = ImGui.GetCursorScreenPos();
            var size = new Vector2(ImGui.GetContentRegionAvail().X, ImGui.GetFrameHeight());
            if (ImGui.InvisibleButton("##gridbtn" + id, size))
            {
                ImGui.OpenPopup(popupId);
                _openedAt = ImGui.GetTime();
                _scrollToCurrent = true;
                _searches[id] = "";
                isOpen = true;
            }
            DrawClosed(min, size, ImGui.IsItemHovered() || isOpen, current, noneLabel, icons);

            if (!isOpen)
                return false;

            float t = (float)Math.Clamp((ImGui.GetTime() - _openedAt) / OpenSeconds, 0, 1);
            float ease = 1 - (1 - t) * (1 - t) * (1 - t);

            var viewport = ImGui.GetMainViewport();
            var style = ImGui.GetStyle();
            float width =
                Columns * TileWidth
                + (Columns - 1) * Spacing
                + style.WindowPadding.X * 2
                + style.ScrollbarSize
                + FitSlack;
            float height = MathF.Min(MaxHeight, viewport.Size.Y - 2 * EdgeMargin);
            float top = Math.Clamp(
                min.Y - Rise,
                viewport.Pos.Y + EdgeMargin,
                viewport.Pos.Y + viewport.Size.Y - height - EdgeMargin
            );
            ImGui.SetNextWindowPos(new Vector2(anchorX - (1 - ease) * OpenSlide, top));
            ImGui.SetNextWindowSize(new Vector2(width, height));
            ImGui.PushStyleVar(ImGuiStyleVar.Alpha, MathF.Max(ease, 0.01f));
            bool changed = false;
            if (
                ImGui.BeginPopup(
                    popupId,
                    ImGuiWindowFlags.NoMove
                        | ImGuiWindowFlags.NoResize
                        | ImGuiWindowFlags.NoSavedSettings
                        | ImGuiWindowFlags.NoScrollbar
                )
            )
            {
                Widgets.DecoratePopup();
                changed = DrawGrid(id, entries, current, ref selected, allowNone, noneLabel, icons);
                ImGui.EndPopup();
            }
            ImGui.PopStyleVar();
            return changed;
        }

        //Looks like the other combos on the panel so the grid rows read as the same control.
        static void DrawClosed(
            Vector2 min,
            Vector2 size,
            bool hot,
            GearEntry current,
            string noneLabel,
            IconCache icons
        )
        {
            var dl = ImGui.GetWindowDrawList();
            var style = ImGui.GetStyle();
            var max = min + size;
            if (SideOrderControls.On)
            {
                SideOrderControls.Pill(
                    dl,
                    min,
                    max,
                    new SideOrderControls.State(hot, ImGui.IsItemActive())
                );
                SideOrderControls.FrameChevron(dl, min, max);
                DrawClosedPreview(dl, min, size, current, noneLabel, icons);
                return;
            }
            float arrowW = size.Y;
            dl.AddRectFilled(
                min,
                max,
                ImGui.GetColorU32(hot ? ImGuiCol.FrameBgHovered : ImGuiCol.FrameBg),
                style.FrameRounding
            );
            dl.AddRectFilled(
                new Vector2(max.X - arrowW, min.Y),
                max,
                ImGui.GetColorU32(hot ? ImGuiCol.ButtonHovered : ImGuiCol.Button),
                style.FrameRounding,
                Widgets.Corners(ImDrawCornerFlags.Right)
            );
            //The same arrow ImGui draws on a combo, at the same place.
            float h = ImGui.GetFontSize();
            float r = h * 0.40f;
            var c = new Vector2(
                max.X - arrowW + style.FramePadding.Y + h * 0.5f,
                min.Y + style.FramePadding.Y + h * 0.5f
            );
            dl.AddTriangleFilled(
                c + new Vector2(0, 0.75f * r),
                c + new Vector2(-0.866f * r, -0.75f * r),
                c + new Vector2(0.866f * r, -0.75f * r),
                ImGui.GetColorU32(ImGuiCol.Text)
            );
            DrawClosedPreview(dl, min, size, current, noneLabel, icons);
        }

        static void DrawClosedPreview(
            ImDrawListPtr dl,
            Vector2 min,
            Vector2 size,
            GearEntry current,
            string noneLabel,
            IconCache icons
        )
        {
            Widgets.IconPreview(
                dl,
                min,
                size,
                icons,
                icons != null && current != null ? IconSource.KeyFor(current) : null,
                current?.DisplayName ?? noneLabel,
                true
            );
            if (
                ImGui.IsItemHovered()
                && current != null
                && !ImGui.IsPopupOpen("", ImGuiPopupFlags.AnyPopupId)
            )
                Widgets.GearTooltip(current);
        }

        static bool DrawGrid(
            string id,
            IReadOnlyList<GearEntry> entries,
            GearEntry current,
            ref GearEntry selected,
            bool allowNone,
            string noneLabel,
            IconCache icons
        )
        {
            bool changed = false;
            bool close = false;

            //Top bar: the search takes the keyboard as the grid appears, so typing filters at once.
            string search = _searches.GetValueOrDefault(id, "");
            float closeW = ImGui.GetFrameHeight();
            //Room for the count at its longest, so the search box does not change width as it filters.
            float countW = ImGui.CalcTextSize($"{entries.Count + 1} items").X;
            float spacing = ImGui.GetStyle().ItemSpacing.X;
            if (ImGui.IsWindowAppearing())
                ImGui.SetKeyboardFocusHere();
            if (
                Widgets.SearchBox(
                    "##gridsearch",
                    ref search,
                    "Search name or codename",
                    ImGui.GetContentRegionAvail().X - closeW - countW - 2 * spacing
                )
            )
            {
                _searches[id] = search;
                _scrollToCurrent = true;
            }

            var rows = new List<GearEntry>();
            if (allowNone && Widgets.Matches(noneLabel, search))
                rows.Add(null);
            foreach (var entry in entries)
                if (Widgets.MatchesGear(entry, search))
                    rows.Add(entry);

            ImGui.SameLine();
            ImGui.AlignTextToFramePadding();
            Widgets.DimText(rows.Count == 1 ? "1 item" : $"{rows.Count} items");
            ImGui.SameLine(ImGui.GetContentRegionMax().X - closeW);
            if (Widgets.Button("X##gridclose", new Vector2(closeW, 0)))
                close = true;
            Widgets.ItemTooltip("Close (Esc)");

            ImGui.Spacing();
            if (Widgets.BeginScrollChild("##tiles", Vector2.Zero))
            {
                int picked = DrawTiles(rows, current, noneLabel, icons, out bool clicked);
                if (picked >= 0)
                {
                    selected = rows[picked];
                    changed = selected != current;
                    close |= clicked;
                }
            }
            ImGui.EndChild();

            if (ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows))
            {
                int at = rows.IndexOf(current);
                //Left and right belong to the search box while it holds text.
                int move = Widgets.GridNav(rows.Count, at, _columns, string.IsNullOrEmpty(search));
                if (move >= 0 && rows[move] != current)
                {
                    selected = rows[move];
                    changed = true;
                    _navScroll = true;
                }
                if (ImGui.IsKeyPressed(ImGui.GetKeyIndex(ImGuiKey.Enter)))
                {
                    //With the current pick filtered away, Enter takes the first match.
                    if (at < 0 && rows.Count > 0)
                    {
                        selected = rows[0];
                        changed = selected != current;
                    }
                    close = true;
                }
                if (ImGui.IsKeyPressed(ImGui.GetKeyIndex(ImGuiKey.Escape)))
                    close = true;
            }

            if (close)
                ImGui.CloseCurrentPopup();
            return changed;
        }

        //Only the rows in view are drawn; the rest of the scroll height is left to a spacer.
        static int DrawTiles(
            List<GearEntry> rows,
            GearEntry current,
            string noneLabel,
            IconCache icons,
            out bool clicked
        )
        {
            clicked = false;
            int picked = -1;
            float avail = ImGui.GetContentRegionAvail().X;
            int columns = Math.Max(1, (int)((avail + Spacing) / (TileWidth + Spacing)));
            _columns = columns;
            float tileW = (avail - Spacing * (columns - 1)) / columns;
            float textH = ImGui.GetTextLineHeight();
            float tileH = (icons != null ? IconSide + 8 : 4) + textH * 2 + 8;
            float rowH = tileH + Spacing;
            int rowCount = (rows.Count + columns - 1) / columns;

            float scroll = ImGui.GetScrollY();
            float view = ImGui.GetWindowHeight();
            int currentIndex = rows.IndexOf(current);
            if (currentIndex >= 0 && (_scrollToCurrent || _navScroll))
            {
                float rowTop = currentIndex / columns * rowH;
                if (_scrollToCurrent)
                    ImGui.SetScrollY(MathF.Max(0, rowTop - (view - rowH) * 0.5f));
                else if (rowTop < scroll)
                    ImGui.SetScrollY(rowTop);
                else if (rowTop + tileH > scroll + view)
                    ImGui.SetScrollY(rowTop + tileH - view);
            }
            _scrollToCurrent = false;
            _navScroll = false;

            var origin = ImGui.GetCursorScreenPos();
            var dl = ImGui.GetWindowDrawList();
            int first = Math.Max(0, (int)(scroll / rowH) - 1);
            int last = Math.Min(rowCount - 1, (int)((scroll + view) / rowH) + 1);
            for (int r = first; r <= last; r++)
            for (int c = 0; c < columns; c++)
            {
                int i = r * columns + c;
                if (i >= rows.Count)
                    break;
                var entry = rows[i];
                var min = origin + new Vector2(c * (tileW + Spacing), r * rowH);
                ImGui.SetCursorScreenPos(min);
                if (ImGui.InvisibleButton($"##tile{i}", new Vector2(tileW, tileH)))
                {
                    picked = i;
                    clicked = true;
                }
                bool hovered = ImGui.IsItemHovered();
                bool isCurrent = entry == current;
                DrawTile(
                    dl,
                    min,
                    new Vector2(tileW, tileH),
                    entry,
                    noneLabel,
                    icons,
                    hovered,
                    isCurrent
                );
                if (hovered)
                {
                    if (entry != null)
                        Widgets.GearTooltip(entry);
                    else
                        Widgets.PlainTooltip(noneLabel);
                }
            }
            ImGui.SetCursorScreenPos(
                origin + new Vector2(0, MathF.Max(0, rowCount * rowH - Spacing))
            );
            ImGui.Dummy(new Vector2(1, 1));
            return picked;
        }

        static void DrawTile(
            ImDrawListPtr dl,
            Vector2 min,
            Vector2 size,
            GearEntry entry,
            string noneLabel,
            IconCache icons,
            bool hovered,
            bool isCurrent
        )
        {
            var style = ImGui.GetStyle();
            var max = min + size;
            var bg =
                isCurrent ? ImGuiCol.Header
                : hovered ? ImGuiCol.HeaderHovered
                : ImGuiCol.FrameBg;
            dl.AddRectFilled(min, max, ImGui.GetColorU32(bg), style.FrameRounding);
            if (isCurrent)
                dl.AddRect(
                    min,
                    max,
                    ImGui.GetColorU32(Theme.Gold),
                    style.FrameRounding,
                    ImDrawCornerFlags.All,
                    2
                );

            float textTop = min.Y + 4;
            if (icons != null)
            {
                var box = new Vector2(min.X + (size.X - IconSide) * 0.5f, min.Y + 6);
                string key = entry != null ? IconSource.KeyFor(entry) : null;
                if (key != null && icons.TryGet(key, out var icon))
                {
                    var (a, b) = IconCache.Fit(icon, box, new Vector2(IconSide, IconSide));
                    dl.AddImage(icon.Id, a, b);
                }
                else if (entry != null && icons.IsMissing(key))
                    dl.AddRect(
                        box + new Vector2(IconSide * 0.2f, IconSide * 0.2f),
                        box + new Vector2(IconSide * 0.8f, IconSide * 0.8f),
                        ImGui.GetColorU32(ImGuiCol.Border),
                        6,
                        ImDrawCornerFlags.All,
                        1.5f
                    );
                else if (entry == null)
                {
                    var centre = box + new Vector2(IconSide, IconSide) * 0.5f;
                    dl.AddCircle(
                        centre,
                        IconSide * 0.3f,
                        ImGui.GetColorU32(ImGuiCol.TextDisabled),
                        32,
                        2
                    );
                    dl.AddLine(
                        centre + new Vector2(-IconSide * 0.21f, IconSide * 0.21f),
                        centre + new Vector2(IconSide * 0.21f, -IconSide * 0.21f),
                        ImGui.GetColorU32(ImGuiCol.TextDisabled),
                        2
                    );
                }
                textTop = min.Y + IconSide + 10;
            }

            string name = entry?.DisplayName ?? noneLabel;
            float textW = size.X - 8;
            bool pushed = Widgets.PushNameFont(true);
            var (line1, line2) = TwoLines(name, textW, pushed);
            uint color = ImGui.GetColorU32(
                entry != null && entry.IsCustom
                    ? Theme.GoldBright
                    : style.Colors[(int)ImGuiCol.Text]
            );
            float lineH = ImGui.GetTextLineHeight();
            DrawCentered(dl, line1, min.X, size.X, textTop, color);
            if (line2 != null)
                DrawCentered(dl, line2, min.X, size.X, textTop + lineH, color);
            if (pushed)
                ImGui.PopFont();
        }

        static void DrawCentered(
            ImDrawListPtr dl,
            string text,
            float x,
            float width,
            float y,
            uint color
        )
        {
            float w = ImGui.CalcTextSize(text).X;
            Widgets.DrawText(dl, new Vector2(x + MathF.Max(4, (width - w) * 0.5f), y), color, text);
        }

        /// <summary>
        /// A name split over two lines at a space where it can be, else at the last character
        /// that fits, with the second line cut short behind an ellipsis when the rest is longer.
        /// </summary>
        static (string, string) TwoLines(string text, float width, bool gameFont)
        {
            if (_lines.TryGetValue((text, width, gameFont), out var cached))
                return cached;
            (string, string) result;
            if (ImGui.CalcTextSize(text).X <= width)
                result = (text, null);
            else
            {
                int fit = 1;
                while (
                    fit < text.Length && ImGui.CalcTextSize(text.Substring(0, fit + 1)).X <= width
                )
                    fit++;
                int space = text.LastIndexOf(' ', Math.Min(fit, text.Length - 1));
                int split = space > 0 ? space : fit;
                string rest = text.Substring(split).TrimStart();
                result = (text.Substring(0, split).TrimEnd(), Ellipsize(rest, width));
            }
            if (_lines.Count > 4096)
                _lines.Clear();
            _lines[(text, width, gameFont)] = result;
            return result;
        }

        static string Ellipsize(string text, float width)
        {
            if (ImGui.CalcTextSize(text).X <= width)
                return text;
            int n = text.Length;
            while (n > 1 && ImGui.CalcTextSize(text.Substring(0, n) + "...").X > width)
                n--;
            return text.Substring(0, n).TrimEnd() + "...";
        }
    }
}
