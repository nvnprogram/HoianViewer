using System;
using System.Collections.Generic;
using System.Linq;
using ShaderLibrary;
using ShaderLibrary.Helpers;

namespace ShaderBundler
{
    /// <summary>One variation to put in the archive: its key, and the binaries behind it.</summary>
    public sealed class BundleRequest
    {
        public string Label;

        /// <summary>The complete option vector this key row is written from. Options absent
        /// from it are set to the archive's default choice.</summary>
        public IReadOnlyDictionary<string, string> KeyOptions;

        public readonly Dictionary<ShaderStage, ShaderBinary> Binaries = new();

        /// <summary>Program in <see cref="BuildSettings.BindingModel"/> whose resource
        /// location tables and used attribute mask describe what the code touches.</summary>
        public int BindingProgramIndex = -1;

        /// <summary>Archive <see cref="BindingProgramIndex"/> indexes, when it is not
        /// <see cref="BuildSettings.BindingModel"/>. One build mixes spliced programs, whose
        /// interface comes from the ubershader they were cut from, with programs copied
        /// straight out of the shipped archive, whose interface is their own.</summary>
        public ShaderModel BindingModel;

        /// <summary>Program in the template whose bnsh header, object blob and stage
        /// reflection blocks are copied. -1 leaves the defaults.</summary>
        public int TemplateProgramIndex = -1;

        /// <summary>Archive <see cref="TemplateProgramIndex"/> indexes, when it is not the
        /// build's template. A program carried over from another archive brings its own.</summary>
        public ShaderModel TemplateModel;

        public override string ToString() => Label ?? "(unnamed)";
    }

    public sealed class BuildSettings
    {
        /// <summary>Written into the bfsha header. Must match the materials'
        /// ShaderAssign.ShaderArchiveName.</summary>
        public string ArchiveName = "Hoian_UBER";

        /// <summary>Where resource location tables come from. Null uses the template.</summary>
        public ShaderModel BindingModel;

        /// <summary>Option table the key rows are packed against. Null uses the template.
        /// It only decides the bit layout of the key, so it is independent of everything
        /// else the template supplies.</summary>
        public ShaderModel OptionModel;

        /// <summary>-1 means key or nothing, which is what the product archives use.</summary>
        public int DefaultProgramIndex = -1;
    }

    public sealed class BuiltProgram
    {
        public BundleRequest Request;
        public int ProgramIndex;
        public int[] Key;
    }

    public sealed class BuildResult
    {
        public BfshaFile Bfsha;
        public ShaderModel Model;
        public readonly List<BuiltProgram> Programs = new();
        public readonly List<string> Notes = new();
        public int VariationCount;
    }

    /// <summary>
    /// Builds the in-memory archive: a clone of a shipped shader model carrying only the
    /// programs asked for.
    /// </summary>
    public sealed class BfshaBundleBuilder
    {
        readonly BfshaFile _template;
        readonly ShaderModel _templateModel;
        readonly BuildSettings _settings;

        public BfshaBundleBuilder(BfshaFile template, string modelName, BuildSettings settings)
        {
            _template = template ?? throw new ArgumentNullException(nameof(template));
            if (!template.ShaderModels.ContainsKey(modelName))
                throw new ArgumentException($"template archive has no shader model '{modelName}'");
            _templateModel = template.ShaderModels[modelName];
            _settings = settings ?? new BuildSettings();

            KeyOrder.AssertDynamicKeyOffsets(_templateModel, $"template '{modelName}'");
            if (_settings.OptionModel != null)
                KeyOrder.AssertDynamicKeyOffsets(_settings.OptionModel, "option model");
        }

