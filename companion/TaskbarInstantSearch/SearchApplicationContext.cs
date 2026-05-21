using System.Drawing;
using System.Windows.Forms;

namespace TaskbarInstantSearch;

internal sealed class SearchApplicationContext : ApplicationContext
{
    private readonly SearchOverlayForm _overlay;
    private readonly ActionExecutor _actionExecutor;
    private readonly AppConfig _config;
    private readonly CerebrasAiPromptService _aiPromptService;
    private readonly IMathRenderer _mathRenderer;
    private readonly WindowTargetTracker _windowTargetTracker;
    private readonly HotkeyManager _hotkeyManager;
    private readonly GlobalEscapeListener _globalEscapeListener;
    private readonly PipeServer _pipeServer;
    private readonly System.Windows.Forms.Timer _foregroundWindowTimer;
    private readonly List<PendingWindowResolve> _pendingWindowResolves = new();
    private CancellationTokenSource? _aiRequestCts;

    public SearchApplicationContext(AppConfig config)
    {
        _config = config;
        _overlay = new SearchOverlayForm(config.Overlay);
        _aiPromptService = new CerebrasAiPromptService(config.Ai);
        _mathRenderer = new NullMathRenderer();
        _windowTargetTracker = new WindowTargetTracker(() => _overlay.Handle);
        _actionExecutor = new ActionExecutor(
            config,
            _windowTargetTracker,
            QueueDelayedWindowResolve,
            ResolvePendingLaunchWindow);
        _overlay.SetActionTriggers(config.AutocompleteTriggers);
        _overlay.SetConfirmationInputs(config.ConfirmationInputTriggers);
        _overlay.SetAiThinkingSuffix(config.Ai.ThinkingSuffix);
        _ = _overlay.Handle;
        _globalEscapeListener = new GlobalEscapeListener(CloseOverlayFromGlobalEscape);
        _overlay.ActionRequested += OnActionRequested;
        _overlay.AiPromptRequested += OnAiPromptRequested;
        _overlay.AiPromptCanceled += OnAiPromptCanceled;
        _overlay.OverlayClosed += (_, _) => _globalEscapeListener.Disable();
        _hotkeyManager = new HotkeyManager(config.Hotkey, ToggleOverlay);
        _pipeServer = new PipeServer(HandlePipeMessage);
        _foregroundWindowTimer = new System.Windows.Forms.Timer { Interval = 250 };
        _foregroundWindowTimer.Tick += (_, _) => RememberForegroundWindow();
        _foregroundWindowTimer.Start();
    }

    public void ToggleOverlay()
    {
        if (_overlay.InvokeRequired)
        {
            _overlay.BeginInvoke(new MethodInvoker(ToggleOverlay));
            return;
        }

        if (_overlay.Visible)
        {
            Logger.Info("ToggleOverlay hiding visible overlay");
            _overlay.HideAndClear();
        }
        else
        {
            Logger.Info("ToggleOverlay showing overlay");
            _windowTargetTracker.CapturePreOverlayTarget();
            _overlay.ShowForInput();
            _globalEscapeListener.Enable();
        }
    }

    private void CloseOverlayFromGlobalEscape()
    {
        if (_overlay.IsDisposed || !_overlay.Visible)
        {
            return;
        }

        _overlay.BeginInvoke(new MethodInvoker(() =>
        {
            if (_overlay.Visible)
            {
                Logger.Info("Global Escape listener closing overlay");
                _overlay.HideAndClear();
            }
        }));
    }

    private void HandlePipeMessage(string type)
    {
        if (type.Equals("toggle", StringComparison.OrdinalIgnoreCase))
        {
            ToggleOverlay();
        }
        else if (type.Equals("open", StringComparison.OrdinalIgnoreCase))
        {
            _overlay.BeginInvoke(new MethodInvoker(() =>
            {
                _windowTargetTracker.CapturePreOverlayTarget();
                _overlay.ShowForInput();
                _globalEscapeListener.Enable();
            }));
        }
        else if (type.Equals("close", StringComparison.OrdinalIgnoreCase))
        {
            _overlay.BeginInvoke(new MethodInvoker(_overlay.HideAndClear));
        }
    }

