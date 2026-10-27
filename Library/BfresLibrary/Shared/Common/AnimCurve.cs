using BfresLibrary.Core;
using System.ComponentModel;
using System.Collections.Generic;

namespace BfresLibrary
{
    /// <summary>
    /// Represents an animation curve used by several sections to control different parameters over time.
    /// </summary>
    public class AnimCurve : IResData
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="AnimCurve"/> class.
        /// </summary>
        public AnimCurve()
        {
            FrameType = AnimCurveFrameType.Single;
            KeyType = AnimCurveKeyType.Single;
            CurveType = AnimCurveType.Cubic;

            AnimDataOffset = 0;
            StartFrame = 0;
            EndFrame = 0;
            Scale = 0;
            Frames = new float[0];
            KeyStepBoolData = new bool[0];
            Keys = new float[0, 4];
        }

        // ---- CONSTANTS ----------------------------------------------------------------------------------------------

        private const ushort _flagsMaskFrameType = 0b00000000_00000011;
        private const ushort _flagsMaskKeyType = 0b00000000_00001100;
        private const ushort _flagsMaskCurveType = 0b00000000_01110000;
        private const ushort _flagsValueKeys = 0x4000;
        private const ushort _flagsSlopeQuantized = 0x0800;
        private const ushort _flagsFrameScaled = 0x0400;
        private const ushort _curveTypeStepFloat = 3 << 4;

        // ---- FIELDS -------------------------------------------------------------------------------------------------

        private ushort _flags;

        // ---- PROPERTIES ---------------------------------------------------------------------------------------------

        /// <summary>
        /// Gets or sets the data type in which <see cref="Frames"/> are loaded and saved. For simplicity, the class
        /// always stores frames as converted <see cref="System.Single"/> instances.
        /// </summary>
        public AnimCurveFrameType FrameType
        {
            get { return (AnimCurveFrameType)(_flags & _flagsMaskFrameType); }
            set { _flags = (ushort)(_flags & ~_flagsMaskFrameType | (ushort)value); }
        }

        /// <summary>
        /// Gets or sets the data type in which <see cref="Keys"/> are loaded and saved. For simplicity, the class
        /// always stores frames as converted <see cref="System.Single"/> instances.
        /// </summary>
        public AnimCurveKeyType KeyType
        {
            get { return (AnimCurveKeyType)(_flags & _flagsMaskKeyType); }
            set { _flags = (ushort)(_flags & ~_flagsMaskKeyType | (ushort)value); }
        }

        /// <summary>
        /// Gets or sets the curve type, determining the number of elements stored with each key.
        /// </summary>
        public AnimCurveType CurveType
        {
            get { return (AnimCurveType)(_flags & _flagsMaskCurveType); }
            set { _flags = (ushort)(_flags & ~_flagsMaskCurveType | (ushort)value); }
        }

        /// <summary>
        /// Gets or sets the pre wrap mode, determining how to wrap the key data.
        /// </summary>
        public WrapMode PreWrap
        {
            get { return (WrapMode)(_flags >> 8 & 3); }
            set { _flags = (ushort)(_flags & 64767 | (ushort)value << 8); }
        }

        /// <summary>
        /// Gets or sets the post wrap mode, determining how to wrap the key data.
        /// </summary>
        public WrapMode PostWrap
        {
            get { return (WrapMode)(_flags >> 12 & 3); }
            set { _flags = (ushort)(_flags & 53247 | (ushort)value << 12); }
        }

        /// <summary>
        /// Gets or sets the memory offset relative to the start of the corresponding animation data structure to
        /// animate the field stored at that address. Note that enums exist in the specific animation which map offsets
        /// to names.
        /// </summary>
        public uint AnimDataOffset { get; set; }

        /// <summary>
        /// Gets or sets the first frame at which a key is placed.
        /// </summary>
        public float StartFrame { get; set; }

        /// <summary>
        /// Gets or sets the last frame at which a key is placed.
        /// </summary>
        public float EndFrame { get; set; }

        /// <summary>
        /// Gets or sets the scale to multiply values of the curve by.
        /// </summary>
        public float Scale { get; set; }

        /// <summary>
        /// Gets or sets the offset to add to the values of the curve (after multiplicating them).
        /// </summary>
        public DWord Offset { get; set; }

        /// <summary>
        /// Gets or sets the difference between the lowest and highest key value.
        /// </summary>
        public float Delta { get; set; }

