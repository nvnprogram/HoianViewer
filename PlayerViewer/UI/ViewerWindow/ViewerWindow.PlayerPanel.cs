using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ImGuiNET;
using PlayerViewer.Core;
using PlayerViewer.Icons;
using Vector2 = System.Numerics.Vector2;
using Vector4 = System.Numerics.Vector4;

namespace PlayerViewer.UI
{
    // Left-hand player configuration panel: player type, gear, colors, lighting, view.
    public partial class ViewerWindow
    {
        static readonly string[] PlayerTypes =
        {
            "Inkling Girl (Player00)",
            "Inkling Boy (Player01)",
            "Octoling Girl (Player02)",
            "Octoling Boy (Player03)",
        };

        void DrawPlayerPanel()
        {
            Widgets.SectionHeader("Player");

            if (Widgets.Button("Reset", new Vector2(-1, 0)))
                ResetPlayerDefaults();

            float half = (ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X) * 0.5f;
            if (Widgets.Button("Save preset", new Vector2(half, 0)))
                SavePreset();
            ImGui.SameLine();
            if (Widgets.Button("Load preset", new Vector2(half, 0)))
                LoadPreset();
            if (!string.IsNullOrEmpty(_presetStatus))
                Widgets.DimText(_presetStatus);

            DrawPlayerTypeCombo();

            GearRow("Hair", GearSlot.Hair, _scene.CurrentHair);
            GearRow("Eyebrow", GearSlot.Eyebrow, _scene.CurrentEyebrow);

            if (Icons != null)
                DrawColorSwatches();
            else
            {
                Widgets.LabeledRow(
                    "Eyes",
                    () =>
                    {
                        ImGui.SetNextItemWidth(-1);
                        Widgets.SliderInt(
                            "##eye",
                            _scene.EyeColor,
                            0,
                            EyeColorCount - 1,
                            v => _scene.ApplyEyeColor(v),
                            SavePlayerConfig
                        );
                    }
                );

                Widgets.LabeledRow(
                    "Skin",
                    () =>
                    {
                        ImGui.SetNextItemWidth(-1);
                        Widgets.SliderInt(
                            "##skin",
                            _scene.SkinTone,
                            0,
                            SkinToneCount - 1,
                            v => _scene.ApplySkinTone(v),
                            SavePlayerConfig
                        );
                    }
                );
            }

            Widgets.Checkbox(
                "Hair physics",
                _scene.HairPhysicsEnabled,
                v =>
                {
                    _scene.HairPhysicsEnabled = v;
                    if (v)
                        _scene.ResetHairPhysics();
                }
            );

            DrawTeamColorSection();

            Widgets.SectionHeader("Gear");
            GearRow("Head", GearSlot.Head, _scene.CurrentHead, allowNone: false);
            GearRow("Clothes", GearSlot.Clothes, _scene.CurrentClothes);
            GearRow("Bottom", GearSlot.Bottom, _scene.CurrentBottom);
            GearRow("Shoes", GearSlot.Shoes, _scene.CurrentShoes);

            Widgets.SectionHeader("Equipment");
            GearRow("Weapon", GearSlot.MainWeapon, _scene.CurrentWeapon, noneLabel: "Free");
            GearRow("Tank", GearSlot.Tank, _scene.CurrentTank);

            DrawLightingSection();
            DrawViewSection();
            DrawLayeredFsSection();
        }

        void DrawViewSection()
        {
            Widgets.SectionHeader("View");
            if (Widgets.Button("Reset camera", new Vector2(-1, 0)))
            {
                if (_standalone != null)
                    _pipeline.FrameSphere(_standalone.GetBounding());
                else
                    _pipeline.FramePlayer();
            }

            Widgets.Checkbox(
                "Self shadow",
                _pipeline.EnableSelfShadow,
                v => _pipeline.EnableSelfShadow = v
            );
            Widgets.ItemTooltip("The game's self shadowing.");

            DrawEnvironmentRows();

            //Background (mode/color/image) lives on the preset; folded in here on the left.
            DrawBackgroundSection();
        }

