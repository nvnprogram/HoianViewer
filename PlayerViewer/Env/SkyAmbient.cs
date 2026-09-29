using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using BCnEncoder.Decoder;
using BCnEncoder.Shared;
using BfresLibrary;
using BfresLibrary.Helpers;
using PlayerViewer.Core;
using PlayerViewer.Core.Formats;

namespace PlayerViewer.Env
{
    /// <summary>
    /// The ambient SH for a scene's params. The game computes it on the GPU from a capture of the
    /// whole stage, which cannot be reproduced here(yet), so this estimates how bright and what colour
    /// that capture is and lays the estimate over the shape of a dumped in-stage SH.
    /// The estimate: the upper hemisphere is the sky model's emission as the environment map sees
    /// it, and the lower one stands in for the stage as a grey ground lit by the main light and
    /// the sky.
    /// </summary>
    public static class SkyAmbient
    {
        /// <summary>Albedo of the stand-in ground, fitted to the AutoWalk dump's downward term.</summary>
        public const float GroundAlbedo = 0.1f;

        //Mip of the sky panorama sampled; 512x128 for the stock skies, plenty for order 2 SH.
        const int SampleMip = 3;
        const int Subdivide = 8;

        static readonly Dictionary<string, List<(Vector3, Vector3)>> _skyCache = new(
            StringComparer.OrdinalIgnoreCase
        );

        /// <summary>
        /// The seven packed rows: <paramref name="referenceRows"/>, the dumped SH of the scene
        /// <paramref name="reference"/> describes, scaled per channel by how much brighter this
        /// scene's estimate is than the reference's. A sky that cannot be read counts as black
        /// and clears <paramref name="skyRead"/>; null when the reference's own sky cannot be read.
        /// </summary>
        public static Vector4[] Compute(
            Romfs romfs,
            EnvParams p,
            EnvParams reference,
            Vector4[] referenceRows,
            out bool skyRead
        )
        {
            var estimate = Estimate(romfs, p, out skyRead);
            var baseline = Estimate(romfs, reference, out bool baselineRead);
            if (!baselineRead)
                return null;

            var scale = Mean(estimate) / Vector3.Max(Mean(baseline), new Vector3(1e-6f));
            var rows = new Vector4[7];
            for (int ch = 0; ch < 3; ch++)
            {
                float k =
                    ch == 0 ? scale.X
                    : ch == 1 ? scale.Y
                    : scale.Z;
                rows[ch] = referenceRows[ch] * k;
                rows[3 + ch] = referenceRows[3 + ch] * k;
            }
            rows[6] = new Vector4(
                referenceRows[6].X * scale.X,
                referenceRows[6].Y * scale.Y,
                referenceRows[6].Z * scale.Z,
                referenceRows[6].W
            );
            return rows;
        }

        /// <summary>The sky and ground estimate alone, packed. A sky that cannot be read counts as black.</summary>
        public static Vector4[] Estimate(Romfs romfs, EnvParams p, out bool skyRead)
        {
            var sky = SkyRadianceSH(romfs, p);
            skyRead = sky != null;
            sky ??= new Vector3[9];

            //Irradiance on a level ground from the sky above it, over pi, is the SH evaluated
            //straight up.
            var skyUp = Evaluate(Pack(sky), Vector3.UnitY);
            float sunElevation = MathF.Max(0, -p.MainLightDirection.Y);
            var sun =
                new Vector3(p.MainLightColor.X, p.MainLightColor.Y, p.MainLightColor.Z)
                * p.MainLightIntens
                * sunElevation
                / MathF.PI;
            var ground = GroundAlbedo * (sun + skyUp);

            var sh = (Vector3[])sky.Clone();
            AddLowerHemisphere(sh, ground);
            return Pack(sh);
        }

        /// <summary>The average over all directions of what packed rows evaluate to.</summary>
        public static Vector3 Mean(Vector4[] rows) =>
            new Vector3(rows[0].W, rows[1].W, rows[2].W)
            + new Vector3(rows[3].Z, rows[4].Z, rows[5].Z) / 3;

