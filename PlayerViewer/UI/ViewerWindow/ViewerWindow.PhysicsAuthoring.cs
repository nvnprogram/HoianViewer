using System;
using System.Collections.Generic;
using System.Linq;
using ImGuiNET;
using PlayerViewer.HairGen;
using PlayerViewer.Physics;
using Vector2 = System.Numerics.Vector2;
using Vector4 = System.Numerics.Vector4;

namespace PlayerViewer.UI
{
    // The physics authoring windows: the main one (what the viewport shows, hair or not, the test
    // motion, the limbs and the two ways to add one) and a small window per limb holding its
    // settings. A setting applies to the running cloth when its control is let go.
    public partial class ViewerWindow
    {
        bool _authoringOpen;
        bool _focusAuthoring;
        bool _showBones = true;

        //Whether the model is authored as hair, from its file name at first: Har_ is hair.
        bool _authorHair;
        string _authorHairFor;

        /// <summary>Whether the standalone model is authored as hair: from its file name until the user sets it.</summary>
        bool AuthorHair
        {
            get
            {
                if (_authorHairFor != StandaloneActor)
                {
                    _authorHairFor = StandaloneActor;
                    _authorHair = StandaloneActor?.StartsWith("Har_") == true;
                }
                return _authorHair;
            }
        }

        /// <summary>Sets the Hair switch for the standalone model, with Keep segment length following it.</summary>
        void SetHairFlag(bool hair)
        {
            _authorHair = hair;
            _authorHairFor = StandaloneActor;
            _clothRuntime.Options.KeepSegmentLength = hair;
        }

        //What the last Done did to the model's bones, shown under the limbs until the next session.
        string _genNotice;

        readonly SideWindows<PaintedLimb> _limbWindows = new("limb", 420);

        //Where each open limb or collidable window sits, which new ones open clear of.
        readonly Dictionary<object, (Vector2 Min, Vector2 Max)> _sideWindowRects = new();
        readonly Dictionary<PaintedLimb, string> _limbPresetPick = new();

        //The authoring window's content height last frame, which its height follows, and where
        //it sat, which new limb windows open beside.
        float _authoringHeight = 640;
        Vector2 _authoringMin,
            _authoringMax;

        const string SuggestedPreset = "Suggested";
        const float SideWindowWidth = 350;

        /// <summary>
        /// Where a control's request waits until every window has drawn, since most of them
        /// rebuild the model or the cloth. <see cref="Defer"/> keeps the first request a slot gets
        /// in a frame, or with replace the latest; the slots run in this order.
        /// </summary>
        enum Deferred
        {
            //The authoring windows: the first wins, and a button replaces it, so an edit let go
            //in the same frame cannot override a press.
            Author,

            //The Skeleton section's edit: last wins.
            Skeleton,

            //Undo or redo, from a button or the keys: last wins.
            Undo,

            //The metadata modal's answer: last wins.
            Meta,

            //Copying from the other gender, from the button or the modal: last wins.
            Copy,

            //The clear modal's answer: last wins.
            Clear,
        }

        readonly Action[] _deferred = new Action[Enum.GetValues<Deferred>().Length];

        void Defer(Deferred slot, Action action, bool replace = false)
        {
            ref var pending = ref _deferred[(int)slot];
            if (replace || pending == null)
                pending = action;
        }

        void CancelDeferred(Deferred slot) => _deferred[(int)slot] = null;

        Action TakeDeferred(Deferred slot)
        {
            var action = _deferred[(int)slot];
            _deferred[(int)slot] = null;
            return action;
        }

        /// <summary>
        /// Runs what the windows asked for, after all of them have drawn. The authoring and
        /// Skeleton requests, and the undo keys after them, only while their section is open.
        /// </summary>
        void RunDeferred()
        {
            if (_standalone != null && _physicsTabActive)
            {
                var aim = TakeAimCommit();
                var author = TakeDeferred(Deferred.Author);
                try
                {
                    aim?.Invoke();
                    author?.Invoke();
                }
                finally
                {
                    RunUndoKeys();
                }
            }
            else if (_standalone != null && _skeletonTabActive)
            {
                var skeleton = TakeDeferred(Deferred.Skeleton);
                try
                {
                    skeleton?.Invoke();
                }
                catch (Exception ex)
                {
                    _skelError = ex.Message;
                    Console.WriteLine($"[Skeleton] {ex}");
                }
                finally
                {
                    RunUndoKeys();
                }
            }
            TakeDeferred(Deferred.Meta)?.Invoke();
            TakeDeferred(Deferred.Copy)?.Invoke();
            TakeDeferred(Deferred.Clear)?.Invoke();
        }

