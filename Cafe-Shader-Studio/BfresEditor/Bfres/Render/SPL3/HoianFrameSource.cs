using System;
using System.Buffers.Binary;
using GLFrameworkEngine;
using OpenTK;

namespace BfresEditor
{
    /// <summary>The camera and target a pass is drawn with, as the context block takes them.</summary>
    public readonly struct HoianView
    {
        public readonly Matrix4 View;
        public readonly Matrix4 Projection;
        public readonly float Near;
        public readonly float Far;

        /// <summary>Vertical field of view in radians.</summary>
        public readonly float Fovy;
        public readonly float Aspect;

        /// <summary>The size of the target the fragment coordinates refer to.</summary>
        public readonly int Width;
        public readonly int Height;

        public HoianView(Matrix4 view, Matrix4 projection, float near, float far, float fovy,
            float aspect, int width, int height)
        {
            View = view;
            Projection = projection;
            Near = near;
            Far = far;
            Fovy = fovy;
            Aspect = aspect;
            Width = width;
            Height = height;
        }
    }

    /// <summary>The dynamic lights a frame hands its materials and effects.</summary>
    public interface ILightTable
    {
        int Count { get; }

        /// <summary>The cluster lists and the light array, as the frame's storage buffers.</summary>
        (byte[] Clusters, byte[] Objects) BuildStorageBuffers();
    }

    /// <summary>
    /// What one game puts in the blocks, storage buffers and system textures every material
    /// shares.
    /// </summary>
    public interface IHoianFrameSource
    {
        /// <summary>A frame block's contents, or null for one the frame does not fill.</summary>
        byte[] Block(string name);

        /// <summary>Writes gsys_context into a zeroed block of the shader model's size.</summary>
        void WriteContext(Span<byte> block, in HoianView view);

        /// <summary>A storage buffer's contents, or null to leave it unbound.</summary>
        byte[] StorageBuffer(string name);

        /// <summary>A texture for an engine sampler, or null for the default.</summary>
        GLTexture Texture(string sampler);

        /// <summary>The cube the effects read for the environment.</summary>
        GLTexture EnvironmentCube { get; }

        /// <summary>Whether materials bind the frame's storage buffers.</summary>
        bool UsesStorageBuffers { get; }

        /// <summary>Whether a parameter a material lacks takes the shader's default.</summary>
        bool MaterialBlockFromDefault { get; }

        /// <summary>The dynamic lights, rebuilt from the table each frame.</summary>
        void SetLights(ILightTable lights);

        /// <summary>No dynamic lights, until a scene sets them again.</summary>
        void ClearLights();
    }

    /// <summary>gsys_context rows both games lay out the same.</summary>
    public static class HoianContext
    {
        /// <summary>The view, view projection, projection and inverse view, and the previous
        /// frame's copies of them, kept equal to the current ones.</summary>
        public static void WriteCamera(Span<byte> block, in HoianView view)
        {
            var viewMatrix = view.View;
            var proj = view.Projection;
            var inverse = viewMatrix.Inverted();
            var viewProj = viewMatrix * proj;

            WriteMat3x4(block, 0, viewMatrix);
            WriteMat4(block, 3, viewProj);
            WriteMat4(block, 7, proj);
            WriteMat3x4(block, 11, inverse);
            WriteMat3x4(block, 21, viewMatrix);
            WriteMat4(block, 24, viewProj);
            WriteMat4(block, 28, proj);
            WriteMat3x4(block, 32, inverse);
        }

        public static void WriteMat3x4(Span<byte> block, int row, Matrix4 m)
        {
            WriteRow(block, row, m.Column0);
            WriteRow(block, row + 1, m.Column1);
            WriteRow(block, row + 2, m.Column2);
        }

        public static void WriteMat4(Span<byte> block, int row, Matrix4 m)
        {
            WriteMat3x4(block, row, m);
            WriteRow(block, row + 3, m.Column3);
        }

        /// <summary>One 16 byte row; a row past the block's end is skipped.</summary>
        public static void WriteRow(Span<byte> block, int row, Vector4 v)
        {
            int at = row * 16;
            if (at + 16 > block.Length)
                return;
            BinaryPrimitives.WriteSingleLittleEndian(block.Slice(at), v.X);
            BinaryPrimitives.WriteSingleLittleEndian(block.Slice(at + 4), v.Y);
            BinaryPrimitives.WriteSingleLittleEndian(block.Slice(at + 8), v.Z);
            BinaryPrimitives.WriteSingleLittleEndian(block.Slice(at + 12), v.W);
        }
    }
}
