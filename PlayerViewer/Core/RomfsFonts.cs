using System;
using System.IO;
using PlayerViewer.Core.Formats;

namespace PlayerViewer.Core
{
    /// <summary>
    /// The game's own text fonts out of <c>romfs/Font</c>, as plain TrueType or OpenType bytes:
    /// BlitzMain, and the CJK font the game pairs it with for a language.
    /// </summary>
    public static class RomfsFonts
    {
        public sealed class Face
        {
            public string Name;
            public byte[] Data;

            //Size relative to BlitzMain, from the pairing file.
            public float Scale = 1;
        }

        /// <summary>BlitzMain, which carries Latin, Cyrillic and kana but no kanji or Hangul.</summary>
        public static Face Blitz(Romfs romfs) => Load(romfs, "Font", "BlitzMain", 1);

        /// <summary>
        /// The font BlitzMain falls back to for a language, at the scale the game gives it
        /// in that archive's <c>fcpx/BlitzMain_S.bfcpx</c>. Every language without an
        /// archive of its own uses the common one's Japanese font.
        /// </summary>
        public static Face Fallback(Romfs romfs, string language) =>
            language switch
            {
                "CNzh" => Load(romfs, "Font_CNzh", "DFP_GBZY7", 0.8f),
                "KRko" => Load(romfs, "Font_KRko", "AsiaKERIN-M", 1.1f),
                "TWzh" => Load(romfs, "Font_TWzh", "DFPT_AZ5", 0.85f),
                _ => Load(romfs, "Font", "FOT-KurokaneStd-EB", 0.8f),
            };

        static Face Load(Romfs romfs, string archive, string font, float scale)
        {
            try
            {
                var data = romfs?.ReadFile($"Font/{archive}.Nin_NX_NVN.bfarc");
                if (data == null)
                    return null;
                var sarc = new Sarc(data);
                string path = sarc.FindFile(p =>
                    Path.GetFileNameWithoutExtension(p) == font
                    && (p.EndsWith(".bfttf") || p.EndsWith(".bfotf"))
                );
                var bytes = path != null ? Decrypt(sarc.GetFile(path)) : null;
                return bytes != null
                    ? new Face
                    {
                        Name = font,
                        Data = bytes,
                        Scale = scale,
                    }
                    : null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Fonts] {archive}/{font}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// A bfttf or bfotf is the font XORed with a 32 bit key, behind an 8 byte header of
        /// the XORed magic and size.
        /// </summary>
        public static byte[] Decrypt(byte[] file)
        {
            if (file == null || file.Length < 12)
                return null;
            ReadOnlySpan<byte> magic = stackalloc byte[] { 0x7F, 0x9A, 0x02, 0x18 };
            Span<byte> key = stackalloc byte[4];
            for (int i = 0; i < 4; i++)
                key[i] = (byte)(file[i] ^ magic[i]);
            int size =
                (file[4] ^ key[0]) << 24
                | (file[5] ^ key[1]) << 16
                | (file[6] ^ key[2]) << 8
                | (file[7] ^ key[3]);
            if (size <= 0 || size > file.Length - 8)
                return null;
            var font = new byte[size];
            for (int i = 0; i < size; i++)
                font[i] = (byte)(file[8 + i] ^ key[i & 3]);
            //TrueType, or OpenType with CFF outlines.
            bool valid =
                (font[0] == 0 && font[1] == 1 && font[2] == 0 && font[3] == 0)
                || (font[0] == 'O' && font[1] == 'T' && font[2] == 'T' && font[3] == 'O');
            return valid ? font : null;
        }

        /// <summary>
        /// Units per em and the hhea ascender and descender, which is what a rasteriser sizes
        /// a font by. Null for a file whose tables cannot be found.
        /// </summary>
        public static (int UnitsPerEm, int Ascent, int Descent)? Metrics(byte[] font)
        {
            static int U16(byte[] b, int at) => b[at] << 8 | b[at + 1];
            static int S16(byte[] b, int at) => (short)U16(b, at);
            if (font == null || font.Length < 16)
                return null;
            int dir = 0;
            if (font[0] == 't' && font[1] == 't' && font[2] == 'c' && font[3] == 'f')
                dir = font[12] << 24 | font[13] << 16 | font[14] << 8 | font[15];
            if (dir < 0 || dir + 12 > font.Length)
                return null;
            int tables = U16(font, dir + 4);
            int head = -1,
                hhea = -1;
            for (int i = 0; i < tables && dir + 12 + i * 16 + 16 <= font.Length; i++)
            {
                int entry = dir + 12 + i * 16;
                string tag = System.Text.Encoding.ASCII.GetString(font, entry, 4);
                int offset =
                    font[entry + 8] << 24
                    | font[entry + 9] << 16
                    | font[entry + 10] << 8
                    | font[entry + 11];
                if (tag == "head")
                    head = offset;
                else if (tag == "hhea")
                    hhea = offset;
            }
            if (head < 0 || hhea < 0 || head + 20 > font.Length || hhea + 8 > font.Length)
                return null;
            return (U16(font, head + 18), S16(font, hhea + 4), S16(font, hhea + 6));
        }
    }
}
