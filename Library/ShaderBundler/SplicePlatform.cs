using System;
using System.Collections.Generic;
using ShaderLibrary;

namespace ShaderBundler
{
    /// <summary>
    /// What splicing for one NVN generation needs beyond the stage layout
    /// </summary>
    public sealed class SplicePlatform
    {
        /// <summary>Splatoon 3's ubershader on NVN.</summary>
        public static readonly SplicePlatform Nvn = new(
            NvnPlatform.Nvn,
            UberArchive.Nvn,
            "Thunder",
            "codegen",
            new[]
            {
                "V1",
                "V2",
                "V3",
                "V4",
                "V5",
                "V6",
                "V7",
                "V8",
                "V9",
                "V10",
                "V11",
                "V12",
                "V13",
                "V14",
            },
            gateListComplete: true,
            reportsSpliceLoss: false,
            CheckNvn
        );

        /// <summary>Splatoon Raiders' ubershader on NVN2.</summary>
        public static readonly SplicePlatform Nvn2 = new(
            NvnPlatform.Nvn2,
            UberArchive.Nvn2,
            "Spiral",
            "codegen-nvn2",
            new[] { "V1", "V2", "V3", "V5", "V13" },
            gateListComplete: false,
            reportsSpliceLoss: true,
            CheckNvn2
        );

        readonly Func<byte[], string> _checkControl;

        SplicePlatform(
            NvnPlatform platform,
            UberSource uber,
            string sliceCacheName,
            string codegenCommand,
            string[] expectedGates,
            bool gateListComplete,
            bool reportsSpliceLoss,
            Func<byte[], string> checkControl
        )
        {
            Platform = platform;
            Uber = uber;
            SliceCacheName = sliceCacheName;
            CodegenCommand = codegenCommand;
            ExpectedGates = expectedGates;
            GateListComplete = gateListComplete;
            ReportsSpliceLoss = reportsSpliceLoss;
            _checkControl = checkControl;
        }

        public NvnPlatform Platform { get; }

        /// <summary>Where the game on this platform keeps its ubershader.</summary>
        public UberSource Uber { get; }

        /// <summary>The game's folder in the splice cache, so the two never share an entry.</summary>
        public string SliceCacheName { get; }

        /// <summary>The specialiser verb that prints this platform's codegen version.</summary>
        public string CodegenCommand { get; }

        /// <summary>The codegen version the specialiser stamps for this platform, or
        /// <see cref="BundleStamp.Unstamped"/> while unknown.</summary>
        public int Codegen { get; set; } = BundleStamp.Unstamped;

        /// <summary>Whether the codegen of either platform is known.</summary>
        public static bool AnyCodegen =>
            Nvn.Codegen != BundleStamp.Unstamped || Nvn2.Codegen != BundleStamp.Unstamped;

        /// <summary>The gates the specialiser prints for this platform. One that is not printed
        /// is a failure, not a pass.</summary>
        public IReadOnlyList<string> ExpectedGates { get; }

        /// <summary>Whether the list is every gate that counts. When it is not, a printed gate
        /// beyond it still fails the splice when it fails.</summary>
        public bool GateListComplete { get; }

        /// <summary>Whether the ubershader has features compiled out, so a splice says which
        /// options and samplers it could not honour.</summary>
        public bool ReportsSpliceLoss { get; }

        public static SplicePlatform For(NvnPlatform platform) =>
            platform == NvnPlatform.Nvn2 ? Nvn2 : Nvn;

        /// <summary>The platform a control section is for.</summary>
        public static SplicePlatform Of(byte[] control) => For(NvnPlatform.Of(control));

        /// <summary>Why a stage's control does not belong in an archive of this platform, or
        /// null when it does.</summary>
        public string CheckControl(byte[] control) => _checkControl(control);

        public override string ToString() => Platform.Name;

        static string CheckNvn(byte[] control) =>
            control.Length != NvnPlatform.Nvn.ControlLength
                ? $"control blob is {control.Length} B, expected is {NvnPlatform.Nvn.ControlLength} B"
                : null;

        static string CheckNvn2(byte[] control)
        {
            if (NvnPlatform.Of(control) != NvnPlatform.Nvn2)
                return "control blob is not an NVN2 one, and the archive is";
            if (!NvnPlatform.JumpTablesFit(control))
                return "the control lists an indirect branch or jump table outside this program's "
                    + "code or constants";
            return null;
        }
    }
}
