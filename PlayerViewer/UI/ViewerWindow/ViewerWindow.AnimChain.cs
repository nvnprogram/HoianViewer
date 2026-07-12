using System;
using System.Collections.Generic;
using System.Linq;
using Vector2 = System.Numerics.Vector2;
using Vector4 = System.Numerics.Vector4;
using ImGuiNET;

namespace PlayerViewer.UI
{
    // Animation chaining: play (and export) a sequence of skeletal animations as one continuous
    // take. The chain drives a single global frame cursor over the concatenated steps; crossing a
    // step boundary rebinds the next animation WITHOUT resetting the cloth sim, so hair flows
    // continuously the way it does across a normal loop wrap. Hair is reset only once at the start.
    // Preview is pure playback; export reuses the same seek so it is deterministic and frame-exact.
    public partial class ViewerWindow
    {
        readonly List<string> _animChain = new();
        int _animMode;            //0 = Single, 1 = Sequence
        bool _chainActive;        //interactive preview is playing
        bool _chainLoop;
        int _chainIndex = -1;     //step currently bound (shared by preview + export)
        int _chainSelected = -1;  //timeline segment selected in the editor
        float _chainCursor;       //global frame cursor for the interactive preview

        float ChainTotalFrames() => _animChain.Sum(n => (float)Math.Max(PlaybackFrameCountOf(n), 1));

        //Binds the step containing global frame g (rebinding only on a boundary, without a hair
        //reset) and positions its local frame. The caller runs the scene Update afterwards.
        void ChainSeek(float g)
        {
            if (_animChain.Count == 0)
                return;
            int i = 0;
            float acc = 0;
            for (; i < _animChain.Count - 1; i++)
            {
                float fc = Math.Max(PlaybackFrameCountOf(_animChain[i]), 1);
                if (g < acc + fc) break;
                acc += fc;
            }
            if (i != _chainIndex)
            {
                PlaybackPlay(_animChain[i], resetHair: false);
                PlaybackSetPaused(true);
                _chainIndex = i;
            }
            float localEnd = Math.Max(PlaybackFrameCountOf(_animChain[i]) - 1, 0);
            PlaybackSetFrame(Math.Min(g - acc, localEnd));
        }

        //Freshly starts a chain run: force a rebind on the first seek, pause the scene's own
        //advance (the chain drives frames), and reset the cloth once so the take is reproducible.
        void BeginChain()
        {
            _chainIndex = -1;
            PlaybackSetPaused(true);
            PlaybackResetHair();
        }

        void StartAnimChainPreview()
        {
            if (_animChain.Count == 0 || ActiveScene == null)
                return;
            BeginChain();
            _chainCursor = 0f;
            _chainActive = true;
        }

        void StopAnimChain() => _chainActive = false;

        //Interactive preview advance; called from OnRenderFrame while a preview is active.
        void UpdateAnimChain(float dt)
        {
            if (_animChain.Count == 0)
            {
                _chainActive = false;
                return;
            }
            float total = ChainTotalFrames();
            _chainCursor += dt * 60f * PlaybackSpeed;
            if (_chainCursor >= total)
            {
                if (_chainLoop)
                    _chainCursor %= Math.Max(total, 1f);
                else
                {
                    _chainActive = false;
                    ChainSeek(Math.Max(total - 1, 0));
                    PlaybackUpdate(dt);
                    return;
                }
            }
            ChainSeek(_chainCursor);
            PlaybackUpdate(dt);
        }

        //--- Sidebar UI (Sequence mode) --------------------------------------------------------

        void DrawModeTabs()
        {
            Widgets.SectionHeader("Animation source");
            if (ImGui.RadioButton("Single", _animMode == 0)) _animMode = 0;
            ImGui.SameLine();
            if (ImGui.RadioButton("Sequence", _animMode == 1)) _animMode = 1;
        }

        void DrawSequencePanel()
        {
            DrawChainTimeline();

            string cur = PlaybackCurrentAnim;
            Widgets.DisabledButton("+ Add current", !string.IsNullOrEmpty(cur), () =>
            {
                _animChain.Add(cur);
                _chainSelected = _animChain.Count - 1;
            });

            bool hasSel = _chainSelected >= 0 && _chainSelected < _animChain.Count;
            if (!hasSel) ImGui.PushStyleVar(ImGuiStyleVar.Alpha, 0.45f);
            if (ImGui.Button("Move <") && hasSel && _chainSelected > 0)
            {
                (_animChain[_chainSelected - 1], _animChain[_chainSelected]) =
                    (_animChain[_chainSelected], _animChain[_chainSelected - 1]);
                _chainSelected--;
            }
            ImGui.SameLine();
            if (ImGui.Button("Move >") && hasSel && _chainSelected < _animChain.Count - 1)
            {
                (_animChain[_chainSelected + 1], _animChain[_chainSelected]) =
                    (_animChain[_chainSelected], _animChain[_chainSelected + 1]);
                _chainSelected++;
            }
            ImGui.SameLine();
            if (ImGui.Button("Remove") && hasSel)
            {
                _animChain.RemoveAt(_chainSelected);
                if (_chainSelected >= _animChain.Count) _chainSelected = _animChain.Count - 1;
                if (_chainIndex >= _animChain.Count) _chainIndex = _animChain.Count - 1;
            }
            if (!hasSel) ImGui.PopStyleVar();
            ImGui.SameLine();
            if (ImGui.Button("Clear")) { _animChain.Clear(); _chainSelected = -1; StopAnimChain(); }

            ImGui.Checkbox("Loop", ref _chainLoop);
            ImGui.SameLine();
            if (_chainActive)
                RedButton("Stop preview", StopAnimChain);
            else
                Widgets.DisabledButton("Preview", _animChain.Count > 0, StartAnimChainPreview);
        }

