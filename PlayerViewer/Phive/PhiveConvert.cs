using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using OpenTK;

namespace PlayerViewer.Phive
{
    /// <summary>
    /// Converts Phive cloth (.bphcl) and shape (.bphsh) files between Splatoon 3 (Havok 2021.1)
    /// and Raiders (Havok 2025.1).
    /// </summary>
    public static partial class PhiveConvert
    {
        public const string Havok2021 = "20210100",
            Havok2025 = "20250100";

        public static byte[] ClothTo2025(byte[] s3Bphcl, out string error) =>
            Run(() => Cloth(s3Bphcl, true), out error);

        public static byte[] ClothToSplatoon3(byte[] raidersBphcl, out string error) =>
            Run(() => Cloth(raidersBphcl, false), out error);

        public static byte[] ShapeTo2025(byte[] s3Bphsh, out string error) =>
            Run(() => Shape(s3Bphsh, true), out error);

        public static byte[] ShapeToSplatoon3(byte[] raidersBphsh, out string error) =>
            Run(() => Shape(raidersBphsh, false), out error);

        /// <summary>The file name with its platform part swapped for the other game's.</summary>
        public static string SuggestName(string fileName, bool to2025) =>
            to2025
                ? fileName.Replace(".Nin_NX_NVN.", ".Nin_Ounce_NVN2.")
                : fileName.Replace(".Nin_Ounce_NVN2.", ".Nin_NX_NVN.");

        sealed class ConvertException : Exception
        {
            public ConvertException(string message)
                : base(message) { }
        }

        static byte[] Run(Func<byte[]> convert, out string error)
        {
            error = null;
            try
            {
                return convert();
            }
            catch (ConvertException e)
            {
                error = e.Message;
            }
            catch (Exception e)
            {
                error = "Conversion failed: " + e.Message;
            }
            return null;
        }

        static string Game(bool is2025) => is2025 ? "Raiders" : "Splatoon 3";

        static HkTagfile ReadSource(byte[] tagfile, bool to2025)
        {
            var tag = HkTagfile.Read(tagfile);
            string from = to2025 ? Havok2021 : Havok2025;
            if (tag.SdkVersion == from)
                return tag;
            if (tag.SdkVersion == (to2025 ? Havok2025 : Havok2021))
                throw new ConvertException($"This is already a {Game(to2025)} file.");
            throw new ConvertException(
                $"Not a {Game(!to2025)} file (Havok {tag.SdkVersion})."
            );
        }

        #region cloth

        static byte[] Cloth(byte[] data, bool to2025)
        {
            if (!PhiveFile.IsPhive(data))
                throw new ConvertException("Not a Phive file.");
            var file = PhiveFile.Read(data);
            var tag = ReadSource(file.Tagfile, to2025);
            if (tag.Root?.TypeName != "hkRootLevelContainer")
                throw new ConvertException("Not a cloth file.");
            if (to2025)
                DropResourceData(tag.Root);
            var target = Target.For(to2025);
            var graph = new Graph(target, to2025);
            var root = (HkObject)graph.Value(tag.Root, null, null);
            ClothRules(graph, root, to2025);
            graph.CheckFields(ClothFields);
            file.Tagfile = HkTagfileWriter.Write(target.Template, root, target.Section);
            return file.Write();
        }

        //Fields one game has and the other lacks, each settled by ClothRules.
        static readonly HashSet<string> ClothFields = new(StringComparer.Ordinal)
        {
            "hclSimClothData.aerodynamicsData",
            "hclSimClothData.skinData",
            "hclSimClothData.setupCollidableSidedness",
            "hclSimClothData.virtualCollisionPointsRadiusScale",
            "hclClothData.customDeformHandlers",
            "hclClothData.customDeformHandlerTable",
            "hclBoneSpaceDeformer.partialWrite",
            "hclBoneSpaceDeformer.batchOffset",
            "hclObjectSpaceDeformer.partialWrite",
            "hclObjectSpaceDeformer.batchOffset",
        };

