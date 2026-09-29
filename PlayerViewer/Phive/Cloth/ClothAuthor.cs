using System;
using System.Collections.Generic;
using System.Linq;
using OpenTK;

namespace PlayerViewer.Phive
{
    /// <summary>A bone of the model a cloth is authored on: its name, parent index and rest world matrix (model space, row vectors).</summary>
    public record AuthorBone(string Name, int Parent, Matrix4 World);

    /// <summary>The physics parameters of a generated piece, shared by a strip spec and a piece layout.</summary>
    public class PieceParams
    {
        public string Name = "Cloth_New";
        public float Mass = 1;
        public float Radius = 0.02f;
        public float Friction = 0.5f;
        public float Gravity = 9.81f;
        public float Damping = 0.001f;
        public float LinkStiffness = 1;
        public bool Stretch = true;
        public bool Bend = true;
        public float BendRatio = 0.8f;
        public bool LocalRange = true;
        public float RangeSetStiffness = 0.6f;
        public int Iterations = 1;

        /// <summary>Container collidables the piece collides with.</summary>
        public List<Collidable> Collidables = new();

        /// <summary>Takes every parameter of another; the collidable list is shared.</summary>
        public void CopyParams(PieceParams from)
        {
            Name = from.Name;
            Mass = from.Mass;
            Radius = from.Radius;
            Friction = from.Friction;
            Gravity = from.Gravity;
            Damping = from.Damping;
            LinkStiffness = from.LinkStiffness;
            Stretch = from.Stretch;
            Bend = from.Bend;
            BendRatio = from.BendRatio;
            LocalRange = from.LocalRange;
            RangeSetStiffness = from.RangeSetStiffness;
            Iterations = from.Iterations;
            Collidables = from.Collidables;
        }
    }

    /// <summary>
    /// What a new strip piece is generated from. Each chain is a run of model bones from the
    /// root down; it becomes a strip two particles wide with one level per chain bone, a fixed
    /// level before the first bone skinned to the anchor, and every chain bone driven.
    /// </summary>
    public class StripSpec : PieceParams
    {
        public int Anchor = -1;
        public List<int[]> Chains = new();

        /// <summary>Strip width across each level.</summary>
        public float Width = 0.06f;

        /// <summary>The fixed level sits this fraction of the first segment before the first chain bone.</summary>
        public float RootOffset = 0.6f;

        /// <summary>An extra free level past the last bone, as a fraction of the last segment; 0 for none.</summary>
        public float TipLength = 0.5f;

        /// <summary>Turns the strip about the chain, degrees.</summary>
        public float Roll;

        public float RangeRoot = 0.45f;
        public float RangeTip = 0.95f;
    }

    /// <summary>
    /// A piece spelled out particle by particle, which <see cref="ClothAuthor.AddPiece"/> turns
    /// into the exporter's object graph. Particles are skinned rigidly to one model bone each.
    /// </summary>
    public class PieceLayout : PieceParams
    {
        public int Anchor;

        public readonly List<Vector3> Positions = new();

        /// <summary>The model bone each particle is skinned to.</summary>
        public readonly List<int> Bone = new();

        public readonly List<int> Chain = new();
        public readonly List<int> Level = new();

        /// <summary>The side of its strip each particle is on; a stretch link ties it to the fixed particle on the same side.</summary>
        public readonly List<int> Side = new();

        public readonly HashSet<int> Fixed = new();
        public readonly List<(int A, int B, int C)> Triangles = new();

        /// <summary>Each driven model bone and the triangle whose frame it follows.</summary>
        public readonly List<(int Bone, int Triangle)> Driven = new();

        /// <summary>The local range radius of each particle; negative for none.</summary>
        public readonly List<float> RangeRadius = new();

        /// <summary>A per particle factor on the range stiffness; empty for none.</summary>
        public readonly List<float> RangeStiffness = new();

        /// <summary>The AAMP preset name, or null to keep the default.</summary>
        public string Preset;

        /// <summary>The model bone each collidable rides on, by index; missing or negative ones go to the bone nearest the collidable.</summary>
        public List<int> CollidableBones = new();

        public int AddParticle(
            Vector3 position,
            int bone,
            int chain,
            int level,
            int side,
            bool isFixed
        )
        {
            Positions.Add(position);
            Bone.Add(bone);
            Chain.Add(chain);
            Level.Add(level);
            Side.Add(side);
            if (isFixed)
                Fixed.Add(Positions.Count - 1);
            return Positions.Count - 1;
        }

        /// <summary>Two triangles per pair of levels of a strip two particles wide, starting at particle <paramref name="start"/>.</summary>
        public void AddStripTriangles(int start, int levels)
        {
            int P(int level, int s) => start + level * 2 + s;
            for (int l = 0; l + 1 < levels; l++)
            {
                Triangles.Add((P(l, 0), P(l, 1), P(l + 1, 0)));
                Triangles.Add((P(l + 1, 0), P(l, 1), P(l + 1, 1)));
            }
        }

        /// <summary>The triangle that drives a bone at a level of a strip: the first of the pair below it, or the last pair's second at the tip.</summary>
        public static int DrivingTriangle(int triStart, int level, int levels) =>
            level + 1 < levels ? triStart + 2 * level : triStart + 2 * (level - 1) + 1;
    }

    /// <summary>
    /// Builds new cloth content into a file's object graph by the rules the stock files were
    /// exported with: strip pieces from bone chains, collidables on bones, and an empty cloth
    /// file from a stock one.
    /// </summary>
    public static class ClothAuthor
    {
        /// <summary>The pinch detection radius the exporter writes on every collidable.</summary>
        const float PinchDetectionRadius = 0.01f;

        /// <summary>The name prefix of a piece's transform set and skeleton.</summary>
        const string SkeletonPrefix = "cloth_skeleton_";

