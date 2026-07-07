using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BfresEditor;
using GLFrameworkEngine;
using OpenTK;
using PlayerViewer.Core;
using PlayerViewer.Core.Formats;
using Toolbox.Core;
using Toolbox.Core.IO;

namespace PlayerViewer.Player
{
    /// <summary>
    /// The assembled player: human base model + welded gear/weapon parts + animation
    /// state. Owns loading, welding, colors and per-frame updates.
    /// </summary>
    public class PlayerScene : IDisposable, IViewScene
    {
        public Romfs Romfs;
        public GameDatabase Database;

        //--- Configuration
        public int PlayerType { get; private set; } = 0;    //0=Inkling F,1=Inkling M,2=Octoling F,3=Octoling M
        public bool IsFemale => PlayerType == 0 || PlayerType == 2;
        public string PlayerModelName => $"Player{PlayerType:00}";

        public GearEntry CurrentHair, CurrentEyebrow, CurrentHead, CurrentClothes,
            CurrentShoes, CurrentBottom, CurrentTank, CurrentWeapon;
        public int EyeColor = 0;      //0..20 (Color_Eye TPA frame)
        public int SkinTone = 0;      //0..8  (Color_Skin SPA frame)
        public int TeamColorIndex = 0;
        public int TeamIndex = 0;     //0=Alpha 1=Bravo 2=Charlie

        //--- Runtime state
        public PartModel Human { get; private set; }
        public readonly Dictionary<PartKind, PartModel> Parts = new();
        public AnimLibrary Anims { get; } = new AnimLibrary();

        public string CurrentAnimName { get; private set; }
        public BfresSkeletalAnim CurrentSkeletal { get; private set; }
        public BfresMaterialAnim CurrentTexPattern { get; private set; }
        public BfresMaterialAnim CurrentShaderParam { get; private set; }
        public BfresVisibilityAnim CurrentBoneVis { get; private set; }
        public float AnimFrame { get; private set; }
        public float AnimSpeed = 1.0f;
        public bool AnimPaused = false;

        public event Action OnPartsChanged;

        int _renderIdCounter = 0;
        readonly AlphaMaskSystem _alphaMask = new();
        List<FMAT> _bodyMaskMaterials;   //M_Body materials with the _o0 override active

        //Hair cloth simulation (bphcl), one sim per cloth piece.
        public bool HairPhysicsEnabled = true;
        readonly List<HairPhysics> _hairPhysics = new();

        public PlayerScene(Romfs romfs, GameDatabase database)
        {
            Romfs = romfs;
            Database = database;
        }

        #region Model loading

        /// <summary>
        /// Loads a bfres model by romfs model name into a renderable BFRES.
        /// </summary>
        public BFRES LoadModelFile(string modelName)
        {
            var data = Romfs.ReadModel(modelName);
            if (data == null)
            {
                Console.WriteLine($"[Scene] Model not found: {modelName}");
                return null;
            }
            var bfres = LoadModelData(data, Path.Combine(Romfs.Root, "Model", modelName + ".bfres"));

            //Variant weapons (Wmn_X_Cstm01) only embed their re-authored textures;
            //everything else (textures AND the fold/carry skeletal anims) resolves
            //from the base weapon's archive.
            int cstm = modelName.IndexOf("_Cstm", StringComparison.Ordinal);
            if (bfres != null && cstm > 0)
                MergeBaseAssets(bfres, modelName.Substring(0, cstm));
            return bfres;
        }

        void MergeBaseAssets(BFRES bfres, string baseModelName)
        {
            try
            {
                var baseData = Romfs.ReadModel(baseModelName);
                if (baseData == null)
                    return;
                var render = (BfresRender)bfres.Renderer;
                var res = new BfresLibrary.ResFile(new MemoryStream(baseData));
                int added = 0;
                foreach (var tex in res.Textures.Values)
                {
                    if (render.Textures.ContainsKey(tex.Name))
                        continue;
                    if (tex is BfresLibrary.Switch.SwitchTexture st)
                    {
                        render.Textures.Add(tex.Name, new BntxTexture(st.BntxFile, st.Texture));
                        added++;
                    }
                }
                int anims = 0;
                foreach (var anim in res.SkeletalAnims.Values)
                {
                    if (bfres.SkeletalAnimations.Any(a => a.Name == anim.Name))
                        continue;
                    bfres.SkeletalAnimations.Add(new BfresSkeletalAnim(res, anim, anim.Name));
                    anims++;
                }
                if (added > 0 || anims > 0)
                    Console.WriteLine($"[Scene] Merged {added} textures, {anims} anims from {baseModelName}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Scene] Base asset merge failed ({baseModelName}): {ex.Message}");
            }
        }

        /// <summary>
        /// Loads a bfres from raw (already decompressed) bytes. The fake path keeps
        /// shader lookup working (romfs/Shader is found relative to romfs/Model).
        /// </summary>
        public BFRES LoadModelData(byte[] data, string fakePath)
        {
            IFileFormat format;
            using (var ms = new MemoryStream(data))
                format = STFileLoader.OpenFileFormat(ms, fakePath);

            if (format is not BFRES bfres)
            {
                Console.WriteLine($"[Scene] Not a BFRES: {fakePath}");
                return null;
            }

            var render = (BfresRender)bfres.Renderer;
            render.ID = (_renderIdCounter++).ToString() + "_" + Path.GetFileNameWithoutExtension(fakePath);
            DataCache.ModelCache[render.ID] = render;
            return bfres;
        }

        //Unequipped part renders parked for reuse: a fresh load pays bfres parse +
        //shader lookup + GL texture upload (a 100-600 ms hitch on equip), while a
        //cached render re-equips instantly. LRU-capped; state that varies per equip
        //(variation mat anims, welds, bone visibility, arrange) is re-applied by the
        //setters anyway.
        const int PartCacheCapacity = 16;
        readonly Dictionary<string, BFRES> _partCache = new();
        readonly List<string> _partCacheLru = new();    //front = oldest

        BFRES TakeCachedPart(string modelName)
        {
            if (!_partCache.Remove(modelName, out var bfres))
                return null;
            _partCacheLru.Remove(modelName);
            var render = (BfresRender)bfres.Renderer;
            DataCache.ModelCache[render.ID] = render;
            return bfres;
        }