        void DrawLightingSection()
        {
            Widgets.SectionHeader("Lighting");
            if (Widgets.Button("Reset lighting", new Vector2(-1, 0)))
                _pipeline.ResetLighting();
            Widgets.Checkbox(
                "Light follows camera",
                _pipeline.LightFollowsCamera,
                v => _pipeline.LightFollowsCamera = v
            );
            if (!_pipeline.LightFollowsCamera)
            {
                ImGui.SetNextItemWidth(-1);
                Widgets.SliderFloat(
                    "##lightaz",
                    _pipeline.LightAzimuth,
                    -180,
                    180,
                    v => _pipeline.LightAzimuth = v,
                    null,
                    "Azimuth %.0f°"
                );
                ImGui.SetNextItemWidth(-1);
                Widgets.SliderFloat(
                    "##lightel",
                    _pipeline.LightElevation,
                    -89,
                    89,
                    v => _pipeline.LightElevation = v,
                    null,
                    "Elevation %.0f°"
                );
            }
        }

        //-1 is the custom colours rather than a set.
        void PickTeamColor(int index)
        {
            _useCustomTeamColor = index < 0;
            if (index >= 0)
                _teamColorIndex = index;
            ApplyTeamColor();
            SavePlayerConfig();
        }

        void DrawTeamColorSection()
        {
            Widgets.SectionHeader("Team Color");
            var colorSet = _db.TeamColors.ElementAtOrDefault(_teamColorIndex);
            ImGui.SetNextItemWidth(-1);
            string teamPreview = _useCustomTeamColor ? "Custom" : colorSet?.Name ?? "(default)";
            if (Widgets.BeginCombo("##teamcolor", teamPreview))
            {
                //"Custom" is row 0, freely picked colors instead of an RSDB set, so the sets
                //sit one row further down than their own index.
                Widgets.PopupRows(
                    "teamcolor",
                    _db.TeamColors.Count + 1,
                    _useCustomTeamColor ? 0 : _teamColorIndex + 1,
                    (row, isSelected) =>
                    {
                        var alpha = row == 0 ? _customTeam.Alpha : _db.TeamColors[row - 1].Alpha;
                        var bravo = row == 0 ? _customTeam.Bravo : _db.TeamColors[row - 1].Bravo;
                        ImGui.ColorButton(
                            $"##swatchA{row}",
                            new Vector4(alpha.X, alpha.Y, alpha.Z, 1),
                            ImGuiColorEditFlags.NoTooltip,
                            new Vector2(14, 14)
                        );
                        ImGui.SameLine();
                        ImGui.ColorButton(
                            $"##swatchB{row}",
                            new Vector4(bravo.X, bravo.Y, bravo.Z, 1),
                            ImGuiColorEditFlags.NoTooltip,
                            new Vector2(14, 14)
                        );
                        ImGui.SameLine();
                        string name = row == 0 ? "Custom" : _db.TeamColors[row - 1].Name;
                        return ImGui.Selectable(name, isSelected);
                    },
                    row => PickTeamColor(row - 1)
                );
                ImGui.EndCombo();
            }
            if (_useCustomTeamColor)
            {
                bool custChanged = false;
                var a = _customTeam.Alpha;
                var b = _customTeam.Bravo;
                var c = _customTeam.Charlie;
                custChanged |= Widgets.ColorSwatch("Alpha##cust", ref a);
                TeamColumn(1);
                custChanged |= Widgets.ColorSwatch("Bravo##cust", ref b);
                TeamColumn(2);
                custChanged |= Widgets.ColorSwatch("Charlie##cust", ref c);
                if (custChanged)
                {
                    _customTeam.Alpha = a;
                    _customTeam.Bravo = b;
                    _customTeam.Charlie = c;
                    _customTeam.Neutral = (a + b) * 0.5f;
                    ApplyTeamColor();
                    SavePlayerConfig();
                }
            }
            if (Widgets.RadioButton("Alpha", _teamIndex == 0))
            {
                _teamIndex = 0;
                ApplyTeamColor();
                SavePlayerConfig();
            }
            TeamColumn(1);
            if (Widgets.RadioButton("Bravo", _teamIndex == 1))
            {
                _teamIndex = 1;
                ApplyTeamColor();
                SavePlayerConfig();
            }
            TeamColumn(2);
            if (Widgets.RadioButton("Charlie", _teamIndex == 2))
            {
                _teamIndex = 2;
                ApplyTeamColor();
                SavePlayerConfig();
            }
        }

