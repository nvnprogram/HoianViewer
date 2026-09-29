using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using ImGuiNET;
using OpenTK.Input;
using PlayerViewer.HairGen;
using PlayerViewer.Phive;
using PlayerViewer.Physics;
using Vector2 = System.Numerics.Vector2;

namespace PlayerViewer.UI
{
    // One undo history for the physics authoring. Each step keeps the whole state after it: the
    // limbs and collidables, the model's exact bytes and the cloth's committed snapshot, so an
    // undo brings back bones a painted limb replaced and walks hand edits of the cloth data in
    // the same order. Byte arrays are shared between steps, never copied.
    public partial class ViewerWindow
    {
        sealed class AuthorState
        {
            public HairGenerator Gen;
            public byte[] GenOriginal;
            public string GenActor;
            public ModelSnapshot Model;
            public byte[] ModelBytes;
            public bool Hair;
            public ClothDocument Cloth;
            public byte[] ClothBytes;

            //-1 when the cloth is not the generator's, 0 as generated, 1 with hand edits.
            public int GenCloth;
            public string SaveActors;
        }

        readonly UndoHistory<AuthorState> _undo = new()
        {
            Limit = 100,
            Budget = 256L << 20,
            Size = UndoBytes,
        };

        //What the live state was made of when the history last saw it. A change of any of these
        //that no step recorded is taken as the current state rather than left to be undone.
        (HairGenerator Gen, byte[] Model, ClothDocument Cloth, byte[] ClothBytes) _undoLive;
        int _undoDepth;
        string _undoLabel;
        bool _undoRestoring;

        static long UndoBytes(IEnumerable<AuthorState> states)
        {
            var seen = new HashSet<byte[]>(ReferenceEqualityComparer.Instance);
            long total = 0;
            foreach (var state in states)
            {
                if (state.ModelBytes != null && seen.Add(state.ModelBytes))
                    total += state.ModelBytes.Length;
                if (state.ClothBytes != null && seen.Add(state.ClothBytes))
                    total += state.ClothBytes.Length;
            }
            return total;
        }

        AuthorState CaptureAuthorState()
        {
            var previous = _undo.Current;
            var model = _standalone?.SourceData;
            //A rebuild that wrote the same model shares the bytes already kept.
            if (
                previous?.ModelBytes != null
                && model != null
                && !ReferenceEquals(previous.ModelBytes, model)
                && previous.ModelBytes.AsSpan().SequenceEqual(model)
            )
                model = previous.ModelBytes;
            var snapshot = _gen != null ? TakeModelSnapshot(previous?.Model) : null;
            if (snapshot != null)
                snapshot.ModelBytes = model;
            return new AuthorState
            {
                Gen = _gen,
                GenOriginal = _genOriginal,
                GenActor = _genActor,
                Model = snapshot,
                ModelBytes = model,
                Hair = AuthorHair,
                Cloth = _cloth,
                ClothBytes = _cloth?.CommittedBytes,
                GenCloth =
                    _genClothVersion < 0 ? -1
                    : GenHasHandEdits ? 1
                    : 0,
                SaveActors = _clothSaveActors,
            };
        }

        static bool SameState(AuthorState a, AuthorState b) =>
            a != null
            && b != null
            && a.Gen == b.Gen
            && ReferenceEquals(a.ModelBytes, b.ModelBytes)
            && a.Cloth == b.Cloth
            && ReferenceEquals(a.ClothBytes, b.ClothBytes)
            && a.GenCloth == b.GenCloth
            && a.Hair == b.Hair
            && a.Model?.Key == b.Model?.Key;

        void NoteUndoLive() =>
            _undoLive = (_gen, _standalone?.SourceData, _cloth, _cloth?.CommittedBytes);

        /// <summary>Starts the history at the current state when it has none.</summary>
        void EnsureUndoBase()
        {
            if (_undo.HasBase || _standalone == null)
                return;
            _undo.Reset(CaptureAuthorState());
            NoteUndoLive();
        }

        /// <summary>Everything the history holds goes with the model.</summary>
        void ResetAuthorUndo()
        {
            _undo.Clear();
            _undoDepth = 0;
            CancelDeferred(Deferred.Undo);
            _undoLive = default;
        }

        void BeginAuthorStep(string label)
        {
            if (_undoDepth++ == 0)
            {
                _undoLabel = label;
                EnsureUndoBase();
            }
        }

        void EndAuthorStep()
        {
            if (--_undoDepth > 0)
                return;
            RecordAuthorStep(_undoLabel);
        }

        /// <summary>
        /// Runs a change as one undo step. The state before it is the one the last step left, so
        /// a value a control changed in place before the step started still belongs to it. Steps
        /// inside a step join the outer one.
        /// </summary>
        void AuthorStep(string label, Action action)
        {
            BeginAuthorStep(label);
            try
            {
                action();
            }
            finally
            {
                EndAuthorStep();
            }
        }

