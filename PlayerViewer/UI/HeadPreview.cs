using System;
using GLFrameworkEngine;
using OpenTK;
using OpenTK.Graphics.OpenGL;
using PlayerViewer.HairGen;

namespace PlayerViewer.UI
{
    /// <summary>
    /// A stand-in for the player's head behind a standalone hair: the ellipsoid fitted to the
    /// players' heads, ray traced over the resolved viewport image, opaque, and depth compared
    /// against the scene, so hair that sinks into the head disappears into it.
    /// </summary>
    public sealed class HeadPreview : IDisposable
    {
        ShaderProgram _shader;
        int _vao;

        /// <summary>The hair's model space to the displayed space.</summary>
        public Matrix4 Model = Matrix4.Identity;

        /// <summary>Draws into the bound display framebuffer. The camera must still hold the matrices the scene was drawn with.</summary>
        public void Draw(GLContext context, DepthTexture sceneDepth, int scale)
        {
            _shader ??= new ShaderProgram(
                new FragmentShader(UiShaders.Load("HeadPreview.frag")),
                new VertexShader(UiShaders.Load("HeadPreview.vert"))
            );
            if (_vao == 0)
                _vao = GL.GenVertexArray();
            var camera = context.Camera;
            var viewProjection = camera.ViewProjectionMatrix;
            var inverseViewProjection = Matrix4.Invert(viewProjection);
            var toUnit = Matrix4.Invert(
                Matrix4.CreateScale(HeadShape.DefaultRadii)
                    * Matrix4.CreateTranslation(HeadShape.DefaultCentre)
                    * Model
            );
            var eye = Matrix4.Invert(camera.ViewMatrix).ExtractTranslation();

            OverlayPass.Begin(context, _shader, sceneDepth, scale, blend: false);
            _shader.SetMatrix4x4("uViewProj", ref viewProjection);
            _shader.SetMatrix4x4("uInvViewProj", ref inverseViewProjection);
            _shader.SetMatrix4x4("uToUnit", ref toUnit);
            _shader.SetVector3("uEye", eye);

            GL.BindVertexArray(_vao);
            GL.DrawArrays(PrimitiveType.Triangles, 0, 3);
            OverlayPass.End();
        }

        public void Dispose()
        {
            _shader?.Dispose();
            _shader = null;
            if (_vao != 0)
                GL.DeleteVertexArray(_vao);
            _vao = 0;
        }
    }
}