        //Side Order sets the team rows in three even columns.
        static void TeamColumn(int column)
        {
            if (!SideOrderControls.On)
            {
                ImGui.SameLine();
                return;
            }
            float left = ImGui.GetStyle().WindowPadding.X + SideOrderLayout.TextIndent;
            float width = ImGui.GetWindowContentRegionMax().X - left;
            ImGui.SameLine(left + width * column / 3);
        }

        void DrawLayeredFsSection()
        {
            Widgets.SectionHeader("LayeredFS (mods)");

            ImGui.SetNextItemWidth(-70);
            if (Widgets.PathInput("##layeredpath", ref _layeredInput, 512))
            {
                _config.LayeredFsPath = _layeredInput;
                _config.Save();
            }
            ImGui.SameLine();
            if (Widgets.Button("...##layeredbrowse", new Vector2(-1, 0)))
            {
                string folder = NativeFolderPicker.SelectFolder(
                    "Select LayeredFS (mod) folder",
                    _layeredInput
                );
                if (!string.IsNullOrEmpty(folder))
                {
                    _layeredInput = folder;
                    _config.LayeredFsPath = folder;
                    _config.Save();
                }
            }

            Widgets.Checkbox(
                "Enable LayeredFS",
                _config.UseLayeredFs,
                v => _config.UseLayeredFs = v,
                () =>
                {
                    _config.Save();
                    _preserveStateOnLoad = true;
                    _needsLoad = true;
                }
            );

            bool dirOk = !string.IsNullOrEmpty(_layeredInput) && LayeredDirExists(_layeredInput);
            if (!string.IsNullOrEmpty(_layeredInput) && !dirOk)
                Widgets.ErrorText("folder not found");
            else if (_romfs != null && _romfs.UseLayered)
                Widgets.SuccessText("active");

            if (Widgets.Button("Reload", new Vector2(-1, 0)))
            {
                _preserveStateOnLoad = true;
                _needsLoad = true;
            }
            Widgets.ItemTooltip("Reloads the romfs and LayeredFS, keeping the player as it is.");
        }

        //The typed LayeredFS folder's existence, looked up at most once a second per path.
        string _layeredChecked;
        bool _layeredExists;
        long _layeredCheckedAt;
        readonly System.Diagnostics.Stopwatch _layeredClock =
            System.Diagnostics.Stopwatch.StartNew();

        bool LayeredDirExists(string path)
        {
            long now = _layeredClock.ElapsedMilliseconds;
            if (path != _layeredChecked || now - _layeredCheckedAt >= 1000)
            {
                _layeredExists = Directory.Exists(path);
                (_layeredChecked, _layeredCheckedAt) = (path, now);
            }
            return _layeredExists;
        }

        //Gap between the left panel and a gear grid opened beside it.
        const float GearGridGap = 6;

        //Row height of the player type list, tall enough for its icons.
        const float PlayerTypeRowHeight = 44;

