using System;
using System.Collections.Generic;
using System.Linq;
using BfresEditor;
using GLFrameworkEngine;
using OpenTK;
using Toolbox.Core;

namespace PlayerViewer.Player
{
    public enum PartKind
    {
        Human,
        Hair,
        Eyebrow,
        Head,
        Clothes,
        Bottom,
        ShoeLeft,
        ShoeRight,
        Tank,
        WeaponMain, //Right hand (Weapon_R)
        WeaponLeft, //Left hand (Weapon_L), such as dualies second model
    }

    /// <summary>
    /// Hair-arrange override for a single hair bone (from spl__HairArrangeParam byml).
    /// </summary>
    public class ArrangeBoneParam
    {
        public Vector3 Scale = Vector3.One;
        public Vector3 RotationDeg = Vector3.Zero;
        public Vector3 Translate = Vector3.Zero;
        public float AnimReduce = 1.0f;
    }

    /// <summary>
    /// One gear/weapon piece: its own BFRES render + skeleton, welded to the human
    /// skeleton every frame by copying world matrices of name-matched bones
    /// (Splatoon's PlayerCustomPart weld callbacks).
    /// </summary>
    public class PartModel
    {
        public PartKind Kind;
        public string ModelName;
        public BFRES Bfres;
        public BfresRender Render;
        public BfresModelAsset ModelAsset;
        public STSkeleton Skeleton;

        //Key into PlayerScene's unequip cache (romfs model name). Null for parts
        //that must not be cached (custom drops / shared-BFRES parts).
        public string CacheKey;

        //Mirrored copy (right shoe): all copied human matrices are negated.
        public bool Mirror;

        //Extra transform (VariationSRT/ManualBindSRT for headgear) applied in the
        //attach bone's local space before the human bone world matrix.
        public Matrix4 AttachOffset = Matrix4.Identity;

        //Hair-arrange bone SRT overrides (hair parts only), keyed by bone name.
        public Dictionary<string, ArrangeBoneParam> HairArrange;

        //The hair actor carries the BlitzCompatible tag (a Splatoon 2 hair): the arrange
        //translation of a bone under Head_Root is read as (y, z, x).
        public bool BlitzCompatible;

        //Static local-pose override (weapon carry pose baked from the model's own
        //skeletal anim, e.g. roller CloseOff), keyed by bone name.
        public Dictionary<string, PoseSrt> PoseOverride;

        public class PoseSrt
        {
            public Vector3 Position;
            public Quaternion Rotation;
            public Vector3 Scale;
        }

        //The part's own skeletal anim when one plays (a weapon following the player). It
        //writes the bones' animation controllers, which then replace the rest and carry pose,
        //and its root local applies on top of the attach bone.
        public BfresSkeletalAnim SkeletalAnim;

        //Per-bone weld target resolved once at attach time. Index = bone index in
        //Skeleton.Bones. Null entry = no human match (posed from parent instead).
        public STBone[] WeldTargets;

        //Per-bone pre-matrix: RotOnly(partBoneRest) * InvRotOnly(humanBoneRest).
        //Cancels the human bone's *rest* rotation so only the animated delta rotation
        //transfers (translation is kept in full). For hair/clothes/shoes the part rest
        //rotations match the human's, so this is identity = plain matrix copy. For
        //headgear (authored upright around the head joint, root rest = identity) it
        //cancels the head bone's 90° rest twist - the ManualBindSRT values in
        //GearHeadParamSet are tiny nudges, so the base bind must already be upright.
        public Matrix4[] WeldPre;

        //Per-bone: the weld came through the name map (an attach point onto the
        //head) rather than a same-named human bone. Gear skeletons bind to the
        //player's by name (the ear cuffs' Ear_L, a helmet's Spine_3 and Neck), and
        //those bones follow the player bone as is.
        public bool[] WeldMapped;

        //Looping material anims the gear plays on its own (the "_Auto" ones),
        //driven by the scene's idle clock.
        public List<BfresMaterialAnim> IdleMaterialAnims = new();

        //Bones in parent-first order for pose propagation.
        public STBone[] OrderedBones;

