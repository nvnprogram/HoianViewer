using ShaderLibrary.IO;
using ShaderLibrary.Switch;
using System;
using System.Collections.Generic;
using System.Dynamic;
using System.IO;
using System.Linq;
using System.Runtime;
using System.Text;
using System.Threading.Tasks;

namespace ShaderLibrary
{
    public class BnshFile
    {
        /// <summary>
        /// The name of the shader file.
        /// </summary>
        public string Name { get; set; } = "dummy";

        /// <summary>
        /// Gets or sets the variation list.
        /// </summary>
        public List<ShaderVariation> Variations { get; set; } = new List<ShaderVariation>();

        /// <summary>
        /// Gets or sets the res file header.
        /// </summary>
        public BinaryHeader BinHeader; //A header shared between bnsh and other formats
        /// <summary>
        /// Gets or sets the shader header.
        /// </summary>
        public BnshHeader Header; //Bnsh header

        public int DataAlignment => (1 << BinHeader.Alignment);

        internal Stream _stream;

        public BnshFile() {
            Variations = new List<ShaderVariation>();
            BinHeader = new BinaryHeader();
            Header = new BnshHeader();

            BinHeader.Magic = 1213419074; //BNSH
            BinHeader.VersionMajor = 2;
            BinHeader.VersionMicro = 5;
            BinHeader.VersionMinor = 1;
            BinHeader.Alignment = 12;
            BinHeader.ByteOrder = 65279;
            BinHeader.BlockOffset = 96;

            Header.Magic = 1668510311; //GRSC
            Header.VariationStartOffset = 192;
            Header.ApiType = 4;
            Header.CompilerVersion = 2048;
            Header.Unknown2 = 4785117553819657;

            Header.CompilerVersion = 131330;
            Header.Unknown2 = 4785117553819657;
        }

        public BnshFile(string filePath) => BnshLoader.Read(File.OpenRead(filePath), this);

        public BnshFile(Stream stream) => BnshLoader.Read(stream, this);

        /// <summary>
        /// Saves the binary to a file stream.
        /// </summary>
        /// <param name="filePath"></param>
        public void Save(string  filePath)
        {
            using (var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write))
                Save(fs);
        }

        /// <summary>
        /// Saves the binary to a provided stream.
        /// </summary>
        /// <param name="stream"></param>
        public void Save(Stream stream)
        {
            BnshSaver saver = new BnshSaver();
            using (var writer = new BinaryDataWriter(stream))
                saver.Save(this, writer);
        }

        /// <summary>
        /// Exports variations to a singular bnsh file.
        /// </summary>
        /// <param name="filePath"></param>
        /// <param name="variation"></param>
        public void ExportVariation(string filePath, params ShaderVariation[] variation)
        {
            BnshFile bnsh = new BnshFile();
            bnsh.Header = this.Header;
            bnsh.BinHeader = this.BinHeader;
            bnsh.Name = this.Name;
            bnsh.Variations.AddRange(variation);
            bnsh.Save(filePath);
        }

        public class ShaderVariation 
        {
            private BnshShaderProgram _program;

            /// <summary>
            /// The shader program instance.
            /// </summary>
            public BnshShaderProgram BinaryProgram
            {
                get
                {
                    // Read as needed. Bnsh has 1000s of programs, so it is more efficent to load as needed.
                    if (_program == null)
                        _program = BnshLoader.ReadBnshShaderProgram(this);
                    return _program;
                } set => _program = value;
            }

            // Variation header
            internal VariationHeader header;
            // Stream to read program data
            internal Stream _stream;

            public ShaderVariation()
            {
                header = new VariationHeader();
            }

            public void Export(string filePath)
            {
                BnshFile bnsh = new BnshFile();
                bnsh.Variations.Add(this);
                bnsh.Save(filePath);
            }
        }

        public class BnshShaderProgram 
        {
            /// <summary>
            /// Gets or sets vertex shader code.
            /// </summary>
            public ShaderCode VertexShader { get; set; }
            /// <summary>
            /// Gets or sets fragment shader code.
            /// </summary>
            public ShaderCode FragmentShader { get; set; }
            /// <summary>
            /// Gets or sets geometry shader code.
            /// </summary>
            public ShaderCode GeometryShader { get; set; }
            /// <summary>
            /// Gets or sets compute shader code.
            /// </summary>
            public ShaderCode ComputeShader { get; set; }
            /// <summary>
            /// Gets or sets hull shader code.
            /// </summary>
            public ShaderCode TessellationControlShader { get; set; }
            /// <summary>
            /// Gets or sets domain shader code.
            /// </summary>
            public ShaderCode TessellationEvalShader { get; set; }

            /// <summary>
            /// Gets or sets vertex shader reflection.
            /// </summary>
            public ShaderReflectionData VertexShaderReflection { get; set; }

            /// <summary>
            /// Gets or sets fragment shader reflection.
            /// </summary>
            public ShaderReflectionData FragmentShaderReflection { get; set; }

            /// <summary>
            /// Gets or sets geometry shader reflection.
            /// </summary>
            public ShaderReflectionData GeometryShaderReflection { get; set; }

            /// <summary>
            /// Gets or sets compute shader reflection.
            /// </summary>
            public ShaderReflectionData ComputeShaderReflection { get; set; }

