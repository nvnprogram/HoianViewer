using System;
using System.Collections.Generic;
using System.Linq;
using OpenTK;
using PlayerViewer.Phive;
using PlayerViewer.Rigging;

namespace PlayerViewer.HairGen
{
    /// <summary>
    /// A chain of bones a cloth piece drives, with the line its strip follows. The bones are in
    /// the rig's skeleton; the path runs from the root of the strand to its tip, which may lie
    /// past the last bone.
    /// </summary>
    public class HairChain
    {
        public string Name;
        public int Anchor;
        public int[] Bones;
        public List<Vector3> Path = new();
        public List<float> PathArc = new();

        /// <summary>Arc length of each bone along the path.</summary>
        public float[] BoneArc;

        /// <summary>Typical cross section radius of the strand where it moves.</summary>
        public float Thickness;

        /// <summary>Arc from the root over which the chain lies on the head; its cloth holds that stretch to the anchor.</summary>
        public float Held;

        /// <summary>Where the chain leaves the head, the root of the part that moves.</summary>
        public Vector3 FreeRoot => At(Held);

        public float FreeLength => Length - Held;

        /// <summary>The strand it was made from, or -1 for a chain taken from the model's bones.</summary>
        public int Strand = -1;

        /// <summary>The limb it was built for, painted or picked from bones; null for a found strand.</summary>
        public PaintedLimb Limb;

        /// <summary>
        /// The point its side axis is taken around (<see cref="HairRigger.SideAxis"/>): null for
        /// the head's centre on hair, else the bone it hangs from.
        /// </summary>
        public Vector3? Hub;

        public float Length => PathArc.Count > 0 ? PathArc[^1] : 0;

        public Vector3 At(float arc) => Polyline.At(Path, PathArc, arc);

        /// <summary>Unit direction of the path at an arc length.</summary>
        public Vector3 Tangent(float arc)
        {
            float step = Math.Max(Length * 0.05f, 0.01f);
            var d = At(Math.Min(arc + step, Length)) - At(Math.Max(arc - step, 0));
            return d.LengthSquared > 1e-12f ? d.Normalized() : -Vector3.UnitY;
        }

        /// <summary>
        /// Unit vector across the chain at a point: the side its limb's aim sets, made square to
        /// the strand, else <see cref="HairRigger.SideAxis"/>.
        /// </summary>
        public Vector3 SideAt(Vector3 at, Vector3 tangent)
        {
            if (Limb?.Aim?.Side is Vector3 side)
            {
                var square = side - tangent * Vector3.Dot(side, tangent);
                if (square.LengthSquared > 1e-4f)
                    return square.Normalized();
            }
            return HairRigger.SideAxis(at, tangent, Hub);
        }

        public void SetPath(IEnumerable<Vector3> points)
        {
            Path = points.ToList();
            PathArc = Polyline.Arcs(Path);
        }
    }

    /// <summary>
    /// The skeleton a hair ends up with, the weights that change, and the chains its cloth drives.
    /// <see cref="SkinRig.Removed"/> holds the bones painted limbs replaced
    /// (<see cref="HairRigger.DropReplaced"/>).
    /// </summary>
    public class HairRig : SkinRig
    {
        public List<HairChain> Chains = new();

        /// <summary>The skin weight each bone carries over the model once the rig is written, summed over the vertices.</summary>
        public float[] Weight = Array.Empty<float>();
    }

    public class RigOptions
    {
        /// <summary>Distance between generated bones along a strand; the stock strands put their levels about this far apart.</summary>
        public float Spacing = 0.18f;

        /// <summary>Surface just below a strand's root blends from the head into the strand over this distance.</summary>
        public float RootBlend = 0.05f;

        /// <summary>
        /// Past the root the strand takes over from the head across this many of its root radii, so a
        /// wide strand eases in rather than swinging its whole breadth from the root line.
        /// </summary>
        public float RootFade = 1f;

        /// <summary>How far either side of a strand's seam with the rest of the hair its weights are blended.</summary>
        public float SeamWidth = 0.06f;

        public int SeamPasses = 8;

        public string BonePrefix = "HairGen";

        public string AnchorBone = "Head_Root";

