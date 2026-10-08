namespace PoAutoRobo.Core.Library;

/// <summary>Undo and redo over whole values: each edit remembers what was there before it.</summary>
/// <param name="kept">How many steps back are remembered; older ones are dropped.</param>
public sealed class EditHistory<T>(int kept = 50)
{
    private readonly List<T> _undo = [];
    private readonly List<T> _redo = [];
    private bool _lastMerges;

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    /// <summary>Records an edit that replaced <paramref name="before"/>. Nothing undone can be redone after it.</summary>
    /// <param name="merges">True for one tick of a continuous change, such as a slider drag: a run of them is undone as one.</param>
    public void Record(T before, bool merges = false)
    {
        if (!(merges && _lastMerges))
        {
            _undo.Add(before);
            if (_undo.Count > kept) _undo.RemoveAt(0);
        }
        _lastMerges = merges;
        _redo.Clear();
    }

    /// <summary>Steps back from <paramref name="current"/>; false when there is nothing to undo.</summary>
    public bool TryUndo(T current, out T previous) => Travel(_undo, _redo, current, out previous);

    public bool TryRedo(T current, out T next) => Travel(_redo, _undo, current, out next);

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
        _lastMerges = false;
    }

    private bool Travel(List<T> from, List<T> to, T current, out T target)
    {
        target = current;
        if (from.Count == 0) return false;
        target = from[^1];
        from.RemoveAt(from.Count - 1);
        to.Add(current);
        _lastMerges = false;
        return true;
    }
}
