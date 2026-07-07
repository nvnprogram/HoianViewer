using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using OpenTK.Graphics.OpenGL;
using OpenTK;
using Toolbox.Core;
using BfresEditor.Properties;
using GLFrameworkEngine;

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
    public class HoianNXRender : BfshaRenderer
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
        /// The world-space direction the main light travels (override or dumped env data).
        /// </summary>
        public static OpenTK.Vector3 GetMainLightDir()
        {
            if (LightDirOverride != null)
                return LightDirOverride.Value.Normalized();
            LoadResourceData();
            if (EnvironmentData != null && EnvironmentData.Length >= EnvLightDirOffset + 12)
            {
                var dir = new OpenTK.Vector3(
                    BitConverter.ToSingle(EnvironmentData, EnvLightDirOffset),
                    BitConverter.ToSingle(EnvironmentData, EnvLightDirOffset + 4),
                    BitConverter.ToSingle(EnvironmentData, EnvLightDirOffset + 8));
                if (dir.LengthSquared > 0.0001f)
                    return dir.Normalized();
            }
            return new OpenTK.Vector3(0, -1, 0);
        }

        // Per-slot brightness ratio of each team color variant relative to its base color,
        // derived from the dumped fp_c8. Layout: 3 teams x 7 variants
        // (base, bright, dark, hue_bright, hue_dark, hue_complement, hue_bright_half).
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

        /// <summary>
        /// Gets a team color variant (team 0-2, variant 0-6) scaled the same way
        /// the dumped in-game buffer scales it relative to the base color.
        /// </summary>
        static System.Numerics.Vector3 GetTeamColorVariant(int team, int variant)
        {
            var col = team == 0 ? TeamAlphaColor : team == 1 ? TeamBravoColor : TeamCharlieColor;
            float ratio = _teamColorRatios != null ? _teamColorRatios[team * 7 + variant] : 1f;
            return col * ratio;
        }

        #endregion

        #region Shader archive lookup

        //Archive resolution result per (shader file, shader model). The full search
        //below hits the filesystem (Directory.GetFiles) and rebuilds candidate lists
        //for every mesh of every model, which adds up to hundreds of ms per model.
        static readonly Dictionary<string, BfshaLibrary.BfshaFile> _archiveLookupCache = new();

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

        public override BfshaLibrary.BfshaFile TryLoadShaderArchive(BFRES bfres, string shaderFile, string shaderModel)
        {
            //If a background prewarm is in flight, wait for it: GlobalShaderCache is
            //not safe to mutate concurrently, and the archive is needed here anyway.
            _prewarmTask?.Wait();
            //Player/gear models all resolve to the game-level shader archives, so the
            //result is independent of the bfres once the global cache is primed.
            //Models that embed their own shader archive (or ship inside a pack) must
            //bypass the memo since their archive is file-specific.
            bfres.UpdateExternalShaderFiles();
            bool fileSpecific = bfres.ShaderFiles.Count > 0 || bfres.FileInfo.ParentArchive != null;

            string memoKey = $"{shaderFile}|{shaderModel}";
            if (!fileSpecific && _archiveLookupCache.TryGetValue(memoKey, out var memo))
                return memo;

            var result = TryLoadShaderArchiveUncached(bfres, shaderFile, shaderModel);
            if (result != null && !fileSpecific)
                _archiveLookupCache[memoKey] = result;
            return result;
        }

        BfshaLibrary.BfshaFile TryLoadShaderArchiveUncached(BFRES bfres, string shaderFile, string shaderModel)
        {
            //Search existing sources first (external files, global shader cache, parent archive)
            var candidates = new List<BfshaLibrary.BfshaFile>();

            bfres.UpdateExternalShaderFiles();
            foreach (var file in bfres.ShaderFiles)
            {
                if (file is BfshaLibrary.BfshaFile bfsha && bfsha.Name.Contains(shaderFile))
                    candidates.Add(bfsha);
            }

            foreach (var file in GlobalShaderCache.ShaderFiles.Values)
            {
                if (file is BfshaLibrary.BfshaFile bfsha && bfsha.Name.Contains(shaderFile))
                    candidates.Add(bfsha);
            }

            var archiveFile = bfres.FileInfo.ParentArchive;
            if (archiveFile != null)
            {
                foreach (var file in archiveFile.Files)
                {
                    if (file.FileName.Contains(shaderFile))
                    {
                        if (file.FileFormat == null)
                            file.FileFormat = file.OpenFile();
                        if (file.FileFormat is BFSHA bfshaFormat)
                            candidates.Add(bfshaFormat.BfshaFile);
                    }
                }
            }

            //Search the game files: a Shader folder next to (or above) the model, or the configured game path.
            foreach (var shaderDir in GetShaderSearchDirs(bfres))
            {
                if (!Directory.Exists(shaderDir))
                    continue;

                //Prefer the main Product archive over the cutscene (Eve) archive.
                var files = Directory.GetFiles(shaderDir)
                    .Where(x => Path.GetFileName(x).StartsWith(shaderFile) &&
                               (x.EndsWith(".bfsha") || x.EndsWith(".bfsha.zs")))
                    .OrderBy(x => Path.GetFileName(x).Contains(".Product.") ? 0 : 1)
                    .ToList();

                foreach (var file in files)
                {
                    var bfsha = LoadArchiveFile(file);
                    if (bfsha != null)
                        candidates.Add(bfsha);
                }

                if (candidates.Count > 0)
                    break;
            }

            if (candidates.Count == 0)
                return null;

            //Prefer an archive that actually contains the requested shader model.
            foreach (var bfsha in candidates)
            {
                if (bfsha.ShaderModels.FirstOrDefault(x => x.Name == shaderModel) != null)
                    return bfsha;
            }
            return candidates[0];
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
        }

        public override void ReloadProgram(BfresMeshAsset mesh)
        {
            var mat = mesh.Shape.Material;

            ProgramPasses.Clear();

            //Collect the material's shader options.
            Dictionary<string, string> options = new Dictionary<string, string>();
            foreach (var op in mat.ShaderOptions)
            {
                string choice = op.Value;
                if (choice == "<Default Value>")
                    continue;
                if (choice == "True") choice = "1";
                else if (choice == "False") choice = "0";
                options[op.Key] = choice;
            }

            this.LoadRenderStateOptions(options, mat);

            // The game seems to derive this from the assigned samplers; materials often leave it
            // at <Default Value>. Without the constraint the lenient search can pick a
            // variant with the albedo fetch compiled out, rendering the mesh white.
            if (!options.ContainsKey("enable_albedo_tex") && HasAssignedTexture(mat, "_a0"))
                options["enable_albedo_tex"] = "1";

            // The game seems to derive gsys_display_face_type from the display face render info.
            // Without it the exact key lookup misses and the lenient search can land on
            // a variant with extra features baked in (e.g. reflector sheen on fabric).
            if (!options.ContainsKey("gsys_display_face_type"))
            {
                string face = mat.GetRenderInfo("gsys_render_state_display_face");
                string faceType = face switch
                {
                    "front" => "1",
                    "back" => "2",
                    "none" => "3",
                    _ => null,   //"both" is the default (0)
                };
                if (faceType != null)
                    options["gsys_display_face_type"] = faceType;
            }

            //Dynamic options set by the engine at runtime.
            options["gsys_weight"] = mesh.Shape.VertexSkinCount.ToString();

            //Remove any option names or choices the shader archive does not know;
            //materials can carry options from a different game version.
            SanitizeOptions(options);

            // Each gsys_assign_type has its own set of program passes.
            // The material pass is first so it is used as the default (index 0).
            // Materials repeat the same option sets across models (skin, gear cloth,
            // accessories...), so the resolved program indices are memoized; the
            // lenient fallback scan is O(program count) and Hoian's UBER shader has
            // tens of thousands of programs.
            var assignTypeOption = ShaderModel.DynamiOptions["gsys_assign_type"];
            var passCache = _programPassCache.GetValue(ShaderModel, _ => new Dictionary<string, int[]>());
            string cacheKey = string.Join(";", options.OrderBy(x => x.Key, StringComparer.Ordinal)
                .Select(x => $"{x.Key}={x.Value}"));

            if (!passCache.TryGetValue(cacheKey, out int[] passIndices))
            {
                var indices = new List<int>();
                if (assignTypeOption != null)
                {
                    var choices = assignTypeOption.ChoiceDict.GetKeys()
                        .Where(x => !string.IsNullOrEmpty(x))
                        .OrderBy(x => x == "gsys_assign_material" ? 0 : 1)
                        .ToList();

                    foreach (var choice in choices)
                    {
                        options["gsys_assign_type"] = choice;
                        int programIndex = ShaderModel.GetProgramIndex(options);
                        if (programIndex != -1)
                            indices.Add(programIndex);
                    }
                }
                else
                {
                    int programIndex = ShaderModel.GetProgramIndex(options);
                    if (programIndex >= 0)
                        indices.Add(programIndex);
                }
                passIndices = indices.ToArray();
                passCache[cacheKey] = passIndices;
            }

            foreach (int programIndex in passIndices)
                this.ProgramPasses.Add(ShaderModel.GetShaderProgram(programIndex));

            if (ProgramPasses.Count == 0)
                Console.WriteLine($"[SPL3] No shader program matched material '{mat.Name}'.");
        }

        static bool HasAssignedTexture(FMAT mat, string sampler)
        {
            if (!mat.Material.ShaderAssign.SamplerAssigns.ContainsKey(sampler))
                return false;

            string resSampler = mat.Material.ShaderAssign.SamplerAssigns[sampler].String;
            return mat.TextureMaps.Any(x => x.Sampler == resSampler);
        }

        //Valid option/choice sets per shader model
        static readonly System.Runtime.CompilerServices.ConditionalWeakTable<BfshaLibrary.ShaderModel,
            Dictionary<string, HashSet<string>>> _validChoicesCache = new();

        //Option set -> resolved program indices, per shader model.
        static readonly System.Runtime.CompilerServices.ConditionalWeakTable<BfshaLibrary.ShaderModel,
            Dictionary<string, int[]>> _programPassCache = new();

        void SanitizeOptions(Dictionary<string, string> options)
        {
            var validChoices = _validChoicesCache.GetValue(ShaderModel, model =>
            {
                var dict = new Dictionary<string, HashSet<string>>();
                foreach (var so in model.StaticOptions.Values)
                    dict[so.Name] = new HashSet<string>(so.ChoiceDict.GetKeys());
                foreach (var dyn in model.DynamiOptions.Values)
                    dict[dyn.Name] = new HashSet<string>(dyn.ChoiceDict.GetKeys());
                return dict;
            });

            foreach (var key in options.Keys.ToList())
            {
                if (!validChoices.TryGetValue(key, out var choices) || !choices.Contains(options[key]))
                    options.Remove(key);
            }
        }

        #endregion

        #region Uniform blocks

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
                    SetBoneMatrixBlock(this.ParentModel.Skeleton, bfresMesh.SkinCount > 1, block, 150);
                    break;
                case "gsys_material":
                    SetMaterialBlock(bfresMaterial, block);
                    WriteTeamColorMaterialUniforms(block, blockSize);
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
                    SetBlockData(block, User2Data, blockSize);
                    break;
                case "gsys_shader_option":
                    SetOptionsBlock(bfresMaterial, block, index, blockSize);
                    break;
                default:
                    SetBlockData(block, null, blockSize);
                    break;
            }

        }

        static byte[] GetEnvironmentData()
        {
            if (LightDirOverride == null || EnvironmentData == null ||
                EnvironmentData.Length < EnvLightDirOffset + 12)
                return EnvironmentData;

            var patched = (byte[])EnvironmentData.Clone();
            var dir = LightDirOverride.Value.Normalized();
            System.Buffer.BlockCopy(BitConverter.GetBytes(dir.X), 0, patched, EnvLightDirOffset, 4);
            System.Buffer.BlockCopy(BitConverter.GetBytes(dir.Y), 0, patched, EnvLightDirOffset + 4, 4);
            System.Buffer.BlockCopy(BitConverter.GetBytes(dir.Z), 0, patched, EnvLightDirOffset + 8, 4);
            return patched;
        }

        /// <summary>
        /// gsys_shader_option: integer choices of the shader options used by the program.
        /// </summary>
        void SetOptionsBlock(FMAT mat, UniformBlock block, int blockIndex, int blockSize)
        {
            if (blockSize <= 0) return;

            byte[] buffer = new byte[blockSize];
            var uniformBlock = ShaderModel.UniformBlocks[blockIndex];

            int index = 0;
            foreach (var param in uniformBlock.Uniforms.Values)
            {
                string uniformName = uniformBlock.Uniforms.GetKey(index++);
                int offset = param.Offset - 1;
                if (offset < 0 || offset + 4 > buffer.Length)
                    continue;

                if (!mat.ShaderOptions.TryGetValue(uniformName, out string option))
                    continue;

                if (option == "True") option = "1";
                else if (option == "False") option = "0";

                if (int.TryParse(option, out int value))
                    System.Buffer.BlockCopy(BitConverter.GetBytes(value), 0, buffer, offset, 4);
            }

            block.Buffer.Clear();
            block.Add(buffer);
        }

        /// <summary>
        /// gsys_user0 (fp_c7): ink/shadow/fog parameters. When our self-shadow prepass
        /// is active, override the shader's built-in shadow distance limit (fp_c7[36].z)
        /// so it doesn't fade shadows too early.
        /// </summary>
        static void SetUser0Block(UniformBlock block, int blockSize)
        {
            if (blockSize <= 0) return;

            byte[] buffer = new byte[blockSize];
            if (User0Data != null)
                System.Buffer.BlockCopy(User0Data, 0, buffer, 0, Math.Min(User0Data.Length, blockSize));

            if (ShadowPrepassTexture != null)
            {
                const int offset36z = 36 * 16 + 8;
                if (offset36z + 4 <= blockSize)
                    System.Buffer.BlockCopy(BitConverter.GetBytes(10000f), 0, buffer, offset36z, 4);
            }

            block.Buffer.Clear();
            block.Add(buffer);
        }

        /// <summary>
        /// Fills a block with dumped data, padded or truncated to the expected block size.
        /// </summary>
        static void SetBlockData(UniformBlock block, byte[] data, int size)
        {
            if (size <= 0) return;

            byte[] buffer = new byte[size];
            if (data != null)
                System.Buffer.BlockCopy(data, 0, buffer, 0, Math.Min(data.Length, size));

            block.Buffer.Clear();
            block.Add(buffer);
        }

        /// <summary>
        /// gsys_context: dumped template with the viewer camera patched in.
        /// Layout (from the decompiled shader): cView (mat3x4), cViewProj (mat4),
        /// cProj (mat4), cViewInv (mat3x4), cNearFar, cScreen, cDist, ...
        /// </summary>
        void SetContextBlock(Camera camera, UniformBlock block, int blockSize)
        {
            byte[] buffer = new byte[blockSize];
            if (ContextTemplate != null)
                System.Buffer.BlockCopy(ContextTemplate, 0, buffer, 0, Math.Min(ContextTemplate.Length, blockSize));

            var viewMatrix = camera.ModelMatrix * camera.ViewMatrix;
            var projMatrix = camera.ProjectionMatrix;
            var viewInverted = viewMatrix.Inverted();
            var viewProjMatrix = viewMatrix * projMatrix;

            float znear = camera.ZNear;
            float zfar = camera.ZFar;
            float zDistance = zfar - znear;

            void Write(int offset, Vector4 value)
            {
                if (offset + 16 > buffer.Length) return;
                System.Buffer.BlockCopy(BitConverter.GetBytes(value.X), 0, buffer, offset, 4);
                System.Buffer.BlockCopy(BitConverter.GetBytes(value.Y), 0, buffer, offset + 4, 4);
                System.Buffer.BlockCopy(BitConverter.GetBytes(value.Z), 0, buffer, offset + 8, 4);
                System.Buffer.BlockCopy(BitConverter.GetBytes(value.W), 0, buffer, offset + 12, 4);
            }
            void WriteMat3x4(int offset, Matrix4 m)
            {
                Write(offset, m.Column0); Write(offset + 16, m.Column1); Write(offset + 32, m.Column2);
            }
            void WriteMat4(int offset, Matrix4 m)
            {
                Write(offset, m.Column0); Write(offset + 16, m.Column1);
                Write(offset + 32, m.Column2); Write(offset + 48, m.Column3);
            }

            WriteMat3x4(0, viewMatrix);         // cView
            WriteMat4(48, viewProjMatrix);      // cViewProj
            WriteMat4(112, projMatrix);         // cProj
            WriteMat3x4(176, viewInverted);     // cViewInv
            Write(224, new Vector4(znear, zfar, zfar / znear, 1.0f - znear / zfar));                                    // cNearFar
            Write(240, new Vector4(1.0f / zDistance, znear / zDistance, camera.AspectRatio, 1.0f / camera.AspectRatio)); // cScreen
            Write(256, new Vector4(zDistance, 0, 0, 0));                                                                 // cDist

            //Previous frame matrices (used for motion vectors); keep them equal to the current frame.
            WriteMat3x4(336, viewMatrix);       // cPrevView
            WriteMat4(384, viewProjMatrix);     // cPrevViewProj
            WriteMat4(448, projMatrix);         // cPrevProj
            WriteMat3x4(512, viewInverted);     // cPrevViewInv

            block.Buffer.Clear();
            block.Add(buffer);
        }

        /// <summary>
        /// gsys_user3: the dumped team color buffer with the current team colors written in.
        /// Each team has 7 vec4 slots (base, bright, dark, hue_bright, hue_dark,
        /// hue_complement, hue_bright_half); the derived slots keep the dumped
        /// brightness ratios relative to the base color.
        /// </summary>
        void SetTeamColorBlock(UniformBlock block, int blockSize)
        {
            byte[] buffer = new byte[blockSize];
            if (User3Data != null)
                System.Buffer.BlockCopy(User3Data, 0, buffer, 0, Math.Min(User3Data.Length, blockSize));

            for (int team = 0; team < 3; team++)
            {
                for (int variant = 0; variant < 7; variant++)
                {
                    int offset = (team * 7 + variant) * 16;
                    if (offset + 12 > buffer.Length) break;

                    var col = GetTeamColorVariant(team, variant);
                    System.Buffer.BlockCopy(BitConverter.GetBytes(col.X), 0, buffer, offset, 4);
                    System.Buffer.BlockCopy(BitConverter.GetBytes(col.Y), 0, buffer, offset + 4, 4);
                    System.Buffer.BlockCopy(BitConverter.GetBytes(col.Z), 0, buffer, offset + 8, 4);
                }
            }

            block.Buffer.Clear();
            block.Add(buffer);
        }

        //fp_c8 variant slot indices (from the decompiled shader).
        const int VariantBright = 1;
        const int VariantDark = 2;
        const int VariantHueBright = 3;
        const int VariantHueDark = 4;
        const int VariantHueComplement = 5;
        const int VariantHueBrightHalf = 6;

        /// <summary>
        /// Overrides the my_team_color* uniforms inside gsys_material (fp_c6) with the
        /// current team colors so characters/gear display team colors like in game.
        /// The player's own team is alpha.
        /// </summary>
        void WriteTeamColorMaterialUniforms(UniformBlock block, int blockSize)
        {
            var matBlock = ShaderModel.UniformBlocks.Values.FirstOrDefault(x =>
                x.Type == BfshaLibrary.UniformBlock.BlockType.Material);
            if (matBlock == null)
                return;

            //Pad the material buffer to the full block size so late uniforms fit.
            while (block.Buffer.Count < blockSize)
                block.Buffer.Add(0);

            var values = new Dictionary<string, System.Numerics.Vector3>
            {
                { "my_team_color",                    GetTeamColorVariant(0, 0) },
                { "my_team_color_bright",             GetTeamColorVariant(0, VariantBright) },
                { "my_team_color_dark",               GetTeamColorVariant(0, VariantDark) },
                { "my_team_color_hue_bright",         GetTeamColorVariant(0, VariantHueBright) },
                { "my_team_color_hue_bright_half",    GetTeamColorVariant(0, VariantHueBrightHalf) },
                { "my_team_color_hue_dark",           GetTeamColorVariant(0, VariantHueDark) },
                { "my_team_color_hue_dark_half",      GetTeamColorVariant(0, VariantHueDark) },
                { "my_team_color_hue_complement",     GetTeamColorVariant(0, VariantHueComplement) },
                { "my_alpha_team_color",              GetTeamColorVariant(0, 0) },
                { "my_bravo_team_color",              GetTeamColorVariant(1, 0) },
                { "my_charlie_team_color",            GetTeamColorVariant(2, 0) },
                { "my_alpha_team_color_hue_bright",   GetTeamColorVariant(0, VariantHueBright) },
                { "my_bravo_team_color_hue_bright",   GetTeamColorVariant(1, VariantHueBright) },
                { "my_charlie_team_color_hue_bright", GetTeamColorVariant(2, VariantHueBright) },
                { "my_alpha_team_color_hue_dark",     GetTeamColorVariant(0, VariantHueDark) },
                { "my_bravo_team_color_hue_dark",     GetTeamColorVariant(1, VariantHueDark) },
                { "my_charlie_team_color_hue_dark",   GetTeamColorVariant(2, VariantHueDark) },
            };

            int index = 0;
            foreach (var param in matBlock.Uniforms.Values)
            {
                string uniformName = matBlock.Uniforms.GetKey(index++);
                if (!values.TryGetValue(uniformName, out var col))
                    continue;

                int offset = param.Offset - 1;
                if (offset + 12 > block.Buffer.Count)
                    continue;

                WriteFloat(block.Buffer, offset, col.X);
                WriteFloat(block.Buffer, offset + 4, col.Y);
                WriteFloat(block.Buffer, offset + 8, col.Z);
            }
        }

        void OverrideMaterialUniforms(UniformBlock block, int blockSize)
        {
            if (!ParentModel.Name.StartsWith("Har_")) return;

            var matBlock = ShaderModel.UniformBlocks.Values.FirstOrDefault(x =>
                x.Type == BfshaLibrary.UniformBlock.BlockType.Material);
            if (matBlock == null) return;

            int index = 0;
            foreach (var param in matBlock.Uniforms.Values)
            {
                string name = matBlock.Uniforms.GetKey(index++);
                if (name != "two_color_complement_paint_intensity") continue;

                int offset = param.Offset - 1;
                if (offset + 4 <= block.Buffer.Count)
                    WriteFloat(block.Buffer, offset, 0f);
                break;
            }
        }

        static void WriteFloat(List<byte> buffer, int offset, float value)
        {
            var bytes = BitConverter.GetBytes(value);
            for (int i = 0; i < 4; i++)
                buffer[offset + i] = bytes[i];
        }

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

        public static void InitTextures()
        {
            if (WhiteTexture != null)
                return;

            WhiteTexture = GLTexture2D.FromBitmap(Resources.white);
            BlackTexture = GLTexture2D.FromBitmap(Resources.black);
            WhiteArrayTexture = GLTexture2DArray.FromBitmap(Resources.white);
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
        };

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
                    if (WhiteDefaultSamplers.Contains(samplerName))
                        return WhiteTexture;
                    return BlackTexture;
            }
        }

        public override void SetTextureUniforms(GLContext control, ShaderProgram shader, STGenericMaterial mat)
        {
            var bfresMaterial = (FMAT)mat;
            var samplerTypes = GetSamplerTypes(shader.program);

            GL.ActiveTexture(TextureUnit.Texture0 + 1);
            if (RenderTools.defaultTex != null)
                GL.BindTexture(TextureTarget.Texture2D, RenderTools.defaultTex.ID);

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

                if (textureIndex != -1)
                {
                    var texMap = bfresMaterial.TextureMaps[textureIndex];
                    var name = texMap.Name;
                    if (bfresMaterial.AnimatedSamplers.ContainsKey(texMap.Sampler))
                        name = bfresMaterial.AnimatedSamplers[texMap.Sampler];

                    BindTexture(shader, GetTextures(), texMap, name, id);
                }
                else
                {
                    //Engine provided textures (shadow maps, light maps, paint, etc).
                    //Pick a default with the texture target the program expects.
                    string uniformName = locationInfo.FragmentLocation != -1
                        ? ConvertSamplerID(locationInfo.FragmentLocation)
                        : ConvertSamplerID(locationInfo.VertexLocation, true);

                    samplerTypes.TryGetValue(uniformName, out var type);

                    GL.ActiveTexture(TextureUnit.Texture0 + id);
                    GetDefaultTexture(sampler, type).Bind();
                }

                if (locationInfo.VertexLocation != -1)
                    shader.SetInt(ConvertSamplerID(locationInfo.VertexLocation, true), id);
                if (locationInfo.FragmentLocation != -1)
                    shader.SetInt(ConvertSamplerID(locationInfo.FragmentLocation), id);
                id++;
            }

            GL.ActiveTexture(TextureUnit.Texture0);
            GL.BindTexture(TextureTarget.Texture2D, 0);
        }

        #endregion
    }
}
