using System;
using System.Collections.Generic;
using System.Linq;
using OpenTK.Graphics.OpenGL;

namespace GLFrameworkEngine
{
    /// <summary>A shader storage buffer bound to a program's block by name.</summary>
    public class StorageBlock
    {
        int _id;
        byte[] _uploaded;

        static readonly Dictionary<(int, string), int> _blockIndexCache = new Dictionary<(int, string), int>();
        static readonly Dictionary<(int, int), int> _bindingCache = new Dictionary<(int, int), int>();

        static StorageBlock()
        {
            ShaderProgram.Deleting += program =>
            {
                foreach (var key in _blockIndexCache.Keys.Where(k => k.Item1 == program).ToList())
                    _blockIndexCache.Remove(key);
                foreach (var key in _bindingCache.Keys.Where(k => k.Item1 == program).ToList())
                    _bindingCache.Remove(key);
            };
        }

        /// <summary>Uploads the contents unless they are the array uploaded last.</summary>
        public void SetData(byte[] data)
        {
            if (_id == 0)
                GL.GenBuffers(1, out _id);
            if (ReferenceEquals(data, _uploaded))
                return;
            GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _id);
            GL.BufferData(BufferTarget.ShaderStorageBuffer, data.Length, data, BufferUsageHint.DynamicDraw);
            GL.BindBuffer(BufferTarget.ShaderStorageBuffer, 0);
            _uploaded = data;
        }

        /// <summary>Binds the buffer at <paramref name="binding"/> and points the program's block there.</summary>
        public void Bind(int programID, string blockName, int binding)
        {
            if (!_blockIndexCache.TryGetValue((programID, blockName), out int index))
            {
                index = GL.GetProgramResourceIndex(programID, ProgramInterface.ShaderStorageBlock, blockName);
                _blockIndexCache[(programID, blockName)] = index;
            }
            if (index < 0)
                return;
            if (!_bindingCache.TryGetValue((programID, index), out int set) || set != binding)
            {
                GL.ShaderStorageBlockBinding(programID, index, binding);
                _bindingCache[(programID, index)] = binding;
            }
            GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, binding, _id);
        }

        public void Dispose()
        {
            if (_id != 0)
                GL.DeleteBuffer(_id);
            _id = 0;
            _uploaded = null;
        }
    }
}
