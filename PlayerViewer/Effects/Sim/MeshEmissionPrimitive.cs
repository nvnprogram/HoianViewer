using System;
using System.Numerics;
using EffectLibrary;

namespace PlayerViewer.Effects.Sim
{
    /// <summary>The primitive volume shape's source: a primitive mesh's vertex positions and normals.</summary>
    public sealed class MeshEmissionPrimitive : IEmissionPrimitive
    {
        readonly Vector3[] _positions;
        readonly Vector3[] _normals;

        MeshEmissionPrimitive(Vector3[] positions, Vector3[] normals)
        {
            _positions = positions;
            _normals = normals;
        }

        public int Count => _positions.Length;

        public Vector3 Position(int index) => _positions[index];

        public Vector3 Normal(int index) => _normals == null ? Vector3.Zero : _normals[index];

        /// <summary>The emitter's volume primitive, or null when it names none or it will not load.</summary>
        public static IEmissionPrimitive From(Emitter emitter, ulong id)
        {
            try
            {
                var mesh = EffectMesh.From(emitter.Set.File, emitter.ResolvePrimitive(id));
                if (mesh?.Find(EffectBindings.PositionAttr) is not EffectVertexAttribute pos)
                    return null;
                int n = mesh.VertexCount(pos);
                var positions = new Vector3[n];
                Span<float> v = stackalloc float[4];
                for (int i = 0; i < n; i++)
                {
                    v.Clear();
                    mesh.Read(pos, i, v);
                    positions[i] = new Vector3(v[0], v[1], v[2]);
                }
                Vector3[] normals = null;
                if (mesh.Find(EffectBindings.NormalAttr) is EffectVertexAttribute nrm)
                {
                    normals = new Vector3[n];
                    for (int i = 0; i < n && i < mesh.VertexCount(nrm); i++)
                    {
                        v.Clear();
                        mesh.Read(nrm, i, v);
                        normals[i] = new Vector3(v[0], v[1], v[2]);
                    }
                }
                return new MeshEmissionPrimitive(positions, normals);
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
