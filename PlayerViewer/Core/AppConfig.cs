using System;
using System.IO;
using Newtonsoft.Json;

namespace PlayerViewer.Core
{
    /// <summary>
    /// Composited export/viewport background. Part of <see cref="PlayerConfig"/> so it travels
    /// with a preset (a preset captures the whole look: gear, colors, and background).
    /// </summary>
    public class BackgroundConfig
    {
        public int Mode; //0 Transparent, 1 Color, 2 Image
        public float[] Color = { 0f, 1f, 0f }; //Color mode; green reproduces the old greenscreen
        public string ImagePath = "";
        public int ScaleMode; //0 Fill, 1 Fit, 2 Stretch
        public float Zoom = 1f;
        public float OffsetX;
        public float OffsetY;
        public bool Tile;
        public int TileX = 1;
        public int TileY = 1;

        //Clamp user-supplied (preset/settings) values into valid ranges.
        public void Normalize()
        {
            Mode = System.Math.Clamp(Mode, 0, 2);
            ScaleMode = System.Math.Clamp(ScaleMode, 0, 2);
            if (Color == null || Color.Length < 3)
                Color = new[] { 0f, 1f, 0f };
            ImagePath ??= "";
            TileX = System.Math.Max(1, TileX);
            TileY = System.Math.Max(1, TileY);
        }
    }

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

        //Composited export/viewport background, saved and loaded with the preset.
        public BackgroundConfig Background = new();
    }

    /// <summary>
    /// Persisted app configuration (romfs paths etc). Stored in the per-user data folder.
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

        //Export supersample factor (1-8). Exports render internally at this multiple of the
        //capture size; with trim on, the crop keeps that internal resolution so a loosely
        //framed subject still exports sharp. VRAM and temp-disk use scale with the square.
        public int ExportSupersample = 1;

        //Physics warm-up: plays the animation (/ first animation in the sequence) through
        //this many extra times before recording starts without capturing. Physics reset
        //whenever an animation loads, so frame 0 has a twitch each time the exported
        //WebP/WebM loops. A warm-up lets the sim settle first. 0 = disabled.
        public int PrerollLoops = 1;

        //--- Capture-panel selections (persisted so they stick between runs)
        public int CaptureResIndex = 2; //index into the resolution dropdown
        public int ExportFormat = 0; //0 PNG, 1 MP4, 2 WebP, 3 WebM
        public int ExportFps = 60;
        public int AnimMode = 0; //0 Single, 1 Sequence

        public PlayerConfig Player = new();

        static string FilePath => Path.Combine(AppPaths.DataDir, "settings.json");

        public static AppConfig Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var config =
                        JsonConvert.DeserializeObject<AppConfig>(File.ReadAllText(FilePath))
                        ?? new AppConfig();
                    //Guard against corrupt/zero sizes (e.g. saved while minimized).
                    if (config.WindowWidth < 200)
                        config.WindowWidth = 1600;
                    if (config.WindowHeight < 200)
                        config.WindowHeight = 900;
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
