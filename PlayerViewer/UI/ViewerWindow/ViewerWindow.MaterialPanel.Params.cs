using System;
using System.Collections.Generic;
using System.Linq;
using BfresEditor;
using BfresLibrary;
using ImGuiNET;
using PlayerViewer.Materials;
using Vector2 = System.Numerics.Vector2;

namespace PlayerViewer.UI
{
    // Params tab of the material editor.
    public partial class ViewerWindow
    {
        string _paramSearch = "";

        /// <summary>
        /// The material's shader parameters, which are what the material uniform block is
        /// built from every time the material is drawn, so an edit here shows immediately.
        /// A three or four float param whose name reads like a colour gets a colour picker.
        /// </summary>
        void DrawShaderParams(FMAT material)
        {
            var parameters = material.Material.ShaderParams;
            if (parameters == null || parameters.Count == 0)
            {
                Widgets.DimText("none");
                return;
            }

            var baseline = Baseline(material);
            DrawResetAll(
                "shader param",
                baseline.ChangedParams(material.Material).ToList(),
                name =>
                {
                    baseline.ResetParam(material.Material, name);
                    MirrorAnimatedParam(material, name);
                },
                material,
                //Nothing to reload: see ParamEdited.
                reload: false
            );

            FilterRow("##paramsearch", ref _paramSearch);

            int shown = 0;
            for (int i = 0; i < parameters.Count; i++)
            {
                string name = parameters.GetKey(i);
                if (!Widgets.Matches(name, _paramSearch))
                    continue;
                shown++;
                var param = parameters[i];
                bool changed = baseline.ParamChanged(material.Material, name);
                DrawEntryName($"{name}  ({param.Type})", changed, dim: true);
                ImGui.PushID($"sp{name}");
                DrawParamValue(
                    material,
                    param,
                    ResetAction(
                        material,
                        changed,
                        () =>
                        {
                            baseline.ResetParam(material.Material, name);
                            MirrorAnimatedParam(material, name);
                        },
                        reload: false
                    )
                );
                ImGui.PopID();
            }
            if (shown == 0)
                Widgets.DimText("nothing matches the filter");
        }

        //The animated copy is what the renderer prefers when it exists, so a write has to land
        //there too or a played animation would keep overwriting it. Its own array, since the
        //animation player writes the animated copy in place.
        static void MirrorAnimatedParam(FMAT material, string name)
        {
            if (
                material.Material.ShaderParams.ContainsKey(name)
                && material.AnimatedParams.TryGetValue(name, out var animated)
            )
            {
                object value = material.Material.ShaderParams[name].DataValue;
                animated.DataValue = value is Array array ? array.Clone() : value;
            }
        }

        /// <summary>
        /// A shader param is pure uniform data, so nothing is reloaded and nothing is
        /// recompiled: gsys_material is rebuilt from the material on every draw, and
        /// FMAT.ShaderParams holds the very ShaderParam objects being edited here, so the new
        /// value is on screen on the next frame by itself.
        /// </summary>
        static void ParamEdited(FMAT material, BfresLibrary.ShaderParam param) =>
            MirrorAnimatedParam(material, param.Name);

