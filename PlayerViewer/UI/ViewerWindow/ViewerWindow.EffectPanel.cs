using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using EffectLibrary;
using ImGuiNET;
using PlayerViewer.Effects.Sim;
using PlayerViewer.Effects.Viewer;
using Vector2 = System.Numerics.Vector2;
using Vector4 = System.Numerics.Vector4;

namespace PlayerViewer.UI
{
    // Left-hand panel of the effect viewer: the file, its searchable set list, and the selected
    // set's emitters, transform and scene settings.
    public partial class ViewerWindow
    {
        string _effectSearch = "";
        int _effectKindFilter;
        List<int> _effectRows;
        string _effectRowsSearch;
        int _effectRowsKind = -1;
        int _effectRowsAnalysed = -1;
        Emitter _effectEmitter;
        bool _effectScrollToSelection;

        static readonly string[] EffectKindFilters =
        {
            "All sets",
            "Loops (any)",
            "Periodic",
            "One-shot",
            "Does not loop",
        };

        static readonly string[] VolumeTypes =
        {
            "point",
            "circle",
            "circle, divided",
            "filled circle",
            "sphere",
            "sphere, divided",
            "sphere, 64 divided",
            "filled sphere",
            "cylinder",
            "filled cylinder",
            "box",
            "filled box",
            "line",
            "line, divided",
            "rectangle",
            "primitive",
        };

        static readonly string[] BlendTypes = { "normal", "add", "subtract", "screen", "multiply" };

        void DrawEffectPanel()
        {
            _effectTransformTab = false;
            Widgets.SectionHeader("Effect Viewer");
            Widgets.ColoredText(Theme.GoldBright, _effectFile?.Name ?? "(no file)");
            if (_effectFile != null)
            {
                ImGui.PushTextWrapPos();
                Widgets.DimText(_effectFile.Path);
                ImGui.PopTextWrapPos();
            }
            if (_effectError != null)
                Widgets.ErrorText(_effectError);

            ImGui.Spacing();
            float half = (ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X) * 0.5f;
            bool idle = !ExportBusy;
            bool back = false;
            Widgets.DisabledButton("Back to player", idle, new Vector2(half, 0), () => back = true);
            if (back)
            {
                CloseEffect();
                return;
            }
            ImGui.SameLine();
            Widgets.DisabledButton("Open file...", idle, new Vector2(half, 0), BrowseEffectFile);
            DrawRomfsEffectCombo(idle);

            if (_effectFile == null)
                return;

            DrawEffectSetList();
            DrawEffectSetDetails();
        }

        void BrowseEffectFile()
        {
            string file = NativeFolderPicker.OpenFile(
                "Open Effect",
                "Effect files (*.esetb.byml.zs;*.esetb.byml)",
                "*.esetb.byml.zs;*.esetb.byml"
            );
            if (!string.IsNullOrEmpty(file))
                OpenEffect(file);
        }

        /// <summary>The game's own effect files, one click away; dimmed and inert unless idle.</summary>
        void DrawRomfsEffectCombo(bool idle)
        {
            var files = RomfsEffectFiles();
            if (files.Count == 0)
                return;
            string current = _effectFile?.Path;
            int index = files.FindIndex(f =>
                string.Equals(f, current, StringComparison.OrdinalIgnoreCase)
            );
            if (!idle)
                ImGui.PushStyleVar(ImGuiStyleVar.Alpha, 0.45f);
            ImGui.SetNextItemWidth(-1);
            if (
                Widgets.BeginCombo(
                    "##romfseffects",
                    index >= 0 ? RomfsEffectLabel(files[index]) : "Game effect files..."
                )
            )
            {
                Widgets.PopupRows(
                    "romfseffects",
                    files.Count,
                    index,
                    (row, selected) => ImGui.Selectable(RomfsEffectLabel(files[row]), selected),
                    row =>
                    {
                        if (row != index && idle)
                            OpenEffect(files[row]);
                    }
                );
                ImGui.EndCombo();
            }
            if (!idle)
                ImGui.PopStyleVar();
        }

        string RomfsEffectLabel(string path)
        {
            string name = Path.GetFileName(path).Replace(".esetb.byml.zs", "");
            bool dlc =
                _romfs?.SdodrRoot != null
                && path.StartsWith(_romfs.SdodrRoot, StringComparison.OrdinalIgnoreCase);
            return dlc ? name + "  (Side Order)" : name;
        }

        LoopInfo ListLoop(EmitterSet set) => _effectFile.Loop(set);

