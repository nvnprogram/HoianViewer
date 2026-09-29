using System;
using System.Collections.Generic;
using System.Linq;
using OpenTK;
using PlayerViewer.Rigging;

namespace PlayerViewer.HairGen
{
    public class LimbOptions
    {
        /// <summary>Spacing of the cross sections the centre line is traced from.</summary>
        public float RingStep = 0.02f;

        /// <summary>A limb with no unpainted border grows from its surface within this distance of its closest approach to the scalp.</summary>
        public float SourceBand = 0.06f;

        /// <summary>
        /// Root nodes further from the tip than the farthest one by more than this share of the
        /// limb's length are dropped, so the sides of a lock painted out of a sheet are not roots.
        /// </summary>
        public float RootTrim = 0.3f;

        /// <summary>Of a border loop that runs along the limb, the part within this of its top, by how far off the head it is, is the root.</summary>
        public float RootBand = 0.06f;

        /// <summary>A border farther than this below the limb's top, by how far off the head it is, is not its root; the top is.</summary>
        public float TopReach = 0.05f;

        /// <summary>How much height counts against distance off the scalp when telling a limb's root end from its tip.</summary>
        public float HeightWeight = 0.5f;

        /// <summary>Separate pieces of a limb closer than this are joined into one, as the lobes and ties of a braid modelled apart.</summary>
        public float JoinGap = 0.03f;

        /// <summary>Fewer painted nodes than this make no strand.</summary>
        public int MinNodes = 6;

        /// <summary>A limb shorter than this from root to tip makes no strand.</summary>
        public float MinLength = 0.04f;

        /// <summary>A branch off the limb at least this long, and <see cref="BranchShare"/> of it, is reported.</summary>
        public float BranchReport = 0.12f;

        /// <summary>The share of the limb's length a reported branch also reaches.</summary>
        public float BranchShare = 0.25f;

        /// <summary>
        /// Rings at a level are one cross section when the surface within this many ring steps
        /// either side of their level joins them, as the two arcs of a level set folding on a curl.
        /// </summary>
        public float BandReach = 1;

        /// <summary>A cross section lies on the head when its nodes' median distance to the head, face included, is under this.</summary>
        public float LieDistance = 0.06f;

        /// <summary>Only this share of a limb from its root may be held on the head where it lies on it.</summary>
        public float LieReach = 0.6f;

        /// <summary>A stretch lying on the head shorter than this is not held: its own bone would sit next to the root's.</summary>
        public float MinHeld = 0.05f;

        /// <summary>A branch is reported only where it is farther than this off the head.</summary>
        public float ForkOffHead = 0.08f;

        /// <summary>A separate piece farther than this from the limb's surface is reported.</summary>
        public float StrayReport = 0.05f;
    }

    /// <summary>
    /// Strands from painted limbs. Each limb grows from the end where its paint borders the rest
    /// of the hair nearer the head, or without a head, its higher end; distance along the painted
    /// surface from there is the level function, and the centroids of its cross sections are the
    /// centre line, which lies inside the painted hull.
    /// </summary>
    public static class LimbStrands
    {
        sealed class Ring
        {
            public Vector3 Centroid;
            public float Perimeter;
            public bool Closed;
            public int Triangle;
            public readonly List<Vector3> Points = new();
        }

        /// <summary>The rings at one level on one piece of the band around it, as one cross section.</summary>
        sealed class Section
        {
            public readonly int Key;
            public readonly List<Ring> Rings;
            public readonly float Perimeter;
            public readonly Vector3 Centroid;

            /// <summary>How far its outline reaches from its centroid.</summary>
            public readonly float Extent;

            public Section(int key, List<Ring> rings)
            {
                Key = key;
                Rings = rings;
                Perimeter = rings.Sum(r => r.Perimeter);
                Centroid =
                    rings.Aggregate(Vector3.Zero, (s, r) => s + r.Centroid * r.Perimeter)
                    / Math.Max(Perimeter, 1e-9f);
                var centroid = Centroid;
                Extent = rings
                    .SelectMany(r => r.Points)
                    .Select(p => (p - centroid).Length)
                    .DefaultIfEmpty(0)
                    .Max();
            }
        }

