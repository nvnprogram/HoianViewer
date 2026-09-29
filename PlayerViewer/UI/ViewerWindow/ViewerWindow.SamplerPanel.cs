using System;
using System.Collections.Generic;
using System.Linq;
using BfresEditor;
using BfresLibrary;
using GLFrameworkEngine;
using ImGuiNET;
using PlayerViewer.Materials;
using PlayerViewer.Textures;
using TextureTarget = OpenTK.Graphics.OpenGL.TextureTarget;
using Vector2 = System.Numerics.Vector2;

namespace PlayerViewer.UI
{
    // Samplers tab of the material editor. The list is the archive's declared samplers, not
    // the material's, so a sampler the material has not bound is a row saying so rather than
    // something you have to know is missing.
    public partial class ViewerWindow
    {
        string _samplerSearch = "";
        bool _samplerShowUnread;

        //Kept beside the baselines: both belong to the loaded model and clear with it.
        readonly Dictionary<FMAT, SamplerBindings> _samplerBindings = new();

        SamplerBindings Bindings(FMAT material)
        {
            if (!_samplerBindings.TryGetValue(material, out var bindings))
                _samplerBindings[material] = bindings = new SamplerBindings(
                    material.Name,
                    material.Material,
                    Baseline(material),
                    name => Textures?.Has(name) ?? false,
                    () => MaterialEdited(material),
                    line => Console.WriteLine("[Material] " + line)
                );
            return bindings;
        }

        void DrawSamplers(FMAT material, BfshaLibrary.ShaderModel shaderModel)
        {
            var read = SamplersRead(material);

            if (shaderModel == null)
            {
                ImGui.PushTextWrapPos();
                Widgets.DimText(
                    "No archive resolved, so only what the material stores can be listed."
                );
                ImGui.PopTextWrapPos();
                DrawOrphanSamplers(material, Array.Empty<string>());
                return;
            }

            var declared = new List<string>();
            for (int i = 0; i < shaderModel.Samplers.Count; i++)
                declared.Add(shaderModel.Samplers.GetKey(i));

            var bindings = Bindings(material);
            bindings.RestoreRemembered(declared, read);

            DrawResetAll(
                "sampler",
                Baseline(material).ChangedSamplers(material.Material).ToList(),
                bindings.Reset,
                material
            );

            ImGui.PushTextWrapPos();
            Widgets.DimText(
                read == null
                    ? $"{declared.Count} samplers declared by {shaderModel.Name}. Which of them "
                        + "the program reads is not known until the material has a program."
                    : $"{read.Count} of {declared.Count} declared samplers are read by this "
                        + "material's programs. The rest carry no binding in them at all."
            );
            ImGui.PopTextWrapPos();
            Widgets.CheckboxControl(
                "Show the ones the shader does not read",
                ref _samplerShowUnread
            );
            FilterRow("##sampsearch", ref _samplerSearch);

            Widgets.BeginScrollChild("##samplist", new Vector2(0, 0));
            foreach (string sampler in declared)
            {
                bool isRead = read == null || read.Contains(sampler);
                if (!isRead && !_samplerShowUnread)
                    continue;
                if (!Widgets.Matches(sampler, _samplerSearch))
                    continue;
                DrawSamplerRow(material, sampler, isRead);
            }
            DrawOrphanSamplers(material, declared);
            ImGui.EndChild();
        }

