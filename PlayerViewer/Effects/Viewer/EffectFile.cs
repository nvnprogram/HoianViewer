using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using EffectLibrary;
using PlayerViewer.Core.Formats;
using PlayerViewer.Effects.Sim;

namespace PlayerViewer.Effects.Viewer
{
    /// <summary>What the viewer cannot show exactly, per emitter and summed per set.</summary>
    [Flags]
    public enum EffectGaps
    {
        None = 0,

        /// <summary>A stream out emitter the CPU steps in place of its compute programs.</summary>
        CpuFallback = 1,
        CustomField = 4,

        /// <summary>Connection and super stripes are not simulated, so these emitters are not drawn.</summary>
        Stripes = 8,
    }

    /// <summary>One set's line in the list: its name, emitter count and gaps, read once.</summary>
    public sealed class EffectSetSummary
    {
        public EmitterSet Set { get; init; }
        public string Name { get; init; }
        public int EmitterCount { get; init; }
        public EffectGaps Gaps { get; init; }
    }

    /// <summary>
    /// An open effect file: the esetb, a summary per set, and the loop classification of every
    /// set, worked out on a background thread in list order with the selected set first.
    /// </summary>
    public sealed class EffectFile : IDisposable
    {
        public string Path { get; }
        public string Name { get; }
        public Esetb Esetb { get; }
        public VfxFile Vfx => Esetb.Vfx;
        public IReadOnlyList<EffectSetSummary> Sets { get; }

        readonly ConcurrentDictionary<(EmitterSet, int), LoopInfo> _loops = new();
        readonly ConcurrentDictionary<(EmitterSet, int), string> _failures = new();
        readonly BlockingCollection<(EmitterSet Set, int MinPeriod)> _urgent = new();
        readonly CancellationTokenSource _stop = new();
        readonly Thread _worker;
        volatile int _backgroundDone;

        EffectFile(string path, Esetb esetb, int minimumPeriod)
        {
            _minimumPeriod = Math.Max(minimumPeriod, 0);
            Path = path;
            Esetb = esetb;
            Name = FileName(path);
            Sets = esetb
                .Vfx.EmitterSets.OrderBy(set => set.Name, StringComparer.OrdinalIgnoreCase)
                .Select(set => new EffectSetSummary
                {
                    Set = set,
                    Name = set.Name,
                    EmitterCount = set.AllEmitters.Count(),
                    Gaps = set.AllEmitters.Aggregate(EffectGaps.None, (g, e) => g | GapsOf(e)),
                })
                .ToList();

            //The simulation reads these lazily; parse them here so the analysis thread and the
            //render thread never race to build them.
            _ = Vfx.NativePrimitives;
            _ = Vfx.Primitives?.ResFile;

            _worker = new Thread(Work)
            {
                IsBackground = true,
                Name = "EffectLoopAnalysis",
                Priority = ThreadPriority.BelowNormal,
            };
            _worker.Start();
        }

        /// <summary>Reads an esetb, zstd compressed or not. Throws on a file that is not one.</summary>
        public static EffectFile Open(string path, int minimumPeriod) =>
            new(path, Esetb.Load(path), minimumPeriod);

        static string FileName(string path)
        {
            string name = System.IO.Path.GetFileName(path);
            foreach (string ext in new[] { ".zs", ".byml", ".esetb" })
                if (name.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                    name = name[..^ext.Length];
            return name;
        }

        /// <summary>Whether a dropped or picked file looks like an esetb.</summary>
        public static bool IsEffectPath(string path) =>
            path != null
            && (
                path.EndsWith(".esetb.byml.zs", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".esetb.byml", StringComparison.OrdinalIgnoreCase)
            );

        public static EffectGaps GapsOf(Emitter e)
        {
            var gaps = EffectGaps.None;
            if (e.CalcType == EmitterCalcType.GpuCompute)
                gaps |= EffectGaps.CpuFallback;
            if (e.FindSub("FCSF") != null)
                gaps |= EffectGaps.CustomField;
            if (e.FindSub("EP01") != null || e.FindSub("EP03") != null)
                gaps |= EffectGaps.Stripes;
            return gaps;
        }

        volatile int _minimumPeriod;

        /// <summary>The shortest loop period, in frames, the background pass classifies with.
        /// Changing it starts the pass over.</summary>
        public int MinimumPeriod
        {
            get => _minimumPeriod;
            set => _minimumPeriod = Math.Max(value, 0);
        }

        /// <summary>The classification, or null while it is still being worked out.</summary>
        public LoopInfo Loop(EmitterSet set, int minimumPeriod) =>
            _loops.GetValueOrDefault((set, minimumPeriod));

        /// <summary>The classification at <see cref="MinimumPeriod"/>.</summary>
        public LoopInfo Loop(EmitterSet set) => Loop(set, _minimumPeriod);

        /// <summary>Replaces a classification the loop check settled.</summary>
        public void Settle(EmitterSet set, int minimumPeriod, LoopInfo info) =>
            _loops[(set, minimumPeriod)] = info;

        /// <summary>Puts a set at the front of the queue.</summary>
        public void Request(EmitterSet set, int minimumPeriod)
        {
            if (!_loops.ContainsKey((set, minimumPeriod)))
                _urgent.Add((set, minimumPeriod));
        }

        /// <summary>Sets whose classification is known, of <see cref="Sets"/>.</summary>
        public int AnalysedCount => _backgroundDone;

        void Work()
        {
            var token = _stop.Token;
            int next = 0;
            int min = _minimumPeriod;
            try
            {
                while (!token.IsCancellationRequested)
                {
                    if (min != _minimumPeriod)
                    {
                        min = _minimumPeriod;
                        next = 0;
                        _backgroundDone = 0;
                    }
                    if (_urgent.TryTake(out var job, next < Sets.Count ? 0 : 250, token))
                    {
                        Analyse(job.Set, job.MinPeriod, token);
                        continue;
                    }
                    if (next < Sets.Count)
                    {
                        Analyse(Sets[next].Set, min, token);
                        _backgroundDone = ++next;
                    }
                }
            }
            catch (OperationCanceledException) { }
        }

        /// <summary>Classifies with the ring capacities counted for the seed the playback loops
        /// with, so a loop run needs no recount.</summary>
        void Analyse(EmitterSet set, int minimumPeriod, CancellationToken token)
        {
            var key = (set, minimumPeriod);
            if (_loops.ContainsKey(key) || _failures.ContainsKey(key))
                return;
            try
            {
                _loops[key] = LoopAnalysis.Analyze(
                    set,
                    minimumPeriod,
                    MeshEmissionPrimitive.From,
                    EmitterProgram.SamplesTexture,
                    EffectPlayback.DefaultSeed,
                    token,
                    EmitterProgram.SamplesTextureInVertex
                );
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _failures[key] = ex.Message;
                _loops[key] = LoopInfo.NotLoopable($"the analysis failed: {ex.Message}");
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _worker.Join(2000);
            _urgent.Dispose();
            _stop.Dispose();
        }
    }
}
