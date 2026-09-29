using System;
using System.Collections.Generic;
using System.Linq;
using ImGuiNET;
using OpenTK;
using PlayerViewer.HairGen;
using PlayerViewer.Phive;
using PlayerViewer.Physics;
using PlayerViewer.Rigging;
using Vector2 = System.Numerics.Vector2;
using Vector4 = OpenTK.Vector4;

namespace PlayerViewer.UI
{
    // Adding a limb from the model's own bones: a session like painting's, in which the user picks
    // chains from the bone list or on the joints in the viewport. A chain runs from the bone picked
    // down its first children; a click on a bone inside a chain ends it there.
    public partial class ViewerWindow
    {
        string _bonePickFilter = "";
        int _bonePickHover = -1;
        string _bonePickNote;
        readonly ViewportClick _bonePickClick = new();

        /// <summary>The model's bones as the generator read them, the ones a bone limb names.</summary>
        IReadOnlyList<AuthorBone> PickBones => _gen.Mesh.Bones;

        void StartBoneLimb()
        {
            if (!EnsureGenerator())
                return;
            BeginPaint(
                new LimbPaintSession
                {
                    Limb = new PaintedLimb
                    {
                        Name = NewLimbName(),
                        BoneChains = new(),
                        HoldOnHead = false,
                    },
                    IsNew = true,
                    Bones = true,
                    Chains = new(),
                }
            );
        }

        string NewLimbName()
        {
            int n = _gen.Limbs.Count + 1;
            while (_gen.Limbs.Exists(l => BoneSafeName(l.Name) == BoneSafeName($"Limb {n}")))
                n++;
            return $"Limb {n}";
        }

        /// <summary>Picks a limb's bones again; its chains stay as they were until Save.</summary>
        void StartEditBoneLimb(PaintedLimb limb)
        {
            var chains = new List<List<int>>();
            foreach (var names in limb.BoneChains)
            {
                var bones = names.Select(n => BoneTree.IndexOf(PickBones, n)).ToList();
                if (bones.Count > 0 && bones.All(b => b >= 0))
                    chains.Add(bones);
            }
            BoneTree.JoinChains(PickBones, chains);
            BeginPaint(
                new LimbPaintSession
                {
                    Limb = limb,
                    Bones = true,
                    Chains = chains,
                    Style = limb.Style,
                }
            );
        }

        /// <summary>The chain a bone starts: down first children to the end.</summary>
        int[] NaturalChain(int root) => BoneTree.ChainFrom(PickBones, root);

        /// <summary>
        /// A bone picked in the list or the viewport: the root of a chain removes it, a bone inside
        /// a chain or past its end along its first children ends it there, any other bone starts a
        /// chain, joined onto the chain it runs into.
        /// </summary>
        void PickBone(int bone)
        {
            PushPaintUndo();
            PickBoneCore(bone);
            DropPaintUndoIfSame();
        }

        void PickBoneCore(int bone) =>
            _bonePickNote = BoneChainPick.Pick(
                PickBones,
                _paint.Chains,
                bone,
                b => ReplacedBone(b)?.Name,
                OtherLimbBones().ToDictionary(e => e.Key, e => e.Value.Name)
            );

        /// <summary>Bones other limbs' chains drive, by the limb.</summary>
        Dictionary<int, PaintedLimb> OtherLimbBones()
        {
            var taken = new Dictionary<int, PaintedLimb>();
            foreach (var limb in _gen.Limbs.Where(l => l.IsBoneLimb && l != _paint?.Limb))
            foreach (var names in limb.BoneChains)
            foreach (var name in names)
                if (BoneTree.IndexOf(PickBones, name) is int b and >= 0)
                    taken[b] = limb;
            return taken;
        }

        /// <summary>The painted limb that replaced a bone of the model, or null.</summary>
        PaintedLimb ReplacedBone(int bone)
        {
            if (_gen.Rig == null || !_gen.Rig.Removed.Contains(bone))
                return null;
            string name = PickBones[bone].Name;
            return _gen.Limbs.FirstOrDefault(l => l.Replaced.Contains(name));
        }

