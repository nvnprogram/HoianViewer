using System;
using System.Collections.Generic;
using System.Linq;
using OpenTK;
using Toolbox.Core;

namespace PlayerViewer.Player
{
    /// <summary>
    /// Runtime hair cloth simulation driven by .bphcl data (one instance per cloth
    /// piece). Particles are verlet-integrated in model space, pinned to the skinned
    /// animation pose, constrained by the authored link/range sets, collided against
    /// the authored capsules, and finally written back to the hair bones through the
    /// triangle-frame bone deform.
    /// 
    /// Not necessarily perfectly accurate to Havok, since its not based on a proper decomp of Havok clothes.
    /// </summary>
    public class HairPhysics
    {
        public bool Enabled = true;

        readonly HairClothPiece _piece;
        readonly STBone[] _bones;          //cloth skeleton index -> scene bone
        readonly STBone[] _driven;         //bones written by the deform (subset)

        readonly Vector3[] _pos;
        readonly Vector3[] _prev;
        readonly Vector3[] _skinned;       //animation-pose skinned vertices
        readonly Matrix4[] _boneWorld;     //current per-frame transform set
        readonly int[] _refVertex;         //particle -> reference buffer vertex (-1: none)
        readonly float[][] _capsuleMinDist; //per collidable x particle: rest-aware pushout distance
        bool _primed;
        float _accumulator;

        const float StepTime = 1.0f / 60.0f;
        const int MaxSubsteps = 4;

        HairPhysics(HairClothPiece piece, STBone[] bones)
        {
            _piece = piece;
            _bones = bones;
            _driven = piece.BoneDeforms.Select(d => bones[d.BoneIndex]).ToArray();

            int n = piece.Particles.Length;
            _pos = new Vector3[n];
            _prev = new Vector3[n];
            _skinned = new Vector3[piece.SkinVertices.Length];
            _boneWorld = new Matrix4[piece.BoneRefPose.Length];

            //Particle -> reference-buffer vertex: MoveParticles pairs (fixed), then
            //local-range reference vertices, then identity where in range.
            _refVertex = new int[n];
            for (int p = 0; p < n; p++)
            {
                _refVertex[p] = p < _skinned.Length ? p : -1;
                foreach (var range in piece.LocalRanges)
                    if (range.Particle == p) { _refVertex[p] = range.ReferenceVertex; break; }
            }
            foreach (var (vertex, particle) in piece.VertexParticlePairs)
                if (particle >= 0 && particle < n)
                    _refVertex[particle] = vertex;

            //The authored rest pose legitimately overlaps the collision capsules
            //(they approximate the head/torso loosely), so collision must not
            //shove resting hair outward. Per particle+capsule, allow at least the
            //rest-pose penetration depth.
            _capsuleMinDist = new float[piece.Collidables.Count][];
            for (int c = 0; c < piece.Collidables.Count; c++)
            {
                var col = piece.Collidables[c];
                var rest = col.BoneOffset * piece.BoneRefPose[col.BoneIndex];
                Vector3 a = Vector3.TransformPosition(col.Start, rest);
                Vector3 b = Vector3.TransformPosition(col.End, rest);

                _capsuleMinDist[c] = new float[n];
                for (int p = 0; p < n; p++)
                {
                    float restDist = DistanceToSegment(piece.RestPositions[p], a, b);
                    float minDist = col.Radius + piece.Particles[p].Radius;
                    _capsuleMinDist[c][p] = Math.Min(minDist, restDist);
                }
            }
        }

        static float DistanceToSegment(Vector3 p, Vector3 a, Vector3 b)
        {
            Vector3 ab = b - a;
            float t = ab.LengthSquared > 1e-9f
                ? MathHelper.Clamp(Vector3.Dot(p - a, ab) / ab.LengthSquared, 0, 1) : 0;
            return (p - (a + ab * t)).Length;
        }

