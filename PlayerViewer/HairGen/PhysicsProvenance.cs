using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenTK;
using PlayerViewer.Phive;
using PlayerViewer.Rigging;

namespace PlayerViewer.HairGen
{
    /// <summary>
    /// What a physics authoring session made of a model, embedded in the saved bfres as an
    /// external file so a later session can pick it up: the limbs, the collidables, the options,
    /// the bones the rig added and the hand edits of the generated cloth. Painted limbs are kept
    /// as vertex indices of the saved model's shapes, which the rig write leaves in place.
    /// </summary>
    public sealed class PhysicsProvenance
    {
        public const string FileName = "HoianViewer.physics.json";
        public const string FormatName = "HoianViewer.physics";
        public const int CurrentVersion = 1;

        public string Format { get; set; } = FormatName;
        public int Version { get; set; } = CurrentVersion;
        public string Viewer { get; set; }
        public string Saved { get; set; }
        public string Model { get; set; }

        /// <summary>Always "saved": the saved model, skeleton edits and rig included, is what a load builds on.</summary>
        public string Base { get; set; } = "saved";
        public bool Hair { get; set; }
        public bool Merge { get; set; }
        public float Spacing { get; set; }

        /// <summary>Every bone the rig added to the model, chains and placeholders, in skeleton order.</summary>
        public List<string> GeneratedBones { get; set; } = new();
        public List<ShapeRecord> Shapes { get; set; } = new();

        /// <summary>
        /// The rest positions the session's generator had where the model a load builds on reads
        /// others: a rest position is weight averaged, so the rig's new weights move it by float
        /// noise, and the strands with it.
        /// </summary>
        public List<RestRecord> Rest { get; set; } = new();
        public List<LimbRecord> Limbs { get; set; } = new();
        public List<ColliderRecord> Colliders { get; set; } = new();
        public List<ColliderRecord> ColliderEdits { get; set; } = new();
        public ClothRecord Cloth { get; set; }

        public sealed class ShapeRecord
        {
            public string Name { get; set; }
            public int Vertices { get; set; }
        }

        public sealed class LimbRecord
        {
            public string Name { get; set; }

            /// <summary>"painted" or "bones".</summary>
            public string Kind { get; set; }
            public string Colour { get; set; }
            public StyleRecord Style { get; set; }
            public bool Hold { get; set; }
            public bool OwnPiece { get; set; }

            /// <summary>A painted limb's vertices per shape, as index ranges ("0-15,20").</summary>
            public List<PaintRecord> Paint { get; set; }
            public List<string[]> Chains { get; set; }

            /// <summary>The bone the limb hangs from, which a load keeps on a model that is not hair.</summary>
            public string Hangs { get; set; }

            /// <summary>The bones the rig made for the limb.</summary>
            public List<string> Bones { get; set; }

            /// <summary>The model's own bones the limb replaced, in this session or an earlier one.</summary>
            public List<string> Replaced { get; set; }

            /// <summary>A painted limb's straight chain as the user aimed it: its root and unit direction in rest model space.</summary>
            public float[] AimRoot { get; set; }
            public float[] AimDirection { get; set; }
            public float[] AimSide { get; set; }

            /// <summary>Absent loads as straight.</summary>
            public bool? AimStraight { get; set; }
        }

        public sealed class RestRecord
        {
            public string Shape { get; set; }
            public int Index { get; set; }
            public string Vertices { get; set; }

            /// <summary>x, y, z per vertex as little endian floats, base64.</summary>
            public string Positions { get; set; }
        }

        public sealed class PaintRecord
        {
            public string Shape { get; set; }
            public int Index { get; set; }
            public string Vertices { get; set; }
        }

        public sealed class StyleRecord
        {
            public string Preset { get; set; }
            public string Motion { get; set; }
            public float Stiffness { get; set; }
            public float Bounce { get; set; }
            public float Reach { get; set; }
            public float Width { get; set; }

            /// <summary>True for a floaty strand; absent otherwise.</summary>
            public bool? Floaty { get; set; }
        }

        public sealed class ColliderRecord
        {
            public string Name { get; set; }
            public string Kind { get; set; }
            public string Bone { get; set; }
            public float[] Start { get; set; }
            public float[] End { get; set; }
            public float Radius { get; set; }
            public bool Enabled { get; set; }

            /// <summary>Limb indices that collide with it; null for every limb, or for a default one its height rule.</summary>
            public List<int> Limbs { get; set; }
        }