        /// <summary>
        /// The small windows open on objects of one kind, a limb's or a collidable's, in the order
        /// opened. A window stays listed while its object is gone, so an undo that brings the
        /// object back brings its window back too.
        /// </summary>
        sealed class SideWindows<T>
            where T : class
        {
            readonly List<T> _open = new();
            readonly Dictionary<T, int> _ids = new();
            int _nextId;

            public readonly string IdPrefix;
            public readonly float FirstHeight;

            //Opened since they last drew, so they are placed beside the authoring window.
            public readonly HashSet<T> Place = new();

            //Each window's content height last frame, which its height follows.
            public readonly Dictionary<T, float> Heights = new();

            //A name field's text as typed, while its window has focus.
            public readonly Dictionary<T, string> Names = new();

            public T Focus;

            public SideWindows(string idPrefix, float firstHeight)
            {
                IdPrefix = idPrefix;
                FirstHeight = firstHeight;
            }

            public IReadOnlyList<T> Open => _open;

            public bool Contains(T item) => _open.Contains(item);

            public bool Remove(T item) => _open.Remove(item);

            /// <summary>The window's id, kept for the object, so a rename keeps its place.</summary>
            public int Id(T item)
            {
                if (!_ids.TryGetValue(item, out int id))
                    _ids[item] = id = _nextId++;
                return id;
            }

            public void Show(T item)
            {
                if (!_open.Contains(item))
                {
                    _open.Add(item);
                    Place.Add(item);
                }
                Focus = item;
            }

            public void Clear()
            {
                _open.Clear();
                Place.Clear();
                _ids.Clear();
                Heights.Clear();
                Names.Clear();
                Focus = null;
            }

            /// <summary>The object's name as a text field; the new name when an edit of it finished with a change, else null.</summary>
            public string EditName(T item, string current, string id)
            {
                if (
                    !Names.TryGetValue(item, out string name)
                    || !ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows)
                )
                    name = current ?? "";
                Widgets.InputText(id, ref name, 64);
                Names[item] = name;
                return ImGui.IsItemDeactivatedAfterEdit() && name != current ? name : null;
            }
        }

        int LimbId(PaintedLimb limb) => _limbWindows.Id(limb);

        void OpenLimbWindow(PaintedLimb limb)
        {
            _limbWindows.Show(limb);
            _genSelected = _gen?.Limbs.IndexOf(limb) ?? -1;
        }

        void CloseLimbWindows()
        {
            _limbWindows.Clear();
            _sideWindowRects.Clear();
            _limbPresetPick.Clear();
            CloseColliderWindows();
        }

        void DrawPhysicsAuthoringWindows()
        {
            if (_standalone == null || !_physicsTabActive)
                return;
            _genHovered = -1;
            _colHovered = null;
            //The focused limb is the one whose window was used last, while it is open.
            if (
                _gen == null
                || _genSelected < 0
                || _genSelected >= _gen.Limbs.Count
                || !_limbWindows.Contains(_gen.Limbs[_genSelected])
            )
                _genSelected = -1;
            if (_authoringOpen || _paint != null)
                DrawAuthoringWindow();
            //A window of a limb or collidable that is gone is not drawn, and its place is freed.
            var shown = new HashSet<object>();
            if (_paint == null && _gen != null)
            {
                foreach (var limb in _limbWindows.Open.Where(_gen.Limbs.Contains).ToList())
                {
                    shown.Add(limb);
                    DrawSideWindow(
                        _limbWindows,
                        limb,
                        limb.Name,
                        () => _genHovered = _gen.Limbs.IndexOf(limb),
                        () => _genSelected = _gen.Limbs.IndexOf(limb),
                        () => DrawLimbWindowContents(limb, _gen.Limbs.IndexOf(limb))
                    );
                }
                DrawColliderWindows(shown);
            }
            foreach (var gone in _sideWindowRects.Keys.Where(k => !shown.Contains(k)).ToList())
                _sideWindowRects.Remove(gone);
        }

        void DrawAuthoringWindow()
        {
            ImGui.SetNextWindowPos(ToolWindowPos(), ImGuiCond.FirstUseEver);
            ImGui.SetNextWindowSize(new Vector2(390, _authoringHeight), ImGuiCond.FirstUseEver);
            float height = FitHeight(_authoringHeight);
            ImGui.SetNextWindowSizeConstraints(new Vector2(300, height), new Vector2(900, height));
            if (_focusAuthoring)
            {
                ImGui.SetNextWindowFocus();
                _focusAuthoring = false;
            }
            bool open = true;
            if (BeginCardWindow("Physics Maker###physicsauthoring", ref open))
            {
                Widgets.Guarded("HairGen", DrawAuthoringContents);
                _authoringHeight = ContentHeight();
                _authoringMin = ImGui.GetWindowPos();
                _authoringMax = _authoringMin + ImGui.GetWindowSize();
            }
            ImGui.End();
            //A paint session keeps the window up: its Done and Cancel are here.
            if (!open && _paint == null)
                _authoringOpen = false;
        }

        /// <summary>A window's height for its content, kept inside the main window.</summary>
        static float FitHeight(float content)
        {
            float room =
                ImGui.GetMainViewport().Size.Y - SideOrderLayout.CardsTop - 2 * SideOrderLayout.Gap;
            return Math.Clamp(content, 120, Math.Max(120, room));
        }

        /// <summary>The height the current window's content asked for, title bar and padding included.</summary>
        static float ContentHeight() =>
            MathF.Ceiling(
                ImGui.GetCursorPosY()
                    - ImGui.GetStyle().ItemSpacing.Y
                    + ImGui.GetStyle().WindowPadding.Y
            );

        void DrawAuthoringContents()
        {
            DrawUndoButtons();
            Widgets.SectionHeader("Show");
            DrawAuthorShow();
            Widgets.SectionHeader("Model");
            DrawHairToggle();
            if (_paint != null)
            {
                Widgets.SectionHeader(_paint.Bones ? "Picking bones" : "Painting");
                if (_paint.Bones)
                    DrawBonePickControls();
                else
                    DrawPaintControls();
                return;
            }
            if (_cloth != null)
                DrawTestMotion();
            Widgets.SectionHeader("Limbs");
            DrawLimbList();
            if (_genNotice != null)
            {
                ImGui.PushTextWrapPos();
                Widgets.ColoredText(Theme.Cyan, _genNotice);
                ImGui.PopTextWrapPos();
            }
            DrawAddLimbButtons();
            DrawColliderSection();
            DrawAllLimbsOptions();
        }