        /// <summary>
        /// Hair: every chain hangs from <see cref="AnchorBone"/> and bones are framed about the
        /// head. Otherwise each strand hangs from its own <see cref="Strand.Anchor"/>.
        /// </summary>
        public bool Hair = true;
    }

    public static partial class HairRigger
    {
        static readonly Vector3 HeadCentre = HeadShape.DefaultCentre;

        /// <summary>
        /// A unit vector across a strand at a point, square to the strand: along the head's
        /// surface, or with a hub, round the bone it hangs from.
        /// </summary>
        public static Vector3 SideAxis(Vector3 at, Vector3 tangent, Vector3? hub = null)
        {
            if (hub is Vector3 centre)
                return SideAxisAbout(at, tangent, centre);
            var radial = at - HeadCentre;
            var side = Vector3.Cross(tangent, radial);
            if (side.LengthSquared < 1e-8f)
                side = Vector3.Cross(tangent, Vector3.UnitZ);
            if (side.LengthSquared < 1e-8f)
                side = Vector3.UnitX;
            return side.Normalized();
        }

        //Square to the radial from the hub, or to its level part when the strand runs along it.
        static Vector3 SideAxisAbout(Vector3 at, Vector3 tangent, Vector3 hub)
        {
            var radial = at - hub;
            var side = Vector3.Cross(tangent, radial);
            if (side.LengthSquared < 0.04f * radial.LengthSquared * tangent.LengthSquared)
            {
                radial.Y = 0;
                side = Vector3.Cross(tangent, radial);
            }
            if (side.LengthSquared < 1e-8f)
                side = Vector3.Cross(tangent, Vector3.UnitZ);
            if (side.LengthSquared < 1e-8f)
                side = Vector3.UnitX;
            return side.Normalized();
        }

        /// <summary>A bone frame at a point: X along the strand, Y across it, Z out of the surface.</summary>
        static Matrix4 Frame(Vector3 at, Vector3 tangent, HairChain chain)
        {
            var y = chain.SideAt(at, tangent);
            var z = Vector3.Cross(tangent, y).Normalized();
            y = Vector3.Cross(z, tangent).Normalized();
            return new Matrix4(
                new Vector4(tangent, 0),
                new Vector4(y, 0),
                new Vector4(z, 0),
                new Vector4(at, 1)
            );
        }

        /// <summary>
        /// A name made safe for bone and piece names: every character but an ASCII letter or digit
        /// becomes an underscore, and the ends lose theirs. Empty when nothing is left.
        /// </summary>
        public static string BoneSafe(string name) =>
            new string(
                (name ?? "").Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_').ToArray()
            ).Trim('_');

        /// <summary>A strand's name made safe for bone and piece names, or Strand and its id.</summary>
        static string ChainName(Strand strand)
        {
            var clean = BoneSafe(strand.Name);
            return clean.Length > 0 ? clean : $"Strand{strand.Id}";
        }

