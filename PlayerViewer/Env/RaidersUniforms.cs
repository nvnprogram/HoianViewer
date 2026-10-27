using System;
using System.Numerics;
using BfresEditor;

namespace PlayerViewer.Env
{
    /// <summary>
    /// The shared blocks and storage buffers of Raiders' player shaders: gsys_context,
    /// gsys_environment with its Hoian extension, gsys_user0, and the light probe buffers the
    /// material pass takes its ambient from. Row layouts are in env.md and raiders.md.
    /// </summary>
    public static class RaidersUniforms
    {
        /// <summary>Through row 177, the last the shaders read.</summary>
        public const int EnvironmentSize = 2848;
        public const int User0Size = 1360;

        //The context layout is Splatoon 3's up to here, the rest i havent dumped so 0 for now.
        const int ContextSharedSize = 1088;

        const int RowAmbientSH = 97;
        const int RowAmbientSHCopy = 116;

        /// <summary>
        /// gsys_environment. <paramref name="template"/> supplies the constant gsys rows 0 to 24,
        /// the same in both games; <paramref name="ambientSH"/> is the seven packed rows, without
        /// the irradiance intensity, which goes in row 109.
        /// </summary>
        public static byte[] BuildEnvironment(
            byte[] template,
            RaidersEnvParams p,
            Vector4[] ambientSH
        )
        {
            var buf = new byte[EnvironmentSize];
            if (template != null)
                Buffer.BlockCopy(template, 0, buf, 0, Math.Min(template.Length, 25 * 16));

            //Rows 4 to 6, 10 to 15 and 23 as Splatoon 3 writes them.
            var gsys = EnvUniforms.BuildEnvironment(buf.AsSpan(0, 25 * 16).ToArray(), p, true);
            Buffer.BlockCopy(gsys, 0, buf, 0, 25 * 16);
            ClearEmptyFog(buf, 10, p.DepthFogStart, p.DepthFogEnd);
            ClearEmptyFog(buf, 13, p.HeightFogStart, p.HeightFogEnd);
            for (int row = 25; row <= 32; row++)
                Write(buf, row, Vector4.Zero);

            //The cubemap pass's copy of the main light.
            Buffer.BlockCopy(buf, 5 * 16, buf, 44 * 16, 16);

            //Bake remaps, one row each for the field and the field background sets.
            Write(
                buf,
                40,
                new Vector4(
                    -p.BakeShadowIntensOffset * p.BakeShadowIntensScale,
                    p.BakeShadowIntensScale,
                    0,
                    0
                )
            );
            Write(
                buf,
                41,
                new Vector4(
                    -p.BakeShadowFldBGIntensOffset * p.BakeShadowFldBGIntensScale,
                    p.BakeShadowFldBGIntensScale,
                    0,
                    0
                )
            );
            Write(
                buf,
                42,
                new Vector4(
                    p.BakeAOIntensScale,
                    -p.BakeAOIntensOffset * p.BakeAOIntensScale,
                    p.BakeAOMainLightOcclude,
                    0
                )
            );
            Write(
                buf,
                43,
                new Vector4(
                    p.BakeAOFldBGIntensScale,
                    -p.BakeAOFldBGIntensOffset * p.BakeAOFldBGIntensScale,
                    p.BakeAOFldBGMainLightOcclude,
                    0
                )
            );

            if (ambientSH != null)
                for (int i = 0; i < 7; i++)
                {
                    Write(buf, RowAmbientSH + i, ambientSH[i]);
                    Write(buf, RowAmbientSHCopy + i, ambientSH[i]);
                }

            //Radial fog: Splatoon 3's user0 rows 53 to 55, repacked.
            Write(buf, 106, new Vector4(p.DepthFogScattering, p.RadialFogBlendFactor, 0, 0));
            Write(
                buf,
                107,
                new Vector4(
                    p.RadialFogLobeCtrl,
                    p.RadialFogSizeCtrl,
                    1f / float.Exp2(2f * p.RadialFogSizeCtrl),
                    p.RadialFogShadowInfluence
                )
            );
            Write(
                buf,
                108,
                new Vector4(
                    p.RadialFogColor.X * p.RadialFogIntens,
                    p.RadialFogColor.Y * p.RadialFogIntens,
                    p.RadialFogColor.Z * p.RadialFogIntens,
                    0
                )
            );

            Write(
                buf,
                109,
                new Vector4(
                    p.CubeMapEmissionScale,
                    p.IrradianceIntensity,
                    p.CubeMapDirectLightScale,
                    0
                )
            );

            WriteExpHeightFog(buf, p, p.ExpFogStartHeight);

            //No dynamic lights: the cap on the cluster's light count.
            BitConverter.TryWriteBytes(buf.AsSpan(177 * 16, 4), 0);
            return buf;
        }

