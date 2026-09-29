using System;
using System.IO;

namespace PlayerViewer.Effects.Gl
{
    /// <summary>The viewer's own effect pass shaders, embedded from Effects/Gl/Shaders.</summary>
    static class EffectShaders
    {
        public static string Load(string name)
        {
            string resource = $"PlayerViewer.Effects.Gl.Shaders.{name}";
            using var stream =
                typeof(EffectShaders).Assembly.GetManifestResourceStream(resource)
                ?? throw new InvalidOperationException($"missing embedded shader {resource}");
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
    }
}
