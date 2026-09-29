using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GLFrameworkEngine;
using ImGuiNET;
using PlayerViewer.Textures;
using SixLabors.ImageSharp.PixelFormats;
using Toolbox.Core;
using BntxFile = Syroot.NintenTools.NSW.Bntx.BntxFile;
using TextureTarget = OpenTK.Graphics.OpenGL.TextureTarget;
using Vector2 = System.Numerics.Vector2;
using Vector4 = System.Numerics.Vector4;

namespace PlayerViewer.UI
{
    // Textures tab of the standalone panel: the model's texture container, with create,
    // replace, rename, delete and export. Nothing here writes to disk except an export;
    // the BNTX is re-serialised into the bfres when the model is saved.
    public partial class ViewerWindow
    {
        //-1 offers whatever the image and the sampler suggest; anything else is the index
        //into TextureFormats.All the user pinned.
        int _importFormatIndex = -1;
        int _reencodeFormatIndex = -1;
        string _reencodeSelectedFor;
        string _textureError;
        string _textureNote;

        const string ImageFilterLabel = "Images (*.png;*.jpg;*.bmp;*.tga)";
        const string ImageFilterPatterns = "*.png;*.jpg;*.jpeg;*.bmp;*.tga";

        //Rename draft and delete confirmation, both for the texture the detail window shows.
        string _renameDraft = "";
        string _pendingDelete;

        //New flat colour texture.
        System.Numerics.Vector4 _newColour = new(0, 0, 0, 1);
        int _newColourSize = 4;
        string _newColourName = "NewTexture";

        TextureStore _textures;

        TextureStore Textures =>
            _standalone?.Render == null
                ? null
                : _textures ??= new TextureStore(_standalone.Bfres, _standalone.Render);

        void DrawTexturesTab()
        {
            var store = Textures;
            if (store == null)
                return;

            bool haveContainer = store.Bntx != null;
            if (!haveContainer)
                Widgets.DimText("This model has no texture container, so nothing can be added.");

            Widgets.DisabledButton("Import texture...", haveContainer, ImportTexture);
            Widgets.ItemTooltip("Adds an image to the model's textures, with mips.");
            ImGui.SetNextItemWidth(-1);
            DrawFormatCombo("##importfmt", ref _importFormatIndex, true);
            Widgets.ItemTooltip("Automatic: BC4 for greyscale, BC3 with alpha, else BC1 sRGB.");

            if (haveContainer && ImGui.TreeNode("New flat colour"))
            {
                DrawNewColourTexture();
                ImGui.TreePop();
            }

            if (_textureError != null)
                Widgets.ErrorText(_textureError);
            if (_textureNote != null)
                Widgets.DimText(_textureNote);

            FilterRow("##texsearch", ref _textureSearch);
            Widgets.DimText($"{_standalone.Render.Textures.Count} texture(s)");

            Widgets.BeginList("##texlist", new Vector2(0, 0));
            var rows = new List<string>();
            foreach (
                var entry in _standalone.Render.Textures.OrderBy(
                    x => x.Key,
                    StringComparer.OrdinalIgnoreCase
                )
            )
            {
                if (!Widgets.Matches(entry.Key, _textureSearch))
                    continue;

                rows.Add(entry.Key);
                Widgets.RowFill(
                    _selectedTexture == entry.Key,
                    2 * ImGui.GetTextLineHeightWithSpacing()
                );
                if (ImGui.Selectable($"{entry.Key}##tex{entry.Key}", _selectedTexture == entry.Key))
                    SelectTexture(entry.Key);
                Widgets.KeepRowVisible(TextureListId, _selectedTexture == entry.Key);

                var texture = entry.Value;
                ImGui.Indent(14);
                Widgets.DimText(
                    $"{texture.Platform.OutputFormat}  {texture.Width}x{texture.Height}  "
                        + $"{texture.MipCount} mip(s)"
                );
                ImGui.Unindent(14);
            }

            int move = Widgets.ListNav(TextureListId, rows.Count, rows.IndexOf(_selectedTexture));
            if (move >= 0)
                SelectTexture(rows[move]);
            Widgets.EndList();
        }

        const string TextureListId = "texlist";

        void SelectTexture(string name)
        {
            _selectedTexture = name;
            _renameDraft = name ?? "";
            _pendingDelete = null;
        }

        void ClearTextureStatus()
        {
            _textureError = null;
            _textureNote = null;
        }