        /// <summary>Radiance SH of the sky's upper hemisphere, with the params' env map adjustments.</summary>
        static Vector3[] SkyRadianceSH(Romfs romfs, EnvParams p)
        {
            var sh = new Vector3[9];
            if (!p.SkyEnable || string.IsNullOrEmpty(p.SkyActor))
                return sh;

            string actor = Path.GetFileName(p.SkyActor);
            int dot = actor.IndexOf('.');
            if (dot > 0)
                actor = actor.Substring(0, dot);

            if (!_skyCache.TryGetValue(actor, out var samples))
            {
                samples = SampleSkyModel(romfs, SkyModelName(romfs, actor));
                _skyCache[actor] = samples;
            }
            if (samples == null)
                return null;

            float rad = p.SkyRotate * (MathF.PI / 180f);
            float cr = MathF.Cos(rad),
                sr = MathF.Sin(rad);
            var lumaWeights = new Vector3(0.2126f, 0.7152f, 0.0722f);
            foreach (var (dir, radiance) in samples)
            {
                var d = new Vector3(cr * dir.X + sr * dir.Z, dir.Y, -sr * dir.X + cr * dir.Z);
                float luma = Vector3.Dot(radiance, lumaWeights);
                var c = Vector3.Lerp(new Vector3(luma), radiance, p.SkySaturationInEnvMap);
                Accumulate(sh, d, Vector3.Max(Vector3.Zero, c) * p.SkyEmissionIntensInEnvMap);
            }
            return sh;
        }

        //A sky actor names its model through its ModelInfo component, which does not always
        //match the actor name.
        static string SkyModelName(Romfs romfs, string actor)
        {
            var pack = romfs.GetActorPack(actor);
            string info = pack?.FindFile(n =>
                n.StartsWith("Component/ModelInfo/") && n.EndsWith(".bgyml")
            );
            if (info != null)
            {
                var root = Byml.AsHash(new Byml(pack.GetFile(info)).Root);
                string fmdb = Byml.GetString(root, "Fmdb");
                if (fmdb.Length > 0)
                    return Path.GetFileNameWithoutExtension(fmdb);
            }
            return actor;
        }

