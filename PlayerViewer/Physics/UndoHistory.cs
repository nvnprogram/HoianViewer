using System;
using System.Collections.Generic;

namespace PlayerViewer.Physics
{
    /// <summary>
    /// A linear history of whole states: the state after each step with the step's label, and a
    /// cursor on the current one. Undo and redo move the cursor; a new step drops what was undone.
    /// The oldest steps go past <see cref="Limit"/> steps or past <see cref="Budget"/> bytes as
    /// <see cref="Size"/> counts them.
    /// </summary>
    public sealed class UndoHistory<T>
        where T : class
    {
        readonly List<(string Label, T State)> _states = new();
        int _cursor = -1;

        public int Limit = 100;
        public long Budget = long.MaxValue;

        /// <summary>The bytes the given states hold between them, for the budget; null for no budget.</summary>
        public Func<IEnumerable<T>, long> Size;

        public bool HasBase => _cursor >= 0;
        public T Current => _cursor >= 0 ? _states[_cursor].State : null;
        public bool CanUndo => _cursor > 0;
        public bool CanRedo => _cursor >= 0 && _cursor < _states.Count - 1;

        /// <summary>The step an undo takes back, or null.</summary>
        public string UndoLabel => CanUndo ? _states[_cursor].Label : null;

        /// <summary>The step a redo makes again, or null.</summary>
        public string RedoLabel => CanRedo ? _states[_cursor + 1].Label : null;

        /// <summary>Steps behind the cursor and ahead of it.</summary>
        public int UndoCount => Math.Max(0, _cursor);
        public int RedoCount => _cursor < 0 ? 0 : _states.Count - 1 - _cursor;

        public void Clear()
        {
            _states.Clear();
            _cursor = -1;
        }

        /// <summary>Starts the history at a state, with nothing to undo.</summary>
        public void Reset(T state)
        {
            Clear();
            _states.Add((null, state));
            _cursor = 0;
        }

        /// <summary>Replaces the current state without making a step, for a change nothing can undo.</summary>
        public void Rebase(T state)
        {
            if (_cursor < 0)
                Reset(state);
            else
                _states[_cursor] = (_states[_cursor].Label, state);
        }

        /// <summary>Adds a step ending in the given state.</summary>
        public void Push(string label, T state)
        {
            if (_cursor < 0)
            {
                Reset(state);
                return;
            }
            _states.RemoveRange(_cursor + 1, _states.Count - _cursor - 1);
            _states.Add((label, state));
            _cursor++;
            while (
                _states.Count > 2
                && (_states.Count - 1 > Limit || (Size != null && Size(StatesOnly()) > Budget))
            )
            {
                _states.RemoveAt(0);
                _cursor--;
            }
        }

        IEnumerable<T> StatesOnly()
        {
            foreach (var (_, state) in _states)
                yield return state;
        }

        public T Undo()
        {
            if (!CanUndo)
                return null;
            _cursor--;
            return _states[_cursor].State;
        }

        public T Redo()
        {
            if (!CanRedo)
                return null;
            _cursor++;
            return _states[_cursor].State;
        }
    }
}
