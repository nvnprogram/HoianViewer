using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using OpenTK;
using OpenTK.Graphics.OpenGL;

namespace GLFrameworkEngine
{
    public class UniformBlock
    {
        public List<byte> Buffer = new List<byte>();

        public int Size => Buffer.Count * sizeof(byte);

        private int ID;

        /// <summary>A renderer's stamp for when the contents were built, so it can reuse them.</summary>
        public long Epoch;

        //What the GL buffer holds, so an upload of the same bytes is skipped.
        private byte[] _uploaded = Array.Empty<byte>();
        private int _uploadedSize = -1;

        static readonly byte[] Zeros = new byte[16];

        public UniformBlock()
        {
            GL.GenBuffers(1, out ID);
        }

        /// <summary>Replaces the contents with <paramref name="data"/>, zero padded to <paramref name="size"/> bytes.</summary>
        public void SetData(ReadOnlySpan<byte> data, int size)
        {
            Buffer.Clear();
            int n = Math.Min(data.Length, size);
            Buffer.AddRange(data.Slice(0, n));
            AddZeros(size - n);
        }

        public void AddZeros(int count)
        {
            for (; count > 0; count -= Zeros.Length)
                Buffer.AddRange(Zeros.AsSpan(0, Math.Min(count, Zeros.Length)));
        }

        public void Add(byte[] value)
        {
            Buffer.AddRange(value);
        }

        public void Add(uint[] value)
        {
            for (int i = 0; i < value.Length; i++)
                Add(value[i]);
        }

        public void Add(int[] value)
        {
            for (int i = 0; i < value.Length; i++)
                Add(value[i]);
        }

        public void Add(float[] value)
        {
            for (int i = 0; i < value.Length; i++)
                AddFloat(value[i]);
        }

        public void Add(Vector2[] value)
        {
            for (int i = 0; i < value.Length; i++)
                Add(value[i]);
        }

        public void Add(Vector3[] value)
        {
            for (int i = 0; i < value.Length; i++)
                Add(value[i]);
        }

        public void Add(Vector4[] value)
        {
            for (int i = 0; i < value.Length; i++)
                Add(value[i]);
        }

        public void Add(float value)
        {
            Span<byte> b = stackalloc byte[4];
            BinaryPrimitives.WriteSingleLittleEndian(b, value);
            Buffer.AddRange(b);
        }

        void Add(int value)
        {
            Span<byte> b = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(b, value);
            Buffer.AddRange(b);
        }

        void Add(uint value)
        {
            Span<byte> b = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(b, value);
            Buffer.AddRange(b);
        }

        public void AddFloat(float value)
        {
            Add(value);
            AddZeros(12); //Padding
        }

        public void AddInt(int value)
        {
            Add(value);
            AddZeros(12); //Padding
        }

        public void Add(Vector2 value)
        {
            Add(value.X);
            Add(value.Y);
        }

        public void Add(Vector3 value)
        {
            Add(value.X);
            Add(value.Y);
            Add(value.Z);
            AddZeros(4); //Buffer aligned so make sure it's 16 bytes size
        }

        public void Add(Vector4 value)
        {
            Add(value.X);
            Add(value.Y);
            Add(value.Z);
            Add(value.W);
        }

        public void Bind()
        {
            GL.BindBuffer(BufferTarget.UniformBuffer, ID);
        }

        //GL.GetUniformBlockIndex blocks on the driver thread, so cache the result
        //per program/name; it is called for every block of every mesh each frame.
        static readonly Dictionary<(int, string), int> _blockIndexCache = new Dictionary<(int, string), int>();

        //The binding each program's block was last given; it is program state, so it only
        //needs setting when it changes.
        static readonly Dictionary<(int, int), int> _bindingCache = new Dictionary<(int, int), int>();

        static UniformBlock()
        {
            ShaderProgram.Deleting += program =>
            {
                foreach (var key in _blockIndexCache.Keys.Where(k => k.Item1 == program).ToList())
                    _blockIndexCache.Remove(key);
                foreach (var key in _bindingCache.Keys.Where(k => k.Item1 == program).ToList())
                    _bindingCache.Remove(key);
            };
        }

        static int GetBlockIndex(int programID, string name)
        {
            if (!_blockIndexCache.TryGetValue((programID, name), out int index))
            {
                index = GL.GetUniformBlockIndex(programID, name);
                _blockIndexCache[(programID, name)] = index;
            }
            return index;
        }

        //The buffer each uniform binding point holds, as bound through here. Code binding its
        //own uniform buffers calls ForgetBindings after.
        static readonly Dictionary<int, int> _boundBuffers = new Dictionary<int, int>();

        /// <summary>Forgets which buffers the binding points hold, after something else bound its own.</summary>
        public static void ForgetBindings() => _boundBuffers.Clear();

        public void RenderBuffer(int programID, string name, int binding = -1)
        {
            var index = GetBlockIndex(programID, name);
            if (index != -1)
            {
                binding = binding != -1 ? binding : index;
                if (!_bindingCache.TryGetValue((programID, index), out int set) || set != binding)
                {
                    GL.UniformBlockBinding(programID, index, binding);
                    _bindingCache[(programID, index)] = binding;
                }
            }
            else if (binding == -1)
            {
                return;
            }

            if (!_boundBuffers.TryGetValue(binding, out int bound) || bound != ID)
            {
                GL.BindBufferBase(BufferRangeTarget.UniformBuffer, binding, ID);
                _boundBuffers[binding] = ID;
            }
            UpdateBufferData();
        }

        public void UpdateBufferData()
        {
            var data = CollectionsMarshal.AsSpan(Buffer);
            if (data.Length == _uploadedSize && data.SequenceEqual(_uploaded.AsSpan(0, _uploadedSize)))
                return;

            Bind();
            if (data.Length == _uploadedSize)
                GL.BufferSubData(BufferTarget.UniformBuffer, IntPtr.Zero, data.Length, ref MemoryMarshal.GetReference(data));
            else
                GL.BufferData(BufferTarget.UniformBuffer, data.Length, ref MemoryMarshal.GetReference(data), BufferUsageHint.DynamicDraw);
            GL.BindBuffer(BufferTarget.UniformBuffer, 0);

            if (_uploaded.Length < data.Length)
                _uploaded = new byte[data.Length];
            data.CopyTo(_uploaded);
            _uploadedSize = data.Length;
        }

        public void Dispose()
        {
            //A deleted buffer leaves its binding points, and its name can come back.
            foreach (var key in _boundBuffers.Where(p => p.Value == ID).Select(p => p.Key).ToList())
                _boundBuffers.Remove(key);
            GL.DeleteBuffer(ID);
            Buffer.Clear();
            _uploadedSize = -1;
        }
    }
}
