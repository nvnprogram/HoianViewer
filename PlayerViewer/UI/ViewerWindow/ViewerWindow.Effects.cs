using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using EffectLibrary;
using PlayerViewer.Effects.Sim;
using PlayerViewer.Effects.Viewer;

namespace PlayerViewer.UI
{
    // The effect viewer's lifecycle: opening a file, choosing a set, stepping it, framing it,
    // and the pieces of the export that differ from a model's.
    public partial class ViewerWindow
    {
        EffectScene _effect;
        EffectFile _effectFile;
        string _effectError;

        /// <summary>--set: the emitter set to select once the file given to --open is loaded.</summary>
        public string AutoOpenSet;

        System.Numerics.Vector3 _backgroundBeforeEffect;

        int EffectMinLoopFrames => (int)MathF.Round(_config.Effect.MinLoopSeconds * 60);

        EffectPlayback EffectPlay => _effect?.Playback;

        /// <summary>Opens an esetb as the effect scene, closing a standalone model if one is open.</summary>
        void OpenEffect(string path, string setName = null)
        {
            if (_scene == null || ExportBusy)
                return;
            EffectFile file;
            try
            {
                file = EffectFile.Open(path, EffectMinLoopFrames);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Effect] Could not open {path}: {ex}");
                if (_effect != null)
                    _effectError = $"Could not open {Path.GetFileName(path)}: {ex.Message}";
                else
                    _standaloneError = $"Could not open {Path.GetFileName(path)}: {ex.Message}";
                return;
            }

            if (_standalone != null)
            {
                TearDownStandalone();
                CompactHeap();
            }
            bool first = _effect == null;
            if (first)
            {
                _effect = new EffectScene { SceneParams = ActiveSceneParams };
                _backgroundBeforeEffect = _pipeline.BackgroundColor;
                var bg = _config.Effect.Background;
                _pipeline.BackgroundColor = new System.Numerics.Vector3(bg[0], bg[1], bg[2]);
            }
            ReleaseEffectFile();
            _effectFile = file;
            _effectError = null;
            _effectSearch = "";
            _effectRows = null;
            _effect.Companion = _config.Effect.ShowPlayer ? _scene : null;
            _effect.ShowGrid = _config.Effect.ShowGrid;
            Console.WriteLine($"[Effect] Opened {path}: {file.Sets.Count} sets");

            var set =
                (setName != null ? file.Vfx.EmitterSets.Find(setName) : null)
                ?? (file.Sets.Count > 0 ? file.Sets[0].Set : null);
            SelectEffectSet(set, frame: true);
            _effectScrollToSelection = true;
        }

        /// <summary>The previous file's GPU objects, programs and parsed data go.</summary>
        void ReleaseEffectFile()
        {
            _effect?.Playback.Clear();
            _effect?.ReleaseFileResources();
            _effectFile?.Dispose();
            _effectFile = null;
            _effectEmitter = null;
            _loopCheck = null;
            ReleaseShaderPrograms();
            CompactHeap();
        }

        /// <summary>Closes the effect scene and returns to the player.</summary>
        void CloseEffect()
        {
            if (ExportBusy)
                return;
            TearDownEffect();
            _pipeline.FramePlayer();
        }

        /// <summary>The one way the effect scene goes away, from a close, a model open or a romfs change.</summary>
        void TearDownEffect()
        {
            if (_effect == null)
                return;
            ReleaseEffectFile();
            _effect.Dispose();
            _effect = null;
            _effectError = null;
            _pipeline.BackgroundColor = _backgroundBeforeEffect;
            ReleaseShaderPrograms();
        }

        void SelectEffectSet(EmitterSet set, bool frame)
        {
            if (ExportBusy)
                return;
            _effectEmitter = null;
            _loopCheck = null;
            _effect.Hidden.Clear();
            var play = _effect.Playback;
            if (set == null)
            {
                play.Clear();
                return;
            }
            play.Translation = System.Numerics.Vector3.Zero;
            play.RotationDegrees = System.Numerics.Vector3.Zero;
            play.Scale = System.Numerics.Vector3.One;
            play.SetRandom(RandomMode.Game, EffectPlayback.DefaultSeed);
            play.FallbackLength = Math.Max(
                _config.Effect.ClipStart + _config.Effect.ClipLength,
                60
            );
            play.Playing = true;
            play.Select(set, _effectFile.Loop(set, EffectMinLoopFrames));
            _effectFile.Request(set, EffectMinLoopFrames);
            if (frame)
                FrameEffect();
        }

        /// <summary>Hands the playback a classification once the analysis thread has one.</summary>
        void PumpEffectLoop()
        {
            var play = EffectPlay;
            if (play?.Set == null || _effectFile == null)
                return;
            _effectFile.MinimumPeriod = EffectMinLoopFrames;
            var loop = _effectFile.Loop(play.Set, EffectMinLoopFrames);
            if (loop == null)
                _effectFile.Request(play.Set, EffectMinLoopFrames);
            else if (!ReferenceEquals(loop, play.Loop))
            {
                play.SetLoop(loop);
                if (loop.Unsure != null && !_animExporting)
                    SettleUnsureLoop(play, loop);
            }
        }