        void DrawNewColourTexture()
        {
            ImGui.SetNextItemWidth(-1);
            Widgets.InputText("##newcolname", ref _newColourName, 64);
            ImGui.SetNextItemWidth(-1);
            Widgets.ColorEdit4("##newcol", ref _newColour, ImGuiColorEditFlags.AlphaBar);
            ImGui.SetNextItemWidth(-1);
            if (Widgets.Framed(() => ImGui.InputInt("##newcolsize", ref _newColourSize)))
                _newColourSize = Math.Clamp(_newColourSize, 1, 512);
            Widgets.ItemTooltip("Side in pixels");

            Widgets.DisabledButton(
                "Create",
                !string.IsNullOrWhiteSpace(_newColourName),
                CreateColourTexture
            );
            Widgets.ItemTooltip("A stand-in for a map a material lacks.");
        }

        void CreateColourTexture()
        {
            ClearTextureStatus();
            var store = Textures;
            var bntx = store?.Bntx;
            if (bntx == null)
                return;

            try
            {
                string name = TextureImport.UniqueName(bntx, _newColourName.Trim());
                var colour = new Rgba32(_newColour.X, _newColour.Y, _newColour.Z, _newColour.W);
                var texture = TextureImport.Solid(
                    name,
                    _newColourSize,
                    colour,
                    bntx.Textures.FirstOrDefault()
                );
                store.Install(texture);
                SelectTexture(name);
                _textureNote = $"created {name}";
                Console.WriteLine($"[Texture] Created {name} {_newColourSize}x{_newColourSize}");
            }
            catch (Exception ex)
            {
                _textureError = ex.Message;
                Console.WriteLine($"[Texture] Create failed: {ex}");
            }
        }

        /// <summary>
        /// The selected texture, in its own window beside the left panel, where a readable
        /// preview does not push the list away.
        /// </summary>
        void DrawTextureWindow()
        {
            if (_standalone?.Render == null || _selectedTexture == null)
                return;
            if (!_standalone.Render.Textures.TryGetValue(_selectedTexture, out var tex))
            {
                _selectedTexture = null;
                return;
            }

            var viewport = ImGui.GetMainViewport();
            ImGui.SetNextWindowPos(
                new Vector2(viewport.Pos.X + LeftPanelWidth + 16, viewport.Pos.Y + 64),
                ImGuiCond.FirstUseEver
            );
            ImGui.SetNextWindowSize(new Vector2(420, 560), ImGuiCond.FirstUseEver);
            ImGui.SetNextWindowSizeConstraints(new Vector2(260, 180), new Vector2(2000, 2000));

            bool open = true;
            //Fixed id so position and size survive switching texture. NoFocusOnAppearing
            //leaves the arrow keys with the list this window was opened from.
            if (
                BeginCardWindow(
                    $"{_selectedTexture}###texviewer",
                    ref open,
                    ImGuiWindowFlags.NoFocusOnAppearing
                )
            )
            {
                ImGui.PushTextWrapPos();
                Widgets.ColoredText(Theme.GoldBright, _selectedTexture);
                Widgets.DimText(
                    $"{tex.Platform.OutputFormat}  {tex.Width}x{tex.Height}  "
                        + $"{tex.MipCount} mip(s)"
                );
                ImGui.PopTextWrapPos();

                DrawTextureActions(tex);
                ImGui.Spacing();
                DrawTexturePreview(tex);
            }
            ImGui.End();

            if (!open)
                _selectedTexture = null;
        }

        void DrawTextureActions(STGenericTexture tex)
        {
            var store = Textures;
            string name = _selectedTexture;

            if (Widgets.Button("Export as PNG..."))
                ExportTexture(tex, name);
            ImGui.SameLine();
            string refusal = TextureStore.ReplaceRefusal(tex);
            Widgets.DisabledButton(
                "Replace...",
                store?.Bntx != null && refusal == null,
                () => ReplaceTexture(name)
            );
            Widgets.ItemTooltip(refusal ?? "Replaces the image, keeping the name.");

            if (refusal == null)
                DrawReencode(tex, name);
            else
                Widgets.DimText(refusal);

            ImGui.SetNextItemWidth(TextureActionFieldWidth());
            Widgets.InputText("##rename", ref _renameDraft, 64);
            ImGui.SameLine();
            Widgets.DisabledButton(
                "Rename",
                store?.Bntx != null
                    && !string.IsNullOrWhiteSpace(_renameDraft)
                    && _renameDraft != name,
                () => RenameTexture(name, _renameDraft.Trim())
            );
            Widgets.ItemTooltip("Materials using it follow the new name.");

            var users = store?.UsedBy(name) ?? new System.Collections.Generic.List<string>();
            if (users.Count > 0)
                Widgets.DimText($"bound by {users.Count} material(s)");

            if (_pendingDelete == name)
            {
                ImGui.PushTextWrapPos();
                Widgets.ErrorText(
                    users.Count == 0
                        ? "Delete this texture?"
                        : $"Delete this texture? {users.Count} material(s) name it and will draw "
                            + "with a default until they are pointed somewhere else."
                );
                ImGui.PopTextWrapPos();
                float half = (ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X) / 2;
                Widgets.RedButton("Delete it", new Vector2(half, 0), () => DeleteTexture(name));
                ImGui.SameLine();
                if (Widgets.Button("Cancel", new Vector2(half, 0)))
                    _pendingDelete = null;
            }
            else
                Widgets.RedButton("Delete...", () => _pendingDelete = name);
        }

