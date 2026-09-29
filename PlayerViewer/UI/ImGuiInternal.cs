using System.Runtime.InteropServices;

namespace PlayerViewer.UI
{
    /// <summary>Internal ImGui calls the native library exports but this wrapper leaves out.</summary>
    static class ImGuiInternal
    {
        /// <summary>ImGuiItemFlags_Disabled: the item takes no hover, click or drag.</summary>
        public const int ItemFlagDisabled = 1 << 2;

        [DllImport(
            "cimgui",
            CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "igPushItemFlag"
        )]
        public static extern void PushItemFlag(int option, byte enabled);

        [DllImport(
            "cimgui",
            CallingConvention = CallingConvention.Cdecl,
            EntryPoint = "igPopItemFlag"
        )]
        public static extern void PopItemFlag();
    }
}
