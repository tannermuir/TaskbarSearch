using System.Windows.Forms;

namespace TaskbarCapsLockIndicator;

internal sealed class GlobalCapsLockToggleListener : IDisposable
{
    private readonly Action _onToggle;
    private readonly NativeMethods.LowLevelKeyboardProc _keyboardProc;
    private IntPtr _hookHandle;
    private bool _capsLockPressed;

    public GlobalCapsLockToggleListener(Action onToggle)
    {
        _onToggle = onToggle;
        _keyboardProc = KeyboardProc;
        Register();
    }

    public void Dispose()
    {
        if (_hookHandle == IntPtr.Zero)
        {
            return;
        }

        NativeMethods.UnhookWindowsHookEx(_hookHandle);
        _hookHandle = IntPtr.Zero;
    }

    private void Register()
    {
        IntPtr moduleHandle = NativeMethods.GetModuleHandleW(null);
        _hookHandle = NativeMethods.SetWindowsHookExW(
            NativeMethods.WH_KEYBOARD_LL,
            _keyboardProc,
            moduleHandle,
            0);
        if (_hookHandle != IntPtr.Zero)
        {
            Logger.Info("Registered global Caps Lock toggle listener");
        }
        else
        {
            Logger.Error("Failed to register global Caps Lock toggle listener");
        }
    }

    private IntPtr KeyboardProc(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var key = (Keys)System.Runtime.InteropServices.Marshal.ReadInt32(lParam);
            if (key == Keys.CapsLock)
            {
                if (wParam == NativeMethods.WM_KEYDOWN || wParam == NativeMethods.WM_SYSKEYDOWN)
                {
                    if (!_capsLockPressed)
                    {
                        _capsLockPressed = true;
                        _onToggle();
                    }
                }
                else if (wParam == NativeMethods.WM_KEYUP || wParam == NativeMethods.WM_SYSKEYUP)
                {
                    _capsLockPressed = false;
                }
            }
        }

        return NativeMethods.CallNextHookEx(_hookHandle, code, wParam, lParam);
    }
}