        static void ClothRules(Graph graph, HkObject root, bool to2025)
        {
            foreach (var (source, made) in graph.Objects)
            {
                switch (made.Type.Name)
                {
                    case "hclSimClothData":
                        if (to2025)
                            To2025(made);
                        else
                            Check2025Only(source);
                        break;
                    case "hclClothData":
                        if (!to2025 && source.Array("customDeformHandlers").Count > 0)
                            throw new ConvertException(
                                "The cloth uses custom deform handlers, which Splatoon 3 lacks."
                            );
                        break;
                    case "hclBoneSpaceDeformer":
                    case "hclObjectSpaceDeformer":
                        if (to2025 && source.Bool("partialWrite"))
                            throw new ConvertException("A deformer writes partially, which Raiders lacks.");
                        if (!to2025 && source.Int("batchOffset") != 0)
                            throw new ConvertException(
                                "A deformer has a batch offset, which Splatoon 3 lacks."
                            );
                        break;
                }
            }

            var variants = root.Array("namedVariants");
            if (to2025)
                return;
            if (!variants.Objects.Any(v => v.String("className") == "hkMemoryResourceContainer"))
            {
                var t = graph.Target;
                var container = HkObject.Create(t.Type("hkMemoryResourceContainer"));
                container["name"] = "";
                var variant = HkObject.Create(variants.ElementType);
                variant["name"] = "Resource Data";
                variant["className"] = "hkMemoryResourceContainer";
                variant["variant"] = container;
                variants.Insert(Math.Min(1, variants.Count), variant);
            }
        }

        //Raiders saves no resource container; Splatoon 3's is always empty.
        static void DropResourceData(HkObject root)
        {
            var variants = root.Array("namedVariants");
            for (int i = variants.Count - 1; i >= 0; i--)
                if (variants[i] is HkObject v && v.String("className") == "hkMemoryResourceContainer")
                {
                    var c = v.Object("variant");
                    if (c != null && (c.Array("resourceHandles").Count > 0 || c.Array("children").Count > 0))
                        throw new ConvertException("The file carries resource data Raiders cannot hold.");
                    variants.RemoveAt(i);
                }
        }

        //The values every Raiders cloth carries; the aerodynamic density is the mass over the rest area.
        static void To2025(HkObject sim)
        {
            var aero = sim.Object("aerodynamicsData");
            aero.Set("liftCoefficient", 0.2f);
            aero.Set("dragCoefficient", 1f);
            aero.Set("surfaceDensity", SurfaceDensity(sim));
            aero.Set("useSurfaceDensityWithSimpleWind", true);
            aero.Set("numParticles", sim.Array("particleDatas").Count);
            aero.Set("constantParticleLiftFactor", 1f);
            aero.Set("constantParticleDragFactor", 1f);
            aero.Set("useConstantLiftFactor", true);
            aero.Set("useConstantDragFactor", true);
            var sided = sim.Array("setupCollidableSidedness");
            for (int i = 0; i < sim.Array("perInstanceCollidables").Count; i++)
                sided.AddValue(false);
            sim.Set("virtualCollisionPointsRadiusScale", 1f);
        }

        static float SurfaceDensity(HkObject sim)
        {
            var pose = sim.Array("simClothPoses").ObjectAt(0)?.Array("positions");
            var tri = sim.Array("triangleIndices").Ints();
            if (pose == null)
                return 0;
            //In single precision, half of each cross product summed: what the stock files hold.
            float area = 0;
            for (int t = 0; t + 2 < tri.Length; t += 3)
            {
                if (tri[t + 2] >= pose.Count)
                    continue;
                var a = HkValue.ToVector4(pose[tri[t]]).Xyz;
                var b = HkValue.ToVector4(pose[tri[t + 1]]).Xyz;
                var c = HkValue.ToVector4(pose[tri[t + 2]]).Xyz;
                area += 0.5f * Vector3.Cross(b - a, c - a).Length;
            }
            return area > 0 ? sim.Float("totalMass") / area : 0;
        }

        static void Check2025Only(HkObject sim)
        {
            if (sim.Object("skinData") != null)
                throw new ConvertException("The cloth uses skin data, which Splatoon 3 lacks.");
            if (sim.Array("setupCollidableSidedness").Any(v => v is bool b && b))
                throw new ConvertException("The cloth uses collidable sidedness, which Splatoon 3 lacks.");
            if (Math.Abs(sim.Float("virtualCollisionPointsRadiusScale", 1) - 1) > 1e-6f)
                throw new ConvertException(
                    "The cloth scales its virtual collision points, which Splatoon 3 cannot."
                );
        }

        #endregion

        #region graph

        /// <summary>
        /// The other game's types
        /// </summary>
        sealed class Target
        {
            public HkTypeCatalog Catalog;
            public HkTagfile Template;
            public byte[] Section;
            readonly Dictionary<string, HkType> _bySig = new(StringComparer.Ordinal);

            static readonly Target[] _cache = new Target[2];

            public static Target For(bool to2025)
            {
                lock (_cache)
                    return _cache[to2025 ? 1 : 0] ??= Build(to2025);
            }

            public HkType Find(string sig) => _bySig.GetValueOrDefault(sig);

            public HkType Type(string sig) =>
                Find(sig)
                ?? throw new ConvertException($"{sig} has no {Game(_is2025)} counterpart.");

            bool _is2025;

