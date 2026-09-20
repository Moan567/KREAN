#version 330 core
in vec3 vNormal;
in vec3 vWorld;
in vec2 vUv;
in float vDepth;

uniform vec3 uColor;
uniform vec3 uFogColor;
uniform vec3 uSunDir;
uniform sampler2D uTex;
uniform bool uUseTex;
uniform int uPointLightCount;
uniform vec3 uPointLightPos[16];
uniform vec3 uPointLightColor[16];
uniform float uPointLightRange[16];
uniform float uPointLightIntensity[16];

out vec4 FragColor;

void main()
{
    vec3 n = normalize(vNormal);

    float diff = max(dot(n, normalize(uSunDir)), 0.0);
    float hemi = n.y * 0.5 + 0.5;
    float light = 0.30 + 0.30 * hemi + 0.50 * diff;
    light = floor(light * 5.0 + 0.5) / 5.0;

    // point lights - additive diffuse + attenuation
    float pointAccum = 0.0;
    vec3 pointColorAccum = vec3(0.0);
    for(int i=0;i<16;i++) {
        if(i >= uPointLightCount) break;
        vec3 lp = uPointLightPos[i];
        vec3 lc = uPointLightColor[i];
        float range = uPointLightRange[i];
        float intens = uPointLightIntensity[i];
        vec3 toLight = lp - vWorld;
        float dist = length(toLight);
        if(dist > range) continue;
        vec3 L = toLight / max(dist, 0.001);
        float ndotl = max(dot(n, L), 0.0);
        float atten = clamp(1.0 - dist / range, 0.0, 1.0);
        atten = atten * atten; // quadratic falloff
        pointAccum += ndotl * atten * intens;
        pointColorAccum += lc * ndotl * atten * intens;
    }
    // blend point lights into base light (tinted)
    light = clamp(light + pointAccum * 0.9, 0.0, 1.8);

    vec3 baseColor = uColor;
    vec3 texColor = vec3(1.0);
    if(uUseTex) texColor = texture(uTex, vUv).rgb;
    // if textured, use tex; otherwise checker
    vec3 col;
    if(uUseTex) col = baseColor * texColor;
    else {
        vec3 a = abs(n);
        vec2 uv = (a.y > a.x && a.y > a.z) ? vWorld.xz : ((a.x > a.z) ? vWorld.zy : vWorld.xy);
        float checker = mod(floor(uv.x) + floor(uv.y), 2.0);
        col = baseColor * mix(0.90, 1.0, checker);
    }
    // tint by point light color (average)
    if(pointAccum > 0.01) {
        vec3 avgPointCol = pointColorAccum / max(pointAccum, 0.001);
        // blend towards warm point color
        col = mix(col, col * avgPointCol * 1.2, clamp(pointAccum*0.6, 0.0, 0.7));
        col *= (1.0 + pointAccum*0.35);
    }
    col *= light;

    float fog = clamp((vDepth - 25.0) / 90.0, 0.0, 1.0);
    FragColor = vec4(mix(col, uFogColor, fog), 1.0);
}
