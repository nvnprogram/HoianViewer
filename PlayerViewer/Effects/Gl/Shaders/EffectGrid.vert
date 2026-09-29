#version 330

layout (location = 0) in vec4 aLine;

uniform mat4 uViewProj;

out float Strength;
out vec3 World;

void main()
{
    World = vec3(aLine.x, 0.0, aLine.z);
    Strength = aLine.w;
    gl_Position = uViewProj * vec4(World, 1.0);
}
