using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime;
using CafeStudio.UI;
using GLFrameworkEngine;
using ImGuiNET;
using OpenTK;
using OpenTK.Graphics;
using OpenTK.Graphics.OpenGL;
using OpenTK.Input;
using PlayerViewer.Core;
using PlayerViewer.Player;

namespace PlayerViewer.UI
{
    /// <summary>
    /// Main interactive window: 3D viewport + player configuration UI.
    ///
    /// The class is split across several files by concern (all <c>partial class
    /// ViewerWindow</c>): this file owns the window lifecycle and scene loading;
    /// <c>ViewerWindow.Layout.cs</c> the top-level layout/menu; <c>*.PlayerPanel.cs</c>,
    /// <c>*.Viewport.cs</c>, <c>*.CapturePanel.cs</c> and <c>*.Standalone.cs</c> the
    /// respective UI sections.
    /// </summary>
    public partial class ViewerWindow : GameWindow
    {
        readonly AppConfig _config;

        ImGuiController _imgui;
        ScenePipeline _pipeline;
        Romfs _romfs;
        GameDatabase _db;
        PlayerScene _scene;
        readonly VideoRecorder _recorder = new();

        //--- UI state
        string _romfsInput = "";
        string _sdodrInput = "";
        string _layeredInput = "";
        string _romfsError = null;
        bool _needsLoad;
        bool _preserveStateOnLoad;
        string _animSearch = "";
        int _teamColorIndex;
        int _teamIndex;
        bool _useCustomTeamColor = true;
        readonly TeamColorSet _customTeam = new()
        {
            Name = "Custom",
            Alpha = new System.Numerics.Vector3(0.925f, 0.243f, 0.549f),
            Bravo = new System.Numerics.Vector3(0.196f, 0.855f, 0.302f),
            Charlie = new System.Numerics.Vector3(0.980f, 0.769f, 0.196f),
            Neutral = new System.Numerics.Vector3(0.56f, 0.55f, 0.43f),
        };
        float _uiFrame;   //frame slider mirror
        int _captureRes = 2;   //index into CaptureSizes
        bool _captureTransparent = true;

        //--- Standalone model viewing (dropped/browsed files, outside the player)
        StandaloneScene _standalone;
        string _standaloneError;

        //--- dev self-test: save a window screenshot after N frames and exit
        public string AutoScreenshotPath;
        public int AutoScreenshotFrame = 40;
        public string AutoOpenFile;   //opens a standalone model right after load
        public string AutoRecordPath; //records N frames of video then exits
        public int AutoRecordFrames = 120;
        int _frameCounter;

        //--- Deterministic full-animation export: drives the timeline frame-by-frame
        //(ignoring wall clock) so every animation frame lands exactly once. Reuses the
        //current camera and the chosen background (greenscreen for MP4, alpha for WebP).
        bool _animExporting;
        float _animExportIndex;    //current animation-frame position being captured
        int _animExportTotal;      //frame count of the animation
        float _animExportAdvance;  //animation frames advanced per output frame ((60/fps) * speed)
        int _exportFps = 60;       //two-tick control: 30 or 60
        bool _animExportTransparent;
        bool _animExportTrim;      //snapshot of TrimDeadspace taken at export start
        VideoRecorder.OutputFormat _animExportFormat;
        System.Numerics.Vector3 _animExportGreen;
        bool _animExportPrevPaused;
        float _animExportPrevFrame;
        System.Numerics.Vector3 _animExportPrevBg;
        BufferedAnimExporter _bufferedExporter;   //non-null during the trim (buffered) export

        //--- Unified capture UI
        int _exportFormat;   //0 = PNG, 1 = MP4, 2 = WebP, 3 = Record (real-time)
        bool _showSettings;
        //Self-correcting layout: capture controls stay pinned, the animation list absorbs
        //slack. We size the list from last frame's measured control height.
        float _measuredCaptureHeight = 220;
        float _measuredStandaloneTailHeight = 160;

