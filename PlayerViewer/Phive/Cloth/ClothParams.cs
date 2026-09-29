using System.Collections.Generic;
using System.Linq;

namespace PlayerViewer.Phive
{
    /// <summary>
    /// The AAMP half of a cloth resource: a <c>cloth_mesh_N</c> object per piece and a
    /// <c>collidable_N</c> per collidable, matched to the tagfile by their Name parameter.
    /// </summary>
    public class ClothParams
    {
        public Aamp Aamp { get; }

        public ClothParams(Aamp aamp) => Aamp = aamp;

        AampList List(string name)
        {
            var list = Aamp.Root.FindList(name);
            if (list == null)
            {
                list = new AampList { Hash = Aamp.Hash(name) };
                Aamp.Root.Lists.Add(list);
            }
            return list;
        }

        AampList MeshList => List("cloth_mesh_list");
        AampList CollidableList => List("collidable_list");

        public IEnumerable<MeshParams> Meshes =>
            (Aamp.Root.FindList("cloth_mesh_list")?.Objects ?? new List<AampObject>()).Select(
                o => new MeshParams(o)
            );

        public IEnumerable<CollidableParams> Collidables =>
            (Aamp.Root.FindList("collidable_list")?.Objects ?? new List<AampObject>()).Select(
                o => new CollidableParams(o)
            );

        public MeshParams MeshFor(string piece) => Meshes.FirstOrDefault(m => m.Name == piece);

        public CollidableParams CollidableFor(string name) =>
            Collidables.FirstOrDefault(c => c.Name == name);

        /// <summary>
        /// Adds the parameters of a new piece: a copy of an existing mesh's (the same parameters in
        /// the same order) or the stock default set, under the next <c>cloth_mesh_N</c>.
        /// </summary>
        public MeshParams AddMesh(string piece, string baseBone, MeshParams copyOf = null)
        {
            var list = MeshList;
            var source = copyOf?.Source ?? list.Objects.FirstOrDefault();
            var obj = source != null ? source.Clone() : DefaultMesh();
            obj.Hash = Aamp.Hash($"cloth_mesh_{list.Objects.Count}");
            list.Objects.Add(obj);
            var mesh = new MeshParams(obj) { Name = piece, BaseBone = baseBone };
            return mesh;
        }

        public void RemoveMesh(string piece)
        {
            var list = MeshList;
            list.Objects.RemoveAll(o => new MeshParams(o).Name == piece);
            Renumber(list, "cloth_mesh_");
        }

        public CollidableParams AddCollidable(string name)
        {
            var list = CollidableList;
            var obj = new AampObject { Hash = Aamp.Hash($"collidable_{list.Objects.Count}") };
            obj.Params.Add(Str("Name", name));
            obj.Params.Add(Bool("ForReplace", false));
            list.Objects.Add(obj);
            return new CollidableParams(obj);
        }

        public void RemoveCollidable(string name)
        {
            var list = CollidableList;
            list.Objects.RemoveAll(o => new CollidableParams(o).Name == name);
            Renumber(list, "collidable_");
        }

        static void Renumber(AampList list, string prefix)
        {
            for (int i = 0; i < list.Objects.Count; i++)
                list.Objects[i].Hash = Aamp.Hash(prefix + i);
        }

        /// <summary>The ten parameters the loader reads, plus the eleventh every stock file carries, in the stock order, with the values of a stock leather piece.</summary>
        static AampObject DefaultMesh()
        {
            var obj = new AampObject();
            obj.Params.Add(Str("Name", ""));
            obj.Params.Add(Str("BaseBone", ""));
            obj.Params.Add(Str("WindPreset", ""));
            obj.Params.Add(Bool("BoneCorrection", true));
            obj.Params.Add(Str("BoneCorrectionAxisOrder", "xyz"));
            obj.Params.Add(Bool("Twist", false));
            obj.Params.Add(Str("TwistSwingAxis", "y"));
            obj.Params.Add(Float("TwistAngleCoef", 0));
            obj.Params.Add(Float("TwistMaxAngle", 0));
            obj.Params.Add(
                new AampParam
                {
                    Hash = MeshParams.UnknownBoolHash,
                    Type = AampType.Bool,
                    Data = new byte[4],
                }
            );
            obj.Params.Add(Str("Preset", "leather"));
            return obj;
        }

