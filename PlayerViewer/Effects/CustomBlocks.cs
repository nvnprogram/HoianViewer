using System;
using System.Buffers.Binary;
using System.IO;
using System.Numerics;
using EffectLibrary;
using PlayerViewer.Env;

namespace PlayerViewer.Effects
{
    /// <summary>
    /// The game's custom shader blocks, built for a viewer rather than a stage: Custom0 (the
    /// scene) and Custom1 (per emitter: team colour and the emitter's CSDP parameters). Custom0
    /// starts from a block captured in game. Custom2 is the light table, <see cref="Env.LightCluster"/>.
    /// </summary>
    public static class CustomBlocks
    {
        static byte[] _custom0;

        internal static byte[] Custom0Template => _custom0 ??= Load("Custom0.bin");

        static byte[] Load(string name)
        {
            using var stream =
                typeof(CustomBlocks).Assembly.GetManifestResourceStream(
                    $"PlayerViewer.Effects.Resources.{name}"
                ) ?? throw new InvalidOperationException($"missing embedded block {name}");
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            return ms.ToArray();
        }

        /// <summary>
        /// The scene block.
        /// </summary>
        public static byte[] BuildCustom0(
            Custom0Layout layout,
            byte[] environment,
            byte[] user0,
            EnvParams scene,
            Vector3 towardLight,
            Vector3 playerPosition,
            int width,
            int height,
            float seconds
        )
        {
            var b = (byte[])Custom0Template.Clone();
            bool env = environment != null && environment.Length >= layout.EnvironmentSize;
            if (env)
                Copy(b, environment, layout.Environment);
            var fog = layout.FogInEnvironment
                ? (env ? environment : null)
                : (user0 != null && user0.Length >= 56 * 16 ? user0 : null);
            if (fog != null)
                Copy(b, fog, layout.Fog);
            if (scene != null && layout.WritesScene)
                WriteScene(b, scene);
            W3(b, 0x010, towardLight);
            foreach (var (at, length) in layout.Cleared)
                Array.Clear(b, at, length);
            if (layout.WritesScene)
            {
                W3(b, 0x2E0, playerPosition);
                W3(b, 0x380, playerPosition);
            }
            W(b, layout.TargetAt, width);
            W(b, layout.TargetAt + 4, height);
            W(b, layout.TargetAt + 8, 1f / Math.Max(width, 1));
            W(b, layout.TargetAt + 12, 1f / Math.Max(height, 1));
            if (layout.WritesScene)
                W(b, 0x390, seconds);
            return b;
        }

        static void Copy(byte[] b, byte[] src, Custom0Copy[] copies)
        {
            foreach (var c in copies)
                Buffer.BlockCopy(src, 16 * c.Row + 4 * c.Component, b, c.At, 4 * c.Count);
        }

        //A stage's bloom threshold and manual exposure, and the ink map scale the game uses in
        //battle, twice the lobby template's.
        static void WriteScene(byte[] b, EnvParams p)
        {
            W(b, 0x1E0, p.BloomThreshold);
            W(b, 0x1F0, 1f / 200);
            W(b, 0x1F4, 1f / 200);
            W(b, 0x270, p.ManualExposure is float stops ? float.Exp2(stops) : 1f);
        }

        /// <summary>
        /// The strength of the light's sharp highlight in the effects' sphere maps. Captured
        /// stages, which all set IsUseCubeMapIntens, fit the light colour times CubeMapIntens
        /// squared; the lobby, which does not, fits the light colour times its intensity.
        /// </summary>
        public static Vector3 HighlightLobe(byte[] environment, EnvParams scene)
        {
            if (environment == null || environment.Length < 6 * 16)
                return Vector3.Zero;
            var light = new Vector3(
                R(environment, 5, 0),
                R(environment, 5, 1),
                R(environment, 5, 2)
            );
            float intens = R(environment, 5, 3);
            if (scene is { IsUseCubeMapIntens: true } && intens > 0)
                return light / intens * (CubeLobeScale * scene.CubeMapIntens * scene.CubeMapIntens);
            return light * IntensLobeScale;
        }

        const float CubeLobeScale = 0.0095f;
        const float IntensLobeScale = 0.086f;

        static float R(byte[] src, int row, int component) =>
            BinaryPrimitives.ReadSingleLittleEndian(src.AsSpan(16 * row + 4 * component));

        /// <summary>
        /// The per emitter block as the game's callback fills it. The ink getters are the values
        /// the models' gsys_user0 carries, read from <paramref name="user0"/> when given; the set
        /// alpha is full. The team colour variants the game derives from its colour tables are the
        /// colour given, linear, and a brighter scale of it.
        /// </summary>
        public static byte[] BuildCustom1(Emitter emitter, Vector3 teamColor, byte[] user0 = null)
        {
            var b = new byte[EffectBindings.Custom1Size];
            var csdp = emitter.FindSub("CSDP");
            byte[] p =
                csdp != null && csdp.Data.Length >= 120 ? csdp.Data.ToArray() : new byte[120];
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
            W(b, 0x140, P(21));
            W(b, 0x150, P(25));
            W(b, 0x154, P(26));
            W(b, 0x158, (float)U(27));

            //The ink getters: in every capture the numbers of the models' gsys_user0 rows, but
            //for 0x00, which only switches with night.
            bool u0 = user0 != null && user0.Length >= 56 * 16;
            bool night = u0 && R(user0, EnvUniforms.User0RowNight, 3) > 0.5f;
            if ((flags & 0x40) != 0)
            {
                W(b, 0x00, night ? 0.2f : 0.24f);
                W(b, 0x04, 0);
                W(b, 0x08, u0 ? R(user0, EnvUniforms.User0RowInkEnv, 0) : 0.015f);
                W(b, 0x2C, u0 ? R(user0, EnvUniforms.User0RowInkRim, 2) : 0.1f);
            }
            if ((flags & 0x41) != 0)
            {
                W(b, 0x3C, u0 ? R(user0, EnvUniforms.User0RowInkRimCurve, 1) : 0.625f);
                W(b, 0x40, P(4));
                W(b, 0x44, P(5));
                W(b, 0x48, u0 ? R(user0, EnvUniforms.User0RowInkRimCurve, 2) : 0.875f);
            }

            bool normalise = (U(28) & 1) != 0;
            Vector3 Team()
            {
                if (!normalise)
                    return teamColor;
                float lum = MathF.Max(
                    teamColor.X * 0.2126f + teamColor.Y * 0.7152f + teamColor.Z * 0.0722f,
                    0.01f
                );
                return teamColor * (P(29) / lum);
            }
            if ((flags & 1) != 0)
            {
                W3(b, 0x20, Team());
                //The brighter variant the game derives, as a plain scale of the base colour.
                W3(b, 0x30, Team() * BrightVariant);
            }
            else
            {
                if ((flags & 8) != 0)
                    W3(b, 0x20, Team());
                if ((flags & 0x10) != 0)
                    W3(b, 0x30, Team());
            }

            //The set's alpha, which a viewer never lowers; every branch the game takes then writes 1.
            W(b, 0xB0, 1f);
            return b;
        }

        /// <summary>
        /// The second team colour over the first in the lobby captures. The game takes both from
        /// gsys_user0 rows 3 and 4, which a stage darkens by a factor of its own.
        /// </summary>
        internal const float BrightVariant = 1.14f;

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
