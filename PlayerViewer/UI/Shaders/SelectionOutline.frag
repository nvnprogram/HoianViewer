#version 330

layout (location = 0) out vec4 FragColor;

uniform sampler2D uCoverage;
uniform vec3 uColor;
uniform float uHidden;

const float Width = 2.0;
const int Reach = 4;
const float ProbeLod = 4.0;

void main()
{
    ivec2 size = textureSize(uCoverage, 0);
    if (textureLod(uCoverage, gl_FragCoord.xy / vec2(size), ProbeLod).r == 0.0)
        discard;

    ivec2 p = ivec2(gl_FragCoord.xy);
    vec2 inside = texelFetch(uCoverage, p, 0).rg;

    vec2 dist = vec2(1e4);
    for (int y = -Reach; y <= Reach; y++)
    {
        for (int x = -Reach; x <= Reach; x++)
        {
            float d = length(vec2(x, y));
            if (d > Width + 2.0)
                continue;
            vec2 m = texelFetch(uCoverage, clamp(p + ivec2(x, y), ivec2(0), size - 1), 0).rg;
            if (m.r > 0.0)
                dist.x = min(dist.x, d + 0.5 - m.r);
            if (m.g > 0.0)
                dist.y = min(dist.y, d + 0.5 - m.g);
        }
    }

    vec2 outside = 1.0 - inside;
    vec2 band = clamp(Width + 0.5 - dist, 0.0, 1.0);
    vec2 rim = (clamp(Width + 1.5 - dist, 0.0, 1.0) - band) * outside;
    band *= outside;

    float a = max(band.y, band.x * uHidden);
    float dark = min(max(rim.y, rim.x * uHidden) * 0.5, 1.0 - a);
    if (a + dark <= 0.0)
        discard;
    FragColor = vec4(uColor * a, a + dark);
}