        /// <summary>
        /// A strand per limb in order, each with <see cref="Strand.Limb"/> set; a limb that makes no
        /// strand is left out and says why in its <see cref="PaintedLimb.Problem"/>. A node painted
        /// into several limbs belongs to the last of them. Without a head (not hair) height takes
        /// the place of the distance off the scalp and nothing is held.
        /// </summary>
        public static StrandSet Build(
            MeshGraph graph,
            HeadShape head,
            IReadOnlyList<PaintedLimb> limbs,
            LimbOptions options = null,
            float[] scalpCache = null,
            float[] surfaceCache = null
        )
        {
            options ??= new LimbOptions();
            int n = graph.Nodes.Count;
            if (scalpCache == null || scalpCache.Length != n)
            {
                scalpCache = new float[n];
                Array.Fill(scalpCache, float.NaN);
            }
            float Scalp(int v)
            {
                if (head == null)
                    return -graph.Nodes[v].Y;
                if (float.IsNaN(scalpCache[v]))
                    scalpCache[v] = head.CraniumDistance(graph.Nodes[v]);
                return scalpCache[v];
            }
            var set = new StrandSet
            {
                Graph = graph,
                NodeStrand = Enumerable.Repeat(-1, n).ToArray(),
                NodeArc = new float[n],
                ScalpDistance = scalpCache,
                Geodesic = Enumerable.Repeat(float.PositiveInfinity, n).ToArray(),
            };

            var owner = Enumerable.Repeat(-1, n).ToArray();
            for (int l = 0; l < limbs.Count; l++)
                foreach (int v in limbs[l].Nodes)
                    if (v >= 0 && v < n)
                        owner[v] = l;
            var nodesOf = new List<int>[limbs.Count];
            for (int l = 0; l < limbs.Count; l++)
                nodesOf[l] = new List<int>();
            for (int v = 0; v < n; v++)
                if (owner[v] >= 0)
                    nodesOf[owner[v]].Add(v);

            var trianglesOf = new List<int>[limbs.Count];
            for (int l = 0; l < limbs.Count; l++)
                trianglesOf[l] = new List<int>();
            for (int t = 0; t < graph.Triangles.Count; t++)
            {
                var (a, b, c) = graph.Triangles[t];
                if (owner[a] >= 0 && owner[a] == owner[b] && owner[b] == owner[c])
                    trianglesOf[owner[a]].Add(t);
            }

            //Every node's distance off the head, which the rig's weights ease by; the seams' blend
            //reaches past the limbs.
            if (surfaceCache == null || surfaceCache.Length != n)
                surfaceCache = Enumerable.Repeat(float.NaN, n).ToArray();
            for (int v = 0; v < n && head != null; v++)
                if (float.IsNaN(surfaceCache[v]))
                    surfaceCache[v] = head.SurfaceDistance(graph.Nodes[v]);
            set.HeadDistance =
                head != null
                    ? surfaceCache.ToArray()
                    : Enumerable.Repeat(float.PositiveInfinity, n).ToArray();
            for (int l = 0; l < limbs.Count; l++)
            {
                var limb = limbs[l];
                limb.Problem = null;
                var strand = BuildOne(
                    set,
                    owner,
                    l,
                    nodesOf[l],
                    trianglesOf[l],
                    Scalp,
                    head,
                    limb.HoldOnHead && head != null,
                    limb.Aim,
                    options,
                    out string problem
                );
                limb.Problem = problem;
                if (strand == null)
                    continue;
                strand.Limb = limb;
                strand.Name = limb.Name;
            }
            return set;
        }

