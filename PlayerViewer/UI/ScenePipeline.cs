using System;
using GLFrameworkEngine;
using OpenTK;
using OpenTK.Graphics.OpenGL;
using PlayerViewer.Player;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace PlayerViewer.UI
{
    /// <summary>
    /// Minimal offscreen render pipeline for the player scene: supersampled linear
    /// HDR buffer, then a gamma+downsample pass into a displayable RGBA8 texture.
    /// Also renders one-off captures at arbitrary resolutions.
    /// </summary>
    public class ScenePipeline : IDisposable
    {
        public GLContext Context { get; private set; }
        public Camera Camera => Context.Camera;

        public int Width { get; private set; } = 1;
        public int Height { get; private set; } = 1;

        /// <summary>Viewport background (sRGB). Alpha is ignored for display.</summary>
        public System.Numerics.Vector3 BackgroundColor = new(0.075f, 0.075f, 0.09f);

        /// <summary>Main light shines from the camera when enabled.</summary>
        public bool LightFollowsCamera;

        float _lightAzimuth = 100,
            _lightElevation = -55;
        bool _lightCustomized;
        public float LightAzimuth
        {
            get => _lightAzimuth;
            set
            {
                _lightAzimuth = value;
                _lightCustomized = true;
            }
        }
        public float LightElevation
        {
            get => _lightElevation;
            set
            {
                _lightElevation = value;
                _lightCustomized = true;
            }
        }

        /// <summary>Restores the dumped default lighting (follow-cam off, no override).</summary>
        public void ResetLighting()
        {
            LightFollowsCamera = false;
            _lightAzimuth = 100;
            _lightElevation = -55;
            _lightCustomized = false; //fall back to the dumped default direction
        }

        void UpdateLightOverride()
        {
            if (LightFollowsCamera)
            {
                //Direction the light travels = camera forward. Mirror it into the
                //azimuth/elevation sliders so disabling follow-cam keeps the light
                //where it last was.
                var forward = -Camera.InverseRotationMatrix.Row2;
                _lightElevation = MathHelper.RadiansToDegrees(
                    (float)Math.Asin(MathHelper.Clamp(forward.Y, -1, 1))
                );
                _lightAzimuth = MathHelper.RadiansToDegrees(
                    (float)Math.Atan2(forward.X, forward.Z)
                );
                _lightCustomized = true;
                BfresEditor.HoianNXRender.LightDirOverride = forward;
            }
            else if (_lightCustomized)
            {
                float az = MathHelper.DegreesToRadians(_lightAzimuth);
                float el = MathHelper.DegreesToRadians(_lightElevation);
                BfresEditor.HoianNXRender.LightDirOverride = new Vector3(
                    (float)(Math.Cos(el) * Math.Sin(az)),
                    (float)Math.Sin(el),
                    (float)(Math.Cos(el) * Math.Cos(az))
                );
            }
            else
                BfresEditor.HoianNXRender.LightDirOverride = null; //dumped default
        }

        //The framework's MSAA framebuffer path is broken (the color attachment ends
        //up non-multisampled and incomplete on strict drivers), so anti-alias by
        //supersampling: render 2x and downsample with linear filtering in the gamma pass.
        Framebuffer _screen; //RGBA16F (linear), supersampled
        DepthTexture _screenDepth;
        Framebuffer _final; //RGBA8 (sRGB encoded by the gamma pass)
        FinalQuad _quad;
        SelfShadowRenderer _selfShadow;

        //Live export-background preview: a fullscreen textured quad drawn behind the scene in
        //opaque passes. The pixels come from ExportUtil.BuildBackground so the preview matches
        //the exported composite exactly.
        readonly BackgroundQuad _bgQuad = new();
        int _bgTex;

        //Half-res color copy for refraction (once per frame, between opaque/transparent).
        int _refractionFbo;
        GLTexture2D _refractionColor;
        int _refractionW,
            _refractionH;

        /// <summary>Game-accurate self shadowing (shadow prepass). On by default.</summary>
        public bool EnableSelfShadow = true;
        const int SuperSample = 2;

        //Above this pixel count captures render at 1x (a 4K capture is sharp already).
        const long SuperSampleBudget = 2560L * 1440L;

        static int ScaleFor(int width, int height) =>
            (long)width * height > SuperSampleBudget ? 1 : SuperSample;

        //When >0, export/capture renders use this supersample scale instead of the auto
        //budget above (set from the Settings factor); reset to 0 for interactive sizing.
        public int ExportScaleOverride;
        int _screenScale = SuperSample;

        int EffectiveScale(int width, int height) =>
            ExportScaleOverride > 0 ? ExportScaleOverride : ScaleFor(width, height);

        public void Init()
        {
            Context = new GLContext();
            Context.Camera = new Camera();
            Context.Camera.ZNear = 0.01f;
            Context.Camera.Mode = Camera.CameraMode.Inspect; //instantiates the controller
            Context.UseSRBFrameBuffer = true;
            //Camera math needs a valid aspect ratio before framing (0x0 -> NaN distance).
            Context.Width = Width;
            Context.Height = Height;
            Context.Camera.Width = Width;
            Context.Camera.Height = Height;
            FramePlayer();

            _screen = CreateScreenBuffer(
                Width * SuperSample,
                Height * SuperSample,
                out _screenDepth
            );
            _final = new Framebuffer(
                FramebufferTarget.Framebuffer,
                Width,
                Height,
                PixelInternalFormat.Rgba8,
                1
            );
            _quad = new FinalQuad();
        }

        //Scene color buffer with a sampleable depth texture (the shadow prepass
        //reconstructs world positions from it).
        static Framebuffer CreateScreenBuffer(int width, int height, out DepthTexture depth)
        {
            var fbo = new Framebuffer(
                FramebufferTarget.Framebuffer,
                width,
                height,
                PixelInternalFormat.Rgba16f,
                1,
                useDepth: false
            );
            depth = new DepthTexture(width, height, PixelInternalFormat.DepthComponent24);
            fbo.AddAttachment(FramebufferAttachment.DepthAttachment, depth);
            return fbo;
        }

        /// <summary>
        /// Blits the scene color at half resolution for refraction, and binds the live
        /// scene depth texture directly. The shader handles the OpenGL <-> NX Y-flip via a
        /// patched texture() call (see <see cref="TegraShaderDecoder.PatchSamplerYFlip"/>).
        /// </summary>
        void CaptureRefractionBuffers(Framebuffer screen, DepthTexture depth, int ssW, int ssH)
        {
            int halfW = ssW / 2,
                halfH = ssH / 2;
            if (halfW < 1 || halfH < 1)
                return;

            if (_refractionFbo == 0 || _refractionW != halfW || _refractionH != halfH)
            {
                DisposeRefraction(); //frees the previous-size fbo + color texture
                _refractionColor = new GLTexture2D();
                GL.BindTexture(TextureTarget.Texture2D, _refractionColor.ID);
                GL.TexImage2D(
                    TextureTarget.Texture2D,
                    0,
                    PixelInternalFormat.R11fG11fB10f,
                    halfW,
                    halfH,
                    0,
                    PixelFormat.Rgb,
                    PixelType.Float,
                    IntPtr.Zero
                );
                GL.TexParameter(
                    TextureTarget.Texture2D,
                    TextureParameterName.TextureMinFilter,
                    (int)TextureMinFilter.Linear
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
                _refractionFbo = GL.GenFramebuffer();
                GL.BindFramebuffer(FramebufferTarget.Framebuffer, _refractionFbo);
                GL.FramebufferTexture2D(
                    FramebufferTarget.Framebuffer,
                    FramebufferAttachment.ColorAttachment0,
                    TextureTarget.Texture2D,
                    _refractionColor.ID,
                    0
                );
                _refractionW = halfW;
                _refractionH = halfH;
            }

            GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, screen.ID);
            GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, _refractionFbo);
            GL.BlitFramebuffer(
                0,
                0,
                ssW,
                ssH,
                0,
                0,
                halfW,
                halfH,
                ClearBufferMask.ColorBufferBit,
                BlitFramebufferFilter.Linear
            );

            BfresEditor.HoianNXRender.RefractionColorBuffer = _refractionColor;
            BfresEditor.HoianNXRender.RefractionDepthBuffer = depth;
        }

        void DisposeRefraction()
        {
            if (_refractionFbo != 0)
                GL.DeleteFramebuffer(_refractionFbo);
            _refractionFbo = 0;
            _refractionColor?.Dispose();
            _refractionColor = null;
            _refractionW = _refractionH = 0;
        }

        //Fallback self-shadow light bounds (player-sized), used when the scene has
        //no valid render bounds yet.
        Vector4 _shadowBounds = new Vector4(0, 0.85f, 0, 1.6f);

        //Union of the scene's render bounding spheres, so the light frustum always
        //covers the ACTUAL content (a framed player sphere would clip stage models,
        //and "Reset camera" must not shrink the shadows).
        Vector4 ComputeShadowBounds(IViewScene scene)
        {
            var min = new Vector3(float.MaxValue);
            var max = new Vector3(float.MinValue);
            foreach (var render in scene.AllRenders())
            {
                var bs = render.BoundingSphere;
                if (bs.W <= 0.0001f || !render.IsVisible)
                    continue;
                min = Vector3.ComponentMin(min, bs.Xyz - new Vector3(bs.W));
                max = Vector3.ComponentMax(max, bs.Xyz + new Vector3(bs.W));
            }
            if (min.X > max.X)
                return _shadowBounds;
            var center = (min + max) * 0.5f;
            float radius = Math.Max((max - min).Length * 0.5f, 0.1f);
            //Slight inflation so carried gear/overhangs still cast.
            return new Vector4(center, radius * 1.1f);
        }

        /// <summary>Frames the whole player (stands at origin, ~1.6 units tall).</summary>
        public void FramePlayer()
        {
            FrameSphere(new Vector4(0, 0.85f, 0, 1.15f));
        }

        /// <summary>Frames an arbitrary bounding sphere (standalone models).</summary>
        public void FrameSphere(Vector4 sphere)
        {
            _shadowBounds = new Vector4(sphere.Xyz, sphere.W * 1.4f);
            Camera.ZNear = Math.Max(0.01f, sphere.W * 0.01f);
            Camera.FrameBoundingSphere(sphere);
            Camera.RotationX = 0;
            Camera.RotationY = 0;
            Camera.UpdateMatrices();
        }

        public void Resize(int width, int height)
        {
            width = Math.Max(width, 1);
            height = Math.Max(height, 1);
            int scale = EffectiveScale(width, height);
            if (width == Width && height == Height && scale == _screenScale)
                return;
            Width = width;
            Height = height;
            _screenScale = scale;
            _screen.Resize(width * scale, height * scale);
            _final.Resize(width, height);
            Context.Width = width;
            Context.Height = height;
            Camera.Width = width;
            Camera.Height = height;
            Camera.UpdateMatrices();
        }

        public int ViewportTextureId => ((GLTexture)_final.Attachments[0]).ID;

        /// <summary>Renders the scene into the final displayable texture.</summary>
        public void Render(IViewScene scene)
        {
            RenderInternal(
                scene,
                _screen,
                _final,
                Width,
                Height,
                EffectiveScale(Width, Height),
                new System.Numerics.Vector4(
                    BackgroundColor.X,
                    BackgroundColor.Y,
                    BackgroundColor.Z,
                    1
                ),
                false,
                _screenDepth
            );
        }

        public static bool DebugTrace;

        void Trace(string stage, int w, int h)
        {
            if (!DebugTrace)
                return;
            var err = GL.GetError();
            var status = GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
            float[] px = new float[4];
            GL.ReadPixels(w / 2, h / 2, 1, 1, PixelFormat.Rgba, PixelType.Float, px);
            Console.WriteLine(
                $"[Pipeline] {stage}: err={err} fbo={status} center=({px[0]:F3},{px[1]:F3},{px[2]:F3},{px[3]:F3}) "
                    + $"camPos=({Camera.GetViewPostion().X:F2},{Camera.GetViewPostion().Y:F2},{Camera.GetViewPostion().Z:F2}) dist={Camera.TargetDistance:F2}"
            );
        }

        void RenderInternal(
            IViewScene scene,
            Framebuffer screen,
            Framebuffer final,
            int width,
            int height,
            int scale,
            System.Numerics.Vector4 background,
            bool keepAlpha,
            DepthTexture screenDepth = null
        )
        {
            int ssWidth = width * scale;
            int ssHeight = height * scale;

            UpdateLightOverride();
            Context.SetActive();
            //The scene renders at the supersampled size; aspect is unchanged.
            Context.Width = ssWidth;
            Context.Height = ssHeight;
            Context.ScreenBuffer = screen; //used by XLU/color-pass materials
            Camera.Width = ssWidth;
            Camera.Height = ssHeight;
            Camera.UpdateMatrices();

            //ImGui's renderer leaves scissor/blend state behind; reset it.
            GL.Disable(EnableCap.ScissorTest);
            GL.Disable(EnableCap.Blend);
            GL.Disable(EnableCap.FramebufferSrgb);
            GL.DepthMask(true);
            GL.ColorMask(true, true, true, true);
            GL.DepthFunc(DepthFunction.Lequal);
            GL.BindVertexArray(0);
            GL.UseProgram(0);
            GL.ActiveTexture(TextureUnit.Texture0);

            if (scene != null)
                foreach (var render in scene.AllRenders())
                    render.OnBeforeDraw(Context);

            bool selfShadow = EnableSelfShadow && scene != null && screenDepth != null;
            if (selfShadow)
            {
                _selfShadow ??= new SelfShadowRenderer();
                _selfShadow.RenderLightDepth(Context, scene, ComputeShadowBounds(scene));
            }

            //Background is authored in sRGB; the scene renders linear.
            float Lin(float v) => (float)Math.Pow(v, 2.2);

            void DrawScenePass()
            {
                screen.Bind();
                GL.Viewport(0, 0, ssWidth, ssHeight);
                GL.ClearColor(
                    Lin(background.X),
                    Lin(background.Y),
                    Lin(background.Z),
                    background.W
                );
                GL.Clear(
                    ClearBufferMask.ColorBufferBit
                        | ClearBufferMask.DepthBufferBit
                        | ClearBufferMask.StencilBufferBit
                );

                //Opaque (viewport / non-alpha) passes preview the export background behind the
                //scene. Transparent capture (keepAlpha) skips it so the alpha oracle is intact.
                if (!keepAlpha && _bgTex != 0)
                    _bgQuad.Draw(Context, _bgTex);

                GL.Enable(EnableCap.DepthTest);

                if (scene != null)
                {
                    scene.Draw(Context, Pass.OPAQUE);

                    bool refract =
                        screenDepth != null && BfresEditor.HoianNXRender.NeedsRefractionBuffers;
                    if (refract)
                    {
                        CaptureRefractionBuffers(screen, screenDepth, ssWidth, ssHeight);
                        screen.Bind();
                        GL.Viewport(0, 0, ssWidth, ssHeight);
                        GL.TextureBarrier();
                    }

                    if (keepAlpha)
                        GL.ColorMask(true, true, true, false);
                    scene.Draw(Context, Pass.TRANSPARENT);
                    if (keepAlpha)
                        GL.ColorMask(true, true, true, true);

                    if (refract)
                    {
                        BfresEditor.HoianNXRender.RefractionColorBuffer = null;
                        BfresEditor.HoianNXRender.RefractionDepthBuffer = null;
                    }
                }
                Context.CurrentShader = null;
                screen.Unbind();
            }

            //First pass fills the scene depth used to build the shadow prepass, the
            //second pass renders with the prepass bound (game shading path).
            DrawScenePass();
            Trace("after scene", ssWidth, ssHeight);

            if (selfShadow)
            {
                var camViewProj = Camera.ModelMatrix * Camera.ViewMatrix * Camera.ProjectionMatrix;
                _selfShadow.GeneratePrepass(Context, screenDepth, camViewProj, ssWidth, ssHeight);

                BfresEditor.HoianNXRender.ShadowPrepassTexture = _selfShadow.PrepassTexture;
                DrawScenePass();
                BfresEditor.HoianNXRender.ShadowPrepassTexture = null;
                Trace("after shadowed scene", ssWidth, ssHeight);
            }

            //Gamma + downsample pass into the display/capture buffer
            final.Bind();
            GL.Viewport(0, 0, width, height);
            GL.ClearColor(0, 0, 0, 0);
            GL.Clear(ClearBufferMask.ColorBufferBit);
            _quad.Draw(Context, (GLTexture)screen.Attachments[0], keepAlpha);
            Trace("after quad", width, height);
            final.Unbind();

            //Restore camera to display size for input math.
            Context.Width = Width;
            Context.Height = Height;
            Camera.Width = Width;
            Camera.Height = Height;
        }

        /// <summary>
        /// Renders a one-off capture at the given resolution. transparent=true clears
        /// alpha 0 and keeps coverage in the output (background rgb still applies to
        /// semi-transparent edges).
        /// </summary>
        public Image<Rgba32> Capture(
            IViewScene scene,
            int width,
            int height,
            System.Numerics.Vector3 background,
            bool transparent,
            int scaleOverride = 0
        )
        {
            int scale = scaleOverride > 0 ? scaleOverride : ScaleFor(width, height);
            var screen = CreateScreenBuffer(width * scale, height * scale, out var screenDepth);
            var final = new Framebuffer(
                FramebufferTarget.Framebuffer,
                width,
                height,
                PixelInternalFormat.Rgba8,
                1
            );
            try
            {
                RenderInternal(
                    scene,
                    screen,
                    final,
                    width,
                    height,
                    scale,
                    new System.Numerics.Vector4(
                        background.X,
                        background.Y,
                        background.Z,
                        transparent ? 0 : 1
                    ),
                    transparent,
                    screenDepth
                );
                final.Bind();
                byte[] pixels = new byte[width * height * 4];
                GL.ReadBuffer(ReadBufferMode.ColorAttachment0);
                GL.ReadPixels(
                    0,
                    0,
                    width,
                    height,
                    PixelFormat.Rgba,
                    PixelType.UnsignedByte,
                    pixels
                );
                final.Unbind();
                return ToImage(pixels, width, height, transparent);
            }
            finally
            {
                screen.Dispoe();
                screenDepth.Dispose();
                final.Dispoe();
                Camera.UpdateMatrices();
            }
        }

        //Wraps bottom-up RGBA8 bytes (OpenGL row order) into a top-down ImageSharp image.
        //When not transparent the alpha channel is forced opaque.
        static Image<Rgba32> ToImage(byte[] rgba, int width, int height, bool transparent)
        {
            if (!transparent)
                for (int i = 3; i < rgba.Length; i += 4)
                    rgba[i] = 255;
            var image = Image.LoadPixelData<Rgba32>(rgba, width, height);
            image.Mutate(x => x.Flip(FlipMode.Vertical));
            return image;
        }

        /// <summary>
        /// Renders one frame at the current viewport size into the display buffers and
        /// returns raw bottom-up RGBA8 bytes (OpenGL row order, ffmpeg-ready).
        /// transparent=true keeps the real alpha channel; otherwise the frame is
        /// composited over <paramref name="background"/> opaquely. The buffer is rented
        /// from <see cref="System.Buffers.ArrayPool{T}"/>; ownership transfers to the caller.
        /// Synchronous, so every frame deterministically maps 1:1.
        /// </summary>
        public byte[] CaptureFrameBytes(
            IViewScene scene,
            System.Numerics.Vector3 background,
            bool transparent,
            out int width,
            out int height
        )
        {
            width = Width;
            height = Height;
            RenderInternal(
                scene,
                _screen,
                _final,
                Width,
                Height,
                EffectiveScale(Width, Height),
                new System.Numerics.Vector4(
                    background.X,
                    background.Y,
                    background.Z,
                    transparent ? 0 : 1
                ),
                transparent,
                _screenDepth
            );

            int size = Width * Height * 4;
            var buf = System.Buffers.ArrayPool<byte>.Shared.Rent(size);
            _final.Bind();
            GL.ReadBuffer(ReadBufferMode.ColorAttachment0);
            GL.ReadPixels(0, 0, Width, Height, PixelFormat.Rgba, PixelType.UnsignedByte, buf);
            _final.Unbind();
            return buf;
        }

        /// <summary>
        /// Uploads a full-frame straight-RGBA buffer as the live background preview (drawn behind
        /// the scene in opaque passes). Pass null to clear it (Transparent mode).
        /// </summary>
        public void SetBackgroundBuffer(byte[] rgba, int w, int h)
        {
            if (rgba == null || w <= 0 || h <= 0)
            {
                if (_bgTex != 0)
                {
                    GL.DeleteTexture(_bgTex);
                    _bgTex = 0;
                }
                return;
            }
            if (_bgTex == 0)
                _bgTex = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, _bgTex);
            GL.TexImage2D(
                TextureTarget.Texture2D,
                0,
                PixelInternalFormat.Rgba,
                w,
                h,
                0,
                PixelFormat.Rgba,
                PixelType.UnsignedByte,
                rgba
            );
            GL.TexParameter(
                TextureTarget.Texture2D,
                TextureParameterName.TextureMinFilter,
                (int)TextureMinFilter.Linear
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
            GL.BindTexture(TextureTarget.Texture2D, 0);
        }

        public void Dispose()
        {
            DisposeRefraction();
            if (_bgTex != 0)
            {
                GL.DeleteTexture(_bgTex);
                _bgTex = 0;
            }
            _screen?.Dispoe();
            _final?.Dispoe();
            _selfShadow?.Dispose();
        }
    }

    /// <summary>Fullscreen quad that draws the background texture (sRGB) into the linear scene
    /// buffer, linearizing so the gamma pass restores it; transparent texels are discarded so
    /// letterbox/off-frame areas keep the clear color.</summary>
    class BackgroundQuad
    {
        ShaderProgram _shader;
        VertexBufferObject _vao;

        const string Vert =
            "#version 330\n"
            + "layout (location = 0) in vec2 aPos;\n"
            + "layout (location = 1) in vec2 aTexCoords;\n"
            + "out vec2 TexCoords;\n"
            + "void main(){ gl_Position = vec4(aPos, 0.0, 1.0); TexCoords = aTexCoords; }\n";

        const string Frag =
            "#version 330\n"
            + "precision highp float;\n"
            + "in vec2 TexCoords;\n"
            + "uniform sampler2D uTex;\n"
            + "out vec4 FragColor;\n"
            + "void main(){\n"
            + "  vec4 c = texture(uTex, TexCoords);\n"
            + "  if (c.a < 0.004) discard;\n"
            + "  FragColor = vec4(pow(c.rgb, vec3(2.2)), 1.0);\n"
            + "}\n";

        void Init()
        {
            if (_shader != null)
                return;
            _shader = new ShaderProgram(new FragmentShader(Frag), new VertexShader(Vert));

            int buffer = GL.GenBuffer();
            _vao = new VertexBufferObject(buffer);
            _vao.AddAttribute(0, 2, VertexAttribPointerType.Float, false, 16, 0);
            _vao.AddAttribute(1, 2, VertexAttribPointerType.Float, false, 16, 8);
            _vao.Initialize();

            float[] data = { -1, 1, 0, 1, -1, -1, 0, 0, 1, 1, 1, 1, 1, -1, 1, 0 };
            GL.BufferData(
                BufferTarget.ArrayBuffer,
                sizeof(float) * data.Length,
                data,
                BufferUsageHint.StaticDraw
            );
        }

        public void Draw(GLContext context, int tex)
        {
            Init();
            GL.Disable(EnableCap.Blend);
            GL.Disable(EnableCap.CullFace);
            GL.Disable(EnableCap.DepthTest);
            GL.DepthMask(false);

            context.CurrentShader = _shader;
            GL.ActiveTexture(TextureUnit.Texture0);
            GL.BindTexture(TextureTarget.Texture2D, tex);
            _shader.SetInt("uTex", 0);

            _vao.Enable(_shader);
            _vao.Use();
            GL.DrawArrays(PrimitiveType.TriangleStrip, 0, 4);
            GL.BindTexture(TextureTarget.Texture2D, 0);
            GL.UseProgram(0);
            GL.DepthMask(true);
        }
    }

    /// <summary>
    /// Fullscreen quad running the FinalHDR gamma shader (linear -> sRGB), with
    /// optional alpha passthrough for transparent captures.
    /// </summary>
    class FinalQuad
    {
        ShaderProgram _shader;
        VertexBufferObject _vao;

        void Init()
        {
            if (_shader != null)
                return;
            string frag = System.IO.File.ReadAllText("Shaders/FinalHDR.frag");
            string vert = System.IO.File.ReadAllText("Shaders/FinalHDR.vert");
            _shader = new ShaderProgram(new FragmentShader(frag), new VertexShader(vert));

            int buffer = GL.GenBuffer();
            _vao = new VertexBufferObject(buffer);
            _vao.AddAttribute(0, 2, VertexAttribPointerType.Float, false, 16, 0);
            _vao.AddAttribute(1, 2, VertexAttribPointerType.Float, false, 16, 8);
            _vao.Initialize();

            float[] data = { -1, 1, 0, 1, -1, -1, 0, 0, 1, 1, 1, 1, 1, -1, 1, 0 };
            GL.BufferData(
                BufferTarget.ArrayBuffer,
                sizeof(float) * data.Length,
                data,
                BufferUsageHint.StaticDraw
            );
        }

        public void Draw(GLContext context, GLTexture color, bool keepAlpha)
        {
            Init();
            GL.Disable(EnableCap.Blend);
            GL.Disable(EnableCap.CullFace);
            GL.Disable(EnableCap.DepthTest);

            context.CurrentShader = _shader;
            _shader.SetInt("ENABLE_BLOOM", 0);
            _shader.SetInt("ENABLE_LUT", 0);
            _shader.SetInt("ENABLE_SRGB", 1);
            _shader.SetInt("ENABLE_FBO_ALPHA", keepAlpha ? 1 : 0);

            GL.ActiveTexture(TextureUnit.Texture1);
            color.Bind();
            //Linear filtering does the supersample downsample.
            GL.TexParameter(
                TextureTarget.Texture2D,
                TextureParameterName.TextureMinFilter,
                (int)TextureMinFilter.Linear
            );
            GL.TexParameter(
                TextureTarget.Texture2D,
                TextureParameterName.TextureMagFilter,
                (int)TextureMagFilter.Linear
            );
            _shader.SetInt("uColorTex", 1);

            _vao.Enable(_shader);
            _vao.Use();
            GL.DrawArrays(PrimitiveType.TriangleStrip, 0, 4);
            GL.BindTexture(TextureTarget.Texture2D, 0);
            GL.UseProgram(0);
            GL.Enable(EnableCap.DepthTest);
        }
    }
}
