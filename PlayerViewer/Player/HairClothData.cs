using System;
using System.Collections.Generic;
using System.Linq;
using OpenTK;
using PlayerViewer.Core.Formats;

namespace PlayerViewer.Player
{
    /// <summary>
    /// Hair cloth simulation data extracted from a .bphcl file.
    /// One file contains several independent pieces (Cloth_Hair_R / _L / Rear...),
    /// each with its own particle set, constraints and driven bones.
    /// </summary>
    public class HairClothData
    {
        public List<HairClothPiece> Pieces { get; } = new();

        public static HairClothData Load(byte[] bphcl)
        {
            var tag = HkTagfile.ParseBphcl(bphcl);
            var data = new HairClothData();

            //Collect skeletons by name (cloth_skeleton_Cloth_Hair_R ...).
            var skeletons = new Dictionary<string, Dictionary<string, object>>(StringComparer.Ordinal);
            foreach (var (_, records) in tag.AllItems("hkaSkeleton"))
                foreach (var skel in records)
                    if (Str(skel, "name") is string n && !skeletons.ContainsKey(n))
                        skeletons[n] = skel;

            foreach (var (_, records) in tag.AllItems("hclClothData"))
                foreach (var cloth in records)
                {
                    var piece = ParsePiece(cloth, skeletons);
                    if (piece != null)
                        data.Pieces.Add(piece);
                }
            return data;
        }

