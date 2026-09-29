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
using PlayerViewer.Effects.Viewer;
using PlayerViewer.Player;

namespace PlayerViewer.UI
{
    /// <summary>
    /// The main window. This file holds its lifecycle and scene loading; the other
    /// ViewerWindow files hold one panel each.
    /// </summary>
    public partial class ViewerWindow : GameWindow
    {
        readonly AppConfig _config;

        ImGuiController _imgui;
        ScenePipeline _pipeline;
        Romfs _romfs;
        GameDatabase _db;
        PlayerScene _scene;

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
        float _uiFrame; //frame slider mirror
        int _captureRes = 2; //index into CaptureSizes

        //A model opened on its own, outside the player.
        StandaloneScene _standalone;
        string _standaloneError;

        public string AutoOpenFile; //--open <file>: opens a standalone model right after load

        //An animation export drives the timeline frame by frame rather than by the clock, so
        //every frame lands exactly once.
        bool _animExporting;
        float _animExportIndex; //current animation-frame position being captured
        int _animExportTotal; //frame count of the animation
        float _animExportAdvance; //animation frames advanced per output frame ((60/fps) * speed)
        int _exportFps = 60; //30 or 60
        bool _animExportTrim; //snapshot of TrimDeadspace taken at export start
        int _animExportSupersample; //snapshot of ExportSupersample taken at export start
        bool _animExportChain; //exporting the whole sequence (Sequence mode) vs a single anim
        OutputFormat _animExportFormat;
        byte[] _animExportBg; //full-frame composite background (null = keep alpha)
        bool _animExportPrevPaused;
        float _animExportPrevFrame;
        BufferedAnimExporter _bufferedExporter; //capturing or encoding

        /// <summary>
        /// An export is capturing or encoding. It drives the scene it started on until it ends,
        /// so nothing opens or closes a scene meanwhile.
        /// </summary>
        bool ExportBusy => _animExporting || _bufferedExporter != null;

        //A file dropped during an export, opened once the export has ended.
        string _dropAfterExport;

        int _exportFormat; //row of ExportFormats
        bool _showSettings;

        //The background is saved with the preset; these track when the viewport's copy is rebuilt.
        Core.BackgroundConfig Bg => _config.Player.Background;
        bool _bgDirty = true; //rebuild the live preview buffer on next frame
        int _bgPreviewW = -1,
            _bgPreviewH = -1;

        //Last frame's heights of the controls under the lists, which the lists are sized around.
        float _measuredCaptureHeight = 220;
        float _measuredStandaloneTailHeight = 160;

        public ViewerWindow(AppConfig config)
            : base(
                config.WindowWidth,
                config.WindowHeight,
                new GraphicsMode(new ColorFormat(32), 24, 8, 4, new ColorFormat(32), 2, false),
                "Splatoon 3 Player Viewer",
                GameWindowFlags.Default,
                DisplayDevice.Default,
                3,
                2,
                GraphicsContextFlags.Default
            )
        {
            _config = config;
            _romfsInput = config.RomfsPath ?? "";
            _sdodrInput = config.SdodrRomfsPath ?? "";
            _layeredInput = config.LayeredFsPath ?? "";

            //Clamped in case the ranges changed.
            _captureRes = Math.Clamp(config.CaptureResIndex, 0, CaptureSizes.Length - 1);
            _exportFormat = Math.Clamp(config.ExportFormat, 0, ExportFormatLabels.Length - 1);
            _exportFps = config.ExportFps == 30 ? 30 : 60;
            _animMode = config.AnimMode == 1 ? 1 : 0;

            config.Normalize();
            InstallTitleBar();
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);

            Console.WriteLine(
                $"[GL] {GL.GetString(StringName.Renderer)} ({GL.GetString(StringName.Vendor)})"
            );
            Console.WriteLine(
                "[GL] parallel shader compile: "
                    + (
                        GLFrameworkEngine.ShaderProgram.SupportsParallelCompile
                            ? "yes"
                            : "no, links block the render thread"
                    )
            );

