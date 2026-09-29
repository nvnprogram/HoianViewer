using System;
using System.Collections.Generic;
using System.Runtime;
using ImGuiNET;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Vector2 = System.Numerics.Vector2;
using Vector4 = System.Numerics.Vector4;

namespace PlayerViewer.UI
{
    public partial class ViewerWindow
    {
        static readonly (string Label, int W, int H)[] CaptureSizes =
        {
            ("1280 x 1280", 1280, 1280),
            ("1920 x 1080", 1920, 1080),
            ("3840 x 2160 (4K)", 3840, 2160),
            ("2160 x 3840 (4K portrait)", 2160, 3840),
        };

        //The format combo's rows; the config stores the row index. A still has no animation format.
        static readonly (string Label, string Button, OutputFormat? Anim)[] ExportFormats =
        {
            ("PNG (current frame)", "Export PNG", null),
            ("MP4", "Export MP4", OutputFormat.Mp4),
            ("WebP (transparent)", "Export WebP", OutputFormat.WebpTransparent),
            ("WebM (transparent)", "Export WebM", OutputFormat.WebmTransparent),
            ("PNG sequence", "Export PNG sequence", OutputFormat.PngSequence),
        };

        static readonly string[] ExportFormatLabels = Array.ConvertAll(ExportFormats, f => f.Label);

        static readonly string[] BgModeLabels = { "Transparent", "Color", "Image" };
        static readonly string[] BgScaleLabels = { "Fill", "Fit", "Stretch" };

        void DrawRightSidebar()
        {
            DrawPlaybackControls();

            float spacing = ImGui.GetStyle().ItemSpacing.Y;
            float avail = VisibleHeightBelowCursor();
            float listH = Math.Max(avail - _measuredCaptureHeight - spacing, 90);
            DrawAnimList(listH);

            float y0 = ImGui.GetCursorPosY();
            DrawModeTabs();
            if (_animMode == 1)
                DrawSequencePanel();
            DrawCapturePanel();
            _measuredCaptureHeight = ImGui.GetCursorPosY() - y0;
        }

        void DrawPlaybackControls()
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

            void SetPaused(bool value)
            {
                if (standalone)
                    _standalone.AnimPaused = value;
                else
                    _scene.AnimPaused = value;
            }
            void SetSpeed(float value)
            {
                if (standalone)
                    _standalone.AnimSpeed = value;
                else
                    _scene.AnimSpeed = value;
            }
            void SetFrame(float value)
            {
                if (standalone)
                    _standalone.SetAnimFrame(value);
                else
                    _scene.SetAnimFrame(value);
            }

            if (SideOrderControls.On)
            {
                DrawPlaybackSideOrder(paused, speed, rawFrameCount, SetPaused, SetSpeed, SetFrame);
                return;
            }

            Widgets.ColoredText(Theme.GoldBright, currentAnim ?? "(none)");

            if (Widgets.Button(paused ? "  Play  " : " Pause "))
                SetPaused(!paused);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(-1);
            if (ImGui.SliderFloat("##speed", ref speed, 0.01f, 10.0f, "speed %.2fx"))
                SetSpeed(speed);

            float frameCount = Math.Max(rawFrameCount - 1, 1);
            ImGui.SetNextItemWidth(-1);
            if (Widgets.SliderFloat("##frame", ref _uiFrame, 0, frameCount, "frame %.0f"))
            {
                SetFrame(_uiFrame);
                SetPaused(true);
            }

            ImGui.Spacing();
            ImGui.AlignTextToFramePadding();
            Widgets.DimText("Search");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(-1);
            Widgets.InputText("##animsearch", ref _animSearch, 64);
        }

