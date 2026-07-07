using ShaderLibrary.Common;
using ShaderLibrary.Helpers;
using ShaderLibrary.WiiU;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;
using System.Xml.Serialization;
using static ShaderLibrary.IntermediateShader;

namespace ShaderLibrary
{
    /// <summary>
    /// Represents an intermediate shader format that stores glsl meta data for shader conversion.
    /// </summary>
    public class IntermediateShader
    {
        public List<ShaderModelInfo> ShaderModels { get; set; } = new List<ShaderModelInfo>();

        public byte[] VertexShaderSource = new byte[0];
        public byte[] FragmentShaderSource = new byte[0];
        public byte[] GeometryShaderSource = new byte[0];
        public byte[] ComputeShaderSource = new byte[0];

        public string Name { get; set; } = "Dummy";

        [XmlIgnore]
        private ShaderModelInfo _currentModel;

        public IntermediateShader() { }

        public static IntermediateShader CreateFromFolder(string folder)
        {
            // Build the shader from source code
            IntermediateShader shader = new IntermediateShader();
            shader.Name = new DirectoryInfo(folder).Name;

            void LoadShaderModel(string modelFolder)
            {
                string modelName = new DirectoryInfo(modelFolder).Name;

                string vertexShaders = "";
                string pixelShaders = "";
                // Get main source (vertex/fragment) and compile
                // The user should only have one .vert and one .frag
                // Then .glsl for any other external shaders
                // Use glsl loader so we can gather includes and other external shaders
                foreach (var file in Directory.GetFiles(modelFolder))
                {
                    if (file.EndsWith(".vert"))
                        vertexShaders = GLSLShaderLoader.LoadShader(file);
                    else if (file.EndsWith(".frag"))
                        pixelShaders = GLSLShaderLoader.LoadShader(file);
                }

                if (!string.IsNullOrEmpty(vertexShaders))
                    shader.AddShader(new GLSLParser(vertexShaders), true, modelName);
                if (!string.IsNullOrEmpty(pixelShaders))
                    shader.AddShader(new GLSLParser(pixelShaders), false, modelName);
            }

            // The user can either do multiple folders for shader models or do one for singular model
            var dirs = Directory.GetDirectories(folder);
            if (dirs.Length > 0)
            {
                foreach (var sub in dirs)
                    LoadShaderModel(sub);
            }
            else
                LoadShaderModel(folder);

            foreach (var shaderModel in shader.ShaderModels)
                shaderModel.BuildLookupData();

            return shader;
        }

        public static IntermediateShader LoadFromXml(string path)
        {
            using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

            var serializer = new XmlSerializer(typeof(IntermediateShader));
            return (IntermediateShader)serializer.Deserialize(reader);
        }

        public void AddShader(GLSLParser glsl, bool isVertex, string modelName = "Shader")
        {
            if (isVertex)
                VertexShaderSource = Encoding.UTF8.GetBytes(glsl.Source);
            else
                FragmentShaderSource = Encoding.UTF8.GetBytes(glsl.Source);

            _currentModel = ShaderModels.FirstOrDefault(X => X.Name == modelName);

            if (_currentModel == null)
            {
                _currentModel = new ShaderModelInfo();
                _currentModel.Name = modelName;
                ShaderModels.Add(_currentModel);
            }

            foreach (var op in glsl.StaticOptions)
                if (!_currentModel.StaticOptions.Any(x => x.ID == op.Key))
                    _currentModel.StaticOptions.Add(op.Value);

            foreach (var op in glsl.DynamicOptions)
                if (!_currentModel.DynamicOptions.Any(x => x.ID == op.Key))
                    _currentModel.DynamicOptions.Add(op.Value);

            foreach (var block in glsl.UniformBlocks)
                if (!_currentModel.UniformBlocks.Any(x => x.ID == block.Key))
                    _currentModel.UniformBlocks.Add(block.Value);

            foreach (var sampler in glsl.Samplers)
                if (!_currentModel.Samplers.Any(x => x.ID == sampler.Key))
                    _currentModel.Samplers.Add(sampler.Value);

            foreach (var sampler in glsl.RenderInfos)
                if (!_currentModel.RenderInfos.Any(x => x.Name == sampler.Key))
                    _currentModel.RenderInfos.Add(sampler.Value);

            if (isVertex)
            {
                foreach (var attr in glsl.InputAttributes)
                    if (!_currentModel.Attributes.Any(x => x.ID == attr.Key))
                        _currentModel.Attributes.Add(attr.Value);
            }
        }

