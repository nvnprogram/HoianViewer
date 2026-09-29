using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ImGuiNET;
using PlayerViewer.HairGen;
using PlayerViewer.Phive;
using PlayerViewer.Physics;
using Vector2 = System.Numerics.Vector2;

namespace PlayerViewer.UI
{
    // Copying the other gender's physics onto a gendered model (_F from _M and back). With the
    // viewer's metadata on the other model its authoring is carried over and built here; without,
    // the cloth of a pack the user confirms is copied as it is.
    public partial class ViewerWindow
    {
        const string CopyModalId = "Copy physics##copyphysics";
        bool _copyModal;
        string _copyPackPath = "";
        string _copyPartner;

        //The partner the Copy button last looked for, in which romfs, and whether it was there.
        (string Partner, Core.Romfs Romfs, bool Found) _copyProbe;

        /// <summary>The other gender's actor of a model named _F or _M, else null.</summary>
        string PartnerActor =>
            StandaloneActor is string a && (a.EndsWith("_F") || a.EndsWith("_M"))
                ? a[..^2] + (a.EndsWith("_F") ? "_M" : "_F")
                : null;

        bool PartnerFound(string partner)
        {
            if (_copyProbe.Partner != partner || _copyProbe.Romfs != _romfs)
                _copyProbe = (
                    partner,
                    _romfs,
                    _romfs.ModelExists(partner) || _romfs.FileExists($"Pack/Actor/{partner}.pack")
                );
            return _copyProbe.Found;
        }

        void DrawCopyFromPartnerButton()
        {
            string partner = PartnerActor;
            if (partner == null)
                return;
            bool found = PartnerFound(partner);
            Widgets.DisabledButton(
                $"Copy from {partner}",
                found && _paint == null && !_animExporting,
                new Vector2(-1, 0),
                () => Defer(Deferred.Copy, () => StartCopyFromPartner(partner), replace: true)
            );
            Widgets.ItemTooltip(
                found ? $"Replaces this model's physics with {partner}'s." : $"No {partner} found."
            );
        }

        /// <summary>The other model's authoring when it carries the metadata, else the modal for its pack.</summary>
        void StartCopyFromPartner(string partner)
        {
            byte[] model = null;
            PhysicsProvenance meta = null;
            try
            {
                model = _romfs.ReadModel(partner);
                var files = model != null ? Core.BfresBytes.Read(model).ExternalFiles : null;
                if (files != null && files.ContainsKey(PhysicsProvenance.FileName))
                    meta = PhysicsProvenance.Parse(files[PhysicsProvenance.FileName].Data);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Copy] reading {partner}: {ex.Message}");
            }
            if (meta != null)
            {
                CopyAuthoring(partner, model, meta);
                return;
            }
            _copyPartner = partner;
            string pack = _romfs.Resolve($"Pack/Actor/{partner}.pack");
            _copyPackPath = pack != null ? Path.GetFullPath(pack) : "";
            _copyModal = true;
        }

