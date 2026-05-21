using System.Diagnostics;
using System.Windows.Forms;

namespace TaskbarInstantSearch;

internal sealed class ActionExecutor
{
    private readonly AppConfig _config;
    private readonly WindowTargetTracker _windowTargetTracker;
    private readonly Action<ActionConfig> _delayedWindowResolver;
    private readonly Func<IntPtr> _pendingWindowResolver;

    public ActionExecutor(
        AppConfig config,
        WindowTargetTracker windowTargetTracker,
        Action<ActionConfig> delayedWindowResolver,
        Func<IntPtr> pendingWindowResolver)
    {
        _config = config;
        _windowTargetTracker = windowTargetTracker;
        _delayedWindowResolver = delayedWindowResolver;
        _pendingWindowResolver = pendingWindowResolver;
    }

    public ActionExecutionResult Execute(string input)
    {
        if (!_config.ValidActions.TryGetValue(input, out ActionConfig? action))
        {
            return new ActionExecutionResult { Error = "" };
        }

        try
        {
            bool isCommandAction = IsCommandAction(input);
            if (string.Equals(action.Type, "showDesktop", StringComparison.OrdinalIgnoreCase))
            {
                ShowDesktop();
                Logger.Info($"Executed trigger '{action.Trigger}' -> show desktop");
                return new ActionExecutionResult { Success = true };
            }

            if (string.Equals(action.Type, "selfRestart", StringComparison.OrdinalIgnoreCase))
            {
                RestartSelf();
                Logger.Info($"Executed trigger '{action.Trigger}' -> restart companion app");
                return new ActionExecutionResult { Success = true };
            }

            if (string.Equals(action.Type, "sleepComputer", StringComparison.OrdinalIgnoreCase))
            {
                SleepComputer();
                Logger.Info($"Executed trigger '{action.Trigger}' -> sleep computer");
                return new ActionExecutionResult { Success = true };
            }

            if (string.Equals(action.Type, "closeActiveWindow", StringComparison.OrdinalIgnoreCase))
            {
                IntPtr consumedWindow = CloseActiveWindowTarget();
                Logger.Info($"Executed trigger '{action.Trigger}' -> close active window");
                return new ActionExecutionResult { Success = true, ConsumedWindow = consumedWindow };
            }

            if (string.Equals(action.Type, "minimizeActiveWindow", StringComparison.OrdinalIgnoreCase))
            {
                IntPtr consumedWindow = ShowActiveWindowTarget(NativeMethods.SW_MINIMIZE);
                Logger.Info($"Executed trigger '{action.Trigger}' -> minimize active window");
                return new ActionExecutionResult { Success = true, ConsumedWindow = consumedWindow };
            }

            if (string.Equals(action.Type, "maximizeActiveWindow", StringComparison.OrdinalIgnoreCase))
            {
                IntPtr targetWindow = ShowActiveWindowTarget(NativeMethods.SW_MAXIMIZE);
                Logger.Info($"Executed trigger '{action.Trigger}' -> maximize active window");
                return new ActionExecutionResult { Success = true, ActivatedWindow = targetWindow };
            }

            if (string.Equals(action.Type, "copyText", StringComparison.OrdinalIgnoreCase))
            {
                ClipboardHelper.SetText(action.Text ?? "");
                Logger.Info($"Executed trigger '{action.Trigger}' -> copy text");
                return new ActionExecutionResult { Success = true };
            }

            if (string.Equals(action.Type, "prefixMenu", StringComparison.OrdinalIgnoreCase))
            {
                return new ActionExecutionResult();
            }

            if (string.Equals(action.Type, "command", StringComparison.OrdinalIgnoreCase) &&
                action.ReuseExistingWindow &&
                TryActivateExistingAppWindow(action, out IntPtr activatedExistingWindow))
            {
                if (activatedExistingWindow != IntPtr.Zero)
                {
                    _windowTargetTracker.NoteActivatedWindow(activatedExistingWindow);
                }

                Logger.Info($"Activated existing app for trigger '{action.Trigger}'");
                return new ActionExecutionResult
                {
                    Success = true,
                    ActivatedWindow = activatedExistingWindow,
                    IsCommandAction = true
                };
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = action.Command!,
                Arguments = BuildArguments(action.Arguments ?? new List<string>()),
                WorkingDirectory = string.IsNullOrWhiteSpace(action.WorkingDirectory)
                    ? Environment.CurrentDirectory
                    : action.WorkingDirectory!,
                UseShellExecute = true
            };

            Process? process = Process.Start(startInfo);
            IntPtr activatedWindow = WaitForProcessWindow(process);
            if (activatedWindow == IntPtr.Zero && action.ActivateAfterLaunch)
            {
                activatedWindow = WaitForActionWindow(action);
            }

            if (activatedWindow != IntPtr.Zero && action.ActivateAfterLaunch)
            {
                ActivateWindow(activatedWindow);
            }

            if (activatedWindow != IntPtr.Zero)
            {
                _windowTargetTracker.NoteActivatedWindow(activatedWindow);
            }
            else if (isCommandAction)
            {
                _delayedWindowResolver(action);
            }

            Logger.Info($"Executed trigger '{action.Trigger}' -> '{action.Command}'");
            return new ActionExecutionResult
            {
                Success = true,
                ActivatedWindow = activatedWindow,
                IsCommandAction = isCommandAction
            };
        }
        catch (Exception exception)
        {
            Logger.Error($"Failed to execute trigger '{action.Trigger}'", exception);
            return new ActionExecutionResult { Error = exception.Message };
        }
    }