        void ParkPart(string cacheKey, BFRES bfres)
        {
            var render = (BfresRender)bfres.Renderer;
            DataCache.ModelCache.Remove(render.ID);
            //Duplicate key (both shoes use one model name) or uncacheable: drop it.
            if (cacheKey == null || _partCache.ContainsKey(cacheKey))
            {
                render.Dispose();
                return;
            }
            _partCache[cacheKey] = bfres;
            _partCacheLru.Add(cacheKey);
            if (_partCacheLru.Count > PartCacheCapacity)
            {
                string evict = _partCacheLru[0];
                _partCacheLru.RemoveAt(0);
                ((BfresRender)_partCache[evict].Renderer).Dispose();
                _partCache.Remove(evict);
            }
        }

        void ClearPartCache()
        {
            foreach (var bfres in _partCache.Values)
                ((BfresRender)bfres.Renderer).Dispose();
            _partCache.Clear();
            _partCacheLru.Clear();
        }

        /// <summary>
        /// subModel: name of the model inside the bfres when it contains several
        /// (dualies: Wmn_X + Wmn_X_L in one file). Other models get hidden.
        /// </summary>
        PartModel CreatePart(PartKind kind, string modelName, BFRES loaded = null, string subModel = null)
        {
            if (string.IsNullOrEmpty(modelName))
                return null;

            //Parts created from a pre-loaded BFRES (custom drops) are not cacheable.
            string cacheKey = loaded == null ? modelName : null;
            var bfres = loaded ?? TakeCachedPart(modelName) ?? LoadModelFile(modelName);
            if (bfres == null || bfres.Renderer.Models.Count == 0)
                return null;

            var render = (BfresRender)bfres.Renderer;
            var asset = (BfresModelAsset)render.Models[0];
            if (subModel != null && render.Models.Count > 1)
            {
                asset = render.Models.OfType<BfresModelAsset>()
                    .FirstOrDefault(m => m.ModelData.Name == subModel) ?? asset;
            }
            //Hide all but the selected model (each part owns its own BFRES instance).
            foreach (var m in render.Models.OfType<BfresModelAsset>())
                m.IsVisible = m == asset;

            return new PartModel
            {
                Kind = kind,
                ModelName = subModel ?? modelName,
                Bfres = bfres,
                Render = render,
                ModelAsset = asset,
                Skeleton = asset.ModelData.Skeleton,
                CacheKey = cacheKey,
            };
        }

        void DestroyPart(PartKind kind)
        {
            if (!Parts.TryGetValue(kind, out var part))
                return;
            Parts.Remove(kind);
            ParkPart(part.CacheKey, part.Bfres);
        }

        #endregion

        #region Player type / human

        /// <summary>
        /// Loads (or reloads) the human base model and animation library, then
        /// re-applies the current configuration.
        /// </summary>
        public void SetPlayerType(int type)
        {
            PlayerType = type;

            //Clear all parts (models are gender specific; parked in the part cache
            //so flipping back is instant).
            foreach (var kind in Parts.Keys.ToList())
                DestroyPart(kind);
            if (Human != null)
            {
                ParkPart(Human.CacheKey, Human.Bfres);
                Human = null;
            }

            //Animation parsing is pure CPU work (no GL), so it can run alongside the
            //human model load instead of after it.
            var animTask = System.Threading.Tasks.Task.Run(() => Anims.Load(Romfs, PlayerModelName));

            Human = CreatePart(PartKind.Human, PlayerModelName);
            if (Human == null)
                throw new Exception($"Failed to load {PlayerModelName}");

            animTask.Wait();

            //Defaults: id 0 rows. Weapon defaults to none ("Free" in game terms).
            //Head defaults to Hed_INV000 (the game's invisible no-headgear actor,
            //which still applies default hair-arrange presets).
            CurrentHead ??= Database.Head.FirstOrDefault(x => x.RowId == "Hed_INV000");
            CurrentHair ??= Database.Hair.FirstOrDefault(x => x.Id == 0) ?? Database.Hair.FirstOrDefault();
            CurrentEyebrow ??= Database.Eyebrow.FirstOrDefault(x => x.Id == 0) ?? Database.Eyebrow.FirstOrDefault();
            CurrentBottom ??= Database.Bottom.FirstOrDefault(x => x.Id == 0) ?? Database.Bottom.FirstOrDefault();
            CurrentTank ??= Database.Tank.FirstOrDefault(x => x.Id == 0) ?? Database.Tank.FirstOrDefault();

            //Rebuild all parts against the new skeleton.
            SetHair(CurrentHair);
            SetEyebrow(CurrentEyebrow);
            SetGear(GearSlot.Head, CurrentHead);
            SetGear(GearSlot.Clothes, CurrentClothes);
            SetGear(GearSlot.Shoes, CurrentShoes);
            SetBottom(CurrentBottom);
            SetTank(CurrentTank);
            SetWeapon(CurrentWeapon);
            ApplyEyeColor(EyeColor);
            ApplySkinTone(SkinTone);

            //Restore/replay animation
            PlayAnim(CurrentAnimName ?? "Wait");

            OnPartsChanged?.Invoke();
        }

        string GenderSuffix => IsFemale ? "_F" : "_M";

        /// <summary>
        /// Some gear actors are split per gender (Clt_AMB007_F/_M) while their RSDB
        /// row keeps the base name; try the plain actor first, then the gendered one.
        /// </summary>
        string ResolveGearModel(string rowId) =>
            Database.ResolveModelName(rowId) ?? Database.ResolveModelName(rowId + GenderSuffix);

        /// <summary>
        /// Variation 1 of some gear swaps to a dedicated "_V1" model with different
        /// geometry (e.g. Shs_HAP016_V1 drops the socks); texture-only variations
        /// stay on the base model and use the model-named material anim instead.
        /// Some _V1 actors point at models that don't exist (Btm_001_V1) - those
        /// fall back to the base model.
        /// </summary>
        string ResolveGearModelForEntry(GearEntry entry)
        {
            if (entry.Variation >= 1)
            {
                string v1 = ResolveGearModel(entry.RowId + "_V1");
                if (v1 != null && Romfs.ModelExists(v1))
                    return v1;
            }
            return ResolveGearModel(entry.RowId);
        }