        /// <summary>The classes a generated strip piece is built from; a file must declare them all first.</summary>
        public static readonly string[] PieceTypes =
        {
            "hclClothData",
            "hclSimClothData",
            "hclSimClothData::ParticleData",
            "hclSimClothData::CollidablePinchingData",
            "hclSimClothPose",
            "hclStandardLinkConstraintSet",
            "hclStandardLinkConstraintSet::Link",
            "hclStretchLinkConstraintSet",
            "hclStretchLinkConstraintSet::Link",
            "hclBendLinkConstraintSet",
            "hclBendLinkConstraintSet::Link",
            "hclLocalRangeConstraintSet",
            "hclLocalRangeConstraintSet::LocalConstraint",
            "hclLocalRangeConstraintSet::LocalStiffnessConstraint",
            "hclTransitionConstraintSet",
            "hclTransitionConstraintSet::PerParticle",
            "hclScratchBufferDefinition",
            "hclBufferDefinition",
            "hclTransformSetDefinition",
            "hclBoneSpaceSkinPOperator",
            "hclBoneSpaceDeformer::OneBlendEntryBlock",
            "hclBoneSpaceDeformer::LocalBlockP",
            "hclMoveParticlesOperator",
            "hclMoveParticlesOperator::VertexParticlePair",
            "hclSimulateOperator",
            "hclSimulateOperator::Config",
            "hclSimpleMeshBoneDeformOperator",
            "hclSimpleMeshBoneDeformOperator::TriangleBonePair",
            "hclCopyVerticesOperator",
            "hclClothState",
            "hclStateDependencyGraph",
            "hkaSkeleton",
            "hkaBone",
        };

        public static readonly string[] CollidableTypes =
        {
            "hclCollidable",
            "hclCapsuleShape",
            "hclSphereShape",
        };

        /// <summary>
        /// Empties a cloth file so it can take new pieces: no pieces, no collidables, no
        /// skeletons, no AAMP entries. Its TYPE section, header and root layout stay.
        /// </summary>
        public static void Clear(ClothFile file)
        {
            file.Container.Source.Array("clothDatas").Clear();
            file.Container.Source.Array("collidables").Clear();
            file.AnimationContainer?.Array("skeletons").Clear();
            if (file.Params != null)
                foreach (var list in file.Params.Aamp.Root.Lists)
                    list.Objects.Clear();
        }

        /// <summary>
        /// Adds a collidable to the container (kept sorted by name, as the exporter lists them)
        /// with its rest transform at the bone, and an AAMP entry for it. The shape is given in
        /// the bone's space: capsule ends and radius, or a sphere's centre and radius.
        /// </summary>
        public static Collidable AddCollidable(
            ClothFile file,
            string name,
            CollidableShapeKind kind,
            Matrix4 boneWorld,
            Vector3 start,
            Vector3 end,
            float radius
        )
        {
            var col = file.New("hclCollidable");
            col.Set("transform", HkValue.FromMatrix(HkValue.Affine(boneWorld)));
            col.Set("name", name);
            col.Set("pinchDetectionRadius", PinchDetectionRadius);
            col.Set("enabled", true);
            HkObject shape;
            if (kind == CollidableShapeKind.Sphere)
            {
                shape = file.New("hclSphereShape");
                col.Set("shape", shape);
                ClothEdit.SetSphere(new Collidable(col), start, radius);
            }
            else
            {
                shape = file.New("hclCapsuleShape");
                col.Set("shape", shape);
                ClothEdit.SetCapsule(new Collidable(col), start, end, radius);
            }
            var list = file.Container.Source.Array("collidables");
            int at = 0;
            while (
                at < list.Count
                && string.CompareOrdinal(((HkObject)list[at]).String("name"), name) < 0
            )
                at++;
            list.Insert(at, col);
            file.Params?.AddCollidable(name);
            return new Collidable(col);
        }

        /// <summary>Renames a collidable and its AAMP entry. Pieces point at the instance, so they follow.</summary>
        public static void RenameCollidable(ClothFile file, Collidable collidable, string name)
        {
            var p = file.Params?.CollidableFor(collidable.Name);
            collidable.Name = name;
            if (p != null)
                p.Name = name;
        }

        /// <summary>Removes a collidable from the container, every piece that uses it, and the AAMP.</summary>
        public static void RemoveCollidable(ClothFile file, Collidable collidable)
        {
            foreach (var piece in file.Container.ClothDatas)
                Detach(piece, collidable);
            file.Container.Source.Array("collidables").Remove(collidable.Source);
            file.Params?.RemoveCollidable(collidable.Name);
        }

        /// <summary>
        /// Lets a piece collide with a collidable: its per instance list, the transform map entry
        /// (the bone it follows, added to the piece skeleton when missing, and the offset from
        /// that bone), a pinching record, and the masks and usage that follow.
        /// </summary>
        public static void Attach(
            ClothFile file,
            ClothData piece,
            Collidable collidable,
            IReadOnlyList<AuthorBone> model,
            int modelBone
        )
        {
            var sim = piece.SimClothDatas[0];
            if (sim.PerInstanceCollidables.Any(c => c.Source == collidable.Source))
                return;
            int clothBone = EnsureBone(file, piece, model, modelBone);
            AddCollidable(sim.Source, collidable, clothBone, model[modelBone].World);
            Refresh(piece);
        }

        /// <summary>A collidable in a piece's list: the cloth bone it follows, its offset from that bone and a pinching record.</summary>
        static void AddCollidable(
            HkObject sim,
            Collidable collidable,
            int clothBone,
            Matrix4 boneWorld
        )
        {
            sim.Array("perInstanceCollidables").Add(collidable.Source);
            var map = sim.Object("collidableTransformMap");
            map.Array("transformIndices").AddValue(clothBone);
            map.Array("offsets").Add(HkValue.FromMatrix(CollidableOffset(collidable, boneWorld)));
            var pinch = sim.Array("collidablePinchingDatas");
            if (pinch.ElementType != null)
            {
                var record = HkObject.Create(pinch.ElementType);
                record.Set("pinchDetectionRadius", PinchDetectionRadius);
                pinch.Add(record);
            }
        }

        /// <summary>A collidable's offset from the bone it follows: its rest transform in that bone's space.</summary>
        public static Matrix4 CollidableOffset(Collidable collidable, Matrix4 boneWorld) =>
            HkValue.Affine(collidable.Transform) * Matrix4.Invert(boneWorld);

        public static void Detach(ClothData piece, Collidable collidable)
        {
            var sim = piece.SimClothDatas[0];
            var list = sim.Source.Array("perInstanceCollidables");
            int index = list.IndexOf(collidable.Source);
            if (index < 0)
                return;
            list.RemoveAt(index);
            var map = sim.CollidableTransformMap;
            RemoveAtIfPresent(map.Array("transformIndices"), index);
            RemoveAtIfPresent(map.Array("offsets"), index);
            RemoveAtIfPresent(sim.Source.Array("collidablePinchingDatas"), index);
            Refresh(piece);
        }

