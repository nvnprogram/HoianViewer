using System;
using System.Linq;
using OpenTK;

namespace PlayerViewer.Phive
{
    /// <summary>How an instance runs its piece; the parts the game sets per cloth component rather than per file.</summary>
    public class ClothInstanceOptions
    {
        /// <summary>Keep each driven bone at the skeleton's distance from its cloth parent: the cloth component's flag 0x20000000.</summary>
        public bool KeepSegmentLength;

        /// <summary>Collide sphere collidables.</summary>
        public bool CollideSpheres = true;
    }

    /// <summary>One cloth piece simulated against a skeleton and written back to it, as the game's cloth runtime steps it.</summary>
    public class ClothInstance
    {
        public bool Enabled = true;

        readonly ClothPiece _piece;
        readonly IClothBinding _binding;
        readonly ClothInstanceOptions _options;

        readonly Vector3[] _pos;
        readonly Vector3[] _prev;
        readonly Vector3[] _before; //positions before the latest step, for the render blend
        readonly Vector3[] _skinBefore,
            _skinAfter; //each particle's skinned point at those two steps
        readonly Vector3[] _render; //what the bones are written from
        readonly Vector3[] _skinned; //animation pose skinned vertices
        readonly Matrix4[] _boneWorld; //current per frame transform set
        readonly int[] _refVertex; //particle -> reference buffer vertex (-1: none)
        readonly bool[] _fixed;
        bool _primed;
        float _accumulator;
        float _lastDt; //the particle time step of the previous step
        float _transitionTime; //seconds since the release from the animation pose
        readonly Matrix4[] _colWorld; //collidable poses for this step
        readonly Matrix4[] _colPrevWorld; //and for the previous one, for their velocity
        bool _colPrevValid;

        //The state an export converges back to, so a looping clip does not jump when it wraps.
        Vector3[] _convergePos;
        Vector3[] _convergePrev;

        const float StepTime = 1.0f / 60.0f;
        const int MaxSteps = 4;

        //After a reset: one second of 30 Hz steps while the transition set releases the cloth.
        const float WarmUpStep = 1.0f / 30.0f;
        const int WarmUpSteps = 30;

        //Per driven bone: the bone record the game sets up at bind (SetUpAim).
        readonly int[] _driven; //cloth bone index per bone deform
        readonly int[] _writeOrder; //driven bone indices in cloth bone order
        readonly int[] _aimChild; //cloth bone index or -1
        readonly Quaternion[] _aimQuat;
        readonly bool[] _aimAligned; //child sits on the x axis: aim x at it directly
        readonly bool[] _aimNegate;
        readonly int[] _lengthParent; //cloth bone index or -1
        readonly float[] _segmentLength;
        readonly Matrix4[] _rawFrame; //this frame's deform output per driven bone
        Matrix4[] _skeletonBefore; //diagnostics: the skeleton pose each driven bone had before the write

        public ClothPiece Piece => _piece;

        /// <summary>Current particle positions; the constraint kernels move these.</summary>
        public Vector3[] Positions => _pos;

        /// <summary>Previous particle positions; the gap to <see cref="Positions"/> is the velocity.</summary>
        public Vector3[] PreviousPositions => _prev;

        /// <summary>The reference vertices skinned to this frame's animation pose.</summary>
        public Vector3[] Skinned => _skinned;

        /// <summary>Seconds since the release from the animation pose.</summary>
        public float TransitionTime => _transitionTime;

        public bool IsFixed(int particle) => _fixed[particle];

        /// <summary>The positions the bones were last written from: the latest step blended with the one before by the backlog.</summary>
        public Vector3[] RenderPositions => _render;

        /// <summary>Whether the instance has been started from an animation pose.</summary>
        public bool Primed => _primed;

        /// <summary>A collidable's pose as of the last update: its bone as fed, times its offset.</summary>
        public Matrix4 CollidableWorld(int collidable) =>
            _piece.Collidables[collidable].BoneOffset
            * _boneWorld[_piece.Collidables[collidable].BoneIndex];

        /// <summary>Whether the collision pass collides this collidable (a shape with a kernel, enabled, and spheres only when asked).</summary>
        public bool IsCollided(int collidable) => Collides(_piece.Collidables[collidable]);

