using System.Collections.Generic;
using System.Linq;

namespace PlayerViewer.Phive
{
    public enum ConstraintSetKind
    {
        Unknown,
        StandardLink,
        StretchLink,
        BendLink,
        CompressibleLink,
        LocalRange,
        Transition,
        BendStiffness,
        Volume,
    }

    /// <summary>
    /// An hclConstraintSet of a sim cloth, by its class. The per link and per particle records
    /// stay in the graph; <see cref="Records"/> gives them in file order, which is also the
    /// order the solver applies them in.
    /// </summary>
    public class ConstraintSet
    {
        public HkObject Source { get; }
        public ConstraintSetKind Kind { get; }

        protected ConstraintSet(HkObject source, ConstraintSetKind kind)
        {
            Source = source;
            Kind = kind;
        }

        public static ConstraintSet Wrap(HkObject source)
        {
            var t = source.Type;
            var kind =
                t.IsA("hclStandardLinkConstraintSet") ? ConstraintSetKind.StandardLink
                : t.IsA("hclStretchLinkConstraintSet") ? ConstraintSetKind.StretchLink
                : t.IsA("hclBendLinkConstraintSet") ? ConstraintSetKind.BendLink
                : t.IsA("hclCompressibleLinkConstraintSet") ? ConstraintSetKind.CompressibleLink
                : t.IsA("hclLocalRangeConstraintSet") ? ConstraintSetKind.LocalRange
                : t.IsA("hclTransitionConstraintSet") ? ConstraintSetKind.Transition
                : t.IsA("hclBendStiffnessConstraintSet") ? ConstraintSetKind.BendStiffness
                : t.IsA("hclVolumeConstraint") ? ConstraintSetKind.Volume
                : ConstraintSetKind.Unknown;
            return kind switch
            {
                ConstraintSetKind.LocalRange => new LocalRangeSet(source),
                ConstraintSetKind.Transition => new TransitionSet(source),
                ConstraintSetKind.BendStiffness => new BendStiffnessSet(source),
                _ => new ConstraintSet(source, kind),
            };
        }

        public string Name
        {
            get => Source.String("name");
            set => Source.Set("name", value);
        }

        public string TypeName => Source.Type.Name;

        /// <summary>The per element records: links, local constraints, per particle data or the volume's apply data.</summary>
        public virtual HkArray Records =>
            Kind switch
            {
                ConstraintSetKind.Transition => Source.Array("perParticleData"),
                ConstraintSetKind.Volume => Source.Array("applyDatas"),
                _ => Source.Array("links"),
            };

        public override string ToString() => $"{TypeName} '{Name}' ({Records.Count})";
    }

    /// <summary>
    /// hclLocalRangeConstraintSet. The file stores its entries in one of four arrays: plain
    /// or with a per particle stiffness, each as a sphere or a capsule.
    /// </summary>
    public class LocalRangeSet : ConstraintSet
    {
        public LocalRangeSet(HkObject source)
            : base(source, ConstraintSetKind.LocalRange) { }

        /// <summary>The set's stiffness; with per particle values the two multiply.</summary>
        public float Stiffness
        {
            get => Source.Has("stiffness") ? Source.Float("stiffness") : 1.0f;
            set => Source.Set("stiffness", value);
        }

        public HkArray LocalConstraints => Source.Array("localConstraints");
        public HkArray LocalStiffnessConstraints => Source.Array("localStiffnessConstraints");
        public HkArray LocalCapsuleConstraints => Source.Array("localCapsuleConstraints");
        public HkArray LocalCapsuleStiffnessConstraints =>
            Source.Array("localCapsuleStiffnessConstraints");

        /// <summary>The sphere entries: the plain array when it has any, else the per particle stiffness one.</summary>
        public override HkArray Records =>
            LocalConstraints.Count > 0 ? LocalConstraints : LocalStiffnessConstraints;

        public int ShapeType => Source.Int("shapeType");
        public bool ApplyNormalComponent => Source.Bool("applyNormalComponent");
        public int ReferenceMeshBufferIdx => Source.Int("referenceMeshBufferIdx");
    }

    /// <summary>hclTransitionConstraintSet: the release from the animation pose after a reset.</summary>
    public class TransitionSet : ConstraintSet
    {
        public TransitionSet(HkObject source)
            : base(source, ConstraintSetKind.Transition) { }

        public float ToSimPeriod
        {
            get => Source.Has("toSimPeriod") ? Source.Float("toSimPeriod") : 1.0f;
            set => Source.Set("toSimPeriod", value);
        }

        public float ToAnimPeriod => Source.Float("toAnimPeriod");
    }

    /// <summary>hclBendStiffnessConstraintSet: four particle bending elements.</summary>
    public class BendStiffnessSet : ConstraintSet
    {
        public BendStiffnessSet(HkObject source)
            : base(source, ConstraintSetKind.BendStiffness) { }

        public bool UseRestPoseConfig
        {
            get => Source.Bool("useRestPoseConfig");
            set => Source.Set("useRestPoseConfig", value);
        }

        public bool ClampBendStiffness
        {
            get => Source.Bool("clampBendStiffness");
            set => Source.Set("clampBendStiffness", value);
        }

        public float MaxRestPoseHeightSq
        {
            get =>
                Source.Has("maxRestPoseHeightSq")
                    ? Source.Float("maxRestPoseHeightSq")
                    : float.MaxValue;
            set => Source.Set("maxRestPoseHeightSq", value);
        }
    }
}
