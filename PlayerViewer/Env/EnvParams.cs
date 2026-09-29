using System;
using System.Collections.Generic;
using System.Numerics;
using PlayerViewer.Core.Formats;

namespace PlayerViewer.Env
{
    /// <summary>
    /// The part of a scene's RenderingDay / RenderingNight gyml that reaches the player shaders.
    /// A field the gyml leaves out takes the game's own default, read out of the binary's
    /// parameter registration.
    /// </summary>
    public class EnvParams
    {
        public string Name = "";

        public float MainLightLatitude = 90;
        public float MainLightLongitude = 90;
        public Vector4 MainLightColor = Vector4.One;
        public float MainLightIntens = 4;

        public float DepthFogStart = 50;
        public float DepthFogEnd = 700;
        public float DepthFogScattering = 0.6f;
        public Vector4 DepthFogColor = new(0.3f, 0.6f, 1.0f, 0.2f);

        public float HeightFogStart = 40;
        public float HeightFogEnd = 400;
        public Vector4 HeightFogColor = new(0.23f, 0.45f, 1.0f, 0.25f);
        public Vector3 HeightFogDir = new(0, -1, 0);

        public float RadialFogLobeCtrl = 1;
        public float RadialFogSizeCtrl = 5;
        public Vector4 RadialFogColor = new(1.0f, 0.95f, 0.7f, 1.0f);
        public float RadialFogIntens = 1;
        public float RadialFogBlendFactor = 0;

        public float BakeShadowIntensOffset = 0.5f;
        public float BakeShadowIntensScale = 2.5f;
        public float BakeAOIntensOffset = 0;
        public float BakeAOIntensScale = 1;
        public float BakeAOMainLightOcclude = 0;

        /// <summary>The light's strength in the environment map when <see cref="IsUseCubeMapIntens"/> is set.</summary>
        public float CubeMapIntens = 4;
        public bool IsUseCubeMapIntens;

        /// <summary>No default registration was read; 4 is what a scene without the key shows.</summary>
        public float BloomThreshold = 4;

        /// <summary>A fixed exposure in stops, or null when the scene auto exposes.</summary>
        public float? ManualExposure;

        public bool SkyEnable = true;
        public string SkyActor = "";
        public float SkyEmissionIntensInEnvMap = 1;
        public float SkySaturationInEnvMap = 1;
        public float SkyRotate = 0;

        /// <summary>The direction the main light travels, as the game derives it.</summary>
        public Vector3 MainLightDirection
        {
            get
            {
                float lat = MainLightLatitude * (MathF.PI / 180f);
                float lon = MainLightLongitude * (MathF.PI / 180f);
                return new Vector3(
                    -MathF.Cos(lat) * MathF.Sin(lon),
                    -MathF.Sin(lat),
                    -MathF.Cos(lat) * MathF.Cos(lon)
                );
            }
        }

