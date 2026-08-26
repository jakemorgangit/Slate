namespace Slate.Services.Planning;

public enum ToastLevel { Info, Success, Warning, Error }

public sealed record Toast(Guid Id, ToastLevel Level, string Title, string? Detail);

/// <summary>Transient, non-blocking notifications rendered in the corner of the window.</summary>
public sealed class ToastService
{
    private readonly Lock _gate = new();
    private readonly List<Toast> _toasts = [];

    /// <summary>
    /// A snapshot rather than the list itself. Auto-dismissal runs on a pool thread, and
    /// the renderer must never be walking the same list something else is removing from.
    /// </summary>
    public IReadOnlyList<Toast> Toasts
    {
        get { lock (_gate) return [.. _toasts]; }
    }

    public event Action? Changed;

    public void Info(string title, string? detail = null) => Push(ToastLevel.Info, title, detail);
    public void Success(string title, string? detail = null) => Push(ToastLevel.Success, title, detail);
    public void Warning(string title, string? detail = null) => Push(ToastLevel.Warning, title, detail);
    public void Error(string title, string? detail = null) => Push(ToastLevel.Error, title, detail);

    private void Push(ToastLevel level, string title, string? detail)
    {
        var toast = new Toast(Guid.NewGuid(), level, title, detail);
        lock (_gate) _toasts.Add(toast);
        Changed?.Invoke();

        // Errors stay until dismissed; everything else clears itself.
        if (level == ToastLevel.Error) return;

        _ = Task.Delay(TimeSpan.FromSeconds(level == ToastLevel.Warning ? 8 : 4))
            .ContinueWith(_ => Dismiss(toast.Id), TaskScheduler.Default);
    }

    public void Dismiss(Guid id)
    {
        bool removed;
        lock (_gate) removed = _toasts.RemoveAll(t => t.Id == id) > 0;

        if (removed) Changed?.Invoke();
    }

    public void Clear()
    {
        lock (_gate)
        {
            if (_toasts.Count == 0) return;
            _toasts.Clear();
        }

        Changed?.Invoke();
    }
}