        void RecordAuthorStep(string label)
        {
            //A paint session is half made; its Done is the step.
            if (_undoRestoring || _paint != null || _standalone == null)
                return;
            EnsureUndoBase();
            var state = CaptureAuthorState();
            if (!SameState(state, _undo.Current))
            {
                _undo.Push(label, state);
                Console.WriteLine($"[Undo] {label}");
            }
            NoteUndoLive();
        }

        /// <summary>A hand edit of the cloth data, committed by the Cloth Editor or the tab, is a step of its own.</summary>
        void OnClothCommitted(ClothDocument doc)
        {
            if (doc != _cloth || _undoDepth > 0 || _undoRestoring)
                return;
            RecordAuthorStep(ClothEditLabel());
        }

        string ClothEditLabel()
        {
            var datas = _cloth?.File.Container?.ClothDatas;
            string piece =
                datas != null && _clothSelPiece >= 0 && _clothSelPiece < datas.Count
                    ? datas[_clothSelPiece].Name
                    : null;
            string what = _clothSel switch
            {
                ClothSelKind.Particle when piece != null => $"particle {_clothSelIndex} of {piece}",
                ClothSelKind.Particles when piece != null => $"the particles of {piece}",
                ClothSelKind.Set when piece != null => $"a constraint set of {piece}",
                ClothSelKind.Collidable
                    when piece == null
                        && _clothSelIndex >= 0
                        && _clothSelIndex < (_cloth?.File.Container?.Collidables.Count ?? 0) =>
                    _cloth.File.Container.Collidables[_clothSelIndex].Name,
                _ when piece != null => piece,
                _ => _cloth?.Source.FileName ?? "the cloth",
            };
            return "edit cloth data: " + what;
        }

        /// <summary>After everything that draws: takes a change no step recorded (a cloth file opened, a session cancelled) as the current state.</summary>
        void SyncUndoBase()
        {
            if (_standalone == null || _paint != null || _undoDepth > 0)
                return;
            if (!_undo.HasBase)
            {
                EnsureUndoBase();
                return;
            }
            if ((_gen, _standalone.SourceData, _cloth, _cloth?.CommittedBytes) == _undoLive)
                return;
            _undo.Rebase(CaptureAuthorState());
            TestHookNote("undo rebase");
            NoteUndoLive();
        }

        void UndoAuthoring()
        {
            if (_paint != null)
            {
                PaintUndo(_paint.Undo, _paint.Redo);
                return;
            }
            string label = _undo.UndoLabel;
            if (_undo.Undo() is not AuthorState state)
                return;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            RestoreAuthorState(state);
            _genNotice = $"Undone: {label}.";
            Console.WriteLine($"[Undo] undone {label} in {watch.ElapsedMilliseconds} ms");
        }

        void RedoAuthoring()
        {
            if (_paint != null)
            {
                PaintUndo(_paint.Redo, _paint.Undo);
                return;
            }
            string label = _undo.RedoLabel;
            if (_undo.Redo() is not AuthorState state)
                return;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            RestoreAuthorState(state);
            _genNotice = $"Redone: {label}.";
            Console.WriteLine($"[Undo] redone {label} in {watch.ElapsedMilliseconds} ms");
        }

        /// <summary>
        /// Puts a kept state back: the generator and its limbs, the model's bytes, the cloth
        /// document and its committed snapshot, so hand edits come back exactly.
        /// </summary>
        void RestoreAuthorState(AuthorState state)
        {
            _undoRestoring = true;
            try
            {
                bool docChanged = _cloth != state.Cloth;
                if (_gen != state.Gen)
                {
                    _gen = state.Gen;
                    _genOriginal = state.GenOriginal;
                    _genActor = state.GenActor;
                    _genSelected = _genHovered = -1;
                    _pipeline.BoneTints = null;
                }
                if (AuthorHair != state.Hair)
                {
                    SetHairFlag(state.Hair);
                    _clothRuntime.Invalidate();
                }
                if (_gen != null && state.Model != null)
                    RestoreModelSnapshot(state.Model, rebuildCloth: false);
                else
                    ShowModelBytesUnlessSame(state.ModelBytes);
                //The generator's derived state, the default collidables and what each build found.
                if (_gen?.Rig != null)
                    try
                    {
                        _gen.BuildCloth(EmptyGeneratedCloth());
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[Undo] {ex}");
                    }
                _cloth = state.Cloth;
                _cloth?.Restore(state.ClothBytes);
                if (docChanged)
                {
                    _clothRuntime.Invalidate();
                    _clothSaveActors = state.SaveActors;
                }
                if (state.GenCloth < 0 || _cloth == null)
                    UnmarkGeneratedCloth();
                else
                    MarkGeneratedCloth(edited: state.GenCloth > 0);
                _clothFaulted = false;
                _limbWindows.Names.Clear();
                _colWindows.Names.Clear();
                ValidateClothSelection();
            }
            finally
            {
                _undoRestoring = false;
            }
            NoteUndoLive();
        }