        void DrawCopyModal()
        {
            if (!_copyModal || _standalone == null)
            {
                _copyModal = false;
                return;
            }
            if (!ImGui.IsPopupOpen(CopyModalId))
                ImGui.OpenPopup(CopyModalId);
            if (!Widgets.BeginCenteredModal(CopyModalId, 560))
                return;
            ImGui.PushTextWrapPos();
            Widgets.Text($"Copy the cloth from {_copyPartner}'s pack:");
            ImGui.PopTextWrapPos();
            float browse = ImGui.CalcTextSize("Browse...").X + ImGui.GetStyle().FramePadding.X * 2;
            ImGui.SetNextItemWidth(-browse - ImGui.GetStyle().ItemSpacing.X);
            Widgets.PathInput("##copypack", ref _copyPackPath, 512);
            ImGui.SameLine();
            if (Widgets.Button("Browse...", new Vector2(browse, 0)))
            {
                string path = NativeFolderPicker.OpenFile(
                    "Actor pack",
                    "Actor pack (*.pack.zs)",
                    "*.pack.zs;*.pack"
                );
                if (!string.IsNullOrEmpty(path))
                    _copyPackPath = path;
            }
            bool exists = File.Exists(_copyPackPath);
            if (!exists)
                Widgets.ErrorText("No file there.");
            ImGui.Spacing();
            float half = (ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X) / 2;
            Widgets.DisabledButton(
                "Apply",
                exists,
                new Vector2(half, 0),
                () =>
                {
                    string path = _copyPackPath,
                        partner = _copyPartner;
                    Defer(Deferred.Copy, () => CopyCloth(path, partner), replace: true);
                    _copyModal = false;
                    ImGui.CloseCurrentPopup();
                }
            );
            ImGui.SameLine();
            if (Widgets.Button("Cancel", new Vector2(-1, 0)))
            {
                _copyModal = false;
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndPopup();
        }

        /// <summary>The cloth of a pack's ClothList, as the only cloth of this model under its own name.</summary>
        void CopyCloth(string packPath, string partner)
        {
            //The model's own cloth is looked for first, or finding it later would replace the copy.
            if (!_clothSearched)
                FindStandaloneCloth();
            _clothError = null;
            try
            {
                var pack = ClothPacks.ReadPackFile(packPath);
                var entries = Packs.ClothEntries(pack).Where(pack.Files.ContainsKey).ToList();
                if (entries.Count == 0)
                {
                    _clothError = $"{Path.GetFileName(packPath)} has no cloth.";
                    return;
                }
                string entry = entries[0];
                var bytes = pack.Get(entry);
                var file = ClothFile.Load(bytes);
                var have = StandaloneBoneNames();
                var missing = file
                    .Skeletons.SelectMany(s => s.BoneNames)
                    .Distinct()
                    .Where(n => !have.Contains(n))
                    .ToList();
                if (missing.Count > 0)
                {
                    _clothError =
                        $"Not copied: this model lacks {FirstFew(missing)}, which the cloth moves.";
                    return;
                }
                string own = Path.GetFileName(entry).Contains(pack.Actor)
                    ? entry.Replace(pack.Actor, StandaloneActor)
                    : $"Phive/Cloth/{StandaloneActor}.bphcl";
                var source = new ClothSource { Entry = own, ReplacesList = true };
                source.Actors.Add(StandaloneActor);
                AuthorStep(
                    $"copy the cloth of {pack.Actor}",
                    () =>
                    {
                        if (_gen != null && (_gen.Limbs.Count > 0 || _gen.UserColliders.Count > 0))
                            ClearAllLimbs();
                        OpenClothDocument(bytes, source);
                        _clothSaveActors = StandaloneActor;
                    }
                );
                _clothNote =
                    $"Copied {Path.GetFileName(entry)} from {pack.Actor} as {Path.GetFileName(own)}."
                    + (
                        entries.Count > 1
                            ? $" {entries.Count - 1} more cloth(s) in its list were not."
                            : ""
                    );
                Console.WriteLine($"[Copy] {packPath}: {_clothNote}");
            }
            catch (Exception ex)
            {
                _clothError = "Copying failed: " + ex.Message;
                Console.WriteLine($"[Copy] {ex}");
            }
        }

        /// <summary>
        /// The other model's authoring built on this one: its skeleton edits made here, its paint
        /// matched onto this mesh, its limbs, collidables and hand edits restored. The history
        /// starts here, as on loading the metadata.
        /// </summary>
        void CopyAuthoring(string partner, byte[] partnerModel, PhysicsProvenance meta)
        {
            var report = new List<string>();
            try
            {
                //This model as opened, without bones its own earlier authoring added.
                byte[] targetBase =
                    _gen != null ? _genOriginal
                    : _meta != null
                        ? PhysicsProvenance.BaseModel(
                            _standalone.SourceData,
                            _meta.GeneratedBones,
                            report
                        )
                    : _standalone.SourceData;
                var ported = PhysicsCopy.Port(
                    targetBase,
                    _romfs,
                    partner,
                    partnerModel,
                    meta,
                    report
                );

                AdoptGenerator(ported.Mesh, ported.Base, ported.Physics.Hair);
                report.AddRange(ported.Physics.Restore(_gen, out _));
                RebuildLimbs();
                _clothSaveActors = StandaloneActor;
                if (ported.Physics.Cloth is { Generated: true } cloth && _cloth != null)
                {
                    if (ProvenanceRestore.ApplyEdits(cloth, _cloth.File, report) > 0)
                        CommitCloth();
                    if (ProvenanceRestore.Sha(_cloth.Save()) == cloth.Hash)
                        report.Add($"the cloth is identical to {partner}'s");
                }
                _metaAnswer = _meta != null ? MetaAnswer.Discarded : _metaAnswer;
                ResetAuthorUndo();
                EnsureUndoBase();
                _authoringOpen = true;
                _selectPhysicsTab = true;
                _genNotice = AuthoringNotice($"Copied {partner}'s physics", "", report);
            }
            catch (Exception ex)
            {
                _genError = "Copying failed: " + ex.Message;
                Console.WriteLine($"[Copy] {ex}");
            }
            Console.WriteLine(
                $"[Copy] {partner} to {StandaloneActor}: {string.Join(" | ", report)}"
            );
        }
    }
}
