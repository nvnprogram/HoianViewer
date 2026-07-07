using System;
using System.IO;
using Newtonsoft.Json;

namespace PlayerViewer.Core
{
    /// <summary>
    /// Persisted app configuration (romfs path etc). Stored next to the executable.
    /// </summary>
    public class AppConfig
    {
        public string RomfsPath = "";
        public string LayeredFsPath = "";
        public bool UseLayeredFs = false;
        public int WindowWidth = 1600;
        public int WindowHeight = 900;

        static string FilePath => Path.Combine(AppContext.BaseDirectory, "playerviewer_config.json");

        public static AppConfig Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var config = JsonConvert.DeserializeObject<AppConfig>(File.ReadAllText(FilePath)) ?? new AppConfig();
                    //Guard against corrupt/zero sizes (e.g. saved while minimized).
                    if (config.WindowWidth < 200) config.WindowWidth = 1600;
                    if (config.WindowHeight < 200) config.WindowHeight = 900;
                    return config;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Config] Failed to load: {ex.Message}");
            }
            return new AppConfig();
        }

        public void Save()
        {
            try
            {
                File.WriteAllText(FilePath, JsonConvert.SerializeObject(this, Formatting.Indented));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Config] Failed to save: {ex.Message}");
            }
        }
    }
}