        /// <summary>The viewport's overlays, two to a line.</summary>
        void DrawAuthorShow()
        {
            float first = ImGui.GetCursorPosX();
            float second =
                first + (ImGui.GetContentRegionAvail().X + ImGui.GetStyle().ItemSpacing.X) / 2;
            Widgets.CheckboxControl("Tint limbs", ref _genTint);
            Widgets.ItemTooltip("Colours the model by the limb that moves it.");
            ImGui.SameLine(second);
            Widgets.CheckboxControl("Bones", ref _showBones);
            Widgets.ItemTooltip("Draws each limb's bones in its colour, as the cloth moves them.");
            ImGui.SetCursorPosX(first);
            Widgets.CheckboxControl("Particles##show", ref _clothShowParticles);
            ImGui.SameLine(second);
            Widgets.CheckboxControl("Links##show", ref _clothShowLinks);
            ImGui.SetCursorPosX(first);
            Widgets.CheckboxControl("Collidables##show", ref _clothShowShapes);
            Widgets.ItemTooltip("The capsules and spheres the cloth collides with.");
            ImGui.SameLine(second);
            Widgets.CheckboxControl("Ranges##show", ref _clothShowRanges);
            Widgets.ItemTooltip("Each particle's allowed range, as a circle.");
            ImGui.SetCursorPosX(first);
            HairOnlyCheckbox(
                "Show head",
                ref _showHead,
                "Shows a stand-in for the player's head, so hair sinking into it hides as in game.",
                "there is no head to show"
            );
            ImGui.SameLine(second);
            Widgets.CheckboxControl("Selected only", ref _clothOnlySelected);
            Widgets.ItemTooltip("Shows only the cloth piece selected in the Physics tab's data.");
        }

        /// <summary>
        /// A checkbox that only applies to hair: disabled otherwise, saying why. With
        /// <paramref name="otherwise"/> it is also disabled for another reason, given.
        /// </summary>
        bool HairOnlyCheckbox(
            string label,
            ref bool value,
            string tip,
            string why,
            string otherwise = null
        )
        {
            if (AuthorHair && otherwise == null)
            {
                bool changed = Widgets.CheckboxControl(label, ref value);
                Widgets.ItemTooltip(tip);
                return changed;
            }
            bool shown = value;
            Widgets.BeginDisabled();
            Widgets.CheckboxControl(label, ref shown);
            Widgets.EndDisabled();
            Widgets.ItemTooltip(
                otherwise
                    ?? $"Hair only: {why}. Turn Hair on in the Physics Maker window to use it."
            );
            return false;
        }

        void DrawHairToggle()
        {
            bool hair = AuthorHair;
            Widgets.BeginDisabled(_paint != null);
            if (Widgets.CheckboxControl("Hair", ref hair))
                Defer(Deferred.Author, () => SetAuthorHair(hair));
            Widgets.EndDisabled();
            Widgets.ItemTooltip(
                _paint != null
                    ? "Finish or cancel the limb first."
                    : "Har_ models start as hair. Rebuilds the limbs and cloth."
            );
            ImGui.PushTextWrapPos();
            Widgets.DimText(
                AuthorHair
                    ? "Hair: limbs start at the head, hang from Head_Root and get the head collidables."
                    : "Not hair: limbs hang from the bone under their paint, and no head collidables are added."
            );
            ImGui.PopTextWrapPos();
        }

        void DrawTestMotion()
        {
            if (!Widgets.CollapsingHeader("Test motion and simulation"))
                return;
            float label = Widgets.Column(96);
            Widgets.LabelRow("Motion", label);
            int mode = (int)_clothMotion.Mode;
            if (Widgets.Combo("##motion", ref mode, ClothMotion.Labels, ClothMotion.Labels.Length))
                _clothMotion.Mode = (ClothMotion.Kind)mode;
            Widgets.ItemTooltip("Moves the model's root so the cloth has something to react to.");
            Widgets.LabelRow("Amount", label);
            Widgets.SliderValue("##motionamount", ref _clothMotion.Amount, 0, 2, "%.2f", out _);
            Widgets.LabelRow("Speed", label);
            Widgets.SliderValue("##motionspeed", ref _clothMotion.Speed, 0.1f, 3, "%.2f", out _);

            bool keep = _clothRuntime.Options.KeepSegmentLength;
            if (Widgets.CheckboxControl("Keep segment length", ref keep))
                _clothRuntime.Options.KeepSegmentLength = keep;
            Widgets.ItemTooltip("Bones keep their length, as hair does in game.");
            bool spheres = _clothRuntime.Options.CollideSpheres;
            if (Widgets.CheckboxControl("Collide spheres", ref spheres))
                _clothRuntime.Options.CollideSpheres = spheres;
            Widgets.ItemTooltip(
                "Collides sphere collidables, as the game does. A preview setting only: it changes "
                    + "nothing in the file."
            );
        }

        void DrawLimbList()
        {
            if (_gen == null || _gen.Limbs.Count == 0)
            {
                ImGui.PushTextWrapPos();
                Widgets.DimText("No limbs yet. " + PaintNote);
                ImGui.PopTextWrapPos();
                return;
            }
            int count = _gen.Limbs.Count;
            float row = ImGui.GetTextLineHeightWithSpacing();
            float height = Math.Min(count, 8) * row + 2 * ImGui.GetStyle().WindowPadding.Y + 6;
            Widgets.BeginList("##limbs", new Vector2(0, height));
            for (int i = 0; i < count; i++)
                DrawLimbListRow(i);
            Widgets.EndList();
        }

