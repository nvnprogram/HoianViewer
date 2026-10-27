using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PlayerViewer.Core.Formats;

namespace PlayerViewer.Core
{
    /// <summary>The tank slots a gadget can sit in.</summary>
    public enum GadgetSlot
    {
        L,
        R,
        A,
        Special,
    }

    /// <summary>Where a gadget sits on a tank in one slot: offset in metres, rotation in degrees.</summary>
    public struct GadgetPlacement
    {
        public System.Numerics.Vector3 Position;
        public System.Numerics.Vector3 Rotation;
    }

    public partial class GameDatabase
    {
        //Clothes row -> the head row its outfit carries.
        public Dictionary<string, string> OutfitHeads = new();

        //Gadget row -> scale and per slot placement on the tank.
        public Dictionary<string, (float Scale, GadgetPlacement[] Slots)> GadgetPlacements = new();

        /// <summary>
        /// Splatoon Raiders: gloves come with the clothes, the special tank has no table row, and
        /// gear is named through the outfit unlock conditions rather than by row.
        /// </summary>
        LoadSteps SpiralSteps() =>
            new()
            {
                AfterTank = LoadTankStyles,
                AfterWeapons = () =>
                {
                    foreach (var weapon in MainWeapons)
                        weapon.NameKey = weapon.RowId;
                    LoadOutfitNames();
                    foreach (var entry in Clothes.Concat(Head))
                        entry.VariationAnim = VariationAnimOf(entry.RowId);
                    LoadGloves();
                    LoadGadgets();
                },
            };

        //Raiders specials, mounted on the tank by the special tank model.
        public List<GearEntry> Special = new();

        //A special's idol and the special tank's bone group that shows it.
        static readonly (string Idol, string Group)[] SpecialGroups =
        {
            ("Mantaro", "Mantaro"),
            ("Utsuho", "Utsuho"),
            ("Fuka", "Fuuka"),
        };

        //Each tank's style, from its TankInfo row.
        void LoadTankStyles()
        {
            foreach (var row in ReadTable("TankInfo").OfType<Dictionary<string, object>>())
            {
                var entry = Tank.Find(e => e.RowId == Byml.GetString(row, "__RowId"));
                if (entry == null)
                    continue;
                entry.Style = StyleName(Byml.GetInt(row, "Style"));
                entry.NameKey = entry.Style is "Agility" or "Strength" or "Technique"
                    ? $"PlayerStyle_{entry.Style}_00"
                    : "PlayerStyle_Name";
            }
        }

        /// <summary>
        /// The model of a row that has one per body (hair, eyebrows, bottoms). Raiders names it
        /// in the row's actor for that body, and it is often the other body's model.
        /// </summary>
        public string GenderedModel(string rowId, string genderSuffix) =>
            Romfs.Game == GameKind.Spiral
                ? ResolveModelName(rowId + genderSuffix) ?? rowId + genderSuffix
                : rowId + genderSuffix;

        /// <summary>The tank harness size a clothes row asks for (S, M, L), or "" for the default.</summary>
        public static string HarnessTypeOf(Dictionary<string, object> row)
        {
            switch (row?.GetValueOrDefault("HarnessType"))
            {
                case string name:
                    return name;
                case uint hash:
                    foreach (var name in new[] { "S", "M", "L" })
                        if (Murmur3.Hash(name) == hash)
                            return name;
                    break;
            }
            return "";
        }

        static string StyleName(int hash)
        {
            foreach (var name in new[] { "Default", "Agility", "Strength", "Technique" })
                if ((int)Murmur3.Hash(name) == hash)
                    return name;
            return "";
        }

