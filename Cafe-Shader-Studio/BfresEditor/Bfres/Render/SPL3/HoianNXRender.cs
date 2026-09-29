using System;
using System.Runtime.InteropServices;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using OpenTK.Graphics.OpenGL;
using OpenTK;
using Toolbox.Core;
using BfresEditor.Properties;
using GLFrameworkEngine;
using Gsys;

namespace BfresEditor
{
    /// <summary>
    /// Material renderer for Splatoon 3 (Hoian) models using the game's Hoian_UBER shader archive.
    ///
    /// Uniform block layout:
    ///   gsys_context     (vp_c3/fp_c3)  - camera/view data.
    ///   gsys_shape       (vp_c4)        - shape transform.
    ///   gsys_material    (vp_c5/fp_c6)  - material params from the BFRES
    ///   gsys_environment (fp_c5)        - lighting environment (dir light + SH ambient)
    ///   gsys_user0       (fp_c7)        - ink/shadow/fog parameters from a dumped in-game buffer.
    ///   gsys_user3       (fp_c8)        - team colors (7 derived variants per team).
    ///   gsys_user2       (fp_c10)       - clustered light table from a dumped in-game buffer.
    ///   gsys_skeleton                   - bone matrices.
    /// </summary>
    public partial class HoianNXRender : BfshaRenderer
    {
        /// <summary>
        /// Optional path to the Splatoon 3 romfs folder used to locate the shader archive
        /// when the model is not loaded from inside a romfs dump. Set from Config.json.
        /// </summary>
        public static string GamePath = "";

        /// <summary>
        /// Whether any Splatoon 3 material is currently loaded (used to show the SP3 tool window).
        /// </summary>
        public static bool IsActive { get; private set; }

        // Team colors (linear RGB) editable at runtime.
        public static System.Numerics.Vector3 TeamAlphaColor = new System.Numerics.Vector3(0.4483f, 0.2684f, 0.7456f);
        public static System.Numerics.Vector3 TeamBravoColor = new System.Numerics.Vector3(0.3057f, 0.3961f, 0.9980f);
        public static System.Numerics.Vector3 TeamCharlieColor = new System.Numerics.Vector3(0.5507f, 0.1312f, 0.1312f);

        /// <summary>The colour set's hue settings for each team, applied to its hue variants.</summary>
        public static TeamColorVariants.HueOffset TeamAlphaHue, TeamBravoHue, TeamCharlieHue;

        /// <summary>
        /// Resets the team colors back to the dumped in-game defaults.
        /// </summary>
        public static void ResetTeamColors()
        {
            if (User3Data == null) return;
            TeamAlphaColor = ReadVec3(User3Data, 0);
            TeamBravoColor = ReadVec3(User3Data, 7 * 16);
            TeamCharlieColor = ReadVec3(User3Data, 14 * 16);
        }

        /// <summary>
        /// Logs per material shader option sets, resolved program passes and every sampler
        /// binding. Very verbose.
        /// </summary>
        public static readonly bool DebugMaterials = TegraShaderDecoder.DebugLog;

        public override bool UseRenderer(FMAT material, string archive, string model)
        {
            bool use = archive != null && archive.StartsWith("Hoian_UBER");
            if (use) IsActive = true;
            return use;
        }

        #region Dumped uniform data (Resources/SPL3)

        static bool _resourcesLoaded = false;
        static byte[] ContextTemplate;  // fp_c3.bin - gsys_context (camera etc)
        static byte[] EnvironmentData;  // fp_c5.bin - gsys_environment
        static byte[] User0Data;        // fp_c7.bin - gsys_user0
        static byte[] User3Data;        // fp_c8.bin - gsys_user3 (team colors)
        static byte[] User2Data;        // fp_c10.bin - gsys_user2 (light clusters)

        /// <summary>
        /// Optional world-space main light direction (direction the light travels).
        /// When set, it is patched into the gsys_environment block each frame.
        /// </summary>
        public static OpenTK.Vector3? LightDirOverride = null;

        /// <summary>
        /// Generated gsys_environment and gsys_user0 contents. When set they replace the dumped
        /// blocks of the current uniform set; the other blocks still come from the dump.
        /// </summary>
        public static byte[] EnvironmentOverride = null;
        public static byte[] User0Override = null;

        /// <summary>A light cluster to bind as gsys_user2 in place of the empty one.</summary>
        public static byte[] LightClusterOverride = null;

        static byte[] EnvBlock => EnvironmentOverride ?? EnvironmentData;
        static byte[] User0Block => User0Override ?? User0Data;

        //Offset of the main directional light vector inside fp_c5 (env block).
        const int EnvLightDirOffset = 22 * 16 + 16;

        /// <summary>
        /// Optional screen-space shadow prepass texture (bound as gsys_shadow_prepass).
        /// The game renders this from scene depth + a cascade shadow map; its green
        /// channel is the sun visibility, which also damps the env specular by
        /// cEnvSpecShadowRate (fp_c7[36].w).
        /// </summary>
        public static GLTexture ShadowPrepassTexture = null;

        /// <summary>
        /// Half-resolution scene color buffer for refraction materials (blitz_refract_type).
        /// Set by the pipeline between opaque and transparent passes.
        /// </summary>
        public static GLTexture RefractionColorBuffer = null;

        /// <summary>
        /// Scene depth buffer for refraction materials. Set by the pipeline.
        /// </summary>
        public static GLTexture RefractionDepthBuffer = null;

        /// <summary>
        /// Set to true when any loaded material has blitz_refract_type, so the
        /// pipeline knows it needs to capture refraction buffers this frame.
        /// </summary>
        public static bool NeedsRefractionBuffers = false;

        /// <summary>
        /// The world-space direction the main light travels (override or dumped env data).
        /// </summary>
        public static OpenTK.Vector3 GetMainLightDir()
        {
            if (LightDirOverride != null)
                return LightDirOverride.Value.Normalized();
            LoadResourceData();
            var env = EnvBlock;
            if (env != null && env.Length >= EnvLightDirOffset + 12)
            {
                var dir = new OpenTK.Vector3(
                    BitConverter.ToSingle(env, EnvLightDirOffset),
                    BitConverter.ToSingle(env, EnvLightDirOffset + 4),
                    BitConverter.ToSingle(env, EnvLightDirOffset + 8));
                if (dir.LengthSquared > 0.0001f)
                    return dir.Normalized();
            }
            return new OpenTK.Vector3(0, -1, 0);
        }

        // Per row brightness ratio of the dumped fp_c8 to its team's base colour, 3 teams x 7 rows.
        // Only the ink rows use it.
        static float[] _teamColorRatios;

        /// <summary>
        /// Which dumped uniform set to load from Resources/: "SPL3" (gear viewer dump,
        /// "Viewer") or "SPL3_AutoWalk" (in-stage autowalk dump, "AutoWalk").
        /// Switch via SetUniformSet so the cached blocks get reloaded.
        /// </summary>
        public static string UniformSetDir { get; private set; } = "SPL3";