        public void Save(string filePath)
        {
            using (var writer = new System.IO.StringWriter())
            {
                var serializer = new XmlSerializer(typeof(IntermediateShader));
                serializer.Serialize(writer, this);
                writer.Flush();

                File.WriteAllText(filePath, writer.ToString());
            }
        }

        public BfshaFile CreateBfshaFileSwitch()
        {
            BfshaFile bfsha = new BfshaFile();
            bfsha.Name = Name;
            bfsha.Path = "";
            bfsha.IsWiiU = false;
            //Switch
            bfsha.DataAlignment = 4096;
            bfsha.BinHeader.Alignment = 12;
            bfsha.BinHeader.ByteOrder = 65279;
            bfsha.BinHeader.VersionMajor = 7;
            bfsha.BinHeader.VersionMicro = 0;
            bfsha.BinHeader.VersionMinor = 0;
            bfsha.Flags = 0;

            foreach (var model in ShaderModels) {
                ShaderModel shaderModel = CreateShaderModel(model);
                bfsha.ShaderModels.Add(shaderModel.Name, shaderModel);
            }

            return bfsha;
        }

        public BfshaFile CreateBfshaFileWiiU()
        {
            BfshaFile bfsha = new BfshaFile();
            bfsha.Name = Name;
            bfsha.Path = "";
            bfsha.IsWiiU = true;
            //Wii U
            bfsha.DataAlignment = 256;
            bfsha.BinHeader.ByteOrder = 65279;
            bfsha.BinHeader.VersionMajor = 3;
            bfsha.BinHeader.VersionMicro = 4;
            bfsha.BinHeader.VersionMinor = 2;
            bfsha.Flags = 4;

            foreach (var model in ShaderModels) {
                ShaderModel shaderModel = CreateShaderModel(model);
                bfsha.ShaderModels.Add(shaderModel.Name, shaderModel);
            }

            return bfsha;
        }

        public ShaderModel CreateShaderModel(ShaderModelInfo model)
        {
            ShaderModel shaderModel = new ShaderModel();
            shaderModel.Name = model.Name;
            shaderModel.BnshFile = new BnshFile();

            ShaderOption ConvertOption(string key, OptionMacro op)
            {
                ShaderOption option = new ShaderOption();
                option.Name = key;

                // Split choice by : (value : label)
                foreach (var choice in op.Choices)
                    option.Choices.Add(op.GetOptionChoice(choice), null);

                string defaultChoice = op.GetOptionChoice(op.DefaultChoice);
                if (!option.Choices.ContainsKey(defaultChoice))
                    option.Choices.Add(defaultChoice, null);

                option.DefaultChoiceIdx = (short)option.Choices.GetIndex(op.DefaultChoice);
                option.BlockOffset = 0;
                option.Flags = 0;
                return option;
            }

            foreach (var op in model.StaticOptions)
                shaderModel.StaticOptions.Add(op.ID, ConvertOption(op.ID, op));

            foreach (var op in model.DynamicOptions)
                shaderModel.DynamicOptions.Add(op.ID, ConvertOption(op.ID, op));

            ShaderOptionCreator.SetupOptionKeyFlags(shaderModel);

            foreach (var samp in model.Samplers.OrderBy(x => x.ID))
                shaderModel.Samplers.Add(samp.ID, new BfshaSampler()
                {
                    Index = (byte)shaderModel.Samplers.Count,
                    Annotation = "", 
                    GX2Type = (byte)GetGX2SamplerType(samp.Type),
                    GX2Count = 1,
                });

            foreach (var attr in model.Attributes)
                shaderModel.Attributes.Add(attr.ID, new BfshaAttribute()
                {
                    Index = (byte)shaderModel.Attributes.Count,
                    Location = (sbyte)attr.Location,
                    GX2Count = (byte)attr.ArrayCount,
                    GX2Type = (byte)GetValueType(attr.Type),
                });

            //unk
            shaderModel.UnknownIndices2[0] = 255;
            shaderModel.UnknownIndices2[1] = 255;
            shaderModel.UnknownIndices2[2] = 255;
            shaderModel.UnknownIndices2[3] = 255;

            //for testing
            shaderModel.DefaultProgramIndex = 0;

            foreach (var b in model.UniformBlocks)
            {
                BfshaUniformBlock block = new BfshaUniformBlock();
                block.header.Index = (byte)shaderModel.UniformBlocks.Count;
                block.DefaultBuffer = b.GetBuffer();
                block.header.Size = (ushort)b.Size;
                shaderModel.UniformBlocks.Add(b.ID, block);

                if (block.DefaultBuffer?.Length > 0)
                    block.header.Size = (ushort)block.DefaultBuffer.Length;

                //material, shape, skeleton, option block indices
                switch (b.Type)
                {
                    case BlockType.Material: shaderModel.BlockIndices[0] = block.header.Index; break;
                    case BlockType.Shape:    shaderModel.BlockIndices[1] = block.header.Index; break;
                    case BlockType.Skeleton: shaderModel.BlockIndices[2] = block.header.Index; break;
                    case BlockType.Option:   shaderModel.BlockIndices[3] = block.header.Index; break;
                }

                //block types
                switch (b.Type)
                {
                    case BlockType.Material:    block.header.Type = 1; break;
                    case BlockType.Shape:       block.header.Type = 2; break;
                    case BlockType.Skeleton:    block.header.Type = 3; break;
                    case BlockType.Option:      block.header.Type = 4; break;
                    default:                    block.header.Type = 0; break;
                }

                if (block.DefaultBuffer?.Length > 0)
                {
                    foreach (var u in b.Uniforms)
                    {
                        block.Uniforms.Add(u.ID, new BfshaUniform()
                        {
                            BlockIndex = block.Index,
                            DataOffset = (ushort)u.Offset,
                            Index = block.Uniforms.Count,
                            Name = u.ID,
                            GX2Count = (ushort)u.ArrayCount,
                            GX2Type = (byte)GetValueType(u.Type),
                            GX2ParamType = (byte)GetParamType(u.Type, u.ArrayCount)
                        });
                    }
                }

                shaderModel.KeyTable = new int[0];
            }

            return shaderModel;
        }