        /// <summary>
        /// Generates bones along every strand that leaves the head and weights the strand's
        /// surface to them. Strands branching off another follow their parent rigidly.
        /// </summary>
        public static HairRig Generate(
            SkinnedMesh mesh,
            StrandSet strands,
            RigOptions options = null
        )
        {
            options ??= new RigOptions();
            var rig = new HairRig
            {
                Bones = mesh.Bones.ToList(),
                OriginalBoneCount = mesh.Bones.Count,
                Graph = strands.Graph,
            };
            int headAnchor = Math.Max(0, rig.BoneIndex(options.AnchorBone));
            var names = new HashSet<string>(rig.Bones.Select(b => b.Name));
            var strandBones = new Dictionary<int, (int[] Bones, float[] Arc)>();
            int AnchorOf(Strand s) => options.Hair || s.Anchor < 0 ? headAnchor : s.Anchor;

            foreach (var strand in strands.Strands.Where(s => s.Parent < 0))
            {
                //A held stretch and the rest are spaced apart, so a bone sits where the strand leaves
                //the head and the cloth turns it there as it turns a root.
                float held = Math.Clamp(strand.Held, 0, strand.Length);
                int heldCount =
                    held > 0 ? Math.Max(1, (int)MathF.Round(held / options.Spacing)) : 0;
                int freeCount = Math.Max(
                    1,
                    (int)MathF.Round((strand.Length - held) / options.Spacing)
                );
                int count = heldCount + freeCount;
                int anchor = AnchorOf(strand);
                var chain = new HairChain
                {
                    Name = ChainName(strand),
                    Anchor = anchor,
                    Strand = strand.Id,
                    Limb = strand.Limb,
                    Hub = options.Hair ? null : rig.Bones[anchor].World.Row3.Xyz,
                    Held = held,
                    Thickness = strand
                        .Radius.Where((_, i) => strand.Arc[i] >= held)
                        .DefaultIfEmpty(strand.Radius.Average())
                        .Average(),
                };
                chain.SetPath(strand.Centre);
                var bones = new int[count];
                var arcs = new float[count];
                int parent = anchor;
                for (int j = 0; j < count; j++)
                {
                    float arc =
                        j < heldCount
                            ? j * held / heldCount
                            : held + (j - heldCount) * (strand.Length - held) / freeCount;
                    var world = Frame(strand.At(arc), chain.Tangent(arc), chain);
                    string name = $"{options.BonePrefix}_{strand.Id}_{j + 1}";
                    for (int k = 2; !names.Add(name); k++)
                        name = $"{options.BonePrefix}_{strand.Id}_{j + 1}_{k}";
                    bones[j] = rig.Bones.Count;
                    arcs[j] = arc;
                    rig.Bones.Add(new AuthorBone(name, parent, world));
                    parent = bones[j];
                }
                chain.Bones = bones;
                chain.BoneArc = arcs;
                rig.Chains.Add(chain);
                strandBones[strand.Id] = (bones, arcs);
            }

            for (int node = 0; node < strands.NodeStrand.Length; node++)
            {
                int s = strands.NodeStrand[node];
                if (s < 0)
                    continue;
                var strand = strands.Strands[s];
                float arc = strands.NodeArc[node];
                while (strand.Parent >= 0)
                {
                    arc = strand.ParentArc;
                    strand = strands.Strands[strand.Parent];
                }
                if (!strandBones.TryGetValue(strand.Id, out var chain))
                    continue;
                int anchor = AnchorOf(strand);
                float fade = Math.Min(options.RootFade * strand.Radius[0], strand.Length * 0.3f);
                float held = Math.Clamp(strand.Held, 0, strand.Length);
                if (held > 0)
                {
                    float liftFade = Math.Min(
                        options.RootFade * strand.RadiusAt(held),
                        (strand.Length - held) * 0.3f
                    );
                    rig.NodeWeights[node] = BlendHeld(
                        chain.Bones,
                        chain.Arc,
                        held,
                        arc,
                        anchor,
                        options.RootBlend,
                        fade,
                        liftFade
                    );
                }
                else
                    rig.NodeWeights[node] = BlendAlong(
                        chain.Bones,
                        chain.Arc,
                        arc,
                        anchor,
                        options.RootBlend,
                        fade
                    );
            }
            SmoothSeams(rig, mesh, strands, options);
            return rig;
        }