        /// <summary>
        /// Re-encodes the texture into another format, a decode and an encode, so block format
        /// to block format loses a little each time. The button lights once another is picked.
        /// </summary>
        void DrawReencode(STGenericTexture tex, string name)
        {
            var current = TextureFormats.Match(tex.Platform.OutputFormat);
            if (_reencodeSelectedFor != name)
            {
                _reencodeSelectedFor = name;
                _reencodeFormatIndex =
                    current == null ? -1 : Array.IndexOf(TextureFormats.All, current);
            }

            ImGui.SetNextItemWidth(TextureActionFieldWidth());
            DrawFormatCombo("##reencodefmt", ref _reencodeFormatIndex, false);
            ImGui.SameLine();
            bool changed =
                _reencodeFormatIndex >= 0
                && (current == null || TextureFormats.All[_reencodeFormatIndex] != current);
            Widgets.DisabledButton(
                "Re-encode",
                changed && Textures?.Bntx != null,
                () => Reencode(tex, name, TextureFormats.All[_reencodeFormatIndex])
            );
            Widgets.ItemTooltip(
                current == null
                    ? "Its format can't be written, so this changes it."
                    : "Rewrites the texture in the chosen format, keeping the name."
            );
        }

        //The rename and re-encode fields leave room for the wider of their two buttons.
        static float TextureActionFieldWidth() =>
            -(
                MathF.Max(Widgets.ButtonWidth("Rename"), Widgets.ButtonWidth("Re-encode"))
                + ImGui.GetStyle().ItemSpacing.X
            );

        void Reencode(STGenericTexture tex, string name, TextureFormat format)
        {
            ClearTextureStatus();
            var store = Textures;
            var bntx = store?.Bntx;
            if (bntx == null)
                return;
            if (TextureStore.ReplaceRefusal(tex) is string refusal)
            {
                _textureError = refusal;
                return;
            }

            try
            {
                using var image = TextureStore.Decode(tex);
                var built = TextureImport.FromImage(
                    image,
                    name,
                    format,
                    bntx.Textures.FirstOrDefault()
                );
                store.Install(built);
                _reencodeSelectedFor = null;
                _textureNote = $"re-encoded {name} as {format.Name}";
                Console.WriteLine(
                    $"[Texture] Re-encoded {name} {built.Width}x{built.Height} as {format.Name}"
                );
            }
            catch (Exception ex)
            {
                _textureError = ex.Message;
                Console.WriteLine($"[Texture] Re-encode of {name} failed: {ex}");
            }
        }

        /// <summary>The format list, with an automatic entry when the caller allows one.</summary>
        void DrawFormatCombo(string id, ref int index, bool allowAuto)
        {
            string label =
                index >= 0 && index < TextureFormats.All.Length
                    ? TextureFormats.All[index].Name
                    : "Automatic";

            if (!Widgets.BeginCombo(id, label))
                return;

            //Automatic is row 0 when it is offered, so the arrows count from -1 in that case.
            int first = allowAuto ? -1 : 0;
            if (allowAuto)
            {
                if (ImGui.Selectable("Automatic", index < 0))
                    index = -1;
                Widgets.KeepRowVisible(id, index < 0);
            }
            for (int i = 0; i < TextureFormats.All.Length; i++)
            {
                if (ImGui.Selectable(TextureFormats.All[i].Name, index == i))
                    index = i;
                Widgets.KeepRowVisible(id, index == i);
            }

            int move = Widgets.PopupListNav(id, TextureFormats.All.Length - first, index - first);
            if (move >= 0)
                index = move + first;
            ImGui.EndCombo();
        }

