using System.Numerics;
using ImGuiNET;

namespace PlayerViewer.UI
{
    /// <summary>
    /// The colours the panels use as text accents and fills. Each interface look supplies one.
    /// </summary>
    public sealed class ThemePalette
    {
        public Vector4 Gold { get; init; }
        public Vector4 GoldDim { get; init; }
        public Vector4 GoldBright { get; init; }
        public Vector4 Bg { get; init; }
        public Vector4 BgPanel { get; init; }
        public Vector4 BgItem { get; init; }
        public Vector4 BgItemHover { get; init; }
        public Vector4 BgItemActive { get; init; }
        public Vector4 TextMain { get; init; }
        public Vector4 TextDim { get; init; }
        public Vector4 Error { get; init; }
        public Vector4 Success { get; init; }
        public Vector4 Cyan { get; init; }
        public Vector4 RedButtonBg { get; init; }
        public Vector4 RedButtonHover { get; init; }
    }

    /// <summary>
    /// Black and gold ImGui theme. Its colours read through the active palette, so the Side Order
    /// looks can swap them; <see cref="Apply"/> restores the classic ones.
    /// </summary>
    public static class Theme
    {
        public static readonly ThemePalette Classic = new()
        {
            Gold = new(0.85f, 0.68f, 0.24f, 1.00f),
            GoldDim = new(0.62f, 0.48f, 0.15f, 1.00f),
            GoldBright = new(1.00f, 0.83f, 0.36f, 1.00f),
            Bg = new(0.055f, 0.055f, 0.065f, 1.00f),
            BgPanel = new(0.085f, 0.085f, 0.10f, 1.00f),
            BgItem = new(0.13f, 0.13f, 0.15f, 1.00f),
            BgItemHover = new(0.19f, 0.18f, 0.16f, 1.00f),
            BgItemActive = new(0.28f, 0.24f, 0.15f, 1.00f),
            TextMain = new(0.92f, 0.91f, 0.88f, 1.00f),
            TextDim = new(0.55f, 0.54f, 0.52f, 1.00f),
            Error = new(0.90f, 0.35f, 0.30f, 1.00f),
            Success = new(0.40f, 0.85f, 0.40f, 1.00f),
            Cyan = new(0.35f, 0.80f, 0.92f, 1.00f),
            RedButtonBg = new(0.55f, 0.12f, 0.10f, 1.00f),
            RedButtonHover = new(0.70f, 0.16f, 0.13f, 1.00f),
        };

        static ThemePalette _active = Classic;

        /// <summary>Makes <paramref name="palette"/> the one the colours below read from.</summary>
        public static void Use(ThemePalette palette) => _active = palette ?? Classic;

        public static Vector4 Gold => _active.Gold;
        public static Vector4 GoldDim => _active.GoldDim;
        public static Vector4 GoldBright => _active.GoldBright;
        public static Vector4 Bg => _active.Bg;
        public static Vector4 BgPanel => _active.BgPanel;
        public static Vector4 BgItem => _active.BgItem;
        public static Vector4 BgItemHover => _active.BgItemHover;
        public static Vector4 BgItemActive => _active.BgItemActive;
        public static Vector4 TextMain => _active.TextMain;
        public static Vector4 TextDim => _active.TextDim;
        public static Vector4 Error => _active.Error;
        public static Vector4 Success => _active.Success;
        public static Vector4 Cyan => _active.Cyan;
        public static Vector4 RedButtonBg => _active.RedButtonBg;
        public static Vector4 RedButtonHover => _active.RedButtonHover;

