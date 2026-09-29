using System;
using System.Linq;
using PlayerViewer.Core;

namespace PlayerViewer.Rigging
{
    /// <summary>The Head_Root a hair's physics hangs from.</summary>
    public static class HeadRoot
    {
        public const string Name = "Head_Root";

        /// <summary>The model with a Head_Root added at its origin when it has none; the same array when it has one.</summary>
        public static byte[] Ensure(byte[] bytes)
        {
            var res = BfresBytes.Read(bytes);
            var model = res.Models.Values.First();
            if (model.Skeleton.Bones.ContainsKey(Name))
                return bytes;
            BoneEdit.InsertRoot(model, Name);
            Console.WriteLine("[Skeleton] Head_Root added for the physics");
            return BfresBytes.ToBytes(res);
        }
    }
}
