using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using YetAnotherLosslessCutter;
using YetAnotherLosslessCutter.Undo;
using Xunit;

namespace YetAnotherLosslessCutter.Tests;

/// <summary>
/// Tests for the in-memory undo / redo stack and the four concrete actions that
/// back segment mutations. Locks in the invariants every new feature will rely on
/// (push clears redo, capacity caps the stack, drag-end produces one entry per
/// drag, etc.).
/// </summary>
public class UndoStackTests
{
    private static VideoSegment MakeSeg(double from = 0, double to = 10, double max = 60)
    {
        var s = new VideoSegment { MaxDuration = TimeSpan.FromSeconds(max) };
        s.SetCutTimes(TimeSpan.FromSeconds(from), TimeSpan.FromSeconds(to));
        return s;
    }

    private sealed class CountingAction : IUndoAction
    {
        public int UndoCount, RedoCount;
        public string Description => "test";
        public void Undo() => UndoCount++;
        public void Redo() => RedoCount++;
    }

    // ---- UndoStack basics ----

    [Fact]
    public void NewStack_HasNoUndoOrRedo()
    {
        var s = new UndoStack();
        Assert.False(s.CanUndo);
        Assert.False(s.CanRedo);
        Assert.False(s.Undo()); // safe no-op
        Assert.False(s.Redo()); // safe no-op
    }

    [Fact]
    public void Push_EnablesUndo_AndFiresChanged()
    {
        var s = new UndoStack();
        var changed = 0;
        s.Changed += () => changed++;
        s.Push(new CountingAction());
        Assert.True(s.CanUndo);
        Assert.False(s.CanRedo);
        Assert.Equal(1, changed);
    }

    [Fact]
    public void Undo_InvokesAction_AndMovesToRedo()
    {
        var s = new UndoStack();
        var a = new CountingAction();
        s.Push(a);
        Assert.True(s.Undo());
        Assert.Equal(1, a.UndoCount);
        Assert.False(s.CanUndo);
        Assert.True(s.CanRedo);
    }

    [Fact]
    public void Redo_InvokesAction_AndMovesBackToUndo()
    {
        var s = new UndoStack();
        var a = new CountingAction();
        s.Push(a);
        s.Undo();
        Assert.True(s.Redo());
        Assert.Equal(1, a.RedoCount);
        Assert.True(s.CanUndo);
        Assert.False(s.CanRedo);
    }

    [Fact]
    public void Push_ClearsRedoStack()
    {
        var s = new UndoStack();
        var a = new CountingAction();
        var b = new CountingAction();
        s.Push(a);
        s.Undo();
        Assert.True(s.CanRedo);
        s.Push(b);
        Assert.False(s.CanRedo); // pushing a new action invalidates redo
    }

    [Fact]
    public void Capacity_EvictsOldestActions()
    {
        var s = new UndoStack();
        // Push 250 actions; only the most recent 200 should be retained.
        var first = new CountingAction();
        s.Push(first);
        for (var i = 0; i < 249; i++) s.Push(new CountingAction());
        // Drain the entire stack — the very first action should not appear in undo
        // results because it was evicted.
        while (s.Undo()) { }
        Assert.Equal(0, first.UndoCount);
    }

    [Fact]
    public void Clear_EmptiesBothStacks_AndFiresChanged()
    {
        var s = new UndoStack();
        s.Push(new CountingAction());
        s.Push(new CountingAction());
        s.Undo();
        var changed = 0;
        s.Changed += () => changed++;
        s.Clear();
        Assert.False(s.CanUndo);
        Assert.False(s.CanRedo);
        Assert.Equal(1, changed);
    }

    [Fact]
    public void Clear_OnEmptyStack_DoesNotFireChanged()
    {
        var s = new UndoStack();
        var changed = 0;
        s.Changed += () => changed++;
        s.Clear();
        Assert.Equal(0, changed);
    }

    // ---- AddSegmentAction ----