        /// <summary>A cloth bone's world matrix as fed to the last update, axes normalised.</summary>
        public Matrix4 FedBoneWorld(int bone) => _boneWorld[bone];

        /// <summary>
        /// Takes over the running state of another instance of the same piece, so a piece that is
        /// recompiled after an edit carries on from where it was rather than restarting. Nothing is
        /// taken when the particle count differs.
        /// </summary>
        public void CopyStateFrom(ClothInstance other)
        {
            if (other == null || !other._primed || other._pos.Length != _pos.Length)
                return;
            Array.Copy(other._pos, _pos, _pos.Length);
            Array.Copy(other._prev, _prev, _prev.Length);
            Array.Copy(other._before, _before, _before.Length);
            Array.Copy(other._skinBefore, _skinBefore, _skinBefore.Length);
            Array.Copy(other._skinAfter, _skinAfter, _skinAfter.Length);
            Array.Copy(other._render, _render, _render.Length);
            _primed = true;
            _accumulator = other._accumulator;
            _lastDt = other._lastDt;
            _transitionTime = other._transitionTime;
            _colPrevValid =
                other._colPrevValid && other._colPrevWorld.Length == _colPrevWorld.Length;
            if (_colPrevValid)
                Array.Copy(other._colPrevWorld, _colPrevWorld, _colPrevWorld.Length);
        }

        public float InvMass(int p) => _fixed[p] ? 0 : _piece.Particles[p].InvMass;

        /// <summary>
        /// Binds a compiled piece to a skeleton. Returns null when the piece has no skin or
        /// drives no bone, or the binding does not cover its transform set.
        /// </summary>
        public static ClothInstance Create(
            ClothPiece piece,
            IClothBinding binding,
            ClothInstanceOptions options = null
        )
        {
            if (piece.SkinVertices == null || piece.BoneDeforms.Count == 0)
                return null;
            if (binding == null || binding.BoneCount != piece.BoneNames.Length)
                return null;
            return new ClothInstance(piece, binding, options ?? new ClothInstanceOptions());
        }

        ClothInstance(ClothPiece piece, IClothBinding binding, ClothInstanceOptions options)
        {
            _piece = piece;
            _binding = binding;
            _options = options;
            _driven = piece.BoneDeforms.Select(d => d.BoneIndex).ToArray();

            int nd = piece.BoneDeforms.Count;
            _writeOrder = Enumerable
                .Range(0, nd)
                .OrderBy(i => piece.BoneDeforms[i].BoneIndex)
                .ToArray();
            _aimChild = new int[nd];
            _aimQuat = new Quaternion[nd];
            _aimAligned = new bool[nd];
            _aimNegate = new bool[nd];
            _lengthParent = new int[nd];
            _segmentLength = new float[nd];
            _rawFrame = new Matrix4[nd];
            for (int i = 0; i < nd; i++)
                SetUpAim(i);

            int n = piece.Particles.Length;
            _pos = new Vector3[n];
            _prev = new Vector3[n];
            _before = new Vector3[n];
            _skinBefore = new Vector3[n];
            _skinAfter = new Vector3[n];
            _render = new Vector3[n];
            _skinned = new Vector3[piece.SkinVertices.Length];
            _boneWorld = new Matrix4[piece.BoneNames.Length];
            _fixed = new bool[n];
            _colWorld = new Matrix4[piece.Collidables.Count];
            _colPrevWorld = new Matrix4[piece.Collidables.Count];
            foreach (int f in piece.FixedParticles)
                if (f >= 0 && f < n)
                    _fixed[f] = true;
            _refVertex = piece.ReferenceVertices;
        }

        /// <summary>Restarts the sim from the current animation pose next update.</summary>
        public void Reset()
        {
            _primed = false;
            _colPrevValid = false;
            _lastDt = 0;
            _convergePos = null;
            _convergePrev = null;
        }

        /// <summary>
        /// Records the current particle state as the pose an export converges back to.
        /// Verlet carries velocity in the gap between the two buffers, so both are kept.
        /// </summary>
        public void CaptureConvergeState()
        {
            if (!_primed)
                return;
            _convergePos = (Vector3[])_pos.Clone();
            _convergePrev = (Vector3[])_prev.Clone();
        }