    private void OnActionRequested(object? sender, ActionRequestedEventArgs e)
    {
        CancelAiRequest();
        if (_actionExecutor.RequiresConfirmation(e.Input) &&
            e.InvocationKind != ActionInvocationKind.Confirmed)
        {
            return;
        }

        if (!_actionExecutor.HasAction(e.Input))
        {
            return;
        }

        bool selfRestartAction = _actionExecutor.IsSelfRestartAction(e.Input);
        bool closeAfterExecution =
            e.InvocationKind == ActionInvocationKind.TabCompleted ||
            e.InvocationKind == ActionInvocationKind.Confirmed ||
            string.Equals(GetActionBehavior(e.Input, e.InvocationKind), "close", StringComparison.OrdinalIgnoreCase) ||
            _actionExecutor.RequiresCloseAfterExecution(e.Input);

        if (closeAfterExecution && !selfRestartAction)
        {
            _overlay.CloseAndReset();
        }
        else if (!closeAfterExecution)
        {
            _overlay.ClearAndStayOpen();
        }

        ActionExecutionResult result = _actionExecutor.Execute(e.Input);
        if (result.Success)
        {
            if (result.ActivatedWindow != IntPtr.Zero)
            {
                _windowTargetTracker.NoteActivatedWindow(result.ActivatedWindow);
            }

            if (result.ConsumedWindow != IntPtr.Zero)
            {
                _windowTargetTracker.ConsumeTarget(result.ConsumedWindow);
            }

            if (closeAfterExecution)
            {
                _overlay.CloseAndReset();
            }

            if (result.IsCommandAction)
            {
                if (!closeAfterExecution)
                {
                    _overlay.RestoreInputForKeepOpenAction(TimeSpan.FromMilliseconds(3200));
                }

                _overlay.BeginInvoke(new MethodInvoker(() =>
                {
                    _hotkeyManager.ReRegister();
                    Logger.Info($"Post-command hotkey refresh after '{e.Input}'");
                }));
            }
        }
        else if (!string.IsNullOrEmpty(result.Error))
        {
            Logger.Error($"Action failed for '{e.Input}': {result.Error}");
        }
    }