        void GearRow(
            string label,
            GearSlot slot,
            GearEntry current,
            bool allowNone = true,
            string noneLabel = "Blank"
        )
        {
            //The grid opens beside the panel, so it needs the panel's right edge.
            float panelRight = ImGui.GetWindowPos().X + ImGui.GetWindowWidth();
            var icons = HasIcons(slot) ? Icons : null;
            Widgets.LabeledRow(
                label,
                () =>
                {
                    GearEntry selected;
                    bool changed =
                        _config.GearGrid && HasGrid(slot)
                            ? GearGrid.Draw(
                                label,
                                _db.GetList(slot),
                                current,
                                out selected,
                                allowNone,
                                noneLabel,
                                icons,
                                panelRight + GearGridGap
                            )
                            : Widgets.GearCombo(
                                label,
                                _db.GetList(slot),
                                current,
                                out selected,
                                allowNone,
                                noneLabel,
                                icons,
                                e => IconSource.KeyFor(e, _scene.IsFemale)
                            );
                    if (changed)
                    {
                        _scene.SetGear(slot, selected);
                        SavePlayerConfig();
                    }
                }
            );
        }

        const int EyeColorCount = 21;
        const int SkinToneCount = 9;

        void SetPlayerType(int type)
        {
            _scene.SetPlayerType(type);
            ApplyTeamColor();
            SavePlayerConfig();
        }

        void DrawPlayerTypeCombo()
        {
            int playerType = _scene.PlayerType;
            var icons = Icons;
            ImGui.SetNextItemWidth(-1);
            if (icons == null)
            {
                if (Widgets.ComboIndex("##playertype", ref playerType, PlayerTypes))
                    SetPlayerType(playerType);
                return;
            }

            var min = ImGui.GetCursorScreenPos();
            var size = new Vector2(ImGui.CalcItemWidth(), ImGui.GetFrameHeight());
            var dl = ImGui.GetWindowDrawList();
            bool open = Widgets.BeginCombo("##playertype", "", ImGuiComboFlags.HeightLarge);
            Widgets.IconPreview(
                dl,
                min,
                size,
                icons,
                IconSource.PlayerTypeKey(playerType),
                PlayerTypes[playerType],
                true
            );
            if (!open)
                return;
            Widgets.PopupRows(
                "playertype",
                PlayerTypes.Length,
                playerType,
                (row, isSelected) =>
                    Widgets.IconSelectable(
                        $"{PlayerTypes[row]}##pt{row}",
                        isSelected,
                        icons,
                        IconSource.PlayerTypeKey(row),
                        true,
                        PlayerTypeRowHeight
                    ),
                SetPlayerType
            );
            ImGui.EndCombo();
        }

        /// <summary>
        /// Eye colour and skin tone as two swatch dropdowns side by side, each showing its
        /// chosen swatch and opening onto a small grid of all of them.
        /// </summary>
        void DrawColorSwatches()
        {
            Widgets.LabeledRow(
                "Eyes",
                () =>
                {
                    float spacing = ImGui.GetStyle().ItemSpacing.X;
                    float width =
                        (
                            ImGui.GetContentRegionAvail().X
                            - ImGui.CalcTextSize("Skin").X
                            - spacing * 2
                        ) * 0.5f;
                    SwatchCombo(
                        "eye",
                        "Eye colour",
                        _scene.EyeColor,
                        EyeColorCount,
                        IconSource.EyeKey,
                        v => _scene.ApplyEyeColor(v),
                        width
                    );
                    ImGui.SameLine();
                    ImGui.AlignTextToFramePadding();
                    ImGui.TextColored(
                        SideOrderControls.On ? Theme.TextMain : Theme.TextDim,
                        "Skin"
                    );
                    ImGui.SameLine();
                    SwatchCombo(
                        "skin",
                        "Skin tone",
                        _scene.SkinTone,
                        SkinToneCount,
                        IconSource.SkinKey,
                        v => _scene.ApplySkinTone(v),
                        -1
                    );
                }
            );
        }

