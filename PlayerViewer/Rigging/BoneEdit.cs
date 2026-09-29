using System;
using System.Collections.Generic;
using System.Linq;
using BfresLibrary;
using BfresLibrary.Helpers;
using Syroot.Maths;
using Matrix3 = OpenTK.Matrix3;
using Matrix4 = OpenTK.Matrix4;
using Quaternion = OpenTK.Quaternion;
using Vector3 = OpenTK.Vector3;

namespace PlayerViewer.Rigging
{
    /// <summary>
    /// A bone's rest transform as the file keeps it: translation, rotation as Euler XYZ radians
    /// (a quaternion bone's converted) and scale.
    /// </summary>
    public readonly record struct BoneSrt(Vector3 Translation, Vector3 Rotation, Vector3 Scale);

    /// <summary>
    /// Edits of a bfres model's skeleton that leave the file consistent: every smooth bone's
    /// inverse bind matrix is the inverse of its rest world, the transform flags match the
    /// values, and a rename reaches every name the file keeps for the bone.
    /// </summary>
    public static class BoneEdit
    {
        public static BoneSrt Read(Bone bone)
        {
            var r = bone.Rotation;
            var rotation =
                bone.FlagsRotation == BoneFlagsRotation.EulerXYZ
                    ? new Vector3(r.X, r.Y, r.Z)
                    : ModelSkin.EulerXYZ(
                        Core.BoneMath.Rotation(new Quaternion(r.X, r.Y, r.Z, r.W))
                    );
            return new BoneSrt(
                new Vector3(bone.Position.X, bone.Position.Y, bone.Position.Z),
                rotation,
                new Vector3(bone.Scale.X, bone.Scale.Y, bone.Scale.Z)
            );
        }

        /// <summary>Why a bone cannot take a name, or null when it can.</summary>
        public static string NameProblem(Model model, int index, string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return "A bone needs a name.";
            if (name != name.Trim())
                return "The name starts or ends with a space.";
            var bones = model.Skeleton.Bones.Values.ToList();
            for (int i = 0; i < bones.Count; i++)
                if (i != index && bones[i].Name == name)
                    return $"Bone {i} is already called {name}.";
            return null;
        }

        /// <summary>
        /// Renames a bone and every name the file keeps for it: the skeleton's dictionary and the
        /// bone curves of the file's skeletal and bone visibility animations. Returns how many
        /// animation curves were renamed.
        /// </summary>
        public static int Rename(ResFile res, Model model, int index, string name)
        {
            if (NameProblem(model, index, name) is string problem)
                throw new ArgumentException(problem);
            var bone = model.Skeleton.Bones[index];
            string old = bone.Name;
            if (old == name)
                return 0;
            model.Skeleton.Bones.Rename(old, name);
            bone.Name = name;
            int curves = 0;
            foreach (var anim in res.SkeletalAnims.Values)
            foreach (var boneAnim in anim.BoneAnims ?? new List<BoneAnim>())
                if (boneAnim.Name == old)
                {
                    boneAnim.Name = name;
                    curves++;
                }
            foreach (var anim in res.BoneVisibilityAnims.Values)
                if (anim.Names != null)
                    for (int i = 0; i < anim.Names.Count; i++)
                        if (anim.Names[i] == old)
                        {
                            anim.Names[i] = name;
                            curves++;
                        }
            return curves;
        }

