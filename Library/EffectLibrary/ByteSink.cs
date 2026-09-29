using System;
using System.Buffers.Binary;

namespace EffectLibrary
{
    /// <summary>A growable output buffer with back patching, sized for files of a hundred MB.</summary>
    sealed class ByteSink
    {
        byte[] _buffer;

        public int Position { get; private set; }

        public ByteSink(int capacity) => _buffer = new byte[Math.Max(capacity, 256)];

        void Reserve(int count)
        {
            int needed = Position + count;
            if (needed <= _buffer.Length)
                return;
            int size = _buffer.Length;
            while (size < needed)
                size = size < 1 << 30 ? size * 2 : int.MaxValue;
            Array.Resize(ref _buffer, size);
        }

        public void Write(ReadOnlySpan<byte> bytes)
        {
            Reserve(bytes.Length);
            bytes.CopyTo(_buffer.AsSpan(Position));
            Position += bytes.Length;
        }

        public void WriteU32(uint value)
        {
            Reserve(4);
            BinaryPrimitives.WriteUInt32LittleEndian(_buffer.AsSpan(Position), value);
            Position += 4;
        }

        /// <summary>Zero fills up to the next multiple of <paramref name="alignment"/>.</summary>
        public void Align(int alignment)
        {
            int target = (Position + alignment - 1) / alignment * alignment;
            Reserve(target - Position);
            _buffer.AsSpan(Position, target - Position).Clear();
            Position = target;
        }

        public void PatchU32(int at, uint value) =>
            BinaryPrimitives.WriteUInt32LittleEndian(_buffer.AsSpan(at), value);

        public void PatchU16(int at, ushort value) =>
            BinaryPrimitives.WriteUInt16LittleEndian(_buffer.AsSpan(at), value);

        public uint ReadU32(int at) => BinaryPrimitives.ReadUInt32LittleEndian(_buffer.AsSpan(at));

        public byte[] ToArray() => _buffer.AsSpan(0, Position).ToArray();
    }
}
