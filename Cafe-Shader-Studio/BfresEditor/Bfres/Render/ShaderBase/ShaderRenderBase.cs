using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using GLFrameworkEngine;

namespace BfresEditor
{
    public class ShaderRenderBase : BfresMaterialAsset
    {
        /// <summary>
        /// Determines to enable SRGB or not when drawn to the final framebuffer.
        /// </summary>
        public virtual bool UseSRGB { get; } = true;

        /// <summary>
        /// Determines if the current material has a valid program.
        /// If the model fails to find a program in ReloadProgram, it will fail to load the shader.
        /// </summary>
        public virtual bool HasValidProgram { get; }

        /// <summary>
        /// Set after the first successful render. First renders pay one-time costs
        /// (UBO creation, sampler reflection, driver program specialization) and are
        /// budgeted per frame to avoid hitching when many meshes appear at once.
        /// </summary>
        public bool FirstRenderDone;

        static long _firstRenderFrame = -1;
        static int _firstRenderCount;

        /// <summary>
        /// Claims a slot in this frame's first-render budget. Returns false when the
        /// budget is spent; the caller should skip the mesh (invisible) this frame.
        /// </summary>
        public static bool TryClaimFirstRenderSlot()
        {
            if (_firstRenderFrame != GLFrameworkEngine.ShaderProgram.FrameStamp)
            {
                _firstRenderFrame = GLFrameworkEngine.ShaderProgram.FrameStamp;
                _firstRenderCount = 0;
            }
            if (_firstRenderCount >= 1)
                return false;
            _firstRenderCount++;
            return true;
        }

        /// <summary>
        /// Shader information from the decoded shader.
        /// This is used to store constants and source information.
        /// </summary>
        public ShaderInfo GLShaderInfo => GLShaders[ShaderIndex];

        /// <summary>
        /// Gets or sets a list of shaders used.
        /// </summary>
        public ShaderInfo[] GLShaders = new ShaderInfo[10];

        public int ShaderIndex { get; set; } = 0;

        /// <summary>
        /// Determines when to use this renderer for the given material.
        /// This is typically done from the shader archive or shader model name.
        /// The material can also be used for shader specific render information to check.
        /// </summary>
        /// <returns></returns>
        public virtual bool UseRenderer(FMAT material, string archive, string model)
        {
            return false;
        }

        /// <summary>
        /// A list of uniform blocks to store the current block data.
        /// Blocks are obtained using GetBlock() and added if one does not exist.
        /// </summary>
        public static Dictionary<string, UniformBlock> UniformBlocks = new Dictionary<string, UniformBlock>();

        /// <summary>
        /// Loads the material renderer for the first time.
        /// </summary>
        public virtual void TryLoadShader(BFRES bfres, FMDL fmdl, FSHP mesh, BfresMeshAsset meshAsset)
        {

        }

        /// <summary>
        /// Checks if the program needs to be reloaded from a change in shader pass.
        /// </summary>
        public virtual void CheckProgram(GLContext control, BfresMeshAsset mesh, int pass = 0)
        {

        }

        /// <summary>
        /// Reloads the program passes to render onto.
        /// If the program pass list is empty, the material will not load.
        /// </summary>
        public virtual void ReloadProgram(BfresMeshAsset mesh)
        {

        }

        /// <summary>
        /// Mesh loading info for loading additional data like hardcoded vertex attributes.
        /// </summary>
        public virtual void LoadMesh(BfresMeshAsset mesh)
        {

        }

        /// <summary>
        /// Reloads render info and state settings into the materials blend state for rendering.
        /// </summary>
        public virtual void ReloadRenderState(BfresMeshAsset mesh)
        {

        }

        /// <summary>
        /// Gets or sets the state of the shader file.
        /// </summary>
        public ShaderState ShaderFileState { get; set; }

        public enum ShaderState
        {
            /// <summary>
            /// The shader file is from a global source.
            /// </summary>
            Global,
            /// <summary>
            /// The shader file is embedded in a resource file.
            /// </summary>
            EmbeddedResource,
            /// <summary>
            /// The shader file is inside an archive file.
            /// </summary>
            EmbeddedArchive,
        }
    }
}