        //The bone that receives AttachOffset (the mapped root such as Head_Root/Root).
        public STBone AttachBone;

        //Restore the attach bone's bind matrix after welding (headgear only; other
        //parts share the human skeleton's bind space and must not be offset).
        public bool RestoreAttachBind;

        public bool Visible = true;

        /// <summary>
        /// Resolves weld targets against the human skeleton.
        /// nameMap maps special gear bone names to human bone names (e.g. Head_Root->Head).
        /// mirrorLR remaps _L/_R suffixes (right shoe reusing the left shoe model).
        /// </summary>
        public void ResolveWelds(
            STSkeleton human,
            Dictionary<string, string> nameMap,
            bool mirrorLR = false,
            bool uprightWeld = false,
            bool mapOnly = false
        )
        {
            WeldTargets = new STBone[Skeleton.Bones.Count];
            WeldPre = new Matrix4[Skeleton.Bones.Count];
            WeldMapped = new bool[Skeleton.Bones.Count];
            for (int i = 0; i < Skeleton.Bones.Count; i++)
            {
                string name = Skeleton.Bones[i].Name;
                if (nameMap != null && nameMap.TryGetValue(name, out string mapped))
                {
                    name = mapped;
                    WeldMapped[i] = true;
                }
                else if (mapOnly)
                {
                    //Weapons: only the mapped root welds. Their internal bones reuse
                    //generic names (a roller's "Neck" is its shaft joint) that must
                    //not weld onto the player's same-named bones.
                    WeldPre[i] = Matrix4.Identity;
                    continue;
                }
                else if (mirrorLR)
                    name = SwapLR(name);

                WeldTargets[i] = human.SearchBone(name);

                //Headgear is authored upright around the head joint. Cancel the
                //human head bone's rest twist so only the animated delta transfers.
                //The gear bone's own rest rotation is ignored: models like Hed_COP111
                //re-use the player skeleton's Head bone (with its ~90° rest twist)
                //but the mesh is still authored upright in model space.
                if (uprightWeld && WeldMapped[i] && WeldTargets[i] != null && !mirrorLR)
                {
                    var humanRot = RestWorldRotation(WeldTargets[i]);
                    WeldPre[i] = Matrix4.CreateFromQuaternion(Quaternion.Invert(humanRot));
                }
                else
                    WeldPre[i] = Matrix4.Identity;
            }

            //Parent-first ordering (bfres is usually already sorted, but be safe)
            var ordered = new List<STBone>(Skeleton.Bones.Count);
            var visited = new HashSet<STBone>();
            void Visit(STBone b)
            {
                if (!visited.Add(b))
                    return;
                if (b.Parent != null)
                    Visit(b.Parent);
                ordered.Add(b);
            }
            foreach (var bone in Skeleton.Bones)
                Visit(bone);
            OrderedBones = ordered.ToArray();
        }

        /// <summary>
        /// World-space rest rotation of a bone (composed rest local rotations up the
        /// parent chain; rest scales on player/gear skeletons are 1).
        /// </summary>
        static Quaternion RestWorldRotation(STBone bone)
        {
            Quaternion rot = bone.Rotation;
            var parent = bone.Parent;
            while (parent != null)
            {
                rot = parent.Rotation * rot;
                parent = parent.Parent;
            }
            return rot;
        }

        public static string SwapLR(string name)
        {
            if (name.EndsWith("_L"))
                return name.Substring(0, name.Length - 2) + "_R";
            if (name.EndsWith("_R"))
                return name.Substring(0, name.Length - 2) + "_L";
            return name;
        }

        /// <summary>
        /// Copies the (already updated) human bone world matrices onto this part's
        /// bones. Unmatched bones are posed from their parent using their rest local
        /// SRT (with hair-arrange overrides when present).
        /// </summary>
        //State of the last weld pass, per bone: the full world matrix, scale
        //included, and the bone's own local scale. Bfres skeletons use Maya scaling:
        //a bone's own scale affects its skinned vertices and the positions of its
        //children; a child with segment scale compensate drops that scale from its own
        //axes, one without it inherits it (Scaler_B under Scaler_A in Har_SQD013, which
        //an arrange preset shrinks to fit a hat's hole).
        readonly Dictionary<STBone, Vector3> _weldScale = new();
        readonly Dictionary<STBone, Matrix4> _weldFull = new();