            static Target Build(bool to2025)
            {
                var own = HkTypeCatalog.Stock(to2025 ? Havok2025 : Havok2021);
                var other = HkTypeCatalog.Stock(to2025 ? Havok2021 : Havok2025);
                var catalog = new HkTypeCatalog { Types = new(own.Types, StringComparer.Ordinal) };
                foreach (var (sig, def) in other.Types)
                {
                    if (def.Body == null || !Borrowable(def.Name))
                        continue;
                    string mine = Rename(sig, to2025);
                    if (catalog.Types.TryGetValue(mine, out var have) && have.Body != null)
                        continue;
                    catalog.Types[mine] = RenameDef(def, to2025, have);
                }

                var builder = new HkTypeBuilder(catalog);
                foreach (var (sig, def) in catalog.Types)
                    if (def.Body != null && Complete(catalog, sig, new HashSet<string>()))
                        builder.Write(sig);
                var section = builder.Section.Write();

                string sdk = to2025 ? Havok2025 : Havok2021;
                var target = new Target
                {
                    Catalog = catalog,
                    Section = section,
                    Template = HkTagfile.Read(EmptyTagfile(sdk, section)),
                    _is2025 = to2025,
                };
                foreach (var t in target.Template.Types)
                    if (t != null && t.Signature.Length > 0)
                        target._bySig.TryAdd(t.Signature, t);
                return target;
            }

            //Cloth classes whose layout neither game's files contradict; container templates follow their argument.
            static bool Borrowable(string name) => name.StartsWith("hcl", StringComparison.Ordinal)
                || name is "hkArray" or "T*" or "hkRefPtr" or "T[N]";

            static bool Complete(HkTypeCatalog c, string sig, HashSet<string> seen)
            {
                if (sig == null || !seen.Add(sig))
                    return true;
                if (!c.Types.TryGetValue(sig, out var def))
                    return false;
                foreach (var t in def.Templates)
                    if (HkTypeCatalog.IsTypeParam(t.Param) && !Complete(c, t.Type, seen))
                        return false;
                var b = def.Body;
                if (b == null)
                    return true;
                return Complete(c, b.Parent, seen)
                    && Complete(c, b.Subtype, seen)
                    && b.Fields.All(f => Complete(c, f.Type, seen))
                    && b.Interfaces.All(i => Complete(c, i.Type, seen));
            }

            static HkTypeCatalog.TypeDef RenameDef(HkTypeCatalog.TypeDef def, bool to2025, HkTypeCatalog.TypeDef have)
            {
                string R(string s) => s == null ? null : Rename(s, to2025);
                var b = def.Body;
                return new HkTypeCatalog.TypeDef
                {
                    Name = R(def.Name),
                    Templates = def.Templates
                        .Select(t => new HkTypeCatalog.Template { Param = t.Param, Type = R(t.Type), Value = t.Value })
                        .ToList(),
                    Hash = have?.Hash,
                    Body = new HkTypeCatalog.Body
                    {
                        Parent = R(b.Parent),
                        Subtype = R(b.Subtype),
                        OptBits = b.OptBits,
                        Format = b.Format,
                        Version = b.Version,
                        Size = b.Size,
                        Align = b.Align,
                        Flags = b.Flags,
                        FieldCountWord = b.FieldCountWord,
                        Attribute = b.Attribute,
                        Mutable = b.Mutable,
                        Fields = b.Fields
                            .Select(f => new HkTypeCatalog.Field
                            {
                                Name = f.Name,
                                Flags = f.Flags,
                                Extra = f.Extra,
                                Offset = f.Offset,
                                Type = R(f.Type),
                            })
                            .ToList(),
                        Interfaces = b.Interfaces
                            .Select(i => new HkTypeCatalog.Interface { Type = R(i.Type), Value = i.Value })
                            .ToList(),
                    },
                };
            }
        }

        static readonly Regex Names2025 = new(@"\bhka(Skeleton|Bone)\b");
        static readonly Regex ToSplatoon3Names = new(@"\bhk(Skeleton|Bone)\b");

        /// <summary>A type signature with the classes Havok renamed between the versions renamed.</summary>
        static string Rename(string sig, bool to2025) =>
            to2025
                ? Names2025.Replace(sig, "hk$1")
                : ToSplatoon3Names.Replace(sig, "hka$1");

        //SDKV, an empty DATA, the TYPE section and an index with the null item only.
        static byte[] EmptyTagfile(string sdk, byte[] typeSection)
        {
            var body = new List<byte>();
            body.AddRange(Section("SDKV", Encoding.ASCII.GetBytes(sdk), 1));
            body.AddRange(Section("DATA", Array.Empty<byte>(), 1));
            body.AddRange(typeSection);
            body.AddRange(Section("INDX", Section("ITEM", new byte[12], 1), 0));
            return Section("TAG0", body.ToArray(), 0);
        }

