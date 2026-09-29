using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BfresEditor;
using EffectLibrary;
using GLFrameworkEngine;
using OpenTK.Graphics.OpenGL;
using PlayerViewer.Effects.Gl;
using PlayerViewer.Effects.Sim;
using PlayerViewer.Env;
using PlayerViewer.Player;
using NVector3 = System.Numerics.Vector3;

namespace PlayerViewer.Effects.Viewer
{
    /// <summary>
    /// Draws the set <see cref="Playback"/> is playing, in the game's pass order: opaque
    /// emitters with their depth prepass in the opaque pass; in the transparent pass the
    /// emitters drawn before the frame buffer copy, the copy of colour and depth, then the rest.
    /// Optionally draws a companion scene (the player) and a ground grid around it.
    /// </summary>
    public sealed class EffectScene : ILayeredScene, IDisposable
    {
        public EffectPlayback Playback { get; } = new();

        /// <summary>Drawn with the effect, or null.</summary>
        public IViewScene Companion;

        public bool ShowGrid = true;

        /// <summary>
        /// The params of the scene the models' lighting was built from, for what the uniform
        /// blocks do not carry; null for the Viewer look.
        /// </summary>
        public Func<EnvParams> SceneParams;

        /// <summary>Emitters the user has hidden.</summary>
        public HashSet<Emitter> Hidden { get; } = new();

        /// <summary>Emitters whose draw threw, with the message; they are skipped from then on.</summary>
        public Dictionary<Emitter, string> Failed { get; } = new();

        EffectRenderer _renderer;
        readonly EffectHostTextures _host = new();
        readonly Dictionary<Emitter, byte[]> _custom1 = new();
        NVector3 _custom1Team;
        byte[] _custom1User0;
        readonly LightCluster _lights = new();
        byte[] _custom2;
        SceneRenderInfo _info;
        EffectGrid _grid;
        Unpremultiply _unpremultiply;

        public EffectScene()
        {
            _host.EnvironmentCubeArray = () =>
                HoianNXRender.EnvCubeArray is { } c
                    ? new GlTexture(c.ID, TextureTarget.TextureCubeMapArray)
                    : null;
        }

        EffectRenderer Renderer => _renderer ??= new EffectRenderer { HostSource = _host.Get };

        /// <summary>Draws the last render skipped because their program was still linking.</summary>
        public int PendingDraws => _renderer?.PendingDraws ?? 0;

        public IEnumerable<BfresRender> AllRenders() =>
            Companion?.AllRenders() ?? Enumerable.Empty<BfresRender>();

        public bool SelfShadow => Companion != null;

        public void BeginRender(in SceneRenderInfo info)
        {
            _info = info;
            Renderer.BeginFrame();
            BuildLights();
        }

        /// <summary>The set's light particles as the game's light table, which the effects read
        /// as Custom2 and the companion's models as gsys_user2.</summary>
        void BuildLights()
        {
            _lights.Reset();
            if (Playback.Instance is { } inst)
                EffectLights.Collect(inst, _lights, e => Draws(e.Def.Source));
            _custom2 = _lights.Build();
            if (Companion != null)
                HoianNXRender.LightClusterOverride = _custom2;
        }

        /// <summary>Drops the GPU side of the previous file, for a file switch.</summary>
        public void ReleaseFileResources()
        {
            _renderer?.Dispose();
            _renderer = null;
            _custom1.Clear();
            Failed.Clear();
            Hidden.Clear();
        }

