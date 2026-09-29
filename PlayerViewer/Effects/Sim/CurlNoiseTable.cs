using System;
using System.IO;
using System.Numerics;

namespace PlayerViewer.Effects.Sim
{
    /// <summary>
    /// The runtime's curl noise vector field: 32 cubed cells of three signed bytes, x outermost,
    /// read as value / 127. The bytes are the game's own, taken from its executable.
    /// </summary>
    public static class CurlNoiseTable
    {
        public const int Side = 32;

        static sbyte[] _data;

        static sbyte[] Data => _data ??= Load();

        static sbyte[] Load()
        {
            using var stream =
                typeof(CurlNoiseTable).Assembly.GetManifestResourceStream(
                    "PlayerViewer.Effects.Resources.CurlNoise.bin"
                ) ?? throw new InvalidOperationException("missing embedded curl noise table");
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            return (sbyte[])(Array)ms.ToArray();
        }

        /// <summary>The cell a coordinate falls in: truncated, then the absolute value, wrapped.</summary>
        public static Vector3 Nearest(Vector3 c) =>
            Cell(Math.Abs((int)c.X), Math.Abs((int)c.Y), Math.Abs((int)c.Z));

        /// <summary>
        /// Trilinear between the cell and the next one up on each axis, each index made positive
        /// on its own, weighted by the coordinate less its truncation.
        /// </summary>
        public static Vector3 Interpolated(Vector3 c)
        {
            int x = (int)c.X,
                y = (int)c.Y,
                z = (int)c.Z;
            int x0 = Math.Abs(x),
                y0 = Math.Abs(y),
                z0 = Math.Abs(z);
            int x1 = Math.Abs(x + 1),
                y1 = Math.Abs(y + 1),
                z1 = Math.Abs(z + 1);
            float fx = c.X - x,
                fy = c.Y - y,
                fz = c.Z - z;
            var a = Lerp(Cell(x0, y0, z0), Cell(x1, y0, z0), fx);
            a = Lerp(a, Lerp(Cell(x0, y1, z0), Cell(x1, y1, z0), fx), fy);
            var b = Lerp(Cell(x0, y0, z1), Cell(x1, y0, z1), fx);
            b = Lerp(b, Lerp(Cell(x0, y1, z1), Cell(x1, y1, z1), fx), fy);
            return Lerp(a, b, fz);
        }

        static Vector3 Lerp(Vector3 a, Vector3 b, float t) => VfxMath.Fma(b - a, t, a);

        static Vector3 Cell(int x, int y, int z)
        {
            var d = Data;
            int i = 3 * (z % Side + (y % Side + x % Side * Side) * Side);
            return new Vector3(d[i] / 127f, d[i + 1] / 127f, d[i + 2] / 127f);
        }
    }
}
