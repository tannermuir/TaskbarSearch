using System.Windows.Forms;

namespace TaskbarInstantSearch;

internal sealed class HotkeyManager : NativeWindow, IDisposable
{
    private const int HotkeyId = 1;
    private readonly Action _onHotkey;
    private readonly string _hotkey;
    private uint _modifiers;
    private Keys _key;
    private bool _registered;

    public HotkeyManager(string hotkey, Action onHotkey)
    {
        _onHotkey = onHotkey;
        _hotkey = hotkey;
        CreateHandle(new CreateParams());

        if (TryParseHotkey(hotkey, out _modifiers, out _key))
        {
            Register();
        }
    }

    public void EnsureRegistered()
    {
        if (_registered)
        {
            return;
        }

        Register();
    }

    public void ReRegister()
    {
        if (_registered)
        {
            NativeMethods.UnregisterHotKey(Handle, HotkeyId);
            _registered = false;
        }

        Register();
    }

    public static bool TryParseHotkey(string hotkey, out uint modifiers, out Keys key)
    {
        modifiers = 0;
        key = Keys.None;

        if (string.IsNullOrWhiteSpace(hotkey))
        {
            return false;
        }

        string[] parts = hotkey.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (string part in parts)
        {
            if (part.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) ||
                part.Equals("Control", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= NativeMethods.MOD_CONTROL;
            }
            else if (part.Equals("Alt", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= NativeMethods.MOD_ALT;
            }
            else if (part.Equals("Shift", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= NativeMethods.MOD_SHIFT;
            }
            else if (part.Equals("Win", StringComparison.OrdinalIgnoreCase) ||
                     part.Equals("Windows", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= NativeMethods.MOD_WIN;
            }
            else if (Enum.TryParse(part, true, out Keys parsedKey))
            {
                key = parsedKey;
            }
            else
            {
                return false;
            }
        }

        return key != Keys.None;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == NativeMethods.WM_HOTKEY && m.WParam.ToInt32() == HotkeyId)
        {
            Logger.Info($"Hotkey '{_hotkey}' received");
            _onHotkey();
            return;
        }

        base.WndProc(ref m);
    }

    public void Dispose()
    {
        if (_registered)
        {
            NativeMethods.UnregisterHotKey(Handle, HotkeyId);
            _registered = false;
        }

        DestroyHandle();
    }

    private void Register()
    {
        _registered = NativeMethods.RegisterHotKey(
            Handle, HotkeyId, _modifiers, (uint)_key);
        if (_registered)
        {
            Logger.Info($"Registered hotkey '{_hotkey}'");
        }
        else
        {
            Logger.Error($"RegisterHotKey failed for '{_hotkey}'");
        }
    }
}