        public BuildResult Build(IReadOnlyList<BundleRequest> requests)
        {
            var t = _templateModel;
            var opt = _settings.OptionModel ?? t;

            var bnsh = new BnshFile
            {
                BinHeader = t.BnshFile.BinHeader,
                Header = t.BnshFile.Header,
                Name = t.BnshFile.Name,
                Variations = new List<BnshFile.ShaderVariation>(),
            };

            var model = new ShaderModel
            {
                Name = t.Name,
                StaticOptions = CopyOptions(opt.StaticOptions),
                DynamicOptions = CopyOptions(opt.DynamicOptions),
                Samplers = CopySamplers(t.Samplers),
                Attributes = CopyAttributes(t.Attributes),
                UniformBlocks = CopyBlocks(t.UniformBlocks),
                StorageBuffers = CopyStorage(t.StorageBuffers),
                Images = CopyImages(t.Images),
                SymbolData = t.SymbolData,
                StaticKeyLength = opt.StaticKeyLength,
                DynamicKeyLength = opt.DynamicKeyLength,
                Unknown2 = t.Unknown2,
                BlockIndices = (byte[])t.BlockIndices?.Clone(),
                UnknownIndices2 = (byte[])t.UnknownIndices2?.Clone(),
                MaxRingItemSize = t.MaxRingItemSize,
                MaxVSRingItemSize = t.MaxVSRingItemSize,
                UnknownTable = (byte[])t.UnknownTable?.Clone(),
                DefaultProgramIndex = -1,
                BnshFile = bnsh,
                Programs = new List<BfshaShaderProgram>(),
                KeyTable = Array.Empty<int>(),
            };

            var bfsha = new BfshaFile
            {
                BinHeader = _template.BinHeader,
                Name = _settings.ArchiveName,
                Path = _template.Path,
                IsWiiU = _template.IsWiiU,
                Flags = _template.Flags,
                StringPool = _template.StringPool,
            };
            bfsha.ShaderModels.Add(model.Name, model);

            var result = new BuildResult { Bfsha = bfsha, Model = model };
            int stride = model.StaticKeyLength + model.DynamicKeyLength;
            var platform = SplicePlatform.For(NvnPlatform.Of(t.BnshFile));

            var keys = new List<int>(requests.Count * stride);
            var rowOwner = new Dictionary<string, RowOwner>(StringComparer.Ordinal);
            var rowConflicts = new List<string>();

            foreach (var req in requests)
            {
                var templateBp =
                    req.TemplateProgramIndex >= 0
                        ? (req.TemplateModel ?? t)
                            .GetVariation(req.TemplateProgramIndex)
                            ?.BinaryProgram
                        : null;

                var binaryProgram = new BnshFile.BnshShaderProgram();
                if (templateBp != null)
                {
                    binaryProgram.header = templateBp.header;
                    binaryProgram.header.ObjectSize = (uint)(templateBp.MemoryData?.Length ?? 256);
                }

                if (req.Binaries.Count == 0)
                    throw new InvalidOperationException($"{req}: no stage binaries");

                foreach (var kv in req.Binaries)
                {
                    var bin = kv.Value;
                    if (bin?.ByteCode == null || bin.ByteCode.Length == 0)
                        throw new InvalidOperationException($"{req}/{kv.Key}: empty byte code");
                    if (bin.ControlCode == null || bin.ControlCode.Length == 0)
                        throw new InvalidOperationException($"{req}/{kv.Key}: empty control code");

                    if (bin.ByteCode.Length % 256 != 0)
                        result.Notes.Add(
                            $"{req}/{kv.Key}: bytecode is {bin.ByteCode.Length} B, not a "
                                + "multiple of 256, so the next shader in the bnsh is misaligned"
                        );
                    if (platform.CheckControl(bin.ControlCode) is string problem)
                        result.Notes.Add($"{req}/{kv.Key}: {problem}");

                    AssignStage(
                        binaryProgram,
                        kv.Key,
                        new BnshFile.ShaderCode
                        {
                            ByteCode = bin.ByteCode,
                            ControlCode = bin.ControlCode,
                            Reserved = StageReserved(templateBp, kv.Key) ?? new byte[32],
                        },
                        templateBp
                    );
                }

                if (templateBp?.MemoryData != null)
                    binaryProgram.MemoryData = (byte[])templateBp.MemoryData.Clone();
                if (platform.Platform.HasStageInfo)
                    SetStageInfo(binaryProgram, templateBp);

                int variationIndex = bnsh.Variations.Count;
                bnsh.Variations.Add(new BnshFile.ShaderVariation { BinaryProgram = binaryProgram });

                var prog = new BfshaShaderProgram { VariationIndex = variationIndex, Flags = 0 };
                ApplyBindings(prog, model, req);

                int programIndex = model.Programs.Count;
                model.Programs.Add(prog);
                keys.AddRange(WriteKeyRow(model, req.KeyOptions));

                string rowSig = RowSignature(keys, programIndex, stride);
                if (rowOwner.TryGetValue(rowSig, out var owner))
                {
                    model.Programs.RemoveAt(programIndex);
                    keys.RemoveRange(programIndex * stride, stride);
                    bnsh.Variations.RemoveAt(variationIndex);

                    if (!SameCode(owner.Program, binaryProgram))
                        rowConflicts.Add(
                            $"key row {RowHex(owner.Row)} is claimed by two cells whose code differs."
                        );
                    else
                        result.Notes.Add(
                            $"{req} shares its key row and code with {owner.Label}, so one program "
                                + "serves both"
                        );
                    continue;
                }

                var row = new int[stride];
                keys.CopyTo(programIndex * stride, row, 0, stride);
                rowOwner[rowSig] = new RowOwner
                {
                    Label = req.ToString(),
                    Row = row,
                    Program = binaryProgram,
                };
                result.Programs.Add(
                    new BuiltProgram
                    {
                        Request = req,
                        ProgramIndex = programIndex,
                        Key = row,
                    }
                );
            }

            if (rowConflicts.Count > 0)
                throw new InvalidOperationException(
                    $"{rowConflicts.Count} key row collision(s) between cells whose code differs; the archive "
                        + $"is ambiguous and was not built. First: {rowConflicts[0]}"
                );

            model.KeyTable = keys.ToArray();
            model.DefaultProgramIndex = _settings.DefaultProgramIndex;
            SortByKey(model, result);

            result.VariationCount = bnsh.Variations.Count;
            return result;
        }

