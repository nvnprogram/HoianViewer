using System;
using System.Collections.Generic;
using System.Linq;
using BfresLibrary;
using BfresLibrary.GX2;
using BfresLibrary.Helpers;
using Syroot.Maths;
using Matrix4 = OpenTK.Matrix4;
using Vector3 = OpenTK.Vector3;

namespace PlayerViewer.Rigging
{
    /// <summary>
    /// Writes a rig into a bfres model: the added bones with smooth matrices, new weights for
    /// every vertex welded into a node the rig reweights, and the model's bones the rig replaced
    /// taken out. A shape that was rigid becomes smooth skinned, its vertices moved into model
    /// space.
    /// </summary>
    public static class RigWriter
    {
        public static void Apply(Model model, SkinnedMesh mesh, SkinRig rig)
        {
            var skeleton = model.Skeleton;
            skeleton.InverseModelMatrices ??= new List<Matrix3x4>();
            skeleton.MatrixToBoneList ??= new List<ushort>();
            var bones = skeleton.Bones.Values.ToList();
            int oldSmooth = skeleton.InverseModelMatrices.Count;
            var eulerMode = bones.Count > 0 && bones[0].FlagsRotation == BoneFlagsRotation.EulerXYZ;

            //Bones, each with a smooth matrix after the old ones unless it skins nothing.
            var added = new List<int>();
            for (int i = rig.OriginalBoneCount; i < rig.Bones.Count; i++)
            {
                var ab = rig.Bones[i];
                var bone = ModelSkin.NewBone(
                    ab.Name,
                    ab.Parent,
                    eulerMode ? BoneFlagsRotation.EulerXYZ : BoneFlagsRotation.Quaternion
                );
                ModelSkin.SetLocal(
                    bone,
                    ab.World,
                    ab.Parent >= 0 ? rig.Bones[ab.Parent].World : Matrix4.Identity
                );
                bone.ApplySegmentScaleCompensate = true;
                bone.TransformScaleOne = true;
                skeleton.Bones.Add(bone.Name, bone);
                if (!rig.Unskinned.Contains(i))
                    added.Add(i);
            }

            //Smooth matrices: the old ones, then the added bones' and those of any old bone a
            //rewritten vertex uses that had none, then the rigid ones shifted past them.
            var allBones = skeleton.Bones.Values.ToList();
            int anchor = rig.ShapeAnchor;
            var used = new HashSet<int>(
                rig.NodeWeights.Values.SelectMany(w => w.Select(x => x.Bone))
            );
            for (int p = 0; p < mesh.Parts.Count; p++)
                if (rig.Rewrites(p))
                {
                    used.UnionWith(mesh.Parts[p].Bones.SelectMany(b => b));
                    used.Add(anchor);
                }
            foreach (int b in used.OrderBy(x => x))
                if (b < rig.OriginalBoneCount && allBones[b].SmoothMatrixIndex < 0)
                    added.Add(b);
            var palette = skeleton.MatrixToBoneList.ToList();
            var smoothPart = palette.Take(oldSmooth).ToList();
            var rigidPart = palette.Skip(oldSmooth).ToList();
            foreach (int i in added)
            {
                allBones[i].SmoothMatrixIndex = (short)smoothPart.Count;
                smoothPart.Add((ushort)i);
                skeleton.InverseModelMatrices.Add(
                    ModelSkin.ToMatrix3x4(Matrix4.Invert(rig.Bones[i].World))
                );
            }
            int shift = added.Count;
            foreach (var b in allBones)
                if (b.RigidMatrixIndex >= 0)
                    b.RigidMatrixIndex = (short)(b.RigidMatrixIndex + shift);
            skeleton.MatrixToBoneList = smoothPart.Concat(rigidPart).ToList();
            if (skeleton.MirroredBoneIndices != null && skeleton.MirroredBoneIndices.Length > 0)
                skeleton.MirroredBoneIndices = skeleton
                    .MirroredBoneIndices.Concat(
                        Enumerable
                            .Range(rig.OriginalBoneCount, rig.Bones.Count - rig.OriginalBoneCount)
                            .Select(i => (ushort)i)
                    )
                    .ToArray();

            int SmoothIndex(int bone) => allBones[bone].SmoothMatrixIndex;

            var shapes = model.Shapes.Values.ToList();
            for (int p = 0; p < mesh.Parts.Count; p++)
            {
                var part = mesh.Parts[p];
                var shape = shapes[part.ShapeIndex];
                var vb = model.VertexBuffers[shape.VertexBufferIndex];
                var helper = new VertexBufferHelper(vb, Syroot.BinaryData.ByteOrder.LittleEndian);
                if (!rig.Rewrites(p))
                {
                    if (shift > 0 && shape.VertexSkinCount > 0)
                    {
                        ModelSkin.MapSlots(helper, s => s >= oldSmooth ? s + shift : s);
                        model.VertexBuffers[shape.VertexBufferIndex] = ModelSkin.Rebuilt(helper);
                    }
                    continue;
                }
                RewriteShape(
                    model,
                    shape,
                    part,
                    rig.Graph.WeightNodeOf[p],
                    helper,
                    rig,
                    anchor,
                    SmoothIndex
                );
            }
            if (rig.Removed.Count > 0)
                RemoveBones(model, rig.Removed.Select(i => rig.Bones[i].Name).ToHashSet());
        }

