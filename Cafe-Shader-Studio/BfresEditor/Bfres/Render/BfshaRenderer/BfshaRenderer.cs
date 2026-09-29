using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Toolbox.Core;
using OpenTK.Graphics.OpenGL;
using OpenTK;
using System.IO;
using GLFrameworkEngine;

namespace BfresEditor
{
    //Note to developers
    //To create a custom renderer, it needs a few things.

    //First an override to ReloadProgram(). This determines what shader program to use in the shader.
    //These are determined by the static and dynamic option choices in a shader model (ShaderModel).
    //Generally you can handle most of these options automatically from loading the shader options from the bfres material.
    //Dynamic options you can directly add them all from the shader model and adjust what is necessary
    //One very important option value you may need to adjust is skin count which varies between games on what type of option it is and what is called.

    //The next step is to optionally do a ReloadRenderState() function. Here you set render state information onto the mesh that may be used for the shader.
    //Currently used for things like setting draw priority values or polygon offset handling (all of this is a per game thing)

    //Another optional step is the LoadMesh() function. Here you can add addtional hardcoded attributes from mesh.Attributes
    //Check KSA/KSARender for a look at how it is used.

    //Next is the Render loop. If you plan to override this, you must include a base.Render() to draw the main bfsha render loop
    //Currently used to check for initialized textures hardcoded in engine, but that can be done elsewhere where the textures are loaded.

    //Lastly there is the uniform block loading from LoadUniformBlock(). 
    //Here you switch between the block names and fill the block with actual data.
    //Keep in mind the data is cleared each loop to be filled back up.
    //However there are plans to keep it cached and update only when necessary.

    /// <summary>
    /// A bfsha render class to help render shader binaries from a shader archive.
    /// This includes methods to help load some block data automatically.
    /// </summary>
    [Serializable]
    public class BfshaRenderer : ShaderRenderBase
    {
        private ShaderProgram shaderProgram;

        /// <summary>
        /// A list of programs used for multiple passes.
        /// This value is generally for dynamic options with mutliple pass programs
        /// </summary>
        public List<BfshaLibrary.ResShaderProgram> ProgramPasses = new List<BfshaLibrary.ResShaderProgram>();

        /// <summary>
        /// Determines to enable SRGB or not when drawn to the final framebuffer.
        /// </summary>
        public override bool UseSRGB { get; } = true;

        /// <summary>
        /// Determines if the current material has a valid program.
        /// If the model fails to find a program in ReloadProgram, it will fail to load the shader.
        /// </summary>
        public override bool HasValidProgram => ProgramPasses.Count > 0;

        /// <summary>
        /// Determines to reload the glsl shader file or not.
        /// </summary>
        private bool UpdateShader = false;

        /// <summary>
        /// The opengl shader used to render.
        /// </summary>
        public override ShaderProgram Shader => shaderProgram;

        /// <summary>
        /// Determines when to use this renderer for the given material.
        /// This is typically done from the shader archive or shader model name.
        /// The material can also be used for shader specific render information to check.
        /// </summary>
        /// <returns></returns>
        public override bool UseRenderer(FMAT material, string archive, string model)
        {
            return false;
        }

        /// <summary>
        /// A list of blocks which are cached to not update after the next frame.
        /// </summary>
        public List<string> BlocksToCache = new List<string>();

        /// <summary>
        /// The active shader model used for shader information.
        /// </summary>
        public BfshaLibrary.ShaderModel ShaderModel { get; set; }

        /// <summary>
        /// The shader model the archive probe chose, which stays the authority for the
        /// program key even while <see cref="RebindArchive"/> has a generated archive bound.
        /// </summary>
        public BfshaLibrary.ShaderModel BaseShaderModel { get; private set; }

        public BfshaLibrary.BfshaFile BaseShaderArchiveFile { get; private set; }

        /// <summary>
        /// The archive the shader model was taken from. 
        /// </summary>
        public BfshaLibrary.BfshaFile ShaderArchiveFile { get; set; }

        public bool IsSwitch => ShaderModel.BnshFileStream != null;

        public BfshaRenderer() { }

        public BfshaRenderer(BfshaLibrary.ShaderModel shaderModel)
        {
            ShaderModel = shaderModel;
        }

        //Profiling for the initial shader load path.
        public static readonly System.Diagnostics.Stopwatch ArchiveTime = new System.Diagnostics.Stopwatch();
        public static readonly System.Diagnostics.Stopwatch OnLoadTime = new System.Diagnostics.Stopwatch();

        /// <summary>
        /// Loads the material renderer for the first time.
        /// </summary>
        /// <returns></returns>
        public override void TryLoadShader(BFRES bfres, FMDL fmdl, FSHP mesh, BfresMeshAsset meshAsset)
        {
            ArchiveTime.Start();
            var bfsha = TryLoadShaderArchive(bfres,
                mesh.Material.ShaderArchive,
                mesh.Material.ShaderModel);
            ArchiveTime.Stop();

            if (bfsha == null)
                return;

            var shaderModel = bfsha.ShaderModels.Values.FirstOrDefault(x => x.Name == mesh.Material.ShaderModel);
            if (shaderModel != null)
            {
                ShaderArchiveFile = bfsha;
                OnLoadTime.Start();
                OnLoad(shaderModel, fmdl, mesh, meshAsset);
                OnLoadTime.Stop();
            }
        }