        /// <summary>
        /// Switches the dumped uniform set (fp_c3/c5/c7/c8/c10) and reloads it.
        /// Resets team colors to the new dump's defaults; callers that override
        /// them (PlayerViewer) should re-apply afterwards.
        /// </summary>
        public static void SetUniformSet(string dirName)
        {
            if (UniformSetDir == dirName) return;
            UniformSetDir = dirName;
            _resourcesLoaded = false;
            LoadResourceData();
        }

        //Public so external tools (PlayerViewer) can force-load before overriding
        //TeamAlphaColor etc - otherwise the lazy load on first draw would clobber them.
        public static void LoadResourceData()
        {
            if (_resourcesLoaded) return;
            _resourcesLoaded = true;

            string dir = Path.Combine("Resources", UniformSetDir);
            byte[] TryLoad(string file)
            {
                string path = Path.Combine(dir, file);
                if (File.Exists(path)) return File.ReadAllBytes(path);
                Console.WriteLine($"[SPL3] Missing uniform data file {path}; using zeros.");
                return null;
            }

            ContextTemplate = TryLoad("fp_c3.bin");
            EnvironmentData = TryLoad("fp_c5.bin");
            User0Data = TryLoad("fp_c7.bin");
            User3Data = TryLoad("fp_c8.bin");
            User2Data = TryLoad("fp_c10.bin");

            ClearClusterLights();
            InitTeamColorData();
        }

        /// <summary>
        /// Clears the light cluster grid in the dumped gsys_user2 buffer.
        /// </summary>
        static void ClearClusterLights()
        {
            if (User2Data == null)
                return;

            byte[] noLights = BitConverter.GetBytes(-1);
            for (int cell = 0; cell < 400; cell++)
            {
                int offset = cell * 16;
                if (offset + 4 > User2Data.Length) break;
                System.Buffer.BlockCopy(noLights, 0, User2Data, offset, 4);
            }
        }

        static System.Numerics.Vector3 ReadVec3(byte[] data, int offset)
        {
            return new System.Numerics.Vector3(
                BitConverter.ToSingle(data, offset),
                BitConverter.ToSingle(data, offset + 4),
                BitConverter.ToSingle(data, offset + 8));
        }

        static void InitTeamColorData()
        {
            if (User3Data == null || User3Data.Length < 21 * 16)
                return;

            var baseColors = new[] { ReadVec3(User3Data, 0), ReadVec3(User3Data, 7 * 16), ReadVec3(User3Data, 14 * 16) };
            TeamAlphaColor = baseColors[0];
            TeamBravoColor = baseColors[1];
            TeamCharlieColor = baseColors[2];

            _teamColorRatios = new float[21];
            _variantsBuilt = false;
            for (int i = 0; i < 21; i++)
            {
                var baseCol = baseColors[i / 7];
                float baseMax = Math.Max(baseCol.X, Math.Max(baseCol.Y, baseCol.Z));
                if (baseMax < 0.001f) { _teamColorRatios[i] = 1f; continue; }

                var cur = ReadVec3(User3Data, i * 16);
                float curMax = Math.Max(cur.X, Math.Max(cur.Y, cur.Z));
                _teamColorRatios[i] = curMax / baseMax;
            }
        }

        //Every variant of the three team colours, rebuilt when a colour, a hue setting or the offset table changes.
        static readonly System.Numerics.Vector3[] _variants = new System.Numerics.Vector3[3 * TeamColorVariants.KindCount];
        static (System.Numerics.Vector3, System.Numerics.Vector3, System.Numerics.Vector3, TeamColorVariants.HueOffset,
            TeamColorVariants.HueOffset, TeamColorVariants.HueOffset, object) _variantsKey;
        static bool _variantsBuilt;

        /// <summary>
        /// A variant of a team colour (0 alpha, 1 bravo, 2 charlie). Ink and InkBright depend on
        /// the scene, so they keep the dumped buffer's ratio to the base colour.
        /// </summary>
        static System.Numerics.Vector3 GetTeamColorVariant(int team, TeamColorVariants.Kind kind)
        {
            var key = (TeamAlphaColor, TeamBravoColor, TeamCharlieColor, TeamAlphaHue, TeamBravoHue, TeamCharlieHue,
                (object)TeamColorVariants.Offsets);
            if (!_variantsBuilt || key != _variantsKey)
            {
                for (int t = 0; t < 3; t++)
                {
                    var col = t == 0 ? TeamAlphaColor : t == 1 ? TeamBravoColor : TeamCharlieColor;
                    var hue = t == 0 ? TeamAlphaHue : t == 1 ? TeamBravoHue : TeamCharlieHue;
                    for (int k = 0; k < TeamColorVariants.KindCount; k++)
                        _variants[t * TeamColorVariants.KindCount + k] = TeamColorVariants.Get(col, (TeamColorVariants.Kind)k, hue);
                    float Ratio(int row) => _teamColorRatios != null ? _teamColorRatios[t * 7 + row] : 1f;
                    _variants[t * TeamColorVariants.KindCount + (int)TeamColorVariants.Kind.Ink] = col * Ratio(3);
                    _variants[t * TeamColorVariants.KindCount + (int)TeamColorVariants.Kind.InkBright] = col * Ratio(4);
                }
                _variantsKey = key;
                _variantsBuilt = true;
            }
            return _variants[team * TeamColorVariants.KindCount + (int)kind];
        }

        #endregion

        #region Shader archive lookup

        static System.Threading.Tasks.Task _prewarmTask;

        /// <summary>
        /// Starts decompressing/parsing the game shader archives (Hoian_UBER ~25MB
        /// zstd) on a background thread. The first shader lookup waits for it, so
        /// calling this right after GamePath is set hides the archive load behind
        /// startup work. Safe to call multiple times.
        /// </summary>
        public static void PrewarmShaderArchives()
        {
            if (_prewarmTask != null || string.IsNullOrEmpty(GamePath))
                return;
            string dir = Path.Combine(GamePath, "Shader");
            if (!Directory.Exists(dir))
                return;

            _prewarmTask = System.Threading.Tasks.Task.Run(() =>
            {
                var files = Directory.GetFiles(dir)
                    .Where(x => x.EndsWith(".bfsha") || x.EndsWith(".bfsha.zs"))
                    .OrderBy(x => Path.GetFileName(x).Contains(".Product.") ? 0 : 1)
                    .ToList();
                foreach (var file in files)
                    LoadArchiveFile(file);
            });
        }

        /// <summary>
        /// Loads the material renderer, choosing between the shader archives that can
        /// serve this material.
        ///
        /// An archive shipped with the model wins over the game one, but only if it can
        /// actually draw the material.
        /// </summary>
        //The file the material came from, for the probe an edit re-runs.
        BFRES _bfres;

