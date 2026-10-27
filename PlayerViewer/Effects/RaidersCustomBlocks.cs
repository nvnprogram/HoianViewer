using System;
using System.Buffers.Binary;
using System.Numerics;
using BfresEditor;
using EffectLibrary;
using PlayerViewer.Env;
using Variant = BfresEditor.TeamColorVariants.Kind;

namespace PlayerViewer.Effects
{
    /// <summary>
    /// Raiders' Custom1, which grows to 0x1F0 with a row per team colour variant and reads a CSDP
    /// with a word inserted at 21. Its Custom0 is <see cref="Custom0Layout.Raiders"/>.
    /// </summary>
    public static class RaidersCustomBlocks
    {
        public const int Custom1Size = 0x1F0;

        /// <summary>The linear base colour and hue settings of each team, alpha, bravo, charlie.</summary>
        public readonly record struct Teams(
            Vector3 Alpha,
            Vector3 Bravo,
            Vector3 Charlie,
            TeamColorVariants.HueOffset AlphaHue,
            TeamColorVariants.HueOffset BravoHue,
            TeamColorVariants.HueOffset CharlieHue
        );

        public static byte[] BuildCustom1(Emitter emitter, in Teams teams, byte[] user0 = null)
        {
            var b = new byte[Custom1Size];
            var csdp = emitter.FindSub("CSDP");
            byte[] p =
                csdp != null && csdp.Data.Length >= 192 ? csdp.Data.ToArray() : new byte[192];
            float P(int i) => BinaryPrimitives.ReadSingleLittleEndian(p.AsSpan(4 * i));
            uint U(int i) => BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(4 * i));
            ulong flags = emitter.Section.GetU64(emitter.Map.CustomShaderFlags);

            W(b, 0x00, P(6));
            W(b, 0x04, P(7));
            W(b, 0x08, P(8));
            W(b, 0x0C, P(12));
            W(b, 0x10, P(9));
            W(b, 0x14, P(10));
            W(b, 0x18, P(11));
            W(b, 0x64, P(13));
            W(b, 0x60, P(14));
            W(b, 0x54, (float)U(15));
            W(b, 0xA0, P(16));
            W(b, 0xA4, P(17));
            W(b, 0xC0, P(19));
            W(b, 0xC4, P(20));
            W(b, 0xC8, P(21));
            W(b, 0x140, P(22));
            W(b, 0x150, P(26));
            W(b, 0x154, P(27));
            W(b, 0x158, (float)U(28));

            uint pick = U(0);
            int team =
                (pick & 2) != 0 ? 2
                : (pick & 4) != 0 ? 0
                : (pick & 8) != 0 ? 1
                : (pick & 0x10) != 0 ? 2
                : 0;

            bool u0 = user0 != null && user0.Length >= 56 * 16;
            bool night = u0 && R(user0, EnvUniforms.User0RowNight, 3) > 0.5f;
            if ((flags & 0x40) != 0)
            {
                W(b, 0x00, night ? 0.2f : 0.24f);
                W(b, 0x04, 0);
                W(b, 0x08, 0.015f);
                W(b, 0x2C, 0.1f);
                W(b, 0x4C, (flags & 0x100000000) != 0 && team == 1 ? 1.1f : 0);
            }
            if ((flags & 0x41) != 0)
            {
                W(b, 0x3C, u0 ? R(user0, EnvUniforms.User0RowInkRimCurve, 1) : 0.625f);
                W(b, 0x40, P(4));
                W(b, 0x44, P(5));
                W(b, 0x48, u0 ? R(user0, EnvUniforms.User0RowInkRimCurve, 2) : 0.875f);
            }

            var (baseColor, hue) = team switch
            {
                1 => (teams.Bravo, teams.BravoHue),
                2 => (teams.Charlie, teams.CharlieHue),
                _ => (teams.Alpha, teams.AlphaHue),
            };
            bool normalise = (U(29) & 1) != 0;
            Vector3 Of(int row)
            {
                Vector3 c = VariantOf(baseColor, row, hue);
                if (!normalise)
                    return c;
                float lum = MathF.Max(c.X * 0.2126f + c.Y * 0.7152f + c.Z * 0.0722f, 0.01f);
                return c * (P(30) / lum);
            }
            if ((flags & 1) != 0)
            {
                Vector3 ink = VariantOf(baseColor, InkRow, hue);
                if (MathF.Abs(P(3)) > 0.001f)
                    ink = TeamColorVariants.Shift(ink, true, P(3), 0, 0);
                if (normalise)
                    ink *=
                        P(30)
                        / MathF.Max(ink.X * 0.2126f + ink.Y * 0.7152f + ink.Z * 0.0722f, 0.01f);
                W3(b, 0x20, ink);
                W3(b, 0x30, Of(InkBrightRow));
            }
            else
            {
                if ((flags & 8) != 0)
                    W3(b, 0x20, Of(RowOfMask(U(1))));
                if ((flags & 0x10) != 0)
                    W3(b, 0x30, Of(RowOfMask(U(2))));
            }
            W3(b, 0x160, Of(InkRow));
            W3(b, 0x170, Of(UIEffectRow));
            W3(b, 0x180, Of(ModelRow));
            W3(b, 0x190, Of(BrightRow));
            W3(b, 0x1A0, Of(DarkRow));
            W3(b, 0x1B0, Of(HueBrightRow));
            W3(b, 0x1C0, Of(HueDarkRow));

            //The emission intensity a game actor can scale, full here.
            if ((flags & 0x100000) != 0)
                W(b, 0x90, 1f);
            W(b, 0xB0, 1f);
            if ((flags & 0x40000000000) != 0)
            {
                W(b, 0x1D0, P(31));
                W(b, 0x1D4, P(32));
                W(b, 0x1D8, P(33));
            }
            if ((flags & 0x1000000000) != 0)
            {
                W(b, 0x1E0, P(44));
                W(b, 0x1E4, P(46));
            }
            return b;
        }

        const int BrightRow = 2,
            DarkRow = 3,
            HueBrightRow = 4,
            HueDarkRow = 6,
            ModelRow = 8,
            InkRow = 9,
            InkBrightRow = 10,
            MiniMapInkRow = 14,
            UIEffectRow = 16;

        /// <summary>The variant row a CSDP colour word selects; Ink by default.</summary>
        static int RowOfMask(uint mask) =>
            mask switch
            {
                2 => UIEffectRow,
                4 => ModelRow,
                8 => BrightRow,
                16 => DarkRow,
                32 => HueBrightRow,
                64 => HueDarkRow,
                128 => MiniMapInkRow,
                _ => InkRow,
            };

        static Vector3 VariantOf(Vector3 baseColor, int row, TeamColorVariants.HueOffset hue) =>
            row switch
            {
                InkRow or 15 => baseColor,
                InkBrightRow => baseColor * CustomBlocks.BrightVariant,
                MiniMapInkRow => TeamColorVariants.Get(baseColor, Variant.MiniMapInk, hue),
                UIEffectRow => TeamColorVariants.Get(baseColor, Variant.UIEffect, hue),
                _ => TeamColorVariants.Get(baseColor, (Variant)row, hue),
            };

        static float R(byte[] src, int row, int component) =>
            BinaryPrimitives.ReadSingleLittleEndian(src.AsSpan(16 * row + 4 * component));

        static void W(byte[] b, int at, float v) =>
            BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(at), v);

        static void W3(byte[] b, int at, Vector3 v)
        {
            W(b, at, v.X);
            W(b, at + 4, v.Y);
            W(b, at + 8, v.Z);
        }
    }
}
