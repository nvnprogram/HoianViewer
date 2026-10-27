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
            if (_romfs?.Game == GameKind.Spiral)
            {
                DrawRaidersEnvRows();
                return;
            }

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

            int picked = VariantRow(_envVariants.Select(e => e.Variant).ToList(), _envVariant);
            if (picked >= 0)
            {
                _envVariant = picked;
                ApplyStageEnv();
            }
            if (_envStatus != null)
                Widgets.DimText(_envStatus);
        }

        //The Time row: a button per variant, wrapping. Returns the one clicked, else -1.
        int VariantRow(IReadOnlyList<string> variants, int current)
        {
            int clicked = -1;
            Widgets.LabeledRow(
                "Time",
                () =>
                {
                    float left = ImGui.GetCursorPosX();
                    float right = ImGui.GetWindowContentRegionMax().X;
                    float padding = ImGui.GetStyle().FramePadding.X * 2;
                    for (int i = 0; i < variants.Count; i++)
                    {
                        //Coop stages have four variants, which do not fit on one line.
                        if (i > 0)
                        {
                            float width = ImGui.CalcTextSize(variants[i]).X + padding;
                            ImGui.SameLine();
                            if (ImGui.GetCursorPosX() + width > right)
                            {
                                ImGui.NewLine();
                                ImGui.SetCursorPosX(left);
                            }
                        }
                        bool active = i == current;
                        bool sideOrder = SideOrderControls.On;
                        if (active && !sideOrder)
                            ImGui.PushStyleColor(
                                ImGuiCol.Button,
                                ImGui.GetStyle().Colors[(int)ImGuiCol.ButtonActive]
                            );
                        if (Widgets.Button(variants[i], default, active) && !active)
                            clicked = i;
                        if (active && !sideOrder)
                            ImGui.PopStyleColor();
                    }
                }
            );
            Widgets.ItemTooltip("Time of day");
            if (clicked >= 0)
            {
                _envVariantPicks.Remove(variants[clicked]);
                _envVariantPicks.Insert(0, variants[clicked]);
            }
            return clicked;
        }

        //Raiders: the Stage list is its scenes, each lit by its own rendering param group.
        System.Threading.Tasks.Task<List<RaidersScene>> _raidersScenesTask;
        Romfs _raidersScenesRomfs;
        List<RaidersScene> _raidersScenes;
        List<string> _raidersSceneNames;
        string _raidersScene = RaidersEnv.DefaultScene;
        int _raidersVariant;
        string _raidersEnvGroup = RaidersEnv.DefaultScene;
        string _raidersEnvStatus;

        void DrawRaidersEnvRows()
        {
            if (_raidersScenesRomfs != _romfs)
            {
                var romfs = _romfs;
                _raidersScenesRomfs = romfs;
                _raidersScenes = null;
                _raidersScenesTask = System.Threading.Tasks.Task.Run(
                    () => RaidersEnvCatalog.ListScenes(romfs)
                );
            }
            if (_raidersScenes == null)
            {
                if (!_raidersScenesTask.IsCompleted)
                {
                    Widgets.LabeledRow("Stage", () => Widgets.DimText("Reading stages..."));
                    return;
                }
                _raidersScenes = _raidersScenesTask.IsCompletedSuccessfully
                    ? _raidersScenesTask.Result
                    : new List<RaidersScene>();
                _raidersSceneNames = _raidersScenes.Select(x => x.Name).ToList();
            }

            Widgets.LabeledRow(
                "Stage",
                () =>
                {
                    if (
                        Widgets.StringCombo(
                            "##raidersscene",
                            _raidersScene,
                            _raidersSceneNames,
                            out var picked
                        )
                        && picked != null
                        && picked != _raidersScene
                    )
                    {
                        _raidersScene = picked;
                        var variants = RaidersSceneVariants();
                        _raidersVariant = Math.Max(
                            0,
                            _envVariantPicks
                                .Select(name => variants.IndexOf(name))
                                .FirstOrDefault(i => i >= 0)
                        );
                        ApplyRaidersEnv();
                    }
                }
            );
            Widgets.ItemTooltip("The scene the lighting comes from");

            var names = RaidersSceneVariants();
            if (names.Count > 1)
            {
                int picked = VariantRow(names, _raidersVariant);
                if (picked >= 0)
                {
                    _raidersVariant = picked;
                    ApplyRaidersEnv();
                }
            }
            if (_raidersEnvStatus != null)
                Widgets.DimText(_raidersEnvStatus);
        }

        List<string> RaidersSceneVariants() =>
            _raidersScenes
                ?.FirstOrDefault(x => x.Name == _raidersScene)
                ?.Variants.Select(v => v.Variant)
                .ToList() ?? new List<string>();

        void ApplyRaidersEnv()
        {
            var scene = _raidersScenes?.FirstOrDefault(x => x.Name == _raidersScene);
            if (scene == null || _romfs == null)
                return;
            _raidersVariant = Math.Clamp(_raidersVariant, 0, scene.Variants.Count - 1);
            string group = scene.Variants[_raidersVariant].Group;
            var frame = LoadRaidersFrame(_romfs, group);
            if (frame == null)
            {
                _raidersEnvStatus = "No lighting for this stage.";
                return;
            }
            (BfresEditor.HoianNXRender.FrameSource as IDisposable)?.Dispose();
            BfresEditor.HoianNXRender.FrameSource = frame;
            _raidersEnvGroup = group;
            _raidersEnvStatus = frame.Estimated
                ? "Ambient and reflections estimated from the sky."
                : null;
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
            BfresEditor.ThunderFrame.Instance.EnvironmentOverride = EnvUniforms.BuildEnvironment(
                envTemplate,
                env.Params,
                fog: !env.NamedEnvSet,
                ambientSH: ambient
            );
            BfresEditor.ThunderFrame.Instance.User0Override = EnvUniforms.BuildUser0(
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
            BfresEditor.ThunderFrame.Instance.EnvironmentOverride = null;
            BfresEditor.ThunderFrame.Instance.User0Override = null;
        }

        static PlayerViewer.Env.RaidersFrame LoadRaidersFrame(
            Romfs romfs,
            string group = RaidersEnv.DefaultScene
        )
        {
            try
            {
                var frame = PlayerViewer.Env.RaidersFrame.Load(romfs, group);
                if (frame == null)
                    Console.WriteLine("[Env] Raiders lighting not found; using Splatoon 3's.");
                else if (frame.AmbientSH == null)
                    Console.WriteLine("[Env] Raiders env map unreadable; ambient left black.");
                return frame;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Env] Raiders lighting failed: {ex.Message}");
                return null;
            }
        }

        static byte[] ReadResource(string path) =>
            File.Exists(path) ? File.ReadAllBytes(path) : null;
    }
}
