using System;
using System.Collections.Generic;
using System.Linq;
using OpenTK;
using PlayerViewer.Core;

namespace PlayerViewer.Phive
{
    /// <summary>
    /// A cloth resource (.bphcl): the Phive container, its tagfile and typed views of the cloth
    /// objects in it. The views read and write the tagfile's object graph directly, so an edit
    /// through them is an edit of the graph a writer serialises.
    /// </summary>
    public class ClothFile
    {
        public PhiveFile Phive { get; private set; }
        public HkTagfile Tagfile { get; private set; }

        /// <summary>The hclClothContainer, null when the file carries none.</summary>
        public ClothContainer Container { get; private set; }

        /// <summary>The hkaSkeletons of the animation container, one per cloth piece in the stock files.</summary>
        public List<ClothSkeleton> Skeletons { get; } = new();

        /// <summary>The hkaAnimationContainer the skeletons live in.</summary>
        public HkObject AnimationContainer { get; private set; }

        /// <summary>The AAMP parameters, null when the file's AAMP could not be read (it is then written back as it came).</summary>
        public ClothParams Params { get; private set; }

        public static ClothFile Load(byte[] bphcl)
        {
            var phive = PhiveFile.Read(bphcl);
            var file = new ClothFile { Phive = phive, Tagfile = HkTagfile.Read(phive.Tagfile) };
            file.Bind();
            if (phive.Aamp.Length > 0)
            {
                try
                {
                    file.Params = new ClothParams(Aamp.Read(phive.Aamp));
                }
                catch (Exception ex)
                {
                    Console.WriteLine(
                        $"[Cloth] parameters unreadable, kept as they are: {ex.Message}"
                    );
                    file.Params = null;
                }
            }
            return file;
        }

        void Bind()
        {
            var root = Tagfile.Root;
            foreach (
                var variant in root?.Array("namedVariants").Objects ?? Enumerable.Empty<HkObject>()
            )
            {
                switch (variant.Object("variant"))
                {
                    case HkObject o when o.Type.IsA("hclClothContainer"):
                        Container ??= new ClothContainer(o);
                        break;
                    case HkObject o when o.Type.IsA("hkaAnimationContainer"):
                        AnimationContainer ??= o;
                        Skeletons.AddRange(
                            o.Array("skeletons").Objects.Select(s => new ClothSkeleton(s))
                        );
                        break;
                }
            }
        }

        /// <summary>The file as bytes, its tagfile and AAMP serialised again from the object graph and the parameters.</summary>
        public byte[] Save() => Write(false);

        /// <summary>
        /// The file as bytes with every type this one knows kept in its TYPE section, for reloading
        /// in the editor, where a type the graph does not use yet must stay available.
        /// </summary>
        public byte[] Snapshot() => Write(true);

        /// <param name="types">A TYPE section to write or build from in place of the tagfile's own.</param>
        byte[] Write(bool keepTypes, byte[] types = null) =>
            new PhiveFile
            {
                Header = Phive.Header,
                Tagfile = HkTagfileWriter.Write(Tagfile, Tagfile.Root, types, keepTypes),
                Aamp = Params != null ? Pad16(Params.Aamp.Write()) : Phive.Aamp,
            }.Write();

        static byte[] Pad16(byte[] data)
        {
            var padded = new byte[(data.Length + 15) & ~15];
            data.CopyTo(padded, 0);
            return padded;
        }

        /// <summary>
        /// The types a graph needs that this file declares no body for, from <paramref name="wanted"/>.
        /// </summary>
        public List<string> MissingTypes(IEnumerable<string> wanted)
        {
            var section = HkTypeSection.Read(Tagfile.TypeSection);
            var declared = new HashSet<string>(section.Bodies.Select(b => section.NameOf(b.Id)));
            return wanted.Where(w => !declared.Contains(w)).Distinct().ToList();
        }

