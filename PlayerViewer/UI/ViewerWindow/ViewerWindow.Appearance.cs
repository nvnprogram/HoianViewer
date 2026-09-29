using System;
using System.Collections.Generic;
using CafeStudio.UI;
using ImGuiNET;
using PlayerViewer.Core;

namespace PlayerViewer.UI
{
    // The interface look: classic or Side Order, the Side Order theme, and switching between
    // them live. Style and fonts both change between frames, never inside one.
    public partial class ViewerWindow
    {
        //The look in effect, which trails the config by a frame after a change.
        InterfaceMode _uiMode;
        bool _appearanceDirty;

        //ImGui's style as created, which every look starts from.
        ImGuiStyle _defaultStyle;

        //The look being set up, which the style, fonts and assets follow. Drawing code reads
        //SideOrderControls.On, which is true once it is.
        bool SideOrderMode => _uiMode != InterfaceMode.Classic;

        SideOrderBackground _background;

        /// <summary>Per frame, before the UI is built: the clock, the layout state and the textures.</summary>
        void UpdateSideOrder(float seconds)
        {
            if (!SideOrderMode)
                return;
            SideOrderLayout.Tick(seconds);
            SideOrderLayout.TitleBar = TitleBarOn;
            bool filled =
                _titleBar?.FillsScreen()
                ?? (
                    WindowState == OpenTK.WindowState.Maximized
                    || WindowState == OpenTK.WindowState.Fullscreen
                );
            TestHookOverride("filled", ref filled);
            SideOrderLayout.Filled = filled;
            SideOrderAssets.Load();
        }

        /// <summary>The floral background, into the default framebuffer before ImGui draws.</summary>
        void DrawSideOrderBackground()
        {
            bool floral = true;
            TestHookOverride("floral", ref floral);
            if (!SideOrderControls.On || !floral || SideOrderTheme.Current is not { } theme)
                return;
            using var _ = FramePerf.Section("background");
            (_background ??= new SideOrderBackground()).Draw(theme, Width, Height);
        }

        /// <summary>Creates the ImGui controller with the configured look's fonts and style.</summary>
        unsafe void InitAppearance()
        {
            _uiMode = _config.Mode;
            PrepareFonts();
            InstallPendingFonts();
            _imgui = new ImGuiController(Width, Height);
            //The controller's first frame has begun with no default font set, so the current
            //font is the main one.
            var main = ImGui.GetFont();
            _mainAscent = main.Ascent;
            _mainLineHeight = main.FontSize;
            _fonts?.AfterBuild();
            _defaultStyle = *ImGui.GetStyle().NativePtr;
            ApplyStyle();
        }

        unsafe void ApplyStyle()
        {
            *ImGui.GetStyle().NativePtr = _defaultStyle;
            if (SideOrderMode)
                SideOrderTheme.For(_config.Theme).Apply();
            else
            {
                SideOrderTheme.Clear();
                Theme.Apply();
            }
            ApplyWindowBorder();
        }

        void ApplyWindowBorder()
        {
            if (_titleBar == null)
                return;
            var border = SideOrderMode ? _config.Border : WindowOutline.System;
            if (border == WindowOutline.System)
            {
                _titleBar.SetBorderColour(CustomTitleBar.Native.DWMWA_COLOR_DEFAULT);
                return;
            }
            //The colour the background averages to along the window's edge, so the border
            //reads as the background's own.
            SideOrderAssets.Load();
            var edge = SideOrderBackground.EdgeMean(
                SideOrderTheme.For(_config.Theme),
                Width,
                Height
            );
            _titleBar.SetBorderColour(ColorRef(edge));
        }

        static uint ColorRef(System.Numerics.Vector4 c) =>
            (uint)Math.Round(Math.Clamp(c.X, 0, 1) * 255)
            | (uint)Math.Round(Math.Clamp(c.Y, 0, 1) * 255) << 8
            | (uint)Math.Round(Math.Clamp(c.Z, 0, 1) * 255) << 16;

