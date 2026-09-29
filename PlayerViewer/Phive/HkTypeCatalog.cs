using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace PlayerViewer.Phive
{
    /// <summary>
    /// Havok's reflected types as a game build saves them, keyed by full name with template
    /// arguments: each type's identity, its body and its hash. The stock catalogue is
    /// harvested from every tagfile of the romfs, one per SDK version.
    /// </summary>
    public sealed class HkTypeCatalog
    {
        public sealed class Template
        {
            public string Param { get; set; }
            public string Type { get; set; }
            public ulong Value { get; set; }
        }

        public sealed class Field
        {
            public string Name { get; set; }
            public ulong Flags { get; set; }
            public ulong Extra { get; set; }
            public ulong Offset { get; set; }
            public string Type { get; set; }
        }

        public sealed class Interface
        {
            public string Type { get; set; }
            public ulong Value { get; set; }
        }

        public sealed class Body
        {
            public string Parent { get; set; }
            public string Subtype { get; set; }
            public ulong OptBits { get; set; }
            public ulong Format { get; set; }
            public ulong Version { get; set; }
            public ulong Size { get; set; }
            public ulong Align { get; set; }
            public ulong Flags { get; set; }
            public ulong FieldCountWord { get; set; }
            public ulong Attribute { get; set; }
            public ulong Mutable { get; set; }
            public List<Field> Fields { get; set; } = new();
            public List<Interface> Interfaces { get; set; } = new();

            public bool SameAs(Body o) =>
                Parent == o.Parent
                && Subtype == o.Subtype
                && OptBits == o.OptBits
                && Format == o.Format
                && Version == o.Version
                && Size == o.Size
                && Align == o.Align
                && Flags == o.Flags
                && FieldCountWord == o.FieldCountWord
                && Attribute == o.Attribute
                && Mutable == o.Mutable
                && Fields.Count == o.Fields.Count
                && Fields
                    .Zip(o.Fields)
                    .All(p =>
                        p.First.Name == p.Second.Name
                        && p.First.Flags == p.Second.Flags
                        && p.First.Extra == p.Second.Extra
                        && p.First.Offset == p.Second.Offset
                        && p.First.Type == p.Second.Type
                    )
                && Interfaces.Count == o.Interfaces.Count
                && Interfaces
                    .Zip(o.Interfaces)
                    .All(p => p.First.Type == p.Second.Type && p.First.Value == p.Second.Value);
        }

        public sealed class TypeDef
        {
            public string Name { get; set; }
            public List<Template> Templates { get; set; } = new();
            public Body Body { get; set; }
            public uint? Hash { get; set; }
        }

        public Dictionary<string, TypeDef> Types { get; set; } = new(StringComparer.Ordinal);

        static Dictionary<string, HkTypeCatalog> _stock;

        /// <summary>The embedded catalogue of an SDK version, or an empty one for a version it lacks.</summary>
        public static HkTypeCatalog Stock(string sdkVersion)
        {
            _stock ??= LoadEmbedded();
            return _stock.TryGetValue(sdkVersion ?? "", out var c) ? c : new HkTypeCatalog();
        }

        static Dictionary<string, HkTypeCatalog> LoadEmbedded()
        {
            using var stream = typeof(HkTypeCatalog).Assembly.GetManifestResourceStream(
                "PlayerViewer.Phive.HkTypeCatalog.json"
            );
            return stream == null ? new() : Read(stream);
        }

        public static Dictionary<string, HkTypeCatalog> Read(Stream stream) =>
            JsonSerializer.Deserialize<Dictionary<string, HkTypeCatalog>>(stream) ?? new();

        public static void Write(Stream stream, Dictionary<string, HkTypeCatalog> catalogs) =>
            JsonSerializer.Serialize(
                stream,
                catalogs,
                new JsonSerializerOptions
                {
                    DefaultIgnoreCondition = System
                        .Text
                        .Json
                        .Serialization
                        .JsonIgnoreCondition
                        .WhenWritingDefault,
                }
            );

        /// <summary>This catalogue with what <paramref name="section"/> knows and it does not added. Its own entries win.</summary>
        public HkTypeCatalog Overlay(HkTypeSection section)
        {
            var copy = new HkTypeCatalog { Types = new(Types, StringComparer.Ordinal) };
            copy.Harvest(section, null);
            return copy;
        }

        /// <summary>
        /// Adds the types of a TYPE section: new types whole, and the body or hash a known type
        /// lacks. A body or hash that differs from the known one is a conflict, listed in
        /// <paramref name="conflicts"/> when given and otherwise left as it was.
        /// </summary>
        public void Harvest(HkTypeSection section, List<string> conflicts)
        {
            var sig = Signatures(section);
            string S(int id) => id > 0 && id < sig.Length ? sig[id] : null;
            var hashes = new Dictionary<int, uint>();
            foreach (var (id, hash) in section.Hashes)
                hashes.TryAdd(id, hash);
            var bodies = new Dictionary<int, HkTypeSection.Body>();
            foreach (var b in section.Bodies)
                bodies.TryAdd(b.Id, b);

            for (int id = 1; id < section.Names.Count; id++)
            {
                Types.TryGetValue(sig[id], out var known);
                Body body = null;
                if (bodies.TryGetValue(id, out var b))
                {
                    body = new Body
                    {
                        Parent = S(b.ParentId),
                        Subtype = S(b.SubtypeId),
                        OptBits = b.OptBits,
                        Format = b.Format,
                        Version = b.Version,
                        Size = b.Size,
                        Align = b.Align,
                        Flags = b.Flags,
                        FieldCountWord = b.FieldCountWord,
                        Attribute = b.Attribute,
                        Mutable = b.Mutable,
                    };
                    foreach (var f in b.Fields)
                        body.Fields.Add(
                            new Field
                            {
                                Name = section.FieldStrings[f.NameIndex],
                                Flags = f.Flags,
                                Extra = f.Extra,
                                Offset = f.Offset,
                                Type = S(f.TypeId),
                            }
                        );
                    foreach (var (t, v) in b.Interfaces)
                        body.Interfaces.Add(new Interface { Type = S(t), Value = v });
                }
                uint? hash = hashes.TryGetValue(id, out var h) ? h : null;

                if (known == null)
                {
                    var n = section.Names[id];
                    var def = new TypeDef
                    {
                        Name = section.TypeStrings[n.NameIndex],
                        Body = body,
                        Hash = hash,
                    };
                    foreach (var (ni, v) in n.Templates)
                    {
                        string p = section.TypeStrings[ni];
                        def.Templates.Add(
                            IsTypeParam(p)
                                ? new Template { Param = p, Type = S((int)v) }
                                : new Template { Param = p, Value = v }
                        );
                    }
                    Types[sig[id]] = def;
                    continue;
                }

                if (body != null && known.Body != null && !body.SameAs(known.Body))
                    conflicts?.Add("body of " + sig[id]);
                if (hash != null && known.Hash != null && hash != known.Hash)
                    conflicts?.Add("hash of " + sig[id]);
                if ((body != null && known.Body == null) || (hash != null && known.Hash == null))
                    Types[sig[id]] = new TypeDef
                    {
                        Name = known.Name,
                        Templates = known.Templates,
                        Body = known.Body ?? body,
                        Hash = known.Hash ?? hash,
                    };
            }
        }

        public static bool IsTypeParam(string param) => param.StartsWith('t');

        /// <summary>Every id's full name: the type name with its template arguments, type arguments by their own full name.</summary>
        public static string[] Signatures(HkTypeSection section)
        {
            var memo = new string[section.Names.Count];
            string Sig(int id)
            {
                if (id <= 0 || id >= section.Names.Count)
                    return null;
                if (memo[id] != null)
                    return memo[id];
                memo[id] = "?";
                var n = section.Names[id];
                memo[id] = Format(
                    section.TypeStrings[n.NameIndex],
                    n.Templates.Select(t => (section.TypeStrings[t.NameIndex], t.Value)),
                    v => Sig((int)v)
                );
                return memo[id];
            }
            for (int i = 1; i < section.Names.Count; i++)
                Sig(i);
            return memo;
        }

        /// <summary>The full name of a type from its name and TNA1 template arguments.</summary>
        public static string Format(
            string name,
            IEnumerable<(string Param, ulong Value)> templates,
            Func<ulong, string> typeName
        )
        {
            var list = templates.ToList();
            if (list.Count == 0)
                return name;
            var sb = new StringBuilder(name).Append('<');
            for (int i = 0; i < list.Count; i++)
            {
                if (i > 0)
                    sb.Append(',');
                var (p, v) = list[i];
                sb.Append(p).Append('=').Append(IsTypeParam(p) ? typeName(v) : v.ToString());
            }
            return sb.Append('>').ToString();
        }
    }
}
