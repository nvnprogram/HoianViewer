using System;
using System.Linq;
using GLFrameworkEngine;
using OpenTK;
using OpenTK.Graphics.OpenGL;
using PlayerViewer.Player;

namespace PlayerViewer.UI
{
    /// <summary>
    /// Outlines one material's silhouette over the resolved viewport image. Its meshes go into a
    /// supersampled mask, depth compared against the scene depth texture in the shader so the
    /// scene's own buffer is only read, and a display resolution pass traces the mask's edge.
    /// </summary>
    sealed class SelectionOutline : IDisposable
    {
        //Parts of the silhouette hidden behind other geometry draw at this fraction.
        const float HiddenStrength = 0.4f;

        //The outline shader's coarse probe reads this mip, so the coverage texture is padded
        //to a multiple of its texel size.
        const int ProbeAlign = 16;

        ShaderProgram _maskShader,
            _coverageShader,
            _outlineShader;

        int _maskFbo,
            _maskTex,
            _maskW,
            _maskH;
        int _coverageFbo,
            _coverageTex,
            _coverageW,
            _coverageH;

        /// <summary>
        /// Draws the outline of <paramref name="material"/> into the currently bound display
        /// framebuffer. The camera must still hold the matrices the scene was drawn with.
        /// </summary>
        public void Draw(
            GLContext context,
            IViewScene scene,
            BfresEditor.FMAT material,
            DepthTexture sceneDepth,
            int width,
            int height,
            int scale
        )
        {
            GL.GetInteger(GetPName.DrawFramebufferBinding, out int target);
            Init();
            int ssWidth = width * scale;
            int ssHeight = height * scale;
            EnsureTargets(ssWidth, ssHeight, width, height);

            GL.BindFramebuffer(FramebufferTarget.Framebuffer, _maskFbo);
            GL.Viewport(0, 0, ssWidth, ssHeight);
            GL.ClearColor(0, 0, 0, 0);
            GL.Clear(ClearBufferMask.ColorBufferBit);
            bool drawn = DrawMask(context, scene, material, sceneDepth);

            if (drawn)
            {
                GL.BindFramebuffer(FramebufferTarget.Framebuffer, _coverageFbo);
                GL.Viewport(0, 0, width, height);
                //The padding beyond the viewport must read as empty to the probe.
                GL.Clear(ClearBufferMask.ColorBufferBit);
                context.CurrentShader = _coverageShader;
                GL.ActiveTexture(TextureUnit.Texture0);
                GL.BindTexture(TextureTarget.Texture2D, _maskTex);
                _coverageShader.SetInt("uMask", 0);
                _coverageShader.SetInt("uFactor", scale);
                DrawQuad(_coverageShader);

                GL.BindFramebuffer(FramebufferTarget.Framebuffer, target);
                GL.Viewport(0, 0, width, height);
                GL.BindTexture(TextureTarget.Texture2D, _coverageTex);
                GL.GenerateMipmap(GenerateMipmapTarget.Texture2D);
                GL.Enable(EnableCap.Blend);
                GL.BlendEquation(BlendEquationMode.FuncAdd);
                GL.BlendFunc(BlendingFactor.One, BlendingFactor.OneMinusSrcAlpha);
                context.CurrentShader = _outlineShader;
                _outlineShader.SetInt("uCoverage", 0);
                var accent = Theme.GoldBright;
                _outlineShader.SetVector3("uColor", new Vector3(accent.X, accent.Y, accent.Z));
                _outlineShader.SetFloat("uHidden", HiddenStrength);
                DrawQuad(_outlineShader);
                GL.Disable(EnableCap.Blend);
            }

            GL.BindTexture(TextureTarget.Texture2D, 0);
            context.CurrentShader = null;
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, target);
            GL.Viewport(0, 0, width, height);
        }

        //Draws the material's meshes with the scene's visibility rules and cull state.
        bool DrawMask(
            GLContext context,
            IViewScene scene,
            BfresEditor.FMAT material,
            DepthTexture sceneDepth
        )
        {
            var camera = context.Camera;
            bool began = ScenePipeline.DrawSceneMeshes(
                scene,
                () =>
                {
                    context.CurrentShader = _maskShader;
                    var mtxCam = camera.ViewProjectionMatrix;
                    _maskShader.SetMatrix4x4("mtxCam", ref mtxCam);
                    _maskShader.SetVector2("uNearFar", new Vector2(camera.ZNear, camera.ZFar));
                    GL.ActiveTexture(TextureUnit.Texture0);
                    sceneDepth.Bind();
                    _maskShader.SetInt("uSceneDepth", 0);
                    GL.Disable(EnableCap.DepthTest);
                    GL.DepthMask(false);
                    GL.Enable(EnableCap.Blend);
                    GL.BlendEquation(BlendEquationMode.Max);
                    return _maskShader;
                },
                (model, mesh) =>
                    mesh.Shape.Material == material
                    && mesh.IsVisible
                    && model.ModelData.Skeleton.Bones[mesh.BoneIndex].Visible,
                prepare: mesh =>
                {
                    ((BfresEditor.BfresMaterialAsset)mesh.MaterialAsset).SetRenderState(material);
                    //The depth offset the scene draws a seal mesh with.
                    GL.Enable(EnableCap.PolygonOffsetFill);
                    GL.PolygonOffset(mesh.IsSealPass ? -1f : 0f, mesh.IsSealPass ? 1f : 0f);
                }
            );

            if (!began)
                return false;

            GL.BlendEquation(BlendEquationMode.FuncAdd);
            GL.Disable(EnableCap.Blend);
            GL.DepthMask(true);
            GL.Enable(EnableCap.DepthTest);
            GL.Enable(EnableCap.CullFace);
            GL.CullFace(CullFaceMode.Back);
            GL.BindVertexArray(0);
            return true;
        }

