using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PlayerViewer.Core;
using PlayerViewer.Core.Formats;
using PlayerViewer.Phive;

namespace PlayerViewer.Physics
{
    /// <summary>
    /// Where an actor's cloth lives and how a pack is changed to carry an edited or new one.
    /// An actor finds its cloth through its ActorParam's PhysicsRef, the PhysicsParam's
    /// ControllerSetPath and the ControllerSetParam's ClothList, each possibly inherited through
    /// <c>$parent</c> from a file in the pack or in the Bootup pack.
    /// </summary>
    public class ClothPacks
    {
        readonly Romfs _romfs;
        Sarc _bootup;

        public ClothPacks(Romfs romfs) => _romfs = romfs;

        Sarc Bootup => _bootup ??= LoadBootup();

        Sarc LoadBootup()
        {
            var data = _romfs.ReadFile("Pack/Bootup.Nin_NX_NVN.pack");
            return data != null ? new Sarc(data) : null;
        }

        /// <summary>"Work/A/B.gyml" as the pack entry "A/B.bgyml"; "Work/Phive/Cloth/X.phcl" as "Phive/Cloth/X.bphcl".</summary>
        public static string EntryOf(string workPath, string relativeTo = null)
        {
            if (string.IsNullOrEmpty(workPath))
                return null;
            string rel = workPath;
            if (rel.StartsWith("./") && relativeTo != null)
                rel = Path.GetDirectoryName(relativeTo)!.Replace('\\', '/') + "/" + rel[2..];
            else if (rel.StartsWith("Work/"))
                rel = rel[5..];
            if (rel.EndsWith(".gyml"))
                rel = rel[..^5] + ".bgyml";
            else if (rel.EndsWith(".phcl"))
                rel = rel[..^5] + ".bphcl";
            return rel;
        }

        public static string WorkPathOf(string entry)
        {
            string p = "Work/" + entry;
            if (p.EndsWith(".bgyml"))
                p = p[..^6] + ".gyml";
            else if (p.EndsWith(".bphcl"))
                p = p[..^6] + ".phcl";
            return p;
        }

        /// <summary>A pack's files by name, as the editor rewrites them.</summary>
        public class PackFiles
        {
            public string Actor;
            public Dictionary<string, byte[]> Files = new(StringComparer.Ordinal);

            public byte[] Get(string name) =>
                name != null && Files.TryGetValue(name, out var d) ? d : null;
        }

        public PackFiles ReadPack(string actor)
        {
            var sarc = _romfs.GetActorPack(actor);
            if (sarc == null)
                return null;
            var pack = new PackFiles { Actor = actor };
            foreach (var (name, data) in sarc.Files)
                pack.Files[name] = data.ToArray();
            return pack;
        }

        /// <summary>A pack read from a file anywhere, named after the file.</summary>
        public static PackFiles ReadPackFile(string path)
        {
            var sarc = new Sarc(Romfs.Decompress(File.ReadAllBytes(path)));
            string name = Path.GetFileName(path);
            int dot = name.IndexOf('.');
            var pack = new PackFiles { Actor = dot > 0 ? name[..dot] : name };
            foreach (var (entry, data) in sarc.Files)
                pack.Files[entry] = data.ToArray();
            return pack;
        }

        byte[] Resolve(PackFiles pack, string entry) =>
            pack.Get(entry) ?? (entry != null ? Bootup?.GetFile(entry) : null);

        Dictionary<string, object> Gyml(PackFiles pack, string entry)
        {
            var data = Resolve(pack, entry);
            return data != null ? Byml.AsHash(new Byml(data).Root) : null;
        }

        /// <summary>A key of a gyml, following <c>$parent</c> when the file does not set it.</summary>
        object Inherited(PackFiles pack, string entry, Func<Dictionary<string, object>, object> get)
        {
            for (int guard = 0; entry != null && guard < 8; guard++)
            {
                var hash = Gyml(pack, entry);
                if (hash == null)
                    return null;
                var v = get(hash);
                if (v != null)
                    return v;
                entry =
                    hash.TryGetValue("$parent", out var parent) && parent is string ps
                        ? EntryOf(ps, entry)
                        : null;
            }
            return null;
        }

        static string ActorParamEntry(string actor) =>
            $"Actor/{actor}.engine__actor__ActorParam.bgyml";