        public ViewerWindow(AppConfig config)
            : base(config.WindowWidth, config.WindowHeight,
                  new GraphicsMode(new ColorFormat(32), 24, 8, 4, new ColorFormat(32), 2, false),
                  "Splatoon 3 Player Viewer",
                  GameWindowFlags.Default, DisplayDevice.Default, 3, 2, GraphicsContextFlags.Default)
        {
            _config = config;
            _romfsInput = config.RomfsPath ?? "";
            _sdodrInput = config.SdodrRomfsPath ?? "";
            _layeredInput = config.LayeredFsPath ?? "";
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);

            //Anchor Toolbox's Shaders/Plugins/Hashes lookups to the exe directory. Its default
            //comes from Assembly.Location, which is empty under single-file publish and would null
            //those paths (crashing the plugin scan). AppContext.BaseDirectory is always correct.
            Toolbox.Core.Runtime.ExecutableDir =
                AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            _imgui = new ImGuiController(Width, Height);
            Theme.Apply();
            ImGui.GetIO().ConfigWindowsMoveFromTitleBarOnly = true;

            RenderTools.Init();
            Toolbox.Core.FileManager.GetFileFormats();

            //Interactive mode: compile uncached shader programs asynchronously so a
            //new gear's shaders never stall the render thread (meshes pop in a few
            //frames later instead).
            BfresEditor.TegraShaderDecoder.AllowDeferredCompile = true;

            _pipeline = new ScenePipeline();
            _pipeline.Init();

            if (Romfs.IsValidRoot(_config.RomfsPath))
                _needsLoad = true;
        }

        void LoadGame()
        {
            _needsLoad = false;
            var state = _preserveStateOnLoad && _scene != null ? SceneState.Capture(_scene) : null;
            _preserveStateOnLoad = false;
            try
            {
                _standalone?.Dispose();
                _standalone = null;
                _scene?.Dispose();
                _scene = null;
                CompactHeap();

                _romfs = new Romfs(_config.RomfsPath, _config.LayeredFsPath, _config.UseLayeredFs,
                    _config.SdodrRomfsPath);
                BfresEditor.HoianNXRender.GamePath = _config.RomfsPath;
                //Decompress/parse the ~25MB UBER shader archive while the database loads.
                BfresEditor.HoianNXRender.PrewarmShaderArchives();
                //Load default/cubemap textures now instead of during the first material render.
                BfresEditor.HoianNXRender.InitTextures();
                _db = new GameDatabase(_romfs);

                _scene = new PlayerScene(_romfs, _db);
                if (state != null)
                {
                    state.Restore(_scene, _db);
                    _teamColorIndex = Math.Min(_teamColorIndex, Math.Max(_db.TeamColors.Count - 1, 0));
                }
                else if (_config.Player?.Hair != null || _config.Player?.PlayerType != 0)
                {
                    RestorePlayerConfig();
                }
                else
                {
                    _scene.SetPlayerType(0);
                    _pipeline.FramePlayer();
                }
                ApplyTeamColor();
                _romfsError = null;
            }
            catch (Exception ex)
            {
                _romfsError = ex.Message;
                Console.WriteLine($"[UI] Load failed: {ex}");
            }
        }

        /// <summary>Snapshot of the scene configuration, reapplied after a LayeredFS reload.</summary>
        class SceneState
        {
            int _playerType;
            int _eye, _skin;
            string _anim;
            float _frame;
            bool _paused;
            readonly Dictionary<GearSlot, (string RowId, int Variation, string CustomPath)> _gear = new();

