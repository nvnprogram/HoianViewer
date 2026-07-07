using System;
using System.IO;
using OpenTK;
using OpenTK.Graphics;
using Toolbox.Core;
using GLFrameworkEngine;

namespace CafeShaderStudio
{
    class Program
    {
        static void Main(string[] args)
        {
            RenderResourceCreator.CreateTextureInstance += (sender, e) =>
            {
                var tex = sender as STGenericTexture;
                return GLTexture.FromGenericTexture(tex, tex.Parameters);
            };

            Runtime.DisplayBones = false;

            if (args.Length >= 1 && File.Exists(args[0]))
                MainWindow.PendingAutoLoadFile = args[0];

            GraphicsMode mode = new GraphicsMode(new ColorFormat(32), 24, 8, 4, new ColorFormat(32), 2, false);
            MainWindow wnd = new MainWindow(mode);
            wnd.VSync = OpenTK.VSyncMode.On;
            wnd.Run();
        }
    }
}
