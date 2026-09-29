using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace PlayerViewer.Effects.Sim
{
    /// <summary>
    /// A running simulation copied at one frame. The simulation is deterministic, so resuming a
    /// copy and stepping on gives exactly what stepping the original would have: a scrub back
    /// starts from the nearest snapshot instead of frame 0. The copy is kept pristine; each
    /// resume works on a fresh copy of it.
    /// </summary>
    public sealed class SimSnapshot
    {
        readonly EffectSimulation _sim;
        readonly EmitterSetInstance _set;

        /// <summary>The frame the simulation was at.</summary>
        public int Frame { get; }

        /// <summary>An estimate of the memory the copy holds.</summary>
        public long Bytes { get; }

        SimSnapshot(EffectSimulation sim, EmitterSetInstance set, int frame, long bytes)
        {
            _sim = sim;
            _set = set;
            Frame = frame;
            Bytes = bytes;
        }

        public static SimSnapshot Take(EffectSimulation sim, EmitterSetInstance set, int frame)
        {
            var copy = new SimCopier();
            var s = (EffectSimulation)copy.Copy(sim);
            return new SimSnapshot(s, (EmitterSetInstance)copy.Copy(set), frame, copy.Bytes);
        }

        /// <summary>A simulation to continue from <see cref="Frame"/>, and its copy of the set.</summary>
        public (EffectSimulation Sim, EmitterSetInstance Set) Resume()
        {
            var copy = new SimCopier();
            var s = (EffectSimulation)copy.Copy(_sim);
            return (s, (EmitterSetInstance)copy.Copy(_set));
        }
    }

    /// <summary>
    /// Deep copies the simulation's object graph, keeping shared references shared. The resource
    /// side (the file, the emitter definitions and what hangs off them, delegates) is read only
    /// and not copied. Lists and dictionaries are rebuilt rather than copied field by field,
    /// since the emitter keyed ones hash by identity.
    /// </summary>
    sealed class SimCopier
    {
        readonly Dictionary<object, object> _map = new(ReferenceEqualityComparer.Instance);

        public long Bytes;

        static readonly ConcurrentDictionary<Type, FieldInfo[]> FieldsByType = new();
        static readonly ConcurrentDictionary<Type, bool> PlainTypes = new();

        public object Copy(object o)
        {
            if (o == null)
                return null;
            var t = o.GetType();
            if (t.IsValueType)
                return IsPlain(t) ? o : CopyFields(o, RuntimeHelpers.GetUninitializedObject(t), t);
            if (_map.TryGetValue(o, out var done))
                return done;
            if (IsShared(o, t))
                return o;
            Bytes += 32;
            if (o is Array a)
                return CopyArray(a);
            if (t.IsGenericType)
            {
                var g = t.GetGenericTypeDefinition();
                if (g == typeof(List<>))
                {
                    var src = (IList)o;
                    var list = (IList)Activator.CreateInstance(t, src.Count);
                    _map[o] = list;
                    foreach (var item in src)
                        list.Add(Copy(item));
                    return list;
                }
                if (g == typeof(Dictionary<,>))
                {
                    var src = (IDictionary)o;
                    var comparer = t.GetProperty("Comparer").GetValue(o);
                    var dict = (IDictionary)Activator.CreateInstance(t, src.Count, comparer);
                    _map[o] = dict;
                    foreach (DictionaryEntry kv in src)
                        dict.Add(Copy(kv.Key), Copy(kv.Value));
                    return dict;
                }
            }
            var copy = RuntimeHelpers.GetUninitializedObject(t);
            _map[o] = copy;
            return CopyFields(o, copy, t);
        }

        object CopyFields(object from, object to, Type t)
        {
            foreach (var f in Fields(t))
                f.SetValue(to, Copy(f.GetValue(from)));
            return to;
        }

        object CopyArray(Array a)
        {
            var element = a.GetType().GetElementType();
            if (IsPlain(element))
            {
                var clone = (Array)a.Clone();
                _map[a] = clone;
                Bytes += element.IsPrimitive ? Buffer.ByteLength(a) : a.LongLength * 16;
                return clone;
            }
            var lengths = new int[a.Rank];
            for (int d = 0; d < a.Rank; d++)
                lengths[d] = a.GetLength(d);
            var copy = Array.CreateInstance(element, lengths);
            _map[a] = copy;
            Bytes += a.LongLength * 8;
            if (a.Rank == 1)
            {
                for (int i = 0; i < a.Length; i++)
                    copy.SetValue(Copy(a.GetValue(i)), i);
                return copy;
            }
            var index = new int[a.Rank];
            for (long n = 0; n < a.LongLength; n++)
            {
                long rest = n;
                for (int d = a.Rank - 1; d >= 0; d--)
                {
                    index[d] = (int)(rest % lengths[d]);
                    rest /= lengths[d];
                }
                copy.SetValue(Copy(a.GetValue(index)), index);
            }
            return copy;
        }

        /// <summary>The resource side of the graph, which a step never writes.</summary>
        static bool IsShared(object o, Type t) =>
            o is string or Type or Delegate
            || o is EmitterDef or StripeParams or FieldSet or AnimTrack or FieldAnim
            || o is IEmissionPrimitive
            || t.Namespace == "EffectLibrary";

        static FieldInfo[] Fields(Type t) =>
            FieldsByType.GetOrAdd(
                t,
                static t =>
                {
                    var list = new List<FieldInfo>();
                    for (var b = t; b != null && b != typeof(object); b = b.BaseType)
                        foreach (
                            var f in b.GetFields(
                                BindingFlags.Instance
                                    | BindingFlags.Public
                                    | BindingFlags.NonPublic
                                    | BindingFlags.DeclaredOnly
                            )
                        )
                            list.Add(f);
                    return list.ToArray();
                }
            );

        /// <summary>A value type holding no references anywhere inside, copied by assignment.</summary>
        static bool IsPlain(Type t) =>
            PlainTypes.GetOrAdd(
                t,
                static t =>
                {
                    if (t.IsPrimitive || t.IsEnum)
                        return true;
                    if (!t.IsValueType)
                        return false;
                    foreach (var f in Fields(t))
                        if (!IsPlain(f.FieldType))
                            return false;
                    return true;
                }
            );
    }
}
