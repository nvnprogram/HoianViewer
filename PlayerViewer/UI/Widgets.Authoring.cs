using System;
using System.Numerics;
using ImGuiNET;

namespace PlayerViewer.UI
{
    // Helpers the physics authoring, the Cloth Editor and the Skeleton section share.
    public static partial class Widgets
    {
        /// <summary>
        /// A full width button that asks again before it acts: a press arms it for
        /// <paramref name="key"/>, and the red <paramref name="confirm"/> button then runs
        /// <paramref name="action"/>. Armed for another key, it shows <paramref name="label"/>.
        /// </summary>
        public static void ConfirmButton(
            string label,
            string confirm,
            object key,
            ref object pending,
            Action action
        )
        {
            if (!Equals(pending, key))
            {
                if (Button(label, new Vector2(-1, 0)))
                    pending = key;
                return;
            }
            bool pressed = false;
            RedButton(confirm, new Vector2(-1, 0), () => pressed = true);
            if (!pressed)
                return;
            pending = null;
            action();
        }

        /// <summary>
        /// Begins a modal centred on the main window, at most <paramref name="width"/> wide, with
        /// no close button. End it with <c>ImGui.EndPopup</c> when this returns true.
        /// </summary>
        public static bool BeginCenteredModal(string id, float width)
        {
            var viewport = ImGui.GetMainViewport();
            ImGui.SetNextWindowPos(
                viewport.Pos + viewport.Size / 2,
                ImGuiCond.Always,
                new Vector2(0.5f, 0.5f)
            );
            ImGui.SetNextWindowSize(new Vector2(Math.Min(width, viewport.Size.X - 40), 0));
            return BeginModal(id, ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoSavedSettings);
        }

        //ImGui.NET takes flags only with a p_open, and a p_open gives the modal a close button.
        static unsafe bool BeginModal(string id, ImGuiWindowFlags flags)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(id + "\0");
            fixed (byte* p = bytes)
                return ImGuiNative.igBeginPopupModal(p, null, flags) != 0;
        }

        /// <summary>
        /// A row's label, with the item after it placed at <paramref name="column"/> and, with
        /// <paramref name="fill"/>, running to the end of the line.
        /// </summary>
        public static void LabelRow(string label, float column, string tip = null, bool fill = true)
        {
            ImGui.AlignTextToFramePadding();
            Text(label);
            if (tip != null)
                ItemTooltip(tip);
            ImGui.SameLine(column);
            if (fill)
                ImGui.SetNextItemWidth(-1);
        }

        /// <summary>The width a button needs for its label in the current look.</summary>
        public static float ButtonWidth(string label) =>
            ImGui.CalcTextSize(Visible(label)).X
            + 2 * ImGui.GetStyle().FramePadding.X
            + (SideOrder ? 12 : 0);

        /// <summary>
        /// Draws a window's contents. A throw is logged under <paramref name="log"/> and shown in
        /// the window, and whatever the contents left open is closed, so the ImGui stacks stay
        /// balanced for the rest of the frame.
        /// </summary>
        public static void Guarded(string log, Action draw)
        {
            var context = ImGui.GetCurrentContext();
            var window = ImGui.GetCurrentWindow();
            int windows = context.CurrentWindowStack.Size,
                ids = window.IDStack.Size,
                styles = context.StyleVarStack.Size,
                colours = context.ColorStack.Size,
                fonts = context.FontStack.Size,
                lists = _listBottoms.Count,
                framed = _framedDepth,
                disabled = _disabled.Count,
                columns = ImGui.GetColumnsCount();
            try
            {
                draw();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{log}] {ex}");
                while (context.CurrentWindowStack.Size > windows)
                {
                    var flags = context.CurrentWindow.Flags;
                    if ((flags & ImGuiWindowFlags.Popup) != 0)
                        ImGui.EndPopup();
                    else if ((flags & ImGuiWindowFlags.ChildWindow) == 0)
                        ImGui.End();
                    else if (_listBottoms.Count > lists)
                        EndList();
                    else
                        ImGui.EndChild();
                }
                while (_framedDepth > framed)
                    EndFramed();
                while (_disabled.Count > disabled)
                    EndDisabled();
                if (columns == 1 && ImGui.GetColumnsCount() > 1)
                    ImGui.Columns(1);
                while (window.IDStack.Size > ids)
                    ImGui.PopID();
                if (context.StyleVarStack.Size > styles)
                    ImGui.PopStyleVar(context.StyleVarStack.Size - styles);
                if (context.ColorStack.Size > colours)
                    ImGui.PopStyleColor(context.ColorStack.Size - colours);
                while (context.FontStack.Size > fonts)
                    ImGui.PopFont();
                ErrorText(ex.Message);
            }
        }
    }
}
