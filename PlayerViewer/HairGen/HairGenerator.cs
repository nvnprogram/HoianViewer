using System;
using System.Collections.Generic;
using System.Linq;
using BfresLibrary;
using OpenTK;
using PlayerViewer.Phive;
using PlayerViewer.Rigging;

namespace PlayerViewer.HairGen
{
    /// <summary>
    /// Physics for a model from its mesh: a strand per limb the user painted, a rig for them
    /// (bones generated along each strand, or the model's own chains), chains on the bones of
    /// the limbs picked from the skeleton, a style per chain, and the cloth built from all of it.
    /// Each step can be redone on its own, so a style change only rebuilds the cloth. With a head
    /// the model is hair; without one nothing assumes a head.
    /// </summary>
    public partial class HairGenerator
    {
        public readonly SkinnedMesh Mesh;
        public LimbOptions LimbOptions;
        public RigOptions RigOptions;
        public ClothGenOptions ClothOptions;

        public StrandSet Strands { get; private set; }
        public HairRig Rig { get; private set; }

        /// <summary>A style per chain of the rig, in chain order.</summary>
        public List<StrandStyle> Styles { get; private set; } = new();

        public HairGenerator(SkinnedMesh mesh, HeadShape head)
            : this(mesh, head, new LimbOptions(), new RigOptions(), new ClothGenOptions()) { }

        HairGenerator(
            SkinnedMesh mesh,
            HeadShape head,
            LimbOptions limbOptions,
            RigOptions rigOptions,
            ClothGenOptions clothOptions
        )
        {
            Mesh = mesh;
            LimbOptions = limbOptions;
            RigOptions = rigOptions;
            ClothOptions = clothOptions;
            SetHead(head);
        }

        /// <summary>The head the hair rests on, in the model's frame; null when the model is not hair.</summary>
        public HeadShape Head { get; private set; }

        public bool Hair => Head != null;

        /// <summary>
        /// Makes the model hair with this head, or not hair with none: limb roots, holds, anchors,
        /// bone names and colliders follow. The next build uses it.
        /// </summary>
        public void SetHead(HeadShape head)
        {
            Head = head;
            _scalp = _surface = null;
            RigOptions.Hair = ClothOptions.Hair = head != null;
            RigOptions.BonePrefix = head != null ? "HairGen" : "ClothGen";
        }

        MeshGraph _graph;

        /// <summary>The visible surface welded into nodes; painted limbs are sets of its nodes.</summary>
        public MeshGraph Graph => _graph ??= new MeshGraph(Mesh);

        /// <summary>The limbs, painted and picked from bones, in the order the list shows them.</summary>
        public List<PaintedLimb> Limbs { get; } = new();

        float[] _scalp,
            _surface;

        /// <summary>
        /// Builds a strand from each painted limb, leaving out <paramref name="except"/> (the limb
        /// being repainted or repicked), then the rig from them and the bone limbs.
        /// </summary>
        public void BuildFromLimbs(PaintedLimb except = null)
        {
            if (except != null)
                except.Problem = null;
            if (Hair)
            {
                _scalp ??= Enumerable.Repeat(float.NaN, Graph.Nodes.Count).ToArray();
                _surface ??= Enumerable.Repeat(float.NaN, Graph.Nodes.Count).ToArray();
            }
            Strands = LimbStrands.Build(
                Graph,
                Head,
                Limbs.Where(l => l != except && !l.IsBoneLimb).ToList(),
                LimbOptions,
                _scalp,
                _surface
            );
            if (!Hair)
                foreach (var strand in Strands.Strands)
                    strand.Anchor =
                        strand.Limb?.Hang is string hang
                        && Mesh.BoneIndex(hang) is int kept and >= 0
                            ? kept
                            : HangBone(strand);
            BuildRig(except);
            foreach (
                var limb in Limbs.Where(l =>
                    !l.IsBoneLimb && l.Aim?.Straight != true && l != except
                )
            )
                if (AimOf(limb) is LimbAim traced)
                    limb.Traced = traced with { Side = null, Straight = false };
        }

        const float MirrorTolerance = 0.004f;
        int[] _mirror;

        /// <summary>For each node the node at its image across the model's X = 0 plane, or -1 when none lies that close.</summary>
        public int[] MirrorNodes => _mirror ??= BuildMirror();

        int[] BuildMirror()
        {
            var nodes = Graph.Nodes;
            var grid = new SpatialGrid(0.02f);
            for (int i = 0; i < nodes.Count; i++)
                grid.Add(i, nodes[i]);
            var mirror = new int[nodes.Count];
            for (int i = 0; i < nodes.Count; i++)
            {
                var image = new Vector3(-nodes[i].X, nodes[i].Y, nodes[i].Z);
                int best = -1;
                float bestDistance = MirrorTolerance * MirrorTolerance;
                foreach (int j in grid.Near(image))
                {
                    float d = (nodes[j] - image).LengthSquared;
                    if (d <= bestDistance)
                    {
                        bestDistance = d;
                        best = j;
                    }
                }
                mirror[i] = best;
            }
            return mirror;
        }

