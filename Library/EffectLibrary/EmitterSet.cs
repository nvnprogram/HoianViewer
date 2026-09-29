using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace EffectLibrary
{
    /// <summary>The ESTA's emitter sets, in file order, with lookup by name.</summary>
    public sealed class EffectSetList : IReadOnlyList<EmitterSet>
    {
        readonly List<EmitterSet> _sets;
        readonly Dictionary<string, EmitterSet> _byName = new(StringComparer.Ordinal);

        internal EffectSetList(VfxFile file)
        {
            var esta = file.FindSection("ESTA");
            _sets =
                esta == null
                    ? new List<EmitterSet>()
                    : esta.Children.Select((x, i) => new EmitterSet(file, x, i)).ToList();
            foreach (var set in _sets)
                _byName.TryAdd(set.Name, set);
        }

        public EmitterSet this[int index] => _sets[index];

        public int Count => _sets.Count;

        public EmitterSet Find(string name) => _byName.GetValueOrDefault(name);

        public IEnumerator<EmitterSet> GetEnumerator() => _sets.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>
    /// An ESET: the unit the game plays by name. Payload 0xB8 bytes in v46.
    /// </summary>
    public sealed class EmitterSet
    {
        public const int PayloadSize = 0xB8;
        public const int NameOffset = 0x10;
        public const int NameLength = 96;

        public VfxFile File { get; }
        public VfxSection Section { get; }
        public int Index { get; }

        /// <summary>The top level emitters; child emitters hang off them.</summary>
        public IReadOnlyList<Emitter> Emitters { get; }

        internal EmitterSet(VfxFile file, VfxSection section, int index)
        {
            if (section.Tag != "ESET")
                throw new InvalidOperationException($"ESTA child {index} is {section.Tag}");
            File = file;
            Section = section;
            Index = index;
            Emitters = section.Children.Select((x, i) => new Emitter(this, null, x, i)).ToList();
        }

        public string Name
        {
            get => Section.GetString(NameOffset, NameLength);
            set => Section.SetString(NameOffset, NameLength, value);
        }

        /// <summary>Emitters in the set including children, as the game counts them.</summary>
        public int EmitterCountField => (int)Section.GetU32(0x70);

        public ushort UserDataBit => Section.GetU16(0x74);

        public uint GetUserData(int index) => Section.GetU32(0x78 + 4 * index);

        /// <summary>Every emitter, each parent before its children.</summary>
        public IEnumerable<Emitter> AllEmitters => Emitters.SelectMany(x => x.SelfAndDescendants);

        public override string ToString() => Name;
    }
}
