using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using BfresEditor;
using GLFrameworkEngine;
using OpenTK.Graphics.OpenGL;
using PlayerViewer.Core;
using Toolbox.Core;

namespace PlayerViewer.Env
{
    /// <summary>
    /// Raiders' fill of the inputs every material shares, built from one rendering param group:
    /// the blocks, the light probe buffers and the env map cube.
    /// </summary>
    public sealed class RaidersFrame : IHoianFrameSource, IDisposable
    {
        public RaidersEnvParams Params { get; private set; }

        /// <summary>The packed ambient rows, or null when the env map could not be read.</summary>
        public Vector4[] AmbientSH { get; private set; }

        public EnvMapCube EnvMap { get; private set; }

        /// <summary>
        /// Whether the group has no env map of its own, so the ambient and the reflection cube
        /// are estimated from its sky.
        /// </summary>
        public bool Estimated { get; private set; }

        byte[] _environment;
        float _fogCameraHeight = float.NaN;
        int _lightCount;
        byte[] _user0;
        readonly byte[] _contextTemplate;
        readonly byte[] _environmentTemplate;
        readonly Dictionary<string, byte[]> _storage = new();
        GLTextureCubeArray _envCube;
        bool _envCubeFiltered;
        bool _envCubeTried;
        System.Threading.Tasks.Task<byte[][]> _reflection;

        //Buffers nothing writes: the cluster lists, the light array, the vertex user buffers.
        static readonly byte[] Zeros = new byte[256];

        RaidersFrame()
        {
            _contextTemplate = ReadResource("fp_c3.bin");
            _environmentTemplate = ReadResource("fp_c5.bin");
        }

        /// <summary>
        /// The frame for a param group, Customize by default. Null when the group is missing.
        /// Without <paramref name="reflections"/> the reflection cube is not built.
        /// </summary>
        public static RaidersFrame Load(
            Romfs romfs,
            string scene = RaidersEnv.DefaultScene,
            bool reflections = true
        )
        {
            var p = RaidersEnv.Load(romfs, scene);
            if (p == null)
                return null;
            var frame = new RaidersFrame();
            frame.EnvMap = EnvMapCube.Load(romfs, p.EnvMapTexture);
            frame.AmbientSH = frame.EnvMap?.AmbientSH();
            //Stages capture their env map from the stage itself at run time.
            if (frame.EnvMap == null && p.EnvMapTexture == null)
            {
                frame.AmbientSH = SkyAmbient.Estimate(romfs, p, out _);
                frame.EnvMap = EnvMapCube.FromSH(frame.AmbientSH);
                frame.Estimated = true;
            }
            frame.Build(p);
            var envMap = frame.EnvMap;
            if (envMap != null && reflections)
                frame._reflection = System.Threading.Tasks.Task.Run(() =>
                {
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    var layers = EnvPrefilter.Build(envMap);
                    Console.WriteLine(
                        $"[Raiders] Reflection cube filtered in {watch.ElapsedMilliseconds} ms"
                    );
                    return layers;
                });
            return frame;
        }

        void Build(RaidersEnvParams p)
        {
            Params = p;
            _environment = RaidersUniforms.BuildEnvironment(_environmentTemplate, p, AmbientSH);
            _user0 = RaidersUniforms.BuildUser0(p);
            _storage["gsys_user6"] = RaidersUniforms.ProbeGrid();
            _storage["gsys_user5"] = RaidersUniforms.Probes(AmbientSH);
        }

        public byte[] Block(string name) =>
            name switch
            {
                "gsys_environment" => _environment,
                "gsys_user0" => _user0,
                _ => null,
            };

        public void WriteContext(Span<byte> block, in HoianView view)
        {
            RaidersUniforms.WriteContext(block, _contextTemplate, view);
            RaidersUniforms.WriteLightClusterHeader(block, view, _lightCount);
            UpdateFog(view.View.Inverted().ExtractTranslation().Y);
        }