        static Strand BuildOne(
            StrandSet set,
            int[] owner,
            int limb,
            List<int> nodes,
            List<int> triangles,
            Func<int, float> scalp,
            HeadShape head,
            bool hold,
            LimbAim aim,
            LimbOptions o,
            out string problem
        )
        {
            problem = null;
            var graph = set.Graph;
            if (nodes.Count < o.MinNodes || triangles.Count == 0)
            {
                problem =
                    nodes.Count == 0 ? "Nothing painted." : "Too little painted to make a strand.";
                return null;
            }

            //Pieces that touch, like the lobes and ties of a braid modelled apart, are joined; the
            //biggest group is the limb, and the others follow it.
            var pieces = Pieces(graph, owner, limb, nodes);
            var joins = Joins(graph, pieces, o.JoinGap, out var groups);
            var area = NodeAreas(graph, triangles);
            var body = groups.MaxBy(p => p.Sum(v => area.GetValueOrDefault(v)));
            var inBody = new HashSet<int>(body);
            var bodyTriangles = triangles
                .Where(t => inBody.Contains(graph.Triangles[t].A))
                .ToList();
            if (body.Count < o.MinNodes || bodyTriangles.Count == 0)
            {
                problem = "The paint is scattered; no piece is big enough to make a strand.";
                return null;
            }
            if (aim is { Straight: true })
                return Aimed(set, body, groups, aim, hold, o, out problem);

            var root = Root(graph, inBody, body, joins, scalp, o);
            var previous = new int[graph.Nodes.Count];
            var g = graph.Distances(root, inBody, joins, previous);
            int tip = body.MaxBy(v => float.IsInfinity(g[v]) ? -1 : g[v]);
            float top = g[tip];
            if (top < o.MinLength)
            {
                problem = "Too short to make a strand.";
                return null;
            }

            var strand = new Strand { Id = set.Strands.Count, Island = graph.Island[root[0]] };
            var notes = new List<string>();
            float h = o.RingStep;
            int levels = (int)MathF.Floor(top / h - 0.5f) + 1;
            float Level(int k) => (k + 0.5f) * h;

            //The main line runs from the root to the farthest painted point, along the shortest
            //path between them; at each level it passes through one cross section.
            var path = new List<int> { tip };
            while (previous[path[^1]] >= 0)
                path.Add(previous[path[^1]]);
            path.Reverse();

            var nodeTriangle = new Dictionary<int, int>();
            foreach (int t in bodyTriangles)
            {
                var (a, b, c) = graph.Triangles[t];
                nodeTriangle[a] = nodeTriangle[b] = nodeTriangle[c] = t;
            }

            //Every ring at a level is binned by the piece of the band around it that it lies on,
            //links between pieces included, so a braid's interleaved lobes are one section and a
            //side panel's width is another.
            var bands = new List<Dictionary<int, int>>();
            var mainParts = new List<HashSet<int>>();
            int step = 0;
            for (int k = 0; k < levels; k++)
            {
                float level = Level(k);
                var band = Band(
                    graph,
                    g,
                    bodyTriangles,
                    (k - o.BandReach) * h,
                    (k + 1 + o.BandReach) * h,
                    joins
                );
                bands.Add(band);
                var rings = Sections(graph, g, bodyTriangles, level);
                while (step + 1 < path.Count && g[path[step + 1]] < level)
                    step++;
                int u = path[step],
                    w = path[Math.Min(step + 1, path.Count - 1)];
                var crossing =
                    g[w] > g[u]
                        ? Vector3.Lerp(
                            graph.Nodes[u],
                            graph.Nodes[w],
                            Math.Clamp((level - g[u]) / (g[w] - g[u]), 0, 1)
                        )
                        : graph.Nodes[w];
                if (rings.Count == 0)
                {
                    strand.Centre.Add(crossing);
                    strand.Radius.Add(strand.Radius.Count > 0 ? strand.Radius[^1] : 0.01f);
                    mainParts.Add(new HashSet<int>());
                    continue;
                }
                var sections = rings
                    .GroupBy(r => band[r.Triangle])
                    .Select(x => new Section(x.Key, x.ToList()))
                    .ToList();
                var main = sections.MinBy(x =>
                    x.Rings.Min(r => r.Points.Min(q => (q - crossing).LengthSquared))
                );
                //Pieces of one section apart in the band, as where a level set folds on a tight
                //bend, lie within each other's reach; a real fork's tails do not.
                var parts = sections
                    .Where(x =>
                        x == main
                        || (x.Centroid - main.Centroid).Length < 0.9f * (x.Extent + main.Extent)
                    )
                    .ToList();
                float perimeter = parts.Sum(x => x.Perimeter);
                strand.Centre.Add(
                    parts.Aggregate(Vector3.Zero, (sum, x) => sum + x.Centroid * x.Perimeter)
                        / Math.Max(perimeter, 1e-9f)
                );
                bool open = parts.Count == 1 && main.Rings.Count == 1 && !main.Rings[0].Closed;
                strand.Radius.Add(open ? perimeter / 4 : perimeter / (2 * MathF.PI));
                mainParts.Add(parts.Select(x => x.Key).ToHashSet());
            }
            int centrePoints = strand.Centre.Count;
            FinishCentre(strand, graph.Nodes[tip], top, Level(0), h);

            //A node is on the main line when its piece of the band at its level is; the rest,
            //the tails of a fork, move with the point they leave the main line at.
            bool OnMain(int v)
            {
                if (float.IsInfinity(g[v]) || !nodeTriangle.TryGetValue(v, out int t))
                    return true;
                int k = Math.Min((int)(g[v] / h), levels - 1);
                if (g[v] < k * h || g[v] > (k + 1) * h)
                    return true;
                return mainParts[k].Count == 0 || mainParts[k].Contains(bands[k][t]);
            }
            var attach = new int[graph.Nodes.Count];
            var fromMain = graph.Distances(
                body.Where(OnMain).ToList(),
                inBody,
                joins,
                origin: attach
            );
            foreach (int v in body)
            {
                int from = attach[v] >= 0 ? attach[v] : v;
                set.NodeStrand[v] = strand.Id;
                set.NodeArc[v] = ArcAt(
                    strand,
                    centrePoints,
                    float.IsInfinity(g[from]) ? 0 : g[from],
                    Level(0),
                    h,
                    top
                );
                set.Geodesic[v] = g[v];
            }
            //Only a tail that leaves the head is worth a warning; part of a panel lying on the
            //head or the nape is not.
            float fork = body.Where(v =>
                    attach[v] >= 0 && attach[v] != v && fromMain[v] >= o.BranchReport
                )
                .Where(v => head == null || head.Distance(graph.Nodes[v]) > o.ForkOffHead)
                .Select(v => fromMain[v])
                .DefaultIfEmpty(0)
                .Max();
            if (fork >= o.BranchReport && fork >= o.BranchShare * top)
                notes.Add($"A {fork:0.00} branch rides along without bones of its own.");

            //Where the limb's cross sections lie on the head, as a side panel does from its parting,
            //its cloth holds them to the head rather than swinging them about the parting.
            if (hold)
                strand.Held = HeldArc(
                    set,
                    strand,
                    body.Where(v => !float.IsInfinity(g[v])).GroupBy(v => (int)(g[v] / h)),
                    k => k >= centrePoints || Level(k) > o.LieReach * top,
                    strand.Length,
                    o
                );

            //Pieces apart from the limb follow its nearest node.
            var strays = groups.Where(p => p != body).ToList();
            if (strays.Count > 0)
            {
                float farthest = 0;
                foreach (var piece in strays)
                {
                    float gap = float.MaxValue;
                    foreach (int v in piece)
                    {
                        int nearest = body.MinBy(b =>
                            (graph.Nodes[b] - graph.Nodes[v]).LengthSquared
                        );
                        gap = Math.Min(gap, (graph.Nodes[nearest] - graph.Nodes[v]).Length);
                        set.NodeStrand[v] = strand.Id;
                        set.NodeArc[v] = set.NodeArc[nearest];
                    }
                    farthest = Math.Max(farthest, gap);
                }
                //Pieces touching the limb, like a braid's tie, are expected and not reported.
                if (farthest > o.StrayReport)
                    notes.Insert(
                        0,
                        $"Painted in {groups.Count} pieces; the smaller ones follow the biggest."
                    );
            }
            problem = notes.Count > 0 ? string.Join(" ", notes) : null;
            set.Strands.Add(strand);
            return strand;
        }

