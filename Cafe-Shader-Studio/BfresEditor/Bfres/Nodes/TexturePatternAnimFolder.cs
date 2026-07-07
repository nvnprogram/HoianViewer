using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using BfresLibrary;
using GLFrameworkEngine;

namespace BfresEditor
{
    public class TexturePatternAnimFolder : SubSectionBase
    {
        public override string Header => "Texture Pattern Animations";

        public TexturePatternAnimFolder(BFRES bfres, ResFile resFile, ResDict<MaterialAnim> resDict)
        {
            //Independent per-anim wrapper construction; parallel build, ordered add.
            var anims = resDict.Values.ToList();
            var wrappers = new BfresMaterialAnim[anims.Count];
            Parallel.For(0, anims.Count, i =>
            {
                wrappers[i] = new BfresMaterialAnim(anims[i], resFile.Name);
            });

            for (int i = 0; i < anims.Count; i++)
            {
                var node = new BfresNodeBase(anims[i].Name);
                AddChild(node);
                node.Tag = wrappers[i];
                bfres.MaterialAnimations.Add(wrappers[i]);
            }
        }
    }
}
