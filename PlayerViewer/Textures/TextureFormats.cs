using System;
using System.Collections.Generic;
using System.Linq;
using BCnEncoder.Shared;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Syroot.NintenTools.NSW.Bntx.GFX;

namespace PlayerViewer.Textures
{
    /// <summary>
    /// The surface formats an import may write, and which one to offer for a given sampler.
    /// </summary>
    public sealed class TextureFormat
    {
        public string Name;
        public SurfaceFormat Surface;
        public Toolbox.Core.TexFormat Tex;

        /// <summary>Null for the uncompressed formats, which are written as they are.</summary>
        public CompressionFormat? Compression;

        /// <summary>Endpoints are signed, so a block encoded unsigned needs its two endpoint
        /// bytes reinterpreted.</summary>
        public bool Signed;

        public bool Srgb => Name.EndsWith("sRGB", StringComparison.Ordinal);

        public override string ToString() => Name;
    }

    public static class TextureFormats
    {
        public static readonly TextureFormat[] All = new[]
        {
            Make(
                "BC1 sRGB",
                SurfaceFormat.BC1_SRGB,
                Toolbox.Core.TexFormat.BC1_SRGB,
                CompressionFormat.Bc1
            ),
            Make(
                "BC1 UNORM",
                SurfaceFormat.BC1_UNORM,
                Toolbox.Core.TexFormat.BC1_UNORM,
                CompressionFormat.Bc1
            ),
            Make(
                "BC3 sRGB",
                SurfaceFormat.BC3_SRGB,
                Toolbox.Core.TexFormat.BC3_SRGB,
                CompressionFormat.Bc3
            ),
            Make(
                "BC3 UNORM",
                SurfaceFormat.BC3_UNORM,
                Toolbox.Core.TexFormat.BC3_UNORM,
                CompressionFormat.Bc3
            ),
            Make(
                "BC4 UNORM",
                SurfaceFormat.BC4_UNORM,
                Toolbox.Core.TexFormat.BC4_UNORM,
                CompressionFormat.Bc4
            ),
            Make(
                "BC4 SNORM",
                SurfaceFormat.BC4_SNORM,
                Toolbox.Core.TexFormat.BC4_SNORM,
                CompressionFormat.Bc4,
                signed: true
            ),
            Make(
                "BC5 UNORM",
                SurfaceFormat.BC5_UNORM,
                Toolbox.Core.TexFormat.BC5_UNORM,
                CompressionFormat.Bc5
            ),
            Make(
                "BC5 SNORM",
                SurfaceFormat.BC5_SNORM,
                Toolbox.Core.TexFormat.BC5_SNORM,
                CompressionFormat.Bc5,
                signed: true
            ),
            Make(
                "BC7 sRGB",
                SurfaceFormat.BC7_SRGB,
                Toolbox.Core.TexFormat.BC7_SRGB,
                CompressionFormat.Bc7
            ),
            Make(
                "BC7 UNORM",
                SurfaceFormat.BC7_UNORM,
                Toolbox.Core.TexFormat.BC7_UNORM,
                CompressionFormat.Bc7
            ),
            Make(
                "RGBA8 sRGB",
                SurfaceFormat.R8_G8_B8_A8_SRGB,
                Toolbox.Core.TexFormat.RGBA8_SRGB,
                null
            ),
            Make(
                "RGBA8 UNORM",
                SurfaceFormat.R8_G8_B8_A8_UNORM,
                Toolbox.Core.TexFormat.RGBA8_UNORM,
                null
            ),
        };

        static TextureFormat Make(
            string name,
            SurfaceFormat surface,
            Toolbox.Core.TexFormat tex,
            CompressionFormat? compression,
            bool signed = false
        ) =>
            new TextureFormat
            {
                Name = name,
                Surface = surface,
                Tex = tex,
                Compression = compression,
                Signed = signed,
            };

        public static TextureFormat Bc1Srgb => Find(SurfaceFormat.BC1_SRGB);

        public static TextureFormat Find(SurfaceFormat surface) =>
            All.FirstOrDefault(x => x.Surface == surface) ?? Bc1Srgb;

