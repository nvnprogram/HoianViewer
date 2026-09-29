using System;
using System.Linq;
using ImGuiNET;
using OpenTK;
using PlayerViewer.Phive;
using PlayerViewer.Physics;
using Vector2 = System.Numerics.Vector2;

namespace PlayerViewer.UI
{
    // The cloth inspector: the selected file, piece, particle, constraint set or collidable, every
    // value editable in place. A value that differs from the file as opened is drawn in cyan
    // with a Reset beside it; releasing a control closes the edit into one undo step.
    public partial class ViewerWindow
    {
        const float ClothLabelWidth = 150;

        //The deform operator's bone offsets are bytes into its array of 4x4 float matrices.

        int _clothHoverRecord = -1;

        //The piece or collidable whose Remove was pressed, waiting for the second press.
        object _clothPendingDelete;
        string _clothRename;
        object _clothRenameFor;
        float _clothBulkValue = 1;
        float _clothBulkScale = 1;

        void DrawClothInspectorWindow()
        {
            _clothHoverRecord = -1;
            if (
                _standalone == null
                || _cloth == null
                || _clothSel == ClothSelKind.None
                || _paint != null
                || !_physicsTabActive
            )
                return;
            ValidateClothSelection();

            //Against the right sidebar, where it covers the least of a model framed in the middle.
            var viewport = ImGui.GetMainViewport();
            float right =
                SideOrderControls.On && _rightCardLeft > 0
                    ? _rightCardLeft - 2 * SideOrderLayout.Gap
                    : viewport.Pos.X + viewport.Size.X - 300 - 16;
            ImGui.SetNextWindowPos(
                new Vector2(Math.Max(viewport.Pos.X, right - 520), viewport.Pos.Y + 64),
                ImGuiCond.FirstUseEver
            );
            ImGui.SetNextWindowSize(new Vector2(520, 600), ImGuiCond.FirstUseEver);
            ImGui.SetNextWindowSizeConstraints(new Vector2(480, 200), new Vector2(1000, 4000));
            bool open = true;
            if (
                BeginCardWindow(
                    "Cloth Editor###clotheditor",
                    ref open,
                    ImGuiWindowFlags.NoFocusOnAppearing
                )
            )
                Widgets.Guarded("Cloth", DrawClothInspector);
            ImGui.End();
            if (!open)
                _clothSel = ClothSelKind.None;
        }

        void DrawClothInspector()
        {
            var datas = _cloth.File.Container.ClothDatas;
            switch (_clothSel)
            {
                case ClothSelKind.File:
                    DrawClothFileInspector();
                    break;
                case ClothSelKind.Piece:
                    DrawPieceInspector(datas[_clothSelPiece]);
                    break;
                case ClothSelKind.Particles:
                    DrawParticlesInspector(datas[_clothSelPiece]);
                    break;
                case ClothSelKind.Particle:
                    DrawParticleInspector(datas[_clothSelPiece], _clothSelIndex);
                    break;
                case ClothSelKind.Set:
                    DrawSetInspector(datas[_clothSelPiece], _clothSelIndex);
                    break;
                case ClothSelKind.Collidable:
                    var col =
                        _clothSelPiece >= 0
                            ? datas[_clothSelPiece].SimClothDatas[0].PerInstanceCollidables[
                                _clothSelIndex
                            ]
                            : _cloth.File.Container.Collidables[_clothSelIndex];
                    DrawCollidableInspector(col);
                    break;
            }
        }

        /// <summary>A value edit of the cloth data, as one undo step.</summary>
        void CommitCloth()
        {
            _cloth.Changed();
            _cloth.Commit();
        }

        /// <summary>An edit that changed the cloth data's shape, as one undo step.</summary>
        void CommitClothStructure()
        {
            _cloth.StructureChanged();
            _cloth.Commit();
        }

        void RowLabel(string label, bool changed, string tip)
        {
            ImGui.AlignTextToFramePadding();
            Widgets.ColoredText(changed ? Theme.Cyan : Theme.TextMain, label);
            //A label longer than the column pushes its control along rather than under it.
            float end =
                ImGui.GetItemRectMax().X
                - ImGui.GetWindowPos().X
                + ImGui.GetScrollX()
                + ImGui.GetStyle().ItemSpacing.X;
            if (tip != null)
                Widgets.ItemTooltip(tip);
            ImGui.SameLine(Math.Max(ClothLabelWidth, end));
        }

        /// <summary>Marks the document after a control moved, and closes the edit when the control is released.</summary>
        void AfterControl(bool edited)
        {
            if (edited)
                _cloth.Changed();
            if (ImGui.IsItemDeactivatedAfterEdit())
                _cloth.Commit();
        }

        bool ResetButton(string id, bool changed, Action reset)
        {
            if (!changed)
                return false;
            ImGui.SameLine();
            if (!Widgets.SmallButton("Reset##" + id))
                return false;
            reset();
            CommitCloth();
            return true;
        }

        bool ClothFloat(
            string label,
            float value,
            float? baseline,
            Action<float> set,
            float speed = 0.001f,
            float min = 0,
            float max = 0,
            string format = "%.4f",
            string tip = null
        )
        {
            bool changed = baseline.HasValue && baseline.Value != value;
            RowLabel(label, changed, tip);
            ImGui.SetNextItemWidth(changed ? -64 : -1);
            float v = value;
            Widgets.BeginFramed();
            bool edited = ImGui.DragFloat("##" + label, ref v, speed, min, max, format);
            Widgets.EndFramed();
            if (edited)
                set(v);
            AfterControl(edited);
            return ResetButton(label, changed, () => set(baseline.Value)) || edited;
        }

