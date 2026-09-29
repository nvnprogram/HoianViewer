using GLFrameworkEngine;
using OpenTK;
using PlayerViewer.HairGen;
using Vector2 = System.Numerics.Vector2;

namespace PlayerViewer.UI
{
    public partial class ViewerWindow
    {
        /// <summary>The viewport image's placement, so world points can be put on it and the mouse cast into it.</summary>
        struct ViewMap
        {
            public Matrix4 ViewProjection;
            public Vector2 Pos,
                Size;
            public float U0,
                U1;

            public static ViewMap Of(
                Camera camera,
                Vector2 pos,
                Vector2 size,
                Vector2 uv0,
                Vector2 uv1
            ) =>
                new()
                {
                    ViewProjection = camera.ViewProjectionMatrix,
                    Pos = pos,
                    Size = size,
                    U0 = uv0.X,
                    U1 = uv1.X,
                };

            public bool Project(Vector3 world, out Vector2 screen)
            {
                var c = new Vector4(world, 1) * ViewProjection;
                screen = default;
                if (c.W <= 1e-5f)
                    return false;
                float u = c.X / c.W * 0.5f + 0.5f;
                float v = c.Y / c.W * 0.5f + 0.5f;
                screen = new Vector2(
                    Pos.X + (u - U0) / (U1 - U0) * Size.X,
                    Pos.Y + (1 - v) * Size.Y
                );
                return true;
            }

            /// <summary>The mouse ray in the space <paramref name="frame"/> takes to the world, with a unit direction.</summary>
            public bool Ray(Matrix4 frame, Vector2 mouse, out Vector3 origin, out Vector3 direction)
            {
                origin = direction = default;
                if (Size.X <= 0 || Size.Y <= 0)
                    return false;
                float u = U0 + (mouse.X - Pos.X) / Size.X * (U1 - U0);
                float v = 1 - (mouse.Y - Pos.Y) / Size.Y;
                return LimbBrush.ClipRay(
                    frame * ViewProjection,
                    u * 2 - 1,
                    v * 2 - 1,
                    out origin,
                    out direction
                );
            }
        }
    }
}
