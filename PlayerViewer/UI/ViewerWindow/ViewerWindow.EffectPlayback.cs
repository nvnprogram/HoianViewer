using System;
using ImGuiNET;
using PlayerViewer.Effects.Sim;
using PlayerViewer.Effects.Viewer;
using Vector2 = System.Numerics.Vector2;
using Vector4 = System.Numerics.Vector4;

namespace PlayerViewer.UI
{
    // Right-hand panel of the effect viewer: playback, the timeline, the export clip and the
    // loop check, above the shared capture panel.
    public partial class ViewerWindow
    {
        void DrawEffectSidebar()
        {
            var play = EffectPlay;
            DrawEffectPlayback(play);
            DrawEffectClipSection(play);
            DrawCapturePanel();
        }

        void DrawEffectPlayback(EffectPlayback play)
        {
            Widgets.SectionHeader("Playback");
            if (play?.Set == null)
            {
                Widgets.DimText("Pick an emitter set.");
                return;
            }
            Widgets.BeginDisabled(_animExporting);
            if (SideOrderControls.On)
                DrawEffectPlaybackSideOrder(play);
            else
            {
                float spacing = ImGui.GetStyle().ItemSpacing.X;
                var (w, step) = PlaybackRow(ImGui.GetContentRegionAvail().X - 4 * spacing, 3);
                if (Widgets.Button(play.Playing ? "Pause" : "Play", new Vector2(w, 0)))
                    play.Playing = !play.Playing;
                ImGui.SameLine();
                DrawEffectStepButtons(play, w, step);

                ImGui.SetNextItemWidth(-1);
                Widgets.SliderFloat("##effectspeed", ref play.Speed, 0.05f, 4f, "speed %.2fx");

                DrawEffectTimeline(play);
                DrawEffectLoopLine(play);
            }
            Widgets.EndDisabled();
        }

        /// <summary>
        /// The player's animation card for an effect: the round play button beside the speed
        /// slider, the restart, fade and step buttons under the slider, then the timeline.
        /// </summary>
        void DrawEffectPlaybackSideOrder(EffectPlayback play)
        {
            var style = ImGui.GetStyle();
            float row = ImGui.GetFrameHeight();
            var start = ImGui.GetCursorScreenPos();
            if (Widgets.RoundPlayButton("##effectplay", play.Playing, out float x))
                play.Playing = !play.Playing;
            Widgets.ItemTooltip(play.Playing ? "Pause" : "Play");

            ImGui.SetNextItemWidth(-1);
            float column = ImGui.CalcTextSize("Speed 4.00x").X + 12;
            Widgets.SliderFloat(
                "##effectspeed",
                ref play.Speed,
                0.05f,
                4f,
                "Speed %.2fx",
                true,
                column
            );

            ImGui.SetCursorScreenPos(new Vector2(x, start.Y + row + style.ItemSpacing.Y));
            var (w, step) = PlaybackRow(
                ImGui.GetContentRegionAvail().X - 3 * style.ItemSpacing.X,
                2
            );
            DrawEffectStepButtons(play, w, step);
            ImGui.Dummy(new Vector2(0, 2));

            DrawEffectTimeline(play);
            DrawEffectLoopLine(play);
        }

        /// <summary>
        /// Splits a row's width, spacing taken out, into <paramref name="wide"/> equal buttons and
        /// the two step buttons, which narrow down to their arrows first so Fade out fits.
        /// </summary>
        static (float Wide, float Step) PlaybackRow(float width, int wide)
        {
            var pad = ImGui.GetStyle().FramePadding.X;
            float fits = ImGui.CalcTextSize("Fade out").X + 2 * pad;
            float least = ImGui.CalcTextSize("<").X + 2 * pad;
            float step = Math.Clamp(
                (width - wide * fits) / 2,
                least,
                ImGui.GetFrameHeight() * 1.5f
            );
            return ((width - 2 * step) / wide, step);
        }

        /// <summary>Restart, Fade out and the two frame steps, on one line.</summary>
        void DrawEffectStepButtons(EffectPlayback play, float w, float step)
        {
            if (Widgets.Button("Restart", new Vector2(w, 0)))
            {
                play.Restart();
                play.Playing = true;
            }
            ImGui.SameLine();
            if (Widgets.Button("Fade out", new Vector2(w, 0)))
                play.Fade();
            Widgets.ItemTooltip("Emitters stop and fade the way the game ends a looping effect.");
            ImGui.SameLine();
            if (Widgets.Button("<", new Vector2(step, 0)))
            {
                play.Playing = false;
                play.Seek(Math.Max(play.DisplayFrame - 1, 0));
            }
            Widgets.ItemTooltip("Back one frame");
            ImGui.SameLine();
            if (Widgets.Button(">", new Vector2(step, 0)))
            {
                play.Playing = false;
                play.Advance();
            }
            Widgets.ItemTooltip("Forward one frame");
        }

