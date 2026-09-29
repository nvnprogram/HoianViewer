using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace PlayerViewer.Phive
{
    /// <summary>
    /// A tagfile's TYPE section as editable tables: the type and field strings, the names with
    /// their template arguments, the bodies and the hashes. <see cref="Write"/> gives the stock
    /// section back byte for byte; <see cref="Import"/> copies types from another file's section,
    /// with everything they reference, so a graph can use classes its own file never declared.
    /// </summary>
    public class HkTypeSection
    {
        public class TypeName
        {
            public int NameIndex;
            public List<(int NameIndex, ulong Value)> Templates = new();
        }

        public class Field
        {
            public int NameIndex;
            public ulong Flags;
            public ulong Extra;
            public ulong Offset;
            public int TypeId;
        }

        public class Body
        {
            public int Id;
            public int ParentId;
            public ulong OptBits;
            public ulong Format,
                Version,
                Size,
                Align,
                Flags;
            public int SubtypeId;
            public ulong FieldCountWord;
            public List<Field> Fields = new();
            public List<(int TypeId, ulong Value)> Interfaces = new();
            public ulong Attribute,
                Mutable;
        }

        public const ulong OptFormat = 1 << 0,
            OptSubtype = 1 << 1,
            OptVersion = 1 << 2,
            OptSizeAlign = 1 << 3,
            OptFlags = 1 << 4,
            OptDecls = 1 << 5,
            OptInterfaces = 1 << 6,
            OptAttributeString = 1 << 7,
            OptMutable = 1 << 8;

        public List<string> TypeStrings = new();
        public List<string> FieldStrings = new();

        /// <summary>By type id; entry 0 is the null type and stays null.</summary>
        public List<TypeName> Names = new() { null };
        public List<Body> Bodies = new();
        public List<(int TypeId, uint Hash)> Hashes = new();

        /// <summary>TPTR slots: the highest hashed id plus one. Negative for one per type.</summary>
        public int PointerCount = -1;

        /// <summary>Reads a whole TYPE section, its 8 byte header included.</summary>
        public static HkTypeSection Read(byte[] section)
        {
            var s = new HkTypeSection();
            int end = (int)(BinaryPrimitives.ReadUInt32BigEndian(section) & 0x3FFFFFFF);
            int pos = 8;
            while (pos + 8 <= end)
            {
                int size = (int)(
                    BinaryPrimitives.ReadUInt32BigEndian(section.AsSpan(pos)) & 0x3FFFFFFF
                );
                if (size < 8)
                    break;
                string magic = Encoding.ASCII.GetString(section, pos + 4, 4);
                var payload = section.AsSpan(pos + 8, size - 8).ToArray();
                switch (magic)
                {
                    case "TST1":
                        s.TypeStrings = ReadStrings(payload);
                        break;
                    case "FST1":
                        s.FieldStrings = ReadStrings(payload);
                        break;
                    case "TNA1":
                        s.ReadNames(payload);
                        break;
                    case "TBDY":
                        s.ReadBodies(payload);
                        break;
                    case "THSH":
                        s.ReadHashes(payload);
                        break;
                    case "TPTR":
                    case "TPAD":
                        break;
                    default:
                        throw new InvalidOperationException($"Unknown TYPE subsection {magic}");
                }
                pos += size;
            }
            return s;
        }

        static List<string> ReadStrings(byte[] payload)
        {
            var list = new List<string>();
            int pos = 0;
            while (pos < payload.Length && payload[pos] != 0xFF)
            {
                int len = 0;
                while (pos + len < payload.Length && payload[pos + len] != 0)
                    len++;
                list.Add(Encoding.UTF8.GetString(payload, pos, len));
                pos += len + 1;
            }
            return list;
        }

        void ReadNames(byte[] payload)
        {
            var r = new Vle(payload);
            ulong count = r.Read();
            for (ulong i = 1; i < count; i++)
            {
                var name = new TypeName { NameIndex = (int)r.Read() };
                ulong templates = r.Read();
                for (ulong t = 0; t < templates; t++)
                    name.Templates.Add(((int)r.Read(), r.Read()));
                Names.Add(name);
            }
        }

        void ReadBodies(byte[] payload)
        {
            var r = new Vle(payload);
            while (r.HasMore)
            {
                int id = (int)r.Read();
                //Zero padding after the last body reads as a null id.
                if (id == 0)
                    break;
                var b = new Body
                {
                    Id = id,
                    ParentId = (int)r.Read(),
                    OptBits = r.Read(),
                };
                if ((b.OptBits & OptFormat) != 0)
                    b.Format = r.Read();
                if ((b.OptBits & OptSubtype) != 0)
                    b.SubtypeId = (int)r.Read();
                if ((b.OptBits & OptVersion) != 0)
                    b.Version = r.Read();
                if ((b.OptBits & OptSizeAlign) != 0)
                {
                    b.Size = r.Read();
                    b.Align = r.Read();
                }
                if ((b.OptBits & OptFlags) != 0)
                    b.Flags = r.Read();
                if ((b.OptBits & OptDecls) != 0)
                {
                    b.FieldCountWord = r.Read();
                    for (ulong i = 0; i < (b.FieldCountWord & 0xFFFF); i++)
                    {
                        var f = new Field { NameIndex = (int)r.Read(), Flags = r.Read() };
                        if ((f.Flags & 0x80) != 0)
                            f.Extra = r.Read();
                        f.Offset = r.Read();
                        f.TypeId = (int)r.Read();
                        b.Fields.Add(f);
                    }
                }
                if ((b.OptBits & OptInterfaces) != 0)
                {
                    ulong n = r.Read();
                    for (ulong i = 0; i < n; i++)
                        b.Interfaces.Add(((int)r.Read(), r.Read()));
                }
                if ((b.OptBits & OptAttributeString) != 0)
                    b.Attribute = r.Read();
                if ((b.OptBits & OptMutable) != 0)
                    b.Mutable = r.Read();
                Bodies.Add(b);
            }
        }

        void ReadHashes(byte[] payload)
        {
            var r = new Vle(payload);
            ulong count = r.Read();
            for (ulong i = 0; i < count; i++)
            {
                int id = (int)r.Read();
                Hashes.Add((id, r.ReadU32()));
            }
        }

        /// <summary>The section with its header, subsections in the stock order.</summary>
        public byte[] Write()
        {
            var names = new VleWriter();
            names.Write((ulong)Names.Count);
            for (int i = 1; i < Names.Count; i++)
            {
                names.Write((ulong)Names[i].NameIndex);
                names.Write((ulong)Names[i].Templates.Count);
                foreach (var (n, v) in Names[i].Templates)
                {
                    names.Write((ulong)n);
                    names.Write(v);
                }
            }

            var bodies = new VleWriter();
            foreach (var b in Bodies)
            {
                bodies.Write((ulong)b.Id);
                bodies.Write((ulong)b.ParentId);
                bodies.Write(b.OptBits);
                if ((b.OptBits & OptFormat) != 0)
                    bodies.Write(b.Format);
                if ((b.OptBits & OptSubtype) != 0)
                    bodies.Write((ulong)b.SubtypeId);
                if ((b.OptBits & OptVersion) != 0)
                    bodies.Write(b.Version);
                if ((b.OptBits & OptSizeAlign) != 0)
                {
                    bodies.Write(b.Size);
                    bodies.Write(b.Align);
                }
                if ((b.OptBits & OptFlags) != 0)
                    bodies.Write(b.Flags);
                if ((b.OptBits & OptDecls) != 0)
                {
                    bodies.Write(b.FieldCountWord);
                    foreach (var f in b.Fields)
                    {
                        bodies.Write((ulong)f.NameIndex);
                        bodies.Write(f.Flags);
                        if ((f.Flags & 0x80) != 0)
                            bodies.Write(f.Extra);
                        bodies.Write(f.Offset);
                        bodies.Write((ulong)f.TypeId);
                    }
                }
                if ((b.OptBits & OptInterfaces) != 0)
                {
                    bodies.Write((ulong)b.Interfaces.Count);
                    foreach (var (t, v) in b.Interfaces)
                    {
                        bodies.Write((ulong)t);
                        bodies.Write(v);
                    }
                }
                if ((b.OptBits & OptAttributeString) != 0)
                    bodies.Write(b.Attribute);
                if ((b.OptBits & OptMutable) != 0)
                    bodies.Write(b.Mutable);
            }

            var hashes = new VleWriter();
            hashes.Write((ulong)Hashes.Count);
            foreach (var (id, hash) in Hashes)
            {
                hashes.Write((ulong)id);
                hashes.WriteU32(hash);
            }

            var parts = new List<byte>();
            parts.AddRange(
                Section("TPTR", new byte[(PointerCount < 0 ? Names.Count : PointerCount) * 8], 0)
            );
            parts.AddRange(Section("TST1", Strings(TypeStrings), 0xFF));
            parts.AddRange(Section("TNA1", names.ToArray(), 0));
            parts.AddRange(Section("FST1", Strings(FieldStrings), 0xFF));
            parts.AddRange(Section("TBDY", bodies.ToArray(), 0));
            parts.AddRange(Section("THSH", hashes.ToArray(), 0));
            parts.AddRange(Section("TPAD", Array.Empty<byte>(), 0));
            return Section("TYPE", parts.ToArray(), 0, 0);
        }

        static byte[] Strings(List<string> strings)
        {
            var bytes = new List<byte>();
            foreach (var s in strings)
            {
                bytes.AddRange(Encoding.UTF8.GetBytes(s));
                bytes.Add(0);
            }
            return bytes.ToArray();
        }

        static byte[] Section(string magic, byte[] body, byte pad, uint flags = 1)
        {
            int size = 8 + ((body.Length + 3) & ~3);
            var output = new byte[size];
            BinaryPrimitives.WriteUInt32BigEndian(output, (uint)size | (flags << 30));
            Encoding.ASCII.GetBytes(magic).CopyTo(output, 4);
            body.CopyTo(output, 8);
            for (int i = 8 + body.Length; i < size; i++)
                output[i] = pad;
            return output;
        }

        public string NameOf(int id) =>
            id > 0 && id < Names.Count ? TypeStrings[Names[id].NameIndex] : "";

        public Body BodyOf(int id) => Bodies.FirstOrDefault(b => b.Id == id);

        /// <summary>The ids whose type has this name (templated names are shared by many ids).</summary>
        public IEnumerable<int> IdsNamed(string name)
        {
            for (int i = 1; i < Names.Count; i++)
                if (TypeStrings[Names[i].NameIndex] == name)
                    yield return i;
        }

        /// <summary>
        /// Copies the named types from <paramref name="donor"/>, with every type they reference,
        /// skipping the ones this section already declares. Existing ids never move.
        /// Returns the names of the types that were added.
        /// </summary>
        public List<string> Import(HkTypeSection donor, IEnumerable<string> typeNames)
        {
            var mine = new Dictionary<string, int>(StringComparer.Ordinal);
            var mySignatures = HkTypeCatalog.Signatures(this);
            for (int i = 1; i < Names.Count; i++)
                mine.TryAdd(mySignatures[i], i);
            var donorSignatures = HkTypeCatalog.Signatures(donor);
            var map = new Dictionary<int, int>();
            var added = new List<string>();

            int ImportId(int d)
            {
                if (d <= 0)
                    return d;
                if (map.TryGetValue(d, out int mapped))
                    return mapped;
                string sig = d < donorSignatures.Length ? donorSignatures[d] : "";
                if (mine.TryGetValue(sig, out int existing))
                {
                    map[d] = existing;
                    //A name declared without a body (never instantiated there) takes the donor's.
                    if (BodyOf(existing) == null && donor.BodyOf(d) != null)
                    {
                        CopyBody(d, existing);
                        added.Add(donor.NameOf(d));
                    }
                    return existing;
                }
                int id = Names.Count;
                map[d] = id;
                mine[sig] = id;
                var dn = donor.Names[d];
                var name = new TypeName
                {
                    NameIndex = Intern(TypeStrings, donor.TypeStrings[dn.NameIndex]),
                };
                Names.Add(name);
                foreach (var (ni, v) in dn.Templates)
                {
                    string tn = donor.TypeStrings[ni];
                    name.Templates.Add(
                        (
                            Intern(TypeStrings, tn),
                            HkTypeCatalog.IsTypeParam(tn) ? (ulong)ImportId((int)v) : v
                        )
                    );
                }
                if (donor.BodyOf(d) != null)
                    CopyBody(d, id);
                foreach (var (hid, hash) in donor.Hashes)
                    if (hid == d)
                        Hashes.Add((id, hash));
                added.Add(donor.NameOf(d));
                return id;
            }

            void CopyBody(int d, int id)
            {
                var db = donor.BodyOf(d);
                if ((db.OptBits & OptAttributeString) != 0)
                    throw new NotSupportedException(
                        $"Type {donor.NameOf(d)} carries an attribute string"
                    );
                var body = new Body
                {
                    Id = id,
                    OptBits = db.OptBits,
                    Format = db.Format,
                    Version = db.Version,
                    Size = db.Size,
                    Align = db.Align,
                    Flags = db.Flags,
                    FieldCountWord = db.FieldCountWord,
                    Mutable = db.Mutable,
                };
                Bodies.Add(body);
                body.ParentId = ImportId(db.ParentId);
                body.SubtypeId = ImportId(db.SubtypeId);
                foreach (var f in db.Fields)
                    body.Fields.Add(
                        new Field
                        {
                            NameIndex = Intern(FieldStrings, donor.FieldStrings[f.NameIndex]),
                            Flags = f.Flags,
                            Extra = f.Extra,
                            Offset = f.Offset,
                            TypeId = ImportId(f.TypeId),
                        }
                    );
                foreach (var (t, v) in db.Interfaces)
                    body.Interfaces.Add((ImportId(t), v));
            }

            foreach (string typeName in typeNames)
            {
                var ids = donor.IdsNamed(typeName).ToList();
                if (ids.Count == 0)
                    throw new KeyNotFoundException($"The donor declares no type {typeName}");
                foreach (int d in ids)
                    ImportId(d);
            }
            return added;
        }

        static int Intern(List<string> table, string s)
        {
            int i = table.IndexOf(s);
            if (i >= 0)
                return i;
            table.Add(s);
            return table.Count - 1;
        }

        sealed class Vle
        {
            readonly byte[] _d;
            int _pos;

            public Vle(byte[] d) => _d = d;

            public bool HasMore => _pos < _d.Length;

            byte Next() => _pos < _d.Length ? _d[_pos++] : (byte)0;

            public uint ReadU32()
            {
                uint v = BinaryPrimitives.ReadUInt32LittleEndian(_d.AsSpan(_pos));
                _pos += 4;
                return v;
            }

            public ulong Read()
            {
                byte b0 = Next();
                if ((b0 & 0x80) == 0)
                    return b0;
                if ((b0 & 0xC0) == 0x80)
                    return ((ulong)(b0 & 0x3F) << 8) | Next();
                if ((b0 & 0xE0) == 0xC0)
                    return ((ulong)(b0 & 0x1F) << 16) | ((ulong)Next() << 8) | Next();
                if ((b0 & 0xF8) == 0xE0)
                    return ((ulong)(b0 & 0x07) << 24)
                        | ((ulong)Next() << 16)
                        | ((ulong)Next() << 8)
                        | Next();
                int extra =
                    (b0 & 0xF8) == 0xE8 ? 4
                    : (b0 & 0xFE) == 0xF0 ? 7
                    : b0 == 0xF8 ? 5
                    : b0 == 0xF9 ? 8
                    : throw new NotSupportedException($"VLE lead byte 0x{b0:X2}");
                ulong v =
                    (b0 & 0xF8) == 0xE8 ? (ulong)(b0 & 0x07)
                    : (b0 & 0xFE) == 0xF0 ? (ulong)(b0 & 0x01)
                    : 0;
                for (int i = 0; i < extra; i++)
                    v = (v << 8) | Next();
                return v;
            }
        }

        sealed class VleWriter
        {
            readonly List<byte> _b = new();

            public byte[] ToArray() => _b.ToArray();

            public void WriteU32(uint v)
            {
                _b.Add((byte)v);
                _b.Add((byte)(v >> 8));
                _b.Add((byte)(v >> 16));
                _b.Add((byte)(v >> 24));
            }

            public void Write(ulong v)
            {
                if (v < 0x80)
                    _b.Add((byte)v);
                else if (v < 0x4000)
                {
                    _b.Add((byte)(0x80 | (v >> 8)));
                    _b.Add((byte)v);
                }
                else if (v < 0x200000)
                {
                    _b.Add((byte)(0xC0 | (v >> 16)));
                    _b.Add((byte)(v >> 8));
                    _b.Add((byte)v);
                }
                else if (v < 0x8000000)
                {
                    _b.Add((byte)(0xE0 | (v >> 24)));
                    _b.Add((byte)(v >> 16));
                    _b.Add((byte)(v >> 8));
                    _b.Add((byte)v);
                }
                else if (v < 0x800000000)
                {
                    _b.Add((byte)(0xE8 | (v >> 32)));
                    for (int i = 3; i >= 0; i--)
                        _b.Add((byte)(v >> (i * 8)));
                }
                else
                {
                    _b.Add(0xF9);
                    for (int i = 7; i >= 0; i--)
                        _b.Add((byte)(v >> (i * 8)));
                }
            }
        }
    }
}
