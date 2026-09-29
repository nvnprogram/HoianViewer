using System;
using System.Collections.Generic;
using System.Linq;
using OpenTK;
using PlayerViewer.Phive;
using PlayerViewer.Rigging;

namespace PlayerViewer.HairGen
{
    public class ClothGenOptions
    {
        /// <summary>Target distance between the strip's levels; the stock strands average this.</summary>
        public float LevelSpacing = 0.18f;

        /// <summary>Levels closer than this are merged, so a bone that close to the last level is driven from it rather than given its own.</summary>
        public float MinLevelSpacing = 0.12f;

        /// <summary>Hair gets the stock head, chest and body colliders and clears the head; anything else gets no collider.</summary>
        public bool Hair = true;

        public bool HeadCollider = true;

        /// <summary>Adds the chest capsule the stock hairs give any strand whose tip reaches below the chin.</summary>
        public bool ChestCollider = true;

        /// <summary>A strand whose tip is below this height (hair frame) collides with the chest.</summary>
        public float ChestBelow = -0.13f;

        public string HeadBone = "Head_Root";
        public string ChestBone = "Spine_3";

        /// <summary>Body bones given a capsule for strands whose tip is below the height, with the capsule in the bone's space.</summary>
        public (string Bone, float Below)[] BodyColliders =
        {
            ("Arm_1_L", -0.25f),
            ("Arm_1_R", -0.25f),
            ("Waist", -0.6f),
        };

        /// <summary>Particles start at least this far outside the head capsule, beyond their own radius.</summary>
        public float HeadClearance = 0.03f;

        /// <summary>The furthest a strip is moved to clear the head capsule.</summary>
        public float MaxHeadShift = 0.08f;

        /// <summary>Chains closer than this, beyond their strips' half widths, share one piece so they cannot cross.</summary>
        public float MergeDistance = 0.03f;

        public static readonly Dictionary<
            string,
            (Vector3 Start, Vector3 End, float Radius)
        > BodyCapsules = new()
        {
            ["Waist"] = (new(0.05f, 0, -0.10f), new(0.05f, 0, 0.10f), 0.16f),
            ["Arm_1_L"] = (new(0.02f, 0, 0), new(0.22f, 0, 0), 0.06f),
            ["Arm_1_R"] = (new(-0.02f, 0, 0), new(-0.22f, 0, 0), 0.06f),
        };
    }

    /// <summary>
    /// Turns a rig's chains into cloth pieces by the stock hairs' recipe: per chain a strip two
    /// particles wide with levels about a stock spacing apart, fixed and skinned to the head at
    /// the root and along a stretch lying on it, driving every chain bone, its solver values from
    /// the strand's style. Colliders are shaped like the stock head and chest ones.
    /// </summary>
    public static class HairClothGen
    {
        //The head capsule the cloth collides with, taken from SQD013
        static readonly Vector3 HeadStart = new(0, -0.095f, 0);
        static readonly Vector3 HeadEnd = new(0, 0.305f, 0);
        const float HeadRadius = 0.3f;

        //The skull alone, which the strips are moved clear of when built. Clearing the whole
        //collidable would push every strip beside the face outward; the solver eases them out
        //of it instead, as it does the stock strands resting inside theirs.
        static readonly Vector3 SkullStart = new(0, 0.125f, 0.02f);
        static readonly Vector3 SkullEnd = new(0, 0.325f, 0.02f);
        const float SkullRadius = 0.245f;

        //The stock chest capsule, in the chest bone's own space.
        const float ChestHalfLength = 0.177f;
        const float ChestRadius = 0.198f;

        //The range of a strip's width.
        const float MinWidth = 0.05f;
        const float MaxWidth = 0.35f;

