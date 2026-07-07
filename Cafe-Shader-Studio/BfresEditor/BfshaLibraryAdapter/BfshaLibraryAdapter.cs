using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ShaderLibrary;
using ShaderLibrary.Helpers;

namespace BfshaLibrary
{
    public class BfshaFile
    {
        internal ShaderLibrary.BfshaFile _inner;

        public string Name => _inner.Name;
        public string Path => _inner.Path;

        public ShaderModelCollection ShaderModels { get; private set; }

        public BfshaFile() { _inner = new ShaderLibrary.BfshaFile(); ShaderModels = new ShaderModelCollection(_inner); }
        public BfshaFile(string filePath) { _inner = new ShaderLibrary.BfshaFile(filePath); ShaderModels = new ShaderModelCollection(_inner); }
        public BfshaFile(Stream stream) { _inner = new ShaderLibrary.BfshaFile(stream); ShaderModels = new ShaderModelCollection(_inner); }

        public void Save(Stream stream, bool leaveOpen = false) => _inner.Save(stream);
        public void Save(string filePath) => _inner.Save(filePath);
    }

    public class ShaderModelCollection
    {
        private ShaderLibrary.BfshaFile _file;
        private Dictionary<string, ShaderModel> _cache = new Dictionary<string, ShaderModel>();

        internal ShaderModelCollection(ShaderLibrary.BfshaFile file) { _file = file; }

        public ShaderModel this[string key]
        {
            get
            {
                if (!_cache.ContainsKey(key))
                    _cache[key] = new ShaderModel(_file.ShaderModels[key]);
                return _cache[key];
            }
        }

        public ShaderModel this[int index]
        {
            get
            {
                string key = _file.ShaderModels.GetKey(index);
                return this[key];
            }
        }

        public int Count => _file.ShaderModels.Count;

        public string GetKey(int index) => _file.ShaderModels.GetKey(index);

        public IEnumerable<ShaderModel> Values
        {
            get
            {
                for (int i = 0; i < _file.ShaderModels.Count; i++)
                    yield return this[i];
            }
        }

        public ShaderModel FirstOrDefault(Func<ShaderModel, bool> predicate)
        {
            foreach (var m in Values)
                if (predicate(m))
                    return m;
            return null;
        }
    }

    public class ShaderModel
    {
        internal ShaderLibrary.ShaderModel _inner;

        internal ShaderModel(ShaderLibrary.ShaderModel inner)
        {
            _inner = inner;
            StaticOptions = new ShaderOptionDict(_inner.StaticOptions);
            DynamiOptions = new ShaderOptionDict(_inner.DynamicOptions);
            Attributes = new AttributeDict(_inner.Attributes);
            Samplers = new SamplerDict(_inner.Samplers);
            UniformBlocks = new UniformBlockDict(_inner.UniformBlocks);
        }

        public string Name => _inner.Name;
        public Stream BnshFileStream => _inner.BnshFile != null ? new MemoryStream() : null;

        public ShaderOptionDict StaticOptions { get; private set; }
        public ShaderOptionDict DynamiOptions { get; private set; }
        public ShaderOptionDict DynamicOptions => DynamiOptions;

        public AttributeDict Attributes { get; private set; }
        public SamplerDict Samplers { get; private set; }
        public UniformBlockDict UniformBlocks { get; private set; }

        public int ProgramCount => _inner.Programs.Count;
        public int[] KeyTable => _inner.KeyTable;
        public int StaticKeyLength => _inner.StaticKeyLength;
        public int DynamicKeyLength => _inner.DynamicKeyLength;
        public uint MaxRingItemSize => 0;

        public int GetProgramIndex(Dictionary<string, string> options)
        {
            return ShaderOptionSearcher.GetProgramIndex(_inner, options);
        }

        public ResShaderProgram GetShaderProgram(int index)
        {
            return new ResShaderProgram(_inner.Programs[index], this);
        }

        public ShaderVariation GetShaderVariation(ResShaderProgram program)
        {
            var variation = _inner.GetVariation(program._inner);
            if (variation == null) return null;
            return new ShaderVariation(variation);
        }
    }

    public class ShaderOptionDict
    {
        private ShaderLibrary.ResDict<ShaderLibrary.ShaderOption> _inner;
        private Dictionary<int, ShaderOption> _cache = new Dictionary<int, ShaderOption>();

        internal ShaderOptionDict(ShaderLibrary.ResDict<ShaderLibrary.ShaderOption> inner) { _inner = inner; }

