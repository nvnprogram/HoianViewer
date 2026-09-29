using System;
using System.Diagnostics;

namespace PlayerViewer.Shaders
{
    /// <summary>
    /// Timestamps the path from a material edit to the splice reaching the screen. Off except in
    /// a local debug build, because it prints per stage and per splice.
    ///
    /// Every line is measured from the edit that started the round, so the numbers read as a
    /// latency budget rather than as durations to be added up.
    /// </summary>
    public static partial class SpliceTrace
    {
        public static readonly bool Enabled = ReadSwitch();

        static bool ReadSwitch()
        {
            bool on = false;
            Switch(ref on);
            return on;
        }

        static partial void Switch(ref bool on);

        static long _edit;

        public static void Edit(double settleMs)
        {
            if (!Enabled)
                return;
            _edit = Stopwatch.GetTimestamp();
            Console.WriteLine($"[SpliceT] 0.0ms edit (settle {settleMs:0}ms)");
        }

        public static void Log(string what)
        {
            if (!Enabled || _edit == 0)
                return;
            Console.WriteLine(
                $"[SpliceT] {Stopwatch.GetElapsedTime(_edit).TotalMilliseconds:0.0}ms {what}"
            );
        }

        /// <summary>A line that is not part of an edit's timeline, printed only under the
        /// same switch.</summary>
        public static void Note(string what)
        {
            if (Enabled)
                Console.WriteLine("[Splice] " + what);
        }
    }
}