        /// <summary>Rows that pass the search and the loop filter, rebuilt only when either
        /// changes or more classifications arrive.</summary>
        List<int> EffectRows()
        {
            bool kindDepends = _effectKindFilter != 0;
            int analysed = kindDepends ? _effectFile.AnalysedCount : 0;
            if (
                _effectRows != null
                && _effectRowsSearch == _effectSearch
                && _effectRowsKind == _effectKindFilter
                && _effectRowsAnalysed == analysed
            )
                return _effectRows;
            _effectRowsSearch = _effectSearch;
            _effectRowsKind = _effectKindFilter;
            _effectRowsAnalysed = analysed;
            var rows = new List<int>();
            var sets = _effectFile.Sets;
            for (int i = 0; i < sets.Count; i++)
            {
                if (!Widgets.Matches(sets[i].Name, _effectSearch))
                    continue;
                if (kindDepends && !KindMatches(ListLoop(sets[i].Set)))
                    continue;
                rows.Add(i);
            }
            return _effectRows = rows;
        }

        bool KindMatches(LoopInfo loop) =>
            loop != null
            && _effectKindFilter switch
            {
                1 => loop.Kind != LoopKind.NotLoopable,
                2 => loop.Kind == LoopKind.Periodic,
                3 => loop.Kind == LoopKind.OneShot,
                4 => loop.Kind == LoopKind.NotLoopable,
                _ => true,
            };

        void DrawEffectSetList()
        {
            Widgets.SectionHeader("Emitter sets");
            float width = ImGui.GetContentRegionAvail().X;
            Widgets.SearchBox("##effectsearch", ref _effectSearch, "Search sets", width * 0.58f);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(-1);
            Widgets.Combo(
                "##effectkind",
                ref _effectKindFilter,
                EffectKindFilters,
                EffectKindFilters.Length
            );

            var rows = EffectRows();
            var sets = _effectFile.Sets;
            int analysed = _effectFile.AnalysedCount;
            string status = $"{rows.Count} of {sets.Count} sets";
            if (analysed < sets.Count)
                status += $", classifying {analysed}/{sets.Count}";
            Widgets.DimText(status);

            float rowHeight = ImGui.GetTextLineHeightWithSpacing();
            float listHeight = Math.Max(VisibleHeightBelowCursor() * 0.42f, 8 * rowHeight);
            Widgets.BeginList("##effectsets", new Vector2(0, listHeight));

            var current = EffectPlay?.Set;
            int currentRow = current == null ? -1 : rows.FindIndex(r => sets[r].Set == current);
            int move = Widgets.VirtualRows(
                "effectsets",
                rows.Count,
                currentRow,
                r => DrawEffectSetRow(sets[rows[r]], r, current),
                _effectScrollToSelection
            );
            if (move >= 0)
                SelectEffectSet(sets[rows[move]].Set, frame: true);
            if (move >= 0 || currentRow >= 0)
                _effectScrollToSelection = false;
            Widgets.EndList();
        }

        void DrawEffectSetRow(EffectSetSummary s, int row, EmitterSet current)
        {
            var min = ImGui.GetCursorScreenPos();
            float width = ImGui.GetContentRegionAvail().X;
            if (Widgets.ListRow($"##set{row}", s.Set == current))
                SelectEffectSet(s.Set, frame: true);
            bool hovered = ImGui.IsItemHovered();

            var dl = ImGui.GetWindowDrawList();
            var loop = ListLoop(s.Set);
            var (badge, color) = LoopBadge(loop);
            string right = $"{s.EmitterCount}  {badge}";
            if (s.Gaps != EffectGaps.None)
                right = "!  " + right;
            float rightWidth = ImGui.CalcTextSize(right).X;
            Widgets.PushClip(dl, min, new Vector2(min.X + width - rightWidth - 8, min.Y + 40));
            Widgets.DrawText(dl, min, ImGui.GetColorU32(ImGuiCol.Text), s.Name);
            dl.PopClipRect();
            var at = new Vector2(min.X + width - rightWidth, min.Y);
            if (s.Gaps != EffectGaps.None)
            {
                Widgets.DrawText(dl, at, ImGui.GetColorU32(Theme.Error), "!");
                at.X += ImGui.CalcTextSize("!  ").X;
            }
            Widgets.DrawText(dl, at, ImGui.GetColorU32(Theme.TextDim), $"{s.EmitterCount}");
            at.X += ImGui.CalcTextSize($"{s.EmitterCount}  ").X;
            Widgets.DrawText(dl, at, ImGui.GetColorU32(color), badge);

            if (hovered)
            {
                Widgets.BeginTooltip();
                ImGui.TextUnformatted(s.Name);
                ImGui.TextUnformatted($"{s.EmitterCount} emitters");
                ImGui.PushTextWrapPos(420);
                ImGui.TextUnformatted(LoopText(loop));
                foreach (string gap in GapTexts(s.Gaps))
                    Widgets.ColoredText(Theme.Error, gap);
                ImGui.PopTextWrapPos();
                ImGui.EndTooltip();
            }
        }

