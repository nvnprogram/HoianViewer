using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using BfresLibrary;
using Toolbox.Core;
using Toolbox.Core.ViewModels;

namespace BfresEditor
{
    public class FMDL : STGenericModel, IRenamableNode, ICheckableNode, IExportReplaceNode
    {
        /// <summary>
        /// Determines if the mesh is visible in the viewer.
        /// </summary>
        public bool IsVisible { get; set; } = true;

        public Model Model { get; set; }

        public ResFile resFile { get; set; }

        private NodeBase NodeUI { get; set; }

        public override string Name
        {
            get { return Model.Name; }
            set { Model.Name = value; }
        }

        public void Renamed(string text)
        {
            this.Name = text;
        }

        public void OnChecked(bool visible) {
            this.IsVisible = visible;
        }

        public string GetRenameText() => this.Name;

        public FMDL(NodeBase node, BFRES bfres, Model model) : base() {
            NodeUI = node;
            Model = model;
            Skeleton = new FSKL(model.Skeleton);

            //Each FSHP decodes the full vertex/index buffers of its shape into
            //managed lists, which dominates model load time; the shapes are
            //independent so decode them in parallel.
            var shapes = model.Shapes.Values.ToList();
            var meshes = new FSHP[shapes.Count];
            System.Threading.Tasks.Parallel.For(0, shapes.Count, i =>
            {
                meshes[i] = new FSHP(bfres.ResFile, (FSKL)Skeleton, this, shapes[i]);
            });
            Meshes.AddRange(meshes);

            foreach (var mat in model.Materials.Values)
                Materials.Add(new FMAT(bfres, this, model, mat));

            foreach (FSHP shape in Meshes)
                shape.Material = (FMAT)Materials[shape.Shape.MaterialIndex];
        }

        #region events

        public FileFilter[] ReplaceFilter => new FileFilter[]
        {
          new FileFilter(".dae", "dae"),
          new FileFilter(".bfmdl", "Raw Binary Model"),
        };

        public FileFilter[] ExportFilter => new FileFilter[]
        {
          new FileFilter(".dae", "dae"),
          new FileFilter(".bfmdl", "Raw Binary Model"),
        };

        public void Replace(string fileName)
        {
            if (Utils.GetExtension(fileName) == ".bfmdl")
                Model.Import(fileName, resFile);
        }

        public void Export(string fileName)
        {
            if (Utils.GetExtension(fileName) == ".dae")
            {
                var settings = new Toolbox.Core.Collada.DAE.ExportSettings();

                Toolbox.Core.Collada.DAE.Export(fileName, settings, this, GetTextures(), this.Skeleton);
            }
            if (Utils.GetExtension(fileName) == ".bfmdl")
                Model.Export(fileName, resFile);
        }

        #endregion

        private List<STGenericTexture> GetTextures()
        {
            return new List<STGenericTexture>();
        }
    }
}