        //The Side Order animation card: the current animation on a pill, the round play button
        //beside the speed and frame sliders, and the search field over the list.
        void DrawPlaybackSideOrder(
            bool paused,
            float speed,
            float rawFrameCount,
            Action<bool> setPaused,
            Action<float> setSpeed,
            Action<float> setFrame
        )
        {
            ImGui.SetNextItemWidth(-1);
            Widgets.LabelPill(PlaybackCurrentAnim ?? "(none)");

            var style = ImGui.GetStyle();
            float row = ImGui.GetFrameHeight();
            var start = ImGui.GetCursorScreenPos();
            if (Widgets.RoundPlayButton("##playpause", !paused, out float sliderX))
                setPaused(!paused);
            Widgets.ItemTooltip(paused ? "Play" : "Pause");

            float column = ImGui.CalcTextSize("Speed 10.00x").X + 12;
            ImGui.SetNextItemWidth(-1);
            if (
                Widgets.SliderFloat("##speed", ref speed, 0.01f, 10.0f, "Speed %.2fx", true, column)
            )
                setSpeed(speed);

            float frameCount = Math.Max(rawFrameCount - 1, 1);
            ImGui.SetCursorScreenPos(new Vector2(sliderX, start.Y + row + style.ItemSpacing.Y));
            ImGui.SetNextItemWidth(-1);
            if (
                Widgets.SliderFloat(
                    "##frame",
                    ref _uiFrame,
                    0,
                    frameCount,
                    "frame %.0f",
                    true,
                    column
                )
            )
            {
                setFrame(_uiFrame);
                setPaused(true);
            }

            ImGui.Dummy(new Vector2(0, 4));
            Widgets.SearchField("##animsearch", ref _animSearch, "Search", -1);
        }

        void DrawAnimList(float height)
        {
            bool standalone = _standalone != null;
            string currentAnim = standalone ? _standalone.CurrentAnimName : _scene.CurrentAnimName;
            List<string> animNames = standalone ? _standalone.AnimNames : _scene.Anims.AnimNames;

            void SetPaused(bool value)
            {
                if (standalone)
                    _standalone.AnimPaused = value;
                else
                    _scene.AnimPaused = value;
            }
            void Play(string name)
            {
                StopAnimChain();
                if (standalone)
                    _standalone.PlayAnim(name);
                else
                    _scene.PlayAnim(name);
            }

            Widgets.BeginList("##animlist", new Vector2(0, height));
            if (animNames.Count == 0)
            {
                Widgets.DimText("no skeletal animations");
                Widgets.EndList();
                return;
            }

            void Pick(string name)
            {
                Play(name);
                SetPaused(name == null);
            }

            //null is the blank row, and it is a row the arrows can land on like any other.
            var rows = AnimRows(animNames, standalone);
            int currentRow = rows.IndexOf(currentAnim);

            int move = Widgets.VirtualRows(
                AnimListId,
                rows.Count,
                currentRow,
                r =>
                {
                    string name = rows[r];
                    if (Widgets.ListRow(name ?? "<BLANK>", name == currentAnim))
                        Pick(name);
                }
            );
            if (move >= 0)
                Pick(rows[move]);
            Widgets.EndList();
        }

        //The animation list's rows after the search, kept until the names or the search change.
        List<string> _animRows = new();
        List<string> _animRowsFrom;
        int _animRowsCount = -1;
        string _animRowsSearch;
        bool _animRowsBlank;

        List<string> AnimRows(List<string> animNames, bool blank)
        {
            if (
                _animRowsFrom == animNames
                && _animRowsCount == animNames.Count
                && _animRowsSearch == _animSearch
                && _animRowsBlank == blank
            )
                return _animRows;
            _animRows = new List<string>(animNames.Count + 1);
            if (blank)
                _animRows.Add(null);
            foreach (var name in animNames)
                if (
                    string.IsNullOrEmpty(_animSearch)
                    || name.Contains(_animSearch, StringComparison.OrdinalIgnoreCase)
                )
                    _animRows.Add(name);
            (_animRowsFrom, _animRowsCount, _animRowsSearch, _animRowsBlank) = (
                animNames,
                animNames.Count,
                _animSearch,
                blank
            );
            return _animRows;
        }

        const string AnimListId = "animlist";

        //Stores the capture panel's choices in the config.
        void SaveCaptureSettings()
        {
            _config.CaptureResIndex = _captureRes;
            _config.ExportFormat = _exportFormat;
            _config.ExportFps = _exportFps;
            _config.AnimMode = _animMode;
            _config.Save();
        }

        //The background is part of the preset: saves it and has the viewport's preview rebuilt.
        void BackgroundChanged()
        {
            _bgDirty = true;
            _config.Save();
        }

        System.Numerics.Vector3 BgColorVec => new(Bg.Color[0], Bg.Color[1], Bg.Color[2]);