        /// <summary>
        /// The painted limb whose paint is mostly this one's mirror image: the other side's twin,
        /// or the limb itself when it is centred on the plane. Null when there is none.
        /// </summary>
        public PaintedLimb MirrorOf(PaintedLimb limb)
        {
            if (limb.IsBoneLimb || limb.Nodes.Count == 0)
                return null;
            var mirror = MirrorNodes;
            var image = new HashSet<int>();
            foreach (int n in limb.Nodes)
                if (n >= 0 && n < mirror.Length && mirror[n] >= 0)
                    image.Add(mirror[n]);
            PaintedLimb best = null;
            float bestShare = 0.6f;
            foreach (var other in Limbs.Where(l => !l.IsBoneLimb && l.Nodes.Count > 0))
            {
                float share =
                    other.Nodes.Count(image.Contains)
                    / (float)Math.Max(limb.Nodes.Count, other.Nodes.Count);
                if (share > bestShare)
                {
                    bestShare = share;
                    best = other;
                }
            }
            return best;
        }

        /// <summary>
        /// A painted limb's frame as built, its side always given: a straight aim's root and
        /// direction, else its traced centre line's root and the way to its tip. Null without a chain.
        /// </summary>
        public LimbAim AimOf(PaintedLimb limb)
        {
            if (limb.IsBoneLimb)
                return null;
            int c = ChainOf(limb);
            if (c < 0)
                return limb.Aim is { Straight: true } ? limb.Aim : null;
            var chain = Rig.Chains[c];
            var aim = limb.Aim;
            if (aim is not { Straight: true })
            {
                var path = chain.Path;
                if (path.Count < 2)
                    return null;
                var d = path[^1] - path[0];
                if (d.LengthSquared < 1e-10f)
                    return null;
                aim = new LimbAim(path[0], d.Normalized(), aim?.Side, false);
            }
            return aim with { Side = chain.SideAt(aim.Root, aim.Direction) };
        }

        /// <summary>The index in <see cref="HairRig.Chains"/> of a limb's first (longest) chain, or -1 when it has none.</summary>
        public int ChainOf(PaintedLimb limb)
        {
            if (Rig == null || limb == null)
                return -1;
            return Rig.Chains.FindIndex(c => c.Limb == limb);
        }

        /// <summary>Every chain of a limb; a bone limb can have several.</summary>
        public IEnumerable<int> ChainsOf(PaintedLimb limb) =>
            Rig == null || limb == null
                ? Enumerable.Empty<int>()
                : Enumerable.Range(0, Rig.Chains.Count).Where(i => Rig.Chains[i].Limb == limb);

        /// <summary>The limb a chain was built for, or null.</summary>
        public PaintedLimb LimbOf(int chain) =>
            Rig != null && chain >= 0 && chain < Rig.Chains.Count ? Rig.Chains[chain].Limb : null;

        /// <summary>A test build's rig from the model's own chains in place of generated bones.</summary>
        partial void RigFromModel(ref bool generate);

        /// <summary>
        /// Rebuilds the rig from the strands and the bone limbs, leaving out
        /// <paramref name="except"/>. A limb's chains take the limb's style, which a first build
        /// suggests; a found strand's keeps the style of the chain of its name.
        /// </summary>
        public void BuildRig(PaintedLimb except = null)
        {
            var previous =
                Rig?.Chains.Zip(Styles, (c, s) => (c.Name, s))
                    .GroupBy(x => x.Name)
                    .ToDictionary(x => x.Key, x => x.First().s)
                ?? new();
            bool generate = true;
            RigFromModel(ref generate);
            if (generate)
                Rig = HairRigger.Generate(Mesh, Strands, RigOptions);
            AddBoneLimbs(except);
            if (Hair)
            {
                AddChestBone();
                AddBodyBones();
            }
            HairRigger.DropReplaced(Rig, Mesh, KeepNames());
            NoteReplaced();
            foreach (var limb in Limbs.Where(l => l.IsBoneLimb))
            {
                var bones = ChainsOf(limb).SelectMany(c => Rig.Chains[c].Bones).ToList();
                if (bones.Count > 0 && bones.All(b => Rig.Weight[b] <= 0))
                    limb.Problem = (
                        (limb.Problem ?? "") + " Its bones carry none of the surface."
                    ).Trim();
            }
            Styles = new List<StrandStyle>();
            for (int i = 0; i < Rig.Chains.Count; i++)
            {
                var chain = Rig.Chains[i];
                var limb = LimbOf(i);
                if (limb != null)
                    Styles.Add(limb.Style ??= Suggest(chain));
                else
                    Styles.Add(previous.TryGetValue(chain.Name, out var s) ? s : Suggest(chain));
            }
            _close = HairClothGen.Groups(Rig.Chains, ClothOptions);
            Regroup();
        }