        /// <summary>A limb's row: its colour, name, length and how it moves. A click opens its window.</summary>
        void DrawLimbListRow(int i)
        {
            var limb = _gen.Limbs[i];
            int chainIndex = LimbChain(limb);
            var chain = chainIndex >= 0 && _gen.Rig != null ? _gen.Rig.Chains[chainIndex] : null;
            string how =
                chain == null ? "no bones"
                : GroupLeader(limb) is PaintedLimb leader ? "moves with " + leader.Name
                : limb.Style == null ? ""
                : limb.Style.Preset ?? "custom";
            string kind = limb.IsBoneLimb ? "   bones" : "";
            string text =
                chain != null
                    ? $"{limb.Name}{kind}   {chain.Length:0.00}   {how}"
                    : $"{limb.Name}{kind}   {how}";
            //Room at the start of the row for the colour swatch.
            if (Widgets.ListRow($"      {text}##limbrow{LimbId(limb)}", _genSelected == i))
                OpenLimbWindow(limb);
            var min = ImGui.GetItemRectMin();
            if (ImGui.IsItemHovered())
            {
                _genHovered = i;
                Widgets.PlainTooltip(LimbSummary(limb, chain) + "\nClick for its settings.");
            }
            var hue = StrandHue(i);
            float side = ImGui.GetTextLineHeight() - 6;
            var a = new Vector2(min.X + 3, min.Y + (ImGui.GetItemRectSize().Y - side) / 2);
            ImGui
                .GetWindowDrawList()
                .AddRectFilled(a, a + new Vector2(side), Color(hue.X, hue.Y, hue.Z, 1), 3);
        }

        string LimbSummary(PaintedLimb limb, HairChain chain)
        {
            string hangs =
                chain == null ? ""
                : HairStyles.Hangs(chain) ? ", hangs"
                : ", does not hang";
            if (limb.IsBoneLimb)
            {
                var chains = _gen.ChainsOf(limb).Select(i => _gen.Rig.Chains[i]).ToList();
                return chains.Count == 0
                    ? $"Picked from the model's bones: {limb.BoneChains.Count} chain(s), none usable."
                    : $"{chain.Length:0.00} long{hangs}. Moves {chains.Count} chain(s) of the model's bones, hanging from {_gen.Rig.Bones[chain.Anchor].Name}.";
            }
            string replaced =
                limb.Replaced.Count > 0 ? $" Replaces the model's {BoneList(limb.Replaced)}." : "";
            if (limb.ReplacedEarlier.Count > 0)
                replaced +=
                    $" Replaced {BoneList(limb.ReplacedEarlier)} in the session the model was saved from.";
            return chain != null
                ? $"{chain.Length:0.00} long, {chain.Bones.Length} bone(s){hangs}, {limb.Nodes.Count} points painted, hanging from {_gen.Rig.Bones[chain.Anchor].Name}.{replaced}"
                : $"{limb.Nodes.Count} points painted, no bones.";
        }

        /// <summary>The first few names, then how many more.</summary>
        static string FirstFew(IReadOnlyList<string> names, int shown = 6) =>
            string.Join(", ", names.Take(shown))
            + (names.Count > shown ? $" and {names.Count - shown} more" : "");

        /// <summary>The limb whose style a merged limb moves in, or null when it moves in its own.</summary>
        PaintedLimb GroupLeader(PaintedLimb limb)
        {
            int chain = LimbChain(limb);
            if (chain < 0)
                return null;
            var leader = _gen.LimbOf(_gen.GroupOf(chain)[0]);
            return leader != null && leader != limb ? leader : null;
        }

        /// <summary>The other limbs moving in one piece with this one.</summary>
        List<PaintedLimb> GroupPartners(PaintedLimb limb)
        {
            int chain = LimbChain(limb);
            if (chain < 0)
                return new List<PaintedLimb>();
            return _gen.GroupOf(chain)
                .Select(_gen.LimbOf)
                .Where(l => l != null && l != limb)
                .Distinct()
                .ToList();
        }

        void DrawAddLimbButtons()
        {
            const string paint = "Add limb by painting";
            const string bones = "Add limb from bones";
            float spacing = ImGui.GetStyle().ItemSpacing.X;
            float avail = ImGui.GetContentRegionAvail().X;
            float half = (avail - spacing) / 2;
            bool pair = half >= Math.Max(Widgets.ButtonWidth(paint), Widgets.ButtonWidth(bones));
            var size = new Vector2(pair ? half : -1, 0);
            if (Widgets.Button(paint, size))
                Defer(Deferred.Author, StartNewLimb);
            Widgets.ItemTooltip(
                "Paint the part that should move. Done builds bones and cloth inside it, replacing model bones only it used."
            );
            if (pair)
                ImGui.SameLine();
            if (Widgets.Button(bones, size))
                Defer(Deferred.Author, StartBoneLimb);
            Widgets.ItemTooltip("Pick chains of the model's own bones for the cloth to move.");
        }

