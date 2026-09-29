using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PlayerViewer.Core;
using PlayerViewer.Core.Formats;
using BntxFile = Syroot.NintenTools.NSW.Bntx.BntxFile;

namespace PlayerViewer.Icons
{
    /// <summary>A decoded icon: straight alpha RGBA8, display (sRGB) values, rows top down.</summary>
    public sealed class IconImage
    {
        public int Width;
        public int Height;
        public byte[] Rgba;
    }

    /// <summary>
    /// Reads the game's own icons out of the romfs, by key. Gear and weapons have one texture
    /// per row; bottoms, hair, eyebrows, player types, skin tones and eye colours only exist
    /// as layout parts, so those are composited the way the customisation screen draws them.
    /// Called from the icon worker thread only.
    /// </summary>
    public sealed class IconSource
    {
        readonly Romfs _romfs;
        readonly GameDatabase _db;
        readonly Dictionary<string, LayoutArchive> _layouts = new();

        public IconSource(Romfs romfs, GameDatabase db)
        {
            _romfs = romfs;
            _db = db;
        }

        /// <summary>
        /// The icon key for a list entry, or null for a slot that has no icons. Eyebrow icons
        /// differ by gender, so theirs carries it.
        /// </summary>
        public static string KeyFor(GearEntry entry, bool female = true)
        {
            if (entry == null || entry.IsCustom)
                return null;
            return entry.Slot switch
            {
                GearSlot.Head or GearSlot.Clothes or GearSlot.Shoes => "Gear/" + entry.RowId,
                GearSlot.MainWeapon => "Wpn/" + (entry.IconRowId ?? entry.RowId),
                GearSlot.Bottom => $"Bottom/{entry.RowId}/{entry.Variation}",
                GearSlot.Hair => "Hair/" + entry.RowId,
                GearSlot.Eyebrow => $"Eyebrow/{entry.RowId}/{(female ? "F" : "M")}",
                _ => null,
            };
        }

        public static string PlayerTypeKey(int type) => $"PlayerType/{type}";

        public static string SkinKey(int index) => $"Skin/{index}";

        public static string EyeKey(int index) => $"Eye/{index}";

        /// <summary>Decodes an icon, or returns null when the romfs has none for the key.</summary>
        public IconImage Load(string key)
        {
            var parts = key.Split('/');
            switch (parts[0])
            {
                case "Gear":
                    return LoadSingle($"UI/Icon/Gear/{parts[1]}.bntx");
                case "Wpn":
                    return LoadSingle($"UI/Icon/Wpn/Wst_{parts[1]}.bntx");
                case "Bottom":
                    return Bottom(parts[1], int.Parse(parts[2]));
                case "Hair":
                    return Hair(parts[1]);
                case "Eyebrow":
                    return Eyebrow(parts[1], parts[2] == "F");
                case "PlayerType":
                    return LayoutIcons.PlayerType(Layout("BtnSelectHead_00"), int.Parse(parts[1]));
                case "Skin":
                    return LayoutIcons.Colour(Layout("BtnSelectColor_00"), int.Parse(parts[1]));
                case "Eye":
                    return LayoutIcons.Colour(
                        Layout("BtnSelectColor_00"),
                        LayoutIcons.EyeFrameBase + int.Parse(parts[1])
                    );
            }
            return null;
        }

        IconImage LoadSingle(string path)
        {
            var data = _romfs.ReadFile(path);
            if (data == null)
                return null;
            var bntx = new BntxFile(new MemoryStream(data));
            var texture = bntx.Textures.FirstOrDefault();
            return texture == null ? null : Surface.DecodeImage(bntx, texture);
        }

        //The Type animation keys a bottom's Id; Order is only the menu's sort key.
        IconImage Bottom(string rowId, int variation)
        {
            if (!_db.BottomRows.TryGetValue(rowId, out var row))
                return null;
            int id = Byml.GetInt(row, "Id", -1);
            if (id < 0)
                return null;
            return LayoutIcons.Bottom(Layout("BtnSelectPants_00"), id, variation);
        }

        //Type animation frame by hair Id, as the game's head button hardcodes it. It gives Id 6
        //frame 4.4, which shows frame 4's texture.
        static readonly Dictionary<int, int> HairFrames = new()
        {
            [0] = 0,
            [1] = 1,
            [2] = 3,
            [3] = 2,
            [4] = 9,
            [5] = 8,
            [6] = 4,
            [7] = 5,
            [8] = 6,
            [9] = 7,
            [10] = 10,
            [11] = 11,
            [12] = 12,
            [13] = 13,
            [14] = 14,
            [15] = 15,
            [30] = 100,
            [31] = 102,
            [32] = 101,
            [33] = 103,
            [34] = 106,
            [35] = 107,
            [36] = 104,
            [37] = 105,
        };

        //A Side Order variant shows its base hair.
        IconImage Hair(string rowId)
        {
            int variant = rowId.IndexOf("_Sdodr", StringComparison.Ordinal);
            if (variant > 0)
                rowId = rowId.Substring(0, variant);
            var entry = _db.Hair.Find(e => e.RowId == rowId);
            if (entry == null || !HairFrames.TryGetValue(entry.Id, out int frame))
                return null;
            return LayoutIcons.Hair(Layout("BtnSelectHead_00"), frame);
        }

        //Six frames per player type, in player type order; the first four are the shapes, by Id
        //within the species (octoling ids start at 20).
        IconImage Eyebrow(string rowId, bool female)
        {
            var entry = _db.Eyebrow.Find(e => e.RowId == rowId);
            if (entry == null)
                return null;
            bool squid = rowId.StartsWith("Eyb_SQD", StringComparison.Ordinal);
            int shape = squid ? entry.Id : entry.Id - 20;
            if (shape < 0 || shape > 3)
                return null;
            int playerType = (squid ? 0 : 2) + (female ? 0 : 1);
            return LayoutIcons.Eyebrow(Layout("BtnSelectEyebrow_00"), playerType * 6 + shape);
        }

        LayoutArchive Layout(string name)
        {
            if (!_layouts.TryGetValue(name, out var archive))
            {
                archive = LayoutArchive.Load(_romfs, name);
                _layouts[name] = archive;
            }
            return archive;
        }
    }
}
