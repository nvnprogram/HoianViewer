using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;

namespace PlayerViewer.Phive
{
    /// <summary>One changed value: where it is from the tagfile root (or the AAMP root) and what it became.</summary>
    public sealed class ClothValueEdit
    {
        public string Path { get; set; }
        public JsonNode Value { get; set; }
    }

    /// <summary>
    /// The value differences between two cloth files of one structure, and applying them to a
    /// third built the same way. Paths run from the tagfile root (or <c>aamp</c>) by field, index
    /// or unique <c>{name}</c>; what a value cannot express is listed as structural.
    /// </summary>
    public static class ClothDiff
    {
        public sealed class Result
        {
            public List<ClothValueEdit> Edits = new();
            public List<string> Structural = new();

            /// <summary>The largest difference between two reals the edits carry.</summary>
            public float Largest;
        }

        public static Result Compare(ClothFile from, ClothFile to)
        {
            var walk = new Walker();
            if (!from.TypeSectionBytes.AsSpan().SequenceEqual(to.TypeSectionBytes))
                walk.Result.Structural.Add("TYPE section: the declared types differ");
            walk.Record(from.Tagfile.Root, to.Tagfile.Root, "");
            if (from.Params != null && to.Params != null)
                walk.List(from.Params.Aamp.Root, to.Params.Aamp.Root, "aamp");
            else if ((from.Params == null) != (to.Params == null))
                walk.Result.Structural.Add("aamp: one file has no readable parameters");
            return walk.Result;
        }

        sealed class Walker
        {
            public readonly Result Result = new();
            readonly Dictionary<object, string> _from = new(ReferenceEqualityComparer.Instance);
            readonly Dictionary<object, string> _to = new(ReferenceEqualityComparer.Instance);

            void Structural(string path, string what) =>
                Result.Structural.Add($"{(path.Length > 0 ? path : "root")}: {what}");

            void Note(float a, float b)
            {
                float d = Math.Abs(a - b);
                if (float.IsFinite(d))
                    Result.Largest = Math.Max(Result.Largest, d);
            }

            void Edit(string path, object value) =>
                Result.Edits.Add(new ClothValueEdit { Path = path, Value = Encode(value) });

            public void Record(HkObject a, HkObject b, string path)
            {
                if (a.Type.Name != b.Type.Name)
                {
                    Structural(path, $"{a.Type.Name} became {b.Type.Name}");
                    return;
                }
                var fields = a.Type.AllFields;
                for (int i = 0; i < fields.Count; i++)
                    Value(
                        a.Values[i],
                        b.Values[i],
                        fields[i].Type,
                        path.Length > 0 ? path + "/" + fields[i].Name : fields[i].Name
                    );
            }

            void Value(object a, object b, HkType declared, string path)
            {
                var kind = declared?.EffectiveKind ?? HkKind.Void;
                if (kind == HkKind.Pointer || (a is HkObject && kind != HkKind.Record))
                {
                    Pointer(a, b, declared, path);
                    return;
                }
                switch (a)
                {
                    case null when b == null:
                        return;
                    case HkObject x when b is HkObject y:
                        Record(x, y, path);
                        return;
                    case HkArray x when b is HkArray y:
                        Array(x, y, path);
                        return;
                    case float[] x when b is float[] y:
                        if (x.Length != y.Length)
                            Structural(path, "tuple length changed");
                        else if (!x.Select(Bits).SequenceEqual(y.Select(Bits)))
                        {
                            for (int i = 0; i < x.Length; i++)
                                Note(x[i], y[i]);
                            Edit(path, y);
                        }
                        return;
                    case object[] x when b is object[] y:
                        if (x.Length != y.Length)
                        {
                            Structural(path, "tuple length changed");
                            return;
                        }
                        for (int i = 0; i < x.Length; i++)
                            Value(x[i], y[i], declared?.Resolved.Subtype, $"{path}[{i}]");
                        return;
                    case byte[] x when b is byte[] y:
                        if (!x.AsSpan().SequenceEqual(y))
                            Edit(path, y);
                        return;
                }
                if (a is HkObject || b is HkObject || a is HkArray || b is HkArray)
                {
                    Structural(path, "a record or array where the other has none");
                    return;
                }
                if (!Same(a, b))
                {
                    if (a != null && b != null && a.GetType() != b.GetType())
                        Structural(path, $"{a.GetType().Name} became {b.GetType().Name}");
                    else
                    {
                        if (Real(a) is float x && Real(b) is float y)
                            Note(x, y);
                        Edit(path, b);
                    }
                }
            }