        /// <summary>
        /// Texture variations are frames of a material anim named after the model.
        /// </summary>
        void ApplyVariationAnim(PartModel part, GearEntry entry)
        {
            if (part?.Bfres == null || entry == null)
                return;
            var varAnim = part.Bfres.MaterialAnimations.FirstOrDefault(x => x.Name == part.ModelName);
            if (varAnim != null)
                ScopedAnimPlayer.ApplyMaterialAnim(varAnim, entry.Variation, new[] { part.ModelAsset });
        }

        #endregion

        #region Gear setters

        static readonly Dictionary<string, string> HeadBoneMap = new()
        {
            { "Root", "Head" },
            { "Root_Model", "Head" },
            { "Head_Root", "Head" },
        };

        public void SetHair(GearEntry entry)
        {
            CurrentHair = entry;
            DestroyPart(PartKind.Hair);
            _hairPhysics.Clear();
            if (entry == null) { OnPartsChanged?.Invoke(); return; }

            string modelName = entry.IsCustom ? entry.RowId : entry.RowId + GenderSuffix;
            var part = entry.IsCustom
                ? CreateCustomPart(PartKind.Hair, entry)
                : CreatePart(PartKind.Hair, modelName);
            if (part == null) { OnPartsChanged?.Invoke(); return; }

            part.ResolveWelds(Human.Skeleton, HeadBoneMap);
            part.AttachBone = part.Skeleton.SearchBone("Head_Root") ?? part.Skeleton.SearchBone("Root");
            Parts[PartKind.Hair] = part;

            LoadHairPhysics(entry, part);

            //Re-apply headgear so its hair-arrange / SRT for this hair kicks in.
            ApplyHeadgearParams();
            ApplyGearSkinColor();
            OnPartsChanged?.Invoke();
        }

        /// <summary>
        /// Loads the hair's cloth file (Phive/Cloth/*.bphcl in its actor pack) and
        /// creates one simulation per cloth piece. Missing cloth is normal (many
        /// hairs are rigid).
        /// </summary>
        void LoadHairPhysics(GearEntry entry, PartModel part)
        {
            if (entry.IsCustom)
                return;
            try
            {
                var pack = Romfs.GetActorPack(entry.RowId + GenderSuffix);
                string file = pack?.FindFile(x => x.StartsWith("Phive/Cloth/"));
                if (file == null)
                    return;

                var cloth = HairClothData.Load(pack.GetFile(file));
                foreach (var piece in cloth.Pieces)
                {
                    var sim = HairPhysics.Create(piece, part.Skeleton, Human.Skeleton);
                    if (sim != null)
                        _hairPhysics.Add(sim);
                }
                Console.WriteLine($"[Scene] Hair cloth: {_hairPhysics.Count} piece(s) for {entry.RowId}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Scene] Hair cloth load failed: {ex.Message}");
            }
        }

        public void SetEyebrow(GearEntry entry)
        {
            CurrentEyebrow = entry;
            DestroyPart(PartKind.Eyebrow);
            if (entry == null) { OnPartsChanged?.Invoke(); return; }

            string modelName = entry.IsCustom ? entry.RowId : entry.RowId + GenderSuffix;
            var part = entry.IsCustom
                ? CreateCustomPart(PartKind.Eyebrow, entry)
                : CreatePart(PartKind.Eyebrow, modelName);
            if (part == null) { OnPartsChanged?.Invoke(); return; }

            part.ResolveWelds(Human.Skeleton, HeadBoneMap);
            part.AttachBone = part.Skeleton.SearchBone("Head_Root");
            Parts[PartKind.Eyebrow] = part;
            ApplyGearSkinColor();
            OnPartsChanged?.Invoke();
        }

        public void SetGear(GearSlot slot, GearEntry entry)
        {
            switch (slot)
            {
                case GearSlot.Head: SetHead(entry); break;
                case GearSlot.Clothes: SetClothes(entry); break;
                case GearSlot.Shoes: SetShoes(entry); break;
                case GearSlot.Hair: SetHair(entry); break;
                case GearSlot.Eyebrow: SetEyebrow(entry); break;
                case GearSlot.Bottom: SetBottom(entry); break;
                case GearSlot.Tank: SetTank(entry); break;
                case GearSlot.MainWeapon:
                case GearSlot.SpecialWeapon: SetWeapon(entry); break;
            }
        }

        void SetHead(GearEntry entry)
        {
            //"No headgear" is Hed_INV000, like the game: an invisible model whose
            //pack still carries the default hair-arrange presets (SQD004's extra
            //curl shells collapse through Blitz_SQD004_0). Null only stays null on
            //a modded romfs that removed the row.
            entry ??= Database.Head.FirstOrDefault(x => x.RowId == "Hed_INV000");

            CurrentHead = entry;
            DestroyPart(PartKind.Head);
            //ApplyHeadgearParams also on removal: it resets hair arrange / ear hiding
            //back to defaults (otherwise no-head keeps the previous cap's arrange).
            if (entry == null) { ApplyHeadgearParams(); UpdateAlphaMask(); OnPartsChanged?.Invoke(); return; }

            PartModel part;
            if (entry.IsCustom)
                part = CreateCustomPart(PartKind.Head, entry);
            else
            {
                string modelName = ResolveGearModelForEntry(entry);
                if (modelName == null) { Console.WriteLine($"[Scene] No model for {entry.RowId}"); ApplyHeadgearParams(); return; }
                part = CreatePart(PartKind.Head, modelName);
            }
            if (part == null) { ApplyHeadgearParams(); OnPartsChanged?.Invoke(); return; }

            ApplyVariationAnim(part, entry);
            //Some headgear name their root after the model (Hed_EYE002) instead of
            //Root/Head_Root; weld whatever the skeleton root is onto the head bone.
            var rootBone = part.Skeleton.Bones.FirstOrDefault(b => b.Parent == null);
            var headMap = new Dictionary<string, string>(HeadBoneMap);
            if (rootBone != null && !headMap.ContainsKey(rootBone.Name))
                headMap[rootBone.Name] = "Head";
            part.ResolveWelds(Human.Skeleton, headMap, uprightWeld: true);
            part.AttachBone = part.Skeleton.SearchBone("Root")
                ?? part.Skeleton.SearchBone("Root_Model")
                ?? part.Skeleton.SearchBone("Head_Root")
                ?? rootBone;
            part.RestoreAttachBind = true;
            Parts[PartKind.Head] = part;

            ApplyHeadgearParams();
            UpdateAlphaMask();
            OnPartsChanged?.Invoke();
        }

