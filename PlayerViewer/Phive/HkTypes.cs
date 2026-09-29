using System;
using System.Collections.Generic;
using System.Linq;
using OpenTK;

namespace PlayerViewer.Phive
{
    public enum HkKind : uint
    {
        Void = 0,
        Opaque = 1,
        Bool = 2,
        String = 3,
        Int = 4,
        Float = 5,
        Pointer = 6,
        Record = 7,
        Array = 8,
    }

    public readonly record struct HkField(string Name, uint Flags, int Offset, HkType Type);

    /// <summary>
    /// A reflected type from a tagfile's TYPE section. A type with no body of its own (an
    /// alias such as hkVector4 or hkUint16) resolves through its parent.
    /// </summary>
    public sealed class HkType
    {
        public int Id;
        public string Name = "";

        /// <summary>The full name with template arguments, the type's identity across files.</summary>
        public string Signature = "";
        public HkType Parent;
        public HkType Subtype;
        public uint OptBits,
            Format,
            Version,
            Size,
            Align,
            Flags;
        public List<HkField> Fields = new();

        /// <summary>TNA1 template arguments: the parameter name ('t' or 'v' prefixed) and its value.</summary>
        public List<(string Name, ulong Value)> Templates = new();

        /// <summary>No TBDY body: named in TNA1 only, resolved to the "f" suffixed concrete type.</summary>
        public bool IsStub;

        public HkKind Kind => (HkKind)(Format & 0x1F);

        /// <summary>The type that carries the format: this one, or the first ancestor with one.</summary>
        public HkType Resolved => Kind != HkKind.Void || Parent == null ? this : Parent.Resolved;

        public HkKind EffectiveKind => Resolved.Kind;

        public int ByteSize => Size > 0 ? (int)Size : Parent?.ByteSize ?? 0;

        public int Alignment => Align != 0 ? (int)(Align & 0xFFF) : Parent?.Alignment ?? 4;

        /// <summary>The byte width of an integer type: 1, 2, 4 or 8.</summary>
        public int IntBytes
        {
            get
            {
                uint bits = Resolved.Format >> 10;
                return bits <= 8 ? 1
                    : bits <= 16 ? 2
                    : bits <= 32 ? 4
                    : 8;
            }
        }

        /// <summary>Whether an integer type is signed.</summary>
        public bool IsSigned => (Resolved.Format & (1 << 9)) != 0;

        /// <summary>Element count of a fixed inline tuple (hkVector4f is 4, hkTransformf 16), 0 for hkArray.</summary>
        public int TupleCount => (int)(Resolved.Format >> 8);

        /// <summary>The record layout type: the resolved type, or the first ancestor that declares fields.</summary>
        public HkType RecordType
        {
            get
            {
                var t = Resolved;
                while (t.Fields.Count == 0 && t.Parent != null)
                    t = t.Parent;
                return t;
            }
        }

        List<HkField> _allFields;
        Dictionary<string, int> _fieldIndex;

        /// <summary>Every field of a record, inherited ones first.</summary>
        public IReadOnlyList<HkField> AllFields
        {
            get
            {
                if (_allFields != null)
                    return _allFields;
                var list = new List<HkField>();
                if (Parent != null)
                {
                    var parentRecord = Parent.RecordType;
                    if (parentRecord != this)
                        list.AddRange(parentRecord.AllFields);
                }
                list.AddRange(Fields);
                _allFields = list;
                return list;
            }
        }

        /// <summary>Index into <see cref="AllFields"/> by name; a derived field shadows an inherited one.</summary>
        public int FieldIndex(string name)
        {
            if (_fieldIndex == null)
            {
                var index = new Dictionary<string, int>(StringComparer.Ordinal);
                var all = AllFields;
                for (int i = 0; i < all.Count; i++)
                    index[all[i].Name] = i;
                _fieldIndex = index;
            }
            return _fieldIndex.TryGetValue(name, out int i2) ? i2 : -1;
        }

        /// <summary>The element type of a tuple or array field of this record.</summary>
        public HkType ElementTypeOf(string field) =>
            AllFields[FieldIndex(field)].Type.Resolved.Subtype;

        /// <summary>True when this type or one of its ancestors has the given name.</summary>
        public bool IsA(string name)
        {
            for (var t = this; t != null; t = t.Parent)
                if (t.Name == name)
                    return true;
            return false;
        }

        public override string ToString() => Name;
    }

    /// <summary>
    /// One record of the tagfile: a single object item, an element of a record array, or a
    /// record stored inline in another. Values are in <see cref="HkType.AllFields"/> order and
    /// keep the file's exact types, so a writer can encode them back unchanged.
    /// </summary>
    public sealed class HkObject
    {
        public HkType Type { get; }
        public object[] Values { get; }

        /// <summary>The item this record was read from, 0 for an inline record or a new one.</summary>
        public int SourceItem { get; internal set; }

        public HkObject(HkType type)
        {
            Type = type;
            Values = new object[type.AllFields.Count];
        }

        public string TypeName => Type.Name;

        public bool Has(string field) => Type.FieldIndex(field) >= 0;

