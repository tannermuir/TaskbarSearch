namespace TaskbarInstantSearch;

internal sealed class WindowTargetTracker
{
    private readonly Func<IntPtr> _overlayHandleProvider;
    private readonly List<IntPtr> _mruWindows = new();

    public WindowTargetTracker(Func<IntPtr> overlayHandleProvider)
    {
        _overlayHandleProvider = overlayHandleProvider;
    }

    public void ObserveForegroundWindow()
    {
        NoteWindow(NativeMethods.GetForegroundWindow());
    }

    public void CapturePreOverlayTarget()
    {
        NoteWindow(NativeMethods.GetForegroundWindow());
    }

    public void NoteActivatedWindow(IntPtr window)
    {
        NoteWindow(window);
    }

    public IntPtr ResolveCurrentTarget()
    {
        RemoveInvalidTargets();
        return _mruWindows.Count == 0 ? IntPtr.Zero : _mruWindows[0];
    }

    public void ConsumeTarget(IntPtr window)
    {
        if (window == IntPtr.Zero)
        {
            return;
        }

        _mruWindows.RemoveAll(candidate => candidate == window);
        RemoveInvalidTargets();
    }

    public void RemoveInvalidTargets()
    {
        _mruWindows.RemoveAll(window => !IsUsableTargetWindow(window));
    }

    public bool IsUsableTarget(IntPtr window)
    {
        return IsUsableTargetWindow(window);
    }

    private void NoteWindow(IntPtr window)
    {
        if (!IsUsableTargetWindow(window))
        {
            return;
        }

        _mruWindows.RemoveAll(candidate => candidate == window);
        _mruWindows.Insert(0, window);
        if (_mruWindows.Count > 32)
        {
            _mruWindows.RemoveRange(32, _mruWindows.Count - 32);
        }
    }

    private bool IsUsableTargetWindow(IntPtr window)
    {
        if (window == IntPtr.Zero ||
            window == _overlayHandleProvider() ||
            !NativeMethods.IsWindow(window) ||
            !NativeMethods.IsWindowVisible(window) ||
            NativeMethods.IsIconic(window))
        {
            return false;
        }

        NativeMethods.GetWindowThreadProcessId(window, out uint processId);
        if (processId == Environment.ProcessId)
        {
            return false;
        }

        string className = GetWindowClassName(window);
        return !string.Equals(className, "Shell_TrayWnd", StringComparison.Ordinal) &&
               !string.Equals(className, "Shell_SecondaryTrayWnd", StringComparison.Ordinal) &&
               !string.Equals(className, "Progman", StringComparison.Ordinal) &&
               !string.Equals(className, "WorkerW", StringComparison.Ordinal);
    }

    private static string GetWindowClassName(IntPtr window)
    {
        char[] className = new char[256];
        int length = NativeMethods.GetClassNameW(window, className, className.Length);
        return length <= 0 ? "" : new string(className, 0, length);
    }
}

internal enum ActionInvocationKind
{
    TypedExact,
    TabCompleted,
    Confirmed
}

internal sealed class ActionExecutionResult
{
    public bool Success { get; init; }
    public string Error { get; init; } = "";
    public IntPtr ActivatedWindow { get; init; }
    public IntPtr ConsumedWindow { get; init; }
    public bool IsCommandAction { get; init; }
}