        /// <summary>The physics chain of an actor: its PhysicsParam and ControllerSetParam entries, each null when not set anywhere.</summary>
        public (string Physics, string ControllerSet) PhysicsChain(PackFiles pack)
        {
            string physicsWork =
                Inherited(
                    pack,
                    ActorParamEntry(pack.Actor),
                    h =>
                        h.TryGetValue("Components", out var c)
                        && c is Dictionary<string, object> comps
                        && comps.TryGetValue("PhysicsRef", out var r)
                            ? r
                            : null
                ) as string;
            string physics = EntryOf(physicsWork);
            string csWork =
                Inherited(
                    pack,
                    physics,
                    h => h.TryGetValue("ControllerSetPath", out var v) ? v : null
                ) as string;
            return (physics, EntryOf(csWork));
        }

        /// <summary>The pack entries of the actor's ClothList, in list order.</summary>
        public List<string> ClothEntries(PackFiles pack)
        {
            var (_, cs) = PhysicsChain(pack);
            var list =
                Inherited(pack, cs, h => h.TryGetValue("ClothList", out var v) ? v : null)
                as List<object>;
            var result = new List<string>();
            foreach (var item in list ?? new List<object>())
                if (
                    item is Dictionary<string, object> e
                    && e.TryGetValue("Path", out var p)
                    && p is string path
                )
                    result.Add(EntryOf(path));
            return result;
        }

        /// <summary>The first ClothList entry the pack itself carries, null when none.</summary>
        public string FirstCloth(PackFiles pack) =>
            ClothEntries(pack).FirstOrDefault(e => pack.Files.ContainsKey(e));

        /// <summary>The bytes of the first cloth an actor's pack carries, null when it has none.</summary>
        public byte[] ClothOf(string actor)
        {
            var pack = ReadPack(actor);
            return pack?.Get(FirstCloth(pack));
        }

        /// <summary>
        /// The cloth a model's actor carries: the actor named after the model, and for a
        /// gendered hair the other gender's pack too when it names the same file (the male
        /// packs carry the female named file). Null when there is no pack at all.
        /// </summary>
        public ClothSource FindFor(string actor)
        {
            var pack = ReadPack(actor);
            if (pack == null)
                return null;
            var source = new ClothSource();
            source.Actors.Add(actor);
            source.Entry = FirstCloth(pack);
            if (source.Entry == null)
            {
                source.IsNew = true;
                source.Entry = $"Phive/Cloth/{actor}.bphcl";
            }
            string other = OtherGender(actor);
            if (other != null)
            {
                var otherPack = ReadPack(other);
                if (otherPack != null)
                {
                    var otherEntries = ClothEntries(otherPack);
                    if (
                        source.IsNew ? otherEntries.Count == 0 : otherEntries.Contains(source.Entry)
                    )
                        source.Actors.Add(other);
                }
            }
            return source;
        }

        static string OtherGender(string actor) =>
            actor.EndsWith("_F") ? actor[..^2] + "_M"
            : actor.EndsWith("_M") ? actor[..^2] + "_F"
            : null;

        public byte[] ReadCloth(ClothSource source) =>
            source.IsNew ? null : ReadPack(source.Actors[0])?.Get(source.Entry);

        /// <summary>The stock hair cloths the editor takes classes and a starting file from.</summary>
        static readonly string[] Donors =
        {
            "Har_OCT004_F",
            "Har_SQD003_F",
            "Har_SQD000_F",
            "Har_OCT000_F",
            "Har_SQD004_F",
        };

        List<byte[]> _donorSections;

        /// <summary>TYPE sections of stock cloths, the classes a new or extended file can import.</summary>
        public IReadOnlyList<byte[]> DonorTypeSections()
        {
            if (_donorSections != null)
                return _donorSections;
            _donorSections = new List<byte[]>();
            foreach (var actor in Donors)
            {
                try
                {
                    var cloth = ClothOf(actor);
                    if (cloth != null)
                        _donorSections.Add(ClothFile.Load(cloth).TypeSectionBytes);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Cloth] donor {actor} unreadable: {ex.Message}");
                }
            }
            return _donorSections;
        }

        /// <summary>A stock cloth emptied of its content: the start of a new file.</summary>
        public ClothFile EmptyCloth()
        {
            foreach (var actor in Donors)
            {
                var cloth = ClothOf(actor);
                if (cloth == null)
                    continue;
                var file = ClothFile.Load(cloth);
                ClothAuthor.Clear(file);
                return ClothFile.Load(file.Snapshot());
            }
            throw new InvalidOperationException("No stock cloth found in the romfs to start from");
        }