        /// <summary>Shows model bytes unless the model shown already has them, so an undo that leaves the model alone does not reload it.</summary>
        void ShowModelBytesUnlessSame(byte[] bytes)
        {
            if (
                bytes == null
                || ReferenceEquals(bytes, _standalone.SourceData)
                || bytes.AsSpan().SequenceEqual(_standalone.SourceData)
            )
                return;
            ShowModelBytes(bytes);
        }

        /// <summary>Ctrl+Z undoes, Ctrl+Shift+Z and Ctrl+Y redo, unless a text field or a control has the keyboard or a stroke is being painted.</summary>
        void HandleAuthorShortcuts()
        {
            var io = ImGui.GetIO();
            if (
                !io.KeyCtrl
                || io.KeyAlt
                || io.WantTextInput
                || Widgets.Typing
                || ImGui.IsAnyItemActive()
                || _paintStroke
            )
                return;
            if (ImGui.IsKeyPressed((int)Key.Z))
                Defer(Deferred.Undo, io.KeyShift ? RedoAuthoring : UndoAuthoring, replace: true);
            else if (ImGui.IsKeyPressed((int)Key.Y))
                Defer(Deferred.Undo, RedoAuthoring, replace: true);
        }

        /// <summary>After the section's own requests: the undo keys, then the undo or redo asked for, then any change no step recorded.</summary>
        void RunUndoKeys()
        {
            HandleAuthorShortcuts();
            try
            {
                TakeDeferred(Deferred.Undo)?.Invoke();
            }
            catch (Exception ex)
            {
                _genError = "Undo failed: " + ex.Message;
                Console.WriteLine($"[Undo] {ex}");
            }
            SyncUndoBase();
        }

        bool CanUndoAuthoring => _paint != null ? _paint.Undo.Count > 0 : _undo.CanUndo;
        bool CanRedoAuthoring => _paint != null ? _paint.Redo.Count > 0 : _undo.CanRedo;

        string UndoTip()
        {
            if (_paint != null)
                return _paint.Undo.Count > 0
                    ? $"Undo the last {(_paint.Bones ? "pick" : "stroke")} (Ctrl+Z)"
                    : "Nothing to undo in this " + (_paint.Bones ? "pick" : "paint");
            return _undo.CanUndo ? $"Undo: {_undo.UndoLabel} (Ctrl+Z)" : "Nothing to undo";
        }

        string RedoTip()
        {
            if (_paint != null)
                return _paint.Redo.Count > 0
                    ? $"Redo the {(_paint.Bones ? "pick" : "stroke")} undone (Ctrl+Shift+Z or Ctrl+Y)"
                    : "Nothing to redo";
            return _undo.CanRedo
                ? $"Redo: {_undo.RedoLabel} (Ctrl+Shift+Z or Ctrl+Y)"
                : "Nothing to redo";
        }

        /// <summary>
        /// Undo and Redo, each naming its step in a tooltip, in the first two of
        /// <paramref name="slots"/> equal places across the line; Redo fills the line when there
        /// are two. Returns a place's width. Both run once the windows have drawn.
        /// </summary>
        float DrawUndoButtons(int slots = 2)
        {
            float spacing = ImGui.GetStyle().ItemSpacing.X;
            float width = (ImGui.GetContentRegionAvail().X - (slots - 1) * spacing) / slots;
            Widgets.DisabledButton(
                "Undo",
                CanUndoAuthoring,
                new Vector2(width, 0),
                () => Defer(Deferred.Undo, UndoAuthoring, replace: true)
            );
            TestHookNote("undo button");
            if (Widgets.ItemHovered())
                Widgets.PlainTooltip(UndoTip());
            ImGui.SameLine();
            Widgets.DisabledButton(
                "Redo",
                CanRedoAuthoring,
                new Vector2(slots == 2 ? -1 : width, 0),
                () => Defer(Deferred.Undo, RedoAuthoring, replace: true)
            );
            TestHookNote("redo button");
            if (Widgets.ItemHovered())
                Widgets.PlainTooltip(RedoTip());
            return width;
        }