        static (string, Vector4) LoopBadge(LoopInfo loop) =>
            loop?.Kind switch
            {
                null => ("...", Theme.TextDim),
                LoopKind.Periodic => (
                    $"loop {loop.Period / 60f:0.0}s{(loop.Unsure != null ? "?" : "")}",
                    Theme.Cyan
                ),
                LoopKind.OneShot => ($"once {loop.Length / 60f:0.0}s", Theme.Success),
                _ => ("no loop", Theme.TextDim),
            };

        /// <summary>The classification in words, as the list tooltip and the details show it.</summary>
        static string LoopText(LoopInfo loop) =>
            loop?.Kind switch
            {
                null => "Loops: working it out...",
                LoopKind.OneShot => $"Loops: one-shot {Seconds(loop.Length)}",
                LoopKind.Periodic =>
                    $"Loops: periodic N = {Seconds(loop.Period)} after {Seconds(loop.Start)} warm-up"
                        + (loop.Unsure != null ? ", checked by rendering once opened" : ""),
                _ => $"Does not loop: {loop.Reason}",
            };

        /// <summary>The classification with only the first reason it does not loop.</summary>
        static string ShortLoopText(LoopInfo loop)
        {
            if (loop?.Kind != LoopKind.NotLoopable)
                return LoopText(loop);
            var reasons = loop.Reason.Split("; ");
            return reasons.Length == 1
                ? LoopText(loop)
                : $"Does not loop: {reasons[0]} (and {reasons.Length - 1} more)";
        }

        static string Seconds(int frames) => $"{frames / 60f:0.00} s";

        static IEnumerable<string> GapTexts(EffectGaps gaps)
        {
            if ((gaps & EffectGaps.CpuFallback) != 0)
                yield return "Stream out emitters run on the CPU in place of their compute programs.";
            if ((gaps & EffectGaps.CustomField) != 0)
                yield return "A custom field is not simulated.";
            if ((gaps & EffectGaps.Stripes) != 0)
                yield return "Connection and super stripes are not simulated and not drawn.";
        }

        void DrawEffectSetDetails()
        {
            var play = EffectPlay;
            var set = play?.Set;
            if (set == null)
                return;
            var summary = _effectFile.Sets.FirstOrDefault(s => s.Set == set);

            Widgets.SectionHeader("Set");
            Widgets.ColoredText(Theme.GoldBright, set.Name);
            ImGui.PushTextWrapPos();
            Widgets.DimText($"{summary?.EmitterCount ?? 0} emitters");
            Widgets.Text(ShortLoopText(play.Loop));
            if (ImGui.IsItemHovered() && play.Loop?.Kind == LoopKind.NotLoopable)
            {
                Widgets.BeginTooltip();
                ImGui.PushTextWrapPos(460);
                foreach (string reason in play.Loop.Reason.Split("; "))
                    ImGui.TextUnformatted(reason);
                ImGui.PopTextWrapPos();
                ImGui.EndTooltip();
            }
            if (summary != null)
                foreach (string gap in GapTexts(summary.Gaps))
                    Widgets.ColoredText(Theme.Error, gap);
            if (play.Error != null)
                Widgets.ErrorText("Simulation stopped: " + play.Error);
            ImGui.PopTextWrapPos();

            if (Widgets.BeginTabBar("##effecttabs"))
            {
                if (Widgets.BeginTabItem("Emitters"))
                {
                    DrawEmitterTree(set);
                    DrawEmitterInfo();
                    ImGui.EndTabItem();
                }
                if (Widgets.BeginTabItem("Transform"))
                {
                    _effectTransformTab = true;
                    DrawEffectTransform();
                    ImGui.EndTabItem();
                }
                if (Widgets.BeginTabItem("Scene"))
                {
                    DrawEffectSceneSettings();
                    ImGui.EndTabItem();
                }
                Widgets.EndTabBar();
            }
        }

