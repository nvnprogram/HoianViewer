using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BfresLibrary;
using ImGuiNET;
using PlayerViewer.HairGen;
using PlayerViewer.Phive;
using PlayerViewer.Physics;
using PlayerViewer.Rigging;
using Vector2 = System.Numerics.Vector2;
using Vector4 = OpenTK.Vector4;

namespace PlayerViewer.UI
{
    // The generator behind the physics authoring: the limbs of the standalone model rigged and
    // built into the cloth the Physics tab edits, and the model snapshots the undo history keeps.
    // Rig changes swap the shown model for the re-rigged one.
    public partial class ViewerWindow
    {
        HairGenerator _gen;
        byte[] _genOriginal;
        string _genActor;
        int _genSelected = -1;
        int _genHovered = -1;
        string _genError;
        bool _genTint = true;

        //The cloth version the generator last wrote, -1 when the document is not its cloth; a
        //later version is a hand edit a rebuild drops.
        int _genClothVersion = -1;

        //The generator's cloth came with hand edits, as a metadata load or an undo put it back.
        bool _genClothEdited;

        /// <summary>The document is the generated cloth as it is now, hand edited or not.</summary>
        void MarkGeneratedCloth(bool edited = false)
        {
            _genClothVersion = _cloth.Version;
            _genClothEdited = edited;
        }

        void UnmarkGeneratedCloth()
        {
            _genClothVersion = -1;
            _genClothEdited = false;
        }

        bool _selectPhysicsTab;

        /// <summary>
        /// The limbs and the model they made at one moment: each limb's settings (the limb objects
        /// themselves are kept, so their windows survive), the list's order, the generator's
        /// global settings and the exact model bytes shown.
        /// </summary>
        sealed class ModelSnapshot
        {
            public List<(PaintedLimb Limb, LimbState State)> Limbs;
            public List<(LimbCollider Collider, LimbCollider State)> Colliders;
            public Dictionary<string, LimbCollider> ColliderEdits;
            public byte[] ModelBytes;
            public float Spacing;
            public bool Merge;
            public bool Hair;

            //Everything above but the bytes, so two snapshots can be told apart cheaply.
            public string Key;
        }

        sealed record LimbState(
            string Name,
            HashSet<int> Nodes,
            List<string[]> BoneChains,
            StrandStyle Style,
            bool HoldOnHead,
            bool OwnPiece,
            string Hang,
            LimbAim Aim
        );

        /// <summary>The limbs and model now; painted sets equal to those of <paramref name="previous"/> share its copy.</summary>
        ModelSnapshot TakeModelSnapshot(ModelSnapshot previous = null)
        {
            HashSet<int> Nodes(PaintedLimb limb)
            {
                var kept = previous?.Limbs.FirstOrDefault(x => x.Limb == limb).State?.Nodes;
                return kept != null && kept.SetEquals(limb.Nodes)
                    ? kept
                    : new HashSet<int>(limb.Nodes);
            }
            var snapshot = new ModelSnapshot
            {
                Limbs = _gen
                    .Limbs.Select(l =>
                        (
                            l,
                            new LimbState(
                                l.Name,
                                Nodes(l),
                                l.BoneChains?.Select(c => (string[])c.Clone()).ToList(),
                                l.Style?.Clone(),
                                l.HoldOnHead,
                                l.OwnPiece,
                                l.Hang,
                                l.Aim
                            )
                        )
                    )
                    .ToList(),
                Colliders = _gen.UserColliders.Select(c => (c, c.Clone())).ToList(),
                ColliderEdits = _gen.ColliderEdits.ToDictionary(e => e.Key, e => e.Value.Clone()),
                ModelBytes = _standalone.SourceData,
                Spacing = _gen.RigOptions.Spacing,
                Merge = _gen.MergeCloseLimbs,
                Hair = _gen.Hair,
            };
            snapshot.Key = SnapshotKey(snapshot);
            return snapshot;
        }