    [Fact]
    public void AddSegmentAction_UndoRemoves_RedoReinserts()
    {
        var list = new ObservableCollection<VideoSegment>();
        var seg = MakeSeg();
        list.Add(seg);
        var action = new AddSegmentAction(list, seg, 0);
        action.Undo();
        Assert.Empty(list);
        action.Redo();
        Assert.Single(list);
        Assert.Same(seg, list[0]);
    }

    [Fact]
    public void AddSegmentAction_RedoClampsIndexToCount()
    {
        // If the stored index is now beyond the (shrunk) list, redo should still
        // append rather than throw — defensive against external list mutations.
        var list = new ObservableCollection<VideoSegment>();
        var seg = MakeSeg();
        var action = new AddSegmentAction(list, seg, 5);
        action.Redo();
        Assert.Single(list);
        Assert.Same(seg, list[0]);
    }

    // ---- RemoveSegmentAction ----

    [Fact]
    public void RemoveSegmentAction_UndoRestoresAtIndex_AndUnmarks()
    {
        var list = new ObservableCollection<VideoSegment>
        {
            MakeSeg(0, 5),
            MakeSeg(10, 20),
            MakeSeg(30, 40),
        };
        var middle = list[1];
        middle.MarkedForDeletion = true;
        list.Remove(middle);
        var action = new RemoveSegmentAction(list, middle, 1);
        action.Undo();
        Assert.Equal(3, list.Count);
        Assert.Same(middle, list[1]);
        Assert.False(middle.MarkedForDeletion);
    }

    [Fact]
    public void RemoveSegmentAction_RedoMarksAndRemoves()
    {
        var list = new ObservableCollection<VideoSegment> { MakeSeg() };
        var seg = list[0];
        var action = new RemoveSegmentAction(list, seg, 0);
        action.Redo();
        Assert.Empty(list);
        Assert.True(seg.MarkedForDeletion);
    }

    // ---- ChangeSegmentTimesAction ----

    [Fact]
    public void ChangeSegmentTimesAction_RoundTrip()
    {
        var seg = MakeSeg(10, 20);
        var action = new ChangeSegmentTimesAction(seg, 10, 20, 5, 25);
        action.Redo();
        Assert.Equal(5.0, seg.CutFromSeconds, 6);
        Assert.Equal(25.0, seg.CutToSeconds, 6);
        action.Undo();
        Assert.Equal(10.0, seg.CutFromSeconds, 6);
        Assert.Equal(20.0, seg.CutToSeconds, 6);
    }

    [Fact]
    public void ChangeSegmentTimesAction_TranslatePastCurrentRange_RestoresExactly()
    {
        // The reason SetCutTimes exists: setting from/to via individual property
        // setters would clamp one bound by the *current* other bound, pinning the
        // segment partway through the move. The undo/redo path must survive a
        // full translation past the current range.
        var seg = MakeSeg(10, 20);
        var action = new ChangeSegmentTimesAction(seg, 10, 20, 50, 60);
        action.Redo();
        Assert.Equal(50.0, seg.CutFromSeconds, 6);
        Assert.Equal(60.0, seg.CutToSeconds, 6);
        action.Undo();
        Assert.Equal(10.0, seg.CutFromSeconds, 6);
        Assert.Equal(20.0, seg.CutToSeconds, 6);
    }

    // ---- ClearAllSegmentsAction ----

    [Fact]
    public void ClearAllSegmentsAction_UndoRestoresInOrder_AndUnmarks()
    {
        var a = MakeSeg(0, 5);
        var b = MakeSeg(10, 20);
        var c = MakeSeg(30, 40);
        var list = new ObservableCollection<VideoSegment>();
        var snapshot = new[] { a, b, c };

        // Simulate the user-clear-all path: mark each for deletion, clear the list,
        // then push the action.
        foreach (var s in snapshot) s.MarkedForDeletion = true;

        var action = new ClearAllSegmentsAction(list, snapshot);
        action.Undo();
        Assert.Equal(3, list.Count);
        Assert.Same(a, list[0]);
        Assert.Same(b, list[1]);
        Assert.Same(c, list[2]);
        Assert.False(a.MarkedForDeletion);
        Assert.False(b.MarkedForDeletion);
        Assert.False(c.MarkedForDeletion);
    }