        /// <summary>
        /// Gets the frame numbers at which keys of the same index in the <see cref="Keys"/> array are placed.
        /// </summary>
        public float[] Frames { get; set; }

        /// <summary>
        /// Gets an array of elements forming the elements of keys placed at the frames of the same index in the
        /// <see cref="Frames"/> array.
        /// </summary>
        public float[,] Keys { get; set; }

        public bool[] KeyStepBoolData { get; set; }

        /// <summary>
        /// The packed bits behind <see cref="KeyStepBoolData"/>, one bit per key, low bit
        /// first. Held exactly because <see cref="Keys"/> is float and loses the low bits of
        /// a full 32 bit mask.
        /// </summary>
        public uint[] BooleanWords { get; set; } = new uint[0];

        /// <summary>
        /// The flags as the file stored them. A value keyed curve is converted on load to the
        /// encoding of earlier files, so <see cref="Keys"/> and the type properties describe the
        /// converted curve.
        /// </summary>
        public ushort SourceFlags { get; private set; }

        /// <summary>Whether the file stored this curve as value keys.</summary>
        public bool IsValueKeyed => (SourceFlags & _flagsValueKeys) != 0;

        /// <summary>Whether the file stored this curve as Hermite values and slopes.</summary>
        public bool IsHermite => IsValueKeyed && (SourceFlags & _flagsMaskCurveType) == 0;

        /// <summary>
        /// Whether the file stored this curve as a held float value per key, a type earlier files
        /// lack. It loads as a linear curve with no slope.
        /// </summary>
        public bool IsStepFloat => IsValueKeyed && (SourceFlags & _flagsMaskCurveType) == _curveTypeStepFloat;

        /// <summary>The decoded value at each key of a Hermite curve, null for other curves.</summary>
        public float[] HermiteValues { get; set; }

        /// <summary>The decoded slope per frame at each key of a Hermite curve.</summary>
        public float[] HermiteSlopes { get; set; }

        private int ElementsPerKey
        {
            get
            {
                switch (CurveType)
                {
                    case AnimCurveType.Cubic:
                        return 4;
                    case AnimCurveType.Linear:
                        return 2;
                    default:
                        return 1;
                }
            }
        }

        // ---- METHODS ------------------------------------------------------------------------------------------------

        /// <summary>
        /// Calculates the bake size of the animation curve.
        /// </summary>
        /// <returns></returns>
        public uint CalculateBakeSize(bool isSwitch)
        {
            if (isSwitch)
            {
                if (CurveType == AnimCurveType.StepInt || CurveType == AnimCurveType.StepBool)
                    return (uint)(1 * (EndFrame - StartFrame + 1) + 12) + 4;
                else
                    return (uint)(4 * (EndFrame - StartFrame + 1) + 32 ) - 4;
            }
            else
            {
                if (CurveType == AnimCurveType.StepInt || CurveType == AnimCurveType.StepBool)
                    return (uint)(1 * (EndFrame - StartFrame + 1) + 8);
                else
                    return (uint)(4 * (EndFrame - StartFrame + 1) + 20);
            }
        }

        public AnimCurve Copy()
        {
            AnimCurve curve = new AnimCurve();
            curve._flags = this._flags;
            curve.AnimDataOffset = this.AnimDataOffset;
            curve.CurveType = this.CurveType;
            curve.FrameType = this.FrameType;
            curve.Delta = this.Delta;
            curve.EndFrame = this.EndFrame;
            curve.StartFrame = this.StartFrame;
            curve.Offset = this.Offset;
            curve.Scale = this.Scale;
            curve.Frames = new float[this.Frames.Length];
            curve.Keys = new float[this.Keys.Length / this.ElementsPerKey, this.ElementsPerKey];

            for (int i = 0; i < this.Frames.Length; i++)
                curve.Frames[i] = this.Frames[i];

            for (int i = 0; i < this.Keys.Length / this.ElementsPerKey; i++)
            {
                for (int j = 0; j < this.ElementsPerKey; j++)
                    curve.Keys[i, j] = this.Keys[i, j];
            }

            curve.KeyStepBoolData = (bool[])this.KeyStepBoolData?.Clone();
            curve.BooleanWords = (uint[])this.BooleanWords?.Clone() ?? new uint[0];

            return curve;
        }