    private async void OnAiPromptRequested(object? sender, AiPromptRequestedEventArgs e)
    {
        CancelAiRequest();
        if (ArithmeticEvaluator.TryEvaluate(e.Prompt, out string arithmeticAnswer))
        {
            try
            {
                ClipboardHelper.SetText(arithmeticAnswer);
            }
            catch (Exception exception)
            {
                Logger.Error("Failed to copy arithmetic response to clipboard", exception);
            }

            _overlay.SetAiResult($"{e.Prompt} = {arithmeticAnswer}", null, null);
            return;
        }

        var cancellationTokenSource = new CancellationTokenSource();
        _aiRequestCts = cancellationTokenSource;
        CancellationToken cancellationToken = cancellationTokenSource.Token;

        AiPromptResult result = await _aiPromptService.PromptAsync(e.Prompt, cancellationToken);
        if (cancellationToken.IsCancellationRequested || result.FailureKind == AiPromptFailureKind.Canceled)
        {
            return;
        }

        Bitmap? renderedBitmap = null;
        string? renderSource = null;
        if (result.Success)
        {
            string answer = result.Text.Trim();
            if (_config.Math.Enabled &&
                _config.Math.RenderAiLatexResponses &&
                string.Equals(_config.Math.Notation, "latex", StringComparison.OrdinalIgnoreCase) &&
                LatexDetector.TryNormalizeLatex(answer, out string normalizedLatex) &&
                _mathRenderer.CanRender(normalizedLatex))
            {
                renderSource = normalizedLatex;
                renderedBitmap = await _mathRenderer.RenderLatexAsync(
                    normalizedLatex,
                    new MathRenderOptions
                    {
                        MaxWidth = _config.Math.MaxRenderWidth,
                        MaxHeight = _config.Math.MaxRenderHeight
                    },
                    cancellationToken);
            }
        }

        if (cancellationToken.IsCancellationRequested)
        {
            renderedBitmap?.Dispose();
            return;
        }

        if (_overlay.IsDisposed)
        {
            renderedBitmap?.Dispose();
            return;
        }

        _overlay.BeginInvoke(new MethodInvoker(() =>
        {
            if (cancellationToken.IsCancellationRequested)
            {
                renderedBitmap?.Dispose();
                return;
            }

            if (result.Success)
            {
                string answer = result.Text.Trim();
                try
                {
                    ClipboardHelper.SetText(answer);
                }
                catch (Exception exception)
                {
                    Logger.Error("Failed to copy AI response to clipboard", exception);
                }

                if (renderSource == null &&
                    _config.Math.Enabled &&
                    _config.Math.RenderAiLatexResponses &&
                    LatexDetector.TryNormalizeLatex(answer, out string normalizedLatex))
                {
                    renderSource = normalizedLatex;
                }

                _overlay.SetAiResult(answer, renderSource, renderedBitmap);
            }
            else
            {
                renderedBitmap?.Dispose();
                _overlay.SetAiFailure(GetAiFailureSuffix(result.FailureKind));
            }
        }));
    }

    private void OnAiPromptCanceled(object? sender, EventArgs e)
    {
        CancelAiRequest();
    }

    private string GetAiFailureSuffix(AiPromptFailureKind failureKind)
    {
        return failureKind switch
        {
            AiPromptFailureKind.Canceled => "",
            AiPromptFailureKind.MissingKey => _config.Ai.MissingKeySuffix,
            AiPromptFailureKind.RateLimited => _config.Ai.RateLimitSuffix,
            _ => _config.Ai.ErrorSuffix
        };
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            CancelAiRequest();
            _pipeServer.Dispose();
            _hotkeyManager.Dispose();
            _globalEscapeListener.Dispose();
            _overlay.Dispose();
            _foregroundWindowTimer.Dispose();
        }

