using System;
using OpenTK.Graphics.OpenGL;

namespace PlayerViewer.Effects.Gl
{
    /// <summary>A particle primitive's vertex and index buffers on the GPU, in the stored formats.</summary>
    public sealed class GlMesh : IDisposable
    {
        readonly EffectMesh _mesh;
        readonly int[] _buffers;
        readonly int _indices;

        public int IndexCount { get; }

        public GlMesh(EffectMesh mesh)
        {
            _mesh = mesh;
            _buffers = new int[mesh.Buffers.Count];
            GL.GenBuffers(_buffers.Length, _buffers);
            for (int i = 0; i < _buffers.Length; i++)
            {
                GL.BindBuffer(BufferTarget.ArrayBuffer, _buffers[i]);
                GL.BufferData(
                    BufferTarget.ArrayBuffer,
                    mesh.Buffers[i].Length,
                    mesh.Buffers[i],
                    BufferUsageHint.StaticDraw
                );
            }
            GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
            _indices = GL.GenBuffer();
            IndexCount = mesh.Indices.Length;
            GL.BindBuffer(BufferTarget.ElementArrayBuffer, _indices);
            GL.BufferData(
                BufferTarget.ElementArrayBuffer,
                mesh.Indices.Length * 4,
                mesh.Indices,
                BufferUsageHint.StaticDraw
            );
            GL.BindBuffer(BufferTarget.ElementArrayBuffer, 0);
        }

        /// <summary>Points the program's mesh inputs at the buffers and binds the indices, into
        /// the bound vertex array.</summary>
        public void Bind(EmitterProgram program)
        {
            foreach (var a in _mesh.Attributes)
            {
                int location = program.Attribute(a.Name);
                if (location < 0)
                    continue;
                GL.BindBuffer(BufferTarget.ArrayBuffer, _buffers[a.Buffer]);
                GL.EnableVertexAttribArray(location);
                var (type, normalized, size) = a.Type switch
                {
                    VertexComponentType.Float => (
                        VertexAttribPointerType.Float,
                        false,
                        a.Components
                    ),
                    VertexComponentType.Half => (
                        VertexAttribPointerType.HalfFloat,
                        false,
                        a.Components
                    ),
                    VertexComponentType.UNorm8 => (
                        VertexAttribPointerType.UnsignedByte,
                        true,
                        a.Components
                    ),
                    VertexComponentType.SNorm8 => (
                        VertexAttribPointerType.Byte,
                        true,
                        a.Components
                    ),
                    VertexComponentType.UNorm16 => (
                        VertexAttribPointerType.UnsignedShort,
                        true,
                        a.Components
                    ),
                    VertexComponentType.SNorm16 => (
                        VertexAttribPointerType.Short,
                        true,
                        a.Components
                    ),
                    VertexComponentType.SNorm10_10_10_2 => (
                        VertexAttribPointerType.Int2101010Rev,
                        true,
                        4
                    ),
                    _ => (VertexAttribPointerType.UnsignedInt2101010Rev, true, 4),
                };
                GL.VertexAttribPointer(
                    location,
                    size,
                    type,
                    normalized,
                    _mesh.Strides[a.Buffer],
                    a.Offset
                );
                GL.VertexAttribDivisor(location, 0);
            }
            GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
            GL.BindBuffer(BufferTarget.ElementArrayBuffer, _indices);
        }

        public void Dispose()
        {
            GL.DeleteBuffers(_buffers.Length, _buffers);
            GL.DeleteBuffer(_indices);
        }
    }
}
