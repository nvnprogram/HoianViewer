using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BfresEditor;
using BfresLibrary;
using PlayerViewer.Core;
using Toolbox.Core.Animations;

namespace PlayerViewer.Player
{
    /// <summary>
    /// Layered animation lookup for a player type.
    /// Priority (high wins): Player0X_v-* > Player0X > Player00_v-* > Player00.
    /// Skeletal/texture-pattern/bone-visibility animations are resolved independently
    /// by name through the same layering.
    /// </summary>
    public class AnimLibrary
    {
        class Entry<T>
        {
            public T Anim;
            public ResFile File;
            public int Priority;
        }

        readonly Dictionary<string, Entry<SkeletalAnim>> _skeletal = new(StringComparer.Ordinal);
        readonly Dictionary<string, Entry<MaterialAnim>> _texPattern = new(StringComparer.Ordinal);
        readonly Dictionary<string, Entry<MaterialAnim>> _shaderParam = new(StringComparer.Ordinal);
        readonly Dictionary<string, Entry<VisibilityAnim>> _boneVis = new(StringComparer.Ordinal);

        //Wrapper cache so repeated plays don't re-generate keys.
        readonly Dictionary<string, BfresSkeletalAnim> _skeletalWrappers = new();
        readonly Dictionary<string, BfresMaterialAnim> _texPatternWrappers = new();
        readonly Dictionary<string, BfresMaterialAnim> _shaderParamWrappers = new();
        readonly Dictionary<string, BfresVisibilityAnim> _boneVisWrappers = new();

        public List<string> AnimNames { get; private set; } = new List<string>();

        /// <summary>The animation files loaded, the player's own first.</summary>
        public List<ResFile> Files { get; private set; } = new List<ResFile>();

        //Parsed anim files, cached per model name for the library's lifetime: they
        //are immutable once parsed and re-parsing them (zstd + full ResFile parse)
        //cost ~1s per player-type switch.
        readonly Dictionary<string, ResFile> _parsed = new();

        /// <summary>
        /// Loads all animation sources for the given player model (e.g. "Player02"):
        /// Player00, Player00_v-*, PlayerXX, PlayerXX_v-*.
        /// </summary>
        public void Load(Romfs romfs, string playerModel)
        {
            _skeletal.Clear();
            _texPattern.Clear();
            _shaderParam.Clear();
            _boneVis.Clear();
            _skeletalWrappers.Clear();
            _texPatternWrappers.Clear();
            _shaderParamWrappers.Clear();
            _boneVisWrappers.Clear();

            var sources = new List<(string name, int priority)> { ("Player00", 0) };
            foreach (var variant in romfs.ListModelFiles("Player00_v-"))
                sources.Add((variant, 1));
            if (playerModel != "Player00")
            {
                sources.Add((playerModel, 2));
                foreach (var variant in romfs.ListModelFiles(playerModel + "_v-"))
                    sources.Add((variant, 3));
            }
            var cutscenes = romfs.Layout.CutsceneFiles();
            foreach (var file in cutscenes)
                sources.Add((file, -1));

            //Parse uncached sources in parallel (independent files, no GL work).
            var missing = sources
                .Where(s => !_parsed.ContainsKey(s.name))
                .Select(s => s.name)
                .Distinct()
                .ToArray();
            var parsed = new ResFile[missing.Length];
            System.Threading.Tasks.Parallel.For(
                0,
                missing.Length,
                i =>
                {
                    byte[] data = romfs.ReadModelFile(romfs.AnimFileOf(missing[i]));
                    if (data != null)
                        parsed[i] = new ResFile(new MemoryStream(data));
                }
            );
            for (int i = 0; i < missing.Length; i++)
                _parsed[missing[i]] = parsed[i]; //null = file absent; cached too

            foreach (var (name, priority) in sources)
            {
                var resFile = _parsed.GetValueOrDefault(name);
                if (resFile == null)
                    continue;

                //Cutscene files reuse shot names, so their animations go under the file's name.
                string prefix = priority < 0 ? name + "/" : "";
                foreach (var anim in resFile.SkeletalAnims.Values)
                    Add(_skeletal, prefix + anim.Name, anim, resFile, priority);
                foreach (var anim in resFile.TexPatternAnims.Values)
                    Add(_texPattern, prefix + anim.Name, anim, resFile, priority);
                foreach (var anim in resFile.ShaderParamAnims.Values)
                    Add(_shaderParam, prefix + anim.Name, anim, resFile, priority);
                foreach (var anim in resFile.BoneVisibilityAnims.Values)
                    Add(_boneVis, prefix + anim.Name, anim, resFile, priority);
            }

            Files = sources
                .OrderByDescending(x => x.priority)
                .Select(x => _parsed.GetValueOrDefault(x.name))
                .Where(x => x != null)
                .Distinct()
                .ToList();
            //The cutscenes after the player's own animations.
            AnimNames = _skeletal
                .Keys.OrderBy(x => x.Contains('/'))
                .ThenBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToList();
            //"Wait" is the natural default; float it to the top.
            if (AnimNames.Remove("Wait"))
                AnimNames.Insert(0, "Wait");
        }

