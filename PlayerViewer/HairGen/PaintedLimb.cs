using System;
using System.Collections.Generic;
using OpenTK;

namespace PlayerViewer.HairGen
{
    /// <summary>
    /// A limb of the model and how it moves: painted mesh nodes with bones built inside them, or
    /// chains of the model's own bones.
    /// </summary>
    public class PaintedLimb
    {
        public string Name;

        /// <summary>Nodes of <see cref="HairGenerator.Graph"/> the limb covers; empty for a bone limb.</summary>
        public HashSet<int> Nodes = new();

        /// <summary>Bone names of each chain, root to tip; null for a painted limb.</summary>
        public List<string[]> BoneChains;

        public bool IsBoneLimb => BoneChains != null;

        /// <summary>Null until the first build suggests one.</summary>
        public StrandStyle Style;

        /// <summary>Fixes the part lying on the head instead of simulating it. Painted hair limbs only, off unless the user turns it on.</summary>
        public bool HoldOnHead;

        /// <summary>Keeps its own cloth piece when close limbs are merged.</summary>
        public bool OwnPiece;

        /// <summary>What the last build found wrong, or null.</summary>
        public string Problem;

        /// <summary>Model bones the last build removed in favour of this limb's.</summary>
        public List<string> Replaced = new();

        /// <summary>Bones replaced before the save its metadata came from.</summary>
        public List<string> ReplacedEarlier = new();

        /// <summary>The bone a non-hair limb hangs from, kept from a saved session; null finds it.</summary>
        public string Hang;

        /// <summary>The frame set by hand; null traces it from the paint.</summary>
        public LimbAim Aim;

        /// <summary>The traced frame from the last build without an aim.</summary>
        public LimbAim Traced;
    }

    /// <summary>
    /// A limb's frame in rest model space. Side is the axis its strip lies across (null takes it
    /// from the head); a curved aim keeps the traced centre line and uses only the side.
    /// </summary>
    public sealed record LimbAim(
        Vector3 Root,
        Vector3 Direction,
        Vector3? Side = null,
        bool Straight = true
    )
    {
        static Vector3 Mirror(Vector3 v) => new(-v.X, v.Y, v.Z);

        /// <summary>Mirrored across X = 0.</summary>
        public LimbAim Mirrored() =>
            this with
            {
                Root = Mirror(Root),
                Direction = Mirror(Direction),
                Side = Side is Vector3 s ? Mirror(s) : null,
            };

        /// <summary>The side at roll 0.</summary>
        public static Vector3 LevelSide(Vector3 direction)
        {
            var side = Vector3.Cross(Vector3.UnitY, direction);
            if (side.LengthSquared < 1e-8f)
                side = Vector3.Cross(Vector3.UnitZ, direction);
            return side.Normalized();
        }

        /// <summary>Degrees the side is turned from level.</summary>
        public float Roll()
        {
            if (Side is not Vector3 side)
                return 0;
            var level = LevelSide(Direction);
            var up = Vector3.Cross(Direction, level);
            return MathHelper.RadiansToDegrees(
                MathF.Atan2(Vector3.Dot(side, up), Vector3.Dot(side, level))
            );
        }

        public static Vector3 SideAt(Vector3 direction, float roll)
        {
            var level = LevelSide(direction);
            var up = Vector3.Cross(direction, level);
            float r = MathHelper.DegreesToRadians(roll);
            return (level * MathF.Cos(r) + up * MathF.Sin(r)).Normalized();
        }

        /// <summary>Turned about the root.</summary>
        public LimbAim Rotated(Quaternion q)
        {
            var d = Vector3.Transform(Direction, q).Normalized();
            var s = Vector3.Transform(Side ?? LevelSide(Direction), q);
            s -= d * Vector3.Dot(s, d);
            return this with
            {
                Direction = d,
                Side = s.LengthSquared > 1e-8f ? s.Normalized() : LevelSide(d),
            };
        }

        /// <summary>Yaw from +Z toward +X and pitch up from level, in degrees.</summary>
        public (float Yaw, float Pitch) Angles() =>
            (
                MathHelper.RadiansToDegrees(MathF.Atan2(Direction.X, Direction.Z)),
                MathHelper.RadiansToDegrees(MathF.Asin(Math.Clamp(Direction.Y, -1, 1)))
            );

        public static Vector3 FromAngles(float yaw, float pitch)
        {
            float y = MathHelper.DegreesToRadians(yaw),
                p = MathHelper.DegreesToRadians(pitch);
            return new Vector3(
                MathF.Sin(y) * MathF.Cos(p),
                MathF.Sin(p),
                MathF.Cos(y) * MathF.Cos(p)
            );
        }
    }
}