        /// <summary>The texture an emitter slot binds, for the info panel.</summary>
        public GlTexture? Texture(Emitter emitter, int slot)
        {
            try
            {
                return Renderer.Texture(emitter, slot);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>Whether the emitter is drawn at all: visible in the file, not hidden, and
        /// not a plugin the simulation does not run.</summary>
        public bool Draws(Emitter e) =>
            e.Visible
            && !Hidden.Contains(e)
            && !Failed.ContainsKey(e)
            && (EffectFile.GapsOf(e) & EffectGaps.Stripes) == 0;

        static bool IsOpaque(uint path) => path is 12 or 13 or 17 or 18 or 20 or 21;

        public void Draw(GLContext control, Pass pass)
        {
            if (pass == Pass.OPAQUE)
            {
                //The resolve weights colour by alpha, so a transparent target starts from zero
                //colour: the effect layer is then premultiplied over black.
                if (_info.KeepAlpha)
                    GL.ClearBuffer(ClearBuffer.Color, 0, new float[4]);
                Companion?.Draw(control, pass);
                DrawEmitters(control, Group.Opaque);
            }
            else if (pass == Pass.TRANSPARENT)
            {
                if (Companion != null)
                {
                    if (_info.KeepAlpha)
                        GL.ColorMask(true, true, true, false);
                    Companion.Draw(control, pass);
                    GL.ColorMask(true, true, true, true);
                }
                if (ShowGrid && _info.Viewport)
                {
                    _grid ??= new EffectGrid();
                    _grid.Draw(control.Camera);
                }
                DrawEmitters(control, Group.BeforeCopy);
                if (control.ScreenBuffer != null)
                    _host.CopyFrame(control.ScreenBuffer.ID, _info.Width, _info.Height);
                DrawEmitters(control, Group.AfterCopy);
                if (_info.KeepAlpha && control.ScreenBuffer != null)
                {
                    _unpremultiply ??= new Unpremultiply();
                    _unpremultiply.Apply(control.ScreenBuffer.ID, _info.Width, _info.Height);
                }
            }
        }

        enum Group
        {
            Opaque,
            BeforeCopy,
            AfterCopy,
        }

        static readonly Dictionary<string, byte[]> _dumpedBlocks = new();

        /// <summary>A block of the uniform set the models use, as dumped.</summary>
        static byte[] DumpedBlock(string file)
        {
            string path = Path.Combine("Resources", HoianNXRender.UniformSetDir, file);
            if (!_dumpedBlocks.TryGetValue(path, out var data))
                _dumpedBlocks[path] = data = File.Exists(path) ? File.ReadAllBytes(path) : null;
            return data;
        }

        void DrawEmitters(GLContext control, Group group)
        {
            var inst = Playback.Instance;
            if (inst == null || !inst.Alive && inst.Emitters.Count == 0)
                return;

            var emitters = inst.AllEmitters()
                .Where(e => Draws(e.Def.Source) && !EffectLights.DrawsNothing(e.Def))
                .ToList();
            var chosen = group switch
            {
                Group.Opaque => emitters.Where(e => IsOpaque(e.Def.DrawPath)).ToList(),
                Group.BeforeCopy => emitters.Where(e => e.Def.DrawPath == 0).ToList(),
                _ => emitters
                    .Where(e => e.Def.DrawPath != 0 && !IsOpaque(e.Def.DrawPath))
                    .OrderBy(e => PathOrder(e.Def.DrawPath))
                    .ToList(),
            };
            if (chosen.Count == 0)
                return;

            var renderer = Renderer;
            renderer.CoverageAlpha = _info.KeepAlpha;
            byte[] view = BuildView(control.Camera);
            float offset = Playback.TimeOffset;
            var towardLight = ToNumerics(-HoianNXRender.GetMainLightDir());
            var environment = HoianNXRender.EnvironmentOverride ?? DumpedBlock("fp_c5.bin");
            var user0 = HoianNXRender.User0Override ?? DumpedBlock("fp_c7.bin");
            var scene = SceneParams?.Invoke();
            byte[] custom0 = CustomBlocks.BuildCustom0(
                environment,
                user0,
                scene,
                towardLight,
                NVector3.Zero,
                _info.Width,
                _info.Height,
                (Playback.Frame - offset) / 60f
            );
            _host.UpdateSphereMaps(towardLight, CustomBlocks.HighlightLobe(environment, scene));
            var team = HoianNXRender.TeamAlphaColor;
            if (team != _custom1Team || user0 != _custom1User0)
            {
                _custom1.Clear();
                _custom1Team = team;
                _custom1User0 = user0;
            }

            //Inputs are built once: the opaque group draws each emitter twice.
            var inputs = new List<(EmitterInstance, List<EmitterDrawInputs>)>();
            foreach (var e in chosen)
            {
                List<EmitterDrawInputs> draws;
                try
                {
                    draws = SimDrawInputs.BuildVisible(e, offset, Playback.AgeOffset);
                }
                catch (Exception ex)
                {
                    Fail(e.Def.Source, ex);
                    continue;
                }
                var areaLoop = e.Def.AreaLoop is { } al ? AreaLoop.Blocks(al, e.Srt, view) : null;
                foreach (var d in draws)
                {
                    d.PluginBlocks = areaLoop;
                    d[EffectBlock.View] = view;
                    d[EffectBlock.Custom0] = custom0;
                    d[EffectBlock.Custom1] = Custom1(e.Def.Source, team, user0);
                    d[EffectBlock.Custom2] = _custom2;
                }
                inputs.Add((e, draws));
            }

            if (group == Group.Opaque)
            {
                DrawAll(inputs, EffectPass.DepthPrepass);
                //The colour draws read this frame's scene depth, not the previous frame's copy.
                if (control.ScreenBuffer != null)
                    _host.CopyDepth(control.ScreenBuffer.ID, _info.Width, _info.Height);
                DrawAll(inputs, EffectPass.Opaque);
            }
            else
                DrawAll(inputs, EffectPass.Translucent);
            renderer.EndDraws();
        }

        void DrawAll(List<(EmitterInstance, List<EmitterDrawInputs>)> inputs, EffectPass pass)
        {
            foreach (var (e, draws) in inputs)
            {
                var source = e.Def.Source;
                if (Failed.ContainsKey(source))
                    continue;
                try
                {
                    foreach (var d in draws)
                        Renderer.Draw(source, pass, d);
                }
                catch (Exception ex)
                {
                    Fail(source, ex);
                }
            }
        }

        void Fail(Emitter e, Exception ex)
        {
            if (Failed.TryAdd(e, ex.Message))
                Console.WriteLine($"[Effect] {e.Set.Name}/{e.Name} will not draw: {ex}");
        }

        //Paths drawn after the copy, in the order the game's passes run them.
        static int PathOrder(uint path) =>
            path switch
            {
                1 => 0,
                2 => 1,
                7 => 2,
                8 => 3,
                23 => 4,
                25 => 5,
                19 => 6,
                _ => 7,
            };

        byte[] Custom1(Emitter e, NVector3 team, byte[] user0)
        {
            if (!_custom1.TryGetValue(e, out var block))
                _custom1[e] = block = CustomBlocks.BuildCustom1(e, team, user0);
            return block;
        }

        static byte[] BuildView(Camera camera)
        {
            var view = ToNumerics(camera.ViewMatrix);
            var projection = ToNumerics(camera.ProjectionMatrix);
            System.Numerics.Matrix4x4.Invert(view, out var inverse);
            var eye = new NVector3(inverse.M41, inverse.M42, inverse.M43);
            return ViewBlock.Build(view, projection, eye, camera.ZNear, camera.ZFar, camera.Fov);
        }

        static System.Numerics.Matrix4x4 ToNumerics(OpenTK.Matrix4 m) =>
            new(
                m.M11,
                m.M12,
                m.M13,
                m.M14,
                m.M21,
                m.M22,
                m.M23,
                m.M24,
                m.M31,
                m.M32,
                m.M33,
                m.M34,
                m.M41,
                m.M42,
                m.M43,
                m.M44
            );

        static NVector3 ToNumerics(OpenTK.Vector3 v) => new(v.X, v.Y, v.Z);

        public void Dispose()
        {
            _renderer?.Dispose();
            _renderer = null;
            _host.Dispose();
            _grid?.Dispose();
            _unpremultiply?.Dispose();
        }
    }
}
