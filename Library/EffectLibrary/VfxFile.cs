using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;

namespace EffectLibrary
{
    /// <summary>
    /// A VFXB effect binary (the PtclBin of an esetb).
    /// </summary>
    public sealed class VfxFile
    {
        public const int HeaderSize = 0x40;
        public const ushort SupportedVersion = 46;

        /// <summary>The 0x40 byte file header. The file size at 0x1C is rewritten on save.</summary>
        public byte[] Header { get; }

        public List<VfxSection> Sections { get; }

        EffectSetList _sets;
        TextureArchive _textures;
        PrimitiveArchive _primitives;
        NativePrimitiveList _nativePrimitives;
        ShaderArchive _shaders;

        VfxFile(byte[] header, List<VfxSection> sections)
        {
            Header = header;
            Sections = sections;
        }

        public static VfxFile Read(Memory<byte> data)
        {
            var span = data.Span;
            if (span.Length < HeaderSize || !span[..8].SequenceEqual("VFXB    "u8))
                throw new InvalidOperationException("Not a VFXB file");
            var header = span[..HeaderSize].ToArray();
            ushort version = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(0x0A));
            if (version != SupportedVersion)
                throw new NotSupportedException(
                    $"VFXB version {version}; only {SupportedVersion} (Splatoon 3) is read"
                );
            int first = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(0x16));
            return new VfxFile(header, VfxLayout.ReadTop(data, first));
        }

        public byte[] Write()
        {
            int estimate = BinaryPrimitives.ReadInt32LittleEndian(Header.AsSpan(0x1C));
            var sink = new ByteSink(estimate + 0x10000);
            sink.Write(Header);
            VfxLayout.WriteTop(sink, Sections);
            sink.PatchU32(0x1C, (uint)sink.Position);
            return sink.ToArray();
        }

        public ushort Version => BinaryPrimitives.ReadUInt16LittleEndian(Header.AsSpan(0x0A));

        /// <summary>The file name stored at 0x20, such as "static.Product.b00".</summary>
        public string Name
        {
            get => FixedString.Read(Header.AsSpan(0x20, 0x20));
            set => FixedString.Write(Header.AsSpan(0x20, 0x20), value);
        }

        public VfxSection FindSection(string tag) => Sections.Find(x => x.Tag == tag);

        /// <summary>The emitter sets in ESTA order, which is also the esetb's Esets order.</summary>
        public EffectSetList EmitterSets => _sets ??= new EffectSetList(this);

        public TextureArchive Textures => _textures ??= TextureArchive.From(FindSection("GRTF"));

        public PrimitiveArchive Primitives =>
            _primitives ??= PrimitiveArchive.From(FindSection("G3PR"));

        public NativePrimitiveList NativePrimitives =>
            _nativePrimitives ??= new NativePrimitiveList(FindSection("PRMA"));

        public ShaderArchive Shaders => _shaders ??= ShaderArchive.From(FindSection("GRSN"));

        /// <summary>Every emitter, children after their parent, set by set.</summary>
        public IEnumerable<Emitter> AllEmitters => EmitterSets.SelectMany(x => x.AllEmitters);

        /// <summary>Drops the typed views so they are rebuilt from the section tree, after a
        /// structural edit (a section added, removed or replaced).</summary>
        public void Refresh()
        {
            _sets = null;
            _textures = null;
            _primitives = null;
            _nativePrimitives = null;
            _shaders = null;
        }
    }
}
