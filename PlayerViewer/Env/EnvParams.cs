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
            p.ReadMainLight(Hash(lighting, "MainLight"), "Intens");
            p.ReadSky(Hash(lighting, "SkySphere"));

            var fog = Hash(root, "Fog");
            p.ReadDepthFog(Hash(fog, "DepthFog"));
            p.ReadHeightFog(Hash(fog, "HeightFog"));
            p.ReadRadialFog(Hash(fog, "RadialFog"));

            p.ReadBake(Hash(Hash(root, "Shadow"), "BakeShadow"));

            var post = Hash(root, "PostEffect");
            p.BloomThreshold = Byml.GetFloat(Hash(post, "Bloom"), "Threshold", p.BloomThreshold);
            var exposure = Hash(post, "HDRExposure");
            if (Byml.GetString(exposure, "ExposureType") == "ManualExposure")
                p.ManualExposure = Byml.GetFloat(Hash(exposure, "ManualExposure"), "Value", 0);
            return p;
        }

        //The groups both games' params share; each game keeps them under its own keys.
        internal void ReadMainLight(Dictionary<string, object> main, string intensKey)
        {
            MainLightLatitude = Byml.GetFloat(main, "Latitude", MainLightLatitude);
            MainLightLongitude = Byml.GetFloat(main, "Longitude", MainLightLongitude);
            MainLightColor = Color(main, "Color", MainLightColor);
            MainLightIntens = Byml.GetFloat(main, intensKey, MainLightIntens);
            CubeMapIntens = Byml.GetFloat(main, "CubeMapIntens", CubeMapIntens);
            IsUseCubeMapIntens = Byml.GetBool(main, "IsUseCubeMapIntens", IsUseCubeMapIntens);
        }

        internal void ReadSky(Dictionary<string, object> sky)
        {
            SkyEnable = Byml.GetBool(sky, "Enable", SkyEnable);
            SkyActor = Byml.GetString(sky, "ActorName", SkyActor);
            SkyEmissionIntensInEnvMap = Byml.GetFloat(
                sky,
                "EmissionIntensInEnvMap",
                SkyEmissionIntensInEnvMap
            );
            SkySaturationInEnvMap = Byml.GetFloat(sky, "SaturationInEnvMap", SkySaturationInEnvMap);
            SkyRotate = Byml.GetFloat(sky, "Rotate", SkyRotate);
        }

        internal void ReadDepthFog(Dictionary<string, object> depth)
        {
            DepthFogStart = Byml.GetFloat(depth, "Start", DepthFogStart);
            DepthFogEnd = Byml.GetFloat(depth, "End", DepthFogEnd);
            DepthFogScattering = Byml.GetFloat(depth, "ScatteringCoeff", DepthFogScattering);
            DepthFogColor = Color(depth, "Color", DepthFogColor);
        }

        internal void ReadHeightFog(Dictionary<string, object> height)
        {
            HeightFogStart = Byml.GetFloat(height, "Start", HeightFogStart);
            HeightFogEnd = Byml.GetFloat(height, "End", HeightFogEnd);
            HeightFogColor = Color(height, "Color", HeightFogColor);
            var dir = Hash(height, "Dir");
            if (dir != null)
                HeightFogDir = new Vector3(
                    Byml.GetFloat(dir, "X", HeightFogDir.X),
                    Byml.GetFloat(dir, "Y", HeightFogDir.Y),
                    Byml.GetFloat(dir, "Z", HeightFogDir.Z)
                );
        }

        internal void ReadRadialFog(Dictionary<string, object> radial)
        {
            RadialFogLobeCtrl = Byml.GetFloat(radial, "LobeCtrl", RadialFogLobeCtrl);
            RadialFogSizeCtrl = Byml.GetFloat(radial, "SizeCtrl", RadialFogSizeCtrl);
            RadialFogColor = Color(radial, "Color", RadialFogColor);
            RadialFogIntens = Byml.GetFloat(radial, "Intens", RadialFogIntens);
            RadialFogBlendFactor = Byml.GetFloat(radial, "BlendFactor", RadialFogBlendFactor);
        }

        internal void ReadBake(Dictionary<string, object> bake)
        {
            BakeShadowIntensOffset = Byml.GetFloat(
                bake,
                "BakeShadowIntensOffset",
                BakeShadowIntensOffset
            );
            BakeShadowIntensScale = Byml.GetFloat(bake, "BakeShadowIntensScale", BakeShadowIntensScale);
            BakeAOIntensOffset = Byml.GetFloat(bake, "BakeAOIntensOffset", BakeAOIntensOffset);
            BakeAOIntensScale = Byml.GetFloat(bake, "BakeAOIntensScale", BakeAOIntensScale);
            BakeAOMainLightOcclude = Byml.GetFloat(
                bake,
                "BakeAOMainLightOcclude",
                BakeAOMainLightOcclude
            );
        }

        internal static Dictionary<string, object> Hash(Dictionary<string, object> hash, string key) =>
            hash != null && hash.TryGetValue(key, out var v)
                ? v as Dictionary<string, object>
                : null;

        internal static Vector4 Color(Dictionary<string, object> hash, string key, Vector4 fallback)
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
