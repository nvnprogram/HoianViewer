#version 330

layout (location = 0, index = 0) out vec4 Add;
layout (location = 0, index = 1) out vec4 Scale;

in vec4 tint;

uniform sampler2D uSceneDepth;
uniform vec2 uNearFar;
uniform int uFactor;
uniform float uStrength;

float Linear(float depth)
{
    return uNearFar.x * uNearFar.y / (uNearFar.y - depth * (uNearFar.y - uNearFar.x));
}

void main()
{
    ivec2 texel = ivec2(gl_FragCoord.xy) * uFactor;
    float scene = Linear(texelFetch(uSceneDepth, texel, 0).r);
    float z = Linear(gl_FragCoord.z);
    float tolerance = 2.0 * fwidth(z) + 0.001 * z;
    if (z > scene + tolerance)
        discard;
    float a = clamp(tint.a, 0.0, 1.0) * uStrength;
    Scale = vec4(0.5 * mix(vec3(1.0), tint.rgb, a), 1.0);
    Add = vec4(0.5 * tint.rgb * a, 1.0);
}
