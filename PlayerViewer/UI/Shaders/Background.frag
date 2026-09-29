#version 330
precision highp float;

//The export background image, sRGB, into the linear scene; clear texels keep the clear colour.
in vec2 TexCoords;
uniform sampler2D uTex;
out vec4 FragColor;

void main()
{
    vec4 c = texture(uTex, TexCoords);
    if (c.a < 0.004)
        discard;
    FragColor = vec4(pow(c.rgb, vec3(2.2)), 1.0);
}
