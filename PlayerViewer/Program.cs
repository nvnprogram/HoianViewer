using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using GLFrameworkEngine;
using OpenTK;
using OpenTK.Graphics;
using PlayerViewer.Core;
using Toolbox.Core;

namespace PlayerViewer
{
    class Program
    {
        [DllImport("libc", SetLastError = true)]
        static extern int setenv(string name, string value, int overwrite);

        /// <summary>
        /// Mesa turns its threaded dispatch (glthread) off again as soon as an entry
        /// point it cannot marshal is looked up, and OpenTK looks up every entry point
        /// at startup. The driver state left behind by that switch is corrupt: the next
        /// unrelated GL call faults inside the driver while releasing a stale object.
        /// Opting out up front costs nothing here, since glthread never gets to run any
        /// work for this process anyway. Set mesa_glthread in the environment to override.
        /// </summary>
        static void ConfigureDriverWorkarounds()
        {
            if (!OperatingSystem.IsLinux())
                return;
            if (Environment.GetEnvironmentVariable("mesa_glthread") != null)
                return;

            try
            {
                setenv("mesa_glthread", "false", 0);
            }
            catch (DllNotFoundException) { } //Not glibc, nothing to opt out of.
            catch (EntryPointNotFoundException) { }
        }

        [STAThread]
        static void Main(string[] args)
        {
            ConfigureDriverWorkarounds();

            Directory.SetCurrentDirectory(AppContext.BaseDirectory);
            BfresEditor.TegraShaderDecoder.CacheDir = AppPaths.ShaderCacheDir;
            ShaderBundler.BundleStamp.Codegen = ShaderBundler.UberspecRunner.QueryCodegen(
                ShaderBundler.UberspecRunner.FindExecutable(AppContext.BaseDirectory)
            );

            RenderResourceCreator.CreateTextureInstance += (sender, e) =>
            {
                var tex = sender as STGenericTexture;
                return GLTexture.FromGenericTexture(tex, tex.Parameters);
            };
            Runtime.DisplayBones = false;

            var config = AppConfig.Load();
            using var window = new UI.ViewerWindow(config);
            window.VSync = VSyncMode.On;

            int openArg = Array.IndexOf(args, "--open");
            if (openArg >= 0 && openArg + 1 < args.Length)
                window.AutoOpenFile = args[openArg + 1];

            window.Run();
        }
    }
}
