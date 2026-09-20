using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using BfresEditor;
using PlayerViewer.Core;
using ShaderBundler;

namespace PlayerViewer.Shaders
{
    /// <summary>
    /// The splicer's model side: the variation grids, the demand handed to the scheduler,
    /// the outcomes coming back and the live preview, pumped once a frame by the window.
    /// The ubershader context is romfs scoped and outlives the model; everything else here
    /// belongs to the model that is open.
    /// </summary>
    public sealed class SpliceQueue
    {
        readonly Func<IReadOnlyList<BfresModelAsset>> _models;
        readonly Func<FMAT, IEnumerable<uint>> _weights;

        readonly Dictionary<FMAT, MaterialVariations> _variations = new();
        readonly HashSet<FMAT> _dirty = new();

        //One entry per job, so a cell that needs both a quick splice and the guarded one is
        //in here twice. A job key is shared by every cell that would splice identically.
        readonly List<(MaterialVariations Owner, VariationCell Cell, bool Preview)> _pending =
            new();
        readonly List<string> _pendingKeys = new();
        readonly HashSet<string> _pendingSeen = new(StringComparer.Ordinal);
        readonly HashSet<(VariationCell Cell, bool Preview)> _pendingCells = new();
        string[] _demandKeys = Array.Empty<string>();

        //A grid is a few ms to build and a model can carry a couple of hundred, so the sweep
        //that follows the splicer being switched on is spread over frames.
        const int GridsPerFrame = 2;

        //The settle exists for widgets that report a change while still being manipulated,
        //so a text field does not splice every keystroke. A combo commits once and gets none.
        static readonly TimeSpan HeldSettle = TimeSpan.FromMilliseconds(150);
        static readonly TimeSpan CommitSettle = TimeSpan.Zero;
        long _lastEdit;
        TimeSpan _editSettle = CommitSettle;

        public SpliceQueue(
            Func<IReadOnlyList<BfresModelAsset>> models,
            Func<FMAT, IEnumerable<uint>> weights
        )
        {
            _models = models ?? throw new ArgumentNullException(nameof(models));
            _weights = weights ?? throw new ArgumentNullException(nameof(weights));
        }

        public UberContext Uber { get; private set; }
        public CompileScheduler Scheduler { get; private set; }
        public LivePreview Preview { get; } = new();

        public int SplicesDone { get; private set; }
        public int SplicesFailed { get; private set; }

        public bool Ready => Uber != null && Uber.State == UberState.Ready;
        public bool Loading => Uber != null && Uber.State == UberState.Loading;

        /// <summary>Creates the context on first use and starts its load. Safe every frame.</summary>
        public void EnsureContext(Romfs romfs)
        {
            if (romfs == null)
                return;
            if (Uber == null)
            {
                Uber = new UberContext(romfs);
                Scheduler = new CompileScheduler(Uber);
            }
            Uber.Ensure();
        }

        /// <summary>
        /// Hands every material back to its shipped archive and forgets the grids and the
        /// demand. The context and the scheduler stay, since they belong to the romfs.
        /// </summary>
        public void Reset()
        {
            Preview.Reset(_models());
            _variations.Clear();
            _dirty.Clear();
            ClearPending();
            _demandKeys = Array.Empty<string>();
            SplicesDone = 0;
            SplicesFailed = 0;
            Scheduler?.SetDemand(Array.Empty<CompileRequest>());
        }

        /// <summary>Reset, then drop the context and its scheduler. For a romfs change.</summary>
        public void DropContext()
        {
            Reset();
            Scheduler?.Dispose();
            Scheduler = null;
            Uber = null;
        }

        /// <summary>An edit happened; the grid is rebuilt on the next pull. held means the
        /// widget is still being manipulated, which opens the settle window.</summary>
        public void Invalidate(FMAT material, bool held)
        {
            if (material == null)
                return;
            _dirty.Add(material);
            _lastEdit = Stopwatch.GetTimestamp();
            _editSettle = held ? HeldSettle : CommitSettle;
            SpliceTrace.Edit(_editSettle.TotalMilliseconds);
        }

        /// <summary>The full grid, rebuilt when dirty. Falls back to the existence grid while
        /// the context is not ready.</summary>
        public MaterialVariations Get(FMAT material)
        {
            if (!Ready)
                return Existence(material);
            var v = Slot(material);
            if (_dirty.Remove(material) | v.ExistenceOnly)
                Build(v, Uber);
            return v;
        }

        /// <summary>The existence grid, which needs the serving archive and nothing else.</summary>
        public MaterialVariations Existence(FMAT material)
        {
            var v = Slot(material);
            if (_dirty.Remove(material) || !v.ExistenceOnly)
                Build(v, null);
            return v;
        }

        public bool TryGet(FMAT material, out MaterialVariations v)
        {
            v = null;
            return material != null && _variations.TryGetValue(material, out v);
        }

