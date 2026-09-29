namespace PlayerViewer.Rigging
{
    /// <summary>Disjoint sets of the integers below a count.</summary>
    sealed class UnionFind
    {
        readonly int[] _parent;

        public UnionFind(int count)
        {
            _parent = new int[count];
            for (int i = 0; i < count; i++)
                _parent[i] = i;
        }

        public int Find(int x)
        {
            while (_parent[x] != x)
            {
                _parent[x] = _parent[_parent[x]];
                x = _parent[x];
            }
            return x;
        }

        public void Union(int a, int b)
        {
            a = Find(a);
            b = Find(b);
            if (a != b)
                _parent[a] = b;
        }
    }
}
