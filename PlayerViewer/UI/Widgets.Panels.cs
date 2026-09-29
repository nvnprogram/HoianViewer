using System;
using ImGuiNET;

namespace PlayerViewer.UI
{
    public static partial class Widgets
    {
        /// <summary>A card's shadow and rim round the current popup in Side Order.</summary>
        public static void DecoratePopup()
        {
            if (SideOrder)
                PopupDecor(ImGui.GetStyle().PopupRounding);
        }

        /// <summary>
        /// Arrow keys over a grid of <paramref name="columns"/> filled row by row. Up and down move
        /// a row; left and right a cell when <paramref name="horizontal"/>. Returns the cell to
        /// move to, or -1 when nothing moves.
        /// </summary>
        public static int GridNav(int count, int current, int columns, bool horizontal)
        {
            if (count == 0)
                return -1;
            int step = 0;
            if (ImGui.IsKeyPressed(ImGui.GetKeyIndex(ImGuiKey.DownArrow)))
                step += columns;
            if (ImGui.IsKeyPressed(ImGui.GetKeyIndex(ImGuiKey.UpArrow)))
                step -= columns;
            if (horizontal && ImGui.IsKeyPressed(ImGui.GetKeyIndex(ImGuiKey.RightArrow)))
                step++;
            if (horizontal && ImGui.IsKeyPressed(ImGui.GetKeyIndex(ImGuiKey.LeftArrow)))
                step--;
            if (step == 0)
                return -1;
            if (current < 0)
                return 0;
            int next = current + step;
            //A step down from above the last row lands on the last cell rather than nowhere.
            if (next >= count)
                next = step > 0 && current / columns < (count - 1) / columns ? count - 1 : current;
            next = Math.Clamp(next, 0, count - 1);
            return next == current ? -1 : next;
        }
    }
}