        void ExportTexture(STGenericTexture tex, string name)
        {
            ClearTextureStatus();
            string path = NativeFolderPicker.SaveFile(
                "Export Texture",
                name + ".png",
                "PNG (*.png)",
                "*.png"
            );
            if (string.IsNullOrEmpty(path))
                return;
            try
            {
                if (!path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                    path += ".png";
                TextureStore.ExportPng(tex, path);
                _textureNote = $"wrote {Path.GetFileName(path)}";
                Console.WriteLine($"[Texture] Exported {name} to {path}");
            }
            catch (Exception ex)
            {
                _textureError = ex.Message;
                Console.WriteLine($"[Texture] Export of {name} failed: {ex}");
            }
        }

        void ReplaceTexture(string name)
        {
            ClearTextureStatus();
            string path = NativeFolderPicker.OpenFile(
                "Replace Texture",
                ImageFilterLabel,
                ImageFilterPatterns
            );
            if (string.IsNullOrEmpty(path))
                return;
            InstallFromFile(path, name, PinnedImportFormat() ?? CurrentFormat(name), null);
        }

        /// <summary>
        /// Reads an image over the texture a sampler is bound to. Unlike an import nothing is
        /// added and no binding moves, so every material naming the texture follows.
        /// </summary>
        void ReplaceOnSampler(string name)
        {
            ClearTextureStatus();
            if (Textures?.Bntx == null || !Textures.Has(name))
                return;

            string path = NativeFolderPicker.OpenFile(
                "Replace Texture",
                ImageFilterLabel,
                ImageFilterPatterns
            );
            if (string.IsNullOrEmpty(path))
                return;
            InstallFromFile(path, name, CurrentFormat(name), null);
        }

        /// <summary>
        /// The format a texture is already stored in, or null for one the editor cannot write,
        /// which falls back to reading the image.
        /// </summary>
        TextureFormat CurrentFormat(string name)
        {
            var tex = Textures?.Find(name);
            return tex == null ? null : TextureFormats.Match(tex.Platform.OutputFormat);
        }

        void RenameTexture(string oldName, string newName)
        {
            ClearTextureStatus();
            try
            {
                int repointed = Textures.Rename(oldName, newName);
                SelectTexture(newName);
                _textureNote =
                    repointed == 0 ? "renamed" : $"renamed, {repointed} texture ref(s) repointed";
                Console.WriteLine(
                    $"[Texture] Renamed {oldName} to {newName}, {repointed} ref(s) repointed"
                );
                ReloadMaterialsAfterTextureChange();
            }
            catch (Exception ex)
            {
                _textureError = ex.Message;
                Console.WriteLine($"[Texture] Rename of {oldName} failed: {ex}");
            }
        }

        void DeleteTexture(string name)
        {
            ClearTextureStatus();
            _pendingDelete = null;
            try
            {
                Textures.Delete(name);
                _selectedTexture = null;
                _textureNote = $"deleted {name}";
                Console.WriteLine($"[Texture] Deleted {name}");
            }
            catch (Exception ex)
            {
                _textureError = ex.Message;
                Console.WriteLine($"[Texture] Delete of {name} failed: {ex}");
            }
        }

        void ImportTexture()
        {
            ClearTextureStatus();
            if (Textures?.Bntx == null)
                return;

            string path = NativeFolderPicker.OpenFile(
                "Import Texture",
                ImageFilterLabel,
                ImageFilterPatterns
            );
            if (string.IsNullOrEmpty(path))
                return;
            InstallFromFile(path, null, PinnedImportFormat(), null);
        }

        TextureFormat PinnedImportFormat() =>
            _importFormatIndex >= 0 && _importFormatIndex < TextureFormats.All.Length
                ? TextureFormats.All[_importFormatIndex]
                : null;

        //Builds and installs a texture from an image file. A null name imports under a fresh
        //name derived from the file; a given one replaces that texture in place. A null format
        //is decided by the image and, where there is one, the sampler it is being bound to.
        string InstallFromFile(
            string path,
            string replacing,
            TextureFormat format,
            string assignKey
        )
        {
            var store = Textures;
            var bntx = store?.Bntx;
            if (bntx == null)
                return null;
            if (
                replacing != null
                && TextureStore.ReplaceRefusal(store.Find(replacing)) is string refusal
            )
            {
                _textureError = refusal;
                return null;
            }

            try
            {
                string name =
                    replacing
                    ?? TextureImport.UniqueName(bntx, Path.GetFileNameWithoutExtension(path));
                var texture = InstallImage(path, name, format, assignKey);
                SelectTexture(name);
                _textureNote =
                    $"{(replacing == null ? "imported" : "replaced")} {name} "
                    + $"{texture.Width}x{texture.Height} as "
                    + TextureFormats.Match(texture.Format)?.Name;
                return name;
            }
            catch (Exception ex)
            {
                _textureError = ex.Message;
                Console.WriteLine($"[Texture] Import of {path} failed: {ex}");
                return null;
            }
        }

        /// <summary>
        /// Builds a texture from an image file and puts it in the container under the name. A
        /// null format is decided by the image and, where there is one, the sampler.
        /// </summary>
        Syroot.NintenTools.NSW.Bntx.Texture InstallImage(
            string path,
            string name,
            TextureFormat format,
            string assignKey
        )
        {
            var bntx = Textures.Bntx;
            var texture =
                format != null
                    ? TextureImport.FromFile(path, name, format, bntx.Textures.FirstOrDefault())
                    : TextureImport.FromFile(
                        path,
                        name,
                        assignKey,
                        bntx.Textures.FirstOrDefault(),
                        out _
                    );
            Textures.Install(texture);
            Console.WriteLine(
                $"[Texture] Installed {name} {texture.Width}x{texture.Height} {texture.Format} "
                    + $"{texture.MipCount} mip(s) from {path}"
            );
            return texture;
        }

        /// <summary>
        /// Adds a texture straight onto a sampler: one dialog, the format the shipped textures
        /// use for that sampler, and the binding made. Returns the name it installed under.
        /// </summary>
        string ImportOntoSampler(string assignKey)
        {
            ClearTextureStatus();
            if (Textures?.Bntx == null)
                return null;

            string path = NativeFolderPicker.OpenFile(
                "Import Texture",
                ImageFilterLabel,
                ImageFilterPatterns
            );
            if (string.IsNullOrEmpty(path))
                return null;
            return InstallFromFile(path, null, null, assignKey);
        }

        /// <summary>A texture at a fixed height, for the sampler rows. Null or unknown draws
        /// nothing, so a caller does not have to check first.</summary>
        void DrawThumbnail(string textureName, float height)
        {
            if (
                string.IsNullOrEmpty(textureName)
                || _standalone?.Render == null
                || !_standalone.Render.Textures.TryGetValue(textureName, out var tex)
            )
                return;

            DrawThumbnail(tex, height);
        }

        void DrawThumbnail(STGenericTexture tex, float height)
        {
            if (tex.RenderableTex == null)
                tex.LoadRenderableTexture();
            if (tex.RenderableTex is not GLTexture gl || gl.Target != TextureTarget.Texture2D)
            {
                Widgets.DimText($"{tex.Platform.OutputFormat}, no flat preview");
                return;
            }

            float width = height * tex.Width / Math.Max(1u, tex.Height);
            ImGui.Image((IntPtr)gl.ID, new Vector2(width, height));
            ImGui.SameLine();
            ImGui.BeginGroup();
            Widgets.DimText($"{tex.Width}x{tex.Height}");
            Widgets.DimText(tex.Platform.OutputFormat.ToString());
            ImGui.EndGroup();
        }

        //FMAT caches the texture NAMES its samplers resolve to, so a name that moved has to be
        //pushed back through the wrappers. Content written under the same name needs nothing.
        void ReloadMaterialsAfterTextureChange()
        {
            foreach (var model in StandaloneModels())
            foreach (var mesh in model.Meshes)
            {
                if (mesh.Shape.Material is not BfresEditor.FMAT material)
                    continue;
                try
                {
                    material.Reload(material.Material);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Material] Reload of {material.Name} failed: {ex.Message}");
                }
            }
        }

        //The controller passes ImGui texture handles straight through as GL names, so the
        //renderable texture the scene already uses can be drawn here as it is.
        void DrawTexturePreview(STGenericTexture tex)
        {
            if (tex.RenderableTex == null)
                tex.LoadRenderableTexture();
            if (tex.RenderableTex is not GLTexture gl || gl.Target != TextureTarget.Texture2D)
            {
                Widgets.DimText("no flat preview for this texture target");
                return;
            }

            var avail = ImGui.GetContentRegionAvail();
            float width = Math.Max(avail.X, 64);
            float height = width * tex.Height / Math.Max(1u, tex.Width);
            float room = Math.Max(avail.Y, 96);
            if (height > room)
            {
                width *= room / height;
                height = room;
            }
            ImGui.Image((IntPtr)gl.ID, new Vector2(width, height));
        }
    }
}
