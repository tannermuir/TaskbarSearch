using System.Windows.Forms;

namespace TaskbarInstantSearch;

internal sealed class GlobalEscapeListener : IDisposable
{
    private readonly Action _onEscape;
    private readonly NativeMethods.LowLevelKeyboardProc _keyboardProc;
    private IntPtr _hookHandle;
    private bool _enabled;

    public GlobalEscapeListener(Action onEscape)
    {
        _onEscape = onEscape;
        _keyboardProc = KeyboardProc;
    }

    public void Enable()
    {
        if (_enabled)
        {
            return;
        }

        IntPtr moduleHandle = NativeMethods.GetModuleHandleW(null);
        _hookHandle = NativeMethods.SetWindowsHookExW(
            NativeMethods.WH_KEYBOARD_LL,
            _keyboardProc,
            moduleHandle,
            0);
        _enabled = _hookHandle != IntPtr.Zero;
        if (_enabled)
        {
            Logger.Info("Registered global Escape listener");
        }
        else
        {
            Logger.Error("Failed to register global Escape listener");
        }
    }

    public void Disable()
    {
        if (!_enabled)
        {
            return;
        }

        NativeMethods.UnhookWindowsHookEx(_hookHandle);
        _hookHandle = IntPtr.Zero;
        _enabled = false;
        Logger.Info("Unregistered global Escape listener");
    }

    public void Dispose()
    {
        Disable();
    }

    private IntPtr KeyboardProc(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 &&
            (wParam == NativeMethods.WM_KEYDOWN || wParam == NativeMethods.WM_SYSKEYDOWN))
        {
            var key = (Keys)System.Runtime.InteropServices.Marshal.ReadInt32(lParam);
            if (key == Keys.Escape)
            {
                _onEscape();
            }
        }

        return NativeMethods.CallNextHookEx(_hookHandle, code, wParam, lParam);
    }
}
