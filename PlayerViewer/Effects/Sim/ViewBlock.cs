using System;
using System.Buffers.Binary;
using System.Numerics;

namespace PlayerViewer.Effects.Sim
{
    /// <summary>
    /// The view block (NnVfx2ViewParam, 0x250 bytes) from a camera. Matrices are stored row by
    /// row and act on column vectors.
    /// </summary>
    public static class ViewBlock
    {
        /// <summary>
        /// Builds the block. <paramref name="view"/> and <paramref name="projection"/> are in
        /// System.Numerics' row vector convention, as <see cref="Matrix4x4.CreateLookAt"/> and
        /// <see cref="Matrix4x4.CreatePerspectiveFieldOfView"/> return them.
        /// </summary>
        public static byte[] Build(
            Matrix4x4 view,
            Matrix4x4 projection,
            Vector3 eye,
            float near,
            float far,
            float fovY
        )
        {
            var b = new byte[EffectBindings.ViewSize];
            var v = Matrix4x4.Transpose(view);
            var p = Matrix4x4.Transpose(projection);
            var vp = p * v;
            Matrix4x4.Invert(v, out var iv);
            Matrix4x4.Invert(p, out var ip);
            Matrix4x4.Invert(vp, out var ivp);
            Write(b, 0x000, v);
            Write(b, 0x040, p);
            Write(b, 0x080, vp);
            Write(b, 0x0C0, iv);
            Write(b, 0x100, ip);
            Write(b, 0x140, ivp);
            // The billboard matrix undoes the view rotation.
            var bill = new Matrix4x4(
                v.M11,
                v.M21,
                v.M31,
                0,
                v.M12,
                v.M22,
                v.M32,
                0,
                v.M13,
                v.M23,
                v.M33,
                0,
                0,
                0,
                0,
                1
            );
            Write(b, 0x180, bill);
            W4(b, 0x1C0, new Vector4(v.M31, v.M32, v.M33, 0));
            W4(b, 0x1D0, new Vector4(eye, 0));
            W4(b, 0x1E0, new Vector4(near, far, near * far, far - near));
            // The first and third words are constants in every capture; their meaning is open.
            W4(b, 0x1F0, new Vector4(1.57f, fovY, 0.01f, 0));
            return b;
        }

        static void Write(byte[] b, int at, in Matrix4x4 m)
        {
            W4(b, at, new Vector4(m.M11, m.M12, m.M13, m.M14));
            W4(b, at + 0x10, new Vector4(m.M21, m.M22, m.M23, m.M24));
            W4(b, at + 0x20, new Vector4(m.M31, m.M32, m.M33, m.M34));
            W4(b, at + 0x30, new Vector4(m.M41, m.M42, m.M43, m.M44));
        }

        static void W4(byte[] b, int at, Vector4 v)
        {
            var s = b.AsSpan(at);
            BinaryPrimitives.WriteSingleLittleEndian(s, v.X);
            BinaryPrimitives.WriteSingleLittleEndian(s[4..], v.Y);
            BinaryPrimitives.WriteSingleLittleEndian(s[8..], v.Z);
            BinaryPrimitives.WriteSingleLittleEndian(s[12..], v.W);
        }
    }
}