        /// <summary>The picking controls in the authoring window: the bone list, the chains and Done and Cancel.</summary>
        void DrawBonePickControls()
        {
            var limb = _paint.Limb;
            var chains = _paint.Chains;
            var bones = PickBones;
            Widgets.ColoredText(
                Theme.GoldBright,
                (_paint.IsNew ? "Picking bones for " : "Repicking bones for ") + limb.Name
            );
            ImGui.PushTextWrapPos();
            Widgets.DimText(
                "Click a bone to start a chain, one in it to end it there, its first to drop it."
            );
            ImGui.PopTextWrapPos();

            Widgets.SearchBox("##bonepickfilter", ref _bonePickFilter, "filter bones", -1);
            var member = new Dictionary<int, (int Chain, int At)>();
            for (int c = 0; c < chains.Count; c++)
            for (int k = 0; k < chains[c].Count; k++)
                member[chains[c][k]] = (c, k);
            var taken = OtherLimbBones();
            float rowHeight = ImGui.GetTextLineHeightWithSpacing();
            Widgets.BeginList(
                "##bonepick",
                new Vector2(0, Math.Min(bones.Count, 12) * rowHeight + 12)
            );
            int hovered = -1;
            for (int i = 0; i < bones.Count; i++)
            {
                if (!Widgets.Matches(bones[i].Name, _bonePickFilter))
                    continue;
                int depth = 0;
                for (int p = bones[i].Parent; p >= 0 && depth < 16; p = bones[p].Parent)
                    depth++;
                ImGui.SetCursorPosX(ImGui.GetCursorPosX() + depth * 10);
                string role = "";
                bool locked = false;
                if (member.TryGetValue(i, out var m))
                    role =
                        m.At == 0 ? $"   chain {m.Chain + 1}, first"
                        : m.At == chains[m.Chain].Count - 1 ? $"   chain {m.Chain + 1}, last"
                        : $"   chain {m.Chain + 1}";
                else if (taken.TryGetValue(i, out var owner))
                    (role, locked) = ("   in " + owner.Name, true);
                else if (ReplacedBone(i) is PaintedLimb by)
                    (role, locked) = ("   replaced by " + by.Name, true);
                Widgets.BeginDisabled(locked);
                int row = i;
                if (Widgets.ListRow($"{bones[i].Name}{role}##pb{i}", member.ContainsKey(i)))
                    Defer(Deferred.Author, () => PickBone(row));
                Widgets.EndDisabled();
                if (Widgets.ItemHovered())
                    hovered = i;
            }
            Widgets.EndList();
            if (hovered >= 0)
                _bonePickHover = hovered;

            if (chains.Count == 0)
                Widgets.DimText("No chain picked yet.");
            float label = Widgets.Column(96);
            for (int c = 0; c < chains.Count; c++)
            {
                var chain = chains[c];
                var natural = NaturalChain(chain[0]);
                ImGui.PushTextWrapPos();
                Widgets.Text(
                    $"Chain {c + 1}: " + string.Join(" > ", chain.Select(b => bones[b].Name))
                );
                ImGui.PopTextWrapPos();
                ImGui.AlignTextToFramePadding();
                Widgets.Text("Ends at");
                ImGui.SameLine(label);
                float remove = Widgets.ButtonWidth("Remove");
                ImGui.SetNextItemWidth(
                    Math.Max(
                        60,
                        ImGui.GetContentRegionAvail().X - remove - ImGui.GetStyle().ItemSpacing.X
                    )
                );
                if (Widgets.BeginCombo($"##tip{c}", bones[chain[^1]].Name))
                {
                    foreach (int b in natural)
                        if (ImGui.Selectable($"{bones[b].Name}##tip{c}_{b}", b == chain[^1]))
                        {
                            PushPaintUndo();
                            int at = Array.IndexOf(natural, b);
                            chain.Clear();
                            chain.AddRange(natural.Take(at + 1));
                        }
                    ImGui.EndCombo();
                }
                ImGui.SameLine();
                int index = c;
                if (Widgets.Button($"Remove##chain{c}", new Vector2(-1, 0)))
                    Defer(
                        Deferred.Author,
                        () =>
                        {
                            PushPaintUndo();
                            chains.RemoveAt(index);
                        }
                    );
            }

            ImGui.PushTextWrapPos();
            Widgets.DimText("The model's bones stay as they are; the cloth moves them.");
            if (_bonePickNote != null)
                Widgets.ColoredText(Theme.Cyan, _bonePickNote);
            ImGui.PopTextWrapPos();
            float half = (ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X) / 2;
            Widgets.DisabledButton(
                _paint.IsNew ? "Done" : "Save",
                chains.Count > 0,
                new Vector2(half, 0),
                () => Defer(Deferred.Author, FinishPaint, replace: true)
            );
            Widgets.ItemTooltip(
                _paint.IsNew
                    ? "Builds this limb's cloth on the picked bones."
                    : "Builds this limb again on the picked bones."
            );
            ImGui.SameLine();
            if (Widgets.Button("Cancel", new Vector2(half, 0)))
                Defer(Deferred.Author, CancelPaint, replace: true);
            Widgets.ItemTooltip(
                _paint.IsNew ? "Drops this limb." : "Keeps the limb's bones as they were."
            );
            if (_genError != null)
                Widgets.ErrorText(_genError);
        }