        //Rebuilds the viewport's background when the settings or the viewport size change, with
        //the export's BuildBackground so the two match. Transparent clears it.
        void UpdateBackgroundPreview()
        {
            if (Bg.Mode == 0)
            {
                _pipeline.SetBackgroundBuffer(null, 0, 0);
                _bgPreviewW = -1;
                return;
            }
            if (_bgDirty || _bgPreviewW != _pipeline.Width || _bgPreviewH != _pipeline.Height)
            {
                var buf = ExportUtil.BuildBackground(
                    _pipeline.Width,
                    _pipeline.Height,
                    Bg,
                    bottomUp: true
                );
                _pipeline.SetBackgroundBuffer(buf, _pipeline.Width, _pipeline.Height);
                _bgPreviewW = _pipeline.Width;
                _bgPreviewH = _pipeline.Height;
                _bgDirty = false;
            }
        }

        //Transparent, a colour or an image, previewed in the viewport as an export composites it.
        void DrawBackgroundSection()
        {
            Widgets.SectionHeader("Background");

            ImGui.SetNextItemWidth(-1);
            Widgets.Combo("##bgmode", Bg.Mode, BgModeLabels, v => Bg.Mode = v, BackgroundChanged);

            if (Bg.Mode == 0 && ExportFormats[_exportFormat].Anim == OutputFormat.Mp4)
                Widgets.DimText("MP4 has no alpha; transparent exports as black.");

            if (Bg.Mode == 1)
            {
                ImGui.SetNextItemWidth(-1);
                Widgets.ColorEdit3(
                    "##bgcolor",
                    BgColorVec,
                    v => Bg.Color = new[] { v.X, v.Y, v.Z },
                    ImGuiColorEditFlags.None,
                    BackgroundChanged
                );
            }
            else if (Bg.Mode == 2)
            {
                if (Widgets.Button("Browse image..."))
                {
                    string p = NativeFolderPicker.OpenFile(
                        "Background image",
                        "Images (*.png;*.jpg;*.jpeg)",
                        "*.png;*.jpg;*.jpeg"
                    );
                    if (!string.IsNullOrEmpty(p))
                    {
                        Bg.ImagePath = p;
                        BackgroundChanged();
                    }
                }
                if (!string.IsNullOrEmpty(Bg.ImagePath))
                {
                    ImGui.SameLine();
                    Widgets.DimText(System.IO.Path.GetFileName(Bg.ImagePath));
                }
                ImGui.SetNextItemWidth(-1);
                Widgets.Combo(
                    "##bgscale",
                    Bg.ScaleMode,
                    BgScaleLabels,
                    v => Bg.ScaleMode = v,
                    BackgroundChanged
                );
                ImGui.SetNextItemWidth(-1);
                Widgets.SliderFloat(
                    "##bgzoom",
                    Bg.Zoom,
                    0.1f,
                    4f,
                    v => Bg.Zoom = v,
                    BackgroundChanged,
                    "zoom %.2f"
                );
                var off = new Vector2(Bg.OffsetX, Bg.OffsetY);
                ImGui.SetNextItemWidth(-1);
                if (ImGui.SliderFloat2("##bgoff", ref off, -1f, 1f, "offset %.2f"))
                {
                    Bg.OffsetX = off.X;
                    Bg.OffsetY = off.Y;
                    BackgroundChanged();
                }
                Widgets.Checkbox("Tile", Bg.Tile, v => Bg.Tile = v, BackgroundChanged);
                if (Bg.Tile)
                {
                    ImGui.SameLine();
                    ImGui.SetNextItemWidth(70);
                    Widgets.InputInt(
                        "##tilex",
                        Bg.TileX,
                        v => Bg.TileX = Math.Max(1, v),
                        BackgroundChanged
                    );
                    ImGui.SameLine();
                    ImGui.SetNextItemWidth(70);
                    Widgets.InputInt(
                        "##tiley",
                        Bg.TileY,
                        v => Bg.TileY = Math.Max(1, v),
                        BackgroundChanged
                    );
                }
            }
        }

