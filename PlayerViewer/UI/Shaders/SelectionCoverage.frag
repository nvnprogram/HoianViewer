#version 330

layout (location = 0) out vec4 FragColor;

uniform sampler2D uMask;
uniform int uFactor;

void main()
{
    ivec2 origin = ivec2(gl_FragCoord.xy) * uFactor;
    vec2 sum = vec2(0.0);
    if ((uFactor & 1) == 0)
    {
        vec2 texel = 1.0 / vec2(textureSize(uMask, 0));
        int blocks = uFactor / 2;
        for (int y = 0; y < blocks; y++)
            for (int x = 0; x < blocks; x++)
                sum += texture(uMask, (vec2(origin + 2 * ivec2(x, y)) + 1.0) * texel).rg;
        FragColor = vec4(sum / float(blocks * blocks), 0.0, 0.0);
        return;
    }
    for (int y = 0; y < uFactor; y++)
        for (int x = 0; x < uFactor; x++)
            sum += texelFetch(uMask, origin + ivec2(x, y), 0).rg;
    FragColor = vec4(sum / float(uFactor * uFactor), 0.0, 0.0);
}