        /// <summary>
        /// Blends the weights across every seam between a strand and the rest of the hair, or
        /// between two strands, so the surface there bends rather than tears: nodes within
        /// <see cref="RigOptions.SeamWidth"/> of a seam are averaged with their neighbours a few
        /// times, the rest held as they are.
        /// </summary>
        static void SmoothSeams(
            HairRig rig,
            SkinnedMesh mesh,
            StrandSet strands,
            RigOptions options
        )
        {
            var graph = strands.Graph;
            int n = graph.Nodes.Count;
            var weights = new Dictionary<int, float>[n];
            for (int node = 0; node < n; node++)
            {
                var (p, v) = graph.FirstVertex[node];
                weights[node] = rig.NodeWeights.TryGetValue(node, out var w)
                    ? w.ToDictionary(x => x.Bone, x => x.Weight)
                    : mesh.Parts[p]
                        .Bones[v]
                        .Zip(mesh.Parts[p].Weights[v])
                        .GroupBy(x => x.First)
                        .ToDictionary(g => g.Key, g => g.Sum(x => x.Second));
            }
            var seam = Enumerable
                .Range(0, n)
                .Where(v =>
                    weights[v] != null
                    && graph
                        .Adjacent[v]
                        .Any(a => strands.NodeStrand[a.Node] != strands.NodeStrand[v])
                )
                .ToList();
            if (seam.Count == 0)
                return;
            var distance = graph.Distances(seam);
            //The rest of the hair takes less of the blend, so a strand rooted inside the cap does not drag the cap with it.
            var zone = Enumerable
                .Range(0, n)
                .Where(v =>
                    weights[v] != null
                    && distance[v]
                        <= (
                            strands.NodeStrand[v] >= 0
                                ? options.SeamWidth
                                : options.SeamWidth * 0.4f
                        )
                )
                .ToList();
            for (int pass = 0; pass < options.SeamPasses; pass++)
            {
                var next = new Dictionary<int, Dictionary<int, float>>();
                foreach (int v in zone)
                {
                    var sum = new Dictionary<int, float>(weights[v]);
                    int count = 1;
                    foreach (var (a, _) in graph.Adjacent[v])
                    {
                        if (weights[a] == null)
                            continue;
                        foreach (var (bone, w) in weights[a])
                            sum[bone] = sum.GetValueOrDefault(bone) + w;
                        count++;
                    }
                    next[v] = sum.ToDictionary(x => x.Key, x => x.Value / count);
                }
                foreach (var (v, w) in next)
                    weights[v] = w;
            }
            foreach (int v in zone)
            {
                var top = weights[v]
                    .Where(x => x.Value > 1e-4f)
                    .OrderByDescending(x => x.Value)
                    .Take(4)
                    .ToList();
                float total = top.Sum(x => x.Value);
                rig.NodeWeights[v] = top.Select(x => (x.Key, x.Value / total)).ToArray();
            }
        }

        /// <summary>
        /// Weights along a chain: linear between the two bones around the arc, the whole faded in
        /// from the anchor between rootBlend below the root and rootFade past it.
        /// </summary>
        static (int, float)[] BlendAlong(
            int[] bones,
            float[] arcs,
            float arc,
            int anchor,
            float rootBlend,
            float rootFade
        )
        {
            float w = Math.Clamp((arc + rootBlend) / (rootBlend + rootFade), 0, 1);
            if (w <= 0)
                return new[] { (anchor, 1f) };
            var along = new[] { (bones[^1], 1f) };
            if (arc < 0)
                along = new[] { (bones[0], 1f) };
            else
                for (int j = 0; j + 1 < bones.Length; j++)
                    if (arc < arcs[j + 1])
                    {
                        float t = (arc - arcs[j]) / (arcs[j + 1] - arcs[j]);
                        along = new[] { (bones[j], 1 - t), (bones[j + 1], t) };
                        break;
                    }
            if (w >= 1)
                return along;
            return along.Select(x => (x.Item1, x.Item2 * w)).Append((anchor, 1 - w)).ToArray();
        }

        /// <summary>
        /// Weights along a chain part of which lies on the head: the bones there carry the surface
        /// down to where it leaves as the anchor carries a root, and the moving bones ease in below,
        /// so no surface above the lift moves with a bone that turns about it.
        /// </summary>
        static (int, float)[] BlendHeld(
            int[] bones,
            float[] arcs,
            float held,
            float arc,
            int anchor,
            float rootBlend,
            float rootFade,
            float liftFade
        )
        {
            int split = Array.FindIndex(arcs, a => a >= held - 1e-4f);
            if (split <= 0)
                return BlendAlong(bones, arcs, arc, anchor, rootBlend, rootFade);
            var free = BlendAlong(
                bones[split..],
                arcs[split..].Select(a => a - held).ToArray(),
                arc - held,
                -1,
                rootBlend,
                liftFade
            );
            float rest = free.Where(x => x.Item1 < 0).Sum(x => x.Item2);
            var result = free.Where(x => x.Item1 >= 0).ToList();
            if (rest > 0)
                result.AddRange(
                    BlendAlong(bones[..split], arcs[..split], arc, anchor, rootBlend, rootFade)
                        .Select(x => (x.Item1, x.Item2 * rest))
                );
            return result.GroupBy(x => x.Item1).Select(g => (g.Key, g.Sum(x => x.Item2))).ToArray();
        }

