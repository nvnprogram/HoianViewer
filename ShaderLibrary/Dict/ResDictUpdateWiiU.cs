using System;
using System.Numerics;
using System.Linq;
using System.Text;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;

namespace ShaderLibrary
{
    public class ResDictUpdateWiiU
    {
        static internal Node[] UpdateNodes(List<string> keys)
        {
            List<Node> _nodes = new Node[keys.Count + 1].ToList();
            for (ushort i = 1; i < keys.Count + 1; i++)
                _nodes[i] = new Node() { Key = keys[i - 1] };

            // Create a new root node with empty key so the length can be retrieved throughout the process.
            _nodes[0] = new Node() { Key = String.Empty };

            // Update the data-referencing nodes.
            for (ushort i = 1; i < _nodes.Count; i++)
            {
                Node current = _nodes[i];
                string curKey = current.Key;

                // Iterate through the tree to get the string for bit comparison.
                Node parent = _nodes[0];
                Node child = _nodes[parent.IdxLeft];
                while (parent.Reference > child.Reference)
                {
                    parent = child;
                    child = GetDirection(curKey, child.Reference) == 1 ? _nodes[child.IdxRight] : _nodes[child.IdxLeft];
                }
                uint reference = (uint)Math.Max(curKey.Length, child.Key.Length) * 8;
                // Check for duplicate keys.
                while (GetDirection(child.Key, reference) == GetDirection(curKey, reference))
                {
                    if (reference == 0) throw new Exception($"Duplicate key \"{curKey}\".");
                    reference--;
                }
                current.Reference = reference;

                // Form the tree structure of the nodes.
                parent = _nodes[0];
                child = _nodes[parent.IdxLeft];
                // Find the node where to insert the current one.
                while (parent.Reference > child.Reference && child.Reference > reference)
                {
                    parent = child;
                    child = GetDirection(curKey, child.Reference) == 1 ? _nodes[child.IdxRight] : _nodes[child.IdxLeft];
                }
                // Attach left or right depending on the resulting direction bit.
                if (GetDirection(curKey, current.Reference) == 1)
                {
                    current.IdxLeft = (ushort)_nodes.IndexOf(child);
                    current.IdxRight = i;
                }
                else
                {
                    current.IdxLeft = i;
                    current.IdxRight = (ushort)_nodes.IndexOf(child);
                }
                // Attach left or right to the parent depending on the resulting parent direction bit.
                if (GetDirection(curKey, parent.Reference) == 1)
                {
                    parent.IdxRight = i;
                }
                else
                {
                    parent.IdxLeft = i;
                }
            }

            // Remove the dummy empty key in the root again.
            _nodes[0].Key = null;

            return _nodes.ToArray();
        }

        static int GetDirection(string name, uint reference)
        {
            int walkDirection = (int)(reference >> 3);
            int bitPosition = (int)(reference & 0b00000111);
            return walkDirection < name.Length ? (name[walkDirection] >> bitPosition) & 1 : 0;
        }

        // ---- CLASSES ------------------------------------------------------------------------------------------------

        internal class Node
        {
            internal uint Reference;
            internal ushort IdxLeft;
            internal ushort IdxRight;
            internal string Key;

            internal Node()
            {
                Reference = UInt32.MaxValue;
            }
        }
    }
}