        /// <summary>A key over everything a model snapshot holds, so a step that changed nothing makes no step.</summary>
        static string SnapshotKey(ModelSnapshot s)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            sb.Append(s.Spacing.ToString("R", inv))
                .Append('|')
                .Append(s.Merge)
                .Append('|')
                .Append(s.Hair);
            foreach (var (limb, st) in s.Limbs)
            {
                sb.Append("|L")
                    .Append(RuntimeHelpers.GetHashCode(limb))
                    .Append(':')
                    .Append(st.Name);
                sb.Append(':').Append(st.Nodes.Count).Append(':').Append(NodesHash(st.Nodes));
                if (st.BoneChains != null)
                    foreach (var chain in st.BoneChains)
                        sb.Append(':').Append(string.Join(">", chain));
                if (st.Style is StrandStyle y)
                    sb.Append(
                        string.Create(
                            inv,
                            $":{y.Preset}:{y.Motion}:{y.Stiffness:R}:{y.Bounce:R}:{y.Reach:R}:{y.Width:R}:{y.Floaty}"
                        )
                    );
                sb.Append(':').Append(st.HoldOnHead).Append(':').Append(st.OwnPiece);
                sb.Append(':').Append(st.Hang);
                if (st.Aim is LimbAim aim)
                    sb.Append(
                        string.Create(
                            inv,
                            $":{aim.Root.X:R},{aim.Root.Y:R},{aim.Root.Z:R}>{aim.Direction.X:R},{aim.Direction.Y:R},{aim.Direction.Z:R}>{aim.Side?.X:R},{aim.Side?.Y:R},{aim.Side?.Z:R}>{aim.Straight}"
                        )
                    );
            }
            foreach (var (collider, st) in s.Colliders)
                sb.Append("|C")
                    .Append(RuntimeHelpers.GetHashCode(collider))
                    .Append(ColliderKey(st));
            foreach (
                var (name, edit) in s.ColliderEdits.OrderBy(e => e.Key, StringComparer.Ordinal)
            )
                sb.Append("|E").Append(name).Append(ColliderKey(edit));
            return sb.ToString();
        }

        static string ColliderKey(LimbCollider c)
        {
            string limbs =
                c.Limbs == null
                    ? "*"
                    : string.Join(",", c.Limbs.Select(RuntimeHelpers.GetHashCode).OrderBy(h => h));
            return string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $":{c.Name}:{c.Kind}:{c.Bone}:{c.Start.X:R},{c.Start.Y:R},{c.Start.Z:R}:{c.End.X:R},{c.End.Y:R},{c.End.Z:R}:{c.Radius:R}:{c.Enabled}:{limbs}:{c.Below:R}"
            );
        }

        static string NodesHash(HashSet<int> nodes)
        {
            ulong sum = 0,
                xor = 0;
            foreach (int n in nodes)
            {
                ulong z = (ulong)n + 0x9E3779B97F4A7C15UL;
                z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
                z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
                z ^= z >> 31;
                sum += z;
                xor ^= z;
            }
            return $"{sum:x}{xor:x}";
        }

        //Undo inside a paint or pick session: each stroke or pick, until Done or Cancel.

        (HashSet<int> Nodes, List<List<int>> Chains) PaintSessionState() =>
            (new HashSet<int>(_paint.Limb.Nodes), _paint.Chains?.Select(c => c.ToList()).ToList());

        /// <summary>Keeps the paint or picks as they are before a stroke or a pick changes them.</summary>
        void PushPaintUndo()
        {
            if (_paint == null)
                return;
            _paint.Undo.Add(PaintSessionState());
            if (_paint.Undo.Count > 100)
                _paint.Undo.RemoveAt(0);
            _paint.Redo.Clear();
        }

        /// <summary>Drops the last kept state when the stroke or pick after it changed nothing.</summary>
        void DropPaintUndoIfSame()
        {
            if (_paint == null || _paint.Undo.Count == 0)
                return;
            var (nodes, chains) = _paint.Undo[^1];
            bool same =
                nodes.SetEquals(_paint.Limb.Nodes)
                && (
                    chains == null
                    || (
                        chains.Count == _paint.Chains.Count
                        && chains.Zip(_paint.Chains).All(p => p.First.SequenceEqual(p.Second))
                    )
                );
            if (same)
                _paint.Undo.RemoveAt(_paint.Undo.Count - 1);
        }

        void PaintUndo(
            List<(HashSet<int> Nodes, List<List<int>> Chains)> from,
            List<(HashSet<int> Nodes, List<List<int>> Chains)> to
        )
        {
            if (from.Count == 0)
                return;
            to.Add(PaintSessionState());
            var (nodes, chains) = from[^1];
            from.RemoveAt(from.Count - 1);
            _paint.Limb.Nodes = new HashSet<int>(nodes);
            if (chains != null)
            {
                _paint.Chains.Clear();
                _paint.Chains.AddRange(chains.Select(c => c.ToList()));
            }
            _paintVersion++;
        }
    }
}