        //A fog whose start and end meet would divide by zero; Raiders scenes ship such fogs at
        //alpha 0, so its factor is written as 0 instead of the infinite slope.
        static void ClearEmptyFog(byte[] buf, int row, float start, float end)
        {
            if (MathF.Abs(end - start) > 1e-6f)
                return;
            WriteFloat(buf, row + 1, 3, 0);
            Write(buf, row + 2, new Vector4(0, 1, 0, 0));
        }

        /// <summary>
        /// The exponential height fog, rows 111, 112, 114 and 115. The shaders integrate the
        /// density along the ray from the camera, so 111.y holds the density at the camera's
        /// height and the block is rewritten when that height changes.
        /// </summary>
        public static void WriteExpHeightFog(byte[] buf, RaidersEnvParams p, float cameraHeight)
        {
            float startDensity = Math.Clamp(p.ExpFogStartDensity, 1e-5f, 1f);
            float endDensity = Math.Clamp(p.ExpFogEndDensity, 1e-5f, 1f);
            float heightRange = p.ExpFogEndHeight - p.ExpFogStartHeight;
            float falloff =
                heightRange != 0
                    ? (MathF.Log(endDensity) - MathF.Log(startDensity)) / heightRange
                    : 0;
            float exponent =
                MathF.Log(startDensity) + falloff * (cameraHeight - p.ExpFogStartHeight);
            float density =
                p.ExpFogBaseDensity * 0.01f * 1.4427f * MathF.Exp(Math.Clamp(exponent, -50f, 50f));
            //z is added to the exponent and w is exp(z), the limit for a level ray.
            Write(buf, 111, new Vector4(falloff, density, 0, 1));
            Write(
                buf,
                112,
                new Vector4(
                    p.ExpFogIntensity * p.ExpFogColor.X,
                    p.ExpFogIntensity * p.ExpFogColor.Y,
                    p.ExpFogIntensity * p.ExpFogColor.Z,
                    p.ExpFogColor.W
                )
            );
            float colorRange = p.ExpFogEndColorDistance - p.ExpFogStartColorDistance;
            Write(
                buf,
                114,
                new Vector4(
                    p.ExpFogEndColorScale - p.ExpFogStartColorScale,
                    colorRange != 0 ? 1f / colorRange : 0,
                    p.ExpFogStartColorScale,
                    p.ExpFogStartColorDistance
                )
            );
            Write(buf, 115, new Vector4(p.ExpFogStartHeight, 0, 0, 0));
        }

        /// <summary>
        /// gsys_user0: the frame, the ink shading values of the day set and the shadow and rim
        /// constants. The frame is held at 0, as the Splatoon 3 dump's time is.
        /// </summary>
        public static byte[] BuildUser0(RaidersEnvParams p)
        {
            var buf = new byte[User0Size];
            const float frame = 0;
            const float thicknessBase = 0.95f,
                thicknessWall = 0.75f,
                mottari = 1.75f;
            const float thicknessPeriod = 0.03125f,
                thicknessAmplitude = 0.015625f;
            float anim = thicknessAmplitude * MathF.Sin(thicknessPeriod * frame);

            Write(buf, 0, new Vector4(frame, frame / 60, 0, 0));
            Write(buf, 1, new Vector4(0.05f, 1.8f, SubsurfaceEmission(p.MainLightIntens), 0.125f));
            Write(buf, 2, new Vector4(0.1f, 0, 0.5f, frame));
            Write(
                buf,
                3,
                new Vector4(
                    Math.Clamp(thicknessBase + anim, 0, 1),
                    Math.Clamp(thicknessWall + anim, 0, 1),
                    mottari,
                    frame / 60
                )
            );
            Write(buf, 4, new Vector4(0, 1, 0.6f, 0.2f));
            Write(buf, 5, new Vector4(5, 1, 1.75f, 1));
            Write(
                buf,
                7,
                new Vector4(thicknessBase, thicknessWall, thicknessPeriod, thicknessAmplitude)
            );

            WriteFloat(buf, 21, 3, 0.3f);
            WriteFloat(buf, 22, 2, 0.0003125f);
            WriteFloat(buf, 22, 3, 0.0003125f);
            WriteFloat(buf, 36, 2, 1000);
            WriteFloat(buf, 36, 3, 0.7f);
            Write(buf, 45, new Vector4(0, 0.625f, 0.875f, 1));
            return buf;
        }

        //The ink's subsurface emission term for a main light intensity: bias 0.1, 0.2 at its
        //strongest, rising from intensity 2 to 7.
        static float SubsurfaceEmission(float intensity)
        {
            const float bias = 0.1f,
                atLight7 = 0.2f,
                atLight2 = 0;
            return bias + Math.Clamp((intensity - 2) * atLight7 / 5, atLight2, atLight7);
        }

