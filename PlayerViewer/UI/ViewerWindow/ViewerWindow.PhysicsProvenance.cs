using System;
using System.Collections.Generic;
using System.Linq;
using BfresLibrary;
using ImGuiNET;
using PlayerViewer.HairGen;
using PlayerViewer.Phive;
using PlayerViewer.Rigging;
using Vector2 = System.Numerics.Vector2;

namespace PlayerViewer.UI
{
    // The physics metadata a save embeds in the model (HairGen/PhysicsProvenance.cs), and the
    // modal that offers to load it when a model carrying it is opened.
    public partial class ViewerWindow
    {
        enum MetaAnswer
        {
            None,
            Pending,
            Loaded,
            Discarded,
        }

        //What the opened model carried: the metadata, or why it could not be read.
        PhysicsProvenance _meta;
        string _metaError;
        MetaAnswer _metaAnswer;
        List<string> _metaReport = new();

        const string MetaModalId = "Physics metadata###physicsmeta";

        void ResetProvenance()
        {
            _meta = null;
            _metaError = null;
            _metaAnswer = MetaAnswer.None;
            _metaReport = new();
            CancelDeferred(Deferred.Meta);
            TestHookNote("meta modal", false);
        }

        /// <summary>Looks for the metadata in a model just opened; the modal asks what to do with it.</summary>
        void DetectProvenance()
        {
            ResetProvenance();
            var files = _standalone?.Bfres?.ResFile?.ExternalFiles;
            if (files == null || !files.ContainsKey(PhysicsProvenance.FileName))
                return;
            try
            {
                _meta = PhysicsProvenance.Parse(files[PhysicsProvenance.FileName].Data);
                Console.WriteLine($"[Meta] {StandaloneActor}: {_meta.Summary()}");
            }
            catch (Exception ex)
            {
                _metaError = ex.Message;
                Console.WriteLine($"[Meta] {StandaloneActor}: unreadable metadata: {ex.Message}");
            }
            _metaAnswer = MetaAnswer.Pending;
        }

