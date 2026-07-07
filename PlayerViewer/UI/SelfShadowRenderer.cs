using System;
using OpenTK;
using OpenTK.Graphics.OpenGL;
using GLFrameworkEngine;
using PlayerViewer.Player;

namespace PlayerViewer.UI
{
    /// <summary>
    /// Replicates the game's screen-space shadow prepass (gsys_shadow_prepass).
    /// Pipeline per frame:
    ///  1. RenderLightDepth: draw the scene from the light into a depth map.
    ///  2. (caller renders the main pass once to fill the scene depth texture)
    ///  3. GeneratePrepass: fullscreen pass -> (r=const, g=visibility, b=const, a=1).
    ///     Visibility = 4 hardware-PCF taps (linear LEQUAL comparison sampler, like
    ///     the game's cascade-map sampler) spread a texel apart, averaged.
    ///  4. (caller re-renders the main pass with HoianNXRender.ShadowPrepassTexture set)
    /// </summary>
    public class SelfShadowRenderer : IDisposable
    {
        const int ShadowMapSize = 2048;

        //Matches the dumped prepass output: r/b are constants from fp_c3[34].
        const float OutR = 0.9792057f;
        const float OutB = 0.1946104f;

        Framebuffer _lightFbo;
        DepthTexture _lightDepth;
        Framebuffer _prepassFbo;
        ShaderProgram _shader;
        VertexBufferObject _vao;
        Matrix4 _lightViewProj;
        float _lightRadius = 1f;

        public GLTexture PrepassTexture => (GLTexture)_prepassFbo?.Attachments[0];

        void Init()
        {
            if (_lightFbo != null)
                return;

            _lightFbo = new Framebuffer(FramebufferTarget.Framebuffer,
                ShadowMapSize, ShadowMapSize, PixelInternalFormat.Rgba8, 1, useDepth: false);
            _lightDepth = new DepthTexture(ShadowMapSize, ShadowMapSize, PixelInternalFormat.DepthComponent24);
            _lightFbo.AddAttachment(FramebufferAttachment.DepthAttachment, _lightDepth);

            //Match the game's cascade-map sampler: linear filtering with LEQUAL
            //depth comparison (hardware PCF), clamp-to-border white. The prepass
            //shader samples this through a sampler2DShadow.
            _lightDepth.Bind();
            _lightDepth.MinFilter = TextureMinFilter.Linear;
            _lightDepth.MagFilter = TextureMagFilter.Linear;
            _lightDepth.UpdateParameters();
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureCompareMode,
                (int)TextureCompareMode.CompareRefToTexture);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureCompareFunc,
                (int)All.Lequal);
            _lightDepth.Unbind();

            _prepassFbo = new Framebuffer(FramebufferTarget.Framebuffer,
                4, 4, PixelInternalFormat.Rgba8, 1, useDepth: false);

            string frag = System.IO.File.ReadAllText("Shaders/SelfShadowPrepass.frag");
            string vert = System.IO.File.ReadAllText("Shaders/SelfShadowPrepass.vert");
            _shader = new ShaderProgram(new FragmentShader(frag), new VertexShader(vert));

            int buffer = GL.GenBuffer();
            _vao = new VertexBufferObject(buffer);
            _vao.AddAttribute(0, 2, VertexAttribPointerType.Float, false, 16, 0);
            _vao.AddAttribute(1, 2, VertexAttribPointerType.Float, false, 16, 8);
            _vao.Initialize();

