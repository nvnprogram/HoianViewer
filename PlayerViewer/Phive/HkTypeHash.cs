using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace PlayerViewer.Phive
{
    /// <summary>
    /// The type hash a tagfile's THSH section carries
    /// </summary>
    public sealed class HkTypeHash
    {
        readonly HkTypeCatalog _catalog;
        readonly Dictionary<string, uint> _memo = new(StringComparer.Ordinal);

        public HkTypeHash(HkTypeCatalog catalog) => _catalog = catalog;

        /// <summary>The hash of a type with a body, or null when the catalogue has no body for it.</summary>
        public uint? Calc(string sig) =>
            _catalog.Types.TryGetValue(sig, out var def) && def.Body != null ? Hash(sig) : null;

        uint Hash(string sig)
        {
            if (sig == null)
                return 0;
            if (_memo.TryGetValue(sig, out uint known))
                return known;
            //A type met again while its own hash is in progress counts as 0, as in Havok.
            _memo[sig] = 0;
            _catalog.Types.TryGetValue(sig, out var def);
            var body = def?.Body;
            uint h = body != null ? Hash(body.Parent) : 0;
            ulong opt = body?.OptBits ?? 0;
            if ((opt & HkTypeSection.OptInterfaces) != 0)
                foreach (var i in body.Interfaces)
                {
                    h = AddName(h, i.Type);
                    h = AddU32(h, (uint)i.Value);
                }
            if ((opt & HkTypeSection.OptFormat) != 0)
            {
                uint format = (uint)body.Format;
                h = AddU32(h, format);
                if ((format & 0xF) == (uint)HkKind.Array && ((format >> 5) & 3) == 1)
                    h = AddU32(h, Hash(body.Subtype));
            }
            h = AddName(h, sig);
            h = AddTemplate(h, sig);
            if ((opt & HkTypeSection.OptVersion) != 0)
                h = AddU32(h, (uint)body.Version);
            if ((opt & HkTypeSection.OptSizeAlign) != 0)
            {
                //The file splits size from alignment; the hash takes them packed as the runtime holds them.
                uint align = (uint)body.Align;
                h = AddU32(h, (uint)body.Size | ((align & 0xFFF) << 16) | ((align >> 12) << 28));
            }
            if (body != null && body.Flags != 0)
                h = AddU32(h, (uint)body.Flags);
            if ((opt & HkTypeSection.OptDecls) != 0)
                foreach (var f in body.Fields)
                {
                    h = AddString(h, f.Name);
                    h = AddU32(h, (uint)f.Offset);
                    h = AddU32(h, Hash(f.Type));
                }
            _memo[sig] = h;
            return h;
        }

        uint AddName(uint h, string sig) =>
            sig != null && _catalog.Types.TryGetValue(sig, out var def) ? AddString(h, def.Name) : h;

        uint AddTemplate(uint h, string sig)
        {
            if (sig == null || !_catalog.Types.TryGetValue(sig, out var def))
                return h;
            foreach (var t in def.Templates)
                if (HkTypeCatalog.IsTypeParam(t.Param))
                {
                    h = AddName(h, t.Type);
                    h = AddTemplate(h, t.Type);
                }
                else
                    h = AddU32(h, (uint)t.Value);
            return h;
        }

        static uint AddString(uint h, string s) => Crc(h, Encoding.UTF8.GetBytes(s ?? ""));

        static uint AddU32(uint h, uint v)
        {
            Span<byte> b = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(b, v);
            return Crc(h, b);
        }

        static readonly uint[] Table = BuildTable();

        static uint[] BuildTable()
        {
            var t = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint c = i;
                for (int k = 0; k < 8; k++)
                    c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
                t[i] = c;
            }
            return t;
        }

        static uint Crc(uint h, ReadOnlySpan<byte> data)
        {
            uint c = ~h;
            foreach (byte b in data)
                c = Table[(c ^ b) & 0xFF] ^ (c >> 8);
            return ~c;
        }
    }
}
