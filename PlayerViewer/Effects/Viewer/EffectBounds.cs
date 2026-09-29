using System;
using System.Collections.Generic;
using System.Numerics;
using EffectLibrary;
using PlayerViewer.Effects.Sim;

namespace PlayerViewer.Effects.Viewer
{
    /// <summary>
    /// Where a set's particles go, for framing the camera: a dry run of the simulation sampled
    /// every few frames. A GPU emitter's particles are placed with the vertex shader's closed
    /// form for velocity, air resistance and gravity; fields are ignored.
    /// </summary>
    public static class EffectBounds
    {
        //A particle counts only this young. Debris thrown for seconds (a break effect's pieces go
        //tens of units) would otherwise frame the set as a speck.
        const float MaxAge = 30;

        /// <summary>A sphere (centre, radius) holding nearly every sampled particle.</summary>
        public static Vector4 Measure(
            EmitterSet set,
            in Matrix34 matrix,
            int frames,
            Func<Emitter, ulong, IEmissionPrimitive> primitives
        )
        {
            var points = new List<Vector3>();
            float size = 0;
            int sizes = 0;
            try
            {
                var sim = new EffectSimulation { PrimitiveSource = primitives };
                var inst = sim.Play(set, matrix);
                for (int f = 0; f < frames && inst.Alive && points.Count < 20000; f++)
                {
                    sim.Calculate(1);
                    if (f % 4 != 0)
                        continue;
                    foreach (var e in inst.AllEmitters())
                        Sample(e, points, ref size, ref sizes);
                }
            }
            catch (Exception)
            {
                //A set that will not simulate still gets a default framing.
            }
            if (points.Count == 0)
                return new Vector4(matrix.T + new Vector3(0, 0.5f, 0), 1.5f);

            //Percentiles per axis, so a few particles thrown far do not shrink everything else.
            Vector3 lo = Percentile(points, 0.02f),
                hi = Percentile(points, 0.98f);
            var centre = (lo + hi) * 0.5f;
            float radius = (hi - lo).Length() * 0.5f;
            radius += sizes > 0 ? size / sizes : 0;
            return new Vector4(centre, MathF.Max(radius, 0.25f));
        }

        static void Sample(EmitterInstance e, List<Vector3> points, ref float size, ref int sizes)
        {
            var ps = e.Particles;
            if (ps == null || EffectLights.DrawsNothing(e.Def))
                return;
            var pos = ps[ParticleStream.LocalPos];
            var vec = ps[ParticleStream.LocalVec];
            var scale = ps[ParticleStream.Scale];
            bool cpu = e.Def.CalcType == EmitterCalcType.Cpu || e.CpuFallback;
            for (int slot = 0; slot < ps.Capacity; slot++)
            {
                float age = e.Time - ps.CreateTime[slot];
                if (!ps.IsAlive(slot, e.Time) || age > MaxAge)
                    continue;
                var p = new Vector3(pos[slot * 4], pos[slot * 4 + 1], pos[slot * 4 + 2]);
                if (!cpu && vec != null)
                {
                    var v = new Vector3(vec[slot * 4], vec[slot * 4 + 1], vec[slot * 4 + 2]);
                    float dyn = scale != null ? scale[slot * 4 + 3] : 1;
                    p += VectorFromTime(e, v, age) * dyn;
                }
                var world = e.Srt.Transform(p);
                if (float.IsFinite(world.X) && float.IsFinite(world.Y) && float.IsFinite(world.Z))
                    points.Add(world);
                if (scale != null)
                {
                    size += MathF.Max(MathF.Abs(scale[slot * 4]), MathF.Abs(scale[slot * 4 + 1]));
                    sizes++;
                }
            }
        }

        //The particle vertex shader's closed form for a GPU particle's travel after t frames:
        //velocity decayed by air resistance plus gravity integrated under the same decay.
        static Vector3 VectorFromTime(EmitterInstance e, Vector3 v, float t)
        {
            float k = e.Def.AirRegist;
            bool constant = k == 1 || !(k > 0);
            float travelTime = constant ? t : (1 - MathF.Pow(k, t)) / (1 - k);
            float gravTime = constant
                ? t * t * 0.5f
                : (t - (MathF.Pow(k, t) - 1) / MathF.Log(k)) / (1 - k);
            var g = e.Def.GravityAxis * e.AnimValues[(int)EmitterAnim.GravityScale].X;
            if (e.Def.WorldGravity)
                g = new Vector3(
                    VfxMath.Dot(e.Rt.R0, g),
                    VfxMath.Dot(e.Rt.R1, g),
                    VfxMath.Dot(e.Rt.R2, g)
                );
            return v * travelTime + g * gravTime;
        }

        static Vector3 Percentile(List<Vector3> points, float q)
        {
            var xs = new float[points.Count];
            var ys = new float[points.Count];
            var zs = new float[points.Count];
            for (int i = 0; i < points.Count; i++)
                (xs[i], ys[i], zs[i]) = (points[i].X, points[i].Y, points[i].Z);
            Array.Sort(xs);
            Array.Sort(ys);
            Array.Sort(zs);
            int k = Math.Clamp((int)(q * (points.Count - 1)), 0, points.Count - 1);
            return new Vector3(xs[k], ys[k], zs[k]);
        }
    }
}
