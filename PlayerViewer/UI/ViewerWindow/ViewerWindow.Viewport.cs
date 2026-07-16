using System;
using ImGuiNET;
using OpenTK;
using OpenTK.Input;
using PlayerViewer.Player;
using Vector2 = System.Numerics.Vector2;
using Vector4 = System.Numerics.Vector4;

namespace PlayerViewer.UI
{
    // Center viewport: renders the active scene and handles orbit/pan/zoom camera input.
    public partial class ViewerWindow
    {
        //--- viewport camera input
        bool _viewportHovered;
        bool _mouseDown;

        IViewScene ActiveScene => _standalone != null ? _standalone : _scene;

        void DrawViewport()
        {
            var size = ImGui.GetContentRegionAvail();

            //Freeze the render size while exporting: the deterministic export (and the trim
            //path's temp buffer) assume every frame shares fixed dimensions.
            if (!_animExporting)
                _pipeline.Resize((int)size.X, (int)size.Y);

            //During an export the capture pass already renders each frame (often supersampled),
            // so skip the redundant viewport render and just preview the last captured frame.
            if (!_animExporting)
            {
                UpdateBackgroundPreview();
                _pipeline.Render(ActiveScene);
            }

            var pos = ImGui.GetCursorScreenPos();
            //Fit the render texture into the region, preserving aspect. Normally its already
            //region-sized (1:1); during a supersampled export its larger, so scale it down
            //instead of overflowing the region so it doesn't "zoom"
            float fit = Math.Min(size.X / _pipeline.Width, size.Y / _pipeline.Height);
            var imgSize = new Vector2(_pipeline.Width * fit, _pipeline.Height * fit);
            ImGui.Image(
                (IntPtr)_pipeline.ViewportTextureId,
                imgSize,
                new Vector2(0, 1),
                new Vector2(1, 0)
            );

            _viewportHovered = ImGui.IsItemHovered();
            //Freeze the camera during a full-animation export so every frame shares
            //the exact same viewpoint.
            if (!_animExporting)
                UpdateCameraInput(pos);
        }

        void UpdateCameraInput(Vector2 viewportScreenPos)
        {
            var io = ImGui.GetIO();
            var cam = _pipeline.Camera;
            bool changed = false;

            bool leftDown = ImGui.IsMouseDown(ImGuiMouseButton.Left);
            bool rightDown = ImGui.IsMouseDown(ImGuiMouseButton.Right);
            bool midDown = ImGui.IsMouseDown(ImGuiMouseButton.Middle);
            bool anyDown = leftDown || rightDown || midDown;

            //Drags only start inside the viewport, but keep tracking outside it.
            if (_viewportHovered && anyDown && !_mouseDown)
                _mouseDown = true;
            if (!anyDown)
                _mouseDown = false;

            if (_mouseDown)
            {
                var delta = io.MouseDelta;
                if (midDown || (leftDown && io.KeyShift))
                {
                    //Pan, scaled so the model roughly follows the cursor.
                    float scale = (float)Math.Sin(cam.Fov) * cam.TargetDistance;
                    float dx = -delta.X / Math.Max(1, cam.Width) * scale;
                    float dy = delta.Y / Math.Max(1, cam.Height) * scale;
                    var rot = cam.InverseRotationMatrix;
                    cam.TargetPosition += rot.Row0 * dx + rot.Row1 * dy;
                    changed = true;
                }
                else if (leftDown || rightDown)
                {
                    //Orbit around the target.
                    cam.RotationY += delta.X * 0.008f;
                    cam.RotationX += delta.Y * 0.008f;
                    cam.RotationX = MathHelper.Clamp(
                        cam.RotationX,
                        -MathHelper.PiOver2 + 0.01f,
                        MathHelper.PiOver2 - 0.01f
                    );
                    changed = true;
                }
            }

            if (_viewportHovered && io.MouseWheel != 0)
            {
                cam.TargetDistance = Math.Max(
                    0.05f,
                    cam.TargetDistance * (1.0f - io.MouseWheel * 0.12f)
                );
                changed = true;
            }

            //WASD pans in camera space (W/S = forward/back, A/D = left/right).
            if (!io.WantTextInput && Focused)
            {
                var kb = Keyboard.GetState();
                float move = cam.TargetDistance * io.DeltaTime;
                var dir = OpenTK.Vector3.Zero;
                var rot = cam.InverseRotationMatrix;
                if (kb.IsKeyDown(Key.W))
                    dir -= rot.Row2;
                if (kb.IsKeyDown(Key.S))
                    dir += rot.Row2;
                if (kb.IsKeyDown(Key.A))
                    dir -= rot.Row0;
                if (kb.IsKeyDown(Key.D))
                    dir += rot.Row0;
                if (kb.IsKeyDown(Key.Space))
                    dir += rot.Row1;
                if (kb.IsKeyDown(Key.ShiftLeft) || kb.IsKeyDown(Key.ShiftRight))
                    dir -= rot.Row1;
                if (dir != OpenTK.Vector3.Zero)
                {
                    cam.TargetPosition += dir * move;
                    changed = true;
                }
            }

            if (changed)
                cam.UpdateMatrices();
        }
    }
}
