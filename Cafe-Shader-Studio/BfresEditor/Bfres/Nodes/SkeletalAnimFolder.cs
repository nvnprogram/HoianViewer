using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using BfresLibrary;

namespace BfresEditor
{
    public class SkeletalAnimFolder : SubSectionBase
    {
        public override string Header => "Skeletal Animations";

        public SkeletalAnimFolder(BFRES bfres, ResFile resFile, ResDict<SkeletalAnim> resDict)
        {
            //Wrapper construction expands every keyframe of every bone; anims are
            //independent so build them in parallel and add in order.
            var anims = resDict.Values.ToList();
            var wrappers = new BfresSkeletalAnim[anims.Count];
            Parallel.For(0, anims.Count, i =>
            {
                wrappers[i] = new BfresSkeletalAnim(resFile, anims[i], resFile.Name);
            });

            for (int i = 0; i < anims.Count; i++)
            {
                var node = new BfresNodeBase(anims[i].Name);
                node.Icon = "/Images/SkeletonAnimation.png";
                AddChild(node);
                node.Tag = wrappers[i];
                bfres.SkeletalAnimations.Add(wrappers[i]);
            }
        }
    }
}