        /// <summary>
        /// Puts the programs and their key rows in the order the shipped archives use. Only
        /// the program list and the key table move: a program carries its own variation
        /// index, so no binary is touched by the permutation.
        /// </summary>
        static void SortByKey(ShaderModel model, BuildResult result)
        {
            int n = model.Programs.Count;
            if (n < 2)
                return;
            int stride = model.StaticKeyLength + model.DynamicKeyLength;
            var table = model.KeyTable;

            var order = Enumerable.Range(0, n).ToArray();
            Array.Sort(
                order,
                (a, b) =>
                {
                    int c = KeyOrder.CanonicalCmp(table, a * stride, table, b * stride, stride);
                    return c != 0 ? c : a.CompareTo(b);
                }
            );

            var oldToNew = new int[n];
            for (int i = 0; i < n; i++)
                oldToNew[order[i]] = i;

            var progs = new List<BfshaShaderProgram>(n);
            var sorted = new int[n * stride];
            for (int i = 0; i < n; i++)
            {
                progs.Add(model.Programs[order[i]]);
                Array.Copy(table, order[i] * stride, sorted, i * stride, stride);
            }
            model.Programs = progs;
            model.KeyTable = sorted;
            foreach (var bp in result.Programs)
                bp.ProgramIndex = oldToNew[bp.ProgramIndex];
        }

        /// <summary>
        /// One program's key row, written by the same code the lookup uses. An option absent
        /// from the vector lands on the archive's default choice, and a choice the archive
        /// does not declare is an error rather than a silent miss.
        /// </summary>
        static int[] WriteKeyRow(ShaderModel model, IReadOnlyDictionary<string, string> options)
        {
            var known = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var kv in options)
            {
                var o =
                    model.StaticOptions.ContainsKey(kv.Key) ? model.StaticOptions[kv.Key]
                    : model.DynamicOptions.ContainsKey(kv.Key) ? model.DynamicOptions[kv.Key]
                    : null;
                if (o == null)
                    continue;
                if (o.Choices.GetIndex(kv.Value) < 0)
                    throw new InvalidOperationException(
                        $"'{kv.Value}' is not a choice the archive declares for option '{o.Name}'"
                    );
                known[kv.Key] = kv.Value;
            }
            return ShaderOptionSearcher.WriteOptionKeys(model, known)
                ?? throw new InvalidOperationException("the key writer refused the option set");
        }

        sealed class RowOwner
        {
            public string Label;
            public int[] Row;
            public BnshFile.BnshShaderProgram Program;
        }

        static bool SameCode(BnshFile.BnshShaderProgram a, BnshFile.BnshShaderProgram b) =>
            SameStage(a.VertexShader, b.VertexShader)
            && SameStage(a.FragmentShader, b.FragmentShader);

        static bool SameStage(BnshFile.ShaderCode x, BnshFile.ShaderCode y)
        {
            if (x?.ByteCode == null || y?.ByteCode == null)
                return x?.ByteCode == null && y?.ByteCode == null;
            return x.ByteCode.AsSpan().SequenceEqual(y.ByteCode)
                && (x.ControlCode ?? Array.Empty<byte>())
                    .AsSpan()
                    .SequenceEqual(y.ControlCode ?? Array.Empty<byte>());
        }

        static string RowSignature(List<int> table, int program, int stride)
        {
            var sb = new System.Text.StringBuilder(stride * 9);
            for (int i = 0; i < stride; i++)
                sb.Append(((uint)table[program * stride + i]).ToString("X8")).Append(',');
            return sb.ToString();
        }

        static string RowHex(int[] row) =>
            string.Join(",", row.Select(x => ((uint)x).ToString("X8")));