        /// <summary>
        /// The sky dome's upper half as (direction, radiance times solid angle) samples, or null
        /// when the model is missing or its sky is not an HDR emission panorama.
        /// </summary>
        static List<(Vector3, Vector3)> SampleSkyModel(Romfs romfs, string modelName)
        {
            var data = romfs.ReadModel(modelName);
            if (data == null)
                return null;
            try
            {
                var res = new ResFile(new MemoryStream(data));
                var model = res.Models.Values.FirstOrDefault();
                var shape = model
                    ?.Shapes.Values.OrderByDescending(s =>
                        model.VertexBuffers[s.VertexBufferIndex].VertexCount
                    )
                    .FirstOrDefault();
                if (shape == null)
                    return null;
                var material = model.Materials[shape.MaterialIndex];
                if (
                    material.TextureRefs.Count == 0
                    || !res.Textures.TryGetValue(material.TextureRefs[0].Name, out var tex)
                    || tex is not BfresLibrary.Switch.SwitchTexture switchTex
                )
                    return null;

                var texture = new BfresEditor.BntxTexture(switchTex.BntxFile, switchTex.Texture);
                if (texture.Platform.OutputFormat != Toolbox.Core.TexFormat.BC6H_UF16)
                    return null;
                int mip = Math.Min(SampleMip, (int)texture.MipCount - 1);
                int w = Math.Max(1, (int)texture.Width >> mip);
                int h = Math.Max(1, (int)texture.Height >> mip);
                int aw = (w + 3) / 4 * 4;
                int ah = (h + 3) / 4 * 4;
                var pixels = new BcDecoder().DecodeRawHdr(
                    texture.GetDeswizzledSurface(0, mip),
                    aw,
                    ah,
                    CompressionFormat.Bc6U
                );

                var tint = Vector3.One;
                if (
                    material.ShaderParams.TryGetValue("emission_color", out var ec)
                    && ec.DataValue is float[] c
                    && c.Length >= 3
                )
                    tint = new Vector3(c[0], c[1], c[2]);
                if (
                    material.ShaderParams.TryGetValue("emission_intensity", out var ei)
                    && ei.DataValue is float intensity
                )
                    tint *= intensity;

                var vb = new VertexBufferHelper(model.VertexBuffers[shape.VertexBufferIndex]);
                var pos = vb["_p0"].Data;
                var uvs = vb["_u0"].Data;
                var idx = shape.Meshes[0].GetIndices().Select(i => (int)i).ToArray();

                Vector3 Dir(int i) => Vector3.Normalize(new Vector3(pos[i].X, pos[i].Y, pos[i].Z));
                Vector2 Uv(int i) => new(uvs[i].X, uvs[i].Y);
                Vector3 Sample(Vector2 uv)
                {
                    float u = uv.X - MathF.Floor(uv.X);
                    float v = Math.Clamp(uv.Y, 0, 1);
                    int x = Math.Min(w - 1, (int)(u * w));
                    int y = Math.Min(h - 1, (int)(v * h));
                    var px = pixels[y * aw + x];
                    return new Vector3(px.r, px.g, px.b) * tint;
                }

                var samples = new List<(Vector3, Vector3)>();
                for (int t = 0; t + 2 < idx.Length; t += 3)
                    SampleTriangle(
                        samples,
                        Dir(idx[t]),
                        Dir(idx[t + 1]),
                        Dir(idx[t + 2]),
                        Uv(idx[t]),
                        Uv(idx[t + 1]),
                        Uv(idx[t + 2]),
                        Sample
                    );
                return samples;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Env] Sky model {modelName} unreadable: {ex.Message}");
                return null;
            }
        }

        //Splits a sky triangle into small ones, each weighted by its solid angle. Only the upper
        //hemisphere counts: the sky's lower half mirrors the top and the stage covers it in game.
        static void SampleTriangle(
            List<(Vector3, Vector3)> samples,
            Vector3 da,
            Vector3 db,
            Vector3 dc,
            Vector2 ua,
            Vector2 ub,
            Vector2 uc,
            Func<Vector2, Vector3> sample
        )
        {
            const float n = Subdivide;
            Vector3 D(float s, float q) => Vector3.Normalize(da * (1 - s - q) + db * s + dc * q);
            Vector2 U(float s, float q) => ua * (1 - s - q) + ub * s + uc * q;

            for (int i = 0; i < Subdivide; i++)
            for (int j = 0; j < Subdivide - i; j++)
            for (int k = 0; k < 2; k++)
            {
                if (k == 1 && i + j == Subdivide - 1)
                    continue;
                float s0,
                    q0,
                    s1,
                    q1,
                    s2,
                    q2;
                if (k == 0)
                    (s0, q0, s1, q1, s2, q2) = (
                        i / n,
                        j / n,
                        (i + 1) / n,
                        j / n,
                        i / n,
                        (j + 1) / n
                    );
                else
                    (s0, q0, s1, q1, s2, q2) = (
                        (i + 1) / n,
                        j / n,
                        (i + 1) / n,
                        (j + 1) / n,
                        i / n,
                        (j + 1) / n
                    );

                var d0 = D(s0, q0);
                var d1 = D(s1, q1);
                var d2 = D(s2, q2);
                var dir = Vector3.Normalize(d0 + d1 + d2);
                if (dir.Y < 0)
                    continue;
                var radiance = sample(U((s0 + s1 + s2) / 3, (q0 + q1 + q2) / 3));
                samples.Add((dir, radiance * SolidAngle(d0, d1, d2)));
            }
        }