        static ShaderParamType GetParamType(ValueType type, uint arrayCount)
        {
            switch (type)
            {
                case ValueType.FLOAT: return ShaderParamType.Float;
                case ValueType.FLOAT2: return ShaderParamType.Float2;
                case ValueType.FLOAT3: return ShaderParamType.Float3;
                case ValueType.FLOAT4:
                    if (arrayCount == 2) //TEXSRT
                        return ShaderParamType.TexSrt;

                    return ShaderParamType.Float4;
                case ValueType.INT: return ShaderParamType.Int;
                case ValueType.INT2: return ShaderParamType.Int2;
                case ValueType.INT3: return ShaderParamType.Int3;
                case ValueType.INT4: return ShaderParamType.Int4;
                case ValueType.BOOL: return ShaderParamType.Bool;
            }
            throw new NotImplementedException($"{type} {arrayCount}");
        }

        static GX2ShaderVarType GetValueType(ValueType type)
        {
            switch (type)
            {
                case ValueType.FLOAT: return GX2ShaderVarType.FLOAT;
                case ValueType.FLOAT2: return GX2ShaderVarType.FLOAT2;
                case ValueType.FLOAT3: return GX2ShaderVarType.FLOAT3;
                case ValueType.FLOAT4: return GX2ShaderVarType.FLOAT4;
                case ValueType.INT: return GX2ShaderVarType.INT;
                case ValueType.INT2: return GX2ShaderVarType.INT2;
                case ValueType.INT3: return GX2ShaderVarType.UINT3;
                case ValueType.INT4: return GX2ShaderVarType.INT4;
                case ValueType.BOOL: return GX2ShaderVarType.BOOL;
                case ValueType.MAT2x4: return GX2ShaderVarType.FLOAT2X4;
            }
            throw new NotImplementedException($"{type}");
        }

        static GX2SamplerVarType GetGX2SamplerType(Sampler.SamplerType type)
        {
            switch (type)
            {
                case Sampler.SamplerType.Sampler2D: return GX2SamplerVarType.SAMPLER_2D;
                case Sampler.SamplerType.Sampler3D: return GX2SamplerVarType.SAMPLER_3D;
                case Sampler.SamplerType.SamplerCube: return GX2SamplerVarType.SAMPLER_CUBE;
                case Sampler.SamplerType.SamplerCubeArray: return GX2SamplerVarType.SAMPLER_CUBE_ARRAY;
                case Sampler.SamplerType.Sampler2DArray: return GX2SamplerVarType.SAMPLER_2D_ARRAY;
                case Sampler.SamplerType.Sampler1D: return GX2SamplerVarType.SAMPLER_1D;
            }
            throw new NotImplementedException($"{type}");
        }