        void DrawAllLimbsOptions()
        {
            if (_gen == null || _gen.Limbs.Count == 0)
            {
                if (_genError != null)
                    Widgets.ErrorText(_genError);
                return;
            }
            Widgets.SectionHeader("All limbs");
            if (_gen.CloseLimbs.Count > 0)
            {
                if (_gen.MergeCloseLimbs)
                {
                    bool merge = true;
                    if (Widgets.CheckboxControl("Move close limbs as one", ref merge))
                        Defer(Deferred.Author, () => SetMergeCloseLimbs(merge));
                    Widgets.ItemTooltip(
                        "Moves close limbs as one piece, in the longest one's style. Cannot be turned back on."
                    );
                }
                ImGui.PushTextWrapPos();
                if (_gen.MergeCloseLimbs)
                    Widgets.DimText(
                        "Close limbs move as one piece. Changing a limb's settings splits it off."
                    );
                foreach (var set in _gen.CloseLimbs)
                    Widgets.DimText(
                        (_gen.MergeCloseLimbs ? "Close: " : "Close enough to cross: ")
                            + string.Join(
                                ", ",
                                set.Select(l =>
                                    l.Name
                                    + (l.OwnPiece && _gen.MergeCloseLimbs ? " (own piece)" : "")
                                )
                            )
                    );
                ImGui.PopTextWrapPos();
            }

            Widgets.LabelRow(
                "Bone spacing",
                Widgets.Column(96),
                "Distance between generated bones along a limb. The stock strands are about 0.18."
            );
            Widgets.SliderValue(
                "##spacing",
                ref _gen.RigOptions.Spacing,
                0.1f,
                0.35f,
                "%.3f",
                out bool spacingDone
            );
            if (spacingDone)
            {
                float spacing = _gen.RigOptions.Spacing;
                Defer(
                    Deferred.Author,
                    () => AuthorStep($"bone spacing {spacing:0.###}", RebuildLimbs)
                );
            }

            ImGui.PushTextWrapPos();
            if (_gen.Limbs.All(l => l.Style == null || l.Style.Motion == StrandMotion.Rigid))
                Widgets.ColoredText(
                    Theme.Cyan,
                    "Every limb is rigid: there is nothing to save, and the hair keeps the cloth it has."
                );
            if (GenHasHandEdits)
                Widgets.ColoredText(
                    Theme.Cyan,
                    "The cloth has edits of its own; changing a limb rebuilds it without them."
                );
            ImGui.PopTextWrapPos();
            if (_genError != null)
                Widgets.ErrorText(_genError);
            if (Widgets.Button("Clear all", new Vector2(-1, 0)))
                Defer(Deferred.Author, () => AuthorStep("clear all", ClearAllLimbs), replace: true);
            Widgets.ItemTooltip(
                "Deletes every limb and collidable, restoring the model's bones. Ctrl+Z brings them back."
            );
        }

        /// <summary>Every limb and authored collidable deleted, the model rebuilt as opened and the cloth emptied.</summary>
        void ClearAllLimbs()
        {
            CloseLimbWindows();
            _gen.Limbs.Clear();
            _gen.UserColliders.Clear();
            _gen.ColliderEdits.Clear();
            _genSelected = _genHovered = -1;
            _colSelected = _colHovered = null;
            RebuildLimbs();
        }

        void SetMergeCloseLimbs(bool merge) =>
            AuthorStep(
                merge ? "move close limbs as one" : "move close limbs apart",
                () =>
                {
                    _gen.MergeCloseLimbs = merge;
                    //Turned on, every close set moves as one again.
                    if (merge)
                        foreach (var limb in _gen.Limbs)
                            limb.OwnPiece = false;
                    _gen.Regroup();
                    RebuildGeneratedCloth();
                }
            );

        const string ReplaceNotice =
            "Done replaces the model's bones under this limb. Ctrl+Z or deleting the limb brings them back.";

        (List<string> Replaced, List<string> Kept) _paintUnder = (new(), new());
        int _paintUnderVersion = -1;

        /// <summary>
        /// The model's bones a Done would remove with the paint as it is, and those mostly under
        /// it that it keeps, by name; worked out again when the paint changes.
        /// </summary>
        (List<string> Replaced, List<string> Kept) PaintedBonesUnder()
        {
            if (_paintUnderVersion != _paintVersion)
            {
                _paintUnderVersion = _paintVersion;
                var (replaced, kept) = _gen.PaintPreview(_paint.Limb.Nodes);
                _paintUnder = (
                    replaced.Select(b => _gen.Mesh.Bones[b].Name).ToList(),
                    kept.Select(b => _gen.Mesh.Bones[b].Name).ToList()
                );
            }
            return _paintUnder;
        }

        /// <summary>The brush and the buttons that end painting.</summary>
        void DrawPaintControls()
        {
            var limb = _paint.Limb;
            Widgets.ColoredText(
                Theme.GoldBright,
                (_paint.IsNew ? "Painting " : "Repainting ") + limb.Name
            );
            ImGui.PushTextWrapPos();
            Widgets.DimText(PaintNote);
            Widgets.ColoredText(Theme.Cyan, ReplaceNotice);
            var (replaced, kept) = PaintedBonesUnder();
            if (replaced.Count > 0)
                Widgets.ColoredText(
                    Theme.Cyan,
                    $"Wholly under the paint, so Done removes them: {BoneList(replaced)}."
                );
            else if (limb.Nodes.Count > 0)
                Widgets.DimText("No bone of the model lies wholly under the paint yet.");
            if (kept.Count > 0)
                Widgets.DimText(
                    $"Mostly under the paint, kept for the surface outside it: {FirstFew(kept)}."
                );
            ImGui.PopTextWrapPos();
            float label = Widgets.Column(96);
            Widgets.LabelRow(
                "Mode",
                label,
                "Volume paints inside the brush, Surface along the mesh. R switches.",
                fill: false
            );
            if (Widgets.RadioButton("Volume##brushmode", !_surfaceBrush))
                _surfaceBrush = false;
            ImGui.SameLine();
            if (Widgets.RadioButton("Surface##brushmode", _surfaceBrush))
                _surfaceBrush = true;
            Widgets.LabelRow(
                "Brush",
                label,
                "The brush's radius: through space for Volume, along the surface for Surface."
            );
            Widgets.SliderValue("##brush", ref _brushRadius, BrushMin, BrushMax, "%.3f", out _);
            ImGui.PushTextWrapPos();
            Widgets.DimText("Drag paints, Ctrl erases, R switches mode, Shift+wheel sizes.");
            Widgets.DimText($"{limb.Nodes.Count} points painted");
            if (!_paint.IsNew && GenHasHandEdits)
                Widgets.ColoredText(
                    Theme.Cyan,
                    "Save rebuilds the cloth without its edits in the Cloth Editor; Cancel keeps them."
                );
            ImGui.PopTextWrapPos();
            float half = (ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X) / 2;
            Widgets.DisabledButton(
                _paint.IsNew ? "Done" : "Save",
                limb.Nodes.Count > 0,
                new Vector2(half, 0),
                () => Defer(Deferred.Author, FinishPaint, replace: true)
            );
            Widgets.ItemTooltip(
                _paint.IsNew
                    ? "Builds this limb's bones and cloth."
                    : "Builds this limb again from the new paint."
            );
            ImGui.SameLine();
            if (Widgets.Button("Cancel", new Vector2(half, 0)))
                Defer(Deferred.Author, CancelPaint, replace: true);
            Widgets.ItemTooltip(
                _paint.IsNew
                    ? "Drops this limb."
                    : "Puts the limb, the model and the cloth back as they were before this repaint."
            );
            if (_genError != null)
                Widgets.ErrorText(_genError);
        }