        static void RemoveAtIfPresent(HkArray array, int index)
        {
            if (index < array.Count)
                array.RemoveAt(index);
        }

        /// <summary>Views are rebuilt from the graph by the caller; these are the derived values a collidable change moves.</summary>
        static void Refresh(ClothData piece)
        {
            var fresh = new ClothData(piece.Source);
            ClothEdit.RecomputeCollisionMasks(fresh.SimClothDatas[0]);
            ClothEdit.RecomputeTransformUsage(fresh);
        }

        /// <summary>The cloth bone index of a model bone in a piece's skeleton, appended (parented to its nearest cloth ancestor) when missing.</summary>
        public static int EnsureBone(
            ClothFile file,
            ClothData piece,
            IReadOnlyList<AuthorBone> model,
            int modelBone
        )
        {
            var skeleton = file.SkeletonFor(piece);
            string name = model[modelBone].Name;
            var names = skeleton.BoneNames;
            int existing = Array.IndexOf(names, name);
            if (existing >= 0)
                return existing;

            int parent = ClothParent(model, modelBone, a => Array.IndexOf(names, model[a].Name));
            AppendBone(
                skeleton.Source,
                name,
                parent,
                model[modelBone].World,
                parent >= 0 ? WorldOf(model, names[parent]) : Matrix4.Identity
            );
            int index = names.Length;
            int count = index + 1;
            foreach (var def in piece.TransformSetDefinitions)
                def.Source.Set("numTransforms", count);
            ResizeTrackers(piece, count);
            return index;
        }

        static Matrix4 WorldOf(IReadOnlyList<AuthorBone> model, string name) =>
            model.FirstOrDefault(b => b.Name == name)?.World ?? Matrix4.Identity;

        /// <summary>The cloth index of a model bone's nearest ancestor in the cloth skeleton, -1 for none.</summary>
        static int ClothParent(
            IReadOnlyList<AuthorBone> model,
            int bone,
            Func<int, int> clothIndexOf
        )
        {
            int parent = -1;
            for (int a = model[bone].Parent; a >= 0 && parent < 0; a = model[a].Parent)
                parent = clothIndexOf(a);
            return parent;
        }

        /// <summary>Appends a bone to a cloth skeleton with its reference pose relative to its parent.</summary>
        static void AppendBone(
            HkObject skeleton,
            string name,
            int parent,
            Matrix4 world,
            Matrix4 parentWorld
        )
        {
            var bone = HkObject.Create(skeleton.Array("bones").ElementType);
            bone.Set("name", name);
            skeleton.Array("bones").Add(bone);
            skeleton.Array("parentIndices").AddValue(parent);
            var pose = skeleton.Array("referencePose");
            pose.Add(QsTransform(pose.ElementType, world * Matrix4.Invert(parentWorld)));
        }

        /// <summary>Every tracker of every operator and state widened to the new bone count.</summary>
        static void ResizeTrackers(ClothData piece, int bones)
        {
            IEnumerable<HkObject> accesses = piece
                .Operators.SelectMany(o => o.Source.Array("usedTransformSets").Objects)
                .Concat(
                    piece.ClothStates.Objects.SelectMany(s => s.Array("usedTransformSets").Objects)
                );
            foreach (var access in accesses)
            {
                var trackers = access
                    .Object("transformSetUsage")
                    ?.Array("perComponentTransformTrackers");
                if (trackers == null)
                    continue;
                foreach (var t in trackers.Objects)
                foreach (var which in ClothEdit.TrackerBitSets)
                    ClothEdit.ResizeBits(t.Object(which), bones);
            }
        }

        static HkObject QsTransform(HkType type, Matrix4 local)
        {
            var qs = HkObject.Create(type);
            Vector3 scale = new(
                local.Row0.Xyz.Length,
                local.Row1.Xyz.Length,
                local.Row2.Xyz.Length
            );
            var rotation = local.ExtractRotation();
            qs.Set("translation", HkValue.FromVector(new Vector4(local.Row3.Xyz, 0)));
            qs.Set("rotation", new[] { rotation.X, rotation.Y, rotation.Z, rotation.W });
            qs.Set("scale", HkValue.FromVector(new Vector4(scale, 1)));
            return qs;
        }

        /// <summary>
        /// Generates a strip piece and adds it with its skeleton and AAMP entry. The file must
        /// already declare <see cref="PieceTypes"/>. Returns the new piece.
        /// </summary>
        public static ClothData AddStripPiece(
            ClothFile file,
            StripSpec spec,
            IReadOnlyList<AuthorBone> model
        )
        {
            if (spec.Chains.Count == 0 || spec.Chains.Any(c => c.Length == 0))
                throw new ArgumentException("A strip needs at least one chain of bones");
            if (spec.Anchor < 0)
                spec.Anchor = model[spec.Chains[0][0]].Parent;
            if (spec.Anchor < 0)
                throw new ArgumentException("The chain's first bone has no parent to anchor to");

            var layout = new PieceLayout { Anchor = spec.Anchor };
            layout.CopyParams(spec);
            for (int c = 0; c < spec.Chains.Count; c++)
            {
                int[] chain = spec.Chains[c];
                var origins = chain.Select(b => model[b].World.Row3.Xyz).ToList();
                var levels = new List<(Vector3 Origin, int Bone, Vector3 Dir)>();
                Vector3 first =
                    origins.Count > 1
                        ? origins[1] - origins[0]
                        : origins[0] - model[spec.Anchor].World.Row3.Xyz;
                if (first.LengthSquared < 1e-10f)
                    first = -Vector3.UnitY * 0.1f;
                levels.Add((origins[0] - first * spec.RootOffset, spec.Anchor, first.Normalized()));
                for (int i = 0; i < chain.Length; i++)
                {
                    Vector3 dir =
                        i + 1 < chain.Length ? origins[i + 1] - origins[i]
                        : i > 0 ? origins[i] - origins[i - 1]
                        : first;
                    levels.Add(
                        (
                            origins[i],
                            chain[i],
                            dir.LengthSquared > 1e-10f ? dir.Normalized() : Vector3.UnitY
                        )
                    );
                }
                if (spec.TipLength > 0)
                {
                    Vector3 last = chain.Length > 1 ? origins[^1] - origins[^2] : first;
                    levels.Add((origins[^1] + last * spec.TipLength, chain[^1], last.Normalized()));
                }

                int start = layout.Positions.Count;
                for (int l = 0; l < levels.Count; l++)
                {
                    var (origin, bone, dir) = levels[l];
                    Vector3 side = SideAxis(model[bone].World, dir, spec.Roll);
                    for (int s = 0; s < 2; s++)
                        layout.AddParticle(
                            origin + side * (s == 0 ? -0.5f : 0.5f) * spec.Width,
                            bone,
                            c,
                            l,
                            s,
                            l == 0
                        );
                }
                layout.AddStripTriangles(start, levels.Count);
                int triStart = layout.Triangles.Count - 2 * (levels.Count - 1);
                for (int i = 0; i < chain.Length; i++)
                    layout.Driven.Add(
                        (chain[i], PieceLayout.DrivingTriangle(triStart, i + 1, levels.Count))
                    );
            }

            //Radii grow from the root to the tip of each chain, as a fraction of the distance to its fixed level.
            for (int p = 0; p < layout.Positions.Count; p++)
            {
                if (layout.Fixed.Contains(p) || !spec.LocalRange)
                {
                    layout.RangeRadius.Add(-1);
                    continue;
                }
                var roots = layout.Fixed.Where(f => layout.Chain[f] == layout.Chain[p]).ToList();
                Vector3 rootCentre =
                    roots.Aggregate(Vector3.Zero, (acc, f) => acc + layout.Positions[f])
                    / roots.Count;
                int levels =
                    layout.Level.Where((_, i) => layout.Chain[i] == layout.Chain[p]).Max() + 1;
                float t = levels > 2 ? (layout.Level[p] - 1) / (float)(levels - 2) : 1;
                layout.RangeRadius.Add(
                    (spec.RangeRoot + (spec.RangeTip - spec.RangeRoot) * t)
                        * (layout.Positions[p] - rootCentre).Length
                );
            }
            return AddPiece(file, layout, model);
        }

