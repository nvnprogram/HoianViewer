using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using PlayerViewer.Core;
using PlayerViewer.Core.Formats;
using BntxFile = Syroot.NintenTools.NSW.Bntx.BntxFile;

namespace PlayerViewer.Icons
{
    /// <summary>One layout archive: its layout, its animations and its decoded textures.</summary>
    public sealed class LayoutArchive
    {
        public string Name;
        public Bflyt Layout;
        public readonly Dictionary<string, Bflan> Anims = new(StringComparer.Ordinal);
        readonly Dictionary<string, Surface> _decoded = new(StringComparer.Ordinal);
        BntxFile _bntx;

        public static LayoutArchive Load(Romfs romfs, string name)
        {
            var data = romfs.ReadFile($"Layout/{name}.Nin_NX_NVN.blarc");
            if (data == null)
                return null;
            var sarc = new Sarc(data);
            var archive = new LayoutArchive { Name = name };
            foreach (var (path, bytes) in sarc.Files)
            {
                if (path.EndsWith(".bflyt"))
                    archive.Layout = new Bflyt(bytes.ToArray());
                else if (path.EndsWith(".bflan"))
                    archive.Anims[Path.GetFileNameWithoutExtension(path)] = new Bflan(
                        bytes.ToArray()
                    );
                else if (path.EndsWith(".bntx"))
                    archive._bntx = new BntxFile(new MemoryStream(bytes.ToArray()));
            }
            return archive.Layout != null && archive._bntx != null ? archive : null;
        }

        public Bflan Anim(string suffix) => Anims.GetValueOrDefault($"{Name}_{suffix}");

        public Surface Texture(string name)
        {
            if (name == null)
                return null;
            if (_decoded.TryGetValue(name, out var surface))
                return surface;
            var texture = _bntx.Textures.FirstOrDefault(t => t.Name == name);
            surface = texture != null ? Surface.Decode(_bntx, texture) : null;
            _decoded[name] = surface;
            return surface;
        }
    }

    /// <summary>
    /// Icons that the game only has as layout parts, put together the way the layout draws
    /// them. A picture's colour is its material's black and white colours blended by the
    /// texture and multiplied by the pane's vertex colours, all in linear space.
    /// </summary>
    public static class LayoutIcons
    {
        /// <summary>The colour layout's Variation animation keeps skin tones from frame 0
        /// and eye colours from this frame on.</summary>
        public const int EyeFrameBase = 20;

        /// <summary>
        /// A bottom: a shape and a detail layer, chosen by the Type animation at the row's
        /// Id. A bottom with variations has its own pattern animation, recognised by
        /// starting on the same two textures.
        /// </summary>
        public static IconImage Bottom(LayoutArchive archive, int frame, int variation)
        {
            var type = archive?.Anim("Type");
            if (type == null || !HasKey(type, "P_Btn_00", frame))
                return null;
            string[] layers = { "P_Btn_00", "P_Btn_01" };
            var textures = layers.Select(l => PatternTexture(type, l, frame)).ToArray();

            if (variation > 0)
            {
                foreach (var (name, anim) in archive.Anims)
                {
                    if (!name.Contains("_Pattern"))
                        continue;
                    var first = layers.Select(l => PatternTexture(anim, l, 0)).ToArray();
                    if (first.SequenceEqual(textures))
                    {
                        textures = layers.Select(l => PatternTexture(anim, l, variation)).ToArray();
                        break;
                    }
                }
            }
            return Composite(archive, layers, textures, null, 0);
        }

        /// <summary>A hair on the head shape, both chosen by the Type animation at the frame.</summary>
        public static IconImage Hair(LayoutArchive archive, int frame)
        {
            var type = archive?.Anim("Type");
            if (type == null || !HasKey(type, "P_Btn_01", frame))
                return null;
            string[] layers = { "P_Btn_00", "P_Btn_01" };
            var textures = layers.Select(l => PatternTexture(type, l, frame)).ToArray();
            return Composite(archive, layers, textures, null, 0);
        }

        /// <summary>An eyebrow pair alone, chosen by the TypeEyebrow animation at the frame.</summary>
        public static IconImage Eyebrow(LayoutArchive archive, int frame)
        {
            var type = archive?.Anim("TypeEyebrow");
            if (type == null || !HasKey(type, "P_Eyebrow_00", frame))
                return null;
            string[] layers = { "P_Eyebrow_00" };
            var textures = layers.Select(l => PatternTexture(type, l, frame)).ToArray();
            return Composite(archive, layers, textures, null, 0);
        }

        /// <summary>A player type: body, hair and the hair's colour layer.</summary>
        public static IconImage PlayerType(LayoutArchive archive, int type)
        {
            var anim = archive?.Anim("Variation");
            if (anim == null)
                return null;
            string[] layers = { "P_Base_00", "P_HairBase_00", "P_HairColor_00" };
            var textures = layers.Select(l => PatternTexture(anim, l, type)).ToArray();
            return Composite(archive, layers, textures, null, 0);
        }

