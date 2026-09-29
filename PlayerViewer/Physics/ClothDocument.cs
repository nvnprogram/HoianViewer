using System;
using System.Collections.Generic;
using PlayerViewer.Phive;

namespace PlayerViewer.Physics
{
    /// <summary>Where a cloth being edited came from, and so where a save puts it back.</summary>
    public class ClothSource
    {
        /// <summary>The actor packs that carry the file, the first being the one it was read from. Empty for a loose file.</summary>
        public List<string> Actors = new();

        /// <summary>The entry inside the packs, <c>Phive/Cloth/&lt;name&gt;.bphcl</c>.</summary>
        public string Entry;

        /// <summary>The file on disk it was opened from, for a loose file.</summary>
        public string DiskPath;

        /// <summary>True when the cloth did not exist before and the packs need wiring for it.</summary>
        public bool IsNew;

        /// <summary>A save makes it the only cloth the pack's ClothList names, as for a copied one.</summary>
        public bool ReplacesList;

        public string FileName => System.IO.Path.GetFileName(Entry ?? DiskPath ?? "cloth.bphcl");

        public override string ToString() =>
            Actors.Count > 0 ? $"{string.Join(", ", Actors)}: {Entry}" : DiskPath ?? "(new)";
    }

    /// <summary>
    /// One cloth file open in the editor: the graph being edited, the bytes it was opened with,
    /// and undo as whole file snapshots. An edit changes the graph in place and bumps
    /// <see cref="Version"/>; <see cref="Commit"/> closes the edit into one undo step, which is
    /// what a widget does when it is released.
    /// </summary>
    public class ClothDocument
    {
        public ClothFile File { get; private set; }
        public ClothFile BaselineFile { get; }
        public byte[] Baseline { get; }
        public ClothSource Source { get; }

        /// <summary>Bumped by every change; the runtime recompiles when it moves.</summary>
        public int Version { get; private set; }

        /// <summary>Bumped when the graph was replaced or pieces, particles or collidables were added or removed.</summary>
        public int StructureVersion { get; private set; }

        readonly List<byte[]> _undo = new();
        readonly List<byte[]> _redo = new();
        byte[] _committed;
        bool _modified;

        const int UndoLimit = 200;

        /// <summary>Off when an outer history keeps the steps: commits then keep no undo of their own.</summary>
        public bool KeepHistory = true;

        /// <summary>Raised after a commit that changed the file, and after a reset.</summary>
        public event Action<ClothDocument> Committed;

        /// <summary>The snapshot of the last commit. Never changed in place, so it can be shared.</summary>
        public byte[] CommittedBytes => _committed;

        public ClothDocument(byte[] bytes, ClothSource source)
        {
            Source = source;
            Baseline = bytes;
            BaselineFile = ClothFile.Load(bytes);
            File = ClothFile.Load(bytes);
            _committed = bytes;
        }

        public bool CanUndo => _undo.Count > 0;
        public bool CanRedo => _redo.Count > 0;
        public int UndoCount => _undo.Count;

        /// <summary>Whether the committed state differs from the file as opened.</summary>
        public bool IsModified => _modified;

        /// <summary>A value changed in place; the graph's shape is the same.</summary>
        public void Changed() => Version++;

        /// <summary>The graph changed shape. The views are rebuilt from a save and reload, so no view holds a stale list.</summary>
        public void StructureChanged()
        {
            File = ClothFile.Load(File.Snapshot());
            Version++;
            StructureVersion++;
        }

        /// <summary>
        /// Takes a file that replaces the one being edited (types imported, a piece generated) as
        /// the current state. With <paramref name="keepState"/> a file of the same pieces and
        /// particle counts counts as a value edit, so the running cloth carries on.
        /// </summary>
        public void Replace(ClothFile file, bool keepState = false)
        {
            var before = File;
            File = ClothFile.Load(file.Snapshot());
            Version++;
            if (!keepState || !SameShape(before, File))
                StructureVersion++;
        }

        static bool SameShape(ClothFile a, ClothFile b)
        {
            var x = a.Container?.ClothDatas;
            var y = b.Container?.ClothDatas;
            if (x == null || y == null || x.Count != y.Count)
                return false;
            for (int i = 0; i < x.Count; i++)
                if (
                    x[i].Name != y[i].Name
                    || x[i].SimClothDatas[0].ParticleCount != y[i].SimClothDatas[0].ParticleCount
                )
                    return false;
            return true;
        }

        /// <summary>Closes the edits since the last commit into one undo step. Nothing happens when the file is unchanged.</summary>
        public void Commit()
        {
            var now = File.Snapshot();
            if (now.AsSpan().SequenceEqual(_committed))
                return;
            if (KeepHistory)
            {
                _undo.Add(_committed);
                if (_undo.Count > UndoLimit)
                    _undo.RemoveAt(0);
                _redo.Clear();
            }
            _committed = now;
            _modified = !now.AsSpan().SequenceEqual(Baseline);
            Committed?.Invoke(this);
        }

        /// <summary>
        /// Makes a committed snapshot the current state, for an outer history. A file of the same
        /// pieces and particle counts counts as a value edit, so the running cloth carries on.
        /// </summary>
        public void Restore(byte[] committed)
        {
            var before = File;
            _committed = committed;
            File = ClothFile.Load(committed);
            _modified = !committed.AsSpan().SequenceEqual(Baseline);
            Version++;
            if (!SameShape(before, File))
                StructureVersion++;
        }

        public void Undo()
        {
            if (_undo.Count == 0)
                return;
            Commit();
            if (_undo.Count == 0)
                return;
            _redo.Add(_committed);
            _committed = _undo[^1];
            _undo.RemoveAt(_undo.Count - 1);
            Load(_committed);
        }

        public void Redo()
        {
            if (_redo.Count == 0)
                return;
            _undo.Add(_committed);
            _committed = _redo[^1];
            _redo.RemoveAt(_redo.Count - 1);
            Load(_committed);
        }

        /// <summary>Back to the file as opened, as one undoable step.</summary>
        public void ResetAll()
        {
            Commit();
            if (_committed.AsSpan().SequenceEqual(Baseline))
                return;
            if (KeepHistory)
            {
                _undo.Add(_committed);
                _redo.Clear();
            }
            _committed = Baseline;
            Load(Baseline);
            Committed?.Invoke(this);
        }

        void Load(byte[] bytes)
        {
            File = ClothFile.Load(bytes);
            _modified = !bytes.AsSpan().SequenceEqual(Baseline);
            Version++;
            StructureVersion++;
        }

        /// <summary>The file as it would be written now.</summary>
        public byte[] Save() => File.Save();

        /// <summary>The bytes last written out, null before the first save.</summary>
        public byte[] LastSaved { get; private set; }

        /// <summary>Whether the committed state differs from what was last written (or from the file as opened, before any save).</summary>
        public bool HasUnsavedChanges =>
            !_committed.AsSpan().SequenceEqual(_savedState ?? Baseline);

        //The snapshot of what was written, comparable with the committed state where the written bytes are not.
        byte[] _savedState;

        /// <summary>Records a write. The baseline stays the file as opened, so a reset still goes back to it.</summary>
        public void MarkSaved(byte[] written)
        {
            LastSaved = written;
            _savedState = File.Snapshot();
            Source.IsNew = false;
        }
    }
}
