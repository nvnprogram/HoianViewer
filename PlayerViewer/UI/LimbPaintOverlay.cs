using System;
using System.Collections.Generic;
using GLFrameworkEngine;
using OpenTK;
using OpenTK.Graphics.OpenGL;
using PlayerViewer.Rigging;

namespace PlayerViewer.UI
{
    /// <summary>
    /// The painted limbs drawn over the resolved viewport image: the welded surface's triangles
    /// with a colour per node, depth compared against the scene and blended as the weight overlay
    /// is, and the brush lighting the surface it reaches.
    /// </summary>
    public sealed class LimbPaintOverlay : IDisposable
    {
        ShaderProgram _shader;
        int _vao,
            _positions,
            _colours,
            _reach,
            _indices,
            _indexCount;
        MeshGraph _uploaded;
        float[] _colourData;
        bool _coloursDirty;
        float[] _reachData;
        bool _reachDirty;

        /// <summary>Rest model space to the displayed space.</summary>
        public Matrix4 Model = Matrix4.Identity;

        /// <summary>The brush centre in rest model space and its radius; a radius of 0 draws none.</summary>
        public Vector4 Brush;
        public Vector3 BrushColour = Vector3.One;

        /// <summary>The brush lights by <see cref="SetReach"/> rather than as a sphere.</summary>
        public bool SurfaceBrush;

        public MeshGraph Graph { get; private set; }

        public void SetGraph(MeshGraph graph)
        {
            if (Graph == graph)
                return;
            Graph = graph;
            _colourData = new float[graph.Nodes.Count * 4];
            _coloursDirty = true;
            _reachData = new float[graph.Nodes.Count];
            Array.Fill(_reachData, 2f);
            _reachDirty = true;
        }

        /// <summary>Sets every node's premultiplied colour and opacity; nodes left out are unpainted.</summary>
        public void SetColours(IReadOnlyDictionary<int, Vector4> colours)
        {
            if (_colourData == null)
                return;
            Array.Clear(_colourData);
            foreach (var (node, c) in colours)
            {
                if (node < 0 || node * 4 + 3 >= _colourData.Length)
                    continue;
                _colourData[node * 4] = c.X;
                _colourData[node * 4 + 1] = c.Y;
                _colourData[node * 4 + 2] = c.Z;
                _colourData[node * 4 + 3] = c.W;
            }
            _coloursDirty = true;
        }

        /// <summary>Each node's distance along the surface from the surface brush over its radius; null for none in reach.</summary>
        public void SetReach(IReadOnlyDictionary<int, float> reach)
        {
            if (_reachData == null)
                return;
            Array.Fill(_reachData, 2f);
            if (reach != null)
                foreach (var (node, r) in reach)
                    if (node >= 0 && node < _reachData.Length)
                        _reachData[node] = r;
            _reachDirty = true;
        }

        void Upload()
        {
            if (_vao == 0)
            {
                _vao = GL.GenVertexArray();
                _positions = GL.GenBuffer();
                _colours = GL.GenBuffer();
                _reach = GL.GenBuffer();
                _indices = GL.GenBuffer();
            }
            GL.BindVertexArray(_vao);
            if (_uploaded != Graph)
            {
                var positions = new float[Graph.Nodes.Count * 3];
                for (int i = 0; i < Graph.Nodes.Count; i++)
                {
                    positions[i * 3] = Graph.Nodes[i].X;
                    positions[i * 3 + 1] = Graph.Nodes[i].Y;
                    positions[i * 3 + 2] = Graph.Nodes[i].Z;
                }
                GL.BindBuffer(BufferTarget.ArrayBuffer, _positions);
                GL.BufferData(
                    BufferTarget.ArrayBuffer,
                    positions.Length * 4,
                    positions,
                    BufferUsageHint.StaticDraw
                );
                GL.EnableVertexAttribArray(0);
                GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 12, 0);

                //A two sided card welds into the same triangle twice, and blending it twice would darken it.
                var seen = new HashSet<(int, int, int)>();
                var indices = new List<int>(Graph.Triangles.Count * 3);
                foreach (var (a, b, c) in Graph.Triangles)
                {
                    int lo = Math.Min(a, Math.Min(b, c)),
                        hi = Math.Max(a, Math.Max(b, c));
                    if (!seen.Add((lo, a + b + c - lo - hi, hi)))
                        continue;
                    indices.Add(a);
                    indices.Add(b);
                    indices.Add(c);
                }
                GL.BindBuffer(BufferTarget.ElementArrayBuffer, _indices);
                GL.BufferData(
                    BufferTarget.ElementArrayBuffer,
                    indices.Count * 4,
                    indices.ToArray(),
                    BufferUsageHint.StaticDraw
                );
                _indexCount = indices.Count;
                _uploaded = Graph;
                _coloursDirty = true;
                _reachDirty = true;
            }
            if (_coloursDirty)
            {
                GL.BindBuffer(BufferTarget.ArrayBuffer, _colours);
                GL.BufferData(
                    BufferTarget.ArrayBuffer,
                    _colourData.Length * 4,
                    _colourData,
                    BufferUsageHint.DynamicDraw
                );
                GL.EnableVertexAttribArray(1);
                GL.VertexAttribPointer(1, 4, VertexAttribPointerType.Float, false, 16, 0);
                _coloursDirty = false;
            }
            if (_reachDirty)
            {
                GL.BindBuffer(BufferTarget.ArrayBuffer, _reach);
                GL.BufferData(
                    BufferTarget.ArrayBuffer,
                    _reachData.Length * 4,
                    _reachData,
                    BufferUsageHint.DynamicDraw
                );
                GL.EnableVertexAttribArray(2);
                GL.VertexAttribPointer(2, 1, VertexAttribPointerType.Float, false, 4, 0);
                _reachDirty = false;
            }
        }

        /// <summary>Draws into the bound display framebuffer. The camera must still hold the matrices the scene was drawn with.</summary>
        public void Draw(GLContext context, DepthTexture sceneDepth, int scale)
        {
            if (Graph == null || Graph.Triangles.Count == 0)
                return;
            _shader ??= new ShaderProgram(
                new FragmentShader(UiShaders.Load("LimbPaint.frag")),
                new VertexShader(UiShaders.Load("LimbPaint.vert"))
            );
            Upload();
            OverlayPass.Begin(context, _shader, sceneDepth, scale, blend: true);
            var mtxCam = context.Camera.ViewProjectionMatrix;
            _shader.SetMatrix4x4("mtxCam", ref mtxCam);
            var model = Model;
            _shader.SetMatrix4x4("mtxMdl", ref model);
            _shader.SetVector4("uBrush", Brush);
            _shader.SetVector3("uBrushColour", BrushColour);
            _shader.SetInt("uSurface", SurfaceBrush ? 1 : 0);

            GL.BindVertexArray(_vao);
            GL.DrawElements(PrimitiveType.Triangles, _indexCount, DrawElementsType.UnsignedInt, 0);
            OverlayPass.End();
        }

        public void Dispose()
        {
            _shader?.Dispose();
            _shader = null;
            if (_vao != 0)
            {
                GL.DeleteVertexArray(_vao);
                GL.DeleteBuffer(_positions);
                GL.DeleteBuffer(_colours);
                GL.DeleteBuffer(_reach);
                GL.DeleteBuffer(_indices);
                _vao = 0;
            }
            _uploaded = null;
        }
    }
}
