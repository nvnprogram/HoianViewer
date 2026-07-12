using System;
using System.IO;
using Newtonsoft.Json;

namespace PlayerViewer.Core
{
    
    public class PlayerConfig
    {
        public int PlayerType;
        public int EyeColor;
        public int SkinTone;
        public string Hair;
        public int HairVariation;
        public string Eyebrow;
        public int EyebrowVariation;
        public string Head;
        public int HeadVariation;
        public string Clothes;
        public int ClothesVariation;
        public string Bottom;
        public int BottomVariation;
        public string Shoes;
        public int ShoesVariation;
        public string Tank;
        public int TankVariation;
        public string Weapon;
        public int WeaponVariation;
        public int TeamColorIndex;
        public int TeamIndex;
        public bool UseCustomTeamColor = true;
        public float[] CustomAlpha = { 0.925f, 0.243f, 0.549f };
        public float[] CustomBravo = { 0.196f, 0.855f, 0.302f };
        public float[] CustomCharlie = { 0.980f, 0.769f, 0.196f };
    }


    /// <summary>
    /// Persisted app configuration (romfs path etc). Stored next to the executable.
    /// </summary>
    public class AppConfig
    {
        public string RomfsPath = "";
        public string SdodrRomfsPath = "";
        public string LayeredFsPath = "";
        public bool UseLayeredFs = false;
        public int WindowWidth = 1600;
        public int WindowHeight = 900;

        //--- Export/capture settings (configured in the Settings window)
        //Trim fully-transparent deadspace off exported frames. Uses the transparent
        //render as an alpha oracle, so it also crops greenscreen MP4s.
        public bool TrimDeadspace = false;
        //Extra pixels of transparent margin kept around the content bounding box.
        public int TrimMarginPx = 0;
        //WebP encode quality: 100 = lossless (bit-exact), below = lossy (smaller/faster).
        public int WebpQuality = 100;

        public PlayerConfig Player = new();

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