            public static SceneState Capture(PlayerScene scene)
            {
                var s = new SceneState
                {
                    _playerType = scene.PlayerType,
                    _eye = scene.EyeColor,
                    _skin = scene.SkinTone,
                    _anim = scene.CurrentAnimName,
                    _frame = scene.AnimFrame,
                    _paused = scene.AnimPaused,
                };
                void Add(GearSlot slot, GearEntry e)
                {
                    if (e != null) s._gear[slot] = (e.RowId, e.Variation, e.CustomPath);
                    else s._gear[slot] = (null, 0, null);
                }
                Add(GearSlot.Hair, scene.CurrentHair);
                Add(GearSlot.Eyebrow, scene.CurrentEyebrow);
                Add(GearSlot.Head, scene.CurrentHead);
                Add(GearSlot.Clothes, scene.CurrentClothes);
                Add(GearSlot.Bottom, scene.CurrentBottom);
                Add(GearSlot.Shoes, scene.CurrentShoes);
                Add(GearSlot.Tank, scene.CurrentTank);
                Add(GearSlot.MainWeapon, scene.CurrentWeapon);
                return s;
            }

            public void Restore(PlayerScene scene, GameDatabase db)
            {
                scene.SetPlayerType(_playerType);
                foreach (var (slot, gear) in _gear)
                {
                    //Defaults set by SetPlayerType stand in when the row disappeared.
                    if (gear.RowId == null)
                    {
                        if (slot is GearSlot.Head or GearSlot.Clothes or GearSlot.Shoes or GearSlot.Tank or GearSlot.MainWeapon)
                            scene.SetGear(slot, null);
                        continue;
                    }
                    var entry = db.GetList(slot).FirstOrDefault(x =>
                        x.RowId == gear.RowId && x.Variation == gear.Variation);
                    if (entry != null)
                        scene.SetGear(slot, entry);
                }
                scene.ApplyEyeColor(_eye);
                scene.ApplySkinTone(_skin);
                if (_anim != null)
                {
                    scene.PlayAnim(_anim);
                    scene.SetAnimFrame(_frame);
                }
                scene.AnimPaused = _paused;
            }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            _imgui?.WindowResized(Width, Height);
        }

        protected override void OnKeyPress(KeyPressEventArgs e)
        {
            base.OnKeyPress(e);
            _imgui.PressChar(e.KeyChar);
        }

        protected override void OnFileDrop(FileDropEventArgs e)
        {
            base.OnFileDrop(e);
            string file = e.FileName;
            if (file == null || (!file.EndsWith(".bfres") && !file.EndsWith(".bfres.zs") && !file.EndsWith(".zs")))
                return;
            OpenStandalone(file);
        }

        /// <summary>Opens a loose bfres as a standalone model (no player).</summary>
        void OpenStandalone(string file)
        {
            if (_romfs == null)
                return;
            try
            {
                bool hadPrevious = _standalone != null;
                _standalone?.Dispose();
                _standalone = null;
                if (hadPrevious)
                    CompactHeap();

                _standalone = StandaloneScene.FromFile(file, _romfs);
                _standaloneError = _standalone == null ? "Failed to load model" : null;
                if (_standalone != null)
                {
                    _animSearch = "";
                    _pipeline.FrameSphere(_standalone.GetBounding());
                }
            }
            catch (Exception ex)
            {
                _standaloneError = ex.Message;
                Console.WriteLine($"[UI] Standalone load failed: {ex}");
            }
        }

        void CloseStandalone()
        {
            _standalone?.Dispose();
            _standalone = null;
            _standaloneError = null;
            _animSearch = "";
            CompactHeap();
            _pipeline.FramePlayer();
        }

        static void CompactHeap()
        {
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
        }

        protected override void OnClosed(EventArgs e)
        {
            _recorder.Dispose();
            //Aborts any in-flight capture/encode and deletes the temp raw buffer.
            _bufferedExporter?.Dispose();
            //Width/Height are 0 when closed while minimized; don't persist that.
            if (WindowState == WindowState.Normal && Width > 0 && Height > 0)
            {
                _config.WindowWidth = Width;
                _config.WindowHeight = Height;
            }
            _config.Save();
            base.OnClosed(e);
        }