        /// <summary>The preset a chain's shape suggests; the hair rules only on hair.</summary>
        public StrandStyle Suggest(HairChain chain) => HairStyles.Suggest(chain, Hair);

        List<int[]> _close = new();

        /// <summary>
        /// Groups the chains again and gives each limb's chains its limb's style, after a limb's
        /// style, its <see cref="PaintedLimb.OwnPiece"/> or <see cref="MergeCloseLimbs"/> changed.
        /// A limb's own chains are always one piece. The rig is not rebuilt.
        /// </summary>
        public void Regroup()
        {
            if (Rig == null)
                return;
            int count = Rig.Chains.Count;
            for (int i = 0; i < count; i++)
                if (LimbOf(i) is PaintedLimb limb)
                    Styles[i] = limb.Style ??= Suggest(Rig.Chains[i]);
            //Limbs are the user's pieces, merged only when asked; found strands always are.
            bool limbs = Rig.Chains.Any(c => c.Limb != null);
            CloseLimbs = limbs
                ? _close
                    .Select(g => g.Select(LimbOf).Where(l => l != null).Distinct().ToArray())
                    .Where(g => g.Length > 1)
                    .ToList()
                : new();
            if (!limbs)
                Groups = _close;
            else
            {
                var union = new UnionFind(count);
                foreach (var limb in Limbs)
                {
                    var own = ChainsOf(limb).ToList();
                    foreach (int i in own.Skip(1))
                        union.Union(own[0], i);
                }
                if (MergeCloseLimbs)
                    foreach (var g in _close)
                    {
                        var free = g.Where(i => LimbOf(i)?.OwnPiece != true).ToList();
                        foreach (int i in free.Skip(1))
                            union.Union(free[0], i);
                    }
                Groups = Enumerable
                    .Range(0, count)
                    .GroupBy(union.Find)
                    .Select(g => g.OrderByDescending(i => Rig.Chains[i].Length).ToArray())
                    .OrderBy(g => g.Min())
                    .ToList();
            }
            foreach (var group in Groups)
            foreach (int rider in group.Skip(1))
                Styles[rider] = Styles[group[0]];
        }

        /// <summary>
        /// Chains that share one piece, longest first; they share one style object too. One per
        /// limb unless <see cref="MergeCloseLimbs"/>.
        /// </summary>
        public List<int[]> Groups { get; private set; } = new();

        /// <summary>
        /// Limbs whose chains come close enough to pass through each other as separate pieces,
        /// each set longest first. Separate pieces do not collide with each other.
        /// </summary>
        public List<PaintedLimb[]> CloseLimbs { get; private set; } = new();

        /// <summary>
        /// Builds each set of <see cref="CloseLimbs"/> as one piece, driven from its longest limb
        /// in its style. Off unless a saved model had it on.
        /// </summary>
        public bool MergeCloseLimbs;

        /// <summary>The group a chain belongs to.</summary>
        public int[] GroupOf(int chain) =>
            Groups.FirstOrDefault(g => g.Contains(chain)) ?? new[] { chain };

        /// <summary>
        /// Gives a chain's limb a style. The first chain of a group sets the whole group's; another
        /// keeps it on its limb for when the group splits.
        /// </summary>
        public void SetStyle(int chain, StrandStyle style)
        {
            if (LimbOf(chain) is PaintedLimb limb)
            {
                limb.Style = style;
                Regroup();
                return;
            }
            var group = GroupOf(chain);
            if (group[0] != chain)
                return;
            foreach (int i in group)
                Styles[i] = style;
        }

        /// <summary>Bones a replaced region never takes with it: the anchors the game looks up by name.</summary>
        IEnumerable<string> KeepNames()
        {
            yield return RigOptions.AnchorBone;
            yield return "Skl_Root";
            yield return "Root";
            if (!Hair)
                yield break;
            yield return ClothOptions.HeadBone;
            yield return ClothOptions.ChestBone;
            foreach (var (name, _) in ClothOptions.BodyColliders)
                yield return name;
        }

        /// <summary>The chains of painted limbs; only they may add bones to the model.</summary>
        IEnumerable<HairChain> PaintedChains => Rig.Chains.Where(c => c.Limb?.IsBoneLimb != true);