        void IResData.Load(ResFileLoader loader)
        {
            long FrameArrayOffset = 0;
            long KeyArrayOffset = 0;
            ushort numKey = 0;
            if (loader.IsSwitch) {
                FrameArrayOffset = loader.ReadOffset();
                KeyArrayOffset = loader.ReadOffset();
                _flags = loader.ReadUInt16();
                numKey = loader.ReadUInt16();
                AnimDataOffset = loader.ReadUInt32();
                StartFrame = loader.ReadSingle();
                EndFrame = loader.ReadSingle();
                Scale = loader.ReadSingle();
                Offset = loader.ReadSingle();
                Delta = loader.ReadSingle();
                int padding = loader.ReadInt32();
                SourceFlags = _flags;
                if ((_flags & _flagsValueKeys) != 0)
                {
                    LoadValueKeys(loader, numKey, FrameArrayOffset, KeyArrayOffset);
                    return;
                }
            }
            else
            {
                _flags = loader.ReadUInt16();
                numKey = loader.ReadUInt16();
                AnimDataOffset = loader.ReadUInt32();
                StartFrame = loader.ReadSingle();
                EndFrame = loader.ReadSingle();
                Scale = loader.ReadSingle();
                Offset = loader.ReadSingle();
                if (loader.ResFile.Version >= 0x03040000)
                {
                    Delta = loader.ReadSingle();
                }
                FrameArrayOffset = loader.ReadOffset();
                KeyArrayOffset = loader.ReadOffset();
            }

            Frames = loader.LoadCustom(() =>
            {
                switch (FrameType)
                {
                    case AnimCurveFrameType.Single:
                        return loader.ReadSingles(numKey);
                    case AnimCurveFrameType.Decimal10x5:
                        float[] dec10x5Frames = new float[numKey];
                        for (int i = 0; i < numKey; i++)
                        {
                            dec10x5Frames[i] = (float)loader.ReadDecimal10x5();
                        }
                        return dec10x5Frames;
                    case AnimCurveFrameType.Byte:
                        float[] byteFrames = new float[numKey];
                        for (int i = 0; i < numKey; i++)
                        {
                            byteFrames[i] = loader.ReadByte();
                        }
                        return byteFrames;
                    default:
                        throw new ResException($"Invalid {nameof(FrameType)}.");
                }
            }, (uint)FrameArrayOffset);
            //A bool curve stores one bit per key, so its key data is words rather than
            //keys and it is read separately. Keys is float and cannot carry a 32 bit mask.
            if (CurveType == AnimCurveType.StepBool)
            {
                int wordCount = (numKey + 31) / 32;
                BooleanWords = loader.LoadCustom(() => loader.ReadUInt32s(wordCount), (uint)KeyArrayOffset);

                KeyStepBoolData = new bool[numKey];
                for (int i = 0; i < numKey; i++)
                    KeyStepBoolData[i] = (BooleanWords[i >> 5] & (1u << (i & 31))) != 0;

                Keys = new float[wordCount, 1];
                for (int i = 0; i < wordCount; i++)
                    Keys[i, 0] = BooleanWords[i];
                return;
            }

            Keys = loader.LoadCustom(() =>
            {
                int elementsPerKey = ElementsPerKey;
                float[,] keys = new float[numKey, elementsPerKey];
                switch (KeyType)
                {
                    case AnimCurveKeyType.Single:
                        for (int i = 0; i < numKey; i++)
                        {
                            for (int j = 0; j < elementsPerKey; j++)
                            {
                                if (CurveType == AnimCurveType.StepInt)
                                    keys[i, j] = loader.ReadUInt32();
                                else
                                    keys[i, j] = loader.ReadSingle();
                            }
                        }
                        break;
                    case AnimCurveKeyType.Int16:
                        for (int i = 0; i < numKey; i++)
                        {
                            for (int j = 0; j < elementsPerKey; j++)
                            {
                                keys[i, j] = loader.ReadInt16();
                            }
                        }
                        break;
                    case AnimCurveKeyType.SByte:
                        for (int i = 0; i < numKey; i++)
                        {
                            for (int j = 0; j < elementsPerKey; j++)
                            {
                                keys[i, j] = loader.ReadSByte();
                            }
                        }
                        break;
                    default:
                        throw new ResException($"Invalid {nameof(KeyType)}.");
                }
                return keys;
            }, (uint)KeyArrayOffset);
        }