        base.Dispose(disposing);
    }

    private void CancelAiRequest()
    {
        CancellationTokenSource? cancellationTokenSource = _aiRequestCts;
        _aiRequestCts = null;
        if (cancellationTokenSource == null)
        {
            return;
        }

        try
        {
            cancellationTokenSource.Cancel();
        }
        finally
        {
            cancellationTokenSource.Dispose();
        }
    }

    private void RememberForegroundWindow()
    {
        if (_overlay.Visible)
        {
            return;
        }

        _windowTargetTracker.ObserveForegroundWindow();
    }

    private void QueueDelayedWindowResolve(ActionConfig action)
    {
        if (_overlay.IsDisposed)
        {
            return;
        }

        var pending = new PendingWindowResolve(action);
        _pendingWindowResolves.Insert(0, pending);
        RemoveExpiredPendingWindowResolves();
        string trigger = action.Trigger ?? "";
        int[] delays = { 100, 250, 500, 900, 1400, 2200, 3200 };
        foreach (int delay in delays)
        {
            var timer = new System.Windows.Forms.Timer { Interval = delay };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                timer.Dispose();
                if (pending.Completed)
                {
                    return;
                }

                IntPtr window = FindActionWindow(action);
                if (window == IntPtr.Zero)
                {
                    return;
                }

                pending.Completed = true;
                _pendingWindowResolves.Remove(pending);
                _windowTargetTracker.NoteActivatedWindow(window);
                Logger.Info($"Delayed window target resolved for '{trigger}'");
            };
            timer.Start();
        }
    }

    private IntPtr ResolvePendingLaunchWindow()
    {
        RemoveExpiredPendingWindowResolves();
        foreach (PendingWindowResolve pending in _pendingWindowResolves.ToList())
        {
            if (pending.Completed)
            {
                _pendingWindowResolves.Remove(pending);
                continue;
            }

            IntPtr window = FindActionWindow(pending.Action);
            if (window == IntPtr.Zero)
            {
                continue;
            }

            pending.Completed = true;
            _pendingWindowResolves.Remove(pending);
            _windowTargetTracker.NoteActivatedWindow(window);
            Logger.Info($"Pending window target resolved for '{pending.Action.Trigger}'");
            return window;
        }

        return IntPtr.Zero;
    }

    private void RemoveExpiredPendingWindowResolves()
    {
        DateTime now = DateTime.UtcNow;
        _pendingWindowResolves.RemoveAll(pending => pending.Completed || pending.ExpiresUtc <= now);
    }

    private IntPtr FindActionWindow(ActionConfig action)
    {
        HashSet<string> classNames = (action.ReuseWindowClasses ?? new List<string>())
            .Where(className => !string.IsNullOrWhiteSpace(className))
            .ToHashSet(StringComparer.Ordinal);

        List<string> processNames = GetActionProcessNames(action);
        IntPtr matchedWindow = IntPtr.Zero;
        NativeMethods.EnumWindows((window, _) =>
        {
            if (!_windowTargetTracker.IsUsableTarget(window))
            {
                return true;
            }

            string className = GetWindowClassName(window);
            if (classNames.Count > 0 && classNames.Contains(className))
            {
                matchedWindow = window;
                return false;
            }

            if (processNames.Count > 0)
            {
                NativeMethods.GetWindowThreadProcessId(window, out uint processId);
                if (ProcessNameMatches(processId, processNames))
                {
                    matchedWindow = window;
                    return false;
                }
            }

            return true;
        }, IntPtr.Zero);

        return matchedWindow;
    }

    private static List<string> GetActionProcessNames(ActionConfig action)
    {
        if (action.ReuseProcessNames is { Count: > 0 })
        {
            return action.ReuseProcessNames
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(Path.GetFileNameWithoutExtension)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList()!;
        }

        if (!string.IsNullOrWhiteSpace(action.Command))
        {
            string processName = Path.GetFileNameWithoutExtension(action.Command);
            if (!string.IsNullOrWhiteSpace(processName))
            {
                return new List<string> { processName };
            }
        }

        return new List<string>();
    }

    private static bool ProcessNameMatches(uint processId, List<string> processNames)
    {
        if (processId == 0)
        {
            return false;
        }

        try
        {
            using System.Diagnostics.Process process = System.Diagnostics.Process.GetProcessById((int)processId);
            return processNames.Contains(process.ProcessName, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string GetWindowClassName(IntPtr window)
    {
        char[] className = new char[256];
        int length = NativeMethods.GetClassNameW(window, className, className.Length);
        return length <= 0 ? "" : new string(className, 0, length);
    }

    private sealed class PendingWindowResolve
    {
        public PendingWindowResolve(ActionConfig action)
        {
            Action = action;
            ExpiresUtc = DateTime.UtcNow.AddSeconds(5);
        }

        public ActionConfig Action { get; }
        public DateTime ExpiresUtc { get; }
        public bool Completed { get; set; }
    }

    private string GetActionBehavior(string input, ActionInvocationKind invocationKind)
    {
        if (!_config.ValidActions.TryGetValue(input, out ActionConfig? action))
        {
            return invocationKind == ActionInvocationKind.TabCompleted ? "close" : "keepOpen";
        }

        return invocationKind == ActionInvocationKind.TabCompleted
            ? action.TabCompletionBehavior ?? "close"
            : action.ExactMatchBehavior ?? "keepOpen";
    }
}