        /// <summary>A strand reaching below the chin collides with the chest, which rides on a placeholder the game moves to the body.</summary>
        void AddChestBone()
        {
            string name = ClothOptions.ChestBone;
            if (
                Rig.BoneIndex(name) >= 0
                || !PaintedChains.Any(c => c.Path[^1].Y < ClothOptions.ChestBelow)
            )
                return;
            int anchor = Math.Max(0, Rig.BoneIndex(RigOptions.AnchorBone));
            Rig.Unskinned.Add(Rig.Bones.Count);
            Rig.Bones.Add(new AuthorBone(name, anchor, Rig.Bones[anchor].World));
        }

        /// <summary>
        /// Placeholders for the body bones long strands collide with, at the player's rest pose;
        /// the hair welds bones to the player's by name, so they follow the arms and waist. A limb
        /// on the model's own bones adds none: it leaves the skeleton as it is.
        /// </summary>
        void AddBodyBones()
        {
            int anchor = Math.Max(0, Rig.BoneIndex(RigOptions.AnchorBone));
            float lowest = PaintedChains.Select(c => c.Path[^1].Y).DefaultIfEmpty(0).Min();
            foreach (var (name, below) in ClothOptions.BodyColliders)
            {
                if (
                    lowest >= below
                    || Rig.BoneIndex(name) >= 0
                    || !Head.BodyBones.TryGetValue(name, out var world)
                )
                    continue;
                Rig.Unskinned.Add(Rig.Bones.Count);
                Rig.Bones.Add(new AuthorBone(name, anchor, world));
            }
        }

        /// <summary>
        /// A chain per picked run of the model's bones, hanging from the parent of its first bone,
        /// a limb's chains longest first. Nothing is written into the model for them.
        /// </summary>
        void AddBoneLimbs(PaintedLimb except)
        {
            var taken = new Dictionary<int, PaintedLimb>();
            foreach (var chain in Rig.Chains)
            foreach (int b in chain.Bones)
                taken[b] = chain.Limb;
            foreach (var limb in Limbs.Where(l => l.IsBoneLimb && l != except))
            {
                limb.Problem = null;
                limb.Replaced.Clear();
                var chains = new List<HairChain>();
                var problems = new List<string>();
                foreach (var names in limb.BoneChains)
                {
                    var bones = names.Select(Rig.BoneIndex).ToArray();
                    int missing = Array.FindIndex(bones, b => b < 0 || b >= Rig.OriginalBoneCount);
                    if (names.Length == 0 || missing >= 0)
                    {
                        problems.Add(
                            names.Length == 0
                                ? "A chain has no bones."
                                : $"The model has no bone {names[missing]}."
                        );
                        continue;
                    }
                    int anchor = Rig.Bones[bones[0]].Parent;
                    if (anchor < 0)
                    {
                        problems.Add($"{names[0]} has no parent to hang from.");
                        continue;
                    }
                    if (bones.FirstOrDefault(taken.ContainsKey, -1) is int used and >= 0)
                    {
                        problems.Add(
                            $"{Rig.Bones[used].Name} already moves with {taken[used]?.Name ?? "another chain"}."
                        );
                        continue;
                    }
                    var (tip, thickness) = BoneChainShape(bones, anchor);
                    var chain = HairRigger.ChainFromBones(Rig.Bones, bones, anchor, tip, thickness);
                    if (chain.Length < 0.01f)
                    {
                        problems.Add($"The chain from {names[0]} is too short to move.");
                        continue;
                    }
                    chain.Limb = limb;
                    chain.Hub = Hair ? null : Rig.Bones[anchor].World.Row3.Xyz;
                    foreach (int b in bones)
                        taken[b] = limb;
                    chains.Add(chain);
                }
                string safe = HairRigger.BoneSafe(limb.Name);
                chains = chains.OrderByDescending(c => c.Length).ToList();
                for (int i = 0; i < chains.Count; i++)
                    chains[i].Name = (safe.Length > 0 ? safe : "Limb") + (i > 0 ? $"_{i + 1}" : "");
                Rig.Chains.AddRange(chains);
                if (chains.Count == 0 && problems.Count == 0)
                    problems.Add("No bones picked.");
                limb.Problem = problems.Count > 0 ? string.Join(" ", problems) : null;
            }
        }

