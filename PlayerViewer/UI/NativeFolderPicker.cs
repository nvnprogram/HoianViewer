using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace PlayerViewer.UI
{
    /// <summary>
    /// Native file/folder dialogs. On Windows this drives the modern Explorer-style
    /// IFileDialog (COM); on Linux/macOS it brokers to tinyfiledialogs (zenity/kdialog
    /// or the OS dialog). The COM path is only ever entered on Windows, so nothing here
    /// is Windows-bound at runtime elsewhere. All methods return null when cancelled.
    /// </summary>
    static class NativeFolderPicker
    {
        public static string SelectFolder(string title, string startPath = null)
        {
            try { return OperatingSystem.IsWindows() ? SelectFolderCom(title, startPath) : SelectFolderTfd(title, startPath); }
            catch { return null; }
        }

        public static string OpenFile(string title, string filterDisplay, string filterExt)
        {
            try { return OperatingSystem.IsWindows() ? OpenFileCom(title, filterDisplay, filterExt) : OpenFileTfd(title, filterDisplay, filterExt); }
            catch { return null; }
        }

        public static string SaveFile(string title, string defaultName, string filterDisplay, string filterExt)
        {
            try { return OperatingSystem.IsWindows() ? SaveFileCom(title, defaultName, filterDisplay, filterExt) : SaveFileTfd(title, defaultName, filterDisplay, filterExt); }
            catch { return null; }
        }

        // ---- Windows: modern IFileDialog (Explorer picker) ----

        static string OpenFileCom(string title, string filterDisplay, string filterExt)
        {
            int hr = CoCreateInstance(ref CLSID_FileOpenDialog, IntPtr.Zero, 1,
                ref IID_IFileOpenDialog, out IntPtr dlg);
            if (hr != 0) return null;

            try
            {
                GetOptions(dlg, out uint opts);
                SetOptions(dlg, opts | 0x40 /* FOS_FORCEFILESYSTEM */);

                if (!string.IsNullOrEmpty(title))
                    SetTitle(dlg, title);

                var filter = new COMDLG_FILTERSPEC { pszName = filterDisplay, pszSpec = filterExt };
                SetFileTypes(dlg, 1, ref filter);

                hr = Show(dlg, GetActiveWindow());
                if (hr != 0) return null;

                hr = GetResult(dlg, out IntPtr item);
                if (hr != 0 || item == IntPtr.Zero) return null;
                try { return DisplayName(item); }
                finally { Marshal.Release(item); }
            }
            finally { Marshal.Release(dlg); }
        }

        static string SaveFileCom(string title, string defaultName, string filterDisplay, string filterExt)
        {
            int hr = CoCreateInstance(ref CLSID_FileSaveDialog, IntPtr.Zero, 1,
                ref IID_IFileSaveDialog, out IntPtr dlg);
            if (hr != 0) return null;

            try
            {
                GetOptions(dlg, out uint opts);
                SetOptions(dlg, opts | 0x2 /* FOS_OVERWRITEPROMPT */ | 0x40 /* FOS_FORCEFILESYSTEM */);

                if (!string.IsNullOrEmpty(title))
                    SetTitle(dlg, title);
                if (!string.IsNullOrEmpty(defaultName))
                    SetFileName(dlg, defaultName);

                var filter = new COMDLG_FILTERSPEC { pszName = filterDisplay, pszSpec = filterExt };
                SetFileTypes(dlg, 1, ref filter);

                hr = Show(dlg, GetActiveWindow());
                if (hr != 0) return null;

                hr = GetResult(dlg, out IntPtr item);
                if (hr != 0 || item == IntPtr.Zero) return null;
                try { return DisplayName(item); }
                finally { Marshal.Release(item); }
            }
            finally { Marshal.Release(dlg); }
        }

        static string SelectFolderCom(string title, string startPath)
        {
            int hr = CoCreateInstance(ref CLSID_FileOpenDialog, IntPtr.Zero, 1,
                ref IID_IFileOpenDialog, out IntPtr dlg);
            if (hr != 0) return null;

            try
            {
                GetOptions(dlg, out uint opts);
                SetOptions(dlg, opts | 0x20 /* FOS_PICKFOLDERS */ | 0x40 /* FOS_FORCEFILESYSTEM */);

                if (!string.IsNullOrEmpty(title))
                    SetTitle(dlg, title);

                if (!string.IsNullOrEmpty(startPath))
                {
                    hr = SHCreateItemFromParsingName(startPath, IntPtr.Zero,
                        ref IID_IShellItem, out IntPtr folder);
                    if (hr == 0 && folder != IntPtr.Zero)
                    {
                        SetFolder(dlg, folder);
                        Marshal.Release(folder);
                    }
                }

                hr = Show(dlg, GetActiveWindow());
                if (hr != 0) return null;

                hr = GetResult(dlg, out IntPtr item);
                if (hr != 0 || item == IntPtr.Zero) return null;
                try { return DisplayName(item); }
                finally { Marshal.Release(item); }
            }
            finally { Marshal.Release(dlg); }
        }

        static string DisplayName(IntPtr item)
        {
            int hr = GetDisplayName(item, 0x80058000 /* SIGDN_FILESYSPATH */, out IntPtr namePtr);
            if (hr != 0) return null;
            string path = Marshal.PtrToStringUni(namePtr);
            Marshal.FreeCoTaskMem(namePtr);
            return path;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct COMDLG_FILTERSPEC
        {
            [MarshalAs(UnmanagedType.LPWStr)] public string pszName;
            [MarshalAs(UnmanagedType.LPWStr)] public string pszSpec;
        }

        static Guid CLSID_FileOpenDialog = new("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7");
        static Guid CLSID_FileSaveDialog = new("C0B4E2F3-BA21-4773-8DBA-335EC946EB8B");
        static Guid IID_IFileOpenDialog = new("D57C7288-D4AD-4768-BE02-9D969532D960");
        static Guid IID_IFileSaveDialog = new("84BCCD23-5FDE-4CDB-AEA4-AF64B83D78AB");
        static Guid IID_IShellItem = new("43826D1E-E718-42EE-BC55-A1E261C37BFE");

        [DllImport("ole32")] static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, uint ctx, ref Guid iid, out IntPtr obj);
        [DllImport("user32")] static extern IntPtr GetActiveWindow();
        [DllImport("shell32", CharSet = CharSet.Unicode)] static extern int SHCreateItemFromParsingName(string path, IntPtr bc, ref Guid iid, out IntPtr item);

        // IFileDialog vtable offsets (IUnknown=3, IModalWindow=1, IFileDialog=15).
        static int Show(IntPtr dlg, IntPtr hwnd) => Marshal.GetDelegateForFunctionPointer<ShowDelegate>(Vtbl(dlg, 3))(dlg, hwnd);
        static void SetOptions(IntPtr dlg, uint opts) => Marshal.GetDelegateForFunctionPointer<SetOptionsDelegate>(Vtbl(dlg, 9))(dlg, opts);
        static void GetOptions(IntPtr dlg, out uint opts) { opts = 0; Marshal.GetDelegateForFunctionPointer<GetOptionsDelegate>(Vtbl(dlg, 10))(dlg, out opts); }
        static void SetFileTypes(IntPtr dlg, uint count, ref COMDLG_FILTERSPEC filters) => Marshal.GetDelegateForFunctionPointer<SetFileTypesDelegate>(Vtbl(dlg, 4))(dlg, count, ref filters);
        static void SetFileName(IntPtr dlg, string name) => Marshal.GetDelegateForFunctionPointer<SetFileNameDelegate>(Vtbl(dlg, 15))(dlg, name);
        static void SetFolder(IntPtr dlg, IntPtr folder) => Marshal.GetDelegateForFunctionPointer<SetFolderDelegate>(Vtbl(dlg, 12))(dlg, folder);
        static void SetTitle(IntPtr dlg, string title) => Marshal.GetDelegateForFunctionPointer<SetTitleDelegate>(Vtbl(dlg, 17))(dlg, title);
        static int GetResult(IntPtr dlg, out IntPtr item) => Marshal.GetDelegateForFunctionPointer<GetResultDelegate>(Vtbl(dlg, 20))(dlg, out item);
        static int GetDisplayName(IntPtr item, uint sigdn, out IntPtr name) => Marshal.GetDelegateForFunctionPointer<GetDisplayNameDelegate>(Vtbl(item, 5))(item, sigdn, out name);

        static IntPtr Vtbl(IntPtr obj, int slot)
        {
            IntPtr vtbl = Marshal.ReadIntPtr(obj);
            return Marshal.ReadIntPtr(vtbl, slot * IntPtr.Size);
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int ShowDelegate(IntPtr self, IntPtr hwnd);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int SetOptionsDelegate(IntPtr self, uint opts);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetOptionsDelegate(IntPtr self, out uint opts);
        [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)] delegate int SetFileTypesDelegate(IntPtr self, uint count, ref COMDLG_FILTERSPEC filters);
        [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)] delegate int SetFileNameDelegate(IntPtr self, string name);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int SetFolderDelegate(IntPtr self, IntPtr folder);
        [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)] delegate int SetTitleDelegate(IntPtr self, string title);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetResultDelegate(IntPtr self, out IntPtr item);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetDisplayNameDelegate(IntPtr self, uint sigdn, out IntPtr name);

        // ---- Linux/macOS: tinyfiledialogs ----

        static string SelectFolderTfd(string title, string startPath) =>
            Str(tinyfd_selectFolderDialog(title, startPath ?? ""));

        static string OpenFileTfd(string title, string filterDisplay, string filterExt) =>
            WithPatterns(filterExt, (count, patterns) =>
                tinyfd_openFileDialog(title, "", count, patterns, filterDisplay, 0));

        static string SaveFileTfd(string title, string defaultName, string filterDisplay, string filterExt) =>
            WithPatterns(filterExt, (count, patterns) =>
                tinyfd_saveFileDialog(title, defaultName ?? "", count, patterns, filterDisplay));

        // tinyfiledialogs wants one glob per array entry ("*.png"), so a combined
        // "*.bfres;*.zs" filter is split apart. The UTF-8 pattern buffers are freed after.
        static string WithPatterns(string filterExt, Func<int, IntPtr[], IntPtr> call)
        {
            string[] patterns = string.IsNullOrEmpty(filterExt)
                ? Array.Empty<string>()
                : filterExt.Split(';', StringSplitOptions.RemoveEmptyEntries);
            IntPtr[] utf8 = patterns.Select(Utf8).ToArray();
            try { return Str(call(utf8.Length, utf8)); }
            finally { foreach (var p in utf8) Marshal.FreeHGlobal(p); }
        }

        static IntPtr Utf8(string s)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(s + '\0');
            IntPtr p = Marshal.AllocHGlobal(bytes.Length);
            Marshal.Copy(bytes, 0, p, bytes.Length);
            return p;
        }

        static string Str(IntPtr p) => p == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(p);

        const string Tfd = "tinyfiledialogs";

        [DllImport(Tfd, CallingConvention = CallingConvention.Cdecl)]
        static extern IntPtr tinyfd_selectFolderDialog(
            [MarshalAs(UnmanagedType.LPUTF8Str)] string title,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string defaultPath);

        [DllImport(Tfd, CallingConvention = CallingConvention.Cdecl)]
        static extern IntPtr tinyfd_openFileDialog(
            [MarshalAs(UnmanagedType.LPUTF8Str)] string title,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string defaultPathAndFile,
            int numOfFilterPatterns,
            IntPtr[] filterPatterns,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string singleFilterDescription,
            int allowMultipleSelects);

        [DllImport(Tfd, CallingConvention = CallingConvention.Cdecl)]
        static extern IntPtr tinyfd_saveFileDialog(
            [MarshalAs(UnmanagedType.LPUTF8Str)] string title,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string defaultPathAndFile,
            int numOfFilterPatterns,
            IntPtr[] filterPatterns,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string singleFilterDescription);
    }
}
