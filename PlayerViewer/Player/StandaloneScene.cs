using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BfresEditor;
using GLFrameworkEngine;
using OpenTK;
using PlayerViewer.Core;
using Toolbox.Core;
using Toolbox.Core.IO;

namespace PlayerViewer.Player
{
    /// <summary>Anything the viewport pipeline can render.</summary>
    public interface IViewScene
    {
        IEnumerable<BfresRender> AllRenders();
        void Draw(GLContext control, Pass pass);
    }

    /// <summary>
    /// Views a single bfres model outside the player scope (dropped file or romfs
    /// model). Lists the model's own skeletal animations; playing one also plays
    /// same-named texture-pattern / bone-visibility animations from the same file.
    /// </summary>
    public class StandaloneScene : IDisposable, IViewScene
    {
        public string Name { get; private set; } = "";
        public string SourcePath { get; private set; } = "";

        public BFRES Bfres { get; private set; }
        public BfresRender Render { get; private set; }

        public List<string> AnimNames { get; } = new();

        public string CurrentAnimName { get; private set; }
        public BfresSkeletalAnim CurrentSkeletal { get; private set; }
        public BfresMaterialAnim CurrentTexPattern { get; private set; }
        public BfresVisibilityAnim CurrentBoneVis { get; private set; }
        public float AnimFrame { get; private set; }
        public float AnimSpeed = 1.0f;
        public bool AnimPaused = false;

        //Default state for reset: bone visibility and shape (FSHP) visibility.
        readonly Dictionary<STBone, bool> _defaultBoneVisibility = new();
        readonly Dictionary<string, bool> _defaultShapeVisibility = new();

        /// <summary>Loads a model from a loose file (.bfres / .bfres.zs / .zs).</summary>
        public static StandaloneScene FromFile(string filePath, Romfs romfs)
        {
            var raw = File.ReadAllBytes(filePath);
            raw = Romfs.Decompress(raw);

            string stem = Path.GetFileName(filePath);
            if (stem.EndsWith(".zs"))
                stem = stem[..^3];
            if (stem.EndsWith(".bfres"))
                stem = stem[..^6];

            //Fake path inside romfs Model so the shader archive lookup works.
            string fakePath = Path.Combine(romfs.Root, "Model", stem + ".bfres");
            return Load(raw, fakePath, stem, filePath, romfs);
        }

        /// <summary>Loads a model from the (layered) romfs by model name.</summary>
        public static StandaloneScene FromRomfs(string modelName, Romfs romfs)
        {
            var data = romfs.ReadModel(modelName);
            if (data == null)
                return null;
            string fakePath = Path.Combine(romfs.Root, "Model", modelName + ".bfres");
            return Load(
                data,
                fakePath,
                modelName,
                romfs.Resolve($"Model/{modelName}.bfres") ?? "",
                romfs
            );
        }

        static StandaloneScene Load(
            byte[] data,
            string fakePath,
            string name,
            string sourcePath,
            Romfs romfs
        )
        {
            IFileFormat format;
            using (var ms = new MemoryStream(data))
                format = STFileLoader.OpenFileFormat(ms, fakePath);

            if (format is not BFRES bfres || bfres.Renderer.Models.Count == 0)
            {
                Console.WriteLine($"[Standalone] Not a renderable BFRES: {sourcePath}");
                return null;
            }

            var scene = new StandaloneScene
            {
                Name = name,
                SourcePath = sourcePath,
                Bfres = bfres,
                Render = (BfresRender)bfres.Renderer,
            };
            scene.Render.ID = "standalone_" + name;
            DataCache.ModelCache[scene.Render.ID] = scene.Render;

            BfresHelpers.ResolveSharedAssets(bfres, data, romfs);

            foreach (var anim in bfres.SkeletalAnimations)
                scene.AnimNames.Add(anim.Name);
            scene.AnimNames.Sort(StringComparer.OrdinalIgnoreCase);

            foreach (var model in scene.Render.Models.OfType<BfresModelAsset>())
            {
                foreach (var bone in model.ModelData.Skeleton.Bones)
                    scene._defaultBoneVisibility[bone] = bone.Visible;
                foreach (var mesh in model.Meshes)
                    scene._defaultShapeVisibility[model.ModelData.Name + "/" + mesh.Name] =
                        mesh.Shape.IsVisible;
            }

            return scene;
        }