            /// <summary>
            /// Gets or sets hull shader reflection.
            /// </summary>
            public ShaderReflectionData TessellationControlShaderReflection { get; set; }

            /// <summary>
            /// Gets or sets domain shader reflection.
            /// </summary>
            public ShaderReflectionData TessellationEvalShaderReflection { get; set; }

            /// <summary>
            /// Gets or sets object memory data.
            /// </summary>
            public byte[] MemoryData { get; internal set; } = new byte[256];

            /// <summary>
            /// The bnsh header.
            /// </summary>
            public BnshShaderProgramHeader header;

            public BnshShaderProgram()
            {
                header = new BnshShaderProgramHeader()
                {
                    Flags = 2, ObjectSize = 256,
                    Reserved8 = 128104,
                };
            }
        }

        /// <summary>
        /// Represents shader code for a binary format.
        /// </summary>
        public class ShaderCode
        {
            /// <summary>
            /// The control shader used to configure reading byte code.
            /// This shader commonly stores constants used for the c1 buffer.
            /// </summary>
            public byte[] ControlCode;

            /// <summary>
            /// The raw shader byte code in nvn format.
            /// </summary>
            public byte[] ByteCode;

            /// <summary>
            /// Reserved data.
            /// </summary>
            public byte[] Reserved = new byte[32];
        }

        /// <summary>
        /// Represents shader reflection.
        /// This stores location info for inputs, outputs, attributes, blocks, and storage buffers.
        /// </summary>
        public class ShaderReflectionData
        {
            /// <summary>
            /// The reflection header.
            /// </summary>
            public BnshShaderReflectionHeader header;

            /// <summary>
            /// Location lookup of input variables.
            /// </summary>
            public ResDict<ResUint32> Inputs = new ResDict<ResUint32>();

            /// <summary>
            /// Location lookup of output variables.
            /// </summary>
            public ResDict<ResUint32> Outputs = new ResDict<ResUint32>();

            /// <summary>
            /// Location lookup of samplers variables.
            /// </summary>
            public ResDict<ResUint32> Samplers = new ResDict<ResUint32>();

            /// <summary>
            /// Location lookup of constant buffers.
            /// </summary>
            public ResDict<ResUint32> UniformBuffers = new ResDict<ResUint32>();

            /// <summary>
            /// Location lookup of storage buffers.
            /// </summary>
            public ResDict<ResUint32> StorageBuffers = new ResDict<ResUint32>();

            /// <summary>
            /// The slot list used to store location indices.
            /// </summary>
            internal int[] Slots = new int[0];

            public ShaderReflectionData()
            {
                header = new BnshShaderReflectionHeader();
            }

            /// <summary>
            /// Assigns slot data as values in each dictionary
            /// </summary>
            /// <param name="slots"></param>
            internal void AssignSlots(int[] slots)
            {
                void Set(ResDict<ResUint32> dict, int idx, int startIdx)
                {
                    if (slots.Length > startIdx + idx)
                        dict[idx] = new ResUint32((uint)slots[startIdx + idx]);
                }

                for (int i = 0; i < Inputs.Count; i++)
                    Set(Inputs, i, 0);
                for (int i = 0; i < Outputs.Count; i++)
                    Set(Outputs, i, this.header.OutputIdx);
                for (int i = 0; i < Samplers.Count; i++)
                    Set(Samplers, i, this.header.SamplerIdx);
                for (int i = 0; i < UniformBuffers.Count; i++)
                    Set(UniformBuffers, i, this.header.UniformBufferIdx);
                for (int i = 0; i < StorageBuffers.Count; i++)
                    Set(StorageBuffers, i, this.header.StorageBufferIdx);

                UpdateSlots();
            }

            /// <summary>
            /// Updates all slot data back from dictionaries
            /// </summary>
            public void UpdateSlots()
            {
                List<int> slots = new List<int>();

                void Set(ResDict<ResUint32> dict, ref int startIdx)
                {
                    startIdx = slots.Count;
                    foreach (var val in dict.Values)
                        slots.Add((int)val.Value);
                }

                int first = 0;
                Set(Inputs, ref first);
                Set(Outputs, ref this.header.OutputIdx);
                Set(Samplers, ref this.header.SamplerIdx);
                Set(UniformBuffers, ref this.header.UniformBufferIdx);
                Set(StorageBuffers, ref this.header.StorageBufferIdx);

                this.Slots = slots.ToArray();
            }

            public int GetInputLocation(string key)
            {
                 var index = this.Inputs.Keys.ToList().IndexOf(key);
                if (Slots.Length > index)
                    return Slots[index];
                return -1;
            }

            public int GetConstantBufferLocation(string key)
            {
                var index = this.UniformBuffers.Keys.ToList().IndexOf(key);
                if (Slots.Length > index + this.header.UniformBufferIdx)
                    return Slots[index + this.header.UniformBufferIdx];
                return -1;
            }

            public int GetSamplerLocation(string key)
            {
                var index = this.Samplers.Keys.ToList().IndexOf(key);
                if (Slots.Length > index + this.header.SamplerIdx)
                    return Slots[index + this.header.SamplerIdx];
                return -1;
            }

            public int GetOutputLocation(string key)
            {
                var index = this.Outputs.Keys.ToList().IndexOf(key);
                if (Slots.Length > index + this.header.OutputIdx)
                    return Slots[index + this.header.OutputIdx];
                return -1;
            }
        }
    }
}