        void SetClothes(GearEntry entry)
        {
            CurrentClothes = entry;
            DestroyPart(PartKind.Clothes);
            if (entry != null)
            {
                PartModel part = entry.IsCustom
                    ? CreateCustomPart(PartKind.Clothes, entry)
                    : CreatePart(PartKind.Clothes, ResolveGearModelForEntry(entry));
                if (part != null)
                {
                    ApplyVariationAnim(part, entry);
                    part.ResolveWelds(Human.Skeleton, null);
                    Parts[PartKind.Clothes] = part;
                }
            }
            UpdateTankHarness();
            UpdateAlphaMask();
            OnPartsChanged?.Invoke();
        }

        void SetShoes(GearEntry entry)
        {
            CurrentShoes = entry;
            DestroyPart(PartKind.ShoeLeft);
            DestroyPart(PartKind.ShoeRight);
            if (entry == null) { UpdateAlphaMask(); OnPartsChanged?.Invoke(); return; }

            string modelName = entry.IsCustom ? null : ResolveGearModelForEntry(entry);

            var left = entry.IsCustom
                ? CreateCustomPart(PartKind.ShoeLeft, entry)
                : CreatePart(PartKind.ShoeLeft, modelName);
            if (left != null)
            {
                ApplyVariationAnim(left, entry);
                left.ResolveWelds(Human.Skeleton, null);
                Parts[PartKind.ShoeLeft] = left;
            }

            var right = entry.IsCustom
                ? CreateCustomPart(PartKind.ShoeRight, entry)
                : CreatePart(PartKind.ShoeRight, modelName);
            if (right != null)
            {
                ApplyVariationAnim(right, entry);
                right.Mirror = true;
                right.ResolveWelds(Human.Skeleton, null, mirrorLR: true);
                Parts[PartKind.ShoeRight] = right;
            }
            UpdateAlphaMask();
            OnPartsChanged?.Invoke();
        }

        void SetBottom(GearEntry entry)
        {
            CurrentBottom = entry;
            DestroyPart(PartKind.Bottom);
            if (entry != null)
            {
                string modelName = entry.IsCustom ? null : entry.RowId + GenderSuffix;
                PartModel part = entry.IsCustom
                    ? CreateCustomPart(PartKind.Bottom, entry)
                    : CreatePart(PartKind.Bottom, modelName);
                if (part != null)
                {
                    ApplyVariationAnim(part, entry);
                    part.ResolveWelds(Human.Skeleton, null);
                    Parts[PartKind.Bottom] = part;
                }
            }
            UpdateAlphaMask();
            OnPartsChanged?.Invoke();
        }

        void SetTank(GearEntry entry)
        {
            CurrentTank = entry;
            DestroyPart(PartKind.Tank);
            if (entry != null)
            {
                //TankInfo rows point at their actor via SpecActor (PlayerTank_Jetpack
                //-> ModelInfo -> Tnk_JetPack.bfres).
                string actor = !string.IsNullOrEmpty(entry.ActorName) ? entry.ActorName : entry.RowId;
                PartModel part = entry.IsCustom
                    ? CreateCustomPart(PartKind.Tank, entry)
                    : CreatePart(PartKind.Tank, Database.ResolveModelName(actor));
                if (part != null)
                {
                    part.ResolveWelds(Human.Skeleton, null);
                    Parts[PartKind.Tank] = part;
                }
            }
            UpdateTankHarness();
            OnPartsChanged?.Invoke();
        }

        void SetWeapon(GearEntry entry)
        {
            CurrentWeapon = entry;
            DestroyPart(PartKind.WeaponMain);
            DestroyPart(PartKind.WeaponLeft);
            if (entry == null) { OnPartsChanged?.Invoke(); return; }

            (string file, string model) mainModel, leftModel;
            if (entry.IsCustom)
            {
                mainModel = (entry.RowId, null);
                leftModel = (null, null);
            }
            else
                (mainModel, leftModel) = Database.ResolveWeaponModels(entry);

            bool isStringer = !entry.IsCustom &&
                (entry.ActorName.Contains("Stringer") || entry.RowId.Contains("_Strn_"));
            string mainHandBone = isStringer ? "Weapon_L" : "Weapon_R";

            if (mainModel.file != null)
            {
                var part = entry.IsCustom
                    ? CreateCustomPart(PartKind.WeaponMain, entry)
                    : CreatePart(PartKind.WeaponMain, mainModel.file, subModel: mainModel.model);
                if (part != null)
                {
                    part.ResolveWelds(Human.Skeleton, new Dictionary<string, string> { { "Root", mainHandBone } }, mapOnly: true);
                    part.AttachBone = part.Skeleton.SearchBone("Root");
                    ApplyWeaponCarryPose(part);
                    Parts[PartKind.WeaponMain] = part;
                }
            }
            if (leftModel.file != null)
            {
                var part = CreatePart(PartKind.WeaponLeft, leftModel.file, subModel: leftModel.model);
                if (part != null)
                {
                    part.ResolveWelds(Human.Skeleton, new Dictionary<string, string> { { "Root", "Weapon_L" } }, mapOnly: true);
                    part.AttachBone = part.Skeleton.SearchBone("Root");
                    //The _L fmdb is pre-mirrored geometry, so its intended world
                    //transform is mirror(Weapon_R world). The player's Weapon_L bone
                    //frame = mirror(Weapon_R) with an extra 180° local-X twist
                    //(verified from bone dumps), so cancel that twist here.
                    part.AttachOffset = Matrix4.CreateRotationX(MathHelper.Pi);
                    Parts[PartKind.WeaponLeft] = part;
                }
            }
            OnPartsChanged?.Invoke();
        }