        static HairClothPiece ParsePiece(Dictionary<string, object> cloth,
            Dictionary<string, Dictionary<string, object>> skeletons)
        {
            var piece = new HairClothPiece { Name = Str(cloth, "name") ?? "" };
            void Fail(string why) => Console.WriteLine($"[HairCloth] piece '{piece.Name}' dropped: {why}");

            //--- Skeleton: transform set definition name matches the hkaSkeleton name.
            var tsd = FirstRecord(cloth, "transformSetDefinitions");
            string skelName = tsd != null ? Str(tsd, "name") : null;
            if (skelName == null || !skeletons.TryGetValue(skelName, out var skel))
            { Fail($"skeleton '{skelName}' not found (have: {string.Join(",", skeletons.Keys)})"); return null; }

            if (skel.GetValueOrDefault("bones") is not List<Dictionary<string, object>> bones ||
                skel.GetValueOrDefault("parentIndices") is not List<object> parents ||
                skel.GetValueOrDefault("referencePose") is not List<Dictionary<string, object>> pose)
            { Fail("skeleton missing bones/parents/pose"); return null; }

            int numBones = bones.Count;
            piece.BoneNames = bones.Select(b => Str(b, "name") ?? "").ToArray();
            piece.BoneParents = parents.Select(p => Convert.ToInt32(p)).ToArray();

            //Reference pose: local hkQsTransforms -> composed model-space matrices.
            var local = new Matrix4[numBones];
            for (int i = 0; i < numBones; i++)
            {
                var t = Vec3(pose[i].GetValueOrDefault("translation"));
                var q = Quat(pose[i].GetValueOrDefault("rotation"));
                var s = Vec3(pose[i].GetValueOrDefault("scale"), Vector3.One);
                local[i] = Matrix4.CreateScale(s) * Matrix4.CreateFromQuaternion(q) *
                    Matrix4.CreateTranslation(t);
            }
            piece.BoneRefPose = new Matrix4[numBones];
            for (int i = 0; i < numBones; i++)
            {
                int parent = piece.BoneParents[i];
                piece.BoneRefPose[i] = parent >= 0 ? local[i] * piece.BoneRefPose[parent] : local[i];
            }

            //--- Sim cloth data
            var sim = FirstRecord(cloth, "simClothDatas");
            if (sim == null)
            { Fail("no simClothDatas"); return null; }

            if (sim.GetValueOrDefault("particleDatas") is List<Dictionary<string, object>> particles)
                piece.Particles = particles.Select(p => new HairParticle
                {
                    InvMass = F(p, "invMass"),
                    Radius = F(p, "radius"),
                    Friction = F(p, "friction"),
                }).ToArray();
            else
            { Fail("no particleDatas"); return null; }

            piece.FixedParticles = IntArray(sim.GetValueOrDefault("fixedParticles"));
            piece.TriangleIndices = IntArray(sim.GetValueOrDefault("triangleIndices"));

            if (sim.GetValueOrDefault("simulationInfo") is Dictionary<string, object> simInfo)
            {
                piece.Gravity = Vec3(simInfo.GetValueOrDefault("gravity"));
                piece.DampingPerSecond = F(simInfo, "globalDampingPerSecond");
            }

            //Rest positions (DefaultClothPose)
            var poseRec = FirstRecord(sim, "simClothPoses");
            if (poseRec?.GetValueOrDefault("positions") is List<object> restPos)
                piece.RestPositions = restPos.Select(v => Vec3(v)).ToArray();

            //--- Constraint sets (staticConstraintSets is a list of pointer-refs)
            if (sim.GetValueOrDefault("staticConstraintSets") is List<object> sets)
            {
                foreach (var setObj in sets)
                {
                    var set = (setObj as List<Dictionary<string, object>>)?.FirstOrDefault();
                    if (set == null) continue;
                    string name = Str(set, "name") ?? "";
                    string detail = "";
                    foreach (var kv in set)
                    {
                        if (kv.Value is List<Dictionary<string, object>> recs && recs.Count > 0)
                            detail += $" {kv.Key}({recs.Count}: {string.Join(",", recs[0].Keys)})";
                        else if (kv.Value is List<object> lo && lo.Count > 0)
                            detail += $" {kv.Key}[{lo.Count}]";
                    }
                    piece.ConstraintSetNames.Add($"{name}:{detail}");

                    //Classify by field shape (names vary), keeping the set order so
                    //the simulate operator's constraintExecution indices resolve.
                    var kind = HairConstraintKind.Unknown;
                    if (set.GetValueOrDefault("links") is List<Dictionary<string, object>> links && links.Count > 0)
                    {
                        if (links[0].ContainsKey("restLength"))
                        {
                            var parsed = links.Select(l => new HairLink
                            {
                                A = Convert.ToInt32(l.GetValueOrDefault("particleA") ?? 0),
                                B = Convert.ToInt32(l.GetValueOrDefault("particleB") ?? 0),
                                RestLength = F(l, "restLength"),
                                Stiffness = F(l, "stiffness"),
                            }).ToList();
                            if (name.Contains("Stretch")) { piece.StretchLinks = parsed; kind = HairConstraintKind.Stretch; }
                            else { piece.StandardLinks = parsed; kind = HairConstraintKind.Standard; }
                        }
                        else if (links[0].ContainsKey("bendMinLength"))
                        {
                            piece.BendLinks = links.Select(l => new HairLink
                            {
                                A = Convert.ToInt32(l.GetValueOrDefault("particleA") ?? 0),
                                B = Convert.ToInt32(l.GetValueOrDefault("particleB") ?? 0),
                                MinLength = F(l, "bendMinLength"),
                                MaxLength = F(l, "stretchMaxLength"),
                                BendStiffness = F(l, "bendStiffness"),
                                StretchStiffness = F(l, "stretchStiffness"),
                            }).ToList();
                            kind = HairConstraintKind.Bend;
                        }
                        //else: hclBendStiffnessConstraintSet quads (particleA..D) - not simulated.
                    }
                    else if (set.GetValueOrDefault("localConstraints") is List<Dictionary<string, object>> locals ||
                             set.GetValueOrDefault("localStiffnessConstraints") is List<Dictionary<string, object>> stiffLocals && (locals = stiffLocals) != null)
                    {
                        float setStiffness = set.ContainsKey("stiffness") ? F(set, "stiffness") : 1.0f;
                        piece.LocalRanges = locals.Select(l => new HairLocalRange
                        {
                            Particle = Convert.ToInt32(l.GetValueOrDefault("particleIndex") ?? 0),
                            ReferenceVertex = Convert.ToInt32(l.GetValueOrDefault("referenceVertex") ?? 0),
                            Radius = F(l, "shapeRadius"),
                            MaxNormal = F(l, "maxNormalDistance"),
                            MinNormal = F(l, "minNormalDistance"),
                            Stiffness = l.ContainsKey("stiffness") ? F(l, "stiffness") : setStiffness,
                        }).ToList();
                        kind = HairConstraintKind.LocalRange;
                    }
                    else if (set.ContainsKey("perParticleData"))
                        kind = HairConstraintKind.Transition;
                    piece.ConstraintSetKinds.Add(kind);
                }
            }

            //--- Collidables (capsules attached to transform-set entries)
            if (sim.GetValueOrDefault("collidableTransformMap") is Dictionary<string, object> ctm &&
                sim.GetValueOrDefault("perInstanceCollidables") is List<object> collidables)
            {
                int[] transformIndices = IntArray(ctm.GetValueOrDefault("transformIndices"));
                var offsets = (ctm.GetValueOrDefault("offsets") as List<object>)?
                    .Select(m => Mat4(m)).ToArray() ?? Array.Empty<Matrix4>();

                for (int i = 0; i < collidables.Count; i++)
                {
                    var col = (collidables[i] as List<Dictionary<string, object>>)?.FirstOrDefault();
                    var shape = col != null ? FirstRecord(col, "shape") : null;
                    if (shape == null || !shape.ContainsKey("start"))
                        continue;
                    piece.Collidables.Add(new HairCollidable
                    {
                        Name = Str(col, "name") ?? "",
                        Start = Vec3(shape.GetValueOrDefault("start")),
                        End = Vec3(shape.GetValueOrDefault("end")),
                        Radius = F(shape, "radius"),
                        Transform = Mat4(col.GetValueOrDefault("transform")),
                        BoneIndex = i < transformIndices.Length ? transformIndices[i] : 0,
                        BoneOffset = i < offsets.Length ? offsets[i] : Matrix4.Identity,
                    });
                }
            }

            //--- Operators
            if (cloth.GetValueOrDefault("operators") is List<object> ops)
            {
                foreach (var opObj in ops)
                {
                    var op = (opObj as List<Dictionary<string, object>>)?.FirstOrDefault();
                    if (op == null) continue;

                    if (op.ContainsKey("objectSpaceDeformer") || op.ContainsKey("boneSpaceDeformer"))
                        ParseSkinOperator(op, piece);
                    else if (op.GetValueOrDefault("simulateOpConfigs") is List<Dictionary<string, object>> cfgs &&
                             cfgs.FirstOrDefault() is Dictionary<string, object> cfg)
                    {
                        piece.SubSteps = Math.Max(1, Convert.ToInt32(cfg.GetValueOrDefault("subSteps") ?? 1));
                        piece.SolveIterations = Math.Max(1, Convert.ToInt32(cfg.GetValueOrDefault("numberOfSolveIterations") ?? 1));
                        piece.ConstraintExecution = IntArray(cfg.GetValueOrDefault("constraintExecution"))
                            .Where(i => i >= 0).ToArray();
                    }
                    else if (op.GetValueOrDefault("vertexParticlePairs") is List<Dictionary<string, object>> vpp)
                        piece.VertexParticlePairs = vpp.Select(p => (
                            Convert.ToInt32(p.GetValueOrDefault("vertexIndex") ?? 0),
                            Convert.ToInt32(p.GetValueOrDefault("particleIndex") ?? 0))).ToList();
                    else if (op.GetValueOrDefault("triangleBonePairs") is List<Dictionary<string, object>> tbp)
                    {
                        var localTransforms = (op.GetValueOrDefault("localBoneTransforms") as List<object>)?
                            .Select(m => Mat4(m)).ToArray() ?? Array.Empty<Matrix4>();
                        piece.BoneAxis = Convert.ToInt32(op.GetValueOrDefault("boneAxis") ?? 0);
                        for (int i = 0; i < tbp.Count; i++)
                        {
                            piece.BoneDeforms.Add(new HairBoneDeform
                            {
                                //boneOffset is a byte offset into the transform set (64 = one matrix).
                                BoneIndex = Convert.ToInt32(tbp[i].GetValueOrDefault("boneOffset") ?? 0) / 64,
                                //triangleOffset is a byte offset into triangleIndices (u16).
                                TriangleStart = Convert.ToInt32(tbp[i].GetValueOrDefault("triangleOffset") ?? 0) / 2,
                                LocalBoneTransform = i < localTransforms.Length ? localTransforms[i] : Matrix4.Identity,
                            });
                        }
                    }
                }
            }

            if (piece.SkinVertices == null) { Fail("no skin operator (SkinVertices)"); return null; }
            if (piece.RestPositions == null) { Fail("no rest positions"); return null; }

            //No simulate config found: execute every parsed set once, authored order.
            if (piece.ConstraintExecution.Length == 0)
                piece.ConstraintExecution = Enumerable.Range(0, piece.ConstraintSetKinds.Count).ToArray();
            return piece;
        }