        /// <summary>
        /// Called once when the renderer can be loaded from a given shader model and mesh.
        /// </summary>
        public void OnLoad(BfshaLibrary.ShaderModel shaderModel, FMDL model, FSHP mesh, BfresMeshAsset meshAsset)
        {
            var shapeBlock = shaderModel.UniformBlocks.Values.FirstOrDefault(x =>
            x.Type == BfshaLibrary.UniformBlock.BlockType.Shape);

            //Models may update the shape block outside the shader if the shape block is unused so update mesh matrix manually
            if (shapeBlock.Size == 0 && mesh.VertexSkinCount == 0)
            {
                mesh.UpdateVertexBuffer(true);
                meshAsset.UpdateVertexBuffer();
            }

            //Assign some necessary data
            meshAsset.MaterialAsset = this;

            //Force reload from material editing
            mesh.ShaderReload += delegate
            {
                Console.WriteLine($"Reloading shader program {meshAsset.Name}");
                this.ReloadRenderState(meshAsset);
                this.ReloadProgram(meshAsset);
                mesh.HasValidShader = this.HasValidProgram;

                Console.WriteLine($"Program Validation: {this.HasValidProgram}");
                this.UpdateShader = true;
            };

            ShaderModel = shaderModel;
            BaseShaderModel = shaderModel;
            BaseShaderArchiveFile = ShaderArchiveFile;
            MaterialData = mesh.Material;
            ParentModel = model;
            //Load mesh function for loading the custom shader for the first time
            LoadMesh(meshAsset);
            ReloadRenderState(meshAsset);
            ReloadProgram(meshAsset);

            var bfresMaterial = (FMAT)this.MaterialData;

            //Remap the vertex layouts from shader model attributes
            if (!IsSwitch)
            {
                //GX2 shaders can be directly mapped via string and location searches
                Dictionary<string, string> attributeLocations = new Dictionary<string, string>();
                for (int i = 0; i < shaderModel.Attributes.Count; i++)
                {
                    string key = shaderModel.Attributes.GetKey(i);
                    attributeLocations.Add(key, $"{key}_0_0");
                }
                meshAsset.UpdateVaoAttributes(attributeLocations);
            }
            else
            {
                Dictionary<string, int> attributeLocations = new Dictionary<string, int>();
                for (int i = 0; i < shaderModel.Attributes.Count; i++)
                {
                    string key = shaderModel.Attributes.GetKey(i);
                    int location = shaderModel.Attributes[i].Location;
                    attributeLocations.Add(key, location);
                }

                if (HoianNXRender.DebugMaterials)
                    Console.WriteLine($"[SPL3dbg] material '{MaterialData?.Name}' attributes shader["
                        + string.Join(",", attributeLocations.Select(x => $"{x.Key}={x.Value}"))
                        + "] mesh[" + string.Join(",", meshAsset.Attributes.Select(x => x.name)) + "]");

                meshAsset.UpdateVaoAttributes(attributeLocations);
            }
        }

        /// <summary>
        /// The shader samplers the resolved programs actually read, which is a subset of what
        /// the archive declares: a specialised program only carries a location for the
        /// samplers its own branches reach. The union over every pass this material is drawn
        /// with, since a sampler only zonly reads still has to be bound.
        ///
        /// This is what SetTextureUniforms iterates, so a sampler outside it is not merely
        /// unused, it is absent from the program's binding table.
        /// </summary>
        public HashSet<string> SamplersRead()
        {
            var read = new HashSet<string>();
            if (ShaderModel == null)
                return read;
            foreach (var pass in ProgramPasses)
            {
                var locations = pass.SamplerLocations;
                for (int i = 0; i < ShaderModel.Samplers.Count && i < locations.Length; i++)
                    if (locations[i].VertexLocation != -1 || locations[i].FragmentLocation != -1)
                        read.Add(ShaderModel.Samplers.GetKey(i));
            }
            return read;
        }

        /// <summary>
        /// Points this material at a different shader archive, skipping the archive probe.
        /// The material editor uses it to preview a variation it has just generated, which
        /// lives in an archive assembled in memory rather than one on disk.
        /// </summary>
        public void RebindArchive(BfshaLibrary.BfshaFile archive, BfshaLibrary.ShaderModel model,
            BfresMeshAsset meshAsset)
        {
            if (!SwitchArchive(archive ?? BaseShaderArchiveFile, model ?? BaseShaderModel, meshAsset, false))
                return;
            ReloadProgram(meshAsset);
            meshAsset.Shape.HasValidShader = HasValidProgram;
        }

        /// <summary>
        /// Points the material at another archive: the held programs go back, the mesh and
        /// render state follow the new model, and the program is left for the caller to
        /// resolve. With asBase the archive also becomes what the probe chose.
        /// </summary>
        protected bool SwitchArchive(BfshaLibrary.BfshaFile archive, BfshaLibrary.ShaderModel model,
            BfresMeshAsset meshAsset, bool asBase)
        {
            if (model == null)
                return false;
            ShaderArchiveFile = archive;
            ShaderModel = model;
            if (asBase)
            {
                BaseShaderArchiveFile = archive;
                BaseShaderModel = model;
            }

            ReleasePrograms();
            _pendingPrep = null;
            UpdateShader = true;

            LoadMesh(meshAsset);
            ReloadRenderState(meshAsset);

            if (IsSwitch)
            {
                var attributeLocations = new Dictionary<string, int>();
                for (int i = 0; i < ShaderModel.Attributes.Count; i++)
                    attributeLocations.Add(ShaderModel.Attributes.GetKey(i),
                        ShaderModel.Attributes[i].Location);
                meshAsset.UpdateVaoAttributes(attributeLocations);
            }
            return true;
        }

