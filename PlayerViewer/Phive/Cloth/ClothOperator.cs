using System.Collections.Generic;
using System.Linq;
using OpenTK;

namespace PlayerViewer.Phive
{
    /// <summary>An hclOperator of a cloth piece, wrapped by its class.</summary>
    public class ClothOperator
    {
        public HkObject Source { get; }

        protected ClothOperator(HkObject source) => Source = source;

        public static ClothOperator Wrap(HkObject source)
        {
            var t = source.Type;
            if (t.IsA("hclObjectSpaceSkinOperator") || t.IsA("hclBoneSpaceSkinOperator"))
                return new SkinOperator(source);
            if (t.IsA("hclSimulateOperator"))
                return new SimulateOperator(source);
            if (t.IsA("hclMoveParticlesOperator"))
                return new MoveParticlesOperator(source);
            if (t.IsA("hclSimpleMeshBoneDeformOperator"))
                return new MeshBoneDeformOperator(source);
            return new ClothOperator(source);
        }

        public string Name => Source.String("name");
        public string TypeName => Source.Type.Name;

        public override string ToString() => $"{TypeName} '{Name}'";
    }

    /// <summary>
    /// hclObjectSpaceSkinPOperator and hclBoneSpaceSkinPOperator: the skinned reference
    /// positions of the piece's vertices from its transform set. Blend entries come in blocks of
    /// 16 vertices per influence count; control bytes give the block order.
    /// </summary>
    public class SkinOperator : ClothOperator
    {
        public SkinOperator(HkObject source)
            : base(source) { }

        public bool BoneSpace => Source.Object("boneSpaceDeformer") != null;

        /// <summary>hclObjectSpaceDeformer or hclBoneSpaceDeformer (inline).</summary>
        public HkObject Deformer =>
            Source.Object("objectSpaceDeformer") ?? Source.Object("boneSpaceDeformer");

        /// <summary>Transform set indices the blend entries' bone indices refer to; empty means all, unmapped.</summary>
        public int[] TransformSubset => Source.Array("transformSubset").Ints();

        /// <summary>Object space only: per subset bone, skin mesh space to bone space.</summary>
        public HkArray BoneFromSkinMeshTransforms => Source.Array("boneFromSkinMeshTransforms");

        /// <summary>Local position blocks (LocalBlockP), 16 entries each, in block order.</summary>
        public HkArray LocalPs => Source.Array("localPs");

        /// <summary>The blend entry block arrays by influence count, as the deformer declares them.</summary>
        public IEnumerable<(int Count, HkArray Blocks)> BlendEntryBlocks()
        {
            var d = Deformer;
            if (d == null)
                yield break;
            for (int n = 1; n <= 8; n++)
            {
                string key = BlendKey(n);
                if (d.Has(key))
                    yield return (n, d.Array(key));
            }
        }

        public static string BlendKey(int n) =>
            n switch
            {
                1 => "oneBlendEntries",
                2 => "twoBlendEntries",
                3 => "threeBlendEntries",
                4 => "fourBlendEntries",
                5 => "fiveBlendEntries",
                6 => "sixBlendEntries",
                7 => "sevenBlendEntries",
                _ => "eightBlendEntries",
            };
    }

    /// <summary>hclSimulateOperator: the configs that say which constraint sets run, in what order.</summary>
    public class SimulateOperator : ClothOperator
    {
        public SimulateOperator(HkObject source)
            : base(source) { }

        public IEnumerable<SimulateConfig> Configs =>
            Source.Array("simulateOpConfigs").Objects.Select(o => new SimulateConfig(o));
    }

    /// <summary>hclSimulateOperator::Config.</summary>
    public class SimulateConfig
    {
        public HkObject Source { get; }

        public SimulateConfig(HkObject source) => Source = source;

        public string Name => Source.String("name");

        /// <summary>Constraint set indices in solve order; -1 is the collision pass, run in that slot.</summary>
        public HkArray ConstraintExecution => Source.Array("constraintExecution");

        public HkArray InstanceCollidablesUsed => Source.Array("instanceCollidablesUsed");

        public int SubSteps
        {
            get => Source.Int("subSteps", 1);
            set => Source.Set("subSteps", value);
        }

        public int NumberOfSolveIterations
        {
            get => Source.Int("numberOfSolveIterations", 1);
            set => Source.Set("numberOfSolveIterations", value);
        }

        public bool UseAllInstanceCollidables
        {
            get => Source.Bool("useAllInstanceCollidables", true);
            set => Source.Set("useAllInstanceCollidables", value);
        }

        public bool AdaptConstraintStiffness => Source.Bool("adaptConstraintStiffness");
    }

    /// <summary>hclMoveParticlesOperator: pins particles to reference vertices (the fixed ones).</summary>
    public class MoveParticlesOperator : ClothOperator
    {
        public MoveParticlesOperator(HkObject source)
            : base(source) { }

        public IEnumerable<(int Vertex, int Particle)> VertexParticlePairs =>
            Source
                .Array("vertexParticlePairs")
                .Objects.Select(p => (p.Int("vertexIndex"), p.Int("particleIndex")));
    }

    /// <summary>
    /// hclSimpleMeshBoneDeformOperator: drives transform set entries from particle triangles.
    /// Each pair addresses its bone and triangle by byte offset (64 per matrix, 2 per index).
    /// </summary>
    public class MeshBoneDeformOperator : ClothOperator
    {
        public MeshBoneDeformOperator(HkObject source)
            : base(source) { }

        public const int BoneMatrixBytes = 64;

        public IEnumerable<(int BoneOffset, int TriangleOffset)> TriangleBonePairs =>
            Source
                .Array("triangleBonePairs")
                .Objects.Select(p => (p.Int("boneOffset"), p.Int("triangleOffset")));

        public Matrix4[] LocalBoneTransforms =>
            Source.Array("localBoneTransforms").Select(HkValue.ToMatrix).ToArray();

        /// <summary>The axis kept exact when the frame is orthonormalised; absent in the older exporter's files.</summary>
        public int BoneAxis => Source.Int("boneAxis");
    }
}