        void DrawProvenanceModal()
        {
            TestHookNote("meta modal", false);
            if (_standalone == null || _metaAnswer != MetaAnswer.Pending)
                return;
            if (!ImGui.IsPopupOpen(MetaModalId))
                ImGui.OpenPopup(MetaModalId);
            if (!Widgets.BeginCenteredModal(MetaModalId, 480))
                return;
            TestHookNote("meta modal", true);
            ImGui.PushTextWrapPos();
            if (_meta != null)
            {
                Widgets.Text(
                    $"{StandaloneActor} carries physics metadata from a previous HoianViewer session."
                );
                ImGui.Spacing();
                Widgets.ColoredText(Theme.Gold, _meta.Summary() + ".");
                ImGui.Spacing();
                Widgets.DimText(
                    "Load restores the limbs, collidables and cloth edits. Discard drops the metadata on the next save."
                );
            }
            else
            {
                Widgets.Text(
                    $"{StandaloneActor} carries physics metadata this HoianViewer cannot read."
                );
                Widgets.ErrorText(_metaError ?? "");
                Widgets.DimText("Keep leaves it in the file; Discard drops it on the next save.");
            }
            ImGui.PopTextWrapPos();
            ImGui.Spacing();
            float half = (ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X) / 2;
            if (_meta != null)
            {
                if (Widgets.Button("Load", new Vector2(half, 0), accent: true))
                {
                    Defer(Deferred.Meta, LoadProvenance, replace: true);
                    ImGui.CloseCurrentPopup();
                }
            }
            else if (Widgets.Button("Keep", new Vector2(half, 0)))
            {
                _metaAnswer = MetaAnswer.None;
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if (Widgets.Button("Discard", new Vector2(-1, 0)))
            {
                Defer(Deferred.Meta, DiscardProvenance, replace: true);
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndPopup();
        }

        void DiscardProvenance()
        {
            if (_metaAnswer == MetaAnswer.None && _meta == null && _metaError == null)
                return;
            _metaAnswer = MetaAnswer.Discarded;
            Console.WriteLine($"[Meta] {StandaloneActor}: discarded, the next save drops it");
        }

        /// <summary>
        /// Rebuilds the authoring from the metadata on the opened model, on the saved model less
        /// its generated bones, and brings its cloth back. The shown model stays as saved, and the
        /// history starts here, as on opening a file.
        /// </summary>
        void LoadProvenance()
        {
            var meta = _meta;
            if (meta == null || _standalone == null || _paint != null)
                return;
            var report = new List<string>();
            var watch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var saved = _standalone.SourceData;
                var baseBytes = PhysicsProvenance.BaseModel(saved, meta.GeneratedBones, report);
                if (meta.Model != null && meta.Model != StandaloneActor)
                    report.Add($"saved as {meta.Model}, opened as {StandaloneActor}");
                var mesh = SkinnedMesh.FromModel(Core.BfresBytes.FirstModel(baseBytes));
                meta.ApplyRest(mesh);
                AdoptGenerator(mesh, baseBytes, meta.Hair);
                report.AddRange(meta.Restore(_gen, out bool rejoined));
                _gen.BuildFromLimbs();
                var rig = ProvenanceRestore.CompareModels(
                    GeneratedModelBytes(),
                    ProvenanceRestore.WithoutProvenance(saved)
                );
                int rigLine = report.Count;
                report.Add("rig against the saved model: " + rig);

                RebuildGeneratedCloth();
                bool identical = false;
                if (meta.Cloth is { } cloth && _cloth != null)
                    identical = RestoreCloth(cloth, rejoined, rig.BonesDiffer, report);
                _clothRuntime.Invalidate();
                ResetAuthorUndo();
                EnsureUndoBase();
                _metaAnswer = MetaAnswer.Loaded;
                _authoringOpen = true;
                _selectPhysicsTab = true;
                //The rig comparison and an exact cloth are for the log; anything else is news.
                int identicalLine = identical ? report.Count - 1 : -1;
                var news = report.Where((_, i) => i != rigLine && i != identicalLine);
                _genNotice = AuthoringNotice(
                    "Loaded the physics metadata",
                    identical ? ", the cloth as saved" : "",
                    news
                );
            }
            catch (Exception ex)
            {
                //Nothing half built stays; the metadata stays in the file as if unanswered.
                ResetHairGen();
                ResetAuthorUndo();
                _cloth = null;
                _clothSearched = false;
                _clothRuntime.Invalidate();
                _metaAnswer = MetaAnswer.None;
                report.Add("load failed: " + ex.Message);
                _genError = "Loading the physics metadata failed: " + ex.Message;
                Console.WriteLine($"[Meta] {ex}");
            }
            _metaReport = report;
            Console.WriteLine(
                $"[Meta] loaded in {watch.ElapsedMilliseconds} ms: {string.Join(" | ", report)}"
            );
        }

        /// <summary>
        /// The saved cloth brought back onto the cloth just generated, its lines added to the
        /// report. True when the cloth came out identical to the saved one.
        /// </summary>
        bool RestoreCloth(
            PhysicsProvenance.ClothRecord cloth,
            bool rebuilt,
            bool rigDiffers,
            List<string> report
        )
        {
            if (!string.IsNullOrEmpty(cloth.Packs))
                _clothSaveActors = cloth.Packs;
            var result = ProvenanceRestore.RestoreCloth(
                cloth,
                _cloth.File,
                _cloth.Source.Entry,
                rebuilt,
                rigDiffers,
                rigDiffers ? StandaloneBoneNames() : null,
                report
            );
            if (result.Commit)
                CommitCloth();
            if (result.Take != null)
            {
                _cloth.Restore(result.Take);
                MarkGeneratedCloth(edited: result.HandEdited);
            }
            return result.Identical;
        }

        /// <summary>The notice after the authoring was rebuilt elsewhere: what it holds now, then the lines worth reading.</summary>
        string AuthoringNotice(string what, string cloth, IEnumerable<string> news)
        {
            var lines = news.ToList();
            return $"{what}: {_gen.Limbs.Count} limb(s), "
                + $"{_gen.UserColliders.Count + _gen.ColliderEdits.Count} collidable(s) added or edited"
                + cloth
                + (lines.Count > 0 ? ". " + string.Join("; ", lines) : "")
                + ".";
        }

        /// <summary>The authoring as metadata, or null when there is none to write.</summary>
        PhysicsProvenance CaptureProvenance()
        {
            if (
                _gen == null
                || (
                    _gen.Limbs.Count == 0
                    && _gen.UserColliders.Count == 0
                    && _gen.ColliderEdits.Count == 0
                )
            )
                return null;
            var meta = PhysicsProvenance.Capture(_gen, StandaloneActor);
            if (_gen.Rig != null && _standalone?.SourceData != null)
            {
                var basis = PhysicsProvenance.BaseModel(
                    _standalone.SourceData,
                    meta.GeneratedBones,
                    null
                );
                meta.CaptureRest(
                    _gen.Mesh,
                    SkinnedMesh.FromModel(Core.BfresBytes.FirstModel(basis))
                );
            }
            for (int i = 0; i < meta.Limbs.Count; i++)
            {
                var hue = StrandHue(i);
                meta.Limbs[i].Colour =
                    $"#{(int)(hue.X * 255):x2}{(int)(hue.Y * 255):x2}{(int)(hue.Z * 255):x2}";
            }
            if (_cloth == null)
                return meta;
            ClothFile generated = null;
            if (GenOwnsCloth)
            {
                generated = EmptyGeneratedCloth();
                _gen.BuildCloth(generated);
            }
            meta.Cloth = ProvenanceRestore.Record(
                _cloth.Source.Entry,
                _clothSaveActors,
                _cloth.CommittedBytes,
                generated
            );
            return meta;
        }

        /// <summary>
        /// Puts the current metadata into a model about to be written, or takes the old one out
        /// when it was answered and nothing is authored now; left alone otherwise. Returns what
        /// puts the model back, or null, and a line for the report.
        /// </summary>
        (Action Undo, string Note) EmbedProvenance(ResFile res)
        {
            var files = res.ExternalFiles;
            string name = PhysicsProvenance.FileName;
            bool had = files.ContainsKey(name);
            var previous = had ? files[name] : null;
            Action undo = () =>
            {
                if (files.ContainsKey(name))
                    files.RemoveKey(name);
                if (previous != null)
                    files.Add(name, previous);
            };
            var meta = CaptureProvenance();
            if (meta != null)
            {
                var file = new ExternalFile { Name = name, Data = meta.ToBytes() };
                if (had)
                    files[name] = file;
                else
                    files.Add(name, file);
                return (undo, $"physics metadata written: {meta.Summary()}");
            }
            if (had && _metaAnswer is MetaAnswer.Loaded or MetaAnswer.Discarded)
            {
                files.RemoveKey(name);
                return (undo, "physics metadata dropped");
            }
            return (null, had ? "physics metadata kept as it was" : null);
        }

        /// <summary>The bytes of a model with the metadata put in or taken out as a save would, for the mod romfs write.</summary>
        byte[] WithProvenanceFor(byte[] model, List<string> notes)
        {
            var res = Core.BfresBytes.Read(model);
            Action undo;
            string note;
            try
            {
                (undo, note) = EmbedProvenance(res);
            }
            catch (Exception ex)
            {
                notes.Add("physics metadata not written: " + ex.Message);
                Console.WriteLine($"[Meta] {ex}");
                return model;
            }
            if (note != null)
                notes.Add(note);
            if (undo == null)
                return model;
            return Core.BfresBytes.ToBytes(res);
        }
    }
}