        /// <summary>
        /// Builds a piece from a layout and adds it with its skeleton and AAMP entry, by the
        /// exporter's rules. The file must already declare <see cref="PieceTypes"/>.
        /// </summary>
        public static ClothData AddPiece(
            ClothFile file,
            PieceLayout layout,
            IReadOnlyList<AuthorBone> model
        )
        {
            var positions = layout.Positions;
            var particleBone = layout.Bone;
            var fixedParticles = layout.Fixed.OrderBy(x => x).ToList();
            var fixedSet = layout.Fixed;
            var triangles = layout.Triangles;
            var driven = layout.Driven;
            int n = positions.Count;
            if (n == 0 || fixedParticles.Count == 0)
                throw new ArgumentException("A piece needs particles and at least one fixed one");
            if (layout.Collidables.Count > ClothEdit.MaxCollidables)
                throw new ArgumentException(
                    $"A piece collides with at most {ClothEdit.MaxCollidables} collidables"
                );

            //The cloth skeleton: every bone the piece uses, in model order, under its nearest included ancestor.
            var needed = new SortedSet<int> { layout.Anchor };
            needed.UnionWith(particleBone);
            needed.UnionWith(driven.Select(d => d.Bone));
            var collidableBones = new List<int>();
            for (int i = 0; i < layout.Collidables.Count; i++)
            {
                int b =
                    i < layout.CollidableBones.Count && layout.CollidableBones[i] >= 0
                        ? layout.CollidableBones[i]
                        : NearestBone(model, HkValue.Affine(layout.Collidables[i].Transform));
                collidableBones.Add(b);
                needed.Add(b);
            }
            var clothBones = needed.ToList();
            var clothIndex = clothBones.Select((b, i) => (b, i)).ToDictionary(x => x.b, x => x.i);
            int nb = clothBones.Count;

            string name = layout.Name;
            var skeleton = file.New("hkaSkeleton");
            skeleton.Set("name", SkeletonPrefix + name);
            foreach (int b in clothBones)
            {
                int parent = ClothParent(
                    model,
                    b,
                    a => clothIndex.TryGetValue(a, out int pi) ? pi : -1
                );
                AppendBone(
                    skeleton,
                    model[b].Name,
                    parent,
                    model[b].World,
                    parent >= 0 ? model[clothBones[parent]].World : Matrix4.Identity
                );
            }

            //Particles.
            int free = n - fixedParticles.Count;
            float mass = free > 0 ? layout.Mass * (1.0f / free) : 0;
            var inv = new float[n];
            var sim = file.New("hclSimClothData");
            sim.Set("name", "Simulation");
            var info = sim.Object("simulationInfo");
            info.Set("gravity", HkValue.FromVector(new Vector4(0, -layout.Gravity, 0, 0)));
            info.Set("globalDampingPerSecond", layout.Damping);
            for (int p = 0; p < n; p++)
            {
                var pd = new ParticleData(HkObject.Create(sim.Array("particleDatas").ElementType));
                pd.SetMass(fixedSet.Contains(p) ? 0 : mass);
                inv[p] = pd.InvMass;
                pd.Radius = layout.Radius;
                pd.Friction = layout.Friction;
                sim.Array("particleDatas").Add(pd.Source);
            }
            foreach (int f in fixedParticles)
                sim.Array("fixedParticles").AddValue(f);
            sim.Array("simOpIds").AddValue(2);
            var pose = file.New("hclSimClothPose");
            pose.Set("name", "DefaultClothPose");
            foreach (var p in positions)
                pose.Array("positions").Add(HkValue.FromVector(new Vector4(p, 1)));
            sim.Array("simClothPoses").Add(pose);

            //Constraint sets in the stock order, executed as the stock strips execute them.
            var sets = new List<(string Key, HkObject Set)>();
            var standard = file.New("hclStandardLinkConstraintSet");
            standard.Set("name", "Standard Links");
            var edges = TriangleEdges(triangles)
                .Where(e => !(fixedSet.Contains(e.A) && fixedSet.Contains(e.B)))
                .ToList();
            foreach (var (a, b) in edges)
            {
                var link = HkObject.Create(standard.Array("links").ElementType);
                link.Set("particleA", a);
                link.Set("particleB", b);
                link.Set("restLength", (positions[a] - positions[b]).Length);
                link.Set(
                    "stiffness",
                    ClothEdit.LinkStiffness(layout.LinkStiffness, inv[a] + inv[b])
                );
                standard.Array("links").Add(link);
            }
            sets.Add(("standard", standard));
            if (layout.Stretch)
            {
                var stretch = file.New("hclStretchLinkConstraintSet");
                stretch.Set("name", "Stretch Link");
                for (int p = 0; p < n; p++)
                {
                    if (fixedSet.Contains(p))
                        continue;
                    int root = fixedParticles
                        .Where(f => layout.Chain[f] == layout.Chain[p])
                        .OrderBy(f => layout.Side[f] == layout.Side[p] ? 0 : 1)
                        .First();
                    var link = HkObject.Create(stretch.Array("links").ElementType);
                    link.Set("particleA", root);
                    link.Set("particleB", p);
                    link.Set("restLength", (positions[root] - positions[p]).Length);
                    link.Set("stiffness", 1.0f);
                    stretch.Array("links").Add(link);
                }
                sets.Add(("stretch", stretch));
            }
            if (layout.LocalRange)
            {
                var range = file.New("hclLocalRangeConstraintSet");
                range.Set("name", "Local Range");
                var ranged = Enumerable
                    .Range(0, n)
                    .Where(p => !fixedSet.Contains(p) && layout.RangeRadius[p] >= 0)
                    .ToList();
                float Scale(int p) =>
                    p < layout.RangeStiffness.Count ? layout.RangeStiffness[p] : 1.0f;
                var perParticle = range.Array("localStiffnessConstraints");
                bool varies =
                    ranged.Select(Scale).Distinct().Count() > 1
                    && perParticle.ElementType?.FieldIndex("stiffness") >= 0;
                float peak = varies ? ranged.Max(Scale) : 1;
                range.Set(
                    "stiffness",
                    layout.RangeSetStiffness
                        * (varies ? peak : ranged.Select(Scale).DefaultIfEmpty(1).First())
                );
                var records = varies ? perParticle : range.Array("localConstraints");
                foreach (int p in ranged)
                {
                    var entry = HkObject.Create(records.ElementType);
                    entry.Set("particleIndex", p);
                    entry.Set("referenceVertex", p);
                    entry.Set("shapeRadius", layout.RangeRadius[p]);
                    entry.Set("maxNormalDistance", ClothEdit.FltMaxExported);
                    entry.Set("minNormalDistance", -ClothEdit.FltMaxExported);
                    if (varies)
                        entry.Set("stiffness", Scale(p) / peak);
                    records.Add(entry);
                }
                sets.Add(("localrange", range));
            }
            var transition = file.New("hclTransitionConstraintSet");
            transition.Set("name", "Transition");
            for (int p = 0; p < n; p++)
            {
                var entry = HkObject.Create(transition.Array("perParticleData").ElementType);
                entry.Set("particleIndex", p);
                entry.Set("referenceVertex", p);
                entry.Set("toSimMaxDistance", 1.0f);
                transition.Array("perParticleData").Add(entry);
            }
            CopyTransitionPeriods(file, transition);
            sets.Add(("transition", transition));
            if (layout.Bend)
            {
                var bend = file.New("hclBendLinkConstraintSet");
                bend.Set("name", "Bend Links");
                foreach (
                    var (a, b) in BendPairs(triangles, edges)
                        .Where(e => !(fixedSet.Contains(e.A) && fixedSet.Contains(e.B)))
                )
                {
                    var link = HkObject.Create(bend.Array("links").ElementType);
                    float d = (positions[a] - positions[b]).Length;
                    float k = ClothEdit.LinkStiffness(layout.LinkStiffness, inv[a] + inv[b]);
                    link.Set("particleA", a);
                    link.Set("particleB", b);
                    link.Set("bendMinLength", d * layout.BendRatio);
                    link.Set("stretchMaxLength", d);
                    link.Set("bendStiffness", k);
                    link.Set("stretchStiffness", k);
                    bend.Array("links").Add(link);
                }
                sets.Add(("bend", bend));
            }
            for (int i = 0; i < sets.Count; i++)
            {
                sets[i].Set.Object("constraintId")?.Set("value", i);
                sim.Array("staticConstraintSets").Add(sets[i].Set);
            }
            int SetIndex(string key) => sets.FindIndex(s => s.Key == key);
            var execution = new List<int>();
            foreach (
                string key in new[]
                {
                    "localrange",
                    "transition",
                    "standard",
                    "bend",
                    "bend",
                    "stretch",
                }
            )
                if (SetIndex(key) >= 0)
                    execution.Add(SetIndex(key));
            execution.Add(-1);

            //Collidables, their bones and offsets.
            sim.Object("collidableTransformMap")
                .Set("transformSetIndex", layout.Collidables.Count > 0 ? 0 : -1);
            for (int i = 0; i < layout.Collidables.Count; i++)
                AddCollidable(
                    sim,
                    layout.Collidables[i],
                    clothIndex[collidableBones[i]],
                    model[collidableBones[i]].World
                );
            var transfer = sim.Object("transferMotionData");
            transfer.Set("maxTranslationBlend", 1.0f);
            transfer.Set("maxRotationBlend", 1.0f);
            sim.Set("landscapeCollisionEnabled", true);
            CopyLandscape(file, sim.Object("landscapeCollisionData"));
            var simData = new SimClothData(sim);
            ClothEdit.RecomputeCollisionMasks(simData);
            ClothEdit.RecomputeTotals(simData);
            foreach (var (a, b, c) in triangles)
            {
                sim.Array("triangleIndices").AddValue(a);
                sim.Array("triangleIndices").AddValue(b);
                sim.Array("triangleIndices").AddValue(c);
            }
            for (int p = 0; p < n; p++)
                sim.Array("perParticlePinchDetectionEnabledFlags").AddValue(false);

            //Buffers: skin scratch (one reference vertex per particle), then current and previous.
            var data = file.New("hclClothData");
            data.Set("name", name);
            data.Array("simClothDatas").Add(sim);
            var scratch = file.New("hclScratchBufferDefinition");
            SetBuffer(scratch, name, "SkinScratchBuf", 6, n, 0, 2);
            var current = file.New("hclBufferDefinition");
            SetBuffer(current, name, "SimCurrentBuf", 1, n, triangles.Count, 1);
            var previous = file.New("hclBufferDefinition");
            SetBuffer(previous, name, "SimPrevBuf", 2, n, triangles.Count, 1);
            data.Array("bufferDefinitions").Add(scratch);
            data.Array("bufferDefinitions").Add(current);
            data.Array("bufferDefinitions").Add(previous);
            var transformSet = file.New("hclTransformSetDefinition");
            transformSet.Set("name", SkeletonPrefix + name);
            transformSet.Set("type", 1);
            transformSet.Set("numTransforms", nb);
            data.Array("transformSetDefinitions").Add(transformSet);

            //Operators: skin, move fixed particles, simulate, bone deform, the two gathers.
            var skinBones = particleBone
                .Select(b => clothIndex[b])
                .Distinct()
                .OrderBy(x => x)
                .ToList();
            var subset = skinBones.Count < nb ? skinBones : new List<int>();
            var skin = file.New("hclBoneSpaceSkinPOperator");
            SetOperator(skin, "Skin", 0);
            skin.Array("usedBuffers")
                .Add(BufferAccess(skin.Array("usedBuffers").ElementType, 0, 6, false, 0));
            skin.Array("usedTransformSets")
                .Add(
                    TransformAccess(
                        skin.Array("usedTransformSets").ElementType,
                        9,
                        skinBones,
                        skinBones,
                        Array.Empty<int>(),
                        nb
                    )
                );
            foreach (int s in subset)
                skin.Array("transformSubset").AddValue(s);
            var deformer = skin.Object("boneSpaceDeformer");
            var oneBlend = deformer.Array("oneBlendEntries");
            var localPs = skin.Array("localPs");
            for (int start = 0; start < n; start += 16)
            {
                var block = HkObject.Create(oneBlend.ElementType);
                var local = HkObject.Create(localPs.ElementType);
                var vertices = (object[])block["vertexIndices"];
                var bones = (object[])block["boneIndices"];
                var locals = (object[])local["localPosition"];
                var vertexType = block.Type.ElementTypeOf("vertexIndices");
                var boneType = block.Type.ElementTypeOf("boneIndices");
                for (int e = 0; e < 16; e++)
                {
                    int v = Math.Min(start + e, n - 1);
                    int clothBone = clothIndex[particleBone[v]];
                    vertices[e] = HkValue.Coerce(v, vertexType);
                    bones[e] = HkValue.Coerce(
                        subset.Count > 0 ? subset.IndexOf(clothBone) : clothBone,
                        boneType
                    );
                    Vector3 lp = Vector3.TransformPosition(
                        positions[v],
                        Matrix4.Invert(model[particleBone[v]].World)
                    );
                    locals[e] = new[] { lp.X, lp.Y, lp.Z, 1.0f };
                }
                oneBlend.Add(block);
                localPs.Add(local);
                deformer.Array("controlBytes").AddValue(3);
            }
            deformer.Set("startVertexIndex", 0);
            deformer.Set("endVertexIndex", n - 1);

            var move = file.New("hclMoveParticlesOperator");
            SetOperator(move, "MoveFixedParticles", 1);
            var moveBuffers = move.Array("usedBuffers");
            moveBuffers.Add(BufferAccess(moveBuffers.ElementType, 0, 1, false, 0));
            moveBuffers.Add(BufferAccess(moveBuffers.ElementType, 1, 2, false, 1));
            moveBuffers.Add(BufferAccess(moveBuffers.ElementType, 2, 2, false, 2));
            foreach (int f in fixedParticles)
            {
                var pair = HkObject.Create(move.Array("vertexParticlePairs").ElementType);
                pair.Set("vertexIndex", f);
                pair.Set("particleIndex", f);
                move.Array("vertexParticlePairs").Add(pair);
            }

            var simulate = file.New("hclSimulateOperator");
            SetOperator(simulate, "Simulate", 2);
            var simBuffers = simulate.Array("usedBuffers");
            simBuffers.Add(BufferAccess(simBuffers.ElementType, 0, 1, false, 0));
            simBuffers.Add(BufferAccess(simBuffers.ElementType, 1, 15, false, 1));
            simBuffers.Add(BufferAccess(simBuffers.ElementType, 2, 15, false, 2));
            var collidableCloth = collidableBones.Select(b => clothIndex[b]).Distinct().ToList();
            if (collidableCloth.Count > 0)
                simulate
                    .Array("usedTransformSets")
                    .Add(
                        TransformAccess(
                            simulate.Array("usedTransformSets").ElementType,
                            9,
                            collidableCloth,
                            collidableCloth,
                            Array.Empty<int>(),
                            nb
                        )
                    );
            var config = HkObject.Create(simulate.Array("simulateOpConfigs").ElementType);
            config.Set("name", "Default Config");
            foreach (int e in execution)
                config.Array("constraintExecution").AddValue(e);
            config.Set("subSteps", 1);
            config.Set("numberOfSolveIterations", Math.Max(1, layout.Iterations));
            config.Set("useAllInstanceCollidables", true);
            simulate.Array("simulateOpConfigs").Add(config);

            var meshBone = file.New("hclSimpleMeshBoneDeformOperator");
            SetOperator(meshBone, "MeshBone", 3);
            meshBone
                .Array("usedBuffers")
                .Add(BufferAccess(meshBone.Array("usedBuffers").ElementType, 1, 1, true, 1));
            var drivenCloth = driven.Select(d => clothIndex[d.Bone]).ToList();
            meshBone
                .Array("usedTransformSets")
                .Add(
                    TransformAccess(
                        meshBone.Array("usedTransformSets").ElementType,
                        2,
                        Array.Empty<int>(),
                        Array.Empty<int>(),
                        drivenCloth,
                        nb
                    )
                );
            meshBone.Set("inputBufferIdx", 1);
            meshBone.Set("outputTransformSetIdx", 0);
            foreach (var (bone, tri) in driven.OrderBy(d => clothIndex[d.Bone]))
            {
                var pair = HkObject.Create(meshBone.Array("triangleBonePairs").ElementType);
                pair.Set("boneOffset", clothIndex[bone] * 64);
                pair.Set("triangleOffset", tri * 6);
                meshBone.Array("triangleBonePairs").Add(pair);
                var (a, b, c) = triangles[tri];
                meshBone
                    .Array("localBoneTransforms")
                    .Add(
                        HkValue.FromMatrix(
                            model[bone].World
                                * Matrix4.Invert(
                                    TriangleFrame(positions[a], positions[b], positions[c])
                                )
                        )
                    );
            }
            if (meshBone.Has("boneAxis"))
                meshBone.Set("boneAxis", 3);

            HkObject Gather(string opName, int id, int output)
            {
                var gather = file.New("hclCopyVerticesOperator");
                SetOperator(gather, opName, id);
                var buffers = gather.Array("usedBuffers");
                buffers.Add(BufferAccess(buffers.ElementType, 0, 1, false, 0));
                buffers.Add(BufferAccess(buffers.ElementType, output, 6, false, output));
                gather.Set("inputBufferIdx", 0);
                gather.Set("outputBufferIdx", output);
                gather.Set("numberOfVertices", n);
                return gather;
            }
            foreach (
                var op in new[]
                {
                    skin,
                    move,
                    simulate,
                    meshBone,
                    Gather("VertexGatherCurrent", 4, 1),
                    Gather("VertexGatherPrev", 5, 2),
                }
            )
                data.Array("operators").Add(op);

            //States: Default runs the simulation, the other two copy the animation pose in.
            var read = skinBones.Union(collidableCloth).Distinct().ToList();
            var states = data.Array("clothStateDatas");
            states.Add(
                State(
                    file,
                    "Default",
                    new[] { 0, 1, 2, 3 },
                    new[] { (0, 7, false, 0), (1, 15, true, 1) },
                    (11, read, read, drivenCloth),
                    nb
                )
            );
            states.Add(
                State(
                    file,
                    "AnimToSimCurrent",
                    new[] { 0, 4 },
                    new[] { (0, 7, false, 0), (1, 6, false, 1) },
                    (9, skinBones, skinBones, new List<int>()),
                    nb
                )
            );
            states.Add(
                State(
                    file,
                    "AnimToSimPrev",
                    new[] { 0, 5 },
                    new[] { (0, 7, false, 0), (2, 6, false, 2) },
                    (9, skinBones, skinBones, new List<int>()),
                    nb
                )
            );
            data.Set("targetPlatform", 131072);

            file.Container.Source.Array("clothDatas").Add(data);
            file.AnimationContainer.Array("skeletons").Add(skeleton);
            var mesh = file.Params?.AddMesh(name, model[layout.Anchor].Name);
            if (mesh != null && layout.Preset != null)
                mesh.Preset = layout.Preset;
            return new ClothData(data);
        }

