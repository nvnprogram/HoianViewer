using System;
using OpenTK;

namespace PlayerViewer.Physics
{
    /// <summary>
    /// A test motion for the model's root, so a cloth on a model with no animation still has
    /// something to react to. The pose is a pure function of time.
    /// </summary>
    public class ClothMotion
    {
        public enum Kind
        {
            None,
            Sway,
            Nod,
            Turn,
            Bounce,
            Circle,
        }

        public static readonly string[] Labels = Enum.GetNames<Kind>();

        public Kind Mode = Kind.Sway;
        public float Amount = 0.5f;
        public float Speed = 1.0f;
        public bool Paused;

        float _time;

        public void Advance(float dt)
        {
            if (!Paused)
                _time += dt * Speed;
        }

        /// <summary>Back to the start of the motion.</summary>
        public void Reset() => _time = 0;

        public Matrix4 Current()
        {
            float s = MathF.Sin(_time * MathF.PI);
            float a = Amount;
            return Mode switch
            {
                Kind.Sway => Matrix4.CreateTranslation(0.4f * a * s, 0, 0),
                Kind.Nod => Matrix4.CreateRotationX(0.6f * a * s),
                Kind.Turn => Matrix4.CreateRotationY(1.2f * a * s),
                Kind.Bounce => Matrix4.CreateTranslation(0, 0.3f * a * MathF.Abs(s), 0),
                Kind.Circle => Matrix4.CreateTranslation(
                    0.3f * a * MathF.Cos(_time * MathF.PI),
                    0,
                    0.3f * a * MathF.Sin(_time * MathF.PI)
                ),
                _ => Matrix4.Identity,
            };
        }
    }
}