        public int Count => _inner.Count;

        public ShaderOption this[int index]
        {
            get
            {
                if (!_cache.ContainsKey(index))
                    _cache[index] = new ShaderOption(_inner[index]);
                return _cache[index];
            }
        }

        public ShaderOption this[string key]
        {
            get
            {
                int idx = _inner.GetIndex(key);
                if (idx < 0) return null;
                return this[idx];
            }
        }

        public string GetKey(int index) => _inner.GetKey(index);

        public ShaderOptionChoiceDict GetKeys()
        {
            var keys = new List<string>();
            for (int i = 0; i < _inner.Count; i++)
                keys.Add(_inner.GetKey(i));
            return new ShaderOptionChoiceDict(keys);
        }

        public IEnumerable<ShaderOption> Values
        {
            get
            {
                for (int i = 0; i < _inner.Count; i++)
                    yield return this[i];
            }
        }
    }

    public class ShaderOptionChoiceDict
    {
        private List<string> _keys;
        internal ShaderOptionChoiceDict(List<string> keys) { _keys = keys; }

        public string GetKey(int index) => index >= 0 && index < _keys.Count ? _keys[index] : "";
        public int Count => _keys.Count;

        public IEnumerable<string> GetKeys()
        {
            return _keys;
        }

        public IEnumerator<string> GetEnumerator() => _keys.GetEnumerator();
    }

    public class ShaderOption
    {
        internal ShaderLibrary.ShaderOption _inner;

        internal ShaderOption(ShaderLibrary.ShaderOption inner)
        {
            _inner = inner;
            var keys = new List<string>();
            for (int i = 0; i < inner.Choices.Count; i++)
                keys.Add(inner.Choices.GetKey(i));
            ChoiceDict = new ShaderOptionChoiceDict(keys);
        }

        public string Name => _inner.Name;
        public ShaderOptionChoiceDict ChoiceDict { get; private set; }
        public uint[] ChoiceValues => _inner.ChoiceValues;
        public string[] choices
        {
            get
            {
                var keys = new string[_inner.Choices.Count];
                for (int i = 0; i < _inner.Choices.Count; i++)
                    keys[i] = _inner.Choices.GetKey(i);
                return keys;
            }
        }
        public string defaultChoice => _inner.DefaultChoice;

        public int bit32Index => _inner.Bit32Index;
        public int keyOffset => _inner.KeyOffset;

        public int GetChoiceIndex(int key) => _inner.GetChoiceIndex(key);
    }

    public class AttributeDict : IEnumerable<KeyValuePair<string, AttributeWrapper>>
    {
        private ShaderLibrary.ResDict<ShaderLibrary.BfshaAttribute> _inner;
        internal AttributeDict(ShaderLibrary.ResDict<ShaderLibrary.BfshaAttribute> inner) { _inner = inner; }

        public int Count => _inner.Count;
        public string GetKey(int index) => _inner.GetKey(index);
        public AttributeWrapper this[int index] => new AttributeWrapper(_inner[index]);