        /// <summary>
        /// Rollers/brushes carry their own fold anims (Open/Close); at rest the
        /// model is authored in the deployed pose, but a held weapon uses the
        /// closed one. Bake the CloseOff (or final Close) frame as the part's
        /// static local pose for its unwelded bones.
        /// </summary>
        void ApplyWeaponCarryPose(PartModel part)
        {
            var anim = part.Bfres.SkeletalAnimations.FirstOrDefault(a => a.Name == "Close")
                ?? part.Bfres.SkeletalAnimations.FirstOrDefault(a => a.Name == "CloseOff");
            if (anim == null)
                return;

            anim.SkeletonOverride = part.Skeleton;
            anim.SetFrame(anim.FrameCount);
            anim.NextFrame();   //writes the pose + runs skeleton.Update()

            part.PoseOverride = new Dictionary<string, PartModel.PoseSrt>();
            foreach (var bone in part.Skeleton.Bones)
            {
                var local = bone.Parent != null
                    ? bone.Transform * Matrix4.Invert(bone.Parent.Transform)
                    : bone.Transform;
                part.PoseOverride[bone.Name] = new PartModel.PoseSrt
                {
                    Position = local.ExtractTranslation(),
                    Rotation = local.ExtractRotation(),
                    Scale = local.ExtractScale(),
                };
            }
        }

        PartModel CreateCustomPart(PartKind kind, GearEntry entry)
        {
            try
            {
                var raw = File.ReadAllBytes(entry.CustomPath);
                raw = Romfs.Decompress(raw);
                //Fake path inside romfs Model so shader lookup works.
                string fakePath = Path.Combine(Romfs.Root, "Model",
                    Path.GetFileNameWithoutExtension(entry.CustomPath.Replace(".zs", "")) + ".bfres");
                var bfres = LoadModelData(raw, fakePath);
                return bfres == null ? null : CreatePart(kind, entry.RowId, bfres);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Scene] Failed to load custom part: {ex.Message}");
                return null;
            }
        }

        #endregion

        #region Headgear params (VariationSRT / ManualBindSRT / HairArrange)

        /// <summary>
        /// Applies headgear parameters that depend on the (headgear, variation, hair)
        /// combination: gear attach SRT and hair arrange bone overrides.
        /// </summary>
        //Ear hiding requested by the current headgear (VariationSRT HideEar).
        public string EarHideMode { get; private set; } = "Visible";

        void ApplyHeadgearParams()
        {
            //Reset defaults
            EarHideMode = "Visible";
            if (Parts.TryGetValue(PartKind.Hair, out var hair))
            {
                hair.HairArrange = null;
                ApplyAfroShell(hair, null);
            }
            if (Parts.TryGetValue(PartKind.Head, out var head))
                head.AttachOffset = Matrix4.Identity;

            if (CurrentHead == null || CurrentHead.IsCustom || head == null)
                return;

            var pack = Romfs.GetActorPack(CurrentHead.RowId);
            if (pack == null)
                return;

            //GearHeadParamSet from the gear's own pack.
            var row = Database.HeadRows.GetValueOrDefault(CurrentHead.RowId);
            string paramPath = row != null ? Byml.GetString(row, "HeadParamSetPath") : null;
            byte[] paramData = Romfs.ResolveWorkPath(pack, paramPath);
            if (paramData == null)
            {
                string file = pack.FindFile(x => x.Contains("GearHeadParamSet"));
                if (file != null) paramData = pack.GetFile(file);
            }
            if (paramData == null)
                return;

            var root = Byml.AsHash(new Byml(paramData).Root);
            if (root == null)
                return;

            int variation = CurrentHead.Variation;
            string hairKey = GetHairKey();

            //--- Attach SRT: ManualBindSRT.V<k>_<HAIR> (a full per-hair bind override)
            //when present, otherwise the generic VariationSRT.V<k> (cap worn backwards
            //etc). They are alternatives, not composed - the manual values already
            //include the variation rotation (e.g. both carry the ~48°/180° flip).
            Matrix4 offset = Matrix4.Identity;
            Dictionary<string, object> srt = null;

            var manualBind = Byml.AsHash(root.GetValueOrDefault("ManualBindSRT"));
            if (manualBind != null && hairKey != null)
                srt = Byml.AsHash(manualBind.GetValueOrDefault($"V{variation}_{hairKey}"));

            var variationSrt = Byml.AsHash(root.GetValueOrDefault("VariationSRT"));
            var varEntry = variationSrt != null ? Byml.AsHash(variationSrt.GetValueOrDefault($"V{variation}")) : null;

            //V0 entries usually only carry HideEar, but some gear (Hed_AMB020) puts a
            //real bind SRT there too; ReadSrt returns identity when no SRT keys exist.
            if (srt == null)
                srt = varEntry;

            if (srt != null)
                offset = ReadSrt(srt);
            head.AttachOffset = offset;

            //--- HideEar: Visible / HideAll / HideLong / HideLeft (absent = Visible)
            if (varEntry != null)
                EarHideMode = Byml.GetString(varEntry, "HideEar") ?? "Visible";

            //--- HairArrange.V<k>.PresetMap.<HAIR>
            if (hair != null && hairKey != null)
            {
                var hairArrange = Byml.AsHash(root.GetValueOrDefault("HairArrange"));
                var varSet = hairArrange != null ? Byml.AsHash(hairArrange.GetValueOrDefault($"V{variation}")) : null;
                var presetMap = varSet != null ? Byml.AsHash(varSet.GetValueOrDefault("PresetMap")) : null;
                string arrangePath = presetMap != null ? Byml.GetString(presetMap, hairKey) : null;

                if (!string.IsNullOrEmpty(arrangePath))
                {
                    var arrangeData = Romfs.ResolveWorkPath(pack, arrangePath);
                    if (arrangeData != null)
                    {
                        hair.HairArrange = ParseHairArrange(arrangeData, out string afroType);
                        ApplyAfroShell(hair, afroType);
                    }
                }
            }
        }

        //Afro-style hairs (Har_OCT003) contain one mesh shell per headgear family,
        //each hanging off a marker bone; the arrange preset's AfroType picks the
        //visible shell. Default (no headgear / no preset) = Base.
        static readonly string[] AfroShells = { "Base", "Cap", "Fullface", "Headband", "HeadphoneA" };