        /// <summary>
        /// Reloads the program passes to render onto.
        /// If the program pass list is empty, the material will not load and display a red error handling material.
        /// </summary>
        public override void ReloadProgram(BfresMeshAsset mesh)
        {

        }

        /// <summary>
        /// Mesh loading info for loading additional data like hardcoded vertex attributes.
        /// </summary>
        public override void LoadMesh(BfresMeshAsset mesh)
        {

        }

        /// <summary>
        /// Reloads render info and state settings into the materials blend state for rendering.
        /// </summary>
        public override void ReloadRenderState(BfresMeshAsset mesh)
        {

        }

        /// <summary>
        /// The render loop to draw the material
        /// </summary>
        public override void Render(GLContext control, ShaderProgram shader, GenericPickableMesh mesh)
        {
            var bfresMaterial = (FMAT)this.MaterialData;
            var bfresMesh = (BfresMeshAsset)mesh;

            //Set the SRGB setting
            control.UseSRBFrameBuffer = UseSRGB;

            var programID = shader.program;

            //Set constants saved from shader code to the first uniform block of each stage
            if (IsSwitch)
            {
                LoadVertexShaderConstantBlock(programID);
                LoadPixelShaderConstantBlock(programID);
            }

            if (!IsSwitch)
                CafeShaderDecoder.SetShaderConstants(shader, programID, bfresMaterial);

            //Set in tool selection coloring
            shader.SetVector4("extraBlock.selectionColor", new Vector4(0));
            if (bfresMesh.IsSelected)
                shader.SetVector4("extraBlock.selectionColor", new Vector4(1, 1, 0.5f, 0.010f));

            //Alpha test emulation appended to decompiled pixel shaders.
            shader.SetIntCached("css_alphaTest", bfresMaterial.BlendState.AlphaTest ? 1 : 0);
            shader.SetFloat("css_alphaRef", bfresMaterial.BlendState.AlphaValue);
            shader.SetIntCached("css_alphaFunc", BfresMaterialAsset.GetAlphaFunc(bfresMaterial.BlendState.AlphaFunction));

            //Set material raster state and texture samplers
            SetBlendState(bfresMaterial);
            SetTextureUniforms(control, shader, MaterialData);
            SetRenderState(bfresMaterial);

            int binding = IsSwitch ? 2 : 0;
            for (int i = 0; i < ShaderModel.UniformBlocks.Count; i++)
            {
                string name = ShaderModel.UniformBlocks.GetKey(i);
                var uniformBlock = ShaderModel.UniformBlocks[i];

                var locationInfo = ProgramPasses[this.ShaderIndex].UniformBlockLocations[i];
                int fragLocation = locationInfo.FragmentLocation;
                int vertLocation = locationInfo.VertexLocation;

                //Block unused for this program so skip it
                if (fragLocation == -1 && vertLocation == -1)
                    continue;

                var shaderBlock = GetBlock(name + "vs", false, IsFrameBlock(name));

                //If a block is not cached, update it in the render loop, unless it was already
                //built in this render or pass and depends on nothing that changed since.
                if (!BlocksToCache.Contains(name)) {
                    long epoch = BlockLifetime(name) switch
                    {
                        BlockReuse.Render => RenderEpoch,
                        BlockReuse.Pass => PassEpoch,
                        _ => 0,
                    };
                    if (epoch == 0 || shaderBlock.Epoch != epoch)
                    {
                        shaderBlock.Buffer.Clear();
                        LoadUniformBlock(control, shader, i, shaderBlock, name, mesh);
                        shaderBlock.Epoch = epoch;
                    }
                }

                RenderBlock(shaderBlock, programID, vertLocation, fragLocation, binding++);
            }
        }

        /// <summary>
        /// Loads a given uniform block. Switch between the name to determine what type of block data to load.
        /// Fill the UniformBlock type with data.
        /// </summary>
        public virtual void LoadUniformBlock(GLContext control, ShaderProgram shader, int index,
            UniformBlock block, string name, GenericPickableMesh mesh)
        {
         
        }   

        /// <summary>
        /// A helper method to auto map commonly used render info settings to options.
        /// Not all games use the same render info settings so this only works for certain games!
        /// </summary>
        public virtual void LoadRenderStateOptions(Dictionary<string, string> options, FMAT mat) {
            ShaderOptionHelper.LoadRenderStateOptions(options, mat);
        }

        /// <summary>
        /// Fills the first constant block with constants from the shader code.
        /// This method must be called during render if the shader requires constants.
        /// </summary>
        public void LoadVertexShaderConstantBlock(int programID)
        {
            if (GLShaderInfo.VertexConstants == null)
                return;

            var firstBlock = GetBlock("vp_c1");
            firstBlock.Add(GLShaderInfo.VertexConstants);
            firstBlock.RenderBuffer(programID, "_vp_c1", 0);
        }