        /// <summary>
        /// hclObjectSpaceSkinPOperator / hclBoneSpaceSkinPOperator: bone-skinned
        /// positions for all cloth vertices. Blend entries come in blocks of 16
        /// vertices with N bones per vertex. The bone-space variant stores local
        /// positions directly in bone space (no boneFromSkinMesh matrices) as
        /// unpacked float4s.
        /// </summary>
        static void ParseSkinOperator(Dictionary<string, object> op, HairClothPiece piece)
        {
            piece.TransformSubset = IntArray(op.GetValueOrDefault("transformSubset"));
            piece.BoneFromSkinMesh = (op.GetValueOrDefault("boneFromSkinMeshTransforms") as List<object>)?
                .Select(m => Mat4(m)).ToArray()
                //Bone-space skinning: local positions are authored in bone space.
                ?? Enumerable.Repeat(Matrix4.Identity, Math.Max(piece.TransformSubset.Length, 1)).ToArray();

            bool boneSpace = op.GetValueOrDefault("boneSpaceDeformer") is Dictionary<string, object>;
            var osd = op.GetValueOrDefault("objectSpaceDeformer") as Dictionary<string, object>
                ?? op.GetValueOrDefault("boneSpaceDeformer") as Dictionary<string, object>;
            if (osd == null)
                return;

            int endVertex = Convert.ToInt32(osd.GetValueOrDefault("endVertexIndex") ?? 0);
            var verts = new HairSkinVertex[endVertex + 1];

            //Local positions: blocks of 16 entries (see LocalBlockP). Object-space
            //packs hkPackedVector3 (4 raw u16 bit patterns, one shared position per
            //vertex); bone-space stores plain float4 vectors, one PER BLEND SLOT
            //(layout v*N+b within the block) with the blend weight in W.
            var localPs = new List<Vector4>();
            if (op.GetValueOrDefault("localPs") is List<Dictionary<string, object>> blocks)
            {
                foreach (var block in blocks)
                {
                    if (block.GetValueOrDefault("localPosition") is not List<object> packed)
                        continue;
                    foreach (var entry in packed)
                    {
                        var vals = entry as List<object>
                            ?? (entry as Dictionary<string, object>)?.GetValueOrDefault("values") as List<object>;
                        if (vals == null || vals.Count != 4)
                            continue;
                        if (vals[0] is float or double)
                            localPs.Add(new Vector4(Convert.ToSingle(vals[0]), Convert.ToSingle(vals[1]),
                                Convert.ToSingle(vals[2]), Convert.ToSingle(vals[3])));
                        else
                            localPs.Add(new Vector4(UnpackVector3(vals), 1));
                    }
                }
            }

            //Blend entry blocks of each type, consumed in controlBytes order (each
            //control byte = the blend count of the next 16-vertex block). Local
            //positions are stored per block in that same order.
            var queues = new Dictionary<int, Queue<Dictionary<string, object>>>();
            for (int n = 1; n <= 8; n++)
            {
                string key = n switch
                {
                    1 => "oneBlendEntries", 2 => "twoBlendEntries", 3 => "threeBlendEntries",
                    4 => "fourBlendEntries", 5 => "fiveBlendEntries", 6 => "sixBlendEntries",
                    7 => "sevenBlendEntries", _ => "eightBlendEntries",
                };
                if (osd.GetValueOrDefault(key) is List<Dictionary<string, object>> entries)
                    queues[n] = new Queue<Dictionary<string, object>>(entries);
            }

            //controlBytes gives the entry type of each consecutive 16-vertex block
            //(and thereby the order of the local-position blocks). Enum values:
            //0=four, 1=three, 2=two, 3=one blend (5..8 blends appended as 4..7).
            int[] blendCountForControl = { 4, 3, 2, 1, 8, 7, 6, 5 };
            int[] controlBytes = IntArray(osd.GetValueOrDefault("controlBytes"))
                .Where(b => b >= 0 && b < 8).Select(b => blendCountForControl[b]).ToArray();
            if (controlBytes.Length == 0)
            {
                //Single-type deformers can omit control bytes.
                controlBytes = queues.OrderByDescending(kv => kv.Key)
                    .SelectMany(kv => Enumerable.Repeat(kv.Key, kv.Value.Count)).ToArray();
            }

            int globalBlock = 0;
            foreach (int bonesPerVertex in controlBytes)
            {
                if (!queues.TryGetValue(bonesPerVertex, out var queue) || queue.Count == 0)
                    { globalBlock++; continue; }
                var block = queue.Dequeue();

                int[] vertexIndices = IntArray(block.GetValueOrDefault("vertexIndices"));
                int[] boneIndices = IntArray(block.GetValueOrDefault("boneIndices"));
                int[] weights = IntArray(block.GetValueOrDefault("boneWeights"));
                for (int v = 0; v < vertexIndices.Length && v < 16; v++)
                {
                    int vi = vertexIndices[v];
                    if (vi > endVertex || verts[vi] != null) continue;
                    var sv = new HairSkinVertex
                    {
                        Bones = new int[bonesPerVertex],
                        Weights = new float[bonesPerVertex],
                    };
                    if (boneSpace)
                    {
                        //Bone-space: one float4 position PER BLEND SLOT (AoS, N
                        //consecutive slots per vertex), weight in W. Positions are
                        //per bone, so store them per slot.
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
                        //Object-space: one shared packed position per vertex; byte
                        //weights of one vertex sum to 255. One-blend blocks have no
                        //weight array (implicit 1).
                        int localIdx = globalBlock * 16 + v;
                        sv.LocalPos = localIdx < localPs.Count ? localPs[localIdx].Xyz : Vector3.Zero;
                        for (int b = 0; b < bonesPerVertex; b++)
                        {
                            int idx = v * bonesPerVertex + b;
                            sv.Bones[b] = idx < boneIndices.Length ? boneIndices[idx] : 0;
                            sv.Weights[b] = idx < weights.Length ? weights[idx] / 255.0f
                                : (bonesPerVertex == 1 ? 1.0f : 0);
                        }
                    }
                    verts[vi] = sv;
                }
                globalBlock++;
            }

            piece.SkinVertices = verts;
        }