            void Pointer(object a, object b, HkType declared, string path)
            {
                if (a is string || b is string)
                {
                    if (!Same(a, b))
                        Edit(path, b);
                    return;
                }
                if (a == null && b == null)
                    return;
                if (a == null || b == null)
                {
                    Structural(path, a == null ? "a pointer was set" : "a pointer was cleared");
                    return;
                }
                bool seenA = _from.TryGetValue(a, out string atA);
                bool seenB = _to.TryGetValue(b, out string atB);
                if (seenA || seenB)
                {
                    if (!seenA || !seenB || atA != atB)
                        Structural(path, "points at another target");
                    return;
                }
                _from[a] = path;
                _to[b] = path;
                switch (a)
                {
                    case HkObject x when b is HkObject y:
                        Record(x, y, path);
                        break;
                    case HkArray x when b is HkArray y:
                        Array(x, y, path);
                        break;
                    default:
                        Structural(path, "points at another kind of target");
                        break;
                }
            }

            void Array(HkArray a, HkArray b, string path)
            {
                var element = a.ElementType?.EffectiveKind ?? HkKind.Void;
                bool plain =
                    element is HkKind.Int or HkKind.Float or HkKind.Bool
                    || (element == HkKind.String && a.All(x => x is string or null));
                if (a.Count != b.Count)
                {
                    if (plain)
                        Edit(path, b);
                    else
                        Structural(path, $"{a.Count} elements became {b.Count}");
                    return;
                }
                var keys = Keys(a);
                for (int i = 0; i < a.Count; i++)
                    Value(a[i], b[i], a.ElementType, path + keys[i]);
            }

            public void List(AampList a, AampList b, string path)
            {
                if (a.Lists.Count != b.Lists.Count || a.Objects.Count != b.Objects.Count)
                {
                    Structural(path, "lists or objects added or removed");
                    return;
                }
                for (int i = 0; i < a.Lists.Count; i++)
                    if (a.Lists[i].Hash != b.Lists[i].Hash)
                        Structural($"{path}/L{a.Lists[i].Hash:X8}", "list renamed");
                    else
                        List(a.Lists[i], b.Lists[i], $"{path}/{AampKey("L", a.Lists, i)}");
                for (int i = 0; i < a.Objects.Count; i++)
                {
                    var x = a.Objects[i];
                    var y = b.Objects[i];
                    string at = $"{path}/{AampKey("O", a.Objects, i)}";
                    if (x.Hash != y.Hash)
                    {
                        Structural(at, "object renamed");
                        continue;
                    }
                    var names = x.Params.Select(p => p.Hash).ToList();
                    if (
                        !names.SequenceEqual(y.Params.Select(p => p.Hash))
                        || names.Distinct().Count() != names.Count
                    )
                    {
                        Structural(at, "parameters added, removed or reordered");
                        continue;
                    }
                    for (int k = 0; k < x.Params.Count; k++)
                    {
                        var p = x.Params[k];
                        var q = y.Params[k];
                        if (
                            p.Type != q.Type
                            || !p.Data.AsSpan().SequenceEqual(q.Data)
                            || p.Text != q.Text
                        )
                            Result.Edits.Add(
                                new ClothValueEdit
                                {
                                    Path = $"{at}/P{p.Hash:X8}",
                                    Value = EncodeParam(q),
                                }
                            );
                    }
                }
            }
        }

        /// <summary>Each element's path key: its name when unique in the array, so a reorder keeps the path; else its index.</summary>
        static string[] Keys(HkArray array)
        {
            var names = array
                .Select(x =>
                    (x as HkObject)?.Has("name") == true ? ((HkObject)x).String("name") : null
                )
                .ToArray();
            var keys = new string[array.Count];
            for (int i = 0; i < keys.Length; i++)
                keys[i] =
                    names[i] is string n
                    && n.Length > 0
                    && n.IndexOfAny(Reserved) < 0
                    && names.Count(x => x == n) == 1
                        ? "{" + n + "}"
                        : $"[{i}]";
            return keys;
        }

        static readonly char[] Reserved = { '/', '[', ']', '{', '}' };

        static string AampKey<T>(List<T> items, int i, Func<T, uint> hash) =>
            items.Count(x => hash(x) == hash(items[i])) == 1 ? $"{hash(items[i]):X8}" : $"#{i}";

        static string AampKey(string kind, List<AampList> items, int i) =>
            kind + AampKey(items, i, x => x.Hash);

        static string AampKey(string kind, List<AampObject> items, int i) =>
            kind + AampKey(items, i, x => x.Hash);