        void SwatchCombo(
            string id,
            string label,
            int value,
            int count,
            Func<int, string> key,
            Action<int> apply,
            float width
        )
        {
            var icons = Icons;
            ImGui.SetNextItemWidth(width);
            var min = ImGui.GetCursorScreenPos();
            var size = new Vector2(ImGui.CalcItemWidth(), ImGui.GetFrameHeight());
            var dl = ImGui.GetWindowDrawList();
            const float swatch = 40;
            int perRow = count <= 9 ? count : 7;
            var style = ImGui.GetStyle();
            ImGui.SetNextWindowSizeConstraints(
                new Vector2(
                    perRow * swatch
                        + (perRow - 1) * style.ItemSpacing.X
                        + style.WindowPadding.X * 2,
                    0
                ),
                new Vector2(float.MaxValue, float.MaxValue)
            );
            bool open = Widgets.BeginCombo("##" + id, "", ImGuiComboFlags.HeightLarge);
            Widgets.IconPreview(dl, min, size, icons, key(value), value.ToString(), false);
            if (!open)
                return;

            int picked = -1;
            for (int i = 0; i < count; i++)
            {
                if (i % perRow != 0)
                    ImGui.SameLine();
                var at = ImGui.GetCursorScreenPos();
                if (
                    ImGui.Selectable(
                        $"##{id}{i}",
                        i == value,
                        ImGuiSelectableFlags.None,
                        new Vector2(swatch, swatch)
                    )
                )
                    picked = i;
                if (ImGui.IsItemHovered())
                    Widgets.PlainTooltip($"{label} {i}");
                if (icons.TryGet(key(i), out var icon))
                {
                    var (a, b) = IconCache.Fit(
                        icon,
                        at + new Vector2(2, 2),
                        new Vector2(swatch - 4, swatch - 4)
                    );
                    ImGui.GetWindowDrawList().AddImage(icon.Id, a, b);
                }
            }

            //The arrows apply as they go.
            if (ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows))
            {
                int next = Widgets.GridNav(count, value, perRow, true);
                if (next >= 0)
                {
                    apply(next);
                    SavePlayerConfig();
                }
            }
            if (picked >= 0)
            {
                apply(picked);
                SavePlayerConfig();
            }
            ImGui.EndCombo();
        }

        void ApplyTeamColor()
        {
            var set = _useCustomTeamColor
                ? _customTeam
                : _db?.TeamColors.ElementAtOrDefault(_teamColorIndex);
            if (set != null && _scene != null)
                _scene.ApplyTeamColor(set, _teamIndex);
        }

        void ResetPlayerDefaults()
        {
            _scene.CurrentHair = null;
            _scene.CurrentEyebrow = null;
            _scene.CurrentHead = null;
            _scene.CurrentClothes = null;
            _scene.CurrentBottom = null;
            _scene.CurrentShoes = null;
            _scene.CurrentTank = null;
            _scene.CurrentWeapon = null;
            _scene.EyeColor = 0;
            _scene.SkinTone = 0;
            _scene.SetPlayerType(0);

            _teamColorIndex = 0;
            _teamIndex = 0;
            _useCustomTeamColor = true;
            ApplyTeamColor();

            _pipeline.FramePlayer();
            SavePlayerConfig();
        }

