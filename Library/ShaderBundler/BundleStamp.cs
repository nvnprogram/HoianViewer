using System;
using System.Collections.Generic;
using ShaderLibrary;

namespace ShaderBundler
{
    /// <summary>What an embedded archive is, as far as regenerating it goes.</summary>
    public enum BundleAge
    {
        /// <summary>Nothing in it came from a specialiser other than this one.</summary>
        Current,

        /// <summary>A program came from another codegen. Drop the archive and rebuild.</summary>
        Stale,

        /// <summary>Nothing to judge: no specialiser to compare against, or no programs.</summary>
        Unknown,
    }

    /// <summary>
    /// The provenance stamp the specialiser writes
    /// </summary>
    public static class BundleStamp
    {
        /// <summary>'UBSP'</summary>
        public const uint Magic = 0x50534255;

        /// <summary>Offset of the stamp inside the 2176 byte control blob.</summary>
        public const int Offset = 0x768;

        /// <summary>Offset of the code hash inside the control blob.</summary>
        public const int CodeHashOffset = 0x7e0;

        /// <summary>What <see cref="Read"/> answers for a program with no stamp.</summary>
        public const int Unstamped = -1;

        /// <summary>
        /// The codegen version of the specialiser this build splices with, or
        /// <see cref="Unstamped"/> if uninit.
        /// </summary>
        public static int Codegen { get; set; } = Unstamped;

        /// <summary>The codegen version stamped in a control blob, or <see cref="Unstamped"/>.</summary>
        public static int Read(byte[] control)
        {
            if (control == null || control.Length < Offset + 8)
                return Unstamped;
            if (BitConverter.ToUInt32(control, Offset) != Magic)
                return Unstamped;
            return BitConverter.ToInt32(control, Offset + 4);
        }

        /// <summary>
        /// The control blob's code hash
        /// </summary>
        public static ulong CodeHash(byte[] control) =>
            control == null || control.Length < CodeHashOffset + 8
                ? 0
                : BitConverter.ToUInt64(control, CodeHashOffset);

        /// <summary>
        /// Whether an embedded archive holds a program this build's specialiser did not make.
        ///
        /// An unstamped program is either one the specialiser made before the stamp existed or
        /// a stock variation, and <paramref name="uberCodeHashes"/> is what tells
        /// those apart (since we keep the uber's code hash on the uberslicer)
        /// </summary>
        public static BundleAge Age(BfshaFile archive, ISet<ulong> uberCodeHashes)
        {
            if (archive == null || Codegen == Unstamped)
                return BundleAge.Unknown;

            bool any = false;
            for (int i = 0; i < archive.ShaderModels.Count; i++)
            {
                var bnsh = archive.ShaderModels[i]?.BnshFile;
                if (bnsh == null)
                    continue;
                foreach (var variation in bnsh.Variations)
                {
                    var bp = variation.BinaryProgram;
                    foreach (var code in new[] { bp?.VertexShader, bp?.FragmentShader })
                    {
                        if (code?.ControlCode == null)
                            continue;
                        any = true;

                        int stamp = Read(code.ControlCode);
                        if (stamp == Codegen)
                            continue;
                        if (stamp != Unstamped)
                            return BundleAge.Stale;
                        if (
                            uberCodeHashes != null
                            && uberCodeHashes.Contains(CodeHash(code.ControlCode))
                        )
                            return BundleAge.Stale;
                    }
                }
            }
            return any ? BundleAge.Current : BundleAge.Unknown;
        }
    }
}