        static int Bits(float f) => BitConverter.SingleToInt32Bits(f);

        static float? Real(object v) =>
            v switch
            {
                float f => f,
                double d => (float)d,
                Half h => (float)h,
                _ => null,
            };

        static bool Same(object a, object b) =>
            a switch
            {
                null => b == null,
                float x when b is float y => Bits(x) == Bits(y),
                double x when b is double y => BitConverter.DoubleToInt64Bits(x)
                    == BitConverter.DoubleToInt64Bits(y),
                Half x when b is Half y => BitConverter.HalfToInt16Bits(x)
                    == BitConverter.HalfToInt16Bits(y),
                _ => a.Equals(b),
            };

        static JsonNode Encode(object value) =>
            value switch
            {
                null => null,
                bool b => JsonValue.Create(b),
                float f => float.IsFinite(f)
                    ? JsonValue.Create(f)
                    : JsonValue.Create(f.ToString(CultureInfo.InvariantCulture)),
                double d => double.IsFinite(d)
                    ? JsonValue.Create(d)
                    : JsonValue.Create(d.ToString(CultureInfo.InvariantCulture)),
                Half h => Encode((float)h),
                string s => JsonValue.Create(s),
                byte[] raw => new JsonObject { ["bytes"] = Convert.ToBase64String(raw) },
                float[] fs => new JsonArray(fs.Select(f => Encode(f)).ToArray()),
                HkArray a => new JsonArray(a.Select(Encode).ToArray()),
                ulong u => JsonValue.Create(u),
                IConvertible c => JsonValue.Create(Convert.ToInt64(c)),
                _ => throw new InvalidOperationException($"no encoding for {value.GetType().Name}"),
            };

        static JsonNode EncodeParam(AampParam p)
        {
            var node = new JsonObject { ["aampType"] = (int)p.Type };
            if (p.IsString)
                node["text"] = p.Text;
            else
                node["bytes"] = Convert.ToBase64String(p.Data);
            return node;
        }

        /// <summary>
        /// Sets each edit's value in a file built the way the compared one was, converting it to
        /// the type the value there has. Returns what could not be applied, with why.
        /// </summary>
        public static List<string> Apply(ClothFile file, IEnumerable<ClothValueEdit> edits)
        {
            //Resolve every path first: an edit of a name changes what a later path points at.
            var list = edits.ToList();
            var errors = new string[list.Count];
            var setters = new Action[list.Count];
            for (int i = 0; i < list.Count; i++)
                try
                {
                    setters[i] = list[i].Path.StartsWith("aamp/", StringComparison.Ordinal)
                        ? ResolveParam(file, list[i])
                        : ResolveValue(file.Tagfile.Root, list[i]);
                }
                catch (Exception ex)
                {
                    errors[i] = ex.Message;
                }
            for (int i = 0; i < list.Count; i++)
                try
                {
                    setters[i]?.Invoke();
                }
                catch (Exception ex)
                {
                    errors[i] = ex.Message;
                }
            var failed = new List<string>();
            for (int i = 0; i < list.Count; i++)
                if (errors[i] != null)
                    failed.Add($"{list[i].Path}: {errors[i]}");
            return failed;
        }

        /// <summary>What sets the edit's value at its path, found now and run later.</summary>
        static Action ResolveValue(HkObject root, ClothValueEdit edit)
        {
            //The container of the last step and how to set the value in it.
            object at = root;
            Func<object> get = null;
            Action<object> set = null;
            foreach (var segment in edit.Path.Split('/'))
            {
                int cut = segment.IndexOfAny(new[] { '[', '{' });
                string field = cut < 0 ? segment : segment[..cut];
                var obj =
                    at as HkObject ?? throw new InvalidOperationException($"no record at {field}");
                int f = obj.Type.FieldIndex(field);
                if (f < 0)
                    throw new InvalidOperationException($"{obj.Type.Name} has no field {field}");
                get = () => obj.Values[f];
                set = v => obj.Values[f] = v;
                at = obj.Values[f];
                for (int k = cut; k >= 0 && k < segment.Length; )
                {
                    int end = segment.IndexOf(segment[k] == '[' ? ']' : '}', k);
                    string key = segment.Substring(k + 1, end - k - 1);
                    bool byName = segment[k] == '{';
                    k = end + 1;
                    switch (at)
                    {
                        case HkArray array:
                        {
                            int i = byName
                                ? IndexOfName(array, key)
                                : int.Parse(key, CultureInfo.InvariantCulture);
                            if (i < 0 || i >= array.Count)
                                throw new InvalidOperationException($"no element {key}");
                            var owner = array;
                            get = () => owner[i];
                            set = v => owner[i] = v;
                            at = array[i];
                            break;
                        }
                        case object[] tuple when !byName:
                        {
                            int i = int.Parse(key, CultureInfo.InvariantCulture);
                            var owner = tuple;
                            get = () => owner[i];
                            set = v => owner[i] = v;
                            at = tuple[i];
                            break;
                        }
                        default:
                            throw new InvalidOperationException($"nothing to index at {key}");
                    }
                }
            }
            return () => set(Decode(edit.Value, get()));
        }