        static void AddLowerHemisphere(Vector3[] sh, Vector3 radiance)
        {
            const int count = 4096;
            float omega = 4 * MathF.PI / count;
            float golden = MathF.PI * (3 - MathF.Sqrt(5));
            for (int i = 0; i < count; i++)
            {
                float y = 1 - 2 * (i + 0.5f) / count;
                if (y >= 0)
                    continue;
                float r = MathF.Sqrt(1 - y * y);
                float a = golden * i;
                Accumulate(
                    sh,
                    new Vector3(MathF.Cos(a) * r, y, MathF.Sin(a) * r),
                    radiance * omega
                );
            }
        }

        static float SolidAngle(Vector3 a, Vector3 b, Vector3 c)
        {
            float num = Vector3.Dot(a, Vector3.Cross(b, c));
            float den = 1 + Vector3.Dot(a, b) + Vector3.Dot(b, c) + Vector3.Dot(c, a);
            return MathF.Abs(2 * MathF.Atan2(num, den));
        }

        static void Accumulate(Vector3[] sh, Vector3 d, Vector3 weighted)
        {
            sh[0] += weighted * 0.282095f;
            sh[1] += weighted * (0.488603f * d.Y);
            sh[2] += weighted * (0.488603f * d.Z);
            sh[3] += weighted * (0.488603f * d.X);
            sh[4] += weighted * (1.092548f * d.X * d.Y);
            sh[5] += weighted * (1.092548f * d.Y * d.Z);
            sh[6] += weighted * (0.315392f * (3 * d.Z * d.Z - 1));
            sh[7] += weighted * (1.092548f * d.X * d.Z);
            sh[8] += weighted * (0.546274f * (d.X * d.X - d.Y * d.Y));
        }

        /// <summary>
        /// Radiance SH to the irradiance over pi form the shaders evaluate:
        /// dot(row0..2, (n, 1)) + dot(row3..5, (xy, yz, zz, xz)) + row6 * (x*x - y*y).
        /// </summary>
        static Vector4[] Pack(Vector3[] sh)
        {
            const float a1 = 2f / 3,
                a2 = 0.25f;
            var l1x = sh[3] * (a1 * 0.488603f);
            var l1y = sh[1] * (a1 * 0.488603f);
            var l1z = sh[2] * (a1 * 0.488603f);
            var dc = sh[0] * 0.282095f - sh[6] * (a2 * 0.315392f);
            var bxy = sh[4] * (a2 * 1.092548f);
            var byz = sh[5] * (a2 * 1.092548f);
            var bzz = sh[6] * (a2 * 3 * 0.315392f);
            var bxz = sh[7] * (a2 * 1.092548f);
            var c = sh[8] * (a2 * 0.546274f);

            var rows = new Vector4[7];
            for (int ch = 0; ch < 3; ch++)
            {
                float G(Vector3 v) =>
                    ch == 0 ? v.X
                    : ch == 1 ? v.Y
                    : v.Z;
                rows[ch] = new Vector4(G(l1x), G(l1y), G(l1z), G(dc));
                rows[3 + ch] = new Vector4(G(bxy), G(byz), G(bzz), G(bxz));
            }
            rows[6] = new Vector4(c, 1);
            return rows;
        }

        /// <summary>Evaluates packed rows for a direction, as the shaders do.</summary>
        public static Vector3 Evaluate(Vector4[] rows, Vector3 n)
        {
            var linear = new Vector4(n, 1);
            var quad = new Vector4(n.X * n.Y, n.Y * n.Z, n.Z * n.Z, n.X * n.Z);
            float x2y2 = n.X * n.X - n.Y * n.Y;
            return new Vector3(
                Vector4.Dot(rows[0], linear) + Vector4.Dot(rows[3], quad) + rows[6].X * x2y2,
                Vector4.Dot(rows[1], linear) + Vector4.Dot(rows[4], quad) + rows[6].Y * x2y2,
                Vector4.Dot(rows[2], linear) + Vector4.Dot(rows[5], quad) + rows[6].Z * x2y2
            );
        }
    }
}