        public sealed class ClothRecord
        {
            public string Entry { get; set; }
            public string Packs { get; set; }

            /// <summary>Whether the open cloth was the generated one; the edits apply only then.</summary>
            public bool Generated { get; set; }

            /// <summary>SHA-256 of the cloth as generated and of the cloth as saved, hex.</summary>
            public string GeneratedHash { get; set; }
            public string Hash { get; set; }
            public List<ClothValueEdit> Edits { get; set; } = new();
            public List<string> Structural { get; set; } = new();

            /// <summary>
            /// The cloth as saved, zstd compressed and base64: what a load restores when the cloth
            /// built again differs from it, as it would once the generation itself changes.
            /// </summary>
            public string Saved { get; set; }

            public void KeepSaved(byte[] cloth)
            {
                using var zstd = new ZstdSharp.Compressor(19);
                Saved = Convert.ToBase64String(zstd.Wrap(cloth).ToArray());
            }

            public byte[] SavedBytes()
            {
                if (Saved == null)
                    return null;
                using var zstd = new ZstdSharp.Decompressor();
                return zstd.Unwrap(Convert.FromBase64String(Saved)).ToArray();
            }
        }

        static readonly JsonSerializerOptions Json = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        };

        public byte[] ToBytes() => JsonSerializer.SerializeToUtf8Bytes(this, Json);

        /// <summary>A deep copy, through the file form.</summary>
        public PhysicsProvenance Clone() => Parse(ToBytes());

        /// <summary>The metadata in a file's bytes; throws on another format or a newer version.</summary>
        public static PhysicsProvenance Parse(byte[] data)
        {
            var meta =
                JsonSerializer.Deserialize<PhysicsProvenance>(data, Json)
                ?? throw new FormatException("empty");
            if (meta.Format != FormatName)
                throw new FormatException($"not {FormatName}");
            if (meta.Version > CurrentVersion)
                throw new FormatException(
                    $"version {meta.Version}, written by a newer HoianViewer (this one reads {CurrentVersion})"
                );
            return meta;
        }

        [JsonIgnore]
        public int PaintedCount => Limbs.Count(l => l.Kind == "painted");

        /// <summary>"3 limbs (2 painted, 1 from bones), 2 collidables, 2 hand edits, saved ..."</summary>
        public string Summary()
        {
            int painted = PaintedCount;
            int bones = Limbs.Count - painted;
            int edits = (Cloth?.Edits.Count ?? 0) + (Cloth?.Structural.Count ?? 0);
            string when = DateTime.TryParse(
                Saved,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                out var t
            )
                ? t.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
                : "at an unknown time";
            return $"{Count(Limbs.Count, "limb")} ({painted} painted, {bones} from bones), "
                + $"{Count(Colliders.Count, "collidable")} added and {ColliderEdits.Count} edited, "
                + $"{Count(edits, "hand edit")} of the cloth data, saved {when}";
        }

        static string Count(int n, string what) => $"{n} {what}{(n == 1 ? "" : "s")}";