        /// <summary>
        /// Where a picked chain ends and how thick it is, from the surface its bones carry: the
        /// tip reaches the farthest point the last bone carries past it, and the thickness is the
        /// median distance of that surface from the bones.
        /// </summary>
        (Vector3 Tip, float Thickness) BoneChainShape(int[] chain, int anchor)
        {
            var set = chain.ToHashSet();
            var points = chain.Select(b => Rig.Bones[b].World.Row3.Xyz).ToList();
            var last = points[^1];
            var dir =
                points.Count > 1
                    ? points[^1] - points[^2]
                    : last - Rig.Bones[anchor].World.Row3.Xyz;
            dir = dir.LengthSquared > 1e-10f ? dir.Normalized() : -Vector3.UnitY;
            float reach = 0;
            var distances = new List<float>();
            foreach (var (node, influences) in NodeInfluences())
            {
                float on = influences.Where(x => set.Contains(x.Bone)).Sum(x => x.Weight);
                if (on < 0.5f)
                    continue;
                var p = Graph.Nodes[node];
                distances.Add(DistanceToLine(p, points));
                if (influences.Where(x => x.Bone == chain[^1]).Sum(x => x.Weight) >= 0.3f)
                    reach = Math.Max(reach, Vector3.Dot(p - last, dir));
            }
            if (reach <= 0 && points.Count == 1)
                reach = Math.Max(0.03f, 0.5f * (last - Rig.Bones[anchor].World.Row3.Xyz).Length);
            float thickness =
                distances.Count > 0
                    ? Math.Clamp(
                        distances.OrderBy(x => x).ElementAt(distances.Count / 2),
                        0.01f,
                        0.2f
                    )
                    : 0.03f;
            return (last + dir * reach, thickness);
        }

        static float DistanceToLine(Vector3 p, List<Vector3> line)
        {
            if (line.Count == 1)
                return (p - line[0]).Length;
            float best = float.MaxValue;
            for (int i = 0; i + 1 < line.Count; i++)
                best = Math.Min(
                    best,
                    (p - Geometry.ClosestOnSegment(p, line[i], line[i + 1])).Length
                );
            return best;
        }

        Dictionary<int, (int Bone, float Weight)[]> _nodeInfluences;
        float[] _boneTotals;

        /// <summary>The model's own weights on each welded node, from the first vertex welded into it.</summary>
        Dictionary<int, (int Bone, float Weight)[]> NodeInfluences()
        {
            if (_nodeInfluences != null)
                return _nodeInfluences;
            _nodeInfluences = new();
            _boneTotals = new float[Mesh.Bones.Count];
            for (int node = 0; node < Graph.Nodes.Count; node++)
            {
                var (p, v) = Graph.FirstVertex[node];
                var part = Mesh.Parts[p];
                var influences = part.Bones[v].Zip(part.Weights[v], (b, w) => (b, w)).ToArray();
                _nodeInfluences[node] = influences;
                foreach (var (b, w) in influences)
                    if (b < _boneTotals.Length)
                        _boneTotals[b] += w;
            }
            return _nodeInfluences;
        }

        /// <summary>
        /// The model's weight per bone over every painted limb's nodes and these, and the bones
        /// that carry any weight on these.
        /// </summary>
        (float[] Inside, HashSet<int> Touched) PaintedWeight(IEnumerable<int> nodes)
        {
            var influences = NodeInfluences();
            var own = nodes.ToHashSet();
            var painted = Limbs.Where(l => !l.IsBoneLimb).SelectMany(l => l.Nodes).ToHashSet();
            painted.UnionWith(own);
            var inside = new float[_boneTotals.Length];
            var touched = new HashSet<int>();
            foreach (int node in painted)
                if (influences.TryGetValue(node, out var inf))
                    foreach (var (b, w) in inf)
                    {
                        inside[b] += w;
                        if (own.Contains(node) && w > 0)
                            touched.Add(b);
                    }
            return (inside, touched);
        }

        /// <summary>
        /// The model's bones the painted limbs cover most of: each carries weight on the nodes and
        /// at least half of all it carries lies on them or on other painted limbs. They are what a
        /// rig built from that paint replaces, and what a limb there does not hang from.
        /// </summary>
        public List<int> BonesUnder(IEnumerable<int> nodes)
        {
            var (inside, touched) = PaintedWeight(nodes);
            var keep = KeepNames().ToHashSet();
            return touched
                .Where(b =>
                    b > 0
                    && inside[b] >= 0.5f * _boneTotals[b]
                    && !keep.Contains(Mesh.Bones[b].Name)
                )
                .OrderBy(b => b)
                .ToList();
        }