        /// <summary>
        /// Unpacks an hkPackedVector3: 4 signed 16-bit values where xyz are mantissas
        /// and w is a shared exponent, decoded as f[i] = (values[i] &lt;&lt; 16 as s32
        /// -&gt; float) * bitcast_float(values[3] &lt;&lt; 16).
        /// </summary>
        static Vector3 UnpackVector3(List<object> rawBits)
        {
            short[] v = rawBits.Select(x => unchecked((short)(Convert.ToInt32(x) & 0xFFFF))).ToArray();
            float exp = BitConverter.Int32BitsToSingle(v[3] << 16);
            return new Vector3(
                (float)(v[0] << 16) * exp,
                (float)(v[1] << 16) * exp,
                (float)(v[2] << 16) * exp);
        }

        #region graph helpers

        static string Str(Dictionary<string, object> d, string key) =>
            d.GetValueOrDefault(key) as string;

        static float F(Dictionary<string, object> d, string key) =>
            d.GetValueOrDefault(key) is object v ? Convert.ToSingle(v) : 0;

        static Dictionary<string, object> FirstRecord(Dictionary<string, object> d, string key)
        {
            //Arrays of pointers decode as List<object> of List<Dictionary> (one record
            //per pointed-to item); plain pointers decode directly to the record list.
            var v = d.GetValueOrDefault(key);
            if (v is List<Dictionary<string, object>> recs)
                return recs.FirstOrDefault();
            if (v is List<object> list)
                return (list.FirstOrDefault() as List<Dictionary<string, object>>)?.FirstOrDefault();
            return null;
        }