        /// <summary>
        /// The generator's authoring state: settings, limbs, collidables and the bones its rig
        /// added. The cloth record is the caller's.
        /// </summary>
        public static PhysicsProvenance Capture(HairGenerator gen, string model)
        {
            var meta = new PhysicsProvenance
            {
                Viewer = ViewerVersion(),
                Saved = DateTime.UtcNow.ToString(
                    "yyyy-MM-ddTHH:mm:ssZ",
                    CultureInfo.InvariantCulture
                ),
                Model = model,
                Hair = gen.Hair,
                Merge = gen.MergeCloseLimbs,
                Spacing = gen.RigOptions.Spacing,
            };
            if (gen.Rig != null)
                meta.GeneratedBones = gen
                    .Rig.Bones.Skip(gen.Rig.OriginalBoneCount)
                    .Select(b => b.Name)
                    .ToList();
            foreach (var part in gen.Mesh.Parts)
                meta.Shapes.Add(new ShapeRecord { Name = part.Name, Vertices = part.Rest.Length });
            foreach (var limb in gen.Limbs)
            {
                var record = new LimbRecord
                {
                    Name = limb.Name,
                    Kind = limb.IsBoneLimb ? "bones" : "painted",
                    Style = limb.Style == null ? null : StyleOf(limb.Style),
                    Hold = limb.HoldOnHead,
                    OwnPiece = limb.OwnPiece,
                };
                int chain = gen.ChainOf(limb);
                if (limb.IsBoneLimb)
                    record.Chains = limb.BoneChains.Select(c => (string[])c.Clone()).ToList();
                else
                {
                    record.Paint = PaintOf(gen.Mesh, gen.Graph, limb.Nodes);
                    record.Bones = gen.ChainsOf(limb)
                        .SelectMany(c => gen.Rig.Chains[c].Bones)
                        .Where(b => b >= gen.Rig.OriginalBoneCount)
                        .Select(b => gen.Rig.Bones[b].Name)
                        .ToList();
                    record.Replaced = limb
                        .Replaced.Concat(limb.ReplacedEarlier)
                        .Distinct()
                        .ToList();
                    if (!gen.Hair && chain >= 0)
                        record.Hangs = gen.Rig.Bones[gen.Rig.Chains[chain].Anchor].Name;
                    if (limb.Aim is LimbAim aim)
                    {
                        record.AimRoot = new[] { aim.Root.X, aim.Root.Y, aim.Root.Z };
                        record.AimDirection = new[]
                        {
                            aim.Direction.X,
                            aim.Direction.Y,
                            aim.Direction.Z,
                        };
                        if (aim.Side is Vector3 side)
                            record.AimSide = new[] { side.X, side.Y, side.Z };
                        record.AimStraight = aim.Straight;
                    }
                }
                meta.Limbs.Add(record);
            }
            foreach (var collider in gen.UserColliders)
                meta.Colliders.Add(ColliderOf(collider, gen.Limbs));
            foreach (var (_, edit) in gen.ColliderEdits.OrderBy(e => e.Key, StringComparer.Ordinal))
                meta.ColliderEdits.Add(ColliderOf(edit, gen.Limbs));
            return meta;
        }

        static string ViewerVersion()
        {
            var assembly = typeof(PhysicsProvenance).Assembly;
            var info = assembly
                .GetCustomAttributes(
                    typeof(System.Reflection.AssemblyInformationalVersionAttribute),
                    false
                )
                .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
                .FirstOrDefault()
                ?.InformationalVersion;
            return "HoianViewer " + (info ?? assembly.GetName().Version?.ToString() ?? "unknown");
        }

        static StyleRecord StyleOf(StrandStyle s) =>
            new()
            {
                Preset = s.Preset,
                Motion = s.Motion.ToString(),
                Stiffness = s.Stiffness,
                Bounce = s.Bounce,
                Reach = s.Reach,
                Width = s.Width,
                Floaty = s.Floaty ? true : null,
            };

        /// <summary>Graph nodes as the vertex ranges of each shape they cover.</summary>
        public static List<PaintRecord> PaintOf(
            SkinnedMesh mesh,
            MeshGraph graph,
            IReadOnlySet<int> nodes
        )
        {
            var list = new List<PaintRecord>();
            for (int p = 0; p < mesh.Parts.Count; p++)
            {
                var of = graph.NodeOf[p];
                var vertices = new List<int>();
                for (int v = 0; v < of.Length; v++)
                    if (of[v] >= 0 && nodes.Contains(of[v]))
                        vertices.Add(v);
                if (vertices.Count > 0)
                    list.Add(
                        new PaintRecord
                        {
                            Shape = mesh.Parts[p].Name,
                            Index = p,
                            Vertices = Ranges(vertices),
                        }
                    );
            }
            return list;
        }

        static ColliderRecord ColliderOf(LimbCollider c, List<PaintedLimb> limbs) =>
            new()
            {
                Name = c.Name,
                Kind = c.Kind.ToString(),
                Bone = c.Bone,
                Start = new[] { c.Start.X, c.Start.Y, c.Start.Z },
                End = new[] { c.End.X, c.End.Y, c.End.Z },
                Radius = c.Radius,
                Enabled = c.Enabled,
                //A limb deleted since stays in the list unseen; it has no index to keep.
                Limbs = c
                    .Limbs?.Select(l => limbs.IndexOf(l))
                    .Where(i => i >= 0)
                    .OrderBy(i => i)
                    .ToList(),
            };