        bool ClothInt(
            string label,
            int value,
            int? baseline,
            Action<int> set,
            int min,
            int max,
            string tip = null
        )
        {
            bool changed = baseline.HasValue && baseline.Value != value;
            RowLabel(label, changed, tip);
            ImGui.SetNextItemWidth(changed ? -64 : -1);
            int v = value;
            bool edited = Widgets.SliderInt("##" + label, ref v, min, max, "%d");
            if (edited)
                set(v);
            AfterControl(edited);
            return ResetButton(label, changed, () => set(baseline.Value)) || edited;
        }

        bool ClothBool(
            string label,
            bool value,
            bool? baseline,
            Action<bool> set,
            string tip = null
        )
        {
            bool changed = baseline.HasValue && baseline.Value != value;
            RowLabel(label, changed, tip);
            bool v = value;
            bool edited = Widgets.CheckboxControl("##" + label, ref v);
            if (edited)
            {
                set(v);
                CommitCloth();
            }
            return ResetButton(label, changed, () => set(baseline.Value)) || edited;
        }

        bool ClothText(
            string label,
            string value,
            string baseline,
            Action<string> set,
            string tip = null
        )
        {
            bool changed = baseline != null && baseline != value;
            RowLabel(label, changed, tip);
            ImGui.SetNextItemWidth(changed ? -64 : -1);
            string v = value ?? "";
            bool edited = Widgets.InputText("##" + label, ref v, 128);
            if (edited)
                set(v);
            AfterControl(edited);
            return ResetButton(label, changed, () => set(baseline)) || edited;
        }

        bool ClothVec3(
            string label,
            Vector3 value,
            Vector3? baseline,
            Action<Vector3> set,
            float speed = 0.002f,
            string tip = null
        )
        {
            bool changed = baseline.HasValue && baseline.Value != value;
            RowLabel(label, changed, tip);
            ImGui.SetNextItemWidth(changed ? -64 : -1);
            var v = new System.Numerics.Vector3(value.X, value.Y, value.Z);
            Widgets.BeginFramed(3);
            bool edited = ImGui.DragFloat3("##" + label, ref v, speed, 0, 0, "%.4f");
            Widgets.EndFramed();
            if (edited)
                set(new Vector3(v.X, v.Y, v.Z));
            AfterControl(edited);
            return ResetButton(label, changed, () => set(baseline.Value)) || edited;
        }

        /// <summary>
        /// A table cell's value, filling its column. Hovering or dragging it marks
        /// <paramref name="record"/> for the viewport, when one is given.
        /// </summary>
        void CellDrag(
            string id,
            float value,
            Action<float> set,
            float speed,
            float min,
            float max,
            string format,
            int record = -1
        )
        {
            ImGui.SetNextItemWidth(-1);
            float v = value;
            Widgets.BeginFramed();
            bool edited = ImGui.DragFloat(id, ref v, speed, min, max, format);
            Widgets.EndFramed();
            if (edited)
                set(v);
            if (record >= 0 && (ImGui.IsItemHovered() || ImGui.IsItemActive()))
                _clothHoverRecord = record;
            AfterControl(edited);
            ImGui.NextColumn();
        }

        /// <summary>A name edited as text and applied on Enter, since a rename touches several places.</summary>
        void ClothRename(string label, object owner, string current, Action<string> apply)
        {
            if (!ReferenceEquals(_clothRenameFor, owner))
            {
                _clothRenameFor = owner;
                _clothRename = current;
            }
            RowLabel(label, false, "Press Enter to apply.");
            ImGui.SetNextItemWidth(-1);
            if (
                Widgets.InputText(
                    "##rename" + label,
                    ref _clothRename,
                    96,
                    ImGuiInputTextFlags.EnterReturnsTrue
                )
                && !string.IsNullOrWhiteSpace(_clothRename)
                && _clothRename != current
            )
            {
                apply(_clothRename.Trim());
                CommitClothStructure();
                _clothRenameFor = null;
            }
        }

        static void Header(string text)
        {
            ImGui.Spacing();
            Widgets.ColoredText(Theme.Gold, text);
            ImGui.Separator();
        }

        ClothData BasePiece(ClothData piece) =>
            _cloth.BaselineFile.Container?.ClothDatas.FirstOrDefault(d => d.Name == piece.Name);

        SimClothData BaseSim(ClothData piece) => BasePiece(piece)?.SimClothDatas.FirstOrDefault();

        ParticleData BaseParticle(ClothData piece, int p)
        {
            var sim = BaseSim(piece);
            return sim != null && sim.ParticleCount == piece.SimClothDatas[0].ParticleCount
                ? sim.Particles.ElementAt(p)
                : null;
        }

        ConstraintSet BaseSet(ClothData piece, int s)
        {
            var sim = BaseSim(piece);
            var set = piece.SimClothDatas[0].ConstraintSets[s];
            if (sim == null || s >= sim.ConstraintSets.Count)
                return null;
            var b = sim.ConstraintSets[s];
            return b.Kind == set.Kind && b.Records.Count == set.Records.Count ? b : null;
        }

        Collidable BaseCollidable(Collidable c) =>
            _cloth.BaselineFile.Container?.Collidables.FirstOrDefault(b =>
                b.Name == c.Name && b.ShapeKind == c.ShapeKind
            );

        void DrawClothFileInspector()
        {
            var file = _cloth.File;
            Widgets.ColoredText(Theme.GoldBright, _cloth.Source.FileName);
            ImGui.PushTextWrapPos();
            Widgets.DimText(_cloth.Source.ToString());
            Widgets.DimText(
                $"{file.Container.ClothDatas.Count} piece(s), {file.Container.Collidables.Count} collidable(s), "
                    + $"{file.Skeletons.Count} skeleton(s), {HkTypeSection.Read(file.TypeSectionBytes).Bodies.Count} declared types"
            );
            var missing = file.MissingTypes(
                ClothAuthor.PieceTypes.Concat(ClothAuthor.CollidableTypes)
            );
            Widgets.DimText(
                missing.Count == 0
                    ? "Declares every class a generated piece uses."
                    : $"Lacks {missing.Count} class(es) a generated piece uses; they are imported from the stock cloths when one is added."
            );
            if (file.Params == null)
                Widgets.ErrorText("The AAMP half could not be read; it is written back unchanged.");
            ImGui.PopTextWrapPos();

            if (file.Params != null)
            {
                Header("Collidable parameters (AAMP)");
                Widgets.DimText("ForReplace, for each collidable:");
                foreach (var cp in file.Params.Collidables)
                {
                    var baseParams = _cloth.BaselineFile.Params?.CollidableFor(cp.Name);
                    ClothBool(
                        cp.Name,
                        cp.ForReplace,
                        baseParams?.ForReplace,
                        v => cp.ForReplace = v
                    );
                }
            }
        }