        /// <summary>
        /// A chain straight from the skeleton's bones, its path running on to the tip when that
        /// lies beyond the last bone.
        /// </summary>
        public static HairChain ChainFromBones(
            IReadOnlyList<AuthorBone> bones,
            int[] chain,
            int anchor,
            Vector3 tip,
            float thickness
        ) => ChainOnBones(bones, chain.ToList(), anchor, tip, thickness, -1);

        static HairChain ChainOnBones(
            IReadOnlyList<AuthorBone> bones,
            List<int> chain,
            int anchor,
            Vector3 tip,
            float thickness,
            int strand
        )
        {
            var result = new HairChain
            {
                Name = bones[chain[0]].Name,
                Anchor = anchor,
                Bones = chain.ToArray(),
                Strand = strand,
                Thickness = thickness,
            };
            var points = chain.Select(b => bones[b].World.Row3.Xyz).ToList();
            var last = points[^1];
            var dir =
                points.Count > 1
                    ? (points[^1] - points[^2]).Normalized()
                    : (tip - last).Normalized();
            if (Vector3.Dot(tip - last, dir) > 0.03f)
                points.Add(tip);
            result.SetPath(points);
            result.BoneArc = chain.Select((_, i) => result.PathArc[i]).ToArray();
            return result;
        }

        /// <summary>
        /// Marks the model's bones a painted limb replaced, in <see cref="HairRig.Removed"/>: those
        /// that carried weight where the rig rewrote it and carry none once it is written, with
        /// their descendants that skin nothing. A bone stays when a chain hangs from it or drives
        /// it, a shape is bound to it, it is named in <paramref name="keepNames"/>, or a bone that
        /// stays is below it. Also fills <see cref="HairRig.Weight"/>.
        /// </summary>
        public static void DropReplaced(
            HairRig rig,
            SkinnedMesh mesh,
            IEnumerable<string> keepNames
        )
        {
            int n = rig.OriginalBoneCount;
            var before = new float[n];
            var after = new float[rig.Bones.Count];
            var region = new bool[n];
            int anchor = rig.ShapeAnchor;
            var keep = new HashSet<int> { 0, anchor };
            foreach (var chain in rig.Chains)
            {
                keep.Add(chain.Anchor);
                keep.UnionWith(chain.Bones);
            }
            keep.UnionWith(rig.Unskinned);
            foreach (var name in keepNames)
                if (rig.BoneIndex(name) is int b and >= 0)
                    keep.Add(b);

            var graph = rig.Graph;
            for (int p = 0; p < mesh.Parts.Count; p++)
            {
                var part = mesh.Parts[p];
                bool changes = rig.Rewrites(p);
                bool wasRigid = part.SkinCount < 2;
                keep.Add(changes && wasRigid ? anchor : part.ShapeBone);
                for (int v = 0; v < part.Rest.Length; v++)
                {
                    for (int k = 0; k < part.Bones[v].Length; k++)
                        if (part.Bones[v][k] < n)
                            before[part.Bones[v][k]] += part.Weights[v][k];
                    int node = graph.WeightNodeOf[p][v];
                    bool rewritten = changes && node >= 0 && rig.NodeWeights.ContainsKey(node);
                    if (rewritten)
                        for (int k = 0; k < part.Bones[v].Length; k++)
                            if (part.Bones[v][k] < n && part.Weights[v][k] > 0)
                                region[part.Bones[v][k]] = true;
                    var influences = changes
                        ? rig.Influences(part, v, node, anchor, wasRigid)
                        : part.Bones[v].Zip(part.Weights[v], (b, w) => (b, w)).ToArray();
                    foreach (var (b, w) in influences)
                        after[b] += w;
                }
            }
            rig.Weight = after;

            var removed = new HashSet<int>();
            for (int b = 0; b < n; b++)
                if (region[b] && after[b] <= 0 && !keep.Contains(b))
                    removed.Add(b);
            //Bones below a replaced one that skin nothing, as a chain's tip bone, go with it.
            BoneTree.AddChildren(
                removed,
                n,
                b => rig.Bones[b].Parent,
                b => before[b] <= 0 && after[b] <= 0 && !keep.Contains(b)
            );
            BoneTree.KeepAncestors(removed, rig.Bones.Count, b => rig.Bones[b].Parent);
            rig.Removed = removed;
        }
    }
}