        static void ApplyAfroShell(PartModel hair, string afroType)
        {
            if (hair?.Skeleton == null)
                return;
            if (!AfroShells.Any(s => hair.Skeleton.SearchBone(s) != null))
                return;
            string active = string.IsNullOrEmpty(afroType) ? "Base" : afroType;
            foreach (var shell in AfroShells)
                hair.SetBoneVisible(shell, shell == active);
        }

        /// <summary>Hair preset key: "Har_SQD012" -> "SQD012".</summary>
        string GetHairKey()
        {
            if (CurrentHair == null || CurrentHair.IsCustom)
                return null;
            string id = CurrentHair.RowId;
            return id.StartsWith("Har_") ? id.Substring(4) : id;
        }

        static Dictionary<string, ArrangeBoneParam> ParseHairArrange(byte[] data, out string afroType)
        {
            afroType = null;
            var result = new Dictionary<string, ArrangeBoneParam>();
            var root = Byml.AsHash(new Byml(data).Root);
            if (root == null) return result;
            afroType = Byml.GetString(root, "AfroType");

            if (root.GetValueOrDefault("BoneParamArray") is List<object> bones)
            {
                foreach (var entry in bones.OfType<Dictionary<string, object>>())
                {
                    string boneName = Byml.GetString(entry, "BoneName");
                    if (string.IsNullOrEmpty(boneName))
                        continue;

                    var param = new ArrangeBoneParam
                    {
                        AnimReduce = Byml.GetFloat(entry, "AnimReduceRt", 1.0f),
                        Scale = ReadVec3(entry, "Scale", Vector3.One),
                        RotationDeg = ReadVec3(entry, "Rotation", Vector3.Zero),
                        Translate = ReadVec3(entry, "Transform", Vector3.Zero),
                    };
                    result[boneName] = param;
                }
            }
            return result;
        }

        static Vector3 ReadVec3(Dictionary<string, object> hash, string key, Vector3 def)
        {
            var v = Byml.AsHash(hash.GetValueOrDefault(key));
            if (v == null) return def;
            return new Vector3(Byml.GetFloat(v, "X"), Byml.GetFloat(v, "Y"), Byml.GetFloat(v, "Z"));
        }

        static Matrix4 ReadSrt(Dictionary<string, object> srt)
        {
            if (srt == null)
                return Matrix4.Identity;
            Vector3 scale = Vector3.One, rotate = Vector3.Zero, translate = Vector3.Zero;
            var s = Byml.AsHash(srt.GetValueOrDefault("Scale"));
            if (s != null) scale = new Vector3(Byml.GetFloat(s, "X", 1), Byml.GetFloat(s, "Y", 1), Byml.GetFloat(s, "Z", 1));
            //ManualBindSRT uses "Rotate", VariationSRT uses "Rotation".
            var r = Byml.AsHash(srt.GetValueOrDefault("Rotate")) ?? Byml.AsHash(srt.GetValueOrDefault("Rotation"));
            if (r != null) rotate = new Vector3(Byml.GetFloat(r, "X"), Byml.GetFloat(r, "Y"), Byml.GetFloat(r, "Z"));
            var t = Byml.AsHash(srt.GetValueOrDefault("Translate"));
            if (t != null) translate = new Vector3(Byml.GetFloat(t, "X"), Byml.GetFloat(t, "Y"), Byml.GetFloat(t, "Z"));

            //Euler XYZ, applied X first (row-vector convention).
            return Matrix4.CreateScale(scale) *
                Matrix4.CreateRotationX(MathHelper.DegreesToRadians(rotate.X)) *
                Matrix4.CreateRotationY(MathHelper.DegreesToRadians(rotate.Y)) *
                Matrix4.CreateRotationZ(MathHelper.DegreesToRadians(rotate.Z)) *
                Matrix4.CreateTranslation(translate);
        }

        #endregion

        #region Tank harness

        /// <summary>
        /// Shows the harness strap variant matching the current clothes' HarnessType
        /// (S/M/L + F suffix for female bodies), or the hidden stub.
        /// </summary>
        void UpdateTankHarness()
        {
            if (!Parts.TryGetValue(PartKind.Tank, out var tank))
                return;

            string type = "S";
            bool hide = false;
            if (CurrentClothes != null && Database.ClothesRows.TryGetValue(CurrentClothes.RowId, out var row))
            {
                type = Byml.GetString(row, "HarnessType");
                if (string.IsNullOrEmpty(type)) type = "S";
                hide = Byml.GetBool(row, "IsHideHarness");
            }

            foreach (var suffix in new[] { "S", "M", "L", "SF", "MF", "LF" })
                tank.SetBoneVisible($"Harness_{suffix}", false);
            tank.SetBoneVisible("Harness_Hide", false);

            if (hide)
                tank.SetBoneVisible("Harness_Hide", true);
            else
                tank.SetBoneVisible($"Harness_{type}{(IsFemale ? "F" : "")}", true);
        }

        #endregion

        #region Alpha mask (skin hiding under gear)

        /// <summary>
        /// Mask texture name contributed by a gear entry: AlphaMaskV1 for variation
        /// rows when present, else the per-gender AlphaMaskF/AlphaMaskM.
        /// </summary>
        string GetAlphaMaskName(GearEntry entry, Dictionary<string, Dictionary<string, object>> rows)
        {
            if (entry == null || entry.IsCustom || !rows.TryGetValue(entry.RowId, out var row))
                return null;
            //Geometry variants (_V1 models) use AlphaMaskV1 exclusively - empty
            //means no skin is covered (e.g. bare feet in sock-less sandals).
            //Texture-only variations keep the base gendered mask.
            if (entry.Variation >= 1 && UsesV1Model(entry))
            {
                string v1 = Byml.GetString(row, "AlphaMaskV1");
                return string.IsNullOrEmpty(v1) ? null : v1;
            }
            string name = Byml.GetString(row, IsFemale ? "AlphaMaskF" : "AlphaMaskM");
            return string.IsNullOrEmpty(name) ? null : name;
        }

        bool UsesV1Model(GearEntry entry)
        {
            string v1 = ResolveGearModel(entry.RowId + "_V1");
            return v1 != null && Romfs.ModelExists(v1);
        }