        /// <summary>
        /// Adds the colliders and one piece per group of chains to an emptied cloth file that
        /// already declares the piece and collidable types; without groups, chains close together
        /// are grouped. Chains styled rigid get no piece. Without <paramref name="colliders"/> the
        /// defaults of <see cref="DefaultColliders"/> are used.
        /// </summary>
        public static void Build(
            ClothFile file,
            HairRig rig,
            IReadOnlyList<StrandStyle> styles,
            ClothGenOptions options = null,
            IReadOnlyList<int[]> groups = null,
            IReadOnlyList<LimbCollider> colliders = null
        )
        {
            options ??= new ClothGenOptions();
            var bones = rig.Bones;
            var moving = Moving(rig, styles, options, groups);
            if (moving.Count == 0)
            {
                foreach (var c in colliders ?? Array.Empty<LimbCollider>())
                {
                    c.BuiltName = null;
                    c.Problem = "No limb moves.";
                }
                return;
            }
            colliders ??= DefaultColliders(rig, moving.Select(m => m.Chain), options);

            //The strips clear the skull, or the head collidable as the user edited it.
            (Vector3 A, Vector3 B, float R)? head = options.Hair
                ? (SkullStart, SkullEnd, SkullRadius)
                : null;
            var headEdit = colliders.FirstOrDefault(c =>
                c.Default && c.Edited && c.Name == "Collidable_" + options.HeadBone
            );
            if (headEdit != null)
            {
                int b = rig.BoneIndex(headEdit.Bone);
                head =
                    headEdit.Enabled && b >= 0
                        ? (
                            Vector3.TransformPosition(headEdit.Start, bones[b].World),
                            Vector3.TransformPosition(
                                headEdit.Kind == CollidableShapeKind.Sphere
                                    ? headEdit.Start
                                    : headEdit.End,
                                bones[b].World
                            ),
                            headEdit.Radius
                        )
                        : null;
            }

            var layouts = moving
                .Select(m => Layout(m.Chain, m.Style, m.Riders, bones, options, head))
                .ToList();

            var added = new List<(Collidable Collidable, int Bone, LimbCollider Source)>();
            var taken = new HashSet<string>();
            foreach (var c in colliders)
            {
                c.Problem = null;
                c.BuiltName = null;
                if (!c.Enabled)
                    continue;
                int bone = rig.BoneIndex(c.Bone);
                if (bone < 0 || rig.Removed.Contains(bone))
                {
                    c.Problem = $"The model has no bone {c.Bone}.";
                    continue;
                }
                string name = c.Name;
                for (int k = 2; taken.Contains(name); k++)
                    name = $"{c.Name}_{k}";
                taken.Add(name);
                c.BuiltName = name;
                added.Add(
                    (
                        ClothAuthor.AddCollidable(
                            file,
                            name,
                            c.Kind,
                            bones[bone].World,
                            c.Start,
                            c.End,
                            c.Radius
                        ),
                        bone,
                        c
                    )
                );
            }

            for (int i = 0; i < layouts.Count; i++)
            {
                var layout = layouts[i];
                var limbs = moving[i]
                    .Riders.Prepend(moving[i].Chain)
                    .Select(c => c.Limb)
                    .Where(l => l != null)
                    .ToHashSet();
                float tip = moving[i].Chain.Path[^1].Y;
                foreach (var (collidable, bone, source) in added)
                {
                    bool collides =
                        source.Limbs != null ? source.Limbs.Overlaps(limbs)
                        : source.Below is float below ? tip < below
                        : true;
                    if (!collides)
                        continue;
                    if (layout.Collidables.Count >= ClothEdit.MaxCollidables)
                    {
                        source.Problem =
                            $"{layout.Name} collides with {ClothEdit.MaxCollidables} already, the most a piece can.";
                        continue;
                    }
                    layout.Collidables.Add(collidable);
                    layout.CollidableBones.Add(bone);
                }
                var names = file.Container.ClothDatas.Select(d => d.Name).ToHashSet();
                for (int k = 2; names.Contains(layout.Name); k++)
                    layout.Name = $"Cloth_{moving[i].Chain.Name}_{k}";
                ClothAuthor.AddPiece(file, layout, bones);
            }
        }

        /// <summary>
        /// The pieces a build makes: per group its first chain, its style and the chains riding on
        /// it, leaving out rigid and boneless ones.
        /// </summary>
        public static List<(HairChain Chain, StrandStyle Style, List<HairChain> Riders)> Moving(
            HairRig rig,
            IReadOnlyList<StrandStyle> styles,
            ClothGenOptions options,
            IReadOnlyList<int[]> groups = null
        ) =>
            (groups ?? Groups(rig.Chains, options))
                .Select(g =>
                    (
                        Chain: rig.Chains[g[0]],
                        Style: styles[g[0]],
                        Riders: g.Skip(1).Select(i => rig.Chains[i]).ToList()
                    )
                )
                .Where(x => x.Style.Motion != StrandMotion.Rigid && x.Chain.Bones.Length > 0)
                .ToList();