        /// <summary>
        /// What a build with this paint would do to the model's bones, from the model's own
        /// weights: the bones it touches that would go, all they carry lying under painted limbs
        /// (with the bones below them that carry nothing), and those mostly under the paint that
        /// stay because surface outside it still uses them.
        /// </summary>
        public (List<int> Replaced, List<int> Kept) PaintPreview(IEnumerable<int> nodes)
        {
            var (inside, touched) = PaintedWeight(nodes);
            int n = Mesh.Bones.Count;
            var keep = KeepNames().Select(Mesh.BoneIndex).Where(b => b >= 0).ToHashSet();
            keep.Add(0);
            keep.UnionWith(Mesh.Parts.Select(p => p.ShapeBone));
            foreach (var limb in Limbs.Where(l => l.IsBoneLimb))
            foreach (var names in limb.BoneChains)
            foreach (var name in names)
                if (Mesh.BoneIndex(name) is int b and >= 0)
                {
                    keep.Add(b);
                    keep.Add(Mesh.Bones[b].Parent);
                }
            var set = Enumerable
                .Range(0, n)
                .Where(b =>
                    _boneTotals[b] > 0 && inside[b] >= 0.999f * _boneTotals[b] && !keep.Contains(b)
                )
                .ToHashSet();
            BoneTree.AddChildren(
                set,
                n,
                b => Mesh.Bones[b].Parent,
                b => _boneTotals[b] <= 0 && !keep.Contains(b)
            );
            BoneTree.KeepAncestors(set, n, b => Mesh.Bones[b].Parent);
            //Of those, the ones this paint touches and the empty ones hanging below them.
            bool Mine(int b)
            {
                for (int x = b; x >= 0 && set.Contains(x); x = Mesh.Bones[x].Parent)
                    if (touched.Contains(x))
                        return true;
                return false;
            }
            var replaced = set.Where(Mine).OrderBy(b => b).ToList();
            var kept = touched
                .Where(b =>
                    !set.Contains(b) && !keep.Contains(b) && inside[b] >= 0.5f * _boneTotals[b]
                )
                .OrderBy(b => b)
                .ToList();
            return (replaced, kept);
        }

        /// <summary>
        /// The bone a painted limb hangs from when the model is not hair: the bone carrying most of
        /// its root, or when that one is under the paint, the first bone above it that is not.
        /// </summary>
        int HangBone(Strand strand)
        {
            var influences = NodeInfluences();
            var root = new Dictionary<int, float>();
            var all = new Dictionary<int, float>();
            for (int node = 0; node < Strands.NodeStrand.Length; node++)
            {
                if (
                    Strands.NodeStrand[node] != strand.Id
                    || !influences.TryGetValue(node, out var inf)
                )
                    continue;
                foreach (var (b, w) in inf)
                {
                    all[b] = all.GetValueOrDefault(b) + w;
                    if (Strands.NodeArc[node] <= 0.05f)
                        root[b] = root.GetValueOrDefault(b) + w;
                }
            }
            var from = root.Count > 0 ? root : all;
            if (from.Count == 0)
                return 0;
            int bone = from.MaxBy(x => x.Value).Key;
            var under = BonesUnder(strand.Limb?.Nodes ?? new HashSet<int>()).ToHashSet();
            //A bone that carries nothing moves with its parent, so the limb hangs above it too.
            while (Mesh.Bones[bone].Parent >= 0 && (under.Contains(bone) || _boneTotals[bone] <= 0))
                bone = Mesh.Bones[bone].Parent;
            return bone;
        }

        /// <summary>Lists on each painted limb the model's bones the rig removed for it: those it carried most of.</summary>
        void NoteReplaced()
        {
            foreach (var limb in Limbs.Where(l => !l.IsBoneLimb))
                limb.Replaced.Clear();
            if (Rig.Removed.Count == 0)
                return;
            var influences = NodeInfluences();
            var share = new Dictionary<int, Dictionary<PaintedLimb, float>>();
            for (int node = 0; node < Strands.NodeStrand.Length; node++)
            {
                int s = Strands.NodeStrand[node];
                if (s < 0 || Strands.Strands[s].Limb is not PaintedLimb limb)
                    continue;
                if (!influences.TryGetValue(node, out var inf))
                    continue;
                foreach (var (b, w) in inf)
                {
                    if (!Rig.Removed.Contains(b))
                        continue;
                    if (!share.TryGetValue(b, out var byLimb))
                        share[b] = byLimb = new();
                    byLimb[limb] = byLimb.GetValueOrDefault(limb) + w;
                }
            }
            //A bone that carried nothing goes with the nearest replaced bone above it.
            PaintedLimb Owner(int b)
            {
                for (int x = b; x >= 0; x = Rig.Bones[x].Parent)
                    if (share.TryGetValue(x, out var byLimb))
                        return byLimb.MaxBy(y => y.Value).Key;
                return null;
            }
            foreach (int b in Rig.Removed.OrderBy(x => x))
                Owner(b)?.Replaced.Add(Rig.Bones[b].Name);
        }

        /// <summary>Builds the cloth into an emptied file that declares the piece and collidable types.</summary>
        public void BuildCloth(ClothFile empty)
        {
            UpdateDefaultColliders();
            HairClothGen.Build(empty, Rig, Styles, ClothOptions, Groups, Colliders.ToList());
        }

        /// <summary>The colliders the user placed, in list order.</summary>
        public List<LimbCollider> UserColliders { get; } = new();

        /// <summary>The user's edits of default colliders by name, which a rebuild applies again.</summary>
        public Dictionary<string, LimbCollider> ColliderEdits { get; } = new();

        /// <summary>The default colliders the rig and styles make now, the user's edits applied; reused per name so windows keep their reference.</summary>
        public List<LimbCollider> DefaultColliders { get; private set; } = new();

