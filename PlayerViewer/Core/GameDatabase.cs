using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PlayerViewer.Core.Formats;

namespace PlayerViewer.Core
{
    public enum GearSlot
    {
        Head,
        Clothes,
        Shoes,
        Hair,
        Eyebrow,
        Bottom,
        Tank,
        MainWeapon,
        SpecialWeapon,
    }

    /// <summary>One selectable entry (gear piece, hair, weapon, ...).</summary>
    public class GearEntry
    {
        public GearSlot Slot;
        public string RowId = "";       //RSDB row id, e.g. "Hed_ACC003" or "Blaster_LightLong_00"
        public int Id = -1;
        public string Label = "";       //Localized-ish label (JP) from RSDB
        public int Variation;           //Variation index (0 = default)
        public int VariationCount;      //Total variations
        public bool IsCustom;           //Loaded from a user file, not the romfs
        public string CustomPath;       //Source path for custom entries
        public string WeaponType = "";  //Weapon rows: Versus/Coop/Mission/...
        public string ActorName = "";   //Actor pack name (weapons: from GameActor/SpecActor)

        /// <summary>Display name for UI lists.</summary>
        public string DisplayName
        {
            get
            {
                string name = RowId;
                if (Variation > 0) name += $" (v{Variation})";
                if (IsCustom) name += "  [custom]";
                return name;
            }
        }
    }

    public class TeamColorSet
    {
        public string Name = "";
        public System.Numerics.Vector3 Alpha;   //sRGB 0-1
        public System.Numerics.Vector3 Bravo;
        public System.Numerics.Vector3 Charlie;
        public System.Numerics.Vector3 Neutral;
    }

    /// <summary>
    /// Parsed RSDB tables + model name resolution through actor packs.
    /// </summary>
    public class GameDatabase
    {
        public Romfs Romfs { get; }

        public List<GearEntry> Head = new();
        public List<GearEntry> Clothes = new();
        public List<GearEntry> Shoes = new();
        public List<GearEntry> Hair = new();
        public List<GearEntry> Eyebrow = new();
        public List<GearEntry> Bottom = new();
        public List<GearEntry> Tank = new();
        public List<GearEntry> MainWeapons = new();
        public List<GearEntry> SpecialWeapons = new();
        public List<TeamColorSet> TeamColors = new();

        //RowId -> raw RSDB row for extra fields (HarnessType, AlphaMaskF/M/V1 etc)
        public Dictionary<string, Dictionary<string, object>> HeadRows = new();
        public Dictionary<string, Dictionary<string, object>> ClothesRows = new();
        public Dictionary<string, Dictionary<string, object>> ShoesRows = new();
        public Dictionary<string, Dictionary<string, object>> BottomRows = new();

        public GameDatabase(Romfs romfs)
        {
            Romfs = romfs;
            Load();
        }

        public List<GearEntry> GetList(GearSlot slot) => slot switch
        {
            GearSlot.Head => Head,
            GearSlot.Clothes => Clothes,
            GearSlot.Shoes => Shoes,
            GearSlot.Hair => Hair,
            GearSlot.Eyebrow => Eyebrow,
            GearSlot.Bottom => Bottom,
            GearSlot.Tank => Tank,
            GearSlot.MainWeapon => MainWeapons,
            GearSlot.SpecialWeapon => SpecialWeapons,
            _ => new List<GearEntry>(),
        };