        /// <summary>
        /// Takes bones out of a model with their weights, each vertex's share going to the nearest
        /// bone above that stays, or to <paramref name="fallback"/> when none does: a saved model's
        /// generated bones, before its limbs are built again. Returns the names that could not go.
        /// </summary>
        public static List<string> RemoveWithWeights(
            Model model,
            ISet<string> names,
            int fallback = 0
        )
        {
            var mesh = SkinnedMesh.FromModel(model);
            var graph = new MeshGraph(mesh);
            var rig = new SkinRig
            {
                Bones = mesh.Bones.ToList(),
                OriginalBoneCount = mesh.Bones.Count,
                Graph = graph,
            };
            int Kept(int b)
            {
                while (b >= 0 && names.Contains(mesh.Bones[b].Name))
                    b = mesh.Bones[b].Parent;
                return b >= 0 ? b : fallback;
            }
            //Of the vertices welded into a node, the first carrying a bone that goes speaks for it.
            for (int p = 0; p < mesh.Parts.Count; p++)
            {
                var part = mesh.Parts[p];
                for (int v = 0; v < part.Rest.Length; v++)
                {
                    int node = graph.WeightNodeOf[p][v];
                    if (
                        node < 0
                        || rig.NodeWeights.ContainsKey(node)
                        || !part.Bones[v].Any(b => names.Contains(mesh.Bones[b].Name))
                    )
                        continue;
                    rig.NodeWeights[node] = part.Bones[v]
                        .Zip(part.Weights[v])
                        .GroupBy(x => Kept(x.First))
                        .Select(g => (g.Key, g.Sum(x => x.Second)))
                        .ToArray();
                }
            }
            for (int b = 0; b < mesh.Bones.Count; b++)
                if (names.Contains(mesh.Bones[b].Name))
                    rig.Removed.Add(b);
            Apply(model, mesh, rig);
            var left = model.Skeleton.Bones.Keys.ToHashSet();
            return names.Where(left.Contains).ToList();
        }