            //Anchor Toolbox's Shaders/Plugins/Hashes lookups to the exe directory. Its default
            //comes from Assembly.Location, which is empty under single-file publish and would null
            //those paths (crashing the plugin scan). AppContext.BaseDirectory is always correct.
            Toolbox.Core.Runtime.ExecutableDir = AppContext.BaseDirectory.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar
            );

            InitAppearance();
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
                TearDownEffect();
                _romfsEffects = null;
                TearDownStandalone();
                //The ubershader and its option table come out of the romfs being replaced.
                DisposeVariations();
                _scene?.Dispose();
                _scene = null;
                ReleaseShaderPrograms();
                CompactHeap();

                _romfs = new Romfs(
                    _config.RomfsPath,
                    _config.LayeredFsPath,
                    _config.UseLayeredFs,
                    _config.SdodrRomfsPath
                );
                BfresEditor.HoianNXRender.GamePath = _config.RomfsPath;
                //Decompress/parse the ~25MB UBER shader archive while the database loads.
                BfresEditor.HoianNXRender.PrewarmShaderArchives();
                //Load default/cubemap textures now instead of during the first material render.
                BfresEditor.HoianNXRender.InitTextures();
                _db = new GameDatabase(_romfs);
                if (_db.TeamColorOffsets != null)
                    BfresEditor.TeamColorVariants.Offsets = _db.TeamColorOffsets;
                InitLists();

                _scene = new PlayerScene(_romfs, _db);
                if (state != null)
                {
                    state.Restore(_scene, _db);
                    _teamColorIndex = Math.Min(
                        _teamColorIndex,
                        Math.Max(_db.TeamColors.Count - 1, 0)
                    );
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
            int _eye,
                _skin;
            string _anim;
            float _frame;
            bool _paused;
            readonly Dictionary<GearSlot, (string RowId, int Variation, string CustomPath)> _gear =
                new();

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
                    if (e != null)
                        s._gear[slot] = (e.RowId, e.Variation, e.CustomPath);
                    else
                        s._gear[slot] = (null, 0, null);
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
                        if (
                            slot
                            is GearSlot.Head
                                or GearSlot.Clothes
                                or GearSlot.Shoes
                                or GearSlot.Tank
                                or GearSlot.MainWeapon
                        )
                            scene.SetGear(slot, null);
                        continue;
                    }
                    var entry = db.GetList(slot)
                        .FirstOrDefault(x =>
                            x.RowId == gear.RowId && x.Variation == gear.Variation
                        );
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

        //ImGui's key state comes from these rather than from a poll of the keyboard device,
        //which reports nothing in this OpenTK build.
        protected override void OnKeyDown(KeyboardKeyEventArgs e)
        {
            base.OnKeyDown(e);
            _imgui.KeyDown(e.Key);
        }

        protected override void OnKeyUp(KeyboardKeyEventArgs e)
        {
            base.OnKeyUp(e);
            _imgui.KeyUp(e.Key);
        }

        protected override void OnMouseWheel(MouseWheelEventArgs e)
        {
            base.OnMouseWheel(e);
            _imgui.MouseWheel(e.Mouse.Scroll.X, e.Mouse.Scroll.Y);
        }

        protected override void OnFileDrop(FileDropEventArgs e)
        {
            base.OnFileDrop(e);
            OpenDroppedFile(e.FileName);
        }

        void OpenDroppedFile(string file)
        {
            if (ExportBusy)
            {
                _dropAfterExport = file;
                Console.WriteLine($"[UI] {file} opens once the export has ended");
                return;
            }
            if (EffectFile.IsEffectPath(file))
            {
                OpenEffect(file);
                return;
            }
            if (
                file == null
                || (
                    !file.EndsWith(".bfres") && !file.EndsWith(".bfres.zs") && !file.EndsWith(".zs")
                )
            )
                return;
            OpenStandalone(file);
        }