        private void LoadValueKeys(ResFileLoader loader, int numKey, long frameOffset, long keyOffset)
        {
            float frameScale = Scale;
            float valueScale = Offset;
            float valueOffset = Delta;
            bool frameScaled = (_flags & _flagsFrameScaled) != 0;
            Frames = new float[numKey];
            using (loader.TemporarySeek(frameOffset, System.IO.SeekOrigin.Begin))
            {
                for (int i = 0; i < numKey; i++)
                {
                    switch (FrameType)
                    {
                        case AnimCurveFrameType.Single:
                            Frames[i] = loader.ReadSingle();
                            break;
                        case AnimCurveFrameType.Decimal10x5:
                            Frames[i] = frameScaled ? loader.ReadUInt16() * frameScale : loader.ReadInt16() / 32f;
                            break;
                        default:
                            Frames[i] = frameScaled ? loader.ReadByte() * frameScale : loader.ReadByte();
                            break;
                    }
                }
            }

            ushort curveType = (ushort)(_flags & _flagsMaskCurveType);
            if (CurveType == AnimCurveType.StepBool)
            {
                int wordCount = (numKey + 31) / 32;
                BooleanWords = loader.LoadCustom(() => loader.ReadUInt32s(wordCount), (uint)keyOffset);
                KeyStepBoolData = new bool[numKey];
                for (int i = 0; i < numKey; i++)
                    KeyStepBoolData[i] = (BooleanWords[i >> 5] & (1u << (i & 31))) != 0;
                Keys = new float[wordCount, 1];
                for (int i = 0; i < wordCount; i++)
                    Keys[i, 0] = BooleanWords[i];
                FinishValueKeys(AnimCurveType.StepBool, 0);
                return;
            }

            bool hermite = curveType == 0;
            var values = new float[numKey];
            var slopes = hermite ? new float[numKey] : null;
            using (loader.TemporarySeek(keyOffset, System.IO.SeekOrigin.Begin))
            {
                float slopeScale = 0, slopeOffset = 0;
                if (hermite && (_flags & _flagsSlopeQuantized) != 0)
                {
                    slopeScale = loader.ReadSingle();
                    slopeOffset = loader.ReadSingle();
                }
                for (int i = 0; i < numKey; i++)
                {
                    values[i] = ReadQuantizedValue(loader, valueScale, valueOffset);
                    if (hermite)
                        slopes[i] = ReadQuantizedValue(loader, slopeScale, slopeOffset);
                }
            }

            if (hermite)
            {
                HermiteValues = values;
                HermiteSlopes = slopes;
                // The rest of the library and the viewer read cubic coefficients over each segment.
                Keys = new float[numKey, 4];
                for (int i = 0; i < numKey; i++)
                {
                    float p0 = values[i];
                    Keys[i, 0] = p0;
                    if (i + 1 >= numKey)
                        continue;
                    float dt = Frames[i + 1] - Frames[i];
                    if (dt <= 0)
                        continue;
                    float m0 = slopes[i] * dt;
                    float p1 = values[i + 1];
                    float m1 = slopes[i + 1] * dt;
                    Keys[i, 1] = m0;
                    Keys[i, 2] = 3 * (p1 - p0) - 2 * m0 - m1;
                    Keys[i, 3] = 2 * (p0 - p1) + m0 + m1;
                }
                FinishValueKeys(AnimCurveType.Cubic, numKey > 0 ? values[numKey - 1] - values[0] : 0);
            }
            else if (CurveType == AnimCurveType.StepInt)
            {
                Keys = new float[numKey, 1];
                for (int i = 0; i < numKey; i++)
                    Keys[i, 0] = (float)System.Math.Round(values[i]);
                FinishValueKeys(AnimCurveType.StepInt, 0);
            }
            else
            {
                // A linear key holds its value and the change to the next; a held value has none.
                bool linear = curveType == (ushort)AnimCurveType.Linear;
                Keys = new float[numKey, 2];
                for (int i = 0; i < numKey; i++)
                {
                    Keys[i, 0] = values[i];
                    if (linear && i + 1 < numKey)
                        Keys[i, 1] = values[i + 1] - values[i];
                }
                FinishValueKeys(AnimCurveType.Linear, numKey > 0 ? values[numKey - 1] - values[0] : 0);
            }
        }