        public void ApplyWeld()
        {
            if (WeldTargets == null)
                return;
            _weldScale.Clear();
            _weldFull.Clear();

            foreach (var bone in OrderedBones)
            {
                int i = Skeleton.Bones.IndexOf(bone);
                var target = WeldTargets[i];

                Matrix4 full; //world matrix, scale included
                Vector3 scale; //own local scale
                if (target != null)
                {
                    Matrix4 rt = WeldPre[i] * target.Transform; //rotation and translation only
                    if (RestoreAttachBind && !WeldMapped[i])
                    {
                        //A headgear bone bound to the player bone of its name. The
                        //gear SRT offset still moves its mesh, applied in gear model
                        //space so the gear stays rigid (the ear cuffs' per hair nudges
                        //act on a mesh that is entirely on Ear_L).
                        if (AttachOffset != Matrix4.Identity)
                            rt = Matrix4.Invert(bone.Inverse) * AttachOffset * bone.Inverse * rt;
                    }
                    else
                    {
                        //Headgear (RestoreAttachBind): every mapped bone is an attach
                        //point onto the head, so the gear SRT offset applies to all of
                        //them - meshes may rig to Root_Model rather than Root (Hed_AMB020).
                        //Other parts (weapons) only offset the mapped root.
                        bool isAttach = RestoreAttachBind || bone == AttachBone;
                        if (isAttach && AttachOffset != Matrix4.Identity)
                            rt = AttachOffset * rt;
                        //Headgear: the game binds the gear's model origin to the head
                        //bone. Most gear roots have an identity bind so this is a no-op,
                        //but some (Hed_HAT020) are authored offset from the origin with
                        //the offset in the bind pose; skinning multiplies by the inverse
                        //bind, so put the bind back or the authored offset is lost.
                        if (RestoreAttachBind)
                            rt = Matrix4.Invert(bone.Inverse) * rt;
                    }
                    //Hair arrange on welded bones (Head_Root): rotation acts in the
                    //bone's local frame, translate in model space, scale is the
                    //bone's own (compensated) scale.
                    scale = Vector3.One;
                    if (SkeletalAnim != null && bone == AttachBone)
                    {
                        var ctrl = bone.AnimationController;
                        rt =
                            Matrix4.CreateFromQuaternion(ctrl.Rotation)
                            * Matrix4.CreateTranslation(ctrl.Position)
                            * rt;
                        scale = ctrl.Scale;
                    }
                    if (HairArrange != null && HairArrange.TryGetValue(bone.Name, out var rootArr))
                    {
                        rt = ArrangeRotation(rootArr) * rt;
                        rt.Row3.Xyz += rootArr.Translate;
                        scale = ClampScale(rootArr.Scale);
                    }
                    if (Mirror)
                        rt = NegateMatrix(rt);
                    full = Matrix4.CreateScale(scale) * rt;
                }
                else
                {
                    Vector3 parentScale =
                        bone.Parent != null && _weldScale.TryGetValue(bone.Parent, out var ps)
                            ? ps
                            : Vector3.One;
                    Matrix4 parentFull =
                        bone.Parent != null && _weldFull.TryGetValue(bone.Parent, out var pf)
                            ? pf
                            : Matrix4.Identity;

                    GetLocalPose(bone, out scale, out Quaternion rot, out Vector3 pos);
                    //The engine's Maya composition: the parent's scale stays on the
                    //offset; a compensating bone takes it off its own axes with the
                    //inverse between its rotation and its translation.
                    bool ssc =
                        SkeletalAnim != null
                            ? bone.AnimationController.UseSegmentScaleCompensate
                            : bone.UseSegmentScaleCompensate;
                    Matrix4 compensate = ssc
                        ? Matrix4.CreateScale(
                            1.0f / parentScale.X,
                            1.0f / parentScale.Y,
                            1.0f / parentScale.Z
                        )
                        : Matrix4.Identity;
                    full =
                        Matrix4.CreateScale(scale)
                        * Matrix4.CreateFromQuaternion(rot)
                        * compensate
                        * Matrix4.CreateTranslation(pos)
                        * parentFull;
                }

                _weldFull[bone] = full;
                _weldScale[bone] = scale;
                bone.Transform = full;
            }
        }