        void SavePlayerConfig()
        {
            if (_scene == null)
                return;
            var p = _config.Player;
            p.PlayerType = _scene.PlayerType;
            p.EyeColor = _scene.EyeColor;
            p.SkinTone = _scene.SkinTone;
            static void SaveGear(GearEntry e, out string rowId, out int variation)
            {
                rowId = e?.RowId;
                variation = e?.Variation ?? 0;
            }
            SaveGear(_scene.CurrentHair, out p.Hair, out p.HairVariation);
            SaveGear(_scene.CurrentEyebrow, out p.Eyebrow, out p.EyebrowVariation);
            SaveGear(_scene.CurrentHead, out p.Head, out p.HeadVariation);
            SaveGear(_scene.CurrentClothes, out p.Clothes, out p.ClothesVariation);
            SaveGear(_scene.CurrentBottom, out p.Bottom, out p.BottomVariation);
            SaveGear(_scene.CurrentShoes, out p.Shoes, out p.ShoesVariation);
            SaveGear(_scene.CurrentTank, out p.Tank, out p.TankVariation);
            SaveGear(_scene.CurrentWeapon, out p.Weapon, out p.WeaponVariation);
            p.TeamColorIndex = _teamColorIndex;
            p.TeamIndex = _teamIndex;
            p.UseCustomTeamColor = _useCustomTeamColor;
            p.CustomAlpha = new[] { _customTeam.Alpha.X, _customTeam.Alpha.Y, _customTeam.Alpha.Z };
            p.CustomBravo = new[] { _customTeam.Bravo.X, _customTeam.Bravo.Y, _customTeam.Bravo.Z };
            p.CustomCharlie = new[]
            {
                _customTeam.Charlie.X,
                _customTeam.Charlie.Y,
                _customTeam.Charlie.Z,
            };
            _config.Save();
        }

        void RestorePlayerConfig()
        {
            var p = _config.Player;
            if (p == null)
                return;

            GearEntry FindGear(List<GearEntry> list, string rowId, int variation)
            {
                if (rowId == null)
                    return null;
                return list.FirstOrDefault(x => x.RowId == rowId && x.Variation == variation)
                    ?? list.FirstOrDefault(x => x.RowId == rowId);
            }

            _scene.EyeColor = p.EyeColor;
            _scene.SkinTone = p.SkinTone;
            _scene.CurrentHair = FindGear(_db.Hair, p.Hair, p.HairVariation);
            _scene.CurrentEyebrow = FindGear(_db.Eyebrow, p.Eyebrow, p.EyebrowVariation);
            _scene.CurrentHead = FindGear(_db.Head, p.Head, p.HeadVariation);
            _scene.CurrentClothes = FindGear(_db.Clothes, p.Clothes, p.ClothesVariation);
            _scene.CurrentBottom = FindGear(_db.Bottom, p.Bottom, p.BottomVariation);
            _scene.CurrentShoes = FindGear(_db.Shoes, p.Shoes, p.ShoesVariation);
            _scene.CurrentTank = FindGear(_db.Tank, p.Tank, p.TankVariation);
            _scene.CurrentWeapon = FindGear(_db.MainWeapons, p.Weapon, p.WeaponVariation);
            _scene.SetPlayerType(p.PlayerType);

            _teamColorIndex = p.TeamColorIndex;
            _teamIndex = p.TeamIndex;
            _useCustomTeamColor = p.UseCustomTeamColor;
            if (p.CustomAlpha is { Length: 3 })
                _customTeam.Alpha = new System.Numerics.Vector3(
                    p.CustomAlpha[0],
                    p.CustomAlpha[1],
                    p.CustomAlpha[2]
                );
            if (p.CustomBravo is { Length: 3 })
                _customTeam.Bravo = new System.Numerics.Vector3(
                    p.CustomBravo[0],
                    p.CustomBravo[1],
                    p.CustomBravo[2]
                );
            if (p.CustomCharlie is { Length: 3 })
                _customTeam.Charlie = new System.Numerics.Vector3(
                    p.CustomCharlie[0],
                    p.CustomCharlie[1],
                    p.CustomCharlie[2]
                );
            _customTeam.Neutral = (_customTeam.Alpha + _customTeam.Bravo) * 0.5f;
            ApplyTeamColor();

            _scene.ApplyEyeColor(p.EyeColor);
            _scene.ApplySkinTone(p.SkinTone);

            //The background is part of the preset.
            p.Background ??= new Core.BackgroundConfig();
            p.Background.Normalize();
            _bgDirty = true;
        }
    }
}
