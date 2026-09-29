using System;
using System.Collections.Generic;
using System.Numerics;
using ImGuiNET;
using PlayerViewer.Core;
using PlayerViewer.Icons;

namespace PlayerViewer.UI
{
    /// <summary>
    /// Reusable themed widgets: searchable gear combos, section headers, labeled rows,
    /// and bound controls.
    /// </summary>
    public static partial class Widgets
    {
        static readonly Dictionary<string, string> _searches = new();

        static bool SideOrder => SideOrderControls.On;

        /// <summary>The alpha a disabled control is drawn at, times the style's.</summary>
        public const float DisabledAlpha = 0.45f;

        //Per BeginDisabled: whether it disabled, and whether it dimmed.
        static readonly Stack<(bool Flag, bool Dim)> _disabled = new();
        static int _disabledDepth;
        static float _undimmedAlpha = 1;

        /// <summary>Items drawn now take no input and are dimmed, inside a <see cref="BeginDisabled"/>.</summary>
        public static bool Disabled => _disabledDepth > 0;

        /// <summary>
        /// Items up to <see cref="EndDisabled"/> take no input and are drawn dimmed, in both looks.
        /// This build's ImGui predates its own BeginDisabled. Pair within one window.
        /// </summary>
        public static void BeginDisabled(bool disabled = true)
        {
            bool dim = disabled && _disabledDepth == 0;
            if (dim)
            {
                _undimmedAlpha = ImGui.GetStyle().Alpha;
                ImGui.PushStyleVar(ImGuiStyleVar.Alpha, _undimmedAlpha * DisabledAlpha);
            }
            if (disabled)
            {
                ImGuiInternal.PushItemFlag(ImGuiInternal.ItemFlagDisabled, 1);
                _disabledDepth++;
            }
            _disabled.Push((disabled, dim));
        }

        public static void EndDisabled()
        {
            var (flag, dim) = _disabled.Pop();
            if (flag)
            {
                ImGuiInternal.PopItemFlag();
                _disabledDepth--;
            }
            if (dim)
                ImGui.PopStyleVar();
        }

        //A tooltip over a disabled item is drawn at full strength.
        static bool PushUndimmed()
        {
            if (!Disabled)
                return false;
            ImGui.PushStyleVar(ImGuiStyleVar.Alpha, _undimmedAlpha);
            return true;
        }

        /// <summary>The last item is hovered, disabled or not, for its tooltip.</summary>
        public static bool ItemHovered() =>
            ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled);

        public static void SectionHeader(string text)
        {
            if (UiFonts.Header is { } header)
            {
                //A card's first header sits at a fixed height below its top, the others after a gap.
                if (ImGui.GetCursorPosY() > ImGui.GetStyle().WindowPadding.Y + 1)
                    ImGui.Dummy(new Vector2(0, SideOrderLayout.HeaderGap));
                else
                    ImGui.SetCursorPosY(
                        SideOrderLayout.FirstHeaderTop - SideOrderLayout.ScrollInset.Y
                    );
                ImGui.PushFont(header);
                ImGui.PushStyleColor(ImGuiCol.Text, Theme.TextMain);
                IndentLineStart();
                ImGui.TextUnformatted(text.ToUpperInvariant());
                ImGui.PopStyleColor();
                ImGui.PopFont();
                return;
            }
            ImGui.Spacing();
            TextInColour(Theme.Gold, text.ToUpperInvariant());
            ImGui.PushStyleColor(ImGuiCol.Separator, Theme.GoldDim);
            ImGui.Separator();
            ImGui.PopStyleColor();
            ImGui.Spacing();
        }

        /// <summary>
        /// In Side Order, moves text that starts a line in from the frames' edge, where the look
        /// sets its headers, labels and checkboxes.
        /// </summary>
        public static void IndentLineStart()
        {
            if (!SideOrder)
                return;
            float x = ImGui.GetCursorPosX();
            if (x <= ImGui.GetStyle().WindowPadding.X + 0.5f)
                ImGui.SetCursorPosX(x + SideOrderLayout.TextIndent);
        }

        //Where a labelled row's control starts in the classic look.
        const float ClassicLabelColumn = 92;

        /// <summary>Label + combo on one line with fixed label column.</summary>
        public static void LabeledRow(string label, Action drawControl)
        {
            if (SideOrder)
            {
                ImGui.AlignTextToFramePadding();
                IndentLineStart();
                ImGui.TextUnformatted(label);
                ImGui.SameLine(SideOrderLayout.LabelColumn);
                drawControl();
                return;
            }
            ImGui.AlignTextToFramePadding();
            TextInColour(Theme.TextDim, label);
            ImGui.SameLine(ClassicLabelColumn);
            drawControl();
        }

        //The size ImGui gives an item asked for at size, with the default from its label.
        static Vector2 ItemSize(Vector2 size, Vector2 fallback)
        {
            float availX = ImGui.GetContentRegionAvail().X;
            float x =
                size.X == 0 ? fallback.X
                : size.X < 0 ? Math.Max(4, availX + size.X)
                : size.X;
            float y = size.Y == 0 ? fallback.Y : size.Y;
            return new Vector2(x, y);
        }

        static string Visible(string label)
        {
            int hash = label.IndexOf("##", StringComparison.Ordinal);
            return hash >= 0 ? label.Substring(0, hash) : label;
        }

        /// <summary>A button, drawn as a raised pill in Side Order; <paramref name="accent"/> is the accent pill.</summary>
        public static bool Button(string label, Vector2 size = default, bool accent = false) =>
            SideOrder ? PillButton(label, size, accent, false) : ImGui.Button(label, size);

        //A Side Order button: the pill, the accent or the red one, under ImGui's button.
        static bool PillButton(string label, Vector2 size, bool accent, bool danger)
        {
            var style = ImGui.GetStyle();
            var min = ImGui.GetCursorScreenPos();
            var sz = ItemSize(size, ImGui.CalcTextSize(Visible(label)) + style.FramePadding * 2);
            uint id = ImGui.GetID(label);
            var state = SideOrderControls.Predict(id, min, min + sz, accent) with
            {
                Danger = danger,
            };
            SideOrderControls.Pill(ImGui.GetWindowDrawList(), min, min + sz, state);
            SideOrderControls.PushClearFrame();
            if (accent)
                ImGui.PushStyleColor(ImGuiCol.Text, SideOrderControls.Colours.AccentText);
            bool pressed = ImGui.Button(label, size);
            if (accent)
                ImGui.PopStyleColor();
            SideOrderControls.PopClearFrame();
            SideOrderControls.Remember(id);
            return pressed;
        }

        /// <summary>
        /// A combo whose closed frame is a raised pill with a chevron in Side Order. The preview is
        /// drawn here, clipped short of the chevron.
        /// </summary>
        public static bool BeginCombo(
            string id,
            string preview,
            ImGuiComboFlags flags = ImGuiComboFlags.None
        )
        {
            if (!SideOrder)
                return ImGui.BeginCombo(id, preview, flags);
            var dl = ImGui.GetWindowDrawList();
            var min = ImGui.GetCursorScreenPos();
            var max = min + new Vector2(ImGui.CalcItemWidth(), ImGui.GetFrameHeight());
            uint gid = ImGui.GetID(id);
            SideOrderControls.Pill(dl, min, max, SideOrderControls.PredictCombo(gid, min, max));
            SideOrderControls.PushClearFrame();
            int hidden = HideGrab();
            bool open = ImGui.BeginCombo(id, "", flags | ImGuiComboFlags.NoArrowButton);
            ImGui.PopStyleColor(hidden);
            SideOrderControls.PopClearFrame();
            if (open)
            {
                SideOrderControls.MarkOpen(gid);
                PopupDecor(ImGui.GetStyle().PopupRounding);
                DecorateScrollbar();
            }
            if (!string.IsNullOrEmpty(preview))
            {
                PushClip(dl, min, new Vector2(max.X - (max.Y - min.Y), max.Y));
                DrawText(
                    dl,
                    min + ImGui.GetStyle().FramePadding,
                    ImGui.GetColorU32(SideOrderControls.Colours.Text),
                    preview
                );
                dl.PopClipRect();
            }
            SideOrderControls.FrameChevron(dl, min, max);
            return open;
        }

        /// <summary>A Side Order pill the width of the item showing <paramref name="text"/>, not interactive.</summary>
        public static void LabelPill(string text)
        {
            var dl = ImGui.GetWindowDrawList();
            var min = ImGui.GetCursorScreenPos();
            var max = min + new Vector2(ImGui.CalcItemWidth(), ImGui.GetFrameHeight());
            ImGui.Dummy(max - min);
            SideOrderControls.Pill(dl, min, max, default);
            var pad = ImGui.GetStyle().FramePadding;
            PushClip(dl, min, new Vector2(max.X - pad.X, max.Y));
            DrawText(dl, min + pad, ImGui.GetColorU32(SideOrderControls.Colours.Text), text);
            dl.PopClipRect();
        }

        /// <summary>
        /// The Side Order animation card's round play button, as tall as two rows, with the
        /// cursor left at the top of the column beside it, whose left edge is
        /// <paramref name="columnX"/>. Returns whether it was clicked; it stays the last item, so
        /// a tooltip after it is the button's.
        /// </summary>
        public static bool RoundPlayButton(string id, bool playing, out float columnX)
        {
            var style = ImGui.GetStyle();
            float block = ImGui.GetFrameHeight() * 2 + style.ItemSpacing.Y;
            var start = ImGui.GetCursorScreenPos();
            var size = new Vector2(block + 6, block);
            bool clicked = ImGui.InvisibleButton(id, size);
            SideOrderControls.RoundButton(
                ImGui.GetWindowDrawList(),
                start,
                start + size,
                playing,
                new SideOrderControls.State(ImGui.IsItemHovered(), ImGui.IsItemActive())
            );
            columnX = start.X + size.X + style.ItemSpacing.X;
            ImGui.SetCursorScreenPos(new Vector2(columnX, start.Y));
            return clicked;
        }

        /// <summary>ImGui.Combo over a string array, drawn through <see cref="BeginCombo"/>.</summary>
        public static bool ComboIndex(string id, ref int current, string[] items)
        {
            if (!SideOrder)
                return ImGui.Combo(id, ref current, items, items.Length);
            bool changed = false;
            if (!BeginCombo(id, current >= 0 && current < items.Length ? items[current] : ""))
                return false;
            for (int i = 0; i < items.Length; i++)
            {
                ImGui.PushID(i);
                if (ImGui.Selectable(items[i], i == current))
                {
                    current = i;
                    changed = true;
                }
                if (i == current)
                    ImGui.SetItemDefaultFocus();
                ImGui.PopID();
            }
            ImGui.EndCombo();
            return changed;
        }

        /// <summary>A text field, a raised pill in Side Order.</summary>
        public static bool InputText(string id, ref string text, uint maxLength)
        {
            if (!SideOrder)
                return ImGui.InputText(id, ref text, maxLength);
            var min = ImGui.GetCursorScreenPos();
            var max = min + new Vector2(ImGui.CalcItemWidth(), ImGui.GetFrameHeight());
            uint gid = ImGui.GetID(id);
            var state = SideOrderControls.Predict(gid, min, max);
            SideOrderControls.Pill(
                ImGui.GetWindowDrawList(),
                min,
                max,
                state with
                {
                    Held = false,
                }
            );
            SideOrderControls.PushClearFrame();
            bool changed = ImGui.InputText(id, ref text, maxLength);
            SideOrderControls.PopClearFrame();
            SideOrderControls.Remember(gid);
            return changed;
        }