    [Fact]
    public void ClearAllSegmentsAction_RedoClearsAndMarks()
    {
        var list = new ObservableCollection<VideoSegment>
        {
            MakeSeg(0, 5),
            MakeSeg(10, 20),
        };
        var snapshot = new[] { list[0], list[1] };
        var action = new ClearAllSegmentsAction(list, snapshot);
        action.Redo();
        Assert.Empty(list);
        Assert.True(snapshot[0].MarkedForDeletion);
        Assert.True(snapshot[1].MarkedForDeletion);
    }

    [Fact]
    public void ClearAllSegmentsAction_RoundTripPreservesOrder()
    {
        var a = MakeSeg(0, 5);
        var b = MakeSeg(10, 20);
        var list = new ObservableCollection<VideoSegment> { a, b };
        var snapshot = new[] { a, b };
        var action = new ClearAllSegmentsAction(list, snapshot);
        action.Redo();
        action.Undo();
        Assert.Equal(2, list.Count);
        Assert.Same(a, list[0]);
        Assert.Same(b, list[1]);
    }

    // ---- CompositeAction ----

    [Fact]
    public void CompositeAction_UndoesInReverseOrder_RedoesInForwardOrder()
    {
        var order = new List<string>();
        var a = new RecordingAction("A", order);
        var b = new RecordingAction("B", order);
        var c = new RecordingAction("C", order);
        var composite = new CompositeAction("batch", a, b, c);

        composite.Redo();
        Assert.Equal(new[] { "redo:A", "redo:B", "redo:C" }, order);

        order.Clear();
        composite.Undo();
        Assert.Equal(new[] { "undo:C", "undo:B", "undo:A" }, order);
    }

    [Fact]
    public void CompositeAction_AsSingleStackEntry_OneCtrlZUndoesAll()
    {
        var list = new ObservableCollection<VideoSegment>();
        var stack = new UndoStack();
        var s1 = MakeSeg(0, 5);
        var s2 = MakeSeg(10, 15);
        list.Add(s1);
        list.Add(s2);

        // Push a composite that "added both segments" — undoing should remove both.
        stack.Push(new CompositeAction("batch add",
            new AddSegmentAction(list, s1, 0),
            new AddSegmentAction(list, s2, 1)));

        stack.Undo();
        Assert.Empty(list);

        stack.Redo();
        Assert.Equal(2, list.Count);
        Assert.Same(s1, list[0]);
        Assert.Same(s2, list[1]);
    }

    private sealed class RecordingAction(string label, List<string> log) : IUndoAction
    {
        public string Description => label;
        public void Undo() => log.Add($"undo:{label}");
        public void Redo() => log.Add($"redo:{label}");
    }

    // ---- Integration: drive UndoStack with real actions ----

    [Fact]
    public void DriveStack_AddThenChange_UndoesInReverseOrder()
    {
        var list = new ObservableCollection<VideoSegment>();
        var stack = new UndoStack();

        var seg = MakeSeg(0, 10, max: 100);
        list.Add(seg);
        stack.Push(new AddSegmentAction(list, seg, 0));

        var oldFrom = seg.CutFromSeconds;
        var oldTo = seg.CutToSeconds;
        seg.SetCutTimes(TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(40));
        stack.Push(new ChangeSegmentTimesAction(seg, oldFrom, oldTo,
            seg.CutFromSeconds, seg.CutToSeconds));

        // First undo reverts the time change.
        stack.Undo();
        Assert.Single(list);
        Assert.Equal(0.0, seg.CutFromSeconds, 6);
        Assert.Equal(10.0, seg.CutToSeconds, 6);

        // Second undo removes the segment.
        stack.Undo();
        Assert.Empty(list);

        // Redo replays in original order.
        stack.Redo();
        Assert.Single(list);
        Assert.Equal(0.0, seg.CutFromSeconds, 6);
        stack.Redo();
        Assert.Equal(20.0, seg.CutFromSeconds, 6);
        Assert.Equal(40.0, seg.CutToSeconds, 6);
    }
}