        /// <summary>
        /// Fills the first constant block with constants from the shader code.
        /// This method must be called during render if the shader requires constants.
        /// </summary>
        public void LoadPixelShaderConstantBlock(int programID)
        {
            if (GLShaderInfo.PixelConstants == null)
                return;

            var firstBlock = GetBlock("fp_c1");
            firstBlock.Add(GLShaderInfo.PixelConstants);
            firstBlock.RenderBuffer(programID, "_fp_c1", 1);
        }

        /// <summary>
        /// Searches for the shader archive file in external files, parent archive, and the global shader cache.
        /// </summary>
        /// <returns></returns>
        public virtual BfshaLibrary.BfshaFile TryLoadShaderArchive(BFRES bfres, string shaderFile, string shaderModel)
        {
            //Check external files.
            bfres.UpdateExternalShaderFiles();
            foreach (var file in bfres.ShaderFiles) {
                if (file is BfshaLibrary.BfshaFile && ((BfshaLibrary.BfshaFile)file).Name.Contains(shaderFile)) {
                    return (BfshaLibrary.BfshaFile)file;
                }
            }

            //Check global shader cache
            foreach (var file in GlobalShaderCache.ShaderFiles.Values)
            {
                if (file is BfshaLibrary.BfshaFile) {
                    if (((BfshaLibrary.BfshaFile)file).Name.Contains(shaderFile)) {
                        return (BfshaLibrary.BfshaFile)file;
                    }
                }
            }

            //Check external archives parenting the file.
            var archiveFile = bfres.FileInfo.ParentArchive;
            if (archiveFile == null)
                return null;

            foreach (var file in archiveFile.Files) {
                if (file.FileName.Contains(shaderFile)) {
                    if (file.FileFormat == null)
                        file.FileFormat = file.OpenFile();

                    return ((BFSHA)file.FileFormat).BfshaFile;
                }
            }
            return null;
        }

        //In-flight background decompiles per program pass (deferred compile mode).
        private System.Threading.Tasks.Task[] _pendingPrep;

        /// <summary>
        /// Checks if the program needs to be reloaded from a change in shader pass.
        /// </summary>
        public override void CheckProgram(GLContext control, BfresMeshAsset mesh, int pass = 0)
        {
            if (ProgramPasses.Count == 0) {
                return;
            }

            ShaderIndex = pass;
            if (GLShaders[pass] == null || UpdateShader) {
                //Interactive mode: run the expensive bytecode decompile on a worker
                //thread and leave the mesh shaderless (invisible) until it is done,
                //instead of stalling the render thread.
                if (TegraShaderDecoder.AllowDeferredCompile && IsSwitch && !UpdateShader)
                {
                    _pendingPrep ??= new System.Threading.Tasks.Task[GLShaders.Length];
                    if (_pendingPrep[pass] == null)
                        _pendingPrep[pass] = TegraShaderDecoder.PrepareShaderAsync(
                            ShaderModel.GetShaderVariation(ProgramPasses[pass]));

                    //Creating the GL shader objects below reads the ~1MB decompiled
                    //sources and costs ~10ms, so it shares the per-frame budget with
                    //first renders instead of running for every mesh in one frame.
                    if (!_pendingPrep[pass].IsCompleted || !ShaderRenderBase.TryClaimFirstRenderSlot())
                    {
                        shaderProgram = null;
                        return;
                    }
                }
                ReloadGLSLShaderFile(ProgramPasses[pass]);
            }
            shaderProgram = GLShaders[pass].Program;
        }

        /// <summary>
        /// Reloads the glsl shader file from the shader cache or saves a translated one if does not exist.
        /// </summary>
        public void ReloadGLSLShaderFile(BfshaLibrary.ResShaderProgram program) {
            if (IsSwitch)
                DecodeSwitchBinary(program);
            else
                DecodeWiiUBinary(program);

            UpdateShader = false;

            var matBlock = ShaderModel.UniformBlocks.Values.FirstOrDefault(x => x.Type == BfshaLibrary.UniformBlock.BlockType.Material);
            if (matBlock != null && GLShaderInfo != null)
            {
                var locationInfo = ProgramPasses[this.ShaderIndex].UniformBlockLocations[matBlock.Index];
                string blockNameFSH = IsSwitch ? $"fp_c{locationInfo.FragmentLocation + 3}.data" : $"CBUFFER_{locationInfo.FragmentLocation}.values";
                string blockNameVSH = IsSwitch ? $"vp_c{locationInfo.VertexLocation + 3}.data" : $"CBUFFER_{locationInfo.VertexLocation}.values";

                GLShaderInfo.CreateUsedUniformListVertex(matBlock, blockNameVSH);
                GLShaderInfo.CreateUsedUniformListPixel(matBlock, blockNameFSH);
            }
        }

        private static readonly HashSet<string> _framebufferSamplers = new HashSet<string>
        {
            "gsys_depth_buffer", "gsys_color_buffer"
        };