        /// <summary>
        /// Gives a bone a new rest transform. With <paramref name="moveMesh"/> the surface the bone
        /// and the bones below it carry moves with them: its vertices are posed and written back.
        /// Without, the surface stays and only the bones move: vertices kept in a moved bone's
        /// space are written into its new space. Either way every moved smooth bone's inverse bind
        /// matrix becomes the inverse of its new rest world, and the bounds of the shapes it
        /// touches are made again. Components equal to the stored ones are left exactly as stored.
        /// </summary>
        public static void SetTransform(Model model, int index, BoneSrt srt, bool moveMesh)
        {
            var skeleton = model.Skeleton;
            var bones = skeleton.Bones.Values.ToList();
            var bone = bones[index];
            var oldWorld = ModelSkin.RestWorlds(bones);
            var current = Read(bone);

            bone.Position = new Vector3F(srt.Translation.X, srt.Translation.Y, srt.Translation.Z);
            bone.Scale = new Vector3F(srt.Scale.X, srt.Scale.Y, srt.Scale.Z);
            if (srt.Rotation != current.Rotation)
            {
                if (bone.FlagsRotation == BoneFlagsRotation.EulerXYZ)
                    bone.Rotation = new Vector4F(
                        srt.Rotation.X,
                        srt.Rotation.Y,
                        srt.Rotation.Z,
                        bone.Rotation.W
                    );
                else
                {
                    var q = Toolbox.Core.STMath.FromEulerAngles(srt.Rotation).Normalized();
                    bone.Rotation = new Vector4F(q.X, q.Y, q.Z, q.W);
                }
            }
            UpdateFlags(bone);

            var newWorld = ModelSkin.RestWorlds(bones);
            var moved = new bool[bones.Count];
            bool any = false;
            for (int i = 0; i < bones.Count; i++)
                any |= moved[i] = oldWorld[i] != newWorld[i];
            if (!any)
                return;

            var inverses = skeleton.InverseModelMatrices ?? new List<Matrix3x4>();
            var oldInverse = inverses.Select(ModelSkin.ToMatrix4).ToList();
            for (int i = 0; i < bones.Count; i++)
            {
                int slot = bones[i].SmoothMatrixIndex;
                if (moved[i] && slot >= 0 && slot < inverses.Count)
                    inverses[slot] = ModelSkin.ToMatrix3x4(Matrix4.Invert(newWorld[i]));
            }

            var palette = skeleton.MatrixToBoneList ?? new List<ushort>();
            int smoothCount = inverses.Count;
            var done = new Dictionary<int, Vector3[]>();
            foreach (var shape in model.Shapes.Values)
            {
                if (!done.TryGetValue(shape.VertexBufferIndex, out var rest))
                {
                    rest = MoveVertices(
                        model,
                        shape,
                        palette,
                        smoothCount,
                        oldInverse,
                        oldWorld,
                        newWorld,
                        moved,
                        moveMesh
                    );
                    done[shape.VertexBufferIndex] = rest;
                }
                bool touched =
                    rest != null
                    || moved[shape.BoneIndex]
                    || (shape.SkinBoneIndices?.Any(b => b < moved.Length && moved[b]) ?? false);
                if (touched)
                    UpdateBounds(
                        model,
                        shape,
                        rest ?? RestPositions(model, shape, palette, smoothCount, newWorld),
                        palette,
                        smoothCount,
                        newWorld
                    );
            }
        }

        /// <summary>
        /// The transform flags of a bone's values, exactly as the stock files set them. The
        /// hierarchy flags are left alone: stock files keep them clear and the runtime works them
        /// out.
        /// </summary>
        public static void UpdateFlags(Bone bone)
        {
            var t = bone.Position;
            var r = bone.Rotation;
            var s = bone.Scale;
            var flags = bone.FlagsTransform & BoneFlagsTransform.SegmentScaleCompensate;
            if (t.X == 0 && t.Y == 0 && t.Z == 0)
                flags |= BoneFlagsTransform.TranslateZero;
            bool rotZero =
                bone.FlagsRotation == BoneFlagsRotation.EulerXYZ
                    ? r.X == 0 && r.Y == 0 && r.Z == 0
                    : r.X == 0 && r.Y == 0 && r.Z == 0 && r.W == 1;
            if (rotZero)
                flags |= BoneFlagsTransform.RotateZero;
            if (s.X == s.Y && s.Y == s.Z)
                flags |= BoneFlagsTransform.ScaleUniform;
            if (s.X * s.Y * s.Z == 1)
                flags |= BoneFlagsTransform.ScaleVolumeOne;
            bone.FlagsTransform = flags;
        }

