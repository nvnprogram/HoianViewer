using System;
using System.Collections.Generic;
using System.Linq;
using OpenTK;

namespace PlayerViewer.Phive
{
    /// <summary>
    /// Edits that keep the values the file caches in step with what they are derived from, by
    /// the exporter's rules. A caller changes the authored value through one of
    /// these and never writes a derived field itself.
    /// </summary>
    public static class ClothEdit
    {
        /// <summary>The largest float32 the exporter writes as a local range normal limit.</summary>
        public const float FltMaxExported = 3.40282e38f;

        /// <summary>
        /// Sets a particle's mass. The inverse mass follows, and every standard and bend link
        /// touching the particle keeps its normalised stiffness (stiffness times the sum of the
        /// inverse masses), which is what the exporter authored.
        /// </summary>
        public static void SetMass(SimClothData sim, int particle, float mass)
        {
            var particles = sim.Particles.ToList();
            var normalised = NormalisedLinkStiffness(sim, particles);
            particles[particle].SetMass(Math.Max(mass, 0));
            ApplyNormalisedLinkStiffness(sim, particles, normalised);
            RecomputeTotals(sim);
        }

        /// <summary>
        /// Sets the piece's total mass, spread evenly over its free particles the way the
        /// exporter spreads an authored piece mass, keeping every link's normalised stiffness.
        /// </summary>
        public static void SetTotalMass(SimClothData sim, float total)
        {
            var particles = sim.Particles.ToList();
            var normalised = NormalisedLinkStiffness(sim, particles);
            var fixedSet = new HashSet<int>(sim.FixedParticles.Ints());
            int free = particles.Count - fixedSet.Count;
            float each = free > 0 ? total * (1.0f / free) : 0;
            for (int i = 0; i < particles.Count; i++)
                particles[i].SetMass(fixedSet.Contains(i) ? 0 : each);
            ApplyNormalisedLinkStiffness(sim, particles, normalised);
            RecomputeTotals(sim);
        }

        /// <summary>Per standard and bend link: its stiffness times (invMassA + invMassB).</summary>
        static Dictionary<HkObject, (float A, float B)> NormalisedLinkStiffness(
            SimClothData sim,
            List<ParticleData> particles
        )
        {
            var result = new Dictionary<HkObject, (float, float)>(
                ReferenceEqualityComparer.Instance
            );
            foreach (var set in sim.ConstraintSets)
            {
                foreach (var link in LinkRecords(set))
                {
                    float sum = InvMassSum(link, particles);
                    if (set.Kind == ConstraintSetKind.StandardLink)
                        result[link] = (link.Float("stiffness") * sum, 0);
                    else if (set.Kind == ConstraintSetKind.BendLink)
                        result[link] = (
                            link.Float("bendStiffness") * sum,
                            link.Float("stretchStiffness") * sum
                        );
                }
            }
            return result;
        }

        static void ApplyNormalisedLinkStiffness(
            SimClothData sim,
            List<ParticleData> particles,
            Dictionary<HkObject, (float A, float B)> normalised
        )
        {
            foreach (var set in sim.ConstraintSets)
            foreach (var link in LinkRecords(set))
            {
                if (!normalised.TryGetValue(link, out var k))
                    continue;
                float sum = InvMassSum(link, particles);
                if (sum <= 0)
                    continue;
                if (set.Kind == ConstraintSetKind.StandardLink)
                    link.Set("stiffness", LinkStiffness(k.A, sum));
                else
                {
                    link.Set("bendStiffness", LinkStiffness(k.A, sum));
                    link.Set("stretchStiffness", LinkStiffness(k.B, sum));
                }
            }
        }

        static IEnumerable<HkObject> LinkRecords(ConstraintSet set) =>
            set.Kind is ConstraintSetKind.StandardLink or ConstraintSetKind.BendLink
                ? set.Records.Objects
                : Enumerable.Empty<HkObject>();

        static float InvMassSum(HkObject link, List<ParticleData> particles)
        {
            int a = link.Int("particleA"),
                b = link.Int("particleB");
            float sum = 0;
            if (a < particles.Count)
                sum += particles[a].InvMass;
            if (b < particles.Count)
                sum += particles[b].InvMass;
            return sum;
        }

        /// <summary>A link's stored stiffness for a normalised one: k over the sum of its particles' inverse masses, k when both are fixed.</summary>
        public static float LinkStiffness(float k, float invMassSum) =>
            invMassSum > 0 ? k / invMassSum : k;

        /// <summary>The normalised stiffness of a standard link (1 is the exporter's default).</summary>
        public static float LinkK(SimClothData sim, HkObject link, string field = "stiffness") =>
            link.Float(field) * InvMassSum(link, sim.Particles.ToList());

