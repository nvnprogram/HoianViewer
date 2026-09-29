using System;
using System.Linq;
using OpenTK;

namespace PlayerViewer.Phive
{
    /// <summary>
    /// A compiled constraint set and its kernel, as the game's solver runs it. A set of a class
    /// the solver has no kernel for compiles to a no-op that keeps its index.
    /// </summary>
    public abstract class ClothConstraint
    {
        public string Name;
        public ConstraintSetKind Kind;

        public abstract void Solve(ClothInstance sim);

        public static ClothConstraint Compile(ConstraintSet set)
        {
            ClothConstraint compiled = set.Kind switch
            {
                ConstraintSetKind.StandardLink => new StandardLinks(Links(set)),
                ConstraintSetKind.StretchLink => new StretchLinks(Links(set)),
                ConstraintSetKind.BendLink => new BendLinks(set),
                ConstraintSetKind.LocalRange => new LocalRange((LocalRangeSet)set),
                ConstraintSetKind.Transition => new Transition((TransitionSet)set),
                ConstraintSetKind.BendStiffness => new BendStiffness((BendStiffnessSet)set),
                _ => new NotRun(),
            };
            compiled.Name = set.Name ?? "";
            compiled.Kind = set.Kind;
            return compiled;
        }

        static Link[] Links(ConstraintSet set) =>
            set
                .Records.Objects.Select(l => new Link
                {
                    A = l.Int("particleA"),
                    B = l.Int("particleB"),
                    RestLength = l.Float("restLength"),
                    Stiffness = l.Float("stiffness"),
                })
                .ToArray();

        public struct Link
        {
            public int A,
                B;
            public float RestLength,
                Stiffness;
        }

        /// <summary>No kernel: compressible links and volume constraints, neither decoded.</summary>
        sealed class NotRun : ClothConstraint
        {
            public override void Solve(ClothInstance sim) { }
        }

        /// <summary>Two sided spring to the rest length: each end takes stiffness times its own inverse mass of the error.</summary>
        sealed class StandardLinks : ClothConstraint
        {
            readonly Link[] _links;

            public StandardLinks(Link[] links) => _links = links;

            public override void Solve(ClothInstance sim)
            {
                var pos = sim.Positions;
                foreach (var link in _links)
                {
                    Vector3 d = pos[link.B] - pos[link.A];
                    float len = d.Length;
                    if (len <= 0)
                        continue;
                    Vector3 corr = d * (link.Stiffness * (len - link.RestLength) / len);
                    pos[link.A] += corr * sim.InvMass(link.A);
                    pos[link.B] -= corr * sim.InvMass(link.B);
                }
            }
        }

        /// <summary>One sided maximum length: only particle B moves, by stiffness times the excess, whatever the masses.</summary>
        sealed class StretchLinks : ClothConstraint
        {
            readonly Link[] _links;

            public StretchLinks(Link[] links) => _links = links;

            public override void Solve(ClothInstance sim)
            {
                var pos = sim.Positions;
                foreach (var link in _links)
                {
                    Vector3 d = pos[link.B] - pos[link.A];
                    float len = d.Length;
                    if (len <= 0)
                        continue;
                    float s = link.Stiffness * Math.Min(link.RestLength - len, 0);
                    pos[link.B] += d * (s / len);
                }
            }
        }

        /// <summary>A [min, max] band on the chord, each side with its own stiffness.</summary>
        sealed class BendLinks : ClothConstraint
        {
            readonly (int A, int B, float Min, float Max, float BendK, float StretchK)[] _links;

            public BendLinks(ConstraintSet set) =>
                _links = set
                    .Records.Objects.Select(l =>
                        (
                            l.Int("particleA"),
                            l.Int("particleB"),
                            l.Float("bendMinLength"),
                            l.Float("stretchMaxLength"),
                            l.Float("bendStiffness"),
                            l.Float("stretchStiffness")
                        )
                    )
                    .ToArray();

            public override void Solve(ClothInstance sim)
            {
                var pos = sim.Positions;
                foreach (var link in _links)
                {
                    Vector3 d = pos[link.B] - pos[link.A];
                    float len = d.Length;
                    if (len <= 0)
                        continue;
                    float s =
                        Math.Max(0, len - link.Max) * link.StretchK
                        - Math.Max(0, link.Min - len) * link.BendK;
                    Vector3 corr = d * (s / len);
                    pos[link.A] += corr * sim.InvMass(link.A);
                    pos[link.B] -= corr * sim.InvMass(link.B);
                }
            }
        }

        /// <summary>
        /// Pull toward the skinned reference by stiffness times the distance outside the
        /// sphere. The per particle variant's stiffness is multiplied by the set's.
        /// </summary>
        internal sealed class LocalRange : ClothConstraint
        {
            readonly (int Particle, int ReferenceVertex, float Radius, float Stiffness)[] _ranges;

            public LocalRange(LocalRangeSet set)
            {
                float setStiffness = set.Stiffness;
                _ranges = set
                    .Records.Objects.Select(l =>
                        (
                            l.Int("particleIndex"),
                            l.Int("referenceVertex"),
                            l.Float("shapeRadius"),
                            l.Has("stiffness") ? setStiffness * l.Float("stiffness") : setStiffness
                        )
                    )
                    .ToArray();
            }