        void Init()
        {
            if (_maskShader != null)
                return;
            string quadVert = ScreenQuad.VertexSource;
            _maskShader = new ShaderProgram(
                new FragmentShader(UiShaders.Load("SelectionMask.frag")),
                new VertexShader(UiShaders.Load("SelectionMask.vert"))
            );
            _coverageShader = new ShaderProgram(
                new FragmentShader(UiShaders.Load("SelectionCoverage.frag")),
                new VertexShader(quadVert)
            );
            _outlineShader = new ShaderProgram(
                new FragmentShader(UiShaders.Load("SelectionOutline.frag")),
                new VertexShader(quadVert)
            );
        }

        void DrawQuad(ShaderProgram shader)
        {
            GL.Disable(EnableCap.DepthTest);
            GL.Disable(EnableCap.CullFace);
            ScreenQuad.Draw(shader);
            GL.BindVertexArray(0);
            GL.Enable(EnableCap.DepthTest);
            GL.Enable(EnableCap.CullFace);
        }

        void EnsureTargets(int maskW, int maskH, int width, int height)
        {
            if (maskW != _maskW || maskH != _maskH)
            {
                DeleteTarget(ref _maskFbo, ref _maskTex);
                (_maskFbo, _maskTex) = CreateTarget(maskW, maskH, PixelInternalFormat.Rg8, false);
                _maskW = maskW;
                _maskH = maskH;
            }

            int coverageW = (width + ProbeAlign - 1) / ProbeAlign * ProbeAlign;
            int coverageH = (height + ProbeAlign - 1) / ProbeAlign * ProbeAlign;
            if (coverageW != _coverageW || coverageH != _coverageH)
            {
                DeleteTarget(ref _coverageFbo, ref _coverageTex);
                (_coverageFbo, _coverageTex) = CreateTarget(
                    coverageW,
                    coverageH,
                    PixelInternalFormat.Rg16f,
                    true
                );
                _coverageW = coverageW;
                _coverageH = coverageH;
            }
        }

        static (int fbo, int tex) CreateTarget(
            int width,
            int height,
            PixelInternalFormat format,
            bool mipmapped
        )
        {
            int tex = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, tex);
            GL.TexImage2D(
                TextureTarget.Texture2D,
                0,
                format,
                width,
                height,
                0,
                PixelFormat.Rg,
                PixelType.Float,
                IntPtr.Zero
            );
            GL.TexParameter(
                TextureTarget.Texture2D,
                TextureParameterName.TextureMinFilter,
                (int)(mipmapped ? TextureMinFilter.LinearMipmapNearest : TextureMinFilter.Linear)
            );
            GL.TexParameter(
                TextureTarget.Texture2D,
                TextureParameterName.TextureMagFilter,
                (int)TextureMagFilter.Linear
            );
            GL.TexParameter(
                TextureTarget.Texture2D,
                TextureParameterName.TextureWrapS,
                (int)TextureWrapMode.ClampToEdge
            );
            GL.TexParameter(
                TextureTarget.Texture2D,
                TextureParameterName.TextureWrapT,
                (int)TextureWrapMode.ClampToEdge
            );
            if (mipmapped)
                GL.GenerateMipmap(GenerateMipmapTarget.Texture2D);
            GL.BindTexture(TextureTarget.Texture2D, 0);

            int fbo = GL.GenFramebuffer();
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
            GL.FramebufferTexture2D(
                FramebufferTarget.Framebuffer,
                FramebufferAttachment.ColorAttachment0,
                TextureTarget.Texture2D,
                tex,
                0
            );
            GL.DrawBuffer(DrawBufferMode.ColorAttachment0);
            return (fbo, tex);
        }

        static void DeleteTarget(ref int fbo, ref int tex)
        {
            if (fbo != 0)
                GL.DeleteFramebuffer(fbo);
            if (tex != 0)
                GL.DeleteTexture(tex);
            fbo = tex = 0;
        }

        public void Dispose()
        {
            DeleteTarget(ref _maskFbo, ref _maskTex);
            DeleteTarget(ref _coverageFbo, ref _coverageTex);
            _maskW = _maskH = _coverageW = _coverageH = 0;
            if (_maskShader == null)
                return;
            _maskShader.Dispose();
            _coverageShader.Dispose();
            _outlineShader.Dispose();
            _maskShader = _coverageShader = _outlineShader = null;
        }
    }
}
