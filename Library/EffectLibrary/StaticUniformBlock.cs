using System;
using System.Buffers.Binary;

namespace EffectLibrary
{
    /// <summary>
    /// Builds what the game uploads as the static uniform block: the first 0xA90 bytes of the
    /// ResEmitter after the rewrite the runtime makes at load, the four shader flag words at 0x70
    /// included.
    /// </summary>
    public static class StaticUniformBlock
    {
        public const int FlagWords = 0x70;

        const int ColorFixed = 0;
        const int PatternRandom = 4;

        public static byte[] Build(Emitter emitter)
        {
            var b = emitter.Data.ToArray();

            //A fixed colour goes into key 0 of its table.
            if (b[0xD3C] == ColorFixed)
                for (int i = 0; i < 3; i++)
                    F(b, EmitterLayout.Color0Keys + 4 * i, F(b, 0xD40 + 4 * i));
            if (b[0xD3D] == ColorFixed)
                for (int i = 0; i < 3; i++)
                    F(b, EmitterLayout.Color1Keys + 4 * i, F(b, 0xD50 + 4 * i));
            if (b[0xD3E] == ColorFixed)
                F(b, EmitterLayout.Alpha0Keys, F(b, 0xD4C));
            if (b[0xD3F] == ColorFixed)
                F(b, EmitterLayout.Alpha1Keys, F(b, 0xD5C));

            for (int t = 0; t < EmitterLayout.SamplerCount; t++)
            {
                int anim = EmitterLayout.TextureAnims + EmitterLayout.TextureAnimStride * t;
                int shift = EmitterLayout.TexShiftAnims + EmitterLayout.TexShiftAnimStride * t;
                byte repeat = b[anim + 4];
                if (repeat <= 3)
                {
                    F(b, shift + 0x40, (repeat & 1) != 0 ? 2 : 1);
                    F(b, shift + 0x44, (repeat & 2) != 0 ? 2 : 1);
                }
                if (b[anim + 1] == 0)
                    for (int k = 0; k < 6; k++)
                        F(b, shift + 4 * k, 0);
                if (b[anim + 2] == 0)
                    for (int k = 0; k < 3; k++)
                        F(b, shift + 0x30 + 4 * k, 0);
                if (b[anim + 3] == 0)
                {
                    F(b, shift + 0x18, 0);
                    F(b, shift + 0x1C, 0);
                    F(b, shift + 0x20, 1);
                    F(b, shift + 0x24, 1);
                    F(b, shift + 0x28, 0);
                    F(b, shift + 0x2C, 0);
                }
                //A random pattern anim plays the table 0..n-1.
                if (b[anim] == PatternRandom)
                {
                    int ptn =
                        EmitterLayout.TexPatternAnims + EmitterLayout.TexPatternAnimStride * t;
                    int count = Math.Clamp((int)F(b, ptn + 8), 0, 32);
                    for (int i = 0; i < count; i++)
                        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(ptn + 0x10 + 4 * i), i);
                    F(b, ptn, count);
                }
            }

            //An axis the particle does not rotate about is zeroed in the rotation tables, and its
            //pair at 0xA50 and 0xA60 reset to 0 and 1.
            for (int axis = 0; axis < 3; axis++)
            {
                if (b[0xBF0 + axis] != 0)
                    continue;
                foreach (int table in new[] { 0xA00, 0xA10, 0xA20, 0xA30 })
                    F(b, table + 4 * axis, 0);
                F(b, 0xA50 + 4 * axis, 0);
                F(b, 0xA60 + 4 * axis, 1);
            }

            //Keys past the count repeat the last one, its time plus the key index.
            int[] tables =
            {
                EmitterLayout.Color0Keys,
                EmitterLayout.Alpha0Keys,
                EmitterLayout.Color1Keys,
                EmitterLayout.Alpha1Keys,
                EmitterLayout.ScaleKeys,
                EmitterLayout.ParamKeys,
            };
            for (int k = 0; k < tables.Length; k++)
            {
                int count = BinaryPrimitives.ReadInt32LittleEndian(
                    b.AsSpan(EmitterLayout.KeyCounts + 4 * k)
                );
                if (count <= 0 || count > 8)
                    continue;
                int last = tables[k] + 16 * (count - 1);
                for (int i = count; i < 8; i++)
                {
                    int at = tables[k] + 16 * i;
                    Array.Copy(b, last, b, at, 16);
                    F(b, at + 12, F(b, at + 12) + i);
                }
            }

