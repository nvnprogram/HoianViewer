#version 330

layout (location = 0, index = 0) out vec4 Add;
layout (location = 0, index = 1) out vec4 Scale;

in vec4 tint;
in vec3 rest;
in float reach;

uniform sampler2D uSceneDepth;
uniform vec2 uNearFar;
uniform int uFactor;
uniform vec4 uBrush;
uniform vec3 uBrushColour;
uniform int uSurface;

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
    float a = clamp(tint.a, 0.0, 1.0);
    vec3 colour = a > 0.0 ? tint.rgb / tint.a : vec3(0.0);
    if (uBrush.w > 0.0)
    {
        float d = uSurface != 0 ? reach : distance(rest, uBrush.xyz) / uBrush.w;
        float rim = 1.0 - smoothstep(0.0, 2.0 * fwidth(d) + 0.02, abs(d - 1.0));
        float b = max(d < 1.0 ? 0.5 : 0.0, rim);
        colour = mix(a > 0.0 ? colour : uBrushColour, uBrushColour, b);
        a = max(a, b);
    }
    Scale = vec4(0.5 * mix(vec3(1.0), colour, a), 1.0);
    Add = vec4(0.5 * colour * a, 1.0);
}