        static void Add<T>(
            Dictionary<string, Entry<T>> map,
            string name,
            T anim,
            ResFile file,
            int priority
        )
        {
            if (!map.TryGetValue(name, out var existing) || priority >= existing.Priority)
                map[name] = new Entry<T>
                {
                    Anim = anim,
                    File = file,
                    Priority = priority,
                };
        }

        public bool HasAnim(string name) => _skeletal.ContainsKey(name);

        public BfresSkeletalAnim GetSkeletal(string name)
        {
            if (_skeletalWrappers.TryGetValue(name, out var cached))
                return cached;
            if (!_skeletal.TryGetValue(name, out var entry))
                return null;
            var wrapper = new BfresSkeletalAnim(entry.File, entry.Anim, entry.File.Name);
            _skeletalWrappers[name] = wrapper;
            if (entry.Priority < 0)
                PlaceCutscene(wrapper);
            return wrapper;
        }

        const string CutsceneRootBone = "Skl_Root";

        void PlaceCutscene(BfresSkeletalAnim anim)
        {
            var root = RootGroup(anim);
            var home = _skeletal.ContainsKey("Wait") ? RootGroup(GetSkeletal("Wait")) : null;
            if (root == null)
                return;
            var tracks = new[] { root.Translate.X, root.Translate.Y, root.Translate.Z };
            var homeTracks =
                home == null
                    ? null
                    : new[] { home.Translate.X, home.Translate.Y, home.Translate.Z };
            for (int axis = 0; axis < 3; axis++)
            {
                var track = tracks[axis];
                if (!track.HasKeys)
                    continue;
                float target =
                    homeTracks != null && homeTracks[axis].HasKeys
                        ? homeTracks[axis].GetFrameValue(0)
                        : 0;
                float start = track.GetFrameValue(0);
                if (axis == 1 && Travel(track, anim.FrameCount, start) < MaxCutsceneRise)
                {
                    foreach (var key in track.KeyFrames)
                        key.Value += target - start;
                    continue;
                }
                track.KeyFrames.Clear();
                track.KeyFrames.Add(new STKeyFrame(0, target));
            }
        }

        const float MaxCutsceneRise = 1;

        static float Travel(STAnimationTrack track, float frames, float from)
        {
            float most = 0;
            for (float f = 0; f <= frames; f++)
                most = MathF.Max(most, MathF.Abs(track.GetFrameValue(f) - from));
            return most;
        }

        static BfresSkeletalAnim.BoneAnimGroup RootGroup(BfresSkeletalAnim anim) =>
            anim
                ?.AnimGroups.OfType<BfresSkeletalAnim.BoneAnimGroup>()
                .FirstOrDefault(g => g.Name == CutsceneRootBone);

        public BfresMaterialAnim GetTexPattern(string name)
        {
            if (_texPatternWrappers.TryGetValue(name, out var cached))
                return cached;
            if (!_texPattern.TryGetValue(name, out var entry))
                return null;
            var wrapper = new BfresMaterialAnim(entry.Anim, entry.File.Name);
            _texPatternWrappers[name] = wrapper;
            return wrapper;
        }

        public BfresMaterialAnim GetShaderParam(string name)
        {
            if (_shaderParamWrappers.TryGetValue(name, out var cached))
                return cached;
            if (!_shaderParam.TryGetValue(name, out var entry))
                return null;
            var wrapper = new BfresMaterialAnim(entry.Anim, entry.File.Name);
            _shaderParamWrappers[name] = wrapper;
            return wrapper;
        }

        public BfresVisibilityAnim GetBoneVis(string name)
        {
            if (_boneVisWrappers.TryGetValue(name, out var cached))
                return cached;
            if (!_boneVis.TryGetValue(name, out var entry))
                return null;
            var wrapper = new BfresVisibilityAnim(entry.Anim, entry.File.Name);
            _boneVisWrappers[name] = wrapper;
            return wrapper;
        }
    }
}
