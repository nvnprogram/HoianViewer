using PlayerViewer.Core;
using Syroot.NintenTools.NSW.Bntx;

namespace PlayerViewer.Textures
{
    /// <summary>The order of the 16 byte spans inside a 512 byte GOB.</summary>
    public enum GobLayout
    {
        Tegra,
        Xbar,
    }

    /// <summary>
    /// What a BNTX carries per platform: the target tag, the last part of the file version, the
    /// packager version and access flags of its textures, and the GOB layout of their images.
    /// </summary>
    public sealed class BntxPlatform
    {
        public static readonly BntxPlatform Switch = new("NX  ", 0, 0x10007, 0x20, GobLayout.Tegra);
        public static readonly BntxPlatform Ounce = new("Ounc", 1, 0x100C9, 0x21, GobLayout.Xbar);

        public string Tag { get; }
        public byte Revision { get; }
        public uint PackagerVersion { get; }
        public uint AccessFlags { get; }
        public GobLayout Gob { get; }

        BntxPlatform(string tag, byte revision, uint packager, uint access, GobLayout gob)
        {
            Tag = tag;
            Revision = revision;
            PackagerVersion = packager;
            AccessFlags = access;
            Gob = gob;
        }

        public static BntxPlatform Of(string tag) => tag == Ounce.Tag ? Ounce : Switch;

        public static BntxPlatform Of(BntxFile bntx) => Of(bntx?.PlatformTarget);

        public static BntxPlatform Of(GameKind game) => game == GameKind.Spiral ? Ounce : Switch;

        public override string ToString() => Tag.Trim();
    }

    /// <summary>
    /// Moves a block linear image between the GOB layouts. The GOBs sit at the same offsets in
    /// both; only the spans inside each one move.
    /// </summary>
    public static class GobOrder
    {
        static readonly int[] TegraToXbar = Build();
        static readonly int[] XbarToTegra = Invert(TegraToXbar);

        static int[] Build()
        {
            var tegra = SpanOf(GobLayout.Tegra);
            var xbar = SpanOf(GobLayout.Xbar);
            var map = new int[32];
            for (int span = 0; span < 32; span++)
                map[tegra[span]] = xbar[span];
            return map;
        }

        //Where each span of a one GOB linear surface lands, read off the swizzle itself.
        static int[] SpanOf(GobLayout layout)
        {
            var linear = new byte[512];
            for (int i = 0; i < linear.Length; i++)
                linear[i] = (byte)(i / 16);
            var tiled = Toolbox.Core.Switch.TegraX1Swizzle.swizzle(
                4,
                8,
                1,
                1,
                1,
                1,
                1,
                16,
                0,
                0,
                linear,
                layout == GobLayout.Xbar
            );
            var at = new int[32];
            for (int span = 0; span < 32; span++)
                at[tiled[span * 16]] = span;
            return at;
        }

        static int[] Invert(int[] map)
        {
            var inverse = new int[map.Length];
            for (int i = 0; i < map.Length; i++)
                inverse[map[i]] = i;
            return inverse;
        }

        /// <summary>Reorders every whole GOB in place.</summary>
        public static void Reorder(byte[] data, GobLayout from, GobLayout to)
        {
            if (from == to)
                return;
            var map = to == GobLayout.Xbar ? TegraToXbar : XbarToTegra;
            var gob = new byte[512];
            for (int at = 0; at + 512 <= data.Length; at += 512)
            {
                System.Buffer.BlockCopy(data, at, gob, 0, 512);
                for (int span = 0; span < 32; span++)
                    System.Buffer.BlockCopy(gob, span * 16, data, at + map[span] * 16, 16);
            }
        }
    }
}
