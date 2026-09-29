using System;
using System.Collections.Generic;
using System.Numerics;
using EffectLibrary;

namespace PlayerViewer.Effects.Sim
{
    /// <summary>
    /// A playing emitter set: its matrix, the runtime's per set scales and colour, and its
    /// emitters. Calculates them in the runtime's order, each parent before its children.
    /// </summary>
    public sealed class EmitterSetInstance
    {
        public EffectSimulation System { get; }
        public EmitterSet Resource { get; }

        public Matrix34 WorldSrt = Matrix34.Identity;
        public Matrix34 WorldRt = Matrix34.Identity;

        /// <summary>The lengths of the set matrix's axes.</summary>
        public Vector3 MatrixScale = Vector3.One;

        /// <summary>A multiplier on particle scale the host sets; with the matrix scale it gives <see cref="ParticleScaleForCalc"/>.</summary>
        public Vector3 ParticleScale = Vector3.One;
        public Vector3 ParticleScaleForCalc = Vector3.One;

        public Vector4 Color = Vector4.One;
        public float EmissionRatioScale = 1;
        public float EmissionIntervalScale = 1;
        public float ParticleLifeScale = 1;
        public float AllDirectionVelocityScale = 1;
        public float RandomVelocityScale = 1;
        public float DirectionalVelocityScale = 1;
        public Vector3 AdditionalVelocity;
        public Vector3 VolumeScale = Vector3.One;
        public Vector3 InitRotate;
        public Vector3 BirthScale = Vector3.One;

        /// <summary>The seed emitters of the set seed type share.</summary>
        public uint RandomSeed;

        /// <summary>Set by <see cref="Fade"/>: emitters fade out and stop emitting.</summary>
        public bool Fading { get; private set; }
        public bool KillRequested { get; private set; }

        public float FrameRate { get; private set; }
        public float Time { get; private set; }

        /// <summary>Top level emitters in creation order; each holds its children.</summary>
        public List<EmitterInstance> Emitters { get; } = new();

        internal int EmitterCounter;

        public EmitterSetInstance(EffectSimulation system, EmitterSet resource)
        {
            System = system;
            Resource = resource;
        }

        public bool Alive => Emitters.Count > 0 && !KillRequested;

        /// <summary>EmitterSet::SetMatrix: the RT part is the matrix with its axes normalised.</summary>
        public void SetMatrix(in Matrix34 m)
        {
            WorldSrt = m;
            MatrixScale = new Vector3(
                VfxMath.Length(m.R0),
                VfxMath.Length(m.R1),
                VfxMath.Length(m.R2)
            );
            WorldRt = new Matrix34
            {
                R0 = MatrixScale.X > 0 ? m.R0 * (1f / MatrixScale.X) : Vector3.Zero,
                R1 = MatrixScale.Y > 0 ? m.R1 * (1f / MatrixScale.Y) : Vector3.Zero,
                R2 = MatrixScale.Z > 0 ? m.R2 * (1f / MatrixScale.Z) : Vector3.Zero,
                T = m.T,
            };
            ParticleScaleForCalc = MatrixScale * ParticleScale;
        }

        internal void Initialize()
        {
            RandomSeed = System.Global.Next();
            if (System.SetSeedOverride is uint seed)
                RandomSeed = seed;
            foreach (var res in Resource.Emitters)
            {
                var def = System.GetDef(res);
                var e = new EmitterInstance(this, def, null, -1);
                Emitters.Add(e);
                for (int i = 0; i < def.Children.Length; i++)
                {
                    var cd = def.Children[i];
                    if (cd.EmitterPerParticle)
                        continue;
                    var child = new EmitterInstance(this, cd, e, -1);
                    child.AnchorLife = e.Def.MaxLife;
                    e.ChildLists[i].Add(child);
                    e.LightChildren[i] = child;
                }
            }
        }

        /// <summary>Fades the set out: emission stops where the emitter asks, fades run, then it dies.</summary>
        public void Fade() => Fading = true;

        /// <summary>Kills the set at the start of the next frame.</summary>
        public void Kill() => KillRequested = true;

        readonly Dictionary<EmitterInstance, EmitterInstance.LoopState> _loopStates = new();

        /// <summary>
        /// Loop mode: at the start of the periodic regime the emitter level state is kept, and
        /// at every period boundary after it is put back, so each period emits the same.
        /// </summary>
        void LoopBoundary()
        {
            int n = System.LoopPeriod;
            if (System.Mode != RandomMode.Loop || n <= 0 || Time < System.LoopStart)
                return;
            float k = (Time - System.LoopStart) / n;
            if (k != MathF.Floor(k))
                return;
            foreach (var e in AllEmitters())
            {
                if (e.Def.EmitterPerParticle && e.IsChild)
                    continue;
                if (k == 0 || !_loopStates.ContainsKey(e))
                    _loopStates[e] = e.SaveLoopState();
                else
                    e.RestoreLoopState(_loopStates[e]);
            }
        }

        internal void Calculate(float frameRate)
        {
            if (frameRate > 0)
                LoopBoundary();
            FrameRate = frameRate;
            foreach (var e in Emitters.ToArray())
            {
                bool kill = e.CalculateInSet(frameRate, Fading);
                e.HasLiveChildren = false;
                for (int i = 0; i < e.ChildLists.Length; i++)
                {
                    foreach (var c in e.ChildLists[i].ToArray())
                    {
                        c.ParentKilled = kill;
                        if (c.CalculateInSet(frameRate, Fading))
                        {
                            e.ChildLists[i].Remove(c);
                            c.DetachFromParent();
                        }
                        else
                            e.HasLiveChildren = true;
                    }
                }
                if (kill && !e.HasLiveChildren)
                    Emitters.Remove(e);
            }
            Time += frameRate;
        }

        /// <summary>Every emitter, each parent before its children, in the order the game draws them.</summary>
        public IEnumerable<EmitterInstance> AllEmitters()
        {
            foreach (var e in Emitters)
            {
                yield return e;
                foreach (var list in e.ChildLists)
                foreach (var c in list)
                    yield return c;
            }
        }
    }
}