        /// <summary>
        /// Binds a cloth piece to scene skeletons. Bones are resolved by name against
        /// the hair part skeleton first, then the human skeleton (Spine_3 etc. live
        /// there). Returns null when required bones are missing.
        /// </summary>
        public static HairPhysics Create(HairClothPiece piece, STSkeleton hairSkeleton, STSkeleton humanSkeleton)
        {
            if (piece.SkinVertices == null || piece.BoneDeforms.Count == 0)
                return null;

            var bones = new STBone[piece.BoneNames.Length];
            for (int i = 0; i < bones.Length; i++)
            {
                bones[i] = hairSkeleton.SearchBone(piece.BoneNames[i])
                    ?? humanSkeleton?.SearchBone(piece.BoneNames[i]);
                if (bones[i] == null)
                    return null;
            }
            return new HairPhysics(piece, bones);
        }

        /// <summary>Restarts the sim from the current animation pose next update.</summary>
        public void Reset() => _primed = false;

        /// <summary>Debug: dumps sim state (skinned targets vs particles, capsules).</summary>
        public void DebugDump()
        {
            Console.WriteLine($"[HairPhys] {_piece.Name}");
            for (int p = 0; p < _pos.Length; p++)
            {
                Vector3 target = SkinnedForParticle(p);
                Console.WriteLine($"  p{p}{(IsFixed(p) ? " FIX" : "    ")} pos=({_pos[p].X:F3},{_pos[p].Y:F3},{_pos[p].Z:F3}) " +
                    $"skin=({target.X:F3},{target.Y:F3},{target.Z:F3}) drift={(_pos[p] - target).Length:F3}");
            }
            foreach (var range in _piece.LocalRanges)
            {
                Vector3 c = _skinned[range.ReferenceVertex];
                float d = (_pos[range.Particle] - c).Length;
                Console.WriteLine($"  range p{range.Particle} ref=v{range.ReferenceVertex} r={range.Radius:F3} dist={d:F3}{(d > range.Radius + 0.001f ? " VIOLATED" : "")}");
            }
            foreach (var col in _piece.Collidables)
            {
                var world = col.BoneOffset * _boneWorld[col.BoneIndex];
                Vector3 a = Vector3.TransformPosition(col.Start, world);
                Vector3 b = Vector3.TransformPosition(col.End, world);
                Console.WriteLine($"  capsule '{col.Name}' bone={_piece.BoneNames[col.BoneIndex]} r={col.Radius:F3} " +
                    $"a=({a.X:F3},{a.Y:F3},{a.Z:F3}) b=({b.X:F3},{b.Y:F3},{b.Z:F3})");
            }
        }

        /// <summary>
        /// Runs after the hair part weld (bones hold the pure animation pose) and
        /// overwrites the driven bones with the simulated pose. Bones collapsed by
        /// the active hair-arrange (scale ~0 = hidden under headgear) are left at
        /// their welded transform.
        /// </summary>
        public void Update(float dt, Dictionary<string, ArrangeBoneParam> arrange = null)
        {
            if (!Enabled)
                return;

            //Snapshot the animation pose transform set.
            for (int i = 0; i < _bones.Length; i++)
                _boneWorld[i] = _bones[i].Transform;

            SkinVertices();

            if (!_primed)
            {
                //Start from the authored rest shape carried to the current pose by
                //the cloth root bone (refpose of bone 0 is identity). Skin-buffer
                //lookups can't be used here: dynamic particles have no reliable
                //buffer mapping in every asset.
                var restToWorld = Matrix4.Invert(_piece.BoneRefPose[0]) * _boneWorld[0];
                for (int p = 0; p < _pos.Length; p++)
                    _pos[p] = _prev[p] = Vector3.TransformPosition(_piece.RestPositions[p], restToWorld);
                _primed = true;
            }

            _accumulator = Math.Min(_accumulator + dt, StepTime * MaxSubsteps);
            while (_accumulator >= StepTime)
            {
                Step(StepTime);
                _accumulator -= StepTime;
            }

            WriteBones(arrange);
        }

        Vector3 SkinnedForParticle(int particle)
        {
            int vertex = _refVertex[particle];
            return vertex >= 0 && vertex < _skinned.Length ? _skinned[vertex] : _pos[particle];
        }

