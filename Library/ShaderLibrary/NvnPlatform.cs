using System;

namespace ShaderLibrary
{
    /// <summary>The instruction set a platform's stage code is in.</summary>
    public enum GpuIsa
    {
        Maxwell,
        Ampere,
    }

    /// <summary>
    /// An NVN generation and where it keeps what the tools read out of a stage. A stage's own
    /// control section says which one it is; a container says which one it is laid out for.
    /// </summary>
    public sealed class NvnPlatform
    {
        /// <summary>The first word of a control section, on both generations.</summary>
        public const uint ControlMagic = 0x98761234;

        /// <summary>NVN, Maxwell: control version 1.x, bfsha up to 9.0, BNSH up to 2.3.</summary>
        public static readonly NvnPlatform Nvn = new(
            "NVN",
            GpuIsa.Maxwell,
            stampOffset: 0x768,
            codeHashOffset: 0x7e0,
            codeHeaderSkip: 0x30,
            constantSizeAt: 0x6fc,
            constantOffsetAt: 0x700,
            controlLength: 2176,
            hasStageInfo: false
        );

        /// <summary>NVN2, Ampere: control version 200 and up, bfsha 9.1, BNSH 2.4.</summary>
        public static readonly NvnPlatform Nvn2 = new(
            "NVN2",
            GpuIsa.Ampere,
            stampOffset: 0x7c,
            codeHashOffset: 0x28,
            codeHeaderSkip: 0,
            constantSizeAt: 0x3c,
            constantOffsetAt: 0x40,
            controlLength: 0,
            hasStageInfo: true
        );

        readonly int _constantSizeAt;
        readonly int _constantOffsetAt;

        NvnPlatform(
            string name,
            GpuIsa isa,
            int stampOffset,
            int codeHashOffset,
            int codeHeaderSkip,
            int constantSizeAt,
            int constantOffsetAt,
            int controlLength,
            bool hasStageInfo
        )
        {
            Name = name;
            Isa = isa;
            StampOffset = stampOffset;
            CodeHashOffset = codeHashOffset;
            CodeHeaderSkip = codeHeaderSkip;
            _constantSizeAt = constantSizeAt;
            _constantOffsetAt = constantOffsetAt;
            ControlLength = controlLength;
            HasStageInfo = hasStageInfo;
        }

        public string Name { get; }

        public GpuIsa Isa { get; }

        /// <summary>Where the specialiser's provenance stamp sits in a control: the debug build
        /// id, which no shipped program fills.</summary>
        public int StampOffset { get; }

        /// <summary>Where a control names the compile its stage came from.</summary>
        public int CodeHashOffset { get; }

        /// <summary>Bytes ahead of the program header in the stage code: NVN's 0x30 prefix that
        /// starts with 0x12345678.</summary>
        public int CodeHeaderSkip { get; }

        /// <summary>The length every control of this generation has, or 0 where it varies.</summary>
        public int ControlLength { get; }

        /// <summary>Whether each BNSH program carries a StageInfo block.</summary>
        public bool HasStageInfo { get; }

        public override string ToString() => Name;

        /// <summary>The generation a control section is for. NVN2 needs the magic and a version
        /// of 200 or more; anything else is read as NVN.</summary>
        public static NvnPlatform Of(byte[]? control) =>
            control != null
            && control.Length >= 8
            && BitConverter.ToUInt32(control, 0) == ControlMagic
            && BitConverter.ToUInt32(control, 4) >= 200
                ? Nvn2
                : Nvn;

        /// <summary>The generation a BNSH container is laid out for: 2.4 and later is NVN2's.</summary>
        public static NvnPlatform Of(BnshFile bnsh) =>
            bnsh.BinHeader.VersionMajor > 2
            || (bnsh.BinHeader.VersionMajor == 2 && bnsh.BinHeader.VersionMinor >= 4)
                ? Nvn2
                : Nvn;

        /// <summary>A stage's embedded constants (constant bank 1), which its control locates in
        /// the code. Null when it has none or the control cannot say.</summary>
        public static byte[]? EmbeddedConstants(byte[]? control, byte[]? code)
        {
            var p = Of(control);
            int end = Math.Max(p._constantSizeAt, p._constantOffsetAt) + 4;
            if (control == null || code == null || control.Length < end)
                return null;
            uint size = BitConverter.ToUInt32(control, p._constantSizeAt);
            uint offset = BitConverter.ToUInt32(control, p._constantOffsetAt);
            if (size == 0 || (long)offset + size > code.Length)
                return null;
            return code.AsSpan((int)offset, (int)size).ToArray();
        }

        /// <summary>
        /// Whether every entry of an NVN2 control's indirect branch table (+0xd8) names a branch
        /// inside the code and a jump table inside the embedded constants. A control copied from
        /// another program keeps that program's table.
        /// </summary>
        public static bool JumpTablesFit(byte[]? c)
        {
            if (c == null || c.Length < 0xe0)
                return false;
            uint at = BitConverter.ToUInt32(c, 0xd8),
                size = BitConverter.ToUInt32(c, 0xdc);
            if (size == 0)
                return true;
            if (size < 4 || at + (ulong)size > (ulong)c.Length)
                return false;
            uint n = BitConverter.ToUInt32(c, (int)at);
            if (4 + 16UL * n > size)
                return false;
            uint code = BitConverter.ToUInt32(c, 0x38),
                consts = BitConverter.ToUInt32(c, 0x3c);
            for (int i = 0; i < n; i++)
            {
                int e = (int)at + 4 + 16 * i;
                uint branch = BitConverter.ToUInt32(c, e),
                    dwords = BitConverter.ToUInt32(c, e + 4);
                uint data = BitConverter.ToUInt32(c, e + 12);
                if (branch + 0x80UL >= code || data + 4UL * dwords > consts)
                    return false;
            }
            return true;
        }
    }
}
