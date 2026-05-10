namespace MediaTools.Presentation.Undo;

/// <summary>
/// Debounced undo stack for view-model state: merges rapid edits into one undo step, supports explicit groups for discrete actions.
/// </summary>
public sealed class UndoRedoHost<TSnapshot>
    where TSnapshot : class, IEquatable<TSnapshot>
{
    private const int MaxDepth = 40;

    private readonly List<TSnapshot> _undo = new();
    private readonly List<TSnapshot> _redo = new();
    private TSnapshot _committed;
    private readonly Func<TSnapshot> _capture;
    private readonly Action<TSnapshot> _restore;
    private readonly int _debounceMs;
    private CancellationTokenSource? _debounceCts;
    private readonly Action? _onHistoryChanged;
    private bool _suppress;

    public UndoRedoHost(
        Func<TSnapshot> capture,
        Action<TSnapshot> restore,
        TSnapshot initialCommitted,
        Action? onHistoryChanged = null,
        int debounceMs = 420)
    {
        _capture = capture;
        _restore = restore;
        _committed = initialCommitted;
        _onHistoryChanged = onHistoryChanged;
        _debounceMs = debounceMs;
    }

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    /// <summary>True while undo/redo restore or <see cref="PushUndoFrameAnd"/> mutator runs — skip scheduling new undo steps.</summary>
    public bool IsApplyingHistory => _suppress;

    public void NotifyEdit()
    {
        if (_suppress)
        {
            return;
        }

        _debounceCts?.Cancel();
        _debounceCts = new CancellationTokenSource();
        var token = _debounceCts.Token;
        var ms = _debounceMs;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(ms, token).ConfigureAwait(false);
                if (token.IsCancellationRequested)
                {
                    return;
                }

                var app = global::System.Windows.Application.Current;
                if (app is null)
                {
                    return;
                }

                await app.Dispatcher.InvokeAsync(CommitDebouncedEdit);
            }
            catch (OperationCanceledException)
            {
                // expected when user keeps typing
            }
        });
    }

    private void CommitDebouncedEdit()
    {
        if (_suppress)
        {
            return;
        }

        var now = _capture();
        if (now.Equals(_committed))
        {
            return;
        }

        PushUndo(_committed);
        _committed = now;
        _redo.Clear();
        _onHistoryChanged?.Invoke();
    }

    public void FlushPendingEdit()
    {
        _debounceCts?.Cancel();
        CommitDebouncedEdit();
    }

    /// <summary>Call before a batch of changes; pair with <see cref="EndUndoGroup"/>.</summary>
    public void BeginUndoGroup()
    {
        if (_suppress)
        {
            return;
        }

        _debounceCts?.Cancel();
        PushUndo(_capture());
        _redo.Clear();
        _onHistoryChanged?.Invoke();
    }

    /// <summary>Call after mutations complete for a group started with <see cref="BeginUndoGroup"/>.</summary>
    public void EndUndoGroup()
    {
        if (_suppress)
        {
            return;
        }

        _committed = _capture();
        _onHistoryChanged?.Invoke();
    }

    /// <summary>Discard the undo frame pushed by <see cref="BeginUndoGroup"/> if the operation failed.</summary>
    public void CancelUndoGroup()
    {
        if (_suppress)
        {
            return;
        }

        if (_undo.Count == 0)
        {
            return;
        }

        _undo.RemoveAt(_undo.Count - 1);
        _onHistoryChanged?.Invoke();
    }

    /// <summary>Single synchronous transaction: push current, run mutation, commit.</summary>
    public void PushUndoFrameAnd(Action mutator)
    {
        if (_suppress)
        {
            return;
        }

        _debounceCts?.Cancel();
        PushUndo(_capture());
        _redo.Clear();
        _suppress = true;
        try
        {
            mutator();
        }
        finally
        {
            _suppress = false;
        }

        _committed = _capture();
        _onHistoryChanged?.Invoke();
    }

    public bool TryUndo()
    {
        _debounceCts?.Cancel();
        var now = _capture();
        if (!now.Equals(_committed))
        {
            PushUndo(_committed);
            _committed = now;
        }

        if (!CanUndo)
        {
            return false;
        }

        var previous = PopUndo()!;
        PushRedo(_committed);
        _committed = previous;
        _suppress = true;
        try
        {
            _restore(previous);
        }
        finally
        {
            _suppress = false;
        }

        _committed = _capture();
        _onHistoryChanged?.Invoke();
        return true;
    }

    public bool TryRedo()
    {
        _debounceCts?.Cancel();
        var nowRedo = _capture();
        if (!nowRedo.Equals(_committed))
        {
            PushUndo(_committed);
            _committed = nowRedo;
            _redo.Clear();
        }

        if (!CanRedo)
        {
            return false;
        }

        var next = PopRedo()!;
        PushUndo(_committed);
        _committed = next;
        _suppress = true;
        try
        {
            _restore(next);
        }
        finally
        {
            _suppress = false;
        }

        _committed = _capture();
        _onHistoryChanged?.Invoke();
        return true;
    }

    public void ResetHistory(TSnapshot freshCommitted)
    {
        _debounceCts?.Cancel();
        _undo.Clear();
        _redo.Clear();
        _committed = freshCommitted;
        _onHistoryChanged?.Invoke();
    }

    private void PushUndo(TSnapshot item)
    {
        _undo.Add(item);
        while (_undo.Count > MaxDepth)
        {
            _undo.RemoveAt(0);
        }
    }

    private TSnapshot? PopUndo()
    {
        if (_undo.Count == 0)
        {
            return null;
        }

        var i = _undo.Count - 1;
        var v = _undo[i];
        _undo.RemoveAt(i);
        return v;
    }

    private void PushRedo(TSnapshot item)
    {
        _redo.Add(item);
        while (_redo.Count > MaxDepth)
        {
            _redo.RemoveAt(0);
        }
    }

    private TSnapshot? PopRedo()
    {
        if (_redo.Count == 0)
        {
            return null;
        }

        var i = _redo.Count - 1;
        var v = _redo[i];
        _redo.RemoveAt(i);
        return v;
    }
}