        /// <summary>
        /// A text box for a path: a path too long for the box is drawn smaller until it fits with a
        /// little room to spare, the box keeping its height.
        /// </summary>
        public static bool PathInput(string id, ref string text, uint maxLength)
        {
            const float Spare = 10,
                Smallest = 0.55f;
            float room = ImGui.CalcItemWidth() - 2 * ImGui.GetStyle().FramePadding.X - Spare;
            float wide = ImGui.CalcTextSize(text).X;
            if (wide <= room || room <= 0)
                return InputText(id, ref text, maxLength);
            float height = ImGui.GetFrameHeight();
            ImGui.SetWindowFontScale(Math.Max(Smallest, room / wide));
            var pad = ImGui.GetStyle().FramePadding;
            ImGui.PushStyleVar(
                ImGuiStyleVar.FramePadding,
                new Vector2(pad.X, (height - ImGui.GetFontSize()) / 2)
            );
            bool changed = InputText(id, ref text, maxLength);
            ImGui.PopStyleVar();
            ImGui.SetWindowFontScale(1);
            return changed;
        }

        /// <summary>A radio button, a raised circle or the plain accent circle in Side Order.</summary>
        public static bool RadioButton(string label, bool active)
        {
            if (!SideOrder)
                return ImGui.RadioButton(label, active);
            IndentLineStart();
            PushClearMarks();
            bool pressed = ImGui.RadioButton(label, active);
            PopClearMarks();
            float r = ImGui.GetFrameHeight() / 2;
            var min = ImGui.GetItemRectMin();
            SideOrderControls.Radio(
                ImGui.GetWindowDrawList(),
                min + new Vector2(r),
                r - 1.5f,
                active,
                new SideOrderControls.State(ImGui.IsItemHovered(), ImGui.IsItemActive())
            );
            return pressed;
        }

