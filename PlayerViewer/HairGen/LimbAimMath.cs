using System.Collections.Generic;
using System.Linq;
using OpenTK;

namespace PlayerViewer.HairGen
{
    /// <summary>A frame a dragged aim snaps to when within <see cref="Degrees"/> of it.</summary>
    public sealed record AimSnap(string Name, LimbAim Frame, float Degrees);

    /// <summary>The frames a painted limb's aim is offered: the snaps of a drag and the mirror.</summary>
    public static class LimbAimMath
    {
        public const float SnapDegrees = 8;
        public const float ParallelDegrees = 5;

        /// <summary>
        /// The frames a drag of this limb snaps to: its twin's mirrored, or on the centre plane
        /// when it is its own twin, its traced frame, and the other limbs' and their mirrors.
        /// </summary>
        public static List<AimSnap> Snaps(HairGenerator gen, PaintedLimb limb, LimbAim current)
        {
            var snaps = new List<AimSnap>();
            var twin = gen.MirrorOf(limb);
            if (twin == limb)
            {
                var level = new Vector3(0, current.Direction.Y, current.Direction.Z);
                if (level.LengthSquared > 1e-8f)
                {
                    var d = level.Normalized();
                    snaps.Add(
                        new AimSnap(
                            "centred, flat across",
                            current with
                            {
                                Direction = d,
                                Side = Vector3.UnitX,
                            },
                            SnapDegrees
                        )
                    );
                    snaps.Add(
                        new AimSnap(
                            "centred, on edge",
                            current with
                            {
                                Direction = d,
                                Side = Vector3.Cross(d, Vector3.UnitX).Normalized(),
                            },
                            SnapDegrees
                        )
                    );
                }
            }
            else if (twin != null && gen.AimOf(twin) is LimbAim t)
                snaps.Add(new AimSnap($"mirror of {twin.Name}", t.Mirrored(), SnapDegrees));
            if (limb.Traced is LimbAim traced && gen.ChainOf(limb) is int c && c >= 0)
            {
                var chain = gen.Rig.Chains[c];
                snaps.Add(
                    new AimSnap(
                        "traced from the paint",
                        traced with
                        {
                            Side = HairRigger.SideAxis(traced.Root, traced.Direction, chain.Hub),
                        },
                        SnapDegrees
                    )
                );
            }
            foreach (var other in gen.Limbs.Where(l => l != limb && l != twin))
                if (gen.AimOf(other) is LimbAim o)
                {
                    snaps.Add(new AimSnap($"parallel to {other.Name}", o, ParallelDegrees));
                    snaps.Add(
                        new AimSnap($"mirror of {other.Name}", o.Mirrored(), ParallelDegrees)
                    );
                }
            return snaps;
        }

        /// <summary>
        /// The frame the Mirror button gives: the twin's mirrored, built straight unless the twin
        /// follows a curved traced line, or for a limb that is its own twin, square to the centre
        /// plane and flat across it.
        /// </summary>
        public static LimbAim Mirrored(
            HairGenerator gen,
            PaintedLimb limb,
            PaintedLimb twin,
            LimbAim built
        )
        {
            if (twin == null || built == null)
                return null;
            if (twin != limb)
            {
                if (gen.AimOf(twin)?.Mirrored() is not LimbAim mirrored)
                    return null;
                return mirrored.Straight || !Curved(gen, twin)
                    ? mirrored with
                    {
                        Straight = true,
                    }
                    : mirrored;
            }
            var level = new Vector3(0, built.Direction.Y, built.Direction.Z);
            if (level.LengthSquared < 1e-8f)
                return null;
            return built with
            {
                Root = built.Straight ? new Vector3(0, built.Root.Y, built.Root.Z) : built.Root,
                Direction = level.Normalized(),
                Side = Vector3.UnitX,
                Straight = true,
            };
        }

        /// <summary>Whether a limb's chain has bones enough to bend and its traced line leaves its chord by a fifth of its length.</summary>
        public static bool Curved(HairGenerator gen, PaintedLimb limb)
        {
            int c = gen.ChainOf(limb);
            if (c < 0 || gen.Rig.Chains[c].Bones.Length < 3)
                return false;
            var path = gen.Rig.Chains[c].Path;
            var chord = path[^1] - path[0];
            float length = chord.Length;
            if (length < 1e-4f)
                return false;
            chord /= length;
            return path.Any(p =>
            {
                var off = p - path[0];
                return (off - chord * Vector3.Dot(off, chord)).Length > 0.2f * length;
            });
        }
    }
}