        void SkinVertices()
        {
            for (int vi = 0; vi < _piece.SkinVertices.Length; vi++)
            {
                var sv = _piece.SkinVertices[vi];
                if (sv == null)
                    continue;
                Vector3 result = Vector3.Zero;
                for (int b = 0; b < sv.Bones.Length; b++)
                {
                    if (sv.Weights[b] <= 0)
                        continue;
                    int subset = sv.Bones[b];
                    int bone = subset < _piece.TransformSubset.Length ? _piece.TransformSubset[subset] : subset;
                    //Bone-space deformer: position is authored per blend slot in bone
                    //space. Object-space: shared position through boneFromSkinMesh.
                    Vector3 p = sv.LocalPosPerBone != null ? sv.LocalPosPerBone[b] : sv.LocalPos;
                    var mat = sv.LocalPosPerBone != null ? _boneWorld[bone]
                        : _piece.BoneFromSkinMesh[subset] * _boneWorld[bone];
                    result += sv.Weights[b] * Vector3.TransformPosition(p, mat);
                }
                _skinned[vi] = result;
            }
        }

        void Step(float dt)
        {
            var piece = _piece;
            int subSteps = Math.Clamp(piece.SubSteps, 1, 8);
            float subDt = dt / subSteps;

            for (int s = 0; s < subSteps; s++)
            {
                //Pin fixed particles to the animation pose.
                foreach (int f in piece.FixedParticles)
                {
                    Vector3 target = SkinnedForParticle(f);
                    _prev[f] = _pos[f];
                    _pos[f] = target;
                }

                //Verlet integration for dynamic particles.
                float damping = MathF.Pow(1.0f - piece.DampingPerSecond, subDt);
                Vector3 gravityStep = piece.Gravity * subDt * subDt;
                for (int p = 0; p < _pos.Length; p++)
                {
                    if (piece.Particles[p].InvMass <= 0 || IsFixed(p))
                        continue;
                    Vector3 velocity = (_pos[p] - _prev[p]) * damping;
                    _prev[p] = _pos[p];
                    _pos[p] += velocity + gravityStep;
                }

                //Constraints run in the authored execution order (hclSimulateOperator
                //config), the authored number of times. Stiffness is applied as-is;
                //values >1 just clamp (authored data uses e.g. 1.25/3.33 for near-rigid
                //links and 0.4 for soft strand tips).
                for (int iter = 0; iter < Math.Clamp(piece.SolveIterations, 1, 8); iter++)
                    foreach (int setIndex in piece.ConstraintExecution)
                        SolveConstraintSet(setIndex < piece.ConstraintSetKinds.Count
                            ? piece.ConstraintSetKinds[setIndex] : HairConstraintKind.Unknown);

                SolveCapsules();
            }
        }

        void SolveConstraintSet(HairConstraintKind kind)
        {
            var piece = _piece;
            switch (kind)
            {
                case HairConstraintKind.Standard:
                    //Spring toward rest length.
                    foreach (var link in piece.StandardLinks)
                        SolveDistance(link.A, link.B, link.RestLength, Math.Min(link.Stiffness, 1), onlyIfLonger: false);
                    break;

                case HairConstraintKind.Stretch:
                    //Hard upper bound on length.
                    foreach (var link in piece.StretchLinks)
                        SolveDistance(link.A, link.B, link.RestLength, Math.Min(link.Stiffness, 1), onlyIfLonger: true);
                    break;

                case HairConstraintKind.Bend:
                    //Keep within [bendMinLength, stretchMaxLength].
                    foreach (var link in piece.BendLinks)
                    {
                        float d = (_pos[link.B] - _pos[link.A]).Length;
                        if (d < link.MinLength)
                            SolveDistance(link.A, link.B, link.MinLength, Math.Clamp(link.BendStiffness, 0, 1), onlyIfLonger: false);
                        else if (d > link.MaxLength)
                            SolveDistance(link.A, link.B, link.MaxLength, Math.Clamp(link.StretchStiffness, 0, 1), onlyIfLonger: true);
                    }
                    break;

                case HairConstraintKind.LocalRange:
                    //Keep particles inside a sphere around their skinned reference
                    //position (stops hair drifting off the head). Stiffness < 1
                    //(localStiffnessConstraints) pulls back softly instead of
                    //hard-clamping to the sphere surface.
                    foreach (var range in piece.LocalRanges)
                    {
                        if (IsFixed(range.Particle))
                            continue;
                        Vector3 center = _skinned[range.ReferenceVertex];
                        Vector3 delta = _pos[range.Particle] - center;
                        float dist = delta.Length;
                        if (dist > range.Radius && dist > 1e-7f)
                        {
                            Vector3 clamped = center + delta * (range.Radius / dist);
                            _pos[range.Particle] = Vector3.Lerp(_pos[range.Particle], clamped,
                                Math.Clamp(range.Stiffness, 0, 1));
                        }
                    }
                    break;
            }
        }

