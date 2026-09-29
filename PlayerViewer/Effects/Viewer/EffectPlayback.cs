using System;
using System.Collections.Generic;
using System.Numerics;
using EffectLibrary;
using PlayerViewer.Effects.Sim;

namespace PlayerViewer.Effects.Viewer
{
    /// <summary>
    /// One emitter set playing in the viewer. The simulation steps whole game frames at 60 Hz,
    /// so every frame is reproducible: seeking backwards resumes from the nearest snapshot
    /// taken on the way, or replays from the start with the same seed. Frame f is the state
    /// after f + 1 calculations, the convention of <see cref="LoopAnalysis"/>, so a periodic
    /// set's frame Start and Start + Period draw the same.
    /// </summary>
    public sealed class EffectPlayback
    {
        /// <summary>Frames the sim may catch up on in one call before it drops time instead.</summary>
        const int MaxStepsPerUpdate = 4;

        /// <summary>Periods a looping periodic set plays before it is replayed from its clip start,
        /// so the simulation clock never grows large enough to lose float precision.</summary>
        const int PeriodsBeforeRewind = 64;

        public EmitterSet Set { get; private set; }
        public EffectSimulation Sim { get; private set; }
        EmitterSet _simSet;
        public EmitterSetInstance Instance { get; private set; }

        /// <summary>Resolves the mesh of a primitive volume emitter.</summary>
        public Func<Emitter, ulong, IEmissionPrimitive> Primitives { get; set; } =
            MeshEmissionPrimitive.From;

        /// <summary>The frame on screen, counted from the set's creation.</summary>
        public int Frame { get; private set; } = -1;

        /// <summary>The seed every run starts from, so a set plays the same each time. The
        /// background loop analysis counts its ring capacities for it.</summary>
        public const uint DefaultSeed = 0;

        public RandomMode Mode { get; private set; } = RandomMode.Game;
        public uint Seed { get; private set; } = DefaultSeed;

        /// <summary>The set's loop classification with the period the playback uses, or null
        /// while it is not known.</summary>
        public LoopInfo Loop { get; private set; }

        public bool Playing = true;
        public float Speed = 1;

        /// <summary>The timeline's length for a set that neither ends nor loops.</summary>
        public int FallbackLength = 300;

        /// <summary>Set by <see cref="Fade"/> until the next restart.</summary>
        public bool Ending { get; private set; }

        /// <summary>The memory snapshots may hold before they thin out.</summary>
        const long SnapshotBudget = 256L << 20;

        /// <summary>Snapshots of this run by frame, taken every <see cref="_snapshotEvery"/> frames.</summary>
        readonly List<SimSnapshot> _snapshots = new();
        int _snapshotEvery = 15;
        long _snapshotBytes;

        /// <summary>The set's translation. Changing the transform drops the snapshots, which a
        /// replay with it would no longer pass through.</summary>
        public Vector3 Translation
        {
            get => _translation;
            set => SetTransform(ref _translation, value);
        }

        /// <summary>Degrees, applied x then y then z.</summary>
        public Vector3 RotationDegrees
        {
            get => _rotation;
            set => SetTransform(ref _rotation, value);
        }

        public Vector3 Scale
        {
            get => _scale;
            set => SetTransform(ref _scale, value);
        }

        Vector3 _translation,
            _rotation,
            _scale = Vector3.One;

        void SetTransform(ref Vector3 field, Vector3 value)
        {
            if (field != value)
                ClearSnapshots();
            field = value;
        }

        /// <summary>Loads nothing on its own; the caller hands the loop info in once it is known.</summary>
        public void Select(EmitterSet set, LoopInfo loop)
        {
            ClearSnapshots();
            Set = set;
            Loop = loop;
            Ending = false;
            Restart();
        }

        /// <summary>A new classification for the current set, applied from a fresh start.</summary>
        public void SetLoop(LoopInfo loop)
        {
            ClearSnapshots();
            Loop = loop;
            if (Set != null)
                Replay(Math.Max(Frame, 0));
        }

        LoopInfo _seeded,
            _seededFrom;

        /// <summary><see cref="Loop"/> with its ring capacities for <see cref="Seed"/>, which
        /// takes a dry run of the set only when they were counted for another seed.</summary>
        LoopInfo Seeded()
        {
            if (!ReferenceEquals(_seededFrom, Loop) || _seeded.Seed != Seed)
            {
                if (Loop.Kind == LoopKind.Periodic && Loop.Seed != Seed)
                    Console.WriteLine(
                        $"[Effect] Recounting {Set.Name}'s loop rings for seed {Seed}"
                    );
                _seeded = LoopAnalysis.Reseed(Set, Loop, Seed, Primitives);
                _seededFrom = Loop;
            }
            return _seeded;
        }

