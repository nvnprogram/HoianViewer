using System.Collections.Generic;
using OpenTK;
using PlayerViewer.Rigging;

namespace PlayerViewer.HairGen
{
    /// <summary>A lock, tail or tentacle of a hair: its centre line from where it leaves the head to its tip, and the surface that follows it.</summary>
    public class Strand
    {
        public int Id;
        public int Island;

        /// <summary>The painted limb it was built from, or null for a strand found on the mesh.</summary>
        public PaintedLimb Limb;

        /// <summary>A name for its chain; null names it by its id.</summary>
        public string Name;

        /// <summary>Arc along the centre line from the root over which a painted limb lies on the head.</summary>
        public float Held;

        /// <summary>Centre line points, root first.</summary>
        public List<Vector3> Centre = new();

        /// <summary>Arc length along the centre line at each point.</summary>
        public List<float> Arc = new();

        /// <summary>Cross section radius at each point.</summary>
        public List<float> Radius = new();

        /// <summary>The strand this one branches off, and where along it; -1 when it leaves the head.</summary>
        public int Parent = -1;
        public float ParentArc;

        /// <summary>The model bone it hangs from when that is not the head's; -1 for the rig's anchor.</summary>
        public int Anchor = -1;

        public float Length => Arc.Count > 0 ? Arc[^1] : 0;
        public Vector3 Root => Centre[0];
        public Vector3 Tip => Centre[^1];

        /// <summary>The centre line point at an arc length, clamped to the ends.</summary>
        public Vector3 At(float arc) => Polyline.At(Centre, Arc, arc);

        public float RadiusAt(float arc) => Polyline.At(Radius, Arc, Centre.Count, arc);
    }

    /// <summary>The strands of a mesh, and which strand every welded node follows (-1 for the rigid rest).</summary>
    public partial class StrandSet
    {
        public MeshGraph Graph;
        public List<Strand> Strands = new();
        public int[] NodeStrand;

        /// <summary>Arc length along its strand of each strand node; negative just below the root.</summary>
        public float[] NodeArc;

        public float[] ScalpDistance;
        public float[] Geodesic;

        /// <summary>Distance at rest from each limb node to the head, face included; NaN where not measured.</summary>
        public float[] HeadDistance;
    }
}