        /// <summary>
        /// A limb the user aimed: a straight line from the aim's root, each node at its distance
        /// along it and the radius the nodes' mean distance off it. Paint behind the root stays
        /// with what the limb hangs from.
        /// </summary>
        static Strand Aimed(
            StrandSet set,
            List<int> body,
            List<List<int>> groups,
            LimbAim aim,
            bool hold,
            LimbOptions o,
            out string problem
        )
        {
            problem = null;
            var graph = set.Graph;
            var dir = aim.Direction.Normalized();
            float Along(int v) => Vector3.Dot(graph.Nodes[v] - aim.Root, dir);
            float top = body.Max(Along);
            if (top < o.MinLength)
            {
                problem = "Aimed away from its paint; too little lies ahead of the root.";
                return null;
            }
            int steps = Math.Max(1, (int)MathF.Round(top / o.RingStep));
            float step = top / steps;
            var sum = new float[steps + 1];
            var count = new int[steps + 1];
            foreach (int v in groups.SelectMany(p => p))
            {
                float a = Along(v);
                if (a < -0.5f * step)
                    continue;
                int k = (int)MathF.Round(Math.Min(Math.Max(a, 0), top) / step);
                sum[k] += (graph.Nodes[v] - aim.Root - dir * a).Length;
                count[k]++;
            }
            var strand = new Strand
            {
                Id = set.Strands.Count,
                Island = graph.Island[body.MinBy(v => (graph.Nodes[v] - aim.Root).LengthSquared)],
            };
            float last = -1;
            for (int k = 0; k <= steps; k++)
            {
                strand.Centre.Add(aim.Root + dir * (k * step));
                strand.Arc.Add(k * step);
                if (count[k] > 0)
                    last = sum[k] / count[k];
                strand.Radius.Add(last);
            }
            float first = strand.Radius.FirstOrDefault(r => r >= 0, 0.01f);
            for (int k = 0; k <= steps && strand.Radius[k] < 0; k++)
                strand.Radius[k] = first;

            foreach (int v in groups.SelectMany(p => p))
            {
                set.NodeStrand[v] = strand.Id;
                set.NodeArc[v] = Math.Min(Along(v), top);
            }

            if (hold)
                strand.Held = HeldArc(
                    set,
                    strand,
                    body.Where(v => Along(v) >= 0).GroupBy(v => (int)(Along(v) / step)),
                    k => k > steps || k * step > o.LieReach * top,
                    top,
                    o
                );
            set.Strands.Add(strand);
            return strand;
        }

