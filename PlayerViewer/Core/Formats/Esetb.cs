using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using EffectLibrary;

namespace PlayerViewer.Core.Formats
{
    /// <summary>
    /// An Effect/*.esetb.byml: a BYML hash of {Esets: the emitter set names in ESTA order,
    /// PtclBin: the VFXB as an aligned binary node}. The VFXB is read in place from the BYML's
    /// buffer, so this object owns that buffer.
    /// </summary>
    public sealed class Esetb
    {
        public VfxFile Vfx { get; }

        /// <summary>The Esets array as stored. Saving writes the VFXB's own set names.</summary>
        public IReadOnlyList<string> StoredNames { get; }

        public ushort BymlVersion { get; }

        public uint Alignment { get; }

        Esetb(VfxFile vfx, IReadOnlyList<string> names, ushort version, uint alignment)
        {
            Vfx = vfx;
            StoredNames = names;
            BymlVersion = version;
            Alignment = alignment;
        }

        /// <summary>Reads a file, zstd compressed or not.</summary>
        public static Esetb Load(string path) => Read(Romfs.Decompress(File.ReadAllBytes(path)));

        public static Esetb Read(byte[] byml)
        {
            var doc = new Byml(byml);
            var root = Byml.AsHash(doc.Root);
            if (
                root == null
                || !root.TryGetValue("PtclBin", out var bin)
                || bin is not BymlBinary ptcl
            )
                throw new InvalidOperationException("Not an esetb: no PtclBin binary node");
            var names =
                Byml.AsArray(root.GetValueOrDefault("Esets"))?.Cast<string>().ToList()
                ?? new List<string>();
            return new Esetb(VfxFile.Read(ptcl.Data), names, doc.Version, ptcl.Alignment);
        }

        public byte[] Write()
        {
            var root = new Dictionary<string, object>
            {
                ["Esets"] = Vfx.EmitterSets.Select(x => (object)x.Name).ToList(),
                ["PtclBin"] = new BymlBinary(Vfx.Write(), Alignment),
            };
            return BymlWriter.Write(root, BymlVersion);
        }
    }
}