        public override void TryLoadShader(BFRES bfres, FMDL fmdl, FSHP mesh, BfresMeshAsset meshAsset)
        {
            _bfres = bfres;
            ArchiveTime.Start();
            var archives = EnumerateArchives(bfres, mesh.Material.ShaderArchive).ToList();
            ArchiveTime.Stop();

            BfshaLibrary.ShaderModel chosen = null;
            BfshaLibrary.BfshaFile chosenArchive = null;
            foreach (var (source, bfsha) in archives)
            {
                var model = bfsha.ShaderModels.Values.FirstOrDefault(x => x.Name == mesh.Material.ShaderModel);
                if (model == null)
                    continue;

                if (chosen == null)
                {
                    chosen = model;
                    chosenArchive = bfsha;
                }

                ShaderModel = model;
                if (model.GetProgramIndex(BuildOptions(mesh.Material, meshAsset)) == -1)
                    continue;

                chosen = model;
                chosenArchive = bfsha;
                if (DebugMaterials)
                    Console.WriteLine($"[SPL3dbg] material '{mesh.Material.Name}' archive "
                        + $"'{bfsha.Name}' from {source} ({archives.Count} candidate(s))");
                break;
            }

            if (chosen == null)
                return;

            ShaderArchiveFile = chosenArchive;
            OnLoadTime.Start();
            OnLoad(chosen, fmdl, mesh, meshAsset);
            OnLoadTime.Stop();
        }

        //The archive is chosen by probing for the material's key and an edit moves the key,
        //so an edit that the current archive cannot serve runs the probe again. Only a
        //candidate that serves the material pass replaces it; otherwise the current archive,
        //which may be a preview bound over the base, is left alone.
        void Reprobe(FMAT mat, BfresMeshAsset mesh, Dictionary<string, string> options)
        {
            if (_bfres == null || ShaderModel == null || ShaderModel.GetProgramIndex(options) != -1)
                return;
            foreach (var (source, bfsha) in EnumerateArchives(_bfres, mat.ShaderArchive))
            {
                var model = bfsha.ShaderModels.Values.FirstOrDefault(x => x.Name == mat.ShaderModel);
                if (model == null || ReferenceEquals(model, ShaderModel) || model.GetProgramIndex(options) == -1)
                    continue;
                if (DebugMaterials)
                    Console.WriteLine($"[SPL3dbg] material '{mat.Name}' moved to archive '{bfsha.Name}' from {source}");
                SwitchArchive(bfsha, model, mesh, true);
                return;
            }
        }

        /// <summary>
        /// Shader archives that could serve a material, most specific source first:
        /// embedded in this BFRES, then its parent pack, then a Shader folder next to
        /// (or above) the model, then the configured game path.
        /// </summary>
        IEnumerable<(string Source, BfshaLibrary.BfshaFile Archive)> EnumerateArchives(
            BFRES bfres, string shaderFile)
        {
            _prewarmTask?.Wait();

            bfres.UpdateExternalShaderFiles();
            foreach (var file in bfres.ShaderFiles)
            {
                if (file is BfshaLibrary.BfshaFile bfsha)
                    yield return ("bfres", bfsha);
            }

            var archiveFile = bfres.FileInfo.ParentArchive;
            if (archiveFile != null)
            {
                foreach (var file in archiveFile.Files)
                {
                    if (!file.FileName.Contains(shaderFile))
                        continue;
                    if (file.FileFormat == null)
                        file.FileFormat = file.OpenFile();
                    if (file.FileFormat is BFSHA bfshaFormat)
                        yield return ("pack", bfshaFormat.BfshaFile);
                }
            }

            foreach (var shaderDir in GetShaderSearchDirs(bfres))
            {
                foreach (var file in FindArchiveFiles(shaderDir, shaderFile))
                {
                    var bfsha = LoadArchiveFile(file);
                    if (bfsha != null)
                        yield return (shaderDir, bfsha);
                }
            }
        }

        static readonly Dictionary<string, string[]> _archiveFileCache = new();

        static string[] FindArchiveFiles(string shaderDir, string shaderFile)
        {
            string key = $"{shaderDir}|{shaderFile}";
            if (_archiveFileCache.TryGetValue(key, out var cached))
                return cached;

            string[] files = Array.Empty<string>();
            if (Directory.Exists(shaderDir))
            {
                //Prefer the main Product archive over the cutscene (Eve) archive.
                files = Directory.GetFiles(shaderDir)
                    .Where(x => Path.GetFileName(x).StartsWith(shaderFile) &&
                               (x.EndsWith(".bfsha") || x.EndsWith(".bfsha.zs")))
                    .OrderBy(x => Path.GetFileName(x).Contains(".Product.") ? 0 : 1)
                    .ToArray();
            }

            _archiveFileCache[key] = files;
            return files;
        }

        public override BfshaLibrary.BfshaFile TryLoadShaderArchive(BFRES bfres, string shaderFile, string shaderModel)
        {
            foreach (var (_, bfsha) in EnumerateArchives(bfres, shaderFile))
            {
                if (bfsha.ShaderModels.FirstOrDefault(x => x.Name == shaderModel) != null)
                    return bfsha;
            }
            return null;
        }

        static IEnumerable<string> GetShaderSearchDirs(BFRES bfres)
        {
            //Walk up from the model location looking for a romfs style Shader folder.
            string dir = null;
            try { dir = Path.GetDirectoryName(bfres.FileInfo.FilePath); } catch { }

            for (int i = 0; i < 3 && !string.IsNullOrEmpty(dir); i++)
            {
                yield return Path.Combine(dir, "Shader");
                dir = Path.GetDirectoryName(dir);
            }

            if (!string.IsNullOrEmpty(GamePath))
            {
                yield return Path.Combine(GamePath, "Shader");
                yield return GamePath;
            }
        }

        static BfshaLibrary.BfshaFile LoadArchiveFile(string path)
        {
            //Cache by path so the archive only gets decompressed/parsed once.
            if (GlobalShaderCache.ShaderFiles.ContainsKey(path))
                return GlobalShaderCache.ShaderFiles[path] as BfshaLibrary.BfshaFile;

            try
            {
                BfshaLibrary.BfshaFile bfsha;
                if (path.EndsWith(".zs"))
                {
                    var decompressed = CompressionLibrary.Zstb.SDecompress(File.ReadAllBytes(path));
                    bfsha = new BfshaLibrary.BfshaFile(new MemoryStream(decompressed));
                }
                else
                    bfsha = new BfshaLibrary.BfshaFile(path);

                GlobalShaderCache.ShaderFiles.Add(path, bfsha);
                return bfsha;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SPL3] Failed to load shader archive {path}: {ex.Message}");
                return null;
            }
        }

        #endregion

        #region Program lookup

