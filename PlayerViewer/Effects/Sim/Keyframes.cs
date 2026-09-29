using System;
using System.Numerics;

namespace PlayerViewer.Effects.Sim
{
    /// <summary>
    /// Keys of xyz with their time in w, evaluated as the runtime does for its emitter, field and
    /// particle tables: clamped to the first and last key, then stepped or linear between them.
    /// </summary>
    public static class Keyframes
    {
        /// <summary>
        /// The value at <paramref name="t"/>. Interpolation 1 holds the earlier key and 0 is
        /// linear; any other value, or a time that falls between no two keys, gives
        /// <paramref name="fallback"/>. Sets <paramref name="pastEnd"/> at or past the last key.
        /// </summary>
        public static Vector3 Evaluate(
            ReadOnlySpan<Vector4> keys,
            int interpolation,
            float t,
            Vector3 fallback,
            ref bool pastEnd
        )
        {
            int n = keys.Length;
            if (n == 1 || keys[0].W > t)
                return Xyz(keys[0]);
            if (keys[n - 1].W <= t)
            {
                pastEnd = true;
                return Xyz(keys[n - 1]);
            }
            for (int i = 0; i + 1 < n; i++)
            {
                var a = keys[i];
                var b = keys[i + 1];
                if (a.W <= t && b.W > t)
                {
                    if (interpolation == 1)
                        return Xyz(a);
                    if (interpolation != 0)
                        return fallback;
                    return Xyz(a) + (Xyz(b) - Xyz(a)) * ((t - a.W) / (b.W - a.W));
                }
            }
            return fallback;
        }

        public static Vector3 Evaluate(
            ReadOnlySpan<Vector4> keys,
            int interpolation,
            float t,
            Vector3 fallback
        )
        {
            bool pastEnd = false;
            return Evaluate(keys, interpolation, t, fallback, ref pastEnd);
        }

        static Vector3 Xyz(Vector4 v) => new(v.X, v.Y, v.Z);
    }
}
