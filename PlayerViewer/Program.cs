using System;
using System.IO;
using System.Linq;
using GLFrameworkEngine;
using OpenTK;
using OpenTK.Graphics;
using PlayerViewer.Core;
using Toolbox.Core;

namespace PlayerViewer
{
    class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            Directory.SetCurrentDirectory(AppContext.BaseDirectory);

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
