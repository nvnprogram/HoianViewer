using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Numerics;

namespace PlayerViewer.Effects.Sim
{
    /// <summary>
    /// The area loop plugin (EP04): a draw time effect. The emitter simulates as usual and is
    /// drawn repeat count plus one times, each draw with a plugin block holding the box the
    /// vertex program wraps particles into, its inverse, and that draw's accumulated offset.
    /// </summary>
    public sealed class AreaLoopParams
    {
        public Vector3 RepeatStep;
        public float RepeatCount;
        public Vector3 AreaSize;
        public float ClipHeight;
        public Vector3 AreaPosition;
        public int ClipType;
        public Vector3 EdgeFade;
        public float CameraLoop;
        public Vector3 AreaRotation;

        /// <summary>The 76 byte EP04 payload.</summary>
        public static AreaLoopParams Read(ReadOnlySpan<byte> p) =>
            new()
            {
                RepeatStep = V(p, 0),
                RepeatCount = F(p, 12),
                AreaSize = V(p, 16),
                ClipHeight = F(p, 28),
                AreaPosition = V(p, 32),
                ClipType = BinaryPrimitives.ReadInt32LittleEndian(p[44..]),
                EdgeFade = V(p, 48),
                CameraLoop = F(p, 60),
                AreaRotation = V(p, 64),
            };

        static float F(ReadOnlySpan<byte> p, int o) =>
            BinaryPrimitives.ReadSingleLittleEndian(p[o..]);

        static Vector3 V(ReadOnlySpan<byte> p, int o) => new(F(p, o), F(p, o + 4), F(p, o + 8));
    }

    public static class AreaLoop
    {
        public const int BlockSize = 192;

        /// <summary>
        /// One plugin block per draw. The box sits at the area position and rotation
        /// in the emitter's space, or for a camera loop at the eye, its offset turned by the
        /// billboard matrix.
        /// </summary>
        public static List<byte[]> Blocks(
            AreaLoopParams p,
            in Matrix34 emitterTransform,
            byte[] view
        )
        {
            var box = Matrix34.CreateSrtXyz(Vector3.One, p.AreaRotation, Vector3.Zero);
            if (p.CameraLoop == 0)
            {
                box.T = p.AreaPosition;
                box = Matrix34.Multiply(box, emitterTransform);
            }
            else
            {
                float F(int o) => BinaryPrimitives.ReadSingleLittleEndian(view.AsSpan(o));
                Vector3 Row(int o) => new(F(o), F(o + 4), F(o + 8));
                var a = p.AreaPosition;
                var eye = Row(0x1D0);
                box.T =
                    new Vector3(
                        Vector3.Dot(Row(0x180), a),
                        Vector3.Dot(Row(0x190), a),
                        Vector3.Dot(Row(0x1A0), a)
                    ) + eye;
            }
            var inverse = box.Inverse();

            var list = new List<byte[]>();
            var offset = Vector3.Zero;
            int count = (int)(p.RepeatCount + 1f);
            for (int i = 0; i < count; i++)
            {
                var b = new byte[BlockSize];
                W4(b, 0, new Vector4(offset, p.CameraLoop));
                W4(b, 16, new Vector4(p.EdgeFade, 0));
                Rows(b, 32, box);
                Rows(b, 96, inverse);
                W4(b, 160, new Vector4(p.ClipType, p.ClipHeight, 0, 0));
                W4(b, 176, new Vector4(p.AreaSize, 0));
                list.Add(b);
                offset += p.RepeatStep;
            }
            return list;
        }

        static void Rows(byte[] b, int at, in Matrix34 m)
        {
            W4(b, at, new Vector4(m.R0, 0));
            W4(b, at + 16, new Vector4(m.R1, 0));
            W4(b, at + 32, new Vector4(m.R2, 0));
            W4(b, at + 48, new Vector4(m.T, 1));
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
