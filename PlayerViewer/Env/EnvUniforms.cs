using System;
using System.Numerics;

namespace PlayerViewer.Env
{
    /// <summary>
    /// Writes the fields of gsys_environment and gsys_user0 that come from a scene's rendering
    /// params into copies of dumped templates. Everything else keeps the template's value, which
    /// is the dump's.
    /// </summary>
    public static class EnvUniforms
    {
        public const int EnvironmentSize = 512;

        const int RowViewLight = 4;
        const int RowMainLightColor = 5;
        const int RowMainLightIntens = 6;
        const int RowDepthFogColor = 10;
        const int RowDepthFogRange = 12;
        const int RowHeightFogColor = 13;
        const int RowHeightFogRange = 15;
        const int RowMainLightDir = 23;
        public const int RowAmbientSH = 25;

        const int User0RowBakeShadow = 36;
        const int User0RowBakeAO = 37;
        const int User0RowRadialFog = 53;

        //w is 1 at night, which turns on the emission of gear that only glows at night.
        public const int User0RowNight = 52;

        //Ink shading values the game switches between day and night; no gyml key sets them.
        //Custom1 of the ink effects carries the same numbers.
        public const int User0RowInkRim = 18;
        public const int User0RowInkEnv = 19;
        public const int User0RowInkRimCurve = 45;

        /// <summary>
        /// gsys_environment for these params. <paramref name="view"/> is the camera rotation the
        /// game also writes a view space copy of the light with; no player shader reads that row.
        /// <paramref name="ambientSH"/> is the seven packed SH rows, or null to keep the template's.
        /// Without <paramref name="fog"/> the fog rows are left as the game leaves them when the
        /// environment set has no fog objects.
        /// </summary>
        public static byte[] BuildEnvironment(
            byte[] template,
            EnvParams p,
            bool fog = true,
            Matrix4x4? view = null,
            Vector4[] ambientSH = null
        )
        {
            var buf = new byte[Math.Max(EnvironmentSize, template?.Length ?? 0)];
            if (template != null)
                Buffer.BlockCopy(template, 0, buf, 0, template.Length);

            var dir = p.MainLightDirection;
            float intens = p.MainLightIntens;
            var viewDir = view.HasValue ? Vector3.TransformNormal(dir, view.Value) : dir;

            Write(buf, RowViewLight, new Vector4(viewDir, intens));
            Write(
                buf,
                RowMainLightColor,
                new Vector4(
                    p.MainLightColor.X * intens,
                    p.MainLightColor.Y * intens,
                    p.MainLightColor.Z * intens,
                    intens
                )
            );
            Write(buf, RowMainLightIntens, new Vector4(intens));
            Write(buf, RowMainLightDir, new Vector4(dir, 0));

            if (fog)
            {
                WriteFog(
                    buf,
                    RowDepthFogColor,
                    p.DepthFogColor,
                    new Vector3(0, 0, -1),
                    p.DepthFogStart,
                    p.DepthFogEnd
                );
                WriteFog(
                    buf,
                    RowHeightFogColor,
                    p.HeightFogColor,
                    p.HeightFogDir,
                    p.HeightFogStart,
                    p.HeightFogEnd
                );
            }
            else
            {
                for (int row = RowDepthFogColor; row <= RowHeightFogRange; row++)
                    Write(buf, row, Vector4.Zero);
                Write(buf, RowDepthFogRange, new Vector4(0, 1, 0, 0));
                Write(buf, RowHeightFogRange, new Vector4(0, 1, 0, 0));
            }

            if (ambientSH != null)
                for (int i = 0; i < ambientSH.Length; i++)
                    Write(buf, RowAmbientSH + i, ambientSH[i]);
            return buf;
        }