        void DrawPieceInspector(ClothData piece)
        {
            var file = _cloth.File;
            var sim = piece.SimClothDatas[0];
            var baseSim = BaseSim(piece);
            Widgets.ColoredText(Theme.GoldBright, piece.Name);
            var runtime = _clothRuntime.Pieces.FirstOrDefault(p => p.Index == _clothSelPiece);
            if (runtime?.Problem != null)
                Widgets.ErrorText(runtime.Problem);
            var skeleton = file.SkeletonFor(piece);
            var deform = piece.Operator<MeshBoneDeformOperator>();
            ImGui.PushTextWrapPos();
            Widgets.DimText(
                $"{sim.ParticleCount} particles ({sim.FixedParticles.Count} fixed), {sim.TriangleIndices.Count / 3} triangles, "
                    + $"{sim.ConstraintSets.Count} constraint sets, {sim.PerInstanceCollidables.Count} collidables"
            );
            if (skeleton != null && deform != null)
                Widgets.DimText(
                    "Drives "
                        + string.Join(
                            ", ",
                            deform.TriangleBonePairs.Select(p =>
                                skeleton.BoneNames.ElementAtOrDefault(
                                    p.BoneOffset / MeshBoneDeformOperator.BoneMatrixBytes
                                )
                            )
                        )
                );
            ImGui.PopTextWrapPos();

            ClothRename(
                "Name",
                piece.Source,
                piece.Name,
                name => ClothAuthor.RenamePiece(file, piece, name)
            );

            Header("Simulation");
            var g = sim.Gravity;
            ClothVec3(
                "Gravity",
                g.Xyz,
                baseSim?.Gravity.Xyz,
                v => sim.Gravity = new Vector4(v, g.W),
                0.05f,
                "Per second squared. The game only runs 0 or the default."
            );
            ClothFloat(
                "Damping / s",
                sim.GlobalDampingPerSecond,
                baseSim?.GlobalDampingPerSecond,
                v => sim.GlobalDampingPerSecond = Math.Clamp(v, 0, 1),
                0.0005f,
                0,
                1,
                "%.4f",
                "Velocity lost per second. Stock: 0.001."
            );
            var simulate = piece.Operator<SimulateOperator>();
            var cfg = simulate?.Configs.FirstOrDefault();
            var baseCfg = BasePiece(piece)?.Operator<SimulateOperator>()?.Configs.FirstOrDefault();
            if (cfg != null)
            {
                ClothInt(
                    "Sub steps",
                    cfg.SubSteps,
                    baseCfg?.SubSteps,
                    v => cfg.SubSteps = v,
                    1,
                    8,
                    "Steps per 1/60 s frame. Every stock file: 1."
                );
                ClothInt(
                    "Iterations",
                    cfg.NumberOfSolveIterations,
                    baseCfg?.NumberOfSolveIterations,
                    v => cfg.NumberOfSolveIterations = v,
                    1,
                    8,
                    "Times the execution order runs per step. Every stock file: 1."
                );
                DrawExecutionOrder(piece, cfg, baseCfg);
            }

            Header("Particles, all at once");
            float total = sim.TotalMass;
            ClothFloat(
                "Total mass",
                total,
                baseSim?.TotalMass,
                v => ClothEdit.SetTotalMass(sim, Math.Max(v, 0.001f)),
                0.01f,
                0.001f,
                1000,
                "%.3f",
                "Spread evenly over the free particles."
            );
            var fixedSet = sim.FixedParticles.Ints().ToHashSet();
            float radius = sim
                .Particles.Where((p, i) => !fixedSet.Contains(i))
                .Select(p => p.Radius)
                .DefaultIfEmpty(0)
                .Average();
            ClothFloat(
                "Radius (all)",
                radius,
                null,
                v =>
                {
                    foreach (var p in sim.Particles)
                        p.Radius = Math.Max(v, 0);
                    ClothEdit.RecomputeTotals(sim);
                },
                0.001f,
                0,
                1,
                "%.4f",
                "Sets every particle's collision radius."
            );
            float friction = sim.Particles.Select(p => p.Friction).DefaultIfEmpty(0).Average();
            ClothFloat(
                "Friction (all)",
                friction,
                null,
                v =>
                {
                    foreach (var p in sim.Particles)
                        p.Friction = Math.Clamp(v, 0, 1);
                },
                0.005f,
                0,
                1,
                "%.3f",
                "The share of tangential velocity a contact takes away. Stock: 0.5."
            );

            if (file.Params?.MeshFor(piece.Name) is MeshParams mesh)
                DrawMeshParams(mesh, _cloth.BaselineFile.Params?.MeshFor(piece.Name));

            Header("Remove");
            Widgets.ConfirmButton(
                "Remove piece",
                $"Really remove {piece.Name}",
                piece.Source,
                ref _clothPendingDelete,
                () =>
                {
                    ClothAuthor.RemovePiece(file, piece);
                    CommitClothStructure();
                    SelectCloth(ClothSelKind.File, -1, -1);
                }
            );
        }