        public static void SetLinkK(
            SimClothData sim,
            HkObject link,
            float k,
            string field = "stiffness"
        )
        {
            link.Set(field, LinkStiffness(k, InvMassSum(link, sim.Particles.ToList())));
        }

        /// <summary>totalMass as the float32 running sum, maxParticleRadius, and the landscape particle count.</summary>
        public static void RecomputeTotals(SimClothData sim)
        {
            float total = 0,
                maxRadius = 0;
            foreach (var p in sim.Particles)
            {
                total += p.Mass;
                maxRadius = Math.Max(maxRadius, p.Radius);
            }
            sim.Source.Set("totalMass", total);
            sim.Source.Set("maxParticleRadius", maxRadius);
            if (sim.Source.Has("numLandscapeCollidableParticles"))
                sim.Source.Set(
                    "numLandscapeCollidableParticles",
                    sim.ParticleCount - sim.FixedParticles.Count
                );
        }

        /// <summary>The most collidables a piece can collide with: the top bit of a particle's collision mask is a flag.</summary>
        public const int MaxCollidables = 31;

        /// <summary>
        /// The collision mask of every particle by the exporter's rule: none for a fixed particle,
        /// every collidable of the piece and the landscape bit for a free one.
        /// </summary>
        public static void RecomputeCollisionMasks(SimClothData sim)
        {
            var fixedSet = new HashSet<int>(sim.FixedParticles.Ints());
            int collidables = sim.PerInstanceCollidables.Count;
            uint free =
                0x80000000u
                | (collidables >= MaxCollidables ? 0x7FFFFFFFu : (1u << collidables) - 1);
            var masks = sim.StaticCollisionMasks;
            masks.Clear();
            for (int p = 0; p < sim.ParticleCount; p++)
                masks.AddValue(fixedSet.Contains(p) ? 0u : free);
        }

        /// <summary>
        /// Makes a particle fixed (pinned to its skinned reference vertex, no mass, no collision)
        /// or free (the piece's mean free mass). The move particles pairs, the collision masks and
        /// the totals follow.
        /// </summary>
        public static void SetFixed(ClothData piece, int particle, bool isFixed)
        {
            var sim = piece.SimClothDatas[0];
            var fixedList = sim.FixedParticles.Ints().ToList();
            if (isFixed == fixedList.Contains(particle))
                return;
            var particles = sim.Particles.ToList();
            var normalised = NormalisedLinkStiffness(sim, particles);
            if (isFixed)
            {
                fixedList.Add(particle);
                particles[particle].SetMass(0);
            }
            else
            {
                fixedList.Remove(particle);
                var free = particles
                    .Where((p, i) => !fixedList.Contains(i) && i != particle && p.Mass > 0)
                    .ToList();
                particles[particle].SetMass(free.Count > 0 ? free.Average(p => p.Mass) : 1);
            }
            fixedList.Sort();
            sim.FixedParticles.Clear();
            foreach (int f in fixedList)
                sim.FixedParticles.AddValue(f);
            ApplyNormalisedLinkStiffness(sim, particles, normalised);

            var move = piece.Operator<MoveParticlesOperator>();
            if (move != null)
            {
                var pairs = move.Source.Array("vertexParticlePairs");
                int vertex = ReferenceVertex(piece, particle);
                if (isFixed)
                {
                    if (vertex >= 0 && !pairs.Objects.Any(o => o.Int("particleIndex") == particle))
                    {
                        var pair = HkObject.Create(pairs.ElementType);
                        pair.Set("vertexIndex", vertex);
                        pair.Set("particleIndex", particle);
                        pairs.Add(pair);
                    }
                }
                else
                    pairs.RemoveAll(o => o is HkObject h && h.Int("particleIndex") == particle);
                var sorted = pairs.Objects.OrderBy(o => o.Int("vertexIndex")).ToList();
                pairs.Clear();
                pairs.AddRange(sorted);
            }
            RecomputeCollisionMasks(sim);
            RecomputeTotals(sim);
        }

        /// <summary>The reference vertex a particle follows, as <see cref="ReferenceVertices"/> gives it.</summary>
        public static int ReferenceVertex(ClothData piece, int particle) =>
            ReferenceVertices(piece, SkinVertexCount(piece))[particle];