        /// <summary>Renames a piece and everything named after it: its transform set, its skeleton, its buffers' mesh name and its AAMP entry.</summary>
        public static void RenamePiece(ClothFile file, ClothData piece, string name)
        {
            string old = piece.Name;
            var skeleton = file.SkeletonFor(piece);
            var mesh = file.Params?.MeshFor(old);
            piece.Name = name;
            foreach (var def in piece.TransformSetDefinitions)
                if (def.Name == SkeletonPrefix + old)
                    def.Source.Set("name", SkeletonPrefix + name);
            if (skeleton != null && skeleton.Name == SkeletonPrefix + old)
                skeleton.Name = SkeletonPrefix + name;
            foreach (var buffer in piece.BufferDefinitions.Objects)
                if (buffer.String("meshName") == old)
                    buffer.Set("meshName", name);
            if (mesh != null)
                mesh.Name = name;
        }

        /// <summary>Removes a piece with its skeleton and its AAMP entry.</summary>
        public static void RemovePiece(ClothFile file, ClothData piece)
        {
            var skeleton = file.SkeletonFor(piece);
            file.Container.Source.Array("clothDatas").Remove(piece.Source);
            if (
                skeleton != null
                && !file.Container.ClothDatas.Any(d =>
                    d.Source != piece.Source && file.SkeletonFor(d)?.Source == skeleton.Source
                )
            )
                file.AnimationContainer?.Array("skeletons").Remove(skeleton.Source);
            file.Params?.RemoveMesh(piece.Name);
        }