        /// <summary>
        /// The arc of the centre line a limb holds on the head: that of the last band of nodes
        /// within reach lying on it, or 0 when the hold would be too short or leave too little.
        /// </summary>
        static float HeldArc(
            StrandSet set,
            Strand strand,
            IEnumerable<IGrouping<int, int>> bands,
            Func<int, bool> outOfReach,
            float length,
            LimbOptions o
        )
        {
            int lying = -1;
            foreach (var band in bands)
            {
                if (outOfReach(band.Key))
                    continue;
                if (Median(band.Select(v => set.HeadDistance[v])) < o.LieDistance)
                    lying = Math.Max(lying, band.Key);
            }
            return
                lying >= 0
                && strand.Arc[lying] >= o.MinHeld
                && length - strand.Arc[lying] >= o.MinLength
                ? strand.Arc[lying]
                : 0;
        }

        /// <summary>
        /// Where a limb grows from: where its paint borders the rest of the hair, or for a limb
        /// painted over a whole piece of surface, its part nearest the scalp. Of several border
        /// loops, the one the limb leads away from the head from; of a single loop that runs along
        /// the limb rather than round one end, the part at the end less far off the head. Either is
        /// then cut back to the part at the far end from the tip.
        /// </summary>
        static List<int> Root(
            MeshGraph graph,
            HashSet<int> body,
            List<int> nodes,
            Dictionary<int, List<(int Node, float Length)>> joins,
            Func<int, float> scalp,
            LimbOptions o
        )
        {
            int Far(float[] d) => nodes.MaxBy(v => float.IsInfinity(d[v]) ? -1 : d[v]);
            //How far off the head an end is. Hair hangs from the top, so of two ends about as far
            //off the scalp, the lower is the tip.
            float Off(int v) => scalp(v) - o.HeightWeight * graph.Nodes[v].Y;
            var border = nodes
                .Where(v => graph.Adjacent[v].Any(a => !body.Contains(a.Node)))
                .ToList();
            List<int> root;
            int tip;
            if (border.Count == 0)
            {
                float closest = nodes.Min(scalp);
                root = nodes.Where(v => scalp(v) <= closest + o.SourceBand).ToList();
                tip = Far(graph.Distances(root, body, joins));
            }
            else
            {
                var candidates = Loops(graph, border);
                if (candidates.Count > 1)
                {
                    //Seen from the root, the far end of the paint is farther off the head than the
                    //loop is; seen from the tip, or from where a curl comes back against the hair,
                    //the far end is the root, on the head.
                    (root, tip) = candidates
                        .Select(x =>
                        {
                            int far = Far(graph.Distances(x, body, joins));
                            float near = Median(x.Select(scalp));
                            return (
                                Loop: x,
                                Far: far,
                                Rise: Off(far) - Median(x.Select(Off)),
                                Near: near
                            );
                        })
                        .OrderByDescending(x => x.Rise)
                        .ThenBy(x => x.Near)
                        .Select(x => (x.Loop, x.Far))
                        .First();
                }
                else
                {
                    root = candidates[0];
                    tip = Far(graph.Distances(root, body, joins));
                }
                //A border well below the limb's top, as where a side panel meets the back hair
                //while its parting is a mesh edge, is not what it hangs from: its top is.
                float top = nodes.Min(Off);
                if (Median(root.Select(Off)) > top + o.TopReach)
                {
                    root = nodes.Where(v => Off(v) <= top + o.RootBand).ToList();
                    //Of that, the edge of the surface, as the parting of a panel, so the first
                    //cross sections run along it rather than round a patch of it.
                    var edge = root.Where(v =>
                            graph
                                .Adjacent[v]
                                .Any(a =>
                                    !body.Contains(a.Node)
                                    || graph
                                        .EdgeTriangles[(Math.Min(v, a.Node), Math.Max(v, a.Node))]
                                        .Count == 1
                                )
                        )
                        .ToList();
                    if (edge.Count >= 3)
                        root = edge;
                    tip = Far(graph.Distances(root, body, joins));
                    candidates = new List<List<int>>();
                }
                //A loop whose nodes lie at many distances from the far end runs along the limb
                //rather than round one end: a side panel's parting and back edge, a lock painted
                //out of a sheet, a lobe painted all round. Its top is where the limb hangs from.
                var fromTip = graph.Distances(new[] { tip }, body, joins);
                var along = root.Select(v => fromTip[v])
                    .Where(x => !float.IsInfinity(x))
                    .DefaultIfEmpty(0)
                    .ToList();
                if (candidates.Count > 0 && along.Max() - along.Min() > 0.5f * along.Max())
                {
                    if (candidates.Count == 1)
                    {
                        int end = Far(graph.Distances(new[] { nodes[0] }, body, joins));
                        int other = Far(graph.Distances(new[] { end }, body, joins));
                        tip = Off(end) > Off(other) ? end : other;
                    }
                    float highest = root.Min(Off);
                    root = root.Where(v => Off(v) <= highest + o.RootBand).ToList();
                }
            }
            var fromFar = graph.Distances(new[] { tip }, body, joins);
            float farthest = root.Max(v => float.IsInfinity(fromFar[v]) ? 0 : fromFar[v]);
            var kept = root.Where(v =>
                    !float.IsInfinity(fromFar[v]) && fromFar[v] >= farthest * (1 - o.RootTrim)
                )
                .ToList();
            return kept.Count > 0 ? kept : root;
        }

