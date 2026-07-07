using ShaderLibrary.IO;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;

namespace ShaderLibrary
{
    public class ResDict<T> :  Dictionary<string, T>, IResData where T : IResData, new()
    {
        internal List<Node> _nodes = new List<Node>();

        internal List<Node> GetNodes() => _nodes;

        public ResDict() { }

        //Cached key list/index map. Indexed access previously enumerated Keys via
        //ElementAt which made lookups O(n) (and callers looping over entries O(n^2)).
        //The cache is invalidated by count; dictionaries here are built once on load.
        private string[] _keyCache;
        private Dictionary<string, int> _indexCache;
        private int _cacheCount = -1;

        private void EnsureKeyCache()
        {
            if (_cacheCount == Count)
                return;

            _keyCache = new string[Count];
            _indexCache = new Dictionary<string, int>(Count);
            int i = 0;
            foreach (var key in Keys)
            {
                _keyCache[i] = key;
                _indexCache[key] = i;
                i++;
            }
            _cacheCount = Count;
        }

        public T this[int index]
        {
            get
            {
                string key = GetKey(index);
                return key != null ? this[key] : new T();
            }
            set
            {
                string key = GetKey(index);
                if (key != null)
                    this[key] = value;
            }
        }

        public string GetKey(int index)
        {
            EnsureKeyCache();
            if (index >= 0 && index < _keyCache.Length)
                return _keyCache[index];

            return null;
        }

        public int GetIndex(string key)
        {
            EnsureKeyCache();
            return _indexCache.TryGetValue(key, out int index) ? index : -1;
        }

        public void Read(BinaryDataReader reader)
        {
            reader.ReadUInt32(); //magic
            int numNodes = reader.ReadInt32();

            _nodes.Clear();

            int i = 0;
            for (; numNodes >= 0; numNodes--)
            {
                _nodes.Add(new Node()
                {
                    Reference = reader.ReadUInt32(),
                    IdxLeft = reader.ReadUInt16(),
                    IdxRight = reader.ReadUInt16(),
                    Key = reader.LoadString(reader.ReadUInt64()),
                });
                i++;
            }

            for (int j = 1; j < _nodes.Count; j++)
                this.Add(_nodes[j].Key, new T());
        }

        public void GenerateTree()
        {
            // Update the Patricia trie values in the nodes.
            var newNodes = ResDictUpdate.UpdateNodes(Keys.ToList());

            if (_nodes.Count != newNodes.Length)
                _nodes = newNodes.Select(_ => new Node()).ToList();

            for (int i = 0; i < _nodes.Count; i++)
            {
                _nodes[i].Reference = newNodes[i].Reference;
                _nodes[i].IdxLeft = newNodes[i].IdxLeft;
                _nodes[i].IdxRight = newNodes[i].IdxRight;
                _nodes[i].Key = newNodes[i].Key;
            }
        }

        public void GenerateTreeWiiU()
        {
            // Update the Patricia trie values in the nodes.
            var newNodes = ResDictUpdateWiiU.UpdateNodes(Keys.ToList());

            if (_nodes.Count != newNodes.Length)
                _nodes = newNodes.Select(_ => new Node()).ToList();

            for (int i = 0; i < _nodes.Count; i++)
            {
                _nodes[i].Reference = newNodes[i].Reference;
                _nodes[i].IdxLeft = newNodes[i].IdxLeft;
                _nodes[i].IdxRight = newNodes[i].IdxRight;
                _nodes[i].Key = newNodes[i].Key;
            }
        }

        internal class Node
        {
            internal uint Reference;
            internal ushort IdxLeft;
            internal ushort IdxRight;
            internal string Key;
            internal IResData Value;
        }
    }
}