        void DrawMeshParams(MeshParams mesh, MeshParams baseline)
        {
            Header("Mesh parameters (AAMP)");
            ClothText(
                "BaseBone",
                mesh.BaseBone,
                baseline?.BaseBone,
                v => mesh.BaseBone = v,
                "The bone the piece hangs from."
            );
            ClothText(
                "Preset",
                mesh.Preset,
                baseline?.Preset,
                v => mesh.Preset = v,
                "The named parameter preset: leather on the stock strands, SpringGravity0 on the nape pieces."
            );
            ClothText(
                "WindPreset",
                mesh.WindPreset,
                baseline?.WindPreset,
                v => mesh.WindPreset = v,
                "The movement wind preset, empty for none. The wind only blows while the actor moves."
            );
            ClothBool(
                "BoneCorrection",
                mesh.BoneCorrection,
                baseline?.BoneCorrection,
                v =>
                {
                    mesh.BoneCorrection = v;
                },
                "Turns each bone the cloth moves to point at the next one down the strand."
            );
            ClothText(
                "Axis order",
                mesh.BoneCorrectionAxisOrder,
                baseline?.BoneCorrectionAxisOrder,
                v => mesh.BoneCorrectionAxisOrder = v,
                "Only xyz previews."
            );
            ClothBool("Twist", mesh.Twist, baseline?.Twist, v => mesh.Twist = v);
            ClothText(
                "Twist swing axis",
                mesh.TwistSwingAxis,
                baseline?.TwistSwingAxis,
                v => mesh.TwistSwingAxis = v
            );
            ClothFloat(
                "Twist angle coef",
                mesh.TwistAngleCoef,
                baseline?.TwistAngleCoef,
                v => mesh.TwistAngleCoef = v,
                0.01f
            );
            ClothFloat(
                "Twist max angle",
                mesh.TwistMaxAngle,
                baseline?.TwistMaxAngle,
                v => mesh.TwistMaxAngle = v,
                0.5f
            );
        }

        void DrawExecutionOrder(ClothData piece, SimulateConfig cfg, SimulateConfig baseCfg)
        {
            var sets = piece.SimClothDatas[0].ConstraintSets;
            var order = cfg.ConstraintExecution;
            var names = sets.Select(s => s.Name ?? s.TypeName).Append("Collide").ToArray();
            bool changed =
                baseCfg != null && !baseCfg.ConstraintExecution.Ints().SequenceEqual(order.Ints());
            Widgets.ColoredText(changed ? Theme.Cyan : Theme.TextMain, "Execution order");
            Widgets.ItemTooltip("Constraint sets in solver order. Unlisted sets never run.");
            if (changed)
            {
                ImGui.SameLine();
                if (Widgets.SmallButton("Reset##exec"))
                {
                    order.Clear();
                    foreach (int e in baseCfg.ConstraintExecution.Ints())
                        order.AddValue(e);
                    CommitCloth();
                }
            }
            int remove = -1,
                up = -1;
            for (int i = 0; i < order.Count; i++)
            {
                ImGui.PushID(i);
                int e = Convert.ToInt32(order[i]);
                int current = e < 0 ? names.Length - 1 : Math.Min(e, names.Length - 1);
                ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - 90);
                if (Widgets.Combo("##e", ref current, names, names.Length))
                {
                    order.SetValue(i, current == names.Length - 1 ? -1 : current);
                    CommitCloth();
                }
                ImGui.SameLine();
                if (Widgets.SmallButton("up") && i > 0)
                    up = i;
                ImGui.SameLine();
                if (Widgets.SmallButton("x"))
                    remove = i;
                ImGui.PopID();
            }
            if (up > 0)
            {
                var a = order[up];
                order[up] = order[up - 1];
                order[up - 1] = a;
                CommitCloth();
            }
            if (remove >= 0)
            {
                order.RemoveAt(remove);
                CommitCloth();
            }
            if (Widgets.SmallButton("+ step"))
            {
                order.AddValue(sets.Count > 0 ? 0 : -1);
                CommitCloth();
            }
        }

        void DrawParticlesInspector(ClothData piece)
        {
            var sim = piece.SimClothDatas[0];
            Widgets.ColoredText(Theme.GoldBright, $"{piece.Name}: particles");
            Widgets.DimText("Click a row, or a particle in the viewport, for its details.");
            var particles = sim.Particles.ToList();
            var fixedSet = sim.FixedParticles.Ints().ToHashSet();
            var baseSim = BaseSim(piece);
            var baseParticles =
                baseSim != null && baseSim.ParticleCount == particles.Count
                    ? baseSim.Particles.ToList()
                    : null;
            var baseFixed = baseSim?.FixedParticles.Ints().ToHashSet();

            float valueWidth = MathF.Max(70, (ImGui.GetContentRegionAvail().X - 94) / 3);
            ImGui.Columns(5, "##particles", true);
            ImGui.SetColumnWidth(0, 44);
            ImGui.SetColumnWidth(1, 50);
            for (int c = 2; c < 5; c++)
                ImGui.SetColumnWidth(c, valueWidth);
            foreach (var h in new[] { "#", "fixed", "mass", "radius", "friction" })
            {
                Widgets.DimText(h);
                ImGui.NextColumn();
            }
            ImGui.Separator();
            for (int p = 0; p < particles.Count; p++)
            {
                ImGui.PushID(p);
                var pd = particles[p];
                var bp = baseParticles?[p];
                bool rowChanged =
                    bp != null
                    && (
                        bp.Mass != pd.Mass
                        || bp.Radius != pd.Radius
                        || bp.Friction != pd.Friction
                        || baseFixed.Contains(p) != fixedSet.Contains(p)
                    );
                ImGui.PushStyleColor(ImGuiCol.Text, rowChanged ? Theme.Cyan : Theme.TextMain);
                if (
                    ImGui.Selectable(
                        p.ToString(),
                        _clothSelIndex == p,
                        ImGuiSelectableFlags.SpanAllColumns | ImGuiSelectableFlags.AllowItemOverlap
                    )
                )
                    SelectCloth(ClothSelKind.Particle, _clothSelPiece, p);
                ImGui.PopStyleColor();
                if (ImGui.IsItemHovered())
                    _clothHoverRecord = p;
                ImGui.NextColumn();
                bool isFixed = fixedSet.Contains(p);
                if (Widgets.CheckboxControl("##fixed", ref isFixed))
                {
                    ClothEdit.SetFixed(piece, p, isFixed);
                    CommitCloth();
                }
                ImGui.NextColumn();
                if (isFixed)
                {
                    Widgets.DimText("0");
                    ImGui.NextColumn();
                }
                else
                {
                    int index = p;
                    CellDrag(
                        "##mass",
                        pd.Mass,
                        v => ClothEdit.SetMass(sim, index, v),
                        0.01f,
                        0.001f,
                        1000,
                        "%.3f"
                    );
                }
                CellDrag(
                    "##radius",
                    pd.Radius,
                    v =>
                    {
                        pd.Radius = v;
                        ClothEdit.RecomputeTotals(sim);
                    },
                    0.001f,
                    0,
                    1,
                    "%.4f"
                );
                CellDrag("##friction", pd.Friction, v => pd.Friction = v, 0.005f, 0, 1, "%.3f");
                ImGui.PopID();
            }
            ImGui.Columns(1);
        }

