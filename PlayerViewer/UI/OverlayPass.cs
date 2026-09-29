using GLFrameworkEngine;
using OpenTK;
using OpenTK.Graphics.OpenGL;

namespace PlayerViewer.UI
{
    /// <summary>
    /// The state the viewport overlays draw in, over the resolved image: their shader does the
    /// depth test against the scene's depth, so GL's own depth test, depth writes and culling are off.
    /// </summary>
    static class OverlayPass
    {
        /// <summary>
        /// Binds the shader with the camera's near and far, the supersample factor and the scene
        /// depth. A blended overlay adds its first output and multiplies the image by its second.
        /// </summary>
        public static void Begin(
            GLContext context,
            ShaderProgram shader,
            DepthTexture sceneDepth,
            int scale,
            bool blend
        )
        {
            var camera = context.Camera;
            context.CurrentShader = shader;
            GL.UseProgram(shader.program);
            shader.SetVector2("uNearFar", new Vector2(camera.ZNear, camera.ZFar));
            shader.SetInt("uFactor", scale);
            GL.ActiveTexture(TextureUnit.Texture0);
            sceneDepth.Bind();
            shader.SetInt("uSceneDepth", 0);
            GL.Disable(EnableCap.DepthTest);
            GL.DepthMask(false);
            GL.Disable(EnableCap.CullFace);
            if (blend)
            {
                GL.Enable(EnableCap.Blend);
                GL.BlendEquation(BlendEquationMode.FuncAdd);
                GL.BlendFunc(BlendingFactor.One, BlendingFactor.Src1Color);
            }
            else
                GL.Disable(EnableCap.Blend);
        }

        /// <summary>Puts back the state the scene passes expect.</summary>
        public static void End()
        {
            GL.Disable(EnableCap.Blend);
            GL.DepthMask(true);
            GL.Enable(EnableCap.DepthTest);
            GL.Enable(EnableCap.CullFace);
            GL.CullFace(CullFaceMode.Back);
            GL.BindVertexArray(0);
            GL.BindTexture(TextureTarget.Texture2D, 0);
            GL.UseProgram(0);
        }
    }
}
