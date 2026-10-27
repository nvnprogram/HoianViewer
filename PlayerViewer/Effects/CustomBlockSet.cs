using System;
using System.Numerics;
using EffectLibrary;
using PlayerViewer.Env;

namespace PlayerViewer.Effects
{
    /// <summary>Floats copied into Custom0 from a model block: <c>Count</c> of them from row
    /// <c>Row</c>, starting at component <c>Component</c>, to byte offset <c>At</c>.</summary>
    public readonly record struct Custom0Copy(int At, int Row, int Component = 0, int Count = 4);

    /// <summary>Where a game's Custom0 takes the scene from and what it writes over the template.</summary>
    public sealed class Custom0Layout
    {
        /// <summary>The shortest gsys_environment that holds every row read.</summary>
        public int EnvironmentSize { get; init; }

        /// <summary>The lighting, SH, depth and height fog rows of gsys_environment.</summary>
        public Custom0Copy[] Environment { get; init; }

        /// <summary>Whether the radial fog rows are gsys_environment's rather than gsys_user0's.</summary>
        public bool FogInEnvironment { get; init; }

        public Custom0Copy[] Fog { get; init; }

        /// <summary>Byte ranges zeroed after the copies.</summary>
        public (int At, int Length)[] Cleared { get; init; } = Array.Empty<(int, int)>();

        /// <summary>Where the render target size and its reciprocals go.</summary>
        public int TargetAt { get; init; }

        /// <summary>Whether the player position, the scene time and the stage's own params are
        /// written.</summary>
        public bool WritesScene { get; init; }

        //Rows both games read from gsys_environment at the same place.
        static Custom0Copy[] Shared(int shRow, params Custom0Copy[] more)
        {
            var copies = new System.Collections.Generic.List<Custom0Copy> { new(0x000, 5) };
            for (int i = 0; i < 7; i++)
                copies.Add(new(0x170 + 16 * i, shRow + i));
            copies.AddRange(
                new Custom0Copy[]
                {
                    new(0x200, 10),
                    new(0x210, 11, 3, 1),
                    new(0x214, 12, 0, 1),
                    new(0x240, 13),
                    new(0x250, 14, 3, 1),
                    new(0x254, 15, 0, 1),
                    new(0x260, 14, 0, 3),
                }
            );
            copies.AddRange(more);
            return copies.ToArray();
        }

        //The radial fog: its red times its intensity, or the intensity, at 0x22C (the red is 1 in
        //every Splatoon 3 capture).
        static Custom0Copy[] RadialFog(int row, int secondComponent, int paramRow, int colorRow) =>
            new Custom0Copy[]
            {
                new(0x218, row, 0, 1),
                new(0x21C, row, secondComponent, 1),
                new(0x220, colorRow, 0, 3),
                new(0x22C, colorRow, 0, 1),
                new(0x230, paramRow),
            };

        /// <summary>Every Splatoon 3 capture holds these bit identical to the model blocks' rows.</summary>
        public static readonly Custom0Layout Splatoon3 = new()
        {
            EnvironmentSize = 32 * 16,
            Environment = Shared(25),
            Fog = RadialFog(53, 3, 54, 55),
            TargetAt = 0x360,
            WritesScene = true,
        };

        /// <summary>
        /// Splatoon 3's rows with the SH and the radial fog where Raiders' gsys_environment holds
        /// them, the height fog at 0x360 and the target size at 0x320. 0x2D0 to 0x35F are zeroed,
        /// and so is the cap on the dynamic lights at 0x390, since none are fed.
        /// </summary>
        public static readonly Custom0Layout Raiders = new()
        {
            EnvironmentSize = RaidersUniforms.EnvironmentSize,
            Environment = Shared(
                97,
                new(0x360, 112),
                new(0x370, 114),
                new(0x380, 111)
            ),
            FogInEnvironment = true,
            Fog = RadialFog(106, 1, 107, 108),
            Cleared = new[] { (0x2D0, 0x360 - 0x2D0), (0x390, 0x10) },
            TargetAt = 0x320,
        };
    }

    /// <summary>One game's custom shader blocks, picked by the effect file's VFXB version.</summary>
    public sealed class CustomBlockSet
    {
        public static readonly CustomBlockSet Splatoon3 = new(
            Custom0Layout.Splatoon3,
            (e, team, _, user0) => CustomBlocks.BuildCustom1(e, team, user0)
        );

        public static readonly CustomBlockSet Raiders = new(
            Custom0Layout.Raiders,
            (e, _, teams, user0) => RaidersCustomBlocks.BuildCustom1(e, teams, user0)
        );

        public static CustomBlockSet For(EmitterMap map) => map.IsV46 ? Splatoon3 : Raiders;

        public Custom0Layout Custom0 { get; }

        readonly Func<Emitter, Vector3, RaidersCustomBlocks.Teams, byte[], byte[]> _custom1;

        CustomBlockSet(
            Custom0Layout custom0,
            Func<Emitter, Vector3, RaidersCustomBlocks.Teams, byte[], byte[]> custom1
        )
        {
            Custom0 = custom0;
            _custom1 = custom1;
        }

        /// <summary>The scene block, see <see cref="CustomBlocks.BuildCustom0"/>.</summary>
        public byte[] BuildCustom0(
            byte[] environment,
            byte[] user0,
            EnvParams scene,
            Vector3 towardLight,
            Vector3 playerPosition,
            int width,
            int height,
            float seconds
        ) =>
            CustomBlocks.BuildCustom0(
                Custom0,
                environment,
                user0,
                scene,
                towardLight,
                playerPosition,
                width,
                height,
                seconds
            );

        /// <summary>The per emitter block: Splatoon 3's takes the alpha team colour, Raiders' every
        /// team's.</summary>
        public byte[] BuildCustom1(
            Emitter emitter,
            Vector3 team,
            in RaidersCustomBlocks.Teams teams,
            byte[] user0
        ) => _custom1(emitter, team, teams, user0);
    }
}