            float[] data =
            {
                -1,  1, 0, 1,
                -1, -1, 0, 0,
                 1,  1, 1, 1,
                 1, -1, 1, 0,
            };
            GL.BindBuffer(BufferTarget.ArrayBuffer, buffer);
            GL.BufferData(BufferTarget.ArrayBuffer, sizeof(float) * data.Length, data, BufferUsageHint.StaticDraw);
        }

        /// <summary>
        /// Renders scene depth from the main light's view. The bounding sphere should
        /// cover the player and any carried gear.
        /// </summary>
        public void RenderLightDepth(GLContext context, IViewScene scene, Vector4 boundingSphere)
        {
            Init();

            var lightDir = BfresEditor.HoianNXRender.GetMainLightDir();
            var center = boundingSphere.Xyz;
            float radius = Math.Max(boundingSphere.W, 0.1f);
            _lightRadius = radius;

            var eye = center - lightDir * (radius * 2f);
            var up = Math.Abs(lightDir.Y) > 0.99f ? Vector3.UnitZ : Vector3.UnitY;
            var view = Matrix4.LookAt(eye, center, up);
            var proj = Matrix4.CreateOrthographic(radius * 2.2f, radius * 2.2f, 0.01f, radius * 4f);
            _lightViewProj = view * proj;

            var camera = context.Camera;
            var savedView = camera.ViewMatrix;
            var savedProj = camera.ProjectionMatrix;
            int savedWidth = context.Width, savedHeight = context.Height;

            camera.SetCustomMatrices(view, proj);
            context.Width = ShadowMapSize;
            context.Height = ShadowMapSize;

            _lightFbo.Bind();
            GL.Viewport(0, 0, ShadowMapSize, ShadowMapSize);
            GL.ClearColor(0, 0, 0, 0);
            GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
            GL.Enable(EnableCap.DepthTest);

            //Push the caster depth away from the light to avoid self-shadow acne.
            GL.Enable(EnableCap.PolygonOffsetFill);
            GL.PolygonOffset(4.0f, 16.0f);

            scene.Draw(context, Pass.OPAQUE);

            GL.Disable(EnableCap.PolygonOffsetFill);
            GL.PolygonOffset(0, 0);
            context.CurrentShader = null;
            _lightFbo.Unbind();

            context.Width = savedWidth;
            context.Height = savedHeight;
            camera.SetCustomMatrices(savedView, savedProj);
        }

        /// <summary>
        /// Builds the screen-space prepass from the main camera's depth texture.
        /// </summary>
        public void GeneratePrepass(GLContext context, DepthTexture sceneDepth,
            Matrix4 camViewProj, int width, int height)
        {
            Init();

            if (_prepassFbo.Width != width || _prepassFbo.Height != height)
                _prepassFbo.Resize(width, height);

            var invCamViewProj = camViewProj.Inverted();

            _prepassFbo.Bind();
            GL.Viewport(0, 0, width, height);
            GL.Disable(EnableCap.DepthTest);
            GL.Disable(EnableCap.Blend);
            GL.Disable(EnableCap.CullFace);

            context.CurrentShader = _shader;
            _shader.SetMatrix4x4("invCamViewProj", ref invCamViewProj);
            _shader.SetMatrix4x4("lightViewProj", ref _lightViewProj);
            _shader.SetFloat("outR", OutR);
            _shader.SetFloat("outB", OutB);

            //View-distance fade like the game (fp_c3[47].y=100 start, +900 range),
            //scaled to the scene: a framed view (~2.6-3.6 radii away) keeps its
            //shadows, zooming out well past that fades them out (sub-texel there).
            float fadeStart = Math.Max(100f, _lightRadius * 4f);
            float fadeEnd = fadeStart * 2f;
            var camPos = context.Camera.GetViewPostion();
            _shader.SetVector3("camPos", camPos);
            _shader.SetFloat("fadeStart", fadeStart);
            _shader.SetFloat("fadeInvRange", 1.0f / (fadeEnd - fadeStart));

            //Scene-depth (24-bit) reconstruction error grows ~ d^2/(znear*2^24) world
            //units; converted to light NDC (range = radius*4) with 2x slack it becomes
            //extra shadow bias so distant flat surfaces don't dissolve into acne.
            float znear = Math.Max(context.Camera.ZNear, 0.001f);
            float lightRange = _lightRadius * 4f;
            _shader.SetFloat("biasScale", 2.0f / (znear * 16777216f) / lightRange);

            GL.ActiveTexture(TextureUnit.Texture0);
            sceneDepth.Bind();
            _shader.SetInt("sceneDepth", 0);

            GL.ActiveTexture(TextureUnit.Texture1);
            _lightDepth.Bind();
            _shader.SetInt("lightDepth", 1);

            _vao.Enable(_shader);
            _vao.Use();
            GL.DrawArrays(PrimitiveType.TriangleStrip, 0, 4);

            GL.UseProgram(0);
            context.CurrentShader = null;
            GL.Enable(EnableCap.DepthTest);
            _prepassFbo.Unbind();
        }

        public void Dispose()
        {
            _lightFbo?.Dispoe();
            _prepassFbo?.Dispoe();
        }
    }
}