        /// <summary>Takes a mode or theme change into use. Between frames only.</summary>
        void UpdateAppearance()
        {
            if (!_appearanceDirty)
                return;
            _appearanceDirty = false;
            bool wasSideOrder = SideOrderMode;
            _uiMode = _config.Mode;
            SetTitleBar(_uiMode == InterfaceMode.SideOrderTitleBar);
            ApplyStyle();
            if (SideOrderMode != wasSideOrder)
                PrepareFonts();
        }

        void SetMode(InterfaceMode mode)
        {
            _config.Mode = mode;
            _config.Save();
            _appearanceDirty = true;
        }

        void SetBorder(WindowOutline border)
        {
            _config.Border = border;
            _config.Save();
            ApplyWindowBorder();
        }

        void SetTheme(InterfaceTheme theme)
        {
            _config.Theme = theme;
            _config.Save();
            _appearanceDirty = true;
        }

        /// <summary>The viewport's display background: the theme's in Side Order, else the scene's own.</summary>
        void UpdateViewportBackground()
        {
            //The effect viewer keeps the background picked in its own panel.
            var theme = _effect == null ? SideOrderTheme.Current : null;
            _pipeline.ViewportBackground = theme != null ? Rgb(theme.Viewport) : null;
            _pipeline.ViewportGlow = theme != null ? Rgb(theme.ViewportGlow) : null;
        }

        static System.Numerics.Vector3 Rgb(System.Numerics.Vector4 c) => new(c.X, c.Y, c.Z);

        static readonly (InterfaceMode Mode, string Label)[] ModeRows =
        {
            (InterfaceMode.SideOrderTitleBar, "Side Order with title bar"),
            (InterfaceMode.SideOrder, "Side Order"),
            (InterfaceMode.Classic, "Classic"),
        };

        static readonly (InterfaceTheme Theme, string Label)[] ThemeRows =
        {
            (InterfaceTheme.Light, "Light"),
            (InterfaceTheme.Dark, "Dark"),
            (InterfaceTheme.YetDarker, "Yet Darker"),
        };

        static readonly (WindowOutline Border, string Label)[] BorderRows =
        {
            (WindowOutline.Theme, "Theme"),
            (WindowOutline.System, "System"),
        };

        void DrawAppearanceSettings()
        {
            Widgets.SectionHeader("Appearance");
            var modes = new List<(InterfaceMode Mode, string Label)>();
            foreach (var row in ModeRows)
                if (row.Mode != InterfaceMode.SideOrderTitleBar || _titleBar != null)
                    modes.Add(row);
            //Without the custom bar the title bar mode is plain Side Order.
            var shown =
                _config.Mode == InterfaceMode.SideOrderTitleBar && _titleBar == null
                    ? InterfaceMode.SideOrder
                    : _config.Mode;
            int mode = modes.FindIndex(r => r.Mode == shown);
            EnumRow(
                "Interface",
                "##uimode",
                modes.ConvertAll(r => r.Label),
                mode,
                i => SetMode(modes[i].Mode)
            );

            if (_config.Mode == InterfaceMode.Classic)
                return;
            int theme = Array.FindIndex(ThemeRows, r => r.Theme == _config.Theme);
            EnumRow(
                "Theme",
                "##uitheme",
                Array.ConvertAll(ThemeRows, r => r.Label),
                theme,
                i => SetTheme(ThemeRows[i].Theme)
            );
            if (_titleBar == null)
                return;
            int border = Array.FindIndex(BorderRows, r => r.Border == _config.Border);
            EnumRow(
                "Border",
                "##uiborder",
                Array.ConvertAll(BorderRows, r => r.Label),
                border,
                i => SetBorder(BorderRows[i].Border)
            );
        }

        static void EnumRow(
            string label,
            string id,
            IReadOnlyList<string> rows,
            int current,
            Action<int> pick
        )
        {
            Widgets.LabeledRow(
                label,
                () =>
                {
                    ImGui.SetNextItemWidth(-1);
                    if (!Widgets.BeginCombo(id, current >= 0 ? rows[current] : ""))
                        return;
                    Widgets.PopupRows(
                        id,
                        rows.Count,
                        current,
                        (row, isSelected) => ImGui.Selectable(rows[row], isSelected),
                        pick
                    );
                    ImGui.EndCombo();
                }
            );
        }
    }
}