        /// <summary>Opens a loose bfres as a standalone model (no player).</summary>
        void OpenStandalone(string file)
        {
            if (_scene == null || ExportBusy)
                return;
            try
            {
                bool hadPrevious = _standalone != null || _effect != null;
                TearDownEffect();
                TearDownStandalone();
                //Off for every model: on, it splices the whole model, which is minutes of CPU,
                //and opening a file is usually to look at it.
                SetSplicer(false);
                _bundlePruned = false;
                _bundleNote = null;
                if (hadPrevious)
                    CompactHeap();

                _standalone = StandaloneScene.FromFile(file, _romfs);
                _standaloneError = _standalone == null ? "Failed to load model" : null;
                if (_standalone != null)
                {
                    DetectProvenance();
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
            if (ExportBusy)
                return;
            TearDownStandalone();
            CompactHeap();
            _pipeline.FramePlayer();
        }

        /// <summary>
        /// The one way a standalone model goes away, whichever door it leaves through: the
        /// scene, the selections, the splice grids and the editor state all go together. The
        /// ubershader stays, since it belongs to the romfs rather than to the model.
        /// </summary>
        void TearDownStandalone()
        {
            _standalone?.Dispose();
            _standalone = null;
            _standaloneError = null;
            _selectedMaterial = null;
            _selectedTexture = null;
            _animSearch = "";
            ResetVariations();
            ResetMaterialEditor();
            ResetClothEditor();
            _authoringOpen = false;
            ResetSkeletonTab();
            ReleaseShaderPrograms();
        }

        //The scene's renderers have handed their programs back, so what is left unheld can
        //go. The disk cache brings a program back at its warm cost when it is next needed.
        static void ReleaseShaderPrograms()
        {
            int freed = BfresEditor.TegraShaderDecoder.ReleaseUnused();
            if (freed > 0)
                Console.WriteLine($"[GL] Released {freed} shader program(s)");
        }

        static void CompactHeap()
        {
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
        }

        protected override void OnUnload(EventArgs e)
        {
            //Still has the GL context, which the icon textures need to be deleted.
            DisposeLists();
            _background?.Dispose();
            base.OnUnload(e);
        }

        protected override void OnClosed(EventArgs e)
        {
            //Aborts any in-flight capture/encode and deletes the temp raw buffer.
            _bufferedExporter?.Dispose();
            //Stops the loop analysis thread.
            _effectFile?.Dispose();
            //Kills any specialiser still running.
            DisposeVariations();
            //Width/Height are 0 when closed while minimized; don't persist that. The height is the
            //OS bar's client, which the next launch creates the window with before the bar folds in.
            if (WindowState == WindowState.Normal && Width > 0 && Height > 0)
            {
                _config.WindowWidth = Width;
                _config.WindowHeight = NativeClientHeight;
            }
            _config.Save();
            //Save only marks the config dirty; write it out before the process goes away.
            _config.Flush();
            base.OnClosed(e);
        }

        //The frame's clock. GameWindow's e.Time does not know about the frames the title bar draws
        //inside a move or resize, so after one it would count that time twice.
        readonly System.Diagnostics.Stopwatch _frameClock = System.Diagnostics.Stopwatch.StartNew();
        double _lastFrameTime = -1;

        //A nested message loop inside a frame (a native dialog) must not start another one.
        bool _inFrame;

        protected override void OnRenderFrame(FrameEventArgs e)
        {
            if (_inFrame)
                return;
            _inFrame = true;
            try
            {
                base.OnRenderFrame(e);
                //Minimised, nothing can be seen, so the frame is skipped rather than spun,
                //unless an export is running. The clock restarts, so the restore is no big step.
                if (WindowState == WindowState.Minimized && !ExportBusy)
                {
                    _lastFrameTime = -1;
                    _config.FlushPending();
                    System.Threading.Thread.Sleep(50);
                    return;
                }
                double now = _frameClock.Elapsed.TotalSeconds;
                double dt =
                    _lastFrameTime < 0 ? 1.0 / 60 : Math.Clamp(now - _lastFrameTime, 1e-5, 1);
                _lastFrameTime = now;
                RenderFrame((float)dt);
            }
            finally
            {
                _inFrame = false;
            }
        }

        //Test hooks, implemented in files a release build leaves out; without them the calls
        //compile away. The frame's end is once the UI has rendered, before the swap.
        partial void TestHookFrameBegin();

        partial void TestHookFrameEnd();

        partial void TestHookFrameSwapped();

        partial void TestHookOverride(string name, ref bool value);

        partial void TestHookNote(string what, object data = null);

        void RenderFrame(float dt)
        {
            TestHookFrameBegin();
            GLFrameworkEngine.ShaderProgram.FrameStamp++;

            if (_needsLoad && !ExportBusy)
            {
                LoadGame();
                if (AutoOpenFile != null && _scene != null)
                {
                    if (EffectFile.IsEffectPath(AutoOpenFile))
                        OpenEffect(AutoOpenFile, AutoOpenSet);
                    else
                        OpenStandalone(AutoOpenFile);
                    AutoOpenFile = null;
                }
            }
            if (_dropAfterExport != null && !ExportBusy)
            {
                string waiting = _dropAfterExport;
                _dropAfterExport = null;
                OpenDroppedFile(waiting);
            }

            //Advances the active scene; an export sets the frame and the step itself.
            var update = FramePerf.Section("update", gpu: false);
            if (_animExporting)
            {
                //Chain export walks the concatenated sequence; single export scrubs one anim.
                if (_effect != null)
                    EffectPlay.Seek(_effectExportStart + (int)_animExportIndex);
                else if (_animExportChain)
                    ChainSeek(_animExportIndex);
                else
                    PlaybackSetFrame(_animExportIndex);
                //Cloth steps 1/fps per output frame whatever the speed, as the viewport steps it
                //in real time and the speed only moves the animation.
                if (_effect == null)
                    PlaybackUpdate(1f / _exportFps, ConvergeWeight(_animExportIndex));
                //The pose to converge back to is the one the first exported frame ended on,
                //so it is taken after that frame's step rather than before the export starts.
                if (!_convergeCaptured)
                {
                    _scene?.CaptureHairConvergeState();
                    _convergeCaptured = true;
                }
                _uiFrame = PlaybackAnimFrame;
            }
            else if (_effect != null)
            {
                UpdateEffect(dt);
            }
            else if (_chainActive)
            {
                UpdateAnimChain(dt);
                _uiFrame = PlaybackAnimFrame;
            }
            else if (_standalone != null)
            {
                UpdateStandalone(dt);
                _uiFrame = _standalone.AnimFrame;
            }
            else if (_scene != null)
            {
                _scene.Update(dt);
                _uiFrame = _scene.AnimFrame;
            }

            update.Dispose();

            UpdateAppearance();
            UpdateFonts();
            UpdateSideOrder(dt);
            using (FramePerf.Section("imgui new frame", gpu: false))
                _imgui.Update(this, dt);
            BeginBarFrame();
            bool drawUi = true;
            TestHookOverride("ui", ref drawUi);
            if (drawUi)
                using (FramePerf.Section("ui"))
                    DrawUI();
            EndBarFrame();
            using (FramePerf.Section("pumps", gpu: false))
            {
                PumpVariations();
                _icons?.Pump();
            }

            GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            GL.Viewport(0, 0, Width, Height);
            GL.ClearColor(0.04f, 0.04f, 0.05f, 1);
            GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
            DrawSideOrderBackground();
            using (FramePerf.Section("imgui render"))
                _imgui.Render();
            TestHookFrameEnd();

            SwapBuffers();
            TestHookFrameSwapped();

            //Frame-exact export: capture this frame synchronously, then advance the timeline.
            if (_animExporting)
                CaptureAnimExportFrame();

            _config.FlushPending();
        }
    }
}