        /// <summary>
        /// A period the resource cannot vouch for is kept only if the loop check passes; otherwise
        /// the set is not loopable, for the file's list as well.
        /// </summary>
        void SettleUnsureLoop(EffectPlayback play, LoopInfo loop)
        {
            CheckEffectLoop();
            var settled = _loopCheckOk
                ? loop with { Unsure = null }
                : LoopInfo.NotLoopable($"{loop.Unsure}, and the loop check found: {_loopCheck}");
            _effectFile.Settle(play.Set, EffectMinLoopFrames, settled);
            play.SetLoop(settled);
        }

        void UpdateEffect(float dt)
        {
            PumpEffectLoop();
            var play = EffectPlay;
            //Into the preview it starts at the clip; out of it the set plays from its start.
            if (
                play.Set != null
                && !_animExporting
                && PreviewRandom() is var mode
                && mode != play.Mode
            )
                play.SetRandom(
                    mode,
                    EffectPlayback.DefaultSeed,
                    mode == RandomMode.Loop ? EffectClip().Start : 0
                );
            play.Update(dt);
            if (_effectPreviewClip && play.Set != null && !play.Ending)
            {
                var (start, length) = EffectClip();
                int shown = play.DisplayFrame;
                if (shown < start || shown >= start + length)
                    play.Seek(start);
            }
            if (_effect.Companion != null)
                _scene?.Update(dt);
        }

        /// <summary>
        /// The game's own randomness, except while a periodic loop clip previews on repeat: that
        /// needs randomness that repeats with the period, as the export has.
        /// </summary>
        RandomMode PreviewRandom() =>
            _effectPreviewClip && EffectClipIsLoop && EffectPlay.Loop.Kind == LoopKind.Periodic
                ? RandomMode.Loop
                : RandomMode.Game;

        /// <summary>Points the camera at where the set's particles go over its first seconds.</summary>
        void FrameEffect()
        {
            var play = EffectPlay;
            if (play?.Set == null)
                return;
            int frames = Math.Clamp(play.TimelineLength, 120, 600);
            var s = EffectBounds.Measure(play.Set, play.SetMatrix, frames, play.Primitives);
            var sphere = new OpenTK.Vector4(s.X, s.Y, s.Z, s.W * 1.1f);
            _pipeline.FrameSphere(sphere);
            var cam = _pipeline.Camera;
            cam.TargetPosition = new OpenTK.Vector3(s.X, s.Y, s.Z);
            cam.TargetDistance = Math.Max(cam.TargetDistance, MinFrameDistance);
            cam.RotationX = 0.3f;
            cam.RotationY = -0.5f;
            cam.UpdateMatrices();
        }

        /// <summary>Particles fade out close to the camera, a few units in, as they do in game.</summary>
        const float MinFrameDistance = 4f;

        /// <summary>
        /// The frames an export records, in simulation frames: the set's loop unless a custom clip
        /// is chosen or it has none. A periodic loop needs loop randomness to be exact.
        /// </summary>
        (int Start, int Length) EffectClip()
        {
            var loop = EffectPlay?.Loop;
            if (!_config.Effect.CustomClip && loop != null)
            {
                if (loop.Kind == LoopKind.OneShot)
                    return (0, Math.Max(loop.Length, 1));
                if (loop.Kind == LoopKind.Periodic)
                    return (loop.Start, loop.Period);
            }
            return (_config.Effect.ClipStart, _config.Effect.ClipLength);
        }

        bool EffectClipIsLoop =>
            !_config.Effect.CustomClip && EffectPlay?.Loop is { Kind: not LoopKind.NotLoopable };

        //--- Export: the playback's state before it, put back afterwards.
        int _effectExportStart;
        bool _effectPrevPlaying;
        int _effectPrevFrame;
        RandomMode _effectPrevMode;

        /// <summary>
        /// Whether an effect export renders with a transparent background. Anything else renders
        /// the effect over its background directly, since additive blending cannot be kept in a
        /// straight alpha frame and composited back exactly.
        /// </summary>
        bool EffectKeepsAlpha(OutputFormat? format) =>
            Bg.Mode == 0 && format is not OutputFormat.Mp4;

        /// <summary>Background colour and image a non transparent effect render goes over.</summary>
        System.Numerics.Vector3 EffectBackground() =>
            Bg.Mode == 1 ? BgColorVec : System.Numerics.Vector3.Zero;

        void BeginEffectExport(int total)
        {
            var play = EffectPlay;
            var (start, _) = EffectClip();
            _effectExportStart = start;
            _animExportTotal = total;
            _animExportAdvance = 60f / _exportFps;
            _effectPrevPlaying = play.Playing;
            _effectPrevFrame = play.DisplayFrame;
            _effectPrevMode = play.Mode;
            if (EffectClipIsLoop && play.Loop.Kind == LoopKind.Periodic)
                play.SetRandom(RandomMode.Loop, EffectPlayback.DefaultSeed);
            play.Playing = false;
            play.Seek(start);
        }

