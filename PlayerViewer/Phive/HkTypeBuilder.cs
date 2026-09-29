using System;
using System.Collections.Generic;

namespace PlayerViewer.Phive
{
    /// <summary>
    /// Builds a TYPE section in the order Havok's writer derives one: ids on first lookup, bodies
    /// for written types only, hashes on a type's first top level use.
    /// </summary>
    public sealed class HkTypeBuilder
    {
        readonly HkTypeCatalog _catalog;
        readonly Dictionary<string, int> _ids = new(StringComparer.Ordinal);
        readonly List<string> _sigOf = new() { null };
        readonly List<bool> _enqueued = new() { true };
        readonly Queue<int> _queue = new();
        readonly Dictionary<string, int> _typeStrings = new(StringComparer.Ordinal);
        readonly Dictionary<string, int> _fieldStrings = new(StringComparer.Ordinal);
        readonly HashSet<int> _hashed = new();

        public readonly HkTypeSection Section = new();

        /// <summary>What the catalogue could not supply: a body for a written type or a hash for a top level one.</summary>
        public readonly List<string> Missing = new();

        public HkTypeBuilder(HkTypeCatalog catalog) => _catalog = catalog;

        public int IdOf(string sig) => _ids[sig];

        /// <summary>The id of a type, named but not written.</summary>
        public int Lookup(string sig)
        {
            if (sig == null)
                return 0;
            if (_ids.TryGetValue(sig, out int id))
                return id;
            if (!_catalog.Types.TryGetValue(sig, out var def))
                throw new KeyNotFoundException("No type information for " + sig);
            id = _sigOf.Count;
            _ids[sig] = id;
            _sigOf.Add(sig);
            _enqueued.Add(false);
            var name = new HkTypeSection.TypeName
            {
                NameIndex = Intern(_typeStrings, Section.TypeStrings, def.Name),
            };
            Section.Names.Add(name);
            foreach (var t in def.Templates)
            {
                int param = Intern(_typeStrings, Section.TypeStrings, t.Param);
                name.Templates.Add(
                    (param, HkTypeCatalog.IsTypeParam(t.Param) ? (ulong)Lookup(t.Type) : t.Value)
                );
            }
            return id;
        }

        int Enqueue(string sig)
        {
            int id = Lookup(sig);
            if (!_enqueued[id])
            {
                _enqueued[id] = true;
                _queue.Enqueue(id);
            }
            return id;
        }

        void Drain()
        {
            while (_queue.Count > 0)
            {
                int id = _queue.Dequeue();
                var sig = _sigOf[id];
                var def = _catalog.Types[sig].Body;
                if (def == null)
                {
                    Missing.Add("body of " + sig);
                    continue;
                }
                var body = new HkTypeSection.Body
                {
                    Id = id,
                    ParentId = Enqueue(def.Parent),
                    OptBits = def.OptBits,
                    Format = def.Format,
                    Version = def.Version,
                    Size = def.Size,
                    Align = def.Align,
                    Flags = def.Flags,
                    FieldCountWord = def.FieldCountWord,
                    Attribute = def.Attribute,
                    Mutable = def.Mutable,
                };
                if ((def.OptBits & HkTypeSection.OptSubtype) != 0)
                {
                    //A fixed size array holds its elements inline, so they are written; anything else only names them.
                    var kind = (HkKind)(def.Format & 0x1F);
                    if (kind == HkKind.Array && (def.Format >> 8) != 0)
                        body.SubtypeId = Enqueue(def.Subtype);
                    else if (kind is HkKind.Array or HkKind.Pointer)
                        body.SubtypeId = Lookup(def.Subtype);
                    else
                        Missing.Add($"subtype of {kind} {sig}");
                }
                foreach (var f in def.Fields)
                {
                    int name = Intern(_fieldStrings, Section.FieldStrings, f.Name);
                    body.Fields.Add(
                        new HkTypeSection.Field
                        {
                            NameIndex = name,
                            Flags = f.Flags,
                            Extra = f.Extra,
                            Offset = f.Offset,
                            TypeId = Enqueue(f.Type),
                        }
                    );
                }
                foreach (var i in def.Interfaces)
                    body.Interfaces.Add((Enqueue(i.Type), i.Value));
                if ((def.OptBits & HkTypeSection.OptAttributeString) != 0)
                    Missing.Add("attribute string of " + sig);
                Section.Bodies.Add(body);
            }
        }

        /// <summary>A top level use: the type of an item, or of a slot that gets a patch. Returns its id.</summary>
        public int Write(string sig)
        {
            int id = Enqueue(sig);
            Drain();
            if (_hashed.Add(id))
            {
                var hash = _catalog.Types[sig].Hash;
                if (hash == null)
                    Missing.Add("hash of " + sig);
                Section.Hashes.Add((id, hash ?? 0));
                Section.PointerCount = Math.Max(Section.PointerCount, id + 1);
            }
            return id;
        }

        static int Intern(Dictionary<string, int> map, List<string> table, string s)
        {
            if (!map.TryGetValue(s, out int i))
            {
                i = map[s] = table.Count;
                table.Add(s);
            }
            return i;
        }
    }
}