        /// <summary>A unit vector across the strip: the bone axis most perpendicular to the chain, turned by the roll.</summary>
        static Vector3 SideAxis(Matrix4 boneWorld, Vector3 dir, float rollDegrees)
        {
            Vector3 best = Vector3.UnitX;
            float bestDot = float.MaxValue;
            foreach (
                var axis in new[] { boneWorld.Row2.Xyz, boneWorld.Row0.Xyz, boneWorld.Row1.Xyz }
            )
            {
                if (axis.LengthSquared < 1e-12f)
                    continue;
                float d = Math.Abs(Vector3.Dot(axis.Normalized(), dir));
                if (d < bestDot - 1e-4f)
                {
                    bestDot = d;
                    best = axis.Normalized();
                }
            }
            Vector3 side = best - dir * Vector3.Dot(best, dir);
            if (side.LengthSquared < 1e-10f)
                side = Vector3.Cross(dir, Vector3.UnitY);
            side.Normalize();
            if (rollDegrees != 0)
                side = Vector3.Transform(
                    side,
                    Quaternion.FromAxisAngle(dir, MathHelper.DegreesToRadians(rollDegrees))
                );
            return side;
        }

        /// <summary>The bone a collidable's rest transform sits on: the model bone whose rest origin is nearest its origin.</summary>
        public static int NearestBone(IReadOnlyList<AuthorBone> model, Matrix4 transform)
        {
            int best = 0;
            float bestDistance = float.MaxValue;
            for (int i = 0; i < model.Count; i++)
            {
                float d = (model[i].World.Row3.Xyz - transform.Row3.Xyz).LengthSquared;
                if (d < bestDistance)
                {
                    bestDistance = d;
                    best = i;
                }
            }
            return best;
        }

