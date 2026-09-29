#version 330

layout (location = 0) in vec3 vPositon;
layout (location = 6) in vec4 vBoneWeight;
layout (location = 7) in ivec4 vBoneIndex;

uniform mat4 mtxMdl;
uniform mat4 mtxCam;

uniform mat4 bones[170];
uniform mat4 RigidBindTransform;
uniform int SkinCount;
uniform int UseSkinning;

vec4 skin(vec3 pos, ivec4 index)
{
    if (SkinCount == 1)
        return bones[index.x] * vec4(pos, 1.0);

    vec4 p = bones[index.x] * vec4(pos, 1.0) * vBoneWeight.x;
    p += bones[index.y] * vec4(pos, 1.0) * vBoneWeight.y;
    p += bones[index.z] * vec4(pos, 1.0) * vBoneWeight.z;
    if (vBoneWeight.w < 1.0)
        p += bones[index.w] * vec4(pos, 1.0) * vBoneWeight.w;
    return p;
}

void main()
{
    vec4 world = vec4(vPositon, 1.0);
    if (UseSkinning == 1)
    {
        if (SkinCount == 0)
            world = RigidBindTransform * world;
        else
            world = skin(world.xyz, vBoneIndex);
    }
    gl_Position = mtxCam * mtxMdl * world;
}