        public void SetLights(ILightTable lights)
        {
            var (clusters, objects) = lights.BuildStorageBuffers();
            _storage["gsys_view_proj_data"] = clusters;
            _storage["gsys_env_obj_data"] = objects;
            SetLightCount(lights.Count);
        }

        public void ClearLights()
        {
            _storage.Remove("gsys_view_proj_data");
            _storage.Remove("gsys_env_obj_data");
            SetLightCount(0);
        }

        void SetLightCount(int count)
        {
            if (count == _lightCount)
                return;
            _lightCount = count;
            var environment = (byte[])_environment.Clone();
            RaidersUniforms.WriteLightCount(environment, count);
            _environment = environment;
        }

        //A new array rather than an edit in place, since the renderer caches copies by reference.
        void UpdateFog(float cameraHeight)
        {
            if (Params == null || !(Params.ExpFogColor.W > 0) || cameraHeight == _fogCameraHeight)
                return;
            _fogCameraHeight = cameraHeight;
            var environment = (byte[])_environment.Clone();
            RaidersUniforms.WriteExpHeightFog(environment, Params, cameraHeight);
            _environment = environment;
        }

        public byte[] StorageBuffer(string name) =>
            _storage.TryGetValue(name, out var data) ? data : Zeros;

        public bool UsesStorageBuffers => true;

        public bool MaterialBlockFromDefault => true;

        public GLTexture EnvironmentCube => Texture("gsys_user1");

        public GLTexture Texture(string sampler)
        {
            if (sampler != "gsys_user1")
                return null;
            if (_envCubeFiltered)
                return _envCube;
            //The unfiltered map stands in until the reflection layers are built.
            if (_reflection == null || _reflection.IsCompleted)
            {
                var filtered = Upload(EnvMap, _reflection);
                _envCubeFiltered = true;
                if (filtered != null)
                {
                    _envCube?.Dispose();
                    _envCube = filtered;
                }
            }
            else if (!_envCubeTried)
            {
                _envCubeTried = true;
                _envCube = UploadSource(EnvMap);
            }
            return _envCube;
        }

        //The env map as it is, one cube: every roughness reads it sharp.
        static GLTextureCubeArray UploadSource(EnvMapCube cube)
        {
            if (cube == null)
                return null;
            bool half = cube.Format == TexFormat.RGBA16_FLOAT;
            if (!half && cube.Format != TexFormat.RG11B10_FLOAT)
                return null;
            return GLTextureCubeArray.FromLayers(
                cube.Size,
                1,
                cube.Mips,
                half ? PixelInternalFormat.Rgba16f : PixelInternalFormat.R11fG11fB10f,
                half ? PixelFormat.Rgba : PixelFormat.Rgb,
                half ? PixelType.HalfFloat : PixelType.UnsignedInt10F11F11FRev
            );
        }

        //The reflection layers, built off the render thread at load.
        static GLTextureCubeArray Upload(
            EnvMapCube cube,
            System.Threading.Tasks.Task<byte[][]> layers
        )
        {
            byte[][] mips = null;
            try
            {
                mips = layers?.Result;
            }
            catch (AggregateException ex)
            {
                Console.WriteLine($"[Raiders] Reflection cube: {ex.InnerException?.Message}");
            }
            if (cube == null || mips == null)
                return null;
            return GLTextureCubeArray.FromLayers(
                cube.Size,
                EnvPrefilter.LayerCount,
                mips,
                PixelInternalFormat.R11fG11fB10f,
                PixelFormat.Rgb,
                PixelType.UnsignedInt10F11F11FRev
            );
        }

        //The Splatoon 3 viewer dump, whose shared rows are the same in both games.
        static byte[] ReadResource(string file)
        {
            string path = Path.Combine("Resources", "SPL3", file);
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }

        /// <summary>Frees the cube. Call on the render thread.</summary>
        public void Dispose()
        {
            _envCube?.Dispose();
            _envCube = null;
        }
    }
}