        void DrawSamplerRow(FMAT material, string shaderSampler, bool isRead)
        {
            var mat = material.Material;
            bool engine = MaterialSamplers.EngineProvided(shaderSampler);
            string materialSampler = MaterialSamplers.Target(mat, shaderSampler);
            bool changed = !engine && Baseline(material).SamplerChanged(mat, shaderSampler);

            ImGui.Separator();
            Widgets.ColoredText(
                changed ? Theme.Cyan
                    : engine || !isRead ? Theme.TextDim
                    : Theme.TextMain,
                shaderSampler
            );
            Widgets.ItemTooltip(
                "A sampler the shader archive declares. The material reaches it through one of "
                    + "its own sampler slots, named beside it."
            );
            if (changed)
            {
                ImGui.SameLine();
                if (Widgets.SmallButton($"Reset##rs{shaderSampler}"))
                {
                    Bindings(material).Reset(shaderSampler);
                    MaterialEdited(material);
                }
                Widgets.ItemTooltip("Puts this sampler back to what the file held.");
            }

            if (engine)
            {
                //The engine feeds these: shadow maps, the depth and colour buffers, the BRDF
                //tables.
                ImGui.SameLine();
                Widgets.DimText(isRead ? "provided by engine" : "provided by engine, not read");
                ImGui.Indent(14);
                DrawEngineThumbnail(material, shaderSampler, isRead);
                ImGui.Unindent(14);
                return;
            }

            ImGui.SameLine();
            Widgets.DimText(
                (materialSampler == null ? "no slot" : "slot " + materialSampler)
                    + (isRead ? "" : ", not read by this variation")
            );
            Widgets.ItemTooltip(
                materialSampler == null
                    ? "This material assigns no sampler slot to it, so it binds nothing here. "
                        + "Picking a texture creates the assign."
                    : $"'{shaderSampler}' in the shader is assigned to the material's own "
                        + $"'{materialSampler}' slot, which is what carries the texture."
            );

            ImGui.Indent(14);
            int slot = materialSampler == null ? -1 : mat.Samplers.IndexOf(materialSampler);
            string current =
                slot >= 0 && slot < mat.TextureRefs.Count ? mat.TextureRefs[slot].Name : null;

            float padding = ImGui.GetStyle().FramePadding.X * 2;
            float spacing = ImGui.GetStyle().ItemSpacing.X;
            var importSize = new Vector2(ImGui.CalcTextSize("Import...").X + padding, 0);
            var replaceSize = new Vector2(ImGui.CalcTextSize("Replace").X + padding, 0);

            if (
                ChoiceCombo(
                    $"##samp{shaderSampler}",
                    string.IsNullOrEmpty(current) ? null : current,
                    TextureChoices(),
                    "<Default>",
                    out string picked,
                    width: -(importSize.X + replaceSize.X + spacing * 2)
                )
            )
                Bindings(material).SetTexture(shaderSampler, picked);

            ImGui.SameLine();
            float buttonColumn = ImGui.GetCursorPosX();
            Widgets.DisabledButton(
                $"Import...##impsamp{shaderSampler}",
                Textures?.Bntx != null,
                importSize,
                () => ImportOntoSampler(material, shaderSampler)
            );
            Widgets.ItemTooltip(
                "Adds an image to the model as a new texture and binds it here in one step, in "
                    + "the format the game normally uses for this sampler."
            );

            ImGui.SameLine();
            string refusal = TextureStore.ReplaceRefusal(Textures?.Find(current));
            Widgets.DisabledButton(
                $"Replace##repsamp{shaderSampler}",
                Textures?.Bntx != null && Textures.Has(current) && refusal == null,
                replaceSize,
                () => ReplaceOnSampler(current)
            );
            Widgets.ItemTooltip(
                refusal
                    ?? "Reads an image over the texture already bound here, keeping its name and "
                        + "the format it is stored in. Nothing is added, so every other material "
                        + "that binds the same texture follows this edit too."
            );

            //Under the other two and at their column, because the three are one group: what is
            //bound here goes out, comes back edited, or is swapped for something else.
            var bound = Textures?.Find(current);
            ImGui.SetCursorPosX(buttonColumn);
            Widgets.DisabledButton(
                $"Export...##expsamp{shaderSampler}",
                bound != null,
                new Vector2(importSize.X + spacing + replaceSize.X, 0),
                () => ExportTexture(bound, current)
            );
            Widgets.ItemTooltip(
                "Writes mip 0 of the bound texture out as a PNG. Reading that file back in "
                    + "with Replace lands on the same texture, in the same format."
            );

            if (TextureImport.IsGenerated(current) && !(Textures?.Has(current) ?? false))
                Widgets.DimText(
                    "no texture picked yet, so a "
                        + (current == TextureImport.WhiteName ? "white" : "black")
                        + " one is generated and bound at save time"
                );
            DrawThumbnail(current, 64);
            ImGui.Unindent(14);
            ImGui.Spacing();
        }

        //Material samplers with no shader sampler pointing at them. They are dead weight in
        //the file rather than an error, but a sampler list that did not show them would be
        //lying about what the material carries.
        void DrawOrphanSamplers(FMAT material, IReadOnlyCollection<string> declared)
        {
            var mat = material.Material;
            var assigns = mat.ShaderAssign?.SamplerAssigns;
            var orphans = new List<int>();
            for (int slot = 0; slot < mat.Samplers.Count; slot++)
            {
                string name = mat.Samplers.GetKey(slot);
                bool claimed =
                    assigns != null
                    && assigns
                        .ToArray()
                        .Any(x => x.Value?.String == name && declared.Contains(x.Key));
                if (!claimed)
                    orphans.Add(slot);
            }
            if (orphans.Count == 0)
                return;

            ImGui.Spacing();
            Widgets.SectionHeader("Not reached by any declared sampler");
            foreach (int slot in orphans)
            {
                string name = mat.Samplers.GetKey(slot);
                Widgets.DimText(name);
                ImGui.Indent(14);
                Widgets.DimText(
                    slot < mat.TextureRefs.Count
                        ? mat.TextureRefs[slot].Name ?? ""
                        : "no texture slot"
                );
                ImGui.Unindent(14);
            }
        }

        /// <summary>
        /// The engine feeds these itself, so there is only something to show when the renderer
        /// hands one over.
        /// </summary>
        void DrawEngineThumbnail(FMAT material, string shaderSampler, bool isRead)
        {
            if (!isRead)
                return;

            var texture = material
                .GetRenderer()
                ?.GetExternalTexture(GLContext.ActiveContext, shaderSampler);
            if (texture == null || texture.Target != TextureTarget.Texture2D)
            {
                Widgets.DimText("nothing to preview, the viewer binds a default here");
                return;
            }
            ImGui.Image((IntPtr)texture.ID, new Vector2(64, 64));
        }

        /// <summary>Imports an image and binds it here, so adding a map is one action rather
        /// than an import in one tab and a pick in another.</summary>
        void ImportOntoSampler(FMAT material, string shaderSampler)
        {
            string name = ImportOntoSampler(shaderSampler);
            if (name != null)
                Bindings(material).SetTexture(shaderSampler, name);
        }

        /// <summary>
        /// The samplers the material's own resolved programs read, or null when it has none,
        /// which is what an edit that has not been spliced yet looks like.
        /// </summary>
        static HashSet<string> SamplersRead(FMAT material)
        {
            var read = material.GetRenderer()?.SamplersRead();
            return read == null || read.Count == 0 ? null : read;
        }

        List<string> TextureChoices()
        {
            if (_standalone?.Render == null)
                return new List<string>();
            return _standalone
                .Render.Textures.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }
}