        //The fragment samplers that read a framebuffer, whose UV the decompiled source has
        //to be Y flipped for.
        private HashSet<string> YFlipSamplers(BfshaLibrary.ShaderModel shaderModel,
            BfshaLibrary.ResShaderProgram program)
        {
            HashSet<string> yFlipSamplers = null;
            var samplerLocs = program.SamplerLocations;
            for (int i = 0; i < shaderModel.Samplers.Count && i < samplerLocs.Length; i++)
            {
                if (!_framebufferSamplers.Contains(shaderModel.Samplers.GetKey(i)))
                    continue;
                int loc = samplerLocs[i].FragmentLocation;
                if (loc < 0) continue;
                yFlipSamplers ??= new HashSet<string>();
                yFlipSamplers.Add(ConvertSamplerID(loc));
            }
            return yFlipSamplers;
        }

        /// <summary>
        /// Builds the GL program for one archive program ahead of the draw that needs it and
        /// leaves it in the decoder's cache.
        ///
        /// Must be called on the render thread.
        /// </summary>
        public ShaderInfo PrewarmProgram(BfshaLibrary.ShaderModel shaderModel,
            BfshaLibrary.ResShaderProgram program)
        {
            return TegraShaderDecoder.LoadShaderProgram(shaderModel,
                shaderModel.GetShaderVariation(program), YFlipSamplers(shaderModel, program),
                hold: false);
        }

        //Hands every held program back to the decoder, which keeps it until a scene goes away.
        void ReleasePrograms()
        {
            for (int i = 0; i < GLShaders.Length; i++)
            {
                TegraShaderDecoder.Release(GLShaders[i]);
                GLShaders[i] = null;
            }
        }

        private void DecodeSwitchBinary(BfshaLibrary.ResShaderProgram program)
        {
            var yFlipSamplers = YFlipSamplers(ShaderModel, program);

            TegraShaderDecoder.Release(GLShaders[ShaderIndex]);
            GLShaders[ShaderIndex] = TegraShaderDecoder.LoadShaderProgram(
                ShaderModel, ShaderModel.GetShaderVariation(program), yFlipSamplers);
            shaderProgram = GLShaderInfo.Program;

            if (HoianNXRender.DebugMaterials)
                Console.WriteLine($"[SPL3dbg] material '{MaterialData?.Name}' pass {ShaderIndex} "
                    + $"-> {System.IO.Path.GetFileName(GLShaderInfo.VertPath)} "
                    + $"{System.IO.Path.GetFileName(GLShaderInfo.FragPath)} "
                    + $"constants vp={GLShaderInfo.VertexConstants?.Length.ToString() ?? "null"} "
                    + $"fp={GLShaderInfo.PixelConstants?.Length.ToString() ?? "null"}");
        }

        private void DecodeWiiUBinary(BfshaLibrary.ResShaderProgram program)
        {
            var vertexShader = BfshaGX2ShaderHelper.CreateVertexShader(ShaderModel, program);
            var pixelShader = BfshaGX2ShaderHelper.CreatePixelShader(ShaderModel, program);

            GLShaders[ShaderIndex] = CafeShaderDecoder.LoadShaderProgram(vertexShader, pixelShader);
            shaderProgram = GLShaderInfo.Program;
        }

        /// <summary>
        /// A helper method to set a common shape block layout.
        /// Note not all games use the same shape block data!
        /// </summary>
        public virtual void SetShapeBlock(BfresMeshAsset mesh, Matrix4 transform, UniformBlock block)
        {
            int numSkinning = (int)mesh.SkinCount;

            block.Buffer.Clear();
            block.Add(transform.Column0);
            block.Add(transform.Column1);
            block.Add(transform.Column2);
            block.AddInt(numSkinning);
        }

        /// <summary>
        /// A helper method to set a common skeleton bone block layout.
        /// Note not all games use the same skeleton bone block data!
        /// </summary>
        public virtual void SetBoneMatrixBlock(STSkeleton skeleton, bool useInverse, UniformBlock block, int maxTransforms = 170)
        {
            block.Buffer.Clear();

            //Fixed buffer of max amount of transform values
            for (int i = 0; i < maxTransforms; i++)
            {
                Matrix4 value = Matrix4.Zero;

                //Set the inverse matrix and load the matrix data into 3 vec4s
                if (i < skeleton.Bones.Count)
                {
                    //Check if the bone is smooth skinning aswell for accuracy purposes.
                    if (useInverse || ((BfresBone)skeleton.Bones[i]).UseSmoothMatrix) //Use inverse transforms for smooth skinning
                        value = skeleton.Bones[i].Inverse * skeleton.Bones[i].Transform;
                    else
                        value = skeleton.Bones[i].Transform;
                }

                block.Add(value.Column0);
                block.Add(value.Column1);
                block.Add(value.Column2);
            }
        }

        /// <summary>
        /// A helper method to set a material option block layout.
        /// </summary>
        public virtual void SetMaterialOptionsBlock(FMAT mat, UniformBlock block)
        {
            var uniformBlock = ShaderModel.UniformBlocks.Values.FirstOrDefault(
                x => x.Type == (BfshaLibrary.UniformBlock.BlockType)4);

            //Fill the buffer by program offsets
            var mem = new System.IO.MemoryStream();
            using (var writer = new Toolbox.Core.IO.FileWriter(mem))
            {
                writer.SeekBegin(0);

                int index = 0;
                foreach (var param in uniformBlock.Uniforms.Values)
                {
                    var uniformName = uniformBlock.Uniforms.GetKey(index++);

                    writer.SeekBegin(param.Offset - 1);
                    if (mat.ShaderOptions.ContainsKey(uniformName))
                    {
                        var option = mat.ShaderOptions[uniformName];
                        int value = int.Parse(option);
                        writer.Write(value);
                    }
                }
            }

            block.Buffer.Clear();
            block.Buffer.AddRange(mem.ToArray());
        }

