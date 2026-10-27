using System;
using System.Buffers.Binary;

namespace EffectLibrary
{
    /// <summary>
    /// Builds what the game uploads as the static uniform block: the first 0xA90 bytes of the
    /// ResEmitter (0xCC0 in v54) after the rewrite the runtime makes at load, the four shader
    /// flag words at 0x70 included.
    /// </summary>
    public static class StaticUniformBlock
    {
        public const int FlagWords = 0x70;

        const int ColorFixed = 0;
        const int PatternRandom = 4;

        public static byte[] Build(Emitter emitter)
        {
            var m = emitter.Map;
            if (m.Version != VfxFile.SwitchV46 && m.Version != VfxFile.OunceV54)
                throw new NotSupportedException(
                    $"The load time rewrite is not known for v{m.Version} emitters"
                );
            bool v54 = !m.IsV46;
            var b = emitter.Data.ToArray();
            int A(int v46Offset) => m.At(v46Offset);

            //A fixed colour goes into key 0 of its table.
            if (b[A(0xD3C)] == ColorFixed)
                for (int i = 0; i < 3; i++)
                    F(b, A(EmitterLayout.Color0Keys) + 4 * i, F(b, A(0xD40) + 4 * i));
            if (b[A(0xD3D)] == ColorFixed)
                for (int i = 0; i < 3; i++)
                    F(b, A(EmitterLayout.Color1Keys) + 4 * i, F(b, A(0xD50) + 4 * i));
            if (b[A(0xD3E)] == ColorFixed)
                F(b, A(EmitterLayout.Alpha0Keys), F(b, A(0xD4C)));
            if (b[A(0xD3F)] == ColorFixed)
                F(b, A(EmitterLayout.Alpha1Keys), F(b, A(0xD5C)));

            for (int t = 0; t < m.SamplerCount; t++)
            {
                int anim = m.TextureAnims + m.TextureAnimStride * t;
                int shift = A(EmitterLayout.TexShiftAnims) + EmitterLayout.TexShiftAnimStride * t;
                int ptn = A(EmitterLayout.TexPatternAnims) + EmitterLayout.TexPatternAnimStride * t;
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
                    int count = Math.Clamp((int)F(b, ptn + 8), 0, 32);
                    for (int i = 0; i < count; i++)
                        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(ptn + 0x10 + 4 * i), i);
                    F(b, ptn, count);
                }
                //The array pattern types take their pattern count from the texture.
                if (b[anim] == 5 || v54 && b[anim] == 6)
                {
                    var texture = emitter.Set.File.Textures?.GetTexture(emitter.ResolveTexture(t));
                    if (texture != null)
                        F(b, ptn, (int)texture.ArrayLength);
                }
            }

            //An axis the particle does not rotate about is zeroed in the rotation tables and
            //waves, and its pair at 0xA50 and 0xA60 reset to 0 and 1.
            for (int axis = 0; axis < 3; axis++)
            {
                if (b[A(0xBF0) + axis] != 0)
                    continue;
                foreach (int table in new[] { 0xA00, 0xA10, 0xA20, 0xA30, 0xA70, 0xA80 })
                    F(b, A(table) + 4 * axis, 0);
                F(b, A(0xA50) + 4 * axis, 0);
                F(b, A(0xA60) + 4 * axis, 1);
            }

            //Keys past the count repeat the last one, its time plus the key index.
            foreach (var table in m.KeyTables)
                PadKeys(b, table.CountAt, table.Table, table.MinKeys);

            //Loop rates and random start flags come from the particle data.
            for (int k = 0; k < m.LoopSlots; k++)
            {
                F(b, m.LoopFrames[k], b[m.LoopFlags + k] != 0 ? m.LoopRate(b, k) : 0);
                F(b, m.LoopRandomStart[k], b[m.LoopRandomFlags + k] != 0 ? 1 : 0);
            }

            var flags = BuildFlags(b, emitter);
            for (int i = 0; i < 4; i++)
                BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(FlagWords + 4 * i), flags[i]);

            return b[..m.StaticBlockSize];
        }

        /// <summary>Repeats the last key past the count, when the count is from <paramref name="min"/> to 7.</summary>
        static void PadKeys(byte[] b, int countAt, int table, int min)
        {
            int count = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(countAt));
            if (count < min || count > 8)
                return;
            int last = table + 16 * (count - 1);
            for (int i = count; i < 8; i++)
            {
                int at = table + 16 * i;
                Array.Copy(b, last, b, at, 16);
                F(b, at + 12, F(b, at + 12) + i);
            }
        }

        /// <summary>The shader flag words, bit for bit as the runtime's builder sets them.</summary>
        static uint[] BuildFlags(byte[] b, Emitter emitter)
        {
            var m = emitter.Map;
            int A(int v46Offset) => m.At(v46Offset);
            var f = new uint[4];
            if (b[A(0xB39)] != 0)
                f[0] |= 0x8;

            //Fluctuation wave type, in the high nibble.
            int wave = b[A(0xD87)];
            if (wave <= 0xF)
                f[0] |= 0x1;
            if (wave >> 4 == 1)
                f[0] |= 0x2;
            else if (wave >> 4 == 2)
                f[0] |= 0x4;

            //Three combiner bytes, each a nibble pair, into flag 2.
            for (int i = 0; i < 3; i++)
            {
                int v = b[A(0xC43) + i];
                uint shift = (uint)(3 * i);
                if (v <= 0xF)
                    f[2] |= 0x1000u << (int)shift;
                if (v >> 4 == 2)
                    f[2] |= 0x2000u << (int)shift;
                else if (v >> 4 == 4)
                    f[2] |= 0x4000u << (int)shift;
            }

            //Texture pattern anim types: fit, clamp, loop, random for each of the six slots.
            for (int t = 0; t < m.SamplerCount; t++)
            {
                int type = b[m.TextureAnims + m.TextureAnimStride * t];
                if (type >= 1 && type <= 4)
                    f[0] |= 0x10u << (4 * t + type - 1);
                if (type == 6 && !m.IsV46)
                    f[1] |= 0x200000u << t;
            }

            if (b[A(0xBED)] != 0)
                f[0] |= 0x10000000;
            if (b[A(0xBEE)] != 0)
                f[0] |= 0x20000000;
            if (b[A(0xBEF)] != 0)
                f[0] |= 0x40000000;

            //UV inversion randoms, then the pattern loop random, per slot.
            for (int t = 0; t < m.SamplerCount; t++)
            {
                int anim = m.TextureAnims + m.TextureAnimStride * t;
                if (b[anim + 5] != 0)
                    f[1] |= 1u << (2 * t);
                if (b[anim + 6] != 0)
                    f[1] |= 2u << (2 * t);
                if (b[anim + 7] != 0)
                    f[1] |= 0x1000u << t;
            }

            if (b[A(0xBF3)] != 0)
                f[1] |= 0x40000;
            f[1] |=
                b[A(0xBEC)] == 1 ? 0x100000u
                : b[A(0xBEC)] == 0 ? 0x80000u
                : 0;
            if (b[A(0xBF4)] != 0)
                f[2] |= 0x1;

            //Which fields the emitter has.
            foreach (var (tag, bit) in FieldFlags)
                if (emitter.Section.FindSub(tag) != null)
                    f[2] |= bit;

            int follow = (sbyte)b[m.FollowType];
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