        /// <summary>Rows [p0 - c, p1 - c, cross, c]: the frame the bone deform expresses a bone in.</summary>
        public static Matrix4 TriangleFrame(Vector3 p0, Vector3 p1, Vector3 p2)
        {
            Vector3 c = (p0 + p1 + p2) * (1.0f / 3.0f);
            Vector3 e0 = p0 - c,
                e1 = p1 - c;
            return new Matrix4(
                new Vector4(e0, 0),
                new Vector4(e1, 0),
                new Vector4(Vector3.Cross(e0, e1), 0),
                new Vector4(c, 1)
            );
        }

        static List<(int A, int B)> TriangleEdges(List<(int, int, int)> triangles)
        {
            var edges = new List<(int, int)>();
            var seen = new HashSet<(int, int)>();
            foreach (var (a, b, c) in triangles)
            foreach (var (x, y) in new[] { (a, b), (b, c), (c, a) })
                if (seen.Add((Math.Min(x, y), Math.Max(x, y))))
                    edges.Add((x, y));
            return edges;
        }

        /// <summary>The opposite corners of every pair of triangles sharing an edge, in the order of the shared edges.</summary>
        static List<(int A, int B)> BendPairs(
            List<(int, int, int)> triangles,
            List<(int A, int B)> edges
        )
        {
            var pairs = new List<(int, int)>();
            foreach (var (x, y) in edges)
            {
                var opposite = new List<int>();
                foreach (var (a, b, c) in triangles)
                {
                    var t = new[] { a, b, c };
                    if (t.Contains(x) && t.Contains(y))
                        opposite.Add(t.First(v => v != x && v != y));
                }
                if (opposite.Count == 2)
                    pairs.Add((opposite[0], opposite[1]));
            }
            return pairs;
        }

