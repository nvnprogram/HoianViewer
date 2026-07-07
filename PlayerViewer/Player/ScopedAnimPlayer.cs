using System;
using System.Collections.Generic;
using System.Linq;
using BfresEditor;
using BfresLibrary;
using Toolbox.Core;
using Toolbox.Core.Animations;

namespace PlayerViewer.Player
{
    /// <summary>
    /// Applies material / bone-visibility animation wrappers to a *specific* render or
    /// skeleton instead of everything in DataCache (the stock wrappers animate every
    /// model whose material/bone names match, which cross-contaminates welded parts).
    /// </summary>
    public static class ScopedAnimPlayer
    {
        /// <summary>
        /// Applies a material animation (texture pattern and/or shader params) at the
        /// given frame, but only to materials of the provided models.
        /// </summary>
        public static void ApplyMaterialAnim(BfresMaterialAnim anim, float frame, IEnumerable<BfresModelAsset> models)
        {
            anim.SetFrame(frame);
            foreach (BfresMaterialAnim.MaterialAnimGroup group in anim.AnimGroups)
            {
                foreach (var model in models)
                {
                    foreach (var mesh in model.Meshes)
                    {
                        if (mesh.Material.Name != group.Name)
                            continue;
                        ApplyGroup(anim, group, (FMAT)mesh.Material, frame);
                    }
                }
            }
        }

        static void ApplyGroup(BfresMaterialAnim anim, STAnimGroup group, FMAT material, float frame)
        {
            foreach (var track in group.GetTracks())
            {
                if (track is BfresMaterialAnim.SamplerTrack samplerTrack)
                    ApplySamplerTrack(anim, material, samplerTrack, frame);
                if (track is BfresMaterialAnim.ParamTrack paramTrack)
                    ApplyParamTrack(material, group, paramTrack, frame);
            }

            foreach (var subGroup in group.SubAnimGroups)
                ApplyGroup(anim, subGroup, material, frame);
        }

        static void ApplySamplerTrack(BfresMaterialAnim anim, FMAT material, BfresMaterialAnim.SamplerTrack track, float frame)
        {
            if (anim.TextureList.Count == 0)
                return;

            int value = (int)track.GetFrameValue(frame);
            if (value < 0 || value >= anim.TextureList.Count)
                return;

            material.AnimatedSamplers.Remove(track.Sampler);
            material.AnimatedSamplers.Add(track.Sampler, anim.TextureList[value]);
        }

        static void ApplyParamTrack(FMAT material, STAnimGroup group, BfresMaterialAnim.ParamTrack track, float frame)
        {
            if (!material.ShaderParams.ContainsKey(group.Name))
                return;

            float value = track.GetFrameValue(frame);
            uint index = track.ValueOffset / 4;
            var targetParam = material.ShaderParams[group.Name];

            if (!material.AnimatedParams.ContainsKey(group.Name))
            {
                var copy = new ShaderParam { Type = targetParam.Type, Name = group.Name };
                if (targetParam.DataValue is float[] floats)
                {
                    var dest = new float[floats.Length];
                    Array.Copy(floats, dest, floats.Length);
                    copy.DataValue = dest;
                }
                else
                    copy.DataValue = targetParam.DataValue;
                material.AnimatedParams.Add(group.Name, copy);
            }

            var param = material.AnimatedParams[group.Name];
            switch (targetParam.Type)
            {
                case ShaderParamType.Float: param.DataValue = value; break;
                case ShaderParamType.Float2:
                case ShaderParamType.Float3:
                case ShaderParamType.Float4:
                    var arr = (float[])param.DataValue;
                    if (index < arr.Length) arr[index] = value;
                    break;
                case ShaderParamType.Int: param.DataValue = (int)value; break;
                case ShaderParamType.TexSrt:
                case ShaderParamType.TexSrtEx:
                    {
                        var texSrt = (TexSrt)param.DataValue;
                        var scaleX = texSrt.Scaling.X; var scaleY = texSrt.Scaling.Y;
                        var rotate = texSrt.Rotation;
                        var transX = texSrt.Translation.X; var transY = texSrt.Translation.Y;
                        if (track.ValueOffset == 4) scaleX = value;
                        if (track.ValueOffset == 8) scaleY = value;
                        if (track.ValueOffset == 12) rotate = value;
                        if (track.ValueOffset == 16) transX = value;
                        if (track.ValueOffset == 20) transY = value;
                        param.DataValue = new TexSrt()
                        {
                            Mode = texSrt.Mode,
                            Scaling = new Syroot.Maths.Vector2F(scaleX, scaleY),
                            Rotation = rotate,
                            Translation = new Syroot.Maths.Vector2F(transX, transY),
                        };
                    }
                    break;
            }
        }

        /// <summary>
        /// Applies a bone visibility animation at the given frame, only to the target skeleton.
        /// Also applies to shape (FSHP) visibility when a track name matches a shape name
        /// but not a bone name (Splatoon 3 uses both patterns).
        /// </summary>
        public static void ApplyBoneVisAnim(BfresVisibilityAnim anim, float frame,
            STSkeleton skeleton, IEnumerable<BfresModelAsset> models = null)
        {
            anim.SetFrame(frame);
            foreach (BfresVisibilityAnim.BoneAnimGroup group in anim.AnimGroups)
            {
                bool val = group.Track.GetFrameValue(frame) != 0;
                var bone = skeleton.SearchBone(group.Name);
                if (bone != null)
                {
                    bone.Visible = val;
                    continue;
                }
                if (models == null) continue;
                foreach (var model in models)
                    foreach (var mesh in model.Meshes)
                        if (mesh.Name == group.Name)
                            mesh.Shape.IsVisible = val;
            }
        }

        /// <summary>
        /// Resets material animation state (animated samplers/params) on the given models.
        /// </summary>
        public static void ResetMaterialAnims(IEnumerable<BfresModelAsset> models)
        {
            foreach (var model in models)
            {
                foreach (var mesh in model.Meshes)
                {
                    var mat = (FMAT)mesh.Material;
                    mat.AnimatedSamplers.Clear();
                    mat.AnimatedParams.Clear();
                }
            }
        }
    }
}