        /// <summary>
        /// gsys_user0 for these params: the bake shadow and AO remaps, the radial fog, the night
        /// switch and the ink values that follow it.
        /// </summary>
        public static byte[] BuildUser0(byte[] template, EnvParams p, bool night)
        {
            var buf = new byte[template?.Length ?? 0];
            if (template == null)
                return buf;
            Buffer.BlockCopy(template, 0, buf, 0, template.Length);

            WriteFloat(buf, User0RowNight, 3, night ? 1 : 0);
            WriteFloat(buf, User0RowInkRim, 0, night ? 0.1f : 0.05f);
            WriteFloat(buf, User0RowInkRim, 3, night ? 0.1f : 0.125f);
            WriteFloat(buf, User0RowInkEnv, 0, night ? 0.01f : 0.015f);
            WriteFloat(buf, User0RowInkRimCurve, 1, night ? 1.6f : 0.625f);
            WriteFloat(buf, User0RowInkRimCurve, 2, night ? 0.9f : 0.875f);

            WriteFloat(buf, User0RowBakeAO, 0, p.BakeAOIntensScale);
            WriteFloat(buf, User0RowBakeAO, 1, -p.BakeAOIntensOffset * p.BakeAOIntensScale);
            WriteFloat(buf, User0RowBakeAO, 3, p.BakeAOMainLightOcclude);

            WriteFloat(
                buf,
                User0RowBakeShadow,
                0,
                -p.BakeShadowIntensOffset * p.BakeShadowIntensScale
            );
            WriteFloat(buf, User0RowBakeShadow, 1, p.BakeShadowIntensScale);

            float depthRange = 1f / (p.DepthFogEnd - p.DepthFogStart);
            Write(
                buf,
                User0RowRadialFog,
                new Vector4(
                    p.DepthFogScattering,
                    depthRange,
                    p.DepthFogScattering,
                    p.RadialFogBlendFactor
                )
            );
            Write(
                buf,
                User0RowRadialFog + 1,
                new Vector4(
                    p.RadialFogLobeCtrl,
                    p.RadialFogSizeCtrl,
                    1f / float.Exp2(2f * p.RadialFogSizeCtrl),
                    0
                )
            );
            Write(
                buf,
                User0RowRadialFog + 2,
                new Vector4(
                    p.RadialFogColor.X * p.RadialFogIntens,
                    p.RadialFogColor.Y * p.RadialFogIntens,
                    p.RadialFogColor.Z * p.RadialFogIntens,
                    0
                )
            );
            return buf;
        }

        /// <summary>The seven packed ambient SH rows of a gsys_environment block.</summary>
        public static Vector4[] ReadAmbientRows(byte[] env)
        {
            var rows = new Vector4[7];
            for (int i = 0; i < rows.Length; i++)
            {
                int o = (RowAmbientSH + i) * 16;
                rows[i] = new Vector4(
                    BitConverter.ToSingle(env, o),
                    BitConverter.ToSingle(env, o + 4),
                    BitConverter.ToSingle(env, o + 8),
                    BitConverter.ToSingle(env, o + 12)
                );
            }
            return rows;
        }

        //A fog is three rows: colour, then direction with the start offset, then the reciprocal
        //range with a damp of 1.
        static void WriteFog(
            byte[] buf,
            int row,
            Vector4 color,
            Vector3 dir,
            float start,
            float end
        )
        {
            float inv = 1f / (end - start);
            Write(buf, row, color);
            Write(buf, row + 1, new Vector4(dir, -start * inv));
            Write(buf, row + 2, new Vector4(inv, 1, 0, 0));
        }

        static void Write(byte[] buf, int row, Vector4 v)
        {
            WriteFloat(buf, row, 0, v.X);
            WriteFloat(buf, row, 1, v.Y);
            WriteFloat(buf, row, 2, v.Z);
            WriteFloat(buf, row, 3, v.W);
        }

        static void WriteFloat(byte[] buf, int row, int component, float value)
        {
            int offset = row * 16 + component * 4;
            if (offset + 4 <= buf.Length)
                BitConverter.TryWriteBytes(buf.AsSpan(offset, 4), value);
        }
    }
}
