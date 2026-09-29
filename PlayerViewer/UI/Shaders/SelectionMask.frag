#version 330

layout (location = 0) out vec4 FragColor;

uniform sampler2D uSceneDepth;
uniform vec2 uNearFar;

float Linear(float depth)
{
    return uNearFar.x * uNearFar.y / (uNearFar.y - depth * (uNearFar.y - uNearFar.x));
}

void main()
{
    float scene = Linear(texelFetch(uSceneDepth, ivec2(gl_FragCoord.xy), 0).r);
    float z = Linear(gl_FragCoord.z);
    float tolerance = 1.0 * fwidth(z) + 0.0005 * z;
    FragColor = vec4(1.0, z <= scene + tolerance ? 1.0 : 0.0, 0.0, 0.0);
}
