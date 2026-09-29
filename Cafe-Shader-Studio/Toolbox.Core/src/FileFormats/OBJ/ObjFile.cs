using System;
using System.Collections.Generic;
using System.IO;
using Toolbox.Core.IO;
using Toolbox.Core.ModelView;
using Toolbox.Core.Collada;

namespace Toolbox.Core
{
    public class ObjFile : ObjectTreeNode, IFileFormat, IModelFormat
    {
        public bool CanSave { get; set; } = false;

        public string[] Description { get; set; } = new string[] { "OBJ" };
        public string[] Extension { get; set; } = new string[] { "*.obj" };

        public File_Info FileInfo { get; set; }

        public bool Identify(File_Info fileInfo, Stream stream)
        {
            return fileInfo.Extension == ".obj";
        }

        public OBJ ObjectData;

        public void Load(Stream stream)
        {
            this.Label = FileInfo.FileName;
            Tag = this;

            ObjectData = new OBJ(FileInfo.FileName);
            ObjectData.Load(stream);
            foreach (var child in ObjectData.CreateTreeHiearchy().Children)
                AddChild(child);
        }

        public STGenericModel ToGeneric()
        {
            return ObjectData;
        }

        public void Save(Stream stream)
        {
        }
    }
}
