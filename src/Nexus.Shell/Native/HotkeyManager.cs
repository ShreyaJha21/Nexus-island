using System.Windows.Interop;

namespace Nexus.Shell.Native;

/// <summary>
/// Global shortcuts via RegisterHotKey. A failed registration means another app owns the
/// shortcut, which the shell reports so the user can pick a different one.
/// </summary>
internal sealed class HotkeyManager : IDisposable
{
    private readonly IntPtr _hwnd;
    private readonly Dictionary<int, Action> _handlers = new();
    private int _nextId = 0x4E58; // "NX"

    public HotkeyManager(HwndSource source)
    {
        _hwnd = source.Handle;
        source.AddHook(WndProc);
    }

    /// <summary>Registers a shortcut from settings text. Returns false if it is invalid or taken by another app.</summary>
    public bool TryRegister(Nexus.Core.Settings.Shortcut? shortcut, Action handler)
    {
        if (shortcut is null)
        {
            return false;
        }

        var modifiers = (shortcut.Ctrl ? Win32.MOD_CONTROL : 0) | (shortcut.Alt ? Win32.MOD_ALT : 0) |
                        (shortcut.Shift ? Win32.MOD_SHIFT : 0) | (shortcut.Win ? Win32.MOD_WIN : 0);
        return TryRegister(modifiers, shortcut.VirtualKey, handler);
    }

    /// <summary>Registers a shortcut. Returns false if it is already taken by another app.</summary>
    public bool TryRegister(uint modifiers, uint virtualKey, Action handler)
    {
        var id = _nextId++;
        if (!Win32.RegisterHotKey(_hwnd, id, modifiers | Win32.MOD_NOREPEAT, virtualKey))
        {
            return false;
        }

        _handlers[id] = handler;
        return true;
    }

    public void Dispose()
    {
        foreach (var id in _handlers.Keys)
        {
            Win32.UnregisterHotKey(_hwnd, id);
        }

        _handlers.Clear();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Win32.WM_HOTKEY && _handlers.TryGetValue(wParam.ToInt32(), out var handler))
        {
            handler();
            handled = true;
        }

        return IntPtr.Zero;
    }
}
