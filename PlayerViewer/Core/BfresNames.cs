using System;
using BfresLibrary;

namespace PlayerViewer.Core
{
    /// <summary>
    /// Renames a model or a shape along with every name the file keeps for it: the dictionary
    /// key, the name itself, and for a shape the shape animations that target it.
    /// </summary>
    public static class BfresNames
    {
        public static string ModelProblem(ResFile res, string old, string name) =>
            Problem(name)
            ?? (
                name != old && res.Models.ContainsKey(name) ? "Another model has this name." : null
            );

        public static string ShapeProblem(Model model, string old, string name) =>
            Problem(name)
            ?? (
                name != old && model.Shapes.ContainsKey(name)
                    ? "Another shape of this model has this name."
                    : null
            );

        static string Problem(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return "A name is needed.";
            if (name != name.Trim())
                return "The name starts or ends with a space.";
            //ImGui ends a label at ##, so such a name would show cut short.
            if (name.Contains("##"))
                return "The name holds ##.";
            return null;
        }

        public static void RenameModel(ResFile res, string old, string name)
        {
            if (ModelProblem(res, old, name) is string problem)
                throw new ArgumentException(problem);
            if (old == name)
                return;
            var model = res.Models[old];
            res.Models.Rename(old, name);
            model.Name = name;
        }

        /// <summary>
        /// Renames a shape of <paramref name="model"/>, the vertex shape animations of the file
        /// that target it, and bone visibility entries of that name when no bone has it.
        /// </summary>
        public static void RenameShape(ResFile res, Model model, string old, string name)
        {
            if (ShapeProblem(model, old, name) is string problem)
                throw new ArgumentException(problem);
            if (old == name)
                return;
            var shape = model.Shapes[old];
            model.Shapes.Rename(old, name);
            shape.Name = name;
            foreach (var anim in res.ShapeAnims.Values)
            foreach (var target in anim.VertexShapeAnims ?? Array.Empty<VertexShapeAnim>())
                if (target.Name == old)
                    target.Name = name;
            if (model.Skeleton?.Bones.ContainsKey(old) == true)
                return;
            foreach (var anim in res.BoneVisibilityAnims.Values)
                if (anim.Names != null)
                    for (int i = 0; i < anim.Names.Count; i++)
                        if (anim.Names[i] == old)
                            anim.Names[i] = name;
        }

        /// <summary>The file's bytes with one model renamed; the bytes as they were when it has no such model.</summary>
        public static byte[] RenameModel(byte[] bytes, string old, string name)
        {
            var res = BfresBytes.Read(bytes);
            if (!res.Models.ContainsKey(old))
                return bytes;
            RenameModel(res, old, name);
            return BfresBytes.ToBytes(res);
        }

        /// <summary>The file's bytes with one shape of a model renamed; as they were when it has no such shape.</summary>
        public static byte[] RenameShape(byte[] bytes, string model, string old, string name)
        {
            var res = BfresBytes.Read(bytes);
            if (!res.Models.ContainsKey(model) || !res.Models[model].Shapes.ContainsKey(old))
                return bytes;
            RenameShape(res, res.Models[model], old, name);
            return BfresBytes.ToBytes(res);
        }
    }
}
