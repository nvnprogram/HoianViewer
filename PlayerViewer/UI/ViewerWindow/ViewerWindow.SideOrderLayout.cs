using System;
using ImGuiNET;
using Vector2 = System.Numerics.Vector2;
using Vector4 = System.Numerics.Vector4;

namespace PlayerViewer.UI
{
    // The Side Order layout: the menu row on the floral background at the top of the window, and
    // the panels as cards on the same background: the left panel, the viewport, and the right
    // side stacked.
    public partial class ViewerWindow
    {
        //Last frame's heights of the lower right cards; the animation card takes the rest.
        float _sourceCardHeight = 80;
        float _captureCardHeight = 180;

        //The left card's right edge, which floating windows open clear of.
        float _leftCardRight;

        //The right cards' left edge, which floating windows open clear of too.
        float _rightCardLeft;

        void DrawSideOrderUI()
        {
            var viewport = ImGui.GetMainViewport();
            ImGui.SetNextWindowPos(viewport.Pos);
            ImGui.SetNextWindowSize(viewport.Size);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
            ImGui.Begin(
                "##host",
                ImGuiWindowFlags.NoTitleBar
                    | ImGuiWindowFlags.NoResize
                    | ImGuiWindowFlags.NoMove
                    | ImGuiWindowFlags.NoCollapse
                    | ImGuiWindowFlags.NoBringToFrontOnFocus
                    | ImGuiWindowFlags.NoNavFocus
                    | ImGuiWindowFlags.NoBackground
                    | ImGuiWindowFlags.NoScrollbar
                    | ImGuiWindowFlags.NoScrollWithMouse
            );
            ImGui.PopStyleVar();

            var host = ImGui.GetWindowDrawList();
            var winMin = viewport.Pos;
            var winMax = viewport.Pos + viewport.Size;
            DrawMenuRow(winMin, winMax);

            if (_scene == null)
            {
                ImGui.SetCursorPos(Vector2.Zero);
                DrawRomfsSetup();
                DrawSettingsWindow();
                ImGui.End();
                return;
            }

            const float gap = SideOrderLayout.Gap;
            float width = winMax.X - winMin.X;
            float top = winMin.Y + SideOrderLayout.CardsTop;
            float bottom = winMax.Y - SideOrderLayout.BottomGap;
            var leftMin = new Vector2(winMin.X + gap, top);
            var leftMax = new Vector2(leftMin.X + SideOrderLayout.LeftCardWidth(width), bottom);
            var rightMax = new Vector2(winMax.X - SideOrderLayout.RightEdgeGap, bottom);
            var rightMin = new Vector2(rightMax.X - SideOrderLayout.RightCardWidth(width), top);
            var centreMin = new Vector2(leftMax.X + gap, top);
            var centreMax = new Vector2(Math.Max(centreMin.X + 1, rightMin.X - gap), bottom);
            _leftCardRight = leftMax.X;
            _rightCardLeft = rightMin.X;

            Card("##left", leftMin, leftMax, 2, DrawLeftPanel);

            _pipeline.SelectedMaterial = _standalone != null ? _selectedMaterial : null;

            SideOrderSurface.Draw(
                host,
                centreMin,
                centreMax,
                SideOrderLayout.ViewportRounding,
                3,
                SurfaceKind.Viewport
            );
            ImGui.SetCursorScreenPos(centreMin);
            //The rounded image is the card; a child background would show at its corners.
            ImGui.PushStyleColor(ImGuiCol.ChildBg, Vector4.Zero);
            ImGui.BeginChild(
                "##center",
                centreMax - centreMin,
                false,
                ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse
            );
            ImGui.PopStyleColor();
            DrawViewport();
            ImGui.EndChild();

            if (_effect != null)
                Card("##right", rightMin, rightMax, 4, DrawEffectSidebar);
            else
                DrawRightCards(rightMin, rightMax);

            DrawFloatingWindows();
            ImGui.End();
        }

