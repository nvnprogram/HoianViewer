using ImGuiNET;
using OpenTK;
using OpenTK.Graphics.OpenGL;
using OpenTK.Input;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace CafeStudio.UI
{
    /// <summary>
    /// A modified version of Veldrid.ImGui's ImGuiRenderer.
    /// Manages input for ImGui and handles rendering ImGui's DrawLists with Veldrid.
    /// </summary>
    public partial class ImGuiController : IDisposable
    {
        public static bool ApplicationHasFocus = true;

        private bool _frameBegun;

        // Veldrid objects
        private int _vertexArray;
        private int _vertexBuffer;
        private int _vertexBufferSize;
        private int _indexBuffer;
        private int _indexBufferSize;

        private Texture _fontTexture;
        private Shader _shader;

        private int _windowWidth;
        private int _windowHeight;

        private System.Numerics.Vector2 _scaleFactor = System.Numerics.Vector2.One;

        public static ImFontPtr DefaultFont;

        /// <summary>
        /// Constructs a new ImGuiController.
        /// </summary>
        public ImGuiController(int width, int height)
        {
            _windowWidth = width;
            _windowHeight = height;

            var context = ImGui.CreateContext();

            ImGui.SetCurrentContext(context);
            var io = ImGui.GetIO();
            io.ConfigFlags |= ImGuiConfigFlags.DockingEnable;
            unsafe
            {
                io.NativePtr->IniFilename = null;
            }

            AddFonts(io);

            io.BackendFlags |= ImGuiBackendFlags.RendererHasVtxOffset;

            CreateDeviceResources();
            SetKeyMappings();

            SetPerFrameImGuiData(1f / 60f);

            ImGui.NewFrame();
            _frameBegun = true;
        }

        /// <summary>
        /// Runs after the built in fonts are added, for fonts of the app's own. The main font
        /// stays the first, so it stays the default.
        /// </summary>
        public static Action<ImFontAtlasPtr> ExtraFonts;

        /// <summary>
        /// Builds the atlas again, picking up a changed <see cref="ExtraFonts"/>. Call it
        /// between frames: the atlas is locked from NewFrame until Render.
        /// </summary>
        public void RebuildFonts()
        {
            var io = ImGui.GetIO();
            io.Fonts.Clear();
            AddFonts(io);
            _fontTexture?.Dispose();
            RecreateFontDeviceTexture();
        }

        static void AddFonts(ImGuiIOPtr io)
        {
            //Load the main font file
            unsafe
            {
                var nativeConfig = ImGuiNative.ImFontConfig_ImFontConfig();
                //Add a higher horizontal/vertical sample rate for global scaling.
                (*nativeConfig).OversampleH = 8;
                (*nativeConfig).OversampleV = 8;
                (*nativeConfig).RasterizerMultiply = 1f;
                (*nativeConfig).GlyphOffset = new System.Numerics.Vector2(0);

                io.Fonts.AddFontFromFileTTF("Lib/Font.ttf", 16, nativeConfig);
            }

            //Merge icon fonts. Important that this goes after the main target font being used so it can be merged.
            ImFontConfig config = new ImFontConfig
            {
                MergeMode = 1,
                PixelSnapH = 1,
            };
            char min = Convert.ToChar(57344);
            char max = Convert.ToChar(63743);

            AddFontFromFileTTF("Lib/OpenFontIcons.ttf", 16, config, new[] { min, max, (char)0 });

            //Store the default font for monospaced UI (ie hex viewer)
            DefaultFont = io.Fonts.AddFontDefault();

            ExtraFonts?.Invoke(io.Fonts);
        }

        public static void AddFontFromFileTTF(string filename,
         float sizePixels,
         ImFontConfig config,
         char[] glyphRanges)
        {
            ImGuiIOPtr io = ImGui.GetIO();
            config.OversampleH = 1;
            config.OversampleV = 1;
            config.RasterizerMultiply = 1;

            unsafe
            {
                fixed (char* glyphs = &glyphRanges[0]) {
                    io.Fonts.AddFontFromFileTTF(filename, sizePixels, &config, (IntPtr)glyphs);
                }
            }
        }

        public void WindowResized(int width, int height)
        {
            _windowWidth = width;
            _windowHeight = height;
        }

        public void DestroyDeviceObjects()
        {
            Dispose();
        }

        public void CreateDeviceResources()
        {
            Util.CreateVertexArray("ImGui", out _vertexArray);

            _vertexBufferSize = 10000;
            _indexBufferSize = 2000;

            Util.CreateVertexBuffer("ImGui", out _vertexBuffer);
            Util.CreateElementBuffer("ImGui", out _indexBuffer);

            GL.BindBuffer(BufferTarget.ArrayBuffer, _vertexBuffer);
            GL.BufferData(BufferTarget.ArrayBuffer, _vertexBufferSize, IntPtr.Zero, BufferUsageHint.DynamicDraw);

            GL.BindBuffer(BufferTarget.ElementArrayBuffer, _indexBuffer);
            GL.BufferData(BufferTarget.ElementArrayBuffer, _indexBufferSize, IntPtr.Zero, BufferUsageHint.DynamicDraw);

            RecreateFontDeviceTexture();

            string VertexSource = @"#version 330 core

uniform mat4 projection_matrix;

layout(location = 0) in vec2 in_position;
layout(location = 1) in vec2 in_texCoord;
layout(location = 2) in vec4 in_color;

out vec4 color;
out vec2 texCoord;

void main()
{
    gl_Position = projection_matrix * vec4(in_position, 0, 1);
    color = in_color;
    texCoord = in_texCoord;
}";
            string FragmentSource = @"#version 330 core

uniform sampler2D in_fontTexture;

in vec4 color;
in vec2 texCoord;

out vec4 outputColor;

void main()
{
    outputColor = color * texture(in_fontTexture, texCoord);
}";

            _shader = new Shader("ImGui", VertexSource, FragmentSource);

            GL.BindVertexArray(_vertexArray);
            GL.BindBuffer(BufferTarget.ArrayBuffer, _vertexBuffer);

            //Bind index buffer
            GL.BindBuffer(BufferTarget.ArrayBuffer, _vertexBuffer);
            GL.BindBuffer(BufferTarget.ElementArrayBuffer, _indexBuffer);

            var stride = Unsafe.SizeOf<ImDrawVert>();

            //Bind vertex buffer
            GL.BindVertexArray(_vertexArray);
            GL.BindBuffer(BufferTarget.ArrayBuffer, _vertexBuffer);

            GL.EnableVertexAttribArray(0);
            GL.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, stride, 0);
            GL.EnableVertexAttribArray(1);
            GL.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, stride, 8);
            GL.EnableVertexAttribArray(2);
            GL.VertexAttribPointer(2, 4, VertexAttribPointerType.UnsignedByte, true, stride, 16);

            Util.CheckGLError("End of ImGui setup");
        }

        /// <summary>
        /// Recreates the device texture used to render text.
        /// </summary>
        public void RecreateFontDeviceTexture()
        {
            ImGuiIOPtr io = ImGui.GetIO();
            io.Fonts.GetTexDataAsRGBA32(out IntPtr pixels, out int width, out int height, out int bytesPerPixel);

            _fontTexture = new Texture("ImGui Text Atlas", width, height, pixels);
            _fontTexture.SetMagFilter(TextureMagFilter.Linear);
            _fontTexture.SetMinFilter(TextureMinFilter.Linear);

            io.Fonts.SetTexID((IntPtr)_fontTexture.GLTexture);

            io.Fonts.ClearTexData();
        }

        /// <summary>
        /// Renders the ImGui draw list data.
        /// This method requires a <see cref="GraphicsDevice"/> because it may create new DeviceBuffers if the size of vertex
        /// or index data has increased beyond the capacity of the existing buffers.
        /// A <see cref="CommandList"/> is needed to submit drawing and resource update commands.
        /// </summary>
        public void Render()
        {
            if (_frameBegun)
            {
                _frameBegun = false;
                ImGui.Render();
                RenderImDrawData(ImGui.GetDrawData());
            }
        }

        /// <summary>
        /// Updates ImGui input and IO configuration state.
        /// </summary>
        public void Update(GameWindow wnd, float deltaSeconds)
        {
            if (_frameBegun) {
                ImGui.Render();
            }

            ApplicationHasFocus = wnd.Focused;

            SetPerFrameImGuiData(deltaSeconds);
            UpdateImGuiInput(wnd);
            TestInput(ImGui.GetIO());

            _frameBegun = true;
            ImGui.NewFrame();
        }

        /// <summary>
        /// Sets per-frame data based on the associated window.
        /// This is called by Update(float).
        /// </summary>
        private void SetPerFrameImGuiData(float deltaSeconds)
        {
            ImGuiIOPtr io = ImGui.GetIO();
            io.DisplaySize = new System.Numerics.Vector2(
                _windowWidth / _scaleFactor.X,
                _windowHeight / _scaleFactor.Y);
            io.DisplayFramebufferScale = _scaleFactor;
            io.DeltaTime = deltaSeconds; // DeltaTime is in seconds.
        }

        System.Numerics.Vector2 _wheel, _scrollTotal;
        readonly List<char> PressedChars = new List<char>();

        readonly HashSet<Key> KeysHeld = new HashSet<Key>();
        static readonly Key[] AllKeys = (Key[])Enum.GetValues(typeof(Key));

        public void KeyDown(Key key) => KeysHeld.Add(key);

        public void KeyUp(Key key) => KeysHeld.Remove(key);

        /// <summary>
        /// A wheel event of the window, with the window's running scroll total. The OS sends the
        /// wheel only to the window it scrolls, so a window lying over this one keeps its own.
        /// </summary>
        public void MouseWheel(float totalX, float totalY)
        {
            var total = new System.Numerics.Vector2(totalX, totalY);
            _wheel += total - _scrollTotal;
            _scrollTotal = total;
        }

        /// <summary>
        /// Drops the key state and typing from while the render loop was not running, on the next
        /// frame. A modal native dialog blocks this thread and takes the key releases meant for us.
        /// </summary>
        public static bool DiscardPendingInput;

        /// <summary>Runs after the window's input is read, before the frame, in a test build only.</summary>
        partial void TestInput(ImGuiIOPtr io);

        private void UpdateImGuiInput(GameWindow wnd)
        {
            ImGuiIOPtr io = ImGui.GetIO();

            if (!wnd.Focused)
            {
                _wheel = default;
                io.MouseWheel = 0;
                io.MouseWheelH = 0;
                ClearKeys(io);
                DiscardPendingInput = false;
                return;
            }

            MouseState MouseState = Mouse.GetCursorState();

            bool discard = DiscardPendingInput;
            if (discard)
            {
                DiscardPendingInput = false;
                _wheel = default;
                PressedChars.Clear();
            }

            io.MouseDown[0] = MouseState.LeftButton == ButtonState.Pressed;
            io.MouseDown[1] = MouseState.RightButton == ButtonState.Pressed;
            io.MouseDown[2] = MouseState.MiddleButton == ButtonState.Pressed;

            var screenPoint = new System.Drawing.Point(MouseState.X, MouseState.Y);
            var point = wnd.PointToClient(screenPoint);
            io.MousePos = new System.Numerics.Vector2(point.X, point.Y);

            io.MouseWheel = _wheel.Y;
            io.MouseWheelH = _wheel.X;
            _wheel = default;

            if (discard)
                ClearKeys(io);
            else
                foreach (Key key in AllKeys)
                {
                    io.KeysDown[(int)key] = KeysHeld.Contains(key);
                }

            foreach (var c in PressedChars)
            {
                io.AddInputCharacter(c);
            }
            PressedChars.Clear();

            io.KeyCtrl = !discard && (KeysHeld.Contains(Key.ControlLeft) || KeysHeld.Contains(Key.ControlRight));
            io.KeyAlt = !discard && (KeysHeld.Contains(Key.AltLeft) || KeysHeld.Contains(Key.AltRight));
            io.KeyShift = !discard && (KeysHeld.Contains(Key.ShiftLeft) || KeysHeld.Contains(Key.ShiftRight));
            io.KeySuper = !discard && (KeysHeld.Contains(Key.WinLeft) || KeysHeld.Contains(Key.WinRight));

        }

        void ClearKeys(ImGuiIOPtr io)
        {
            KeysHeld.Clear();
            foreach (Key key in AllKeys)
                io.KeysDown[(int)key] = false;
            io.KeyCtrl = io.KeyAlt = io.KeyShift = io.KeySuper = false;
        }

        public void PressChar(char keyChar)
        {
            PressedChars.Add(keyChar);
        }

        private static void SetKeyMappings()
        {
            ImGuiIOPtr io = ImGui.GetIO();
            io.KeyMap[(int)ImGuiKey.Tab] = (int)Key.Tab;
            io.KeyMap[(int)ImGuiKey.LeftArrow] = (int)Key.Left;
            io.KeyMap[(int)ImGuiKey.RightArrow] = (int)Key.Right;
            io.KeyMap[(int)ImGuiKey.UpArrow] = (int)Key.Up;
            io.KeyMap[(int)ImGuiKey.DownArrow] = (int)Key.Down;
            io.KeyMap[(int)ImGuiKey.PageUp] = (int)Key.PageUp;
            io.KeyMap[(int)ImGuiKey.PageDown] = (int)Key.PageDown;
            io.KeyMap[(int)ImGuiKey.Home] = (int)Key.Home;
            io.KeyMap[(int)ImGuiKey.End] = (int)Key.End;
            io.KeyMap[(int)ImGuiKey.Delete] = (int)Key.Delete;
            io.KeyMap[(int)ImGuiKey.Backspace] = (int)Key.BackSpace;
            io.KeyMap[(int)ImGuiKey.Enter] = (int)Key.Enter;
            io.KeyMap[(int)ImGuiKey.Escape] = (int)Key.Escape;
            io.KeyMap[(int)ImGuiKey.A] = (int)Key.A;
            io.KeyMap[(int)ImGuiKey.C] = (int)Key.C;
            io.KeyMap[(int)ImGuiKey.V] = (int)Key.V;
            io.KeyMap[(int)ImGuiKey.X] = (int)Key.X;
            io.KeyMap[(int)ImGuiKey.Y] = (int)Key.Y;
            io.KeyMap[(int)ImGuiKey.Z] = (int)Key.Z;
        }

        /// <summary>
        /// GL programs drawn in place of the default for commands whose texture is a key here. They
        /// take the default's vertex inputs and its projection_matrix uniform.
        /// </summary>
        public static readonly Dictionary<IntPtr, int> TexturePrograms = new Dictionary<IntPtr, int>();

        private void RenderImDrawData(ImDrawDataPtr draw_data)
        {
            if (draw_data.CmdListsCount == 0)
            {
                return;
            }

            //Every list goes into one buffer at its own offset, the buffer orphaned once a frame,
            //so no upload waits on the draws of the list before it.
            int vertexSize = draw_data.TotalVtxCount * Unsafe.SizeOf<ImDrawVert>();
            int indexSize = draw_data.TotalIdxCount * sizeof(ushort);
            if (vertexSize > _vertexBufferSize)
                _vertexBufferSize = (int)Math.Max(_vertexBufferSize * 1.5f, vertexSize);
            if (indexSize > _indexBufferSize)
                _indexBufferSize = (int)Math.Max(_indexBufferSize * 1.5f, indexSize);

            GL.BindVertexArray(_vertexArray);
            GL.BindBuffer(BufferTarget.ArrayBuffer, _vertexBuffer);
            GL.BufferData(BufferTarget.ArrayBuffer, _vertexBufferSize, IntPtr.Zero, BufferUsageHint.StreamDraw);
            GL.BindBuffer(BufferTarget.ElementArrayBuffer, _indexBuffer);
            GL.BufferData(BufferTarget.ElementArrayBuffer, _indexBufferSize, IntPtr.Zero, BufferUsageHint.StreamDraw);

            int vertexBytes = 0,
                indexBytes = 0;
            for (int i = 0; i < draw_data.CmdListsCount; i++)
            {
                ImDrawListPtr cmd_list = draw_data.CmdListsRange[i];
                int vtx = cmd_list.VtxBuffer.Size * Unsafe.SizeOf<ImDrawVert>();
                int idx = cmd_list.IdxBuffer.Size * sizeof(ushort);
                GL.BufferSubData(BufferTarget.ArrayBuffer, (IntPtr)vertexBytes, vtx, cmd_list.VtxBuffer.Data);
                GL.BufferSubData(BufferTarget.ElementArrayBuffer, (IntPtr)indexBytes, idx, cmd_list.IdxBuffer.Data);
                vertexBytes += vtx;
                indexBytes += idx;
            }
            Util.CheckGLError("Data");

            // Setup orthographic projection matrix into our constant buffer
            ImGuiIOPtr io = ImGui.GetIO();
            Matrix4 mvp = Matrix4.CreateOrthographicOffCenter(
                0.0f,
                io.DisplaySize.X,
                io.DisplaySize.Y,
                0.0f,
                -1.0f,
                1.0f);

            _shader.UseShader();
            GL.UniformMatrix4(_shader.GetUniformLocation("projection_matrix"), false, ref mvp);
            GL.Uniform1(_shader.GetUniformLocation("in_fontTexture"), 0);
            Util.CheckGLError("Projection");

            GL.BindVertexArray(_vertexArray);
            Util.CheckGLError("VAO");

            draw_data.ScaleClipRects(io.DisplayFramebufferScale);

            GL.Enable(EnableCap.Blend);
            GL.Enable(EnableCap.ScissorTest);
            GL.BlendEquation(BlendEquationMode.FuncAdd);
            GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            GL.Disable(EnableCap.CullFace);
            GL.Disable(EnableCap.DepthTest);
            Util.CheckGLError($"Render state");

            GL.ActiveTexture(TextureUnit.Texture0);
            int listVertices = 0,
                listIndices = 0;
            IntPtr boundTexture = (IntPtr)(-1);
            int boundProgram = _shader.Program;
            for (int n = 0; n < draw_data.CmdListsCount; n++)
            {
                ImDrawListPtr cmd_list = draw_data.CmdListsRange[n];

                for (int cmd_i = 0; cmd_i < cmd_list.CmdBuffer.Size; cmd_i++)
                {
                    ImDrawCmdPtr pcmd = cmd_list.CmdBuffer[cmd_i];
                    if (pcmd.UserCallback != IntPtr.Zero)
                    {
                        throw new NotImplementedException();
                    }
                    if (pcmd.ElemCount == 0)
                        continue;

                    if (pcmd.TextureId != boundTexture)
                    {
                        GL.BindTexture(TextureTarget.Texture2D, (int)pcmd.TextureId);
                        boundTexture = pcmd.TextureId;
                        int program = TexturePrograms.TryGetValue(pcmd.TextureId, out int custom) ? custom : _shader.Program;
                        if (program != boundProgram)
                        {
                            GL.UseProgram(program);
                            if (program != _shader.Program)
                                GL.UniformMatrix4(GL.GetUniformLocation(program, "projection_matrix"), false, ref mvp);
                            boundProgram = program;
                        }
                    }

                    // We do _windowHeight - (int)clip.W instead of (int)clip.Y because gl has flipped Y when it comes to these coordinates
                    var clip = pcmd.ClipRect;
                    GL.Scissor((int)clip.X, _windowHeight - (int)clip.W, (int)(clip.Z - clip.X), (int)(clip.W - clip.Y));

                    GL.DrawElementsBaseVertex(
                        PrimitiveType.Triangles,
                        (int)pcmd.ElemCount,
                        DrawElementsType.UnsignedShort,
                        (IntPtr)((listIndices + (int)pcmd.IdxOffset) * sizeof(ushort)),
                        listVertices + (int)pcmd.VtxOffset
                    );
                    Util.CheckGLError("Draw");
                }
                listVertices += cmd_list.VtxBuffer.Size;
                listIndices += cmd_list.IdxBuffer.Size;
            }

            GL.ActiveTexture(TextureUnit.Texture0);
            GL.BindTexture(TextureTarget.Texture2D, 0);

            GL.Disable(EnableCap.Blend);
            GL.Disable(EnableCap.ScissorTest);

            GL.BindVertexArray(0);
        }

        /// <summary>
        /// Frees all graphics resources used by the renderer.
        /// </summary>
        public void Dispose()
        {
            _fontTexture.Dispose();
            _shader.Dispose();
        }
    }
}