        static int IndexOfName(HkArray array, string name)
        {
            for (int i = 0; i < array.Count; i++)
                if (array[i] is HkObject o && o.Has("name") && o.String("name") == name)
                    return i;
            return -1;
        }

        /// <summary>A value read back as the type the value it replaces has.</summary>
        static object Decode(JsonNode node, object current)
        {
            switch (current)
            {
                case float[] floats:
                {
                    var items = node.AsArray();
                    if (items.Count != floats.Length)
                        throw new InvalidOperationException("tuple length differs");
                    return items.Select(x => ReadFloat(x)).ToArray();
                }
                case byte[]:
                    return Convert.FromBase64String(node["bytes"].GetValue<string>());
                case HkArray array:
                {
                    var copy = new HkArray(array.ElementType, 0) { SourceItem = array.SourceItem };
                    var sample = array.Count > 0 ? array[0] : HkValue.Default(array.ElementType);
                    foreach (var item in node.AsArray())
                        copy.Add(Decode(item, sample));
                    array.Clear();
                    array.AddRange(copy);
                    return array;
                }
                case float:
                    return ReadFloat(node);
                case double:
                    return node is JsonValue v && v.TryGetValue(out string s)
                        ? double.Parse(s, CultureInfo.InvariantCulture)
                        : node.GetValue<double>();
                case Half:
                    return (Half)ReadFloat(node);
                case bool:
                    return node.GetValue<bool>();
                case string:
                case null:
                    return node?.GetValue<string>();
                case ulong:
                    return node.GetValue<ulong>();
                case IConvertible c:
                    return Convert.ChangeType(
                        node.GetValue<long>(),
                        c.GetType(),
                        CultureInfo.InvariantCulture
                    );
                default:
                    throw new InvalidOperationException(
                        $"cannot set a {current?.GetType().Name ?? "null"} from a value"
                    );
            }
        }

        static float ReadFloat(JsonNode node) =>
            node is JsonValue v && v.TryGetValue(out string s)
                ? float.Parse(s, CultureInfo.InvariantCulture)
                : node.GetValue<float>();

        static Action ResolveParam(ClothFile file, ClothValueEdit edit)
        {
            var aamp = file.Params?.Aamp ?? throw new InvalidOperationException("no parameters");
            var parts = edit.Path.Split('/');
            var list = aamp.Root;
            AampObject obj = null;
            for (int i = 1; i < parts.Length - 1; i++)
            {
                string part = parts[i];
                if (part[0] == 'L')
                    list = Pick(list.Lists, part[1..], x => x.Hash);
                else if (part[0] == 'O' && i == parts.Length - 2)
                    obj = Pick(list.Objects, part[1..], x => x.Hash);
                else
                    throw new InvalidOperationException($"bad step {part}");
            }
            string last = parts[^1];
            if (obj == null || last[0] != 'P')
                throw new InvalidOperationException("no parameter object");
            uint hash = uint.Parse(last[1..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            var param =
                obj.Find(hash) ?? throw new InvalidOperationException($"no parameter {last[1..]}");
            return () =>
            {
                param.Type = (AampType)edit.Value["aampType"].GetValue<int>();
                if (edit.Value["text"] is JsonNode text)
                    param.Text = text.GetValue<string>();
                else
                    param.Data = Convert.FromBase64String(edit.Value["bytes"].GetValue<string>());
            };
        }

        static T Pick<T>(List<T> items, string key, Func<T, uint> hash)
        {
            if (key.StartsWith('#'))
                return items[int.Parse(key[1..], CultureInfo.InvariantCulture)];
            uint h = uint.Parse(key, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return items.FirstOrDefault(x => hash(x) == h)
                ?? throw new InvalidOperationException($"nothing named {key}");
        }

        /// <summary>A short readable form of an edit for a report.</summary>
        public static string Describe(ClothValueEdit edit)
        {
            string value = edit.Value?.ToJsonString() ?? "null";
            if (value.Length > 60)
                value = value[..57] + "...";
            return $"{edit.Path} = {value}";
        }
    }
}