        /// <summary>
        /// Copies the resource location tables and used attribute mask onto a new program.
        /// They describe what the binary touches, and a specialised splice inherits the
        /// ubershader program's interface, so that program is the source.
        /// </summary>
        void ApplyBindings(BfshaShaderProgram prog, ShaderModel model, BundleRequest req)
        {
            prog.ResetLocations(model);

            var src = req.BindingModel ?? _settings.BindingModel ?? _templateModel;
            int index = req.BindingProgramIndex;
            if (index < 0 || index >= src.Programs.Count)
                throw new InvalidOperationException(
                    $"{req}: no binding program, so every resource location would stay at -1"
                );

            var from = src.Programs[index];
            CopyByName(from.SamplerIndices, src.Samplers, prog.SamplerIndices, model.Samplers);
            CopyByName(
                from.UniformBlockIndices,
                src.UniformBlocks,
                prog.UniformBlockIndices,
                model.UniformBlocks
            );
            CopyByName(
                from.StorageBufferIndices,
                src.StorageBuffers,
                prog.StorageBufferIndices,
                model.StorageBuffers
            );
            CopyByName(from.ImageIndices, src.Images, prog.ImageIndices, model.Images);

            //Attribute flags are positional, so remap through the names: a binding archive
            //with a different attribute order would otherwise corrupt them.
            prog.UsedAttributeFlags = 0;
            for (int i = 0; i < src.Attributes.Count; i++)
            {
                if ((from.UsedAttributeFlags >> i & 1) == 0)
                    continue;
                int dst = model.Attributes.GetIndex(src.Attributes.GetKey(i));
                if (dst >= 0)
                    prog.UsedAttributeFlags |= 1u << dst;
            }
            prog.Flags = from.Flags;
        }

        static void CopyByName<T>(
            List<ShaderIndexHeader> srcList,
            ResDict<T> srcDict,
            List<ShaderIndexHeader> dstList,
            ResDict<T> dstDict
        )
            where T : IResData, new()
        {
            for (int i = 0; i < srcList.Count && i < srcDict.Count; i++)
            {
                int dst = dstDict.GetIndex(srcDict.GetKey(i));
                if (dst < 0 || dst >= dstList.Count)
                    continue;
                dstList[dst] = new ShaderIndexHeader
                {
                    VertexLocation = srcList[i].VertexLocation,
                    GeoemetryLocation = srcList[i].GeoemetryLocation,
                    FragmentLocation = srcList[i].FragmentLocation,
                    ComputeLocation = srcList[i].ComputeLocation,
                };
            }
        }

        static void AssignStage(
            BnshFile.BnshShaderProgram p,
            ShaderStage stage,
            BnshFile.ShaderCode code,
            BnshFile.BnshShaderProgram template
        )
        {
            if (stage == ShaderStage.Vertex)
            {
                p.VertexShader = code;
                p.VertexShaderReflection = template?.VertexShaderReflection;
            }
            else
            {
                p.FragmentShader = code;
                p.FragmentShaderReflection = template?.FragmentShaderReflection;
            }
        }

        /// <summary>
        /// The block an NVN2 stage points at, which ends in a hash of the program, the same for
        /// both stages and different for every program in a shipped archive. A copied program
        /// keeps its own; a new one gets a hash of its code.
        /// </summary>
        static void SetStageInfo(BnshFile.BnshShaderProgram p, BnshFile.BnshShaderProgram template)
        {
            byte[] info = null;
            if (template != null && SameCode(p, template))
                info = template.VertexShader?.StageInfo ?? template.FragmentShader?.StageInfo;
            if (info == null)
            {
                var hash = new System.IO.Hashing.XxHash64();
                foreach (var code in new[] { p.VertexShader, p.FragmentShader })
                {
                    hash.Append(code?.ByteCode ?? Array.Empty<byte>());
                    hash.Append(code?.ControlCode ?? Array.Empty<byte>());
                }
                info = new byte[0x18];
                hash.GetCurrentHash().CopyTo(info, 0x10);
            }
            foreach (var code in new[] { p.VertexShader, p.FragmentShader })
                if (code != null)
                    code.StageInfo = (byte[])info.Clone();
        }

        static byte[] StageReserved(BnshFile.BnshShaderProgram template, ShaderStage stage)
        {
            var code =
                stage == ShaderStage.Vertex ? template?.VertexShader : template?.FragmentShader;
            return code?.Reserved != null ? (byte[])code.Reserved.Clone() : null;
        }

        //Copies of the template's tables, so a built model a caller changes or saves with
        //ShaderLibrary's saver, which rewrites them, leaves the template intact.