        public override void ReloadRenderState(BfresMeshAsset mesh)
        {
            var mat = mesh.Shape.Material;

            if (mat.GetRenderInfo("gsys_static_depth_shadow_only") == "1")
                mesh.IsDepthShadow = true;
            if (mat.GetRenderInfo("gsys_pass") == "seal")
                mesh.IsSealPass = true;
            if (mat.GetRenderInfo("gsys_cube_map_only") == "1")
                mesh.IsCubeMap = true;

            //Translucent (XLU) materials must draw after all opaque geometry;
            //they do not write depth, so drawing them in the opaque pass lets
            //later opaque meshes overwrite them (e.g. glasses lenses over skin).
            if (mat.BlendState.State == GLMaterialBlendState.BlendState.Translucent ||
                mat.BlendState.State == GLMaterialBlendState.BlendState.Custom)
                mesh.Pass = Pass.TRANSPARENT;

            //Materials that sample the game framebuffer (gsys_enable_color_buffer=1)
            //must render in the transparent pass so the pipeline can capture the
            //framebuffer between passes.
            bool usesFramebuffer =
                (mat.ShaderOptions.TryGetValue("gsys_enable_color_buffer", out string ecb) && ecb == "1");
            if (usesFramebuffer)
            {
                mesh.Pass = Pass.TRANSPARENT;
                mesh.IsRefractionPass = true;
                NeedsRefractionBuffers = true;
            }
        }

        /// <summary>
        /// The shader option set used to look up a program: the material's own options, the
        /// ones the engine derives from its render state, and the dynamic ones it writes per
        /// draw.
        /// </summary>
        Dictionary<string, string> BuildOptions(FMAT mat, BfresMeshAsset mesh)
        {
            var options = GsysShaderOptions.BuildStaticOptions(mat.Material);
            GsysShaderOptions.AddDynamicOptions(options, mesh.Shape.VertexSkinCount,
                GsysShaderOptions.AssignTypes[0]);
            return options;
        }

        static readonly string[] RenderPasses =
        {
            "gsys_assign_material",
            "gsys_assign_zonly",
            "gsys_assign_gbuffer",
        };

        public override void ReloadProgram(BfresMeshAsset mesh)
        {
            var mat = mesh.Shape.Material;

            ProgramPasses.Clear();

            var options = BuildOptions(mat, mesh);
            Reprobe(mat, mesh, options);

            //Materials repeat the same option sets across models (skin, gear cloth,
            //accessories...), so the resolved program indices are memoized.
            var passCache = _programPassCache.GetValue(ShaderModel, _ => new Dictionary<string, int[]>());
            string cacheKey = string.Join(";", options.OrderBy(x => x.Key, StringComparer.Ordinal)
                .Select(x => $"{x.Key}={x.Value}"));

            if (!passCache.TryGetValue(cacheKey, out int[] passIndices))
            {
                var indices = new List<int>();
                for (int pass = 0; pass < RenderPasses.Length; pass++)
                {
                    string assignType = GetValidAssignType(RenderPasses[pass]);
                    options["gsys_assign_type"] = assignType;

                    int programIndex = ShaderModel.GetProgramIndex(options);
                    if (DebugMaterials)
                        Console.WriteLine($"[SPL3dbg]   assign {assignType} -> {programIndex}");

                    //Pass 0 is the visible draw. Later passes must keep their index, so the
                    //list stops at the first one that has no program rather than shifting.
                    if (programIndex == -1)
                    {
                        if (pass == 0)
                            Console.WriteLine($"[SPL3] Material '{mat.Name}': no "
                                + $"{assignType} program for its option set.");
                        break;
                    }

                    indices.Add(programIndex);
                }
                passIndices = indices.ToArray();
                passCache[cacheKey] = passIndices;
            }

            foreach (int programIndex in passIndices)
                this.ProgramPasses.Add(ShaderModel.GetShaderProgram(programIndex));

            if (DebugMaterials)
            {
                Console.WriteLine($"[SPL3dbg] material '{mat.Name}' model '{ShaderModel.Name}' "
                    + $"({ShaderModel.ProgramCount} programs) passes [{string.Join(",", passIndices)}]");
                Console.WriteLine($"[SPL3dbg]   options {cacheKey}");
                if (ProgramPasses.Count > 0)
                {
                    var blocks = new List<string>();
                    for (int i = 0; i < ShaderModel.UniformBlocks.Count; i++)
                    {
                        var loc = ProgramPasses[0].UniformBlockLocations[i];
                        if (loc.VertexLocation == -1 && loc.FragmentLocation == -1)
                            continue;
                        blocks.Add($"{ShaderModel.UniformBlocks.GetKey(i)}"
                            + $"(vp_c{loc.VertexLocation + 3}/fp_c{loc.FragmentLocation + 3}"
                            + $" size={ShaderModel.UniformBlocks[i].Size})");
                    }
                    Console.WriteLine($"[SPL3dbg]   blocks {string.Join(" ", blocks)}");

                    var samplers = new List<string>();
                    for (int i = 0; i < ShaderModel.Samplers.Count; i++)
                    {
                        var loc = ProgramPasses[0].SamplerLocations[i];
                        if (loc.VertexLocation == -1 && loc.FragmentLocation == -1)
                            continue;
                        samplers.Add($"{ShaderModel.Samplers.GetKey(i)}="
                            + (loc.FragmentLocation != -1
                                ? ConvertSamplerID(loc.FragmentLocation)
                                : ConvertSamplerID(loc.VertexLocation, true)));
                    }
                    Console.WriteLine($"[SPL3dbg]   samplerUniforms {string.Join(" ", samplers)}");
                }
                foreach (var kv in mat.Material.ShaderAssign.SamplerAssigns)
                {
                    var texMap = mat.TextureMaps.FirstOrDefault(x => x.Sampler == kv.Value.String);
                    Console.WriteLine($"[SPL3dbg]   sampler {kv.Key} -> {kv.Value.String} "
                        + $"-> {(texMap == null ? "<no texture map>" : texMap.Name)}");
                }
            }
        }

        //Assign type choices the current archive declares, per shader model.
        static readonly System.Runtime.CompilerServices.ConditionalWeakTable<BfshaLibrary.ShaderModel,
            HashSet<string>> _assignTypeCache = new();

        string GetValidAssignType(string assignType)
        {
            var available = _assignTypeCache.GetValue(ShaderModel, model =>
            {
                var option = model.DynamiOptions["gsys_assign_type"];
                return option == null
                    ? new HashSet<string>()
                    : new HashSet<string>(option.ChoiceDict.GetKeys().Where(x => !string.IsNullOrEmpty(x)));
            });
            return GsysShaderOptions.GetValidAssignType(assignType, available);
        }

        //Option set -> resolved program indices, per shader model.
        static readonly System.Runtime.CompilerServices.ConditionalWeakTable<BfshaLibrary.ShaderModel,
            Dictionary<string, int[]>> _programPassCache = new();

        #endregion

        #region Uniform blocks

        //Filled alike for every material, so one shared buffer each is uploaded only on a change.
        protected override bool IsFrameBlock(string name) =>
            name == "gsys_context" || name == "gsys_environment" || name == "gsys_user0"
            || name == "gsys_user2" || name == "gsys_user3";

