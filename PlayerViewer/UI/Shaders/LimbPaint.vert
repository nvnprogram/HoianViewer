#version 330

layout (location = 0) in vec3 vPosition;
layout (location = 1) in vec4 vColour;
layout (location = 2) in float vReach;

uniform mat4 mtxMdl;
uniform mat4 mtxCam;

out vec4 tint;
out vec3 rest;
out float reach;

void main()
{
    tint = vColour;
    rest = vPosition;
    reach = vReach;
    gl_Position = mtxCam * mtxMdl * vec4(vPosition, 1.0);
}
