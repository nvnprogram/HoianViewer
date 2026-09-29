using System.Collections.Generic;
using System.Linq;
using OpenTK;

namespace PlayerViewer.Phive
{
    /// <summary>
    /// hclClothData: one independently simulated piece. It owns its sim data, buffers,
    /// transform set, the operators and the states that order them.
    /// </summary>
    public class ClothData
    {
        public HkObject Source { get; }

        public ClothData(HkObject source)
        {
            Source = source;
            SimClothDatas = source
                .Array("simClothDatas")
                .Objects.Select(o => new SimClothData(o))
                .ToList();
            Operators = source.Array("operators").Objects.Select(ClothOperator.Wrap).ToList();
        }

        public string Name
        {
            get => Source.String("name");
            set => Source.Set("name", value);
        }

        public List<SimClothData> SimClothDatas { get; }

        /// <summary>The operators in file order; a state lists the ones it runs by index.</summary>
        public List<ClothOperator> Operators { get; }

        public IEnumerable<TransformSetDefinition> TransformSetDefinitions =>
            Source
                .Array("transformSetDefinitions")
                .Objects.Select(o => new TransformSetDefinition(o));

        /// <summary>hclBufferDefinition and hclScratchBufferDefinition records, kept as the graph has them.</summary>
        public HkArray BufferDefinitions => Source.Array("bufferDefinitions");

        /// <summary>hclClothState records: which operators run, in which order, touching which buffers.</summary>
        public HkArray ClothStates => Source.Array("clothStateDatas");

        public T Operator<T>()
            where T : ClothOperator => Operators.OfType<T>().FirstOrDefault();

        public override string ToString() => $"hclClothData '{Name}'";
    }

    /// <summary>hclTransformSetDefinition: the transform set a piece reads and writes, one matrix per cloth bone.</summary>
    public class TransformSetDefinition
    {
        public HkObject Source { get; }

        public TransformSetDefinition(HkObject source) => Source = source;

        public string Name => Source.String("name");
        public int NumTransforms => Source.Int("numTransforms");
    }

    /// <summary>hclSimClothData: particles, their rest pose, constraint sets and collidables.</summary>
    public class SimClothData
    {
        public HkObject Source { get; }

        public SimClothData(HkObject source)
        {
            Source = source;
            ConstraintSets = source
                .Array("staticConstraintSets")
                .Objects.Select(ConstraintSet.Wrap)
                .ToList();
            PerInstanceCollidables = source
                .Array("perInstanceCollidables")
                .Objects.Select(o => new Collidable(o))
                .ToList();
        }

        public string Name => Source.String("name");

        HkObject SimulationInfo => Source.Object("simulationInfo");

        public Vector4 Gravity
        {
            get => SimulationInfo?.Vector4("gravity") ?? Vector4.Zero;
            set => SimulationInfo?.Set("gravity", HkValue.FromVector(value));
        }

        /// <summary>The fraction of velocity lost per second, applied as (1 - d)^dt per step.</summary>
        public float GlobalDampingPerSecond
        {
            get => SimulationInfo?.Float("globalDampingPerSecond") ?? 0;
            set => SimulationInfo?.Set("globalDampingPerSecond", value);
        }

        public IEnumerable<ParticleData> Particles =>
            Source.Array("particleDatas").Objects.Select(o => new ParticleData(o));

        public int ParticleCount => Source.Array("particleDatas").Count;

        public HkArray FixedParticles => Source.Array("fixedParticles");

        /// <summary>Per particle bit set of the collidables it collides with.</summary>
        public HkArray StaticCollisionMasks => Source.Array("staticCollisionMasks");

        /// <summary>Particle triangles, three u16 indices each; the bone deform addresses them by byte offset.</summary>
        public HkArray TriangleIndices => Source.Array("triangleIndices");

        /// <summary>hclSimClothPose records; the first ("DefaultClothPose") is the rest pose.</summary>
        public IEnumerable<SimClothPose> Poses =>
            Source.Array("simClothPoses").Objects.Select(o => new SimClothPose(o));

        /// <summary>The constraint sets in file order, which is the index space of a simulate config's execution list.</summary>
        public List<ConstraintSet> ConstraintSets { get; }