        void DrawEmitterTree(EmitterSet set)
        {
            foreach (var e in set.Emitters)
                DrawEmitterNode(e);
        }

        void DrawEmitterNode(Emitter e)
        {
            ImGui.PushID(e.GetHashCode());
            bool visible = !_effect.Hidden.Contains(e);
            if (Widgets.CheckboxControl("##vis", ref visible))
            {
                if (visible)
                    _effect.Hidden.Remove(e);
                else
                    _effect.Hidden.Add(e);
            }
            Widgets.ItemTooltip("Draw this emitter");
            ImGui.SameLine();

            var flags = ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.SpanAvailWidth;
            if (e.Children.Count == 0)
                flags |= ImGuiTreeNodeFlags.Leaf | ImGuiTreeNodeFlags.NoTreePushOnOpen;
            else
                flags |= ImGuiTreeNodeFlags.DefaultOpen;
            if (_effectEmitter == e)
                flags |= ImGuiTreeNodeFlags.Selected;
            bool drawn = _effect.Draws(e) || !visible;
            if (!drawn)
                ImGui.PushStyleColor(ImGuiCol.Text, Theme.TextDim);
            bool open = ImGui.TreeNodeEx(e.Name, flags);
            if (!drawn)
                ImGui.PopStyleColor();
            if (ImGui.IsItemClicked())
                _effectEmitter = e;
            if (open && e.Children.Count > 0)
            {
                foreach (var c in e.Children)
                    DrawEmitterNode(c);
                ImGui.TreePop();
            }
            ImGui.PopID();
        }

        void DrawEmitterInfo()
        {
            var e = _effectEmitter;
            if (e == null || e.Set != EffectPlay?.Set)
            {
                Widgets.DimText("Select an emitter for its details.");
                return;
            }
            ImGui.Separator();
            Widgets.ColoredText(Theme.Gold, e.Name);
            var rs = e.RenderState;
            string calc = e.CalcType switch
            {
                EmitterCalcType.Cpu => "CPU",
                EmitterCalcType.Gpu => "GPU",
                _ => "GPU stream out (stepped on the CPU)",
            };
            uint path = e.DrawPath;
            string pass = path is 12 or 13 or 17 or 18 or 20 or 21 ? "opaque" : "translucent";
            Row("Calc", calc);
            Row(
                "Shape",
                e.VolumeType < VolumeTypes.Length
                    ? VolumeTypes[e.VolumeType]
                    : $"type {e.VolumeType}"
            );
            Row(
                "Blend",
                rs.BlendEnable
                    ? (
                        rs.BlendType < BlendTypes.Length
                            ? BlendTypes[rs.BlendType]
                            : $"type {rs.BlendType}"
                    )
                    : "off"
            );
            Row("Pass", $"{pass}, draw path {path}");
            Row("Life", e.InfiniteLife ? "infinite" : $"{e.Life} frames");
            if (!e.Visible)
                Widgets.DimText("Hidden in the file: the game does not draw it.");
            var gaps = EffectFile.GapsOf(e);
            ImGui.PushTextWrapPos();
            foreach (string gap in GapTexts(gaps))
                Widgets.ColoredText(Theme.Error, gap);
            if (_effect.Failed.TryGetValue(e, out string failure))
                Widgets.ErrorText("Draw failed: " + failure);
            ImGui.PopTextWrapPos();

            var textures = e.Set.File.Textures;
            for (int slot = 0; slot < EffectLibrary.EmitterLayout.SamplerCount; slot++)
            {
                var entry = e.ResolveTexture(slot);
                if (entry == null)
                    continue;
                var gl = _effect.Texture(e, slot);
                const float side = 36;
                if (gl is { } t && t.Target == OpenTK.Graphics.OpenGL.TextureTarget.Texture2D)
                    ImGui.Image((IntPtr)t.Id, new Vector2(side, side));
                else
                    ImGui.Dummy(new Vector2(side, side));
                ImGui.SameLine();
                ImGui.BeginGroup();
                ImGui.TextUnformatted($"{slot}: {entry.Name}");
                var tex = textures?.GetTexture(entry);
                if (tex != null)
                    Widgets.DimText(
                        $"{tex.Width}x{tex.Height} {EffectLibrary.TextureArchive.FormatName(textures.GetFileFormat(tex))}"
                    );
                ImGui.EndGroup();
            }
        }

        static void Row(string label, string value)
        {
            Widgets.DimText(label);
            ImGui.SameLine(Widgets.Column(64));
            ImGui.TextUnformatted(value);
        }