        /// <summary>Switches the randomness and replays to <paramref name="frame"/>, by default
        /// the frame on screen.</summary>
        public void SetRandom(RandomMode mode, uint seed, int? frame = null)
        {
            if (mode == Mode && seed == Seed)
                return;
            Mode = mode;
            Seed = seed;
            ClearSnapshots();
            if (Set != null)
                Replay(Math.Max(frame ?? Frame, 0));
        }

        public Matrix34 SetMatrix =>
            Matrix34.CreateSrtXyz(Scale, RotationDegrees * (MathF.PI / 180f), Translation);

        /// <summary>
        /// Applies the transform to the playing set; the next frames use it. A replay starts
        /// with it, so the snapshots of the run so far no longer match one.
        /// </summary>
        public void ApplyMatrix()
        {
            ClearSnapshots();
            Instance?.SetMatrix(SetMatrix);
        }

        /// <summary>Plays the set from its creation: frame 0.</summary>
        public void Restart()
        {
            Ending = false;
            Instance = null;
            Frame = -1;
            if (Set == null)
            {
                Sim = null;
                return;
            }
            //Kept across restarts of one set for its parsed emitters; everything a run changes
            //is put back here, so a replay draws exactly the same.
            if (Sim == null || _simSet != Set)
            {
                Sim = new EffectSimulation { PrimitiveSource = Primitives };
                _simSet = Set;
            }
            Sim.Sets.Clear();
            Sim.LoopCapacities.Clear();
            Sim.Mode = RandomMode.Game;
            Sim.LoopPeriod = 0;
            Sim.LoopStart = 0;
            Sim.Global.SetSeed(Seed);
            if (Mode == RandomMode.Loop)
            {
                if (Loop is { Kind: LoopKind.Periodic })
                    LoopAnalysis.Apply(Sim, Set, Seeded());
                else
                    Sim.Mode = RandomMode.Loop;
                Sim.LoopSeed = Seed;
            }
            try
            {
                Error = null;
                Instance = Sim.Play(Set, SetMatrix);
                Step();
            }
            catch (Exception ex)
            {
                Fail(ex);
            }
        }

        /// <summary>Why the simulation stopped, or null. A set that throws stays on its last frame.</summary>
        public string Error { get; private set; }

        void Fail(Exception ex)
        {
            Error = ex.Message;
            Console.WriteLine($"[Effect] {Set?.Name} stopped simulating: {ex}");
            Sim?.Sets.Clear();
            Instance = null;
        }

        void Step()
        {
            if (Instance == null)
                return;
            try
            {
                Sim.Calculate(1);
            }
            catch (Exception ex)
            {
                Fail(ex);
                return;
            }
            Frame++;
            if (Frame > 0 && Frame % _snapshotEvery == 0 && !Ending)
                TakeSnapshot();
        }

        void TakeSnapshot()
        {
            if (_snapshots.Count > 0 && _snapshots[^1].Frame >= Frame)
                return;
            SimSnapshot snap;
            try
            {
                snap = SimSnapshot.Take(Sim, Instance, Frame);
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"[Effect] snapshot failed, scrubbing replays instead: {ex.Message}"
                );
                _snapshotEvery = int.MaxValue;
                return;
            }
            _snapshots.Add(snap);
            _snapshotBytes += snap.Bytes;
            //Over budget: keep every other snapshot and take them half as often.
            while (_snapshotBytes > SnapshotBudget && _snapshots.Count > 1)
            {
                _snapshotEvery *= 2;
                _snapshotBytes = 0;
                _snapshots.RemoveAll(x => x.Frame % _snapshotEvery != 0);
                foreach (var x in _snapshots)
                    _snapshotBytes += x.Bytes;
            }
        }

        void ClearSnapshots()
        {
            _snapshots.Clear();
            _snapshotBytes = 0;
            _snapshotEvery = 15;
        }

        /// <summary>The latest snapshot at or before <paramref name="frame"/>, or null.</summary>
        SimSnapshot SnapshotAt(int frame)
        {
            SimSnapshot best = null;
            foreach (var x in _snapshots)
                if (x.Frame <= frame && (best == null || x.Frame > best.Frame))
                    best = x;
            return best;
        }

        /// <summary>Continues from a snapshot; false when there is none worth taking.</summary>
        bool Resume(int frame)
        {
            var snap = SnapshotAt(frame);
            if (
                snap == null
                || (!Ending && Instance != null && snap.Frame <= Frame && Frame <= frame)
            )
                return false;
            try
            {
                (Sim, Instance) = snap.Resume();
            }
            catch (Exception ex)
            {
                Fail(ex);
                return false;
            }
            Frame = snap.Frame;
            Error = null;
            Ending = false;
            return true;
        }