        static void CopyTransitionPeriods(ClothFile file, HkObject transition)
        {
            var existing = file
                .Container.ClothDatas.SelectMany(d => d.SimClothDatas)
                .SelectMany(s => s.ConstraintSets)
                .OfType<TransitionSet>()
                .FirstOrDefault();
            foreach (
                var field in new[]
                {
                    "toAnimPeriod",
                    "toAnimPlusDelayPeriod",
                    "toSimPeriod",
                    "toSimPlusDelayPeriod",
                }
            )
                transition.Set(field, existing?.Source.Float(field) ?? 1.0f);
        }

        static void CopyLandscape(ClothFile file, HkObject landscape)
        {
            var existing = file
                .Container.ClothDatas.SelectMany(d => d.SimClothDatas)
                .Select(s => s.Source.Object("landscapeCollisionData"))
                .FirstOrDefault(o => o != null);
            if (existing != null && existing.Type == landscape.Type)
            {
                for (int i = 0; i < landscape.Values.Length; i++)
                    landscape.Values[i] = HkValue.CloneOwned(
                        existing.Values[i],
                        landscape.Type.AllFields[i].Type
                    );
                return;
            }
            landscape.Set("stuckParticlesStretchFactorSq", 9.0f);
            landscape.Set("pinchDetectionRadius", PinchDetectionRadius);
            landscape.Set("collisionTolerance", 0.2f);
        }

        static void SetOperator(HkObject op, string name, int id)
        {
            op.Set("name", name);
            op.Set("operatorID", id);
        }

        static void SetBuffer(
            HkObject buffer,
            string mesh,
            string name,
            int type,
            int vertices,
            int triangles,
            int triangleFormat
        )
        {
            buffer.Set("meshName", mesh);
            buffer.Set("bufferName", name);
            buffer.Set("type", type);
            buffer.Set("numVertices", vertices);
            buffer.Set("numTriangles", triangles);
            var layout = buffer.Object("bufferLayout");
            var elements = (object[])layout["elementsLayout"];
            var slots = (object[])layout["slots"];
            for (int i = 0; i < elements.Length; i++)
            {
                var e = (HkObject)elements[i];
                e.Set("vectorConversion", i == 0 ? 0 : 250);
                e.Set("vectorSize", i == 0 ? 16 : 0);
            }
            for (int i = 0; i < slots.Length; i++)
            {
                var s = (HkObject)slots[i];
                s.Set("flags", i == 0 ? 1 : 0);
                s.Set("stride", i == 0 ? 16 : 0);
            }
            layout.Set("numSlots", 1);
            layout.Set("triangleFormat", triangleFormat);
        }

        static HkObject BufferAccess(
            HkType type,
            int index,
            int flags,
            bool trianglesRead,
            int shadow
        )
        {
            var access = HkObject.Create(type);
            access.Set("bufferIndex", index);
            var usage = access.Object("bufferUsage");
            var components = (object[])usage["perComponentFlags"];
            components[0] = HkValue.Coerce(flags, usage.Type.ElementTypeOf("perComponentFlags"));
            usage.Set("trianglesRead", trianglesRead);
            access.Set("shadowBufferIndex", shadow);
            return access;
        }

        static HkObject TransformAccess(
            HkType type,
            int flags,
            IEnumerable<int> read,
            IEnumerable<int> readBeforeWrite,
            IEnumerable<int> written,
            int bones
        )
        {
            var access = HkObject.Create(type);
            var trackers = access
                .Object("transformSetUsage")
                .Array("perComponentTransformTrackers");
            trackers.Add(HkObject.Create(trackers.ElementType));
            trackers.Add(HkObject.Create(trackers.ElementType));
            ClothEdit.SetTracker(
                access,
                flags,
                read.ToHashSet(),
                readBeforeWrite.ToHashSet(),
                written.ToHashSet(),
                bones
            );
            return access;
        }

        static HkObject State(
            ClothFile file,
            string name,
            int[] operators,
            (int Index, int Flags, bool Triangles, int Shadow)[] buffers,
            (int Flags, List<int> Read, List<int> ReadBeforeWrite, List<int> Written) transforms,
            int bones
        )
        {
            var state = file.New("hclClothState");
            state.Set("name", name);
            foreach (int op in operators)
                state.Array("operators").AddValue(op);
            foreach (var (index, flags, triangles, shadow) in buffers)
                state
                    .Array("usedBuffers")
                    .Add(
                        BufferAccess(
                            state.Array("usedBuffers").ElementType,
                            index,
                            flags,
                            triangles,
                            shadow
                        )
                    );
            state
                .Array("usedTransformSets")
                .Add(
                    TransformAccess(
                        state.Array("usedTransformSets").ElementType,
                        transforms.Flags,
                        transforms.Read,
                        transforms.ReadBeforeWrite,
                        transforms.Written,
                        bones
                    )
                );
            state.Array("usedSimCloths").AddValue(0);

            var graph = file.New("hclStateDependencyGraph");
            var branch = HkObject.Create(graph.Array("branches").ElementType);
            foreach (int i in Enumerable.Range(0, operators.Length))
                branch.Array("stateOperatorIndices").AddValue(i);
            graph.Array("branches").Add(branch);
            graph.Array("rootBranchIds").AddValue(0);
            var children = graph.Array("children");
            var parents = graph.Array("parents");
            var inner = children.ElementType.Resolved.Subtype;
            for (int i = 0; i < operators.Length; i++)
            {
                var c = new HkArray(inner, 1);
                if (i + 1 < operators.Length)
                    c.AddValue(i + 1);
                children.Add(c);
                var p = new HkArray(parents.ElementType.Resolved.Subtype, 1);
                if (i > 0)
                    p.AddValue(i - 1);
                parents.Add(p);
            }
            state.Set("dependencyGraph", graph);
            return state;
        }
    }
}
