#version 330

//Distance is in viewport heights, so the light keeps its shape at any aspect. The
//hash dithers the ramp by half a display step to keep it from banding.
precision highp float;
in vec2 TexCoords;
uniform vec3 uCentre;
uniform vec3 uEdge;
uniform vec2 uSize;
out vec4 FragColor;
void main(){
  vec2 p = (TexCoords - vec2(0.5, 0.56)) * vec2(uSize.x / uSize.y, 1.0);
  float t = clamp(length(p * vec2(0.8, 1.0)) / 0.75, 0.0, 1.0);
  vec3 c = mix(uCentre, uEdge, t * t * (3.0 - 2.0 * t));
  float n = fract(sin(dot(gl_FragCoord.xy, vec2(12.9898, 78.233))) * 43758.5453);
  c += (n - 0.5) / 255.0;
  FragColor = vec4(pow(max(c, vec3(0.0)), vec3(2.2)), 1.0);
}