        //The material's parameters and options depend on no mesh; the frame blocks on the pass.
        protected override BlockReuse BlockLifetime(string name) => name switch
        {
            "gsys_material" or "gsys_shader_option" => BlockReuse.Render,
            _ when IsFrameBlock(name) => BlockReuse.Pass,
            _ => BlockReuse.Draw,
        };

        public override void LoadUniformBlock(GLContext control, ShaderProgram shader, int index, UniformBlock block, string name, GenericPickableMesh mesh)
        {
            LoadResourceData();

            var bfresMaterial = (FMAT)this.MaterialData;
            var bfresMesh = (BfresMeshAsset)mesh;
            var meshBone = ParentModel.Skeleton.Bones[bfresMesh.BoneIndex];
            int blockSize = ShaderModel.UniformBlocks[index].Size;

            switch (name)
            {
                case "gsys_context":
                    SetContextBlock(control.Camera, block, blockSize);
                    break;
                case "gsys_shape":
                    SetShapeBlock(bfresMesh, meshBone.Transform, block);
                    break;
                case "gsys_skeleton":
                    SetBoneMatrixBlock(this.ParentModel.Skeleton, bfresMesh.SkinCount > 1, block,
                        Math.Max(this.ParentModel.Skeleton.Bones.Count, 1));
                    break;
                case "gsys_material":
                    SetMaterialBlock(bfresMaterial, block);
                    WriteTeamColorMaterialUniforms(bfresMaterial, block, blockSize);
                    OverrideMaterialUniforms(block, blockSize);
                    break;
                case "gsys_environment":
                    SetBlockData(block, GetEnvironmentData(), blockSize);
                    break;
                case "gsys_user0":
                    SetUser0Block(block, blockSize);
                    break;
                case "gsys_user3":
                    SetTeamColorBlock(block, blockSize);
                    break;
                case "gsys_user2":
                    SetBlockData(block, LightClusterOverride ?? User2Data, blockSize);
                    break;
                case "gsys_shader_option":
                    SetOptionsBlock(bfresMaterial, block, index, blockSize);
                    break;
                default:
                    SetBlockData(block, null, blockSize);
                    break;
            }

            DumpUniformBlock(bfresMaterial.Name, name, block);
        }

        /// <summary>Writes the bytes a block was just filled with, in a local debug build.</summary>
        static partial void DumpUniformBlock(string material, string name, UniformBlock block);

        //The environment block with the light direction override written in, kept until either changes.
        static byte[] _envPatched, _envPatchedFrom;
        static OpenTK.Vector3 _envPatchedDir;

        static byte[] GetEnvironmentData()
        {
            var env = EnvBlock;
            if (LightDirOverride == null || env == null || env.Length < EnvLightDirOffset + 12)
                return env;

            var dir = LightDirOverride.Value.Normalized();
            if (_envPatched != null && _envPatchedFrom == env && _envPatchedDir == dir)
                return _envPatched;
            var patched = (byte[])env.Clone();
            WriteFloat(patched, EnvLightDirOffset, dir.X);
            WriteFloat(patched, EnvLightDirOffset + 4, dir.Y);
            WriteFloat(patched, EnvLightDirOffset + 8, dir.Z);
            (_envPatched, _envPatchedFrom, _envPatchedDir) = (patched, env, dir);
            return patched;
        }

        //Default choice per option name, for options a material does not set.
        static readonly System.Runtime.CompilerServices.ConditionalWeakTable<BfshaLibrary.ShaderModel,
            Dictionary<string, string>> _defaultChoicesCache = new();

        Dictionary<string, string> GetDefaultChoices()
        {
            return _defaultChoicesCache.GetValue(ShaderModel, model =>
            {
                var dict = new Dictionary<string, string>();
                foreach (var so in model.StaticOptions.Values)
                    dict[so.Name] = so.defaultChoice;
                foreach (var dyn in model.DynamiOptions.Values)
                    dict[dyn.Name] = dyn.defaultChoice;
                return dict;
            });
        }

        /// <summary>
        /// gsys_shader_option: integer choices of the shader options used by the program.
        /// </summary>
        void SetOptionsBlock(FMAT mat, UniformBlock block, int blockIndex, int blockSize)
        {
            if (blockSize <= 0) return;

            block.SetData(default, blockSize);
            var buffer = Bytes(block);
            var defaults = GetDefaultChoices();

            foreach (var pair in GetLayout(blockIndex).Offsets)
            {
                string uniformName = pair.Key;
                int offset = pair.Value;
                if (offset < 0 || offset + 4 > buffer.Length)
                    continue;

                if (!mat.ShaderOptions.TryGetValue(uniformName, out string option)
                    || option == GsysShaderOptions.Unset)
                {
                    if (!defaults.TryGetValue(uniformName, out option))
                        continue;
                }

                if (option == "True") option = "1";
                else if (option == "False") option = "0";

                if (int.TryParse(option, out int value))
                    BinaryPrimitives.WriteInt32LittleEndian(buffer.Slice(offset), value);
            }
        }

        /// <summary>
        /// gsys_user0 (fp_c7): ink/shadow/fog parameters. When our self-shadow prepass
        /// is active, override the shader's built-in shadow distance limit (fp_c7[36].z)
        /// so it doesn't fade shadows too early.
        /// </summary>
        static void SetUser0Block(UniformBlock block, int blockSize)
        {
            if (blockSize <= 0) return;

            block.SetData(User0Block, blockSize);

            if (ShadowPrepassTexture != null)
            {
                const int offset36z = 36 * 16 + 8;
                if (offset36z + 4 <= blockSize)
                    WriteFloat(Bytes(block), offset36z, 10000f);
            }
        }

        /// <summary>
        /// Fills a block with dumped data, padded or truncated to the expected block size.
        /// </summary>
        static void SetBlockData(UniformBlock block, byte[] data, int size)
        {
            if (size <= 0) return;
            block.SetData(data, size);
        }

        //The block's bytes, to write into in place.
        static Span<byte> Bytes(UniformBlock block) => CollectionsMarshal.AsSpan(block.Buffer);