        void DrawEffectLoopLine(EffectPlayback play)
        {
            ImGui.PushTextWrapPos();
            Widgets.IndentLineStart();
            Widgets.ColoredText(LoopBadge(play.Loop).Item2, ShortLoopText(play.Loop));
            ImGui.PopTextWrapPos();
        }

        int _timelineStart;

        /// <summary>
        /// The timeline in frames: one second ticks, the export clip shaded, the frame on screen
        /// marked. A set playing on past its length moves the window along with it. Dragging on
        /// it scrubs, which replays from frame 0 when it goes backwards.
        /// </summary>
        void DrawEffectTimeline(EffectPlayback play)
        {
            int length = Math.Max(play.TimelineLength, 1);
            var (clipStart, clipLength) = EffectClip();
            length = Math.Max(length, clipStart + clipLength);
            int start = _timelineStart = play.TimelineStart(length, _timelineStart);
            int end = start + length;

            const float height = 30;
            float width = ImGui.GetContentRegionAvail().X;
            var p0 = ImGui.GetCursorScreenPos();
            ImGui.InvisibleButton("##effecttimeline", new Vector2(width, height));
            bool active = ImGui.IsItemActive();
            var dl = ImGui.GetWindowDrawList();
            const float pad = 4;
            float X(float frame) => p0.X + pad + (width - 2 * pad) * (frame - start) / length;
            float clipX0 = X(Math.Clamp(clipStart, start, end)),
                clipX1 = X(Math.Clamp(clipStart + clipLength, start, end));

            bool sideOrder = SideOrderControls.On;
            var clipColor = EffectClipIsLoop ? Theme.Cyan : Theme.GoldDim;
            if (sideOrder)
            {
                //A sunken well with the clip as a soft band in it, as the slider groove.
                SideOrderControls.Well(dl, p0, p0 + new Vector2(width, height), 10);
                var band = EffectClipIsLoop ? Theme.Cyan : SideOrderControls.Colours.TrackFill;
                if (clipX1 > clipX0)
                    dl.AddRectFilled(
                        new Vector2(clipX0, p0.Y + 4),
                        new Vector2(clipX1, p0.Y + height - 4),
                        ImGui.GetColorU32(new Vector4(band.X, band.Y, band.Z, 0.4f)),
                        6
                    );
            }
            else
            {
                dl.AddRectFilled(
                    p0,
                    p0 + new Vector2(width, height),
                    ImGui.GetColorU32(Theme.BgItem),
                    3
                );
                if (clipX1 > clipX0)
                    dl.AddRectFilled(
                        new Vector2(clipX0, p0.Y + 3),
                        new Vector2(clipX1, p0.Y + height - 3),
                        ImGui.GetColorU32(
                            new Vector4(clipColor.X, clipColor.Y, clipColor.Z, 0.28f)
                        ),
                        2
                    );
            }
            int step =
                length > 60 * 30 ? 600
                : length > 60 * 8 ? 120
                : 60;
            for (int f = (start + step - 1) / step * step; f <= end; f += step)
            {
                float x = X(f);
                dl.AddLine(
                    new Vector2(x, p0.Y + height - 7),
                    new Vector2(x, p0.Y + height - 2),
                    ImGui.GetColorU32(Theme.TextDim)
                );
                if (f + step <= end || f == start)
                    Widgets.DrawText(
                        dl,
                        new Vector2(x + 2, p0.Y + 1),
                        ImGui.GetColorU32(Theme.TextDim),
                        $"{f / 60}s"
                    );
            }
            int shown = Math.Max(play.DisplayFrame, 0);
            float mx = X(Math.Clamp(shown, start, end));
            if (sideOrder)
            {
                //The playhead as a slim raised pill in the thumb colour.
                var c = SideOrderControls.Colours;
                var a = new Vector2(MathF.Round(mx) - 2, p0.Y + 3);
                var b = new Vector2(MathF.Round(mx) + 2, p0.Y + height - 3);
                SideOrderControls.Shadow(dl, a, b, 2, 1.2f);
                SideOrderControls.Gradient(dl, a, b, c.Thumb, c.AccentBottom, 2);
            }
            else
                dl.AddLine(
                    new Vector2(mx, p0.Y + 1),
                    new Vector2(mx, p0.Y + height - 1),
                    ImGui.GetColorU32(Theme.GoldBright),
                    2
                );

            if (active)
            {
                float t = (ImGui.GetIO().MousePos.X - p0.X - pad) / (width - 2 * pad);
                int frame = Math.Clamp(start + (int)MathF.Round(t * length), start, end - 1);
                play.Playing = false;
                if (frame != play.DisplayFrame)
                    play.Seek(frame);
            }
            if (Widgets.ItemHovered())
            {
                float t = (ImGui.GetIO().MousePos.X - p0.X - pad) / (width - 2 * pad);
                int frame = Math.Clamp(start + (int)MathF.Round(t * length), start, end - 1);
                Widgets.PlainTooltip($"frame {frame}  ({frame / 60f:0.00} s)");
            }

            Widgets.DimText(
                $"frame {shown} / {Math.Max(end, length)}   {shown / 60f:0.00} s"
                    + (
                        play.Ending ? "   ending"
                        : !play.Alive ? "   ended"
                        : ""
                    )
            );
        }

