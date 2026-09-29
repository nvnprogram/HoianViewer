#version 330 core

//A rim light as a glass edge in relief; the uv's encoding and the profile are in ui.md.
uniform float shadeLevel;
in vec4 color;
in vec2 texCoord;
out vec4 outputColor;
void main()
{
    vec2 code = floor((texCoord + 1.0) * 0.25 + 0.125);
    vec2 p = texCoord - code * 4.0;
    vec2 halfSize = 1.0 / abs(vec2(dFdx(texCoord.x), dFdy(texCoord.y)));
    float radius = min(code.x * 0.25, min(halfSize.x, halfSize.y));
    float rest = code.y / 255.0;

    vec2 q = p * halfSize;
    vec2 k = abs(q) - (halfSize - radius);
    float depth = radius - length(max(k, 0.0)) - min(max(k.x, k.y), 0.0);
    vec2 n = k.x > 0.0 && k.y > 0.0 ? normalize(k) * sign(q)
        : k.x > k.y ? vec2(sign(q.x), 0.0) : vec2(0.0, sign(q.y));

    float lit = pow(abs(n.y), 3.0);
    float level = rest + (color.a - rest) * lit;
    float glow = color.a * lit * lit;
    float shade = 1.0 - abs(n.y);
    //Held back until the edge is nearly upright, and on a shape with little straight side, as
    //a pill, it never comes to full strength.
    float straight = smoothstep(0.0, 16.0, 2.0 * (halfSize.y - radius));
    float darkLevel = shadeLevel * min(1.0, color.a * 2.0) * pow(shade, 4.5) * (0.2 + 0.8 * straight);

    //Both profiles averaged over the pixel's width along the normal, so the lines keep their
    //width round the corners.
    float footprint = abs(n.x) + abs(n.y);
    float light = 0.0, dark = 0.0;
    for (int i = 0; i < 4; i++)
    {
        float d = depth + (float(i) - 1.5) * 0.25 * footprint;
        if (d < 0.0)
            continue;
        if (d < shade)
        {
            dark += darkLevel;
            continue;
        }
        d -= shade;
        light += d < 1.25
            ? level * (0.5 + 0.5 * exp(-d / 0.45))
            : glow * 0.22 * exp(-(d - 1.25) / 4.0);
    }
    light *= 0.25;
    dark *= 0.25;
    float alpha = light + dark * (1.0 - light);
    outputColor = vec4(alpha > 0.0 ? color.rgb * light / alpha : color.rgb, alpha);
}
