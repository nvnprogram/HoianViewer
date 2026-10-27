using System;
using System.Numerics;
using System.Threading.Tasks;
using PlayerViewer.Textures;

namespace PlayerViewer.Env
{
    /// <summary>
    /// The reflection cube array Raiders' materials sample: one GGX prefiltered cube per
    /// roughness step, then the unfiltered map. The shaders pick the layer as
    /// round(5.5 * (1 - cos(pi * roughness))) and the mip from screen derivatives.
    /// </summary>
    public static class EnvPrefilter
    {
        public const int RoughnessLayers = 12;

        /// <summary>The roughness layers and the unfiltered map the shaders read at index 12.</summary>
        public const int LayerCount = RoughnessLayers + 1;

        //Rough layers are smooth, so they are filtered at this size and resampled to the rest.
        const int FilterSize = 64;
        const int Samples = 64;

        /// <summary>The roughness a layer stands for, the inverse of the shaders' layer choice.</summary>
        public static float LayerRoughness(int layer) =>
            MathF.Acos(Math.Clamp(1 - layer / 5.5f, -1f, 1f)) / MathF.PI;

        /// <summary>
        /// Per mip of <paramref name="cube"/>, every layer's six faces packed R11G11B10F, layer
        /// after layer.
        /// </summary>
        public static byte[][] Build(EnvMapCube cube)
        {
            int mips = cube.Mips.Length;
            var source = new Vector3[mips][][];
            for (int mip = 0; mip < mips; mip++)
            {
                source[mip] = new Vector3[6][];
                for (int face = 0; face < 6; face++)
                    source[mip][face] = cube.Face(mip, face);
            }

            int filterSize = Math.Min(FilterSize, cube.Size);
            int filterMip = (int)Math.Round(Math.Log2(cube.Size / (double)filterSize));
            //[layer][mip][face]
            var layers = new Vector3[LayerCount][][][];
            layers[0] = source;
            layers[RoughnessLayers] = source;
            var options = new ParallelOptions { MaxDegreeOfParallelism = 4 };
            Parallel.For(
                0,
                (RoughnessLayers - 1) * 6,
                options,
                job =>
                {
                    int layer = 1 + job / 6, face = job % 6;
                    var filtered = Filter(source, cube.Size, face, filterSize, LayerRoughness(layer));
                    lock (layers)
                    {
                        layers[layer] ??= new Vector3[mips][][];
                        for (int mip = 0; mip < mips; mip++)
                            layers[layer][mip] ??= new Vector3[6][];
                        layers[layer][filterMip][face] = filtered;
                    }
                }
            );

            for (int layer = 1; layer < RoughnessLayers; layer++)
            for (int face = 0; face < 6; face++)
            {
                var chain = layers[layer];
                for (int mip = filterMip - 1; mip >= 0; mip--)
                    chain[mip][face] = Resample(chain[filterMip][face], filterSize, cube.Size >> mip);
                for (int mip = filterMip + 1; mip < mips; mip++)
                    chain[mip][face] = Halve(chain[mip - 1][face], Math.Max(1, cube.Size >> (mip - 1)));
            }

            var result = new byte[mips][];
            for (int mip = 0; mip < mips; mip++)
            {
                int s = Math.Max(1, cube.Size >> mip);
                int faceTexels = s * s;
                var data = new byte[LayerCount * 6 * faceTexels * 4];
                for (int layer = 0; layer < LayerCount; layer++)
                for (int face = 0; face < 6; face++)
                {
                    var texels = layers[layer][mip][face];
                    int baseOffset = (layer * 6 + face) * faceTexels * 4;
                    for (int i = 0; i < faceTexels; i++)
                        BitConverter.TryWriteBytes(
                            data.AsSpan(baseOffset + i * 4, 4),
                            FloatTexels.PackR11G11B10(texels[i])
                        );
                }
                result[mip] = data;
            }
            return result;
        }

        //One face of a layer: the environment seen through a GGX lobe around each direction,
        //importance sampled with each sample's mip chosen by its solid angle.
        static Vector3[] Filter(Vector3[][][] source, int size, int face, int outSize, float roughness)
        {
            var output = new Vector3[outSize * outSize];
            float maxLod = source.Length - 1;
            float texelAngle = 4 * MathF.PI / (6f * size * size);
            float a = roughness * roughness;
            for (int y = 0; y < outSize; y++)
            for (int x = 0; x < outSize; x++)
            {
                float u = 2 * (x + 0.5f) / outSize - 1;
                float v = 2 * (y + 0.5f) / outSize - 1;
                var n = Vector3.Normalize(EnvMapCube.Direction(face, u, v));
                var up = MathF.Abs(n.Z) < 0.999f ? Vector3.UnitZ : Vector3.UnitX;
                var tangent = Vector3.Normalize(Vector3.Cross(up, n));
                var binormal = Vector3.Cross(n, tangent);
                var sum = Vector3.Zero;
                float weight = 0;
                for (int i = 0; i < Samples; i++)
                {
                    float xi0 = (float)i / Samples;
                    float xi1 = RadicalInverse((uint)i);
                    float cosT = MathF.Sqrt((1 - xi1) / (1 + (a * a - 1) * xi1));
                    float sinT = MathF.Sqrt(1 - cosT * cosT);
                    float phi = 2 * MathF.PI * xi0;
                    var h =
                        sinT * MathF.Cos(phi) * tangent + sinT * MathF.Sin(phi) * binormal + cosT * n;
                    var l = 2 * Vector3.Dot(n, h) * h - n;
                    float nol = Vector3.Dot(n, l);
                    if (nol <= 0)
                        continue;
                    float noh = MathF.Max(1e-8f, Vector3.Dot(n, h));
                    float d = a / MathF.Max(1e-8f, noh * noh * (a * a - 1) + 1);
                    float pdf = d * d / MathF.PI / 4;
                    float lod =
                        roughness < 0.01f
                            ? 0
                            : Math.Clamp(0.5f * MathF.Log2(1 / (Samples * pdf) / texelAngle), 0, maxLod);
                    sum += SampleLod(source, size, l, lod) * nol;
                    weight += nol;
                }
                output[y * outSize + x] = weight > 0 ? sum / weight : Vector3.Zero;
            }
            return output;
        }