        /// <summary>
        /// Runs after the skeleton holds this frame's animation pose and overwrites the driven
        /// bones with the simulated pose. <paramref name="boneWeights"/>, per cloth bone, blends
        /// the written pose between the cloth (1) and the skeleton (0); a bone at 0 is not
        /// written at all. Null means 1 for every bone.
        /// </summary>
        public void Update(float dt, float[] boneWeights = null, float convergeWeight = 0)
        {
            if (!Enabled)
                return;

            for (int i = 0; i < _boneWorld.Length; i++)
                _boneWorld[i] = UnitAxes(_binding.GetWorld(i));
            //The kept segment lengths are this frame's skeleton pose, before anything is written.
            for (int i = 0; i < _driven.Length; i++)
                _segmentLength[i] =
                    _lengthParent[i] >= 0
                        ? (
                            _binding.GetWorld(_driven[i]).Row3.Xyz
                            - _binding.GetWorld(_lengthParent[i]).Row3.Xyz
                        ).Length
                        : 0;

            SkinVertices();

            if (!_primed)
            {
                for (int p = 0; p < _pos.Length; p++)
                    _pos[p] = _prev[p] = SkinnedForParticle(p);
                _transitionTime = 0;
                _lastDt = 0;
                _colPrevValid = false;
                _primed = true;
                for (int i = 0; i < WarmUpSteps; i++)
                    Step(WarmUpStep);
                Array.Copy(_pos, _before, _pos.Length);
                for (int p = 0; p < _pos.Length; p++)
                    _skinBefore[p] = _skinAfter[p] = SkinnedForParticle(p);
                _accumulator = 0;
            }

            //Fixed 60 Hz steps; the offset from the skin is blended by the backlog onto this frame's pose.
            _accumulator = Math.Min(_accumulator + dt, StepTime * MaxSteps);
            while (_accumulator >= StepTime)
            {
                Array.Copy(_pos, _before, _pos.Length);
                Array.Copy(_skinAfter, _skinBefore, _skinAfter.Length);
                Step(StepTime);
                for (int p = 0; p < _pos.Length; p++)
                    _skinAfter[p] = SkinnedForParticle(p);
                _accumulator -= StepTime;
            }
            float alpha = MathHelper.Clamp(_accumulator / StepTime, 0, 1);
            for (int p = 0; p < _pos.Length; p++)
            {
                var at = _before[p] + (_pos[p] - _before[p]) * alpha;
                if (_refVertex[p] >= 0 && _refVertex[p] < _skinned.Length)
                    at +=
                        _skinned[_refVertex[p]]
                        - (_skinBefore[p] + (_skinAfter[p] - _skinBefore[p]) * alpha);
                _render[p] = at;
            }
            //A blended fixed particle would trail its bone by a step.
            foreach (int f in _piece.FixedParticles)
                if (f >= 0 && f < _render.Length)
                    _render[f] = SkinnedForParticle(f);

            //Converge before the bones are written, so the rendered frame is the blended one.
            if (convergeWeight > 0 && _convergePos != null)
            {
                float w = MathHelper.Clamp(convergeWeight, 0, 1);
                for (int p = 0; p < _pos.Length; p++)
                {
                    _pos[p] += (_convergePos[p] - _pos[p]) * w;
                    _prev[p] += (_convergePrev[p] - _prev[p]) * w;
                    _render[p] = _pos[p];
                }
            }

            WriteBones(boneWeights);
        }

