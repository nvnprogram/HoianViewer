using EffectLibrary;

namespace PlayerViewer.Effects
{
    /// <summary>The passes an emitter is drawn in, as the game orders a frame.</summary>
    public enum EffectPass
    {
        /// <summary>Opaque emitters into the depth prepass: colour writes off, depth written.</summary>
        DepthPrepass,

        /// <summary>Opaque emitters again in the colour pass, depth Equal against the prepass.</summary>
        Opaque,

        /// <summary>Shadow casting emitters into the shadow map: depth only, with depth clamp and
        /// the shadow pass's depth bias.</summary>
        Shadow,

        /// <summary>Blended emitters into the HDR target.</summary>
        Translucent,

        /// <summary>
        /// Blended emitters into the half resolution target that is composited over the HDR
        /// target afterwards. Normal and add blending write zero source alpha here, so the target's
        /// alpha accumulates what is left of the background.
        /// </summary>
        LowRes,
    }

    public enum BlendFactor
    {
        Zero,
        One,
        SrcColor,
        OneMinusSrcColor,
        DstColor,
        OneMinusDstColor,
        SrcAlpha,
        OneMinusSrcAlpha,
    }

    public enum BlendOp
    {
        Add,
        Subtract,
        ReverseSubtract,
    }

    public enum CompareFunc
    {
        Never,
        Less,
        Equal,
        LessEqual,
        Greater,
        NotEqual,
        GreaterEqual,
        Always,
    }

    public enum CullMode
    {
        None,
        Back,
        Front,
    }

    /// <summary>The fixed function state of one emitter draw, independent of the graphics API.</summary>
    public readonly record struct EmitterDrawState(
        bool Blend,
        BlendFactor ColorSrc,
        BlendFactor ColorDst,
        BlendOp ColorOp,
        BlendFactor AlphaSrc,
        BlendFactor AlphaDst,
        BlendOp AlphaOp,
        bool ColorWrite,
        bool DepthTest,
        bool DepthWrite,
        CompareFunc DepthFunc,
        CullMode Cull,
        float DepthBiasSlope = 0,
        float DepthBiasConstant = 0,
        bool DepthClamp = false
    )
    {
        /// <summary>
        /// The state the game draws <paramref name="emitter"/> with in <paramref name="pass"/>.
        /// Normal and add blending match every captured draw; subtract, screen and multiply
        /// follow the older library and are unverified.
        /// </summary>
        public static EmitterDrawState For(Emitter emitter, EffectPass pass)
        {
            var rs = emitter.RenderState;
            var cull = rs.DisplaySide switch
            {
                1 => CullMode.Back,
                2 => CullMode.Front,
                _ => CullMode.None,
            };
            var func = (CompareFunc)(rs.DepthFunc & 7);
            switch (pass)
            {
                case EffectPass.DepthPrepass:
                    return new(
                        false,
                        BlendFactor.One,
                        BlendFactor.Zero,
                        BlendOp.Add,
                        BlendFactor.One,
                        BlendFactor.Zero,
                        BlendOp.Add,
                        false,
                        true,
                        true,
                        func,
                        cull
                    );
                case EffectPass.Shadow:
                    return new(
                        false,
                        BlendFactor.One,
                        BlendFactor.Zero,
                        BlendOp.Add,
                        BlendFactor.One,
                        BlendFactor.Zero,
                        BlendOp.Add,
                        false,
                        true,
                        true,
                        func,
                        cull,
                        5f,
                        0.3f,
                        true
                    );
                case EffectPass.Opaque:
                    return new(
                        false,
                        BlendFactor.One,
                        BlendFactor.Zero,
                        BlendOp.Add,
                        BlendFactor.One,
                        BlendFactor.Zero,
                        BlendOp.Add,
                        true,
                        true,
                        false,
                        CompareFunc.Equal,
                        cull
                    );
            }

            var (src, dst, op) = rs.BlendType switch
            {
                1 => (BlendFactor.SrcAlpha, BlendFactor.One, BlendOp.Add),
                2 => (BlendFactor.SrcAlpha, BlendFactor.One, BlendOp.ReverseSubtract),
                3 => (BlendFactor.OneMinusDstColor, BlendFactor.One, BlendOp.Add),
                4 => (BlendFactor.Zero, BlendFactor.SrcColor, BlendOp.Add),
                _ => (BlendFactor.SrcAlpha, BlendFactor.OneMinusSrcAlpha, BlendOp.Add),
            };
            //Alpha keeps the colour's destination factor; its source is one, or zero at low res.
            var alphaSrc =
                pass == EffectPass.LowRes && rs.BlendType <= 1 ? BlendFactor.Zero : BlendFactor.One;
            var alphaDst = dst == BlendFactor.SrcColor ? BlendFactor.SrcAlpha : dst;
            //A depth write needs the depth test on; every blended emitter captured with the test
            //on has its write off.
            return new(
                rs.BlendEnable,
                src,
                dst,
                op,
                alphaSrc,
                alphaDst,
                op,
                true,
                rs.DepthTest,
                rs.DepthTest && rs.DepthWrite,
                func,
                cull
            );
        }
    }

    public static class EffectPasses
    {
        /// <summary>The general archive program an emitter draws <paramref name="pass"/> with: the
        /// depth mode variant (ZONLY in stock files) for the prepass and the shadow pass, the base
        /// program otherwise.
        /// The pass variant (XLU_Z_PREPASS) drew none of the captured draws.</summary>
        public static int ProgramIndex(Emitter emitter, EffectPass pass) =>
            pass is EffectPass.DepthPrepass or EffectPass.Shadow
                ? emitter.DepthModeShaderIndex
                : emitter.ShaderIndex;
    }
}