        void DrawParticleInspector(ClothData piece, int p)
        {
            var sim = piece.SimClothDatas[0];
            var pd = sim.Particles.ElementAt(p);
            var bp = BaseParticle(piece, p);
            bool isFixed = sim.FixedParticles.Ints().Contains(p);
            Widgets.ColoredText(Theme.GoldBright, $"{piece.Name}: particle {p}");
            if (Widgets.SmallButton("All particles"))
                SelectCloth(ClothSelKind.Particles, _clothSelPiece, p);
            var rest = ClothEdit.RestPositions(sim);
            if (p < rest.Length)
                Widgets.DimText(
                    $"rest ({rest[p].X:F3}, {rest[p].Y:F3}, {rest[p].Z:F3}), reference vertex {ClothEdit.ReferenceVertex(piece, p)}"
                );

            bool baseFixed = BaseSim(piece)?.FixedParticles.Ints().Contains(p) ?? isFixed;
            ClothBool(
                "Fixed",
                isFixed,
                baseFixed,
                v => ClothEdit.SetFixed(piece, p, v),
                "Follows its reference vertex exactly and never collides."
            );
            if (!isFixed)
                ClothFloat(
                    "Mass",
                    pd.Mass,
                    bp?.Mass,
                    v => ClothEdit.SetMass(sim, p, Math.Max(v, 0.001f)),
                    0.01f,
                    0.001f,
                    1000,
                    "%.4f"
                );
            ClothFloat(
                "Radius",
                pd.Radius,
                bp?.Radius,
                v =>
                {
                    pd.Radius = Math.Max(v, 0);
                    ClothEdit.RecomputeTotals(sim);
                },
                0.001f,
                0,
                1
            );
            ClothFloat(
                "Friction",
                pd.Friction,
                bp?.Friction,
                v => pd.Friction = Math.Clamp(v, 0, 1),
                0.005f,
                0,
                1
            );

            Header("Constraints on this particle");
            for (int s = 0; s < sim.ConstraintSets.Count; s++)
            {
                var set = sim.ConstraintSets[s];
                int count = set.Records.Objects.Count(r =>
                    (r.Has("particleA") && (r.Int("particleA") == p || r.Int("particleB") == p))
                    || (r.Has("particleIndex") && r.Int("particleIndex") == p)
                    || (r.Has("particleC") && (r.Int("particleC") == p || r.Int("particleD") == p))
                );
                if (count == 0)
                    continue;
                if (ImGui.Selectable($"{set.Name ?? set.TypeName}: {count}##s{s}"))
                    SelectCloth(ClothSelKind.Set, _clothSelPiece, s);
            }
        }

        void DrawSetInspector(ClothData piece, int s)
        {
            var sim = piece.SimClothDatas[0];
            var set = sim.ConstraintSets[s];
            var baseSet = BaseSet(piece, s);
            Widgets.ColoredText(Theme.GoldBright, $"{piece.Name}: {set.Name}");
            Widgets.DimText($"{set.TypeName}, {set.Records.Count} entries");
            var cfg = piece.Operator<SimulateOperator>()?.Configs.FirstOrDefault();
            int runs = cfg?.ConstraintExecution.Ints().Count(e => e == s) ?? 0;
            if (runs == 0)
                Widgets.ColoredText(
                    Theme.Error,
                    "Not in the execution order: this set never runs."
                );
            else
                Widgets.DimText($"Runs {runs} time(s) per iteration.");

            switch (set.Kind)
            {
                case ConstraintSetKind.StandardLink:
                    DrawLinkTable(sim, set, baseSet, standard: true);
                    break;
                case ConstraintSetKind.StretchLink:
                    DrawLinkTable(sim, set, baseSet, standard: false);
                    break;
                case ConstraintSetKind.BendLink:
                    DrawBendTable(sim, set, baseSet);
                    break;
                case ConstraintSetKind.LocalRange:
                    DrawLocalRange(sim, (LocalRangeSet)set, baseSet as LocalRangeSet);
                    break;
                case ConstraintSetKind.Transition:
                    DrawTransition((TransitionSet)set, baseSet as TransitionSet);
                    break;
                case ConstraintSetKind.BendStiffness:
                    DrawBendStiffness((BendStiffnessSet)set, baseSet as BendStiffnessSet);
                    break;
                default:
                    Widgets.DimText(
                        "The viewer's solver has no kernel for this set; it is kept as it is."
                    );
                    break;
            }
        }