        /// <summary>The pose with its scale removed.</summary>
        static Matrix4 UnitAxes(Matrix4 m)
        {
            Vector3 x = m.Row0.Xyz;
            if (x.LengthSquared < 1e-20f)
                return m;
            x.Normalize();
            Vector3 z = Vector3.Cross(x, m.Row1.Xyz);
            if (z.LengthSquared < 1e-20f)
                return m;
            z.Normalize();
            Vector3 y = Vector3.Cross(z, x);
            return new Matrix4(new Vector4(x, 0), new Vector4(y, 0), new Vector4(z, 0), m.Row3);
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
                    int bone =
                        subset < _piece.TransformSubset.Length
                            ? _piece.TransformSubset[subset]
                            : subset;
                    //Bone space: a position per blend slot. Object space: one, through boneFromSkinMesh.
                    Vector3 p = sv.LocalPosPerBone != null ? sv.LocalPosPerBone[b] : sv.LocalPos;
                    var mat =
                        sv.LocalPosPerBone != null
                            ? _boneWorld[bone]
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

            //A changed step keeps the velocity, not the per step displacement.
            if (_lastDt > 0 && _lastDt != subDt)
            {
                float keep = 1.0f - subDt / _lastDt;
                for (int p = 0; p < _pos.Length; p++)
                    _prev[p] += (_pos[p] - _prev[p]) * keep;
            }
            _lastDt = subDt;

            for (int c = 0; c < piece.Collidables.Count; c++)
                _colWorld[c] =
                    piece.Collidables[c].BoneOffset * _boneWorld[piece.Collidables[c].BoneIndex];
            if (!_colPrevValid)
            {
                Array.Copy(_colWorld, _colPrevWorld, _colWorld.Length);
                _colPrevValid = true;
            }

            for (int s = 0; s < subSteps; s++)
            {
                //Fixed particles sit at their skinned positions with no velocity.
                foreach (int f in piece.FixedParticles)
                    _prev[f] = _pos[f] = SkinnedForParticle(f);

                //Verlet integration for dynamic particles.
                float d = piece.DampingPerSecond;
                float damping =
                    d >= 1 ? 0
                    : d == 0 ? 1
                    : MathF.Pow(1.0f - d, subDt);
                Vector3 gravityStep = piece.Gravity * subDt * subDt;
                for (int p = 0; p < _pos.Length; p++)
                {
                    if (piece.Particles[p].InvMass <= 0 || _fixed[p])
                        continue;
                    Vector3 velocity = (_pos[p] - _prev[p]) * damping;
                    _prev[p] = _pos[p];
                    _pos[p] += velocity + gravityStep;
                }

                //The authored execution order; -1 is the collision pass.
                for (int iter = 0; iter < Math.Clamp(piece.SolveIterations, 1, 8); iter++)
                    foreach (int setIndex in piece.ConstraintExecution)
                    {
                        if (setIndex < 0)
                            SolveCollisions(s, subSteps);
                        else if (setIndex < piece.ConstraintSets.Count)
                            piece.ConstraintSets[setIndex].Solve(this);
                    }

                _transitionTime += subDt;
            }
            Array.Copy(_colWorld, _colPrevWorld, _colWorld.Length);
        }

        bool Collides(ClothCollidable col) =>
            col.Enabled
            && (
                col.Shape == CollidableShapeKind.Capsule
                || col.Shape == CollidableShapeKind.Sphere && _options.CollideSpheres
            );

        /// <summary>
        /// The collision pass: every free particle is pushed out of each collided shape, then the
        /// virtual collision points are; <see cref="Contact"/> sets the velocity.
        /// </summary>
        void SolveCollisions(int subStep, int subSteps)
        {
            var piece = _piece;
            float fraction = (subStep + 1) / (float)subSteps;
            for (int pass = 0; pass < 2; pass++)
            for (int c = 0; c < piece.Collidables.Count; c++)
            {
                if (
                    !piece.UseAllInstanceCollidables
                    && Array.IndexOf(piece.InstanceCollidablesUsed, c) < 0
                )
                    continue;
                var col = piece.Collidables[c];
                if (!Collides(col))
                    continue;
                if (pass == 1 && (!col.VirtualPoints || piece.VirtualPoints.Count == 0))
                    continue;
                Matrix4 world = _colWorld[c];
                Matrix4 old = _colPrevWorld[c];
                Vector3 linDt = (world.Row3.Xyz - old.Row3.Xyz) / subSteps;
                Vector3 angDt = RotationDelta(old, world) / subSteps;
                world.Row3.Xyz = old.Row3.Xyz + linDt * subSteps * fraction;
                var shape = new ShapePose(col, world);
                uint bit = c < 32 ? 1u << c : 0;

                if (pass == 0)
                {
                    for (int p = 0; p < _pos.Length; p++)
                    {
                        if (_fixed[p] || piece.Particles[p].InvMass <= 0)
                            continue;
                        if ((piece.Particles[p].CollisionMask & bit) == 0)
                            continue;
                        if (
                            shape.PushOut(
                                _pos[p],
                                piece.Particles[p].Radius,
                                out Vector3 pushed,
                                out Vector3 n,
                                out Vector3 surface
                            )
                        )
                        {
                            _pos[p] = pushed;
                            Contact(p, n, surface, shape.Centre, linDt, angDt);
                        }
                    }
                }
                else
                {
                    foreach (var vp in piece.VirtualPoints)
                    {
                        int o = vp.Owner;
                        if (
                            o < 0
                            || o >= _pos.Length
                            || vp.Opposite < 0
                            || vp.Opposite >= _pos.Length
                        )
                            continue;
                        if (_fixed[o] || piece.Particles[o].InvMass <= 0)
                            continue;
                        if ((piece.Particles[o].CollisionMask & bit) == 0)
                            continue;
                        Vector3 m = _pos[o] + (_pos[vp.Opposite] - _pos[o]) * vp.Barycentric;
                        if (
                            shape.PushOut(
                                m,
                                piece.Particles[o].Radius,
                                out Vector3 pushed,
                                out _,
                                out _
                            )
                        )
                            _pos[o] += pushed - m;
                    }
                }
            }
        }