        MaterialVariations Slot(FMAT material)
        {
            if (!_variations.TryGetValue(material, out var v))
            {
                v = new MaterialVariations(material);
                _variations[material] = v;
                _dirty.Add(material);
            }
            return v;
        }

        void Build(MaterialVariations v, UberContext uber)
        {
            try
            {
                if (uber == null)
                    v.RebuildExistence(_weights(v.Material));
                else
                    v.Rebuild(uber, _weights(v.Material));
            }
            catch (Exception ex)
            {
                v.Fail(ex.Message);
            }
        }

        /// <summary>Lets a failed cell be spliced again.</summary>
        public void Forget(VariationCell cell)
        {
            Scheduler?.Forget(cell.Key.Job(false));
            cell.State = CellState.None;
            cell.Failure = null;
        }

        //--- Per frame

        /// <summary>
        /// Takes the outcomes, builds a few missing grids, rebuilds the demand and syncs the
        /// previews, the selected material first. Only the outcomes are taken while disabled.
        /// </summary>
        public void Pump(
            FMAT selected,
            bool savePending,
            string modelName,
            bool enabled,
            bool preview = true
        )
        {
            if (Scheduler == null)
                return;

            while (Scheduler.TryTakeOutcome(out var outcome))
                ApplyOutcome(outcome);

            if (!enabled || !Ready)
                return;

            PumpGrids();
            RebuildDemand(selected, savePending);

            if (!preview)
                return;

            //After the demand pass, so a splice that just landed is bound on the same frame.
            Preview.Advance(_models());
            if (selected != null && _variations.TryGetValue(selected, out var v))
                Preview.Sync(selected, v, Uber, modelName, _models());

            //Every other material whose drawn passes have been spliced is bound too, one
            //build at a time, so nothing waits to be selected before it draws.
            if (Preview.Upgrading)
                return;
            foreach (var material in Materials())
            {
                if (material == selected || !_variations.TryGetValue(material, out var other))
                    continue;
                Preview.Sync(material, other, Uber, modelName, _models());
                if (Preview.Upgrading)
                    break;
            }
        }

        IEnumerable<FMAT> Materials()
        {
            foreach (var model in _models())
            foreach (var material in model.ResModel.Materials.OfType<FMAT>())
                yield return material;
        }

        void PumpGrids()
        {
            int budget = GridsPerFrame;
            foreach (var material in Materials())
            {
                var v = Slot(material);
                if (!v.ExistenceOnly && !_dirty.Contains(material))
                    continue;
                Get(material);
                if (--budget <= 0)
                    return;
            }
        }

        void ApplyOutcome(CompileOutcome outcome)
        {
            foreach (var v in _variations.Values)
            foreach (var cell in v.Cells)
            {
                if (v.ExistenceOnly || outcome.Key != cell.Key.Job(outcome.Preview))
                    continue;
                if (outcome.Preview)
                {
                    cell.PreviewState = outcome.Success ? CellState.Ready : CellState.Failed;
                    cell.PreviewReady = outcome.Success;
                    continue;
                }
                cell.State = outcome.Success ? CellState.Ready : CellState.Failed;
                cell.Failure = outcome.Success ? null : outcome.Message;
                cell.Seconds = outcome.Seconds;
            }

            if (outcome.Success)
            {
                SplicesDone++;
                SpliceTrace.Note(
                    outcome.FromCache
                        ? $"{outcome.Label} served from cache"
                        : $"{outcome.Label} in {outcome.Seconds:0.00}s"
                );
            }
            else
            {
                SplicesFailed++;
                Console.WriteLine($"[Splice] {outcome.Label} failed: {outcome.Message}");
            }
        }

        /// <summary>
        /// The grids the queue works through after the selected material, visible materials
        /// first and in model order inside that.
        /// </summary>
        List<MaterialVariations> SpliceOrder(FMAT selected)
        {
            var order = new List<MaterialVariations>();
            foreach (bool visible in new[] { true, false })
            foreach (var material in Materials())
            {
                if (material == selected || material.IsVisible != visible)
                    continue;
                if (
                    _variations.TryGetValue(material, out var v)
                    && v.Ok
                    && !v.ExistenceOnly
                    && v.Error == null
                )
                    order.Add(v);
            }
            return order;
        }

