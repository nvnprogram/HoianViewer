using System;
using System.Collections.Generic;
using System.Linq;
using BfresLibrary;
using OpenTK;
using PlayerViewer.Rigging;

namespace PlayerViewer.HairGen
{
    /// <summary>
    /// The part of the player's head that hair rests on, as points in the hair model's frame.
    /// The face and jaw are left out, so hair hanging in front of the face counts as free.
    /// </summary>
    public class HeadShape
    {
        public readonly List<Vector3> Points;

        /// <summary>The whole head and neck, face and jaw included.</summary>
        public readonly List<Vector3> Surface;

        HeadShape(List<Vector3> points, List<Vector3> surface)
        {
            Points = points;
            Surface = surface;
        }

        /// <summary>Whether a point of the head or neck (hair frame) is scalp or nape rather than face, jaw or throat.</summary>
        static bool IsScalp(Vector3 p) =>
            p.Y >= 0.12f ? !(p.Z > 0.05f && p.Y < 0.36f) : p.Z < -0.02f;

        /// <summary>Rest world matrices, in the hair's frame, of the body bones a hair's colliders ride on.</summary>
        public readonly Dictionary<string, Matrix4> BodyBones = new();

        public static readonly string[] ColliderBones = { "Arm_1_L", "Arm_1_R", "Waist" };

        /// <summary>The head vertices of a player body, those weighted mostly to its head bone, in the hair's frame.</summary>
        public static HeadShape FromBody(
            SkinnedMesh body,
            Matrix4 bodyToHair,
            string headBone = "Head"
        )
        {
            int head = body.BoneIndex(headBone);
            int neck = body.BoneIndex("Neck");
            var points = new List<Vector3>();
            var surface = new List<Vector3>();
            foreach (var part in body.Parts.Where(p => !p.Hidden))
                for (int v = 0; v < part.Rest.Length; v++)
                {
                    float w = 0;
                    for (int k = 0; k < part.Bones[v].Length; k++)
                        if (part.Bones[v][k] == head || part.Bones[v][k] == neck)
                            w += part.Weights[v][k];
                    if (w <= 0.5f)
                        continue;
                    var p = Vector3.TransformPosition(part.Rest[v], bodyToHair);
                    surface.Add(p);
                    if (IsScalp(p))
                        points.Add(p);
                }
            if (points.Count == 0)
                return Default();
            var shape = new HeadShape(points, surface);
            foreach (var name in ColliderBones)
            {
                int b = body.BoneIndex(name);
                if (b >= 0)
                    shape.BodyBones[name] = body.Bones[b].World * bodyToHair;
            }
            return shape;
        }

        /// <summary>
        /// The head of the player a hair model goes on, by its name: the octoling bodies for
        /// octoling hairs, the male ones for a male model. Falls back to <see cref="Default"/>.
        /// </summary>
        public static HeadShape ForHair(Core.Romfs romfs, SkinnedMesh hair, string hairName)
        {
            bool octo = hairName.StartsWith("Har_OCT");
            bool male = hairName.EndsWith("_M");
            string player = "Player0" + ((octo ? 2 : 0) + (male ? 1 : 0));
            Model model;
            try
            {
                var data = romfs.ReadModel(player);
                if (data == null)
                    return Default();
                model = new ResFile(new System.IO.MemoryStream(data)).Models.Values.First();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[HairGen] no player head, using the default: {ex.Message}");
                return Default();
            }
            var body = SkinnedMesh.FromModel(model);
            int head = body.BoneIndex("Head");
            int root = hair.BoneIndex("Head_Root");
            if (head < 0)
                return Default();
            var headRoot = root >= 0 ? hair.Bones[root].World : Matrix4.Identity;
            return FromBody(body, Matrix4.Invert(body.Bones[head].World) * headRoot);
        }

        /// <summary>The centre and radii of the ellipsoid fitted to the players' heads, in the hair's model space.</summary>
        public static readonly Vector3 DefaultCentre = new(0, 0.235f, 0),
            DefaultRadii = new(0.26f, 0.235f, 0.275f);

        /// <summary>An ellipsoid fitted to the players' heads, for when no body is at hand.</summary>
        public static HeadShape Default()
        {
            var centre = DefaultCentre;
            var radii = DefaultRadii;
            var points = new List<Vector3>();
            var surface = new List<Vector3>();
            for (int i = 0; i <= 24; i++)
            for (int j = 0; j < 48; j++)
            {
                float theta = MathF.PI * i / 24,
                    phi = 2 * MathF.PI * j / 48;
                var p =
                    centre
                    + new Vector3(
                        radii.X * MathF.Sin(theta) * MathF.Cos(phi),
                        radii.Y * MathF.Cos(theta),
                        radii.Z * MathF.Sin(theta) * MathF.Sin(phi)
                    );
                if (IsScalp(p))
                    points.Add(p);
                surface.Add(p);
            }
            return new HeadShape(points, surface);
        }

        /// <summary>Distance from a point to the nearest point of the whole head, face included.</summary>
        public float SurfaceDistance(Vector3 p) => Nearest(Surface, p);

        /// <summary>Distance from a point to the nearest scalp point.</summary>
        public float Distance(Vector3 p) => Nearest(Points, p);

        static float Nearest(List<Vector3> points, Vector3 p)
        {
            float best = float.MaxValue;
            foreach (var q in points)
                best = MathF.Min(best, (p - q).LengthSquared);
            return MathF.Sqrt(best);
        }

        List<Vector3> _cranium;

        /// <summary>
        /// Distance to the scalp above the neck. A limb's root is judged by it: a curl hanging by
        /// the neck is close to the nape but still the far end of its limb.
        /// </summary>
        public float CraniumDistance(Vector3 p)
        {
            _cranium ??= Points.Where(q => q.Y >= 0.12f).DefaultIfEmpty(DefaultCentre).ToList();
            return Nearest(_cranium, p);
        }
    }
}