        public object this[string field]
        {
            get
            {
                int i = Type.FieldIndex(field);
                return i >= 0 ? Values[i] : null;
            }
            set
            {
                int i = Type.FieldIndex(field);
                if (i < 0)
                    throw new KeyNotFoundException($"{Type.Name} has no field {field}");
                Values[i] = value;
            }
        }

        /// <summary>Sets a field, converting a number to the field's stored width.</summary>
        public void Set(string field, object value)
        {
            int i = Type.FieldIndex(field);
            if (i < 0)
                throw new KeyNotFoundException($"{Type.Name} has no field {field}");
            Values[i] = HkValue.Coerce(value, Type.AllFields[i].Type);
        }

        public float Float(string field, float fallback = 0) =>
            this[field] is object v ? HkValue.ToSingle(v) : fallback;

        public int Int(string field, int fallback = 0) =>
            this[field] is object v ? HkValue.ToInt32(v) : fallback;

        public uint UInt(string field) => this[field] is object v ? Convert.ToUInt32(v) : 0;

        public bool Bool(string field, bool fallback = false) =>
            this[field] is object v ? Convert.ToBoolean(v) : fallback;

        public string String(string field) => this[field] as string;

        public HkObject Object(string field) => this[field] as HkObject;

        /// <summary>An hkArray field (the reader stores an empty one when unset); a missing field reads as a new empty array.</summary>
        public HkArray Array(string field) => this[field] as HkArray ?? new HkArray();

        /// <summary>A tuple field's reals, flattened (an hkVector4 gives 4, an hkTransform 16).</summary>
        public float[] Floats(string field) => HkValue.Floats(this[field]);

        public Vector4 Vector4(string field) => HkValue.ToVector4(this[field]);

        public Vector3 Vector3(string field) => HkValue.ToVector4(this[field]).Xyz;

        /// <summary>A 4x4 field in the viewer's row vector convention, w column exactly as stored.</summary>
        public Matrix4 Matrix(string field) => HkValue.ToMatrix(this[field]);

        public override string ToString() =>
            $"{Type.Name}{(String("name") is string n ? $" '{n}'" : "")}";

        /// <summary>A new record of the type with every field at its zero value: empty arrays, null pointers, zeroed tuples and inline records.</summary>
        public static HkObject Create(HkType type)
        {
            var obj = new HkObject(type);
            var fields = type.AllFields;
            for (int i = 0; i < fields.Count; i++)
                obj.Values[i] = HkValue.Default(fields[i].Type);
            return obj;
        }

        /// <summary>
        /// A copy that owns its inline records, tuples and hkArrays (so a writer gives it items of
        /// its own) and shares what its pointers point at.
        /// </summary>
        public HkObject Clone()
        {
            var copy = new HkObject(Type);
            var fields = Type.AllFields;
            for (int i = 0; i < fields.Count; i++)
                copy.Values[i] = HkValue.CloneOwned(Values[i], fields[i].Type);
            return copy;
        }
    }

    /// <summary>An hkArray (or a pointer's multi-element target): its elements and their type.</summary>
    public sealed class HkArray : List<object>
    {
        public HkType ElementType;

        /// <summary>The item this array was read from, 0 for an unset or new one.</summary>
        public int SourceItem;

        public HkArray() { }

        public HkArray(HkType elementType, int capacity)
            : base(capacity)
        {
            ElementType = elementType;
        }

        public IEnumerable<HkObject> Objects => this.OfType<HkObject>();

        /// <summary>Sets an element, converting a number to the element type's width.</summary>
        public void SetValue(int index, object value) =>
            this[index] = HkValue.Coerce(value, ElementType);

        public void AddValue(object value) => Add(HkValue.Coerce(value, ElementType));

        public HkObject ObjectAt(int index) =>
            index >= 0 && index < Count ? this[index] as HkObject : null;

        public int[] Ints() => this.Select(v => Convert.ToInt32(v)).ToArray();

        public uint[] UInts() => this.Select(v => Convert.ToUInt32(v)).ToArray();

        public float[] FloatValues() => this.Select(HkValue.ToSingle).ToArray();
    }

    /// <summary>Conversions of tagfile values into the viewer's math types.</summary>
    public static class HkValue
    {
        /// <summary>A stored number as a float; a 2 byte real is a <see cref="System.Half"/>, which
        /// <see cref="Convert"/> does not take.</summary>
        public static float ToSingle(object v) =>
            v is System.Half h ? (float)h : Convert.ToSingle(v);

        public static int ToInt32(object v) =>
            v is System.Half h ? Convert.ToInt32((float)h) : Convert.ToInt32(v);

        public static float[] Floats(object v)
        {
            switch (v)
            {
                case float[] f:
                    return f;
                case object[] parts:
                    var list = new List<float>();
                    foreach (var p in parts)
                        if (p is float single)
                            list.Add(single);
                        else
                            list.AddRange(Floats(p));
                    return list.ToArray();
                default:
                    return System.Array.Empty<float>();
            }
        }

