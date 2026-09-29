using System.Collections.Generic;
using OpenTK;
using PlayerViewer.Phive;

namespace PlayerViewer.HairGen
{
    /// <summary>
    /// A capsule or sphere the limbs' cloth collides with, riding on a bone of the model: one the
    /// user placed, or one the hair generation makes by default, with the user's edits of it. The
    /// shape is in the bone's space.
    /// </summary>
    public class LimbCollider
    {
        public string Name;
        public CollidableShapeKind Kind = CollidableShapeKind.Capsule;
        public string Bone;

        /// <summary>A capsule's ends; a sphere's centre is <see cref="Start"/>.</summary>
        public Vector3 Start,
            End;

        public float Radius = 0.08f;
        public bool Enabled = true;

        /// <summary>
        /// The limbs whose cloth collides with it; null for every limb, or for a default one, the
        /// limbs its height rule picks.
        /// </summary>
        public HashSet<PaintedLimb> Limbs;

        /// <summary>For a default collider, the height a piece's tip must reach below to collide with it; null for any.</summary>
        public float? Below;

        /// <summary>Made by the hair generation rather than placed by the user.</summary>
        public bool Default;

        /// <summary>For a default collider, whether the user's edit of it replaced the generated values.</summary>
        public bool Edited;

        /// <summary>The name the last build gave its collidable, or null when it was left out.</summary>
        public string BuiltName;

        /// <summary>Why the last build left it out, or null.</summary>
        public string Problem;

        public LimbCollider Clone()
        {
            var copy = (LimbCollider)MemberwiseClone();
            copy.Limbs = Limbs != null ? new HashSet<PaintedLimb>(Limbs) : null;
            return copy;
        }

        /// <summary>Takes another's values, keeping this object, which windows hold on to.</summary>
        public void CopyFrom(LimbCollider other)
        {
            Name = other.Name;
            Kind = other.Kind;
            Bone = other.Bone;
            Start = other.Start;
            End = other.End;
            Radius = other.Radius;
            Enabled = other.Enabled;
            Limbs = other.Limbs != null ? new HashSet<PaintedLimb>(other.Limbs) : null;
            Below = other.Below;
            Default = other.Default;
            Edited = other.Edited;
        }
    }
}