        /// <summary>
        /// Puts the limbs back as a snapshot had them and shows its model bytes exactly, the rig
        /// and the generated cloth rebuilt to match.
        /// </summary>
        void RestoreModelSnapshot(ModelSnapshot snapshot, bool rebuildCloth = true)
        {
            if (_gen == null)
                return;
            _gen.Limbs.Clear();
            foreach (var (limb, state) in snapshot.Limbs)
            {
                limb.Name = state.Name;
                limb.Nodes = new HashSet<int>(state.Nodes);
                limb.BoneChains = state.BoneChains?.Select(c => (string[])c.Clone()).ToList();
                limb.Style = state.Style?.Clone();
                limb.HoldOnHead = state.HoldOnHead;
                limb.OwnPiece = state.OwnPiece;
                limb.Hang = state.Hang;
                limb.Aim = state.Aim;
                _gen.Limbs.Add(limb);
            }
            _gen.UserColliders.Clear();
            foreach (var (collider, state) in snapshot.Colliders)
            {
                collider.CopyFrom(state);
                _gen.UserColliders.Add(collider);
            }
            _gen.ColliderEdits.Clear();
            foreach (var (name, edit) in snapshot.ColliderEdits)
                _gen.ColliderEdits[name] = edit.Clone();
            _gen.RigOptions.Spacing = snapshot.Spacing;
            _gen.MergeCloseLimbs = snapshot.Merge;
            if (snapshot.Hair != _gen.Hair)
            {
                SetHairFlag(snapshot.Hair);
                _gen.SetHead(
                    snapshot.Hair ? HeadShape.ForHair(_romfs, _gen.Mesh, _genActor) : null
                );
            }
            _genError = null;
            _genNotice = null;
            try
            {
                _gen.BuildFromLimbs();
            }
            catch (Exception ex)
            {
                _genError = "Building the limbs failed: " + ex.Message;
                Console.WriteLine($"[HairGen] {ex}");
            }
            _genSelected = Math.Min(_genSelected, _gen.Limbs.Count - 1);
            ShowModelBytesUnlessSame(snapshot.ModelBytes);
            if (rebuildCloth && _gen.Rig != null)
                RebuildGeneratedCloth();
        }

        /// <summary>
        /// Authors the model as hair or not: the head it rests on, Keep segment length, and every
        /// limb and the cloth rebuilt, which drops hand edits of the generated cloth.
        /// </summary>
        void SetAuthorHair(bool hair)
        {
            if (hair == AuthorHair)
                return;
            AuthorStep(hair ? "Hair on" : "Hair off", () => ApplyAuthorHair(hair));
        }

        void ApplyAuthorHair(bool hair)
        {
            SetHairFlag(hair);
            _clothRuntime.Invalidate();
            if (_gen == null)
                return;
            _gen.SetHead(hair ? HeadShape.ForHair(_romfs, _gen.Mesh, _genActor) : null);
            if (_gen.Limbs.Count > 0)
                RebuildLimbs();
        }

        bool GenHasHandEdits =>
            _gen != null
            && _genClothVersion >= 0
            && _cloth != null
            && (_genClothEdited || _cloth.Version != _genClothVersion);

        void ResetHairGen()
        {
            _gen = null;
            _genOriginal = null;
            _genActor = null;
            _genSelected = _genHovered = -1;
            _genError = null;
            _genNotice = null;
            TestHookNote("generator reset");
            UnmarkGeneratedCloth();
            _pipeline.BoneTints = null;
            _paint = null;
            _paintStroke = false;
            _pipeline.LimbPaint = null;
            _paintOverlay?.Dispose();
            _paintOverlay = null;
            _aimDrag = AimPart.None;
            _aimCommit = null;
            _aimPreview.Clear();
            CloseLimbWindows();
        }

        const string PaintNote =
            "Paint all the way round each moving part, from where it leaves the model to its tip.";

        bool _showHead;
        HeadPreview _headPreview;

        /// <summary>Puts the stand-in head in the viewport while the Physics tab shows a standalone model and it is switched on.</summary>
        void UpdateHeadPreview()
        {
            if (
                !_showHead
                || !AuthorHair
                || !_physicsTabActive
                || _standalone == null
                || _animExporting
            )
            {
                _pipeline.Head = null;
                return;
            }
            _headPreview ??= new HeadPreview();
            _headPreview.Model = PaintTransform();
            _pipeline.Head = _headPreview;
        }

        int LimbChain(PaintedLimb limb) => _gen.ChainOf(limb);

        /// <summary>The limb's name as its bones carry it; two limbs must not share one.</summary>
        static string BoneSafeName(string name) => HairRigger.BoneSafe(name).ToLowerInvariant();