        public class ShaderModelInfo : UIElement
        {
            /// <summary>
            /// Render info name.
            /// </summary>
            [XmlAttribute("name")]
            public string Name { get; set; }

            public List<OptionMacro> StaticOptions { get; set; } = new();
            public List<OptionMacro> DynamicOptions { get; set; } = new();
            public List<UniformBlock> UniformBlocks { get; set; } = new();
            public List<UniformBlock> StorageBlocks { get; set; } = new();
            public List<Sampler> Samplers { get; set; } = new();
            public List<Attribute> Attributes { get; set; } = new();
            public List<RenderInfo> RenderInfos { get; set; } = new();

            // For symbol name lookup when obtaining location and glsl data
            private Dictionary<string, string> samplerLookup = new();
            private Dictionary<string, string> attributeLookup = new();
            private Dictionary<string, string> uniformBlockLookup = new();
            private Dictionary<string, string> storageBlockLookup = new();

            public void BuildLookupData()
            {
                samplerLookup = Samplers.ToDictionary(s => s.ID, s => s.Symbol);
                attributeLookup = Attributes.ToDictionary(a => a.ID, a => a.Symbol);
                uniformBlockLookup = UniformBlocks.ToDictionary(u => u.ID, u => u.Symbol);
                storageBlockLookup = StorageBlocks.ToDictionary(s => s.ID, s => s.Symbol);
            }

            private static string Lookup(Dictionary<string, string> dict, string key)
                => dict.TryGetValue(key, out var value) ? value : key;
            private static string LookupReverse(Dictionary<string, string> dict, string value)
                => dict.FirstOrDefault(x => x.Value == value).Key;

            public string GetSamplerSymbolName(string name) => Lookup(samplerLookup, name);
            public string GetAttributeSymbolName(string name) => Lookup(attributeLookup, name);
            public string GetUniformBlockSymbolName(string name) => Lookup(uniformBlockLookup, name);
            public string GetStorageBlockSymbolName(string name) => Lookup(storageBlockLookup, name);

            public string GetSamplerBfshaName(string name) => LookupReverse(samplerLookup, name);
            public string GetAttributeBfshaName(string name) => LookupReverse(attributeLookup, name);
            public string GetUniformBlockBfshaName(string name) => LookupReverse(uniformBlockLookup, name);
            public string GetStorageBlockBfshaName(string name) => LookupReverse(storageBlockLookup, name);
        }

        public class RenderInfo : UIElement
        {
            /// <summary>
            /// Render info name.
            /// </summary>
            [XmlAttribute("name")]
            public string Name { get; set;
}            /// <summary>
            /// Render info type.
            /// </summary>
            [XmlAttribute("type")]
            public RenderInfoType Type { get; set; }
            /// <summary>
            /// Render info default choice/value.
            /// </summary>
            [XmlAttribute("default")]
            public string DefaultChoice { get; set; }
            /// <summary>
            /// Render info choices.
            /// </summary>
            [XmlAttribute("choices")]
            public List<string> Choices { get; set; } = new List<string>();
            /// <summary>
            /// Determines if the render info entry is necessary for materials.
            /// If set, the render info will be set with a default value in the material if not present.
            /// </summary>
            [XmlAttribute("optional")]
            public bool Optional { get; set; } = true;
            /// <summary>
            /// The type if render info. The data present in a material.
            /// </summary>
            public enum RenderInfoType
            {
                Int32,
                Float,
                String,
            }
        }

        public class OptionMacro : UIElement
        {
            [XmlAttribute("name")]
            public string ID { get; set; }
            [XmlAttribute("symbol")]
            public string Symbol;
            public List<string> Choices { get; set; } = new List<string>();
            [XmlAttribute("default")]
            [DefaultValue(null)]
            public string DefaultChoice { get; set; }
            [XmlAttribute("branch")]
            [DefaultValue(false)]
            public bool Branch = false;
            [XmlAttribute("type")]
            public string Type { get; set; }

            [XmlAttribute("skin_count_type")]
            [DefaultValue(false)]
            public bool IsSkinCount { get; set; }

            public string GetMacroChoice() => GetMacroChoice(DefaultChoice);

