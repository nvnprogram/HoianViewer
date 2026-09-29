using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PlayerViewer.Core.Formats;

namespace PlayerViewer.Core
{
    /// <summary>
    /// Display names for gear and weapons in one of the languages the romfs ships, read from
    /// that language's message archive.
    /// </summary>
    public class Localization
    {
        public const string None = "None";
        public const string DefaultLanguage = "USen";

        static readonly Dictionary<string, string> LanguageNames = new()
        {
            ["USen"] = "English (US)",
            ["EUen"] = "English (EU)",
            ["USes"] = "Spanish (Americas)",
            ["EUes"] = "Spanish (EU)",
            ["USfr"] = "French (Canada)",
            ["EUfr"] = "French (EU)",
            ["EUde"] = "German",
            ["EUit"] = "Italian",
            ["EUnl"] = "Dutch",
            ["EUru"] = "Russian",
            ["JPja"] = "Japanese",
            ["KRko"] = "Korean",
            ["CNzh"] = "Chinese (Simplified)",
            ["TWzh"] = "Chinese (Traditional)",
        };

        //Which message file names a slot, and how a row id becomes its label there.
        static readonly (GearSlot Slot, string File, bool StripPrefix)[] Sources =
        {
            (GearSlot.Head, "CommonMsg/Gear/GearName_Head.msbt", true),
            (GearSlot.Clothes, "CommonMsg/Gear/GearName_Clothes.msbt", true),
            (GearSlot.Shoes, "CommonMsg/Gear/GearName_Shoes.msbt", true),
            (GearSlot.MainWeapon, "CommonMsg/Weapon/WeaponName_Main.msbt", false),
            (GearSlot.SpecialWeapon, "CommonMsg/Weapon/WeaponName_Special.msbt", false),
        };

        public string Language { get; }

        readonly Dictionary<GearSlot, Dictionary<string, string>> _names = new();

        Localization(string language) => Language = language;

        /// <summary>The language codes the romfs carries a message archive for, by name.</summary>
        public static List<string> Available(Romfs romfs)
        {
            var codes = new List<string>();
            if (romfs == null)
                return codes;
            foreach (var file in romfs.FindFiles("Mals", "*.Product.*.sarc*"))
            {
                string name = Path.GetFileName(file);
                string code = name.Substring(0, name.IndexOf('.'));
                if (!codes.Contains(code))
                    codes.Add(code);
            }
            codes.Sort(
                (a, b) => string.Compare(Describe(a), Describe(b), StringComparison.Ordinal)
            );
            return codes;
        }

        public static string Describe(string code) =>
            code == None ? "None (codenames)"
            : LanguageNames.TryGetValue(code, out var name) ? $"{name} ({code})"
            : code;

        /// <summary>Loads a language, or null when it is None, the romfs lacks it or its archive
        /// cannot be read. A message file that cannot be read leaves its slot on codenames.</summary>
        public static Localization Load(Romfs romfs, string language)
        {
            if (romfs == null || string.IsNullOrEmpty(language) || language == None)
                return null;
            Sarc archive;
            try
            {
                var data = romfs.ReadProduct("Mals", language, "sarc*");
                if (data == null)
                    return null;
                archive = new Sarc(data);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Localization] {language}: {ex.Message}");
                return null;
            }
            var loc = new Localization(language);
            foreach (var (slot, path, _) in Sources)
            {
                var names = new Dictionary<string, string>(StringComparer.Ordinal);
                try
                {
                    if (archive.GetFile(path) is { } data)
                        names = new Msbt(data).Messages;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Localization] {language} {path}: {ex.Message}");
                }
                loc._names[slot] = names;
            }
            return loc;
        }

        /// <summary>The game's name for a row, or null when it has none.</summary>
        public string Name(GearSlot slot, string rowId)
        {
            if (string.IsNullOrEmpty(rowId) || !_names.TryGetValue(slot, out var names))
                return null;
            bool strip = Array.Find(Sources, x => x.Slot == slot).StripPrefix;
            string label = rowId;
            if (strip)
            {
                int underscore = rowId.IndexOf('_');
                label = underscore >= 0 ? rowId.Substring(underscore + 1) : rowId;
            }
            //Rows the game never shows are named "-", which is no name at all.
            return names.TryGetValue(label, out var name) && name.Any(char.IsLetterOrDigit)
                ? name.Trim()
                : null;
        }

        /// <summary>Sets every entry's localized name, or clears them all for no language.</summary>
        public static void Apply(Localization loc, GameDatabase db)
        {
            foreach (GearSlot slot in Enum.GetValues(typeof(GearSlot)))
            foreach (var entry in db.GetList(slot))
                entry.LocalizedName = loc?.Name(slot, entry.RowId);
        }
    }
}
