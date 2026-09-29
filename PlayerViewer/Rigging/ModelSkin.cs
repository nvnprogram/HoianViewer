using System;
using System.Collections.Generic;
using System.Linq;
using BfresLibrary;
using BfresLibrary.Helpers;
using PlayerViewer.Core;
using Syroot.Maths;
using Matrix4 = OpenTK.Matrix4;
using Quaternion = OpenTK.Quaternion;
using Vector3 = OpenTK.Vector3;

namespace PlayerViewer.Rigging
{
    /// <summary>A shape's vertex buffer opened for its skinning: positions and both sets of four indices and weights.</summary>
    public sealed record SkinBuffer(
        VertexBufferHelper Helper,
        VertexBufferHelperAttrib Positions,
        VertexBufferHelperAttrib[] Indices,
        VertexBufferHelperAttrib[] Weights
    )
    {
        /// <summary>An attribute by name, or null.</summary>
        public VertexBufferHelperAttrib Attribute(string name) =>
            Helper.Attributes.FirstOrDefault(a => a.Name == name);
    }

    /// <summary>
    /// A bfres model's skeleton and skinning as the rig writer and the skeleton edits both read
    /// and write them: bone rest worlds, the matrix forms, vertex influences and bounds.
    /// </summary>
    public static class ModelSkin
    {
        /// <summary>A bone's rest rotation; an Euler bone's converted.</summary>
        public static Quaternion Rotation(Bone b)
        {
            var r = b.Rotation;
            return b.FlagsRotation == BoneFlagsRotation.EulerXYZ
                ? Toolbox.Core.STMath.FromEulerAngles(new Vector3(r.X, r.Y, r.Z))
                : new Quaternion(r.X, r.Y, r.Z, r.W);
        }

        /// <summary>The rest world matrix of every bone, as <see cref="BoneMath.RestWorlds"/> composes it.</summary>
        public static Matrix4[] RestWorlds(IList<Bone> bones) =>
            BoneMath.RestWorlds(
                bones
                    .Select(b => new BoneRest(
                        new Vector3(b.Scale.X, b.Scale.Y, b.Scale.Z),
                        Rotation(b),
                        new Vector3(b.Position.X, b.Position.Y, b.Position.Z),
                        b.ParentIndex,
                        b.ApplySegmentScaleCompensate
                    ))
                    .ToList()
            );

        //The file's 3x4 matrices hold a column vector transform; OpenTK's are row vector.
        public static Matrix4 ToMatrix4(Matrix3x4 m) =>
            new(
                m.M11,
                m.M21,
                m.M31,
                0,
                m.M12,
                m.M22,
                m.M32,
                0,
                m.M13,
                m.M23,
                m.M33,
                0,
                m.M14,
                m.M24,
                m.M34,
                1
            );

        public static Matrix3x4 ToMatrix3x4(Matrix4 m) =>
            new(m.M11, m.M21, m.M31, m.M41, m.M12, m.M22, m.M32, m.M42, m.M13, m.M23, m.M33, m.M43);

        /// <summary>Euler angles, X then Y then Z, of a rotation matrix in row vector form.</summary>
        public static Vector3 EulerXYZ(Matrix4 m)
        {
            float b = MathF.Asin(Math.Clamp(-m.M13, -1f, 1f));
            float a,
                c;
            if (MathF.Abs(MathF.Cos(b)) > 1e-6f)
            {
                a = MathF.Atan2(m.M23, m.M33);
                c = MathF.Atan2(m.M12, m.M11);
            }
            else
            {
                a = MathF.Atan2(-m.M32, m.M22);
                c = 0;
            }
            return new Vector3(a, b, c);
        }

        /// <summary>The row vector rotation matrix of Euler angles, X applied first, then Y, then Z.</summary>
        public static Matrix4 FromEulerXYZ(Vector3 euler) =>
            Matrix4.CreateRotationX(euler.X)
            * Matrix4.CreateRotationY(euler.Y)
            * Matrix4.CreateRotationZ(euler.Z);

        /// <summary>A visible bone at the origin with no rotation, unit scale and no matrices.</summary>
        public static Bone NewBone(string name, int parent, BoneFlagsRotation rotation) =>
            new()
            {
                Name = name,
                ParentIndex = (short)parent,
                Position = new Vector3F(0, 0, 0),
                Scale = new Vector3F(1, 1, 1),
                Rotation = new Vector4F(0, 0, 0, 1),
                Flags = BoneFlags.Visible,
                FlagsRotation = rotation,
                SmoothMatrixIndex = -1,
                RigidMatrixIndex = -1,
            };

        /// <summary>
        /// Sets a bone's translation and rotation to put it at a world matrix under a parent at
        /// another, as Euler XYZ or a quaternion by its rotation mode. The scale is the caller's.
        /// </summary>
        public static void SetLocal(Bone bone, Matrix4 world, Matrix4 parentWorld)
        {
            var local = world * Matrix4.Invert(parentWorld);
            var rotation = local.ClearTranslation().ClearScale();
            bone.Position = new Vector3F(local.Row3.X, local.Row3.Y, local.Row3.Z);
            if (bone.FlagsRotation == BoneFlagsRotation.EulerXYZ)
            {
                var e = EulerXYZ(rotation);
                bone.Rotation = new Vector4F(e.X, e.Y, e.Z, bone.Rotation.W);
            }
            else
            {
                var q = rotation.ExtractRotation();
                bone.Rotation = new Vector4F(q.X, q.Y, q.Z, q.W);
            }
        }