    public bool HasAction(string buffer)
    {
        return _config.ValidActions.ContainsKey(buffer);
    }

    public bool IsCommandAction(string buffer)
    {
        return _config.ValidActions.TryGetValue(buffer, out ActionConfig? action) &&
               (string.Equals(action.Type, "command", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(action.Type, "generatedPdf", StringComparison.OrdinalIgnoreCase));
    }

    public bool IsSelfRestartAction(string buffer)
    {
        return _config.ValidActions.TryGetValue(buffer, out ActionConfig? action) &&
               string.Equals(action.Type, "selfRestart", StringComparison.OrdinalIgnoreCase);
    }

    public bool RequiresCloseAfterExecution(string buffer)
    {
        return _config.ValidActions.TryGetValue(buffer, out ActionConfig? action) &&
               (action.RequiresConfirmation ||
                string.Equals(action.Type, "selfRestart", StringComparison.OrdinalIgnoreCase));
    }

    public bool RequiresConfirmation(string buffer)
    {
        return _config.ValidActions.TryGetValue(buffer, out ActionConfig? action) &&
               action.RequiresConfirmation;
    }

    private static string BuildArguments(IEnumerable<string> arguments)
    {
        return string.Join(" ", arguments.Select(QuoteArgument));
    }

    private static string QuoteArgument(string argument)
    {
        if (argument.Length == 0)
        {
            return "\"\"";
        }

        return argument.Any(char.IsWhiteSpace) || argument.Contains('"')
            ? "\"" + argument.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""
            : argument;
    }

    private static void ShowDesktop()
    {
        Type shellType = Type.GetTypeFromProgID("Shell.Application") ??
            throw new InvalidOperationException("Shell.Application COM object is unavailable");
        object shell = Activator.CreateInstance(shellType) ??
            throw new InvalidOperationException("Failed to create Shell.Application COM object");

        try
        {
            shellType.InvokeMember(
                "ToggleDesktop",
                System.Reflection.BindingFlags.InvokeMethod,
                null,
                shell,
                Array.Empty<object>());
        }
        finally
        {
            if (System.Runtime.InteropServices.Marshal.IsComObject(shell))
            {
                System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
            }
        }
    }

    private static void RestartSelf()
    {
        string? executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            executablePath = Application.ExecutablePath;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = executablePath,
            Arguments = "--restart-child",
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = AppContext.BaseDirectory
        });
        Application.Exit();
    }

    private static void SleepComputer()
    {
        if (!NativeMethods.SetSuspendState(
                hibernate: false,
                forceCritical: false,
                disableWakeEvent: false))
        {
            throw new InvalidOperationException("SetSuspendState failed");
        }
    }

    private IntPtr CloseActiveWindowTarget()
    {
        IntPtr targetWindow = ResolveTargetWindow();
        if (targetWindow == IntPtr.Zero)
        {
            throw new InvalidOperationException("No active window is available to close");
        }

        NativeMethods.PostMessageW(targetWindow, NativeMethods.WM_SYSCOMMAND, NativeMethods.SC_CLOSE, IntPtr.Zero);
        NativeMethods.PostMessageW(targetWindow, NativeMethods.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        TryCloseTargetProcessWindow(targetWindow);
        _windowTargetTracker.ConsumeTarget(targetWindow);
        return targetWindow;
    }

    private IntPtr ShowActiveWindowTarget(int showCommand)
    {
        IntPtr targetWindow = ResolveTargetWindow();
        if (targetWindow == IntPtr.Zero)
        {
            throw new InvalidOperationException("No active window is available");
        }

        NativeMethods.ShowWindow(targetWindow, showCommand);
        if (showCommand == NativeMethods.SW_MINIMIZE)
        {
            NativeMethods.PostMessageW(targetWindow, NativeMethods.WM_SYSCOMMAND, NativeMethods.SC_MINIMIZE, IntPtr.Zero);
            _windowTargetTracker.ConsumeTarget(targetWindow);
        }

        return targetWindow;
    }

    private IntPtr ResolveTargetWindow()
    {
        IntPtr pendingWindow = _pendingWindowResolver();
        if (pendingWindow != IntPtr.Zero)
        {
            _windowTargetTracker.NoteActivatedWindow(pendingWindow);
            return pendingWindow;
        }

        return _windowTargetTracker.ResolveCurrentTarget();
    }

    private static bool IsUsableWindow(IntPtr window)
    {
        return window != IntPtr.Zero &&
               NativeMethods.IsWindow(window) &&
               NativeMethods.IsWindowVisible(window);
    }

    private void TryCloseTargetProcessWindow(IntPtr targetWindow)
    {
        NativeMethods.GetWindowThreadProcessId(targetWindow, out uint processId);
        if (processId == 0)
        {
            return;
        }

        try
        {
            using Process process = Process.GetProcessById((int)processId);
            process.CloseMainWindow();
        }
        catch
        {
        }
    }

    private static bool TryActivateExistingAppWindow(ActionConfig action, out IntPtr activatedWindow)
    {
        activatedWindow = IntPtr.Zero;
        if (TryActivateExistingWindowByClass(action, out activatedWindow))
        {
            return true;
        }

        List<string> processNames = GetReuseProcessNames(action);
        if (processNames.Count == 0)
        {
            return false;
        }

        string? expectedExecutablePath = GetExpectedExecutablePath(action);
        bool foundMatchingProcess = false;
        foreach (string processName in processNames)
        {
            foreach (Process process in Process.GetProcessesByName(processName))
            {
                using (process)
                {
                    if (!MatchesExpectedExecutablePath(process, expectedExecutablePath))
                    {
                        continue;
                    }

                    foundMatchingProcess = true;

                    if (TryFindProcessWindow(process, out IntPtr window) &&
                        ActivateWindow(window))
                    {
                        activatedWindow = window;
                        return true;
                    }
                }
            }
        }

        if (foundMatchingProcess)
        {
            Logger.Info($"Reuse existing app found process but no usable window for trigger '{action.Trigger}'");
        }

        return false;
    }

    private static bool TryFindProcessWindow(Process process, out IntPtr matchedWindow)
    {
        matchedWindow = IntPtr.Zero;
        process.Refresh();

        IntPtr mainWindow = process.MainWindowHandle;
        if (IsUsableWindow(mainWindow))
        {
            matchedWindow = mainWindow;
            return true;
        }

        uint expectedProcessId = (uint)process.Id;
        IntPtr foundWindow = IntPtr.Zero;
        NativeMethods.EnumWindows((window, _) =>
        {
            if (!IsUsableWindow(window))
            {
                return true;
            }

            NativeMethods.GetWindowThreadProcessId(window, out uint windowProcessId);
            if (windowProcessId != expectedProcessId)
            {
                return true;
            }

            foundWindow = window;
            return false;
        }, IntPtr.Zero);

        matchedWindow = foundWindow;
        return matchedWindow != IntPtr.Zero;
    }

    private static bool TryActivateExistingWindowByClass(ActionConfig action, out IntPtr activatedWindow)
    {
        activatedWindow = IntPtr.Zero;
        if (action.ReuseWindowClasses is not { Count: > 0 })
        {
            return false;
        }

        HashSet<string> classNames = action.ReuseWindowClasses
            .Where(className => !string.IsNullOrWhiteSpace(className))
            .ToHashSet(StringComparer.Ordinal);
        if (classNames.Count == 0)
        {
            return false;
        }

        IntPtr matchedWindow = IntPtr.Zero;
        NativeMethods.EnumWindows((window, _) =>
        {
            if (!IsUsableWindow(window))
            {
                return true;
            }

            string className = GetWindowClassName(window);
            if (!classNames.Contains(className))
            {
                return true;
            }

            matchedWindow = window;
            return false;
        }, IntPtr.Zero);

        if (matchedWindow != IntPtr.Zero && ActivateWindow(matchedWindow))
        {
            activatedWindow = matchedWindow;
            return true;
        }

        return false;
    }

    private static bool ActivateWindow(IntPtr window)
    {
        if (!IsUsableWindow(window))
        {
            return false;
        }

        if (NativeMethods.IsIconic(window))
        {
            NativeMethods.ShowWindow(window, NativeMethods.SW_RESTORE);
        }

        NativeMethods.SetForegroundWindow(window);
        return true;
    }

    private static string GetWindowClassName(IntPtr window)
    {
        char[] className = new char[256];
        int length = NativeMethods.GetClassNameW(window, className, className.Length);
        return length <= 0 ? "" : new string(className, 0, length);
    }

    private static List<string> GetReuseProcessNames(ActionConfig action)
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

        if (action.ReuseWindowClasses is { Count: > 0 })
        {
            return new List<string>();
        }

        string? command = action.Command;
        if (string.IsNullOrWhiteSpace(command))
        {
            return new List<string>();
        }

        string processName = Path.GetFileNameWithoutExtension(command);
        return string.IsNullOrWhiteSpace(processName)
            ? new List<string>()
            : new List<string> { processName };
    }

    private static string? GetExpectedExecutablePath(ActionConfig action)
    {
        if (action.ReuseProcessNames is { Count: > 0 } ||
            string.IsNullOrWhiteSpace(action.Command) ||
            !Path.IsPathRooted(action.Command))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(action.Command);
        }
        catch
        {
            return null;
        }
    }

    private static bool MatchesExpectedExecutablePath(Process process, string? expectedExecutablePath)
    {
        if (string.IsNullOrWhiteSpace(expectedExecutablePath))
        {
            return true;
        }

        try
        {
            return string.Equals(
                process.MainModule?.FileName,
                expectedExecutablePath,
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return true;
        }
    }

    private static IntPtr WaitForProcessWindow(Process? process)
    {
        if (process == null)
        {
            return IntPtr.Zero;
        }

        try
        {
            for (int i = 0; i < 10; i++)
            {
                process.Refresh();
                IntPtr window = process.MainWindowHandle;
                if (IsUsableWindow(window))
                {
                    return window;
                }

                Thread.Sleep(50);
            }
        }
        catch
        {
        }

        return IntPtr.Zero;
    }

    private static IntPtr WaitForActionWindow(ActionConfig action)
    {
        for (int i = 0; i < 20; i++)
        {
            if (TryFindActionWindow(action, out IntPtr window))
            {
                return window;
            }

            Thread.Sleep(50);
        }

        return IntPtr.Zero;
    }

    private static bool TryFindActionWindow(ActionConfig action, out IntPtr matchedWindow)
    {
        IntPtr foundWindow = IntPtr.Zero;
        HashSet<string> classNames = (action.ReuseWindowClasses ?? new List<string>())
            .Where(className => !string.IsNullOrWhiteSpace(className))
            .ToHashSet(StringComparer.Ordinal);
        List<string> processNames = GetReuseProcessNames(action);

        NativeMethods.EnumWindows((window, _) =>
        {
            if (!IsUsableWindow(window))
            {
                return true;
            }

            string className = GetWindowClassName(window);
            if (classNames.Count > 0 && classNames.Contains(className))
            {
                foundWindow = window;
                return false;
            }

            if (processNames.Count > 0)
            {
                NativeMethods.GetWindowThreadProcessId(window, out uint processId);
                if (processId != 0)
                {
                    try
                    {
                        using Process process = Process.GetProcessById((int)processId);
                        if (processNames.Contains(process.ProcessName, StringComparer.OrdinalIgnoreCase))
                        {
                            foundWindow = window;
                            return false;
                        }
                    }
                    catch
                    {
                    }
                }
            }

            return true;
        }, IntPtr.Zero);

        matchedWindow = foundWindow;
        return matchedWindow != IntPtr.Zero;
    }
}
