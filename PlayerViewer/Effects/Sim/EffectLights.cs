using System;
using System.Buffers.Binary;
using System.Numerics;
using PlayerViewer.Env;

namespace PlayerViewer.Effects.Sim
{
    /// <summary>The CADP payload of a light action emitter.</summary>
    public sealed class LightActionParams
    {
        /// <summary>Bit 2 makes a spot light; bit 4 takes the cone from the particle's scale.</summary>
        public uint Flags;
        public float Falloff;

        /// <summary>The cone's full angle in degrees, when it is not taken from the scale.</summary>
        public float ConeAngle;
        public float SpotExponent;

        public bool Spot => (Flags & 4) != 0;
        public bool ConeFromScale => (Flags & 0x10) != 0;

        public static LightActionParams Read(ReadOnlySpan<byte> p) =>
            new()
            {
                Flags = BinaryPrimitives.ReadUInt32LittleEndian(p),
                Falloff = BinaryPrimitives.ReadSingleLittleEndian(p[4..]),
                ConeAngle = BinaryPrimitives.ReadSingleLittleEndian(p[12..]),
                SpotExponent = BinaryPrimitives.ReadSingleLittleEndian(p[16..]),
            };
    }

    /// <summary>
    /// The lights the game's light action makes of particles. Such an emitter draws nothing; each
    /// of its particles is a point or spot light at the particle, coloured by colour 0 and sized
    /// by its scale.
    /// </summary>
    public static class EffectLights
    {
        /// <summary>The custom action index of the light callback set.</summary>
        public const int LightAction = 1;

        /// <summary>The radial blur callback set; it drives a post effect and draws nothing.</summary>
        public const int RadialBlurAction = 3;

        /// <summary>Whether the game skips drawing the emitter's particles.</summary>
        public static bool DrawsNothing(EmitterDef d) =>
            d.CustomAction is LightAction or RadialBlurAction;

        /// <summary>Adds every live light particle of the set, in emitter order.</summary>
        public static void Collect(
            EmitterSetInstance set,
            LightCluster cluster,
            Func<EmitterInstance, bool> include = null
        )
        {
            foreach (var e in set.AllEmitters())
                if (e.Def.Light != null && (include == null || include(e)))
                    Collect(e, cluster);
        }

        static void Collect(EmitterInstance e, LightCluster cluster)
        {
            var d = e.Def;
            var light = d.Light;
            var ps = e.Particles;
            if (ps == null)
                return;
            var animColor = e.AnimValues[(int)EmitterAnim.Color0];
            float animAlpha = e.AnimValues[(int)EmitterAnim.Alpha0].X;
            var setColor = e.Set.Color;
            var emitterScale = e.FadeScale * e.Set.ParticleScaleForCalc;
            for (int slot = 0; slot < ps.Capacity; slot++)
            {
                if (!ps.IsAlive(slot, e.Time))
                    continue;
                float age = e.Time - ps.CreateTime[slot];
                float life = ps.Life[slot];
                var random = ps.Get(ParticleStream.Random, slot);
                var c = ParticleAnim.EmitterColor(
                    d,
                    0,
                    random,
                    e.ColorScale0,
                    animColor,
                    animAlpha,
                    life,
                    age
                );
                float intensity = c.W * setColor.W * e.FadeAlpha;
                var color = new Vector4(
                    c.X * setColor.X * intensity,
                    c.Y * setColor.Y * intensity,
                    c.Z * setColor.Z * intensity,
                    c.W
                );
                var scale =
                    ParticleAnim.Scale(d, ps.Get3(ParticleStream.Scale, slot), random, life, age)
                    * emitterScale;
                var position = e.Srt.Transform(ps.Get3(ParticleStream.LocalPos, slot));
                float range = scale.Y * 0.5f;
                if (!light.Spot)
                {
                    cluster.AddPoint(position, color, range, light.Falloff);
                    continue;
                }
                float halfAngle;
                if (light.ConeFromScale)
                {
                    range = scale.Y;
                    halfAngle = MathF.Atan2(scale.X * 0.5f, scale.Y);
                }
                else
                    halfAngle = light.ConeAngle * (MathF.PI / 360f);
                var r = ParticleAnim.Rotation(
                    d,
                    ps.Get(ParticleStream.InitRotate, slot),
                    random,
                    age
                );
                cluster.AddSpot(
                    position,
                    e.Rt.TransformNormal(Forward(r)),
                    color,
                    range,
                    light.Falloff,
                    halfAngle,
                    light.SpotExponent
                );
            }
        }

        //Local +z turned by the particle's rotation, z then y then x.
        static Vector3 Forward(Vector3 r)
        {
            float sx = MathF.Sin(r.X),
                cx = MathF.Cos(r.X),
                sy = MathF.Sin(r.Y),
                cy = MathF.Cos(r.Y),
                sz = MathF.Sin(r.Z),
                cz = MathF.Cos(r.Z);
            return new Vector3(cz * sy * cx + sz * sx, sz * sy * cx - cz * sx, cy * cx);
        }
    }
}