        //How one vertex is bound: to a bone's space (rigid), or skinned from model space.
        struct Binding
        {
            public bool Rigid,
                Mixed;
            public int Bone;
            public int[] Slots;
            public float[] Weights;
        }

        static Binding Bind(
            Shape shape,
            VertexBufferHelperAttrib[] indices,
            VertexBufferHelperAttrib[] weights,
            IList<ushort> palette,
            int smoothCount,
            int v
        )
        {
            int skin = shape.VertexSkinCount;
            if (skin == 0)
                return new Binding { Rigid = true, Bone = shape.BoneIndex };
            var slots = new List<int>();
            var ws = new List<float>();
            ModelSkin.Influences(skin, indices, weights, v, slots, ws, palette.Count);
            if (slots.Count == 0)
                return new Binding { Mixed = true };
            bool anyRigid = slots.Any(x => x >= smoothCount);
            if (anyRigid && slots.Count == 1)
                return new Binding { Rigid = true, Bone = palette[slots[0]] };
            float sum = ws.Sum();
            return new Binding
            {
                Mixed = anyRigid,
                Slots = slots.ToArray(),
                Weights = ws.Select(w => w / sum).ToArray(),
            };
        }

        /// <summary>
        /// Writes one vertex buffer's moved vertices. Returns their model space rest positions
        /// after the edit when any changed, else null and the buffer is left alone.
        /// </summary>
        static Vector3[] MoveVertices(
            Model model,
            Shape shape,
            IList<ushort> palette,
            int smoothCount,
            List<Matrix4> oldInverse,
            Matrix4[] oldWorld,
            Matrix4[] newWorld,
            bool[] moved,
            bool moveMesh
        )
        {
            var skin = ModelSkin.SkinAttributes(model, shape);
            var p0 = skin.Positions;
            if (p0 == null)
                return null;
            var normals = new[] { skin.Attribute("_n0") };
            var directions = new[] { skin.Attribute("_t0"), skin.Attribute("_b0") };
            int count = p0.Data.Length;
            var rest = new Vector3[count];
            bool changed = false;
            for (int v = 0; v < count; v++)
            {
                var bind = Bind(shape, skin.Indices, skin.Weights, palette, smoothCount, v);
                var p = new Vector3(p0.Data[v].X, p0.Data[v].Y, p0.Data[v].Z);
                //The change of the vertex's frame, as a matrix on its stored values. A vertex
                //mixing rigid and smooth slots has no one frame and is left as it is.
                Matrix4? change = null;
                if (bind.Rigid)
                {
                    if (!moveMesh && moved[bind.Bone])
                        change = oldWorld[bind.Bone] * Matrix4.Invert(newWorld[bind.Bone]);
                }
                else if (!bind.Mixed && moveMesh && bind.Slots.Any(s => moved[palette[s]]))
                {
                    var sum = new Matrix4();
                    for (int k = 0; k < bind.Slots.Length; k++)
                    {
                        int b = palette[bind.Slots[k]];
                        var m = moved[b]
                            ? oldInverse[bind.Slots[k]] * newWorld[b]
                            : Matrix4.Identity;
                        sum += m * bind.Weights[k];
                    }
                    change = sum;
                }
                if (change is Matrix4 c)
                {
                    changed = true;
                    p = Vector3.TransformPosition(p, c);
                    p0.Data[v] = new Vector4F(p.X, p.Y, p.Z, p0.Data[v].W);
                    var linear = new Matrix3(c);
                    var normal = Matrix3.Transpose(Matrix3.Invert(linear));
                    foreach (var a in normals)
                        if (a != null)
                            a.Data[v] = Turn(a.Data[v], normal);
                    foreach (var a in directions)
                        if (a != null)
                            a.Data[v] = Turn(a.Data[v], linear);
                }
                rest[v] = bind.Rigid ? Vector3.TransformPosition(p, newWorld[bind.Bone]) : p;
            }
            if (!changed)
                return null;
            model.VertexBuffers[shape.VertexBufferIndex] = ModelSkin.Rebuilt(skin.Helper);
            return rest;
        }