        public HkObject CollidableTransformMap => Source.Object("collidableTransformMap");

        /// <summary>The collidables this piece uses, the same instances the container lists.</summary>
        public List<Collidable> PerInstanceCollidables { get; }

        /// <summary>Per collidable in <see cref="PerInstanceCollidables"/> order: the transform set index it follows.</summary>
        public int[] CollidableTransformIndices =>
            CollidableTransformMap?.Array("transformIndices").Ints() ?? System.Array.Empty<int>();

        /// <summary>Per collidable: the offset from its bone to collidable space, as stored.</summary>
        public Matrix4[] CollidableOffsets =>
            CollidableTransformMap?.Array("offsets").Select(HkValue.ToMatrix).ToArray()
            ?? System.Array.Empty<Matrix4>();

        public HkObject VirtualCollisionPointsData => Source.Object("virtualCollisionPointsData");

        public float TotalMass => Source.Float("totalMass");
        public float MaxParticleRadius => Source.Float("maxParticleRadius");

        /// <summary>
        /// The virtual collision points in the game's order: per particle that owns an edge
        /// fan, per edge of the fan, per barycentric of the edge's dictionary entry.
        /// </summary>
        public IEnumerable<(int Owner, int Opposite, float Barycentric)> VirtualEdgePoints()
        {
            var vcp = VirtualCollisionPointsData;
            if (vcp == null)
                yield break;
            float[] bary = vcp.Array("edgeBarycentricsDictionary").FloatValues();
            var dict = vcp.Array("edgeDictionaryEntries")
                .Objects.Select(d =>
                    (start: d.Int("startingBarycentricIndex"), num: d.Int("numBarycentrics"))
                )
                .ToArray();
            var edges = vcp.Array("edges")
                .Objects.Select(e =>
                    (
                        opp: e.Int("oppositeRealParticleIndex"),
                        dict: e.Int("barycentricDictionaryIndex")
                    )
                )
                .ToArray();
            var fans = vcp.Array("edgeFans")
                .Objects.Select(f =>
                    (
                        owner: f.Int("realParticleIndex"),
                        start: f.Int("edgeStartIndex"),
                        num: f.Int("numEdges")
                    )
                )
                .ToArray();
            int[] fanOf = vcp.Array("edgeFanIndices").Ints();
            for (int p = 0; p < fanOf.Length; p++)
            {
                int fi = fanOf[p];
                if (fi < 0 || fi >= fans.Length || fi == 0xFFFF)
                    continue;
                var fan = fans[fi];
                for (int e = fan.start; e < fan.start + fan.num && e < edges.Length; e++)
                {
                    var (opp, di) = edges[e];
                    if (di < 0 || di >= dict.Length)
                        continue;
                    for (
                        int b = dict[di].start;
                        b < dict[di].start + dict[di].num && b < bary.Length;
                        b++
                    )
                        yield return (fan.owner, opp, bary[b]);
                }
            }
        }

        public override string ToString() => $"hclSimClothData '{Name}'";
    }

    /// <summary>hclSimClothData::ParticleData.</summary>
    public class ParticleData
    {
        public HkObject Source { get; }

        public ParticleData(HkObject source) => Source = source;

        public float Mass
        {
            get => Source.Float("mass");
            set => Source.Set("mass", value);
        }

        public float InvMass
        {
            get => Source.Float("invMass");
            set => Source.Set("invMass", value);
        }

        /// <summary>Sets the mass and its inverse, 0 for a massless (fixed) particle.</summary>
        public void SetMass(float mass)
        {
            Mass = mass;
            InvMass = mass > 0 ? 1.0f / mass : 0;
        }

        public float Radius
        {
            get => Source.Float("radius");
            set => Source.Set("radius", value);
        }

        public float Friction
        {
            get => Source.Float("friction");
            set => Source.Set("friction", value);
        }
    }

    /// <summary>hclSimClothPose: particle positions as (x, y, z, 1).</summary>
    public class SimClothPose
    {
        public HkObject Source { get; }

        public SimClothPose(HkObject source) => Source = source;

        public string Name => Source.String("name");

        public Vector4[] Positions => Source.Array("positions").Select(HkValue.ToVector4).ToArray();
    }
}