        static string Ranges(List<int> sorted)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < sorted.Count; )
            {
                int j = i;
                while (j + 1 < sorted.Count && sorted[j + 1] == sorted[j] + 1)
                    j++;
                if (sb.Length > 0)
                    sb.Append(',');
                sb.Append(sorted[i].ToString(CultureInfo.InvariantCulture));
                if (j > i)
                    sb.Append('-').Append(sorted[j].ToString(CultureInfo.InvariantCulture));
                i = j + 1;
            }
            return sb.ToString();
        }

        static IEnumerable<int> ParseRanges(string text)
        {
            foreach (var part in (text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                int dash = part.IndexOf('-');
                int a = int.Parse(dash < 0 ? part : part[..dash], CultureInfo.InvariantCulture);
                int b = dash < 0 ? a : int.Parse(part[(dash + 1)..], CultureInfo.InvariantCulture);
                for (int v = a; v <= b; v++)
                    yield return v;
            }
        }

        /// <summary>
        /// The model a load builds on: the saved one without the metadata file and without the
        /// bones its rig added, their weights moved to the bones they hang from. Bones that cannot
        /// go are named in <paramref name="report"/>.
        /// </summary>
        public static byte[] BaseModel(
            byte[] saved,
            IEnumerable<string> generated,
            List<string> report
        )
        {
            var res = new BfresLibrary.ResFile(new System.IO.MemoryStream(saved));
            if (res.ExternalFiles.ContainsKey(FileName))
                res.ExternalFiles.RemoveKey(FileName);
            var model = res.Models.Values.First();
            var names = generated.ToHashSet();
            var present = names.Where(model.Skeleton.Bones.ContainsKey).ToHashSet();
            if (present.Count < names.Count)
                report?.Add(
                    $"{names.Count - present.Count} generated bone(s) are not in the model"
                );
            if (
                present.Count > 0
                && RigWriter.RemoveWithWeights(model, present) is { Count: > 0 } left
            )
                report?.Add($"still in use, kept: {string.Join(", ", left)}");
            var ms = new System.IO.MemoryStream();
            res.Save(ms);
            return ms.ToArray();
        }

        /// <summary>Keeps every rest position of the generator's mesh that the base model's mesh reads differently.</summary>
        public void CaptureRest(SkinnedMesh mesh, SkinnedMesh basis)
        {
            Rest.Clear();
            for (int p = 0; p < Math.Min(mesh.Parts.Count, basis.Parts.Count); p++)
            {
                var have = mesh.Parts[p].Rest;
                var other = basis.Parts[p].Rest;
                if (have.Length != other.Length)
                    continue;
                var vertices = new List<int>();
                var bytes = new List<byte>();
                for (int v = 0; v < have.Length; v++)
                {
                    if (have[v] == other[v])
                        continue;
                    vertices.Add(v);
                    bytes.AddRange(BitConverter.GetBytes(have[v].X));
                    bytes.AddRange(BitConverter.GetBytes(have[v].Y));
                    bytes.AddRange(BitConverter.GetBytes(have[v].Z));
                }
                if (vertices.Count > 0)
                    Rest.Add(
                        new RestRecord
                        {
                            Shape = mesh.Parts[p].Name,
                            Index = p,
                            Vertices = Ranges(vertices),
                            Positions = Convert.ToBase64String(bytes.ToArray()),
                        }
                    );
            }
        }

        /// <summary>Puts the kept rest positions into a mesh read from the base model, before a generator is made on it.</summary>
        public int ApplyRest(SkinnedMesh mesh)
        {
            int count = 0;
            foreach (var record in Rest)
            {
                if (
                    record.Index < 0
                    || record.Index >= mesh.Parts.Count
                    || mesh.Parts[record.Index].Name != record.Shape
                )
                    continue;
                var rest = mesh.Parts[record.Index].Rest;
                var data = Convert.FromBase64String(record.Positions ?? "");
                int k = 0;
                foreach (int v in ParseRanges(record.Vertices))
                {
                    if (v < rest.Length && k + 12 <= data.Length)
                    {
                        rest[v] = new Vector3(
                            BitConverter.ToSingle(data, k),
                            BitConverter.ToSingle(data, k + 4),
                            BitConverter.ToSingle(data, k + 8)
                        );
                        count++;
                    }
                    k += 12;
                }
            }
            return count;
        }

        /// <summary>
        /// Puts the settings, limbs and collidables into a generator made on the saved model with
        /// its generated bones taken out. Nothing is built. Returns what could not be restored.
        /// </summary>
        public List<string> Restore(HairGenerator gen) => Restore(gen, out _);

        /// <summary>As <see cref="Restore(HairGenerator)"/>; <paramref name="rejoined"/> when a bone limb's chains were saved split and are joined now.</summary>
        public List<string> Restore(HairGenerator gen, out bool rejoined)
        {
            rejoined = false;
            var problems = new List<string>();
            gen.RigOptions.Spacing = Spacing > 0 ? Spacing : gen.RigOptions.Spacing;
            gen.MergeCloseLimbs = Merge;
            gen.Limbs.Clear();
            foreach (var record in Limbs)
            {
                var limb = new PaintedLimb
                {
                    Name = record.Name,
                    HoldOnHead = record.Hold,
                    OwnPiece = record.OwnPiece,
                    Style = record.Style == null ? null : StyleFrom(record.Style),
                };
                if (record.Kind == "bones")
                {
                    limb.BoneChains = BoneTree.JoinChains(
                        gen.Mesh.Bones,
                        record.Chains?.Select(c => (string[])c.Clone()) ?? [],
                        out bool joined
                    );
                    if (joined)
                    {
                        rejoined = true;
                        problems.Add(
                            $"{record.Name}'s chains continue one another and are joined into one"
                        );
                    }
                }
                else
                {
                    limb.Nodes = PaintedNodes(gen.Mesh, gen.Graph, record, problems);
                    limb.Hang = gen.Hair ? null : record.Hangs;
                    limb.ReplacedEarlier = record.Replaced?.ToList() ?? new();
                    if (record.AimRoot is { Length: 3 } r && record.AimDirection is { Length: 3 } d)
                        limb.Aim = new LimbAim(
                            new Vector3(r[0], r[1], r[2]),
                            new Vector3(d[0], d[1], d[2]),
                            record.AimSide is { Length: 3 } s
                                ? new Vector3(s[0], s[1], s[2])
                                : null,
                            record.AimStraight ?? true
                        );
                }
                gen.Limbs.Add(limb);
            }
            gen.UserColliders.Clear();
            foreach (var record in Colliders)
                gen.UserColliders.Add(ColliderFrom(record, gen.Limbs, false));
            gen.ColliderEdits.Clear();
            foreach (var record in ColliderEdits)
                gen.ColliderEdits[record.Name] = ColliderFrom(record, gen.Limbs, true);
            return problems;
        }

        static StrandStyle StyleFrom(StyleRecord s) =>
            new()
            {
                Preset = s.Preset,
                Motion = Enum.TryParse(s.Motion, out StrandMotion m) ? m : StrandMotion.Hanging,
                Stiffness = s.Stiffness,
                Bounce = s.Bounce,
                Reach = s.Reach,
                Width = s.Width,
                Floaty = s.Floaty == true,
            };

        /// <summary>A painted limb's paint as nodes of the graph of the mesh it was painted on.</summary>
        public HashSet<int> PaintedNodes(
            SkinnedMesh mesh,
            MeshGraph graph,
            LimbRecord record,
            List<string> problems
        )
        {
            var nodes = new HashSet<int>();
            foreach (var paint in record.Paint ?? new())
            {
                //By index when the shape there has the name and size it had, else by name.
                var parts = mesh.Parts;
                int p =
                    paint.Index >= 0
                    && paint.Index < parts.Count
                    && parts[paint.Index].Name == paint.Shape
                        ? paint.Index
                        : parts.FindIndex(x => x.Name == paint.Shape);
                var shape = Shapes.FirstOrDefault(s => s.Name == paint.Shape);
                if (p < 0 || (shape != null && parts[p].Rest.Length != shape.Vertices))
                {
                    problems.Add(
                        $"{record.Name}: the shape {paint.Shape} is not the one painted, its paint there is lost"
                    );
                    continue;
                }
                var of = graph.NodeOf[p];
                foreach (int v in ParseRanges(paint.Vertices))
                    if (v >= 0 && v < of.Length && of[v] >= 0)
                        nodes.Add(of[v]);
            }
            return nodes;
        }

        static LimbCollider ColliderFrom(ColliderRecord r, List<PaintedLimb> limbs, bool edit) =>
            new()
            {
                Name = r.Name,
                Kind = Enum.TryParse(r.Kind, out CollidableShapeKind k)
                    ? k
                    : CollidableShapeKind.Capsule,
                Bone = r.Bone,
                Start = Vector(r.Start),
                End = Vector(r.End),
                Radius = r.Radius,
                Enabled = r.Enabled,
                Limbs = r
                    .Limbs?.Where(i => i >= 0 && i < limbs.Count)
                    .Select(i => limbs[i])
                    .ToHashSet(),
                Default = edit,
                Edited = edit,
            };

        static Vector3 Vector(float[] v) =>
            v != null && v.Length >= 3 ? new Vector3(v[0], v[1], v[2]) : Vector3.Zero;
    }
}