        /// <summary>
        /// gsys_context: Splatoon 3's shared prefix from <paramref name="template"/> with the
        /// camera written in, the depth and target rows in full, and the light cluster header
        /// left zero so no dynamic light is found.
        /// </summary>
        public static void WriteContext(Span<byte> block, byte[] template, in HoianView view)
        {
            if (template != null)
                template
                    .AsSpan(0, Math.Min(Math.Min(template.Length, ContextSharedSize), block.Length))
                    .CopyTo(block);

            HoianContext.WriteCamera(block, view);

            float n = view.Near,
                f = view.Far,
                range = f - n;
            float tan = MathF.Tan(view.Fovy / 2);
            float aspect = view.Aspect;
            Write(block, 14, new Vector4(n, f, n / f, 1 - n / f));
            Write(block, 15, new Vector4(1 / range, n / range, aspect, 1 / aspect));
            Write(block, 16, new Vector4(range, 1 / (1 - n / f), 0, 1 / (aspect * tan)));
            Write(block, 17, new Vector4(aspect * tan, tan, view.Fovy, 1 / tan));
            Write(
                block,
                18,
                new Vector4(view.Width, view.Height, 1f / view.Width, 1f / view.Height)
            );
        }

        /// <summary>
        /// The light cluster header, rows 148 to 156: a single cluster with one list, whose
        /// lights start at <see cref="LightCluster.RaidersElementHead"/>.
        /// </summary>
        public static void WriteLightClusterHeader(Span<byte> block, in HoianView view, int lights)
        {
            if (block.Length < 157 * 16)
                return;
            int head = LightCluster.RaidersElementHead;
            WriteIntRow(block, 148, 1, 1, 0, 2);
            WriteIntRow(block, 149, head, 1, 1, 1);
            Write(block, 150, new Vector4(1, 1, 1, 0));
            Write(block, 151, new Vector4(0, 1, view.Near, 0));
            WriteInt(block, 151, 3, lights);
            float projX = view.Projection.M11,
                projY = view.Projection.M22;
            Write(
                block,
                152,
                new Vector4(projX != 0 ? 1 / projX : 1, projY != 0 ? 1 / projY : 1, 0, 0)
            );
            WriteInt(block, 152, 2, 1);
            WriteInt(block, 152, 3, lights);
            Write(block, 153, new Vector4(view.Far, view.Near, view.Far, 0));
            HoianContext.WriteMat3x4(block, 154, view.View);
        }

        /// <summary>Sets the cap on the dynamic light count, row 177.x.</summary>
        public static void WriteLightCount(byte[] environment, int lights) =>
            BitConverter.TryWriteBytes(environment.AsSpan(177 * 16, 4), lights);

        static void WriteIntRow(Span<byte> buf, int row, int x, int y, int z, int w)
        {
            WriteInt(buf, row, 0, x);
            WriteInt(buf, row, 1, y);
            WriteInt(buf, row, 2, z);
            WriteInt(buf, row, 3, w);
        }

        static void WriteInt(Span<byte> buf, int row, int component, int value) =>
            BitConverter.TryWriteBytes(buf.Slice(row * 16 + component * 4, 4), value);

        /// <summary>gsys_user6: a one cell probe grid whose only cell holds probe 0.</summary>
        public static byte[] ProbeGrid()
        {
            var buf = new byte[96];
            WriteInts(buf, 0x30, 1, 1, 1, 0);
            WriteInts(buf, 0x40, 1, 1, 1, 0);
            return buf;
        }

        /// <summary>gsys_user5: a 16 byte header, then one probe holding the ambient SH.</summary>
        public static byte[] Probes(Vector4[] ambientSH)
        {
            var buf = new byte[16 + 7 * 16];
            for (int i = 0; i < 7 && ambientSH != null; i++)
                WriteVec(buf, 16 + i * 16, ambientSH[i]);
            BitConverter.TryWriteBytes(buf.AsSpan(16 + 6 * 16 + 12, 4), 1f);
            return buf;
        }

        static void WriteInts(byte[] buf, int offset, int x, int y, int z, int w)
        {
            BitConverter.TryWriteBytes(buf.AsSpan(offset, 4), x);
            BitConverter.TryWriteBytes(buf.AsSpan(offset + 4, 4), y);
            BitConverter.TryWriteBytes(buf.AsSpan(offset + 8, 4), z);
            BitConverter.TryWriteBytes(buf.AsSpan(offset + 12, 4), w);
        }

        static void WriteVec(Span<byte> buf, int offset, Vector4 v)
        {
            if (offset + 16 > buf.Length)
                return;
            BitConverter.TryWriteBytes(buf.Slice(offset, 4), v.X);
            BitConverter.TryWriteBytes(buf.Slice(offset + 4, 4), v.Y);
            BitConverter.TryWriteBytes(buf.Slice(offset + 8, 4), v.Z);
            BitConverter.TryWriteBytes(buf.Slice(offset + 12, 4), v.W);
        }

        static void Write(Span<byte> buf, int row, Vector4 v) => WriteVec(buf, row * 16, v);

        static void WriteFloat(byte[] buf, int row, int component, float value) =>
            BitConverter.TryWriteBytes(buf.AsSpan(row * 16 + component * 4, 4), value);
    }
}