        //The material block as it is built, before it is copied into the uniform block.
        byte[] _materialScratch = Array.Empty<byte>();

        /// <summary>
        /// A helper method to set a material parameter block layout: each parameter at its
        /// program offset, the block as long as the last one written.
        /// </summary>
        public virtual void SetMaterialBlock(FMAT mat, UniformBlock block)
        {
            var layout = GetLayout(BfshaLibrary.UniformBlock.BlockType.Material);
            if (_materialScratch.Length < layout.Size)
                _materialScratch = new byte[layout.Size];
            Array.Clear(_materialScratch);
            int end = 0;

            Span<float> m = stackalloc float[12];
            bool animated = mat.AnimatedParams.Count > 0;
            foreach (var pair in mat.ShaderParams)
            {
                if (!layout.Offsets.TryGetValue(pair.Key, out int offset))
                    continue;
                var matParam = pair.Value;
                if (animated && mat.AnimatedParams.TryGetValue(pair.Key, out var anim))
                    matParam = anim;

                if (matParam.Type == BfresLibrary.ShaderParamType.TexSrtEx) //Texture matrix (texmtx)
                {
                    CalculateSRT3x4((BfresLibrary.TexSrt)matParam.DataValue, m);
                    WriteFloats(ref end, offset, m.Slice(0, 12));
                }
                else if (matParam.Type == BfresLibrary.ShaderParamType.TexSrt)
                {
                    CalculateSRT2x3((BfresLibrary.TexSrt)matParam.DataValue, m);
                    WriteFloats(ref end, offset, m.Slice(0, 8));
                }
                else if (matParam.DataValue is BfresLibrary.Srt2D srt) //Indirect SRT (ind_texmtx)
                {
                    CalculateSRT(srt, m);
                    WriteFloats(ref end, offset, m.Slice(0, 8));
                }
                else if (matParam.DataValue is float f)
                    BinaryPrimitives.WriteSingleLittleEndian(MaterialSlot(ref end, offset, 4), f);
                else if (matParam.DataValue is float[] floats)
                    WriteFloats(ref end, offset, floats);
                else if (matParam.DataValue is int[] ints)
                {
                    for (int i = 0; i < ints.Length; i++)
                        BinaryPrimitives.WriteInt32LittleEndian(MaterialSlot(ref end, offset + i * 4, 4), ints[i]);
                }
                else if (matParam.DataValue is uint[] uints)
                {
                    for (int i = 0; i < uints.Length; i++)
                        BinaryPrimitives.WriteUInt32LittleEndian(MaterialSlot(ref end, offset + i * 4, 4), uints[i]);
                }
                else if (matParam.DataValue is int n)
                    BinaryPrimitives.WriteInt32LittleEndian(MaterialSlot(ref end, offset, 4), n);
                else if (matParam.DataValue is uint u)
                    BinaryPrimitives.WriteUInt32LittleEndian(MaterialSlot(ref end, offset, 4), u);
                else
                    throw new Exception($"Unsupported render type! {matParam.Type}");
            }

            block.SetData(_materialScratch.AsSpan(0, end), end);
        }

        //The scratch bytes for a value at offset, grown if a value runs past the block.
        Span<byte> MaterialSlot(ref int end, int offset, int length)
        {
            int need = offset + length;
            if (need > _materialScratch.Length)
                Array.Resize(ref _materialScratch, need);
            end = Math.Max(end, need);
            return _materialScratch.AsSpan(offset, length);
        }

        void WriteFloats(ref int end, int offset, ReadOnlySpan<float> values)
        {
            var slot = MaterialSlot(ref end, offset, values.Length * 4);
            for (int i = 0; i < values.Length; i++)
                BinaryPrimitives.WriteSingleLittleEndian(slot.Slice(i * 4), values[i]);
        }