        static int[] IntArray(object v)
        {
            if (v is List<object> list)
                return list.Select(x => Convert.ToInt32(x)).ToArray();
            return Array.Empty<int>();
        }

        static Vector3 Vec3(object v, Vector3? def = null)
        {
            if (v is List<object> list && list.Count >= 3)
                return new Vector3(Convert.ToSingle(list[0]), Convert.ToSingle(list[1]), Convert.ToSingle(list[2]));
            return def ?? Vector3.Zero;
        }

        static Quaternion Quat(object v)
        {
            //hkQuaternionf decodes as a tuple containing one vec4 or as a flat vec4.
            if (v is List<object> list)
            {
                if (list.Count == 4 && list[0] is not List<object>)
                    return new Quaternion(Convert.ToSingle(list[0]), Convert.ToSingle(list[1]),
                        Convert.ToSingle(list[2]), Convert.ToSingle(list[3]));
                if (list.FirstOrDefault() is List<object> inner && inner.Count >= 4)
                    return new Quaternion(Convert.ToSingle(inner[0]), Convert.ToSingle(inner[1]),
                        Convert.ToSingle(inner[2]), Convert.ToSingle(inner[3]));
            }
            return Quaternion.Identity;
        }

        /// <summary>
        /// 16 floats, column vectors (hkMatrix4/hkTransform): col0..col3 where col3 is
        /// the translation. Converted to OpenTK row-vector convention (transpose).
        /// </summary>
        static Matrix4 Mat4(object v)
        {
            if (v is not List<object> list || list.Count < 16)
                return Matrix4.Identity;
            float[] f = list.Select(Convert.ToSingle).ToArray();
            return new Matrix4(
                f[0], f[1], f[2], 0,
                f[4], f[5], f[6], 0,
                f[8], f[9], f[10], 0,
                f[12], f[13], f[14], 1);
        }

