using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using ShaderLibrary;

namespace ShaderBundler
{
    /// <summary>Where one game keeps its debug ubershader.</summary>
    public sealed record UberSource(string SarcName, string EntryName, string ModelName);

    /// <summary>
    /// The debug ubershader, read out of the sample sarc in a romfs Shader folder.
    /// </summary>
    public static class UberArchive
    {
        public const string SarcName = "Sample.Nin_NX_NVN.release.sarc";
        public const string EntryName = "Hoian_UBER.Nin_NX_NVN.bfsha";
        public const string ModelName = "hoian_uber";

        /// <summary>Splatoon 3's.</summary>
        public static readonly UberSource Nvn = new(SarcName, EntryName, ModelName);

        /// <summary>Splatoon Raiders'</summary>
        public static readonly UberSource Nvn2 = new(
            "Sample.Nin_Ounce_NVN2.release.sarc",
            "Hoian_UBER.Nin_Ounce_NVN2.bfsha",
            "hoian_uber"
        );

        /// <summary>Options the ubershader has no code for. A vector that sets one of them
        /// away from its default gets a shader that silently does something else.</summary>
        public static readonly string[] Inexpressible =
        {
            "enable_vat",
            "enable_instancing_skinning",
            "vertex_expression0",
            "pixel_expression0",
        };

        /// <summary>Loads the ubershader archive out of a romfs Shader folder.</summary>
        public static BfshaFile Load(string shaderDirectory)
        {
            string sarc = Path.Combine(shaderDirectory, SarcName);
            if (!File.Exists(sarc) && File.Exists(sarc + ".zs"))
                sarc += ".zs";
            if (!File.Exists(sarc))
                throw new FileNotFoundException(
                    $"no {SarcName} in {shaderDirectory}; the ubershader lives inside it."
                );
            return LoadFile(sarc);
        }

        /// <summary>Loads the ubershader archive out of the given sarc, plain or zstd.</summary>
        public static BfshaFile LoadFile(string sarcPath) => LoadFile(sarcPath, EntryName);

        /// <summary>Loads the named archive out of the given sarc, plain or zstd.</summary>
        public static BfshaFile LoadFile(string sarcPath, string entryName)
        {
            if (!File.Exists(sarcPath))
                throw new FileNotFoundException($"no such sarc: {sarcPath}");
            var files = ReadSarc(Decompress(File.ReadAllBytes(sarcPath)));
            if (!files.TryGetValue(entryName, out var entry))
                throw new InvalidOperationException($"{sarcPath} has no {entryName}");
            return new BfshaFile(new MemoryStream(entry));
        }

        static byte[] Decompress(byte[] data)
        {
            if (data.Length < 4 || BitConverter.ToUInt32(data, 0) != 0xFD2FB528)
                return data;
            using var decompressor = new ZstdSharp.Decompressor();
            return decompressor.Unwrap(data).ToArray();
        }

        //Little endian SARC, enough of it to pull one named entry.
        static Dictionary<string, byte[]> ReadSarc(byte[] data)
        {
            if (
                data.Length < 0x20
                || data[0] != 'S'
                || data[1] != 'A'
                || data[2] != 'R'
                || data[3] != 'C'
            )
                throw new InvalidDataException("not a SARC archive");

            uint dataOffset = BitConverter.ToUInt32(data, 0x0C);
            int sfat = 0x14;
            ushort nodeCount = BitConverter.ToUInt16(data, sfat + 6);
            int nodes = sfat + 0x0C;
            int names = nodes + nodeCount * 0x10 + 8;
            if (names > data.Length || dataOffset > data.Length)
                throw new InvalidDataException("truncated SARC: the node table runs past the end");

            var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < nodeCount; i++)
            {
                int node = nodes + i * 0x10;
                uint attrs = BitConverter.ToUInt32(data, node + 4);
                uint start = BitConverter.ToUInt32(data, node + 8);
                uint end = BitConverter.ToUInt32(data, node + 12);
                if ((attrs & 0x01000000) == 0)
                    continue;

                long nameOffset = names + (long)(attrs & 0xFFFF) * 4;
                if (nameOffset >= data.Length)
                    throw new InvalidDataException(
                        $"truncated SARC: name of entry {i} is past the end"
                    );
                int nameEnd = (int)nameOffset;
                while (nameEnd < data.Length && data[nameEnd] != 0)
                    nameEnd++;
                if (nameEnd >= data.Length)
                    throw new InvalidDataException(
                        $"truncated SARC: name of entry {i} is unterminated"
                    );
                string name = Encoding.UTF8.GetString(
                    data,
                    (int)nameOffset,
                    nameEnd - (int)nameOffset
                );

                long first = (long)dataOffset + start;
                long last = (long)dataOffset + end;
                if (end < start || last > data.Length)
                    throw new InvalidDataException($"truncated SARC: '{name}' runs past the end");
                var bytes = new byte[end - start];
                Array.Copy(data, first, bytes, 0, bytes.Length);
                files[name] = bytes;
            }
            return files;
        }
    }
}
