using System;
using System.Collections.Generic;
using System.Linq;
using PlayerViewer.Phive;
using Toolbox.Core;

namespace PlayerViewer.Physics
{
    /// <summary>
    /// The cloth of the file being edited, simulated on a standalone model's skeleton. Pieces are
    /// recompiled from the graph whenever the document changes, and each new instance takes the
    /// particles of the one it replaces, so an edit shows on the running cloth rather than
    /// restarting it.
    /// </summary>
    public class ClothRuntime
    {
        public class Piece
        {
            public int Index;
            public string Name;
            public ClothPiece Compiled;
            public ClothInstance Sim;
            public STBone[] Bones;

            /// <summary>Why the piece does not run, null when it does.</summary>
            public string Problem;
        }

        public readonly List<Piece> Pieces = new();

        /// <summary>Shared by every instance, so toggling a flag applies on the next step.</summary>
        public readonly ClothInstanceOptions Options = new();

        public bool Enabled = true;

        int _version = -1;
        int _structure = -1;

        /// <summary>Recompiles when the document moved; keeps the running state unless its shape changed.</summary>
        public void Sync(ClothDocument doc, IReadOnlyList<STSkeleton> skeletons)
        {
            if (doc == null)
            {
                Pieces.Clear();
                _version = _structure = -1;
                return;
            }
            if (doc.Version == _version)
                return;
            bool keep = doc.StructureVersion == _structure;
            _version = doc.Version;
            _structure = doc.StructureVersion;
            Build(doc.File, skeletons, keep);
        }

        void Build(ClothFile file, IReadOnlyList<STSkeleton> skeletons, bool keepState)
        {
            var old = Pieces.ToList();
            Pieces.Clear();
            var datas = file.Container?.ClothDatas ?? new List<ClothData>();
            for (int i = 0; i < datas.Count; i++)
            {
                var piece = new Piece { Index = i, Name = datas[i].Name };
                Pieces.Add(piece);
                try
                {
                    piece.Compiled = ClothPiece.Compile(file, datas[i], out string why);
                    if (piece.Compiled == null)
                    {
                        piece.Problem = why;
                        continue;
                    }
                    piece.Bones = piece
                        .Compiled.BoneNames.Select(n => Find(skeletons, n))
                        .ToArray();
                    var missing = piece
                        .Compiled.BoneNames.Where((n, b) => piece.Bones[b] == null)
                        .ToList();
                    if (missing.Count > 0)
                    {
                        piece.Problem = "bones not in the model: " + string.Join(", ", missing);
                        continue;
                    }
                    piece.Sim = ClothInstance.Create(
                        piece.Compiled,
                        new SceneBoneBinding(piece.Bones),
                        Options
                    );
                    if (piece.Sim == null)
                    {
                        piece.Problem = "the piece drives no bone";
                        continue;
                    }
                    if (keepState)
                        piece.Sim.CopyStateFrom(old.FirstOrDefault(o => o.Index == i)?.Sim);
                }
                catch (Exception ex)
                {
                    piece.Problem = ex.Message;
                    piece.Sim = null;
                }
            }
        }

        static STBone Find(IReadOnlyList<STSkeleton> skeletons, string name)
        {
            foreach (var s in skeletons)
            {
                var b = s.SearchBone(name);
                if (b != null)
                    return b;
            }
            return null;
        }

        /// <summary>
        /// Steps every running piece; the skeleton must hold this frame's pose. When not enabled
        /// the cloth is held where it is: its bones are still written, nothing moves.
        /// </summary>
        public void Update(float dt)
        {
            foreach (var p in Pieces)
                p.Sim?.Update(Enabled ? dt : 0);
        }

        public void Reset()
        {
            foreach (var p in Pieces)
                p.Sim?.Reset();
        }

        /// <summary>Forces a full rebuild on the next sync, dropping the running state.</summary>
        public void Invalidate() => _version = _structure = -1;
    }
}