        public static EnvParams FromByml(Dictionary<string, object> root, string name)
        {
            var p = new EnvParams { Name = name };
            if (root == null)
                return p;

            var lighting = Hash(root, "Lighting");
            var main = Hash(lighting, "MainLight");
            p.MainLightLatitude = Byml.GetFloat(main, "Latitude", p.MainLightLatitude);
            p.MainLightLongitude = Byml.GetFloat(main, "Longitude", p.MainLightLongitude);
            p.MainLightColor = Color(main, "Color", p.MainLightColor);
            p.MainLightIntens = Byml.GetFloat(main, "Intens", p.MainLightIntens);
            p.CubeMapIntens = Byml.GetFloat(main, "CubeMapIntens", p.CubeMapIntens);
            p.IsUseCubeMapIntens = Byml.GetBool(main, "IsUseCubeMapIntens", p.IsUseCubeMapIntens);

            var sky = Hash(lighting, "SkySphere");
            p.SkyEnable = Byml.GetBool(sky, "Enable", p.SkyEnable);
            p.SkyActor = Byml.GetString(sky, "ActorName", p.SkyActor);
            p.SkyEmissionIntensInEnvMap = Byml.GetFloat(
                sky,
                "EmissionIntensInEnvMap",
                p.SkyEmissionIntensInEnvMap
            );
            p.SkySaturationInEnvMap = Byml.GetFloat(
                sky,
                "SaturationInEnvMap",
                p.SkySaturationInEnvMap
            );
            p.SkyRotate = Byml.GetFloat(sky, "Rotate", p.SkyRotate);

            var fog = Hash(root, "Fog");
            var depth = Hash(fog, "DepthFog");
            p.DepthFogStart = Byml.GetFloat(depth, "Start", p.DepthFogStart);
            p.DepthFogEnd = Byml.GetFloat(depth, "End", p.DepthFogEnd);
            p.DepthFogScattering = Byml.GetFloat(depth, "ScatteringCoeff", p.DepthFogScattering);
            p.DepthFogColor = Color(depth, "Color", p.DepthFogColor);

            var height = Hash(fog, "HeightFog");
            p.HeightFogStart = Byml.GetFloat(height, "Start", p.HeightFogStart);
            p.HeightFogEnd = Byml.GetFloat(height, "End", p.HeightFogEnd);
            p.HeightFogColor = Color(height, "Color", p.HeightFogColor);
            var dir = Hash(height, "Dir");
            if (dir != null)
                p.HeightFogDir = new Vector3(
                    Byml.GetFloat(dir, "X", p.HeightFogDir.X),
                    Byml.GetFloat(dir, "Y", p.HeightFogDir.Y),
                    Byml.GetFloat(dir, "Z", p.HeightFogDir.Z)
                );

            var radial = Hash(fog, "RadialFog");
            p.RadialFogLobeCtrl = Byml.GetFloat(radial, "LobeCtrl", p.RadialFogLobeCtrl);
            p.RadialFogSizeCtrl = Byml.GetFloat(radial, "SizeCtrl", p.RadialFogSizeCtrl);
            p.RadialFogColor = Color(radial, "Color", p.RadialFogColor);
            p.RadialFogIntens = Byml.GetFloat(radial, "Intens", p.RadialFogIntens);
            p.RadialFogBlendFactor = Byml.GetFloat(radial, "BlendFactor", p.RadialFogBlendFactor);

            var bake = Hash(Hash(root, "Shadow"), "BakeShadow");
            p.BakeShadowIntensOffset = Byml.GetFloat(
                bake,
                "BakeShadowIntensOffset",
                p.BakeShadowIntensOffset
            );
            p.BakeShadowIntensScale = Byml.GetFloat(
                bake,
                "BakeShadowIntensScale",
                p.BakeShadowIntensScale
            );
            p.BakeAOIntensOffset = Byml.GetFloat(bake, "BakeAOIntensOffset", p.BakeAOIntensOffset);
            p.BakeAOIntensScale = Byml.GetFloat(bake, "BakeAOIntensScale", p.BakeAOIntensScale);
            p.BakeAOMainLightOcclude = Byml.GetFloat(
                bake,
                "BakeAOMainLightOcclude",
                p.BakeAOMainLightOcclude
            );

            var post = Hash(root, "PostEffect");
            p.BloomThreshold = Byml.GetFloat(Hash(post, "Bloom"), "Threshold", p.BloomThreshold);
            var exposure = Hash(post, "HDRExposure");
            if (Byml.GetString(exposure, "ExposureType") == "ManualExposure")
                p.ManualExposure = Byml.GetFloat(Hash(exposure, "ManualExposure"), "Value", 0);
            return p;
        }

        static Dictionary<string, object> Hash(Dictionary<string, object> hash, string key) =>
            hash != null && hash.TryGetValue(key, out var v)
                ? v as Dictionary<string, object>
                : null;

        static Vector4 Color(Dictionary<string, object> hash, string key, Vector4 fallback)
        {
            var c = Hash(hash, key);
            if (c == null)
                return fallback;
            return new Vector4(
                Byml.GetFloat(c, "R", fallback.X),
                Byml.GetFloat(c, "G", fallback.Y),
                Byml.GetFloat(c, "B", fallback.Z),
                Byml.GetFloat(c, "A", fallback.W)
            );
        }
    }
}