        private float ReadQuantizedValue(ResFileLoader loader, float scale, float offset)
        {
            switch (KeyType)
            {
                case AnimCurveKeyType.Single:
                    return loader.ReadSingle();
                case AnimCurveKeyType.Int16:
                    return loader.ReadUInt16() * scale + offset;
                default:
                    return loader.ReadByte() * scale + offset;
            }
        }

        private void FinishValueKeys(AnimCurveType type, float delta)
        {
            _flags = (ushort)(_flags & ~(_flagsValueKeys | _flagsSlopeQuantized | _flagsFrameScaled));
            FrameType = AnimCurveFrameType.Single;
            KeyType = AnimCurveKeyType.Single;
            CurveType = type;
            Scale = 1;
            Offset = 0;
            Delta = delta;
        }

        internal long PosFrameOffset;
        internal long PosKeyDataOffset;
        internal bool SaveEntryBlock = true;

        void IResData.Save(ResFileSaver saver)
        {
            UpdateBooleanKeys();

            if (saver.IsSwitch)
            {
                _valueKeysOut = UsesValueKeys(saver.ResFile) ? EncodeValueKeys() : null;
                PosFrameOffset = saver.SaveOffset();
                PosKeyDataOffset = saver.SaveOffset();
                if (_valueKeysOut != null)
                {
                    saver.Write(_valueKeysOut.Flags);
                    saver.Write((ushort)_valueKeysOut.Frames.Length);
                    saver.Write(AnimDataOffset);
                    saver.Write(StartFrame);
                    saver.Write(EndFrame);
                    saver.Write(1f);
                    saver.Write(1f);
                    saver.Write(0f);
                    saver.Write(0);
                    return;
                }
                saver.Write(_flags);
                saver.Write((ushort)Frames.Length);
                saver.Write(AnimDataOffset);
                saver.Write(StartFrame);
                saver.Write(EndFrame);
                saver.Write(Scale);
                saver.Write(Offset);
                saver.Write(Delta);
                saver.Write(0);
            }
            else
            {
                saver.Write(_flags);
                saver.Write((ushort)Frames.Length);
                saver.Write(AnimDataOffset);
                saver.Write(StartFrame);
                saver.Write(EndFrame);
                saver.Write(Scale);
                saver.Write(Offset);
                if (saver.ResFile.Version >= 0x03040000)
                {
                    saver.Write(Delta);
                }

                if (SaveEntryBlock)
                {
                    saver.SaveCustom(Frames, () =>
                    {
                        SaveFrames(saver);
                    });
                    saver.SaveCustom(Keys, () =>
                    {
                        SaveKeyData(saver);
                    });
                }
                else
                {
                    PosFrameOffset = saver.SaveOffsetPos();
                    PosKeyDataOffset = saver.SaveOffsetPos();
                }
            }
        }

        private void UpdateBooleanKeys()
        {
            if (CurveType != AnimCurveType.StepBool)
                return;

            var keyData = KeyStepBoolData ?? new bool[0];
            var words = new uint[(keyData.Length + 31) / 32];

            for (int i = 0; i < keyData.Length; i++)
                if (keyData[i])
                    words[i >> 5] |= 1u << (i & 31);

            BooleanWords = words;

            this.Keys = new float[words.Length, 1];
            for (int i = 0; i < words.Length; i++)
                this.Keys[i, 0] = words[i];
        }

        private sealed class ValueKeyCurve
        {
            internal ushort Flags;
            internal float[] Frames;
            internal float[] Values;
            internal uint[] Words;
        }

        private ValueKeyCurve _valueKeysOut;

        private static bool UsesValueKeys(ResFile file) => file.VersionMajor == 10 && file.VersionMinor >= 3;