        static Vector4F Turn(Vector4F d, Matrix3 m)
        {
            var x = new Vector3(d.X, d.Y, d.Z);
            float length = x.Length;
            x = new Vector3(
                x.X * m.M11 + x.Y * m.M21 + x.Z * m.M31,
                x.X * m.M12 + x.Y * m.M22 + x.Z * m.M32,
                x.X * m.M13 + x.Y * m.M23 + x.Z * m.M33
            );
            if (x.LengthSquared > 1e-12f)
                x = x.Normalized() * length;
            return new Vector4F(x.X, x.Y, x.Z, d.W);
        }

        /// <summary>The model space rest position of every vertex of a shape, bound as the file binds it.</summary>
        static Vector3[] RestPositions(
            Model model,
            Shape shape,
            IList<ushort> palette,
            int smoothCount,
            Matrix4[] world
        )
        {
            var skin = ModelSkin.SkinAttributes(model, shape);
            var p0 = skin.Positions;
            if (p0 == null)
                return Array.Empty<Vector3>();
            var rest = new Vector3[p0.Data.Length];
            for (int v = 0; v < rest.Length; v++)
            {
                var bind = Bind(shape, skin.Indices, skin.Weights, palette, smoothCount, v);
                var p = new Vector3(p0.Data[v].X, p0.Data[v].Y, p0.Data[v].Z);
                rest[v] = bind.Rigid ? Vector3.TransformPosition(p, world[bind.Bone]) : p;
            }
            return rest;
        }

        /// <summary>
        /// The shape's bounds in its bone's space, and a sphere per skin bone in that bone's bind
        /// space around the vertices it weights, as the rig writer makes them.
        /// </summary>
        static void UpdateBounds(
            Model model,
            Shape shape,
            Vector3[] rest,
            IList<ushort> palette,
            int smoothCount,
            Matrix4[] world
        )
        {
            if (rest.Length == 0)
                return;
            ModelSkin.SetBounds(shape, rest, world[shape.BoneIndex]);

            var skinBones = shape.SkinBoneIndices;
            if (
                skinBones == null
                || shape.BoundingRadiusList == null
                || shape.BoundingRadiusList.Count != skinBones.Count
            )
                return;
            var skin = ModelSkin.SkinAttributes(model, shape);
            shape.BoundingRadiusList = ModelSkin.BoneSpheres(
                skinBones,
                rest,
                b => world[b],
                v =>
                {
                    var bind = Bind(shape, skin.Indices, skin.Weights, palette, smoothCount, v);
                    return bind.Rigid
                        ? new[] { bind.Bone }
                        : bind.Slots?.Select(s => (int)palette[s]) ?? Enumerable.Empty<int>();
                }
            );
        }

        /// <summary>
        /// Takes a bone out: its children hang from its parent where they were, and what it
        /// weighted goes to the parent, or for a root to the first root left. Returns why it cannot
        /// go, else null.
        /// </summary>
        public static string Delete(Model model, int index)
        {
            var bones = model.Skeleton.Bones.Values.ToList();
            if (bones.Count < 2)
                return "It is the model's only bone.";
            var bone = bones[index];
            if (
                model.Shapes.Values.FirstOrDefault(s =>
                    s.VertexSkinCount == 0 && s.BoneIndex == index
                ) is
                { } whole
            )
                return $"{whole.Name} is attached to it as a whole.";
            var world = ModelSkin.RestWorlds(bones);
            int parent = bone.ParentIndex;
            for (int i = 0; i < bones.Count; i++)
                if (bones[i].ParentIndex == index)
                    Reparent(
                        bones[i],
                        world[i],
                        parent,
                        parent >= 0 ? world[parent] : Matrix4.Identity
                    );
            int heir =
                parent >= 0
                    ? parent
                    : Enumerable
                        .Range(0, bones.Count)
                        .First(i => i != index && bones[i].ParentIndex < 0);
            foreach (var shape in model.Shapes.Values)
                if (shape.BoneIndex == index)
                    shape.BoneIndex = (ushort)heir;
            var left = RigWriter.RemoveWithWeights(model, new HashSet<string> { bone.Name }, heir);
            return left.Count > 0 ? "Something still uses it." : null;
        }

