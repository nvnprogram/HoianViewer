using System;
using System.Diagnostics;
using System.IO;

namespace PlayerViewer.Core
{
    /// <summary>
    /// Per-user data directory, created on first access: %APPDATA%\PlayerViewer on Windows,
    /// ~/.config/PlayerViewer on Linux/macOS. Holds settings.json and an optional bundled ffmpeg.
    /// </summary>
    public static class AppPaths
    {
        public static string DataDir { get; } = CreateDataDir();

        static string CreateDataDir()
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PlayerViewer");
            Directory.CreateDirectory(dir);
            return dir;
        }

        /// <summary>Opens the data directory in the OS file browser.</summary>
        public static void OpenDataDir()
        {
            try
            {
                if (OperatingSystem.IsWindows())
                    Process.Start(new ProcessStartInfo { FileName = DataDir, UseShellExecute = true });
                else if (OperatingSystem.IsMacOS())
                    Process.Start("open", DataDir);
                else
                    Process.Start("xdg-open", DataDir);
            }
            catch { }
        }
    }
}
