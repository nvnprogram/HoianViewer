using System.Collections.Generic;
using System.Linq;
using PlayerViewer.Core;
using PlayerViewer.Rigging;

namespace PlayerViewer.HairGen
{
    /// <summary>The other gender's physics authoring made to fit a model, for Copy from _F or _M.</summary>
    public static class PhysicsCopy
    {
        /// <summary>
        /// What a copy builds the generator on: this model with the partner's skeleton edits
        /// made, its mesh, and the partner's metadata with its paint matched onto that mesh.
        /// </summary>
        public sealed record Ported(byte[] Base, SkinnedMesh Mesh, PhysicsProvenance Physics);

        /// <summary>
        /// Ports <paramref name="partner"/>'s authoring, read from its model and
        /// <paramref name="meta"/>, onto <paramref name="targetBase"/>, the model this one's
        /// authoring starts from. Its skeleton edits are what its model changed against the
        /// game's own copy of it.
        /// </summary>
        public static Ported Port(
            byte[] targetBase,
            Romfs romfs,
            string partner,
            byte[] partnerModel,
            PhysicsProvenance meta,
            List<string> report
        )
        {
            var partnerBase = PhysicsProvenance.BaseModel(
                partnerModel,
                meta.GeneratedBones,
                report
            );
            var partnerFirst = BfresBytes.FirstModel(partnerBase);
            var sourceMesh = SkinnedMesh.FromModel(partnerFirst);
            meta.ApplyRest(sourceMesh);

            var stock = new Romfs(romfs.Root, null, false, romfs.SdodrRoot).ReadModel(partner);
            if (stock != null)
            {
                var removed = meta
                    .Limbs.Where(l => l.Replaced != null)
                    .SelectMany(l => l.Replaced)
                    .ToHashSet();
                var original = BfresBytes.FirstModel(stock);
                var changes = SkeletonEdits.Diff(original, partnerFirst, removed);
                if (changes.Count > 0)
                {
                    var res = BfresBytes.Read(targetBase);
                    SkeletonEdits.Apply(res, res.Models.Values.First(), original, changes, report);
                    targetBase = BfresBytes.ToBytes(res);
                }
            }

            var targetMesh = SkinnedMesh.FromModel(BfresBytes.FirstModel(targetBase));
            var ported = PhysicsPort.ForModel(meta, sourceMesh, targetMesh, report);
            return new Ported(targetBase, targetMesh, ported);
        }
    }
}