        /// <summary>
        /// Adds a head bone as the first bone, at the rest every stock hair's Head_Root has (the
        /// origin, turned a quarter about X and Z), the parent of every root, which stays where it
        /// was, and weighting nothing: for a model whose own was deleted.
        /// </summary>
        public static void InsertRoot(Model model, string name)
        {
            var skeleton = model.Skeleton;
            var bones = skeleton.Bones.Values.ToList();
            var world = ModelSkin.RestWorlds(bones);
            var root = ModelSkin.NewBone(name, -1, BoneFlagsRotation.EulerXYZ);
            root.Rotation = new Vector4F(MathF.PI / 2, 0, MathF.PI / 2, 1);
            UpdateFlags(root);
            var rootWorld = ModelSkin.RestWorlds(new List<Bone> { root })[0];
            var dict = new ResDict<Bone>();
            dict.Add(root.Name, root);
            for (int i = 0; i < bones.Count; i++)
            {
                var bone = bones[i];
                if (bone.ParentIndex < 0)
                    Reparent(bone, world[i], 0, rootWorld);
                else
                    bone.ParentIndex = (short)(bone.ParentIndex + 1);
                if (bone.BillboardIndex >= 0)
                    bone.BillboardIndex++;
                dict.Add(bone.Name, bone);
            }
            skeleton.Bones = dict;
            if (skeleton.MatrixToBoneList != null)
                skeleton.MatrixToBoneList = skeleton
                    .MatrixToBoneList.Select(b => (ushort)(b + 1))
                    .ToList();
            if (skeleton.MirroredBoneIndices is { } mirrored && mirrored.Length == bones.Count)
                skeleton.MirroredBoneIndices = mirrored
                    .Select(b => (ushort)(b + 1))
                    .Prepend((ushort)0)
                    .ToArray();
            foreach (var shape in model.Shapes.Values)
            {
                shape.BoneIndex++;
                if (shape.SkinBoneIndices != null)
                    shape.SkinBoneIndices = shape
                        .SkinBoneIndices.Select(b => (ushort)(b + 1))
                        .ToList();
            }
        }

        //A bone hung from another parent at the same rest world.
        static void Reparent(Bone bone, Matrix4 world, int parent, Matrix4 parentWorld)
        {
            var local = world * Matrix4.Invert(parentWorld);
            bone.ParentIndex = (short)parent;
            ModelSkin.SetLocal(bone, world, parentWorld);
            bone.Scale = new Vector3F(
                local.Row0.Xyz.Length,
                local.Row1.Xyz.Length,
                local.Row2.Xyz.Length
            );
            UpdateFlags(bone);
        }

        /// <summary>How many vertices each bone weights, over every shape of the model and every detail level.</summary>
        public static int[] VertexCounts(Model model)
        {
            var mesh = SkinnedMesh.FromModel(model);
            var counts = new int[mesh.Bones.Count];
            foreach (var part in mesh.Parts)
                for (int v = 0; v < part.Bones.Length; v++)
                for (int k = 0; k < part.Bones[v].Length; k++)
                    if (part.Weights[v][k] > 0 && part.Bones[v][k] < counts.Length)
                        counts[part.Bones[v][k]]++;
            return counts;
        }
    }
}
