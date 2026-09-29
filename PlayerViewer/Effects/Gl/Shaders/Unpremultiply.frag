#version 330

in vec2 TexCoords;

uniform sampler2D uColorTex;

out vec4 FragColor;

void main()
{
    vec4 c = texelFetch(uColorTex, ivec2(gl_FragCoord.xy), 0);
    vec3 encoded = pow(max(c.rgb, vec3(0.0)), vec3(1.0 / 2.2));
    float a = clamp(max(c.a, max(encoded.r, max(encoded.g, encoded.b))), 0.0, 1.0);
    FragColor = a > 0.0 ? vec4(pow(encoded / a, vec3(2.2)), a) : vec4(0.0);
}
