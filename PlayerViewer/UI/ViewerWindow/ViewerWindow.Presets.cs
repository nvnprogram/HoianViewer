using System;
using System.IO;
using Newtonsoft.Json;
using PlayerViewer.Core;

namespace PlayerViewer.UI
{
    // Save/load the current player loadout (gear, colors, eye/skin) as a standalone preset
    // file: a serialized PlayerConfig, reusing the Save/RestorePlayerConfig apply path.
    public partial class ViewerWindow
    {
        string _presetStatus; //transient message shown under the preset buttons

        void SavePreset()
        {
            if (_scene == null)
                return;
            string def = ExportUtil.Timestamped("player-preset", ".json");
            string path = NativeFolderPicker.SaveFile(
                "Save Preset",
                def,
                "Player preset (*.json)",
                "*.json"
            );
            if (string.IsNullOrEmpty(path))
                return;
            if (!path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                path += ".json";

            //Mirror the live scene into the game's player config, then serialize that snapshot.
            SavePlayerConfig();
            try
            {
                File.WriteAllText(
                    path,
                    JsonConvert.SerializeObject(_config.Active.Player, Formatting.Indented)
                );
                _presetStatus = $"Saved {Path.GetFileName(path)}";
                Console.WriteLine($"[UI] Saved preset {path}");
            }
            catch (Exception ex)
            {
                _presetStatus = $"Save failed: {ex.Message}";
                Console.WriteLine($"[UI] Preset save failed: {ex.Message}");
            }
        }

        void LoadPreset()
        {
            if (_scene == null)
                return;
            string path = NativeFolderPicker.OpenFile(
                "Load Preset",
                "Player preset (*.json)",
                "*.json"
            );
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return;

            PlayerConfig preset;
            try
            {
                preset = JsonConvert.DeserializeObject<PlayerConfig>(File.ReadAllText(path));
            }
            catch (Exception ex)
            {
                _presetStatus = $"Not a valid preset file: {ex.Message}";
                Console.WriteLine($"[UI] Preset parse failed: {ex.Message}");
                return;
            }
            if (preset == null)
            {
                _presetStatus = "Not a valid preset file";
                return;
            }

            try
            {
                //Adopt the preset as the current config and apply it. Gear rows missing from
                //the loaded game resolve to blank slots inside RestorePlayerConfig.
                preset.Normalize();
                _config.Active.Player = preset;
                RestorePlayerConfig();
                SavePlayerConfig();
                _presetStatus = $"Loaded {Path.GetFileName(path)}";
                Console.WriteLine($"[UI] Loaded preset {path}");
            }
            catch (Exception ex)
            {
                _presetStatus = $"Load failed: {ex.Message}";
                Console.WriteLine($"[UI] Preset load failed: {ex.Message}");
            }
        }
    }
}
