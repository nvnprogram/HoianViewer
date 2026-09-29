using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using CafeStudio.UI;
using ImGuiNET;
using PlayerViewer.Core;
using PlayerViewer.Icons;
using Vector2 = System.Numerics.Vector2;

namespace PlayerViewer.UI
{
    // What the pickers show: the game's names in the chosen language, its icons, and the
    // fonts those names need.
    public partial class ViewerWindow
    {
        IconCache _icons;
        List<string> _languages = new();
        UiFonts _fonts;
        UiFonts _pendingFonts;
        bool _fontsDirty;

        //The gear pickers' texts the fonts were last built for; null until a romfs is loaded.
        List<string> _fontTexts;

        //The main font's metrics, which the classic name font is aligned to.
        float _mainAscent,
            _mainLineHeight;

        /// <summary>The icon cache, null until a romfs is loaded, which every list reads as "no icons".</summary>
        IconCache Icons => _icons;

        static bool HasGrid(GearSlot slot) =>
            slot is GearSlot.Head or GearSlot.Clothes or GearSlot.Shoes or GearSlot.MainWeapon;

        static bool HasIcons(GearSlot slot) =>
            HasGrid(slot) || slot is GearSlot.Bottom or GearSlot.Hair or GearSlot.Eyebrow;

        /// <summary>Icons and names for a freshly loaded romfs.</summary>
        void InitLists()
        {
            _icons?.Dispose();
            _icons = new IconCache(new IconSource(_romfs, _db));
            _languages = Localization.Available(_romfs);
            ApplyLanguage();
        }

        /// <summary>
        /// Names every entry in the configured language and prepares the glyphs they need.
        /// The atlas itself is rebuilt at the start of the next frame, outside ImGui's frame.
        /// </summary>
        void ApplyLanguage()
        {
            if (_db == null)
                return;
            var sw = Stopwatch.StartNew();
            var loc = Localization.Load(_romfs, _config.Language);
            Localization.Apply(loc, _db);
            _fontTexts = Enum.GetValues<GearSlot>()
                .SelectMany(slot => _db.GetList(slot))
                .Select(e => e.LocalizedName)
                .ToList();
            PrepareFonts();
            Console.WriteLine(
                $"[UI] Language {loc?.Language ?? Localization.None}: fonts ready, "
                    + $"{sw.ElapsedMilliseconds} ms"
            );
        }

        /// <summary>
        /// Builds the fonts the current look and names need. The atlas takes them at the start of
        /// the next frame, unless they are what it already holds.
        /// </summary>
        void PrepareFonts()
        {
            var fonts = UiFonts.Build(SideOrderMode, _romfs, _config.Language, _fontTexts);
            var target = _fontsDirty ? _pendingFonts : _fonts;
            if (fonts.Signature == target?.Signature)
            {
                fonts.Dispose();
                return;
            }
            _pendingFonts?.Dispose();
            _pendingFonts = fonts;
            _fontsDirty = true;
        }

        //Hands the pending fonts to the atlas builder; the next build picks them up.
        UiFonts InstallPendingFonts()
        {
            _fontsDirty = false;
            var old = _fonts;
            _fonts = _pendingFonts;
            _pendingFonts = null;
            var fonts = _fonts;
            float ascent = _mainAscent,
                lineHeight = _mainLineHeight;
            ImGuiController.ExtraFonts =
                fonts != null ? atlas => fonts.AddTo(atlas, ascent, lineHeight) : null;
            return old;
        }

        /// <summary>Swaps in the fonts a language or look change asked for. Between frames only.</summary>
        void UpdateFonts()
        {
            if (!_fontsDirty)
                return;
            UiFonts.Clear();
            var old = InstallPendingFonts();
            var sw = Stopwatch.StartNew();
            _imgui.RebuildFonts();
            _fonts?.AfterBuild();
            Console.WriteLine($"[UI] Font atlas rebuilt in {sw.ElapsedMilliseconds} ms");
            old?.Dispose();
        }

        void DisposeLists()
        {
            _icons?.Dispose();
            _icons = null;
        }

        void DrawListSettings()
        {
            Widgets.SectionHeader("Lists");
            string current = _config.Language;
            Widgets.LabeledRow(
                "Language",
                () =>
                {
                    ImGui.SetNextItemWidth(-1);
                    if (!Widgets.BeginCombo("##language", Localization.Describe(current)))
                        return;
                    var options = new List<string> { Localization.None };
                    options.AddRange(_languages);
                    Widgets.PopupRows(
                        "language",
                        options.Count,
                        options.IndexOf(current),
                        (row, isSelected) =>
                            ImGui.Selectable(Localization.Describe(options[row]), isSelected),
                        row =>
                        {
                            _config.Language = options[row];
                            _config.Save();
                            ApplyLanguage();
                        }
                    );
                    ImGui.EndCombo();
                }
            );
            Widgets.ItemTooltip("Gear and weapon names. Search matches the name or the codename.");

            Widgets.Checkbox(
                "Grid for weapons and gear",
                _config.GearGrid,
                v => _config.GearGrid = v,
                _config.Save
            );
            Widgets.ItemTooltip("Pick weapons and gear from a tile grid");
        }
    }
}
