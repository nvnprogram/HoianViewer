using System;
using System.Collections.Generic;
using System.Linq;
using OpenTK;

namespace PlayerViewer.Phive
{
    /// <summary>
    /// One cloth piece compiled for the solver: the arrays a <see cref="ClothInstance"/> steps
    /// over, derived from an hclClothData and its skeleton. It holds no state of its own and is
    /// rebuilt from the file after an edit.
    /// </summary>
    public class ClothPiece
    {
        public string Name;

        //Cloth skeleton: the transform set as bones, in transform set order.
        public string[] BoneNames;
        public int[] BoneParents;
        public Matrix4[] BoneRefPose; //model space

        //Skinning of the reference vertices (fixed particles and constraint references).
        public Matrix4[] BoneFromSkinMesh;
        public int[] TransformSubset;
        public ClothSkinVertex[] SkinVertices;

        public ClothParticle[] Particles;
        public int[] FixedParticles;
        public Vector3[] RestPositions;
        public int[] TriangleIndices;
        public Vector3 Gravity;
        public float DampingPerSecond;
        public List<(int Vertex, int Particle)> VertexParticlePairs = new();

        /// <summary>The reference vertex each particle follows, -1 for none.</summary>
        public int[] ReferenceVertices;

        /// <summary>Index aligned with the file's constraint sets; a simulate config's execution list indexes it.</summary>
        public List<ClothConstraint> ConstraintSets = new();
        public List<ClothCollidable> Collidables = new();
        public List<ClothVirtualPoint> VirtualPoints = new();

        //The simulate operator's first config.
        public int SubSteps = 1;
        public int SolveIterations = 1;
        public int[] ConstraintExecution = Array.Empty<int>();
        public bool UseAllInstanceCollidables = true;
        public int[] InstanceCollidablesUsed = Array.Empty<int>();

        public List<ClothBoneDeform> BoneDeforms = new();
        public int BoneAxis;

        /// <summary>Turn each driven bone to aim at its first cloth child: the piece's BoneCorrection parameter, on without one.</summary>
        public bool AimAtChild = true;

        /// <summary>
        /// Compiles every piece of a cloth file the solver can run; a piece it cannot is
        /// reported to the console with the reason and left out.
        /// </summary>
        public static List<ClothPiece> CompileAll(ClothFile file)
        {
            var pieces = new List<ClothPiece>();
            foreach (var data in file.Container?.ClothDatas ?? new List<ClothData>())
            {
                var piece = Compile(file, data, out string why);
                if (piece != null)
                    pieces.Add(piece);
                else
                    Console.WriteLine($"[Cloth] piece '{data.Name}' dropped: {why}");
            }
            return pieces;
        }

        /// <summary>The game keeps a zero gravity and replaces any other with standard gravity when it creates the cloth.</summary>
        public static Vector3 GameGravity(Vector3 authored) =>
            authored == Vector3.Zero ? Vector3.Zero : new Vector3(0, -9.81f, 0);

