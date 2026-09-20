#version 330 core
layout(location = 0) in vec3 aPos;
layout(location = 1) in vec3 aNormal;
layout(location = 2) in vec2 aUv;

uniform mat4 uModel;
uniform mat4 uView;
uniform mat4 uProj;

out vec3 vNormal;
out vec3 vWorld;
out vec2 vUv;
out float vDepth;

void main()
{
    vec4 world = uModel * vec4(aPos, 1.0);
    vWorld = world.xyz;
    vNormal = mat3(transpose(inverse(uModel))) * aNormal;
    vUv = aUv;
    vec4 clip = uProj * uView * world;
    vDepth = clip.w;
    gl_Position = clip;
}