        protected override void OnRenderFrame(FrameEventArgs e)
        {
            base.OnRenderFrame(e);
            GLFrameworkEngine.ShaderProgram.FrameStamp++;

            if (_needsLoad)
            {
                LoadGame();
                if (AutoOpenFile != null && _scene != null)
                {
                    OpenStandalone(AutoOpenFile);
                    AutoOpenFile = null;
                }
            }

            //Advance whichever scene is active. During export we drive the timeline
            //deterministically (fixed frame + fixed dt) instead of by real time.
            if (_animExporting)
            {
                PlaybackSetFrame(_animExportIndex);
                //Cloth dt is wall-clock per output frame (1/fps), independent of playback
                //speed; matches the viewport, where the sim advances in real time and the
                //speed slider only scales how fast the animation cursor moves.
                PlaybackUpdate(1f / _exportFps);
                _uiFrame = PlaybackAnimFrame;
            }
            else if (_standalone != null)
            {
                _standalone.Update((float)e.Time);
                _uiFrame = _standalone.AnimFrame;
            }
            else if (_scene != null)
            {
                _scene.Update((float)e.Time);
                _uiFrame = _scene.AnimFrame;
            }

            _imgui.Update(this, (float)e.Time);
            DrawUI();

            GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            GL.Viewport(0, 0, Width, Height);
            GL.ClearColor(0.04f, 0.04f, 0.05f, 1);
            GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
            _imgui.Render();

            if (AutoRecordPath != null && _scene != null)
            {
                if (!_recorder.IsRecording && _frameCounter < AutoRecordFrames)
                    StartRecording(AutoRecordPath);
                else if (_recorder.IsRecording && _recorder.FrameCount >= AutoRecordFrames)
                {
                    StopRecording();
                    Console.WriteLine($"[UI] Recorded {AutoRecordPath}");
                    AutoRecordPath = null;
                    if (AutoScreenshotPath == null)
                        Close();
                }
            }

            if (AutoScreenshotPath != null && ++_frameCounter >= AutoScreenshotFrame)
            {
                if (ActiveScene != null)
                {
                    using var dbg = _pipeline.Capture(ActiveScene, 512, 512, _pipeline.BackgroundColor, _captureTransparent);
                    SixLabors.ImageSharp.ImageExtensions.SaveAsPng(dbg, AutoScreenshotPath + ".capture.png");
                }
                var pixels = new byte[Width * Height * 4];
                GL.ReadBuffer(ReadBufferMode.Back);
                GL.ReadPixels(0, 0, Width, Height, OpenTK.Graphics.OpenGL.PixelFormat.Rgba, PixelType.UnsignedByte, pixels);
                FlipRowsVertical(pixels, Width, Height);
                using var img = SixLabors.ImageSharp.Image.LoadPixelData<SixLabors.ImageSharp.PixelFormats.Rgba32>(pixels, Width, Height);
                SixLabors.ImageSharp.ImageExtensions.SaveAsPng(img, AutoScreenshotPath);
                Console.WriteLine($"[UI] Screenshot saved {AutoScreenshotPath}");
                Close();
            }

            SwapBuffers();

            //Frame-exact export: render this frame with the chosen background and push
            //it synchronously (no PBO latency), then advance to the next animation frame.
            if (_animExporting)
            {
                CaptureAnimExportFrame();
            }
            //Real-time recording: check timing BEFORE the readback so we skip the GPU
            //transfer entirely for frames we'd drop.
            else if (_recorder.IsCaptureDue())
            {
                var pixels = _pipeline.ReadFinalPixelsAsync(out _);
                if (pixels != null)
                    _recorder.PushFrame(pixels, _pipeline.Width, _pipeline.Height);
            }
        }

        //Flips RGBA8 rows in place (OpenGL's bottom-up readback -> top-down image order).
        static void FlipRowsVertical(byte[] pixels, int width, int height)
        {
            int stride = width * 4;
            byte[] tmp = new byte[stride];
            for (int y = 0; y < height / 2; y++)
            {
                int top = y * stride, bot = (height - 1 - y) * stride;
                Array.Copy(pixels, top, tmp, 0, stride);
                Array.Copy(pixels, bot, pixels, top, stride);
                Array.Copy(tmp, 0, pixels, bot, stride);
            }
        }
    }
}
