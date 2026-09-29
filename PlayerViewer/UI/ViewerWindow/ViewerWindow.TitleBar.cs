using System;
using System.Collections.Generic;
using System.Reflection;
using ImGuiNET;
using OpenTK;
using PlayerViewer.Core;
using Rect = PlayerViewer.UI.CustomTitleBar.Native.RECT;
using Vector2 = System.Numerics.Vector2;
using Vector4 = System.Numerics.Vector4;

namespace PlayerViewer.UI
{
    // The custom title bar on Windows: installing and toggling it, keeping OpenTK's size in step,
    // the window buttons, and the rects its hit test reads.
    public partial class ViewerWindow
    {
        //Null off Windows. Installed for every mode, since it also restores Alt+F4 and Alt+Space.
        CustomTitleBar _titleBar;

        //This frame's rects, handed to the title bar once the UI is drawn.
        List<Rect> _barItems = new();
        List<Rect> _barOverlays = new();
        Rect? _barMax;

        bool TitleBarOn => _titleBar is { Enabled: true };

        //Called from the constructor, while the window is still hidden, so the OS bar never shows.
        void InstallTitleBar()
        {
            _titleBar = CustomTitleBar.Install(
                WindowInfo.Handle,
                (int)SideOrderLayout.TitleRowHeight
            );
            if (_titleBar == null)
                return;
            _titleBar.ModalRender = RenderModalFrame;
            SetTitleBar(_config.Mode == InterfaceMode.SideOrderTitleBar);
        }

        /// <summary>Installs or removes the custom frame. Between frames only.</summary>
        void SetTitleBar(bool on)
        {
            if (_titleBar == null || _titleBar.Enabled == on)
                return;
            _titleBar.SetEnabled(on);
            SyncClientSize();
        }

        static readonly FieldInfo ImplementationField = typeof(NativeWindow).GetField(
            "implementation",
            BindingFlags.NonPublic | BindingFlags.Instance
        );

        /// <summary>
        /// OpenTK refreshes its cached client size only when the window size changes, and never
        /// for a maximised window, so a frame change is pushed into it here.
        /// </summary>
        void SyncClientSize()
        {
            object impl = ImplementationField?.GetValue(this);
            var field = impl
                ?.GetType()
                .GetField("client_rectangle", BindingFlags.NonPublic | BindingFlags.Instance);
            if (field == null)
                return;
            var (w, h) = _titleBar.ClientSize();
            field.SetValue(impl, new System.Drawing.Rectangle(0, 0, w, h));
            OnResize(EventArgs.Empty);
        }

        //A move, a live resize or an open system menu blocks the render loop; the title bar
        //draws the frames from OpenTK's timer instead.
        void RenderModalFrame()
        {
            if (!Exists || IsExiting || _titleBar.Iconic)
                return;
            OnRenderFrame(new FrameEventArgs());
        }

        /// <summary>The client height the OS bar would leave the same window, which is what is saved.</summary>
        int NativeClientHeight => TitleBarOn ? Height - _titleBar.FoldedHeight : Height;

        void BeginBarFrame()
        {
            _barItems.Clear();
            _barOverlays.Clear();
            _barMax = null;
        }

        void EndBarFrame()
        {
            if (_titleBar == null)
                return;
            (_titleBar.Interactive, _barItems) = (_barItems, _titleBar.Interactive);
            (_titleBar.Overlays, _barOverlays) = (_barOverlays, _titleBar.Overlays);
            _titleBar.MaxButton = _barMax;
        }

        static Rect ToRect(Vector2 min, Vector2 max) =>
            new()
            {
                left = (int)MathF.Floor(min.X),
                top = (int)MathF.Floor(min.Y),
                right = (int)MathF.Ceiling(max.X),
                bottom = (int)MathF.Ceiling(max.Y),
            };

        /// <summary>A widget in the bar, which takes clicks instead of dragging the window.</summary>
        void NoteBarItem(Vector2 min, Vector2 max)
        {
            if (TitleBarOn && min.Y < SideOrderLayout.TitleRowHeight)
                _barItems.Add(ToRect(min, max));
        }

        /// <summary>The current ImGui window, if it reaches up over the bar.</summary>
        void NoteBarWindow()
        {
            if (!TitleBarOn)
                return;
            var pos = ImGui.GetWindowPos();
            if (pos.Y < SideOrderLayout.TitleRowHeight)
                _barOverlays.Add(ToRect(pos, pos + ImGui.GetWindowSize()));
        }

        /// <summary>Lays out the window buttons at the top right and reserves their width in the row.</summary>
        void LayoutWindowButtons()
        {
            SideOrderLayout.WindowButtonsWidth = TitleBarOn
                ? SideOrderLayout.WindowButtonsRight
                    + 3 * SideOrderLayout.WindowButtonWidth
                    + 2 * SideOrderLayout.WindowButtonGap
                    + SideOrderLayout.WindowButtonsPathGap
                    - SideOrderLayout.RightEdgeGap
                : 0;
        }

