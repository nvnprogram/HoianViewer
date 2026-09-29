using System;
using System.Collections.Generic;
using GLFrameworkEngine;
using OpenTK;
using OpenTK.Graphics.OpenGL;
using PlayerViewer.Player;

namespace PlayerViewer.UI
{
    /// <summary>
    /// Tints the visible surface of the scene's meshes by the bones they are weighted to, blended
    /// by the vertex weights and multiplied into the resolved image. Bones without a tint add
    /// nothing, so the tint fades where a strand's weight gives way to the head.
    /// </summary>
    sealed class WeightOverlay : IDisposable
    {
        //The length of the bones and tints arrays in WeightTint.vert.
        const int TintBones = 170;

        ShaderProgram _shader;

        /// <summary>Draws into the bound display framebuffer. The camera must still hold the matrices the scene was drawn with.</summary>
        public void Draw(
            GLContext context,
            IViewScene scene,
            IReadOnlyDictionary<string, Vector4> tints,
            DepthTexture sceneDepth,
            int scale,
            float strength
        )
        {
            _shader ??= new ShaderProgram(
                new FragmentShader(UiShaders.Load("WeightTint.frag")),
                new VertexShader(UiShaders.Load("WeightTint.vert"))
            );
            OverlayPass.Begin(context, _shader, sceneDepth, scale, blend: true);
            var mtxCam = context.Camera.ViewProjectionMatrix;
            _shader.SetMatrix4x4("mtxCam", ref mtxCam);
            _shader.SetFloat("uStrength", strength);

            int tintLocation = GL.GetUniformLocation(_shader.program, "tints");
            ScenePipeline.DrawSceneMeshes(
                scene,
                () => _shader,
                (model, mesh) =>
                    mesh.IsVisible && model.ModelData.Skeleton.Bones[mesh.BoneIndex].Visible,
                model =>
                {
                    var bones = model.ModelData.Skeleton.Bones;
                    var table = new float[Math.Min(bones.Count, TintBones) * 4];
                    bool any = false;
                    for (int i = 0; i < table.Length / 4; i++)
                        if (tints.TryGetValue(bones[i].Name, out var t))
                        {
                            table[i * 4] = t.X;
                            table[i * 4 + 1] = t.Y;
                            table[i * 4 + 2] = t.Z;
                            table[i * 4 + 3] = t.W;
                            any = true;
                        }
                    if (any)
                        GL.Uniform4(tintLocation, table.Length / 4, table);
                    return any;
                },
                mesh => _shader.SetInt("RigidBone", mesh.BoneIndex)
            );
            OverlayPass.End();
        }

        public void Dispose()
        {
            _shader?.Dispose();
            _shader = null;
        }
    }
}