        /// <summary>
        /// gsys_context: dumped template with the viewer camera patched in.
        /// Layout (from the decompiled shader): cView (mat3x4), cViewProj (mat4),
        /// cProj (mat4), cViewInv (mat3x4), cNearFar, cScreen, cDist, ...
        /// </summary>
        void SetContextBlock(Camera camera, UniformBlock block, int blockSize)
        {
            block.SetData(ContextTemplate, blockSize);
            var buffer = Bytes(block);

            var viewMatrix = camera.ModelMatrix * camera.ViewMatrix;
            var projMatrix = camera.ProjectionMatrix;
            var viewInverted = viewMatrix.Inverted();
            var viewProjMatrix = viewMatrix * projMatrix;

            float znear = camera.ZNear;
            float zfar = camera.ZFar;
            float zDistance = zfar - znear;

            void Write(Span<byte> buffer, int offset, Vector4 value)
            {
                if (offset + 16 > buffer.Length) return;
                WriteFloat(buffer, offset, value.X);
                WriteFloat(buffer, offset + 4, value.Y);
                WriteFloat(buffer, offset + 8, value.Z);
                WriteFloat(buffer, offset + 12, value.W);
            }
            void WriteMat3x4(Span<byte> buffer, int offset, Matrix4 m)
            {
                Write(buffer, offset, m.Column0); Write(buffer, offset + 16, m.Column1); Write(buffer, offset + 32, m.Column2);
            }
            void WriteMat4(Span<byte> buffer, int offset, Matrix4 m)
            {
                Write(buffer, offset, m.Column0); Write(buffer, offset + 16, m.Column1);
                Write(buffer, offset + 32, m.Column2); Write(buffer, offset + 48, m.Column3);
            }

            WriteMat3x4(buffer, 0, viewMatrix);         // cView
            WriteMat4(buffer, 48, viewProjMatrix);      // cViewProj
            WriteMat4(buffer, 112, projMatrix);         // cProj
            WriteMat3x4(buffer, 176, viewInverted);     // cViewInv
            Write(buffer, 224, new Vector4(znear, zfar, zfar / znear, 1.0f - znear / zfar));                                    // cNearFar
            Write(buffer, 240, new Vector4(1.0f / zDistance, znear / zDistance, camera.AspectRatio, 1.0f / camera.AspectRatio)); // cScreen
            Write(buffer, 256, new Vector4(zDistance, 0, 0, 0));                                                                 // cDist

            //Previous frame matrices (used for motion vectors); keep them equal to the current frame.
            WriteMat3x4(buffer, 336, viewMatrix);       // cPrevView
            WriteMat4(buffer, 384, viewProjMatrix);     // cPrevViewProj
            WriteMat4(buffer, 448, projMatrix);         // cPrevProj
            WriteMat3x4(buffer, 512, viewInverted);     // cPrevViewInv
        }

        //gsys_user3 rows per team, as the game fills them. Bravo's last two rows stay zero.
        static readonly TeamColorVariants.Kind[] User3Rows =
        {
            TeamColorVariants.Kind.Original, TeamColorVariants.Kind.HueBright, TeamColorVariants.Kind.HueDark,
            TeamColorVariants.Kind.Ink, TeamColorVariants.Kind.InkBright, TeamColorVariants.Kind.Bright,
            TeamColorVariants.Kind.HueBright,
        };

        //Rows 21 to 23 hold the neutral colour's variants; the charlie colour stands in for it.
        static readonly TeamColorVariants.Kind[] User3NeutralRows =
        {
            TeamColorVariants.Kind.Original, TeamColorVariants.Kind.Bright, TeamColorVariants.Kind.Dark,
        };

        /// <summary>gsys_user3: the dumped team colour buffer with the current teams' variants written in.</summary>
        void SetTeamColorBlock(UniformBlock block, int blockSize)
        {
            block.SetData(User3Data, blockSize);
            var buffer = Bytes(block);

            for (int team = 0; team < 3; team++)
            {
                for (int row = 0; row < User3Rows.Length; row++)
                {
                    var col = team == 1 && row >= 5
                        ? System.Numerics.Vector3.Zero
                        : GetTeamColorVariant(team, User3Rows[row]);
                    WriteVec3(buffer, (team * 7 + row) * 16, col);
                }
            }
            for (int row = 0; row < User3NeutralRows.Length; row++)
                WriteVec3(buffer, (21 + row) * 16, GetTeamColorVariant(2, User3NeutralRows[row]));
        }

        //The my_team_color variants the game writes into every material's block. The player's own team is alpha.
        static readonly (string Name, TeamColorVariants.Kind Kind)[] TeamColorUniforms =
        {
            ("my_team_color_bright", TeamColorVariants.Kind.Bright),
            ("my_team_color_hue_bright", TeamColorVariants.Kind.HueBright),
            ("my_team_color_hue_bright_half", TeamColorVariants.Kind.HueBrightHalf),
            ("my_team_color_hue_dark", TeamColorVariants.Kind.HueDark),
            ("my_team_color_hue_dark_half", TeamColorVariants.Kind.HueDarkHalf),
        };

        /// <summary>
        /// Writes the team colour uniforms of gsys_material as the game does. my_team_color is the
        /// model colour unless my_team_color_type moves it (7: the material's hue and bright offset
        /// render infos, 8: the ink colour); the complement is the colour half way round the hue
        /// circle, or for type 10 the render info's hue offset away. The dark variant and the per
        /// team hue variants are left at the material's values.
        /// </summary>
        void WriteTeamColorMaterialUniforms(FMAT mat, UniformBlock block, int blockSize)
        {
            var layout = GetLayout(BfshaLibrary.UniformBlock.BlockType.Material);
            if (layout == null)
                return;

            //Pad the material buffer to the full block size so late uniforms fit.
            if (block.Buffer.Count < blockSize)
                block.AddZeros(blockSize - block.Buffer.Count);
            var buffer = Bytes(block);

            var offsets = layout.Offsets;
            foreach (var (name, kind) in TeamColorUniforms)
                WriteVec3(buffer, offsets, name, GetTeamColorVariant(0, kind));
            WriteVec3(buffer, offsets, "my_alpha_team_color", GetTeamColorVariant(0, TeamColorVariants.Kind.Model));
            WriteVec3(buffer, offsets, "my_bravo_team_color", GetTeamColorVariant(1, TeamColorVariants.Kind.Model));
            WriteVec3(buffer, offsets, "my_charlie_team_color", GetTeamColorVariant(2, TeamColorVariants.Kind.Model));

            var original = GetTeamColorVariant(0, TeamColorVariants.Kind.Original);
            var color = GetTeamColorVariant(0, TeamColorVariants.Kind.Model);
            var complement = TeamColorVariants.Shift(original, true, 0.5f, 0f, 0f);
            string type = GetOptionChoice(mat, "my_team_color_type");
            bool huePeak = GetOptionChoice(mat, "disable_hue_peak_offset") != "1";
            if (type == "7")
            {
                float hue = GetRenderInfoSingle(mat, "my_team_color_hue_offset");
                float bright = Math.Clamp(GetRenderInfoSingle(mat, "my_team_color_bright_offset"), -1f, 1f);
                if (hue != 0f || bright != 0f)
                {
                    color = TeamColorVariants.Shift(original, huePeak, hue, 0f, 0f);
                    if (bright != 0f)
                        color = TeamColorVariants.AddValue(color, bright);
                }
            }
            else if (type == "8")
                color = GetTeamColorVariant(0, TeamColorVariants.Kind.Ink);
            else if (type == "10")
                complement = TeamColorVariants.Shift(original, huePeak, GetRenderInfoSingle(mat, "my_team_color_hue_offset"), 0f, 0f);
            WriteVec3(buffer, offsets, "my_team_color", color);
            WriteVec3(buffer, offsets, "my_team_color_hue_complement", complement);
        }