        void DrawCapturePanel()
        {
            Widgets.SectionHeader("Capture");

            bool haveFfmpeg = ExportUtil.FfmpegAvailable;

            //While busy, a bar for the render and then one for the encode.
            if (_animExporting)
            {
                float progress =
                    _animExportTotal > 0 ? Math.Min(_animExportIndex / _animExportTotal, 1f) : 0f;
                int shown = (int)Math.Min(_animExportIndex + 1, _animExportTotal);
                Widgets.ProgressBar(
                    progress,
                    new Vector2(-1, 0),
                    $"Rendering {shown}/{_animExportTotal}"
                );
                Widgets.RedButton("Cancel export", AbortAnimExport);
                return;
            }
            if (_bufferedExporter != null)
            {
                if (_bufferedExporter.IsEncoding)
                {
                    var ex = _bufferedExporter;
                    int total = ex.EncodeTotal;
                    if (total > 0)
                        Widgets.ProgressBar(
                            Math.Min(ex.EncodeProgress / (float)total, 1f),
                            new Vector2(-1, 0),
                            $"{ex.EncodeStage} {ex.EncodeProgress}/{total}"
                        );
                    else
                        Widgets.ProgressBar(
                            (float)(ImGui.GetTime() % 1.0),
                            new Vector2(-1, 0),
                            ex.EncodeStage
                        );
                    return;
                }
                //Encode finished on the worker thread: log and clear.
                FinishBufferedExport();
            }

            ImGui.SetNextItemWidth(-1);
            if (Widgets.BeginCombo("##capres", CaptureSizes[_captureRes].Label))
            {
                Widgets.PopupRows(
                    "capres",
                    CaptureSizes.Length,
                    _captureRes,
                    (row, isSelected) => ImGui.Selectable(CaptureSizes[row].Label, isSelected),
                    row =>
                    {
                        _captureRes = row;
                        SaveCaptureSettings();
                    }
                );
                ImGui.EndCombo();
            }

            ImGui.SetNextItemWidth(-1);
            if (Widgets.ComboIndex("##exportformat", ref _exportFormat, ExportFormatLabels))
                SaveCaptureSettings();

            var format = ExportFormats[_exportFormat];
            bool isAnim = format.Anim != null;

            if (isAnim)
            {
                ImGui.AlignTextToFramePadding();
                Widgets.IndentLineStart();
                Widgets.DimText("FPS");
                ImGui.SameLine();
                if (Widgets.RadioButton("30", _exportFps == 30))
                {
                    _exportFps = 30;
                    SaveCaptureSettings();
                }
                ImGui.SameLine();
                if (Widgets.RadioButton("60", _exportFps == 60))
                {
                    _exportFps = 60;
                    SaveCaptureSettings();
                }
            }

            //The render is always transparent (alpha oracle), so trim applies whenever it's on.
            //An effect over a background is rendered opaque, which leaves nothing to trim.
            bool trimApplies =
                _config.TrimDeadspace && (_effect == null || EffectKeepsAlpha(format.Anim));
            if (trimApplies || _config.TrimDeadspace)
                Widgets.IndentLineStart();
            if (trimApplies)
                Widgets.DimText($"Trim deadspace on (+{_config.TrimMarginPx}px)");
            else if (_config.TrimDeadspace)
                Widgets.DimText("No trim: the effect renders over its background.");

            //In Sequence mode an animation export runs the whole chain instead of the current anim.
            bool exportChain = _animMode == 1 && isAnim && _effect == null;
            bool animReady =
                _effect != null ? EffectPlay?.Set != null
                : exportChain ? _animChain.Count > 0
                : PlaybackHasAnim;
            bool needFfmpeg = isAnim;
            bool canExport = (!needFfmpeg || haveFfmpeg) && (!isAnim || animReady);
            string button = exportChain ? format.Button + " (all steps)" : format.Button;
            Widgets.AccentButton(button, canExport, DoExport);

            if (needFfmpeg && !haveFfmpeg)
                Widgets.DimText("ffmpeg not found (data folder or PATH)");
            else if (isAnim && !animReady)
                Widgets.DimText(
                    exportChain ? "add animations to the sequence" : "select an animation to export"
                );
        }

        void DoExport()
        {
            if (ExportFormats[_exportFormat].Anim is { } format)
                StartAnimExport(format);
            else
                SaveScreenshot();
        }