        public static ClothPiece Compile(ClothFile file, ClothData data, out string why)
        {
            why = null;
            var piece = new ClothPiece { Name = data.Name ?? "" };
            var mesh = file.Params?.MeshFor(piece.Name);
            if (mesh != null)
                piece.AimAtChild = mesh.BoneCorrection;

            var skeleton = file.SkeletonFor(data);
            if (skeleton == null)
            {
                string wanted = data.TransformSetDefinitions.FirstOrDefault()?.Name;
                why =
                    $"skeleton '{wanted}' not found (have: {string.Join(",", file.Skeletons.Select(s => s.Name))})";
                return null;
            }
            piece.BoneNames = skeleton.BoneNames;
            piece.BoneParents = skeleton.ParentIndices;
            piece.BoneRefPose = skeleton.ModelSpaceReferencePose();

            var sim = data.SimClothDatas.FirstOrDefault();
            if (sim == null)
            {
                why = "no simClothDatas";
                return null;
            }
            piece.Particles = sim
                .Particles.Select(p => new ClothParticle
                {
                    InvMass = p.InvMass,
                    Radius = p.Radius,
                    Friction = p.Friction,
                })
                .ToArray();
            if (piece.Particles.Length == 0)
            {
                why = "no particleDatas";
                return null;
            }
            var masks = sim.StaticCollisionMasks;
            for (int p = 0; p < masks.Count && p < piece.Particles.Length; p++)
                piece.Particles[p].CollisionMask = Convert.ToUInt32(masks[p]);

            foreach (var (owner, opposite, barycentric) in sim.VirtualEdgePoints())
                piece.VirtualPoints.Add(
                    new ClothVirtualPoint
                    {
                        Owner = owner,
                        Opposite = opposite,
                        Barycentric = barycentric,
                    }
                );

            piece.FixedParticles = sim.FixedParticles.Ints();
            piece.TriangleIndices = sim.TriangleIndices.Ints();
            piece.Gravity = GameGravity(sim.Gravity.Xyz);
            piece.DampingPerSecond = sim.GlobalDampingPerSecond;
            piece.RestPositions = sim
                .Poses.FirstOrDefault()
                ?.Positions.Select(p => p.Xyz)
                .ToArray();

            foreach (var set in sim.ConstraintSets)
                piece.ConstraintSets.Add(ClothConstraint.Compile(set));

            CompileCollidables(sim, piece);

            foreach (var op in data.Operators)
            {
                switch (op)
                {
                    case SkinOperator skin:
                        CompileSkin(skin, piece);
                        break;
                    case SimulateOperator simulate
                        when simulate.Configs.FirstOrDefault() is SimulateConfig cfg:
                        piece.SubSteps = Math.Max(1, cfg.SubSteps);
                        piece.SolveIterations = Math.Max(1, cfg.NumberOfSolveIterations);
                        piece.ConstraintExecution = cfg.ConstraintExecution.Ints();
                        piece.UseAllInstanceCollidables = cfg.UseAllInstanceCollidables;
                        piece.InstanceCollidablesUsed = cfg.InstanceCollidablesUsed.Ints();
                        break;
                    case MoveParticlesOperator move
                        when move.Source.Array("vertexParticlePairs").Count > 0:
                        piece.VertexParticlePairs = move.VertexParticlePairs.ToList();
                        break;
                    case MeshBoneDeformOperator deform
                        when deform.Source.Array("triangleBonePairs").Count > 0:
                        CompileBoneDeform(deform, piece);
                        break;
                }
            }

            if (piece.SkinVertices == null)
            {
                why = "no skin operator";
                return null;
            }
            if (piece.RestPositions == null)
            {
                why = "no rest positions";
                return null;
            }
            piece.ReferenceVertices = ClothEdit.ReferenceVertices(data, piece.SkinVertices.Length);

            //No simulate config: every set once in file order, then the collision pass.
            if (piece.ConstraintExecution.Length == 0)
                piece.ConstraintExecution = Enumerable
                    .Range(0, piece.ConstraintSets.Count)
                    .Append(-1)
                    .ToArray();
            return piece;
        }

        /// <summary>
        /// The piece's collidables with the bone each follows and the offset from it. A shape the
        /// solver has no kernel for keeps its slot, so collision mask bits stay aligned.
        /// </summary>
        static void CompileCollidables(SimClothData sim, ClothPiece piece)
        {
            int[] transformIndices = sim.CollidableTransformIndices;
            var offsets = sim.CollidableOffsets;
            var collidables = sim.PerInstanceCollidables;
            for (int i = 0; i < collidables.Count; i++)
            {
                var col = collidables[i];
                var compiled = new ClothCollidable
                {
                    Name = col.Name ?? "",
                    Shape = col.ShapeKind,
                    Enabled = col.Enabled,
                    VirtualPoints = col.VirtualCollisionPointCollisionEnabled,
                    Transform = HkValue.Affine(col.Transform),
                    BoneIndex = i < transformIndices.Length ? transformIndices[i] : 0,
                    BoneOffset = i < offsets.Length ? HkValue.Affine(offsets[i]) : Matrix4.Identity,
                };
                switch (compiled.Shape)
                {
                    case CollidableShapeKind.Capsule:
                        (compiled.Start, compiled.End, compiled.Radius) = col.Capsule;
                        break;
                    case CollidableShapeKind.Sphere:
                        var sphere = col.Sphere;
                        compiled.Start = compiled.End = sphere.Xyz;
                        compiled.Radius = sphere.W;
                        break;
                    case CollidableShapeKind.Plane:
                        var plane = col.PlaneEquation;
                        compiled.Start = compiled.End = plane.Xyz;
                        compiled.Radius = plane.W;
                        break;
                }
                piece.Collidables.Add(compiled);
            }
        }

