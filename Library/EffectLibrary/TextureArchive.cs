using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Syroot.NintenTools.NSW.Bntx;

namespace EffectLibrary
{
    /// <summary>One GTNT record: the guid emitters name a texture by, and its BNTX name.</summary>
    public sealed class TextureEntry
    {
        public int Index { get; init; }
        public ulong Id { get; init; }
        public string Name { get; init; }

        public override string ToString() => $"{Name} ({Id:X})";
    }

    /// <summary>
    /// GRTF: the effect textures as one BNTX, with the GTNT table that maps a sampler's guid to a
    /// texture name.
    /// </summary>
    public sealed class TextureArchive
    {
        /// <summary>The format word of one 16 bit unsigned channel, which Syroot's strict reader
        /// rejects. The effect files use it for vertex animation frame tables.</summary>
        public const uint FormatR16Uint = 0x0A03;

        /// <summary>The defined format with the same size and layout, which the reader is given
        /// in its place.</summary>
        public const uint FormatR16Unorm = 0x0A01;

        public VfxSection Section { get; }
        public VfxSection Table { get; }
        public IReadOnlyList<TextureEntry> Entries { get; }

        readonly Dictionary<ulong, TextureEntry> _byId = new();
        BntxFile _bntx;
        HashSet<string> _r16Uint;
        Exception _loadError;

        TextureArchive(VfxSection section)
        {
            Section = section;
            Table = section.FindChild("GTNT");
            Entries = Table == null ? Array.Empty<TextureEntry>() : ReadTable(Table.Data);
            foreach (var entry in Entries)
                _byId.TryAdd(entry.Id, entry);
        }

        internal static TextureArchive From(VfxSection section) =>
            section == null ? null : new TextureArchive(section);

        /// <summary>Records of {u64 guid, u32 offset to the next record or 0, u32 name length
        /// counting the terminator, the name}.</summary>
        static List<TextureEntry> ReadTable(ReadOnlySpan<byte> data)
        {
            var list = new List<TextureEntry>();
            int at = 0;
            while (at + 16 <= data.Length)
            {
                ulong id = BinaryPrimitives.ReadUInt64LittleEndian(data[at..]);
                uint next = BinaryPrimitives.ReadUInt32LittleEndian(data[(at + 8)..]);
                int length = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[(at + 12)..]);
                string name = FixedString.Read(data.Slice(at + 16, length));
                list.Add(
                    new TextureEntry
                    {
                        Index = list.Count,
                        Id = id,
                        Name = name,
                    }
                );
                if (next == 0)
                    break;
                at += (int)next;
            }
            return list;
        }

        public TextureEntry Find(ulong id) => _byId.GetValueOrDefault(id);

        public Memory<byte> BntxBytes => Section.Payload;

        /// <summary>Why the BNTX could not be parsed, once a load has been tried.</summary>
        public Exception LoadError => _loadError;

        /// <summary>
        /// The parsed BNTX, or null when it does not parse. R16_UINT textures come back declared
        /// as R16_UNORM, which has the same bytes; <see cref="IsR16Uint"/> says which they are.
        /// </summary>
        public BntxFile Bntx
        {
            get
            {
                if (_bntx != null || _loadError != null)
                    return _bntx;
                try
                {
                    var unknown = FindR16Uint(BntxBytes.Span);
                    _r16Uint = unknown.Select(x => x.Name).ToHashSet(StringComparer.Ordinal);
                    Stream stream = Streams.Open(BntxBytes);
                    if (unknown.Count > 0)
                    {
                        var copy = BntxBytes.ToArray();
                        foreach (var (at, _) in unknown)
                            BinaryPrimitives.WriteUInt32LittleEndian(
                                copy.AsSpan(at),
                                FormatR16Unorm
                            );
                        stream = new MemoryStream(copy, false);
                    }
                    _bntx = new BntxFile(stream);
                }
                catch (Exception e)
                {
                    _loadError = e;
                }
                return _bntx;
            }
        }

        public bool IsR16Uint(string textureName) => Bntx != null && _r16Uint.Contains(textureName);

        /// <summary>The BRTI format word as the file has it: the high byte is the channel
        /// layout, the low byte the type (1 unorm, 2 snorm, 3 uint, 5 float, 6 srgb).</summary>
        public uint GetFileFormat(Texture texture) =>
            IsR16Uint(texture.Name) ? FormatR16Uint : Convert.ToUInt32(texture.Format);

        /// <summary>
        /// A name for a file format word. Syroot's enum names some of the effect formats wrongly:
        /// it has R16_UINT as 0x0A05 and calls 0x1505, which is R16_G16_B16_A16_FLOAT, a depth
        /// format.
        /// </summary>
        public static string FormatName(uint format) =>
            format switch
            {
                FormatR16Uint => "R16_UINT",
                0x1505 => "R16_G16_B16_A16_FLOAT",
                _ => Enum.GetName(typeof(Syroot.NintenTools.NSW.Bntx.GFX.SurfaceFormat), format)
                    ?? $"0x{format:X4}",
            };

        public Texture GetTexture(TextureEntry entry) =>
            entry == null ? null : Bntx?.Textures.FirstOrDefault(x => x.Name == entry.Name);

        public Texture GetTexture(ulong id) => GetTexture(Find(id));

        /// <summary>
        /// Where the R16_UINT textures keep their format word, and their names. The copy the
        /// reader is given says R16_UNORM there instead.
        /// </summary>
        static List<(int FormatAt, string Name)> FindR16Uint(ReadOnlySpan<byte> s)
        {
            var found = new List<(int, string)>();
            //NX header at 0x20: "NX  ", u32 count, u64 offset of the texture pointer array.
            if (s.Length < 0x30 || !s.Slice(0x20, 4).SequenceEqual("NX  "u8))
                return found;
            int count = BinaryPrimitives.ReadInt32LittleEndian(s[0x24..]);
            long table = BinaryPrimitives.ReadInt64LittleEndian(s[0x28..]);
            for (int i = 0; i < count; i++)
            {
                int brti = (int)BinaryPrimitives.ReadInt64LittleEndian(s[(int)(table + 8 * i)..]);
                if (!s.Slice(brti, 4).SequenceEqual("BRTI"u8))
                    continue;
                if (BinaryPrimitives.ReadUInt32LittleEndian(s[(brti + 0x1C)..]) != FormatR16Uint)
                    continue;
                //BRTI +0x60 holds the offset of the name: a u16 length and the characters.
                int nameAt = (int)BinaryPrimitives.ReadInt64LittleEndian(s[(brti + 0x60)..]);
                int length = BinaryPrimitives.ReadUInt16LittleEndian(s[nameAt..]);
                found.Add(
                    (brti + 0x1C, System.Text.Encoding.UTF8.GetString(s.Slice(nameAt + 2, length)))
                );
            }
            return found;
        }
    }
}
