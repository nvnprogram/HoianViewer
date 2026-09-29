#version 330

in float Strength;
in vec3 World;

uniform vec3 uColor;
uniform float uExtent;

out vec4 FragColor;

void main()
{
    float fade = 1.0 - smoothstep(0.6 * uExtent, uExtent, length(World.xz));
    FragColor = vec4(uColor, Strength * fade);
}