            //Loop rates and random start flags come from the particle data.
            for (int k = 0; k < 5; k++)
            {
                bool loop = b[0xC18 + k] != 0;
                bool random = b[0xC1D + k] != 0;
                F(
                    b,
                    0xA0 + 4 * k,
                    loop ? BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(0xC24 + 4 * k)) : 0
                );
                F(b, 0xB4 + 4 * k, random ? 1 : 0);
            }

            var flags = BuildFlags(b, emitter);
            for (int i = 0; i < 4; i++)
                BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(FlagWords + 4 * i), flags[i]);

            return b[..EmitterLayout.StaticBlockSize];
        }

        /// <summary>The shader flag words, bit for bit as the runtime's builder sets them.</summary>
        static uint[] BuildFlags(byte[] b, Emitter emitter)
        {
            var f = new uint[4];
            if (b[0xB39] != 0)
                f[0] |= 0x8;

            //Fluctuation wave type, in the high nibble.
            int wave = b[0xD87];
            if (wave <= 0xF)
                f[0] |= 0x1;
            if (wave >> 4 == 1)
                f[0] |= 0x2;
            else if (wave >> 4 == 2)
                f[0] |= 0x4;

            //Three combiner bytes, each a nibble pair, into flag 2.
            for (int i = 0; i < 3; i++)
            {
                int v = b[0xC43 + i];
                uint shift = (uint)(3 * i);
                if (v <= 0xF)
                    f[2] |= 0x1000u << (int)shift;
                if (v >> 4 == 2)
                    f[2] |= 0x2000u << (int)shift;
                else if (v >> 4 == 4)
                    f[2] |= 0x4000u << (int)shift;
            }

            //Texture pattern anim types: fit, clamp, loop, random for each of the six slots.
            for (int t = 0; t < EmitterLayout.SamplerCount; t++)
            {
                int type = b[EmitterLayout.TextureAnims + EmitterLayout.TextureAnimStride * t];
                if (type >= 1 && type <= 4)
                    f[0] |= 0x10u << (4 * t + type - 1);
            }

            if (b[0xBED] != 0)
                f[0] |= 0x10000000;
            if (b[0xBEE] != 0)
                f[0] |= 0x20000000;
            if (b[0xBEF] != 0)
                f[0] |= 0x40000000;

            //UV inversion randoms, then the pattern loop random, per slot.
            for (int t = 0; t < EmitterLayout.SamplerCount; t++)
            {
                int anim = EmitterLayout.TextureAnims + EmitterLayout.TextureAnimStride * t;
                if (b[anim + 5] != 0)
                    f[1] |= 1u << (2 * t);
                if (b[anim + 6] != 0)
                    f[1] |= 2u << (2 * t);
                if (b[anim + 7] != 0)
                    f[1] |= 0x1000u << t;
            }

            if (b[0xBF3] != 0)
                f[1] |= 0x40000;
            f[1] |=
                b[0xBEC] == 1 ? 0x100000u
                : b[0xBEC] == 0 ? 0x80000u
                : 0;
            if (b[0xBF4] != 0)
                f[2] |= 0x1;

            //Which fields the emitter has.
            foreach (var (tag, bit) in FieldFlags)
                if (emitter.Section.FindSub(tag) != null)
                    f[2] |= bit;

            int follow = (sbyte)b[EmitterLayout.FollowType];
            if (follow >= 0 && follow <= 2)
                f[2] |= FollowFlags[follow];

            var custom = emitter.Section.FindSub("FCSF");
            if (custom != null && custom.Payload.Length >= 4)
                f[3] = custom.GetU32(0);
            return f;
        }

        static readonly (string Tag, uint Bit)[] FieldFlags =
        {
            ("FRND", 0x2),
            ("FPAD", 0x4),
            ("FMAG", 0x8),
            ("FCOV", 0x10),
            ("FSPN", 0x20),
            ("FCOL", 0x40),
            ("FCLN", 0x80),
            ("FRN1", 0x100),
        };

        static readonly uint[] FollowFlags = { 0x200, 0x800, 0x400 };

        static float F(byte[] b, int at) => BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(at));

        static void F(byte[] b, int at, float value) =>
            BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(at), value);
    }
}
