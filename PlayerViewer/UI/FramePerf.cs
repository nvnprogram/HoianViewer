using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using OpenTK.Graphics.OpenGL;

namespace PlayerViewer.UI
{
    /// <summary>
    /// Named sections of a frame (<see cref="Section"/>), timed by wall clock, the render
    /// thread's own CPU time (from its cycle counter, so time blocked in the driver is not
    /// counted) and GPU timestamps while a run of frames is being measured.
    /// </summary>
    sealed partial class FramePerf
    {
        /// <summary>The run in progress, or null, which makes every section a no-op.</summary>
        public static FramePerf Current { get; private set; }

        readonly Stopwatch _clock = Stopwatch.StartNew();

        //Set once a run's first frame has begun, since its request arrives mid frame.
        bool Begun { get; set; }

        sealed class Stat
        {
            public double Wall,
                Cycles;
            public int Calls;

            //Added up after the swap, when the frame's timestamps are read.
            public double Gpu { get; set; }
            public int GpuCount { get; set; }
        }

        readonly Dictionary<string, Stat> _stats = new();
        readonly List<string> _order = new();

        //Open sections, and this frame's GPU query pairs waiting to be read after the swap.
        readonly Stack<(string Name, double Wall, ulong Cycles, int Query)> _open = new();
        readonly List<(string Name, int Begin, int End)> _pending = new();
        readonly Stack<int> _freeQueries = new();

        [DllImport("kernel32.dll")]
        static extern bool QueryThreadCycleTime(IntPtr thread, out ulong cycles);

        [DllImport("kernel32.dll")]
        static extern IntPtr GetCurrentThread();

        static ulong Cycles()
        {
            QueryThreadCycleTime(GetCurrentThread(), out ulong c);
            return c;
        }

        int Query()
        {
            if (_freeQueries.Count > 0)
                return _freeQueries.Pop();
            GL.GenQueries(1, out int q);
            return q;
        }

        public readonly struct Scope : IDisposable
        {
            readonly FramePerf _perf;

            public Scope(FramePerf perf) => _perf = perf;

            public void Dispose() => _perf?.End();
        }

        /// <summary>Times the code up to the scope's end under <paramref name="name"/>. Sections nest.</summary>
        public static Scope Section(string name, bool gpu = true)
        {
            var perf = Current;
            if (perf == null || !perf.Begun)
                return default;
            int q = 0;
            if (gpu)
            {
                q = perf.Query();
                GL.QueryCounter(q, QueryCounterTarget.Timestamp);
            }
            perf._open.Push((name, perf._clock.Elapsed.TotalMilliseconds, Cycles(), q));
            return new Scope(perf);
        }

        void End()
        {
            if (_open.Count == 0)
                return;
            var (name, wall, cycles, q) = _open.Pop();
            if (!_stats.TryGetValue(name, out var s))
            {
                _stats[name] = s = new Stat();
                _order.Add(name);
            }
            s.Wall += _clock.Elapsed.TotalMilliseconds - wall;
            s.Cycles += Cycles() - cycles;
            s.Calls++;
            if (q != 0)
            {
                int e = Query();
                GL.QueryCounter(e, QueryCounterTarget.Timestamp);
                _pending.Add((name, q, e));
            }
        }
    }
}