        /// <summary>One sphere or capsule at one pose: closest point and push out.</summary>
        readonly struct ShapePose
        {
            readonly bool _capsule;
            readonly Vector3 _a,
                _b;
            readonly float _radius;
            public readonly Vector3 Centre;

            public ShapePose(ClothCollidable col, Matrix4 world)
            {
                _capsule = col.Shape == CollidableShapeKind.Capsule;
                _radius = col.Radius;
                Centre = world.Row3.Xyz;
                _a = Vector3.TransformPosition(col.Start, world);
                _b = Vector3.TransformPosition(col.End, world);
            }

            public bool PushOut(
                Vector3 p,
                float particleRadius,
                out Vector3 pushed,
                out Vector3 n,
                out Vector3 surface
            )
            {
                pushed = p;
                n = Vector3.Zero;
                surface = p;
                Vector3 closest = _capsule ? Geometry.ClosestOnSegment(p, _a, _b) : _a;
                Vector3 delta = p - closest;
                float dist = delta.Length;
                if (dist >= _radius + particleRadius || dist <= 1e-7f)
                    return false;
                n = delta / dist;
                surface = closest + n * _radius;
                pushed = surface + n * particleRadius;
                return true;
            }
        }

        /// <summary>
        /// Rotation from one pose to the next as an angle times axis vector, for the
        /// row vector matrices the scene uses.
        /// </summary>
        static Vector3 RotationDelta(Matrix4 from, Matrix4 to)
        {
            Matrix3 a = new Matrix3(from);
            Matrix3 b = new Matrix3(to);
            a.Row0.Normalize();
            a.Row1.Normalize();
            a.Row2.Normalize();
            b.Row0.Normalize();
            b.Row1.Normalize();
            b.Row2.Normalize();
            a.Transpose();
            Matrix3 d = a * b;
            var axis = new Vector3(d.M23 - d.M32, d.M31 - d.M13, d.M12 - d.M21);
            float len = axis.Length;
            if (len < 1e-7f)
                return Vector3.Zero;
            float cos = MathHelper.Clamp((d.Trace - 1.0f) * 0.5f, -1.0f, 1.0f);
            return axis * (MathF.Acos(cos) / len);
        }

        /// <summary>
        /// Contact response on the previous position: the displacement relative to the
        /// collidable's own motion, less its normal part, is added back scaled by the
        /// particle's friction. The push out velocity itself is kept.
        /// </summary>
        void Contact(
            int p,
            Vector3 n,
            Vector3 surface,
            Vector3 centre,
            Vector3 linDt,
            Vector3 angDt
        )
        {
            Vector3 colDt = linDt + Vector3.Cross(angDt, surface - centre);
            Vector3 rel = _pos[p] - _prev[p] - colDt;
            rel -= n * Vector3.Dot(n, rel);
            _prev[p] += rel * _piece.Particles[p].Friction;
        }

