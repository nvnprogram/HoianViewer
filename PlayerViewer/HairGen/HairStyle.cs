using System;
using System.Collections.Generic;
using System.Linq;
using OpenTK;

namespace PlayerViewer.HairGen
{
    public enum StrandMotion
    {
        /// <summary>No cloth; the strand keeps its weights and moves with the head.</summary>
        Rigid,

        /// <summary>Falls under gravity and swings inside a range that widens toward the tip, like the stock long strands.</summary>
        Hanging,

        /// <summary>No gravity; springs back to its modelled shape, like the stock puffs, buns and upswept tails.</summary>
        Spring,
    }

    /// <summary>
    /// How one strand moves, in three controls from 0 to 1. Stiffness is how hard it returns to
    /// its styled pose, bounce how long it keeps moving, reach how far a hanging strand may
    /// swing from its pose.
    /// </summary>
    public class StrandStyle
    {
        public string Preset;
        public StrandMotion Motion;
        public float Stiffness;
        public float Bounce;
        public float Reach;

        /// <summary>Strip width as a multiple of the strand's thickness.</summary>
        public float Width = 1;

        /// <summary>
        /// Hanging only: the range opens toward a nearly free tip and the levels sit further
        /// apart, so a strand with little bounce drifts after the head and floats back.
        /// </summary>
        public bool Floaty;

        public StrandStyle Clone() => (StrandStyle)MemberwiseClone();

        public bool SameAs(StrandStyle o) =>
            o != null
            && Motion == o.Motion
            && Stiffness == o.Stiffness
            && Bounce == o.Bounce
            && Reach == o.Reach
            && Width == o.Width
            && Floaty == o.Floaty;
    }

    /// <summary>A named starting point for a strand's style.</summary>
    public record StylePreset(
        string Name,
        string Description,
        StrandMotion Motion,
        float Stiffness,
        float Bounce,
        float Reach,
        bool Floaty = false,
        float Width = 1
    )
    {
        public StrandStyle Style() =>
            new()
            {
                Preset = Name,
                Motion = Motion,
                Stiffness = Stiffness,
                Bounce = Bounce,
                Reach = Reach,
                Width = Width,
                Floaty = Floaty,
            };
    }

    public static class HairStyles
    {
        public static readonly StylePreset[] Presets =
        {
            new(
                "Flowing",
                "Long strands and tentacles that swing freely and settle slowly.",
                StrandMotion.Hanging,
                0.5f,
                0.925f,
                0.9f
            ),
            new(
                "Swingy",
                "Medium locks that swing and come back quickly.",
                StrandMotion.Hanging,
                0.7f,
                0.925f,
                0.7f
            ),
            new(
                "Heavy",
                "Drags behind the head without overshooting, like wet or thick hair.",
                StrandMotion.Hanging,
                0.6f,
                0.0f,
                0.9f
            ),
            new(
                "Floaty",
                "Long hair that drifts after the head and floats back without swinging.",
                StrandMotion.Hanging,
                1.0f,
                0.0f,
                1.0f,
                Floaty: true,
                Width: 1.5f
            ),
            new(
                "Loose",
                "Soft and floppy, with a wide swing.",
                StrandMotion.Hanging,
                0.0f,
                0.6f,
                1.0f
            ),
            new(
                "Bouncy",
                "Puffs, buns and tails that jiggle and spring back to their shape.",
                StrandMotion.Spring,
                0.3f,
                1.0f,
                0
            ),
            new(
                "Springy",
                "Upswept or sideways strands that hold their shape but give a little.",
                StrandMotion.Spring,
                0.6f,
                0.7f,
                0
            ),
            new(
                "Stiff",
                "Short locks and quiffs that barely move.",
                StrandMotion.Spring,
                1.0f,
                0.5f,
                0
            ),
            new("Rigid", "No physics.", StrandMotion.Rigid, 0, 0, 0),
        };

        public static StylePreset Find(string name) => Presets.FirstOrDefault(p => p.Name == name);

        /// <summary>Whether a chain hangs: its root to tip direction within 20 degrees of straight down, as the stock hanging pieces are.</summary>
        public static bool Hangs(HairChain chain) => Tilt(chain) <= 20;

        /// <summary>The angle in degrees between a chain's root to tip direction and straight down.</summary>
        public static float Tilt(HairChain chain)
        {
            var d = chain.Path[^1] - chain.FreeRoot;
            if (d.LengthSquared < 1e-8f)
                return 90;
            return MathHelper.RadiansToDegrees(
                MathF.Acos(Math.Clamp(Vector3.Dot(d.Normalized(), -Vector3.UnitY), -1, 1))
            );
        }