        //Proportional timeline: segments sized by each step's length (drawn on the window draw
        //list), labels overlaid as ImGui text (this ImGui.NET build's draw list has no AddText),
        //and a live playhead while previewing or exporting. Click a segment to select it.
        void DrawChainTimeline()
        {
            const float height = 46f;
            var origin = ImGui.GetCursorScreenPos();
            float width = ImGui.GetContentRegionAvail().X;
            ImGui.InvisibleButton("##chaintimeline", new Vector2(width, height));
            bool clicked = ImGui.IsItemClicked();
            var afterStrip = ImGui.GetCursorScreenPos();

            var draw = ImGui.GetWindowDrawList();
            draw.AddRectFilled(origin, origin + new Vector2(width, height),
                ImGui.GetColorU32(new Vector4(0.10f, 0.11f, 0.13f, 1)), 4f);

            var labelCol = new Vector4(0.92f, 0.92f, 0.95f, 1);
            void Label(float lx, string text, float maxW)
            {
                if (maxW <= 24) return;
                ImGui.SetCursorScreenPos(new Vector2(lx, origin.Y + height / 2 - 8));
                ImGui.TextColored(labelCol, FitLabel(text, maxW - 10));
            }

            if (_animChain.Count == 0)
            {
                Label(origin.X + 5, "empty; preview an animation then + Add current", width);
                ImGui.SetCursorScreenPos(afterStrip);
                return;
            }

            int[] frames = _animChain.Select(n => Math.Max(PlaybackFrameCountOf(n), 1)).ToArray();
            float total = frames.Sum();
            bool running = _chainActive || (_animExporting && _animExportChain);

            uint segA = ImGui.GetColorU32(new Vector4(0.22f, 0.24f, 0.30f, 1));
            uint segB = ImGui.GetColorU32(new Vector4(0.27f, 0.29f, 0.36f, 1));
            uint segActive = ImGui.GetColorU32(new Vector4(0.55f, 0.42f, 0.12f, 1));
            uint outline = ImGui.GetColorU32(new Vector4(1, 1, 1, 0.85f));

            float x = origin.X;
            for (int i = 0; i < _animChain.Count; i++)
            {
                float w = width * frames[i] / total;
                var a = new Vector2(x + 1, origin.Y + 2);
                var b = new Vector2(x + w - 1, origin.Y + height - 2);
                draw.AddRectFilled(a, b, running && i == _chainIndex ? segActive : (i % 2 == 0 ? segA : segB), 3f);
                if (i == _chainSelected)
                    draw.AddRect(a, b, outline, 3f);
                x += w;
            }

            float? cursor = _chainActive ? _chainCursor : (_animExporting && _animExportChain ? _animExportIndex : null);
            if (cursor.HasValue)
            {
                float px = origin.X + width * Math.Min(cursor.Value, total) / total;
                draw.AddLine(new Vector2(px, origin.Y), new Vector2(px, origin.Y + height), outline, 2f);
            }

            x = origin.X;
            for (int i = 0; i < _animChain.Count; i++)
            {
                float w = width * frames[i] / total;
                Label(x + 5, _animChain[i], w);
                x += w;
            }
            ImGui.SetCursorScreenPos(afterStrip);

            if (clicked)
            {
                float mx = ImGui.GetMousePos().X - origin.X, acc = 0;
                for (int i = 0; i < _animChain.Count; i++)
                {
                    float w = width * frames[i] / total;
                    if (mx >= acc && mx < acc + w) { _chainSelected = i; break; }
                    acc += w;
                }
            }
        }

        //Truncates a label with ".." so it fits maxW pixels (segment width).
        static string FitLabel(string s, float maxW)
        {
            if (maxW <= 0) return "";
            if (ImGui.CalcTextSize(s).X <= maxW) return s;
            while (s.Length > 1 && ImGui.CalcTextSize(s + "..").X > maxW)
                s = s[..^1];
            return s + "..";
        }
    }
}