        /// <summary>
        /// The colliders hair gets by default, for pieces led by these chains: the stock head
        /// capsule, the chest capsule when a piece reaches below the chin, and the arm and waist
        /// capsules for long ones, each with the height a piece's tip must reach below to collide
        /// with it. None when the model is not hair or a bone is missing.
        /// </summary>
        public static List<LimbCollider> DefaultColliders(
            HairRig rig,
            IEnumerable<HairChain> leaders,
            ClothGenOptions options
        )
        {
            var result = new List<LimbCollider>();
            if (!options.Hair)
                return result;
            var tips = leaders.Select(c => c.Path[^1].Y).ToList();
            var bones = rig.Bones;
            int head = rig.BoneIndex(options.HeadBone);
            if (options.HeadCollider && head >= 0)
            {
                //The particles are pushed out of it rather than the capsule shrinking to clear them.
                var inv = Matrix4.Invert(bones[head].World);
                result.Add(
                    new LimbCollider
                    {
                        Name = "Collidable_" + options.HeadBone,
                        Bone = options.HeadBone,
                        Start = Vector3.TransformPosition(HeadStart, inv),
                        End = Vector3.TransformPosition(HeadEnd, inv),
                        Radius = HeadRadius,
                        Default = true,
                    }
                );
            }
            int chest = rig.BoneIndex(options.ChestBone);
            if (options.ChestCollider && chest >= 0 && tips.Any(y => y < options.ChestBelow))
                result.Add(
                    new LimbCollider
                    {
                        Name = "Collidable_" + options.ChestBone,
                        Bone = options.ChestBone,
                        Start = new Vector3(-ChestHalfLength, 0, 0),
                        End = new Vector3(ChestHalfLength, 0, 0),
                        Radius = ChestRadius,
                        Below = options.ChestBelow,
                        Default = true,
                    }
                );
            foreach (var (name, height) in options.BodyColliders)
            {
                if (rig.BoneIndex(name) < 0 || !tips.Any(y => y < height))
                    continue;
                var (start, end, radius) = ClothGenOptions.BodyCapsules[name];
                result.Add(
                    new LimbCollider
                    {
                        Name = "Collidable_" + name,
                        Bone = name,
                        Start = start,
                        End = end,
                        Radius = radius,
                        Below = height,
                        Default = true,
                    }
                );
            }
            return result;
        }

        /// <summary>
        /// The chains that share a piece, each group with its longest chain first. Separate strips
        /// this close cross each other on every fast turn.
        /// </summary>
        public static List<int[]> Groups(IReadOnlyList<HairChain> chains, ClothGenOptions options)
        {
            var union = new UnionFind(chains.Count);
            for (int i = 0; i < chains.Count; i++)
            for (int j = i + 1; j < chains.Count; j++)
                if (
                    chains[i].Bones.Length > 0
                    && chains[j].Bones.Length > 0
                    && PathDistance(chains[i], chains[j])
                        - HalfWidth(chains[i])
                        - HalfWidth(chains[j])
                        < options.MergeDistance
                )
                    union.Union(i, j);
            return Enumerable
                .Range(0, chains.Count)
                .GroupBy(union.Find)
                .Select(g => g.OrderByDescending(i => chains[i].Length).ToArray())
                .OrderBy(g => g.Min())
                .ToList();
        }

        static float HalfWidth(HairChain chain) =>
            0.5f * Math.Clamp(2 * chain.Thickness, MinWidth, MaxWidth);

        //Closest approach of two paths away from their roots, which may share a parting.
        static float PathDistance(HairChain a, HairChain b)
        {
            float best = float.MaxValue;
            for (float s = Math.Min(0.1f, 0.3f * a.Length); s <= a.Length; s += 0.02f)
            for (float t = Math.Min(0.1f, 0.3f * b.Length); t <= b.Length; t += 0.02f)
                best = Math.Min(best, (a.At(s) - b.At(t)).Length);
            return best;
        }

