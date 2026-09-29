using System;
using ImGuiNET;
using Vector2 = System.Numerics.Vector2;

namespace PlayerViewer.UI
{
    // The physics a model came with, which the Physics Maker offers to clear before it builds
    // from scratch: its cloth, pieces and collidables. The skeleton is left alone, so limbs can
    // still be made from its bones.
    public partial class ViewerWindow
    {
        const string ClearModalId = "Clear out the original physics?###clearphysics";

        //Asked once per model; Cancel asks again next time.
        bool _clearAsked;
        bool _clearModal;

        /// <summary>The open cloth when it is the model's own rather than one the Physics Maker made.</summary>
        bool HasOriginalPhysics =>
            _standalone != null
            && _paint == null
            && _cloth?.File.Container != null
            && _genClothVersion < 0
            && !_cloth.Source.IsNew
            && !_cloth.Source.Entry.EndsWith(HairGenSuffix + ".bphcl", StringComparison.Ordinal)
            && _cloth.Source.Entry != GeneratedEntry(StandaloneActor, hair: false)
            && (
                _cloth.File.Container.ClothDatas.Count > 0
                || _cloth.File.Container.Collidables.Count > 0
            );

        /// <summary>Opens the Physics Maker, first asking whether to clear the model's own physics.</summary>
        void OpenPhysicsMaker()
        {
            if (!_clearAsked && HasOriginalPhysics)
            {
                _clearModal = true;
                return;
            }
            _authoringOpen = true;
            _focusAuthoring = true;
        }

        void DrawClearPhysicsModal()
        {
            if (_clearModal)
            {
                _clearModal = false;
                if (HasOriginalPhysics)
                    ImGui.OpenPopup(ClearModalId);
            }
            if (!ImGui.IsPopupOpen(ClearModalId) || !Widgets.BeginCenteredModal(ClearModalId, 500))
                return;
            var container = _cloth?.File.Container;
            ImGui.PushTextWrapPos();
            if (container != null)
            {
                Widgets.Text(
                    $"{StandaloneActor} comes with its own cloth, {_cloth.Source.FileName}: "
                        + $"{container.ClothDatas.Count} piece(s) and {container.Collidables.Count} collidable(s)."
                );
                ImGui.Spacing();
                Widgets.DimText(
                    "The Physics Maker builds physics from the limbs and collidables you add, replacing this cloth when saved."
                );
                ImGui.Spacing();
                Widgets.ColoredText(
                    Theme.Gold,
                    "Clear leaves an empty cloth; the model and its bones stay. Ctrl+Z undoes."
                );
                Widgets.DimText("Keep leaves the cloth running until the first limb is built.");
            }
            ImGui.PopTextWrapPos();
            ImGui.Spacing();
            float w = (ImGui.GetContentRegionAvail().X - 2 * ImGui.GetStyle().ItemSpacing.X) / 3;
            if (Widgets.Button("Clear", new Vector2(w, 0), accent: true))
            {
                _clearAsked = true;
                Defer(
                    Deferred.Clear,
                    () =>
                    {
                        ClearOriginalPhysics();
                        _authoringOpen = true;
                        _focusAuthoring = true;
                    },
                    replace: true
                );
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if (Widgets.Button("Keep", new Vector2(w, 0)))
            {
                _clearAsked = true;
                Defer(
                    Deferred.Clear,
                    () =>
                    {
                        _authoringOpen = true;
                        _focusAuthoring = true;
                    },
                    replace: true
                );
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if (Widgets.Button("Cancel", new Vector2(-1, 0)))
                ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
        }

        /// <summary>
        /// Replaces the model's own cloth with the generator's, empty until limbs are added,
        /// leaving the model untouched.
        /// </summary>
        void ClearOriginalPhysics()
        {
            if (!HasOriginalPhysics)
                return;
            AuthorStep(
                "clear the original physics",
                () =>
                {
                    string clothName = _cloth.Source.FileName;
                    if (!EnsureGenerator())
                        return;
                    RebuildLimbs();
                    _genNotice =
                        $"Cleared the original physics: {clothName} replaced by an empty cloth; the model and its bones are unchanged.";
                    Console.WriteLine($"[HairGen] {_genNotice}");
                }
            );
        }
    }
}
