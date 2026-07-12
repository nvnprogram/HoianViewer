using System;
using System.Collections.Generic;
using Vector2 = System.Numerics.Vector2;
using Vector4 = System.Numerics.Vector4;
using ImGuiNET;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

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

        static readonly string[] ExportFormatLabels =
        {
            "PNG (current frame)",
            "MP4 (greenscreen)",
            "WebP (transparent)",
            "Record (real-time)",
        };

        void DrawRightSidebar()
        {
            DrawPlaybackControls();

            float spacing = ImGui.GetStyle().ItemSpacing.Y;
            float avail = ImGui.GetContentRegionAvail().Y;
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

            void SetPaused(bool value) { if (standalone) _standalone.AnimPaused = value; else _scene.AnimPaused = value; }
            void SetSpeed(float value) { if (standalone) _standalone.AnimSpeed = value; else _scene.AnimSpeed = value; }
            void SetFrame(float value) { if (standalone) _standalone.SetAnimFrame(value); else _scene.SetAnimFrame(value); }

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
        }

        void DrawAnimList(float height)
        {
            bool standalone = _standalone != null;
            string currentAnim = standalone ? _standalone.CurrentAnimName : _scene.CurrentAnimName;
            List<string> animNames = standalone ? _standalone.AnimNames : _scene.Anims.AnimNames;

            void SetPaused(bool value) { if (standalone) _standalone.AnimPaused = value; else _scene.AnimPaused = value; }
            void Play(string name) { StopAnimChain(); if (standalone) _standalone.PlayAnim(name); else _scene.PlayAnim(name); }

            ImGui.BeginChild("##animlist", new Vector2(0, height), true);
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

        void RedButton(string label, Action onClick)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.55f, 0.12f, 0.10f, 1));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.70f, 0.16f, 0.13f, 1));
            if (ImGui.Button(label, new Vector2(-1, 0)))
                onClick();
            ImGui.PopStyleColor(2);
        }

        void DrawCapturePanel()
        {
            Widgets.SectionHeader("Capture");

            bool haveFfmpeg = VideoRecorder.FfmpegAvailable;

            //--- Busy states: render phase, buffered encode phase, or a
            // real-time recording. Each shows its own progress/stop control.
            if (_animExporting)
            {
                float progress = _animExportTotal > 0
                    ? Math.Min(_animExportIndex / _animExportTotal, 1f) : 0f;
                int shown = (int)Math.Min(_animExportIndex + 1, _animExportTotal);
                ImGui.ProgressBar(progress, new Vector2(-1, 0), $"Rendering {shown}/{_animExportTotal}");
                RedButton("Cancel export", AbortAnimExport);
                return;
            }
            if (_bufferedExporter != null)
            {
                if (_bufferedExporter.IsEncoding)
                {
                    var ex = _bufferedExporter;
                    float p = ex.EncodeTotal > 0
                        ? Math.Min(ex.EncodeProgress / (float)ex.EncodeTotal, 1f) : 0f;
                    ImGui.ProgressBar(p, new Vector2(-1, 0), $"Encoding {ex.EncodeProgress}/{ex.EncodeTotal}");
                    return;
                }
                //Encode finished on the worker thread: log and clear.
                FinishBufferedExport();
            }
            if (_recorder.IsRecording)
            {
                //"###" keeps the widget ID stable while the timer in the label changes,
                //otherwise the click never registers (ID differs between press/release).
                RedButton($"Stop recording ({_recorder.FrameCount / 60.0f:F1}s)###stoprec", StopRecording);
                return;
            }

            //--- Idle: resolution + format options, then one Export button.
            ImGui.SetNextItemWidth(-1);
            if (ImGui.BeginCombo("##capres", CaptureSizes[_captureRes].Label))
            {
                for (int i = 0; i < CaptureSizes.Length; i++)
                    if (ImGui.Selectable(CaptureSizes[i].Label, i == _captureRes))
                        _captureRes = i;
                ImGui.EndCombo();
            }

            ImGui.SetNextItemWidth(-1);
            ImGui.Combo("##exportformat", ref _exportFormat, ExportFormatLabels, ExportFormatLabels.Length);

            bool isPng = _exportFormat == 0;
            bool isRecord = _exportFormat == 3;
            bool isAnim = _exportFormat == 1 || _exportFormat == 2;

            //Format-specific option row.
            if (isPng)
                ImGui.Checkbox("Transparent background", ref _captureTransparent);
            else if (isRecord)
                ImGui.Checkbox("Greenscreen", ref _recordGreenscreen);

            if (isAnim)
            {
                ImGui.AlignTextToFramePadding();
                ImGui.TextColored(Theme.TextDim, "FPS");
                ImGui.SameLine();
                if (ImGui.RadioButton("30", _exportFps == 30)) _exportFps = 30;
                ImGui.SameLine();
                if (ImGui.RadioButton("60", _exportFps == 60)) _exportFps = 60;
            }

            //Trim only applies where we have alpha to detect emptiness (not real-time record).
            bool trimApplies = _config.TrimDeadspace && !isRecord && (!isPng || _captureTransparent);
            if (trimApplies)
                ImGui.TextColored(Theme.TextDim, $"Trim deadspace on (+{_config.TrimMarginPx}px)");

            //In Sequence mode an animation export runs the whole chain instead of the current anim.
            bool exportChain = _animMode == 1 && isAnim;
            bool animReady = exportChain ? _animChain.Count > 0 : PlaybackHasAnim;
            bool needFfmpeg = !isPng;
            bool canExport = (!needFfmpeg || haveFfmpeg) && (!isAnim || animReady);
            Widgets.DisabledButton(ExportButtonLabel(exportChain), canExport, DoExport);

            if (needFfmpeg && !haveFfmpeg)
                ImGui.TextColored(Theme.TextDim, "ffmpeg not found (app folder or PATH)");
            else if (isAnim && !animReady)
                ImGui.TextColored(Theme.TextDim,
                    exportChain ? "add animations to the sequence" : "select an animation to export");
        }

        string ExportButtonLabel(bool sequence) => _exportFormat switch
        {
            0 => "Export PNG",
            1 => sequence ? "Export MP4 (sequence)" : "Export MP4",
            2 => sequence ? "Export WebP (sequence)" : "Export WebP",
            _ => "Start recording",
        };

        void DoExport()
        {
            switch (_exportFormat)
            {
                case 0: SaveScreenshot(); break;
                case 1: StartAnimExport(VideoRecorder.OutputFormat.Mp4, transparent: false); break;
                case 2: StartAnimExport(VideoRecorder.OutputFormat.WebpTransparent, transparent: true); break;
                case 3: StartRecording(); break;
            }
        }

        void SaveScreenshot()
        {
            string def = ExportUtil.Timestamped("player", ".png");
            string path = NativeFolderPicker.SaveFile("Save Screenshot", def, "PNG image (*.png)", "*.png");
            if (string.IsNullOrEmpty(path))
                return;
            if (!path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                path += ".png";
            WriteScreenshot(path);
        }

        //Renders and saves a still at the chosen capture size (no dialog). With trim, renders at
        //the internal (supersampled) resolution and crops from it so a loosely-framed subject
        //stays sharp; otherwise supersamples for anti-aliasing and downsamples to the capture size.
        void WriteScreenshot(string path)
        {
            var (_, w, h) = CaptureSizes[_captureRes];
            int ss = Math.Clamp(_config.ExportSupersample, 1, 8);
            bool trim = _config.TrimDeadspace && _captureTransparent;
            using var img = trim
                ? _pipeline.Capture(ActiveScene, w * ss, h * ss, _pipeline.BackgroundColor, _captureTransparent, 1)
                : _pipeline.Capture(ActiveScene, w, h, _pipeline.BackgroundColor, _captureTransparent, ss);
            if (trim)
                TrimImage(img, _config.TrimMarginPx * ss);
            img.SaveAsPng(path);
            Console.WriteLine($"[UI] Saved {path}");
        }

        //Crops fully-transparent (alpha==0) deadspace off a captured image in place, keeping
        //a margin. No-op if the frame is entirely transparent or nothing would be trimmed.
        static void TrimImage(Image<Rgba32> img, int margin)
        {
            int w = img.Width, h = img.Height;
            int minX = w, minY = h, maxX = -1, maxY = -1;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    if (img[x, y].A != 0)
                    {
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;
                    }

            if (maxX < 0)
                return;
            int x0 = Math.Max(0, minX - margin), y0 = Math.Max(0, minY - margin);
            int x1 = Math.Min(w - 1, maxX + margin), y1 = Math.Min(h - 1, maxY + margin);
            int cw = x1 - x0 + 1, ch = y1 - y0 + 1;
            if (cw >= w && ch >= h)
                return;
            img.Mutate(c => c.Crop(new Rectangle(x0, y0, cw, ch)));
        }

        bool _recordGreenscreen = true;
        System.Numerics.Vector3 _backgroundBeforeRecord;

        void StartRecording()
        {
            string def = ExportUtil.Timestamped("player", ".mp4");
            string path = NativeFolderPicker.SaveFile("Save Video", def, "MP4 video (*.mp4)", "*.mp4");
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

        //--- Playback bridge: both scene types expose the same animation surface but
        //share no interface for it, so route through the active one.
        bool PlaybackHasAnim => (_standalone != null ? _standalone.CurrentSkeletal : _scene?.CurrentSkeletal) != null;
        int PlaybackFrameCount => (int)Math.Round(_standalone != null
            ? (_standalone.CurrentSkeletal?.FrameCount ?? 0f)
            : (_scene?.CurrentSkeletal?.FrameCount ?? 0f));
        float PlaybackAnimFrame => _standalone != null ? _standalone.AnimFrame : (_scene?.AnimFrame ?? 0f);
        bool PlaybackPaused => _standalone != null ? _standalone.AnimPaused : (_scene?.AnimPaused ?? true);
        float PlaybackSpeed => _standalone != null ? _standalone.AnimSpeed : (_scene?.AnimSpeed ?? 1f);
        void PlaybackSetPaused(bool v) { if (_standalone != null) _standalone.AnimPaused = v; else if (_scene != null) _scene.AnimPaused = v; }
        void PlaybackSetFrame(float f) { if (_standalone != null) _standalone.SetAnimFrame(f); else _scene?.SetAnimFrame(f); }
        void PlaybackUpdate(float dt) { if (_standalone != null) _standalone.Update(dt); else _scene?.Update(dt); }
        void PlaybackPlay(string name, bool resetHair) { if (_standalone != null) _standalone.PlayAnim(name); else _scene?.PlayAnim(name, resetHair); }
        void PlaybackResetHair() { if (_standalone == null) _scene?.ResetHairPhysics(); }
        string PlaybackCurrentAnim => _standalone != null ? _standalone.CurrentAnimName : _scene?.CurrentAnimName;
        List<string> PlaybackAnimNames => _standalone != null ? _standalone.AnimNames : (_scene?.Anims.AnimNames ?? new List<string>());
        int PlaybackFrameCountOf(string name) => _standalone != null ? _standalone.SkeletalFrameCount(name) : (_scene?.SkeletalFrameCount(name) ?? 0);

        void StartAnimExport(VideoRecorder.OutputFormat format, bool transparent)
        {
            bool chain = _animMode == 1 && _animChain.Count > 0;
            if (_animExporting || _recorder.IsRecording || _bufferedExporter != null)
                return;
            if (!chain && !PlaybackHasAnim)
                return;
            StopAnimChain();   //deterministic export drives frames itself; don't let the preview fight it
            int total = chain ? (int)Math.Round(ChainTotalFrames()) : PlaybackFrameCount;
            if (total < 1)
                return;

            string ext = transparent ? ".webp" : ".mp4";
            string def = ExportUtil.Timestamped("animation", ext);
            string path = transparent
                ? NativeFolderPicker.SaveFile("Export Animation", def, "WebP image (*.webp)", "*.webp")
                : NativeFolderPicker.SaveFile("Export Animation", def, "MP4 video (*.mp4)", "*.mp4");
            if (string.IsNullOrEmpty(path))
                return;
            if (!path.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                path += ext;

            _animExportPrevBg = _pipeline.BackgroundColor;
            _animExportPrevPaused = PlaybackPaused;
            _animExportPrevFrame = PlaybackAnimFrame;

            //Respect the speed slider by up/downsampling: at 2x we advance the animation
            //cursor twice as far per output frame (fewer frames, shorter clip); at 0.5x,
            //half as far (more frames). Snapshotted so mid-export slider moves don't matter.
            float speed = PlaybackSpeed;
            _animExportAdvance = Math.Max(0.0001f, (60f / _exportFps) * speed);
            _animExportTotal = total;
            _animExportIndex = 0f;
            _animExportTransparent = transparent;
            _animExportFormat = format;
            _animExportGreen = new System.Numerics.Vector3(0, 1, 0);
            _animExportTrim = _config.TrimDeadspace;
            _animExportSupersample = Math.Clamp(_config.ExportSupersample, 1, 8);
            _animExportChain = chain;

            //Supersample the render. With trim on, the crop keeps the full internal resolution
            //(scale 1, larger frame) so a loosely-framed subject stays sharp; without trim, the
            //factor becomes anti-alias supersampling downsampled back to the capture size. Even
            //dimensions keep the raw RGBA stride aligned with ffmpeg's -video_size. Resize is
            //frozen elsewhere for the duration because an export is in progress.
            int ss = _animExportSupersample;
            int bw = _pipeline.Width & ~1, bh = _pipeline.Height & ~1;
            int outW, outH, scale;
            if (_animExportTrim) { outW = (bw * ss) & ~1; outH = (bh * ss) & ~1; scale = 1; }
            else { outW = bw; outH = bh; scale = ss; }
            _pipeline.ExportScaleOverride = scale;
            _pipeline.Resize(outW, outH);

            //Buffer raw frames to disk then encode: faster than piping to ffmpeg live (the
            //render loop never stalls on the encoder). Always render transparent (alpha oracle
            //for the crop); the greenscreen for MP4 is composited during the encode pass.
            _bufferedExporter = new BufferedAnimExporter();
            if (!_bufferedExporter.StartCapture(outW, outH, _exportFps, path, _animExportTrim))
            {
                Console.WriteLine($"[UI] Export failed: {_bufferedExporter.Error}");
                _bufferedExporter.Dispose();
                _bufferedExporter = null;
                _pipeline.ExportScaleOverride = 0;
                return;
            }

            _animExporting = true;
            PlaybackSetPaused(true);
            //Restart cloth from rest so the first exported frame is reproducible; a chain resets
            //once here then runs continuously across steps (ChainSeek rebinds without a reset).
            if (_animExportChain)
                BeginChain();
            else if (_standalone == null)
                _scene?.ResetHairPhysics();
        }

        void CaptureAnimExportFrame()
        {
            //Always render transparent (alpha oracle for the crop), matching the edge matte to
            //the output: WebP keeps this RGB against straight alpha, so a green matte would
            //fringe the cutout; matte it against the viewport background like the PNG path. MP4
            //composites over green in the encode pass, so green edges there key cleanly.
            var matte = _animExportTransparent ? _pipeline.BackgroundColor : _animExportGreen;
            var bytes = _pipeline.CaptureFrameBytes(ActiveScene, matte, transparent: true, out _, out _);
            _bufferedExporter.PushFrame(bytes);

            _animExportIndex += _animExportAdvance;
            if (_animExportIndex >= _animExportTotal)
                FinishAnimExport();
        }

        //Natural completion: the render/capture phase is done. Kick off the encode pass on a
        //worker; the panel polls _bufferedExporter for progress and clears it via
        //FinishBufferedExport when encoding completes.
        void FinishAnimExport()
        {
            //Margin is applied at the crop's (internal) resolution, so scale it with the
            //supersample factor to keep the visual padding consistent.
            _bufferedExporter.FinishCapture(_animExportFormat, _animExportGreen,
                _config.WebpQuality, _config.TrimMarginPx * _animExportSupersample);
            _pipeline.ExportScaleOverride = 0;
            _pipeline.BackgroundColor = _animExportPrevBg;
            PlaybackSetPaused(_animExportPrevPaused);
            PlaybackSetFrame(_animExportPrevFrame);
            _animExporting = false;
        }

        //Cancel button: abort without finishing the encode
        void AbortAnimExport()
        {
            _bufferedExporter?.Abort();
            _bufferedExporter?.Dispose();
            _bufferedExporter = null;
            _pipeline.ExportScaleOverride = 0;
            _pipeline.BackgroundColor = _animExportPrevBg;
            PlaybackSetPaused(_animExportPrevPaused);
            PlaybackSetFrame(_animExportPrevFrame);
            _animExporting = false;
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
        }
    }
}