        private ValueKeyCurve EncodeValueKeys()
        {
            int n = Frames.Length;
            ushort wrap = (ushort)(_flags & 0x3300);
            var frames = new List<float>();
            var values = new List<float>();
            switch (CurveType)
            {
                case AnimCurveType.Cubic:
                    for (int i = 0; i < n; i++)
                    {
                        float f = Frames[i];
                        float outValue = Keys[i, 0] * Scale + Offset.Single;
                        float outDt = i + 1 < n ? Frames[i + 1] - f : 0;
                        float outSlope = outDt > 0 ? Keys[i, 1] * Scale / outDt : 0;
                        // A key read from a pair at one frame already holds the in side, so it is not doubled again.
                        if (i == n - 1 && i > 0 && f <= Frames[i - 1])
                            outSlope = values[values.Count - 1];
                        if (i > 0 && f > Frames[i - 1])
                        {
                            float inDt = f - Frames[i - 1];
                            float inValue = (Keys[i - 1, 0] + Keys[i - 1, 1] + Keys[i - 1, 2] + Keys[i - 1, 3]) * Scale + Offset.Single;
                            float inSlope = (Keys[i - 1, 1] + 2 * Keys[i - 1, 2] + 3 * Keys[i - 1, 3]) * Scale / inDt;
                            if (i == n - 1 || outDt <= 0)
                                outSlope = inSlope;
                            // A slope change counts by how far it can move the value over the longer
                            // neighbouring segment, and both within the rounding of a summed segment.
                            float span = 0.25f * System.Math.Max(inDt, outDt);
                            float magnitude = System.Math.Max(System.Math.Abs(Keys[i - 1, 0] * Scale + Offset.Single), System.Math.Max(System.Math.Abs(inValue), System.Math.Abs(outValue)));
                            if (!Near(inValue, outValue, magnitude) || !Near(inSlope * span, outSlope * span, magnitude))
                            {
                                frames.Add(f);
                                values.Add(inValue);
                                values.Add(inSlope);
                            }
                        }
                        frames.Add(f);
                        values.Add(outValue);
                        values.Add(outSlope);
                    }
                    break;
                case AnimCurveType.Linear:
                    // A held float read from a 10.3 file stays one, a value per key.
                    if (IsStepFloat)
                    {
                        for (int i = 0; i < n; i++)
                        {
                            frames.Add(Frames[i]);
                            values.Add(Keys[i, 0] * Scale + Offset.Single);
                        }
                        return new ValueKeyCurve
                        {
                            Flags = (ushort)(_flagsValueKeys | wrap | _curveTypeStepFloat),
                            Frames = frames.ToArray(),
                            Values = values.ToArray(),
                        };
                    }
                    for (int i = 0; i < n; i++)
                    {
                        float outValue = Keys[i, 0] * Scale + Offset.Single;
                        if (i > 0)
                        {
                            float inValue = (Keys[i - 1, 0] + Keys[i - 1, 1]) * Scale + Offset.Single;
                            if (!Near(inValue, outValue))
                            {
                                frames.Add(Frames[i]);
                                values.Add(inValue);
                            }
                        }
                        frames.Add(Frames[i]);
                        values.Add(outValue);
                    }
                    break;
                case AnimCurveType.StepInt:
                    for (int i = 0; i < n; i++)
                    {
                        frames.Add(Frames[i]);
                        values.Add(Keys[i, 0] + Offset.Int32);
                    }
                    break;
                case AnimCurveType.StepBool:
                    return new ValueKeyCurve
                    {
                        Flags = (ushort)(_flagsValueKeys | wrap | (ushort)AnimCurveType.StepBool),
                        Frames = (float[])Frames.Clone(),
                        Words = BooleanWords ?? new uint[0],
                    };
                default:
                    return null;
            }
            return new ValueKeyCurve
            {
                Flags = (ushort)(_flagsValueKeys | wrap | (ushort)CurveType),
                Frames = frames.ToArray(),
                Values = values.ToArray(),
            };
        }

        private static bool Near(float a, float b, float magnitude = 0) =>
            System.Math.Abs(a - b) <= 1e-5f * System.Math.Max(System.Math.Max(1f, magnitude), System.Math.Max(System.Math.Abs(a), System.Math.Abs(b)));

        public void SaveFrames(ResFileSaver saver)
        {
            if (_valueKeysOut != null)
            {
                saver.Write(_valueKeysOut.Frames);
                return;
            }
            switch (FrameType)
            {
                case AnimCurveFrameType.Single:
                    saver.Write(Frames);
                    break;
                case AnimCurveFrameType.Decimal10x5:
                    foreach (float frame in Frames)
                    {
                        saver.Write((Decimal10x5)frame);
                    }
                    break;
                case AnimCurveFrameType.Byte:
                    foreach (float frame in Frames)
                    {
                        saver.Write((byte)frame);
                    }
                    break;
            }
        }

