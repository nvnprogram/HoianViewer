using System;
using System.Linq;
using OpenTK;
using PlayerViewer.Phive;
using Toolbox.Core;

namespace PlayerViewer.Physics
{
    /// <summary>A cloth binding to Toolbox scene bones, one per cloth bone, resolved by the caller.</summary>
    public class SceneBoneBinding : IClothBinding
    {
        readonly STBone[] _bones;

        public SceneBoneBinding(STBone[] bones) => _bones = bones;

        public STBone this[int bone] => _bones[bone];

        public int BoneCount => _bones.Length;

        public Matrix4 GetWorld(int bone) => _bones[bone].Transform;

        public void SetWorld(int bone, Matrix4 world) => _bones[bone].Transform = world;

        int IndexOf(STBone b) => b == null ? -1 : Array.IndexOf(_bones, b);

        public int ParentOf(int bone) => IndexOf(_bones[bone].Parent);

        public int FirstChildOf(int bone) =>
            IndexOf(_bones[bone].Children.FirstOrDefault(c => IndexOf(c) >= 0));

        public Vector3 RestLocalTranslation(int bone) => _bones[bone].Position;

        public Matrix4 BindWorld(int bone) => Matrix4.Invert(_bones[bone].Inverse);
    }
}