        void DeleteLimb(PaintedLimb limb)
        {
            int index = _gen.Limbs.IndexOf(limb);
            if (index < 0)
                return;
            AuthorStep(
                $"delete limb {limb.Name}",
                () =>
                {
                    _gen.Limbs.RemoveAt(index);
                    if (_genSelected == index)
                        _genSelected = -1;
                    else if (_genSelected > index)
                        _genSelected--;
                    RebuildLimbs();
                }
            );
        }

        static OpenTK.Vector3 StrandHue(int i)
        {
            float h = (i * 0.61803398f + 0.08f) % 1f;
            float r = Math.Abs(h * 6 - 3) - 1,
                g = 2 - Math.Abs(h * 6 - 2),
                b = 2 - Math.Abs(h * 6 - 4);
            return new OpenTK.Vector3(
                Math.Clamp(r, 0, 1),
                Math.Clamp(g, 0, 1),
                Math.Clamp(b, 0, 1)
            );
        }

        /// <summary>Each chain bone's tint: its limb's colour, deepening from root to tip, with the focused limb standing out.</summary>
        Dictionary<string, Vector4> GenTints()
        {
            var tints = new Dictionary<string, Vector4>();
            if (_gen.Rig == null)
                return tints;
            int focus = _genHovered >= 0 ? _genHovered : _genSelected;
            var chains = _gen.Rig.Chains;
            for (int i = 0; i < _gen.Limbs.Count; i++)
            {
                var hue = StrandHue(i);
                bool rigid = _gen.Limbs[i].Style?.Motion == StrandMotion.Rigid;
                float alpha =
                    rigid ? 0.25f
                    : focus < 0 || focus == i ? 1
                    : 0.35f;
                foreach (int c in _gen.ChainsOf(_gen.Limbs[i]))
                {
                    var bones = chains[c].Bones;
                    for (int j = 0; j < bones.Length; j++)
                    {
                        float t = bones.Length > 1 ? j / (float)(bones.Length - 1) : 1;
                        tints[_gen.Rig.Bones[bones[j]].Name] = new Vector4(
                            hue * (0.6f + 0.4f * t),
                            alpha
                        );
                    }
                }
            }
            return tints;
        }

        /// <summary>Makes the generator for the standalone model on first use: its mesh at rest and its head.</summary>
        bool EnsureGenerator()
        {
            if (_gen != null)
                return true;
            _genError = null;
            try
            {
                _clothSearched = true;
                _genActor = StandaloneActor;
                _genOriginal = AuthorHair
                    ? HeadRoot.Ensure(_standalone.SourceData)
                    : _standalone.SourceData;
                var mesh = SkinnedMesh.FromModel(Core.BfresBytes.FirstModel(_genOriginal));
                //Not hair: nothing is read of the player's head.
                _gen = new HairGenerator(
                    mesh,
                    AuthorHair ? HeadShape.ForHair(_romfs, mesh, _genActor) : null
                );
                return true;
            }
            catch (Exception ex)
            {
                _genError = "Generation failed: " + ex.Message;
                _gen = null;
                Console.WriteLine($"[HairGen] {ex}");
                return false;
            }
        }

        /// <summary>Starts the authoring over on a new generator, on <paramref name="baseBytes"/> as the model the rig is written into.</summary>
        void AdoptGenerator(SkinnedMesh mesh, byte[] baseBytes, bool hair)
        {
            ResetHairGen();
            _clothSearched = true;
            _genActor = StandaloneActor;
            _genOriginal = baseBytes;
            SetHairFlag(hair);
            _gen = new HairGenerator(
                mesh,
                hair ? HeadShape.ForHair(_romfs, mesh, _genActor) : null
            );
        }

        /// <summary>Builds the rig from every limb, shows the model it makes and rebuilds the cloth.</summary>
        void RebuildLimbs()
        {
            _genError = null;
            try
            {
                _gen.BuildFromLimbs();
                _genSelected = Math.Min(_genSelected, _gen.Limbs.Count - 1);
                ShowGeneratedModel();
                RebuildGeneratedCloth();
                Console.WriteLine(
                    $"[HairGen] {_genActor}: {_gen.Limbs.Count} limb(s), {_gen.Rig.Chains.Count} chain(s), {_gen.Rig.Bones.Count - _gen.Rig.OriginalBoneCount} bone(s) added"
                );
            }
            catch (Exception ex)
            {
                _genError = "Building the limbs failed: " + ex.Message;
                Console.WriteLine($"[HairGen] {ex}");
            }
        }