        public static Vector4 ToVector4(object v)
        {
            var f = Floats(v);
            return new Vector4(
                f.Length > 0 ? f[0] : 0,
                f.Length > 1 ? f[1] : 0,
                f.Length > 2 ? f[2] : 0,
                f.Length > 3 ? f[3] : 0
            );
        }

        /// <summary>
        /// 16 reals in Havok's column major order (columns 0 to 3, the last the translation)
        /// as a row vector matrix: each Havok column becomes a row, w entries included.
        /// </summary>
        public static Matrix4 ToMatrix(object v)
        {
            var f = Floats(v);
            if (f.Length < 16)
                return Matrix4.Identity;
            return new Matrix4(
                f[0],
                f[1],
                f[2],
                f[3],
                f[4],
                f[5],
                f[6],
                f[7],
                f[8],
                f[9],
                f[10],
                f[11],
                f[12],
                f[13],
                f[14],
                f[15]
            );
        }

        /// <summary>A number in the CLR type a field of the given type stores; anything else unchanged.</summary>
        public static object Coerce(object value, HkType type)
        {
            if (value == null || type == null)
                return value;
            var t = type.Resolved;
            switch (t.Kind)
            {
                case HkKind.Int when value is IConvertible:
                {
                    bool signed = t.IsSigned;
                    switch (t.IntBytes)
                    {
                        case 1:
                            return signed ? Convert.ToSByte(value) : Convert.ToByte(value);
                        case 2:
                            return signed ? Convert.ToInt16(value) : Convert.ToUInt16(value);
                        case 4:
                            return signed ? Convert.ToInt32(value) : Convert.ToUInt32(value);
                        default:
                            return signed ? Convert.ToInt64(value) : Convert.ToUInt64(value);
                    }
                }
                case HkKind.Float when value is IConvertible:
                    return type.ByteSize switch
                    {
                        8 => Convert.ToDouble(value),
                        2 => (System.Half)Convert.ToSingle(value),
                        _ => Convert.ToSingle(value),
                    };
                case HkKind.Bool when value is IConvertible:
                    return Convert.ToBoolean(value);
                default:
                    return value;
            }
        }

        /// <summary>A copy of a value held in a field of the given type: pointer targets shared, everything stored in place copied.</summary>
        public static object CloneOwned(object value, HkType type)
        {
            var kind = type?.EffectiveKind ?? HkKind.Void;
            switch (value)
            {
                case HkObject o when kind == HkKind.Record:
                    return o.Clone();
                case HkArray a when kind == HkKind.Array:
                    var copy = new HkArray(a.ElementType, a.Count);
                    foreach (var element in a)
                        copy.Add(CloneOwned(element, a.ElementType));
                    return copy;
                case float[] f:
                    return (float[])f.Clone();
                case object[] parts:
                    var sub = type?.Resolved.Subtype;
                    return parts.Select(p => CloneOwned(p, sub)).ToArray();
                case byte[] raw:
                    return (byte[])raw.Clone();
                default:
                    return value;
            }
        }

        /// <summary>The zero value of a field of the given type, in the representation the reader uses.</summary>
        public static object Default(HkType declared)
        {
            if (declared == null)
                return null;
            var type = declared.Resolved;
            switch (type.Kind)
            {
                case HkKind.Int:
                    return Coerce(0, declared);
                case HkKind.Float:
                    return Coerce(0f, declared);
                case HkKind.Bool:
                    return false;
                case HkKind.Pointer:
                case HkKind.String:
                    return null;
                case HkKind.Record:
                    return HkObject.Create(declared);
                case HkKind.Array:
                {
                    int count = declared.TupleCount;
                    if (count == 0)
                        return new HkArray(type.Subtype, 0);
                    var element = type.Subtype;
                    if (element == null || element.ByteSize == 0)
                        return new float[type.ByteSize > 0 ? type.ByteSize / 4 : count];
                    if (element.EffectiveKind == HkKind.Float && element.ByteSize == 4)
                        return new float[count];
                    var parts = new object[count];
                    for (int i = 0; i < count; i++)
                        parts[i] = Default(element);
                    return parts;
                }
                default:
                {
                    int size = declared.ByteSize;
                    return size > 0 ? new byte[size] : null;
                }
            }
        }

        public static float[] FromVector(Vector4 v) => new[] { v.X, v.Y, v.Z, v.W };

        /// <summary>The inverse of <see cref="ToMatrix"/>: 16 reals in Havok's column major order.</summary>
        public static float[] FromMatrix(Matrix4 m) =>
            new[]
            {
                m.M11,
                m.M12,
                m.M13,
                m.M14,
                m.M21,
                m.M22,
                m.M23,
                m.M24,
                m.M31,
                m.M32,
                m.M33,
                m.M34,
                m.M41,
                m.M42,
                m.M43,
                m.M44,
            };

        /// <summary>The affine part of a stored matrix: w column forced to (0, 0, 0, 1), as the runtime reads it.</summary>
        public static Matrix4 Affine(Matrix4 m)
        {
            m.M14 = 0;
            m.M24 = 0;
            m.M34 = 0;
            m.M44 = 1;
            return m;
        }
    }
}
