using System;
using System.Collections.Generic;
using System.Linq;
using ImGuiNET;
using PlayerViewer.HairGen;
using PlayerViewer.Phive;
using Vector2 = System.Numerics.Vector2;
using Vector3 = OpenTK.Vector3;
using Vector4 = System.Numerics.Vector4;

namespace PlayerViewer.UI
{
    // The collidables of the generated cloth: their list in the authoring window, a small window
    // per collidable, the checklist in each limb's window, and the viewport's view of the one in
    // use. They live in the generator, so a rebuild keeps them. An edit applies when let go.
    public partial class ViewerWindow
    {
        readonly SideWindows<LimbCollider> _colWindows = new("col", 400);

        //The collidable picked or whose window was used last, and the one under the mouse; both stand out in the viewport.
        LimbCollider _colSelected,
            _colHovered;

        int ColId(LimbCollider collider) => _colWindows.Id(collider);

        void OpenColliderWindow(LimbCollider collider)
        {
            _colWindows.Show(collider);
            _colSelected = collider;
        }

        void CloseColliderWindows()
        {
            _colWindows.Clear();
            _colSelected = _colHovered = null;
        }

        /// <summary>The collidable the viewport shows highlighted: the hovered one, else the one in use.</summary>
        LimbCollider FocusedCollider =>
            _gen == null || !_physicsTabActive || _paint != null
                ? null
                : _colHovered ?? _colSelected;

        bool IsFocusedCollidable(string name) =>
            name != null && FocusedCollider is LimbCollider c && c.BuiltName == name;

        /// <summary>The generated cloth is the open document: collidables added through the authoring survive its rebuilds.</summary>
        bool GenOwnsCloth =>
            _gen?.Rig != null
            && _genClothVersion >= 0
            && _cloth != null
            && _cloth.Source.Entry == GeneratedEntry(_genActor, _gen.Hair);

        const string HairGenSuffix = "_HairGen",
            ClothGenSuffix = "_Cloth";

        /// <summary>The generated cloth's entry: _HairGen for hair, the name generated hair mods already carry; _Cloth otherwise.</summary>
        static string GeneratedEntry(string actor, bool hair) =>
            $"Phive/Cloth/{actor}{(hair ? HairGenSuffix : ClothGenSuffix)}.bphcl";

        void DrawColliderSection()
        {
            Widgets.SectionHeader("Collidables");
            ImGui.PushTextWrapPos();
            if (_gen?.Rig == null || _gen.Limbs.Count == 0)
            {
                Widgets.DimText(
                    "Shapes the cloth cannot enter, such as the head. Add a limb first."
                );
                ImGui.PopTextWrapPos();
                return;
            }
            Widgets.DimText(
                AuthorHair
                    ? "Hair gets the stock head and body ones by default: change or turn them off here, or add more."
                    : "Not hair: no collidables by default. Add them here or from a limb's window."
            );
            ImGui.PopTextWrapPos();
            var colliders = _gen.Colliders.ToList();
            if (colliders.Count > 0)
            {
                float row = ImGui.GetTextLineHeightWithSpacing();
                float height =
                    Math.Min(colliders.Count, 6) * Math.Max(row, ImGui.GetFrameHeight())
                    + 2 * ImGui.GetStyle().WindowPadding.Y
                    + 6;
                Widgets.BeginList("##colliders", new Vector2(0, height));
                foreach (var collider in colliders)
                    DrawColliderRow(collider);
                Widgets.EndList();
            }
            var style = ImGui.GetStyle();
            float half = (ImGui.GetContentRegionAvail().X - style.ItemSpacing.X) / 2;
            if (Widgets.Button("Add capsule", new Vector2(half, 0)))
                Defer(Deferred.Author, () => AddCollider(NewCollider(CollidableShapeKind.Capsule)));
            Widgets.ItemTooltip(
                "A capsule on the bone the selected limb hangs from, which its window then moves and sizes."
            );
            ImGui.SameLine();
            if (Widgets.Button("Add sphere", new Vector2(-1, 0)))
                Defer(Deferred.Author, () => AddCollider(NewCollider(CollidableShapeKind.Sphere)));
            Widgets.ItemTooltip(
                "A sphere on the selected limb's bone. The preview collides with it while Collide spheres is on."
            );
        }