        /// <summary>
        /// A preset for a chain from its shape, following what the stock hairs use for strands
        /// like it. On hair, a hanging strand rooted in front of the head swings less.
        /// </summary>
        public static StrandStyle Suggest(HairChain chain, bool hair = true)
        {
            float length = chain.FreeLength;
            float aspect = length / Math.Max(2 * chain.Thickness, 1e-3f);
            //A strand about as wide as it is long is a sheet or a lump: a two particle strip
            //cannot hold its roll, and one that wide flips over when it swings. A hanging sheet
            //on a soft spring still flips up on a jump.
            string name =
                aspect < 1.2f ? (Tilt(chain) <= 30 ? "Springy" : "Bouncy")
                : aspect < 2f ? "Springy"
                : Hangs(chain) ? (length >= 0.45f ? "Flowing" : "Swingy")
                : length < 0.2f ? "Stiff"
                : "Springy";
            var style = Find(name).Style();
            //Rooted in front, a strand hangs by the face; a wide swing throws it up over it.
            if (hair && style.Motion == StrandMotion.Hanging && InFront(chain))
            {
                style.Reach = Math.Min(style.Reach, 0.5f);
                style.Preset = null;
            }
            return style;
        }

        /// <summary>Whether a hair strand leaves the head in front of it, where it hangs by the face.</summary>
        public static bool InFront(HairChain chain) => chain.FreeRoot.Z > 0.05f;
    }

    /// <summary>The solver values a style resolves to for one chain.</summary>
    public readonly record struct StrandParams(
        bool Gravity,
        float Damping,
        float RootStiffness,
        float TipStiffness,
        float RootRange,
        float TipRange,
        bool Bend,
        string AampPreset,
        bool Floaty = false
    )
    {
        //Floaty: the range fraction of the distance from the root at the root and at the tip, reached
        //along the square of the distance; the stiffness holds to the knee, then eases to the tip's share.
        public const float FloatyRootRange = 0.3f;
        public const float FloatyTipRange = 0.95f;
        public const float FloatyKnee = 0.4f;
        public const float FloatyTipStiffness = 0.5f;

        /// <summary>Level spacing of a floaty strip, wider than the stock spacing the other presets keep.</summary>
        public const float FloatyLevelSpacing = 0.25f;

        /// <summary>Whether a chain takes the floaty ramp: styled floaty and hanging.</summary>
        public static bool Floats(StrandStyle style, HairChain chain) =>
            style.Floaty && style.Motion == StrandMotion.Hanging && HairStyles.Hangs(chain);

        /// <summary>
        /// Reach only applies to a strand that hangs:
        /// a range needs gravity to fall into, and the game runs gravity at full strength or not
        /// at all. Without reach the strand is a spring to its animated pose, whose gain sets the
        /// period; with it, a pendulum held by a soft wall. Damping is spread on a log scale.
        /// </summary>
        public static StrandParams Resolve(
            StrandStyle style,
            HairChain chain,
            int freeLevels,
            bool hair = true
        )
        {
            bool hanging = style.Motion == StrandMotion.Hanging && HairStyles.Hangs(chain);
            float reach = hanging ? Math.Clamp(style.Reach, 0, 1) : 0;
            float s = Math.Clamp(style.Stiffness, 0, 1);
            float kMin = freeLevels <= 1 ? 0.03f : 0.1f;
            float spring = kMin * MathF.Pow(0.6f / kMin, s);
            float range = 0.3f + 0.6f * s;
            float k = spring + (range - spring) * Math.Min(1, reach / 0.5f);
            //Full bounce on a pendulum lets a short strip roll on fast spins.
            float bounce = Math.Clamp(style.Bounce, 0, hanging ? 0.925f : 1);
            float damping = Math.Max(0.001f, 1 - MathF.Pow(10, -4 * (1 - bounce)));
            if (Floats(style, chain))
            {
                //Rooted in front it keeps the tip as tight as any other preset's, clear of the face.
                float tip = FloatyTipRange * reach;
                if (hair && HairStyles.InFront(chain))
                    tip = Math.Min(tip, 0.5f);
                return new StrandParams(
                    true,
                    damping,
                    k,
                    FloatyTipStiffness * k,
                    Math.Min(FloatyRootRange * reach, tip),
                    tip,
                    freeLevels >= 2,
                    "leather",
                    true
                );
            }
            return new StrandParams(
                style.Motion == StrandMotion.Hanging,
                damping,
                k,
                0.7f * k,
                0.4f * reach,
                Math.Min(reach, 0.5f),
                hanging && freeLevels >= 2,
                style.Motion == StrandMotion.Hanging ? "leather" : "SpringGravity0"
            );
        }
    }
}