        static ResDict<ShaderOption> CopyOptions(ResDict<ShaderOption> src)
        {
            var d = new ResDict<ShaderOption>();
            for (int i = 0; i < src.Count; i++)
            {
                var o = src[i];
                if (o == null)
                {
                    d.Add(src.GetKey(i), null);
                    continue;
                }
                var c = new ShaderOption
                {
                    Name = o.Name,
                    BlockOffset = o.BlockOffset,
                    Padding = o.Padding,
                    DefaultChoiceIdx = o.DefaultChoiceIdx,
                    KeyOffset = o.KeyOffset,
                    Bit32Mask = o.Bit32Mask,
                    Bit32Index = o.Bit32Index,
                    Bit32Shift = o.Bit32Shift,
                    Padding2 = o.Padding2,
                    Flags = o.Flags,
                    ChoiceValues = (uint[])o.ChoiceValues?.Clone() ?? Array.Empty<uint>(),
                    Choices = new ResDict<ResUint32>(),
                };
                //The loader leaves the choice dict's values null and carries the numbers in
                //ChoiceValues, so a choice is a name and nothing else here.
                for (int j = 0; j < o.Choices.Count; j++)
                {
                    var choice = o.Choices[j];
                    c.Choices.Add(
                        o.Choices.GetKey(j),
                        choice == null ? null : new ResUint32(choice.Value)
                    );
                }
                d.Add(src.GetKey(i), c);
            }
            return d;
        }

        static ResDict<BfshaUniformBlock> CopyBlocks(ResDict<BfshaUniformBlock> src)
        {
            var d = new ResDict<BfshaUniformBlock>();
            for (int i = 0; i < src.Count; i++)
            {
                var b = src[i];
                if (b == null)
                {
                    d.Add(src.GetKey(i), null);
                    continue;
                }
                var c = new BfshaUniformBlock
                {
                    header = b.header,
                    DefaultBuffer = (byte[])b.DefaultBuffer?.Clone(),
                    Uniforms = new ResDict<BfshaUniform>(),
                };
                for (int j = 0; j < b.Uniforms.Count; j++)
                {
                    var u = b.Uniforms[j];
                    if (u == null)
                    {
                        c.Uniforms.Add(b.Uniforms.GetKey(j), null);
                        continue;
                    }
                    c.Uniforms.Add(
                        b.Uniforms.GetKey(j),
                        new BfshaUniform
                        {
                            Name = u.Name,
                            Index = u.Index,
                            DataOffset = u.DataOffset,
                            BlockIndex = u.BlockIndex,
                            GX2Count = u.GX2Count,
                            GX2Type = u.GX2Type,
                            GX2ParamType = u.GX2ParamType,
                        }
                    );
                }
                d.Add(src.GetKey(i), c);
            }
            return d;
        }

        static ResDict<BfshaSampler> CopySamplers(ResDict<BfshaSampler> src)
        {
            var d = new ResDict<BfshaSampler>();
            for (int i = 0; i < src.Count; i++)
            {
                var s = src[i];
                if (s == null)
                {
                    d.Add(src.GetKey(i), null);
                    continue;
                }
                d.Add(
                    src.GetKey(i),
                    new BfshaSampler
                    {
                        Annotation = s.Annotation,
                        Index = s.Index,
                        GX2Type = s.GX2Type,
                        GX2Count = s.GX2Count,
                    }
                );
            }
            return d;
        }

        static ResDict<BfshaAttribute> CopyAttributes(ResDict<BfshaAttribute> src)
        {
            var d = new ResDict<BfshaAttribute>();
            for (int i = 0; i < src.Count; i++)
            {
                var a = src[i];
                if (a == null)
                {
                    d.Add(src.GetKey(i), null);
                    continue;
                }
                d.Add(
                    src.GetKey(i),
                    new BfshaAttribute
                    {
                        Index = a.Index,
                        Location = a.Location,
                        GX2Type = a.GX2Type,
                        GX2Count = a.GX2Count,
                    }
                );
            }
            return d;
        }

        static ResDict<BfshaStorageBuffer> CopyStorage(ResDict<BfshaStorageBuffer> src)
        {
            var d = new ResDict<BfshaStorageBuffer>();
            for (int i = 0; i < src.Count; i++)
                d.Add(
                    src.GetKey(i),
                    src[i] == null
                        ? null
                        : new BfshaStorageBuffer { Unknowns = (uint[])src[i].Unknowns?.Clone() }
                );
            return d;
        }

        static ResDict<BfshaImageBuffer> CopyImages(ResDict<BfshaImageBuffer> src)
        {
            var d = new ResDict<BfshaImageBuffer>();
            for (int i = 0; i < src.Count; i++)
                d.Add(src.GetKey(i), new BfshaImageBuffer());
            return d;
        }
    }
}
