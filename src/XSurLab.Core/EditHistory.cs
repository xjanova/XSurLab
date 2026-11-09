namespace XSurLab.Core;

/// <summary>
/// Undo and redo over whole-state snapshots. Each entry is the state after an edit and the label of that edit;
/// the first entry is the state the history started from and has no label.
/// </summary>
public sealed class EditHistory<T> where T : class
{
    private readonly List<(T State, string Label)> entries = [];
    private readonly IEqualityComparer<T> comparer;
    private int index;
    public int Limit { get; }
    public T Current => entries[index].State;
    public bool CanUndo => index > 0;
    public bool CanRedo => index < entries.Count - 1;
    /// <summary>Label of the edit Undo reverts.</summary>
    public string? UndoLabel => CanUndo ? entries[index].Label : null;
    /// <summary>Label of the edit Redo applies again.</summary>
    public string? RedoLabel => CanRedo ? entries[index + 1].Label : null;
    public int Count => entries.Count;

    public EditHistory(T initial, IEqualityComparer<T>? comparer = null, int limit = 200)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 2);
        this.comparer = comparer ?? EqualityComparer<T>.Default;
        Limit = limit;
        Reset(initial);
    }

    public void Reset(T state)
    {
        ArgumentNullException.ThrowIfNull(state);
        entries.Clear(); entries.Add((state, "")); index = 0;
    }

    /// <summary>Adds a state after the current one and drops the redo branch. A state equal to the current one is ignored.</summary>
    public bool Record(T state, string label)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (comparer.Equals(state, Current)) return false;
        entries.RemoveRange(index + 1, entries.Count - index - 1);
        entries.Add((state, label));
        // The oldest edit falls off; its state becomes the new starting point.
        if (entries.Count > Limit) { entries.RemoveAt(0); entries[0] = (entries[0].State, ""); }
        index = entries.Count - 1;
        return true;
    }

    public T Undo()
    {
        if (!CanUndo) throw new InvalidOperationException("Nothing to undo.");
        return entries[--index].State;
    }

    public T Redo()
    {
        if (!CanRedo) throw new InvalidOperationException("Nothing to redo.");
        return entries[++index].State;
    }
}