        //The menu bar of a child as tall as the bar, centred in the window's top row. The thin row
        //takes its frame padding in, so the bar is the row.
        void DrawMenuRow(Vector2 winMin, Vector2 winMax)
        {
            const float gap = SideOrderLayout.Gap;
            bool thin = !SideOrderLayout.TitleBar;
            if (thin)
                ImGui.PushStyleVar(
                    ImGuiStyleVar.FramePadding,
                    new Vector2(
                        ImGui.GetStyle().FramePadding.X,
                        (SideOrderLayout.ThinRowHeight - ImGui.GetTextLineHeight()) / 2
                    )
                );
            float height = ImGui.GetFrameHeight();
            ImGui.SetCursorScreenPos(
                new Vector2(
                    winMin.X + gap,
                    winMin.Y + (thin ? 0 : MathF.Round((SideOrderLayout.MenuRow - height) / 2) + 1)
                )
            );
            ImGui.PushStyleColor(ImGuiCol.ChildBg, Vector4.Zero);
            ImGui.PushStyleColor(ImGuiCol.MenuBarBg, Vector4.Zero);
            //A rounded child clips its menu bar short of its corner, which would cut the path.
            ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 0);
            LayoutWindowButtons();
            ImGui.BeginChild(
                "##menurow",
                new Vector2(
                    winMax.X - winMin.X - 2 * gap - SideOrderLayout.WindowButtonsWidth,
                    height
                ),
                false,
                ImGuiWindowFlags.MenuBar
                    | ImGuiWindowFlags.NoScrollbar
                    | ImGuiWindowFlags.NoScrollWithMouse
                    | ImGuiWindowFlags.NoBackground
            );
            ImGui.PopStyleVar();
            ImGui.PopStyleColor(2);
            DrawMenuBar();
            ImGui.EndChild();
            if (thin)
                ImGui.PopStyleVar();
            DrawWindowButtons(winMin, winMax);
        }

        //The animation card's height with its list at the least it is given, as of last frame.
        float _animCardMin = 250;

        const float AnimListMin = 60;

        /// <summary>
        /// The animation card on top and the source and capture cards under it, each as tall as
        /// its content. When the column is too short for all of it, the animation card keeps its
        /// minimum and the lower cards shrink and scroll.
        /// </summary>
        void DrawRightCards(Vector2 min, Vector2 max)
        {
            const float gap = SideOrderLayout.RightStackGap;
            const float floor = 2 * SideOrderLayout.CardRounding;
            float avail = max.Y - min.Y - 2 * gap;
            float animMin = Math.Min(_animCardMin, Math.Max(avail - 2 * floor, 0));
            float room = Math.Max(avail - animMin, 0);
            float source = _sourceCardHeight;
            float capture = _captureCardHeight;
            if (source + capture > room)
            {
                source = Math.Max(
                    MathF.Floor(room * source / (source + capture)),
                    Math.Min(floor, room / 2)
                );
                capture = room - source;
            }
            float captureTop = max.Y - capture;
            float sourceTop = Math.Max(captureTop - gap - source, min.Y);
            var style = ImGui.GetStyle();
            var inset = SideOrderLayout.ScrollInset;
            Card(
                "##right",
                min,
                new Vector2(max.X, Math.Max(sourceTop - gap, min.Y)),
                4,
                () =>
                {
                    DrawPlaybackControls();
                    _animCardMin = MathF.Ceiling(
                        ImGui.GetCursorPosY() + AnimListMin + style.WindowPadding.Y + inset.Y
                    );
                    //The card's child pads less than the style says, by the scroll inset.
                    DrawAnimList(Math.Max(VisibleHeightBelowCursor() + inset.Y, AnimListMin));
                }
            );
            _sourceCardHeight = Card(
                "##source",
                new Vector2(min.X, sourceTop),
                new Vector2(max.X, captureTop - gap),
                5,
                () =>
                {
                    DrawModeTabs();
                    if (_animMode == 1)
                        DrawSequencePanel();
                }
            );
            _captureCardHeight = Card(
                "##capture",
                new Vector2(min.X, captureTop),
                max,
                6,
                DrawCapturePanel
            );
        }