        /// <summary>Each picked chain's bones tinted in the limb's colour, so the surface they carry shows.</summary>
        Dictionary<string, Vector4> PickTints()
        {
            var tints = new Dictionary<string, Vector4>();
            int index = _gen.Limbs.IndexOf(_paint.Limb);
            var hue = StrandHue(index < 0 ? _gen.Limbs.Count : index);
            foreach (var chain in _paint.Chains)
                for (int k = 0; k < chain.Count; k++)
                {
                    float t = chain.Count > 1 ? k / (float)(chain.Count - 1) : 1;
                    tints[PickBones[chain[k]].Name] = new Vector4(hue * (0.6f + 0.4f * t), 1);
                }
            if (_bonePickHover >= 0 && _bonePickHover < PickBones.Count)
                tints[PickBones[_bonePickHover].Name] = new Vector4(1, 1, 1, 1);
            return tints;
        }

        /// <summary>
        /// Every joint of the model in the viewport, the picked chains in the limb's colour, the
        /// hovered joint named; a click without a drag picks the joint under the cursor.
        /// </summary>
        void DrawBonePickViewport(Vector2 pos, Vector2 size, Vector2 uv0, Vector2 uv1, bool hovered)
        {
            var bones = PickBones;
            var map = ViewMap.Of(_pipeline.Camera, pos, size, uv0, uv1);
            var skeletons = _standalone.Skeletons();
            var screen = ProjectJoints(
                map,
                PaintTransform(),
                bones.Select(b => PosedBone(skeletons, b.Name)).ToList()
            );

            int index = _gen.Limbs.IndexOf(_paint.Limb);
            var hue = StrandHue(index < 0 ? _gen.Limbs.Count : index);
            var member = _paint.Chains.SelectMany(c => c).ToHashSet();
            var dl = ImGui.GetWindowDrawList();
            dl.PushClipRect(pos, pos + size);
            DrawJointLinks(dl, screen, bones.Select(b => b.Parent).ToList(), Color(1, 1, 1, 0.18f));
            foreach (var chain in _paint.Chains)
            {
                int parent = bones[chain[0]].Parent;
                Vector2? last = parent >= 0 ? screen[parent] : null;
                foreach (int b in chain)
                {
                    if (screen[b] is not Vector2 s)
                        continue;
                    if (last is Vector2 l)
                    {
                        dl.AddLine(l, s, Color(0, 0, 0, 0.5f), 4);
                        dl.AddLine(l, s, Color(hue.X, hue.Y, hue.Z, 1), 2);
                    }
                    last = s;
                }
            }
            int near = DrawJointDots(
                dl,
                screen,
                member.Contains,
                Color(
                    Math.Min(1, hue.X + 0.35f),
                    Math.Min(1, hue.Y + 0.35f),
                    Math.Min(1, hue.Z + 0.35f),
                    1
                ),
                hovered
            );
            TestHookNote("pick joints", screen);
            int highlight = near >= 0 ? near : _bonePickHover;
            if (highlight >= 0 && screen[highlight] is Vector2 h)
                DrawJointName(dl, h, bones[highlight].Name);
            dl.PopClipRect();
            _bonePickHover = near;

            //The viewport draws before the authoring windows, so the pick applies at once.
            if (_bonePickClick.Update(hovered) && near >= 0)
                PickBone(near);
        }
    }
}