            public (int Particle, int ReferenceVertex, float Radius, float Stiffness)[] Ranges =>
                _ranges;

            public override void Solve(ClothInstance sim)
            {
                var pos = sim.Positions;
                var skinned = sim.Skinned;
                foreach (var range in _ranges)
                {
                    if (sim.IsFixed(range.Particle))
                        continue;
                    Vector3 center = skinned[range.ReferenceVertex];
                    Vector3 d = pos[range.Particle] - center + new Vector3(float.Epsilon);
                    float len = d.Length;
                    if (len <= 0)
                        continue;
                    float s = Math.Min(range.Stiffness * (range.Radius - len), 0);
                    pos[range.Particle] += d * (s / len);
                }
            }
        }

        /// <summary>
        /// The release from the animation pose after a reset: for the transition period each
        /// particle may sit at most (t - delay) / period of its maximum distance from its
        /// reference. Steady state does nothing.
        /// </summary>
        sealed class Transition : ClothConstraint
        {
            readonly float _period;
            readonly (
                int Particle,
                int ReferenceVertex,
                float Delay,
                float MaxDistance
            )[] _particles;

            public Transition(TransitionSet set)
            {
                _period = set.ToSimPeriod;
                _particles = set
                    .Records.Objects.Select(p =>
                        (
                            p.Int("particleIndex"),
                            p.Int("referenceVertex"),
                            p.Float("toSimDelay"),
                            p.Float("toSimMaxDistance")
                        )
                    )
                    .ToArray();
            }

            public override void Solve(ClothInstance sim)
            {
                if (_period <= 0)
                    return;
                var pos = sim.Positions;
                var skinned = sim.Skinned;
                foreach (var t in _particles)
                {
                    if (sim.IsFixed(t.Particle) || t.ReferenceVertex >= skinned.Length)
                        continue;
                    float u = sim.TransitionTime - t.Delay;
                    if (u >= _period)
                        break;
                    Vector3 reference = skinned[t.ReferenceVertex];
                    if (u <= 0)
                    {
                        pos[t.Particle] = reference;
                        continue;
                    }
                    float maxDist = u / _period * t.MaxDistance;
                    Vector3 d = pos[t.Particle] - reference;
                    float len = d.Length;
                    if (len > maxDist && len > 0)
                        pos[t.Particle] = reference + d * (maxDist / len);
                }
            }
        }

        /// <summary>
        /// Linear bending over the four particles around an edge: the weighted sum of positions,
        /// plus the rest curvature in the rest pose variant, applied back by weight and stiffness.
        /// </summary>
        sealed class BendStiffness : ClothConstraint
        {
            readonly bool _useRestPose,
                _clamp;
            readonly float _maxRestHeightSq;
            readonly (
                int[] Particles,
                float[] Weights,
                float Stiffness,
                float RestCurvature
            )[] _links;

            public BendStiffness(BendStiffnessSet set)
            {
                _useRestPose = set.UseRestPoseConfig;
                _clamp = set.ClampBendStiffness;
                _maxRestHeightSq = set.MaxRestPoseHeightSq;
                _links = set
                    .Records.Objects.Select(l =>
                        (
                            new[]
                            {
                                l.Int("particleA"),
                                l.Int("particleB"),
                                l.Int("particleC"),
                                l.Int("particleD"),
                            },
                            new[]
                            {
                                l.Float("weightA"),
                                l.Float("weightB"),
                                l.Float("weightC"),
                                l.Float("weightD"),
                            },
                            l.Float("bendStiffness"),
                            l.Float("restCurvature")
                        )
                    )
                    .ToArray();
            }

            public override void Solve(ClothInstance sim)
            {
                var pos = sim.Positions;
                foreach (var link in _links)
                {
                    int[] ps = link.Particles;
                    float[] w = link.Weights;
                    Vector3 v = Vector3.Zero;
                    for (int i = 0; i < 4; i++)
                        v += pos[ps[i]] * w[i];
                    float k = link.Stiffness;
                    if (_useRestPose)
                    {
                        Vector3 c = pos[ps[2]];
                        Vector3 dc = pos[ps[3]] - c;
                        Vector3 n1 = Vector3.Cross(dc, pos[ps[0]] - c);
                        Vector3 n2 = Vector3.Cross(pos[ps[1]] - c, dc);
                        float e2 = dc.LengthSquared;
                        float l1 = n1.Length,
                            l2 = n2.Length;
                        if (e2 <= 0 || l1 <= 0 || l2 <= 0)
                            continue;
                        float h = l1 * l2 / e2 * link.RestCurvature;
                        if (_clamp && h * h > _maxRestHeightSq)
                            k = 0;
                        Vector3 avg = n1 / l1 + n2 / l2;
                        if (avg.LengthSquared > 0)
                            v += avg.Normalized() * h;
                    }
                    for (int i = 0; i < 4; i++)
                        pos[ps[i]] += v * (sim.InvMass(ps[i]) * w[i] * k);
                }
            }
        }
    }
}
