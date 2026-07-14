using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ImGuiNET;
using PlayerViewer.Core;
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

        static readonly string[] UniformSetLabels = { "Viewer", "AutoWalk" };
        static readonly string[] UniformSetDirs = { "SPL3", "SPL3_AutoWalk" };

        void DrawPlayerPanel()
        {
            Widgets.SectionHeader("Player");

            if (ImGui.Button("Reset", new Vector2(-1, 0)))
                ResetPlayerDefaults();

            float half = (ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X) * 0.5f;
            if (ImGui.Button("Save preset", new Vector2(half, 0)))
                SavePreset();
            ImGui.SameLine();
            if (ImGui.Button("Load preset", new Vector2(half, 0)))
                LoadPreset();
            if (!string.IsNullOrEmpty(_presetStatus))
                Widgets.DimText(_presetStatus);

            int playerType = _scene.PlayerType;
            ImGui.SetNextItemWidth(-1);
            if (ImGui.Combo("##playertype", ref playerType, PlayerTypes, PlayerTypes.Length))
            {
                _scene.SetPlayerType(playerType);
                ApplyTeamColor();
                SavePlayerConfig();
            }

            GearRow("Hair", GearSlot.Hair, _scene.CurrentHair);
            GearRow("Eyebrow", GearSlot.Eyebrow, _scene.CurrentEyebrow);

            Widgets.LabeledRow(
                "Eyes",
                () =>
                {
                    ImGui.SetNextItemWidth(-1);
                    Widgets.SliderInt(
                        "##eye",
                        _scene.EyeColor,
                        0,
                        20,
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
                        8,
                        v => _scene.ApplySkinTone(v),
                        SavePlayerConfig
                    );
                }
            );

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
            if (ImGui.Button("Reset camera", new Vector2(-1, 0)))
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
            Widgets.ItemTooltip("Game-accurate self shadowing (gsys_shadow_prepass).");

            int setIdx = Math.Max(
                Array.IndexOf(UniformSetDirs, BfresEditor.HoianNXRender.UniformSetDir),
                0
            );
            Widgets.LabeledRow(
                "Env",
                () =>
                {
                    ImGui.SetNextItemWidth(-1);
                    if (
                        ImGui.Combo(
                            "##uniset",
                            ref setIdx,
                            UniformSetLabels,
                            UniformSetLabels.Length
                        )
                    )
                    {
                        BfresEditor.HoianNXRender.SetUniformSet(UniformSetDirs[setIdx]);
                        ApplyTeamColor();
                    }
                }
            );

            //Background (mode/color/image) lives on the preset; folded in here on the left.
            DrawBackgroundSection();
        }

        void DrawLightingSection()
        {
            Widgets.SectionHeader("Lighting");
            if (ImGui.Button("Reset lighting", new Vector2(-1, 0)))
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

        void DrawTeamColorSection()
        {
            Widgets.SectionHeader("Team Color");
            var colorSet = _db.TeamColors.ElementAtOrDefault(_teamColorIndex);
            ImGui.SetNextItemWidth(-1);
            string teamPreview = _useCustomTeamColor ? "Custom" : colorSet?.Name ?? "(default)";
            if (ImGui.BeginCombo("##teamcolor", teamPreview))
            {
                //"Custom" first: freely picked colors instead of an RSDB set.
                ImGui.ColorButton(
                    "##swatchCustA",
                    new Vector4(_customTeam.Alpha.X, _customTeam.Alpha.Y, _customTeam.Alpha.Z, 1),
                    ImGuiColorEditFlags.NoTooltip,
                    new Vector2(14, 14)
                );
                ImGui.SameLine();
                ImGui.ColorButton(
                    "##swatchCustB",
                    new Vector4(_customTeam.Bravo.X, _customTeam.Bravo.Y, _customTeam.Bravo.Z, 1),
                    ImGuiColorEditFlags.NoTooltip,
                    new Vector2(14, 14)
                );
                ImGui.SameLine();
                if (ImGui.Selectable("Custom", _useCustomTeamColor))
                {
                    _useCustomTeamColor = true;
                    ApplyTeamColor();
                    SavePlayerConfig();
                }

                for (int i = 0; i < _db.TeamColors.Count; i++)
                {
                    var set = _db.TeamColors[i];
                    //Swatch preview
                    ImGui.ColorButton(
                        $"##swatchA{i}",
                        new Vector4(set.Alpha.X, set.Alpha.Y, set.Alpha.Z, 1),
                        ImGuiColorEditFlags.NoTooltip,
                        new Vector2(14, 14)
                    );
                    ImGui.SameLine();
                    ImGui.ColorButton(
                        $"##swatchB{i}",
                        new Vector4(set.Bravo.X, set.Bravo.Y, set.Bravo.Z, 1),
                        ImGuiColorEditFlags.NoTooltip,
                        new Vector2(14, 14)
                    );
                    ImGui.SameLine();
                    if (ImGui.Selectable(set.Name, !_useCustomTeamColor && i == _teamColorIndex))
                    {
                        _useCustomTeamColor = false;
                        _teamColorIndex = i;
                        ApplyTeamColor();
                        SavePlayerConfig();
                    }
                }
                ImGui.EndCombo();
            }
            if (_useCustomTeamColor)
            {
                bool custChanged = false;
                var a = _customTeam.Alpha;
                var b = _customTeam.Bravo;
                var c = _customTeam.Charlie;
                custChanged |= ImGui.ColorEdit3("Alpha##cust", ref a, ImGuiColorEditFlags.NoInputs);
                ImGui.SameLine();
                custChanged |= ImGui.ColorEdit3("Bravo##cust", ref b, ImGuiColorEditFlags.NoInputs);
                ImGui.SameLine();
                custChanged |= ImGui.ColorEdit3(
                    "Charlie##cust",
                    ref c,
                    ImGuiColorEditFlags.NoInputs
                );
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
            if (ImGui.RadioButton("Alpha", _teamIndex == 0))
            {
                _teamIndex = 0;
                ApplyTeamColor();
                SavePlayerConfig();
            }
            ImGui.SameLine();
            if (ImGui.RadioButton("Bravo", _teamIndex == 1))
            {
                _teamIndex = 1;
                ApplyTeamColor();
                SavePlayerConfig();
            }
            ImGui.SameLine();
            if (ImGui.RadioButton("Charlie", _teamIndex == 2))
            {
                _teamIndex = 2;
                ApplyTeamColor();
                SavePlayerConfig();
            }
        }

        void DrawLayeredFsSection()
        {
            Widgets.SectionHeader("LayeredFS (mods)");

            ImGui.SetNextItemWidth(-70);
            if (ImGui.InputText("##layeredpath", ref _layeredInput, 512))
            {
                _config.LayeredFsPath = _layeredInput;
                _config.Save();
            }
            ImGui.SameLine();
            if (ImGui.Button("...##layeredbrowse", new Vector2(-1, 0)))
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

            bool dirOk = !string.IsNullOrEmpty(_layeredInput) && Directory.Exists(_layeredInput);
            if (!string.IsNullOrEmpty(_layeredInput) && !dirOk)
                Widgets.ErrorText("folder not found");
            else if (_romfs != null && _romfs.UseLayered)
                Widgets.SuccessText("active");

            if (ImGui.Button("Reload", new Vector2(-1, 0)))
            {
                _preserveStateOnLoad = true;
                _needsLoad = true;
            }
            Widgets.ItemTooltip(
                "Reload everything from the current romfs + LayeredFS.\nKeeps the current player configuration."
            );
        }

        void GearRow(
            string label,
            GearSlot slot,
            GearEntry current,
            bool allowNone = true,
            string noneLabel = "Blank"
        )
        {
            Widgets.LabeledRow(
                label,
                () =>
                {
                    if (
                        Widgets.GearCombo(
                            label,
                            _db.GetList(slot),
                            current,
                            out var selected,
                            allowNone,
                            noneLabel
                        )
                    )
                    {
                        _scene.SetGear(slot, selected);
                        SavePlayerConfig();
                    }
                }
            );
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

            //Background travels with the preset; clamp loaded values and rebuild the live preview.
            p.Background ??= new Core.BackgroundConfig();
            p.Background.Normalize();
            _bgDirty = true;
        }
    }
}