        /// <summary>
        /// The model as opened (<see cref="_genOriginal"/>) with the rig written into it, so a
        /// limb that is gone leaves nothing behind; as opened when the rig changes nothing.
        /// </summary>
        byte[] GeneratedModelBytes()
        {
            if (!_gen.ChangesModel)
                return _genOriginal;
            var res = Core.BfresBytes.Read(_genOriginal);
            _gen.WriteModel(res.Models.Values.First());
            return Core.BfresBytes.ToBytes(res);
        }

        /// <summary>Swaps the shown model for the one the rig makes, keeping the camera and the cloth editor.</summary>
        void ShowGeneratedModel() => ShowModelBytes(GeneratedModelBytes());

        /// <summary>Swaps the shown model for these bytes, keeping the animation playing, its frame and its pause.</summary>
        void ShowModelBytes(byte[] bytes)
        {
            var old = _standalone;
            if (ReferenceEquals(bytes, old.SourceData))
                return;
            var (name, source) = (old.Name, old.SourcePath);
            var (anim, frame, paused) = (old.CurrentAnimName, old.AnimFrame, old.AnimPaused);
            old.Dispose();
            _selectedMaterial = null;
            _selectedTexture = null;
            ResetVariations();
            ResetMaterialEditor();
            _standalone = Player.StandaloneScene.FromBytes(bytes, name, source, _romfs);
            _standaloneError = _standalone == null ? "The rigged model failed to load" : null;
            _clothRuntime.Invalidate();
            if (_standalone == null)
                return;
            if (anim != null && _standalone.AnimNames.Contains(anim))
            {
                _standalone.PlayAnim(anim);
                _standalone.SetAnimFrame(frame);
                _standalone.AnimPaused = paused;
            }
        }

        /// <summary>
        /// Builds the cloth from the rig and the styles and makes it the document, as one undo step.
        /// With <paramref name="keepMotion"/> a cloth of the same pieces and particles carries on
        /// moving rather than restarting, as a style edit wants.
        /// </summary>
        void RebuildGeneratedCloth(bool keepMotion = false)
        {
            try
            {
                var file = EmptyGeneratedCloth();
                _gen.BuildCloth(file);
                string entry = GeneratedEntry(_genActor, _gen.Hair);
                if (_cloth == null || _cloth.Source.Entry != entry)
                {
                    var source = new ClothSource { Entry = entry, IsNew = true };
                    source.Actors.Add(_genActor);
                    _clothSaveActors = _genActor;
                    OpenClothDocument(file.Snapshot(), source);
                }
                else
                {
                    int structure = _cloth.StructureVersion;
                    _cloth.Replace(file, keepMotion);
                    _cloth.Commit();
                    if (_cloth.StructureVersion != structure)
                        _clothRuntime.Invalidate();
                }
                MarkGeneratedCloth();
                ValidateClothSelection();
            }
            catch (Exception ex)
            {
                _genError = "Building the cloth failed: " + ex.Message;
                Console.WriteLine($"[HairGen] {ex}");
            }
        }

        /// <summary>An emptied cloth declaring the piece and collidable types, which the generator builds into.</summary>
        ClothFile EmptyGeneratedCloth() =>
            Packs
                .EmptyCloth()
                .WithTypes(
                    ClothAuthor.PieceTypes.Concat(ClothAuthor.CollidableTypes),
                    Packs.DonorTypeSections()
                );

        /// <summary>
        /// Writes the shown model into the mod romfs beside the packs, with the physics metadata:
        /// always, since the metadata lives in it and the model may not be the game's own.
        /// </summary>
        string WriteGeneratedModel(string root, List<string> report)
        {
            if (_standalone == null)
                return null;
            var bytes = WithProvenanceFor(_standalone.SourceData, report);
            string dir = Path.Combine(root, "Model");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, (_genActor ?? StandaloneActor) + ".bfres.zs");
            Core.AtomicFile.Write(path, bytes, compress: true);
            return path;
        }
    }
}