        void Load()
        {
            LoadGearTable("GearInfoHead", GearSlot.Head, Head, HeadRows);
            //Hed_INV000 ("INVISIBLE") is the game's real no-headgear: it renders
            //nothing but still carries the default hair-arrange presets (e.g.
            //Blitz_SQD004_0 collapsing SQD004's extra curl shells). It replaces the
            //old "Blank" option, so float it to the top as the default head.
            int inv = Head.FindIndex(x => x.RowId == "Hed_INV000");
            if (inv > 0)
            {
                var entry = Head[inv];
                Head.RemoveAt(inv);
                Head.Insert(0, entry);
            }
            LoadGearTable("GearInfoClothes", GearSlot.Clothes, Clothes, ClothesRows);
            LoadGearTable("GearInfoShoes", GearSlot.Shoes, Shoes, ShoesRows);
            LoadSimpleTable("HairInfo", GearSlot.Hair, Hair);
            LoadSimpleTable("EyebrowInfo", GearSlot.Eyebrow, Eyebrow);
            LoadGearTable("BottomInfo", GearSlot.Bottom, Bottom, BottomRows);
            LoadSimpleTable("TankInfo", GearSlot.Tank, Tank);
            LoadWeaponTable("WeaponInfoMain", GearSlot.MainWeapon, MainWeapons);
            LoadWeaponTable("WeaponInfoSpecial", GearSlot.SpecialWeapon, SpecialWeapons);
            LoadTeamColors();
        }

        List<object> ReadTable(string table)
        {
            //Version prefix (b20 etc) may change between game versions, and a layered
            //mod may override the table; FindFiles handles both (layered wins).
            var file = Romfs.FindFiles("RSDB", $"{table}.Product.*.rstbl.byml*").LastOrDefault();
            if (file == null)
                return new List<object>();
            var byml = new Byml(Romfs.Decompress(File.ReadAllBytes(file)));
            return byml?.Root as List<object> ?? new List<object>();
        }

        void LoadGearTable(string table, GearSlot slot, List<GearEntry> target,
            Dictionary<string, Dictionary<string, object>> rawRows = null)
        {
            foreach (var row in ReadTable(table).OfType<Dictionary<string, object>>())
            {
                string rowId = Byml.GetString(row, "__RowId");
                if (string.IsNullOrEmpty(rowId))
                    continue;

                rawRows?.TryAdd(rowId, row);

                int varNum = Byml.GetInt(row, "VariationNum", 0);
                for (int v = 0; v <= varNum; v++)
                {
                    target.Add(new GearEntry
                    {
                        Slot = slot,
                        RowId = rowId,
                        Id = Byml.GetInt(row, "Id", -1),
                        Label = Byml.GetString(row, "Label"),
                        Variation = v,
                        VariationCount = varNum + 1,
                    });
                }
            }
            //List.Sort is unstable; without the variation tiebreak v1 can land above v0.
            target.Sort((a, b) =>
            {
                int c = string.Compare(a.RowId, b.RowId, StringComparison.OrdinalIgnoreCase);
                return c != 0 ? c : a.Variation.CompareTo(b.Variation);
            });
        }

        void LoadSimpleTable(string table, GearSlot slot, List<GearEntry> target)
        {
            foreach (var row in ReadTable(table).OfType<Dictionary<string, object>>())
            {
                string rowId = Byml.GetString(row, "__RowId");
                if (string.IsNullOrEmpty(rowId))
                    continue;

                //Tanks reference their actor via SpecActor
                //(Work/Actor/PlayerTank_Jetpack.engine__actor__ActorParam.gyml).
                string actorRef = Byml.GetString(row, "SpecActor");
                string actorName = Path.GetFileName(actorRef ?? "");
                int actorDot = actorName.IndexOf('.');
                if (actorDot >= 0) actorName = actorName.Substring(0, actorDot);

                target.Add(new GearEntry
                {
                    Slot = slot,
                    RowId = rowId,
                    Id = Byml.GetInt(row, "Id", -1),
                    Label = Byml.GetString(row, "Label"),
                    ActorName = actorName,
                });
            }
            //Hair/eyebrows have an Order field ordering them like the in-game UI.
            target.Sort((a, b) => string.Compare(a.RowId, b.RowId, StringComparison.OrdinalIgnoreCase));
        }