        void DrawEffectClipSection(EffectPlayback play)
        {
            if (play?.Set == null)
                return;
            Widgets.SectionHeader("Clip");
            var fx = _config.Effect;
            var loop = play.Loop;
            bool loopable = loop is { Kind: not LoopKind.NotLoopable };
            bool exporting = _animExporting;

            bool custom = fx.CustomClip || !loopable;
            Widgets.BeginDisabled(!loopable);
            if (Widgets.RadioButton("Loop clip", !custom) && !exporting)
            {
                fx.CustomClip = false;
                _config.Save();
            }
            Widgets.EndDisabled();
            ImGui.SameLine();
            if (Widgets.RadioButton("Custom", custom) && !exporting)
            {
                fx.CustomClip = true;
                _config.Save();
            }

            var (start, length) = EffectClip();
            if (custom)
            {
                float half = (ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X) / 2;
                int s = fx.ClipStart,
                    l = fx.ClipLength;
                ImGui.SetNextItemWidth(half);
                if (
                    Widgets.Framed(() =>
                        ImGui.DragInt("##clipstart", ref s, 1, 0, 36000, "start %d")
                    ) && !exporting
                )
                {
                    fx.ClipStart = Math.Max(s, 0);
                    _config.Save();
                }
                ImGui.SameLine();
                ImGui.SetNextItemWidth(half);
                if (
                    Widgets.Framed(() =>
                        ImGui.DragInt("##cliplength", ref l, 1, 1, 36000, "length %d")
                    ) && !exporting
                )
                {
                    fx.ClipLength = Math.Max(l, 1);
                    play.FallbackLength = Math.Max(fx.ClipStart + fx.ClipLength, 60);
                    _config.Save();
                }
                Widgets.DimText($"{Seconds(start)} to {Seconds(start + length)}");
            }
            else if (loop.Kind == LoopKind.Periodic)
            {
                Widgets.DimText(
                    $"[{start}, {start + length}): {Seconds(length)} after {Seconds(start)} warm-up"
                );
                if (play.Mode != RandomMode.Loop)
                    Widgets.DimText("Exported with loop randomness.");
            }
            else
                Widgets.DimText($"[0, {length}): the whole one-shot, {Seconds(length)}");

            float min = fx.MinLoopSeconds;
            ImGui.SetNextItemWidth(-1);
            if (Widgets.SliderFloat("##minloop", ref min, 0f, 20f, "min loop %.1f s") && !exporting)
            {
                fx.MinLoopSeconds = MathF.Round(min * 10) / 10;
                _config.Save();
            }
            Widgets.ItemTooltip("Loops a periodic set over its first period at least this long.");
            if (_exportFps == 30 && length % 2 != 0)
                Widgets.DimText("An odd frame count at 30 fps drops the last frame.");

            Widgets.CheckboxControl("Preview clip on repeat", ref _effectPreviewClip);
            Widgets.ItemTooltip("Plays only the clip, over and over, the way it is exported.");
            Widgets.DisabledButton(
                "Check loop",
                loopable && !exporting && !custom,
                CheckEffectLoop
            );
            Widgets.ItemTooltip("Checks the loop's two ends match, or that a one-shot ends empty.");
            if (_loopCheck != null)
            {
                ImGui.PushTextWrapPos();
                Widgets.ColoredText(_loopCheckOk ? Theme.Success : Theme.Error, _loopCheck);
                ImGui.PopTextWrapPos();
            }
        }
    }
}