        /// <summary>Minimise, maximise or restore, and close, in the host window after the menu row.</summary>
        void DrawWindowButtons(Vector2 winMin, Vector2 winMax)
        {
            if (!TitleBarOn)
                return;
            var dl = ImGui.GetWindowDrawList();
            const float w = SideOrderLayout.WindowButtonWidth;
            const float gap = SideOrderLayout.WindowButtonGap;
            float row = SideOrderLayout.TitleRowHeight;
            float right = winMax.X - SideOrderLayout.WindowButtonsRight;
            float cy = winMin.Y + MathF.Round(row / 2) + 1;
            bool zoomed = _titleBar.Zoomed;
            float ink = _titleBar.Active ? 1 : 0.55f;

            for (int i = 0; i < 3; i++)
            {
                float left = right - (3 - i) * w - (2 - i) * gap;
                var hitMin = new Vector2(left - gap / 2, winMin.Y);
                var hitMax = new Vector2(left + w + gap / 2, winMin.Y + row);
                //Maximised, the close button runs into the screen corner as a native one does.
                if (i == 2 && SideOrderLayout.Filled)
                    hitMax.X = winMax.X;
                ImGui.SetCursorScreenPos(hitMin);
                bool clicked = ImGui.InvisibleButton(
                    i switch
                    {
                        0 => "##winmin",
                        1 => "##winmax",
                        _ => "##winclose",
                    },
                    hitMax - hitMin
                );
                bool hot = ImGui.IsItemHovered();
                bool held = ImGui.IsItemActive();
                if (i == 1)
                    _barMax = ToRect(hitMin, hitMax);
                else
                    NoteBarItem(hitMin, hitMax);

                var centre = new Vector2(left + w / 2, cy);
                var half = new Vector2(w, SideOrderLayout.WindowButtonHeight) / 2;
                if (hot || held)
                    SideOrderControls.Pill(
                        dl,
                        centre - half,
                        centre + half,
                        new SideOrderControls.State(true, held, Danger: i == 2)
                    );
                var colour = i == 2 && (hot || held) ? new Vector4(1, 1, 1, 1) : Theme.TextMain;
                uint col = ImGui.GetColorU32(colour with { W = colour.W * ink });
                switch (i)
                {
                    case 0:
                        MinimiseGlyph(dl, centre, col);
                        if (clicked)
                            _titleBar.Command(CustomTitleBar.Native.SC_MINIMIZE);
                        break;
                    case 1:
                        //The click is the window procedure's, from HTMAXBUTTON; ImGui sees it too.
                        if (zoomed)
                            RestoreGlyph(dl, centre, col);
                        else
                            MaximiseGlyph(dl, centre, col);
                        break;
                    default:
                        CloseGlyph(dl, centre, col);
                        if (clicked)
                            _titleBar.Command(CustomTitleBar.Native.SC_CLOSE);
                        break;
                }
            }

            //Windows 10 draws no top border once the top frame is gone.
            if (Environment.OSVersion.Version.Build < 22000 && !zoomed)
                dl.AddRectFilled(
                    winMin,
                    new Vector2(winMax.X, winMin.Y + 1),
                    ImGui.GetColorU32(new Vector4(0.5f, 0.5f, 0.5f, 1))
                );
        }

        //The glyphs are bold, and drawn from filled rects where they are straight
        //so their edges land on whole pixels.
        static void MinimiseGlyph(ImDrawListPtr dl, Vector2 c, uint col)
        {
            c = Snap(c);
            dl.AddRectFilled(c + new Vector2(-6, -1), c + new Vector2(6, 2), col, 1);
        }

        static void MaximiseGlyph(ImDrawListPtr dl, Vector2 c, uint col)
        {
            c = Snap(c);
            Square(dl, c + new Vector2(-5, -5), c + new Vector2(6, 6), col);
        }

        static void RestoreGlyph(ImDrawListPtr dl, Vector2 c, uint col)
        {
            c = Snap(c);
            var min = c + new Vector2(-6, -3);
            var max = c + new Vector2(3, 6);
            Square(dl, min, max, col);
            //The window behind: its top and right edges, where the front one does not cover it.
            const float t = 2;
            dl.AddRectFilled(
                new Vector2(min.X + 3, min.Y - 3),
                new Vector2(max.X + 3, min.Y - 3 + t),
                col
            );
            dl.AddRectFilled(
                new Vector2(max.X + 3 - t, min.Y - 3),
                new Vector2(max.X + 3, max.Y - 3),
                col
            );
        }

        static void Square(ImDrawListPtr dl, Vector2 min, Vector2 max, uint col)
        {
            const float t = 2;
            dl.AddRectFilled(min, new Vector2(max.X, min.Y + t), col);
            dl.AddRectFilled(new Vector2(min.X, max.Y - t), max, col);
            dl.AddRectFilled(new Vector2(min.X, min.Y + t), new Vector2(min.X + t, max.Y - t), col);
            dl.AddRectFilled(new Vector2(max.X - t, min.Y + t), new Vector2(max.X, max.Y - t), col);
        }

        static void CloseGlyph(ImDrawListPtr dl, Vector2 c, uint col)
        {
            //AddLine offsets its ends by half a pixel itself, which centres it with the rect glyphs.
            c = Snap(c);
            const float r = 5.5f;
            dl.AddLine(c - new Vector2(r), c + new Vector2(r), col, 2.8f);
            dl.AddLine(c + new Vector2(-r, r), c + new Vector2(r, -r), col, 2.8f);
        }

        static Vector2 Snap(Vector2 v) => new(MathF.Round(v.X), MathF.Round(v.Y));
    }
}
