using System;
using System.Buffers.Binary;

namespace EffectLibrary
{
    /// <summary>
    /// A v46 ResEmitter
    /// </summary>
    public sealed class EmitterV46
    {
        readonly VfxSection _section;
        readonly byte[] _copy;

        public EmitterV46(Emitter emitter)
        {
            if (emitter.Map.IsV46)
                _section = emitter.Section;
            else
                _copy = emitter.Map.ToV46(emitter.Data, EmitterLayout.PayloadSize);
        }

        Span<byte> Data => _copy ?? _section.Data;

        public byte GetU8(int offset) => Data[offset];

        public ushort GetU16(int offset) => BinaryPrimitives.ReadUInt16LittleEndian(Data[offset..]);

        public int GetS32(int offset) => BinaryPrimitives.ReadInt32LittleEndian(Data[offset..]);

        public uint GetU32(int offset) => BinaryPrimitives.ReadUInt32LittleEndian(Data[offset..]);

        public ulong GetU64(int offset) => BinaryPrimitives.ReadUInt64LittleEndian(Data[offset..]);

        public float GetF32(int offset) => BinaryPrimitives.ReadSingleLittleEndian(Data[offset..]);
    }
}