        readonly Dictionary<string, LimbCollider> _defaultColliders = new();

        /// <summary>Every collider, the defaults first, as the cloth lists them.</summary>
        public IEnumerable<LimbCollider> Colliders => DefaultColliders.Concat(UserColliders);

        /// <summary>Works out the default colliders for the current rig, styles and groups again.</summary>
        public void UpdateDefaultColliders()
        {
            if (Rig == null)
            {
                DefaultColliders = new();
                return;
            }
            var moving = HairClothGen.Moving(Rig, Styles, ClothOptions, Groups);
            var list = new List<LimbCollider>();
            foreach (
                var made in HairClothGen.DefaultColliders(
                    Rig,
                    moving.Select(m => m.Chain),
                    ClothOptions
                )
            )
            {
                if (ColliderEdits.TryGetValue(made.Name, out var edit))
                {
                    var below = made.Below;
                    made.CopyFrom(edit);
                    made.Below = below;
                    made.Default = made.Edited = true;
                }
                if (!_defaultColliders.TryGetValue(made.Name, out var kept))
                    _defaultColliders[made.Name] = kept = new LimbCollider();
                kept.CopyFrom(made);
                list.Add(kept);
            }
            DefaultColliders = list;
        }

        /// <summary>Keeps a default collider's current values as the user's edit of it.</summary>
        public void EditDefault(LimbCollider collider)
        {
            collider.Edited = true;
            ColliderEdits[collider.Name] = collider.Clone();
        }

        /// <summary>Drops the user's edit of a default collider; it is made as generated again.</summary>
        public void ResetDefault(LimbCollider collider)
        {
            ColliderEdits.Remove(collider.Name);
            UpdateDefaultColliders();
        }

        /// <summary>
        /// Whether a limb's cloth collides with a collider: listed, or with no list every limb, or
        /// for a default one those whose piece reaches below its height. A limb moving in another's
        /// piece collides with what that piece does.
        /// </summary>
        public bool Collides(LimbCollider collider, PaintedLimb limb)
        {
            if (collider.Limbs != null)
                return collider.Limbs.Contains(limb);
            if (collider.Below is not float below)
                return true;
            int chain = ChainOf(limb);
            return chain >= 0 && Rig.Chains[GroupOf(chain)[0]].Path[^1].Y < below;
        }

        /// <summary>Turns a limb's collision with a collider on or off, listing the limbs from then on.</summary>
        public void SetCollides(LimbCollider collider, PaintedLimb limb, bool on)
        {
            collider.Limbs ??= Limbs.Where(l => Collides(collider, l)).ToHashSet();
            if (on)
                collider.Limbs.Add(limb);
            else
                collider.Limbs.Remove(limb);
        }

        /// <summary>A collider name no other collider has, from a stem.</summary>
        public string UniqueColliderName(string stem, LimbCollider except = null)
        {
            var names = Colliders.Where(c => c != except).Select(c => c.Name).ToHashSet();
            string name = stem;
            for (int k = 2; names.Contains(name); k++)
                name = $"{stem}_{k}";
            return name;
        }

        /// <summary>
        /// A capsule for one limb to rest against, on the bone it hangs from, standing for the
        /// body there. For a limb that hangs, a column through that bone beside the limb from its
        /// root to its tip; otherwise along that bone toward a neighbour that is not part of a
        /// chain, its child or else its parent. Its radius reaches to the limb's nearest free point
        /// less a hanging particle's radius and a margin, so the limb starts outside it and cannot
        /// swing in. Null when the limb has no chain.
        /// </summary>
        public LimbCollider CapsuleFor(PaintedLimb limb)
        {
            int index = ChainOf(limb);
            if (index < 0)
                return null;
            var chain = Rig.Chains[index];
            var anchor = Rig.Bones[chain.Anchor];
            var origin = anchor.World.Row3.Xyz;
            var root = chain.FreeRoot;
            var tip = chain.At(chain.Length);
            var dir = tip - root;
            dir = dir.LengthSquared > 1e-10f ? dir.Normalized() : -Vector3.UnitY;
            var a = origin + dir * Vector3.Dot(root - origin, dir);
            var b = origin + dir * Vector3.Dot(tip - origin, dir);
            if (dir.Y > -0.7f && BodyNeighbour(chain.Anchor) is int other)
            {
                a = origin;
                b = Rig.Bones[other].World.Row3.Xyz;
            }
            var axis = new List<Vector3> { a, b };
            float nearest = float.MaxValue;
            for (float s = chain.Held; s <= chain.Length + 1e-4f; s += 0.01f)
                nearest = Math.Min(nearest, DistanceToLine(chain.At(s), axis));
            float radius = Math.Clamp(nearest - CapsuleMargin, 0.02f, 0.5f);
            var inv = Matrix4.Invert(anchor.World);
            string safe = HairRigger.BoneSafe(limb.Name);
            return new LimbCollider
            {
                Name = UniqueColliderName("Collidable_" + (safe.Length > 0 ? safe : "Limb")),
                Bone = anchor.Name,
                Start = Vector3.TransformPosition(a, inv),
                End = Vector3.TransformPosition(b, inv),
                Radius = radius,
                Limbs = new HashSet<PaintedLimb> { limb },
            };
        }

