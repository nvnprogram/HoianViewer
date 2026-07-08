using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Vector2 = System.Numerics.Vector2;
using Vector4 = System.Numerics.Vector4;
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
    /// </summary>
    public class ViewerWindow : GameWindow
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

        static readonly (string Label, int W, int H)[] CaptureSizes =
        {
            ("1280 x 1280", 1280, 1280),
            ("1920 x 1080", 1920, 1080),
            ("3840 x 2160 (4K)", 3840, 2160),
            ("2160 x 3840 (4K portrait)", 2160, 3840),
        };

        static readonly string[] PlayerTypes =
        {
            "Inkling Girl (Player00)",
            "Inkling Boy (Player01)",
            "Octoling Girl (Player02)",
            "Octoling Boy (Player03)",
        };

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

        //--- viewport camera input
        bool _viewportHovered;
        bool _mouseDown;

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
                _standalone?.Dispose();
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
            _pipeline.FramePlayer();
        }

        protected override void OnClosed(EventArgs e)
        {
            _recorder.Dispose();
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

            //Advance whichever scene is active
            if (_standalone != null)
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
                    dbg.Save(AutoScreenshotPath + ".capture.png");
                }
                var pixels = new byte[Width * Height * 4];
                GL.ReadBuffer(ReadBufferMode.Back);
                GL.ReadPixels(0, 0, Width, Height, OpenTK.Graphics.OpenGL.PixelFormat.Bgra, PixelType.UnsignedByte, pixels);
                var bmp = Framebuffer.GetBitmap(Width, Height, pixels);
                bmp.RotateFlip(System.Drawing.RotateFlipType.RotateNoneFlipY);
                bmp.Save(AutoScreenshotPath);
                Console.WriteLine($"[UI] Screenshot saved {AutoScreenshotPath}");
                Close();
            }

            SwapBuffers();

            //Record after the frame is fully rendered. Check timing BEFORE the
            //readback so we skip the GPU transfer entirely for frames we'd drop.
            if (_recorder.IsCaptureDue())
            {
                var pixels = _pipeline.ReadFinalPixelsAsync(out _);
                if (pixels != null)
                    _recorder.PushFrame(pixels, _pipeline.Width, _pipeline.Height);
            }
        }

        #region UI layout

        void DrawUI()
        {
            var viewport = ImGui.GetMainViewport();
            ImGui.SetNextWindowPos(viewport.Pos);
            ImGui.SetNextWindowSize(viewport.Size);
            ImGui.Begin("##host",
                ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove |
                ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoBringToFrontOnFocus | ImGuiWindowFlags.NoNavFocus |
                ImGuiWindowFlags.MenuBar);

            DrawMenuBar();

            if (_scene == null)
            {
                DrawRomfsSetup();
                ImGui.End();
                return;
            }

            float leftWidth = 330;
            float rightWidth = 300;

            ImGui.BeginChild("##left", new Vector2(leftWidth, 0), true);
            if (_standalone != null)
                DrawStandalonePanel();
            else
                DrawPlayerPanel();
            ImGui.EndChild();

            ImGui.SameLine();
            ImGui.BeginChild("##center", new Vector2(-rightWidth - 8, 0), false,
                ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
            DrawViewport();
            ImGui.EndChild();

            ImGui.SameLine();
            ImGui.BeginChild("##right", new Vector2(0, 0), true);
            DrawAnimationPanel();
            DrawCapturePanel();
            ImGui.EndChild();

            ImGui.End();
        }

        void DrawMenuBar()
        {
            if (!ImGui.BeginMenuBar())
                return;

            ImGui.PushStyleColor(ImGuiCol.Text, Theme.Gold);
            ImGui.Text("PLAYER VIEWER");
            ImGui.PopStyleColor();
            ImGui.Separator();

            if (ImGui.BeginMenu("File"))
            {
                if (ImGui.MenuItem("Change romfs path..."))
                {
                    _scene?.Dispose();
                    _scene = null;
                    _romfsInput = _config.RomfsPath ?? "";
                    _sdodrInput = _config.SdodrRomfsPath ?? "";
                }
                if (ImGui.MenuItem("View model file... (or drag && drop)"))
                {
                    string file = NativeFolderPicker.OpenFile("Open Model", "BFRES models (*.bfres;*.zs)", "*.bfres;*.zs");
                    if (!string.IsNullOrEmpty(file))
                        OpenStandalone(file);
                }
                if (_standalone != null && ImGui.MenuItem("Back to player"))
                    CloseStandalone();
                ImGui.Separator();
                if (ImGui.MenuItem("Exit"))
                    Close();
                ImGui.EndMenu();
            }

            if (_romfs != null)
            {
                ImGui.SameLine(ImGui.GetWindowWidth() - 320);
                ImGui.TextColored(Theme.TextDim, TruncatePath(_config.RomfsPath, 42));
            }

            ImGui.EndMenuBar();
        }

        static string TruncatePath(string path, int max)
        {
            if (string.IsNullOrEmpty(path) || path.Length <= max)
                return path ?? "";
            return "..." + path.Substring(path.Length - max + 3);
        }

        void DrawRomfsSetup()
        {
            var avail = ImGui.GetContentRegionAvail();
            ImGui.SetCursorPos(new Vector2(avail.X / 2 - 260, avail.Y / 2 - 90));
            ImGui.BeginChild("##setup", new Vector2(520, 210), true);

            ImGui.PushStyleColor(ImGuiCol.Text, Theme.Gold);
            ImGui.Text("Splatoon 3 romfs path");
            ImGui.PopStyleColor();
            ImGui.Spacing();

            ImGui.SetNextItemWidth(-90);
            ImGui.InputText("##romfs", ref _romfsInput, 512);
            ImGui.SameLine();
            if (ImGui.Button("Browse..."))
            {
                string folder = NativeFolderPicker.SelectFolder("Select romfs folder", _romfsInput);
                if (!string.IsNullOrEmpty(folder))
                    _romfsInput = folder;
            }

            ImGui.Spacing();
            bool valid = Romfs.IsValidRoot(_romfsInput);
            if (!valid && !string.IsNullOrEmpty(_romfsInput))
                ImGui.TextColored(new Vector4(0.9f, 0.35f, 0.3f, 1), "Not a valid romfs (needs Model/ + RSDB/)");
            if (_romfsError != null)
                ImGui.TextColored(new Vector4(0.9f, 0.35f, 0.3f, 1), _romfsError);

            ImGui.Spacing();
            ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1), "Side Order DLC romfs (optional)");
            ImGui.SetNextItemWidth(-90);
            ImGui.InputText("##sdodr", ref _sdodrInput, 512);
            ImGui.SameLine();
            if (ImGui.Button("Browse...##sdodr"))
            {
                string folder = NativeFolderPicker.SelectFolder("Select Side Order romfs folder", _sdodrInput);
                if (!string.IsNullOrEmpty(folder))
                    _sdodrInput = folder;
            }

            ImGui.Spacing();
            if (!valid) ImGui.PushStyleVar(ImGuiStyleVar.Alpha, 0.45f);
            if (ImGui.Button("Load", new Vector2(120, 0)) && valid)
            {
                _config.RomfsPath = _romfsInput;
                _config.SdodrRomfsPath = _sdodrInput;
                _config.Save();
                _needsLoad = true;
            }
            if (!valid) ImGui.PopStyleVar();

            ImGui.EndChild();
        }

        #endregion

        #region Player panel

        void DrawPlayerPanel()
        {
            Widgets.SectionHeader("Player");

            if (ImGui.Button("Reset", new Vector2(-1, 0)))
                ResetPlayerDefaults();

            int playerType = _scene.PlayerType;
            ImGui.SetNextItemWidth(-1);
            if (ImGui.Combo("##playertype", ref playerType, PlayerTypes, PlayerTypes.Length))
            {
                _scene.SetPlayerType(playerType);
                ApplyTeamColor();
                SavePlayerConfig();
            }

            GearRow("Hair", GearSlot.Hair, _db.Hair, _scene.CurrentHair);
            GearRow("Eyebrow", GearSlot.Eyebrow, _db.Eyebrow, _scene.CurrentEyebrow);

            int eye = _scene.EyeColor;
            Widgets.LabeledRow("Eyes", () =>
            {
                ImGui.SetNextItemWidth(-1);
                if (ImGui.SliderInt("##eye", ref eye, 0, 20))
                {
                    _scene.ApplyEyeColor(eye);
                    SavePlayerConfig();
                }
            });

            int skin = _scene.SkinTone;
            Widgets.LabeledRow("Skin", () =>
            {
                ImGui.SetNextItemWidth(-1);
                if (ImGui.SliderInt("##skin", ref skin, 0, 8))
                {
                    _scene.ApplySkinTone(skin);
                    SavePlayerConfig();
                }
            });

            bool hairPhys = _scene.HairPhysicsEnabled;
            if (ImGui.Checkbox("Hair physics", ref hairPhys))
            {
                _scene.HairPhysicsEnabled = hairPhys;
                if (hairPhys)
                    _scene.ResetHairPhysics();
            }

            DrawTeamColorSection();

            Widgets.SectionHeader("Gear");
            GearRow("Head", GearSlot.Head, _db.Head, _scene.CurrentHead, allowNone: false);
            GearRow("Clothes", GearSlot.Clothes, _db.Clothes, _scene.CurrentClothes);
            GearRow("Bottom", GearSlot.Bottom, _db.Bottom, _scene.CurrentBottom);
            GearRow("Shoes", GearSlot.Shoes, _db.Shoes, _scene.CurrentShoes);

            Widgets.SectionHeader("Equipment");
            GearRow("Weapon", GearSlot.MainWeapon, _db.MainWeapons, _scene.CurrentWeapon, noneLabel: "Free");
            GearRow("Tank", GearSlot.Tank, _db.Tank, _scene.CurrentTank);

            DrawLightingSection();
            DrawViewSection();
            DrawLayeredFsSection();
        }

        static readonly string[] UniformSetLabels = { "Viewer", "AutoWalk" };
        static readonly string[] UniformSetDirs = { "SPL3", "SPL3_AutoWalk" };

        void DrawViewSection()
        {
            Widgets.SectionHeader("View");
            if (ImGui.Button("Reset camera", new Vector2(-1, 0)))
            {
                if (_standalone != null)
                    _pipeline.FrameSphere(_standalone.GetBounding());
                else
                    _pipeline.FramePlayer();
            }
            var bg = _pipeline.BackgroundColor;
            if (ImGui.ColorEdit3("Background", ref bg, ImGuiColorEditFlags.NoInputs))
                _pipeline.BackgroundColor = bg;

            bool selfShadow = _pipeline.EnableSelfShadow;
            if (ImGui.Checkbox("Self shadow", ref selfShadow))
                _pipeline.EnableSelfShadow = selfShadow;
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Game-accurate self shadowing (gsys_shadow_prepass).");

            int setIdx = Math.Max(Array.IndexOf(UniformSetDirs, BfresEditor.HoianNXRender.UniformSetDir), 0);
            Widgets.LabeledRow("Env", () =>
            {
                ImGui.SetNextItemWidth(-1);
                if (ImGui.Combo("##uniset", ref setIdx, UniformSetLabels, UniformSetLabels.Length))
                {
                    BfresEditor.HoianNXRender.SetUniformSet(UniformSetDirs[setIdx]);
                    ApplyTeamColor();
                }
            });
        }

        void DrawLightingSection()
        {
            Widgets.SectionHeader("Lighting");
            bool followCam = _pipeline.LightFollowsCamera;
            if (ImGui.Checkbox("Light follows camera", ref followCam))
                _pipeline.LightFollowsCamera = followCam;
            if (!followCam)
            {
                float az = _pipeline.LightAzimuth, el = _pipeline.LightElevation;
                ImGui.SetNextItemWidth(-1);
                if (ImGui.SliderFloat("##lightaz", ref az, -180, 180, "Azimuth %.0f\u00b0"))
                    _pipeline.LightAzimuth = az;
                ImGui.SetNextItemWidth(-1);
                if (ImGui.SliderFloat("##lightel", ref el, -89, 89, "Elevation %.0f\u00b0"))
                    _pipeline.LightElevation = el;
            }
        }

        void DrawTeamColorSection()
        {
            Widgets.SectionHeader("Team Color");
            var colorSet = _db.TeamColors.ElementAtOrDefault(_teamColorIndex);
            ImGui.SetNextItemWidth(-1);
            string teamPreview = _useCustomTeamColor ? "Custom" : colorSet?.Name ?? "(default)";
            if (ImGui.BeginCombo("##teamcolor", teamPreview))
            {
                //"Custom" first: freely picked colors instead of an RSDB set.
                ImGui.ColorButton("##swatchCustA", new Vector4(_customTeam.Alpha.X, _customTeam.Alpha.Y, _customTeam.Alpha.Z, 1),
                    ImGuiColorEditFlags.NoTooltip, new Vector2(14, 14));
                ImGui.SameLine();
                ImGui.ColorButton("##swatchCustB", new Vector4(_customTeam.Bravo.X, _customTeam.Bravo.Y, _customTeam.Bravo.Z, 1),
                    ImGuiColorEditFlags.NoTooltip, new Vector2(14, 14));
                ImGui.SameLine();
                if (ImGui.Selectable("Custom", _useCustomTeamColor))
                {
                    _useCustomTeamColor = true;
                    ApplyTeamColor();
                    SavePlayerConfig();
                }

                for (int i = 0; i < _db.TeamColors.Count; i++)
                {
                    var set = _db.TeamColors[i];
                    //Swatch preview
                    ImGui.ColorButton($"##swatchA{i}", new Vector4(set.Alpha.X, set.Alpha.Y, set.Alpha.Z, 1),
                        ImGuiColorEditFlags.NoTooltip, new Vector2(14, 14));
                    ImGui.SameLine();
                    ImGui.ColorButton($"##swatchB{i}", new Vector4(set.Bravo.X, set.Bravo.Y, set.Bravo.Z, 1),
                        ImGuiColorEditFlags.NoTooltip, new Vector2(14, 14));
                    ImGui.SameLine();
                    if (ImGui.Selectable(set.Name, !_useCustomTeamColor && i == _teamColorIndex))
                    {
                        _useCustomTeamColor = false;
                        _teamColorIndex = i;
                        ApplyTeamColor();
                        SavePlayerConfig();
                    }
                }
                ImGui.EndCombo();
            }
            if (_useCustomTeamColor)
            {
                bool custChanged = false;
                var a = _customTeam.Alpha; var b = _customTeam.Bravo; var c = _customTeam.Charlie;
                custChanged |= ImGui.ColorEdit3("Alpha##cust", ref a, ImGuiColorEditFlags.NoInputs);
                ImGui.SameLine();
                custChanged |= ImGui.ColorEdit3("Bravo##cust", ref b, ImGuiColorEditFlags.NoInputs);
                ImGui.SameLine();
                custChanged |= ImGui.ColorEdit3("Charlie##cust", ref c, ImGuiColorEditFlags.NoInputs);
                if (custChanged)
                {
                    _customTeam.Alpha = a; _customTeam.Bravo = b; _customTeam.Charlie = c;
                    _customTeam.Neutral = (a + b) * 0.5f;
                    ApplyTeamColor();
                    SavePlayerConfig();
                }
            }
            if (ImGui.RadioButton("Alpha", _teamIndex == 0)) { _teamIndex = 0; ApplyTeamColor(); SavePlayerConfig(); }
            ImGui.SameLine();
            if (ImGui.RadioButton("Bravo", _teamIndex == 1)) { _teamIndex = 1; ApplyTeamColor(); SavePlayerConfig(); }
            ImGui.SameLine();
            if (ImGui.RadioButton("Charlie", _teamIndex == 2)) { _teamIndex = 2; ApplyTeamColor(); SavePlayerConfig(); }
        }

        void DrawLayeredFsSection()
        {
            Widgets.SectionHeader("LayeredFS (mods)");

            ImGui.SetNextItemWidth(-70);
            if (ImGui.InputText("##layeredpath", ref _layeredInput, 512))
            {
                _config.LayeredFsPath = _layeredInput;
                _config.Save();
            }
            ImGui.SameLine();
            if (ImGui.Button("...##layeredbrowse", new Vector2(-1, 0)))
            {
                string folder = NativeFolderPicker.SelectFolder("Select LayeredFS (mod) folder", _layeredInput);
                if (!string.IsNullOrEmpty(folder))
                {
                    _layeredInput = folder;
                    _config.LayeredFsPath = folder;
                    _config.Save();
                }
            }

            bool useLayered = _config.UseLayeredFs;
            if (ImGui.Checkbox("Enable LayeredFS", ref useLayered))
            {
                _config.UseLayeredFs = useLayered;
                _config.Save();
                _preserveStateOnLoad = true;
                _needsLoad = true;
            }

            bool dirOk = !string.IsNullOrEmpty(_layeredInput) && Directory.Exists(_layeredInput);
            if (!string.IsNullOrEmpty(_layeredInput) && !dirOk)
                ImGui.TextColored(new Vector4(0.9f, 0.35f, 0.3f, 1), "folder not found");
            else if (_romfs != null && _romfs.UseLayered)
                ImGui.TextColored(new Vector4(0.4f, 0.85f, 0.4f, 1), "active");

            if (ImGui.Button("Reload", new Vector2(-1, 0)))
            {
                _preserveStateOnLoad = true;
                _needsLoad = true;
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Reload everything from the current romfs + LayeredFS.\nKeeps the current player configuration.");
        }

        void GearRow(string label, GearSlot slot, List<GearEntry> entries, GearEntry current,
            bool allowNone = true, string noneLabel = "Blank")
        {
            Widgets.LabeledRow(label, () =>
            {
                if (Widgets.GearCombo(label, entries, current, out var selected, allowNone, noneLabel))
                {
                    _scene.SetGear(slot, selected);
                    SavePlayerConfig();
                }
            });
        }

        void ApplyTeamColor()
        {
            var set = _useCustomTeamColor ? _customTeam : _db?.TeamColors.ElementAtOrDefault(_teamColorIndex);
            if (set != null && _scene != null)
                _scene.ApplyTeamColor(set, _teamIndex);
        }

        void ResetPlayerDefaults()
        {
            _scene.CurrentHair = null;
            _scene.CurrentEyebrow = null;
            _scene.CurrentHead = null;
            _scene.CurrentClothes = null;
            _scene.CurrentBottom = null;
            _scene.CurrentShoes = null;
            _scene.CurrentTank = null;
            _scene.CurrentWeapon = null;
            _scene.EyeColor = 0;
            _scene.SkinTone = 0;
            _scene.SetPlayerType(0);

            _teamColorIndex = 0;
            _teamIndex = 0;
            _useCustomTeamColor = true;
            ApplyTeamColor();

            _pipeline.FramePlayer();
            SavePlayerConfig();
        }

        void SavePlayerConfig()
        {
            if (_scene == null) return;
            var p = _config.Player;
            p.PlayerType = _scene.PlayerType;
            p.EyeColor = _scene.EyeColor;
            p.SkinTone = _scene.SkinTone;
            static void SaveGear(GearEntry e, out string rowId, out int variation)
            {
                rowId = e?.RowId;
                variation = e?.Variation ?? 0;
            }
            SaveGear(_scene.CurrentHair, out p.Hair, out p.HairVariation);
            SaveGear(_scene.CurrentEyebrow, out p.Eyebrow, out p.EyebrowVariation);
            SaveGear(_scene.CurrentHead, out p.Head, out p.HeadVariation);
            SaveGear(_scene.CurrentClothes, out p.Clothes, out p.ClothesVariation);
            SaveGear(_scene.CurrentBottom, out p.Bottom, out p.BottomVariation);
            SaveGear(_scene.CurrentShoes, out p.Shoes, out p.ShoesVariation);
            SaveGear(_scene.CurrentTank, out p.Tank, out p.TankVariation);
            SaveGear(_scene.CurrentWeapon, out p.Weapon, out p.WeaponVariation);
            p.TeamColorIndex = _teamColorIndex;
            p.TeamIndex = _teamIndex;
            p.UseCustomTeamColor = _useCustomTeamColor;
            p.CustomAlpha = new[] { _customTeam.Alpha.X, _customTeam.Alpha.Y, _customTeam.Alpha.Z };
            p.CustomBravo = new[] { _customTeam.Bravo.X, _customTeam.Bravo.Y, _customTeam.Bravo.Z };
            p.CustomCharlie = new[] { _customTeam.Charlie.X, _customTeam.Charlie.Y, _customTeam.Charlie.Z };
            _config.Save();
        }

        void RestorePlayerConfig()
        {
            var p = _config.Player;
            if (p == null) return;

            GearEntry FindGear(List<GearEntry> list, string rowId, int variation)
            {
                if (rowId == null) return null;
                return list.FirstOrDefault(x => x.RowId == rowId && x.Variation == variation)
                    ?? list.FirstOrDefault(x => x.RowId == rowId);
            }

            _scene.EyeColor = p.EyeColor;
            _scene.SkinTone = p.SkinTone;
            _scene.CurrentHair = FindGear(_db.Hair, p.Hair, p.HairVariation);
            _scene.CurrentEyebrow = FindGear(_db.Eyebrow, p.Eyebrow, p.EyebrowVariation);
            _scene.CurrentHead = FindGear(_db.Head, p.Head, p.HeadVariation);
            _scene.CurrentClothes = FindGear(_db.Clothes, p.Clothes, p.ClothesVariation);
            _scene.CurrentBottom = FindGear(_db.Bottom, p.Bottom, p.BottomVariation);
            _scene.CurrentShoes = FindGear(_db.Shoes, p.Shoes, p.ShoesVariation);
            _scene.CurrentTank = FindGear(_db.Tank, p.Tank, p.TankVariation);
            _scene.CurrentWeapon = FindGear(_db.MainWeapons, p.Weapon, p.WeaponVariation);
            _scene.SetPlayerType(p.PlayerType);

            _teamColorIndex = p.TeamColorIndex;
            _teamIndex = p.TeamIndex;
            _useCustomTeamColor = p.UseCustomTeamColor;
            if (p.CustomAlpha is { Length: 3 })
                _customTeam.Alpha = new System.Numerics.Vector3(p.CustomAlpha[0], p.CustomAlpha[1], p.CustomAlpha[2]);
            if (p.CustomBravo is { Length: 3 })
                _customTeam.Bravo = new System.Numerics.Vector3(p.CustomBravo[0], p.CustomBravo[1], p.CustomBravo[2]);
            if (p.CustomCharlie is { Length: 3 })
                _customTeam.Charlie = new System.Numerics.Vector3(p.CustomCharlie[0], p.CustomCharlie[1], p.CustomCharlie[2]);
            _customTeam.Neutral = (_customTeam.Alpha + _customTeam.Bravo) * 0.5f;
            ApplyTeamColor();

            _scene.ApplyEyeColor(p.EyeColor);
            _scene.ApplySkinTone(p.SkinTone);
        }

        #endregion

        #region Viewport

        Player.IViewScene ActiveScene => _standalone != null ? _standalone : _scene;

        void DrawViewport()
        {
            var size = ImGui.GetContentRegionAvail();
            if (!_recorder.IsRecording)
                _pipeline.Resize((int)size.X, (int)size.Y);

            _pipeline.Render(ActiveScene);

            var pos = ImGui.GetCursorScreenPos();
            ImGui.Image((IntPtr)_pipeline.ViewportTextureId, new Vector2(_pipeline.Width, _pipeline.Height),
                new Vector2(0, 1), new Vector2(1, 0));

            _viewportHovered = ImGui.IsItemHovered();
            UpdateCameraInput(pos);

            //Recording indicator overlay
            if (_recorder.IsRecording)
            {
                var draw = ImGui.GetWindowDrawList();
                draw.AddCircleFilled(new Vector2(pos.X + 18, pos.Y + 18), 7,
                    ImGui.GetColorU32(new Vector4(0.9f, 0.15f, 0.15f, 1)));
                var cursor = ImGui.GetCursorPos();
                ImGui.SetCursorScreenPos(new Vector2(pos.X + 32, pos.Y + 10));
                ImGui.TextColored(new Vector4(1, 1, 1, 1), $"REC {_recorder.FrameCount / 60.0f:F1}s");
                ImGui.SetCursorPos(cursor);
            }
        }

        void UpdateCameraInput(Vector2 viewportScreenPos)
        {
            var io = ImGui.GetIO();
            var cam = _pipeline.Camera;
            bool changed = false;

            bool leftDown = ImGui.IsMouseDown(ImGuiMouseButton.Left);
            bool rightDown = ImGui.IsMouseDown(ImGuiMouseButton.Right);
            bool midDown = ImGui.IsMouseDown(ImGuiMouseButton.Middle);
            bool anyDown = leftDown || rightDown || midDown;

            //Drags only start inside the viewport, but keep tracking outside it.
            if (_viewportHovered && anyDown && !_mouseDown)
                _mouseDown = true;
            if (!anyDown)
                _mouseDown = false;

            if (_mouseDown)
            {
                var delta = io.MouseDelta;
                if (midDown || (leftDown && io.KeyShift))
                {
                    //Pan, scaled so the model roughly follows the cursor.
                    float scale = (float)Math.Sin(cam.Fov) * cam.TargetDistance;
                    float dx = -delta.X / Math.Max(1, cam.Width) * scale;
                    float dy = delta.Y / Math.Max(1, cam.Height) * scale;
                    var rot = cam.InverseRotationMatrix;
                    cam.TargetPosition += rot.Row0 * dx + rot.Row1 * dy;
                    changed = true;
                }
                else if (leftDown || rightDown)
                {
                    //Orbit around the target.
                    cam.RotationY += delta.X * 0.008f;
                    cam.RotationX += delta.Y * 0.008f;
                    cam.RotationX = MathHelper.Clamp(cam.RotationX,
                        -MathHelper.PiOver2 + 0.01f, MathHelper.PiOver2 - 0.01f);
                    changed = true;
                }
            }

            if (_viewportHovered && io.MouseWheel != 0)
            {
                cam.TargetDistance = Math.Max(0.05f,
                    cam.TargetDistance * (1.0f - io.MouseWheel * 0.12f));
                changed = true;
            }

            //WASD pans in camera space (W/S = forward/back, A/D = left/right).
            if (!io.WantTextInput && Focused)
            {
                var kb = Keyboard.GetState();
                float move = cam.TargetDistance * io.DeltaTime;
                var dir = OpenTK.Vector3.Zero;
                var rot = cam.InverseRotationMatrix;
                if (kb.IsKeyDown(Key.W)) dir -= rot.Row2;
                if (kb.IsKeyDown(Key.S)) dir += rot.Row2;
                if (kb.IsKeyDown(Key.A)) dir -= rot.Row0;
                if (kb.IsKeyDown(Key.D)) dir += rot.Row0;
                if (kb.IsKeyDown(Key.Space)) dir += rot.Row1;
                if (kb.IsKeyDown(Key.ShiftLeft) || kb.IsKeyDown(Key.ShiftRight)) dir -= rot.Row1;
                if (dir != OpenTK.Vector3.Zero)
                {
                    cam.TargetPosition += dir * move;
                    changed = true;
                }
            }

            if (changed)
                cam.UpdateMatrices();
        }

        #endregion

        #region Animation + capture panels

        void DrawAnimationPanel()
        {
            Widgets.SectionHeader("Animation");

            //Both scene types expose the same playback surface; bridge through locals.
            bool standalone = _standalone != null;
            string currentAnim = standalone ? _standalone.CurrentAnimName : _scene.CurrentAnimName;
            bool paused = standalone ? _standalone.AnimPaused : _scene.AnimPaused;
            float speed = standalone ? _standalone.AnimSpeed : _scene.AnimSpeed;
            float rawFrameCount = standalone
                ? _standalone.CurrentSkeletal?.FrameCount ?? 1
                : _scene.CurrentSkeletal?.FrameCount ?? 1;
            List<string> animNames = standalone ? _standalone.AnimNames : _scene.Anims.AnimNames;

            void SetPaused(bool value) { if (standalone) _standalone.AnimPaused = value; else _scene.AnimPaused = value; }
            void SetSpeed(float value) { if (standalone) _standalone.AnimSpeed = value; else _scene.AnimSpeed = value; }
            void SetFrame(float value) { if (standalone) _standalone.SetAnimFrame(value); else _scene.SetAnimFrame(value); }
            void Play(string name) { if (standalone) _standalone.PlayAnim(name); else _scene.PlayAnim(name); }

            ImGui.TextColored(Theme.GoldBright, currentAnim ?? "(none)");

            if (ImGui.Button(paused ? "  Play  " : " Pause "))
                SetPaused(!paused);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(-1);
            if (ImGui.SliderFloat("##speed", ref speed, 0.1f, 2.0f, "speed %.2fx"))
                SetSpeed(speed);

            float frameCount = Math.Max(rawFrameCount - 1, 1);
            ImGui.SetNextItemWidth(-1);
            if (ImGui.SliderFloat("##frame", ref _uiFrame, 0, frameCount, "frame %.0f"))
            {
                SetFrame(_uiFrame);
                SetPaused(true);
            }

            ImGui.Spacing();
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(Theme.TextDim, "Search");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(-1);
            ImGui.InputText("##animsearch", ref _animSearch, 64);

            var avail = ImGui.GetContentRegionAvail();
            ImGui.BeginChild("##animlist", new Vector2(0, Math.Max(avail.Y - 205, 120)), true);
            if (animNames.Count == 0)
                ImGui.TextColored(Theme.TextDim, "no skeletal animations");
            if (standalone && animNames.Count > 0)
            {
                if (ImGui.Selectable("<BLANK>", currentAnim == null))
                {
                    Play(null);
                    SetPaused(true);
                }
            }
            foreach (var name in animNames)
            {
                if (!string.IsNullOrEmpty(_animSearch) &&
                    !name.Contains(_animSearch, StringComparison.OrdinalIgnoreCase))
                    continue;
                bool isCurrent = name == currentAnim;
                if (ImGui.Selectable(name, isCurrent))
                {
                    Play(name);
                    SetPaused(false);
                }
            }
            ImGui.EndChild();
        }

        void DrawCapturePanel()
        {
            Widgets.SectionHeader("Capture");

            ImGui.SetNextItemWidth(-1);
            if (ImGui.BeginCombo("##capres", CaptureSizes[_captureRes].Label))
            {
                for (int i = 0; i < CaptureSizes.Length; i++)
                    if (ImGui.Selectable(CaptureSizes[i].Label, i == _captureRes))
                        _captureRes = i;
                ImGui.EndCombo();
            }
            ImGui.Checkbox("Transparent background", ref _captureTransparent);

            if (ImGui.Button("Screenshot (PNG)", new Vector2(-1, 0)))
                SaveScreenshot();

            ImGui.Spacing();
            if (!_recorder.IsRecording)
            {
                ImGui.Checkbox("Video greenscreen", ref _recordGreenscreen);
                bool haveFfmpeg = VideoRecorder.FfmpegAvailable;
                if (!haveFfmpeg) ImGui.PushStyleVar(ImGuiStyleVar.Alpha, 0.45f);
                if (ImGui.Button("Record video", new Vector2(-1, 0)) && haveFfmpeg)
                    StartRecording();
                if (!haveFfmpeg)
                {
                    ImGui.PopStyleVar();
                    ImGui.TextColored(Theme.TextDim, "ffmpeg not found (exe folder or PATH)");
                }
            }
            else
            {
                ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.55f, 0.12f, 0.10f, 1));
                ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.70f, 0.16f, 0.13f, 1));
                //"###" keeps the widget ID stable while the timer in the label changes,
                //otherwise the click never registers (ID differs between press/release).
                if (ImGui.Button($"Stop recording ({_recorder.FrameCount / 60.0f:F1}s)###stoprec", new Vector2(-1, 0)))
                    StopRecording();
                ImGui.PopStyleColor(2);
            }
        }

        void SaveScreenshot()
        {
            string path = NativeFolderPicker.SaveFile("Save Screenshot", "player.png", "PNG image (*.png)", "*.png");
            if (string.IsNullOrEmpty(path))
                return;
            if (!path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                path += ".png";

            var (_, w, h) = CaptureSizes[_captureRes];
            using var bmp = _pipeline.Capture(ActiveScene, w, h, _pipeline.BackgroundColor, _captureTransparent);
            bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            Console.WriteLine($"[UI] Saved {path}");
        }

        bool _recordGreenscreen = true;
        System.Numerics.Vector3 _backgroundBeforeRecord;

        void StartRecording()
        {
            string path = NativeFolderPicker.SaveFile("Save Video", "player.mp4", "MP4 video (*.mp4)", "*.mp4");
            if (string.IsNullOrEmpty(path))
                return;
            if (!path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
                path += ".mp4";
            StartRecording(path);
        }

        void StartRecording(string path)
        {
            _backgroundBeforeRecord = _pipeline.BackgroundColor;
            if (_recordGreenscreen)
                _pipeline.BackgroundColor = new System.Numerics.Vector3(0, 1, 0);
            if (!_recorder.Start(_pipeline.Width, _pipeline.Height, path))
                _pipeline.BackgroundColor = _backgroundBeforeRecord;
        }

        void StopRecording()
        {
            //Flush the last PBO frame that the async readback is holding.
            var last = _pipeline.ReadFinalPixelsAsync(out _);
            if (last != null)
                _recorder.PushFrame(last, _pipeline.Width, _pipeline.Height);
            _recorder.Stop();
            if (_recordGreenscreen)
                _pipeline.BackgroundColor = _backgroundBeforeRecord;
        }

        #endregion

        #region Standalone panel

        void DrawStandalonePanel()
        {
            Widgets.SectionHeader("Standalone Model");

            ImGui.TextColored(Theme.GoldBright, _standalone.Name);
            ImGui.PushTextWrapPos();
            ImGui.TextColored(Theme.TextDim, _standalone.SourcePath);
            ImGui.PopTextWrapPos();
            if (_standaloneError != null)
                ImGui.TextColored(new Vector4(0.9f, 0.35f, 0.3f, 1), _standaloneError);

            ImGui.Spacing();
            if (ImGui.Button("Back to player", new Vector2(-1, 0)))
            {
                CloseStandalone();
                return;
            }
            if (ImGui.Button("Frame model", new Vector2(-1, 0)))
                _pipeline.FrameSphere(_standalone.GetBounding());

            var models = _standalone.Render.Models.OfType<BfresEditor.BfresModelAsset>().ToList();
            Widgets.SectionHeader("Models");
            for (int mi = 0; mi < models.Count; mi++)
            {
                var model = models[mi];
                bool visible = model.IsVisible;
                if (ImGui.Checkbox($"##{mi}_vis", ref visible))
                    model.IsVisible = visible;
                ImGui.SameLine();
                if (ImGui.TreeNode($"{model.ModelData.Name}##{mi}"))
                {
                    foreach (var mesh in model.Meshes)
                    {
                        bool meshVis = mesh.Shape.IsVisible;
                        if (ImGui.Checkbox($"{mesh.Name}##{mi}_{mesh.Name}", ref meshVis))
                            mesh.Shape.IsVisible = meshVis;
                    }
                    ImGui.TreePop();
                }
            }

            DrawLightingSection();
            DrawTeamColorSection();
            DrawViewSection();
        }

        #endregion
    }
}
