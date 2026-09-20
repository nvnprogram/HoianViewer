using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using ShaderBundler;

namespace PlayerViewer.Shaders
{
    public sealed class CompileRequest
    {
        /// <summary>The job identity, shared by every cell that would splice identically.</summary>
        public string Key;
        public string Label;
        public SpliceKey Splice;
        public OptionVector Vector;

        /// <summary>
        /// Run the specialiser --quick run for a quick preview.
        /// </summary>
        public bool Preview;
    }

    public sealed class CompileOutcome
    {
        public string Key;
        public string Label;
        public bool Success;
        public string Message;
        public double Seconds;
        public bool Preview;

        /// <summary>Both stages were already in the cache, so the tool never ran.</summary>
        public bool FromCache;
    }

    /// <summary>
    /// Runs specialisations off the render thread.
    /// </summary>
    public sealed class CompileScheduler : IDisposable
    {
        static readonly ShaderStage[] Stages = { ShaderStage.Vertex, ShaderStage.Fragment };

        readonly UberContext _uber;
        readonly object _gate = new();
        readonly List<CompileRequest> _demand = new();
        readonly HashSet<string> _claimed = new(StringComparer.Ordinal);
        readonly List<CompileRequest> _running = new();
        readonly HashSet<string> _finished = new(StringComparer.Ordinal);
        readonly List<string> _active = new();
        readonly ConcurrentQueue<CompileOutcome> _outcomes = new();
        readonly Thread[] _threads;
        readonly string _workRoot;
        readonly CancellationTokenSource _cancel = new();
        bool _stop;

        static readonly int Workers = Math.Clamp(Environment.ProcessorCount - 2, 1, 6);

        //Two threads on top of the pool that only take previews, since a preview needs both
        //drawn passes and they are independent.
        const int PreviewWorkers = 2;

        //One runner for every worker, so they share the options table it writes.
        readonly object _runnerGate = new();
        UberspecRunner _runner;

        UberspecRunner Runner()
        {
            lock (_runnerGate)
                return _runner ??= new UberspecRunner(_uber.SpecialiserPath, _workRoot);
        }

        public CompileScheduler(UberContext uber)
        {
            _uber = uber ?? throw new ArgumentNullException(nameof(uber));
            _workRoot = Path.Combine(Path.GetTempPath(), "PlayerViewerSplice");
            UberspecRunner.SweepWorkRoot(_workRoot);

            _threads = new Thread[Workers + PreviewWorkers];
            for (int i = 0; i < Workers; i++)
            {
                _threads[i] = new Thread(() => Work(false))
                {
                    IsBackground = true,
                    Name = "shader splice " + i,
                };
                _threads[i].Start();
            }
            for (int i = 0; i < PreviewWorkers; i++)
            {
                _threads[Workers + i] = new Thread(() => Work(true))
                {
                    IsBackground = true,
                    Name = "shader splice preview " + i,
                };
                _threads[Workers + i].Start();
            }
        }

        public int Queued
        {
            get
            {
                lock (_gate)
                {
                    int n = 0;
                    foreach (var r in _demand)
                        if (!_claimed.Contains(r.Key) && !_finished.Contains(r.Key))
                            n++;
                    return n;
                }
            }
        }

        public string[] Active
        {
            get
            {
                lock (_gate)
                    return _active.ToArray();
            }
        }

        /// <summary>Running and queued, split by lane, for a one line status.</summary>
        public (int Running, int Queued, int PreviewRunning, int PreviewQueued) Counts()
        {
            lock (_gate)
            {
                int r = 0,
                    q = 0,
                    pr = 0,
                    pq = 0;
                foreach (var x in _running)
                {
                    if (x.Preview)
                        pr++;
                    else
                        r++;
                }
                foreach (var d in _demand)
                {
                    if (_claimed.Contains(d.Key) || _finished.Contains(d.Key))
                        continue;
                    if (d.Preview)
                        pq++;
                    else
                        q++;
                }
                return (r, q, pr, pq);
            }
        }

        /// <summary>
        /// The keys a worker is inside right now, as a snapshot. The caller marks a whole
        /// demand list every frame and the list is the whole model, so asking per key would
        /// be thousands of lock acquisitions a frame.
        /// </summary>
        public HashSet<string> ClaimedKeys()
        {
            lock (_gate)
                return new HashSet<string>(_claimed, StringComparer.Ordinal);
        }

        /// <summary>Replaces the whole queue, highest priority first.</summary>
        public void SetDemand(IReadOnlyList<CompileRequest> demand)
        {
            lock (_gate)
            {
                _demand.Clear();
                _demand.AddRange(demand);
                Monitor.PulseAll(_gate);
            }
        }