        /// <summary>
        /// The reference vertex of every particle as the solver reads it: its move particles pair,
        /// else its last local range entry, else its last transition entry, else its own index
        /// while the skin has that many vertices; -1 for none.
        /// </summary>
        public static int[] ReferenceVertices(ClothData piece, int vertexCount)
        {
            var sim = piece.SimClothDatas[0];
            int n = sim.ParticleCount;
            var map = new int[n];
            for (int p = 0; p < n; p++)
                map[p] = p < vertexCount ? p : -1;
            foreach (
                var kind in new[] { ConstraintSetKind.Transition, ConstraintSetKind.LocalRange }
            )
            foreach (var set in sim.ConstraintSets.Where(s => s.Kind == kind))
            foreach (var r in set.Records.Objects)
            {
                int particle = r.Int("particleIndex");
                if (particle >= 0 && particle < n)
                    map[particle] = r.Int("referenceVertex");
            }
            var move = piece
                .Operators.OfType<MoveParticlesOperator>()
                .LastOrDefault(m => m.Source.Array("vertexParticlePairs").Count > 0);
            if (move != null)
                foreach (var (vertex, particle) in move.VertexParticlePairs)
                    if (particle >= 0 && particle < n)
                        map[particle] = vertex;
            return map;
        }

        /// <summary>The reference vertices the skin writes: the last skin operator's deformer's.</summary>
        public static int SkinVertexCount(ClothData piece) =>
            piece
                .Operators.OfType<SkinOperator>()
                .LastOrDefault(s => s.Deformer != null)
                ?.Deformer.Int("endVertexIndex") + 1
            ?? 0;

        /// <summary>Sets a capsule's ends and radius with its cached direction and inverse squared length.</summary>
        public static void SetCapsule(Collidable c, Vector3 start, Vector3 end, float radius)
        {
            var shape = c.Shape;
            shape.Set("start", HkValue.FromVector(new Vector4(start, 0)));
            shape.Set("end", HkValue.FromVector(new Vector4(end, 0)));
            Vector3 d = end - start;
            float lenSq = d.LengthSquared;
            shape.Set(
                "dir",
                HkValue.FromVector(
                    new Vector4(lenSq > 0 ? d / MathF.Sqrt(lenSq) : Vector3.UnitX, 0)
                )
            );
            shape.Set("radius", radius);
            shape.Set("capLenSqrdInv", lenSq > 0 ? 1.0f / lenSq : 0);
        }

        public static void SetSphere(Collidable c, Vector3 centre, float radius)
        {
            var sphere = c.Shape.Object("sphere");
            sphere?.Set("pos", HkValue.FromVector(new Vector4(centre, radius)));
        }

        public static void SetPlane(Collidable c, Vector3 normal, float offset)
        {
            c.Shape.Set("planeEquation", HkValue.FromVector(new Vector4(normal, offset)));
        }

        /// <summary>Every link's rest length set to its particles' distance in the rest pose.</summary>
        public static void ResetRestLengths(SimClothData sim, ConstraintSet set)
        {
            var rest = sim.Poses.FirstOrDefault()?.Positions;
            if (rest == null)
                return;
            foreach (var link in set.Records.Objects)
            {
                int a = link.Int("particleA"),
                    b = link.Int("particleB");
                if (a >= rest.Length || b >= rest.Length)
                    continue;
                float d = (rest[a].Xyz - rest[b].Xyz).Length;
                switch (set.Kind)
                {
                    case ConstraintSetKind.StandardLink:
                    case ConstraintSetKind.StretchLink:
                        link.Set("restLength", d);
                        break;
                    case ConstraintSetKind.BendLink:
                        float ratio =
                            link.Float("stretchMaxLength") > 0
                                ? link.Float("bendMinLength") / link.Float("stretchMaxLength")
                                : 1;
                        link.Set("stretchMaxLength", d);
                        link.Set("bendMinLength", d * ratio);
                        break;
                }
            }
        }

        /// <summary>
        /// The transform set usage the simulate operator and the Default state declare, from the
        /// bones the piece's collidables follow, the skin reads and the bone deform writes.
        /// </summary>
        public static void RecomputeTransformUsage(ClothData piece)
        {
            var sim = piece.SimClothDatas[0];
            int bones = piece.TransformSetDefinitions.FirstOrDefault()?.NumTransforms ?? 0;
            var collidableBones = sim.CollidableTransformIndices.ToHashSet();
            var skin = piece.Operator<SkinOperator>();
            var skinBones = new HashSet<int>();
            if (skin != null)
                foreach (var t in skin.Source.Array("usedTransformSets").Objects)
                    skinBones.UnionWith(TrackerBits(t, "read"));
            var deform = piece.Operator<MeshBoneDeformOperator>();
            var driven =
                deform
                    ?.TriangleBonePairs.Select(p =>
                        p.BoneOffset / MeshBoneDeformOperator.BoneMatrixBytes
                    )
                    .ToHashSet()
                ?? new HashSet<int>();

            var simulate = piece.Operator<SimulateOperator>();
            if (simulate != null)
            {
                var used = simulate.Source.Array("usedTransformSets");
                if (collidableBones.Count == 0)
                    used.Clear();
                else
                {
                    if (used.Count == 0)
                        used.Add(NewTransformSetAccess(used.ElementType, bones));
                    SetTracker(
                        used.ObjectAt(0),
                        9,
                        collidableBones,
                        collidableBones,
                        new HashSet<int>(),
                        bones
                    );
                }
                var map = sim.CollidableTransformMap;
                if (map != null)
                    map.Set(
                        "transformSetIndex",
                        collidableBones.Count > 0 || sim.PerInstanceCollidables.Count > 0 ? 0 : -1
                    );
            }
            foreach (var state in piece.ClothStates.Objects)
            {
                if (state.String("name") != "Default")
                    continue;
                var used = state.Array("usedTransformSets");
                if (used.Count == 0)
                    continue;
                var read = new HashSet<int>(skinBones);
                read.UnionWith(collidableBones);
                SetTracker(used.ObjectAt(0), 11, read, read, driven, bones);
            }
        }