        /// <summary>
        /// ImGui.Begin for a floating window. Side Order titles it in the header font, opens it
        /// clear of the left card and gives it a card's shadow and rim.
        /// </summary>
        bool BeginCardWindow(
            string name,
            ref bool open,
            ImGuiWindowFlags flags = ImGuiWindowFlags.None
        )
        {
            //No font is pushed around Begin: PopFont would pop the texture off the new window's
            //draw list, not the one it was pushed on, and the window would draw untextured.
            int hidden = Widgets.HideGrab();
            bool visible = ImGui.Begin(name, ref open, flags);
            ImGui.PopStyleColor(hidden);
            NoteBarWindow();
            if (!visible || !SideOrderControls.On)
                return visible;
            var pos = ImGui.GetWindowPos();
            if (
                ImGui.IsWindowAppearing()
                && _leftCardRight > 0
                && pos.X < _leftCardRight + SideOrderLayout.Gap
            )
            {
                pos = new Vector2(
                    _leftCardRight + 2 * SideOrderLayout.Gap,
                    Math.Max(
                        pos.Y,
                        ImGui.GetMainViewport().Pos.Y
                            + SideOrderLayout.CardsTop
                            + SideOrderLayout.Gap
                    )
                );
                ImGui.SetWindowPos(pos);
            }
            //Placed for the classic layout's narrower sidebar, it can cover the right cards.
            float right = pos.X + ImGui.GetWindowWidth();
            if (
                ImGui.IsWindowAppearing()
                && _rightCardLeft > 0
                && right > _rightCardLeft - SideOrderLayout.Gap
            )
            {
                pos.X = Math.Max(
                    _leftCardRight + 2 * SideOrderLayout.Gap,
                    _rightCardLeft - 2 * SideOrderLayout.Gap - ImGui.GetWindowWidth()
                );
                ImGui.SetWindowPos(pos);
            }
            var viewport = ImGui.GetMainViewport();
            float bottom = viewport.Pos.Y + viewport.Size.Y - SideOrderLayout.BottomGap;
            if (ImGui.IsWindowAppearing() && pos.Y + ImGui.GetWindowHeight() > bottom)
            {
                pos.Y = Math.Max(
                    viewport.Pos.Y + SideOrderLayout.CardsTop + SideOrderLayout.Gap,
                    bottom - ImGui.GetWindowHeight()
                );
                ImGui.SetWindowPos(pos);
            }
            SideOrderSurface.DrawWindowDecor(
                ImGui.GetWindowDrawList(),
                pos,
                pos + ImGui.GetWindowSize(),
                ImGui.GetStyle().WindowRounding
            );
            Widgets.DecorateScrollbar((flags & ImGuiWindowFlags.NoTitleBar) == 0);
            return true;
        }

        /// <summary>
        /// A card and the scrolling child inside it, inset from the card's rounded corners so its
        /// scrollbar stays clear of them. Returns the card height its content asked for.
        /// </summary>
        float Card(string id, Vector2 min, Vector2 max, int seed, Action draw)
        {
            SideOrderSurface.Draw(
                ImGui.GetWindowDrawList(),
                min,
                max,
                SideOrderLayout.CardRounding,
                seed,
                SurfaceKind.Card
            );
            var style = ImGui.GetStyle();
            var inset = SideOrderLayout.ScrollInset;
            //The child's padding makes up the inset, so the content sits where the style puts it.
            var padding = new Vector2(style.WindowPadding.X, style.WindowPadding.Y - inset.Y);
            ImGui.SetCursorScreenPos(min + new Vector2(0, inset.Y));
            ImGui.PushStyleColor(ImGuiCol.ChildBg, Vector4.Zero);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, padding);
            int hidden = Widgets.HideGrab();
            ImGui.BeginChild(
                id,
                Vector2.Max(max - min - new Vector2(inset.X, 2 * inset.Y), Vector2.One),
                false,
                ImGuiWindowFlags.AlwaysUseWindowPadding
            );
            ImGui.PopStyleColor(hidden);
            ImGui.PopStyleVar();
            ImGui.PopStyleColor();
            Widgets.DecorateScrollbar();
            draw();
            float wanted = ImGui.GetCursorPosY() - style.ItemSpacing.Y + padding.Y + 2 * inset.Y;
            ImGui.EndChild();
            return MathF.Ceiling(wanted);
        }
    }
}
