using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ImGuiNET;
using PlayerViewer.Core;
using PlayerViewer.Env;
using Vector4 = System.Numerics.Vector4;

namespace PlayerViewer.UI
{
    // The Env rows of the View section: the dumped Viewer lighting, or a stage's own lighting
    // built from its romfs params.
    public partial class ViewerWindow
    {
        static readonly string[] EnvSourceLabels = { "Viewer", "Stage" };
        const int EnvSourceViewer = 0;
        const int EnvSourceStage = 1;

        //The dumped uniform set the Viewer source draws with.
        const string ViewerUniformSet = "SPL3";

        //Dump the generated blocks are laid over: its constant rows are the in-stage ones, and
        //its ambient is the shape the estimated ambient is scaled from.
        const string StageTemplateSet = "SPL3_AutoWalk";
        const string StageTemplateScene = "Vss_AutoWalk00";
        const string StageTemplateVariant = "Day";

        int _envSource = EnvSourceViewer;
        List<string> _envScenes;
        Romfs _envScenesRomfs;
        string _envScene = StageTemplateScene;
        List<SceneEnv> _envVariants;
        int _envVariant;

        //Variant names picked by hand, most recent first. A stage takes the first one it has, so
        //Night survives a trip through a stage without it.
        readonly List<string> _envVariantPicks = new();
        string _envStatus;
        EnvParams _envReference;

        void DrawEnvironmentRows()
        {
            Widgets.LabeledRow(
                "Env",
                () =>
                {
                    int source = _envSource;
                    ImGui.SetNextItemWidth(-1);
                    if (Widgets.ComboIndex("##uniset", ref source, EnvSourceLabels))
                        SetEnvSource(source);
                }
            );
            Widgets.ItemTooltip("Viewer: the locker's lighting. Stage: a stage's.");

            if (_envSource != EnvSourceStage || _romfs == null)
                return;

            if (_envScenes == null || _envScenesRomfs != _romfs)
            {
                _envScenes = EnvCatalog.ListScenes(_romfs);
                _envScenesRomfs = _romfs;
                _envReference = null;
            }

            Widgets.LabeledRow(
                "Stage",
                () =>
                {
                    if (
                        Widgets.StringCombo("##envscene", _envScene, _envScenes, out var picked)
                        && picked != null
                    )
                    {
                        _envScene = picked;
                        _envVariants = null;
                        _envVariant = -1;
                        ApplyStageEnv();
                    }
                }
            );

            if (_envVariants == null || _envVariants.Count == 0)
            {
                if (_envStatus != null)
                    Widgets.DimText(_envStatus);
                return;
            }

            Widgets.LabeledRow(
                "Time",
                () =>
                {
                    float left = ImGui.GetCursorPosX();
                    float right = ImGui.GetWindowContentRegionMax().X;
                    float spacing = ImGui.GetStyle().ItemSpacing.X;
                    float padding = ImGui.GetStyle().FramePadding.X * 2;
                    for (int i = 0; i < _envVariants.Count; i++)
                    {
                        //Coop stages have four variants, which do not fit on one line.
                        if (i > 0)
                        {
                            float width = ImGui.CalcTextSize(_envVariants[i].Variant).X + padding;
                            ImGui.SameLine();
                            if (ImGui.GetCursorPosX() + width > right)
                            {
                                ImGui.NewLine();
                                ImGui.SetCursorPosX(left);
                            }
                        }
                        bool active = i == _envVariant;
                        bool sideOrder = SideOrderControls.On;
                        if (active && !sideOrder)
                            ImGui.PushStyleColor(
                                ImGuiCol.Button,
                                ImGui.GetStyle().Colors[(int)ImGuiCol.ButtonActive]
                            );
                        if (Widgets.Button(_envVariants[i].Variant, default, active) && !active)
                        {
                            _envVariant = i;
                            _envVariantPicks.Remove(_envVariants[i].Variant);
                            _envVariantPicks.Insert(0, _envVariants[i].Variant);
                            ApplyStageEnv();
                        }
                        if (active && !sideOrder)
                            ImGui.PopStyleColor();
                    }
                }
            );
            Widgets.ItemTooltip("Time of day");
            if (_envStatus != null)
                Widgets.DimText(_envStatus);
        }

        void SetEnvSource(int source)
        {
            _envSource = source;
            if (source == EnvSourceStage)
                ApplyStageEnv();
            else
            {
                ClearGeneratedEnv();
                BfresEditor.HoianNXRender.SetUniformSet(ViewerUniformSet);
                ApplyTeamColor();
            }
        }

        void ApplyStageEnv()
        {
            if (_romfs == null)
                return;
            _envStatus = null;
            if (_envVariants == null)
            {
                try
                {
                    _envVariants = EnvCatalog.Load(_romfs, _envScene);
                }
                catch (Exception ex)
                {
                    _envVariants = new List<SceneEnv>();
                    _envStatus = "Unreadable: " + ex.Message;
                }
                if (_envVariants.Count == 0 && _envStatus == null)
                    _envStatus = "No lighting for this stage.";
                if (_envVariant < 0)
                    _envVariant = _envVariantPicks
                        .Select(name => _envVariants.FindIndex(e => e.Variant == name))
                        .FirstOrDefault(i => i >= 0);
                _envVariant = Math.Clamp(_envVariant, 0, Math.Max(0, _envVariants.Count - 1));
            }
            if (_envVariants.Count == 0)
                return;

            var env = _envVariants[_envVariant];
            string dir = Path.Combine("Resources", StageTemplateSet);
            byte[] envTemplate = ReadResource(Path.Combine(dir, "fp_c5.bin"));
            byte[] user0Template = ReadResource(Path.Combine(dir, "fp_c7.bin"));

            _envReference ??= EnvCatalog
                .Load(_romfs, StageTemplateScene)
                .FirstOrDefault(e => e.Variant == StageTemplateVariant)
                ?.Params;
            Vector4[] ambient = null;
            bool skyRead = false;
            if (_envReference != null && envTemplate != null)
                ambient = SkyAmbient.Compute(
                    _romfs,
                    env.Params,
                    _envReference,
                    EnvUniforms.ReadAmbientRows(envTemplate),
                    out skyRead
                );
            if (ambient == null)
                _envStatus = "Ambient not estimated; default kept.";
            else if (!skyRead && env.Params.SkyEnable && env.Params.SkyActor.Length > 0)
                _envStatus = "Sky unreadable; ambient without it.";

            BfresEditor.HoianNXRender.SetUniformSet(StageTemplateSet);
            BfresEditor.HoianNXRender.EnvironmentOverride = EnvUniforms.BuildEnvironment(
                envTemplate,
                env.Params,
                fog: !env.NamedEnvSet,
                ambientSH: ambient
            );
            BfresEditor.HoianNXRender.User0Override = EnvUniforms.BuildUser0(
                user0Template,
                env.Params,
                env.Night
            );
            ApplyTeamColor();
        }

        /// <summary>The params of the picked stage the lighting comes from, or null for the Viewer dump.</summary>
        EnvParams ActiveSceneParams() =>
            _romfs != null
            && _envSource == EnvSourceStage
            && _envVariants != null
            && _envVariant >= 0
            && _envVariant < _envVariants.Count
                ? _envVariants[_envVariant].Params
                : null;

        static void ClearGeneratedEnv()
        {
            BfresEditor.HoianNXRender.EnvironmentOverride = null;
            BfresEditor.HoianNXRender.User0Override = null;
        }

        static byte[] ReadResource(string path) =>
            File.Exists(path) ? File.ReadAllBytes(path) : null;
    }
}