        /// <summary>
        /// Puts a cloth into a pack, wiring it into the actor's own PhysicsParam and ClothList when
        /// they do not name it; with <paramref name="only"/> the list names it alone. Returns the changes.
        /// </summary>
        public List<string> PutCloth(PackFiles pack, string entry, byte[] bphcl, bool only = false)
        {
            var notes = new List<string>();
            notes.Add((pack.Files.ContainsKey(entry) ? "replaced " : "added ") + entry);
            pack.Files[entry] = bphcl;
            var entries = ClothEntries(pack);
            if (only ? entries.Count == 1 && entries[0] == entry : entries.Contains(entry))
                return notes;

            string actorEntry = ActorParamEntry(pack.Actor);
            var actorParam =
                Gyml(pack, actorEntry)
                ?? throw new InvalidOperationException($"{pack.Actor} has no {actorEntry}");
            var (physics, controllerSet) = PhysicsChain(pack);

            string ownPhysics =
                $"Component/Physics/{pack.Actor}.engine__component__PhysicsParam.bgyml";
            string ownControllerSet =
                $"Phive/ControllerSetParam/{pack.Actor}.phive__ControllerSetParam.bgyml";

            if (physics != ownPhysics || !pack.Files.ContainsKey(ownPhysics))
            {
                var p = new Dictionary<string, object>();
                if (physics != null)
                    p["$parent"] = WorkPathOf(physics);
                p["ControllerSetPath"] = WorkPathOf(ownControllerSet);
                pack.Files[ownPhysics] = BymlWriter.Write(p);
                if (
                    !actorParam.TryGetValue("Components", out var c)
                    || c is not Dictionary<string, object> comps
                )
                    actorParam["Components"] = comps = new Dictionary<string, object>();
                comps["PhysicsRef"] = WorkPathOf(ownPhysics);
                pack.Files[actorEntry] = BymlWriter.Write(actorParam, Version(pack, actorEntry));
                notes.Add($"added {ownPhysics}, PhysicsRef set in {actorEntry}");
            }
            else
            {
                var p = Gyml(pack, ownPhysics);
                if (controllerSet != ownControllerSet)
                {
                    p["ControllerSetPath"] = WorkPathOf(ownControllerSet);
                    pack.Files[ownPhysics] = BymlWriter.Write(p, Version(pack, ownPhysics));
                    notes.Add($"ControllerSetPath set in {ownPhysics}");
                }
            }

            Dictionary<string, object> cs;
            if (pack.Files.ContainsKey(ownControllerSet))
                cs = Gyml(pack, ownControllerSet);
            else
            {
                cs = new Dictionary<string, object>();
                if (controllerSet != null && controllerSet != ownControllerSet)
                    cs["$parent"] = WorkPathOf(controllerSet);
                notes.Add($"added {ownControllerSet}");
            }
            var list =
                !only && cs.TryGetValue("ClothList", out var l) && l is List<object> existing
                    ? existing
                    : new List<object>();
            list.Add(
                new Dictionary<string, object>
                {
                    ["Name"] = list.Count == 0 ? "Default" : $"Cloth{list.Count}",
                    ["Path"] = WorkPathOf(entry),
                }
            );
            cs["ClothList"] = list;
            pack.Files[ownControllerSet] = BymlWriter.Write(cs, Version(pack, ownControllerSet));
            notes.Add($"ClothList in {ownControllerSet} names {WorkPathOf(entry)}");
            return notes;
        }

        ushort Version(PackFiles pack, string entry)
        {
            var data = pack.Get(entry);
            return data != null ? new Byml(data).Version : (ushort)7;
        }

        /// <summary>Writes a pack as the romfs keeps it, zstd compressed, under <c>&lt;root&gt;/Pack/Actor</c>, through a temp file.</summary>
        public string WritePack(string romfsRoot, PackFiles pack)
        {
            var sarc = SarcWriter.Write(pack.Files);
            string dir = Path.Combine(romfsRoot, "Pack", "Actor");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, pack.Actor + ".pack.zs");
            AtomicFile.Write(path, sarc, compress: true);
            _romfs.ForgetActorPack(pack.Actor);
            return path;
        }
    }
}