        /// <summary>A bulk value and an Apply button, for setting one field of every entry at once.</summary>
        bool BulkRow(string label, string tip, ref float value, float speed, string format = "%.3f")
        {
            RowLabel(label, false, tip);
            ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - 60);
            Widgets.BeginFramed();
            ImGui.DragFloat("##bulk" + label, ref value, speed, 0, 0, format);
            Widgets.EndFramed();
            ImGui.SameLine();
            return Widgets.Button("Apply##" + label, new Vector2(-1, 0));
        }

        void DrawLinkTable(
            SimClothData sim,
            ConstraintSet set,
            ConstraintSet baseSet,
            bool standard
        )
        {
            Header("All links");
            if (standard)
            {
                if (
                    BulkRow(
                        "Stiffness k",
                        "Sets every link's stiffness.",
                        ref _clothBulkValue,
                        0.01f
                    )
                )
                {
                    foreach (var l in set.Records.Objects)
                        ClothEdit.SetLinkK(sim, l, _clothBulkValue);
                    CommitCloth();
                }
            }
            else if (
                BulkRow("Stiffness", "Sets every link's stiffness.", ref _clothBulkValue, 0.01f)
            )
            {
                foreach (var l in set.Records.Objects)
                    l.Set("stiffness", _clothBulkValue);
                CommitCloth();
            }
            if (BulkRow("Scale rest", "Multiplies every rest length.", ref _clothBulkScale, 0.005f))
            {
                foreach (var l in set.Records.Objects)
                    l.Set("restLength", l.Float("restLength") * _clothBulkScale);
                CommitCloth();
            }
            if (Widgets.Button("Rest lengths from the rest pose", new Vector2(-1, 0)))
            {
                ClothEdit.ResetRestLengths(sim, set);
                CommitCloth();
            }

            Header("Links");
            ImGui.Columns(4, "##links", true);
            foreach (var h in new[] { "A - B", "rest length", standard ? "k" : "stiffness", "" })
            {
                Widgets.DimText(h);
                ImGui.NextColumn();
            }
            ImGui.Separator();
            var records = set.Records.Objects.ToList();
            var baseRecords = baseSet?.Records.Objects.ToList();
            for (int i = 0; i < records.Count; i++)
            {
                var l = records[i];
                var b = baseRecords?[i];
                ImGui.PushID(i);
                bool changed =
                    b != null
                    && (
                        b.Float("restLength") != l.Float("restLength")
                        || b.Float("stiffness") != l.Float("stiffness")
                    );
                Widgets.ColoredText(
                    changed ? Theme.Cyan : Theme.TextMain,
                    $"{l.Int("particleA")} - {l.Int("particleB")}"
                );
                if (ImGui.IsItemHovered())
                    _clothHoverRecord = i;
                ImGui.NextColumn();
                CellDrag(
                    "##rest",
                    l.Float("restLength"),
                    v => l.Set("restLength", Math.Max(v, 0)),
                    0.001f,
                    0,
                    10,
                    "%.4f",
                    i
                );
                CellDrag(
                    "##k",
                    standard ? ClothEdit.LinkK(sim, l) : l.Float("stiffness"),
                    v =>
                    {
                        if (standard)
                            ClothEdit.SetLinkK(sim, l, v);
                        else
                            l.Set("stiffness", v);
                    },
                    0.005f,
                    0,
                    10,
                    "%.3f",
                    i
                );
                if (changed && Widgets.SmallButton("Reset"))
                {
                    l.Set("restLength", b.Float("restLength"));
                    l.Set("stiffness", b.Float("stiffness"));
                    CommitCloth();
                }
                ImGui.NextColumn();
                ImGui.PopID();
            }
            ImGui.Columns(1);
        }

        void DrawBendTable(SimClothData sim, ConstraintSet set, ConstraintSet baseSet)
        {
            Header("All bend links");
            Widgets.DimText(
                "A band on the distance: pushed apart below the minimum, pulled in above the maximum."
            );
            if (
                BulkRow(
                    "Min / max",
                    "Sets every link's minimum to this fraction of its maximum.",
                    ref _clothBulkScale,
                    0.005f
                )
            )
            {
                foreach (var l in set.Records.Objects)
                    l.Set("bendMinLength", l.Float("stretchMaxLength") * _clothBulkScale);
                CommitCloth();
            }
            if (
                BulkRow("Stiffness k", "Sets both sides of every link.", ref _clothBulkValue, 0.01f)
            )
            {
                foreach (var l in set.Records.Objects)
                {
                    ClothEdit.SetLinkK(sim, l, _clothBulkValue, "bendStiffness");
                    ClothEdit.SetLinkK(sim, l, _clothBulkValue, "stretchStiffness");
                }
                CommitCloth();
            }
            if (Widgets.Button("Lengths from the rest pose", new Vector2(-1, 0)))
            {
                ClothEdit.ResetRestLengths(sim, set);
                CommitCloth();
            }

            Header("Bend links");
            ImGui.Columns(5, "##bend", true);
            foreach (var h in new[] { "A - B", "min", "max", "bend k", "stretch k" })
            {
                Widgets.DimText(h);
                ImGui.NextColumn();
            }
            ImGui.Separator();
            var records = set.Records.Objects.ToList();
            var baseRecords = baseSet?.Records.Objects.ToList();
            for (int i = 0; i < records.Count; i++)
            {
                var l = records[i];
                var b = baseRecords?[i];
                ImGui.PushID(i);
                bool changed =
                    b != null
                    && (
                        b.Float("bendMinLength") != l.Float("bendMinLength")
                        || b.Float("stretchMaxLength") != l.Float("stretchMaxLength")
                        || b.Float("bendStiffness") != l.Float("bendStiffness")
                        || b.Float("stretchStiffness") != l.Float("stretchStiffness")
                    );
                Widgets.ColoredText(
                    changed ? Theme.Cyan : Theme.TextMain,
                    $"{l.Int("particleA")} - {l.Int("particleB")}"
                );
                if (ImGui.IsItemHovered())
                    _clothHoverRecord = i;
                ImGui.NextColumn();
                foreach (var field in new[] { "bendMinLength", "stretchMaxLength" })
                    CellDrag(
                        "##" + field,
                        l.Float(field),
                        v => l.Set(field, Math.Max(v, 0)),
                        0.001f,
                        0,
                        10,
                        "%.4f",
                        i
                    );
                foreach (var field in new[] { "bendStiffness", "stretchStiffness" })
                    CellDrag(
                        "##" + field,
                        ClothEdit.LinkK(sim, l, field),
                        v => ClothEdit.SetLinkK(sim, l, v, field),
                        0.005f,
                        -10,
                        10,
                        "%.3f"
                    );
                ImGui.PopID();
            }
            ImGui.Columns(1);
        }

        void DrawLocalRange(SimClothData sim, LocalRangeSet set, LocalRangeSet baseSet)
        {
            Header("Local range");
            Widgets.DimText("Each particle stays within its radius of the mesh.");
            ClothFloat(
                "Stiffness",
                set.Stiffness,
                baseSet?.Stiffness,
                v => set.Stiffness = v,
                0.005f,
                0,
                10,
                "%.3f",
                "The set's stiffness; with per particle values the two multiply. Values above 1 are legal."
            );
            if (BulkRow("Scale radii", "Multiplies every radius.", ref _clothBulkScale, 0.005f))
            {
                foreach (var l in set.Records.Objects)
                    l.Set("shapeRadius", l.Float("shapeRadius") * _clothBulkScale);
                CommitCloth();
            }
            bool perParticle = set.Records.Objects.FirstOrDefault()?.Has("stiffness") == true;
            ImGui.Columns(perParticle ? 3 : 2, "##range", true);
            foreach (
                var h in perParticle
                    ? new[] { "particle", "radius", "stiffness" }
                    : new[] { "particle", "radius" }
            )
            {
                Widgets.DimText(h);
                ImGui.NextColumn();
            }
            ImGui.Separator();
            var records = set.Records.Objects.ToList();
            var baseRecords = baseSet?.Records.Objects.ToList();
            for (int i = 0; i < records.Count; i++)
            {
                var l = records[i];
                var b =
                    baseRecords != null && baseRecords.Count == records.Count
                        ? baseRecords[i]
                        : null;
                ImGui.PushID(i);
                bool changed =
                    b != null
                    && (
                        b.Float("shapeRadius") != l.Float("shapeRadius")
                        || (perParticle && b.Float("stiffness") != l.Float("stiffness"))
                    );
                Widgets.ColoredText(
                    changed ? Theme.Cyan : Theme.TextMain,
                    l.Int("particleIndex").ToString()
                );
                if (ImGui.IsItemHovered())
                    _clothHoverRecord = i;
                ImGui.NextColumn();
                CellDrag(
                    "##r",
                    l.Float("shapeRadius"),
                    v => l.Set("shapeRadius", Math.Max(v, 0)),
                    0.002f,
                    0,
                    10,
                    "%.4f",
                    i
                );
                if (perParticle)
                    CellDrag(
                        "##k",
                        l.Float("stiffness"),
                        v => l.Set("stiffness", v),
                        0.005f,
                        0,
                        10,
                        "%.3f"
                    );
                ImGui.PopID();
            }
            ImGui.Columns(1);
        }

        void DrawTransition(TransitionSet set, TransitionSet baseSet)
        {
            Header("Transition");
            Widgets.DimText(
                "After a restart the game releases the cloth from the animation over one second."
            );
            foreach (
                var field in new[]
                {
                    "toSimPeriod",
                    "toSimPlusDelayPeriod",
                    "toAnimPeriod",
                    "toAnimPlusDelayPeriod",
                }
            )
                if (set.Source.Has(field))
                    ClothFloat(
                        field,
                        set.Source.Float(field),
                        baseSet?.Source.Float(field),
                        v => set.Source.Set(field, Math.Max(v, 0)),
                        0.01f,
                        0,
                        10,
                        "%.3f"
                    );
            if (
                BulkRow(
                    "Max distance",
                    "Sets every particle's toSimMaxDistance: how far it may be from its reference at the end of the release.",
                    ref _clothBulkValue,
                    0.01f
                )
            )
            {
                foreach (var r in set.Records.Objects)
                    r.Set("toSimMaxDistance", _clothBulkValue);
                CommitCloth();
            }
        }

        void DrawBendStiffness(BendStiffnessSet set, BendStiffnessSet baseSet)
        {
            Header("Bend stiffness");
            ClothBool(
                "Rest pose config",
                set.UseRestPoseConfig,
                baseSet?.UseRestPoseConfig,
                v => set.UseRestPoseConfig = v
            );
            ClothBool(
                "Clamp",
                set.ClampBendStiffness,
                baseSet?.ClampBendStiffness,
                v => set.ClampBendStiffness = v
            );
            ClothFloat(
                "Max rest height squared",
                set.MaxRestPoseHeightSq,
                baseSet?.MaxRestPoseHeightSq,
                v => set.MaxRestPoseHeightSq = v,
                0.001f
            );
            if (
                BulkRow(
                    "Scale stiffness",
                    "Multiplies every bend stiffness; keep the sign.",
                    ref _clothBulkScale,
                    0.005f
                )
            )
            {
                foreach (var l in set.Records.Objects)
                    l.Set("bendStiffness", l.Float("bendStiffness") * _clothBulkScale);
                CommitCloth();
            }
        }

        void DrawCollidableInspector(Collidable col)
        {
            var file = _cloth.File;
            var bones = ClothModelBones();
            var baseCol = BaseCollidable(col);
            Widgets.ColoredText(Theme.GoldBright, col.Name);
            Widgets.DimText($"{col.ShapeKind} collidable");
            if (
                col.ShapeKind == CollidableShapeKind.Sphere
                && !_clothRuntime.Options.CollideSpheres
            )
                Widgets.ColoredText(
                    Theme.Error,
                    "The preview skips spheres while Collide spheres is off."
                );
            if (col.ShapeKind == CollidableShapeKind.Plane)
                Widgets.ColoredText(
                    Theme.Error,
                    "Planes are kept but not collided: the game's plane kernel has not been decoded."
                );

            ClothRename(
                "Name",
                col.Source,
                col.Name,
                name => ClothAuthor.RenameCollidable(file, col, name)
            );
            ClothBool("Enabled", col.Enabled, baseCol?.Enabled, v => col.Enabled = v);
            ClothBool(
                "Virtual points",
                col.VirtualCollisionPointCollisionEnabled,
                baseCol?.VirtualCollisionPointCollisionEnabled,
                v => col.VirtualCollisionPointCollisionEnabled = v,
                "Also collides the piece's virtual collision points (edge midpoints), when the piece has them."
            );

            Header("Shape, in the collidable's space");
            switch (col.ShapeKind)
            {
                case CollidableShapeKind.Capsule:
                {
                    var (s, e, r) = col.Capsule;
                    var b =
                        baseCol?.ShapeKind == CollidableShapeKind.Capsule
                            ? baseCol.Capsule
                            : ((Vector3, Vector3, float)?)null;
                    ClothVec3("Start", s, b?.Item1, v => ClothEdit.SetCapsule(col, v, e, r));
                    ClothVec3("End", e, b?.Item2, v => ClothEdit.SetCapsule(col, s, v, r));
                    ClothFloat(
                        "Radius",
                        r,
                        b?.Item3,
                        v => ClothEdit.SetCapsule(col, s, e, Math.Max(v, 0)),
                        0.001f,
                        0,
                        2
                    );
                    break;
                }
                case CollidableShapeKind.Sphere:
                {
                    var sp = col.Sphere;
                    var b =
                        baseCol?.ShapeKind == CollidableShapeKind.Sphere
                            ? baseCol.Sphere
                            : (Vector4?)null;
                    ClothVec3("Centre", sp.Xyz, b?.Xyz, v => ClothEdit.SetSphere(col, v, sp.W));
                    ClothFloat(
                        "Radius",
                        sp.W,
                        b?.W,
                        v => ClothEdit.SetSphere(col, sp.Xyz, Math.Max(v, 0)),
                        0.001f,
                        0,
                        2
                    );
                    break;
                }
                case CollidableShapeKind.Plane:
                {
                    var pl = col.PlaneEquation;
                    ClothVec3(
                        "Normal",
                        pl.Xyz,
                        baseCol?.PlaneEquation.Xyz,
                        v => ClothEdit.SetPlane(col, v, pl.W)
                    );
                    ClothFloat(
                        "Offset",
                        pl.W,
                        baseCol?.PlaneEquation.W,
                        v => ClothEdit.SetPlane(col, pl.Xyz, v),
                        0.002f
                    );
                    break;
                }
            }

            Header("Placement");
            var t = HkValue.Affine(col.Transform);
            ClothVec3(
                "Rest position",
                t.Row3.Xyz,
                baseCol != null ? HkValue.Affine(baseCol.Transform).Row3.Xyz : (Vector3?)null,
                v =>
                {
                    var m = HkValue.Affine(col.Transform);
                    m.Row3 = new Vector4(v, 1);
                    col.Transform = m;
                    ClothCollidables.RefreshOffsets(file, col, bones);
                },
                0.002f,
                "The collidable's rest position in model space."
            );

            Header("Pieces");
            var datas = file.Container.ClothDatas;
            for (int i = 0; i < datas.Count; i++)
            {
                ImGui.PushID(i);
                var piece = datas[i];
                var sim = piece.SimClothDatas[0];
                int slot = sim.PerInstanceCollidables.FindIndex(c => c.Source == col.Source);
                bool on = slot >= 0;
                if (Widgets.CheckboxControl(piece.Name, ref on))
                {
                    if (on)
                        ClothAuthor.Attach(
                            file,
                            piece,
                            col,
                            bones,
                            ClothAuthor.NearestBone(bones, HkValue.Affine(col.Transform))
                        );
                    else
                        ClothAuthor.Detach(piece, col);
                    CommitClothStructure();
                    ImGui.PopID();
                    return;
                }
                if (on)
                {
                    var skeleton = file.SkeletonFor(piece);
                    var names = skeleton?.BoneNames ?? Array.Empty<string>();
                    int bone = sim.CollidableTransformIndices.ElementAtOrDefault(slot);
                    ImGui.SameLine(ClothLabelWidth);
                    ImGui.SetNextItemWidth(-1);
                    if (Widgets.BeginCombo("##bone", names.ElementAtOrDefault(bone) ?? "?"))
                    {
                        for (int b = 0; b < names.Length; b++)
                            if (ImGui.Selectable($"{names[b]}##cb{b}", b == bone))
                            {
                                ClothCollidables.SetBone(file, piece, col, slot, b, bones);
                                CommitCloth();
                            }
                        ImGui.EndCombo();
                    }
                    Widgets.ItemTooltip("The cloth bone this piece moves the collidable with.");
                }
                ImGui.PopID();
            }

            if (file.Params?.CollidableFor(col.Name) is CollidableParams cp)
            {
                Header("Parameters (AAMP)");
                ClothBool(
                    "ForReplace",
                    cp.ForReplace,
                    _cloth.BaselineFile.Params?.CollidableFor(col.Name)?.ForReplace,
                    v => cp.ForReplace = v
                );
            }

            Header("Remove");
            Widgets.ConfirmButton(
                "Remove collidable",
                $"Really remove {col.Name}",
                col.Source,
                ref _clothPendingDelete,
                () =>
                {
                    ClothAuthor.RemoveCollidable(file, col);
                    CommitClothStructure();
                    SelectCloth(ClothSelKind.File, -1, -1);
                }
            );
        }
    }
}
