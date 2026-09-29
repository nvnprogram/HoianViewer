using System;
using System.Numerics;

namespace EffectLibrary
{
    /// <summary>
    /// An emitter keyframe animation (an EA** sub-section): a header of 12 bytes, then
    /// <see cref="KeyCount"/> keys of xyz and a frame time.
    /// </summary>
    public readonly struct EmitterAnimation
    {
        public VfxSection Section { get; }

        public EmitterAnimation(VfxSection section) => Section = section;

        public static bool IsAnimationTag(string tag) =>
            tag
                is "EASL"
                    or "EAES"
                    or "EAER"
                    or "EAET"
                    or "EATR"
                    or "EAOV"
                    or "EADV"
                    or "EAGV"
                    or "EAPL"
                    or "EAC0"
                    or "EAC1"
                    or "EAA0"
                    or "EAA1"
                    or "EASS";

        public string Tag => Section.Tag;

        /// <summary>What the animation drives, after the runtime's names for the tags.</summary>
        public string Target =>
            Tag switch
            {
                "EASL" => "particle scale",
                "EAES" => "emitter scale",
                "EAER" => "emitter rotate",
                "EAET" => "emitter translate",
                "EATR" => "emission rate",
                "EAOV" => "all direction velocity",
                "EADV" => "directional velocity",
                "EAGV" => "gravity",
                "EAPL" => "particle life",
                "EAC0" => "colour 0",
                "EAC1" => "colour 1",
                "EAA0" => "alpha 0",
                "EAA1" => "alpha 1",
                "EASS" => "emitter volume scale",
                _ => "unknown",
            };

        public bool Enabled => Section.GetU8(0) != 0;
        public bool Loop => Section.GetU8(1) != 0;

        /// <summary>Between keys: 0 interpolates linearly, 1 holds the earlier key, anything else
        /// leaves the value where it was.</summary>
        public byte Interpolation => Section.GetU8(2);

        public int KeyCount => (int)Section.GetU32(4);
        public int LoopFrames => (int)Section.GetU32(8);

        /// <summary>Key i as (x, y, z, frame).</summary>
        public Vector4 GetKey(int index)
        {
            if ((uint)index >= (uint)KeyCount)
                throw new ArgumentOutOfRangeException(nameof(index));
            int at = 12 + index * 16;
            return new Vector4(
                Section.GetF32(at),
                Section.GetF32(at + 4),
                Section.GetF32(at + 8),
                Section.GetF32(at + 12)
            );
        }

        public void SetKey(int index, Vector4 key)
        {
            if ((uint)index >= (uint)KeyCount)
                throw new ArgumentOutOfRangeException(nameof(index));
            int at = 12 + index * 16;
            Section.SetF32(at, key.X);
            Section.SetF32(at + 4, key.Y);
            Section.SetF32(at + 8, key.Z);
            Section.SetF32(at + 12, key.W);
        }
    }
}
