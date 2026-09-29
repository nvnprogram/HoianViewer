using System.Collections.Generic;
using OpenTK;
using PlayerViewer.Phive;
using PlayerViewer.Physics;
using Toolbox.Core;

namespace PlayerViewer.Player
{
    /// <summary>
    /// A hair's cloth: the pieces of its bphcl bound to the hair and player skeletons, with the
    /// hair specific parts on top of the Phive layer. The hair model's own Spine_3 follows the
    /// player's, and the arrange preset's AnimReduceRt is each bone's cloth weight.
    /// </summary>
    public class HairCloth
    {
        readonly List<Piece> _pieces = new();

        class Piece
        {
            public ClothInstance Sim;
            public STBone[] Bones;
            public float[] Weights;
        }

        public int PieceCount => _pieces.Count;

        /// <summary>Loads a hair's .bphcl and binds every piece that resolves against the skeletons.</summary>
        public static HairCloth Load(
            byte[] bphcl,
            STSkeleton hairSkeleton,
            STSkeleton humanSkeleton
        )
        {
            var file = ClothFile.Load(bphcl);
            var cloth = new HairCloth();
            //The player's cloth component keeps segment lengths.
            var options = new ClothInstanceOptions { KeepSegmentLength = true };
            foreach (var piece in ClothPiece.CompileAll(file))
            {
                var bones = ResolveBones(piece, hairSkeleton, humanSkeleton);
                if (bones == null)
                    continue;
                var sim = ClothInstance.Create(piece, new SceneBoneBinding(bones), options);
                if (sim != null)
                    cloth._pieces.Add(
                        new Piece
                        {
                            Sim = sim,
                            Bones = bones,
                            Weights = new float[bones.Length],
                        }
                    );
            }
            return cloth;
        }

        /// <summary>
        /// Cloth bones by name against the hair skeleton first, then the player's. The hair model
        /// carries its own Spine_3 under Head_Root, unanimated; the game overwrites its world
        /// matrix with the body's Spine_3 every frame, so the chest capsule follows the spine.
        /// </summary>
        static STBone[] ResolveBones(
            ClothPiece piece,
            STSkeleton hairSkeleton,
            STSkeleton humanSkeleton
        )
        {
            var bones = new STBone[piece.BoneNames.Length];
            for (int i = 0; i < bones.Length; i++)
            {
                string name = piece.BoneNames[i];
                bones[i] =
                    name == "Spine_3"
                        ? humanSkeleton?.SearchBone(name) ?? hairSkeleton.SearchBone(name)
                        : hairSkeleton.SearchBone(name) ?? humanSkeleton?.SearchBone(name);
                if (bones[i] == null)
                    return null;
            }
            return bones;
        }

        /// <summary>
        /// Runs after the hair weld; the arrange preset's AnimReduceRt weights each driven bone
        /// between the cloth (1) and the arranged pose (0).
        /// </summary>
        public void Update(
            float dt,
            Dictionary<string, ArrangeBoneParam> arrange,
            float convergeWeight
        )
        {
            foreach (var piece in _pieces)
            {
                for (int i = 0; i < piece.Bones.Length; i++)
                    piece.Weights[i] =
                        arrange != null && arrange.TryGetValue(piece.Bones[i].Name, out var arr)
                            ? MathHelper.Clamp(arr.AnimReduce, 0, 1)
                            : 1.0f;
                piece.Sim.Update(dt, piece.Weights, convergeWeight);
            }
        }

        public void Reset()
        {
            foreach (var piece in _pieces)
                piece.Sim.Reset();
        }

        public void CaptureConvergeState()
        {
            foreach (var piece in _pieces)
                piece.Sim.CaptureConvergeState();
        }

        public void DebugDump()
        {
            foreach (var piece in _pieces)
                piece.Sim.DebugDump();
        }
    }
}
