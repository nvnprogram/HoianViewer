using System;
using System.Diagnostics;
using System.IO;
using System.Text;
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
            var color = new[] { 0f, 1f, 0f };
            if (Color != null)
                for (int i = 0; i < 3 && i < Color.Length; i++)
                    if (float.IsFinite(Color[i]))
                        color[i] = System.Math.Clamp(Color[i], 0f, 1f);
            Color = color;
            if (!float.IsFinite(Zoom) || Zoom <= 0f)
                Zoom = 1f;
            if (!float.IsFinite(OffsetX))
                OffsetX = 0f;
            if (!float.IsFinite(OffsetY))
                OffsetY = 0f;
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

        public void Normalize()
        {
            Background ??= new BackgroundConfig();
            Background.Normalize();
            CustomAlpha = NormalizeTeamColor(CustomAlpha, 0.925f, 0.243f, 0.549f);
            CustomBravo = NormalizeTeamColor(CustomBravo, 0.196f, 0.855f, 0.302f);
            CustomCharlie = NormalizeTeamColor(CustomCharlie, 0.980f, 0.769f, 0.196f);
        }

        static float[] NormalizeTeamColor(float[] value, float r, float g, float b)
        {
            var color = new[] { r, g, b };
            if (value != null)
                for (int i = 0; i < 3 && i < value.Length; i++)
                    if (float.IsFinite(value[i]))
                        color[i] = System.Math.Clamp(value[i], 0f, 1f);
            return color;
        }
    }

    /// <summary>The effect viewer's settings.</summary>
    public class EffectConfig
    {
        //The shortest loop clip a periodic set is given, in seconds.
        public float MinLoopSeconds = 2f;

        public bool ShowGrid = true;
        public bool ShowPlayer;

        //Viewport background while an effect is open, sRGB.
        public float[] Background = { 0.06f, 0.06f, 0.075f };

        //The export clip when it is not the set's loop: start and length in frames.
        public bool CustomClip;
        public int ClipStart;
        public int ClipLength = 300;

        public void Normalize()
        {
            MinLoopSeconds = System.Math.Clamp(MinLoopSeconds, 0f, 60f);
            ClipStart = System.Math.Clamp(ClipStart, 0, 36000);
            ClipLength = System.Math.Clamp(ClipLength, 1, 36000);
            if (Background is not { Length: 3 })
                Background = new[] { 0.06f, 0.06f, 0.075f };
        }
    }

    /// <summary>How the window looks: the original ImGui style, or the Side Order look.</summary>
    public enum InterfaceMode
    {
        Classic,
        SideOrder,
        SideOrderTitleBar, // Windows only as of now
    }

    /// <summary>The border Windows 11 draws round the window in the Side Order modes.</summary>
    public enum WindowOutline
    {
        Theme,
        System,
    }

    /// <summary>The Side Order look's colour presets.</summary>
    public enum InterfaceTheme
    {
        Light,
        Dark,
        YetDarker,
    }

    /// <summary>
    /// Persisted app configuration (romfs paths etc). Stored in the per-user data folder.
    /// </summary>
    public class AppConfig
    {
        public static InterfaceMode DefaultMode =>
            OperatingSystem.IsWindows() ? InterfaceMode.SideOrderTitleBar : InterfaceMode.SideOrder;

        //--- Appearance, stored by name so a hand edit or a newer name cannot fail the load.
        public string UiMode = DefaultMode.ToString();
        public string UiTheme = nameof(InterfaceTheme.YetDarker);
        public string UiBorder = nameof(WindowOutline.Theme);

        [JsonIgnore]
        public InterfaceMode Mode
        {
            get => ParseMode(UiMode);
            set => UiMode = ParseMode(value.ToString()).ToString();
        }

        [JsonIgnore]
        public InterfaceTheme Theme
        {
            get =>
                Enum.TryParse(UiTheme, true, out InterfaceTheme t) && Enum.IsDefined(t)
                    ? t
                    : InterfaceTheme.YetDarker;
            set => UiTheme = value.ToString();
        }

        [JsonIgnore]
        public WindowOutline Border
        {
            get =>
                Enum.TryParse(UiBorder, true, out WindowOutline b) && Enum.IsDefined(b)
                    ? b
                    : WindowOutline.Theme;
            set => UiBorder = value.ToString();
        }

        //The title bar mode reads as plain Side Order off Windows.
        static InterfaceMode ParseMode(string name)
        {
            if (!Enum.TryParse(name, true, out InterfaceMode mode) || !Enum.IsDefined(mode))
                return DefaultMode;
            return mode == InterfaceMode.SideOrderTitleBar && !OperatingSystem.IsWindows()
                ? InterfaceMode.SideOrder
                : mode;
        }

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

        //Physics convergence: an animation export records the hair cloth pose at its first
        //frame and blends back to it over the last min(0.25s, clip length / 4), so a looping
        //clip does not jump when it wraps. Independent of the warm-up above.
        public bool PhysicsConverge = true;

        //--- Lists
        //Language code of the romfs message archive names come from, or Localization.None.
        public string Language = Localization.DefaultLanguage;

        //Weapons and gear pick from a grid of tiles rather than a dropdown.
        public bool GearGrid = true;

        //--- Material editor
        //Whether the editor may specialise the ubershader.
        public bool UseSplicer = false;

        //--- Capture-panel selections (persisted so they stick between runs)
        public int CaptureResIndex = 2; //index into the resolution dropdown
        public int ExportFormat = 0; //0 PNG, 1 MP4, 2 WebP, 3 WebM, 4 PNG sequence
        public int ExportFps = 60;
        public int AnimMode = 0; //0 Single, 1 Sequence

        public PlayerConfig Player = new();

        public EffectConfig Effect = new();

        static string FilePath => Path.Combine(AppPaths.DataDir, "settings.json");

        //Pre-AppData location, next to the exe
        static string LegacyFilePath =>
            Path.Combine(AppContext.BaseDirectory, "playerviewer_config.json");

        //Writes are coalesced: Save() only marks the config dirty, and the actual file write
        //happens at most this often. Slider callbacks call Save() every frame while dragging.
        static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(1);

        bool _dirty;
        readonly Stopwatch _sinceWrite = Stopwatch.StartNew();

        public static AppConfig Load()
        {
            var config = ReadFrom(FilePath);
            bool migrated = false;
            if (config == null && !File.Exists(FilePath))
            {
                config = ReadFrom(LegacyFilePath);
                migrated = config != null;
            }
            config ??= new AppConfig();
            config.Normalize();
            if (migrated)
            {
                config.WriteToDisk();
                Console.WriteLine($"[Config] Migrated settings from {LegacyFilePath}");
            }
            return config;
        }

        static AppConfig ReadFrom(string path)
        {
            try
            {
                if (File.Exists(path))
                    return JsonConvert.DeserializeObject<AppConfig>(File.ReadAllText(path));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Config] Failed to load {path}: {ex.Message}");
            }
            return null;
        }

        //Clamps loaded values and replaces anything a hand-edited file left null.
        public void Normalize()
        {
            //Guard against corrupt/zero sizes (e.g. saved while minimized).
            if (WindowWidth < 200)
                WindowWidth = 1600;
            if (WindowHeight < 200)
                WindowHeight = 900;
            //Multiplies the render target, so a hand-edited value has to stay in range.
            ExportSupersample = System.Math.Clamp(ExportSupersample, 1, 8);
            if (string.IsNullOrWhiteSpace(Language))
                Language = Localization.DefaultLanguage;
            UiMode = Mode.ToString();
            UiTheme = Theme.ToString();
            UiBorder = Border.ToString();
            Player ??= new PlayerConfig();
            Player.Normalize();
            Effect ??= new EffectConfig();
            Effect.Normalize();
        }

        /// <summary>
        /// Marks the config as needing a write. Cheap enough to call from a per-frame ImGui
        /// change callback; <see cref="FlushPending"/> does the write.
        /// </summary>
        public void Save() => _dirty = true;

        /// <summary>Writes a pending change once the coalescing interval has elapsed.</summary>
        public void FlushPending()
        {
            if (_dirty && _sinceWrite.Elapsed >= FlushInterval)
                Flush();
        }

        /// <summary>Writes a pending change now. Called on shutdown so nothing is lost.</summary>
        public void Flush()
        {
            if (!_dirty)
                return;
            _dirty = false;
            _sinceWrite.Restart();
            WriteToDisk();
        }

        void WriteToDisk()
        {
            try
            {
                AtomicFile.Write(
                    FilePath,
                    Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(this, Formatting.Indented))
                );
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Config] Failed to save: {ex.Message}");
            }
        }
    }
}