        /// <summary>
        /// This file reloaded with the named types imported, each from the first donor TYPE section
        /// declaring it; this file when nothing is missing.
        /// </summary>
        public ClothFile WithTypes(IEnumerable<string> wanted, IEnumerable<byte[]> donorSections)
        {
            var missing = MissingTypes(wanted);
            if (missing.Count == 0)
                return this;
            var section = HkTypeSection.Read(Tagfile.TypeSection);
            foreach (var donorBytes in donorSections)
            {
                if (missing.Count == 0)
                    break;
                var donor = HkTypeSection.Read(donorBytes);
                var declared = new HashSet<string>(donor.Bodies.Select(b => donor.NameOf(b.Id)));
                var take = missing.Where(declared.Contains).ToList();
                if (take.Count == 0)
                    continue;
                section.Import(donor, take);
                missing.RemoveAll(take.Contains);
            }
            if (missing.Count > 0)
                throw new KeyNotFoundException("No donor declares " + string.Join(", ", missing));
            return Load(Write(true, section.Write()));
        }

        /// <summary>The TYPE section as read.</summary>
        public byte[] TypeSectionBytes => Tagfile.TypeSection;

        /// <summary>The type of the given name this file declares a body for, or null.</summary>
        public HkType Type(string name) =>
            Tagfile.Types.FirstOrDefault(t => t != null && !t.IsStub && t.Name == name);

        /// <summary>A new zeroed record of a type this file declares.</summary>
        public HkObject New(string typeName) =>
            HkObject.Create(
                Type(typeName) ?? throw new KeyNotFoundException($"The file declares no {typeName}")
            );

        /// <summary>The skeleton a piece's transform set is named after (first match wins).</summary>
        public ClothSkeleton SkeletonFor(ClothData piece)
        {
            string name = piece.TransformSetDefinitions.FirstOrDefault()?.Name;
            return name == null ? null : Skeletons.FirstOrDefault(s => s.Name == name);
        }
    }

    /// <summary>hclClothContainer: the collidables every piece may share, and the pieces.</summary>
    public class ClothContainer
    {
        public HkObject Source { get; }

        public ClothContainer(HkObject source)
        {
            Source = source;
            Collidables = source
                .Array("collidables")
                .Objects.Select(o => new Collidable(o))
                .ToList();
            ClothDatas = source.Array("clothDatas").Objects.Select(o => new ClothData(o)).ToList();
        }

        public List<Collidable> Collidables { get; }

        /// <summary>One hclClothData per independently simulated piece.</summary>
        public List<ClothData> ClothDatas { get; }
    }

    /// <summary>hkaSkeleton: the cloth's transform set as bones, with the reference pose.</summary>
    public class ClothSkeleton
    {
        public HkObject Source { get; }

        public ClothSkeleton(HkObject source) => Source = source;

        public string Name
        {
            get => Source.String("name");
            set => Source.Set("name", value);
        }

        public HkArray Bones => Source.Array("bones");

        public string[] BoneNames => Bones.Objects.Select(b => b.String("name") ?? "").ToArray();

        public int[] ParentIndices => Source.Array("parentIndices").Ints();

        /// <summary>The local reference pose (hkQsTransform): translation, rotation quaternion, scale; w as stored.</summary>
        public (Vector4 Translation, Quaternion Rotation, Vector4 Scale)[] ReferencePose =>
            Source
                .Array("referencePose")
                .Objects.Select(p =>
                {
                    var q = p.Vector4("rotation");
                    return (
                        p.Vector4("translation"),
                        new Quaternion(q.X, q.Y, q.Z, q.W),
                        p.Vector4("scale")
                    );
                })
                .ToArray();

        /// <summary>The reference pose composed to model space, scale, rotation and translation per bone.</summary>
        public Matrix4[] ModelSpaceReferencePose()
        {
            var parents = ParentIndices;
            return BoneMath.RestWorlds(
                ReferencePose
                    .Select(
                        (p, i) =>
                            new BoneRest(
                                p.Scale.Xyz,
                                p.Rotation,
                                p.Translation.Xyz,
                                i < parents.Length ? parents[i] : -1,
                                false
                            )
                    )
                    .ToList()
            );
        }
    }
}