        /// <summary>
        /// A driven bone's cloth frame from particle positions: its triangle's frame through the
        /// local bone transform, translation raw, rotation rebuilt around the boneAxis column.
        /// </summary>
        Matrix4 DeformFrame(Vector3[] positions, ClothBoneDeform bd)
        {
            var frame = ClothAuthor.TriangleFrame(
                positions[_piece.TriangleIndices[bd.TriangleStart]],
                positions[_piece.TriangleIndices[bd.TriangleStart + 1]],
                positions[_piece.TriangleIndices[bd.TriangleStart + 2]]
            );
            Matrix4 raw = bd.LocalBoneTransform * frame;
            var (x, y, z) = Orthonormal(raw.Row0.Xyz, raw.Row2.Xyz, _piece.BoneAxis);
            return new Matrix4(
                new Vector4(x, 0),
                new Vector4(y, 0),
                new Vector4(z, 0),
                new Vector4(raw.Row3.Xyz, 1)
            );
        }

        /// <summary>A frame rebuilt orthonormal from its x and z axes, keeping the boneAxis one (0 x, else z) exact in direction.</summary>
        static (Vector3 X, Vector3 Y, Vector3 Z) Orthonormal(Vector3 x, Vector3 z, int boneAxis)
        {
            Vector3 y;
            if (boneAxis == 0)
            {
                x.Normalize();
                y = Vector3.Cross(z, x).Normalized();
                z = Vector3.Cross(x, y).Normalized();
            }
            else
            {
                z.Normalize();
                y = Vector3.Cross(z, x).Normalized();
                x = Vector3.Cross(y, z).Normalized();
            }
            return (x, y, z);
        }

        /// <summary>
        /// The bone record the game sets up for a driven bone at bind: the cloth child it aims at,
        /// the rotation taking that child's rest direction onto x, and the parent of its length.
        /// </summary>
        void SetUpAim(int i)
        {
            int bone = _driven[i];
            _aimChild[i] = -1;
            _lengthParent[i] = _binding.ParentOf(bone);
            _aimQuat[i] = Quaternion.Identity;

            int child = _binding.FirstChildOf(bone);
            if (child < 0)
                return;
            _aimChild[i] = child;

            Vector3 d = _binding.RestLocalTranslation(child);
            if (d.LengthSquared <= 0)
            {
                _aimChild[i] = -1;
                return;
            }
            d.Normalize();
            Vector3 a = Vector3.UnitX;
            float dot = Vector3.Dot(d, a);
            if (Math.Abs(1.0f - Math.Abs(dot)) <= 1.19e-7f)
            {
                _aimAligned[i] = true;
                _aimNegate[i] = dot < 0;
                return;
            }
            if (dot < 0)
            {
                _aimNegate[i] = true;
                d = -d;
                dot = -dot;
            }
            float w = MathF.Sqrt((1.0f + dot) * 0.5f);
            if (w <= 1e-6f)
            {
                //Opposite the axis: a half turn about any perpendicular.
                Vector3 axis = Vector3.Cross(d, Vector3.UnitY);
                if (axis.LengthSquared <= 1e-12f)
                    axis = Vector3.Cross(d, Vector3.UnitZ);
                axis.Normalize();
                _aimQuat[i] = new Quaternion(axis, 0);
                return;
            }
            Vector3 v = Vector3.Cross(d, a) / (2.0f * w);
            _aimQuat[i] = new Quaternion(v, w);
        }

        /// <summary>The transform set position of a cloth bone: the deform output for a driven one, the fed pose otherwise.</summary>
        Vector3 RawPosition(int clothIndex)
        {
            for (int i = 0; i < _piece.BoneDeforms.Count; i++)
                if (_piece.BoneDeforms[i].BoneIndex == clothIndex)
                    return _rawFrame[i].Row3.Xyz;
            return _boneWorld[clothIndex].Row3.Xyz;
        }

        static Vector3 Rotate(Quaternion q, Vector3 p)
        {
            Vector3 v = q.Xyz;
            return p + 2.0f * Vector3.Cross(v, Vector3.Cross(v, p) + q.W * p);
        }