        void SaveScreenshot()
        {
            string def = ExportUtil.Timestamped(_effect != null ? "effect" : "player", ".png");
            string path = NativeFolderPicker.SaveFile(
                "Save Screenshot",
                def,
                "PNG image (*.png)",
                "*.png"
            );
            if (string.IsNullOrEmpty(path))
                return;
            if (!path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                path += ".png";
            WriteScreenshot(path);
        }

        //Renders and saves a still without a dialog. The render is transparent, and Color or
        //Image composite it over the background. It renders at ss times the capture size; with
        //trim on the crop is found at that size and resolved down on the CPU, else the pipeline
        //resolves.
        void WriteScreenshot(string path)
        {
            var (_, w, h) = CaptureSizes[_captureRes];
            int ss = ScenePipeline.ClampSupersample(_config.ExportSupersample, w, h);
            if (_effect != null && !EffectKeepsAlpha(null))
            {
                WriteEffectOverBackground(path, w, h, ss);
                return;
            }
            bool trim = _config.TrimDeadspace;
            using (
                var img = trim
                    ? _pipeline.Capture(
                        ActiveScene,
                        w * ss,
                        h * ss,
                        _pipeline.BackgroundColor,
                        transparent: true,
                        1
                    )
                    : _pipeline.Capture(
                        ActiveScene,
                        w,
                        h,
                        _pipeline.BackgroundColor,
                        transparent: true,
                        ss
                    )
            )
            {
                if (img == null)
                {
                    Console.WriteLine($"[UI] Capture failed at {w * ss}x{h * ss}");
                    return;
                }

                var rect = trim
                    ? TrimImage(img, _config.TrimMarginPx * ss, ss)
                    : new Rectangle(0, 0, img.Width, img.Height);
                var outRect = trim
                    ? new Rectangle(rect.X / ss, rect.Y / ss, rect.Width / ss, rect.Height / ss)
                    : rect;

                if (trim && ss > 1)
                    img.Mutate(c =>
                        c.Resize(
                            new ResizeOptions
                            {
                                Size = new Size(outRect.Width, outRect.Height),
                                Sampler = KnownResamplers.Box,
                                Mode = ResizeMode.Stretch,
                                Compand = true,
                                PremultiplyAlpha = true,
                            }
                        )
                    );

                if (Bg.Mode == 0)
                {
                    img.SaveAsPng(path); //Transparent: keep alpha
                }
                else
                {
                    //Composite the (already cropped and resolved) scene over the same crop of the
                    //background, built top-down at the output size so it stays registered.
                    var bgBytes = ExportUtil.BuildBackground(w, h, Bg, bottomUp: false);
                    using var bg = Image.LoadPixelData<Rgba32>(bgBytes, w, h);
                    bg.Mutate(c => c.Crop(outRect).DrawImage(img, 1f));
                    bg.SaveAsPng(path);
                }
                Console.WriteLine($"[UI] Saved {path}");
            }

            ReleaseExportMemory();
        }

        //An effect over a background is rendered straight over it, the way the game blends it.
        void WriteEffectOverBackground(string path, int w, int h, int ss)
        {
            if (Bg.Mode == 2)
                _pipeline.SetBackgroundBuffer(
                    ExportUtil.BuildBackground(w, h, Bg, bottomUp: true),
                    w,
                    h
                );
            else
                _pipeline.SetBackgroundBuffer(null, 0, 0);
            _bgDirty = true;
            using (var img = _pipeline.Capture(ActiveScene, w, h, EffectBackground(), false, ss))
            {
                if (img == null)
                {
                    Console.WriteLine($"[UI] Capture failed at {w * ss}x{h * ss}");
                    return;
                }
                img.SaveAsPng(path);
                Console.WriteLine($"[UI] Saved {path}");
            }
            ReleaseExportMemory();
        }

        static void ReleaseExportMemory()
        {
            SixLabors.ImageSharp.Configuration.Default.MemoryAllocator.ReleaseRetainedResources();
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        }

        //Crops fully-transparent (alpha==0) deadspace off a captured image in place, keeping a
        //margin, and returns the crop rect it applied (full frame if nothing was cropped). The
        //rect is grown outwards to a multiple of align, whose multiple the image dimensions also
        //are, so the crop divides evenly when it is resolved down.
        static Rectangle TrimImage(Image<Rgba32> img, int margin, int align)
        {
            int w = img.Width,
                h = img.Height;
            int minX = w,
                minY = h,
                maxX = -1,
                maxY = -1;
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (img[x, y].A != 0)
                {
                    if (x < minX)
                        minX = x;
                    if (x > maxX)
                        maxX = x;
                    if (y < minY)
                        minY = y;
                    if (y > maxY)
                        maxY = y;
                }

            var full = new Rectangle(0, 0, w, h);
            if (maxX < 0)
                return full;
            int x0 = Math.Max(0, minX - margin),
                y0 = Math.Max(0, minY - margin);
            int x1 = Math.Min(w - 1, maxX + margin),
                y1 = Math.Min(h - 1, maxY + margin);
            if (align > 1)
            {
                x0 -= x0 % align;
                y0 -= y0 % align;
                x1 = Math.Min(w - 1, x1 + align - 1 - x1 % align);
                y1 = Math.Min(h - 1, y1 + align - 1 - y1 % align);
            }
            int cw = x1 - x0 + 1,
                ch = y1 - y0 + 1;
            if (cw >= w && ch >= h)
                return full;
            var rect = new Rectangle(x0, y0, cw, ch);
            img.Mutate(c => c.Crop(rect));
            return rect;
        }

        //Playback of the active scene; the two scene types share no interface for it.
        bool PlaybackHasAnim =>
            (_standalone != null ? _standalone.CurrentSkeletal : _scene?.CurrentSkeletal) != null;
        int PlaybackFrameCount =>
            (int)
                Math.Round(
                    _standalone != null
                        ? (_standalone.CurrentSkeletal?.FrameCount ?? 0f)
                        : (_scene?.CurrentSkeletal?.FrameCount ?? 0f)
                );
        float PlaybackAnimFrame =>
            _standalone != null ? _standalone.AnimFrame : (_scene?.AnimFrame ?? 0f);
        bool PlaybackPaused =>
            _standalone != null ? _standalone.AnimPaused : (_scene?.AnimPaused ?? true);
        float PlaybackSpeed =>
            _standalone != null ? _standalone.AnimSpeed : (_scene?.AnimSpeed ?? 1f);

        void PlaybackSetPaused(bool v)
        {
            if (_standalone != null)
                _standalone.AnimPaused = v;
            else if (_scene != null)
                _scene.AnimPaused = v;
        }

        void PlaybackSetFrame(float f)
        {
            if (_standalone != null)
                _standalone.SetAnimFrame(f);
            else
                _scene?.SetAnimFrame(f);
        }

        void PlaybackUpdate(float dt, float hairConvergeWeight = 0)
        {
            if (_standalone != null)
                UpdateStandalone(dt);
            else
                _scene?.Update(dt, hairConvergeWeight);
        }

        void PlaybackPlay(string name, bool resetHair)
        {
            if (_standalone != null)
            {
                _standalone.PlayAnim(name);
                if (resetHair)
                    _clothRuntime.Reset();
            }
            else
                _scene?.PlayAnim(name, resetHair);
        }

        void PlaybackResetHair()
        {
            if (_standalone == null)
                _scene?.ResetHairPhysics();
            else
                _clothRuntime.Reset();
        }

        string PlaybackCurrentAnim =>
            _standalone != null ? _standalone.CurrentAnimName : _scene?.CurrentAnimName;

        int PlaybackFrameCountOf(string name) =>
            _standalone != null
                ? _standalone.SkeletalFrameCount(name)
                : (_scene?.SkeletalFrameCount(name) ?? 0);

        void StartAnimExport(OutputFormat format)
        {
            bool effect = _effect != null;
            bool chain = !effect && _animMode == 1 && _animChain.Count > 0;
            if (_animExporting || _bufferedExporter != null)
                return;
            if (effect ? EffectPlay?.Set == null : !chain && !PlaybackHasAnim)
                return;
            StopAnimChain(); //deterministic export drives frames itself; don't let the preview fight it
            int total =
                effect ? EffectClip().Length
                : chain ? (int)Math.Round(ChainTotalFrames())
                : PlaybackFrameCount;
            if (total < 1)
                return;

            (string ext, string filterName, string filterExt) = format switch
            {
                OutputFormat.WebpTransparent => (".webp", "WebP image (*.webp)", "*.webp"),
                OutputFormat.WebmTransparent => (".webm", "WebM video (*.webm)", "*.webm"),
                OutputFormat.PngSequence => (".png", "PNG sequence (*.png)", "*.png"),
                _ => (".mp4", "MP4 video (*.mp4)", "*.mp4"),
            };
            string def = ExportUtil.Timestamped(effect ? EffectPlay.Set.Name : "animation", ext);
            string path = NativeFolderPicker.SaveFile(
                "Export Animation",
                def,
                filterName,
                filterExt
            );
            if (string.IsNullOrEmpty(path))
                return;
            if (!path.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                path += ext;

            _animExportPrevPaused = PlaybackPaused;
            _animExportPrevFrame = PlaybackAnimFrame;

            //The speed slider scales how far each output frame moves the animation, taken once.
            float speed = PlaybackSpeed;
            _animExportAdvance = Math.Max(0.0001f, (60f / _exportFps) * speed);
            _animExportTotal = total;
            _animExportIndex = 0f;
            //Alpha is kept only for Transparent on a format with alpha; else the background is
            //composited in.
            bool keepAlpha =
                Bg.Mode == 0
                && (
                    format == OutputFormat.WebpTransparent
                    || format == OutputFormat.WebmTransparent
                    || format == OutputFormat.PngSequence
                );
            _animExportFormat = format;
            _animExportTrim = _config.TrimDeadspace && (!effect || EffectKeepsAlpha(format));
            _animExportChain = chain;
            if (effect)
                BeginEffectExport(total);

            //Frames render at ss times the capture size and the pipeline resolves them before
            //readback, so each is output sized. Even dimensions keep the RGBA stride aligned with
            //ffmpeg's -video_size. The pipeline keeps this size until the export ends.
            var (_, capW, capH) = CaptureSizes[_captureRes];
            int outW = capW & ~1,
                outH = capH & ~1;
            _animExportSupersample = ScenePipeline.ClampSupersample(
                _config.ExportSupersample,
                outW,
                outH
            );
            _pipeline.ExportScaleOverride = _animExportSupersample;
            _pipeline.Resize(outW, outH);

            //Precompute the full-frame background to composite over (null = keep alpha). Built at
            //export resolution and bottom-up to match the OpenGL frames before ffmpeg's vflip.
            _animExportBg = keepAlpha
                ? null
                : ExportUtil.BuildBackground(outW, outH, Bg, bottomUp: true);
            //An effect renders over the background itself rather than being composited on it.
            if (effect && !EffectKeepsAlpha(format))
            {
                _pipeline.SetBackgroundBuffer(Bg.Mode == 2 ? _animExportBg : null, outW, outH);
                _animExportBg = null;
            }

            //Frames stream into ffmpeg as they are rendered. Always render transparent (alpha
            //oracle for the crop); the background is composited into each frame on the way out.
            _bufferedExporter = new BufferedAnimExporter();
            if (
                !_bufferedExporter.StartCapture(
                    outW,
                    outH,
                    _exportFps,
                    path,
                    _animExportTrim,
                    _animExportFormat,
                    _animExportBg,
                    _config.WebpQuality,
                    _config.TrimMarginPx
                )
            )
            {
                Console.WriteLine($"[UI] Export failed: {_bufferedExporter.Error}");
                _bufferedExporter.Dispose();
                _bufferedExporter = null;
                _pipeline.ExportScaleOverride = 0;
                if (effect)
                    EndEffectExport();
                return;
            }

            _animExporting = true;
            _convergeCaptured = false;
            if (effect)
                return;
            PlaybackSetPaused(true);
            //Restart cloth from rest so the first exported frame is reproducible; a chain resets
            //once here then runs continuously across steps (ChainSeek rebinds without a reset).
            if (_animExportChain)
                BeginChain();
            else if (_standalone == null)
                _scene?.ResetHairPhysics();

            //Let cloth/hair sims settle before the first captured frame (see PrerollLoops).
            RunPhysicsWarmup();
        }

        //Max warm-up loops surfaced in the Settings slider; bounds the synchronous pre-roll.
        internal const int PrerollMaxLoops = 5;

        //Longest convergence tail, in seconds. A short clip gets a quarter of its length
        //instead so the blend never eats a meaningful part of the animation.
        const float ConvergeMaxSeconds = 0.25f;

        //Set once the first exported frame has been simulated, so the pose it converges
        //back to is the one that frame actually rendered.
        bool _convergeCaptured;

        /// <summary>
        /// How strongly the frame at <paramref name="index"/> is pulled back toward the
        /// pose captured at the start. 0 until the tail begins, 1 on the final frame.
        /// Smoothstepped: a straight ramp lands the position continuously but kinks the
        /// velocity where the tail starts, which reads as a flinch.
        /// </summary>
        float ConvergeWeight(float index)
        {
            if (!_config.PhysicsConverge || _animExportTotal <= 0)
                return 0;

            float outputFrames = MathF.Ceiling(_animExportTotal / _animExportAdvance);
            if (outputFrames < 2)
                return 0;

            float tail =
                Math.Min(ConvergeMaxSeconds, (outputFrames / _exportFps) * 0.25f) * _exportFps;
            if (tail < 1)
                return 0;

            float remaining = (outputFrames - 1) - (index / _animExportAdvance);
            float t = Math.Clamp(1f - (remaining / tail), 0f, 1f);
            return t * t * (3f - 2f * t);
        }

        //Plays the first animation PrerollLoops times without capturing, so the cloth is settled
        //when recording starts. Runs synchronously at the export's 1/fps step.
        void RunPhysicsWarmup()
        {
            int loops = Math.Clamp(_config.PrerollLoops, 0, PrerollMaxLoops);
            if (loops <= 0)
                return;
            //Warm-up animation = the first step of a chain, or the single anim itself.
            float frames = _animExportChain
                ? Math.Max(PlaybackFrameCountOf(_animChain[0]), 1)
                : PlaybackFrameCount;
            if (frames < 1)
                return;

            float dt = 1f / _exportFps;
            for (int loop = 0; loop < loops; loop++)
            for (float idx = 0; idx < frames; idx += _animExportAdvance)
            {
                //Bind+seek the first step without a hair reset (ChainSeek stays in step 0 for
                //idx < frames), or scrub the single anim; then advance pose + cloth one frame.
                if (_animExportChain)
                    ChainSeek(idx);
                else
                    PlaybackSetFrame(idx);
                PlaybackUpdate(dt);
            }
        }

        void CaptureAnimExportFrame()
        {
            //Always render transparent (alpha oracle for the crop + composite). Matte the edge
            //fringe against the solid background color in Color mode (keeps a green key clean),
            //otherwise a neutral color; the real background is composited on the writer thread.
            var matte = Bg.Mode == 1 ? BgColorVec : _pipeline.BackgroundColor;
            bool transparent = true;
            if (_effect != null && !EffectKeepsAlpha(_animExportFormat))
            {
                matte = EffectBackground();
                transparent = false;
            }
            var buf = _bufferedExporter.RentFrameBuffer();
            if (buf != null)
            {
                _pipeline.CaptureFrameBytes(ActiveScene, matte, transparent, buf);
                _bufferedExporter.PushFrame(buf);
            }

            _animExportIndex += _animExportAdvance;
            if (_animExportIndex >= _animExportTotal)
                FinishAnimExport();
        }

        //Every frame is captured. The encode finishes on a worker, which the panel polls until
        //FinishBufferedExport clears it.
        void FinishAnimExport()
        {
            _bufferedExporter.FinishCapture();
            RestoreAfterExport();
        }

        //Cancel: stops without finishing the encode.
        void AbortAnimExport()
        {
            _bufferedExporter?.Abort();
            _bufferedExporter?.Dispose();
            _bufferedExporter = null;
            RestoreAfterExport();
        }

        //Hands the pipeline and the scene back as they were before the capture started.
        void RestoreAfterExport()
        {
            _pipeline.ExportScaleOverride = 0;
            _animExporting = false;
            if (_effect != null)
            {
                EndEffectExport();
                return;
            }
            PlaybackSetPaused(_animExportPrevPaused);
            PlaybackSetFrame(_animExportPrevFrame);
        }

        void FinishBufferedExport()
        {
            if (_bufferedExporter == null)
                return;
            if (_bufferedExporter.Error != null)
                Console.WriteLine($"[UI] Export failed: {_bufferedExporter.Error}");
            else
                Console.WriteLine($"[UI] Exported {_bufferedExporter.OutputPath}");
            _bufferedExporter.Dispose();
            _bufferedExporter = null;
            _animExportBg = null;
            ReleaseExportMemory();
        }
    }
}