        public IEnumerator<KeyValuePair<string, AttributeWrapper>> GetEnumerator()
        {
            for (int i = 0; i < _inner.Count; i++)
                yield return new KeyValuePair<string, AttributeWrapper>(_inner.GetKey(i), new AttributeWrapper(_inner[i]));
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    public class AttributeWrapper
    {
        private ShaderLibrary.BfshaAttribute _inner;
        internal AttributeWrapper(ShaderLibrary.BfshaAttribute inner) { _inner = inner; }
        public sbyte Location => _inner.Location;
        public byte GX2Type => _inner.GX2Type;
        public byte GX2Count => _inner.GX2Count;
    }

    public class SamplerDict
    {
        private ShaderLibrary.ResDict<ShaderLibrary.BfshaSampler> _inner;
        internal SamplerDict(ShaderLibrary.ResDict<ShaderLibrary.BfshaSampler> inner) { _inner = inner; }

        public int Count => _inner.Count;
        public string GetKey(int index) => _inner.GetKey(index);
        public SamplerWrapper this[int index] => new SamplerWrapper(_inner[index]);

        public IEnumerable<string> GetKeys()
        {
            for (int i = 0; i < _inner.Count; i++)
                yield return _inner.GetKey(i);
        }
    }

    public class SamplerWrapper
    {
        private ShaderLibrary.BfshaSampler _inner;
        internal SamplerWrapper(ShaderLibrary.BfshaSampler inner) { _inner = inner; }
        public byte Index => _inner.Index;
        public byte GX2Type => _inner.GX2Type;
        public byte GX2Count => _inner.GX2Count;
    }

    public class UniformBlockDict
    {
        private ShaderLibrary.ResDict<ShaderLibrary.BfshaUniformBlock> _inner;
        private Dictionary<int, UniformBlock> _cache = new Dictionary<int, UniformBlock>();

        internal UniformBlockDict(ShaderLibrary.ResDict<ShaderLibrary.BfshaUniformBlock> inner) { _inner = inner; }

        public int Count => _inner.Count;
        public string GetKey(int index) => _inner.GetKey(index);

        public UniformBlock this[int index]
        {
            get
            {
                if (!_cache.ContainsKey(index))
                    _cache[index] = new UniformBlock(_inner[index]);
                return _cache[index];
            }
        }

        public IEnumerable<UniformBlock> Values
        {
            get
            {
                for (int i = 0; i < _inner.Count; i++)
                    yield return this[i];
            }
        }
    }

    public class UniformBlock
    {
        internal ShaderLibrary.BfshaUniformBlock _inner;

        internal UniformBlock(ShaderLibrary.BfshaUniformBlock inner)
        {
            _inner = inner;
            Uniforms = new UniformDict(inner.Uniforms);
        }

        public ushort Size => _inner.Size;
        public byte Index => _inner.Index;
        public BlockType Type => (BlockType)_inner.Type;

        public UniformDict Uniforms { get; private set; }

        public enum BlockType
        {
            None,
            Material,
            Shape,
            Option,
            Num,
        }
    }

    public class UniformDict : IEnumerable<KeyValuePair<string, UniformVar>>
    {
        private ShaderLibrary.ResDict<ShaderLibrary.BfshaUniform> _inner;
        internal UniformDict(ShaderLibrary.ResDict<ShaderLibrary.BfshaUniform> inner) { _inner = inner; }

        public int Count => _inner.Count;
        public string GetKey(int index) => _inner.GetKey(index);

        public UniformVar this[int index] => new UniformVar(_inner[index]);

        public IEnumerable<UniformVar> Values
        {
            get
            {
                for (int i = 0; i < _inner.Count; i++)
                    yield return new UniformVar(_inner[i]);
            }
        }

        public IEnumerator<KeyValuePair<string, UniformVar>> GetEnumerator()
        {
            for (int i = 0; i < _inner.Count; i++)
                yield return new KeyValuePair<string, UniformVar>(_inner.GetKey(i), new UniformVar(_inner[i]));
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    public class UniformVar
    {
        internal ShaderLibrary.BfshaUniform _inner;
        internal UniformVar(ShaderLibrary.BfshaUniform inner) { _inner = inner; }

        public string Name => _inner.Name;
        public ushort Offset => _inner.DataOffset;
        public int Index => _inner.Index;
        public byte BlockIndex => _inner.BlockIndex;
        public byte GX2Type => _inner.GX2Type;
        public ushort GX2Count => _inner.GX2Count;
    }

    public class ResShaderProgram
    {
        internal ShaderLibrary.BfshaShaderProgram _inner;
        internal ShaderModel _parentModel;

        internal ResShaderProgram(ShaderLibrary.BfshaShaderProgram inner, ShaderModel parent)
        {
            _inner = inner;
            _parentModel = parent;
        }

        public LocationInfo[] UniformBlockLocations
        {
            get
            {
                var result = new LocationInfo[_inner.UniformBlockIndices.Count];
                for (int i = 0; i < result.Length; i++)
                {
                    result[i] = new LocationInfo
                    {
                        VertexLocation = _inner.UniformBlockIndices[i].VertexLocation,
                        FragmentLocation = _inner.UniformBlockIndices[i].FragmentLocation
                    };
                }
                return result;
            }
        }

        public LocationInfo[] SamplerLocations
        {
            get
            {
                var result = new LocationInfo[_inner.SamplerIndices.Count];
                for (int i = 0; i < result.Length; i++)
                {
                    result[i] = new LocationInfo
                    {
                        VertexLocation = _inner.SamplerIndices[i].VertexLocation,
                        FragmentLocation = _inner.SamplerIndices[i].FragmentLocation
                    };
                }
                return result;
            }
        }

        public bool HasAttribute(int index) => _inner.IsAttributeUsed(index);

        public BfshaLibrary.WiiU.GX2VertexShaderData GX2VertexData =>
            throw new NotSupportedException("GX2 data not available for Switch shaders");
        public BfshaLibrary.WiiU.GX2PixelShaderData GX2PixelData =>
            throw new NotSupportedException("GX2 data not available for Switch shaders");
    }

    public struct LocationInfo
    {
        public int VertexLocation;
        public int FragmentLocation;
    }

    public class ShaderVariation
    {
        internal BnshFile.ShaderVariation _inner;
        internal ShaderVariation(BnshFile.ShaderVariation inner) { _inner = inner; }

        public BinaryProgram BinaryProgram => new BinaryProgram(_inner.BinaryProgram);
    }

    public class BinaryProgram
    {
        internal BnshFile.BnshShaderProgram _inner;
        internal BinaryProgram(BnshFile.BnshShaderProgram inner) { _inner = inner; }

        public ShaderInfoData ShaderInfoData => new ShaderInfoData(_inner);
        public ShaderReflection ShaderReflection => new ShaderReflection(_inner);
    }

    public class ShaderInfoData
    {
        internal BnshFile.BnshShaderProgram _inner;
        internal ShaderInfoData(BnshFile.BnshShaderProgram inner) { _inner = inner; }

        public ShaderCodeData VertexShaderCode => new ShaderCodeDataBinary(_inner.VertexShader);
        public ShaderCodeData PixelShaderCode => new ShaderCodeDataBinary(_inner.FragmentShader);
    }

    public class ShaderReflection
    {
        internal BnshFile.BnshShaderProgram _inner;
        internal ShaderReflection(BnshFile.BnshShaderProgram inner) { _inner = inner; }

        public ShaderReflectionData VertexShaderCode => _inner.VertexShaderReflection != null
            ? new ShaderReflectionData(_inner.VertexShaderReflection)
            : null;
        public ShaderReflectionData PixelShaderCode => _inner.FragmentShaderReflection != null
            ? new ShaderReflectionData(_inner.FragmentShaderReflection)
            : null;
    }

    public class ShaderReflectionData
    {
        internal BnshFile.ShaderReflectionData _inner;
        internal ShaderReflectionData(BnshFile.ShaderReflectionData inner) { _inner = inner; }

        public List<string> ShaderConstantBufferDictionary => _inner.UniformBuffers.Keys.ToList();
        public List<string> ShaderSamplerDictionary => _inner.Samplers.Keys.ToList();
        public List<string> ShaderInputDictionary => _inner.Inputs.Keys.ToList();
        public List<string> ShaderOutputDictionary => _inner.Outputs.Keys.ToList();
    }

    public abstract class ShaderCodeData { }

    public class ShaderCodeDataBinary : ShaderCodeData
    {
        internal BnshFile.ShaderCode _shaderCode;
        internal ShaderCodeDataBinary(BnshFile.ShaderCode shaderCode) { _shaderCode = shaderCode; }

        public List<Stream> BinaryData
        {
            get
            {
                if (_shaderCode == null)
                    return new List<Stream> { new MemoryStream(), new MemoryStream() };

                return new List<Stream>
                {
                    new MemoryStream(_shaderCode.ControlCode ?? Array.Empty<byte>()),
                    new MemoryStream(_shaderCode.ByteCode ?? Array.Empty<byte>())
                };
            }
        }
    }

    public enum ShaderType
    {
        VERTEX,
        PIXEL
    }
}

namespace BfshaLibrary.WiiU
{
    public struct GX2VertexShaderData
    {
        public uint[] Regs;
        public byte[] Data;
        public uint Mode;
    }

    public struct GX2PixelShaderData
    {
        public uint[] Regs;
        public byte[] Data;
        public uint Mode;
    }

    public class GX2UniformVar
    {
        public string Name;
        public uint Offset;
        public uint BlockIndex;
        public uint Count;
        public GX2ShaderVarType Type;
    }

    public class GX2UniformBlock
    {
        public string Name;
        public uint Offset;
        public ushort Size;
    }

    public class GX2SamplerVar
    {
        public string Name;
        public uint Location;
        public GX2SamplerVarType Type;
    }

    public class GX2AttributeVar
    {
        public string Name;
        public sbyte Location;
        public GX2ShaderVarType Type;
        public uint Count;
    }

    public class GX2LoopVar
    {
        public uint Offset;
        public uint Value;
    }

    public enum GX2ShaderVarType : uint { }
    public enum GX2SamplerVarType : uint
    {
        SAMPLER_CUBE = 5,
        SAMPLER_CUBE_ARRAY = 6,
    }
}
