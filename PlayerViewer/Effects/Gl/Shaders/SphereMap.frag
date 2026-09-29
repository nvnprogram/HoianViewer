#version 400

uniform samplerCubeArray uCube;
uniform float uCubeIndex;
uniform float uSize;
uniform vec3 uRight, uUp, uBack, uToLight, uLobe;
uniform float uAlpha;
out vec4 fragColor;
void main()
{
    vec2 n2 = vec2(gl_FragCoord.x, gl_FragCoord.y) / uSize * vec2(2.0, -2.0) + vec2(-1.0, 1.0);
    float len = length(n2);
    if (len > 1.0)
        n2 /= len;
    vec3 n = vec3(n2, sqrt(max(1.0 - dot(n2, n2), 0.0)));
    vec3 r = vec3(0.0, 0.0, -1.0) + 2.0 * n.z * n;
    vec3 w = normalize(r.x * uRight + r.y * uUp + r.z * uBack);
    vec3 c = texture(uCube, vec4(w, uCubeIndex)).rgb;
    float a2 = uAlpha * uAlpha;
    float d = (0.5 + 0.5 * dot(w, uToLight)) * (a2 - 1.0) + 1.0;
    c += uLobe * (a2 / (d * d));
    fragColor = vec4(c, 1.0);
}
