using OpenTK;

namespace PlayerViewer.Phive
{
    public enum CollidableShapeKind
    {
        Unknown,
        Sphere,
        Plane,
        Capsule,
    }

    /// <summary>
    /// hclCollidable: a shape in its own space and the rest transform that places it. The
    /// container lists every collidable once; each piece's sim data points at the ones it uses.
    /// </summary>
    public class Collidable
    {
        public HkObject Source { get; }

        public Collidable(HkObject source) => Source = source;

        public string Name
        {
            get => Source.String("name");
            set => Source.Set("name", value);
        }

        /// <summary>The rest transform as stored (the w column is not always clean).</summary>
        public Matrix4 Transform
        {
            get => Source.Matrix("transform");
            set => Source.Set("transform", HkValue.FromMatrix(value));
        }

        public bool Enabled
        {
            get => Source.Bool("enabled", true);
            set => Source.Set("enabled", value);
        }

        public bool VirtualCollisionPointCollisionEnabled
        {
            get => Source.Bool("virtualCollisionPointCollisionEnabled");
            set => Source.Set("virtualCollisionPointCollisionEnabled", value);
        }

        public bool PinchDetectionEnabled
        {
            get => Source.Bool("pinchDetectionEnabled");
            set => Source.Set("pinchDetectionEnabled", value);
        }

        public HkObject Shape => Source.Object("shape");

        /// <summary>By the shape's class.</summary>
        public CollidableShapeKind ShapeKind =>
            Shape?.Type switch
            {
                HkType t when t.IsA("hclCapsuleShape") => CollidableShapeKind.Capsule,
                HkType t when t.IsA("hclSphereShape") => CollidableShapeKind.Sphere,
                HkType t when t.IsA("hclPlaneShape") => CollidableShapeKind.Plane,
                _ => CollidableShapeKind.Unknown,
            };

        /// <summary>A capsule's ends and radius. The shape also caches dir and capLenSqrdInv, which an edit must keep in step.</summary>
        public (Vector3 Start, Vector3 End, float Radius) Capsule =>
            (Shape.Vector3("start"), Shape.Vector3("end"), Shape.Float("radius"));

        /// <summary>A sphere's centre (xyz) and radius (w), an hkSphere.</summary>
        public Vector4 Sphere => Shape.Object("sphere")?.Vector4("pos") ?? Vector4.Zero;

        /// <summary>A plane's equation: normal in xyz, offset in w.</summary>
        public Vector4 PlaneEquation => Shape.Vector4("planeEquation");

        public override string ToString() => $"{ShapeKind} '{Name}'";
    }
}