        public void SaveKeyData(ResFileSaver saver)
        {
            if (_valueKeysOut != null)
            {
                if (_valueKeysOut.Words != null)
                    foreach (uint word in _valueKeysOut.Words)
                        saver.Write(word);
                else
                    saver.Write(_valueKeysOut.Values);
                return;
            }
            switch (KeyType)
            {
                case AnimCurveKeyType.Single:
                    if (CurveType == AnimCurveType.StepBool)
                    {
                        foreach (uint word in BooleanWords)
                            saver.Write(word);
                        break;
                    }
                    foreach (float key in Keys)
                    {
                        if (CurveType == AnimCurveType.StepInt)
                            saver.Write((uint)key);
                        else
                            saver.Write(key);
                    }
                    break;
                case AnimCurveKeyType.Int16:
                    foreach (float key in Keys)
                    {
                        saver.Write((short)key);
                    }
                    break;
                case AnimCurveKeyType.SByte:
                    foreach (float key in Keys)
                    {
                        saver.Write((sbyte)key);
                    }
                    break;
            }
        }
    }



    /// <summary>
    /// Represents the possible data types in which <see cref="AnimCurve.Frames"/> are stored. For simple library use,
    /// they are always converted them to and from <see cref="Single"/> instances.
    /// </summary>
    public enum AnimCurveFrameType : ushort
    {
        /// <summary>
        /// The frames are stored as <see cref="System.Single"/> instances.
        /// </summary>
        Single,

        /// <summary>
        /// The frames are stored as <see cref="Bfres.Decimal10x5"/> instances.
        /// </summary>
        Decimal10x5,

        /// <summary>
        /// The frames are stored as <see cref="System.Byte"/> instances.
        /// </summary>
        Byte
    }

    /// <summary>
    /// Represents the possible data types in which <see cref="AnimCurve.Keys"/> are stored. For simple library use,
    /// they are always converted them to and from <see cref="Single"/> instances.
    /// </summary>
    public enum AnimCurveKeyType : ushort
    {
        /// <summary>
        /// The keys are stored as <see cref="System.Single"/> instances.
        /// </summary>
        Single = 0 << 2,

        /// <summary>
        /// The keys are stored as <see cref="Bfres.Decimal10x5"/> instances.
        /// </summary>
        Int16 = 1 << 2,

        /// <summary>
        /// The keys are stored as <see cref="System.SByte"/> instances.
        /// </summary>
        SByte = 2 << 2
    }

    /// <summary>
    /// Represents the type of key values stored by this curve. This also determines the number of required elements to
    /// define a key in the <see cref="AnimCurve.Keys"/> array. Use the <see cref="AnimCurve.ElementsPerKey()"/>
    /// method to retrieve the number of elements required for the <see cref="AnimCurve.CurveType"/> of that curve.
    /// </summary>
    public enum AnimCurveType : ushort
    {
        /// <summary>
        /// The curve uses cubic interpolation. 4 elements of the <see cref="AnimCurve.Keys"/> array form a key.
        /// </summary>
        Cubic = 0 << 4,

        /// <summary>
        /// The curve uses linear interpolation. 2 elements of the <see cref="AnimCurve.Keys"/> array form a key.
        /// </summary>
        Linear = 1 << 4,

        /// <summary>
        /// 1 element of the <see cref="AnimCurve.Keys"/> array forms a key.
        /// </summary>
        BakedFloat = 2 << 4,

        /// <summary>
        /// 1 element of the <see cref="AnimCurve.Keys"/> array forms a key.
        /// </summary>
        StepInt = 4 << 4,

        /// <summary>
        /// 1 element of the <see cref="AnimCurve.Keys"/> array forms a key.
        /// </summary>
        BakedInt = 5 << 4,

        /// <summary>
        /// 1 element of the <see cref="AnimCurve.Keys"/> array forms a key.
        /// </summary>
        StepBool = 6 << 4,

        /// <summary>
        /// 1 element of the <see cref="AnimCurve.Keys"/> array forms a key.
        /// </summary>
        BakedBool = 7 << 4
    }

    public enum WrapMode
    {
        Clamp,
        Repeat,
        Mirror,
    }

    public struct AnimConstant
    {
        // ---- FIELDS -------------------------------------------------------------------------------------------------

        /// <summary>
        /// Gets or sets the memory offset relative to the start of the corresponding animation data structure to
        /// animate the field stored at that address. Note that enums exist in the specific animation which map offsets
        /// to names.
        /// </summary>
        public uint AnimDataOffset;

        public DWord Value;
    }
}