        /// <summary>
        /// An outfit is named after the condition that unlocks it, and its head piece after the
        /// outfit whose row number it shares, else the first that carries it.
        /// </summary>
        void LoadOutfitNames()
        {
            foreach (var (rowId, row) in ClothesRows)
            {
                string head = Path.GetFileName(Byml.GetString(row, "HeadGearRef"));
                int dot = head.IndexOf('.');
                if (dot > 0)
                    OutfitHeads[rowId] = head[..dot];
            }

            var conditions = new Dictionary<uint, string>();
            foreach (
                var key in ReadSingleton("spl__UISkinUnlockConditionInfo") is { } info
                    ? Byml.AsArray(info.GetValueOrDefault("SkinUnlockConditions")) ?? new()
                    : new()
            )
            {
                var hash = Byml.AsHash(key);
                string path = Byml.GetString(
                    Byml.AsHash(hash?.GetValueOrDefault("GearInfo")),
                    "GearPathName"
                );
                string row = Path.GetFileName(path);
                int dot = row.IndexOf('.');
                if (dot > 0 && hash?.GetValueOrDefault("ConditionEnum") is var c && c != null)
                    conditions[Convert.ToUInt32(c)] = row[..dot];
            }
            var keys = UnlockConditionKeys();
            foreach (var entry in Clothes)
            {
                var hit = conditions.FirstOrDefault(x => x.Value == entry.RowId);
                if (hit.Value != null && keys.TryGetValue(hit.Key, out var key))
                    entry.NameKey = key;
            }
            foreach (var entry in Head)
            {
                string suffix = entry.RowId[(entry.RowId.IndexOf('_') + 1)..];
                var owners = OutfitHeads.Where(x => x.Value == entry.RowId).Select(x => x.Key);
                string owner =
                    owners.FirstOrDefault(o => o.EndsWith(suffix, StringComparison.Ordinal))
                    ?? owners.OrderBy(o => o, StringComparer.Ordinal).FirstOrDefault();
                var outfit = owner != null ? Clothes.Find(c => c.RowId == owner) : null;
                entry.NameKey = outfit?.NameKey;
                entry.IconRowId = outfit?.RowId;
            }
        }

        //The unlock condition names, matched to their hashes through the message labels.
        Dictionary<uint, string> UnlockConditionKeys()
        {
            var keys = new Dictionary<uint, string>();
            foreach (var language in Localization.Available(Romfs).Take(1))
            {
                var data = Romfs.ReadProduct("Mals", language, "sarc*");
                if (data == null)
                    continue;
                if (new Sarc(data).GetFile("Glossary/GearName.msbt") is { } msbt)
                    foreach (var label in new Msbt(msbt).Messages.Keys)
                        keys[Murmur3.Hash(label)] = label;
            }
            return keys;
        }

        Dictionary<string, object> ReadSingleton(string type)
        {
            try
            {
                var pack = Romfs.ReadFile("Pack/SingletonParam.pack");
                if (pack == null)
                    return null;
                var file = new Sarc(pack).GetFile($"Gyml/Singleton/{type}.{type}.bgyml");
                return file != null ? Byml.AsHash(new Byml(file).Root) : null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DB] {type}: {ex.Message}");
                return null;
            }
        }

