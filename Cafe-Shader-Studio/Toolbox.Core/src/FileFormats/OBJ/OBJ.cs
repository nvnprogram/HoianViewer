using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.IO;
using OpenTK;

namespace Toolbox.Core
{
    /// <summary>
    /// Represents a 3D model stored in the Wavefront OBJ format.
    /// </summary>
    public class OBJ : STGenericModel
    {
        // ---- CONSTANTS ----------------------------------------------------------------------------------------------

        private static readonly char[] _argSeparators = new char[] { ' ' };
        private static readonly char[] _vertexSeparators = new char[] { '/' };

        // ---- CONSTRUCTORS & DESTRUCTOR ------------------------------------------------------------------------------

        /// <summary>
        /// Initializes a new instance of the <see cref="OBJ"/> class.
        /// </summary>
        public OBJ(string name) : base(name)
        {
  
        }

        public void Load(string fileName) {
            Load(File.OpenRead(fileName));
        }

        public void Load(Stream stream)
        {
            STGenericMesh currentMesh = new STGenericMesh();
            STGenericMaterial currentMaterial = new STGenericMaterial();
            STPolygonGroup currentGroup = new STPolygonGroup();

            using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
            {
                List<Vector3> Positions = new List<Vector3>();
                List<Vector2> TexCoords = new List<Vector2>();
                List<Vector3> Normals = new List<Vector3>();

                var enusculture = new CultureInfo("en-US");
                while (!reader.EndOfStream)
                {
                    string line = reader.ReadLine();
                    line = line.Replace(",", ".");

                    // Ignore empty lines and comments.
                    if (String.IsNullOrWhiteSpace(line) || line.StartsWith("#")) continue;

                    string[] args = line.Split(_argSeparators, StringSplitOptions.RemoveEmptyEntries);
                    if (args.Length == 1)
                        continue;

                    switch (args[0])
                    {
                        case "o":
                        case "g":
                            currentMesh = new STGenericMesh();
                            currentMesh.Name = args.Length > 1 ? args[1] : $"Mesh{Meshes.Count}";
                            Meshes.Add(currentMesh);
                            continue;
                        case "v":
                            Positions.Add(new Vector3(
                                Single.Parse(args[1], enusculture),
                                Single.Parse(args[2], enusculture),
                                Single.Parse(args[3], enusculture)));
                            continue;
                        case "vt":
                            TexCoords.Add(new Vector2(Single.Parse(args[1], enusculture), Single.Parse(args[2], enusculture)));
                            continue;
                        case "vn":
                            Normals.Add(new Vector3(Single.Parse(args[1], enusculture), Single.Parse(args[2], enusculture),
                                Single.Parse(args[3])));
                            continue;
                        case "f":
                            if (args.Length != 4)
                                throw new Exception("Obj must be trianglulated!");

                            if (currentMesh.PolygonGroups.Count == 0)
                                currentMesh.PolygonGroups.Add(currentGroup);

                            for (int i = 0; i < 3; i++)
                            {
                                string[] vertexArgs = args[i + 1].Split(_vertexSeparators, StringSplitOptions.None);
                                int positionIndex = Int32.Parse(vertexArgs[0]) - 1;

                                STVertex vertex = new STVertex();
                                vertex.TexCoords = new Vector2[1];
                                vertex.Position = Positions[positionIndex];

                                //Check for valid positions
                                if (float.IsNaN(vertex.Position.X) ||
                                float.IsNaN(vertex.Position.Y) ||
                                float.IsNaN(vertex.Position.Z))
                                {
                                    continue;
                                }

                                if (vertexArgs.Length > 1 && vertexArgs[1] != String.Empty)
                                    vertex.TexCoords[0] = TexCoords[Int32.Parse(vertexArgs[1]) - 1];
                                if (vertexArgs.Length > 2 && vertexArgs[2] != String.Empty)
                                    vertex.Normal = Normals[Int32.Parse(vertexArgs[2]) - 1];

                                currentMesh.Vertices.Add(vertex);
                                currentGroup.Faces.Add((uint)positionIndex);
                            }
                            continue;
                        case "usemtl":
                            {
                                if (args.Length < 2) continue;

                                //Create a poly group for each material
                                currentGroup = new STPolygonGroup();
                                currentMesh.PolygonGroups.Add(currentGroup);

                                currentMaterial = new STGenericMaterial();
                                currentMaterial.Name = args[1];
                                currentGroup.Material = currentMaterial;
                                continue;
                            }
                    }
                }
            }

            foreach (var mesh in Meshes)
            {
                mesh.FlipUvsVertical();
                foreach (var poly in mesh.PolygonGroups)
                    mesh.Optmize(poly);
            }
        }
    }
}