        /// <summary>Border nodes grouped into loops, those sharing a triangle together, leaving out loops much smaller than the biggest.</summary>
        static List<List<int>> Loops(MeshGraph graph, List<int> border)
        {
            var index = border.Select((v, i) => (v, i)).ToDictionary(x => x.v, x => x.i);
            var loops = new UnionFind(border.Count);
            foreach (var (a, b, c) in graph.Triangles)
            {
                int ia = index.GetValueOrDefault(a, -1),
                    ib = index.GetValueOrDefault(b, -1),
                    ic = index.GetValueOrDefault(c, -1);
                if (ia >= 0 && ib >= 0)
                    loops.Union(ia, ib);
                if (ib >= 0 && ic >= 0)
                    loops.Union(ib, ic);
                if (ia >= 0 && ic >= 0)
                    loops.Union(ia, ic);
            }
            var groups = border.GroupBy(v => loops.Find(index[v])).Select(x => x.ToList()).ToList();
            int biggest = groups.Max(x => x.Count);
            return groups
                .Where(x => x.Count >= Math.Min(biggest, Math.Max(3, biggest / 5)))
                .OrderByDescending(x => x.Count)
                .Take(6)
                .ToList();
        }

        static float Median(IEnumerable<float> values)
        {
            var sorted = values.OrderBy(x => x).ToList();
            return sorted[sorted.Count / 2];
        }