        void DrawColliderRow(LimbCollider collider)
        {
            int id = ColId(collider);
            Widgets.RowFill(collider == _colSelected);
            bool on = collider.Enabled;
            if (Widgets.CheckboxControl($"##colon{id}", ref on))
                Defer(
                    Deferred.Author,
                    () =>
                    {
                        collider.Enabled = on;
                        CommitCollider(collider, $"turn {collider.Name} {(on ? "on" : "off")}");
                    }
                );
            Widgets.ItemTooltip("On or off; off leaves it out of the cloth.");
            ImGui.SameLine();
            string tag =
                collider.Problem != null ? "problem"
                : !collider.Enabled ? "off"
                : !collider.Default
                    ? (collider.Kind == CollidableShapeKind.Sphere ? "sphere" : "capsule")
                : collider.Edited ? "default, edited"
                : "hair default";
            bool dim = !collider.Enabled || collider.Problem != null;
            if (dim)
                ImGui.PushStyleColor(ImGuiCol.Text, Theme.TextDim);
            if (ImGui.Selectable($"{collider.Name}##colrow{id}", collider == _colSelected))
                OpenColliderWindow(collider);
            if (dim)
                ImGui.PopStyleColor();
            bool hovered = ImGui.IsItemHovered();
            //What it is, dim at the end of the row, when the name leaves room.
            var min = ImGui.GetItemRectMin();
            var size = ImGui.CalcTextSize(tag);
            var at = new Vector2(ImGui.GetItemRectMax().X - size.X - 4, min.Y);
            if (at.X > min.X + ImGui.CalcTextSize(collider.Name).X + 12)
                Widgets.DrawText(
                    ImGui.GetWindowDrawList(),
                    at,
                    ImGui.GetColorU32(Theme.TextDim),
                    tag
                );
            if (hovered)
            {
                _colHovered = collider;
                Widgets.PlainTooltip(ColliderSummary(collider) + "\nClick to move and size it.");
            }
        }

        string ColliderSummary(LimbCollider collider)
        {
            string who =
                collider.Limbs != null
                    ? collider.Limbs.Count(_gen.Limbs.Contains) switch
                    {
                        0 => "No limb collides with it.",
                        _ => "Collides with "
                            + string.Join(
                                ", ",
                                _gen.Limbs.Where(collider.Limbs.Contains).Select(l => l.Name)
                            )
                            + ".",
                    }
                : collider.Below is float below
                    ? $"Collides with the limbs reaching below {below:0.00}, as the stock hairs do."
                : "Every limb collides with it.";
            string shape =
                collider.Kind == CollidableShapeKind.Sphere
                    ? $"Sphere of radius {collider.Radius:0.000} on {collider.Bone}."
                    : $"Capsule of radius {collider.Radius:0.000}, {(collider.End - collider.Start).Length:0.000} long, on {collider.Bone}.";
            string state = collider.Problem ?? (collider.Enabled ? "" : " Off: not in the cloth.");
            return $"{shape} {who}{(state.Length > 0 ? " " + state : "")}";
        }

        /// <summary>A new collidable of the kind, on the given bone or the one the selected limb hangs from, centred on it.</summary>
        LimbCollider NewCollider(CollidableShapeKind kind, string bone = null)
        {
            var limb =
                _genSelected >= 0 && _genSelected < _gen.Limbs.Count
                    ? _gen.Limbs[_genSelected]
                    : null;
            bone ??= _gen.DefaultColliderBone(limb);
            return new LimbCollider
            {
                Name = _gen.UniqueColliderName("Collidable_" + bone),
                Kind = kind,
                Bone = bone,
                Start = kind == CollidableShapeKind.Capsule ? new Vector3(-0.05f, 0, 0) : default,
                End = kind == CollidableShapeKind.Capsule ? new Vector3(0.05f, 0, 0) : default,
                Radius = 0.08f,
            };
        }

        void AddCollider(LimbCollider collider, string label = null)
        {
            if (collider == null || _gen == null)
                return;
            AuthorStep(
                label ?? $"add {collider.Name}",
                () =>
                {
                    _gen.UserColliders.Add(collider);
                    RebuildGeneratedCloth(keepMotion: true);
                }
            );
            _authoringOpen = true;
            OpenColliderWindow(collider);
        }

        /// <summary>A capsule for one limb, from the limb's shape (see <see cref="HairGenerator.CapsuleFor"/>).</summary>
        void AddLimbCapsule(PaintedLimb limb) =>
            AddCollider(_gen.CapsuleFor(limb), $"add a capsule for {limb.Name}");

        void DeleteCollider(LimbCollider collider)
        {
            if (!_gen.UserColliders.Contains(collider))
                return;
            AuthorStep(
                $"delete {collider.Name}",
                () =>
                {
                    _gen.UserColliders.Remove(collider);
                    RebuildGeneratedCloth(keepMotion: true);
                }
            );
            if (_colSelected == collider)
                _colSelected = null;
        }

        /// <summary>Applies a collidable's edit to the running cloth; a default one keeps it as the user's edit.</summary>
        void CommitCollider(LimbCollider collider, string label = null) =>
            AuthorStep(
                label ?? $"edit {collider.Name}",
                () =>
                {
                    if (collider.Default)
                        _gen.EditDefault(collider);
                    RebuildGeneratedCloth(keepMotion: true);
                }
            );

