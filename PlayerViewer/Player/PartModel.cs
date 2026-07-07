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
        WeaponMain,   //Right hand (Weapon_R)
        WeaponLeft,   //Left hand (Weapon_L), such as dualies second model
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

        //Static local-pose override (weapon carry pose baked from the model's own
        //skeletal anim, e.g. roller CloseOff), keyed by bone name.
        public Dictionary<string, PoseSrt> PoseOverride;

        public class PoseSrt
        {
            public Vector3 Position;
            public Quaternion Rotation;
            public Vector3 Scale;
        }

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
        public void ResolveWelds(STSkeleton human, Dictionary<string, string> nameMap,
            bool mirrorLR = false, bool uprightWeld = false, bool mapOnly = false)
        {
            WeldTargets = new STBone[Skeleton.Bones.Count];
            WeldPre = new Matrix4[Skeleton.Bones.Count];
            for (int i = 0; i < Skeleton.Bones.Count; i++)
            {
                string name = Skeleton.Bones[i].Name;
                if (nameMap != null && nameMap.TryGetValue(name, out string mapped))
                    name = mapped;
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

                //Rest-rotation delta between the gear bone and the human bone.
                //Headgear only (authored upright around the head joint): cancels the
                //head bone's rest twist so only the animated delta rotation transfers.
                //Weapons/hair/clothes/shoes expect the raw human matrix.
                if (uprightWeld && WeldTargets[i] != null && !mirrorLR)
                {
                    var partRot = RestWorldRotation(Skeleton.Bones[i]);
                    var humanRot = RestWorldRotation(WeldTargets[i]);
                    WeldPre[i] = Matrix4.CreateFromQuaternion(partRot) *
                                 Matrix4.CreateFromQuaternion(Quaternion.Invert(humanRot));
                }
                else
                    WeldPre[i] = Matrix4.Identity;
            }

            //Parent-first ordering (bfres is usually already sorted, but be safe)
            var ordered = new List<STBone>(Skeleton.Bones.Count);
            var visited = new HashSet<STBone>();
            void Visit(STBone b)
            {
                if (!visited.Add(b)) return;
                if (b.Parent != null) Visit(b.Parent);
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
            if (name.EndsWith("_L")) return name.Substring(0, name.Length - 2) + "_R";
            if (name.EndsWith("_R")) return name.Substring(0, name.Length - 2) + "_L";
            return name;
        }

        /// <summary>
        /// Copies the (already updated) human bone world matrices onto this part's
        /// bones. Unmatched bones are posed from their parent using their rest local
        /// SRT (with hair-arrange overrides when present).
        /// </summary>
        //Segment scale compensate state of the last weld pass. Bfres skeletons use
        //Maya scaling: a bone's own scale affects its skinned vertices and the
        //POSITIONS of direct children, but never compounds into descendants'
        //rotation/scale (otherwise one arrange-collapsed bone would flatten the
        //whole chain below it instead of just its own mesh segment).
        readonly Dictionary<STBone, Vector3> _weldScale = new();
        readonly Dictionary<STBone, Matrix4> _weldRT = new();     //scale-free world

        public void ApplyWeld()
        {
            if (WeldTargets == null) return;
            _weldScale.Clear();
            _weldRT.Clear();

            foreach (var bone in OrderedBones)
            {
                int i = Skeleton.Bones.IndexOf(bone);
                var target = WeldTargets[i];

                Matrix4 rt;               //world rotation+translation (no scale)
                Vector3 scale;            //own local scale (applies to skinning + child positions)
                if (target != null)
                {
                    rt = WeldPre[i] * target.Transform;
                    //Headgear (RestoreAttachBind): every welded bone is an attach
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
                    //Hair arrange on welded bones (Head_Root): rotation acts in the
                    //bone's local frame, translate in model space, scale is the
                    //bone's own (compensated) scale.
                    scale = Vector3.One;
                    if (HairArrange != null && HairArrange.TryGetValue(bone.Name, out var rootArr))
                    {
                        rt = ArrangeRotation(rootArr) * rt;
                        rt.Row3.Xyz += rootArr.Translate;
                        scale = ClampScale(rootArr.Scale);
                    }
                    if (Mirror)
                        rt = NegateMatrix(rt);
                }
                else
                {
                    Vector3 parentScale = bone.Parent != null &&
                        _weldScale.TryGetValue(bone.Parent, out var ps) ? ps : Vector3.One;
                    Matrix4 parentRT = bone.Parent != null &&
                        _weldRT.TryGetValue(bone.Parent, out var prt) ? prt : Matrix4.Identity;

                    GetLocalPose(bone, out scale, out Quaternion rot, out Vector3 pos);
                    //Child position inherits the parent's own scale (Maya behavior);
                    //rotation/scale do not.
                    pos *= parentScale;
                    rt = Matrix4.CreateFromQuaternion(rot) *
                         Matrix4.CreateTranslation(pos) * parentRT;
                }

                _weldRT[bone] = rt;
                _weldScale[bone] = scale;
                bone.Transform = Matrix4.CreateScale(scale) * rt;
            }
        }

        static Vector3 ClampScale(Vector3 s) => new Vector3(
            Math.Max(s.X, 0.01f), Math.Max(s.Y, 0.01f), Math.Max(s.Z, 0.01f));

        static Matrix4 ArrangeRotation(ArrangeBoneParam arr)
        {
            return Matrix4.CreateFromQuaternion(Quaternion.FromEulerAngles(
                MathHelper.DegreesToRadians(arr.RotationDeg.X),
                MathHelper.DegreesToRadians(arr.RotationDeg.Y),
                MathHelper.DegreesToRadians(arr.RotationDeg.Z)));
        }

        void GetLocalPose(STBone bone, out Vector3 scale, out Quaternion rot, out Vector3 pos)
        {
            scale = bone.Scale;
            rot = bone.Rotation;
            pos = bone.Position;

            if (PoseOverride != null && PoseOverride.TryGetValue(bone.Name, out var pose))
            {
                scale = pose.Scale;
                rot = pose.Rotation;
                pos = pose.Position;
            }

            if (HairArrange != null && HairArrange.TryGetValue(bone.Name, out var arr))
            {
                //Compose the arrange SRT on top of the rest pose (PlayerCustomUtl::
                //hideEarHair_BeforeCalcDraw): scale multiplies (clamped >= 0.01),
                //rotation pre-multiplies, translation adds.
                scale = new Vector3(
                    Math.Max(scale.X * arr.Scale.X, 0.01f),
                    Math.Max(scale.Y * arr.Scale.Y, 0.01f),
                    Math.Max(scale.Z * arr.Scale.Z, 0.01f));
                //S2 decomp (hideEarHair_BeforeCalcDraw): finalRot = restRot * arrangeRot
                //(column conv) = arrange applied in the bone's local frame. In OpenTK
                //quaternion order that is rest * arrange. Euler XYZ, degrees.
                var arrRot = Quaternion.FromEulerAngles(
                    MathHelper.DegreesToRadians(arr.RotationDeg.X),
                    MathHelper.DegreesToRadians(arr.RotationDeg.Y),
                    MathHelper.DegreesToRadians(arr.RotationDeg.Z));
                rot = rot * arrRot;
                //The translate is authored in model space (Y = down folds the OCT001
                //ponytail); hair bones inherit the head bone's 90° rest twist, so
                //express it in the parent's bind frame before adding to the local pos.
                var parentBind = bone.Parent != null
                    ? RestWorldRotation(bone.Parent) : Quaternion.Identity;
                pos += Vector3.Transform(arr.Translate, Quaternion.Invert(parentBind));
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
                -m.Row0.X, -m.Row0.Y, -m.Row0.Z, m.Row0.W,
                -m.Row1.X, -m.Row1.Y, -m.Row1.Z, m.Row1.W,
                -m.Row2.X, -m.Row2.Y, -m.Row2.Z, m.Row2.W,
                m.Row3.X, m.Row3.Y, m.Row3.Z, m.Row3.W);
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