        /// <summary>
        /// The arc lengths of a chain's levels: the root, where it leaves the head when it lies on
        /// it, then bones about a level spacing apart, then the tip. With an even spacing the
        /// levels past the head are spread evenly whatever the bones.
        /// </summary>
        public static List<float> LevelArcs(
            HairChain chain,
            ClothGenOptions options,
            float evenSpacing = 0
        )
        {
            float length = chain.Length;
            float held = Math.Clamp(chain.Held, 0, length);
            var levels = new List<float> { 0 };
            if (held > 1e-4f)
                levels.Add(held);
            if (evenSpacing > 0)
            {
                int n = Math.Max(1, (int)MathF.Round((length - held) / evenSpacing));
                for (int k = 1; k <= n; k++)
                    levels.Add(held + (length - held) * k / n);
                return levels;
            }
            foreach (float arc in chain.BoneArc.Skip(1))
                if (
                    arc > held + 1e-4f
                    && arc - levels[^1] >= options.MinLevelSpacing
                    && length - arc >= options.MinLevelSpacing * 0.5f
                )
                    levels.Add(arc);
            levels.Add(length);
            //Gaps much wider than the spacing get evenly spaced levels between them; the held stretch moves as one.
            var result = new List<float> { levels[0] };
            for (int i = 1; i < levels.Count; i++)
            {
                float gap = levels[i] - levels[i - 1];
                int split =
                    levels[i] <= held + 1e-4f
                        ? 1
                        : Math.Max(1, (int)MathF.Round(gap / options.LevelSpacing));
                for (int k = 1; k <= split; k++)
                    result.Add(levels[i - 1] + gap * k / split);
            }
            return result;
        }

