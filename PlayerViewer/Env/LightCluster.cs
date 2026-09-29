using System;
using System.Buffers.Binary;
using System.Numerics;

namespace PlayerViewer.Env
{
    /// <summary>
    /// The dynamic light table models read as gsys_user2 and effects as their Custom2 block: up to
    /// 30 point and spot lights, and a 20 by 20 grid of cells over XZ that each list up to four of
    /// them. Rebuilt the way the game does each frame: reset, add the lights, write the block.
    /// </summary>
    public sealed class LightCluster
    {
        public const int MaxLights = 30;
        public const int Grid = 20;
        public const int BlockSize = 9216;

        const int ColorRow = 400,
            ParamRow = 430,
            PositionRow = 460,
            DirectionRow = 490,
            SpotRow = 520,
            OriginRow = 550,
            InverseCellRow = 551;

        /// <summary>The grid's corner. The default is the placement every capture shows.</summary>
        public Vector3 Origin = new(-100, -0.5f, -100);
        public Vector3 CellSize = new(10, 1, 10);

        readonly uint[] _cells = new uint[Grid * Grid];
        readonly Light[] _lights = new Light[MaxLights];

        public int Count { get; private set; }

        struct Light
        {
            public Vector4 Color;
            public Vector4 Param;
            public Vector3 Position,
                Direction;
            public bool Spot;
        }

        public LightCluster() => Reset();

        /// <summary>Empty cells, no lights.</summary>
        public void Reset()
        {
            Array.Fill(_cells, uint.MaxValue);
            Count = 0;
        }

        /// <summary>A point light reaching <paramref name="range"/>; false once the table is full
        /// or no cell of the grid is in reach.</summary>
        public bool AddPoint(Vector3 position, Vector4 color, float range, float falloff)
        {
            if (Count >= MaxLights || !Place(position, Vector3.UnitY, Count, false, range, 0))
                return false;
            _lights[Count++] = new Light
            {
                Color = color,
                Param = new Vector4(1 / range, falloff, 1, 0),
                Position = position,
                Direction = Vector3.UnitX,
            };
            return true;
        }

        /// <summary>A spot light along <paramref name="direction"/> with a cone of
        /// <paramref name="halfAngle"/> radians either side.</summary>
        public bool AddSpot(
            Vector3 position,
            Vector3 direction,
            Vector4 color,
            float range,
            float falloff,
            float halfAngle,
            float exponent
        )
        {
            if (Count >= MaxLights || !Place(position, direction, Count, true, range, halfAngle))
                return false;
            float length = direction.Length();
            _lights[Count++] = new Light
            {
                Color = color,
                Param = new Vector4(1 / range, falloff, MathF.Cos(halfAngle), exponent),
                Position = position,
                Direction = length > 0 ? direction / length : direction,
                Spot = true,
            };
            return true;
        }

        /// <summary>
        /// Lists the light in every cell it may reach, newest first in the cell. A point light
        /// reaches a cell whose centre is within its range plus half the cell's diagonal; a spot
        /// light's range there is shortened to the edge of its cone nearest the cell.
        /// </summary>
        bool Place(Vector3 p, Vector3 direction, int index, bool spot, float range, float angle)
        {
            float x = p.X - Origin.X,
                z = p.Z - Origin.Z;
            int cx = (int)MathF.Floor(x / CellSize.X),
                cz = (int)MathF.Floor(z / CellSize.Z);
            int rx = (int)MathF.Ceiling(range / CellSize.X),
                rz = (int)MathF.Ceiling(range / CellSize.Z);
            float halfDiagonal = MathF.Max(CellSize.X, CellSize.Z) * 0.70711f;
            var d = direction.Length() > 0 ? Vector3.Normalize(direction) : direction;
            bool placed = false;
            for (int gx = cx - rx; gx <= cx + rx; gx++)
            {
                if ((uint)gx >= Grid)
                    continue;
                for (int gz = cz - rz; gz <= cz + rz; gz++)
                {
                    if ((uint)gz >= Grid)
                        continue;
                    float dx = (gx + 0.5f) * CellSize.X - x,
                        dz = (gz + 0.5f) * CellSize.Z - z;
                    float distance = MathF.Sqrt(dx * dx + dz * dz);
                    float reach = range;
                    if (spot)
                    {
                        var c = distance > 0 ? new Vector3(dx, 0, dz) / distance : d;
                        if (Math.Clamp(Vector3.Dot(c, d), -1, 1) <= MathF.Cos(angle))
                            reach = MathF.Max(Vector3.Dot(c, ConeEdge(d, c, angle)) * range, 0);
                    }
                    if (distance >= halfDiagonal + reach)
                        continue;
                    ref uint cell = ref _cells[gx + Grid * gz];
                    if (cell >> 24 == 0xFF)
                    {
                        cell = (uint)index | (cell << 8);
                        placed = true;
                    }
                }
            }
            return placed;
        }

        //The spot direction turned by the cone angle about d x c, as the game builds it: the
        //axis is not normalised.
        static Vector3 ConeEdge(Vector3 d, Vector3 c, float angle)
        {
            float s = MathF.Sin(angle * 0.5f),
                w = MathF.Cos(angle * 0.5f);
            var q = new Quaternion(Vector3.Cross(d, c) * s, w);
            float x2 = q.X + q.X,
                y2 = q.Y + q.Y,
                z2 = q.Z + q.Z;
            return new Vector3(
                d.X * (1 - q.Y * y2 - q.Z * z2)
                    + d.Y * (q.X * y2 - w * z2)
                    + d.Z * (q.X * z2 + w * y2),
                d.X * (q.X * y2 + w * z2)
                    + d.Y * (1 - q.X * x2 - q.Z * z2)
                    + d.Z * (q.Y * z2 - w * x2),
                d.X * (q.X * z2 - w * y2)
                    + d.Y * (q.Y * z2 + w * x2)
                    + d.Z * (1 - q.X * x2 - q.Y * y2)
            );
        }

        /// <summary>The block as uploaded. Slots past <see cref="Count"/> keep the reset state:
        /// white, everything else zero.</summary>
        public byte[] Build()
        {
            var b = new byte[BlockSize];
            var span = b.AsSpan();
            for (int i = 0; i < _cells.Length; i++)
                BinaryPrimitives.WriteUInt32LittleEndian(span[(i * 16)..], _cells[i]);
            for (int i = 0; i < MaxLights; i++)
            {
                var l = i < Count ? _lights[i] : new Light { Color = Vector4.One };
                Row(span, ColorRow + i, l.Color);
                Row(span, ParamRow + i, l.Param);
                Row(span, PositionRow + i, new Vector4(l.Position, 0));
                Row(span, DirectionRow + i, new Vector4(l.Direction, 0));
                BinaryPrimitives.WriteUInt32LittleEndian(
                    span[((SpotRow + i) * 16)..],
                    l.Spot ? 1u : 0u
                );
            }
            Row(span, OriginRow, new Vector4(Origin, 0));
            Row(span, InverseCellRow, new Vector4(Vector3.One / CellSize, 0));
            return b;
        }

        static void Row(Span<byte> b, int row, Vector4 v)
        {
            var at = b[(row * 16)..];
            BinaryPrimitives.WriteSingleLittleEndian(at, v.X);
            BinaryPrimitives.WriteSingleLittleEndian(at[4..], v.Y);
            BinaryPrimitives.WriteSingleLittleEndian(at[8..], v.Z);
            BinaryPrimitives.WriteSingleLittleEndian(at[12..], v.W);
        }
    }
}