        //The demand list is rebuilt from the cached cell states every frame and only pushed
        //to the scheduler when it changed.
        void RebuildDemand(FMAT selected, bool savePending)
        {
            ClearPending();

            bool ready = Uber.CanCompile && Stopwatch.GetElapsedTime(_lastEdit) > _editSettle;

            MaterialVariations live = null;
            if (
                ready
                && selected != null
                && _variations.TryGetValue(selected, out var found)
                && found.Ok
                && !found.ExistenceOnly
            )
                live = found;

            //The selected material first: its quick splices of the drawn passes, then the
            //guarded ones, then the rest of its stages.
            if (live != null)
            {
                if (selected.IsVisible)
                {
                    Collect(live, DrawnPasses.IsDrawn, true);
                    Collect(live, DrawnPasses.IsDrawn, false);
                }
                Collect(live, _ => true, false);
            }

            //A save that is waiting jumps the queue.
            if (ready && savePending)
                foreach (var v in SaveCritical())
                    Collect(v, _ => true, false);

            //Then the rest of the model, drawn passes before auxiliary ones. Only the selected
            //material gets a quick splice, since the preview binds one material at a time.
            if (ready)
            {
                var others = SpliceOrder(selected);
                foreach (var v in others)
                    Collect(v, DrawnPasses.IsDrawn, false);
                foreach (var v in others)
                    Collect(v, _ => true, false);
            }

            foreach (var v in _variations.Values)
            foreach (var cell in v.Cells)
            {
                if (cell.State == CellState.Queued || cell.State == CellState.Running)
                    cell.State = CellState.None;
                if (cell.PreviewState == CellState.Queued || cell.PreviewState == CellState.Running)
                    cell.PreviewState = CellState.None;
            }
            var claimed = Scheduler.ClaimedKeys();
            foreach (var (_, cell, preview) in _pending)
            {
                var state = claimed.Contains(cell.Key.Job(preview))
                    ? CellState.Running
                    : CellState.Queued;
                if (preview)
                    cell.PreviewState = state;
                else
                    cell.State = state;
            }

            if (_pendingKeys.Count == _demandKeys.Length)
            {
                bool same = true;
                for (int i = 0; i < _pendingKeys.Count && same; i++)
                    same = _pendingKeys[i] == _demandKeys[i];
                if (same)
                    return;
            }

            _demandKeys = _pendingKeys.ToArray();
            SpliceTrace.Log($"demand pushed ({_pendingKeys.Count})");

            var requests = new List<CompileRequest>(_pendingKeys.Count);
            var taken = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (owner, cell, preview) in _pending)
            {
                string key = cell.Key.Job(preview);
                if (!taken.Add(key))
                    continue;
                requests.Add(
                    new CompileRequest
                    {
                        Key = key,
                        Label =
                            $"{owner.Material.Name} {DrawnPasses.Short(cell.Pass)} w{cell.Weight}"
                            + (preview ? " quick" : ""),
                        Splice = cell.Key,
                        Vector = cell.Vector,
                        Preview = preview,
                    }
                );
            }
            Scheduler.SetDemand(requests);
        }

        void ClearPending()
        {
            _pending.Clear();
            _pendingKeys.Clear();
            _pendingSeen.Clear();
            _pendingCells.Clear();
        }

        void Collect(MaterialVariations v, Func<string, bool> want, bool preview)
        {
            foreach (var cell in v.Cells)
            {
                if (!want(cell.Pass) || !v.IsSelected(cell.Pass))
                    continue;
                if (cell.Exists || cell.Refusal != null)
                    continue;
                if (preview)
                {
                    //A quick splice is only worth making while the guarded one is missing,
                    //and a failure is not retried on its own.
                    if (cell.PreviewReady || cell.State == CellState.Ready)
                        continue;
                    if (cell.PreviewState == CellState.Failed)
                        continue;
                }
                else if (cell.State == CellState.Ready || cell.State == CellState.Failed)
                    continue;
                if (!_pendingCells.Add((cell, preview)))
                    continue;
                _pending.Add((v, cell, preview));
                string key = cell.Key.Job(preview);
                if (_pendingSeen.Add(key))
                    _pendingKeys.Add(key);
            }
        }

        //--- Saving

        /// <summary>The materials a save would embed an archive for, counting the splices
        /// still to come, since those are what the save is waiting on.</summary>
        public IEnumerable<MaterialVariations> SaveCritical()
        {
            foreach (var v in _variations.Values)
            {
                if (!v.Ok || v.ExistenceOnly)
                    continue;
                if (ModelBundle.NeedsArchive(v, Uber, v.Material.Name, null, countPending: true))
                    yield return v;
            }
        }

        /// <summary>
        /// The splices a pending save is waiting on, counted from the cells rather than from
        /// the queue: a cell that dropped out of the demand for a frame is still a splice
        /// that has not happened. Every grid is built first so a material the per frame sweep
        /// has not reached is waited on too.
        /// </summary>
        public int SaveWorkOutstanding()
        {
            if (Uber == null || !Uber.CanCompile)
                return 0;

            foreach (var material in Materials())
                Get(material);

            int outstanding = 0;
            foreach (var v in SaveCritical())
            foreach (var cell in v.Cells)
            {
                if (!v.IsSelected(cell.Pass) || cell.Exists || cell.Refusal != null)
                    continue;
                if (cell.State != CellState.Ready && cell.State != CellState.Failed)
                    outstanding++;
            }
            return outstanding;
        }
    }
}