        static PieceLayout Layout(
            HairChain chain,
            StrandStyle style,
            List<HairChain> riders,
            IReadOnlyList<AuthorBone> bones,
            ClothGenOptions options,
            (Vector3 A, Vector3 B, float R)? head
        )
        {
            //A floaty strip has fewer levels; the bones between them ride on the levels around them.
            var arcs = LevelArcs(
                chain,
                options,
                StrandParams.Floats(style, chain) ? StrandParams.FloatyLevelSpacing : 0
            );
            int levels = arcs.Count;
            //The root level and those along the stretch lying on the head are fixed to it.
            int held = arcs.Count(a => a <= chain.Held + 1e-4f);
            var p = StrandParams.Resolve(style, chain, levels - held, options.Hair);
            float spacing = chain.FreeLength / Math.Max(1, levels - held);
            float width = Math.Clamp(
                2 * chain.Thickness * style.Width,
                MinWidth,
                Math.Max(MinWidth, Math.Min(MaxWidth, spacing * 1.3f))
            );
            if (riders.Count > 0)
            {
                //Wide enough to span the group where it leaves the head.
                float a = chain.Held + Math.Min(0.15f, chain.FreeLength);
                var across = chain.SideAt(chain.At(a), chain.Tangent(a));
                float span = riders.Max(r =>
                    MathF.Abs(Vector3.Dot(r.At(Math.Min(a, r.Length)) - chain.At(a), across))
                );
                width = Math.Min(MaxWidth, Math.Max(width, 2 * span + 2 * chain.Thickness));
            }
            var layout = new PieceLayout
            {
                Name = "Cloth_" + chain.Name,
                Anchor = chain.Anchor,
                Mass = 5,
                Radius = p.Gravity ? 0.02f : 0,
                Gravity = p.Gravity ? 9.81f : 0,
                Damping = p.Damping,
                Bend = p.Bend,
                BendRatio = 0.8f,
                LocalRange = true,
                RangeSetStiffness = 1,
                Preset = p.AampPreset,
                //A spring strip resting against the head otherwise catches on it and stays off its pose.
                Friction = p.Gravity ? 0.5f : 0,
            };

            //The bone each level is skinned to: the last chain bone at or before it, the anchor at the root.
            int BoneAt(float arc)
            {
                int bone = chain.Bones[0];
                for (int j = 0; j < chain.Bones.Length; j++)
                    if (chain.BoneArc[j] <= arc + 1e-4f)
                        bone = chain.Bones[j];
                return bone;
            }
            //Each level's side is square to the chord between its neighbours, which the quads follow,
            //and keeps the previous level's sense so no quad between them is crossed.
            var sides = new Vector3[levels];
            for (int l = 0; l < levels; l++)
            {
                var tangent = chain.Tangent(arcs[l]);
                var chord =
                    chain.At(arcs[Math.Min(l + 1, levels - 1)])
                    - chain.At(arcs[Math.Max(l - 1, 0)]);
                if (chord.LengthSquared > 1e-8f)
                    tangent = chord.Normalized();
                sides[l] = chain.SideAt(chain.At(arcs[l]), tangent);
                if (l > 0 && Vector3.Dot(sides[l], sides[l - 1]) < 0)
                    sides[l] = -sides[l];
            }
            var positions = new List<Vector3>();
            for (int l = 0; l < levels; l++)
            for (int s = 0; s < 2; s++)
                positions.Add(chain.At(arcs[l]) + sides[l] * (s == 0 ? -0.5f : 0.5f) * width);
            //The whole strip moves out of the head capsule along the root's radial, as far as its free
            //particles need; pushing each particle on its own bends the strip, and gravity straightens it.
            if (head is var (ha, hb, hr))
            {
                float clear = hr + layout.Radius + options.HeadClearance;
                var rootAt = chain.FreeRoot;
                var radial = rootAt - Geometry.ClosestOnSegment(rootAt, ha, hb);
                radial = radial.LengthSquared > 1e-8f ? radial.Normalized() : Vector3.UnitZ;
                bool Clears(Vector3 along, float t) =>
                    positions
                        .Skip(2 * held)
                        .All(q =>
                            (
                                q + along * t - Geometry.ClosestOnSegment(q + along * t, ha, hb)
                            ).Length >= clear
                        );
                float Needed(Vector3 along)
                {
                    float lo = 0,
                        hi = 0.6f;
                    for (int i = 0; i < 40; i++)
                    {
                        float mid = 0.5f * (lo + hi);
                        if (Clears(along, mid))
                            hi = mid;
                        else
                            lo = mid;
                    }
                    return hi;
                }
                if (!Clears(radial, 0))
                {
                    float shift = Needed(radial);
                    //Past the limit the root's radial is the wrong way, as for a strand from the crown
                    //down the face; the strip moves away from where it is too close and the solver clears the rest.
                    if (shift > options.MaxHeadShift)
                    {
                        var away = Vector3.Zero;
                        foreach (var q in positions.Skip(2 * held))
                        {
                            var offset = q - Geometry.ClosestOnSegment(q, ha, hb);
                            float gap = clear - offset.Length;
                            if (gap > 0 && offset.LengthSquared > 1e-10f)
                                away += offset.Normalized() * gap;
                        }
                        if (away.LengthSquared > 1e-12f)
                            radial = away.Normalized();
                        shift = Math.Min(Needed(radial), options.MaxHeadShift);
                    }
                    for (int i = 0; i < positions.Count; i++)
                        positions[i] += radial * shift;
                }
            }
            for (int i = 0; i < positions.Count; i++)
                layout.AddParticle(
                    positions[i],
                    i / 2 < held ? chain.Anchor : BoneAt(arcs[i / 2]),
                    0,
                    i / 2,
                    i % 2,
                    i / 2 < held
                );
            layout.AddStripTriangles(0, levels);

            //Each chain bone follows the pair of levels around it, and so does each rider's.
            foreach (var driven in riders.Prepend(chain))
                for (int j = 0; j < driven.Bones.Length; j++)
                {
                    int level = 0;
                    for (int l = 0; l + 1 < levels; l++)
                        if (arcs[l] <= driven.BoneArc[j] + 1e-4f)
                            level = l;
                    layout.Driven.Add(
                        (driven.Bones[j], PieceLayout.DrivingTriangle(0, level, levels))
                    );
                }

            //Range and its stiffness ramp from the last fixed level to the tip.
            var root = (layout.Positions[2 * held - 2] + layout.Positions[2 * held - 1]) * 0.5f;
            float far = layout.Positions.Max(q => (q - root).Length);
            for (int i = 0; i < layout.Positions.Count; i++)
            {
                float u =
                    levels - 1 > held ? (layout.Level[i] - held) / (float)(levels - 1 - held) : 1;
                if (layout.Fixed.Contains(i))
                {
                    layout.RangeRadius.Add(-1);
                    layout.RangeStiffness.Add(1);
                    continue;
                }
                float distance = (layout.Positions[i] - root).Length;
                if (p.Floaty)
                {
                    //The range opens with the square of the distance; the stiffness holds to a knee, then eases.
                    float t = far > 1e-6f ? distance / far : 1;
                    float eased = Math.Max(
                        0,
                        (t - StrandParams.FloatyKnee) / (1 - StrandParams.FloatyKnee)
                    );
                    layout.RangeRadius.Add(
                        (p.RootRange + (p.TipRange - p.RootRange) * t * t) * distance
                    );
                    layout.RangeStiffness.Add(
                        p.RootStiffness + (p.TipStiffness - p.RootStiffness) * eased
                    );
                    continue;
                }
                float fraction = p.RootRange + (p.TipRange - p.RootRange) * u;
                layout.RangeRadius.Add(fraction * distance);
                layout.RangeStiffness.Add(p.RootStiffness + (p.TipStiffness - p.RootStiffness) * u);
            }
            return layout;
        }
    }
}