        public override void SetTextureUniforms(GLContext control, ShaderProgram shader, STGenericMaterial mat)
        {
            var bfresMaterial = (FMAT)mat;

            GL.ActiveTexture(TextureUnit.Texture0 + 1);
            GL.BindTexture(TextureTarget.Texture2D, RenderTools.defaultTex.ID);

            int id = 1;

            //Load bindless textures first (textures can be binded without locations set in the program)
            LoadBindlessTextures(control, shader, ref id);

            //Go through all the shader samplers
            for (int i = 0; i < ShaderModel.Samplers.Count; i++)
            {
                var locationInfo = ProgramPasses[ShaderIndex].SamplerLocations[i];
                //Currently only using the vertex and fragment stages
                if (locationInfo.VertexLocation == -1 && locationInfo.FragmentLocation == -1)
                    continue;

                string sampler = ShaderModel.Samplers.GetKey(i);
                var textureIndex = -1;
                //Sampler assign has a key list of fragment shader samplers, value list of bfres material samplers
                if (bfresMaterial.Material.ShaderAssign.SamplerAssigns.ContainsKey(sampler))
                {
                    //Get the resource sampler
                    //Important to note, fragment samplers are unique while material samplers can be the same
                    //So we need to lookup which material sampler the current fragment sampler uses.
                    string resSampler = bfresMaterial.Material.ShaderAssign.SamplerAssigns[sampler].String;
                    //Find a texture using the sampler
                    textureIndex = bfresMaterial.TextureMaps.FindIndex(x => x.Sampler == resSampler);
                }

                //Cannot find the texture so try loading it from an external source
                if (textureIndex == -1)
                {
                    //Get external textures (ie shadow maps, cubemaps, etc)
                    var texture = GetExternalTexture(control, sampler);
                    if (texture != null)
                    {
                        GL.ActiveTexture(TextureUnit.Texture0 + id);
                        texture.Bind();
                        SetTexture(shader, locationInfo.VertexLocation, locationInfo.FragmentLocation, ref id);
                    }
                    continue;
                }

                //Get the current material texture map
                var texMap = bfresMaterial.TextureMaps[textureIndex];

                var name = texMap.Name;
                //Lookup samplers targeted via animations and use that texture instead if possible
                if (bfresMaterial.AnimatedSamplers.ContainsKey(texMap.Sampler))
                    name = bfresMaterial.AnimatedSamplers[texMap.Sampler];

                BindTexture(shader, GetTextures(), texMap, name, id);
                SetTexture(shader, locationInfo.VertexLocation, locationInfo.FragmentLocation, ref id);
            }

            GL.ActiveTexture(TextureUnit.Texture0);
            GL.BindTexture(TextureTarget.Texture2D, 0);
        }

        public virtual GLTexture GetExternalTexture(GLContext control, string sampler)
        {
            return null;
        }

        public virtual void LoadBindlessTextures(GLContext control, ShaderProgram shader, ref int id)
        {

        }

        //Sets the texture uniform from the locations given.
        private void SetTexture(ShaderProgram shader, int vertexLocation, int fragmentLocation, ref int id)
        {
            if (vertexLocation != -1)
                shader.SetInt(ConvertSamplerID(vertexLocation, true), id);
            if (fragmentLocation != -1)
                shader.SetInt(ConvertSamplerID(fragmentLocation, false), id);

            //Only increase the slot once as each stage share slots.
            id++;
        }

        private void RenderBlock(UniformBlock block, int programID, int vertexLocation, int fragmentLocation, int binding)
        {
            if (vertexLocation != -1)
                block.RenderBuffer(programID, BlockName(vertexLocation, true), binding);

            if (fragmentLocation != -1)
                block.RenderBuffer(programID, BlockName(fragmentLocation, false), binding);
        }

        //The program's name for a block location, built once rather than per draw.
        static readonly Dictionary<(int, bool, bool), string> _blockNames = new Dictionary<(int, bool, bool), string>();

        string BlockName(int location, bool vertex)
        {
            if (!_blockNames.TryGetValue((location, vertex, IsSwitch), out var name))
            {
                name = IsSwitch
                    ? (vertex ? $"_vp_c{location + 3}" : $"_fp_c{location + 3}")
                    : (vertex ? $"vp_{location}" : $"fp_{location}");
                _blockNames[(location, vertex, IsSwitch)] = name;
            }
            return name;
        }

        //Blocks holding this material's own data, so one that has not changed is not uploaded
        //again. Blocks every material fills alike stay in the shared UniformBlocks.
        readonly Dictionary<string, UniformBlock> _ownBlocks = new Dictionary<string, UniformBlock>();

        /// <summary>Whether a block holds the same data for every material, such as the camera or environment.</summary>
        protected virtual bool IsFrameBlock(string name) => false;

        /// <summary>How long a block's contents stay valid once built.</summary>
        protected enum BlockReuse
        {
            //Rebuilt for every draw, as anything mesh dependent must be.
            Draw,
            //The same for the whole scene render: material data.
            Render,
            //The same for one pass: the camera and the frame's shared inputs.
            Pass,
        }

        protected virtual BlockReuse BlockLifetime(string name) => BlockReuse.Draw;

        private UniformBlock GetBlock(string name, bool reset = true, bool shared = false)
        {
            var blocks = shared ? UniformBlocks : _ownBlocks;
            if (!blocks.TryGetValue(name, out var block))
                blocks.Add(name, block = new UniformBlock());

            if (reset)
                block.Buffer.Clear();
            return block;
        }

        /// <summary>A uniform block's size and each uniform's byte offset.</summary>
        protected sealed class BlockLayout
        {
            public int Size;
            public readonly Dictionary<string, int> Offsets = new Dictionary<string, int>();
        }

        //Per shader model, the layout of each block by index.
        static readonly System.Runtime.CompilerServices.ConditionalWeakTable<BfshaLibrary.ShaderModel, BlockLayout[]> _layouts =
            new System.Runtime.CompilerServices.ConditionalWeakTable<BfshaLibrary.ShaderModel, BlockLayout[]>();

        /// <summary>The layout of the uniform block at <paramref name="index"/> in the current shader model.</summary>
        protected BlockLayout GetLayout(int index) => _layouts.GetValue(ShaderModel, BuildLayouts)[index];