        /// <summary>The connected pieces of a limb's nodes, along edges between them.</summary>
        static List<List<int>> Pieces(MeshGraph graph, int[] owner, int limb, List<int> nodes)
        {
            var seen = new HashSet<int>();
            var pieces = new List<List<int>>();
            foreach (int start in nodes)
            {
                if (!seen.Add(start))
                    continue;
                var piece = new List<int> { start };
                for (int i = 0; i < piece.Count; i++)
                    foreach (var (next, _) in graph.Adjacent[piece[i]])
                        if (owner[next] == limb && seen.Add(next))
                            piece.Add(next);
                pieces.Add(piece);
            }
            return pieces;
        }

        /// <summary>
        /// Links between pieces of a limb closer than the gap, each node to its nearest node in the
        /// other piece, and the groups of pieces the links join.
        /// </summary>
        static Dictionary<int, List<(int Node, float Length)>> Joins(
            MeshGraph graph,
            List<List<int>> pieces,
            float gap,
            out List<List<int>> groups
        )
        {
            var joins = new Dictionary<int, List<(int Node, float Length)>>();
            var sets = new UnionFind(pieces.Count);
            for (int i = 0; i < pieces.Count; i++)
            for (int j = i + 1; j < pieces.Count; j++)
            {
                var (a, b) = (pieces[i], pieces[j]);
                foreach (var (from, to) in new[] { (a, b), (b, a) })
                foreach (int v in from)
                {
                    int nearest = -1;
                    float best = gap * gap;
                    foreach (int w in to)
                    {
                        float d = (graph.Nodes[v] - graph.Nodes[w]).LengthSquared;
                        if (d < best)
                        {
                            best = d;
                            nearest = w;
                        }
                    }
                    if (nearest < 0)
                        continue;
                    float length = MathF.Sqrt(best);
                    foreach (var (x, y) in new[] { (v, nearest), (nearest, v) })
                    {
                        if (!joins.TryGetValue(x, out var list))
                            joins[x] = list = new List<(int, float)>();
                        list.Add((y, length));
                    }
                    sets.Union(i, j);
                }
            }
            groups = Enumerable
                .Range(0, pieces.Count)
                .GroupBy(sets.Find)
                .Select(x => x.SelectMany(i => pieces[i]).ToList())
                .ToList();
            return joins;
        }

        static Dictionary<int, float> NodeAreas(MeshGraph graph, List<int> triangles)
        {
            var area = new Dictionary<int, float>();
            foreach (int t in triangles)
            {
                var (a, b, c) = graph.Triangles[t];
                float third =
                    Vector3
                        .Cross(graph.Nodes[b] - graph.Nodes[a], graph.Nodes[c] - graph.Nodes[a])
                        .Length / 6;
                area[a] = area.GetValueOrDefault(a) + third;
                area[b] = area.GetValueOrDefault(b) + third;
                area[c] = area.GetValueOrDefault(c) + third;
            }
            return area;
        }

        /// <summary>Smooths the centre line, adds the tip, and fills in the arc lengths.</summary>
        static void FinishCentre(Strand strand, Vector3 tip, float top, float firstLevel, float h)
        {
            var centre = strand.Centre;
            for (int pass = 0; pass < 3; pass++)
            {
                var copy = centre.ToList();
                for (int i = 1; i + 1 < centre.Count; i++)
                    centre[i] = copy[i - 1] * 0.25f + copy[i] * 0.5f + copy[i + 1] * 0.25f;
            }
            float lastLevel = firstLevel + (centre.Count - 1) * h;
            if (top - lastLevel > h * 0.25f || centre.Count == 1)
            {
                centre.Add(tip);
                strand.Radius.Add(strand.Radius[^1] * 0.5f);
            }
            strand.Arc.Clear();
            strand.Arc.AddRange(Polyline.Arcs(centre));
        }

        /// <summary>A node's arc along the strand from its level: between the cross sections around it, or toward the tip past the last.</summary>
        static float ArcAt(
            Strand strand,
            int sections,
            float level,
            float firstLevel,
            float h,
            float top
        )
        {
            float u = (level - firstLevel) / h;
            if (u <= 0)
                return u * h;
            if (u < sections - 1)
            {
                int i = (int)u;
                return strand.Arc[i] + (strand.Arc[i + 1] - strand.Arc[i]) * (u - i);
            }
            if (sections == strand.Centre.Count)
                return strand.Arc[^1];
            float lastLevel = firstLevel + (sections - 1) * h;
            float t = top > lastLevel ? (level - lastLevel) / (top - lastLevel) : 1;
            return strand.Arc[sections - 1]
                + (strand.Arc[^1] - strand.Arc[sections - 1]) * Math.Clamp(t, 0, 1);
        }