        public static void Apply()
        {
            _active = Classic;
            var style = ImGui.GetStyle();
            style.WindowRounding = 8;
            style.ChildRounding = 8;
            style.FrameRounding = 6;
            style.PopupRounding = 8;
            style.GrabRounding = 6;
            style.TabRounding = 6;
            style.ScrollbarRounding = 8;
            style.ScrollbarSize = 12;
            style.WindowBorderSize = 0;
            style.ChildBorderSize = 0;
            style.PopupBorderSize = 1;
            style.FrameBorderSize = 0;
            style.WindowPadding = new Vector2(10, 10);
            style.FramePadding = new Vector2(9, 5);
            style.ItemSpacing = new Vector2(8, 6);
            style.ItemInnerSpacing = new Vector2(6, 4);
            style.GrabMinSize = 12;
            style.WindowTitleAlign = new Vector2(0.5f, 0.5f);

            var c = style.Colors;
            c[(int)ImGuiCol.Text] = TextMain;
            c[(int)ImGuiCol.TextDisabled] = TextDim;
            c[(int)ImGuiCol.WindowBg] = Bg;
            c[(int)ImGuiCol.ChildBg] = new Vector4(0, 0, 0, 0);
            c[(int)ImGuiCol.PopupBg] = new Vector4(0.07f, 0.07f, 0.08f, 0.98f);
            c[(int)ImGuiCol.Border] = new Vector4(0.30f, 0.26f, 0.16f, 0.55f);
            c[(int)ImGuiCol.BorderShadow] = new Vector4(0, 0, 0, 0);
            c[(int)ImGuiCol.FrameBg] = BgItem;
            c[(int)ImGuiCol.FrameBgHovered] = BgItemHover;
            c[(int)ImGuiCol.FrameBgActive] = BgItemActive;
            c[(int)ImGuiCol.TitleBg] = Bg;
            c[(int)ImGuiCol.TitleBgActive] = new Vector4(0.10f, 0.09f, 0.07f, 1.00f);
            c[(int)ImGuiCol.TitleBgCollapsed] = Bg;
            c[(int)ImGuiCol.MenuBarBg] = BgPanel;
            c[(int)ImGuiCol.ScrollbarBg] = new Vector4(0.04f, 0.04f, 0.05f, 0.6f);
            c[(int)ImGuiCol.ScrollbarGrab] = new Vector4(0.28f, 0.26f, 0.20f, 1.00f);
            c[(int)ImGuiCol.ScrollbarGrabHovered] = GoldDim;
            c[(int)ImGuiCol.ScrollbarGrabActive] = Gold;
            c[(int)ImGuiCol.CheckMark] = GoldBright;
            c[(int)ImGuiCol.SliderGrab] = Gold;
            c[(int)ImGuiCol.SliderGrabActive] = GoldBright;
            c[(int)ImGuiCol.Button] = new Vector4(0.16f, 0.15f, 0.13f, 1.00f);
            c[(int)ImGuiCol.ButtonHovered] = new Vector4(0.32f, 0.27f, 0.15f, 1.00f);
            c[(int)ImGuiCol.ButtonActive] = new Vector4(0.48f, 0.39f, 0.18f, 1.00f);
            c[(int)ImGuiCol.Header] = new Vector4(0.24f, 0.21f, 0.13f, 1.00f);
            c[(int)ImGuiCol.HeaderHovered] = new Vector4(0.34f, 0.29f, 0.16f, 1.00f);
            c[(int)ImGuiCol.HeaderActive] = new Vector4(0.42f, 0.35f, 0.17f, 1.00f);
            c[(int)ImGuiCol.Separator] = new Vector4(0.25f, 0.23f, 0.18f, 0.9f);
            c[(int)ImGuiCol.SeparatorHovered] = GoldDim;
            c[(int)ImGuiCol.SeparatorActive] = Gold;
            c[(int)ImGuiCol.ResizeGrip] = new Vector4(0.30f, 0.27f, 0.18f, 0.6f);
            c[(int)ImGuiCol.ResizeGripHovered] = GoldDim;
            c[(int)ImGuiCol.ResizeGripActive] = Gold;
            c[(int)ImGuiCol.Tab] = new Vector4(0.11f, 0.11f, 0.12f, 1.00f);
            c[(int)ImGuiCol.TabHovered] = new Vector4(0.34f, 0.29f, 0.16f, 1.00f);
            c[(int)ImGuiCol.TabActive] = new Vector4(0.28f, 0.24f, 0.14f, 1.00f);
            c[(int)ImGuiCol.TabUnfocused] = new Vector4(0.09f, 0.09f, 0.10f, 1.00f);
            c[(int)ImGuiCol.TabUnfocusedActive] = new Vector4(0.16f, 0.15f, 0.12f, 1.00f);
            c[(int)ImGuiCol.PlotLines] = Gold;
            c[(int)ImGuiCol.PlotHistogram] = Gold;
            c[(int)ImGuiCol.TextSelectedBg] = new Vector4(0.48f, 0.39f, 0.18f, 0.55f);
            c[(int)ImGuiCol.DragDropTarget] = GoldBright;
            c[(int)ImGuiCol.NavHighlight] = Gold;
            c[(int)ImGuiCol.ModalWindowDimBg] = new Vector4(0, 0, 0, 0.65f);
        }
    }
}
