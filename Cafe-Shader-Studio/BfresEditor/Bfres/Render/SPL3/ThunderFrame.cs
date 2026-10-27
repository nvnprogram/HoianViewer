using System;
using GLFrameworkEngine;
using OpenTK;

namespace BfresEditor
{
    /// <summary>
    /// Splatoon 3's fill of the shared inputs: the blocks of the current dumped uniform set, with
    /// a generated environment and user0 in their place when set. No storage buffers, textures or
    /// dynamic lights of its own.
    /// </summary>
    public sealed class ThunderFrame : IHoianFrameSource
    {
        public static readonly ThunderFrame Instance = new ThunderFrame();

        /// <summary>
        /// Generated gsys_environment and gsys_user0 contents. When set they replace the dumped
        /// blocks of the current uniform set; the other blocks still come from the dump.
        /// </summary>
        public byte[] EnvironmentOverride;
        public byte[] User0Override;

        string _dir;
        byte[] _context;
        byte[] _environment;
        byte[] _user0;

        ThunderFrame() { }

        //Read on first use and again when the uniform set changes.
        void Load()
        {
            string dir = HoianNXRender.UniformSetDir;
            if (_dir == dir)
                return;
            _dir = dir;
            _context = HoianNXRender.ReadUniformFile("fp_c3.bin");
            _environment = HoianNXRender.ReadUniformFile("fp_c5.bin");
            _user0 = HoianNXRender.ReadUniformFile("fp_c7.bin");
        }

        public byte[] Block(string name)
        {
            Load();
            return name switch
            {
                "gsys_environment" => EnvironmentOverride ?? _environment,
                "gsys_user0" => User0Override ?? _user0,
                _ => null,
            };
        }

        /// <summary>The dumped block with the camera written in.</summary>
        public void WriteContext(Span<byte> block, in HoianView view)
        {
            Load();
            if (_context != null)
                _context.AsSpan(0, Math.Min(_context.Length, block.Length)).CopyTo(block);
            HoianContext.WriteCamera(block, view);

            float znear = view.Near, zfar = view.Far, zDistance = zfar - znear;
            HoianContext.WriteRow(block, 14, new Vector4(znear, zfar, zfar / znear, 1.0f - znear / zfar));
            HoianContext.WriteRow(block, 15, new Vector4(1.0f / zDistance, znear / zDistance, view.Aspect, 1.0f / view.Aspect));
            HoianContext.WriteRow(block, 16, new Vector4(zDistance, 0, 0, 0));
        }

        public byte[] StorageBuffer(string name) => null;

        public GLTexture Texture(string sampler) => null;

        public GLTexture EnvironmentCube => HoianNXRender.EnvCubeArray;

        public bool UsesStorageBuffers => false;

        public bool MaterialBlockFromDefault => false;

        public void SetLights(ILightTable lights) { }

        public void ClearLights() { }
    }
}
