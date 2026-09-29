using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace PlayerViewer.UI
{
    /// <summary>
    /// The app's custom title bar on Windows. The window keeps its native style and this subclasses
    /// its procedure: the top frame is folded into the client area for the bar, the left, right
    /// and bottom frames stay native so the OS draws, hit tests and resizes them. Installed once
    /// for the window's life; <see cref="SetEnabled"/> switches between this and the OS bar.
    /// </summary>
    public sealed unsafe partial class CustomTitleBar
    {
        readonly IntPtr _hwnd;
        readonly Native.WndProc _proc; //the native side holds only a pointer to it
        readonly IntPtr _orig;
        bool _modal;
        bool _inModalRender;
        bool _maxDown;

        public bool Enabled { get; private set; }

        /// <summary>The window is the active one, from WM_ACTIVATE.</summary>
        public bool Active { get; private set; } = true;

        /// <summary>The bar's height in client pixels.</summary>
        public int CaptionHeight { get; }

        /// <summary>Client rects of the bar's widgets, from the last frame drawn.</summary>
        public List<Native.RECT> Interactive = new();

        /// <summary>Client rects of ImGui windows and popups over the bar, from the last frame.</summary>
        public List<Native.RECT> Overlays = new();

        /// <summary>The maximise button, reported as HTMAXBUTTON so Windows 11 offers snap layouts.</summary>
        public Native.RECT? MaxButton;

        /// <summary>Draws one frame from inside a move, size or menu loop.</summary>
        public Action ModalRender;

        CustomTitleBar(IntPtr hwnd, int captionHeight)
        {
            _hwnd = hwnd;
            CaptionHeight = captionHeight;
            _proc = Proc;
            _orig = Native.SetWindowLongPtrW(
                hwnd,
                Native.GWLP_WNDPROC,
                Marshal.GetFunctionPointerForDelegate(_proc)
            );
        }

        /// <summary>Subclasses the window, or returns null off Windows or if that fails.</summary>
        public static CustomTitleBar Install(IntPtr hwnd, int captionHeight)
        {
            if (!OperatingSystem.IsWindows() || IntPtr.Size != 8 || hwnd == IntPtr.Zero)
                return null;
            var bar = new CustomTitleBar(hwnd, captionHeight);
            return bar._orig != IntPtr.Zero ? bar : null;
        }

        uint Dpi => Native.GetDpiForWindow(_hwnd);

        /// <summary>The resize frame, which is also how far a maximised window hangs off the screen.</summary>
        public int FrameY =>
            Native.GetSystemMetricsForDpi(Native.SM_CYSIZEFRAME, Dpi)
            + Native.GetSystemMetricsForDpi(Native.SM_CXPADDEDBORDER, Dpi);

        public int FrameX =>
            Native.GetSystemMetricsForDpi(Native.SM_CXSIZEFRAME, Dpi)
            + Native.GetSystemMetricsForDpi(Native.SM_CXPADDEDBORDER, Dpi);

        /// <summary>
        /// How much taller the client is with this bar than under the OS bar, when not maximised.
        /// Taken when the bar is switched, since the window is gone by the time the size is saved.
        /// </summary>
        public int FoldedHeight { get; private set; }

        public bool Zoomed => Native.IsZoomed(_hwnd);

        public bool Iconic => Native.IsIconic(_hwnd);

        /// <summary>
        /// Maximised, or placed so its visible bounds cover the monitor's work area or the whole
        /// monitor, where rounded corners and outer gaps would look wrong.
        /// </summary>
        public bool FillsScreen()
        {
            if (Native.IsZoomed(_hwnd))
                return true;
            if (Native.IsIconic(_hwnd))
                return false;
            if (
                Native.DwmGetWindowAttribute(
                    _hwnd,
                    Native.DWMWA_EXTENDED_FRAME_BOUNDS,
                    out Native.RECT b,
                    Marshal.SizeOf<Native.RECT>()
                ) != 0
            )
                return false;
            var mi = new Native.MONITORINFO { cbSize = Marshal.SizeOf<Native.MONITORINFO>() };
            if (
                !Native.GetMonitorInfoW(
                    Native.MonitorFromWindow(_hwnd, Native.MONITOR_DEFAULTTONEAREST),
                    ref mi
                )
            )
                return false;
            return Covers(b, mi.rcWork) || Covers(b, mi.rcMonitor);
        }

        static bool Covers(Native.RECT a, Native.RECT b) =>
            a.left <= b.left && a.top <= b.top && a.right >= b.right && a.bottom >= b.bottom;

        /// <summary>
        /// Switches between this bar and the OS one. A frame change alone leaves the GL drawable at
        /// the old client size, so the window is resized by a pixel and back. OpenTK's cached
        /// client size goes stale either way and the caller resyncs it.
        /// </summary>
        public void SetEnabled(bool on)
        {
            Enabled = on;
            FoldedHeight = Native.GetSystemMetricsForDpi(Native.SM_CYCAPTION, Dpi) + FrameY;
            //Windows 11 draws its top border without this; Windows 10 needs it to keep the frame.
            var m = new Native.MARGINS { cyTopHeight = on ? 1 : 0 };
            Native.DwmExtendFrameIntoClientArea(_hwnd, ref m);
            Native.GetWindowRect(_hwnd, out var r);
            const uint f = Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER;
            Native.SetWindowPos(
                _hwnd,
                IntPtr.Zero,
                r.left,
                r.top,
                r.Width,
                r.Height + 1,
                f | Native.SWP_FRAMECHANGED
            );
            Native.SetWindowPos(_hwnd, IntPtr.Zero, r.left, r.top, r.Width, r.Height, f);
        }

        /// <summary>
        /// Sets the 1 px border Windows 11 draws round the window: a colour as 0x00BBGGRR, or
        /// <see cref="Native.DWMWA_COLOR_DEFAULT"/>.
        /// Earlier Windows has no such border and ignores it.
        /// </summary>
        public void SetBorderColour(uint colorref) =>
            Native.DwmSetWindowAttribute(_hwnd, Native.DWMWA_BORDER_COLOR, ref colorref, 4);

        /// <summary>The client size OpenTK should report.</summary>
        public (int Width, int Height) ClientSize()
        {
            Native.GetClientRect(_hwnd, out var r);
            return (r.Width, r.Height);
        }

        /// <summary>Posted rather than sent, so it lands between frames.</summary>
        public void Command(int sc) =>
            Native.PostMessageW(_hwnd, Native.WM_SYSCOMMAND, (IntPtr)sc, IntPtr.Zero);

        IntPtr Orig(uint msg, IntPtr w, IntPtr l) =>
            Native.CallWindowProcW(_orig, _hwnd, msg, w, l);

        IntPtr Proc(IntPtr hwnd, uint msg, IntPtr w, IntPtr l)
        {
            switch (msg)
            {
                case Native.WM_NCCALCSIZE when Enabled:
                {
                    //The first rect of NCCALCSIZE_PARAMS, or the rect itself when wParam is 0.
                    var rc = (Native.RECT*)l;
                    int top = rc->top;
                    IntPtr r = Orig(msg, w, l);
                    rc->top = top;
                    if (Native.IsZoomed(hwnd))
                        rc->top += FrameY;
                    return w != IntPtr.Zero ? IntPtr.Zero : r;
                }
                case Native.WM_NCHITTEST when Enabled:
                    return (IntPtr)HitTest(l);

                //DefWindowProc would track and draw a classic maximise button on the press.
                case Native.WM_NCLBUTTONDOWN when Enabled && (int)w == Native.HTMAXBUTTON:
                    _maxDown = true;
                    return IntPtr.Zero;
                case Native.WM_NCLBUTTONDBLCLK when Enabled && (int)w == Native.HTMAXBUTTON:
                    return IntPtr.Zero;
                case Native.WM_NCLBUTTONUP when Enabled && (int)w == Native.HTMAXBUTTON:
                    if (_maxDown)
                        Command(Native.IsZoomed(hwnd) ? Native.SC_RESTORE : Native.SC_MAXIMIZE);
                    _maxDown = false;
                    return IntPtr.Zero;
                case Native.WM_NCMOUSELEAVE:
                    _maxDown = false;
                    break;

                //DefWindowProc's own system menu hit tests a caption that is no longer there.
                case Native.WM_NCRBUTTONUP when Enabled && (int)w == Native.HTCAPTION:
                    ShowSystemMenu((short)((long)l & 0xFFFF), (short)(((long)l >> 16) & 0xFFFF));
                    return IntPtr.Zero;
                case Native.WM_SYSCOMMAND
                    when Enabled && ((long)w & 0xFFF0) == Native.SC_KEYMENU && (long)l == ' ':
                {
                    var p = new Native.POINT { x = 0, y = CaptionHeight };
                    Native.ClientToScreen(hwnd, ref p);
                    ShowSystemMenu(p.x, p.y);
                    return IntPtr.Zero;
                }

                case Native.WM_ACTIVATE:
                    Active = ((long)w & 0xFFFF) != 0;
                    break;

                //OpenTK returns 0 for these without the default handling, which loses Alt+F4 and
                //Alt+Space under either bar. Its key event is raised first.
                case Native.WM_SYSKEYDOWN:
                    Orig(msg, w, l);
                    return Native.DefWindowProcW(hwnd, msg, w, l);
                case Native.WM_SYSCHAR when (int)w == ' ':
                    return Native.DefWindowProcW(hwnd, msg, w, l);

                //OpenTK runs a 1 ms timer (id 1) through these loops that nothing handles, and its
                //render loop is blocked inside them, so the frames are drawn from here.
                case Native.WM_ENTERSIZEMOVE:
                case Native.WM_ENTERMENULOOP:
                    _modal = true;
                    break;
                case Native.WM_EXITSIZEMOVE:
                case Native.WM_EXITMENULOOP:
                    _modal = false;
                    break;
                case Native.WM_TIMER when _modal && (long)w == 1:
                    RenderModal();
                    return IntPtr.Zero;
                case Native.WM_WINDOWPOSCHANGED when _modal:
                {
                    IntPtr r = Orig(msg, w, l); //OpenTK raises OnResize in here
                    RenderModal();
                    return r;
                }
            }
            return Orig(msg, w, l);
        }

        void RenderModal()
        {
            if (_inModalRender || ModalRender == null)
                return;
            _inModalRender = true;
            try
            {
                ModalRender();
            }
            finally
            {
                _inModalRender = false;
            }
        }

        //How much wider than the side frames the top corners' resize bands are.
        const int TopCornerExtra = 4;

        int HitTest(IntPtr l)
        {
            //Left, right and bottom are still native frame, so the default answer is right there.
            int native = (int)Orig(Native.WM_NCHITTEST, IntPtr.Zero, l);
            var pt = new Native.POINT
            {
                x = (short)((long)l & 0xFFFF),
                y = (short)(((long)l >> 16) & 0xFFFF),
            };
            Native.GetWindowRect(_hwnd, out var wr);
            //The native top band is FrameY + 1 rows; a maximised window has none.
            bool top = !Native.IsZoomed(_hwnd) && pt.y <= wr.top + FrameY;

            if (native is Native.HTLEFT or Native.HTRIGHT)
                return top
                    ? (native == Native.HTLEFT ? Native.HTTOPLEFT : Native.HTTOPRIGHT)
                    : native;
            if (native != Native.HTCLIENT)
                return native;
            if (top)
            {
                int corner = FrameX * 2 + TopCornerExtra;
                if (pt.x < wr.left + corner)
                    return Native.HTTOPLEFT;
                if (pt.x >= wr.right - corner)
                    return Native.HTTOPRIGHT;
                return Native.HTTOP;
            }

            var cp = pt;
            Native.ScreenToClient(_hwnd, ref cp);
            if (cp.y >= CaptionHeight)
                return Native.HTCLIENT;
            foreach (var r in Overlays)
                if (Contains(r, cp))
                    return Native.HTCLIENT;
            if (MaxButton is Native.RECT m && Contains(m, cp))
                return Native.HTMAXBUTTON;
            foreach (var r in Interactive)
                if (Contains(r, cp))
                    return Native.HTCLIENT;
            return Native.HTCAPTION;
        }

        /// <summary>The system menu at a screen point, with the item states DefWindowProc would set.</summary>
        public void ShowSystemMenu(int x, int y)
        {
            IntPtr menu = Native.GetSystemMenu(_hwnd, false);
            if (menu == IntPtr.Zero)
                return;
            bool zoomed = Native.IsZoomed(_hwnd),
                iconic = Native.IsIconic(_hwnd);
            void Set(int id, bool on) =>
                Native.EnableMenuItem(
                    menu,
                    (uint)id,
                    Native.MF_BYCOMMAND | (on ? 0u : Native.MF_GRAYED)
                );
            Set(Native.SC_RESTORE, zoomed || iconic);
            Set(Native.SC_MOVE, !zoomed && !iconic);
            Set(Native.SC_SIZE, !zoomed && !iconic);
            Set(Native.SC_MINIMIZE, !iconic);
            Set(Native.SC_MAXIMIZE, !zoomed);
            Set(Native.SC_CLOSE, true);
            Native.SetMenuDefaultItem(menu, Native.SC_CLOSE, 0);
            uint align =
                Native.GetSystemMetrics(Native.SM_MENUDROPALIGNMENT) != 0
                    ? Native.TPM_RIGHTALIGN
                    : 0;
            int cmd = Native.TrackPopupMenu(
                menu,
                Native.TPM_RETURNCMD | Native.TPM_RIGHTBUTTON | align,
                x,
                y,
                0,
                _hwnd,
                IntPtr.Zero
            );
            if (cmd != 0)
                Command(cmd);
        }

        static bool Contains(Native.RECT r, Native.POINT p) =>
            p.x >= r.left && p.x < r.right && p.y >= r.top && p.y < r.bottom;

        public static class Native
        {
            public delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);

            [StructLayout(LayoutKind.Sequential)]
            public struct RECT
            {
                public int left,
                    top,
                    right,
                    bottom;
                public int Width => right - left;
                public int Height => bottom - top;
            }

            [StructLayout(LayoutKind.Sequential)]
            public struct POINT
            {
                public int x,
                    y;
            }

            [StructLayout(LayoutKind.Sequential)]
            public struct MARGINS
            {
                public int cxLeftWidth,
                    cxRightWidth,
                    cyTopHeight,
                    cyBottomHeight;
            }

            [StructLayout(LayoutKind.Sequential)]
            public struct MONITORINFO
            {
                public int cbSize;
                public RECT rcMonitor,
                    rcWork;
                public uint dwFlags;
            }

            public const int GWLP_WNDPROC = -4;
            public const uint WM_ACTIVATE = 0x0006,
                WM_WINDOWPOSCHANGED = 0x0047,
                WM_NCCALCSIZE = 0x0083,
                WM_NCHITTEST = 0x0084,
                WM_NCLBUTTONDOWN = 0x00A1,
                WM_NCLBUTTONUP = 0x00A2,
                WM_NCLBUTTONDBLCLK = 0x00A3,
                WM_NCRBUTTONUP = 0x00A5,
                WM_SYSKEYDOWN = 0x0104,
                WM_SYSCHAR = 0x0106,
                WM_SYSCOMMAND = 0x0112,
                WM_TIMER = 0x0113,
                WM_ENTERMENULOOP = 0x0211,
                WM_EXITMENULOOP = 0x0212,
                WM_ENTERSIZEMOVE = 0x0231,
                WM_EXITSIZEMOVE = 0x0232,
                WM_NCMOUSELEAVE = 0x02A2;
            public const int SC_SIZE = 0xF000,
                SC_MOVE = 0xF010,
                SC_MINIMIZE = 0xF020,
                SC_MAXIMIZE = 0xF030,
                SC_CLOSE = 0xF060,
                SC_KEYMENU = 0xF100,
                SC_RESTORE = 0xF120;
            public const int HTCLIENT = 1,
                HTCAPTION = 2,
                HTMAXBUTTON = 9,
                HTLEFT = 10,
                HTRIGHT = 11,
                HTTOP = 12,
                HTTOPLEFT = 13,
                HTTOPRIGHT = 14;
            public const uint SWP_NOSIZE = 0x1,
                SWP_NOMOVE = 0x2,
                SWP_NOZORDER = 0x4,
                SWP_NOACTIVATE = 0x10,
                SWP_FRAMECHANGED = 0x20,
                SWP_NOOWNERZORDER = 0x200;
            public const int SM_CYCAPTION = 4,
                SM_CXSIZEFRAME = 32,
                SM_CYSIZEFRAME = 33,
                SM_MENUDROPALIGNMENT = 40,
                SM_CXPADDEDBORDER = 92;
            public const uint MF_BYCOMMAND = 0,
                MF_GRAYED = 1;
            public const uint TPM_RIGHTBUTTON = 0x2,
                TPM_RIGHTALIGN = 0x8,
                TPM_RETURNCMD = 0x100;
            public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9,
                DWMWA_BORDER_COLOR = 34;
            public const uint DWMWA_COLOR_DEFAULT = 0xFFFFFFFF;
            public const uint MONITOR_DEFAULTTONEAREST = 2;

            [DllImport("user32.dll")]
            public static extern IntPtr SetWindowLongPtrW(IntPtr h, int index, IntPtr value);

            [DllImport("user32.dll")]
            public static extern IntPtr CallWindowProcW(
                IntPtr prev,
                IntPtr h,
                uint m,
                IntPtr w,
                IntPtr l
            );

            [DllImport("user32.dll")]
            public static extern IntPtr DefWindowProcW(IntPtr h, uint m, IntPtr w, IntPtr l);

            [DllImport("user32.dll")]
            public static extern bool PostMessageW(IntPtr h, uint m, IntPtr w, IntPtr l);

            [DllImport("user32.dll")]
            public static extern bool SetWindowPos(
                IntPtr h,
                IntPtr after,
                int x,
                int y,
                int cx,
                int cy,
                uint flags
            );

            [DllImport("user32.dll")]
            public static extern bool GetWindowRect(IntPtr h, out RECT r);

            [DllImport("user32.dll")]
            public static extern bool GetClientRect(IntPtr h, out RECT r);

            [DllImport("user32.dll")]
            public static extern bool ClientToScreen(IntPtr h, ref POINT p);

            [DllImport("user32.dll")]
            public static extern bool ScreenToClient(IntPtr h, ref POINT p);

            [DllImport("user32.dll")]
            public static extern bool IsZoomed(IntPtr h);

            [DllImport("user32.dll")]
            public static extern bool IsIconic(IntPtr h);

            [DllImport("user32.dll")]
            public static extern uint GetDpiForWindow(IntPtr h);

            [DllImport("user32.dll")]
            public static extern int GetSystemMetricsForDpi(int index, uint dpi);

            [DllImport("user32.dll")]
            public static extern int GetSystemMetrics(int index);

            [DllImport("user32.dll")]
            public static extern IntPtr GetSystemMenu(IntPtr h, bool revert);

            [DllImport("user32.dll")]
            public static extern int EnableMenuItem(IntPtr menu, uint id, uint flags);

            [DllImport("user32.dll")]
            public static extern bool SetMenuDefaultItem(IntPtr menu, int item, int byPos);

            [DllImport("user32.dll")]
            public static extern int TrackPopupMenu(
                IntPtr menu,
                uint flags,
                int x,
                int y,
                int reserved,
                IntPtr h,
                IntPtr rect
            );

            [DllImport("user32.dll")]
            public static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);

            [DllImport("user32.dll")]
            public static extern bool GetMonitorInfoW(IntPtr monitor, ref MONITORINFO info);

            [DllImport("dwmapi.dll")]
            public static extern int DwmExtendFrameIntoClientArea(IntPtr h, ref MARGINS m);

            [DllImport("dwmapi.dll")]
            public static extern int DwmGetWindowAttribute(
                IntPtr h,
                int attr,
                out RECT value,
                int size
            );

            [DllImport("dwmapi.dll")]
            public static extern int DwmSetWindowAttribute(
                IntPtr h,
                int attr,
                ref uint value,
                int size
            );
        }
    }
}