        /// <summary>
        /// Where a limb or collidable window opens: right of the authoring window, else left of
        /// it, at the first place down the columns it covers nothing; cascaded when there is none.
        /// </summary>
        Vector2 PlaceSideWindow(object window, float height)
        {
            var viewport = ImGui.GetMainViewport();
            float gap = SideOrderLayout.Gap;
            float bottom = viewport.Pos.Y + viewport.Size.Y - SideOrderLayout.BottomGap;
            float right =
                SideOrderControls.On && _rightCardLeft > 0
                    ? _rightCardLeft - 2 * gap
                    : viewport.Pos.X + viewport.Size.X - 300 - gap;
            bool authoring = _authoringMax.X > 0;
            float top = authoring ? _authoringMin.Y : viewport.Pos.Y + 64;
            float left = authoring ? _authoringMax.X + gap : viewport.Pos.X + LeftPanelWidth + 420;
            float width = SideWindowWidth;
            left = Math.Max(Math.Min(left, right - width), viewport.Pos.X);
            var others = _sideWindowRects.Where(r => r.Key != window).Select(r => r.Value).ToList();
            if (authoring)
                others.Add((_authoringMin, _authoringMax));
            //A limb's window leaves its own gizmo in sight.
            if (window is PaintedLimb limb && AimRect(limb) is { } gizmo)
                others.Add(gizmo);
            float Covered(Vector2 at) =>
                others.Sum(r =>
                    Math.Max(0, Math.Min(r.Max.X, at.X + width) - Math.Max(r.Min.X, at.X))
                    * Math.Max(0, Math.Min(r.Max.Y, at.Y + height) - Math.Max(r.Min.Y, at.Y))
                );
            int columns = Math.Max(1, (int)((right - left + gap) / (width + gap)));
            var xs = Enumerable.Range(0, columns).Select(c => left + c * (width + gap)).ToList();
            //Then left of the authoring window, over the viewport's other side.
            float leftmost =
                SideOrderControls.On && _leftCardRight > 0
                    ? _leftCardRight + 2 * gap
                    : viewport.Pos.X + LeftPanelWidth + gap;
            if (authoring)
                for (float x = _authoringMin.X - gap - width; x >= leftmost; x -= width + gap)
                    xs.Add(x);
            foreach (float x in xs)
            {
                //Below each window already in the column, or down the column in steps.
                var tops = others
                    .Where(r => r.Min.X < x + width && r.Max.X > x)
                    .Select(r => r.Max.Y + gap)
                    .Concat(Enumerable.Range(0, 12).Select(k => top + k * 40f))
                    .Where(y => y >= top && (y + height <= bottom || y == top))
                    .Distinct()
                    .OrderBy(y => y);
                foreach (float y in tops)
                    if (Covered(new Vector2(x, y)) <= 0)
                        return new Vector2(x, y);
            }
            //No room: cascade, so every window's title bar stays in sight.
            float title = ImGui.GetFrameHeight() + 8;
            for (int k = 0; k < 24; k++)
            {
                var at = new Vector2(
                    Math.Min(left + k * 24, right - width),
                    top + (k % 12) * title
                );
                bool clear = others.All(r =>
                    Math.Abs(r.Min.Y - at.Y) >= title || r.Max.X <= at.X || r.Min.X >= at.X + width
                );
                if (clear)
                    return at;
            }
            return new Vector2(left, top);
        }