        /// <summary>Lets a key be run again, for a retry after a failure.</summary>
        public void Forget(string key)
        {
            lock (_gate)
                _finished.Remove(key);
        }

        public bool TryTakeOutcome(out CompileOutcome outcome) => _outcomes.TryDequeue(out outcome);

        void Work(bool previewLane)
        {
            while (true)
            {
                CompileRequest req = null;
                lock (_gate)
                {
                    while (!_stop && (req = Next(previewLane)) == null)
                        Monitor.Wait(_gate);
                    if (_stop)
                        return;
                    _claimed.Add(req.Key);
                    _active.Add(req.Label);
                    _running.Add(req);
                }

                var outcome = Run(req);

                lock (_gate)
                {
                    _claimed.Remove(req.Key);
                    _active.Remove(req.Label);
                    _running.Remove(req);
                    _finished.Add(req.Key);
                    _demand.RemoveAll(x => x.Key == req.Key);
                    Monitor.PulseAll(_gate);
                }
                _outcomes.Enqueue(outcome);
            }
        }

        //Caller holds the lock. The finished set is what stops a job being picked up again
        //in the frame between it completing and the caller rebuilding the demand list.
        CompileRequest Next(bool previewLane)
        {
            if (!previewLane && PreviewOutstanding())
                return null;

            foreach (var r in _demand)
                if (
                    r.Preview == previewLane
                    && !_claimed.Contains(r.Key)
                    && !_finished.Contains(r.Key)
                )
                    return r;
            return null;
        }

        //Caller holds the lock.
        bool PreviewOutstanding()
        {
            foreach (var r in _running)
                if (r.Preview)
                    return true;
            foreach (var r in _demand)
                if (r.Preview && !_claimed.Contains(r.Key) && !_finished.Contains(r.Key))
                    return true;
            return false;
        }

        CompileOutcome Run(CompileRequest req)
        {
            var outcome = new CompileOutcome
            {
                Key = req.Key,
                Label = req.Label,
                Preview = req.Preview,
                FromCache = true,
            };
            var watch = Stopwatch.StartNew();
            SpliceTrace.Log($"claim {req.Label}");
            try
            {
                var selection = _uber.Resolve(req.Splice.Pass, req.Splice.Weight.ToString());
                SpliceTrace.Log($"resolved {req.Label}");
                var runner = Runner();

                foreach (var stage in Stages)
                {
                    string key = req.Splice.Cache(stage);
                    if (_uber.Cache.Has(key, req.Preview))
                        continue;

                    outcome.FromCache = false;
                    SpliceTrace.Log($"spec start {req.Label} {stage}");
                    var result = runner.Generate(
                        new UberspecRequest
                        {
                            Uber = selection.Stage(stage),
                            OptionBank = selection.OptionBank(stage),
                            OptionsTableJson = _uber.TableJson,
                            OptionsFile = req.Vector.ToOptionsFile(),
                            Label = $"{req.Label} {stage}",
                            Quick = req.Preview,
                        },
                        _cancel.Token
                    );
                    _uber.Cache.Put(key, result.Binary, req.Preview);
                    SpliceTrace.Log($"spec done {req.Label} {stage}");
                }
                outcome.Success = true;
            }
            catch (OperationCanceledException)
            {
                outcome.Success = false;
                outcome.Message = "cancelled";
            }
            catch (Exception ex)
            {
                outcome.Success = false;
                outcome.Message = FirstLine(ex.Message);
                Console.WriteLine($"[Splice] {req.Label} failed: {ex.Message}");
            }
            outcome.Seconds = watch.Elapsed.TotalSeconds;
            return outcome;
        }

        //A gate failure carries the tool's whole stdout, which is not a UI string.
        static string FirstLine(string text)
        {
            if (string.IsNullOrEmpty(text))
                return "failed";
            int end = text.IndexOf('\n');
            string line = (end < 0 ? text : text.Substring(0, end)).TrimEnd('\r');
            return line.Length <= 200 ? line : line.Substring(0, 200) + "...";
        }

        /// <summary>Stops the workers and kills the specialiser processes they are inside.</summary>
        public void Dispose()
        {
            lock (_gate)
            {
                _stop = true;
                _demand.Clear();
                Monitor.PulseAll(_gate);
            }
            _cancel.Cancel();
            foreach (var t in _threads)
                t.Join(2000);
            _cancel.Dispose();
        }
    }
}