        /// <summary>A bone next to this one that is body rather than limb: a child no chain moves, else the parent; at least a centimetre away.</summary>
        int? BodyNeighbour(int bone)
        {
            var moved = Rig.Chains.SelectMany(c => c.Bones).ToHashSet();
            var at = Rig.Bones[bone].World.Row3.Xyz;
            bool Usable(int b) =>
                !moved.Contains(b)
                && !Rig.Removed.Contains(b)
                && (Rig.Bones[b].World.Row3.Xyz - at).Length > 0.01f;
            for (int b = 0; b < Rig.Bones.Count; b++)
                if (Rig.Bones[b].Parent == bone && Usable(b))
                    return b;
            int parent = Rig.Bones[bone].Parent;
            return parent >= 0 && Usable(parent) ? parent : null;
        }

        /// <summary>The room a limb's capsule leaves to the limb: a hanging particle's radius and a margin.</summary>
        public const float CapsuleMargin = 0.035f;

        /// <summary>
        /// The bone a new collider goes on: the one the given limb, or else the last limb, hangs
        /// from; without limbs on hair the head, else the first spine or neck bone, else the root.
        /// </summary>
        public string DefaultColliderBone(PaintedLimb limb)
        {
            if (Rig == null)
                return Mesh.Bones.Count > 0 ? Mesh.Bones[0].Name : null;
            int chain = ChainOf(limb);
            if (chain < 0)
                chain = Limbs.Select(ChainOf).Where(c => c >= 0).DefaultIfEmpty(-1).Last();
            if (chain >= 0)
                return Rig.Bones[Rig.Chains[chain].Anchor].Name;
            if (Hair && Rig.BoneIndex(ClothOptions.HeadBone) >= 0)
                return ClothOptions.HeadBone;
            var body = Rig
                .Bones.Where((b, i) => !Rig.Removed.Contains(i))
                .FirstOrDefault(b => b.Name.StartsWith("Spine") || b.Name.StartsWith("Neck"));
            return (body ?? Rig.Bones[0]).Name;
        }

        /// <summary>Whether the cloth has anything to run; a cloth without pieces must not replace the stock one.</summary>
        public bool HasCloth =>
            Rig != null && Rig.Chains.Where((_, i) => Styles[i].Motion != StrandMotion.Rigid).Any();

        /// <summary>Whether the model file must change: bones were added or removed, or weights moved.</summary>
        public bool ChangesModel => Rig != null && (Rig.AddsBones || Rig.NodeWeights.Count > 0);

        /// <summary>Writes the rig's bones and weights into the model the mesh was read from.</summary>
        public void WriteModel(Model model) => RigWriter.Apply(model, Mesh, Rig);

        /// <summary>
        /// This generator on an edited copy of its model, its skeleton changed: every setting,
        /// limb and collidable carried over as the same objects, and each painted limb's nodes
        /// moved to the nodes of the new surface through the vertices welded into them. The
        /// options are shared. Nothing is built.
        /// </summary>
        public HairGenerator Rebased(SkinnedMesh mesh, HeadShape head)
        {
            var next = new HairGenerator(mesh, head, LimbOptions, RigOptions, ClothOptions)
            {
                MergeCloseLimbs = MergeCloseLimbs,
            };
            var map = new int[Graph.Nodes.Count];
            Array.Fill(map, -1);
            for (int p = 0; p < Math.Min(Graph.NodeOf.Length, next.Graph.NodeOf.Length); p++)
            {
                var from = Graph.NodeOf[p];
                var to = next.Graph.NodeOf[p];
                for (int v = 0; v < Math.Min(from.Length, to.Length); v++)
                    if (from[v] >= 0 && to[v] >= 0 && map[from[v]] < 0)
                        map[from[v]] = to[v];
            }
            foreach (var limb in Limbs)
            {
                if (!limb.IsBoneLimb)
                    limb.Nodes = limb
                        .Nodes.Where(n => n >= 0 && n < map.Length && map[n] >= 0)
                        .Select(n => map[n])
                        .ToHashSet();
                next.Limbs.Add(limb);
            }
            next.UserColliders.AddRange(UserColliders);
            foreach (var (name, edit) in ColliderEdits)
                next.ColliderEdits[name] = edit;
            foreach (var (name, kept) in _defaultColliders)
                next._defaultColliders[name] = kept;
            return next;
        }
    }
}