        bool _bodyMaskActive;

        /// <summary>
        /// Recomposes the body alpha mask (union of head/clothes/bottom/shoes masks)
        /// and redirects M_Body's opacity sampler to it. The body alpha test then
        /// discards skin covered by gear (the game's PlayerAlphaMaskMgr).
        /// </summary>
        void UpdateAlphaMask()
        {
            if (Human == null)
                return;
            _alphaMask.Load(Romfs);

            var names = new[]
            {
                GetAlphaMaskName(CurrentHead, Database.HeadRows),
                GetAlphaMaskName(CurrentClothes, Database.ClothesRows),
                GetAlphaMaskName(CurrentBottom, Database.BottomRows),
                GetAlphaMaskName(CurrentShoes, Database.ShoesRows),
            };

            _bodyMaskActive = _alphaMask.Compose(Human, names.Where(x => x != null));
            _bodyMaskMaterials = Human.ModelAsset.Meshes
                .Select(m => (FMAT)m.Material)
                .Where(m => m.Name == "M_Body")
                .Distinct().ToList();
            ApplyBodyMaskSampler();
            //New parts start with the authored placeholder skin color.
            ApplyGearSkinColor();
        }

        /// <summary>
        /// (Re)applies the sampler redirect; runs every frame because material anims
        /// or resets may rewrite AnimatedSamplers.
        /// </summary>
        void ApplyBodyMaskSampler()
        {
            if (_bodyMaskMaterials == null)
                return;
            foreach (var mat in _bodyMaskMaterials)
            {
                if (_bodyMaskActive)
                    mat.AnimatedSamplers["_o0"] = AlphaMaskSystem.CompositeTextureName;
                else
                    mat.AnimatedSamplers.Remove("_o0");
            }
        }

        #endregion

        #region Colors

        public void ApplyEyeColor(int index)
        {
            EyeColor = index;
            var anim = Anims.GetTexPattern("Color_Eye");
            if (anim != null && Human != null)
                ScopedAnimPlayer.ApplyMaterialAnim(anim, index, new[] { Human.ModelAsset });
        }

        public void ApplySkinTone(int index)
        {
            SkinTone = index;
            //Color_Skin is a shader param anim; the wrapper type is the same.
            var anim = FindMaterialAnim("Color_Skin");
            if (anim != null && Human != null)
                ScopedAnimPlayer.ApplyMaterialAnim(anim, index, new[] { Human.ModelAsset });
            ApplyGearSkinColor();
        }

        /// <summary>
        /// Gear shaders paint exposed skin patches (e.g. the ripped leggings of
        /// Btm_001 v3) using the player_skin_color material uniform, which the game
        /// updates for the current skin tone; the authored default is a green
        /// placeholder. Mirror the tone color that Color_Skin writes into M_Body.
        /// </summary>
        void ApplyGearSkinColor()
        {
            if (Human == null)
                return;
            var body = Human.ModelAsset.Meshes.Select(m => (FMAT)m.Material)
                .FirstOrDefault(m => m.Name == "M_Body");
            if (body == null)
                return;

            float[] skin = null;
            if (body.AnimatedParams.TryGetValue("const_color0", out var animated))
                skin = animated.DataValue as float[];
            if (skin == null && body.ShaderParams.TryGetValue("const_color0", out var bodyParam))
                skin = bodyParam.DataValue as float[];
            if (skin == null)
                return;

            foreach (var part in Parts.Values)
            {
                if (part?.ModelAsset == null)
                    continue;
                foreach (var mesh in part.ModelAsset.Meshes)
                {
                    var mat = (FMAT)mesh.Material;
                    if (!mat.ShaderParams.TryGetValue("player_skin_color", out var target))
                        continue;
                    if (!mat.AnimatedParams.TryGetValue("player_skin_color", out var param))
                    {
                        param = new BfresLibrary.ShaderParam
                        {
                            Name = "player_skin_color",
                            Type = target.Type,
                            DataValue = new float[] { 1, 1, 1, 1 },
                        };
                        mat.AnimatedParams["player_skin_color"] = param;
                    }
                    var arr = (float[])param.DataValue;
                    arr[0] = 0.87f * skin[0];
                    arr[1] = 0.44f * skin[1];
                    arr[2] = 0.24f * skin[2];
                }
            }
        }

        BfresMaterialAnim FindMaterialAnim(string name)
        {
            //Material animations that live in the player model file itself.
            return Human?.Bfres.MaterialAnimations.FirstOrDefault(x => x.Name == name);
        }

        public void ApplyTeamColor(TeamColorSet set, int team)
        {
            if (set == null) return;
            var color = team == 0 ? set.Alpha : team == 1 ? set.Bravo : set.Charlie;
            //The shader wants linear color (flexlion applies pow 2.2).
            var linear = new System.Numerics.Vector3(
                MathF.Pow(color.X, 2.2f), MathF.Pow(color.Y, 2.2f), MathF.Pow(color.Z, 2.2f));
            //Force the dumped uniform buffers in first; the lazy load on first draw
            //resets TeamAlphaColor to the dump's value and would clobber ours.
            HoianNXRender.LoadResourceData();
            HoianNXRender.TeamAlphaColor = linear;
        }

        #endregion

        #region Animation

        public void PlayAnim(string name)
        {
            CurrentAnimName = name;
            AnimFrame = 0;

            CurrentSkeletal = name != null ? Anims.GetSkeletal(name) : null;
            CurrentTexPattern = name != null ? Anims.GetTexPattern(name) : null;
            CurrentShaderParam = name != null ? Anims.GetShaderParam(name) : null;
            CurrentBoneVis = name != null ? Anims.GetBoneVis(name) : null;

            if (CurrentSkeletal != null)
                CurrentSkeletal.SkeletonOverride = Human.Skeleton;

            //Reset skeleton + bone visibility when switching animations.
            if (Human != null)
            {
                foreach (var bone in Human.Skeleton.Bones)
                    bone.Visible = IsBoneDefaultVisible(bone);
                Human.Skeleton.Reset();

                //Clear leftover animated samplers/params from the previous animation
                //(shader param anims would otherwise stick, e.g. shrunk eyes).
                ScopedAnimPlayer.ResetMaterialAnims(new[] { Human.ModelAsset });
                ApplyBodyMaskSampler();

                //Re-apply static color anims cleared by material state resets.
                ApplyEyeColor(EyeColor);
                ApplySkinTone(SkinTone);
            }

            //Pose jumps on anim switch; restart the cloth from the new pose.
            ResetHairPhysics();
        }