        void DrawEffectTransform()
        {
            var play = EffectPlay;
            bool changed = false;
            var t = play.Translation;
            var r = play.RotationDegrees;
            var s = play.Scale;
            ImGui.SetNextItemWidth(-60);
            Widgets.BeginFramed(3);
            changed |= ImGui.DragFloat3("Position", ref t, 0.01f);
            Widgets.EndFramed();
            ImGui.SetNextItemWidth(-60);
            Widgets.BeginFramed(3);
            changed |= ImGui.DragFloat3("Rotation", ref r, 0.5f, -360, 360, "%.1f");
            Widgets.EndFramed();
            ImGui.SetNextItemWidth(-60);
            Widgets.BeginFramed(3);
            changed |= ImGui.DragFloat3("Scale", ref s, 0.01f, 0.01f, 100f);
            Widgets.EndFramed();
            if (changed)
                SetEffectTransform(t, r, s);
            if (Widgets.Button("Reset transform", new Vector2(-1, 0)))
                SetEffectTransform(
                    System.Numerics.Vector3.Zero,
                    System.Numerics.Vector3.Zero,
                    System.Numerics.Vector3.One
                );
            Widgets.DimText("R -> cycle Transform/Rotation/Scale");
        }

        /// <summary>
        /// A playing set is moved in flight, as the game moves one; a paused one is replayed to
        /// the frame on screen, which otherwise would not show the change until it steps.
        /// </summary>
        void SetEffectTransform(
            System.Numerics.Vector3 translation,
            System.Numerics.Vector3 rotation,
            System.Numerics.Vector3 scale
        )
        {
            var play = EffectPlay;
            play.Translation = translation;
            play.RotationDegrees = rotation;
            play.Scale = scale;
            if (play.Playing)
            {
                play.ApplyMatrix();
                return;
            }
            int shown = play.DisplayFrame;
            play.Seek(0);
            play.Seek(shown);
        }

        bool _effectTransformTab;

        /// <summary>The move, rotate and scale gizmo on the set while its Transform tab is open.</summary>
        void DrawEffectGizmo(Vector2 pos, Vector2 size, Vector2 uv0, Vector2 uv1, bool hovered)
        {
            var play = EffectPlay;
            if (_effect == null || !_effectTransformTab || play?.Set == null || _animExporting)
            {
                _effectGizmo.Shown = false;
                _effectGizmo.Axis = -1;
                return;
            }
            var map = ViewMap.Of(_pipeline.Camera, pos, size, uv0, uv1);
            static OpenTK.Vector3 Tk(System.Numerics.Vector3 v) => new(v.X, v.Y, v.Z);
            static System.Numerics.Vector3 Num(OpenTK.Vector3 v) => new(v.X, v.Y, v.Z);
            var value = new GizmoSrt(Tk(play.Translation), Tk(play.RotationDegrees), Tk(play.Scale));
            DrawTransformGizmo(
                _effectGizmo,
                map,
                OpenTK.Matrix4.Identity,
                value,
                hovered,
                out var live
            );
            if (live != value)
                SetEffectTransform(Num(live.Translation), Num(live.Rotation), Num(live.Scale));
        }

        void DrawEffectSceneSettings()
        {
            var fx = _config.Effect;
            if (Widgets.Button("Frame effect", new Vector2(-1, 0)))
                FrameEffect();
            if (Widgets.Checkbox("Ground grid", fx.ShowGrid, v => fx.ShowGrid = v, _config.Save))
                _effect.ShowGrid = fx.ShowGrid;
            Widgets.ItemTooltip("Viewport only; never exported.");
            if (
                Widgets.Checkbox("Show player", fx.ShowPlayer, v => fx.ShowPlayer = v, _config.Save)
            )
                _effect.Companion = fx.ShowPlayer ? _scene : null;
            var bg = new System.Numerics.Vector3(
                fx.Background[0],
                fx.Background[1],
                fx.Background[2]
            );
            ImGui.SetNextItemWidth(-1);
            if (Widgets.ColorEdit3("##effectbg", ref bg))
            {
                fx.Background = new[] { bg.X, bg.Y, bg.Z };
                _pipeline.BackgroundColor = bg;
                _config.Save();
            }
            Widgets.ItemTooltip("Viewport background");
            DrawLightingSection();
            DrawEnvironmentRows();
            DrawTeamColorSection();
            DrawBackgroundSection();
        }
    }
}