        bool IsFixed(int particle) => Array.IndexOf(_piece.FixedParticles, particle) >= 0;

        void SolveDistance(int a, int b, float restLength, float stiffness, bool onlyIfLonger)
        {
            Vector3 delta = _pos[b] - _pos[a];
            float dist = delta.Length;
            if (dist < 1e-7f)
                return;
            if (onlyIfLonger && dist <= restLength)
                return;

            float wa = IsFixed(a) ? 0 : _piece.Particles[a].InvMass;
            float wb = IsFixed(b) ? 0 : _piece.Particles[b].InvMass;
            float wSum = wa + wb;
            if (wSum <= 0)
                return;

            Vector3 correction = delta * ((dist - restLength) / dist) * stiffness;
            _pos[a] += correction * (wa / wSum);
            _pos[b] -= correction * (wb / wSum);
        }

        void SolveCapsules()
        {
            for (int c = 0; c < _piece.Collidables.Count; c++)
            {
                var col = _piece.Collidables[c];
                //Capsule -> model space: collidable offset then the bone transform.
                var world = col.BoneOffset * _boneWorld[col.BoneIndex];
                Vector3 a = Vector3.TransformPosition(col.Start, world);
                Vector3 b = Vector3.TransformPosition(col.End, world);

                for (int p = 0; p < _pos.Length; p++)
                {
                    if (IsFixed(p) || _piece.Particles[p].InvMass <= 0)
                        continue;
                    float minDist = _capsuleMinDist[c][p];
                    if (minDist <= 0)
                        continue;

                    //Closest point on segment ab.
                    Vector3 ab = b - a;
                    float t = ab.LengthSquared > 1e-9f
                        ? MathHelper.Clamp(Vector3.Dot(_pos[p] - a, ab) / ab.LengthSquared, 0, 1) : 0;
                    Vector3 closest = a + ab * t;

                    Vector3 delta = _pos[p] - closest;
                    float dist = delta.Length;
                    if (dist < minDist && dist > 1e-7f)
                        _pos[p] = closest + delta * (minDist / dist);
                }
            }
        }

        /// <summary>
        /// Triangle-frame bone deform (hclSimpleMeshBoneDeformOperator): for each
        /// driven bone, Frame rows = [p0-c, p1-c, cross(e1,e2)/3, c] built from its
        /// source triangle, then Bone = LocalBoneTransform * Frame.
        /// </summary>
        void WriteBones(Dictionary<string, ArrangeBoneParam> arrange)
        {
            for (int i = 0; i < _piece.BoneDeforms.Count; i++)
            {
                var bd = _piece.BoneDeforms[i];
                if (bd.TriangleStart + 2 >= _piece.TriangleIndices.Length)
                    continue;

                //Arrange-collapsed bone: keep the welded (collapsed) transform.
                if (arrange != null && arrange.TryGetValue(_driven[i].Name, out var arr) &&
                    arr.Scale.X * arr.Scale.Y * arr.Scale.Z < 0.001f)
                    continue;

                Vector3 p0 = _pos[_piece.TriangleIndices[bd.TriangleStart]];
                Vector3 p1 = _pos[_piece.TriangleIndices[bd.TriangleStart + 1]];
                Vector3 p2 = _pos[_piece.TriangleIndices[bd.TriangleStart + 2]];

                Vector3 c = (p0 + p1 + p2) / 3.0f;
                Vector3 n = Vector3.Cross(p1 - p0, p2 - p0) / 3.0f;
                Vector3 r0 = p0 - c, r1 = p1 - c;

                var frame = new Matrix4(
                    r0.X, r0.Y, r0.Z, 0,
                    r1.X, r1.Y, r1.Z, 0,
                    n.X, n.Y, n.Z, 0,
                    c.X, c.Y, c.Z, 1);

                _driven[i].Transform = bd.LocalBoneTransform * frame;
            }
        }
    }
}