        /// <summary>Every glove model: the ones clothes come with, then any other in the romfs.</summary>
        void LoadGloves()
        {
            var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in Clothes)
                if (ModelInfoOf(entry.RowId) is { } info)
                    foreach (var sub in SubModelsOf(info))
                        if (sub.StartsWith("Glv_", StringComparison.Ordinal))
                        {
                            entry.GloveModel = sub;
                            names.Add(sub);
                        }
            foreach (var model in Romfs.ListModelFiles("Glv_"))
                names.Add(model);
            foreach (var name in names)
                Glove.Add(
                    new GearEntry
                    {
                        Slot = GearSlot.Glove,
                        RowId = name,
                        ModelName = name,
                    }
                );
        }

        /// <summary>
        /// The gadgets a tank carries: CoopGadgetInfo rows whose equip actor has a model. The
        /// specials share one, the special tank, and show one bone group of it each.
        /// </summary>
        void LoadGadgets()
        {
            foreach (var row in ReadTable("CoopGadgetInfo").OfType<Dictionary<string, object>>())
            {
                string rowId = Byml.GetString(row, "__RowId");
                string actor = Path.GetFileName(Byml.GetString(row, "EquipActor"));
                int dot = actor.IndexOf('.');
                if (string.IsNullOrEmpty(rowId) || dot <= 0)
                    continue;
                actor = actor[..dot];
                string model = ResolveModelName(actor);
                if (model == null)
                    continue;
                string name = rowId.StartsWith("CoopGadgetInfo_") ? rowId[15..] : rowId;
                if (Byml.GetBool(row, "IsSpecial"))
                {
                    uint idol = Convert.ToUInt32(row.GetValueOrDefault("Idol") ?? 0u);
                    string group = SpecialGroups
                        .FirstOrDefault(g => Murmur3.Hash(g.Idol) == idol)
                        .Group;
                    Special.Add(
                        new GearEntry
                        {
                            Slot = GearSlot.Special,
                            RowId = name,
                            ActorName = actor,
                            ModelName = model,
                            NameKey = name,
                            ShowBone = group != null ? $"SurimiSP_{group}_Grp" : null,
                        }
                    );
                    continue;
                }
                if (!model.StartsWith("Ggt_", StringComparison.Ordinal))
                    continue;
                Gadget.Add(
                    new GearEntry
                    {
                        Slot = GearSlot.Gadget,
                        RowId = name,
                        Id = Byml.GetInt(row, "UiOrder", -1),
                        ActorName = actor,
                        ModelName = model,
                        NameKey = name,
                        Style = StyleName(Byml.GetInt(row, "Style")),
                    }
                );
                GadgetPlacements[name] = ReadGadgetPlacement(actor);
            }
            Gadget.Sort((a, b) => a.Id.CompareTo(b.Id));
        }

        //spl__EquipGadgetParam through the actor's parameter table chain, each slot's nearest
        //value first.
        (float, GadgetPlacement[]) ReadGadgetPlacement(string actor)
        {
            float? scale = null;
            var pos = new List<Dictionary<string, object>>();
            var rot = new List<Dictionary<string, object>>();
            try
            {
                for (int depth = 0; depth < 4 && actor != null; depth++)
                {
                    var pack = Romfs.GetActorPack(actor);
                    if (pack == null)
                        break;
                    string parent = null;
                    string table = pack.FindFile(x =>
                        x.StartsWith("Component/GameParameterTable/" + actor + ".")
                    );
                    if (table != null)
                    {
                        var root = Byml.AsHash(new Byml(pack.GetFile(table)).Root);
                        var param = Byml.AsHash(
                            Byml.AsHash(root?.GetValueOrDefault("GameParameters"))
                                ?.GetValueOrDefault("spl__EquipGadgetParam")
                        );
                        if (param != null)
                        {
                            if (scale == null && param.ContainsKey("ModelScale"))
                                scale = Byml.GetFloat(param, "ModelScale", 1f);
                            pos.Add(Byml.AsHash(param.GetValueOrDefault("PosOffset")));
                            rot.Add(Byml.AsHash(param.GetValueOrDefault("RotOffset")));
                        }
                        parent = Byml.GetString(root, "$parent");
                    }
                    if (string.IsNullOrEmpty(parent))
                        break;
                    actor = Path.GetFileName(parent);
                    actor = actor[..actor.IndexOf('.')];
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DB] Gadget placement {actor}: {ex.Message}");
            }

            static System.Numerics.Vector3 Read(
                List<Dictionary<string, object>> maps,
                GadgetSlot slot
            )
            {
                var key = Byml.HashKey(slot.ToString());
                var v = maps.Select(m => Byml.AsHash(m?.GetValueOrDefault(key)))
                    .FirstOrDefault(x => x != null);
                return v == null
                    ? default
                    : new(Byml.GetFloat(v, "X"), Byml.GetFloat(v, "Y"), Byml.GetFloat(v, "Z"));
            }
            var slots = Enum.GetValues<GadgetSlot>()
                .Select(s => new GadgetPlacement
                {
                    Position = Read(pos, s),
                    Rotation = Read(rot, s),
                })
                .ToArray();
            return (scale ?? 1f, slots);
        }

        Dictionary<string, object> ModelInfoOf(string actorName)
        {
            var pack = Romfs.GetActorPack(actorName);
            string file = pack?.FindFile(x => x.StartsWith("Component/ModelInfo/"));
            return file != null ? Byml.AsHash(new Byml(pack.GetFile(file)).Root) : null;
        }

        static IEnumerable<string> SubModelsOf(Dictionary<string, object> modelInfo) =>
            (Byml.AsArray(modelInfo.GetValueOrDefault("SubModels")) ?? new())
                .Select(x => Byml.GetString(Byml.AsHash(x), "FmdbName"))
                .Where(x => !string.IsNullOrEmpty(x));

        /// <summary>The material animation a Raiders clothes row dresses its shared model with, or null.</summary>
        public string VariationAnimOf(string actorName)
        {
            var info = ModelInfoOf(actorName);
            var first = Byml.AsHash(
                Byml.AsArray(info?.GetValueOrDefault("ModelVariationAnims"))?.FirstOrDefault()
            );
            string fmab = Byml.GetString(first, "Fmab");
            return string.IsNullOrEmpty(fmab) ? null : Path.GetFileNameWithoutExtension(fmab);
        }
    }
}