        /// <summary>Whether the set is still alive, or has particles left to show.</summary>
        public bool Alive => Instance != null && Instance.Alive;

        /// <summary>
        /// Moves to <paramref name="frame"/>: forwards by stepping, backwards by replaying from
        /// the start. Deterministic either way.
        /// </summary>
        public void Seek(int frame)
        {
            frame = Math.Max(frame, 0);
            if (Set == null)
                return;
            if (!Resume(frame) && (Sim == null || frame < Frame || Ending))
                Restart();
            while (Frame < frame && Instance != null)
                Step();
        }

        void Replay(int frame)
        {
            if (!Resume(frame))
                Restart();
            while (Frame < frame && Instance != null)
                Step();
        }

        double _accum;

        /// <summary>Advances by wall time <paramref name="seconds"/> at the playback speed.</summary>
        public void Update(double seconds)
        {
            if (Set == null || !Playing)
            {
                _accum = 0;
                return;
            }
            _accum += seconds * 60 * Speed;
            int steps = 0;
            while (_accum >= 1 && steps < MaxStepsPerUpdate)
            {
                _accum -= 1;
                steps++;
                Advance();
            }
            if (_accum >= 1)
                _accum = 0;
        }

        /// <summary>
        /// One frame forward. A set plays as it would in game and restarts once it is over; a
        /// periodic set in loop randomness wraps its clip instead.
        /// </summary>
        public void Advance()
        {
            if (Set == null || Error != null)
                return;
            if (Sim == null)
            {
                Restart();
                return;
            }
            if (ShouldRestart())
            {
                Restart();
                return;
            }
            if (
                Loop is { Kind: LoopKind.Periodic } l
                && Mode == RandomMode.Loop
                && !Ending
                && Frame + 1 >= l.Start + l.Period * PeriodsBeforeRewind
            )
            {
                Replay(l.Start + (Frame + 1 - l.Start) % l.Period);
                return;
            }
            Step();
        }

        bool ShouldRestart() =>
            Loop?.Kind == LoopKind.OneShot && !Ending
                ? Frame + 1 >= Math.Max(Loop.Length, 1)
                : !Alive;

        /// <summary>
        /// The frame as the timeline shows it: inside a periodic clip it wraps back to the clip
        /// start, since the draws there repeat.
        /// </summary>
        public int DisplayFrame =>
            Loop is { Kind: LoopKind.Periodic } l && Mode == RandomMode.Loop && Frame >= l.Start
                ? l.Start + (Frame - l.Start) % l.Period
                : Frame;

        /// <summary>
        /// Off, draws show the emitter's own time past the first period, as the set would look if
        /// it kept running instead of wrapping.
        /// </summary>
        public bool RebaseTime = true;

        /// <summary>Off, infinite life particles show their own age past the first period.</summary>
        public bool RebaseAges = true;

        float LoopOffset =>
            Loop != null && Mode == RandomMode.Loop ? LoopAnalysis.TimeOffset(Loop, Frame) : 0;

        /// <summary>The time taken off every draw so each period draws the same inputs.</summary>
        public float TimeOffset => RebaseTime ? LoopOffset : 0;

        /// <summary>The age taken off infinite life particles, with <see cref="TimeOffset"/>.</summary>
        public float AgeOffset => RebaseAges ? LoopOffset : 0;

        /// <summary>Frames the timeline spans: the set's length, or its fallback.</summary>
        public int TimelineLength =>
            Loop?.Kind switch
            {
                LoopKind.OneShot => Math.Max(Loop.Length, 1),
                LoopKind.Periodic => Loop.Start + Loop.Period,
                _ => FallbackLength,
            };

        /// <summary>
        /// The first frame of a timeline <paramref name="span"/> frames wide that starts at
        /// <paramref name="current"/> now. It stays put while the frame on screen is inside it;
        /// otherwise it moves in half span steps to put the playhead in its second half.
        /// </summary>
        public int TimelineStart(int span, int current)
        {
            int shown = Math.Max(DisplayFrame, 0);
            if (shown >= current && shown < current + span)
                return current;
            int half = Math.Max(span / 2, 1);
            return shown < span ? 0 : (shown / half - 1) * half;
        }

        /// <summary>Emitters fade out and stop emitting, the way the game ends a looping effect.</summary>
        public void Fade()
        {
            if (Instance == null)
                return;
            Instance.Fade();
            Ending = true;
        }

        public void Clear()
        {
            ClearSnapshots();
            Set = null;
            Sim = null;
            _simSet = null;
            Instance = null;
            Loop = null;
            Frame = -1;
            Ending = false;
        }
    }
}