        /// <summary>
        /// Takes bones out of a model: the skeleton, the matrix palette and the inverse matrices,
        /// and every index into them, in shapes and vertices. A bone something still uses (a
        /// shape, a vertex, or a bone below it) stays.
        /// </summary>
        public static void RemoveBones(Model model, ISet<string> names)
        {
            var skeleton = model.Skeleton;
            var bones = skeleton.Bones.Values.ToList();
            var palette = skeleton.MatrixToBoneList?.ToList() ?? new List<ushort>();
            int smoothCount = skeleton.InverseModelMatrices?.Count ?? 0;

            var used = new HashSet<int>();
            var buffers = new Dictionary<int, VertexBufferHelper>();
            var slots = new List<int>();
            var ws = new List<float>();
            foreach (var shape in model.Shapes.Values)
            {
                used.Add(shape.BoneIndex);
                if (shape.SkinBoneIndices != null)
                    used.UnionWith(shape.SkinBoneIndices.Select(x => (int)x));
                if (shape.VertexSkinCount == 0 || buffers.ContainsKey(shape.VertexBufferIndex))
                    continue;
                var skin = ModelSkin.SkinAttributes(model, shape);
                buffers[shape.VertexBufferIndex] = skin.Helper;
                //A slot counts only with weight: an empty one holds slot 0 at weight 0. Without
                //weights stored every slot counts.
                int count = skin.Indices[0]?.Data.Length ?? 0;
                for (int v = 0; v < count; v++)
                {
                    ModelSkin.Influences(
                        shape.VertexSkinCount,
                        skin.Indices,
                        skin.Weights,
                        v,
                        slots,
                        ws,
                        palette.Count,
                        missing: 1
                    );
                    foreach (int slot in slots)
                        used.Add(palette[slot]);
                }
            }
            var remove = Enumerable
                .Range(0, bones.Count)
                .Where(i => names.Contains(bones[i].Name) && !used.Contains(i))
                .ToHashSet();
            BoneTree.KeepAncestors(remove, bones.Count, i => bones[i].ParentIndex);
            if (remove.Count == 0)
                return;

            var map = new int[bones.Count];
            int next = 0;
            for (int i = 0; i < bones.Count; i++)
                map[i] = remove.Contains(i) ? -1 : next++;
            //The palette slots of removed bones go; the rest keep their order, smooth before rigid.
            var slotMap = new int[palette.Count];
            var newPalette = new List<ushort>();
            var newInverse = new List<Matrix3x4>();
            for (int slot = 0; slot < palette.Count; slot++)
            {
                if (map[palette[slot]] < 0)
                {
                    slotMap[slot] = -1;
                    continue;
                }
                slotMap[slot] = newPalette.Count;
                newPalette.Add((ushort)map[palette[slot]]);
                if (slot < smoothCount)
                    newInverse.Add(skeleton.InverseModelMatrices[slot]);
            }

            var dict = new ResDict<Bone>();
            for (int i = 0; i < bones.Count; i++)
            {
                if (map[i] < 0)
                    continue;
                var bone = bones[i];
                bone.ParentIndex = (short)(bone.ParentIndex >= 0 ? map[bone.ParentIndex] : -1);
                bone.SmoothMatrixIndex = (short)(
                    bone.SmoothMatrixIndex >= 0 ? slotMap[bone.SmoothMatrixIndex] : -1
                );
                bone.RigidMatrixIndex = (short)(
                    bone.RigidMatrixIndex >= 0 ? slotMap[bone.RigidMatrixIndex] : -1
                );
                if (bone.BillboardIndex >= 0 && bone.BillboardIndex < bones.Count)
                    bone.BillboardIndex = (short)map[bone.BillboardIndex];
                dict.Add(bone.Name, bone);
            }
            var mirrored = skeleton.MirroredBoneIndices;
            if (mirrored != null && mirrored.Length == bones.Count)
                skeleton.MirroredBoneIndices = Enumerable
                    .Range(0, bones.Count)
                    .Where(i => map[i] >= 0)
                    .Select(i =>
                        (ushort)(
                            mirrored[i] < bones.Count && map[mirrored[i]] >= 0
                                ? map[mirrored[i]]
                                : map[i]
                        )
                    )
                    .ToArray();
            skeleton.Bones = dict;
            skeleton.MatrixToBoneList = newPalette;
            skeleton.InverseModelMatrices = newInverse;

            foreach (var shape in model.Shapes.Values)
            {
                shape.BoneIndex = (ushort)map[shape.BoneIndex];
                if (shape.SkinBoneIndices != null)
                    shape.SkinBoneIndices = shape
                        .SkinBoneIndices.Select(x => (ushort)map[x])
                        .ToList();
            }
            if (Enumerable.Range(0, palette.Count).All(s => slotMap[s] == s))
                return;
            //An empty slot that held a removed bone's slot takes slot 0.
            float Slot(float slot) =>
                slot >= 0 && slot < slotMap.Length ? Math.Max(slotMap[(int)slot], 0) : slot;
            foreach (var (index, helper) in buffers)
            {
                ModelSkin.MapSlots(helper, Slot);
                model.VertexBuffers[index] = ModelSkin.Rebuilt(helper);
            }
        }