        static AampParam Str(string name, string value) =>
            new()
            {
                Hash = Aamp.Hash(name),
                Type = AampType.StringRef,
                Text = value,
            };

        static AampParam Bool(string name, bool value) =>
            new()
            {
                Hash = Aamp.Hash(name),
                Type = AampType.Bool,
                Bool = value,
            };

        static AampParam Float(string name, float value) =>
            new()
            {
                Hash = Aamp.Hash(name),
                Type = AampType.F32,
                Float = value,
            };
    }

    /// <summary>A <c>cloth_mesh_N</c> object. Every property reads its parameter by name and is null or default when the file lacks it.</summary>
    public class MeshParams
    {
        /// <summary>The eleventh parameter of every stock mesh (a bool, false everywhere); its name is not in the 11.3.0 loader.</summary>
        public const uint UnknownBoolHash = 811394937;

        public AampObject Source { get; }

        public MeshParams(AampObject source) => Source = source;

        string GetString(string name) => Source.Find(name)?.Text;

        void SetString(string name, string value)
        {
            var p = Source.Find(name);
            if (p != null)
                p.Text = value ?? "";
        }

        bool GetBool(string name) => Source.Find(name)?.Bool ?? false;

        void SetBool(string name, bool value)
        {
            var p = Source.Find(name);
            if (p != null)
                p.Bool = value;
        }

        float GetFloat(string name) => Source.Find(name)?.Float ?? 0;

        void SetFloat(string name, float value)
        {
            var p = Source.Find(name);
            if (p != null)
                p.Float = value;
        }

        public string Name
        {
            get => GetString("Name");
            set => SetString("Name", value);
        }

        /// <summary>The bone the piece hangs from.</summary>
        public string BaseBone
        {
            get => GetString("BaseBone");
            set => SetString("BaseBone", value);
        }

        /// <summary>The wind preset for the movement wind; empty for none.</summary>
        public string WindPreset
        {
            get => GetString("WindPreset");
            set => SetString("WindPreset", value);
        }

        /// <summary>Turn each driven bone to aim at its first cloth child on write back.</summary>
        public bool BoneCorrection
        {
            get => GetBool("BoneCorrection");
            set => SetBool("BoneCorrection", value);
        }

        /// <summary>Which axis the aim moves; every stock file says xyz.</summary>
        public string BoneCorrectionAxisOrder
        {
            get => GetString("BoneCorrectionAxisOrder");
            set => SetString("BoneCorrectionAxisOrder", value);
        }

        public bool Twist
        {
            get => GetBool("Twist");
            set => SetBool("Twist", value);
        }

        public string TwistSwingAxis
        {
            get => GetString("TwistSwingAxis");
            set => SetString("TwistSwingAxis", value);
        }

        public float TwistAngleCoef
        {
            get => GetFloat("TwistAngleCoef");
            set => SetFloat("TwistAngleCoef", value);
        }

        public float TwistMaxAngle
        {
            get => GetFloat("TwistMaxAngle");
            set => SetFloat("TwistMaxAngle", value);
        }

        /// <summary>The named parameter preset: leather or SpringGravity0 in the stock files.</summary>
        public string Preset
        {
            get => GetString("Preset");
            set => SetString("Preset", value);
        }
    }

    /// <summary>A <c>collidable_N</c> object.</summary>
    public class CollidableParams
    {
        public AampObject Source { get; }

        public CollidableParams(AampObject source) => Source = source;

        public string Name
        {
            get => Source.Find("Name")?.Text;
            set
            {
                var p = Source.Find("Name");
                if (p != null)
                    p.Text = value ?? "";
            }
        }

        public bool ForReplace
        {
            get => Source.Find("ForReplace")?.Bool ?? false;
            set
            {
                var p = Source.Find("ForReplace");
                if (p != null)
                    p.Bool = value;
            }
        }
    }
}