        /// <summary>The format a texture already in the file was stored as, or null when it is
        /// something this list does not offer.</summary>
        public static TextureFormat Match(SurfaceFormat surface) =>
            All.FirstOrDefault(x => x.Surface == surface);

        public static TextureFormat Match(Toolbox.Core.TexFormat tex) =>
            All.FirstOrDefault(x => x.Tex == tex);

        /// <summary>Resolves a name a transfer file recorded, which is a
        /// <see cref="Toolbox.Core.TexFormat"/> spelling.</summary>
        public static TextureFormat FromTexName(string name) =>
            name == null
                ? null
                : All.FirstOrDefault(x =>
                    string.Equals(x.Tex.ToString(), name, StringComparison.OrdinalIgnoreCase)
                );

        /// <summary>
        /// The format the shipped textures mostly use for each shader sampler.
        /// </summary>
        static readonly Dictionary<string, SurfaceFormat> ByAssign = new Dictionary<
            string,
            SurfaceFormat
        >(StringComparer.Ordinal)
        {
            { "_a0", SurfaceFormat.BC1_SRGB },
            { "_n0", SurfaceFormat.BC5_SNORM },
            { "_r0", SurfaceFormat.BC4_UNORM },
            { "_m0", SurfaceFormat.BC4_UNORM },
            { "_op0", SurfaceFormat.BC4_UNORM },
            { "_ao0", SurfaceFormat.BC4_UNORM },
            { "_cp0", SurfaceFormat.BC4_UNORM },
            { "_su0", SurfaceFormat.BC4_UNORM },
            { "_fm0", SurfaceFormat.BC4_UNORM },
            { "_re2", SurfaceFormat.BC4_UNORM },
            { "_t0", SurfaceFormat.BC1_SRGB },
            { "_b0", SurfaceFormat.BC1_SRGB },
            { "_b1", SurfaceFormat.BC1_SRGB },
            { "_pw0", SurfaceFormat.BC1_SRGB },
        };

        /// <summary>
        /// The channel mapping the shipped textures of a format carry, which a new texture
        /// gets. One channel formats repeat red into all four.
        /// </summary>
        public static ChannelType[] Channels(TextureFormat format)
        {
            const ChannelType R = ChannelType.Red,
                G = ChannelType.Green;
            switch (Stored(format))
            {
                case 1:
                    return new[] { R, R, R, R };
                case 2 when format.Signed:
                    return new[] { R, G, ChannelType.Zero, ChannelType.One };
                case 2:
                    return new[] { R, G, ChannelType.One, ChannelType.One };
                default:
                    return new[] { R, G, ChannelType.Blue, ChannelType.Alpha };
            }
        }

        /// <summary>Whether a texture of this format stores every channel the mapping reads.</summary>
        public static bool Holds(TextureFormat format, IEnumerable<ChannelType> mapping)
        {
            int stored = Stored(format);
            return mapping.All(c =>
                c switch
                {
                    ChannelType.Green => stored >= 2,
                    ChannelType.Blue => stored >= 3,
                    ChannelType.Alpha => stored >= 4,
                    _ => true,
                }
            );
        }

        static int Stored(TextureFormat format) =>
            format.Compression switch
            {
                CompressionFormat.Bc4 => 1,
                CompressionFormat.Bc5 => 2,
                _ => 4,
            };

        /// <summary>
        /// What to import as
        /// </summary>
        public static TextureFormat Suggest(string assignKey, Image<Rgba32> image)
        {
            if (assignKey != null && ByAssign.TryGetValue(assignKey, out var known))
                return Find(known);

            if (image == null)
                return Bc1Srgb;

            bool grey = true,
                opaque = true;
            image.ProcessPixelRows(rows =>
            {
                for (int y = 0; y < rows.Height && (grey || opaque); y++)
                {
                    var row = rows.GetRowSpan(y);
                    for (int x = 0; x < row.Length; x++)
                    {
                        var p = row[x];
                        if (p.R != p.G || p.G != p.B)
                            grey = false;
                        if (p.A != 255)
                            opaque = false;
                        if (!grey && !opaque)
                            break;
                    }
                }
            });

            if (grey)
                return Find(SurfaceFormat.BC4_UNORM);
            return opaque ? Bc1Srgb : Find(SurfaceFormat.BC3_SRGB);
        }
    }
}