        #endregion
    }

    public class HairClothPiece
    {
        public string Name;

        //Cloth skeleton (bone 0 = attach root e.g. Head_Root, last often Spine_3).
        public string[] BoneNames;
        public int[] BoneParents;
        public Matrix4[] BoneRefPose;    //model space

        //Skinning (drives fixed particles + local range references)
        public Matrix4[] BoneFromSkinMesh;
        public int[] TransformSubset;    //transform-set indices used by the skin
        public HairSkinVertex[] SkinVertices;

        //Simulation
        public HairParticle[] Particles;
        public int[] FixedParticles;
        public Vector3[] RestPositions;
        public int[] TriangleIndices;
        public Vector3 Gravity;
        public float DampingPerSecond;
        public List<(int Vertex, int Particle)> VertexParticlePairs = new();

        public List<string> ConstraintSetNames = new(); //debug: all authored sets
        public List<HairConstraintKind> ConstraintSetKinds = new(); //index-aligned with staticConstraintSets
        public List<HairLink> StandardLinks = new();
        public List<HairLink> StretchLinks = new();
        public List<HairLink> BendLinks = new();
        public List<HairLocalRange> LocalRanges = new();
        public List<HairCollidable> Collidables = new();

        //Authored solver config (hclSimulateOperator::Config)
        public int SubSteps = 1;
        public int SolveIterations = 1;
        public int[] ConstraintExecution = Array.Empty<int>(); //staticConstraintSets indices, in solve order

        //Bone write-back (simulated triangles -> bone transforms)
        public List<HairBoneDeform> BoneDeforms = new();
        public int BoneAxis;
    }

    public enum HairConstraintKind
    {
        Unknown, Standard, Stretch, Bend, LocalRange, Transition,
    }

    public class HairParticle
    {
        public float InvMass, Radius, Friction;
    }

    public class HairSkinVertex
    {
        public int[] Bones;      //indices into TransformSubset
        public float[] Weights;
        public Vector3 LocalPos; //skin-mesh space (object-space deformer)
        public Vector3[] LocalPosPerBone; //bone space, one per blend slot (bone-space deformer)
    }

    public class HairLink
    {
        public int A, B;
        public float RestLength, Stiffness;
        public float MinLength, MaxLength, BendStiffness, StretchStiffness;
    }

    public class HairLocalRange
    {
        public int Particle, ReferenceVertex;
        public float Radius;
        public float MaxNormal, MinNormal;
        public float Stiffness = 1.0f;
    }

    public class HairCollidable
    {
        public string Name;
        public Vector3 Start, End;
        public float Radius;
        public Matrix4 Transform;    //rest/reference transform
        public int BoneIndex;        //transform-set index the capsule follows
        public Matrix4 BoneOffset;   //offset from the bone to collidable space
    }

    public class HairBoneDeform
    {
        public int BoneIndex;           //transform-set index to write
        public int TriangleStart;       //index into TriangleIndices (start of 3)
        public Matrix4 LocalBoneTransform;
    }
}