        void ResetDefaultCollider(LimbCollider collider) =>
            AuthorStep(
                $"reset {collider.Name}",
                () =>
                {
                    _gen.ResetDefault(collider);
                    RebuildGeneratedCloth(keepMotion: true);
                }
            );

        /// <summary>The open collidable windows of collidables that exist; a window of one gone stays listed for an undo.</summary>
        void DrawColliderWindows(HashSet<object> shown)
        {
            var all = _gen.Colliders.ToHashSet();
            if (_colSelected != null && !all.Contains(_colSelected))
                _colSelected = null;
            foreach (var collider in _colWindows.Open.Where(all.Contains).ToList())
            {
                shown.Add(collider);
                DrawSideWindow(
                    _colWindows,
                    collider,
                    collider.Name,
                    () => _colHovered = collider,
                    () => _colSelected = collider,
                    () => DrawColliderWindowContents(collider)
                );
            }
        }

        void DrawColliderWindowContents(LimbCollider collider)
        {
            float label = Widgets.Column(96);
            bool done = false;

            if (collider.Default)
            {
                Widgets.ColoredText(Theme.GoldBright, collider.Name);
                ImGui.PushTextWrapPos();
                Widgets.DimText(
                    collider.Edited
                        ? "Hair default, edited: the cloth is rebuilt with these values."
                        : "Hair default, as every generated hair cloth gets it. Edits here are kept when the cloth is rebuilt."
                );
                ImGui.PopTextWrapPos();
            }
            else
                DrawColliderNameField(collider, label);

            bool on = collider.Enabled;
            if (Widgets.CheckboxControl("On", ref on))
            {
                collider.Enabled = on;
                done = true;
            }
            Widgets.ItemTooltip("Off leaves it out of the cloth; its settings are kept.");

            Widgets.LabelRow("Shape", label);
            int kind = collider.Kind == CollidableShapeKind.Sphere ? 1 : 0;
            if (Widgets.Combo("##colkind", ref kind, new[] { "Capsule", "Sphere" }, 2))
            {
                collider.Kind =
                    kind == 1 ? CollidableShapeKind.Sphere : CollidableShapeKind.Capsule;
                if (kind == 0 && (collider.End - collider.Start).LengthSquared < 1e-8f)
                    collider.End = collider.Start + new Vector3(0.1f, 0, 0);
                done = true;
            }

            Widgets.LabelRow(
                "Bone",
                label,
                "The bone it rides on. The offsets below are in this bone's space, so it moves with it."
            );
            var bones = _gen
                .Rig.Bones.Where((_, i) => !_gen.Rig.Removed.Contains(i))
                .Select(b => b.Name)
                .ToList();
            if (
                Widgets.StringCombo("##colbone", collider.Bone, bones, out string bone)
                && bone != null
                && bone != collider.Bone
            )
            {
                collider.Bone = bone;
                done = true;
            }

            if (collider.Kind == CollidableShapeKind.Sphere)
                done |= VectorRow(
                    "Centre",
                    "colc",
                    ref collider.Start,
                    0.002f,
                    "%.3f",
                    label,
                    VectorTip
                );
            else
            {
                done |= VectorRow(
                    "Start",
                    "cols",
                    ref collider.Start,
                    0.002f,
                    "%.3f",
                    label,
                    VectorTip
                );
                done |= VectorRow(
                    "End",
                    "cole",
                    ref collider.End,
                    0.002f,
                    "%.3f",
                    label,
                    VectorTip
                );
            }
            Widgets.LabelRow(
                "Radius",
                label,
                "A particle is pushed out to this plus its own radius."
            );
            Widgets.SliderValue(
                "##colradius",
                ref collider.Radius,
                0.005f,
                0.6f,
                "%.3f",
                out bool radiusDone
            );
            done |= radiusDone;

            ImGui.Spacing();
            Widgets.Text("Collides with");
            ImGui.PushTextWrapPos();
            Widgets.DimText(
                collider.Limbs != null ? "Only the limbs ticked."
                : collider.Below is float below
                    ? $"The limbs reaching below {below:0.00}, as the stock hairs do. Ticking a limb lists them instead."
                : "Every limb, including limbs added later. Ticking a limb lists them instead."
            );
            ImGui.PopTextWrapPos();
            for (int i = 0; i < _gen.Limbs.Count; i++)
            {
                var limb = _gen.Limbs[i];
                bool collides = _gen.Collides(collider, limb);
                if (Widgets.CheckboxControl($"{limb.Name}##collimb{LimbId(limb)}", ref collides))
                {
                    _gen.SetCollides(collider, limb, collides);
                    done = true;
                }
                if (GroupLeader(limb) is PaintedLimb leader)
                    Widgets.ItemTooltip(
                        $"Moves in {leader.Name}'s piece, which collides with it when either is ticked."
                    );
            }
            if (collider.Limbs != null)
            {
                string back = collider.Below != null ? "By height, as generated" : "Every limb";
                if (Widgets.SmallButton(back))
                {
                    collider.Limbs = null;
                    done = true;
                }
            }

            if (collider.Problem != null)
            {
                ImGui.PushTextWrapPos();
                Widgets.ErrorText(collider.Problem);
                ImGui.PopTextWrapPos();
            }

            ImGui.Spacing();
            if (collider.Default)
                Widgets.DisabledButton(
                    "Reset to generated",
                    collider.Edited,
                    new Vector2(-1, 0),
                    () =>
                        Defer(Deferred.Author, () => ResetDefaultCollider(collider), replace: true)
                );
            else if (Widgets.Button("Delete", new Vector2(-1, 0)))
                Defer(Deferred.Author, () => DeleteCollider(collider), replace: true);

            if (done)
                Defer(Deferred.Author, () => CommitCollider(collider));
        }

