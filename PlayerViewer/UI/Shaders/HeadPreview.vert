#version 330

out vec2 ndc;

void main()
{
    vec2 p = vec2((gl_VertexID << 1) & 2, gl_VertexID & 2) * 2.0 - 1.0;
    ndc = p;
    gl_Position = vec4(p, 0.0, 1.0);
}