        void DrawParamValue(FMAT material, BfresLibrary.ShaderParam param, Action reset)
        {
            void Changed() => ParamEdited(material, param);

            switch (param.DataValue)
            {
                case float f:
                {
                    float v = f;
                    ImGui.SetNextItemWidth(ControlWidth(reset));
                    if (Widgets.Framed(() => ImGui.DragFloat("##v", ref v, 0.01f)))
                    {
                        param.DataValue = v;
                        Changed();
                    }
                    DrawResetButton("spv", reset);
                    return;
                }
                case float[] values when values.Length > 0:
                {
                    if (LooksLikeColour(param.Name, values.Length))
                    {
                        var colour = new System.Numerics.Vector4(
                            values[0],
                            values[1],
                            values[2],
                            values.Length > 3 ? values[3] : 1f
                        );
                        ImGui.SetNextItemWidth(ControlWidth(reset));
                        if (
                            Widgets.ColorEdit4(
                                "##c",
                                ref colour,
                                ImGuiColorEditFlags.Float | ImGuiColorEditFlags.HDR
                            )
                        )
                        {
                            values[0] = colour.X;
                            values[1] = colour.Y;
                            values[2] = colour.Z;
                            if (values.Length > 3)
                                values[3] = colour.W;
                            Changed();
                        }
                        DrawResetButton("spc", reset);
                        return;
                    }
                    DrawFloatArray(
                        "v",
                        values,
                        v =>
                        {
                            param.DataValue = v;
                            Changed();
                        },
                        reset
                    );
                    return;
                }
                case int[] ints:
                    DrawIntArray(
                        "v",
                        ints,
                        v =>
                        {
                            param.DataValue = v;
                            Changed();
                        },
                        reset
                    );
                    return;
                case int n:
                {
                    int v = n;
                    ImGui.SetNextItemWidth(ControlWidth(reset));
                    if (Widgets.Framed(() => ImGui.InputInt("##v", ref v)))
                    {
                        param.DataValue = v;
                        Changed();
                    }
                    DrawResetButton("spi", reset);
                    return;
                }
                case bool b:
                {
                    bool v = b;
                    if (Widgets.CheckboxControl("##v", ref v))
                    {
                        param.DataValue = v;
                        Changed();
                    }
                    DrawResetButton("spb", reset);
                    return;
                }
                case bool[] bools when bools.Length > 0:
                {
                    for (int i = 0; i < bools.Length; i++)
                    {
                        if (i > 0)
                            ImGui.SameLine();
                        bool v = bools[i];
                        if (Widgets.CheckboxControl($"##b{i}", ref v))
                        {
                            bools[i] = v;
                            Changed();
                        }
                    }
                    DrawResetButton("spba", reset);
                    return;
                }
                case BfresLibrary.TexSrt srt:
                {
                    var scale = new System.Numerics.Vector2(srt.Scaling.X, srt.Scaling.Y);
                    var translate = new System.Numerics.Vector2(
                        srt.Translation.X,
                        srt.Translation.Y
                    );
                    float rotate = srt.Rotation;
                    bool changed = false;
                    ImGui.SetNextItemWidth(ControlWidth(reset));
                    Widgets.BeginFramed(2);
                    changed |= ImGui.DragFloat2("##scale", ref scale, 0.01f);
                    Widgets.EndFramed();
                    Widgets.ItemTooltip("scale");
                    DrawResetButton("spsrt", reset);
                    ImGui.SetNextItemWidth(-1);
                    Widgets.BeginFramed();
                    changed |= ImGui.DragFloat("##rot", ref rotate, 0.01f);
                    Widgets.EndFramed();
                    Widgets.ItemTooltip("rotation");
                    ImGui.SetNextItemWidth(-1);
                    Widgets.BeginFramed(2);
                    changed |= ImGui.DragFloat2("##trans", ref translate, 0.01f);
                    Widgets.EndFramed();
                    Widgets.ItemTooltip("translation");
                    if (!changed)
                        return;
                    srt.Scaling = new Syroot.Maths.Vector2F(scale.X, scale.Y);
                    srt.Rotation = rotate;
                    srt.Translation = new Syroot.Maths.Vector2F(translate.X, translate.Y);
                    param.DataValue = srt;
                    Changed();
                    return;
                }
                default:
                    ImGui.AlignTextToFramePadding();
                    Widgets.DimText(param.DataValue?.ToString() ?? "(null)");
                    DrawResetButton("spd", reset);
                    return;
            }
        }

        static bool LooksLikeColour(string name, int length) =>
            (length == 3 || length == 4)
            && name.IndexOf("color", StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
