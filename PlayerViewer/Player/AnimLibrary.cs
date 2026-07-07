using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BfresLibrary;
using BfresEditor;
using PlayerViewer.Core;

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
            _skeletal.Clear(); _texPattern.Clear(); _shaderParam.Clear(); _boneVis.Clear();
            _skeletalWrappers.Clear(); _texPatternWrappers.Clear(); _shaderParamWrappers.Clear(); _boneVisWrappers.Clear();

            var sources = new List<(string name, int priority)> { ("Player00", 0) };
            foreach (var variant in romfs.ListModelFiles("Player00_v-"))
                sources.Add((variant, 1));
            if (playerModel != "Player00")
            {
                sources.Add((playerModel, 2));
                foreach (var variant in romfs.ListModelFiles(playerModel + "_v-"))
                    sources.Add((variant, 3));
            }

            //Parse uncached sources in parallel (independent files, no GL work).
            var missing = sources.Where(s => !_parsed.ContainsKey(s.name)).Select(s => s.name).Distinct().ToArray();
            var parsed = new ResFile[missing.Length];
            System.Threading.Tasks.Parallel.For(0, missing.Length, i =>
            {
                byte[] data = romfs.ReadModelFile(missing[i]);
                if (data != null)
                    parsed[i] = new ResFile(new MemoryStream(data));
            });
            for (int i = 0; i < missing.Length; i++)
                _parsed[missing[i]] = parsed[i];    //null = file absent; cached too

            foreach (var (name, priority) in sources)
            {
                var resFile = _parsed.GetValueOrDefault(name);
                if (resFile == null)
                    continue;

                foreach (var anim in resFile.SkeletalAnims.Values)
                    Add(_skeletal, anim.Name, anim, resFile, priority);
                foreach (var anim in resFile.TexPatternAnims.Values)
                    Add(_texPattern, anim.Name, anim, resFile, priority);
                foreach (var anim in resFile.ShaderParamAnims.Values)
                    Add(_shaderParam, anim.Name, anim, resFile, priority);
                foreach (var anim in resFile.BoneVisibilityAnims.Values)
                    Add(_boneVis, anim.Name, anim, resFile, priority);
            }

            AnimNames = _skeletal.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
            //"Wait" is the natural default; float it to the top.
            if (AnimNames.Remove("Wait"))
                AnimNames.Insert(0, "Wait");
        }

        static void Add<T>(Dictionary<string, Entry<T>> map, string name, T anim, ResFile file, int priority)
        {
            if (!map.TryGetValue(name, out var existing) || priority >= existing.Priority)
                map[name] = new Entry<T> { Anim = anim, File = file, Priority = priority };
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
            return wrapper;
        }

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