        void LoadWeaponTable(string table, GearSlot slot, List<GearEntry> target)
        {
            foreach (var row in ReadTable(table).OfType<Dictionary<string, object>>())
            {
                string rowId = Byml.GetString(row, "__RowId");
                if (string.IsNullOrEmpty(rowId))
                    continue;

                //GameActor (mains) / SpecActor (specials): Work/Actor/<name>.engine__actor__ActorParam.gyml
                string actorRef = Byml.GetString(row, "GameActor");
                if (string.IsNullOrEmpty(actorRef))
                    actorRef = Byml.GetString(row, "SpecActor");
                string actorName = Path.GetFileName(actorRef ?? "");
                int actorDot = actorName.IndexOf('.');
                if (actorDot >= 0) actorName = actorName.Substring(0, actorDot);

                target.Add(new GearEntry
                {
                    Slot = slot,
                    RowId = rowId,
                    Id = Byml.GetInt(row, "Id", -1),
                    Label = Byml.GetString(row, "Label"),
                    WeaponType = Byml.GetString(row, "Type"),
                    ActorName = actorName,
                });
            }
            target.Sort((a, b) => string.Compare(a.RowId, b.RowId, StringComparison.OrdinalIgnoreCase));
        }

        void LoadTeamColors()
        {
            foreach (var row in ReadTable("TeamColorDataSet").OfType<Dictionary<string, object>>())
            {
                string rowId = Byml.GetString(row, "__RowId");
                //RowId looks like Work/Gyml/<Name>.game__gfx__parameter__TeamColorDataSet.gyml
                string name = rowId;
                int slash = name.LastIndexOf('/');
                if (slash >= 0) name = name.Substring(slash + 1);
                int dot = name.IndexOf('.');
                if (dot >= 0) name = name.Substring(0, dot);

                System.Numerics.Vector3 ReadColor(string key)
                {
                    var c = Byml.AsHash(row.GetValueOrDefault(key));
                    return c == null
                        ? System.Numerics.Vector3.Zero
                        : new System.Numerics.Vector3(Byml.GetFloat(c, "R"), Byml.GetFloat(c, "G"), Byml.GetFloat(c, "B"));
                }

                TeamColors.Add(new TeamColorSet
                {
                    Name = name,
                    Alpha = ReadColor("AlphaTeamColor"),
                    Bravo = ReadColor("BravoTeamColor"),
                    Charlie = ReadColor("CharlieTeamColor"),
                    Neutral = ReadColor("NeutralColor"),
                });
            }
            TeamColors.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        }

        #region model resolution

        /// <summary>
        /// Splits an fmdb reference (Work/Model/Weapon/Wmn_Maneuver_Dual/output/
        /// Wmn_Maneuver_Dual_L.fmdb) into the bfres file name (the folder above
        /// "output": Wmn_Maneuver_Dual) and the model name inside it (the stem:
        /// Wmn_Maneuver_Dual_L). For most gear both are identical.
        /// </summary>
        public static (string file, string model) ParseFmdb(string fmdb)
        {
            string model = Path.GetFileNameWithoutExtension(fmdb);
            var parts = fmdb.Replace('\\', '/').Split('/');
            int outIdx = Array.IndexOf(parts, "output");
            string file = outIdx > 0 ? parts[outIdx - 1] : model;
            return (file, model);
        }

        readonly Dictionary<string, string> _modelNameCache = new();

        /// <summary>
        /// Resolves the bfres model name for an actor through its actor pack's ModelInfo.
        /// Falls back to the actor name itself when the model file exists directly.
        /// </summary>
        public string ResolveModelName(string actorName)
        {
            if (_modelNameCache.TryGetValue(actorName, out var cached))
                return cached;

            string result = null;

            var pack = Romfs.GetActorPack(actorName);
            if (pack != null)
            {
                string modelInfo = pack.FindFile(x => x.StartsWith("Component/ModelInfo/"));
                if (modelInfo != null)
                {
                    var byml = new Byml(pack.GetFile(modelInfo));
                    string fmdb = Byml.GetString(Byml.AsHash(byml.Root), "Fmdb");
                    if (!string.IsNullOrEmpty(fmdb))
                        result = ParseFmdb(fmdb).file;
                }
            }

            if (result == null && Romfs.ModelExists(actorName))
                result = actorName;

            _modelNameCache[actorName] = result;
            return result;
        }