        /// <summary>A user collidable's name; it commits when the field is left, refused when another has it.</summary>
        void DrawColliderNameField(LimbCollider collider, float label)
        {
            Widgets.LabelRow("Name", label);
            string name = _colWindows.EditName(collider, collider.Name, "##colname");
            if (name == null)
                return;
            if (string.IsNullOrWhiteSpace(name) || _gen.UniqueColliderName(name, collider) != name)
            {
                _genError = $"Another collidable is already called {name}.";
                _colWindows.Names[collider] = collider.Name;
                return;
            }
            string old = collider.Name;
            collider.Name = name;
            Defer(Deferred.Author, () => CommitCollider(collider, $"rename {old} to {name}"));
        }

        const string VectorTip = "x, y, z in the bone's space. Drag a box, or click it to type.";

        /// <summary>The collidables in a limb's window: which it collides with, and one click for a capsule of its own.</summary>
        void DrawLimbColliders(PaintedLimb limb)
        {
            ImGui.Spacing();
            Widgets.Text("Collides with");
            var colliders = _gen.Colliders.ToList();
            if (colliders.Count == 0)
            {
                ImGui.PushTextWrapPos();
                Widgets.DimText(
                    "No collidables yet: nothing keeps this limb out of the body. Add a capsule for it below."
                );
                ImGui.PopTextWrapPos();
            }
            foreach (var collider in colliders)
            {
                int id = ColId(collider);
                bool collides = _gen.Collides(collider, limb);
                if (Widgets.CheckboxControl($"##lc{id}", ref collides))
                    Defer(
                        Deferred.Author,
                        () =>
                        {
                            _gen.SetCollides(collider, limb, collides);
                            CommitCollider(
                                collider,
                                $"{limb.Name} {(collides ? "collides with" : "passes through")} {collider.Name}"
                            );
                        }
                    );
                ImGui.SameLine();
                bool dim = !collider.Enabled || collider.Problem != null;
                if (dim)
                    ImGui.PushStyleColor(ImGuiCol.Text, Theme.TextDim);
                if (
                    ImGui.Selectable(
                        $"{collider.Name}{(collider.Default ? "   hair default" : "")}{(collider.Enabled ? "" : "   off")}##lcs{id}",
                        false
                    )
                )
                    OpenColliderWindow(collider);
                if (dim)
                    ImGui.PopStyleColor();
                if (ImGui.IsItemHovered())
                {
                    _colHovered = collider;
                    Widgets.PlainTooltip(ColliderSummary(collider) + "\nClick to open it.");
                }
            }
            bool can = LimbChain(limb) >= 0;
            Widgets.DisabledButton(
                "Add capsule for this limb",
                can,
                new Vector2(-1, 0),
                () => Defer(Deferred.Author, () => AddLimbCapsule(limb), replace: true)
            );
            Widgets.ItemTooltip(
                can
                    ? "A capsule beside this limb on the bone it hangs from; only this limb collides with it."
                    : "The limb has no bones yet."
            );
        }

        /// <summary>The focused collidable drawn at rest on its bone while the cloth does not have it: off, or its bone missing.</summary>
        void DrawFocusedColliderGhost(ImDrawListPtr dl, ViewMap map, OpenTK.Vector3 right)
        {
            if (FocusedCollider is not LimbCollider c || c.BuiltName != null || _gen.Rig == null)
                return;
            int bone = _gen.Rig.BoneIndex(c.Bone);
            if (bone < 0)
                return;
            DrawShape(
                dl,
                map,
                right,
                c.Kind,
                _gen.Rig.Bones[bone].World * _standalone.RootMotion,
                c.Start,
                c.End,
                c.Radius,
                ColGhost,
                null
            );
        }

        static readonly uint ColGhost = Color(1.0f, 0.83f, 0.36f, 0.4f);
    }
}