        /// <summary>The layout of the first block of <paramref name="type"/>, or null when there is none.</summary>
        protected BlockLayout GetLayout(BfshaLibrary.UniformBlock.BlockType type)
        {
            for (int i = 0; i < ShaderModel.UniformBlocks.Count; i++)
                if (ShaderModel.UniformBlocks[i].Type == type)
                    return GetLayout(i);
            return null;
        }

        static BlockLayout[] BuildLayouts(BfshaLibrary.ShaderModel model)
        {
            var layouts = new BlockLayout[model.UniformBlocks.Count];
            for (int i = 0; i < layouts.Length; i++)
            {
                var block = model.UniformBlocks[i];
                var layout = new BlockLayout { Size = block.Size };
                int index = 0;
                foreach (var param in block.Uniforms.Values)
                    layout.Offsets[block.Uniforms.GetKey(index++)] = param.Offset - 1;
                layouts[i] = layout;
            }
            return layouts;
        }

        private static void CalculateSRT3x4(BfresLibrary.TexSrt texSrt, Span<float> m)
        {
            Span<float> t = stackalloc float[8];
            CalculateSRT2x3(texSrt, t);
            m[0] = t[0]; m[1] = t[2]; m[2] = t[4]; m[3] = 0.0f;
            m[4] = t[1]; m[5] = t[3]; m[6] = t[5]; m[7] = 0.0f;
            m[8] = 0.0f; m[9] = 0.0f; m[10] = 1.0f; m[11] = 0.0f;
        }

        private static void CalculateSRT2x3(BfresLibrary.TexSrt texSrt, Span<float> m)
        {
            var scaling = texSrt.Scaling;
            var translate = texSrt.Translation;
            float cosR = (float)Math.Cos(texSrt.Rotation);
            float sinR = (float)Math.Sin(texSrt.Rotation);
            float scalingXC = scaling.X * cosR;
            float scalingXS = scaling.X * sinR;
            float scalingYC = scaling.Y * cosR;
            float scalingYS = scaling.Y * sinR;

            switch (texSrt.Mode)
            {
                default:
                case BfresLibrary.TexSrtMode.ModeMaya:
                    m[0] = scalingXC; m[1] = -scalingYS;
                    m[2] = scalingXS; m[3] = scalingYC;
                    m[4] = -0.5f * (scalingXC + scalingXS - scaling.X) - scaling.X * translate.X;
                    m[5] = -0.5f * (scalingYC - scalingYS + scaling.Y) + scaling.Y * translate.Y + 1.0f;
                    break;
                case BfresLibrary.TexSrtMode.Mode3dsMax:
                    m[0] = scalingXC; m[1] = -scalingYS;
                    m[2] = scalingXS; m[3] = scalingYC;
                    m[4] = -scalingXC * (translate.X + 0.5f) + scalingXS * (translate.Y - 0.5f) + 0.5f;
                    m[5] = scalingYS * (translate.X + 0.5f) + scalingYC * (translate.Y - 0.5f) + 0.5f;
                    break;
                case BfresLibrary.TexSrtMode.ModeSoftimage:
                    m[0] = scalingXC; m[1] = scalingYS;
                    m[2] = -scalingXS; m[3] = scalingYC;
                    m[4] = scalingXS - scalingXC * translate.X - scalingXS * translate.Y;
                    m[5] = -scalingYC - scalingYS * translate.X + scalingYC * translate.Y + 1.0f;
                    break;
            }
            m[6] = 0.0f; m[7] = 0.0f;
        }

        private static void CalculateSRT(BfresLibrary.Srt2D texSrt, Span<float> m)
        {
            var scaling = texSrt.Scaling;
            var translate = texSrt.Translation;
            float cosR = (float)Math.Cos(texSrt.Rotation);
            float sinR = (float)Math.Sin(texSrt.Rotation);

            m[0] = scaling.X * cosR; m[1] = scaling.X * sinR;
            m[2] = -scaling.Y * sinR; m[3] = scaling.Y * cosR;
            m[4] = translate.X; m[5] = translate.Y;
            m[6] = 0.0f; m[7] = 0.0f;
        }

        static readonly Dictionary<(int, bool, bool), string> _samplerNames = new Dictionary<(int, bool, bool), string>();

        protected string SamplerName(int id, bool vertexShader)
        {
            if (!_samplerNames.TryGetValue((id, vertexShader, IsSwitch), out var name))
                _samplerNames[(id, vertexShader, IsSwitch)] = name = ConvertSamplerID(id, vertexShader);
            return name;
        }

        public string ConvertSamplerID(int id, bool vertexShader = false)
        {
            if (IsSwitch)
            {
                if (vertexShader)
                    return "vp_tex_tcb_" + ((id * 2) + 8).ToString("X1");
                else
                    return "fp_tex_tcb_" + ((id * 2) + 8).ToString("X1");
            }
            else
            {
                return $"SPIRV_Cross_CombinedTEXTURE_{id}SAMPLER_{id}";
            }
        }

        public override void Dispose()
        {
            foreach (var block in UniformBlocks.Values)
                block.Dispose();
            foreach (var block in _ownBlocks.Values)
                block.Dispose();

            UniformBlocks.Clear();
            _ownBlocks.Clear();
            ReleasePrograms();
        }
    }
}