        public static SkinBuffer SkinAttributes(Model model, Shape shape)
        {
            var helper = new VertexBufferHelper(
                model.VertexBuffers[shape.VertexBufferIndex],
                Syroot.BinaryData.ByteOrder.LittleEndian
            );
            VertexBufferHelperAttrib Attribute(string name) =>
                helper.Attributes.FirstOrDefault(a => a.Name == name);
            return new SkinBuffer(
                helper,
                Attribute("_p0"),
                new[] { Attribute("_i0"), Attribute("_i1") },
                new[] { Attribute("_w0"), Attribute("_w1") }
            );
        }

        public static float Component(Vector4F v, int i) =>
            i switch
            {
                0 => v.X,
                1 => v.Y,
                2 => v.Z,
                _ => v.W,
            };

        /// <summary>
        /// A skinned vertex's palette slots and weights as stored, zero weights and slots outside
        /// the palette left out. A skin count of one carries no weights and means a weight of one;
        /// otherwise a set of four stored without weights has <paramref name="missing"/> on each.
        /// </summary>
        public static void Influences(
            int skin,
            VertexBufferHelperAttrib[] indices,
            VertexBufferHelperAttrib[] weights,
            int v,
            List<int> slots,
            List<float> ws,
            int paletteCount,
            float missing = 0
        )
        {
            slots.Clear();
            ws.Clear();
            for (int j = 0; j < skin && j < 8; j++)
            {
                var ia = indices[j / 4];
                if (ia == null)
                    break;
                int slot = (int)Component(ia.Data[v], j & 3);
                float w =
                    skin == 1 ? 1
                    : weights[j / 4] != null ? Component(weights[j / 4].Data[v], j & 3)
                    : missing;
                if (w <= 0 || slot < 0 || slot >= paletteCount)
                    continue;
                slots.Add(slot);
                ws.Add(w);
            }
        }

        /// <summary>Rewrites every palette slot of a buffer's skin indices.</summary>
        public static void MapSlots(VertexBufferHelper helper, Func<float, float> map)
        {
            foreach (var name in new[] { "_i0", "_i1" })
            {
                if (!helper.Contains(name))
                    continue;
                var data = helper[name].Data;
                for (int v = 0; v < data.Length; v++)
                    data[v] = new Vector4F(
                        map(data[v].X),
                        map(data[v].Y),
                        map(data[v].Z),
                        map(data[v].W)
                    );
            }
        }

        /// <summary>A vertex buffer from its helper, keeping the helper's skin count.</summary>
        public static VertexBuffer Rebuilt(VertexBufferHelper helper)
        {
            var vb = helper.ToVertexBuffer();
            vb.VertexSkinCount = helper.VertexSkinCount;
            return vb;
        }

        /// <summary>The shape's bounds in its bone's space, around its vertices' model space rest positions.</summary>
        public static void SetBounds(
            Shape shape,
            IReadOnlyList<Vector3> rest,
            Matrix4 shapeBoneWorld
        )
        {
            var toShape = Matrix4.Invert(shapeBoneWorld);
            var min = new Vector3(float.MaxValue);
            var max = new Vector3(float.MinValue);
            foreach (var p in rest)
            {
                var q = Vector3.TransformPosition(p, toShape);
                min = Vector3.ComponentMin(min, q);
                max = Vector3.ComponentMax(max, q);
            }
            var centre = (min + max) * 0.5f;
            var extent = (max - min) * 0.5f;
            for (int i = 0; i < shape.SubMeshBoundings.Count; i++)
                shape.SubMeshBoundings[i] = new Bounding
                {
                    Center = new Vector3F(centre.X, centre.Y, centre.Z),
                    Extent = new Vector3F(extent.X, extent.Y, extent.Z),
                };
            for (int i = 0; i < shape.RadiusArray.Count; i++)
                shape.RadiusArray[i] = extent.Length;
        }

        /// <summary>
        /// A sphere per skin bone in that bone's bind space around the vertices it carries, or a
        /// small one at its origin when it carries none.
        /// </summary>
        public static List<Vector4F> BoneSpheres(
            IEnumerable<ushort> skinBones,
            IReadOnlyList<Vector3> rest,
            Func<int, Matrix4> world,
            Func<int, IEnumerable<int>> carriedBy
        )
        {
            var byBone = new Dictionary<int, List<Vector3>>();
            for (int v = 0; v < rest.Count; v++)
                foreach (int b in carriedBy(v))
                {
                    if (!byBone.TryGetValue(b, out var list))
                        byBone[b] = list = new List<Vector3>();
                    list.Add(rest[v]);
                }
            var spheres = new List<Vector4F>();
            foreach (int b in skinBones)
            {
                if (!byBone.TryGetValue(b, out var points))
                {
                    spheres.Add(new Vector4F(0, 0, 0, 0.01f));
                    continue;
                }
                var inv = Matrix4.Invert(world(b));
                var local = points.Select(p => Vector3.TransformPosition(p, inv)).ToList();
                var bmin = local.Aggregate(new Vector3(float.MaxValue), Vector3.ComponentMin);
                var bmax = local.Aggregate(new Vector3(float.MinValue), Vector3.ComponentMax);
                var c = (bmin + bmax) * 0.5f;
                float r = local.Max(q => (q - c).Length);
                spheres.Add(new Vector4F(c.X, c.Y, c.Z, r));
            }
            return spheres;
        }
    }
}
