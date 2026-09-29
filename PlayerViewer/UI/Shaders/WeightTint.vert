#version 330

layout (location = 0) in vec3 vPositon;
layout (location = 6) in vec4 vBoneWeight;
layout (location = 7) in ivec4 vBoneIndex;

uniform mat4 mtxMdl;
uniform mat4 mtxCam;

uniform mat4 bones[170];
uniform vec4 tints[170];
uniform mat4 RigidBindTransform;
uniform int RigidBone;
uniform int SkinCount;

out vec4 tint;

void main()
{
    vec4 world = vec4(vPositon, 1.0);
    if (SkinCount == 0)
    {
        world = RigidBindTransform * world;
        tint = tints[RigidBone];
    }
    else if (SkinCount == 1)
    {
        world = bones[vBoneIndex.x] * world;
        tint = tints[vBoneIndex.x];
    }
    else
    {
        vec4 p = bones[vBoneIndex.x] * world * vBoneWeight.x;
        p += bones[vBoneIndex.y] * world * vBoneWeight.y;
        p += bones[vBoneIndex.z] * world * vBoneWeight.z;
        tint = tints[vBoneIndex.x] * vBoneWeight.x + tints[vBoneIndex.y] * vBoneWeight.y
            + tints[vBoneIndex.z] * vBoneWeight.z;
        if (vBoneWeight.w < 1.0)
        {
            p += bones[vBoneIndex.w] * world * vBoneWeight.w;
            tint += tints[vBoneIndex.w] * vBoneWeight.w;
        }
        world = p;
    }
    gl_Position = mtxCam * mtxMdl * world;
}