            public string GetMacroChoice(string value)
            {
                if (Type == "string") // string types use index of the choice
                {
                    string choice = value.Split(":").FirstOrDefault();
                    return Choices.IndexOf(choice).ToString();
                }
                else if (Type == "bool") // string types use index of the choice
                {
                    string choice = value.Split(":").FirstOrDefault();
                    return (choice == "true" || choice == "1") ? "true" : "false";
                }
                else
                    return value.Split(":").FirstOrDefault();
            }

            public string GetOptionChoice() => GetOptionChoice(DefaultChoice);
            public string GetOptionChoice(string choice)
            {
                return choice.Split(":").FirstOrDefault();
            }
        }

        public class StorageBlock
        {
            [XmlAttribute("name")]
            public string ID { get; set; }
            [XmlAttribute("symbol")]
            public string Symbol { get; set; }
            [XmlAttribute("size")]
            public uint Size { get; set; }
            [XmlAttribute("index")]
            public int Location { get; set; }
        }

        public class UniformBlock
        {
            [XmlAttribute("name")]
            public string ID { get; set; }
            [XmlAttribute("symbol")]
            public string Symbol { get; set; }
            [XmlAttribute("type")]
            public BlockType Type { get; set; }
            [XmlAttribute("index")]
            public int Location { get; set; }
            [XmlElement("uniforms")]
            public List<Uniform> Uniforms { get; set; } = new();

            [XmlIgnore()]
            public uint Size { get; set; }

            public byte[] GetBuffer()
            {
                var mem = new MemoryStream();
                using (var writer = new BinaryWriter(mem))
                {
                    foreach (var uniform in Uniforms)
                    {
                        var offset = writer.BaseStream.Position;
                        if (!string.IsNullOrEmpty(uniform.DefaultValue))
                        {
                            uniform.Offset = (uint)offset + 1;

                            string[] data_values = uniform.DefaultValue.Split(" ");
                            for (int i = 0; i < data_values.Length; i++)
                                writer.Write(float.Parse(data_values[i], CultureInfo.InvariantCulture));
                        }
                    }
                }
                return mem.ToArray();
            }
        }

        public class Uniform : UIElement
        {
            [XmlAttribute("name")]
            public string ID { get; set; }
            [XmlIgnore()]
            public uint Offset { get; set; }
            [XmlAttribute("type")]
            public ValueType Type { get; set; }
            [XmlAttribute("count")]
            public uint ArrayCount { get; set; } = 1;
            [XmlAttribute("default")]
            public string DefaultValue { get; set; }
        }

        public class Sampler : UIElement
        {
            [XmlAttribute("name")]
            public string ID { get; set; }
            [XmlAttribute("symbol")]
            public string Symbol { get; set; }
            [XmlAttribute("index")]
            public int Location { get; set; }
            [XmlAttribute("type")]
            public SamplerType Type { get; set; } = SamplerType.Sampler2D;

            public enum SamplerType
            {
                Sampler2D,
                Sampler2DArray,
                SamplerCube,
                SamplerCubeArray,
                Sampler3D,
                Sampler1D,
            }
        }

        public class Attribute : UIElement
        {
            [XmlAttribute("name")]
            public string ID { get; set; }
            [XmlAttribute("symbol")]
            public string Symbol { get; set; }
            [XmlAttribute("index")]
            public int Location { get; set; }
            [XmlAttribute("type")]
            public ValueType Type { get; set; }
            [XmlAttribute("count")]
            public uint ArrayCount { get; set; } = 1;
        }

        public class UIElement
        {
            [XmlAttribute("ui_label")]
            [DefaultValue(null)]
            public string Label { get; set; }
            [XmlAttribute("ui_group")]
            [DefaultValue(null)]
            public string Group { get; set; }
            [XmlAttribute("info")]
            [DefaultValue(null)]
            public string Description { get; set; }
            [XmlAttribute("ui_order")]
            [DefaultValue(-1)]
            public int Order { get; set; } = -1;
        }

        public enum BlockType
        {
            None,
            Option = 1,
            ResMaterial = 2,
            Material = 3,
            Shape = 4,
            Skeleton = 5,
        }

        public enum ValueType
        {
            MAT2x4,
            BOOL,
            FLOAT,
            FLOAT2,
            FLOAT3,
            FLOAT4,
            INT,
            INT2,
            INT3,
            INT4,
        }
    }
}