        //The material's choice for an option, or the archive's default when it sets none.
        string GetOptionChoice(FMAT mat, string name)
        {
            if (mat.ShaderOptions.TryGetValue(name, out string choice) && choice != GsysShaderOptions.Unset)
                return choice;
            return GetDefaultChoices().TryGetValue(name, out choice) ? choice : null;
        }

        static float GetRenderInfoSingle(FMAT mat, string name)
        {
            if (mat.Material.RenderInfos.TryGetValue(name, out var info)
                && info.Type == BfresLibrary.RenderInfoType.Single)
            {
                var values = info.GetValueSingles();
                if (values != null && values.Length > 0)
                    return values[0];
            }
            return 0f;
        }

        static void WriteVec3(Span<byte> buffer, Dictionary<string, int> offsets, string name, System.Numerics.Vector3 value)
        {
            if (offsets.TryGetValue(name, out int offset))
                WriteVec3(buffer, offset, value);
        }

        static void WriteVec3(Span<byte> buffer, int offset, System.Numerics.Vector3 value)
        {
            if (offset < 0 || offset + 12 > buffer.Length)
                return;
            WriteFloat(buffer, offset, value.X);
            WriteFloat(buffer, offset + 4, value.Y);
            WriteFloat(buffer, offset + 8, value.Z);
        }

        //two_color_complement_paint_intensity forced by model kind: 0 for hair, 1 for rollers
        //and brushes, left alone otherwise. Read once from the model name.
        float? _complementOverride;
        bool _complementRead;

        void OverrideMaterialUniforms(UniformBlock block, int blockSize)
        {
            var layout = GetLayout(BfshaLibrary.UniformBlock.BlockType.Material);
            if (layout == null) return;

            if (block.Buffer.Count < blockSize)
                block.AddZeros(blockSize - block.Buffer.Count);
            var buffer = Bytes(block);

            if (!_complementRead)
            {
                string model = ParentModel.Name;
                if (model.StartsWith("Har_"))
                    _complementOverride = 0f;
                else if (model.Contains("Roller") || model.Contains("Brush"))
                    _complementOverride = 1f;
                _complementRead = true;
            }

            if (_complementOverride is float complement
                && layout.Offsets.TryGetValue("two_color_complement_paint_intensity", out int c)
                && c + 4 <= buffer.Length)
                WriteFloat(buffer, c, complement);
            if (layout.Offsets.TryGetValue("output_clamp_value", out int o) && o + 4 <= buffer.Length)
                WriteFloat(buffer, o, 100f);
        }

        static void WriteFloat(Span<byte> buffer, int offset, float value) =>
            BinaryPrimitives.WriteSingleLittleEndian(buffer.Slice(offset), value);

        #endregion

        #region Textures

        static GLTexture2D WhiteTexture;
        static GLTexture2D BlackTexture;
        static GLTexture2DArray WhiteArrayTexture;
        static GLTextureCube CubemapTexture;
        static GLTextureCubeArray PrefilterCubeArrayTexture;
        static GLTexture2D BrdfTexture;

        //The game binds its system textures (BRDF LUT, prepasses, probes) with
        //clamp-to-edge linear samplers. Textures loaded via FromBitmap/FromGeneric
        //keep raw GL defaults (repeat + point) unless explicitly configured; clamp-to-edge
        //is important for the BRDF LUT which can be sampled with a negative V coordinate.
        static void SetSystemSamplerParams(GLTexture tex)
        {
            tex.Bind();
            tex.WrapS = TextureWrapMode.ClampToEdge;
            tex.WrapT = TextureWrapMode.ClampToEdge;
            tex.WrapR = TextureWrapMode.ClampToEdge;
            tex.MinFilter = TextureMinFilter.Linear;
            tex.MagFilter = TextureMagFilter.Linear;
            tex.UpdateParameters();
            tex.Unbind();
        }

