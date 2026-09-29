#version 330

layout (location = 0) out vec4 FragColor;

in vec2 ndc;

uniform mat4 uViewProj;
uniform mat4 uInvViewProj;
uniform mat4 uToUnit;
uniform vec3 uEye;
uniform sampler2D uSceneDepth;
uniform vec2 uNearFar;
uniform int uFactor;

float Linear(float depth)
{
    return uNearFar.x * uNearFar.y / (uNearFar.y - depth * (uNearFar.y - uNearFar.x));
}

void main()
{
    vec4 alongH = uInvViewProj * vec4(ndc, 0.0, 1.0);
    vec3 dirW = normalize(alongH.xyz / alongH.w - uEye);

    vec3 o = (uToUnit * vec4(uEye, 1.0)).xyz;
    vec3 d = mat3(uToUnit) * dirW;
    float a = dot(d, d);
    float b = dot(o, d);
    float c = dot(o, o) - 1.0;
    float disc = b * b - a * c;
    if (disc < 0.0 || c < 0.0)
        discard;
    float t = (-b - sqrt(disc)) / a;
    if (t <= 0.0)
        discard;

    vec3 hitW = uEye + dirW * t;
    vec4 clip = uViewProj * vec4(hitW, 1.0);
    float z = Linear(clip.z / clip.w * 0.5 + 0.5);
    float scene = Linear(texelFetch(uSceneDepth, ivec2(gl_FragCoord.xy) * uFactor, 0).r);
    if (z > scene + 0.001 * z)
        discard;

    vec3 normal = normalize(transpose(mat3(uToUnit)) * (o + d * t));
    float lit = 0.35 + 0.65 * max(dot(normal, -dirW), 0.0);
    FragColor = vec4(vec3(0.93, 0.72, 0.6) * lit, 1.0);
}