        /// <summary>
        /// A skin tone or eye colour swatch. The Variation animation sets the colours per
        /// frame and slides the second texture of each layer, a wave mask, into view for the
        /// eye colours that have a pupil or a second tone.
        /// </summary>
        public static IconImage Colour(LayoutArchive archive, int frame)
        {
            var anim = archive?.Anim("Variation");
            if (anim == null)
                return null;
            string[] layers = { "P_Btn_00", "P_Btn_02" };
            return Composite(archive, layers, new string[layers.Length], anim, frame);
        }

        //A frame past the last key would evaluate to the last texture, not to nothing.
        static bool HasKey(Bflan anim, string pane, int frame) =>
            anim.Find(pane, "FLTP", 0, 0)?.Keys.Any(k => k.Frame == frame) == true;

        static string PatternTexture(Bflan anim, string pane, int frame)
        {
            var curve = anim.Find(pane, "FLTP", 0, 0);
            if (curve == null)
                return null;
            int index = (int)curve.Evaluate(frame);
            return index >= 0 && index < anim.Textures.Count ? anim.Textures[index] : null;
        }

        //Textures named per layer override the material's first texture; the animation, when
        //given, overrides colours and texture transforms at the frame.
        static IconImage Composite(
            LayoutArchive archive,
            string[] layers,
            string[] textures,
            Bflan anim,
            int frame
        )
        {
            Surface canvas = null;
            for (int l = 0; l < layers.Length; l++)
            {
                if (!archive.Layout.Pictures.TryGetValue(layers[l], out var pic))
                    continue;
                var mat = pic.Material;
                if (mat == null || mat.Textures.Length == 0)
                    continue;
                var baseTex = archive.Texture(
                    textures[l] ?? archive.Layout.Textures.ElementAtOrDefault(mat.Textures[0])
                );
                if (baseTex == null)
                    continue;
                canvas ??= new Surface(baseTex.Width, baseTex.Height);

                var black = AnimColor(anim, mat.Name, 0, mat.Black, frame);
                var white = AnimColor(anim, mat.Name, 4, mat.White, frame);
                Surface maskTex = null;
                Bflyt.TexSrt srt = default;
                if (mat.Textures.Length > 1 && mat.Srts.Length > 1)
                {
                    maskTex = archive.Texture(
                        archive.Layout.Textures.ElementAtOrDefault(mat.Textures[1])
                    );
                    srt = AnimSrt(anim, mat.Name, mat.Srts[1], frame);
                }
                Draw(canvas, pic, baseTex, maskTex, srt, black, white);
            }
            return canvas?.Trimmed(2).ToImage();
        }

        static void Draw(
            Surface canvas,
            Bflyt.Picture pic,
            Surface baseTex,
            Surface maskTex,
            Bflyt.TexSrt srt,
            Vector4 black,
            Vector4 white
        )
        {
            var vc = pic.VertexColors;
            float rad = srt.Rotate * MathF.PI / 180;
            float cos = MathF.Cos(rad),
                sin = MathF.Sin(rad);
            for (int y = 0; y < canvas.Height; y++)
            {
                float v = (y + 0.5f) / canvas.Height;
                var left = Vector4.Lerp(vc[0], vc[2], v);
                var right = Vector4.Lerp(vc[1], vc[3], v);
                for (int x = 0; x < canvas.Width; x++)
                {
                    float u = (x + 0.5f) / canvas.Width;
                    var tex = baseTex.Sample(u, v);
                    var blend = tex;
                    //With a second texture the first gives the shape and the second, moved by
                    //its texture transform, does the blending.
                    if (maskTex != null)
                    {
                        float cu = u - 0.5f,
                            cv = v - 0.5f;
                        blend = maskTex.Sample(
                            (cu * cos - cv * sin) * srt.Scale.X + 0.5f + srt.Translate.X,
                            (cu * sin + cv * cos) * srt.Scale.Y + 0.5f + srt.Translate.Y
                        );
                    }
                    var color = Vector4.Lerp(black, white, blend) * Vector4.Lerp(left, right, u);
                    float alpha = color.W * (maskTex != null ? tex.W : 1);
                    canvas.Over(
                        y * canvas.Width + x,
                        new Vector3(color.X, color.Y, color.Z),
                        alpha
                    );
                }
            }
        }

        static Vector4 AnimColor(
            Bflan anim,
            string material,
            int firstTarget,
            Vector4 fallback,
            int frame
        )
        {
            if (anim == null)
                return fallback;
            float Channel(int i, float value)
            {
                var curve = anim.Find(material, "FLMC", 0, firstTarget + i);
                return curve != null ? curve.Evaluate(frame) / 255f : value;
            }
            return new Vector4(
                Channel(0, fallback.X),
                Channel(1, fallback.Y),
                Channel(2, fallback.Z),
                Channel(3, fallback.W)
            );
        }

        static Bflyt.TexSrt AnimSrt(Bflan anim, string material, Bflyt.TexSrt srt, int frame)
        {
            if (anim == null)
                return srt;
            float Value(int target, float value)
            {
                var curve = anim.Find(material, "FLTS", 1, target);
                return curve != null ? curve.Evaluate(frame) : value;
            }
            return new Bflyt.TexSrt
            {
                Translate = new Vector2(Value(0, srt.Translate.X), Value(1, srt.Translate.Y)),
                Rotate = Value(2, srt.Rotate),
                Scale = new Vector2(Value(3, srt.Scale.X), Value(4, srt.Scale.Y)),
            };
        }
    }
}