        static byte[] Section(string magic, byte[] body, uint flags)
        {
            var output = new byte[body.Length + 8];
            BinaryPrimitives.WriteUInt32BigEndian(output, (uint)(body.Length + 8) | (flags << 30));
            Encoding.ASCII.GetBytes(magic).CopyTo(output, 4);
            body.CopyTo(output, 8);
            return output;
        }

        /// <summary>
        /// A copy of an object graph in the other game's types: fields by name, shared targets
        /// kept shared, numbers at the new widths.
        /// </summary>
        sealed class Graph
        {
            public readonly Target Target;
            readonly bool _to2025;
            readonly Dictionary<object, object> _memo = new(ReferenceEqualityComparer.Instance);
            public readonly List<(HkObject Source, HkObject Made)> Objects = new();
            readonly HashSet<string> _unmatched = new(StringComparer.Ordinal);

            public Graph(Target target, bool to2025)
            {
                Target = target;
                _to2025 = to2025;
            }

            HkType Map(HkType source) => Target.Type(Rename(source.Signature, _to2025));

            /// <summary>Fails on a field one side has that is not in <paramref name="settled"/>.</summary>
            public void CheckFields(HashSet<string> settled)
            {
                var open = _unmatched.Where(f => !settled.Contains(f)).ToList();
                if (open.Count > 0)
                    throw new ConvertException(
                        $"{open[0]} has no {Game(_to2025)} counterpart."
                    );
            }

            public object Value(object value, HkType from, HkType to)
            {
                switch (value)
                {
                    case null:
                        return null;
                    case string s:
                        return s;
                    case HkObject o:
                        return Record(o, to);
                    case HkArray a:
                        return Array(a, to);
                    case float[] f:
                        return Floats(f, to);
                    case object[] parts:
                    {
                        int count = to?.TupleCount ?? parts.Length;
                        var result = new object[count];
                        for (int i = 0; i < count; i++)
                            result[i] =
                                i < parts.Length
                                    ? Value(parts[i], from?.Resolved.Subtype, to?.Resolved.Subtype)
                                    : HkValue.Default(to?.Resolved.Subtype);
                        return result;
                    }
                    case byte[] raw:
                    {
                        int size = to?.ByteSize ?? raw.Length;
                        var copy = new byte[size > 0 ? size : raw.Length];
                        raw.AsSpan(0, Math.Min(raw.Length, copy.Length)).CopyTo(copy);
                        return copy;
                    }
                    default:
                        return HkValue.Coerce(value, to);
                }
            }

            HkObject Record(HkObject o, HkType to)
            {
                if (_memo.TryGetValue(o, out var done))
                    return (HkObject)done;
                //An inline record takes the field's type, a pointed at one its own.
                var type = to != null && to.EffectiveKind == HkKind.Record ? to : Map(o.Type);
                var made = new HkObject(type);
                _memo[o] = made;
                Objects.Add((o, made));
                var fields = type.AllFields;
                var sourceFields = o.Type.AllFields;
                for (int i = 0; i < fields.Count; i++)
                {
                    int si = o.Type.FieldIndex(fields[i].Name);
                    if (si >= 0)
                        made.Values[i] = Value(o.Values[si], sourceFields[si].Type, fields[i].Type);
                    else
                    {
                        made.Values[i] = HkValue.Default(fields[i].Type);
                        _unmatched.Add(type.RecordType.Name + "." + fields[i].Name);
                    }
                }
                foreach (var f in sourceFields)
                    if (type.FieldIndex(f.Name) < 0)
                        _unmatched.Add(o.Type.RecordType.Name + "." + f.Name);
                return made;
            }

            HkArray Array(HkArray a, HkType to)
            {
                if (_memo.TryGetValue(a, out var done))
                    return (HkArray)done;
                var element =
                    a.ElementType != null
                        ? Map(a.ElementType)
                        : to?.Resolved.Subtype;
                var made = new HkArray(element, a.Count);
                _memo[a] = made;
                foreach (var e in a)
                    made.Add(Value(e, a.ElementType, element));
                return made;
            }

            //A precise transform is a transform with a translation remainder after it.
            static float[] Floats(float[] f, HkType to)
            {
                int count = to == null ? f.Length
                    : to.TupleCount > 0 ? to.TupleCount
                    : to.ByteSize / 4;
                if (count <= 0)
                    count = f.Length;
                var result = new float[count];
                System.Array.Copy(f, result, Math.Min(f.Length, count));
                if (f.Length == 20 && count == 16)
                    for (int i = 0; i < 3; i++)
                        result[12 + i] += f[16 + i];
                return result;
            }
        }

        #endregion
    }
}