        /// <summary>The cross sections of the surface at one level, as rings of crossed edges.</summary>
        static List<Ring> Sections(MeshGraph graph, float[] g, List<int> triangles, float level)
        {
            var edgeIndex = new Dictionary<(int, int), int>();
            var edgeUse = new List<int>();
            var points = new List<Vector3>();
            var segments = new List<(int E0, int E1, int Triangle)>();
            int Edge(int x, int y)
            {
                var key = (Math.Min(x, y), Math.Max(x, y));
                if (!edgeIndex.TryGetValue(key, out int e))
                {
                    e = points.Count;
                    edgeIndex[key] = e;
                    float t = (level - g[x]) / (g[y] - g[x]);
                    points.Add(Vector3.Lerp(graph.Nodes[x], graph.Nodes[y], t));
                    edgeUse.Add(0);
                }
                edgeUse[e]++;
                return e;
            }
            foreach (int t in triangles)
            {
                var (a, b, c) = graph.Triangles[t];
                var crossed = new List<int>(2);
                foreach (var (x, y) in new[] { (a, b), (b, c), (c, a) })
                    if (
                        (g[x] < level) != (g[y] < level)
                        && !float.IsInfinity(g[x])
                        && !float.IsInfinity(g[y])
                    )
                        crossed.Add(Edge(x, y));
                if (crossed.Count == 2)
                    segments.Add((crossed[0], crossed[1], t));
            }
            var sets = new UnionFind(points.Count);
            foreach (var (e0, e1, _) in segments)
                sets.Union(e0, e1);
            var byRoot = new Dictionary<int, Ring>();
            foreach (var (e0, e1, t) in segments)
            {
                int root = sets.Find(e0);
                if (!byRoot.TryGetValue(root, out var ring))
                    byRoot[root] = ring = new Ring { Triangle = t, Closed = true };
                float length = (points[e0] - points[e1]).Length;
                ring.Points.Add(points[e0]);
                ring.Points.Add(points[e1]);
                ring.Centroid += (points[e0] + points[e1]) * 0.5f * length;
                ring.Perimeter += length;
                if (edgeUse[e0] < 2 || edgeUse[e1] < 2)
                    ring.Closed = false;
            }
            foreach (var (root, ring) in byRoot)
                ring.Centroid = ring.Perimeter > 0 ? ring.Centroid / ring.Perimeter : points[root];
            return byRoot.Values.ToList();
        }

        /// <summary>Connected pieces of the surface between two levels, per triangle.</summary>
        static Dictionary<int, int> Band(
            MeshGraph graph,
            float[] g,
            List<int> triangles,
            float low,
            float high,
            Dictionary<int, List<(int Node, float Length)>> joins
        )
        {
            var local = new Dictionary<int, int>();
            var nodeTriangle = new Dictionary<int, int>();
            foreach (int t in triangles)
            {
                local[t] = local.Count;
                var (a, b, c) = graph.Triangles[t];
                nodeTriangle[a] = nodeTriangle[b] = nodeTriangle[c] = local[t];
            }
            var sets = new UnionFind(triangles.Count);
            //A join between pieces links them as a shared edge would.
            foreach (var (x, list) in joins)
            foreach (var (y, _) in list)
                if (
                    Math.Max(g[x], g[y]) >= low
                    && Math.Min(g[x], g[y]) <= high
                    && nodeTriangle.TryGetValue(x, out int tx)
                    && nodeTriangle.TryGetValue(y, out int ty)
                )
                    sets.Union(tx, ty);
            foreach (int t in triangles)
            {
                var (a, b, c) = graph.Triangles[t];
                foreach (var (x, y) in new[] { (a, b), (b, c), (c, a) })
                {
                    float lo = Math.Min(g[x], g[y]),
                        hi = Math.Max(g[x], g[y]);
                    if (hi < low || lo > high)
                        continue;
                    foreach (int other in graph.EdgeTriangles[(Math.Min(x, y), Math.Max(x, y))])
                        if (local.TryGetValue(other, out int o))
                            sets.Union(local[t], o);
                }
            }
            return triangles.ToDictionary(t => t, t => sets.Find(local[t]));
        }
    }
}