        static void RewriteShape(
            Model model,
            Shape shape,
            MeshPart part,
            int[] nodes,
            VertexBufferHelper helper,
            SkinRig rig,
            int anchor,
            Func<int, int> smoothIndex
        )
        {
            int count = part.Rest.Length;
            bool wasRigid = shape.VertexSkinCount < 2;

            //Influences per vertex as rig bones: the rig's new ones, or the vertex's own.
            var influences = new (int Bone, float Weight)[count][];
            for (int v = 0; v < count; v++)
                influences[v] = rig.Influences(part, v, nodes[v], anchor, wasRigid);

            //A rigid shape's vertices are in its bone's space; smooth skinning wants model space.
            if (wasRigid)
            {
                var p0 = helper["_p0"].Data;
                for (int v = 0; v < count; v++)
                    p0[v] = new Vector4F(part.Rest[v].X, part.Rest[v].Y, part.Rest[v].Z, p0[v].W);
                foreach (var name in new[] { "_n0", "_t0", "_b0" })
                {
                    if (!helper.Contains(name))
                        continue;
                    var data = helper[name].Data;
                    for (int v = 0; v < count; v++)
                    {
                        int bone =
                            shape.VertexSkinCount == 0
                                ? part.ShapeBone
                                : part.Bones[v].FirstOrDefault(anchor);
                        var d = Vector3.TransformNormal(
                            new Vector3(data[v].X, data[v].Y, data[v].Z),
                            rig.Bones[bone].World
                        );
                        if (d.LengthSquared > 1e-12f)
                            d.Normalize();
                        data[v] = new Vector4F(d.X, d.Y, d.Z, data[v].W);
                    }
                }
            }

            //Four slot indices and weights; the weight bytes sum to exactly 255.
            var indices = new Vector4F[count];
            var weights = new Vector4F[count];
            for (int v = 0; v < count; v++)
            {
                var inf = influences[v].OrderByDescending(x => x.Weight).ToArray();
                float[] ids = new float[4],
                    ws = new float[4];
                for (int k = 0; k < inf.Length; k++)
                {
                    ids[k] = smoothIndex(inf[k].Bone);
                    ws[k] = inf[k].Weight;
                }
                indices[v] = new Vector4F(ids[0], ids[1], ids[2], ids[3]);
                weights[v] = Quantize(ws);
            }
            SetAttribute(helper, "_i0", GX2AttribFormat.Format_8_8_8_8_UInt, indices);
            SetAttribute(helper, "_w0", GX2AttribFormat.Format_8_8_8_8_UNorm, weights);
            helper.VertexSkinCount = 4;
            model.VertexBuffers[shape.VertexBufferIndex] = ModelSkin.Rebuilt(helper);

            shape.VertexSkinCount = 4;
            if (wasRigid)
                shape.BoneIndex = (ushort)anchor;
            shape.SkinBoneIndices = influences
                .SelectMany(x => x.Where(i => i.Weight > 0).Select(i => (ushort)i.Bone))
                .Distinct()
                .OrderBy(x => x)
                .ToList();
            UpdateBounds(shape, part, influences, rig);

            var material = model.Materials.Values.ElementAt(shape.MaterialIndex);
            foreach (var name in new[] { "_i0", "_w0" })
                if (!material.ShaderAssign.AttribAssigns.ContainsKey(name))
                    material.ShaderAssign.AttribAssigns.Add(name, (ResString)name);
        }

        static void SetAttribute(
            VertexBufferHelper helper,
            string name,
            GX2AttribFormat format,
            Vector4F[] data
        )
        {
            if (helper.Contains(name))
            {
                var a = helper[name];
                a.Format = format;
                a.Data = data;
                return;
            }
            helper.Attributes.Add(
                new VertexBufferHelperAttrib
                {
                    Name = name,
                    Format = format,
                    BufferIndex = (byte)(helper.Attributes.Max(x => x.BufferIndex) + 1),
                    Data = data,
                }
            );
        }

        /// <summary>
        /// The shape's bounds in its bone's space, and one sphere per skin bone in that bone's
        /// bind space around the vertices it weights.
        /// </summary>
        static void UpdateBounds(
            Shape shape,
            MeshPart part,
            (int Bone, float Weight)[][] influences,
            SkinRig rig
        )
        {
            ModelSkin.SetBounds(shape, part.Rest, rig.Bones[shape.BoneIndex].World);
            shape.BoundingRadiusList = ModelSkin.BoneSpheres(
                shape.SkinBoneIndices,
                part.Rest,
                b => rig.Bones[b].World,
                v => influences[v].Where(x => x.Weight > 0).Select(x => x.Bone)
            );
        }

        /// <summary>Unorm8 weights whose bytes sum to exactly 255: floor, then the spare units to the largest remainders.</summary>
        public static Vector4F Quantize(float[] w)
        {
            float sum = w.Sum();
            int[] b = new int[4];
            float[] rem = new float[4];
            int total = 0;
            for (int k = 0; k < 4; k++)
            {
                float x = sum > 0 ? Math.Clamp(w[k] / sum, 0, 1) * 255f : (k == 0 ? 255 : 0);
                b[k] = (int)MathF.Floor(x + 1e-4f);
                rem[k] = x - b[k];
                total += b[k];
            }
            for (int spare = 255 - total; spare > 0; spare--)
            {
                int best = -1;
                for (int k = 0; k < 4; k++)
                    if (w[k] > 0 && (best < 0 || rem[k] > rem[best]))
                        best = k;
                if (best < 0)
                    best = 0;
                b[best]++;
                rem[best] = -1;
            }
            return new Vector4F(b[0] / 255f, b[1] / 255f, b[2] / 255f, b[3] / 255f);
        }
    }
}