        /// <summary>A limb's or collidable's window, as high as its content and opened beside the authoring window.</summary>
        void DrawSideWindow<T>(
            SideWindows<T> windows,
            T item,
            string title,
            Action hovered,
            Action focused,
            Action contents
        )
            where T : class
        {
            if (!windows.Heights.TryGetValue(item, out float content))
                content = windows.FirstHeight;
            float height = FitHeight(content);
            //A window opens beside the authoring window rather than where it was last left.
            if (windows.Place.Remove(item))
                ImGui.SetNextWindowPos(PlaceSideWindow(item, height), ImGuiCond.Always);
            ImGui.SetNextWindowSize(new Vector2(SideWindowWidth, content), ImGuiCond.FirstUseEver);
            ImGui.SetNextWindowSizeConstraints(new Vector2(280, height), new Vector2(900, height));
            if (windows.Focus == item)
            {
                ImGui.SetNextWindowFocus();
                windows.Focus = null;
            }
            bool open = true;
            if (BeginCardWindow($"{title}###{windows.IdPrefix}{windows.Id(item)}", ref open))
            {
                if (ImGui.IsWindowHovered(ImGuiHoveredFlags.RootAndChildWindows))
                    hovered();
                if (ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows))
                    focused();
                Widgets.Guarded("HairGen", contents);
                windows.Heights[item] = ContentHeight();
                var min = ImGui.GetWindowPos();
                _sideWindowRects[item] = (min, min + ImGui.GetWindowSize());
            }
            ImGui.End();
            if (!open)
                windows.Remove(item);
        }

        void DrawLimbWindowContents(PaintedLimb limb, int index)
        {
            int chainIndex = LimbChain(limb);
            var chain = chainIndex >= 0 && _gen.Rig != null ? _gen.Rig.Chains[chainIndex] : null;
            var style = limb.Style;
            float label = Widgets.Column(96);
            var spacing = ImGui.GetStyle().ItemSpacing;

            var hue = StrandHue(index);
            ImGui.ColorButton(
                "##hue",
                new Vector4(hue.X, hue.Y, hue.Z, 1),
                ImGuiColorEditFlags.NoTooltip,
                new Vector2(ImGui.GetFrameHeight())
            );
            ImGui.SameLine();
            DrawLimbNameField(limb);

            ImGui.PushTextWrapPos();
            Widgets.DimText(LimbSummary(limb, chain));
            if (limb.Problem != null || chain == null)
                Widgets.ColoredText(
                    chain == null ? Theme.Error : Theme.Cyan,
                    limb.Problem ?? "No bones were built for this limb."
                );
            DrawLimbGroupNote(limb);
            ImGui.PopTextWrapPos();

            if (chain != null && style != null)
            {
                ImGui.Spacing();
                DrawLimbPreset(limb, chain, label);
                DrawLimbStyle(limb, style, chain, label);
            }
            if (chain != null)
                DrawLimbAimControls(limb, label);
            ImGui.Spacing();
            bool hold = limb.HoldOnHead;
            if (
                HairOnlyCheckbox(
                    "Hold on head",
                    ref hold,
                    HoldTip,
                    "there is no head to hold on",
                    limb.IsBoneLimb
                        ? "Painted limbs only: a limb on the model's bones moves every bone picked."
                        : null
                )
            )
                Defer(
                    Deferred.Author,
                    () =>
                    {
                        limb.HoldOnHead = hold;
                        CommitLimbEdit(
                            limb,
                            rig: true,
                            $"hold on head {(hold ? "on" : "off")} for {limb.Name}"
                        );
                    }
                );

            DrawLimbColliders(limb);

            ImGui.Spacing();
            float half = (ImGui.GetContentRegionAvail().X - spacing.X) / 2;
            if (Widgets.Button("Edit", new Vector2(half, 0)))
                Defer(Deferred.Author, () => StartEditLimb(limb), replace: true);
            Widgets.ItemTooltip(
                limb.IsBoneLimb ? "Picks this limb's bones again." : "Repaints this limb."
            );
            ImGui.SameLine();
            if (Widgets.Button("Delete", new Vector2(-1, 0)))
                Defer(Deferred.Author, () => DeleteLimb(limb), replace: true);
            Widgets.ItemTooltip(
                limb.IsBoneLimb
                    ? "Removes this limb and its cloth; the model's bones stay as they are."
                    : "Removes this limb with its bones and its cloth; the model's bones it replaced come back."
            );
        }

        const string HoldTip =
            "Keeps the part lying on the head in place; bones start where the limb leaves it. Off moves the whole paint.";

        /// <summary>The limb's name, which its bones and cloth piece take, so a new one rebuilds when the field is left.</summary>
        void DrawLimbNameField(PaintedLimb limb)
        {
            ImGui.SetNextItemWidth(-1);
            string name = _limbWindows.EditName(limb, limb.Name, "##limbname");
            if (name == null)
                return;
            string safe = BoneSafeName(name);
            if (safe.Length == 0 || _gen.Limbs.Any(l => l != limb && BoneSafeName(l.Name) == safe))
            {
                _genError = $"Another limb is already called {name}.";
                _limbWindows.Names[limb] = limb.Name;
                return;
            }
            string old = limb.Name;
            limb.Name = name;
            Defer(Deferred.Author, () => AuthorStep($"rename {old} to {name}", RebuildLimbs));
        }

        /// <summary>Whom the limb moves with while close limbs move as one, and the way back into the group.</summary>
        void DrawLimbGroupNote(PaintedLimb limb)
        {
            var partners = GroupPartners(limb);
            if (partners.Count > 0)
            {
                string names = string.Join(", ", partners.Select(l => l.Name));
                var leader = GroupLeader(limb);
                Widgets.DimText(
                    leader != null
                        ? $"Moves as one piece with {names}, in {leader.Name}'s style. Changing a setting here gives it its own piece."
                        : $"Moves as one piece with {names}, in this limb's style. Changing a setting here gives it its own piece."
                );
                return;
            }
            if (!limb.OwnPiece || !_gen.MergeCloseLimbs)
                return;
            var close = _gen.CloseLimbs.FirstOrDefault(s => s.Contains(limb));
            if (close == null)
                return;
            Widgets.DimText(
                "Has its own piece, though it is close to "
                    + string.Join(", ", close.Where(l => l != limb).Select(l => l.Name))
                    + "."
            );
            if (Widgets.SmallButton("Move with them again"))
                Defer(
                    Deferred.Author,
                    () =>
                        AuthorStep(
                            $"{limb.Name} moves with its close limbs again",
                            () =>
                            {
                                limb.OwnPiece = false;
                                _gen.Regroup();
                                RebuildGeneratedCloth();
                            }
                        )
                );
        }

        void DrawLimbPreset(PaintedLimb limb, HairChain chain, float label)
        {
            if (!_limbPresetPick.TryGetValue(limb, out string pick))
                pick = limb.Style?.Preset ?? SuggestedPreset;
            Widgets.LabelRow("Preset", label, fill: false);
            ImGui.SetNextItemWidth(
                Math.Max(
                    60,
                    ImGui.GetContentRegionAvail().X
                        - Widgets.ButtonWidth("Apply")
                        - ImGui.GetStyle().ItemSpacing.X
                )
            );
            if (Widgets.BeginCombo("##preset", pick))
            {
                foreach (var preset in HairStyles.Presets)
                    if (ImGui.Selectable(preset.Name, preset.Name == pick))
                        pick = preset.Name;
                    else if (ImGui.IsItemHovered())
                        Widgets.PlainTooltip(preset.Description);
                if (ImGui.Selectable(SuggestedPreset, pick == SuggestedPreset))
                    pick = SuggestedPreset;
                else if (ImGui.IsItemHovered())
                    Widgets.PlainTooltip(
                        "The preset the limb's shape suggests, as a first build picks it."
                    );
                ImGui.EndCombo();
            }
            _limbPresetPick[limb] = pick;
            ImGui.SameLine();
            if (Widgets.Button("Apply", new Vector2(-1, 0)))
            {
                string chosen = pick;
                Defer(
                    Deferred.Author,
                    () =>
                    {
                        limb.Style =
                            chosen == SuggestedPreset
                                ? _gen.Suggest(chain)
                                : HairStyles.Find(chosen)?.Style() ?? limb.Style;
                        CommitLimbEdit(limb, rig: false, $"apply {chosen} to {limb.Name}");
                    }
                );
            }
            Widgets.ItemTooltip("Replaces this limb's settings with the preset's.");
        }

        void DrawLimbStyle(PaintedLimb limb, StrandStyle style, HairChain chain, float label)
        {
            bool edited = false,
                done = false;
            string what = null;
            int motion = (int)style.Motion;
            Widgets.LabelRow("Moves as", label);
            if (Widgets.Combo("##motion", ref motion, new[] { "Rigid", "Hanging", "Spring" }, 3))
            {
                style.Motion = (StrandMotion)motion;
                edited = done = true;
                what = "Moves as " + style.Motion;
            }
            Widgets.ItemTooltip(
                "Hanging falls under gravity and swings; Spring has no gravity and returns to its modelled shape."
            );
            if (style.Motion != StrandMotion.Rigid)
            {
                StyleValue(
                    "Stiffness",
                    ref style.Stiffness,
                    0,
                    1,
                    "How hard it returns to its styled pose.",
                    label,
                    ref edited,
                    ref done,
                    ref what
                );
                StyleValue(
                    "Bounce",
                    ref style.Bounce,
                    0,
                    1,
                    "How long it keeps moving: 0 drags and settles at once, 1 swings on.",
                    label,
                    ref edited,
                    ref done,
                    ref what
                );
                if (style.Motion == StrandMotion.Hanging)
                {
                    StyleValue(
                        "Reach",
                        ref style.Reach,
                        0,
                        1,
                        "How far it may swing from its styled pose; 0 keeps it on a spring to the pose.",
                        label,
                        ref edited,
                        ref done,
                        ref what
                    );
                    if (Widgets.CheckboxControl("Floaty", ref style.Floaty))
                    {
                        edited = done = true;
                        what = style.Floaty ? "Floaty on" : "Floaty off";
                    }
                    Widgets.ItemTooltip(
                        "Drifts after the head and floats back. Best with little bounce."
                    );
                    if (!HairStyles.Hangs(chain))
                    {
                        ImGui.PushTextWrapPos();
                        Widgets.DimText(
                            "This limb does not hang, so it keeps no reach and springs to its pose."
                        );
                        ImGui.PopTextWrapPos();
                    }
                }
                StyleValue(
                    "Width",
                    ref style.Width,
                    0.3f,
                    2,
                    "The cloth strip's width as a multiple of the limb's thickness.",
                    label,
                    ref edited,
                    ref done,
                    ref what
                );
            }
            if (edited)
                style.Preset = null;
            if (done)
            {
                string step = $"{what ?? "settings"} of {limb.Name}";
                Defer(Deferred.Author, () => CommitLimbEdit(limb, rig: false, step));
            }
        }

        static void StyleValue(
            string name,
            ref float value,
            float min,
            float max,
            string tip,
            float label,
            ref bool edited,
            ref bool done,
            ref string what
        )
        {
            Widgets.LabelRow(name, label, tip);
            edited |= Widgets.SliderValue(
                "##" + name,
                ref value,
                min,
                max,
                "%.3f",
                out bool finished
            );
            done |= finished;
            if (finished)
                what = $"{name} {value:0.###}";
        }

        /// <summary>
        /// Applies an edit of one limb to the running cloth. A limb moving as one with others
        /// leaves the group first, so its own settings take effect; the rest of the group stays.
        /// </summary>
        void CommitLimbEdit(PaintedLimb limb, bool rig, string label) =>
            AuthorStep(
                label,
                () =>
                {
                    bool split = false;
                    if (GroupPartners(limb).Count > 0)
                    {
                        limb.OwnPiece = true;
                        split = true;
                    }
                    if (rig)
                    {
                        RebuildLimbs();
                        return;
                    }
                    _gen.Regroup();
                    RebuildGeneratedCloth(keepMotion: !split);
                }
            );
    }
}
