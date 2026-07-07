using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Serialization;

namespace ShaderLibrary.Xml
{
    public class XmlConverter
    {
        public static string ToXml(BfshaFile bfsha)
        {
            bfsha_file xml_bfsha = new bfsha_file();

            foreach (var shaderModel in bfsha.ShaderModels.Values)
            {
                shader_model xml_shader_model = new shader_model();
                xml_shader_model.Name = shaderModel.Name;
                xml_bfsha.shader_models.Add(xml_shader_model);

                foreach (var op in shaderModel.StaticOptions.Values)
                {
                    xml_shader_model.static_options.Add(new shader_option()
                    {
                        Name = op.Name,
                        DefaultChoice = op.DefaultChoice,
                        Choices = op.Choices.Keys.ToList(),
                    });
                }
                foreach (var op in shaderModel.DynamicOptions.Values)
                {
                    xml_shader_model.dynamic_options.Add(new shader_option()
                    {
                        Name = op.Name,
                        DefaultChoice = op.DefaultChoice,
                        Choices = op.Choices.Keys.ToList(),
                    });
                }
                foreach (var samp in shaderModel.Samplers)
                {
                    xml_shader_model.samplers.Add(new sampler()
                    {
                        Name = samp.Key,
                        alt = samp.Value.Annotation,
                        Index = samp.Value.Index,
                        Gx2Count = samp.Value.GX2Count,
                        Gx2Type = samp.Value.GX2Type,
                    });
                }
                foreach (var attr in shaderModel.Attributes)
                {
                    xml_shader_model.attributes.Add(new attribute()
                    {
                        Name = attr.Key,
                        Location = attr.Value.Location,
                        Index = attr.Value.Index,
                        Gx2Count = attr.Value.GX2Count,
                        Gx2Type = attr.Value.GX2Type,
                    });
                }
                foreach (var block in shaderModel.UniformBlocks)
                {
                    List<uniform> uniforms = new List<uniform>();

                    foreach (var un in block.Value.Uniforms)
                    {
                        uniforms.Add(new uniform()
                        {
                            Index = un.Value.Index,
                            BlockIndex = un.Value.BlockIndex,
                            Name = un.Key,
                            Offset = un.Value.DataOffset,
                            Gx2Count = un.Value.GX2Count,
                            Gx2Type = un.Value.GX2Type,
                        });
                    }

                    xml_shader_model.uniform_blocks.Add(new uniform_block()
                    {
                        Name = block.Key,
                        Type = (int)block.Value.Type,
                        Size = (int)block.Value.Size,
                        Index = block.Value.Index,
                        uniforms = uniforms,
                    });
                }
                foreach (var p in shaderModel.Programs)
                {
                    var xml_program = new shader_program();

                    for (int i = 0; i < shaderModel.Samplers.Count; i++)
                    {
                        xml_program.sampler_locations.Add(new bind_info()
                        {
                            Name = shaderModel.Samplers.GetKey(i),
                            VertexLocation = p.SamplerIndices[i].VertexLocation,
                            FragmentLocation = p.SamplerIndices[i].FragmentLocation,
                        });
                    }

                    for (int i = 0; i < shaderModel.UniformBlocks.Count; i++)
                    {
                        xml_program.block_locations.Add(new bind_info()
                        {
                            Name = shaderModel.UniformBlocks.GetKey(i),
                            VertexLocation = p.UniformBlockIndices[i].VertexLocation,
                            FragmentLocation = p.UniformBlockIndices[i].FragmentLocation,
                        });
                    }

                    for (int i = 0; i < shaderModel.Attributes.Count; i++)
                    {
                        if (p.IsAttributeUsed(i))
                            xml_program.UsedAttributes.Add(shaderModel.Attributes.GetKey(i));
                    }
                    xml_shader_model.shader_programs.Add(xml_program);
                }
            }

            using (var writer = new System.IO.StringWriter())
            {
                var serializer = new XmlSerializer(typeof(bfsha_file));
                serializer.Serialize(writer, xml_bfsha);
                writer.Flush();

                return writer.ToString();
            }
        }

        public class bfsha_file
        {
            public List<shader_model> shader_models { get; set; } = new List<shader_model>();
        }

        public class shader_model
        {
            [XmlAttribute]
            public string Name;
            public List<shader_program> shader_programs { get; set; } = new List<shader_program>();
            public List<shader_option> static_options { get; set; } = new List<shader_option>();
            public List<shader_option> dynamic_options { get; set; } = new List<shader_option>();
            public List<sampler> samplers { get; set; } = new List<sampler>();
            public List<attribute> attributes { get; set; } = new List<attribute>();
            public List<uniform_block> uniform_blocks { get; set; } = new List<uniform_block>();
        }

        public class shader_program
        {
            public List<bind_info> block_locations { get; set; } = new List<bind_info>();
            public List<bind_info> sampler_locations { get; set; } = new List<bind_info>();

            [XmlAttribute]
            public List<string> UsedAttributes { get; set; } = new List<string>();
        }

        public class bind_info
        {
            [XmlAttribute]
            public string Name;
            [XmlAttribute]
            public int VertexLocation;
            [XmlAttribute]
            public int FragmentLocation;
        }

        public class shader_option
        {
            [XmlAttribute]
            public string Name;

            [XmlAttribute]
            public string DefaultChoice;

            [XmlAttribute]
            public List<string> Choices = new List<string>();
        }

        public class uniform_block
        {
            [XmlAttribute]
            public string Name;
            [XmlAttribute]
            public int Size;
            [XmlAttribute]
            public int Type;
            [XmlAttribute]
            public int Index;

            public List<uniform> uniforms = new List<uniform>();
        }

        public class uniform
        {
            [XmlAttribute]
            public string Name;
            [XmlAttribute]
            public int Index;
            [XmlAttribute]
            public int BlockIndex;
            [XmlAttribute]
            public int Offset;

            [XmlAttribute]
            public int Gx2Type;
            [XmlAttribute]
            public int Gx2Count;
        }

        public class attribute
        {
            [XmlAttribute]
            public string Name;
            [XmlAttribute]
            public int Index;
            [XmlAttribute]
            public int Location;

            [XmlAttribute]
            public int Gx2Type;
            [XmlAttribute]
            public int Gx2Count;
        }

        public class sampler
        {
            [XmlAttribute]
            public string Name;
            [XmlAttribute]
            public int Index;
            [XmlAttribute]
            public string alt;

            [XmlAttribute]
            public int Gx2Type;
            [XmlAttribute]
            public int Gx2Count;
        }
    }
}