        /// <summary>
        /// Resolves weapon models for a weapon RSDB row: returns (right/main, left) as
        /// (bfres file, model name) pairs; left is (null,null) for single weapons.
        /// Left model comes from the MirrorModel component (dualies etc) and usually
        /// lives as a second model inside the SAME bfres file (Wmn_X + Wmn_X_L).
        /// </summary>
        public ((string file, string model) main, (string file, string model) left) ResolveWeaponModels(GearEntry weapon)
        {
            string actorName = !string.IsNullOrEmpty(weapon.ActorName)
                ? weapon.ActorName
                : (weapon.Slot == GearSlot.SpecialWeapon ? "Weapon" : "WmnG_") + weapon.RowId;
            var pack = Romfs.GetActorPack(actorName);

            //Some rows use a different actor prefix; fall back to trying the raw name.
            if (pack == null)
                pack = Romfs.GetActorPack(weapon.RowId);

            (string, string) main = (null, null), left = (null, null);

            //Walk this pack and parent packs for ModelInfo / MirrorModel components.
            var visited = new HashSet<string>();
            var current = pack;
            string currentName = actorName;
            for (int depth = 0; depth < 4 && current != null; depth++)
            {
                if (!visited.Add(currentName)) break;

                string mirror = current.FindFile(x => x.StartsWith("Component/MirrorModel/"));
                if (mirror != null && (main.Item1 == null || left.Item1 == null))
                {
                    var byml = new Byml(current.GetFile(mirror));
                    var hash = Byml.AsHash(byml.Root);
                    string rightFmdb = Byml.GetString(hash, "RightFmdb");
                    string leftFmdb = Byml.GetString(hash, "Fmdb");
                    if (main.Item1 == null && !string.IsNullOrEmpty(rightFmdb))
                        main = ParseFmdb(rightFmdb);
                    if (left.Item1 == null && !string.IsNullOrEmpty(leftFmdb))
                        left = ParseFmdb(leftFmdb);
                    //MirrorModel with RightFmdb only (Splat Dualies etc): the left gun
                    //reuses the right mesh - a rigid 180° flip, no dedicated _L model.
                    if (left.Item1 == null && main.Item1 != null)
                        left = main;
                }

                string modelInfo = current.FindFile(x => x.StartsWith("Component/ModelInfo/"));
                if (modelInfo != null && main.Item1 == null)
                {
                    var byml = new Byml(current.GetFile(modelInfo));
                    string fmdb = Byml.GetString(Byml.AsHash(byml.Root), "Fmdb");
                    if (!string.IsNullOrEmpty(fmdb))
                        main = ParseFmdb(fmdb);
                }

                if (main.Item1 != null && left.Item1 != null)
                    break;

                //Follow the $parent actor chain.
                string actorParam = current.FindFile(x => x.StartsWith("Actor/") && x.Contains(currentName));
                actorParam ??= current.FindFile(x => x.StartsWith("Actor/"));
                if (actorParam == null) break;

                var actorByml = new Byml(current.GetFile(actorParam));
                string parent = Byml.GetString(Byml.AsHash(actorByml.Root), "$parent");
                if (string.IsNullOrEmpty(parent)) break;

                //Work/Actor/WeaponManeuverDual.engine__actor__ActorParam.gyml -> WeaponManeuverDual
                string parentName = Path.GetFileName(parent);
                int dot = parentName.IndexOf('.');
                if (dot >= 0) parentName = parentName.Substring(0, dot);

                current = Romfs.GetActorPack(parentName);
                currentName = parentName;
            }

            //Dualie-style weapons: the left fmdb is the actual mirrored second model.
            //For normal weapons MirrorModel is absent and left stays null.
            if (main.Item1 == null && left.Item1 != null)
            {
                //Only a single fmdb found via MirrorModel "Fmdb" key - treat as main.
                main = left;
                left = (null, null);
            }

            return (main, left);
        }

        #endregion
    }
}