        static void CompileBoneDeform(MeshBoneDeformOperator op, ClothPiece piece)
        {
            var local = op.LocalBoneTransforms;
            piece.BoneAxis = op.BoneAxis;
            int i = 0;
            foreach (var (boneOffset, triangleOffset) in op.TriangleBonePairs)
            {
                piece.BoneDeforms.Add(
                    new ClothBoneDeform
                    {
                        BoneIndex = boneOffset / MeshBoneDeformOperator.BoneMatrixBytes,
                        TriangleStart = triangleOffset / 2,
                        LocalBoneTransform =
                            i < local.Length ? HkValue.Affine(local[i]) : Matrix4.Identity,
                    }
                );
                i++;
            }
        }

        /// <summary>
        /// Per reference vertex, the bones, weights and local position it is skinned with. Blend
        /// blocks and local position blocks are both consumed in control byte order.
        /// </summary>
        static void CompileSkin(SkinOperator op, ClothPiece piece)
        {
            piece.TransformSubset = op.TransformSubset;
            var bfsm = op.BoneFromSkinMeshTransforms;
            piece.BoneFromSkinMesh =
                bfsm.Count > 0
                    ? bfsm.Select(m => HkValue.Affine(HkValue.ToMatrix(m))).ToArray()
                    : Enumerable
                        .Repeat(Matrix4.Identity, Math.Max(piece.TransformSubset.Length, 1))
                        .ToArray();

            bool boneSpace = op.BoneSpace;
            var deformer = op.Deformer;
            if (deformer == null)
                return;

            int endVertex = deformer.Int("endVertexIndex");
            var verts = new ClothSkinVertex[endVertex + 1];

            var localPs = new List<Vector4>();
            foreach (var block in op.LocalPs.Objects)
            {
                if (block["localPosition"] is not object[] entries)
                    continue;
                foreach (var entry in entries)
                {
                    if (entry is float[] f && f.Length == 4)
                        localPs.Add(new Vector4(f[0], f[1], f[2], f[3]));
                    else if (
                        entry is HkObject packed
                        && packed["values"] is object[] bits
                        && bits.Length == 4
                    )
                        localPs.Add(new Vector4(UnpackVector3(bits), 1));
                }
            }

            var queues = new Dictionary<int, Queue<HkObject>>();
            foreach (var (count, blocks) in op.BlendEntryBlocks())
                if (blocks.Count > 0)
                    queues[count] = new Queue<HkObject>(blocks.Objects);

            //Control byte values: 0 four, 1 three, 2 two, 3 one blend; 4 to 7 are eight to five.
            int[] blendCountForControl = { 4, 3, 2, 1, 8, 7, 6, 5 };
            int[] controlBytes = deformer
                .Array("controlBytes")
                .Ints()
                .Where(b => b >= 0 && b < 8)
                .Select(b => blendCountForControl[b])
                .ToArray();
            if (controlBytes.Length == 0)
            {
                //A deformer with one block kind may omit its control bytes.
                controlBytes = queues
                    .OrderByDescending(kv => kv.Key)
                    .SelectMany(kv => Enumerable.Repeat(kv.Key, kv.Value.Count))
                    .ToArray();
            }

            int globalBlock = 0;
            foreach (int bonesPerVertex in controlBytes)
            {
                if (!queues.TryGetValue(bonesPerVertex, out var queue) || queue.Count == 0)
                {
                    globalBlock++;
                    continue;
                }
                var block = queue.Dequeue();

                int[] vertexIndices = Ints(block["vertexIndices"]);
                int[] boneIndices = Ints(block["boneIndices"]);
                int[] weights = Ints(block["boneWeights"]);
                for (int v = 0; v < vertexIndices.Length && v < 16; v++)
                {
                    int vi = vertexIndices[v];
                    if (vi > endVertex || verts[vi] != null)
                        continue;
                    var sv = new ClothSkinVertex
                    {
                        Bones = new int[bonesPerVertex],
                        Weights = new float[bonesPerVertex],
                    };
                    if (boneSpace)
                    {
                        sv.LocalPosPerBone = new Vector3[bonesPerVertex];
                        for (int b = 0; b < bonesPerVertex; b++)
                        {
                            int idx = v * bonesPerVertex + b;
                            int localIdx = globalBlock * 16 + idx;
                            sv.Bones[b] = idx < boneIndices.Length ? boneIndices[idx] : 0;
                            var lp = localIdx < localPs.Count ? localPs[localIdx] : Vector4.Zero;
                            sv.LocalPosPerBone[b] = lp.Xyz;
                            sv.Weights[b] = lp.W;
                        }
                        sv.LocalPos = sv.LocalPosPerBone[0];
                    }
                    else
                    {
                        int localIdx = globalBlock * 16 + v;
                        sv.LocalPos =
                            localIdx < localPs.Count ? localPs[localIdx].Xyz : Vector3.Zero;
                        for (int b = 0; b < bonesPerVertex; b++)
                        {
                            int idx = v * bonesPerVertex + b;
                            sv.Bones[b] = idx < boneIndices.Length ? boneIndices[idx] : 0;
                            //A one blend block has no weight array: implicit 1.
                            sv.Weights[b] =
                                idx < weights.Length
                                    ? weights[idx] / 255.0f
                                    : (bonesPerVertex == 1 ? 1.0f : 0);
                        }
                    }
                    verts[vi] = sv;
                }
                globalBlock++;
            }

            piece.SkinVertices = verts;
        }