        /// <summary>Writes every driven bone from its deform frame, aimed, length kept and blended with the skeleton by its weight.</summary>
        void WriteBones(float[] boneWeights)
        {
            //Every deform output first: a bone aims at its child's raw origin, not its written one.
            for (int i = 0; i < _piece.BoneDeforms.Count; i++)
            {
                var bd = _piece.BoneDeforms[i];
                _rawFrame[i] =
                    bd.TriangleStart + 2 < _piece.TriangleIndices.Length
                        ? DeformFrame(_render, bd)
                        : _boneWorld[bd.BoneIndex];
            }

            foreach (int i in _writeOrder)
            {
                var bd = _piece.BoneDeforms[i];
                if (bd.TriangleStart + 2 >= _piece.TriangleIndices.Length)
                    continue;

                float weight = boneWeights != null ? boneWeights[bd.BoneIndex] : 1.0f;
                if (weight <= 0)
                    continue;

                Matrix4 cloth = _rawFrame[i];
                Vector3 x = cloth.Row0.Xyz,
                    y = cloth.Row1.Xyz,
                    z = cloth.Row2.Xyz;
                Vector3 t = cloth.Row3.Xyz;

                //Aim the child's rest direction at its raw origin; y and z follow from the raw z.
                if (_piece.AimAtChild && _aimChild[i] >= 0)
                {
                    Vector3 dir = RawPosition(_aimChild[i]) - t;
                    if (dir.LengthSquared > 1e-12f)
                    {
                        dir.Normalize();
                        if (_aimAligned[i])
                            x = _aimNegate[i] ? -dir : dir;
                        else
                        {
                            Vector3 local = new Vector3(
                                Vector3.Dot(x, dir),
                                Vector3.Dot(y, dir),
                                Vector3.Dot(z, dir)
                            );
                            if (_aimNegate[i])
                                local = -local;
                            Vector3 v = Rotate(_aimQuat[i], local);
                            x = v.X * x + v.Y * y + v.Z * z;
                        }
                        y = Vector3.Cross(z, x).Normalized();
                        z = Vector3.Cross(x, y);
                    }
                }

                //The skeleton's segment length from the parent's current origin, along the raw direction.
                if (_options.KeepSegmentLength && _lengthParent[i] >= 0 && _segmentLength[i] > 0)
                {
                    Vector3 parent = _binding.GetWorld(_lengthParent[i]).Row3.Xyz;
                    Vector3 d = t - parent;
                    if (d.LengthSquared > 1e-12f)
                        t = parent + d.Normalized() * _segmentLength[i];
                }

                Matrix4 skeleton = _binding.GetWorld(bd.BoneIndex);
                _skeletonBefore ??= new Matrix4[_piece.BoneDeforms.Count];
                _skeletonBefore[i] = skeleton;
                Vector3 sx = skeleton.Row0.Xyz,
                    sy = skeleton.Row1.Xyz,
                    sz = skeleton.Row2.Xyz;
                if (weight < 1)
                {
                    //Blend toward the skeleton's unit axes and origin, then rebuild the frame.
                    x = Vector3.Lerp(sx.Normalized(), x, weight);
                    y = Vector3.Lerp(sy.Normalized(), y, weight);
                    z = Vector3.Lerp(sz.Normalized(), z, weight);
                    (x, y, z) = Orthonormal(x, z, _piece.BoneAxis);
                    t = Vector3.Lerp(skeleton.Row3.Xyz, t, weight);
                }
                _binding.SetWorld(
                    bd.BoneIndex,
                    new Matrix4(
                        new Vector4(x * sx.Length, 0),
                        new Vector4(y * sy.Length, 0),
                        new Vector4(z * sz.Length, 0),
                        new Vector4(t, 1)
                    )
                );
            }
        }