        static void FlipTextureY(GLTexture2D tex)
        {
            tex.Bind();
            int w = tex.Width, h = tex.Height;
            byte[] pixels = new byte[w * h * 4];
            GL.GetTexImage(TextureTarget.Texture2D, 0, PixelFormat.Rgba, PixelType.UnsignedByte, pixels);
            int stride = w * 4;
            byte[] flipped = new byte[pixels.Length];
            for (int y = 0; y < h; y++)
                System.Buffer.BlockCopy(pixels, y * stride, flipped, (h - 1 - y) * stride, stride);
            GL.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, w, h,
                PixelFormat.Rgba, PixelType.UnsignedByte, flipped);
            tex.Unbind();
        }

        /// <summary>The prefiltered environment cube array the materials read, loaded on first use.</summary>
        public static GLTextureCubeArray EnvCubeArray
        {
            get
            {
                InitTextures();
                return PrefilterCubeArrayTexture;
            }
        }

        public static void InitTextures()
        {
            if (WhiteTexture != null)
                return;

            WhiteTexture = GLTexture2D.FromRgba(new byte[] { 255, 255, 255, 255 }, 1, 1);
            BlackTexture = GLTexture2D.FromRgba(new byte[] { 0, 0, 0, 255 }, 1, 1);
            WhiteArrayTexture = GLTexture2DArray.FromRgba(new byte[] { 255, 255, 255, 255 }, 1, 1);
            CubemapTexture = GLTextureCube.FromDDS(new DDS(new MemoryStream(Resources.CubemapLightmap)));
            BrdfTexture = GLTexture2D.FromGeneric(new DDS(new MemoryStream(Resources.brdf)), new ImageParameters());
            FlipTextureY(BrdfTexture);

            SetSystemSamplerParams(WhiteTexture);
            SetSystemSamplerParams(BlackTexture);
            SetSystemSamplerParams(WhiteArrayTexture);
            SetSystemSamplerParams(CubemapTexture);
            SetSystemSamplerParams(BrdfTexture);

            //Prefer the game's own prefiltered env cubemap array (dumped, R11G11B10F);
            //the Odyssey stand-in is far brighter and adds a fake sheen on gear.
            string gameCubemap = Path.Combine("Resources", "SPL3", "cubemap.dds");
            if (File.Exists(gameCubemap))
            {
                try
                {
                    PrefilterCubeArrayTexture = GLTextureCubeArray.FromDX10ArrayDDS(gameCubemap);
                    Console.WriteLine($"[SPL3] Loaded game prefilter cubemap array ({PrefilterCubeArrayTexture.ArrayCount} cubes)");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[SPL3] Failed to load {gameCubemap}: {ex.Message}");
                }
            }
            if (PrefilterCubeArrayTexture == null)
                PrefilterCubeArrayTexture = GLTextureCubeArray.FromDDS(new DDS($"Resources{Path.DirectorySeparatorChar}CubemapPrefilter.dds"));
        }

        public override void Render(GLContext control, ShaderProgram shader, GenericPickableMesh mesh)
        {
            InitTextures();
            base.Render(control, shader, mesh);
        }

        //Cache of sampler uniform types per GL program, used to pick default
        //textures with the right texture target for unbound shader samplers.
        static readonly Dictionary<int, Dictionary<string, ActiveUniformType>> _samplerTypeCache =
            new Dictionary<int, Dictionary<string, ActiveUniformType>>();

        public static void ClearRuntimeCaches() => _samplerTypeCache.Clear();

        static HoianNXRender()
        {
            ShaderProgram.Deleting += program => _samplerTypeCache.Remove(program);
        }

        static Dictionary<string, ActiveUniformType> GetSamplerTypes(int programID)
        {
            if (_samplerTypeCache.TryGetValue(programID, out var cached))
                return cached;

            var types = new Dictionary<string, ActiveUniformType>();
            GL.GetProgram(programID, GetProgramParameterName.ActiveUniforms, out int count);
            for (int i = 0; i < count; i++)
            {
                string uniformName = GL.GetActiveUniform(programID, i, out _, out ActiveUniformType type);
                //Strip array suffix
                int bracket = uniformName.IndexOf('[');
                if (bracket >= 0) uniformName = uniformName.Substring(0, bracket);
                types[uniformName] = type;
            }
            _samplerTypeCache[programID] = types;
            return types;
        }

        //Screen/shadow style samplers where "no data" means fully lit (white).
        static readonly HashSet<string> WhiteDefaultSamplers = new HashSet<string>
        {
            "gsys_projection0",
            "gsys_projection1",
            "gsys_shadow_prepass",
            "gsys_static_depth_shadow",
            "gsys_depth_shadow",
            "gsys_depth_shadow_cascade",
            "_b0",
            "_b1",
            "_ao0",
            "_op0",
        };

        //The texture target a sampler of this type reads.
        static TextureTarget? TargetForSamplerType(ActiveUniformType type)
        {
            switch (type)
            {
                case ActiveUniformType.Sampler2D:
                case ActiveUniformType.Sampler2DShadow:
                    return TextureTarget.Texture2D;
                case ActiveUniformType.Sampler2DArray:
                case ActiveUniformType.Sampler2DArrayShadow:
                    return TextureTarget.Texture2DArray;
                case ActiveUniformType.SamplerCube:
                case ActiveUniformType.SamplerCubeShadow:
                    return TextureTarget.TextureCubeMap;
                case ActiveUniformType.SamplerCubeMapArray:
                    return TextureTarget.TextureCubeMapArray;
                case ActiveUniformType.Sampler3D:
                    return TextureTarget.Texture3D;
                default:
                    return null;
            }
        }

        static readonly HashSet<string> _reportedUnbound = new HashSet<string>();

        static void ReportUnboundSampler(FMAT mat, string sampler, string reason)
        {
            string key = $"{mat.Name}/{sampler}";
            if (!_reportedUnbound.Add(key))
                return;

            Console.WriteLine($"[SPL3] Material '{mat.Name}' sampler {sampler}: {reason}; using default.");
        }

        GLTexture GetDefaultTexture(string samplerName, ActiveUniformType type)
        {
            switch (type)
            {
                case ActiveUniformType.SamplerCube:
                case ActiveUniformType.SamplerCubeShadow:
                    return CubemapTexture;
                case ActiveUniformType.SamplerCubeMapArray:
                    return PrefilterCubeArrayTexture;
                case ActiveUniformType.Sampler2DArray:
                case ActiveUniformType.Sampler2DArrayShadow:
                    return WhiteArrayTexture;
                default:
                    //Split-sum specular BRDF lookup tables (scale/bias in rg). Binding
                    //black kills the whole reflection term, e.g. on translucent glass.
                    if (samplerName == "gsys_user5" || samplerName == "gsys_user2")
                        return BrdfTexture;
                    if (samplerName == "gsys_shadow_prepass" && ShadowPrepassTexture != null)
                        return ShadowPrepassTexture;
                    if (samplerName == "gsys_color_buffer" && RefractionColorBuffer != null)
                        return RefractionColorBuffer;
                    if (samplerName == "gsys_depth_buffer" && RefractionDepthBuffer != null)
                        return RefractionDepthBuffer;
                    if (WhiteDefaultSamplers.Contains(samplerName))
                        return WhiteTexture;
                    return BlackTexture;
            }
        }

        public override void SetTextureUniforms(GLContext control, ShaderProgram shader, STGenericMaterial mat)
        {
            var bfresMaterial = (FMAT)mat;
            var samplerTypes = GetSamplerTypes(shader.program);

            int id = 1;
            for (int i = 0; i < ShaderModel.Samplers.Count; i++)
            {
                var locationInfo = ProgramPasses[ShaderIndex].SamplerLocations[i];
                if (locationInfo.VertexLocation == -1 && locationInfo.FragmentLocation == -1)
                    continue;

                string sampler = ShaderModel.Samplers.GetKey(i);

                //Find a material texture assigned to this shader sampler.
                int textureIndex = -1;
                if (bfresMaterial.Material.ShaderAssign.SamplerAssigns.ContainsKey(sampler))
                {
                    string resSampler = bfresMaterial.Material.ShaderAssign.SamplerAssigns[sampler].String;
                    textureIndex = bfresMaterial.TextureMaps.FindIndex(x => x.Sampler == resSampler);
                }

                string uniformName = locationInfo.FragmentLocation != -1
                    ? SamplerName(locationInfo.FragmentLocation, false)
                    : SamplerName(locationInfo.VertexLocation, true);
                samplerTypes.TryGetValue(uniformName, out var type);

                GLTexture bound = null;
                if (textureIndex != -1)
                {
                    var texMap = bfresMaterial.TextureMaps[textureIndex];
                    var name = texMap.Name;
                    if (bfresMaterial.AnimatedSamplers.ContainsKey(texMap.Sampler))
                        name = bfresMaterial.AnimatedSamplers[texMap.Sampler];

                    bound = BindTexture(shader, GetTextures(), texMap, name, id);

                    if (bound != null && TargetForSamplerType(type) is TextureTarget want
                        && bound.Target != want)
                    {
                        ReportUnboundSampler(bfresMaterial, sampler, $"{name} is {bound.Target}, sampler wants {want}");
                        bound = null;
                    }
                    else if (bound == null)
                        ReportUnboundSampler(bfresMaterial, sampler, $"texture '{name}' not found");
                }

                //Either an engine provided texture (shadow maps, light maps, paint, etc)
                //or a material texture that did not resolve. so it dont
                //load whatever the previous draw had there
                if (bound == null)
                {
                    GL.ActiveTexture(TextureUnit.Texture0 + id);
                    GetDefaultTexture(sampler, type).Bind();
                }

                if (locationInfo.VertexLocation != -1)
                    shader.SetIntCached(SamplerName(locationInfo.VertexLocation, true), id);
                if (locationInfo.FragmentLocation != -1)
                    shader.SetIntCached(SamplerName(locationInfo.FragmentLocation, false), id);
                id++;
            }

            GL.ActiveTexture(TextureUnit.Texture0);
            GL.BindTexture(TextureTarget.Texture2D, 0);
        }

        #endregion
    }
}