        static int[] Ints(object tuple) =>
            tuple is object[] values
                ? values.Select(x => Convert.ToInt32(x)).ToArray()
                : Array.Empty<int>();

        /// <summary>
        /// An hkPackedVector3: xyz are 16 bit mantissas and w a shared exponent,
        /// f[i] = (values[i] &lt;&lt; 16 as s32) * bitcast_float(values[3] &lt;&lt; 16).
        /// </summary>
        static Vector3 UnpackVector3(object[] rawBits)
        {
            short[] v = rawBits
                .Select(x => unchecked((short)(Convert.ToInt32(x) & 0xFFFF)))
                .ToArray();
            float exp = BitConverter.Int32BitsToSingle(v[3] << 16);
            return new Vector3(
                (float)(v[0] << 16) * exp,
                (float)(v[1] << 16) * exp,
                (float)(v[2] << 16) * exp
            );
        }
    }

    public class ClothParticle
    {
        public float InvMass,
            Radius,
            Friction;
        public uint CollisionMask = 0xFFFFFFFF; //bit i: collides with collidable i
    }

    public class ClothSkinVertex
    {
        public int[] Bones; //indices into TransformSubset
        public float[] Weights;
        public Vector3 LocalPos; //skin mesh space (object space deformer)
        public Vector3[] LocalPosPerBone; //bone space, one per blend slot (bone space deformer)
    }

    public class ClothVirtualPoint
    {
        public int Owner,
            Opposite; //the point sits on the edge from the owner to the opposite particle
        public float Barycentric;
    }

    public class ClothCollidable
    {
        public string Name;
        public CollidableShapeKind Shape;
        public bool Enabled = true;
        public bool VirtualPoints; //collides the virtual points too
        public Vector3 Start,
            End; //capsule ends; a sphere's centre in both; a plane's normal in both, offset in Radius
        public float Radius;
        public Matrix4 Transform; //rest transform
        public int BoneIndex; //transform set index the collidable follows
        public Matrix4 BoneOffset; //bone to collidable space
    }

    public class ClothBoneDeform
    {
        public int BoneIndex; //transform set index to write
        public int TriangleStart; //index into TriangleIndices (start of 3)
        public Matrix4 LocalBoneTransform;
    }
}