        /// <summary>Diagnostics: the particles against their skinned targets, the fed bones, the driven bones, the collidables.</summary>
        public void DebugDump()
        {
            Console.WriteLine($"[Cloth] {_piece.Name}");
            Matrix4 headInv = Matrix4.Identity;
            ClothCollidable head = _piece.Collidables.Count > 0 ? _piece.Collidables[0] : null;
            Vector3 ha = Vector3.Zero,
                hb = Vector3.Zero;
            if (head != null)
            {
                var hw = head.BoneOffset * _boneWorld[head.BoneIndex];
                headInv = Matrix4.Invert(hw);
                ha = Vector3.TransformPosition(head.Start, hw);
                hb = Vector3.TransformPosition(head.End, hw);
            }
            float AxisDist(Vector3 p) => (p - Geometry.ClosestOnSegment(p, ha, hb)).Length;
            for (int p = 0; p < _pos.Length; p++)
            {
                Vector3 target = SkinnedForParticle(p);
                Vector3 l = Vector3.TransformPosition(_pos[p], headInv);
                Vector3 ls = Vector3.TransformPosition(target, headInv);
                Console.WriteLine(
                    $"  p{p}{(_fixed[p] ? " FIX" : "    ")} pos=({_pos[p].X:F3},{_pos[p].Y:F3},{_pos[p].Z:F3}) "
                        + $"skin=({target.X:F3},{target.Y:F3},{target.Z:F3}) drift={(_pos[p] - target).Length:F3} "
                        + $"colLocal=({l.X:F3},{l.Y:F3},{l.Z:F3}) skinLocal=({ls.X:F3},{ls.Y:F3},{ls.Z:F3}) "
                        + $"axis={AxisDist(_pos[p]):F3} skinAxis={AxisDist(target):F3}"
                );
            }
            for (int b = 0; b < _boneWorld.Length; b++)
            {
                Vector3 t = _boneWorld[b].Row3.Xyz;
                Vector3 r = _boneWorld[0].Row3.Xyz;
                Console.WriteLine(
                    $"  clothBone {_piece.BoneNames[b]} fed=({t.X:F3},{t.Y:F3},{t.Z:F3}) fromRoot={(t - r).Length:F3}"
                );
            }
            for (int i = 0; i < _piece.BoneDeforms.Count; i++)
            {
                var bd = _piece.BoneDeforms[i];
                int bi = bd.BoneIndex;
                Matrix4 now = _binding.GetWorld(bi);
                Matrix4 skel = _skeletonBefore != null ? _skeletonBefore[i] : now;
                Console.WriteLine(
                    $"  bone {_piece.BoneNames[bi]} tri=({_piece.TriangleIndices[bd.TriangleStart]},{_piece.TriangleIndices[bd.TriangleStart + 1]},{_piece.TriangleIndices[bd.TriangleStart + 2]})"
                );
                Console.WriteLine($"    restDeform {Fmt(DeformFrame(_piece.RestPositions, bd))}");
                Console.WriteLine($"    clothRef   {Fmt(_piece.BoneRefPose[bi])}");
                Console.WriteLine($"    sceneBind  {Fmt(_binding.BindWorld(bi))}");
                Console.WriteLine($"    skeleton   {Fmt(skel)}");
                Console.WriteLine($"    written    {Fmt(now)}");
            }
            foreach (var set in _piece.ConstraintSets.OfType<ClothConstraint.LocalRange>())
            foreach (var range in set.Ranges)
            {
                float d = (_pos[range.Particle] - _skinned[range.ReferenceVertex]).Length;
                Console.WriteLine(
                    $"  range p{range.Particle} ref=v{range.ReferenceVertex} r={range.Radius:F3} k={range.Stiffness:F3} dist={d:F3}"
                );
            }
            foreach (var col in _piece.Collidables)
            {
                var world = col.BoneOffset * _boneWorld[col.BoneIndex];
                Vector3 a = Vector3.TransformPosition(col.Start, world);
                Vector3 b = Vector3.TransformPosition(col.End, world);
                Console.WriteLine(
                    $"  {col.Shape}{(Collides(col) ? "" : " (not collided)")} '{col.Name}' bone={_piece.BoneNames[col.BoneIndex]} r={col.Radius:F3} "
                        + $"a=({a.X:F3},{a.Y:F3},{a.Z:F3}) b=({b.X:F3},{b.Y:F3},{b.Z:F3})"
                );
            }
        }

        static string Fmt(Matrix4 m) =>
            $"x=({m.M11:F3},{m.M12:F3},{m.M13:F3}) y=({m.M21:F3},{m.M22:F3},{m.M23:F3}) z=({m.M31:F3},{m.M32:F3},{m.M33:F3}) t=({m.M41:F3},{m.M42:F3},{m.M43:F3})";
    }
}
