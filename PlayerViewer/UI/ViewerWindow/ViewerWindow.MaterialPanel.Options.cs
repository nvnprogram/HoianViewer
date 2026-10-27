using System;
using System.Collections.Generic;
using System.Linq;
using BfresEditor;
using BfresLibrary;
using Gsys;
using ImGuiNET;
using PlayerViewer.Materials;
using PlayerViewer.Shaders;
using Vector2 = System.Numerics.Vector2;

namespace PlayerViewer.UI
{
    // Options tab of the material editor.
    public partial class ViewerWindow
    {
        string _optionSearch = "";
        bool _optionsStoredOnly;

        //The options a splice ignores, set once per draw of the tab.
        ISet<string> _optionsNoEffect = new HashSet<string>();

        /// <summary>
        /// Every option the archive declares, not only the ones the material happens to store.
        /// </summary>
        void DrawShaderOptions(FMAT material, BfshaLibrary.ShaderModel shaderModel)
        {
            var options = material.Material.ShaderAssign.ShaderOptions;
            var baseline = Baseline(material);

            DrawResetAll(
                "shader option",
                baseline.ChangedOptions(material.Material).ToList(),
                name => baseline.ResetOption(material.Material, name),
                material
            );

            if (shaderModel == null)
            {
                ImGui.PushTextWrapPos();
                Widgets.DimText(
                    "No archive resolved, so only what the material stores can be listed."
                );
                ImGui.PopTextWrapPos();
                FilterRow("##optsearch", ref _optionSearch);
                Widgets.BeginScrollChild("##optlist", new Vector2(0, 0));
                foreach (var entry in options.ToArray())
                    if (Widgets.Matches(entry.Key, _optionSearch))
                        DrawFreeTextOption(material, entry.Key, entry.Value.String ?? "");
                ImGui.EndChild();
                return;
            }

            _optionsNoEffect = _uber?.NoEffect ?? new HashSet<string>();
            var declared = shaderModel.StaticOptions;
            int stored = 0;
            for (int i = 0; i < declared.Count; i++)
                if (options.ContainsKey(declared.GetKey(i)))
                    stored++;

            ImGui.PushTextWrapPos();
            Widgets.DimText($"{stored} of {declared.Count} declared options are stored here");
            ImGui.PopTextWrapPos();
            Widgets.CheckboxControl("Only the stored ones", ref _optionsStoredOnly);
            Widgets.ItemTooltip(
                "Off lists everything the bfsha declares. Setting an option the material "
                    + "does not carry adds it automatically."
            );
            FilterRow("##optsearch", ref _optionSearch);

            Widgets.BeginScrollChild("##optlist", new Vector2(0, 0));
            for (int i = 0; i < declared.Count; i++)
            {
                string name = declared.GetKey(i);
                bool have = options.ContainsKey(name);
                if (_optionsStoredOnly && !have)
                    continue;
                if (!Widgets.Matches(name, _optionSearch))
                    continue;
                DrawDeclaredOption(material, name, declared[i], have ? options[name].String : null);
            }

            //Anything the material carries that the archive does not declare. A choice the
            //archive does not know poisons the whole key, so it is worth showing loudly
            //rather than leaving out of a list that claims to be complete.
            foreach (var entry in options.ToArray())
            {
                if (FindOption(shaderModel, entry.Key) != null)
                    continue;
                if (!Widgets.Matches(entry.Key, _optionSearch))
                    continue;
                ImGui.PushTextWrapPos();
                Widgets.ErrorText($"{entry.Key} is not declared by {shaderModel.Name}");
                ImGui.PopTextWrapPos();
                DrawFreeTextOption(material, entry.Key, entry.Value.String ?? "");
            }
            ImGui.EndChild();
        }

        void DrawDeclaredOption(
            FMAT material,
            string name,
            BfshaLibrary.ShaderOption declared,
            string value
        )
        {
            //Absent and the stored "<Default Value>" sentinel mean the same thing to the
            //engine: the option keeps the archive's default choice.
            bool have = value != null;
            bool atDefault = !have || value == GsysShaderOptions.Unset;
            var baseline = Baseline(material);
            bool changed = baseline.OptionChanged(material.Material, name);

            DrawEntryName(name, changed, dim: atDefault);
            if (_optionsNoEffect.Contains(name))
                NoEffectWhenSpliced(ApproximatedWhenSpliced(material, name));

            var reset = ResetAction(
                material,
                changed,
                () => baseline.ResetOption(material.Material, name)
            );
            if (
                !ChoiceCombo(
                    $"##opt{name}",
                    atDefault ? null : value,
                    declared.choices,
                    $"<Default> ({declared.defaultChoice})",
                    out string picked,
                    reset
                )
            )
                return;
            //Picking the default on an option the material never stored is a no-op rather
            //than a reason to add an entry.
            if (picked == null && !have)
                return;
            SetShaderOption(material, name, picked ?? GsysShaderOptions.Unset);
        }

        static void NoEffectWhenSpliced(bool approximated = false)
        {
            ImGui.SameLine();
            Widgets.DimText(approximated ? "approximated when spliced" : "no effect when spliced");
        }

        //Whether this material's splice rebuilds the feature of an option or sampler.
        bool ApproximatedWhenSpliced(FMAT material, string name) =>
            _queue != null
            && _queue.TryGet(material, out var v)
            && v.Layer?.Drawn != null
            && v.Layer.Covered.Contains(name);

        void DrawFreeTextOption(FMAT material, string name, string value)
        {
            var baseline = Baseline(material);
            bool changed = baseline.OptionChanged(material.Material, name);
            DrawEntryName(name, changed, dim: !changed);
            var reset = ResetAction(
                material,
                changed,
                () => baseline.ResetOption(material.Material, name)
            );

            string free = value;
            ImGui.SetNextItemWidth(ControlWidth(reset));
            bool edited = Widgets.InputText($"##opt{name}", ref free, 128);
            DrawResetButton($"opt{name}", reset);
            if (edited)
                SetShaderOption(material, name, free);
        }

        static BfshaLibrary.ShaderOption FindOption(
            BfshaLibrary.ShaderModel shaderModel,
            string name
        )
        {
            if (shaderModel == null)
                return null;
            return shaderModel.StaticOptions[name] ?? shaderModel.DynamicOptions[name];
        }

        void SetShaderOption(FMAT material, string key, string value)
        {
            //A fresh ResString rather than mutating the stored one: the loader can hand the
            //same instance to more than one entry.
            material.Material.ShaderAssign.ShaderOptions.Set(key, new ResString { String = value });
            MaterialEdited(material);
        }
    }
}
