#version 330
precision highp float;

uniform sampler2D uNoise;
uniform sampler2D uGlow;
uniform sampler2D uFloral;
uniform vec2 uRes;
uniform vec3 uBg;
uniform vec3 uFrost;
uniform vec3 uFloral0;
uniform vec3 uFloral1;
uniform float uSheenAlpha;
uniform float uFloralAlpha;
uniform vec2 uDrift0;
uniform vec2 uDrift1;
uniform vec2 uNoiseScroll;
uniform float uWobble;

out vec4 FragColor;

vec3 toSrgb(vec3 c)
{
    c = clamp(c, 0.0, 1.0);
    return mix(c * 12.92, 1.055 * pow(c, vec3(1.0 / 2.4)) - 0.055, step(vec3(0.0031308), c));
}

float floral(vec2 p, float tilePx, vec2 drift, float scale)
{
    vec2 d = texture(uNoise, vec2(0.5) + p / (41.4815 * scale)).rg * 0.02 - 0.01;
    return texture(uFloral, vec2(0.281, 0.719) + p / (tilePx * scale) + drift + d).a;
}

void main()
{
    vec2 p = vec2(gl_FragCoord.x, uRes.y - gl_FragCoord.y);
    vec2 box = p - 0.5 * uRes;

    vec2 n = texture(uNoise, vec2(0.5, 0.938) + vec2(0.000089285716, -0.000089285716) * box + uNoiseScroll).rg;
    vec2 wobble = (n * 0.007 - 0.0035) * uWobble * vec2(3210.5, 2368.4);
    float lobe = texture(uGlow, vec2(0.5) + vec2(0.00031147541, 0.38 / uRes.y) * (box + wobble)).a;
    vec3 c = mix(uBg, uFrost, clamp(lobe * uSheenAlpha, 0.0, 1.0));

    float scale = 1.25;
    float a0 = clamp(floral(p, 213.333, uDrift0, scale) * 0.2353 * uFloralAlpha, 0.0, 1.0);
    float a1 = clamp(floral(p, 160.0, uDrift1, scale) * 0.3922 * uFloralAlpha, 0.0, 1.0);
    float over = 150.0;
    float vg0 = mix(0.55686278, 1.0, clamp((p.y + over) / (uRes.y + 2.0 * over), 0.0, 1.0));

    vec4 acc = vec4(c, 1.0) + vec4(uFloral0 * vg0 * a0, a0) + vec4(uFloral1 * a1, a1);
    FragColor = vec4(min(vec3(1.0), toSrgb(acc.rgb / acc.a) * acc.a), 1.0);
}
