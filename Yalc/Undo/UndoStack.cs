using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace YetAnotherLosslessCutter.Undo;

/// <summary>
/// A reversible user action. Concrete implementations capture before/after state
/// of a single segment mutation. Undo and Redo must be each other's inverse and
/// must not push further actions onto the stack.
/// </summary>
public interface IUndoAction
{
    string Description { get; }
    void Undo();
    void Redo();
}

/// <summary>
/// In-memory undo / redo stack scoped to the current file. Cleared on file change.
/// Capacity-bounded so a long editing session doesn't grow without limit.
/// </summary>
public sealed class UndoStack
{
    private readonly LinkedList<IUndoAction> _undo = new();
    private readonly Stack<IUndoAction> _redo = new();
    private const int Capacity = 200;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    /// <summary>Fires when CanUndo / CanRedo may have changed. UI hooks for menu/button enable state.</summary>
    public event Action? Changed;

    public void Push(IUndoAction action)
    {
        _undo.AddLast(action);
        while (_undo.Count > Capacity) _undo.RemoveFirst();
        _redo.Clear();
        Changed?.Invoke();
    }

    public bool Undo()
    {
        if (_undo.Count == 0) return false;
        var a = _undo.Last!.Value;
        _undo.RemoveLast();
        a.Undo();
        _redo.Push(a);
        Changed?.Invoke();
        return true;
    }

    public bool Redo()
    {
        if (_redo.Count == 0) return false;
        var a = _redo.Pop();
        a.Redo();
        _undo.AddLast(a);
        Changed?.Invoke();
        return true;
    }

    public void Clear()
    {
        if (_undo.Count == 0 && _redo.Count == 0) return;
        _undo.Clear();
        _redo.Clear();
        Changed?.Invoke();
    }
}

public sealed class AddSegmentAction(
    ObservableCollection<VideoSegment> list, VideoSegment seg, int index) : IUndoAction
{
    public string Description => "add segment";
    public void Undo() => list.Remove(seg);
    public void Redo() => list.Insert(Math.Min(index, list.Count), seg);
}

public sealed class RemoveSegmentAction(
    ObservableCollection<VideoSegment> list, VideoSegment seg, int index) : IUndoAction
{
    public string Description => "delete segment";
    public void Undo()
    {
        seg.MarkedForDeletion = false;
        list.Insert(Math.Min(index, list.Count), seg);
    }
    public void Redo()
    {
        seg.MarkedForDeletion = true;
        list.Remove(seg);
    }
}

public sealed class ChangeSegmentTimesAction(
    VideoSegment seg, double oldFrom, double oldTo, double newFrom, double newTo) : IUndoAction
{
    public string Description => "edit segment";
    public void Undo() => seg.SetCutTimes(TimeSpan.FromSeconds(oldFrom), TimeSpan.FromSeconds(oldTo));
    public void Redo() => seg.SetCutTimes(TimeSpan.FromSeconds(newFrom), TimeSpan.FromSeconds(newTo));
}

public sealed class ClearAllSegmentsAction(
    ObservableCollection<VideoSegment> list, VideoSegment[] snapshot) : IUndoAction
{
    public string Description => "clear all segments";
    public void Undo()
    {
        foreach (var s in snapshot)
        {
            s.MarkedForDeletion = false;
            list.Add(s);
        }
    }
    public void Redo()
    {
        foreach (var s in list) s.MarkedForDeletion = true;
        list.Clear();
    }
}

/// <summary>
/// Wraps multiple actions as a single undo step. Bulk operations (silence
/// detection, scene-cut detection, batch ops) push one of these so Ctrl+Z reverses
/// the whole batch in one keystroke instead of N+1 keystrokes. Children are undone
/// in reverse order and redone in forward order.
/// </summary>
public sealed class CompositeAction(string description, params IUndoAction[] actions) : IUndoAction
{
    public string Description => description;
    public void Undo()
    {
        for (var i = actions.Length - 1; i >= 0; i--) actions[i].Undo();
    }
    public void Redo()
    {
        for (var i = 0; i < actions.Length; i++) actions[i].Redo();
    }
}