        static void PushClearMarks()
        {
            var clear = Vector4.Zero;
            ImGui.PushStyleColor(ImGuiCol.FrameBg, clear);
            ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, clear);
            ImGui.PushStyleColor(ImGuiCol.FrameBgActive, clear);
            ImGui.PushStyleColor(ImGuiCol.CheckMark, clear);
        }

        static void PopClearMarks() => ImGui.PopStyleColor(4);

        /// <summary>
        /// A colour swatch that opens the picker, ColorEdit3 without inputs. Side Order draws it as
        /// a raised rounded square.
        /// </summary>
        public static bool ColorSwatch(string label, ref System.Numerics.Vector3 colour)
        {
            if (!SideOrder)
                return ImGui.ColorEdit3(label, ref colour, ImGuiColorEditFlags.NoInputs);
            //Drawn here rather than by ImGui's colour button, which caps its rounding well under
            //the other controls'; a click opens the same picker ColorEdit3 would.
            IndentLineStart();
            var min = ImGui.GetCursorScreenPos();
            float side = ImGui.GetFrameHeight();
            var max = min + new Vector2(side);
            const float r = 8;
            var dl = ImGui.GetWindowDrawList();
            ImGui.PushID(label);
            if (ImGui.InvisibleButton("##swatch", new Vector2(side)))
                ImGui.OpenPopup("##picker");
            bool hot = ImGui.IsItemHovered();
            SideOrderControls.Shadow(dl, min, max, r, 1.2f);
            dl.AddRectFilled(min, max, ImGui.GetColorU32(new Vector4(colour, 1)), r);
            //The shading over the flat colour: a darker bottom, and a glass edge.
            dl.AddImageRounded(
                SideOrderAssets.RampId,
                min,
                max,
                new Vector2(0, 1),
                new Vector2(1, 0),
                SideOrderControls.Col(new Vector4(0, 0, 0, 1), 0.14f),
                r,
                ImDrawCornerFlags.All
            );
            SideOrderSurface.RimLight(dl, min, max, r, Vector4.One, hot ? 0.6f : 0.4f, 0.04f);

            string text = Visible(label);
            if (text.Length > 0)
            {
                ImGui.SameLine(0, ImGui.GetStyle().ItemInnerSpacing.X);
                ImGui.AlignTextToFramePadding();
                ImGui.TextUnformatted(text);
            }

            bool changed = false;
            if (ImGui.BeginPopup("##picker"))
            {
                changed = ImGui.ColorPicker3(
                    "##pick",
                    ref colour,
                    ImGuiColorEditFlags.NoSidePreview | ImGuiColorEditFlags.NoSmallPreview
                );
                ImGui.EndPopup();
            }
            ImGui.PopID();
            return changed;
        }

        /// <summary>
        /// A slider whose value text is the label. Side Order draws the text left of a track with
        /// the accent up to the value and a round thumb; <paramref name="wellAroundLabel"/> puts the
        /// text inside the slider's pill, as in the animation card.
        /// </summary>
        public static bool SliderFloat(
            string id,
            ref float value,
            float min,
            float max,
            string format,
            bool wellAroundLabel = false,
            float labelColumn = 0
        )
        {
            if (!SideOrder)
                return ImGui.SliderFloat(id, ref value, min, max, format);
            float v = value;
            bool changed = LabeledSlider(
                id,
                min,
                max,
                false,
                format,
                x => Printf(format, x),
                () => ImGui.SliderFloat(id, ref v, min, max, format),
                () => v,
                wellAroundLabel,
                labelColumn
            );
            value = v;
            return changed;
        }

        public static bool SliderInt(
            string id,
            ref int value,
            int min,
            int max,
            string format,
            bool wellAroundLabel = false,
            float labelColumn = 0
        )
        {
            if (!SideOrder)
                return ImGui.SliderInt(id, ref value, min, max, format);
            int v = value;
            bool changed = LabeledSlider(
                id,
                min,
                max,
                true,
                format,
                x => Printf(format, x),
                () => ImGui.SliderInt(id, ref v, min, max, format),
                () => v,
                wellAroundLabel,
                labelColumn
            );
            value = v;
            return changed;
        }

        /// <summary>
        /// A slider with its value in a box at its end. Dragging the track or the box moves the
        /// value; a plain click on the box turns it into a text field (see <see cref="ValueBox"/>).
        /// <paramref name="done"/> is true on the frame an edit finishes: the track or box let go
        /// after a change, or a typed value committed.
        /// </summary>
        public static bool SliderValue(
            string id,
            ref float value,
            float min,
            float max,
            string format,
            out bool done
        )
        {
            var style = ImGui.GetStyle();
            float full = ImGui.CalcItemWidth();
            float h = ImGui.GetFrameHeight();
            float text = Math.Max(
                ImGui.CalcTextSize(Printf(format, min)).X,
                ImGui.CalcTextSize(Printf(format, max)).X
            );
            float box = Math.Max(56, text + style.FramePadding.X * 2 + (SideOrder ? 10 : 4));
            float track = Math.Max(40, full - box - style.ItemInnerSpacing.X);
            float v = value;
            ImGui.PushID(id);
            done = false;
            bool changed;
            if (SideOrder)
            {
                var sMin = ImGui.GetCursorScreenPos();
                var sMax = sMin + new Vector2(track, h);
                ImGui.SetNextItemWidth(track);
                PushClearMarks();
                ImGui.PushStyleColor(ImGuiCol.SliderGrab, Vector4.Zero);
                ImGui.PushStyleColor(ImGuiCol.SliderGrabActive, Vector4.Zero);
                changed = ImGui.SliderFloat("##track", ref v, min, max, "");
                ImGui.PopStyleColor(2);
                PopClearMarks();
                var state = new SideOrderControls.State(
                    ImGui.IsItemHovered(),
                    ImGui.IsItemActive()
                );
                float grab = Math.Min(style.GrabMinSize, track - 4);
                float usableMin = sMin.X + 2 + grab / 2;
                float usableMax = sMax.X - 2 - grab / 2;
                float t = max != min ? Math.Clamp((v - min) / (max - min), 0, 1) : 0;
                SideOrderControls.Slider(
                    ImGui.GetWindowDrawList(),
                    sMin,
                    sMax,
                    usableMin,
                    usableMax,
                    usableMin + t * (usableMax - usableMin),
                    state
                );
            }
            else
            {
                ImGui.SetNextItemWidth(track);
                //An empty format draws no text and leaves the value unrounded; the box shows it.
                changed = ImGui.SliderFloat("##track", ref v, min, max, "");
            }
            done |= ImGui.IsItemDeactivatedAfterEdit();
            ImGui.SameLine(0, style.ItemInnerSpacing.X);
            ImGui.SetNextItemWidth(box);
            changed |= ValueBox(
                "##value",
                ref v,
                (max - min) / 300,
                min,
                max,
                format,
                out bool boxDone
            );
            done |= boxDone;
            ImGui.PopID();
            value = v;
            return changed;
        }

        //The value box being typed into, its text, and whether its field has been active yet.
        static uint _typingId;
        static string _typingText = "";
        static bool _typingFocus,
            _typingSeen;
        static int _typingWait,
            _typingFrame;

        //The value box a press started on, where and at what value.
        static uint _pressId;
        static Vector2 _pressPos;
        static float _pressValue;
        static bool _pressMoved;

        /// <summary>
        /// A number box: drag it to move the value, or click it without dragging to type one.
        /// While typing, Enter or clicking elsewhere commits and Escape cancels; a typed value is
        /// clamped to the range when the range is not empty. <paramref name="done"/> is true on the
        /// frame an edit finishes.
        /// </summary>
        public static bool ValueBox(
            string id,
            ref float value,
            float speed,
            float min,
            float max,
            string format,
            out bool done
        )
        {
            done = false;
            uint key = ImGui.GetID(id);
            int frame = ImGui.GetFrameCount();
            //A box that went undrawn while typed into, its window closed, is given up.
            if (_typingId != 0 && _typingFrame < frame - 1)
                _typingId = 0;
            if (_typingId == key)
            {
                _typingFrame = frame;
                return TypeValue(id, ref value, min, max, out done);
            }

            float before = value;
            BeginFramed();
            bool changed = ImGui.DragFloat(id, ref value, speed, min, max, format);
            EndFramed();
            var io = ImGui.GetIO();
            if (ImGui.IsItemActivated())
            {
                _pressId = key;
                _pressPos = io.MousePos;
                _pressValue = before;
                _pressMoved = false;
            }
            if (
                _pressId == key
                && ImGui.IsItemActive()
                && (io.MousePos - _pressPos).Length() > io.MouseDragThreshold
            )
                _pressMoved = true;
            if (_pressId == key && ImGui.IsItemDeactivated())
            {
                _pressId = 0;
                //A press and release in place, not a drag and not ImGui's own Ctrl+click input.
                if (!_pressMoved && value == _pressValue && !io.KeyCtrl)
                {
                    _typingId = key;
                    _typingFrame = frame;
                    _typingText = Printf(format, value);
                    _typingFocus = true;
                    _typingSeen = false;
                    _typingWait = 0;
                }
            }
            done = ImGui.IsItemDeactivatedAfterEdit();
            return changed;
        }

        static bool TypeValue(string id, ref float value, float min, float max, out bool done)
        {
            done = false;
            if (_typingFocus)
            {
                ImGui.SetKeyboardFocusHere();
                _typingFocus = false;
            }
            string text = _typingText;
            //A sunken field with an accent edge, so typing reads apart from the raised box.
            var dl = ImGui.GetWindowDrawList();
            var boxMin = ImGui.GetCursorScreenPos();
            var boxMax = boxMin + new Vector2(ImGui.CalcItemWidth(), ImGui.GetFrameHeight());
            if (SideOrder)
            {
                SideOrderControls.Well(dl, boxMin, boxMax);
                SideOrderControls.PushClearFrame();
            }
            ImGui.InputText(
                id + "##typed",
                ref text,
                32,
                ImGuiInputTextFlags.AutoSelectAll | ImGuiInputTextFlags.CharsScientific
            );
            if (SideOrder)
            {
                SideOrderControls.PopClearFrame();
                dl.AddRect(
                    boxMin - Vector2.One,
                    boxMax + Vector2.One,
                    ImGui.GetColorU32(SideOrderControls.Colours.AccentTop),
                    (boxMax.Y - boxMin.Y) / 2 + 1,
                    ImDrawCornerFlags.All,
                    1.5f
                );
            }
            else
                dl.AddRect(
                    boxMin,
                    boxMax,
                    ImGui.GetColorU32(Theme.Gold),
                    ImGui.GetStyle().FrameRounding,
                    ImDrawCornerFlags.All,
                    1
                );
            _typingText = text;
            bool active = ImGui.IsItemActive();
            _typingSeen |= active;
            bool escape = ImGui.IsKeyPressed(ImGui.GetKeyIndex(ImGuiKey.Escape));
            if (_typingSeen && escape)
            {
                _typingId = 0;
                return false;
            }
            if (!_typingSeen)
            {
                //The focus request never took; give the box back.
                if (++_typingWait > 5)
                    _typingId = 0;
                return false;
            }
            if (active)
                return false;
            _typingId = 0;
            if (
                !float.TryParse(
                    text.Replace(',', '.'),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out float typed
                ) || !float.IsFinite(typed)
            )
                return false;
            if (max > min)
                typed = Math.Clamp(typed, min, max);
            done = true;
            bool changed = typed != value;
            value = typed;
            return changed;
        }

        /// <summary>Whether a value box is being typed into, so shortcuts can leave the keys alone.</summary>
        public static bool Typing => _typingId != 0 && _typingFrame >= ImGui.GetFrameCount() - 1;

        //The layout both sliders share. ImGui's slider runs underneath with every colour clear, so
        //clicking, dragging and the keyboard work as they always did.
        static bool LabeledSlider(
            string id,
            float min,
            float max,
            bool isInt,
            string format,
            Func<float, string> text,
            Func<bool> slider,
            Func<float> read,
            bool wellAroundLabel,
            float labelColumn
        )
        {
            var style = ImGui.GetStyle();
            var dl = ImGui.GetWindowDrawList();
            var pos = ImGui.GetCursorScreenPos();
            float width = ImGui.CalcItemWidth();
            float height = ImGui.GetFrameHeight();
            bool lineStart = ImGui.GetCursorPosX() <= style.WindowPadding.X + 0.5f;
            float indent = lineStart && !wellAroundLabel ? SideOrderLayout.TextIndent : 0;
            float pad = wellAroundLabel ? style.FramePadding.X * 0.7f : 0;
            float column =
                labelColumn > 0
                    ? labelColumn
                    : Math.Max(
                        Math.Max(ImGui.CalcTextSize(text(min)).X, ImGui.CalcTextSize(text(max)).X),
                        ImGui.CalcTextSize(text(read())).X
                    ) + 12;
            var wellMin = pos;
            var wellMax = pos + new Vector2(width, height);
            float sliderX = pos.X + indent + pad + column;
            var sMin = new Vector2(sliderX, pos.Y);
            var sMax = new Vector2(Math.Max(sliderX + 40, wellMax.X), wellMax.Y);

            ImGui.SetCursorScreenPos(sMin);
            ImGui.SetNextItemWidth(sMax.X - sMin.X);
            PushClearMarks();
            ImGui.PushStyleColor(ImGuiCol.SliderGrab, Vector4.Zero);
            ImGui.PushStyleColor(ImGuiCol.SliderGrabActive, Vector4.Zero);
            ImGui.PushStyleColor(ImGuiCol.Text, Vector4.Zero);
            bool changed = slider();
            ImGui.PopStyleColor(3);
            PopClearMarks();
            var state = new SideOrderControls.State(ImGui.IsItemHovered(), ImGui.IsItemActive());

            //ImGui's own grab placement, so the thumb sits where the mouse takes the value.
            float sliderSz = sMax.X - sMin.X - 4;
            float grab = style.GrabMinSize;
            if (isInt && max >= min)
                grab = Math.Max(sliderSz / (max - min + 1), style.GrabMinSize);
            grab = Math.Min(grab, sliderSz);
            float usableMin = sMin.X + 2 + grab / 2;
            float usableMax = sMax.X - 2 - grab / 2;
            float t = max != min ? Math.Clamp((read() - min) / (max - min), 0, 1) : 0;
            float thumbX = usableMin + t * (usableMax - usableMin);

            if (wellAroundLabel)
            {
                SideOrderControls.Slider(dl, wellMin, wellMax, usableMin, usableMax, thumbX, state);
                DrawText(
                    dl,
                    new Vector2(
                        pos.X + pad + style.FramePadding.X * 0.5f,
                        pos.Y + style.FramePadding.Y
                    ),
                    ImGui.GetColorU32(ImGuiCol.Text),
                    text(read())
                );
            }
            else
            {
                SideOrderControls.Slider(dl, sMin, sMax, usableMin, usableMax, thumbX, state);
                DrawText(
                    dl,
                    new Vector2(pos.X + indent, pos.Y + style.FramePadding.Y),
                    ImGui.GetColorU32(ImGuiCol.Text),
                    text(read())
                );
            }
            //ImGui laid the label out after the track but drew it in the cleared text colour.
            string label = Visible(id);
            if (label.Length > 0)
                DrawText(
                    dl,
                    new Vector2(sMax.X + style.ItemInnerSpacing.X, pos.Y + style.FramePadding.Y),
                    ImGui.GetColorU32(ImGuiCol.Text),
                    label
                );
            return changed;
        }

        /// <summary>
        /// The printf formats the sliders use: %d, %.Nf and %%, with the rest literal. Values are
        /// rounded as ImGui rounds them for display.
        /// </summary>
        public static string Printf(string format, float value)
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < format.Length; i++)
            {
                char ch = format[i];
                if (ch != '%' || i + 1 >= format.Length)
                {
                    sb.Append(ch);
                    continue;
                }
                if (format[i + 1] == '%')
                {
                    sb.Append('%');
                    i++;
                    continue;
                }
                int j = i + 1;
                int precision = 6;
                if (j < format.Length && format[j] == '.')
                {
                    int k = j + 1;
                    while (k < format.Length && char.IsDigit(format[k]))
                        k++;
                    precision = k > j + 1 ? int.Parse(format.AsSpan(j + 1, k - j - 1)) : 0;
                    j = k;
                }
                if (j >= format.Length)
                    break;
                char conv = format[j];
                sb.Append(
                    conv == 'd'
                        ? ((int)MathF.Round(value)).ToString(
                            System.Globalization.CultureInfo.InvariantCulture
                        )
                        : value.ToString(
                            "F" + precision,
                            System.Globalization.CultureInfo.InvariantCulture
                        )
                );
                i = j;
            }
            return sb.ToString();
        }

        /// <summary>ImGui.Combo over the first <paramref name="count"/> items, drawn through <see cref="BeginCombo"/>.</summary>
        public static bool Combo(string id, ref int current, string[] items, int count)
        {
            if (!SideOrder)
                return ImGui.Combo(id, ref current, items, count);
            if (count == items.Length)
                return ComboIndex(id, ref current, items);
            var shown = new string[Math.Clamp(count, 0, items.Length)];
            Array.Copy(items, shown, shown.Length);
            return ComboIndex(id, ref current, shown);
        }

        /// <summary>A small button, a raised pill a text line tall in Side Order.</summary>
        public static bool SmallButton(string label)
        {
            if (!SideOrder)
                return ImGui.SmallButton(label);
            //ImGui moves a small button down to the line's text baseline beside a framed item, so
            //the pill goes behind the button's real rect, in a channel under its text.
            uint id = ImGui.GetID(label);
            var dl = ImGui.GetWindowDrawList();
            dl.ChannelsSplit(2);
            dl.ChannelsSetCurrent(1);
            SideOrderControls.PushClearFrame();
            bool pressed = ImGui.SmallButton(label);
            SideOrderControls.PopClearFrame();
            var min = ImGui.GetItemRectMin();
            var max = ImGui.GetItemRectMax();
            bool held = ImGui.IsItemActive();
            dl.ChannelsSetCurrent(0);
            SideOrderControls.Pill(
                dl,
                min,
                max,
                new SideOrderControls.State(ImGui.IsItemHovered() || held, held)
            );
            dl.ChannelsMerge();
            return pressed;
        }

        /// <summary>A text field with flags, a raised pill in Side Order.</summary>
        public static bool InputText(
            string id,
            ref string text,
            uint maxLength,
            ImGuiInputTextFlags flags
        )
        {
            if (!SideOrder)
                return ImGui.InputText(id, ref text, maxLength, flags);
            BeginFramed();
            bool changed = ImGui.InputText(id, ref text, maxLength, flags);
            EndFramed();
            return changed;
        }

        //BeginFramed calls still waiting for their EndFramed.
        static int _framedDepth;

        /// <summary>
        /// Draws the next frame item (a drag, an input, a colour edit) over raised pills in Side
        /// Order, one per component laid out as ImGui lays them, <paramref name="trailing"/> being
        /// the room it keeps after them. Always pair with <see cref="EndFramed"/>.
        /// </summary>
        public static void BeginFramed(int components = 1, float trailing = 0)
        {
            if (!SideOrder)
                return;
            var style = ImGui.GetStyle();
            var dl = ImGui.GetWindowDrawList();
            var min = ImGui.GetCursorScreenPos();
            float full = ImGui.CalcItemWidth() - trailing;
            float h = ImGui.GetFrameHeight();
            float inner = style.ItemInnerSpacing.X;
            int n = Math.Max(components, 1);
            float one = Math.Max(1, MathF.Floor((full - inner * (n - 1)) / n));
            float last = Math.Max(1, MathF.Floor(full - (one + inner) * (n - 1)));
            float x = min.X;
            for (int i = 0; i < n; i++)
            {
                float w = i == n - 1 ? last : one;
                var a = new Vector2(x, min.Y);
                var b = new Vector2(x + w, min.Y + h);
                bool hot = SideOrderControls.Hovering(a, b);
                SideOrderControls.Pill(dl, a, b, new SideOrderControls.State(hot, false));
                x += w + inner;
            }
            SideOrderControls.PushClearFrame();
            _framedDepth++;
        }

        public static void EndFramed()
        {
            if (!SideOrder || _framedDepth == 0)
                return;
            _framedDepth--;
            SideOrderControls.PopClearFrame();
        }

        /// <summary>Runs a frame item through <see cref="BeginFramed"/>, for items with no ref parameter of the caller's.</summary>
        public static bool Framed(Func<bool> item, int components = 1)
        {
            BeginFramed(components);
            bool result = item();
            EndFramed();
            return result;
        }

        /// <summary>ColorEdit3, its channel inputs as pills and its swatch a rounded square in Side Order.</summary>
        public static bool ColorEdit3(
            string label,
            ref Vector3 colour,
            ImGuiColorEditFlags flags = ImGuiColorEditFlags.None
        )
        {
            if (!SideOrder)
                return ImGui.ColorEdit3(label, ref colour, flags);
            BeginColorEdit(3, flags);
            bool changed = ImGui.ColorEdit3(label, ref colour, flags);
            EndColorEdit(flags);
            return changed;
        }

        /// <summary>ColorEdit4, its channel inputs as pills and its swatch a rounded square in Side Order.</summary>
        public static bool ColorEdit4(
            string label,
            ref Vector4 colour,
            ImGuiColorEditFlags flags = ImGuiColorEditFlags.None
        )
        {
            if (!SideOrder)
                return ImGui.ColorEdit4(label, ref colour, flags);
            BeginColorEdit(4, flags);
            bool changed = ImGui.ColorEdit4(label, ref colour, flags);
            EndColorEdit(flags);
            return changed;
        }

        static void BeginColorEdit(int components, ImGuiColorEditFlags flags)
        {
            if ((flags & ImGuiColorEditFlags.NoInputs) == 0)
            {
                float swatch =
                    (flags & ImGuiColorEditFlags.NoSmallPreview) != 0
                        ? 0
                        : ImGui.GetFrameHeight() + ImGui.GetStyle().ItemInnerSpacing.X;
                BeginFramed((flags & ImGuiColorEditFlags.DisplayHex) != 0 ? 1 : components, swatch);
            }
            ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 8);
        }

        static void EndColorEdit(ImGuiColorEditFlags flags)
        {
            ImGui.PopStyleVar();
            if ((flags & ImGuiColorEditFlags.NoInputs) == 0)
                EndFramed();
        }

        /// <summary>A collapsing header, a raised pill across the line in Side Order.</summary>
        public static bool CollapsingHeader(
            string label,
            ImGuiTreeNodeFlags flags = ImGuiTreeNodeFlags.None
        )
        {
            if (!SideOrder)
                return ImGui.CollapsingHeader(label, flags);
            var min = ImGui.GetCursorScreenPos();
            var max = min + new Vector2(ImGui.GetContentRegionAvail().X, ImGui.GetFrameHeight());
            uint id = ImGui.GetID(label);
            SideOrderControls.Pill(
                ImGui.GetWindowDrawList(),
                min,
                max,
                SideOrderControls.Predict(id, min, max) with
                {
                    Held = false,
                }
            );
            var clear = Vector4.Zero;
            ImGui.PushStyleColor(ImGuiCol.Header, clear);
            ImGui.PushStyleColor(ImGuiCol.HeaderHovered, clear);
            ImGui.PushStyleColor(ImGuiCol.HeaderActive, clear);
            bool open = ImGui.CollapsingHeader(label, flags);
            ImGui.PopStyleColor(3);
            SideOrderControls.Remember(id);
            return open;
        }

        /// <summary>A progress bar, an accent fill in a sunken well in Side Order.</summary>
        public static void ProgressBar(float fraction, Vector2 size, string overlay)
        {
            if (!SideOrder)
            {
                ImGui.ProgressBar(fraction, size, overlay);
                return;
            }
            var c = SideOrderControls.Colours;
            var dl = ImGui.GetWindowDrawList();
            var min = ImGui.GetCursorScreenPos();
            var sz = ItemSize(size, new Vector2(ImGui.CalcItemWidth(), ImGui.GetFrameHeight()));
            var max = min + sz;
            ImGui.Dummy(sz);
            SideOrderControls.Well(dl, min, max);
            float t = Math.Clamp(fraction, 0, 1);
            const float inset = 3;
            var fMin = min + new Vector2(inset);
            float fillW = (sz.X - 2 * inset) * t;
            if (fillW >= 1)
                SideOrderControls.Gradient(
                    dl,
                    fMin,
                    new Vector2(fMin.X + fillW, max.Y - inset),
                    c.AccentTop,
                    c.AccentBottom,
                    Math.Min((sz.Y - 2 * inset) / 2, fillW / 2)
                );
            if (!string.IsNullOrEmpty(overlay))
            {
                var ts = ImGui.CalcTextSize(overlay);
                DrawText(
                    dl,
                    new Vector2(
                        MathF.Round(min.X + (sz.X - ts.X) / 2),
                        MathF.Round(min.Y + (sz.Y - ts.Y) / 2)
                    ),
                    ImGui.GetColorU32(c.Text),
                    overlay
                );
            }
        }

        //Side Order tab bars: each bar's tab rects and selection from the last frame, since the
        //pills sit under tabs ImGui lays out itself.
        sealed class TabStrip
        {
            public readonly Dictionary<string, (Vector2 Min, Vector2 Max)> Rects = new();
            public readonly HashSet<string> Seen = new();
            public string Selected;
            public string SelectedNow;
            public float PadX = TabPadMin;

            //The well goes under the tabs and their content once the content's height is known,
            //so the content is drawn on a channel above it.
            public readonly ImDrawListSplitterPtr Splitter = NewSplitter();
            public Vector2 WellMin;
            public float WellRight;
            public float Reach;
        }

        /// <summary>
        /// How far a tab bar's well reaches past the bar and the content on the sides and below,
        /// into the padding of the window holding it.
        /// </summary>
        public const float TabWellPad = 4;
        const float TabWellRounding = 16;

        static readonly Dictionary<uint, TabStrip> _tabStrips = new();
        static readonly Stack<TabStrip> _openStrips = new();

        const int TabColours = 5;

        //A tab's side padding: as roomy as the bar allows, sized from last frame's tabs, and
        //never so wide that a card's four tabs get shortened.
        const float TabPadMin = 7,
            TabPadMax = 12;

        //The gap between tabs; ImGui lays the bar out inside the first tab's call.
        const float TabGap = 2;

        static Vector2 TabPadding(TabStrip strip) =>
            new(strip.PadX, ImGui.GetStyle().FramePadding.Y);

        static float FitTabPadding(TabStrip strip, float width)
        {
            int n = strip.Rects.Count;
            if (n == 0)
                return TabPadMin;
            float text = 0;
            foreach (var label in strip.Rects.Keys)
                text += ImGui.CalcTextSize(Visible(label)).X;
            float spare = width - text - (n - 1) * TabGap - 2;
            return Math.Clamp(MathF.Floor(spare / (2 * n)), TabPadMin, TabPadMax);
        }

        /// <summary>
        /// A tab bar. Side Order draws it as pills in a sunken well, the selected tab in the accent,
        /// and the well carries on down under the open tab's content. Pair a true return with <see cref="EndTabBar"/>, and draw its tabs with
        /// <see cref="BeginTabItem"/> or <see cref="TabItem"/>.
        /// </summary>
        public static bool BeginTabBar(string id, ImGuiTabBarFlags flags = ImGuiTabBarFlags.None)
        {
            if (!SideOrder)
                return ImGui.BeginTabBar(id, flags);
            var dl = ImGui.GetWindowDrawList();
            var min = ImGui.GetCursorScreenPos();
            var max = min + new Vector2(ImGui.GetContentRegionAvail().X, ImGui.GetFrameHeight());
            var strip = BeginStrip(id, dl, min, max.X - min.X);
            //A bar inside another's content stays within it rather than meeting its edges, and
            //moves its tabs in instead.
            float shift = TabWellPad - strip.Reach;
            strip.PadX = FitTabPadding(strip, max.X - min.X - 2 * shift);
            var inset = new Vector2(0, TabPillInset);
            foreach (var (label, (a, b)) in strip.Rects)
                TabPill(
                    dl,
                    a + inset,
                    b - inset,
                    label == strip.Selected,
                    SideOrderControls.Hovering(a, b)
                );
            var clear = Vector4.Zero;
            ImGui.PushStyleColor(ImGuiCol.Tab, clear);
            ImGui.PushStyleColor(ImGuiCol.TabHovered, clear);
            ImGui.PushStyleColor(ImGuiCol.TabActive, clear);
            ImGui.PushStyleColor(ImGuiCol.TabUnfocused, clear);
            ImGui.PushStyleColor(ImGuiCol.TabUnfocusedActive, clear);
            //The bar keeps the padding it began with and draws its labels inside it.
            ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, TabPadding(strip));
            if (shift > 0)
                ImGui.SetCursorScreenPos(min + new Vector2(shift, 0));
            bool open = ImGui.BeginTabBar(id, flags);
            ImGui.PopStyleVar();
            if (!open)
            {
                ImGui.PopStyleColor(TabColours);
                DrawTabWell(dl, strip, max.Y);
                return false;
            }
            strip.Seen.Clear();
            strip.SelectedNow = null;
            _openStrips.Push(strip);
            return true;
        }

        //A strip's well, begun under tabs laid out from origin across width: reaching into the
        //holding window's padding unless nested in another strip, and drawn later on channel 0.
        static TabStrip BeginStrip(string id, ImDrawListPtr dl, Vector2 origin, float width)
        {
            uint key = ImGui.GetID(id);
            if (!_tabStrips.TryGetValue(key, out var strip))
                _tabStrips[key] = strip = new TabStrip();
            strip.Reach = _openStrips.Count > 0 ? 0 : TabWellPad;
            strip.WellMin = new Vector2(origin.X - strip.Reach, origin.Y);
            strip.WellRight = origin.X + width + strip.Reach;
            strip.Splitter.Split(dl, 2);
            strip.Splitter.SetCurrentChannel(dl, 1);
            return strip;
        }

        //The selected tab is the accent pill, a hovered one the plain pill, the rest bare.
        static void TabPill(ImDrawListPtr dl, Vector2 min, Vector2 max, bool on, bool hot)
        {
            if (on)
                SideOrderControls.Pill(
                    dl,
                    min,
                    max,
                    new SideOrderControls.State(hot, false, Accent: true)
                );
            else if (hot)
                SideOrderControls.Pill(dl, min, max, new SideOrderControls.State(false, false));
        }

        public static void EndTabBar()
        {
            ImGui.EndTabBar();
            if (!SideOrder || _openStrips.Count == 0)
                return;
            var strip = _openStrips.Pop();
            ImGui.PopStyleColor(TabColours);
            //ImGui leaves the cursor a line below the content.
            float spacing = ImGui.GetStyle().ItemSpacing.Y;
            DrawTabWell(ImGui.GetWindowDrawList(), strip, ImGui.GetCursorScreenPos().Y - spacing);
            if (strip.SelectedNow != null)
                strip.Selected = strip.SelectedNow;
            var gone = new List<string>();
            foreach (var label in strip.Rects.Keys)
                if (!strip.Seen.Contains(label))
                    gone.Add(label);
            foreach (var label in gone)
                strip.Rects.Remove(label);
        }

        static unsafe ImDrawListSplitterPtr NewSplitter() =>
            new(ImGuiNative.ImDrawListSplitter_ImDrawListSplitter());

        static void DrawTabWell(
            ImDrawListPtr dl,
            TabStrip strip,
            float contentBottom,
            bool shaded = false
        )
        {
            strip.Splitter.SetCurrentChannel(dl, 0);
            var min = strip.WellMin;
            var max = new Vector2(
                strip.WellRight,
                Math.Max(contentBottom, min.Y + ImGui.GetFrameHeight()) + strip.Reach
            );
            SideOrderControls.Well(dl, min, max, TabWellRounding);
            if (shaded)
            {
                //Sunk all round: the list's shade under the top edge, mirrored at the foot with
                //its dark edge, and a fainter one inside the sides.
                var shadow = SideOrderControls.Colours.Shadow;
                ListShade(dl, min, max, TabWellRounding);
                ListShade(dl, min, max, TabWellRounding, fromBottom: true);
                for (int i = 1; i <= 4; i++)
                    dl.AddRect(
                        min + new Vector2(i),
                        max - new Vector2(i),
                        ImGui.GetColorU32(shadow * new Vector4(1, 1, 1, 0.1f * (5 - i) / 4)),
                        TabWellRounding - i,
                        ImDrawCornerFlags.All,
                        1
                    );
                PushClip(dl, new Vector2(min.X - 1, max.Y - 12), max + Vector2.One);
                dl.AddRect(
                    min,
                    max,
                    ImGui.GetColorU32(shadow * new Vector4(1, 1, 1, 0.45f)),
                    TabWellRounding,
                    ImDrawCornerFlags.All,
                    1
                );
                dl.PopClipRect();
            }
            strip.Splitter.Merge(dl);
        }

        /// <summary>
        /// A section selector in rows of equal width tabs, for more sections than one tab bar
        /// fits: <paramref name="rows"/> holds how many tabs each row takes. Classic draws the tab
        /// colours with a line under the last row; Side Order draws pills in a sunken well that
        /// carries on under the content until <see cref="EndSectionTabs"/>. Returns the selected
        /// index, changed by a click. Always pair with <see cref="EndSectionTabs"/>.
        /// </summary>
        public static int BeginSectionTabs(
            string id,
            IReadOnlyList<string> labels,
            int[] rows,
            int selected
        )
        {
            var style = ImGui.GetStyle();
            var dl = ImGui.GetWindowDrawList();
            var start = ImGui.GetCursorScreenPos();
            var origin = start;
            float width = ImGui.GetContentRegionAvail().X;
            float h = ImGui.GetFrameHeight();
            //Side Order pills sit 3 px inside rows that touch, so the well reads as one bed.
            float pitch = SideOrder ? h : h + 3;
            float gap = SideOrder ? TabGap : 3;
            TabStrip strip = null;
            if (SideOrder)
            {
                strip = BeginStrip(id, dl, origin, width);
                //The pills keep the same gap to the well's edges as between their rows.
                float edge = 2 * TabPillInset;
                float side = Math.Max(0, edge - strip.Reach);
                origin += new Vector2(side, TabPillInset);
                width -= 2 * side;
            }
            ImGui.PushID(id);
            int index = 0;
            int picked = selected;
            for (int r = 0; r < rows.Length; r++)
            {
                int n = rows[r];
                float w = MathF.Floor((width - gap * (n - 1)) / n);
                for (int c = 0; c < n && index < labels.Count; c++, index++)
                {
                    float x = origin.X + c * (w + gap);
                    //The last tab takes what rounding left, so each row ends flush.
                    float right = c == n - 1 ? origin.X + width : x + w;
                    var min = new Vector2(x, origin.Y + r * pitch);
                    var max = new Vector2(right, min.Y + h);
                    ImGui.SetCursorScreenPos(min);
                    if (ImGui.InvisibleButton(labels[index], max - min))
                        picked = index;
                    bool hot = ImGui.IsItemHovered();
                    bool on = index == selected;
                    uint textColour;
                    if (SideOrder)
                    {
                        var inset = new Vector2(0, TabPillInset);
                        TabPill(dl, min + inset, max - inset, on, hot);
                        textColour = ImGui.GetColorU32(
                            on
                                ? SideOrderControls.Colours.AccentText
                                : SideOrderControls.Colours.Text
                        );
                    }
                    else
                    {
                        var fill =
                            on ? ImGuiCol.TabActive
                            : hot ? ImGuiCol.TabHovered
                            : ImGuiCol.Tab;
                        dl.AddRectFilled(min, max, ImGui.GetColorU32(fill), style.TabRounding);
                        textColour = ImGui.GetColorU32(ImGuiCol.Text);
                    }
                    string text = Visible(labels[index]);
                    var size = ImGui.CalcTextSize(text);
                    DrawText(
                        dl,
                        new Vector2(
                            MathF.Round((min.X + max.X - size.X) / 2),
                            MathF.Round(min.Y + (h - size.Y) / 2)
                        ),
                        textColour,
                        text
                    );
                }
            }
            ImGui.PopID();
            float bottom =
                origin.Y + (rows.Length - 1) * pitch + h + (SideOrder ? TabPillInset : 0);
            if (!SideOrder)
            {
                dl.AddLine(
                    new Vector2(origin.X, bottom + 1),
                    new Vector2(origin.X + width, bottom + 1),
                    ImGui.GetColorU32(ImGuiCol.TabActive),
                    1
                );
                bottom += 2;
            }
            ImGui.SetCursorScreenPos(new Vector2(start.X, bottom + style.ItemSpacing.Y));
            _openSections.Push(strip);
            return picked;
        }

        static readonly Stack<TabStrip> _openSections = new();

        //How far a tab's pill sits inside its row, and the section well's edge inside the first row.
        const float TabPillInset = 3;

        public static void EndSectionTabs()
        {
            if (_openSections.Count == 0)
                return;
            var strip = _openSections.Pop();
            if (strip == null)
                return;
            DrawTabWell(
                ImGui.GetWindowDrawList(),
                strip,
                ImGui.GetCursorScreenPos().Y - ImGui.GetStyle().ItemSpacing.Y,
                shaded: true
            );
        }

        /// <summary>A tab of a <see cref="BeginTabBar"/> bar.</summary>
        public static bool BeginTabItem(string label)
        {
            if (!SideOrder || _openStrips.Count == 0)
                return ImGui.BeginTabItem(label);
            return StripTab(label, () => ImGui.BeginTabItem(label));
        }

        static bool StripTab(string label, Func<bool> tab)
        {
            var strip = _openStrips.Peek();
            bool selected = label == strip.Selected;
            if (selected)
                ImGui.PushStyleColor(ImGuiCol.Text, SideOrderControls.Colours.AccentText);
            ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, TabPadding(strip));
            ImGui.PushStyleVar(
                ImGuiStyleVar.ItemInnerSpacing,
                new Vector2(TabGap, ImGui.GetStyle().ItemInnerSpacing.Y)
            );
            bool open = tab();
            ImGui.PopStyleVar(2);
            if (selected)
                ImGui.PopStyleColor();
            strip.Rects[label] = (ImGui.GetItemRectMin(), ImGui.GetItemRectMax());
            strip.Seen.Add(label);
            if (open)
                strip.SelectedNow = label;
            return open;
        }

        /// <summary>
        /// Searchable combo for a gear list. Returns true when the selection changed
        /// (selected receives the new entry, null = none). With icons, each row carries its
        /// icon and so does the closed combo.
        /// </summary>
        public static bool GearCombo(
            string label,
            List<GearEntry> entries,
            GearEntry current,
            out GearEntry selected,
            bool allowNone = true,
            string noneLabel = "Blank",
            IconCache icons = null,
            Func<GearEntry, string> iconKey = null
        )
        {
            var rows = GearRows(entries);
            return FilterCombo(
                "##" + label,
                label,
                "gear" + label,
                current?.DisplayName ?? noneLabel,
                rows.Ordered,
                rows.Label,
                MatchesGear,
                entry => entry.IsCustom,
                GearTooltip,
                _ => true,
                current,
                allowNone,
                noneLabel,
                300,
                false,
                out selected,
                icons,
                icons != null ? iconKey ?? (e => IconSource.KeyFor(e)) : null
            );
        }

        static readonly System.Runtime.CompilerServices.ConditionalWeakTable<
            List<GearEntry>,
            GearComboRows
        > _gearRows = new();

        static GearComboRows GearRows(List<GearEntry> entries)
        {
            if (!_gearRows.TryGetValue(entries, out var rows) || rows.Count != entries.Count)
            {
                rows = new GearComboRows(entries);
                _gearRows.AddOrUpdate(entries, rows);
            }
            return rows;
        }

        /// <summary>
        /// A gear list in the combo's order, custom entries first, and each row's label, kept
        /// per list. A label is rebuilt when its entry's name changes with the language.
        /// </summary>
        sealed class GearComboRows
        {
            public readonly List<GearEntry> Ordered;
            public readonly Func<GearEntry, int, string> Label;
            public readonly int Count;

            //Each row's index in the source list, which the label's ID carries.
            readonly int[] _source;
            readonly string[] _labels;
            readonly string[] _names;

            public GearComboRows(List<GearEntry> entries)
            {
                Count = entries.Count;
                var first = new Dictionary<GearEntry, int>(Count);
                for (int i = 0; i < Count; i++)
                    first.TryAdd(entries[i], i);
                Ordered = new List<GearEntry>(Count);
                foreach (var group in new[] { true, false })
                foreach (var entry in entries)
                    if (entry.IsCustom == group)
                        Ordered.Add(entry);
                _source = new int[Count];
                for (int i = 0; i < Count; i++)
                    _source[i] = first[Ordered[i]];
                _labels = new string[Count];
                _names = new string[Count];
                Label = LabelAt;
            }

            string LabelAt(GearEntry entry, int i)
            {
                if (_labels[i] == null || !ReferenceEquals(_names[i], entry.LocalizedName))
                {
                    _names[i] = entry.LocalizedName;
                    _labels[i] = $"{entry.DisplayName}##{_source[i]}";
                }
                return _labels[i];
            }
        }

        /// <summary>A search hit on the shown name, the codename or the internal label.</summary>
        public static bool MatchesGear(GearEntry entry, string search) =>
            string.IsNullOrEmpty(search)
            || Matches(entry.DisplayName, search)
            || Matches(entry.RowId, search)
            || Matches(entry.Label, search);

        /// <summary>
        /// The full name in the game font and the codename under it, for a row whose name may
        /// be cut short.
        /// </summary>
        public static void GearTooltip(GearEntry entry)
        {
            BeginTooltip();
            if (entry.LocalizedName != null)
            {
                bool pushed = PushNameFont(true);
                ImGui.TextUnformatted(entry.LocalizedName);
                if (pushed)
                    ImGui.PopFont();
            }
            ImGui.TextUnformatted(
                entry.RowId + (entry.Variation > 0 ? $" (v{entry.Variation})" : "")
            );
            ImGui.EndTooltip();
        }

        /// <summary>
        /// Pushes the game font for what the gear and player pickers show: game names, codenames
        /// and their none options. Returns whether it pushed, which is the caller's cue to pop.
        /// </summary>
        public static bool PushNameFont(bool localized)
        {
            if (!localized || GameFont.Current is not { } font)
                return false;
            ImGui.PushFont(font);
            return true;
        }

        /// <summary>
        /// Full-width combo over a plain string list with a filter box, for lists too long
        /// to scroll comfortably. Returns true when the selection changed.
        /// </summary>
        public static bool StringCombo(
            string id,
            string current,
            IReadOnlyList<string> items,
            out string selected
        )
        {
            return FilterCombo(
                id,
                id,
                id,
                current ?? "",
                items,
                (item, i) => $"{item}##{i}",
                Matches,
                null,
                null,
                null,
                current,
                false,
                null,
                260,
                true,
                out selected,
                null,
                null
            );
        }

        /// <summary>
        /// The one filtered combo: a search box that takes the focus on open, a fixed height
        /// list of the rows that match, the selection scrolled into view on open and kept in
        /// view by the arrows, and a click as the only thing that closes it. An arrow step
        /// applies the pick and leaves the list up so it can be walked through and looked at.
        /// </summary>
        static bool FilterCombo<T>(
            string comboId,
            string searchKey,
            string navId,
            string preview,
            IReadOnlyList<T> items,
            Func<T, int, string> label,
            Func<T, string, bool> matches,
            Func<T, bool> gold,
            Action<T> tooltip,
            Func<T, bool> nameFont,
            T current,
            bool allowNone,
            string noneLabel,
            float listHeight,
            bool resetSearchOnOpen,
            out T selected,
            IconCache icons,
            Func<T, string> iconKey
        )
            where T : class
        {
            selected = current;
            bool clicked = false;
            bool withIcons = icons != null && iconKey != null;

            ImGui.SetNextItemWidth(-1);
            var frameMin = ImGui.GetCursorScreenPos();
            var frameSize = new Vector2(ImGui.CalcItemWidth(), ImGui.GetFrameHeight());
            var parentList = ImGui.GetWindowDrawList();
            bool currentNamed = nameFont != null && (current == null || nameFont(current));
            //A preview in the name font is drawn after the combo, so no font push spans it.
            bool drawPreview = withIcons || (currentNamed && GameFont.Current != null);
            bool open = BeginCombo(
                comboId,
                drawPreview ? "" : preview,
                ImGuiComboFlags.HeightLarge
            );
            if (drawPreview)
                IconPreview(
                    parentList,
                    frameMin,
                    frameSize,
                    icons,
                    withIcons && current != null ? iconKey(current) : null,
                    preview,
                    currentNamed
                );
            if (!open)
                return false;

            bool justOpened = ImGui.IsWindowAppearing();
            if (justOpened)
            {
                if (resetSearchOnOpen)
                    _searches[searchKey] = "";
                ImGui.SetKeyboardFocusHere();
            }
            string search = _searches.GetValueOrDefault(searchKey, "");
            ImGui.SetNextItemWidth(-1);
            if (ImGui.InputText("##search" + searchKey, ref search, 64))
                _searches[searchKey] = search;

            ImGui.Separator();
            //The rows in the order they are drawn, so an arrow step lands where the eye
            //expects it to rather than somewhere in the unfiltered list.
            var rows = new List<T>();
            if (BeginScrollChild("##list" + searchKey, new Vector2(0, listHeight)))
            {
                if (allowNone && Matches(noneLabel, search))
                {
                    rows.Add(null);
                    if (
                        IconSelectable(
                            noneLabel,
                            current == null,
                            withIcons ? icons : null,
                            null,
                            nameFont != null
                        )
                    )
                    {
                        selected = null;
                        clicked = true;
                    }
                    KeepRowVisible(navId, current == null);
                }

                for (int i = 0; i < items.Count; i++)
                {
                    var item = items[i];
                    if (!matches(item, search))
                        continue;
                    bool isGold = gold != null && gold(item);
                    if (isGold)
                        ImGui.PushStyleColor(ImGuiCol.Text, Theme.GoldBright);
                    bool isSelected = EqualityComparer<T>.Default.Equals(item, current);
                    rows.Add(item);
                    if (
                        IconSelectable(
                            label(item, i),
                            isSelected,
                            withIcons ? icons : null,
                            withIcons ? iconKey(item) : null,
                            nameFont != null && nameFont(item)
                        )
                    )
                    {
                        selected = item;
                        clicked = true;
                    }
                    KeepRowVisible(navId, isSelected);
                    if (isGold)
                        ImGui.PopStyleColor();
                    if (isSelected && justOpened)
                        ImGui.SetScrollHereY();
                    if (tooltip != null && ImGui.IsItemHovered())
                        tooltip(item);
                }
            }
            ImGui.EndChild();

            int move = PopupListNav(navId, rows.Count, rows.IndexOf(current));
            if (move >= 0)
                selected = rows[move];
            if (clicked)
                ImGui.CloseCurrentPopup();
            ImGui.EndCombo();
            return clicked || move >= 0;
        }

        /// <summary>
        /// Text on a draw list. This ImGui.NET build's draw list wrapper has no AddText, so this
        /// goes to the native call with the string as UTF-8.
        /// </summary>
        public static unsafe void DrawText(ImDrawListPtr dl, Vector2 pos, uint color, string text)
        {
            if (string.IsNullOrEmpty(text))
                return;
            int max = System.Text.Encoding.UTF8.GetMaxByteCount(text.Length);
            Span<byte> bytes = max <= 1024 ? stackalloc byte[max] : new byte[max];
            int length = System.Text.Encoding.UTF8.GetBytes(text, bytes);
            fixed (byte* p = bytes)
                ImGuiNative.ImDrawList_AddTextVec2(dl.NativePtr, pos, color, p, p + length);
        }

        /// <summary>
        /// A tab item with flags and no close button. This build's wrapper takes flags only
        /// together with an open flag, which adds the button.
        /// </summary>
        public static bool TabItem(string label, ImGuiTabItemFlags flags)
        {
            if (SideOrder && _openStrips.Count > 0)
                return StripTab(label, () => NativeTabItem(label, flags));
            return NativeTabItem(label, flags);
        }

        static unsafe bool NativeTabItem(string label, ImGuiTabItemFlags flags)
        {
            int max = System.Text.Encoding.UTF8.GetMaxByteCount(label.Length) + 1;
            Span<byte> bytes = max <= 256 ? stackalloc byte[max] : new byte[max];
            int length = System.Text.Encoding.UTF8.GetBytes(label, bytes);
            bytes[length] = 0;
            fixed (byte* p = bytes)
                return ImGuiNative.igBeginTabItem(p, null, flags) != 0;
        }

        /// <summary>
        /// Corner flags as the native ImGui reads them. It is newer than the wrapper and takes its
        /// corner bits four places up, so the wrapper's own values round every corner.
        /// </summary>
        public static ImDrawCornerFlags Corners(ImDrawCornerFlags corners) =>
            corners == ImDrawCornerFlags.None
                ? (ImDrawCornerFlags)0x100
                : (ImDrawCornerFlags)(((int)corners & 0xF) << 4);

        /// <summary>A clip rect that also stays inside the one already in force.</summary>
        public static unsafe void PushClip(ImDrawListPtr dl, Vector2 min, Vector2 max) =>
            ImGuiNative.ImDrawList_PushClipRect(dl.NativePtr, min, max, 1);

        /// <summary>
        /// A text field with greyed hint text while it is empty. This build has no
        /// InputTextWithHint.
        /// </summary>
        public static bool SearchBox(string id, ref string text, string hint, float width)
        {
            if (SideOrder)
                return SearchField(id, ref text, hint, width);
            ImGui.SetNextItemWidth(width);
            var min = ImGui.GetCursorScreenPos();
            bool changed = ImGui.InputText(id, ref text, 64);
            if (string.IsNullOrEmpty(text))
                DrawText(
                    ImGui.GetWindowDrawList(),
                    min + ImGui.GetStyle().FramePadding,
                    ImGui.GetColorU32(ImGuiCol.TextDisabled),
                    hint
                );
            return changed;
        }

        /// <summary>
        /// The Side Order search field: a raised pill with a magnifier before the text and an x
        /// that clears it. <paramref name="width"/> as for SetNextItemWidth.
        /// </summary>
        public static bool SearchField(string id, ref string text, string hint, float width)
        {
            var style = ImGui.GetStyle();
            var dl = ImGui.GetWindowDrawList();
            var min = ImGui.GetCursorScreenPos();
            float w = width > 0 ? width : Math.Max(40, ImGui.GetContentRegionAvail().X + width);
            float h = ImGui.GetFrameHeight();
            var max = min + new Vector2(w, h);
            uint gid = ImGui.GetID(id);
            var state = SideOrderControls.Predict(gid, min, max);
            SideOrderControls.Pill(dl, min, max, state with { Held = false });
            uint textColour = ImGui.GetColorU32(SideOrderControls.Colours.Text);
            SideOrderControls.Magnifier(
                dl,
                min + new Vector2(h * 0.72f, h * 0.5f),
                h * 0.3f,
                textColour
            );

            float iconRoom = h * 1.3f;
            bool clearable = !string.IsNullOrEmpty(text);
            ImGui.SetCursorScreenPos(min + new Vector2(iconRoom - style.FramePadding.X, 0));
            ImGui.SetNextItemWidth(w - iconRoom + style.FramePadding.X - h);
            SideOrderControls.PushClearFrame();
            bool changed = ImGui.InputText(id, ref text, 64);
            SideOrderControls.PopClearFrame();
            SideOrderControls.Remember(gid);
            if (!clearable && !string.IsNullOrEmpty(hint))
                DrawText(
                    dl,
                    new Vector2(min.X + iconRoom, min.Y + style.FramePadding.Y),
                    ImGui.GetColorU32(ImGuiCol.TextDisabled),
                    hint
                );

            var clearMin = new Vector2(max.X - h, min.Y);
            ImGui.SameLine(0, 0);
            ImGui.SetCursorScreenPos(clearMin);
            if (ImGui.InvisibleButton(id + "##clear", new Vector2(h, h)) && clearable)
            {
                text = "";
                changed = true;
            }
            if (clearable)
                SideOrderControls.Cross(
                    dl,
                    clearMin + new Vector2(h * 0.42f, h * 0.5f),
                    h * 0.14f,
                    ImGui.IsItemHovered() ? textColour : ImGui.GetColorU32(ImGuiCol.TextDisabled)
                );
            return changed;
        }

        /// <summary>
        /// In Side Order, draws the current window's vertical scrollbar thumb as a raised pill over
        /// the flat one ImGui drew. Call right after its Begin or BeginChild, where the scroll and
        /// its limit are the ones ImGui drew with; <paramref name="titleBar"/> for a window with one.
        /// </summary>
        public static void DecorateScrollbar(bool titleBar = false)
        {
            if (!SideOrder)
                return;
            float scrollMax = ImGui.GetScrollMaxY();
            if (scrollMax <= 0)
                return;
            var style = ImGui.GetStyle();
            var pos = ImGui.GetWindowPos();
            var size = ImGui.GetWindowSize();
            float top = pos.Y + (titleBar ? ImGui.GetFontSize() + style.FramePadding.Y * 2 : 0);
            float bottom = pos.Y + size.Y - (ImGui.GetScrollMaxX() > 0 ? style.ScrollbarSize : 0);
            var barMin = new Vector2(pos.X + size.X - style.ScrollbarSize, top);
            var barMax = new Vector2(pos.X + size.X, bottom);
            //As ImGui lays out the grab: the bar inset by up to 3 px, the grab's length in
            //proportion to the view, no shorter than the grab minimum.
            float insetX = Math.Clamp(MathF.Floor((barMax.X - barMin.X - 2) / 2), 0, 3);
            float insetY = Math.Clamp(MathF.Floor((barMax.Y - barMin.Y - 2) / 2), 0, 3);
            var trackMin = barMin + new Vector2(insetX, insetY);
            var trackMax = barMax - new Vector2(insetX, insetY);
            float track = trackMax.Y - trackMin.Y;
            if (track <= 1)
                return;
            float avail = bottom - top;
            //A minimised window's cards have a track shorter than the grab minimum.
            float grab = Math.Clamp(
                track * avail / (avail + scrollMax),
                Math.Min(style.GrabMinSize, track),
                track
            );
            float ratio = Math.Clamp(ImGui.GetScrollY() / Math.Max(1, scrollMax), 0, 1);
            float y = trackMin.Y + ratio * (track - grab);
            var min = new Vector2(trackMin.X, y);
            var max = new Vector2(trackMax.X, y + grab);
            bool hot = ImGui.IsWindowHovered() && ImGui.IsMouseHoveringRect(barMin, barMax, false);
            var io = ImGui.GetIO();
            var from = io.MouseClickedPos[0];
            bool held =
                ImGui.IsMouseDown(0)
                && from.X >= barMin.X
                && from.X < barMax.X
                && from.Y >= barMin.Y
                && from.Y < barMax.Y;
            var dl = ImGui.GetWindowDrawList();
            //The window's own clip leaves out the bar but is already cut to what a scrolled
            //parent shows, so only its height is kept.
            var clipMin = dl.GetClipRectMin();
            var clipMax = dl.GetClipRectMax();
            var visMin = new Vector2(pos.X, Math.Max(pos.Y, clipMin.Y));
            var visMax = new Vector2(pos.X + size.X, Math.Min(pos.Y + size.Y, clipMax.Y));
            if (visMax.Y <= visMin.Y)
                return;
            dl.PushClipRect(visMin, visMax);
            SideOrderControls.ScrollThumb(
                dl,
                min,
                max,
                new SideOrderControls.State(hot || held, held)
            );
            dl.PopClipRect();
        }

        /// <summary>
        /// Makes ImGui's own flat grab clear for the Begin that follows, when Side Order draws the
        /// window's thumb as a pill: ImGui's comes out a few pixels longer and would show below
        /// it, and a child's lands in its parent's draw list where the pill cannot cover it.
        /// Returns the colours to pop straight after the Begin.
        /// </summary>
        public static int HideGrab()
        {
            if (!SideOrder)
                return 0;
            ImGui.PushStyleColor(ImGuiCol.ScrollbarGrab, Vector4.Zero);
            ImGui.PushStyleColor(ImGuiCol.ScrollbarGrabHovered, Vector4.Zero);
            ImGui.PushStyleColor(ImGuiCol.ScrollbarGrabActive, Vector4.Zero);
            return 3;
        }

        /// <summary>
        /// A scrolling child whose thumb is the Side Order pill: ImGui's own grab cleared around the
        /// Begin and the pill drawn after it. Pair with ImGui.EndChild.
        /// </summary>
        public static bool BeginScrollChild(
            string id,
            Vector2 size,
            ImGuiWindowFlags flags = ImGuiWindowFlags.None
        )
        {
            int hidden = HideGrab();
            bool open = ImGui.BeginChild(id, size, false, flags);
            ImGui.PopStyleColor(hidden);
            DecorateScrollbar();
            return open;
        }

        //The list being drawn: its rows' clip, and how many rows it has drawn.
        static Vector2 _listMin,
            _listMax;
        static int _listRow;

        const float ListRounding = 14;
        const float ListInset = 4;

        //How near a row may end to the list's edge and still count as touching it.
        const float RowEdgeSnap = 3;

        /// <summary>
        /// A bordered list child. Side Order draws it as a light container rounded at its top and
        /// bottom, with a slim scrollbar kept inside the rounding and rows through <see cref="ListRow"/>.
        /// Always pair with <see cref="EndList"/>.
        /// </summary>
        public static bool BeginList(string id, Vector2 size)
        {
            if (!SideOrder)
                return ImGui.BeginChild(id, size, true);
            var c = SideOrderControls.Colours;
            var dl = ImGui.GetWindowDrawList();
            var min = ImGui.GetCursorScreenPos();
            var sz = ItemSize(size, new Vector2(ImGui.GetContentRegionAvail().X, 100));
            if (size.Y <= 0)
                sz.Y = Math.Max(4, ImGui.GetContentRegionAvail().Y + size.Y);
            var max = min + sz;
            dl.AddRectFilled(min, max, ImGui.GetColorU32(c.ListBg), ListRounding);
            ListShade(dl, min, max, ListRounding);
            dl.AddRect(
                min + new Vector2(0.5f),
                max - new Vector2(0.5f),
                ImGui.GetColorU32(c.Shadow * new Vector4(1, 1, 1, 0.45f)),
                ListRounding,
                ImDrawCornerFlags.All,
                1
            );
            _listBottoms.Push(max.Y);
            _listMin = min + new Vector2(ListInset);
            _listMax = max - new Vector2(ListInset);
            _listRow = 0;

            ImGui.SetCursorScreenPos(min + new Vector2(0, ListInset));
            ImGui.PushStyleColor(ImGuiCol.ChildBg, Vector4.Zero);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(12, 2));
            bool visible = BeginScrollChild(
                id,
                new Vector2(sz.X - SideOrderLayout.ScrollInset.X, sz.Y - 2 * ListInset),
                ImGuiWindowFlags.AlwaysUseWindowPadding
            );
            ImGui.PopStyleVar();
            ImGui.PopStyleColor();
            ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(8, 0));
            //Frames in a row are a text line tall, so a checkbox or tree node fits the row.
            ImGui.PushStyleVar(
                ImGuiStyleVar.FramePadding,
                new Vector2(ImGui.GetStyle().FramePadding.X, 0)
            );
            var clear = Vector4.Zero;
            ImGui.PushStyleColor(ImGuiCol.Header, clear);
            ImGui.PushStyleColor(ImGuiCol.HeaderHovered, clear);
            ImGui.PushStyleColor(ImGuiCol.HeaderActive, clear);
            _listDepth++;
            return visible;
        }

        //A faint shade under the top edge that sinks a list into the card. It spans the whole
        //rect so it keeps the radius; a strip lower than the radius would clamp it.
        static void ListShade(
            ImDrawListPtr dl,
            Vector2 min,
            Vector2 max,
            float rounding,
            bool fromBottom = false
        ) =>
            dl.AddImageRounded(
                SideOrderAssets.RampId,
                min,
                max,
                fromBottom ? new Vector2(0, (max.Y - min.Y) / 10) : Vector2.Zero,
                fromBottom ? new Vector2(1, 0) : new Vector2(1, (max.Y - min.Y) / 10),
                ImGui.GetColorU32(SideOrderControls.Colours.Shadow * new Vector4(1, 1, 1, 0.35f)),
                rounding,
                ImDrawCornerFlags.All
            );

        public static void EndList()
        {
            if (!SideOrder || _listBottoms.Count == 0)
            {
                ImGui.EndChild();
                return;
            }
            ImGui.PopStyleColor(3);
            ImGui.PopStyleVar(2);
            _listDepth = Math.Max(0, _listDepth - 1);
            ImGui.EndChild();
            //The rows' child is inset in the frame, so without this the next item would start
            //inside the frame's foot.
            ImGui.SetCursorScreenPos(new Vector2(ImGui.GetCursorScreenPos().X, _listBottoms.Pop()));
            ImGui.Dummy(Vector2.Zero);
        }

        //The frame bottom of each open Side Order list.
        static readonly Stack<float> _listBottoms = new();

        //How many Side Order lists are open, so a checkbox in a row draws a size smaller.
        static int _listDepth;

        /// <summary>
        /// A row of a <see cref="BeginList"/> list. Side Order rows are plain with every other one
        /// tinted, and the selected row takes the accent across the list's full width, square.
        /// </summary>
        public static bool ListRow(string label, bool selected)
        {
            if (!SideOrder)
                return ImGui.Selectable(label, selected);
            RowFill(selected);
            if (selected)
                ImGui.PushStyleColor(ImGuiCol.Text, SideOrderControls.Colours.RowSelectedText);
            bool clicked = ImGui.Selectable(label, selected);
            if (selected)
                ImGui.PopStyleColor();
            return clicked;
        }

        /// <summary>The index the next row counts as, for a list that draws only the rows in view.</summary>
        public static void SetListRow(int index) => _listRow = index;

        /// <summary>
        /// The rows of a long list of one line rows, inside the list: drawRow for the rows in
        /// view and empty space of the right height for the rest, then the arrows. A row an arrow
        /// moves to, or the current row with scrollToCurrent, is scrolled to mid view when it is
        /// out of view. Returns the row an arrow moved to, or -1.
        /// </summary>
        public static int VirtualRows(
            string navId,
            int count,
            int current,
            Action<int> drawRow,
            bool scrollToCurrent = false
        )
        {
            //Inside a Side Order list rows have no spacing between them.
            float rowHeight = ImGui.GetTextLineHeightWithSpacing();
            float spacing = ImGui.GetStyle().ItemSpacing.Y;
            float scroll = ImGui.GetScrollY();
            float view = ImGui.GetWindowHeight();
            int first = Math.Max((int)(scroll / rowHeight) - 1, 0);
            int last = Math.Min((int)((scroll + view) / rowHeight) + 1, count - 1);
            if (first > 0)
                ImGui.Dummy(new Vector2(1, first * rowHeight - spacing));
            SetListRow(first);
            for (int r = first; r <= last; r++)
                drawRow(r);
            int after = count - 1 - last;
            if (after > 0)
                ImGui.Dummy(new Vector2(1, after * rowHeight - spacing));

            int move = ListNav(navId, count, current);
            int shown =
                move >= 0 ? move
                : scrollToCurrent ? current
                : -1;
            if (shown >= 0)
            {
                float y = shown * rowHeight;
                if (y < scroll || y + rowHeight > scroll + view)
                    ImGui.SetScrollY(y - view * 0.5f);
            }
            return move;
        }

        /// <summary>
        /// The Side Order background of a list row, for a row that starts with something other
        /// than its selectable, such as a visibility checkbox or a tree node. Call at the row's
        /// start. Classic draws nothing.
        /// </summary>
        public static void RowFill(bool selected, float height = 0)
        {
            if (!SideOrder)
                return;
            var c = SideOrderControls.Colours;
            var dl = ImGui.GetWindowDrawList();
            var pos = ImGui.GetCursorScreenPos();
            float h = height > 0 ? height : ImGui.GetTextLineHeight();
            float right =
                ImGui.GetWindowPos().X
                + ImGui.GetWindowWidth()
                - (ImGui.GetScrollMaxY() > 0 ? ImGui.GetStyle().ScrollbarSize + 2 : 0);
            var rowMin = new Vector2(_listMin.X, pos.Y);
            var rowMax = new Vector2(Math.Min(right, _listMax.X), pos.Y + h);
            if (rowMax.Y >= _listMin.Y && rowMin.Y <= _listMax.Y)
            {
                bool hot =
                    !Disabled
                    && ImGui.IsWindowHovered()
                    && ImGui.IsMouseHoveringRect(rowMin, rowMax, false);
                Vector4? fill =
                    selected ? c.RowSelected
                    : hot ? c.RowHover
                    : _listRow % 2 == 1 ? c.RowTint
                    : null;
                if (fill is { } f)
                {
                    //Clipped to the list by hand so a row at its top or bottom takes its rounding.
                    float y0 = Math.Max(rowMin.Y, _listMin.Y),
                        y1 = Math.Min(rowMax.Y, _listMax.Y);
                    var corners = ImDrawCornerFlags.None;
                    if (y0 <= _listMin.Y + RowEdgeSnap)
                        corners |= ImDrawCornerFlags.Top;
                    if (y1 >= _listMax.Y - RowEdgeSnap)
                        corners |= ImDrawCornerFlags.Bot;
                    if (y1 > y0)
                        dl.AddRectFilled(
                            new Vector2(rowMin.X, y0),
                            new Vector2(rowMax.X, y1),
                            ImGui.GetColorU32(f),
                            corners == ImDrawCornerFlags.None ? 0 : ListRounding - ListInset - 1,
                            Corners(corners)
                        );
                }
            }
            _listRow++;
        }

        /// <summary>Row height of a list that shows icons.</summary>
        public const float IconRowHeight = 30;

        /// <summary>
        /// A selectable row, or with icons a taller one with the icon before the text. The
        /// label keeps its ## suffix as the id either way.
        /// </summary>
        public static bool IconSelectable(
            string label,
            bool isSelected,
            IconCache icons,
            string key,
            bool localized,
            float height = IconRowHeight
        )
        {
            if (icons == null)
            {
                bool pushed = PushNameFont(localized);
                bool picked = ImGui.Selectable(label, isSelected);
                if (pushed)
                    ImGui.PopFont();
                return picked;
            }
            string text = Visible(label);
            //Not the item rect: that is widened by half the item spacing, past an unpadded list's clip.
            var min = ImGui.GetCursorScreenPos();
            bool clicked = ImGui.Selectable(
                "##row" + label,
                isSelected,
                ImGuiSelectableFlags.None,
                new Vector2(0, height)
            );
            var dl = ImGui.GetWindowDrawList();
            float side = height - 2;
            if (ImGui.IsItemVisible() && key != null && icons.TryGet(key, out var icon))
            {
                var (a, b) = IconCache.Fit(icon, min + new Vector2(1, 1), new Vector2(side, side));
                dl.AddImage(icon.Id, a, b);
            }
            float textY = min.Y + (height - ImGui.GetTextLineHeight()) * 0.5f;
            bool named = PushNameFont(localized);
            DrawText(
                dl,
                new Vector2(min.X + side + 8, textY),
                ImGui.GetColorU32(ImGuiCol.Text),
                text
            );
            if (named)
                ImGui.PopFont();
            return clicked;
        }

        /// <summary>
        /// Draws an icon and text over a combo frame drawn with an empty preview, the way the
        /// frame would have drawn the text alone. A null key draws the text alone.
        /// </summary>
        public static void IconPreview(
            ImDrawListPtr dl,
            Vector2 min,
            Vector2 size,
            IconCache icons,
            string key,
            string text,
            bool localized
        )
        {
            var style = ImGui.GetStyle();
            //Side Order keeps the icon clear of the pill's round end.
            float inset = SideOrder ? 4 : 2;
            float side = size.Y - 2 * inset;
            float iconX = min.X + (SideOrder ? style.FramePadding.X - 4 : 3);
            float x = min.X + style.FramePadding.X;
            if (key != null)
            {
                if (icons.TryGet(key, out var icon))
                {
                    var (a, b) = IconCache.Fit(
                        icon,
                        new Vector2(iconX, min.Y + inset),
                        new Vector2(side, side)
                    );
                    dl.AddImage(icon.Id, a, b);
                }
                x = SideOrder ? iconX + side + 7 : min.X + side + 7;
            }
            var clipMax = new Vector2(min.X + size.X - size.Y, min.Y + size.Y);
            PushClip(dl, min, clipMax);
            bool pushed = PushNameFont(localized);
            DrawText(
                dl,
                new Vector2(x, min.Y + style.FramePadding.Y),
                ImGui.GetColorU32(ImGuiCol.Text),
                text
            );
            if (pushed)
                ImGui.PopFont();
            dl.PopClipRect();
        }

        /// <summary>
        /// The rows of a plain combo popup with the arrow protocol done for them: drawRow draws
        /// row i and returns true when it was clicked, and pick receives the row chosen by a
        /// click or by an arrow step.
        /// </summary>
        public static void PopupRows(
            string id,
            int count,
            int current,
            Func<int, bool, bool> drawRow,
            Action<int> pick
        )
        {
            for (int i = 0; i < count; i++)
            {
                if (drawRow(i, i == current))
                    pick(i);
                KeepRowVisible(id, i == current);
            }
            int move = PopupListNav(id, count, current);
            if (move >= 0)
                pick(move);
        }

        static readonly HashSet<string> _navScroll = new(StringComparer.Ordinal);

        /// <summary>
        /// Up and down over a list of selectables. Returns the row to move to, or -1 when
        /// nothing moves. Call it inside the list's own window once its rows are drawn, with
        /// the count and the current row taken from the rows as they were drawn.
        /// </summary>
        public static int ListNav(string id, int count, int current) =>
            Navigate(id, count, current, ImGui.IsWindowFocused() && !ImGui.IsAnyItemActive());

        /// <summary>
        /// The same for a list inside a combo popup, where the focus sits on the popup or on
        /// its filter box rather than on the list, and the filter box is active the whole time
        /// the popup is up.
        /// </summary>
        public static int PopupListNav(string id, int count, int current) =>
            Navigate(
                id,
                count,
                current,
                ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows)
            );

        static int Navigate(string id, int count, int current, bool active)
        {
            _navScroll.Remove(id);
            if (!active || count == 0)
                return -1;

            int step = 0;
            if (ImGui.IsKeyPressed(ImGui.GetKeyIndex(ImGuiKey.DownArrow)))
                step++;
            if (ImGui.IsKeyPressed(ImGui.GetKeyIndex(ImGuiKey.UpArrow)))
                step--;
            if (step == 0)
                return -1;

            int next =
                current < 0 || current >= count
                    ? (step > 0 ? 0 : count - 1)
                    : Math.Clamp(current + step, 0, count - 1);
            if (next == current)
                return -1;
            _navScroll.Add(id);
            return next;
        }

        /// <summary>
        /// Follows an arrowed selection that has gone off screen.
        /// </summary>
        public static void KeepRowVisible(string id, bool isSelected)
        {
            if (isSelected && _navScroll.Contains(id) && !ImGui.IsItemVisible())
                ImGui.SetScrollHereY(0.5f);
        }

        /// <summary>Case insensitive filter test, empty filter matches everything.</summary>
        public static bool Matches(string text, string filter) =>
            string.IsNullOrEmpty(filter)
            || (text ?? "").Contains(filter, StringComparison.OrdinalIgnoreCase);

        /// <summary>Full-width button that dims and no-ops when disabled.</summary>
        public static void DisabledButton(string label, bool enabled, Action onClick) =>
            GuardedButton(label, enabled, new Vector2(-1, 0), false, onClick);

        /// <summary>At a given size, for one that shares its line with something else. The
        /// full width default runs to the edge of the content region, so anything after it on
        /// the same line is drawn outside and clipped away without a trace.</summary>
        public static void DisabledButton(
            string label,
            bool enabled,
            Vector2 size,
            Action onClick
        ) => GuardedButton(label, enabled, size, false, onClick);

        /// <summary>A full width accent pill in Side Order, a plain button otherwise; dims and no-ops when disabled.</summary>
        public static void AccentButton(string label, bool enabled, Action onClick) =>
            GuardedButton(label, enabled, new Vector2(-1, 0), true, onClick);

        static void GuardedButton(
            string label,
            bool enabled,
            Vector2 size,
            bool accent,
            Action onClick
        )
        {
            BeginDisabled(!enabled);
            bool pressed = Button(label, size, accent);
            EndDisabled();
            if (pressed)
                onClick();
        }

        /// <summary>
        /// A button that stays pressed while <paramref name="on"/>: the accent pill in Side Order,
        /// the pressed colour otherwise. A click flips it and returns true.
        /// </summary>
        public static bool ToggleButton(string label, ref bool on, Vector2 size = default)
        {
            bool pressed;
            if (SideOrder)
                pressed = Button(label, size, accent: on);
            else
            {
                var colours = ImGui.GetStyle().Colors;
                if (on)
                {
                    ImGui.PushStyleColor(ImGuiCol.Button, colours[(int)ImGuiCol.ButtonActive]);
                    ImGui.PushStyleColor(
                        ImGuiCol.ButtonHovered,
                        colours[(int)ImGuiCol.ButtonActive] * 1.15f
                    );
                }
                pressed = ImGui.Button(label, size);
                if (on)
                    ImGui.PopStyleColor(2);
            }
            if (pressed)
                on = !on;
            return pressed;
        }

        /// <summary>Full-width red (destructive/cancel) button.</summary>
        public static void RedButton(string label, Action onClick) =>
            RedButton(label, new Vector2(-1, 0), onClick);

        /// <summary>Red button at a given size, for a confirm that sits beside a cancel. A
        /// full-width one leaves no room for anything after it on the same line.</summary>
        public static void RedButton(string label, Vector2 size, Action onClick)
        {
            if (SideOrder)
            {
                if (PillButton(label, size, false, true))
                    onClick();
                return;
            }
            ImGui.PushStyleColor(ImGuiCol.Button, Theme.RedButtonBg);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Theme.RedButtonHover);
            if (ImGui.Button(label, size))
                onClick();
            ImGui.PopStyleColor(2);
        }

        /// <summary>A fixed label column, widened in Side Order for the text indent and the wider body font.</summary>
        public static float Column(float classic) =>
            SideOrder ? classic + SideOrderLayout.ColumnWiden : classic;

        /// <summary>Muted caption/status text.</summary>
        public static void DimText(string text) => ColoredText(Theme.TextDim, text);

        /// <summary>Red error/warning text.</summary>
        public static void ErrorText(string text) => ColoredText(Theme.Error, text);

        /// <summary>Green success/active text.</summary>
        public static void SuccessText(string text) => ColoredText(Theme.Success, text);

        /// <summary>
        /// Coloured text, taken as it is rather than as a format. In Side Order text that starts
        /// a line sits in from the frames' edge, its wrapped lines too.
        /// </summary>
        public static void ColoredText(Vector4 colour, string text)
        {
            bool indent = BeginTextIndent();
            TextInColour(colour, text);
            EndTextIndent(indent);
        }

        //What ImGui.TextColored draws, without reading the text as a format.
        static void TextInColour(Vector4 colour, string text)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, colour);
            ImGui.TextUnformatted(text);
            ImGui.PopStyleColor();
        }

        //The text as a format that prints it unchanged, for the calls that only take a format.
        static string Literal(string text) => text.Contains('%') ? text.Replace("%", "%%") : text;

        /// <summary>ImGui.TextUnformatted, indented at a line start in Side Order.</summary>
        public static void Text(string text)
        {
            bool indent = BeginTextIndent();
            ImGui.TextUnformatted(text);
            EndTextIndent(indent);
        }

        /// <summary>ImGui.TextWrapped with the text taken as it is, indented at a line start in Side Order.</summary>
        public static void WrappedText(string text)
        {
            bool indent = BeginTextIndent();
            ImGui.TextWrapped(Literal(text));
            EndTextIndent(indent);
        }

        static bool BeginTextIndent()
        {
            bool indent =
                SideOrder && ImGui.GetCursorPosX() <= ImGui.GetStyle().WindowPadding.X + 0.5f;
            if (indent)
                ImGui.Indent(SideOrderLayout.TextIndent);
            return indent;
        }

        static void EndTextIndent(bool indent)
        {
            if (indent)
                ImGui.Unindent(SideOrderLayout.TextIndent);
        }

        /// <summary>A tooltip taken as text, not as a format string, for names that may hold a %.</summary>
        public static void PlainTooltip(string text)
        {
            bool undimmed = PushUndimmed();
            BeginTooltip();
            ImGui.TextUnformatted(text);
            ImGui.EndTooltip();
            if (undimmed)
                ImGui.PopStyleVar();
        }

        /// <summary>BeginTooltip, with a card's shadow and rim in Side Order.</summary>
        public static void BeginTooltip()
        {
            ImGui.BeginTooltip();
            if (SideOrder)
                PopupDecor(ImGui.GetStyle().WindowRounding);
        }

        //The current popup or tooltip window drawn as a card.
        static void PopupDecor(float rounding)
        {
            var pos = ImGui.GetWindowPos();
            SideOrderSurface.DrawWindowDecor(
                ImGui.GetWindowDrawList(),
                pos,
                pos + ImGui.GetWindowSize(),
                rounding
            );
        }

        /// <summary>Tooltip shown when the last-drawn item is hovered.</summary>
        public static void ItemTooltip(string text)
        {
            if (!ItemHovered())
                return;
            if (SideOrder)
            {
                PlainTooltip(text);
                return;
            }
            bool undimmed = PushUndimmed();
            ImGui.SetTooltip(Literal(text));
            if (undimmed)
                ImGui.PopStyleVar();
        }

        //The bound controls read a value, draw the control, and on an edit pass the new value to
        //set and then run onChanged. Each returns true when the value changed.

        public static bool Checkbox(
            string label,
            bool value,
            Action<bool> set,
            Action onChanged = null
        )
        {
            bool v = value;
            if (!CheckboxControl(label, ref v))
                return false;
            set(v);
            onChanged?.Invoke();
            return true;
        }

        /// <summary>A checkbox, a raised rounded square or the accent with a check in Side Order.</summary>
        public static bool CheckboxControl(string label, ref bool value)
        {
            if (!SideOrder)
                return ImGui.Checkbox(label, ref value);
            IndentLineStart();
            PushClearMarks();
            bool changed = ImGui.Checkbox(label, ref value);
            PopClearMarks();
            float inset = _listDepth > 0 ? 2 : 0;
            SideOrderControls.CheckBox(
                ImGui.GetWindowDrawList(),
                ImGui.GetItemRectMin() + new Vector2(inset),
                ImGui.GetFrameHeight() - 2 * inset,
                value,
                new SideOrderControls.State(ImGui.IsItemHovered(), false)
            );
            return changed;
        }

        public static bool SliderInt(
            string label,
            int value,
            int min,
            int max,
            Action<int> set,
            Action onChanged = null,
            string format = "%d"
        )
        {
            int v = value;
            if (!SliderInt(label, ref v, min, max, format))
                return false;
            set(v);
            onChanged?.Invoke();
            return true;
        }

        public static bool SliderFloat(
            string label,
            float value,
            float min,
            float max,
            Action<float> set,
            Action onChanged = null,
            string format = "%.2f"
        )
        {
            float v = value;
            if (!SliderFloat(label, ref v, min, max, format))
                return false;
            set(v);
            onChanged?.Invoke();
            return true;
        }

        public static bool InputInt(
            string label,
            int value,
            Action<int> set,
            Action onChanged = null
        )
        {
            int v = value;
            BeginFramed();
            bool edited = ImGui.InputInt(label, ref v);
            EndFramed();
            if (!edited)
                return false;
            set(v);
            onChanged?.Invoke();
            return true;
        }

        public static bool Combo(
            string label,
            int value,
            string[] items,
            Action<int> set,
            Action onChanged = null
        )
        {
            int v = value;
            if (!ComboIndex(label, ref v, items))
                return false;
            set(v);
            onChanged?.Invoke();
            return true;
        }

        public static bool ColorEdit3(
            string label,
            Vector3 value,
            Action<Vector3> set,
            ImGuiColorEditFlags flags = ImGuiColorEditFlags.None,
            Action onChanged = null
        )
        {
            Vector3 v = value;
            if (!ColorEdit3(label, ref v, flags))
                return false;
            set(v);
            onChanged?.Invoke();
            return true;
        }
    }
}
