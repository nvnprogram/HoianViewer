using System;
using System.Collections.Generic;
using OpenTK;

namespace PlayerViewer.Rigging
{
    /// <summary>Items hashed by position into cubic cells, for finding what lies near a point.</summary>
    sealed class SpatialGrid
    {
        readonly float _cell;
        readonly Dictionary<(int, int, int), List<int>> _cells = new();

        public SpatialGrid(float cell) => _cell = cell;

        public bool IsEmpty => _cells.Count == 0;

        public (int X, int Y, int Z) Key(Vector3 p) =>
            (
                (int)MathF.Floor(p.X / _cell),
                (int)MathF.Floor(p.Y / _cell),
                (int)MathF.Floor(p.Z / _cell)
            );

        public void Add(int item, Vector3 at)
        {
            var key = Key(at);
            if (!_cells.TryGetValue(key, out var list))
                _cells[key] = list = new List<int>();
            list.Add(item);
        }

        /// <summary>The items in the cells within <paramref name="reach"/> cells of the point's, by x, then y, then z.</summary>
        public IEnumerable<int> Near(Vector3 p, int reach = 1) => Cells(Key(p), 0, reach);

        /// <summary>The items in the cells exactly <paramref name="ring"/> cells from the point's.</summary>
        public IEnumerable<int> Ring(Vector3 p, int ring) => Cells(Key(p), ring, ring);

        /// <summary>Every item, cell by cell in the order the cells were first filled.</summary>
        public IEnumerable<int> All()
        {
            foreach (var list in _cells.Values)
            foreach (int item in list)
                yield return item;
        }

        IEnumerable<int> Cells((int X, int Y, int Z) at, int from, int to)
        {
            for (int i = -to; i <= to; i++)
            for (int j = -to; j <= to; j++)
            for (int k = -to; k <= to; k++)
            {
                if (Math.Max(Math.Abs(i), Math.Max(Math.Abs(j), Math.Abs(k))) < from)
                    continue;
                if (!_cells.TryGetValue((at.X + i, at.Y + j, at.Z + k), out var list))
                    continue;
                foreach (int item in list)
                    yield return item;
            }
        }
    }
}