        static float RadicalInverse(uint bits)
        {
            bits = (bits << 16) | (bits >> 16);
            bits = ((bits & 0x55555555u) << 1) | ((bits & 0xAAAAAAAAu) >> 1);
            bits = ((bits & 0x33333333u) << 2) | ((bits & 0xCCCCCCCCu) >> 2);
            bits = ((bits & 0x0F0F0F0Fu) << 4) | ((bits & 0xF0F0F0F0u) >> 4);
            bits = ((bits & 0x00FF00FFu) << 8) | ((bits & 0xFF00FF00u) >> 8);
            return bits * 2.3283064365386963e-10f;
        }

        static Vector3 SampleLod(Vector3[][][] source, int size, Vector3 dir, float lod)
        {
            int lo = (int)lod;
            int hi = Math.Min(lo + 1, source.Length - 1);
            float t = lod - lo;
            var c = Sample(source[lo], Math.Max(1, size >> lo), dir);
            return t > 0 ? Vector3.Lerp(c, Sample(source[hi], Math.Max(1, size >> hi), dir), t) : c;
        }

        //Bilinear within the face the direction lands on, clamped at its edges.
        static Vector3 Sample(Vector3[][] faces, int s, Vector3 dir)
        {
            var abs = Vector3.Abs(dir);
            int face;
            float u, v;
            if (abs.X >= abs.Y && abs.X >= abs.Z)
            {
                face = dir.X > 0 ? 0 : 1;
                u = (dir.X > 0 ? -dir.Z : dir.Z) / abs.X;
                v = -dir.Y / abs.X;
            }
            else if (abs.Y >= abs.Z)
            {
                face = dir.Y > 0 ? 2 : 3;
                u = dir.X / abs.Y;
                v = (dir.Y > 0 ? dir.Z : -dir.Z) / abs.Y;
            }
            else
            {
                face = dir.Z > 0 ? 4 : 5;
                u = (dir.Z > 0 ? dir.X : -dir.X) / abs.Z;
                v = -dir.Y / abs.Z;
            }
            return Bilinear(faces[face], s, (u + 1) * 0.5f * s - 0.5f, (v + 1) * 0.5f * s - 0.5f);
        }

        static Vector3 Bilinear(Vector3[] texels, int s, float x, float y)
        {
            x = Math.Clamp(x, 0, s - 1);
            y = Math.Clamp(y, 0, s - 1);
            int x0 = (int)x, y0 = (int)y;
            int x1 = Math.Min(x0 + 1, s - 1), y1 = Math.Min(y0 + 1, s - 1);
            float fx = x - x0, fy = y - y0;
            var top = Vector3.Lerp(texels[y0 * s + x0], texels[y0 * s + x1], fx);
            var bottom = Vector3.Lerp(texels[y1 * s + x0], texels[y1 * s + x1], fx);
            return Vector3.Lerp(top, bottom, fy);
        }

        static Vector3[] Resample(Vector3[] texels, int from, int to)
        {
            var output = new Vector3[to * to];
            float scale = from / (float)to;
            for (int y = 0; y < to; y++)
            for (int x = 0; x < to; x++)
                output[y * to + x] = Bilinear(texels, from, (x + 0.5f) * scale - 0.5f, (y + 0.5f) * scale - 0.5f);
            return output;
        }

        static Vector3[] Halve(Vector3[] texels, int from)
        {
            int to = Math.Max(1, from / 2);
            if (from == 1)
                return (Vector3[])texels.Clone();
            var output = new Vector3[to * to];
            for (int y = 0; y < to; y++)
            for (int x = 0; x < to; x++)
                output[y * to + x] =
                    (
                        texels[2 * y * from + 2 * x]
                        + texels[2 * y * from + 2 * x + 1]
                        + texels[(2 * y + 1) * from + 2 * x]
                        + texels[(2 * y + 1) * from + 2 * x + 1]
                    ) * 0.25f;
            return output;
        }
    }
}
