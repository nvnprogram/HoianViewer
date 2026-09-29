using System.Collections.Generic;
using System.Linq;
using BfresLibrary;
using OpenTK;
using PlayerViewer.Phive;

namespace PlayerViewer.Rigging
{
    /// <summary>One shape's vertices at the rest pose with their skin influences, as skeleton bone indices.</summary>
    public class MeshPart
    {
        public string Name;
        public int ShapeIndex;
        public int SkinCount;

        /// <summary>The bone a rigid shape (skin count 0) is bound to.</summary>
        public int ShapeBone;

        /// <summary>Hidden by its visibility bone, like the headgear shells of an afro.</summary>
        public bool Hidden;

        public Vector3[] Rest;
        public int[][] Bones;
        public float[][] Weights;
        public int[] Triangles;
    }

    /// <summary>
    /// A bfres model's skeleton at rest and its shapes skinned to it, read without GL. Positions
    /// are model space, which for a hair model is the head bone's frame.
    /// </summary>
    public class SkinnedMesh
    {
        public List<AuthorBone> Bones = new();
        public List<MeshPart> Parts = new();

        public int BoneIndex(string name) => BoneTree.IndexOf(Bones, name);

        /// <summary>The visibility bones of the shells a hair shows under headgear instead of its Base shape.</summary>
        public static readonly string[] HeadgearShells =
        {
            "Cap",
            "Fullface",
            "Headband",
            "HeadphoneA",
        };

        public static SkinnedMesh FromModel(Model model)
        {
            var mesh = new SkinnedMesh();
            var bones = model.Skeleton.Bones.Values.ToList();
            var world = ModelSkin.RestWorlds(bones);
            for (int i = 0; i < bones.Count; i++)
                mesh.Bones.Add(new AuthorBone(bones[i].Name, bones[i].ParentIndex, world[i]));

            //The afro hairs carry a shell per kind of headgear, switched on by the headgear worn;
            //without one only the Base shell shows.
            var shapeBones = model.Shapes.Values.Select(x => bones[x.BoneIndex].Name).ToHashSet();
            bool hasBase = shapeBones.Contains("Base");

            var skeleton = model.Skeleton;
            int smoothCount = skeleton.InverseModelMatrices?.Count ?? 0;
            var palette = skeleton.MatrixToBoneList;
            int shapeIndex = -1;
            foreach (var shape in model.Shapes.Values)
            {
                shapeIndex++;
                var buffer = ModelSkin.SkinAttributes(model, shape);
                var p0 = buffer.Positions;
                if (p0 == null)
                    continue;
                var slots = new List<int>();
                var slotWeights = new List<float>();
                int count = p0.Data.Length;
                int skin = shape.VertexSkinCount;
                var part = new MeshPart
                {
                    Name = shape.Name,
                    ShapeIndex = shapeIndex,
                    SkinCount = skin,
                    ShapeBone = shape.BoneIndex,
                    Hidden =
                        shape.BoneIndex < bones.Count
                        && (
                            !bones[shape.BoneIndex].Visible
                            || (hasBase && HeadgearShells.Contains(bones[shape.BoneIndex].Name))
                        ),
                    Rest = new Vector3[count],
                    Bones = new int[count][],
                    Weights = new float[count][],
                };
                for (int v = 0; v < count; v++)
                {
                    var p = new Vector3(p0.Data[v].X, p0.Data[v].Y, p0.Data[v].Z);
                    var ids = new List<int>();
                    var ws = new List<float>();
                    var rest = Vector3.Zero;
                    if (skin == 0)
                    {
                        ids.Add(shape.BoneIndex);
                        ws.Add(1);
                        rest = Vector3.TransformPosition(p, world[shape.BoneIndex]);
                    }
                    ModelSkin.Influences(
                        skin,
                        buffer.Indices,
                        buffer.Weights,
                        v,
                        slots,
                        slotWeights,
                        palette?.Count ?? 0
                    );
                    for (int k = 0; k < slots.Count; k++)
                    {
                        int slot = slots[k];
                        float w = slotWeights[k];
                        int bone = palette[slot];
                        ids.Add(bone);
                        ws.Add(w);
                        //A smooth matrix skins a model space vertex; a rigid one a bone local vertex.
                        rest +=
                            (slot < smoothCount ? p : Vector3.TransformPosition(p, world[bone]))
                            * w;
                    }
                    float sum = ws.Sum();
                    if (sum > 0)
                    {
                        for (int j = 0; j < ws.Count; j++)
                            ws[j] /= sum;
                        rest /= sum;
                    }
                    part.Rest[v] = rest;
                    part.Bones[v] = ids.ToArray();
                    part.Weights[v] = ws.ToArray();
                }
                var lod = shape.Meshes[0];
                part.Triangles = lod.GetIndices().Select(x => (int)(x + lod.FirstVertex)).ToArray();
                mesh.Parts.Add(part);
            }
            return mesh;
        }
    }
}