        /// <summary>Bounding sphere of the whole model for camera framing.</summary>
        public Vector4 GetBounding()
        {
            var bs = Render.BoundingSphere;
            if (bs.W <= 0.0001f)
                return new Vector4(0, 0.5f, 0, 1.0f);
            return bs;
        }

        public void PlayAnim(string name)
        {
            var models = Render.Models.OfType<BfresModelAsset>().ToArray();

            ScopedAnimPlayer.ResetMaterialAnims(models);

            foreach (var model in models)
            {
                foreach (var bone in model.ModelData.Skeleton.Bones)
                    if (_defaultBoneVisibility.TryGetValue(bone, out bool visible))
                        bone.Visible = visible;
                foreach (var mesh in model.Meshes)
                    if (
                        _defaultShapeVisibility.TryGetValue(
                            model.ModelData.Name + "/" + mesh.Name,
                            out bool vis
                        )
                    )
                        mesh.Shape.IsVisible = vis;
                model.ModelData.Skeleton.Reset();
            }

            CurrentAnimName = name;
            AnimFrame = 0;

            CurrentSkeletal =
                name != null ? Bfres.SkeletalAnimations.FirstOrDefault(x => x.Name == name) : null;
            CurrentTexPattern =
                name != null ? Bfres.MaterialAnimations.FirstOrDefault(x => x.Name == name) : null;
            CurrentBoneVis =
                name != null
                    ? Bfres.VisibilityAnimations.FirstOrDefault(x => x.Name == name)
                    : null;

            //Animate ALL skeletons in the file (multi-model), but scope to only
            //this render so the hidden player skeleton is not affected.
            if (CurrentSkeletal != null)
            {
                CurrentSkeletal.SkeletonOverride = null;
                CurrentSkeletal.SkeletonOverrides = models
                    .Select(m => m.ModelData.Skeleton)
                    .ToList();
            }
        }

        public void SetAnimFrame(float frame) => AnimFrame = frame;

        /// <summary>Frame count of a skeletal animation by name (0 if unknown), for the chain timeline.</summary>
        public int SkeletalFrameCount(string name)
        {
            var anim =
                name != null ? Bfres.SkeletalAnimations.FirstOrDefault(x => x.Name == name) : null;
            return anim != null ? Math.Max((int)Math.Round((float)anim.FrameCount), 1) : 0;
        }

        public void Update(float deltaSeconds)
        {
            if (CurrentSkeletal == null)
                return;

            if (!AnimPaused)
            {
                AnimFrame += deltaSeconds * 60.0f * AnimSpeed;
                float frameCount = Math.Max(CurrentSkeletal.FrameCount, 1);
                if (CurrentSkeletal.Loop)
                    AnimFrame %= frameCount;
                else if (AnimFrame > frameCount - 1)
                    AnimFrame = frameCount - 1;
            }

            CurrentSkeletal.SetFrame(AnimFrame);
            CurrentSkeletal.NextFrame();

            var models = Render.Models.OfType<BfresModelAsset>().ToArray();
            if (CurrentTexPattern != null)
                ScopedAnimPlayer.ApplyMaterialAnim(CurrentTexPattern, AnimFrame, models);
            if (CurrentBoneVis != null)
                foreach (var model in models)
                    ScopedAnimPlayer.ApplyBoneVisAnim(
                        CurrentBoneVis,
                        AnimFrame,
                        model.ModelData.Skeleton,
                        models
                    );
        }

        public IEnumerable<BfresRender> AllRenders()
        {
            yield return Render;
        }

        public void Draw(GLContext control, Pass pass)
        {
            Render.DrawModel(control, pass, Vector4.Zero);
        }

        public void Dispose()
        {
            DataCache.ModelCache.Remove(Render.ID);
            Render.Dispose();
            Render = null;
            Bfres = null;
            AnimNames.Clear();
            CurrentSkeletal = null;
            CurrentTexPattern = null;
            CurrentBoneVis = null;
            _defaultBoneVisibility.Clear();
            _defaultShapeVisibility.Clear();
        }
    }
}