        //Mouth01..04 etc default hidden; captured at load time from the bfres bone data.
        readonly Dictionary<STBone, bool> _defaultBoneVisibility = new();
        bool IsBoneDefaultVisible(STBone bone)
        {
            if (_defaultBoneVisibility.TryGetValue(bone, out bool visible))
                return visible;
            //BfresBone visibility flag is loaded into STBone.Visible at load time; since
            //anims mutate it, capture the first value we see.
            _defaultBoneVisibility[bone] = bone.Visible;
            return bone.Visible;
        }

        /// <summary>
        /// Per-frame update: advance animation, update the human skeleton, weld parts.
        /// </summary>
        public void Update(float deltaSeconds)
        {
            if (Human == null)
                return;

            if (CurrentSkeletal != null && !AnimPaused)
            {
                AnimFrame += deltaSeconds * 60.0f * AnimSpeed;
                float frameCount = Math.Max(CurrentSkeletal.FrameCount, 1);
                if (CurrentSkeletal.Loop)
                    AnimFrame %= frameCount;
                else if (AnimFrame > frameCount - 1)
                    AnimFrame = frameCount - 1;
            }

            if (CurrentSkeletal != null)
            {
                CurrentSkeletal.SetFrame(AnimFrame);
                CurrentSkeletal.NextFrame();   //updates Human.Skeleton (SkeletonOverride)
            }

            if (CurrentTexPattern != null)
                ScopedAnimPlayer.ApplyMaterialAnim(CurrentTexPattern, AnimFrame, new[] { Human.ModelAsset });
            //Shader param anims scale the eyeball down behind closed eyelids etc.
            if (CurrentShaderParam != null)
                ScopedAnimPlayer.ApplyMaterialAnim(CurrentShaderParam, AnimFrame, new[] { Human.ModelAsset });
            if (CurrentBoneVis != null)
                ScopedAnimPlayer.ApplyBoneVisAnim(CurrentBoneVis, AnimFrame, Human.Skeleton);

            //Material anims can rewrite AnimatedSamplers; keep the mask redirect alive.
            ApplyBodyMaskSampler();

            ApplyEarHide();

            //Weld all parts to the updated human skeleton.
            foreach (var part in Parts.Values)
                part.ApplyWeld();

            //Hair cloth sim runs on top of the welded animation pose. Bones the
            //current hair-arrange collapses (scale ~0, e.g. the OCT001 ponytail
            //hidden under CAP023) must keep their welded transform - the sim
            //would otherwise write an uncollapsed pose back into them.
            if (HairPhysicsEnabled)
            {
                Parts.TryGetValue(PartKind.Hair, out var hairPart);
                foreach (var sim in _hairPhysics)
                    sim.Update(deltaSeconds, hairPart?.HairArrange);
            }
        }

        /// <summary>Restarts hair cloth from the current pose (e.g. anim switch).</summary>
        public void ResetHairPhysics()
        {
            foreach (var sim in _hairPhysics)
                sim.Reset();
        }

        /// <summary>Debug: dumps all hair sim states.</summary>
        public void DumpHairPhysics()
        {
            foreach (var sim in _hairPhysics)
                sim.DebugDump();
        }

        /// <summary>
        /// Collapses ear bones for headgear that hides ears (PlayerCustomUtl::
        /// hideEarHair_BeforeCalcDraw): the scale is applied in bone-local space,
        /// squashing the ear-skinned vertices onto the ear joint. Runs before
        /// welding so ear-covering hair regions collapse with it (matches the game,
        /// where hair is part of the human model).
        /// </summary>
        void ApplyEarHide()
        {
            bool hideL, hideR;
            switch (EarHideMode)
            {
                case "HideAll": hideL = hideR = true; break;
                case "HideLeft": hideL = true; hideR = false; break;
                //"Long" ears = inkling; octoling ears stay visible.
                case "HideLong": hideL = hideR = PlayerType <= 1; break;
                default: return;
            }

            var squash = Matrix4.CreateScale(0.0001f);
            if (hideL && Human.Skeleton.SearchBone("Ear_L") is STBone earL)
                earL.Transform = squash * earL.Transform;
            if (hideR && Human.Skeleton.SearchBone("Ear_R") is STBone earR)
                earR.Transform = squash * earR.Transform;
        }

        public void SetAnimFrame(float frame)
        {
            AnimFrame = frame;
        }

        #endregion

        public IEnumerable<BfresRender> AllRenders()
        {
            if (Human != null)
                yield return Human.Render;
            foreach (var part in Parts.Values)
                if (part.Visible)
                    yield return part.Render;
        }

        /// <summary>
        /// Draws the player for one pass. Mirrored parts (right shoe) flip triangle
        /// winding, so they are drawn with reversed front-face culling.
        /// </summary>
        public void Draw(GLContext control, Pass pass)
        {
            if (Human != null && Human.Render.IsVisible)
                Human.Render.DrawModel(control, pass, Vector4.Zero);

            foreach (var part in Parts.Values)
            {
                if (!part.Visible)
                    continue;
                if (part.Mirror)
                    OpenTK.Graphics.OpenGL.GL.FrontFace(OpenTK.Graphics.OpenGL.FrontFaceDirection.Cw);
                part.Render.DrawModel(control, pass, Vector4.Zero);
                if (part.Mirror)
                    OpenTK.Graphics.OpenGL.GL.FrontFace(OpenTK.Graphics.OpenGL.FrontFaceDirection.Ccw);
            }
        }

        public void Dispose()
        {
            foreach (var kind in Parts.Keys.ToList())
                DestroyPart(kind);
            if (Human != null)
            {
                ParkPart(Human.CacheKey, Human.Bfres);
                Human = null;
            }
            //Parts above were parked into the cache; this disposes everything.
            ClearPartCache();
            _alphaMask.Dispose();
        }
    }
}