        static Vector3 ClampScale(Vector3 s) =>
            new Vector3(Math.Max(s.X, 0.01f), Math.Max(s.Y, 0.01f), Math.Max(s.Z, 0.01f));

        static Matrix4 ArrangeRotation(ArrangeBoneParam arr)
        {
            return Matrix4.CreateFromQuaternion(
                Quaternion.FromEulerAngles(
                    MathHelper.DegreesToRadians(arr.RotationDeg.X),
                    MathHelper.DegreesToRadians(arr.RotationDeg.Y),
                    MathHelper.DegreesToRadians(arr.RotationDeg.Z)
                )
            );
        }

        void GetLocalPose(STBone bone, out Vector3 scale, out Quaternion rot, out Vector3 pos)
        {
            scale = bone.Scale;
            rot = bone.Rotation;
            pos = bone.Position;

            if (SkeletalAnim != null)
            {
                var ctrl = bone.AnimationController;
                scale = ctrl.Scale;
                rot = ctrl.Rotation;
                pos = ctrl.Position;
            }
            else if (PoseOverride != null && PoseOverride.TryGetValue(bone.Name, out var pose))
            {
                scale = pose.Scale;
                rot = pose.Rotation;
                pos = pose.Position;
            }

            if (HairArrange != null && HairArrange.TryGetValue(bone.Name, out var arr))
            {
                //The game's rule, applied once at bind to the rest local: each scale
                //factor is floored at 0.01 and multiplies the rest scale; the rotation
                //is Rz Ry Rx of the degrees (X first) applied in the bone's own frame
                //after the rest rotation; the translation is added unrotated in the
                //parent's frame, its components permuted for a child of Head_Root of a
                //hair tagged BlitzCompatible.
                scale = new Vector3(
                    scale.X * Math.Max(arr.Scale.X, 0.01f),
                    scale.Y * Math.Max(arr.Scale.Y, 0.01f),
                    scale.Z * Math.Max(arr.Scale.Z, 0.01f)
                );
                var arrRot =
                    Quaternion.FromAxisAngle(Vector3.UnitZ, MathHelper.DegreesToRadians(arr.RotationDeg.Z))
                    * Quaternion.FromAxisAngle(Vector3.UnitY, MathHelper.DegreesToRadians(arr.RotationDeg.Y))
                    * Quaternion.FromAxisAngle(Vector3.UnitX, MathHelper.DegreesToRadians(arr.RotationDeg.X));
                rot = rot * arrRot;
                Vector3 t = arr.Translate;
                if (BlitzCompatible && bone.Parent != null && bone.Parent.Name == "Head_Root")
                    t = new Vector3(t.Y, t.Z, t.X);
                pos += t;
            }
        }

        /// <summary>
        /// Negates the rotation 3x3 of the matrix (Splatoon's ShoesCallback mirror).
        /// At rest -R(right leg) reproduces the left-leg orientation with a reflection,
        /// so the left shoe mesh lands mirrored on the right foot. The translation row
        /// must stay untouched (the right foot's position).
        /// </summary>
        public static Matrix4 NegateMatrix(Matrix4 m)
        {
            return new Matrix4(
                -m.Row0.X,
                -m.Row0.Y,
                -m.Row0.Z,
                m.Row0.W,
                -m.Row1.X,
                -m.Row1.Y,
                -m.Row1.Z,
                m.Row1.W,
                -m.Row2.X,
                -m.Row2.Y,
                -m.Row2.Z,
                m.Row2.W,
                m.Row3.X,
                m.Row3.Y,
                m.Row3.Z,
                m.Row3.W
            );
        }

        /// <summary>
        /// Sets bone visibility by name (used for tank harness type selection).
        /// </summary>
        public void SetBoneVisible(string name, bool visible)
        {
            var bone = Skeleton.SearchBone(name);
            if (bone != null)
                bone.Visible = visible;
        }
    }
}