        void EndEffectExport()
        {
            var play = EffectPlay;
            if (play == null)
                return;
            play.SetRandom(_effectPrevMode, EffectPlayback.DefaultSeed);
            play.Seek(_effectPrevFrame);
            play.Playing = _effectPrevPlaying;
            _bgDirty = true;
        }

        //--- Loop check
        string _loopCheck;
        bool _loopCheckOk;
        bool _effectPreviewClip;

        /// <summary>
        /// Renders the two frames a loop joins and compares them: for a periodic set its clip
        /// start and the frame one period later, which must be identical; for a one-shot its last
        /// frame, which must be empty.
        /// </summary>
        void CheckEffectLoop() => CheckEffectLoop(false, null);

        /// <summary>
        /// The loop check with the later frame on the loop's emitter time when
        /// <paramref name="rebaseTime"/> is set, so only infinite life ages run on, and the two
        /// periodic frames handed to <paramref name="frames"/>.
        /// </summary>
        void CheckEffectLoop(bool rebaseTime, Action<byte[], byte[], int, int> frames)
        {
            var play = EffectPlay;
            var loop = play?.Loop;
            if (loop == null || loop.Kind == LoopKind.NotLoopable)
                return;
            bool wasPlaying = play.Playing;
            int wasFrame = play.DisplayFrame;
            var wasMode = play.Mode;
            try
            {
                int w = _pipeline.Width,
                    h = _pipeline.Height;
                if (loop.Kind == LoopKind.Periodic)
                {
                    // The clip wraps from its last frame back to Start, so Start must look exactly
                    // like the set one period on with its own time, not the loop's rebased one.
                    play.SetRandom(RandomMode.Loop, EffectPlayback.DefaultSeed);
                    var a = RenderEffectFrame(loop.Start, w, h);
                    play.RebaseTime = rebaseTime;
                    play.RebaseAges = false;
                    byte[] b;
                    try
                    {
                        b = RenderEffectFrame(loop.Start + loop.Period, w, h);
                    }
                    finally
                    {
                        play.RebaseTime = true;
                        play.RebaseAges = true;
                    }
                    frames?.Invoke(a, b, w, h);
                    int differ = 0,
                        worst = 0;
                    for (int i = 0; i < a.Length; i += 4)
                    {
                        int d = 0;
                        for (int c = 0; c < 4; c++)
                            d = Math.Max(d, Math.Abs(a[i + c] - b[i + c]));
                        if (d > 0)
                            differ++;
                        worst = Math.Max(worst, d);
                    }
                    // Ages a period apart round differently, which can move a value by a step.
                    _loopCheckOk = worst <= 2;
                    _loopCheck =
                        differ == 0
                            ? $"Frames {loop.Start} and {loop.Start + loop.Period} are pixel identical."
                        : _loopCheckOk
                            ? $"Frames {loop.Start} and {loop.Start + loop.Period} differ in {differ} pixels by at most {worst}/255, float rounding."
                        : $"Frames {loop.Start} and {loop.Start + loop.Period} differ in {differ} pixels (max {worst}/255).";
                }
                else
                {
                    int last = Math.Max(loop.Length - 1, 0);
                    var a = RenderEffectFrame(last, w, h);
                    int shown = 0;
                    for (int i = 3; i < a.Length; i += 4)
                        if (a[i] != 0)
                            shown++;
                    _loopCheckOk = shown == 0;
                    _loopCheck = _loopCheckOk
                        ? $"Frame {last}, the last, is empty: the clip starts and ends clear."
                        : $"Frame {last}, the last, still shows {shown} pixels.";
                }
            }
            catch (Exception ex)
            {
                _loopCheckOk = false;
                _loopCheck = "Loop check failed: " + ex.Message;
            }
            finally
            {
                play.SetRandom(wasMode, EffectPlayback.DefaultSeed);
                play.Seek(wasFrame);
                play.Playing = wasPlaying;
            }
            Console.WriteLine($"[Effect] Loop check {play.Set?.Name}: {_loopCheck}");
        }

        /// <summary>One frame rendered off screen, once no program it draws with is still linking.</summary>
        byte[] RenderEffectFrame(int frame, int w, int h)
        {
            EffectPlay.Seek(frame);
            var buf = new byte[w * h * 4];
            for (int tries = 0; ; tries++)
            {
                _pipeline.CaptureFrameBytes(_effect, _pipeline.BackgroundColor, true, buf);
                if (_effect.PendingDraws == 0)
                    return buf;
                if (tries == 600)
                    throw new TimeoutException("its programs did not finish linking");
                System.Threading.Thread.Sleep(50);
            }
        }

        /// <summary>The romfs's own effect files, base and Side Order, found once per romfs.</summary>
        List<string> RomfsEffectFiles()
        {
            if (_romfsEffects == null && _romfs != null)
            {
                try
                {
                    _romfsEffects = _romfs.FindFiles("Effect", "*.esetb.byml.zs");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Effect] Could not list romfs effects: {ex.Message}");
                    _romfsEffects = new List<string>();
                }
            }
            return _romfsEffects ?? new List<string>();
        }

        List<string> _romfsEffects;
    }
}