        static IEnumerable<int> TrackerBits(HkObject access, string which)
        {
            var trackers = access
                .Object("transformSetUsage")
                ?.Array("perComponentTransformTrackers");
            var tracker = trackers?.ObjectAt(0);
            var storage = tracker?.Object(which)?.Object("storage");
            if (storage == null)
                yield break;
            var words = storage.Array("words").UInts();
            for (int w = 0; w < words.Length; w++)
            for (int b = 0; b < 32; b++)
                if ((words[w] & (1u << b)) != 0)
                    yield return w * 32 + b;
        }

        static HkObject NewTransformSetAccess(HkType type, int bones)
        {
            var access = HkObject.Create(type);
            var usage = access.Object("transformSetUsage");
            var trackers = usage.Array("perComponentTransformTrackers");
            trackers.Add(HkObject.Create(trackers.ElementType));
            trackers.Add(HkObject.Create(trackers.ElementType));
            foreach (var t in trackers.Objects)
            foreach (var which in TrackerBitSets)
                SetBits(t.Object(which), new HashSet<int>(), bones);
            return access;
        }

        /// <summary>The first tracker of a transform set access: read, read before write and written bits, the flags byte, the bit count on all of them.</summary>
        public static void SetTracker(
            HkObject access,
            int flags,
            HashSet<int> read,
            HashSet<int> readBeforeWrite,
            HashSet<int> written,
            int bones
        )
        {
            var usage = access.Object("transformSetUsage");
            if (usage["perComponentFlags"] is object[] f && f.Length > 0)
                f[0] = HkValue.Coerce(flags, usage.Type.ElementTypeOf("perComponentFlags"));
            var trackers = usage.Array("perComponentTransformTrackers");
            for (int i = 0; i < trackers.Count; i++)
            {
                var t = trackers.ObjectAt(i);
                SetBits(t.Object("read"), i == 0 ? read : new HashSet<int>(), bones);
                SetBits(
                    t.Object("readBeforeWrite"),
                    i == 0 ? readBeforeWrite : new HashSet<int>(),
                    bones
                );
                SetBits(t.Object("written"), i == 0 ? written : new HashSet<int>(), bones);
            }
        }

        /// <summary>The bit sets of a transform tracker.</summary>
        public static readonly string[] TrackerBitSets = { "read", "readBeforeWrite", "written" };

        static int WordCount(int bits) => Math.Max(1, (bits + 31) / 32);

        /// <summary>Widens a bit set to <paramref name="count"/> bits, keeping the bits it has.</summary>
        public static void ResizeBits(HkObject bitField, int count)
        {
            var storage = bitField?.Object("storage");
            if (storage == null)
                return;
            var words = storage.Array("words");
            while (words.Count < WordCount(count))
                words.AddValue(0u);
            storage.Set("numBits", count);
        }

        static void SetBits(HkObject bitField, HashSet<int> bits, int count)
        {
            var storage = bitField?.Object("storage");
            if (storage == null)
                return;
            var words = storage.Array("words");
            int n = WordCount(count);
            var values = new uint[n];
            foreach (int b in bits)
                if (b >= 0 && b < n * 32)
                    values[b / 32] |= 1u << (b % 32);
            words.Clear();
            foreach (var v in values)
                words.AddValue(v);
            storage.Set("numBits", count);
        }

        /// <summary>Particle positions of the rest pose (xyz).</summary>
        public static Vector3[] RestPositions(SimClothData sim) =>
            sim.Poses.FirstOrDefault()?.Positions.Select(p => p.Xyz).ToArray()
            ?? Array.Empty<Vector3>();
    }
}
