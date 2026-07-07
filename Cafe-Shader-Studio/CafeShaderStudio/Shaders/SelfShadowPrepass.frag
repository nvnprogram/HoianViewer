#version 330

uniform sampler2D sceneDepth;
//Hardware comparison sampler (linear + LEQUAL), matching the game's cascade map
//sampler state (RenderDoc: "Linear (Less Equal), ClampBorder <1,1,1>"). Each tap
//returns a bilinearly filtered PCF result instead of a binary compare.
uniform sampler2DShadow lightDepth;

uniform mat4 invCamViewProj;
uniform mat4 lightViewProj;

uniform float outR;
uniform float outB;

//View-distance shadow fade, matching the game's prepass (fp_c3[47].y/.z:
//visibility += clamp((viewDist - 100) / 900, 0, 1)). Far pixels lose shadow
//smoothly instead of showing unreliable sub-texel shadows.
uniform vec3 camPos;
uniform float fadeStart;    //distance where the fade begins
uniform float fadeInvRange; //1 / (fadeEnd - fadeStart)

//World positions are reconstructed from the 24-bit scene depth buffer, whose
//precision falls off with viewDist^2 (tiny znear). Without compensation the
//reconstruction error exceeds the shadow bias at range and flat surfaces
//(e.g. ground graffiti) break into acne stripes. biasScale converts the
//expected quantization error (~d^2/(znear*2^24)) into light-NDC bias.
uniform float biasScale;

in vec2 TexCoords;

out vec4 fragOutput;

void main()
{
    //The uber shader samples gsys_shadow_prepass with NVN's flipped-Y screen UV
    //(y = 0.5 - 0.5*ndcY), so texel (x,y) must hold the shadow of the scene
    //pixel at (x, 1-y) in GL orientation.
    vec2 sceneUv = vec2(TexCoords.x, 1.0 - TexCoords.y);
    float depth = texture(sceneDepth, sceneUv).r;

    float visibility = 1.0;
    if (depth < 1.0)
    {
        //Reconstruct the world position of this pixel.
        vec4 ndc = vec4(sceneUv * 2.0 - 1.0, depth * 2.0 - 1.0, 1.0);
        vec4 world = invCamViewProj * ndc;
        world /= world.w;

        vec4 lightClip = lightViewProj * world;
        vec3 lightNdc = lightClip.xyz / lightClip.w;
        vec3 lightUv = lightNdc * 0.5 + 0.5;

        float viewDist = distance(world.xyz, camPos);

        if (lightUv.x > 0.0 && lightUv.x < 1.0 && lightUv.y > 0.0 && lightUv.y < 1.0 && lightUv.z < 1.0)
        {
            //The game's prepass takes 4 hardware-filtered dref taps spread a texel
            //apart (base, +dx, +dy, +dx+dy) and averages them; the shader-side bias
            //there is ~0 because the caster pass bakes in a depth offset. We keep a
            //small constant bias on top of the caster-side polygon offset, plus a
            //distance term covering the scene-depth reconstruction error.
            float bias = 0.0025 + viewDist * viewDist * biasScale;
            float ref = lightUv.z - bias;
            vec2 texel = 1.0 / vec2(textureSize(lightDepth, 0));
            float lit = texture(lightDepth, vec3(lightUv.xy, ref))
                      + texture(lightDepth, vec3(lightUv.xy + vec2(texel.x, 0.0), ref))
                      + texture(lightDepth, vec3(lightUv.xy + vec2(0.0, texel.y), ref))
                      + texture(lightDepth, vec3(lightUv.xy + texel, ref));
            visibility = lit * 0.25;

            float fade = clamp((viewDist - fadeStart) * fadeInvRange, 0.0, 1.0);
            visibility = min(visibility + fade, 1.0);
        }
    }

    fragOutput = vec4(outR, visibility, outB, 1.0);
}
