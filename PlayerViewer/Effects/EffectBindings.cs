using System.Collections.Generic;

namespace PlayerViewer.Effects
{
    /// <summary>The uniform blocks an effect program can read.</summary>
    public enum EffectBlock
    {
        /// <summary>NnVfx2ViewParam, 0x250 bytes: camera matrices, once per view.</summary>
        View,

        /// <summary>sysEmitterStaticUniformBlock, 0xA90 bytes: the emitter after the load time
        /// rewrite, see <c>StaticUniformBlock.Build</c>.</summary>
        EmitterStatic,

        /// <summary>NnVfx2EmitterDynamicParam, 0x160 bytes: colours, time and matrices, per
        /// emitter per frame.</summary>
        EmitterDynamic,

        /// <summary>sysEmitterFieldUniformBlock. No draw program reads it; the compute programs
        /// do.</summary>
        EmitterField,

        /// <summary>sysEmitterPluginUniformBlock: the stripe and area loop plugins.</summary>
        EmitterPlugin,

        /// <summary>sysCustomShaderReservedUniformBlockParam.</summary>
        CustomReserved,

        /// <summary>sysCustomShaderUniformBlock0, 0x3A0 bytes: the game's scene lighting, once
        /// per frame.</summary>
        Custom0,

        /// <summary>sysCustomShaderUniformBlock1, 0x160 bytes: the game's per emitter block,
        /// team colours and ink.</summary>
        Custom1,

        /// <summary>sysCustomShaderUniformBlock2: the game's global table.</summary>
        Custom2,

        /// <summary>sysCustomShaderUniformBlock3.</summary>
        Custom3,
    }

    /// <summary>
    /// The names the effect programs' reflection uses. Uniform blocks and samplers sit at the same
    /// slot in every stock program; vertex inputs do not, so those are read per program.
    /// </summary>
    public static class EffectBindings
    {
        public const int BlockCount = 10;

        public static readonly IReadOnlyDictionary<string, EffectBlock> BlockNames = new Dictionary<
            string,
            EffectBlock
        >
        {
            ["NnVfx2ViewParam"] = EffectBlock.View,
            ["sysEmitterStaticUniformBlock"] = EffectBlock.EmitterStatic,
            ["NnVfx2EmitterDynamicParam"] = EffectBlock.EmitterDynamic,
            ["sysEmitterFieldUniformBlock"] = EffectBlock.EmitterField,
            ["sysEmitterPluginUniformBlock"] = EffectBlock.EmitterPlugin,
            ["sysCustomShaderReservedUniformBlockParam"] = EffectBlock.CustomReserved,
            ["sysCustomShaderUniformBlock0"] = EffectBlock.Custom0,
            ["sysCustomShaderUniformBlock1"] = EffectBlock.Custom1,
            ["sysCustomShaderUniformBlock2"] = EffectBlock.Custom2,
            ["sysCustomShaderUniformBlock3"] = EffectBlock.Custom3,
        };

        public const int ViewSize = 0x250;
        public const int StaticSize = 0xA90;
        public const int DynamicSize = 0x160;
        public const int Custom0Size = 0x3A0;
        public const int Custom1Size = 0x160;

        /// <summary>A copy of the scene colour, taken before the refractive emitters draw.</summary>
        public const string FrameBufferTexture = "sysFrameBufferTexture";

        /// <summary>A copy of the scene depth, for soft particles and depth fades.</summary>
        public const string DepthBufferTexture = "sysDepthBufferTexture";

        /// <summary>The emitter's six texture slots. Slots 0 to 2 sit at sampler slots 0 to 2,
        /// slots 3 to 5 at 22 to 24.</summary>
        public static string TextureSampler(int slot) => "sysTextureSampler" + slot;

        public const int TextureSlotCount = 6;

        /// <summary>
        /// The samplers the game's custom shader callback binds and the renderer does not own:
        /// the scene copies, shadow maps, the environment cube array and the game's lookup
        /// tables. The host supplies a texture for each by name.
        /// </summary>
        public static bool IsHostSampler(string name) =>
            name == FrameBufferTexture
            || name == DepthBufferTexture
            || name.StartsWith("sysCustomShader", System.StringComparison.Ordinal);

        /// <summary>
        /// The samplers read in screen space: the two scene copies, and the scene depth and shadow
        /// mask the custom callback binds at slots 15 and 16. A renderer whose render targets are
        /// stored bottom row first flips their V.
        /// </summary>
        public static readonly IReadOnlyCollection<string> ScreenSpaceSamplers = new[]
        {
            FrameBufferTexture,
            DepthBufferTexture,
            "sysCustomShaderTextureSampler5",
            "sysCustomShaderTextureSampler6",
        };

        /// <summary>Per particle vertex inputs, one float4 stream each.</summary>
        public static readonly IReadOnlyList<string> ParticleAttributes = new[]
        {
            "sysLocalPosAttr",
            "sysLocalVecAttr",
            "sysLocalDiffAttr",
            "sysScaleAttr",
            "sysRandomAttr",
            "sysInitRotateAttr",
            "sysColor0Attr",
            "sysColor1Attr",
            "sysEmtMat0Attr",
            "sysEmtMat1Attr",
            "sysEmtMat2Attr",
        };

        /// <summary>The primitive mesh inputs.</summary>
        public const string PositionAttr = "sysPosAttr";
        public const string NormalAttr = "sysNormalAttr";
        public const string TangentAttr = "sysTangentAttr";
        public const string VertexColorAttr = "sysVertexColor0Attr";
        public const string TexCoordAttr = "sysTexCoordAttr";
    }
}
