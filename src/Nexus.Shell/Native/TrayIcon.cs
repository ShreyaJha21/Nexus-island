using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace Nexus.Shell.Native;

/// <summary>
/// Notification-area icon via Shell_NotifyIcon. The menu itself is a WPF ContextMenu styled
/// like the island's Fluent menu, so the tray matches the rest of Nexus.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private const uint CallbackMessage = Win32.WM_APP + 1;
    private readonly HwndSource _source;
    private readonly uint _taskbarCreated;
    private readonly string _tip;
    private IntPtr _icon;

    public TrayIcon(string tip)
    {
        _tip = tip;
        _source = new HwndSource(new HwndSourceParameters("NexusTray")
        {
            ParentWindow = Win32.HWND_MESSAGE,
            Width = 0,
            Height = 0,
            WindowStyle = 0,
        });
        _source.AddHook(WndProc);
        _taskbarCreated = Win32.RegisterWindowMessage("TaskbarCreated");
        _icon = CreateIcon();
        Add();
    }

    public event Action? LeftClick;
    public event Action? RightClick;

    public void ShowBalloon(string title, string text)
    {
        var data = NewData();
        data.uFlags = Win32.NIF_INFO;
        data.szInfoTitle = title;
        data.szInfo = text;
        Win32.Shell_NotifyIcon(Win32.NIM_MODIFY, ref data);
    }

    public void Dispose()
    {
        var data = NewData();
        Win32.Shell_NotifyIcon(Win32.NIM_DELETE, ref data);
        if (_icon != IntPtr.Zero)
        {
            Win32.DestroyIcon(_icon);
        }

        _source.Dispose();
    }

    private void Add()
    {
        var data = NewData();
        data.uFlags = Win32.NIF_MESSAGE | Win32.NIF_ICON | Win32.NIF_TIP | Win32.NIF_SHOWTIP;
        data.uCallbackMessage = CallbackMessage;
        data.hIcon = _icon;
        data.szTip = _tip;
        Win32.Shell_NotifyIcon(Win32.NIM_ADD, ref data);

        data.uTimeoutOrVersion = Win32.NOTIFYICON_VERSION_4;
        Win32.Shell_NotifyIcon(Win32.NIM_SETVERSION, ref data);
    }

    private Win32.NOTIFYICONDATA NewData() => new()
    {
        cbSize = (uint)Marshal.SizeOf<Win32.NOTIFYICONDATA>(),
        hWnd = _source.Handle,
        uID = 1,
        szTip = "",
        szInfo = "",
        szInfoTitle = "",
    };

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == CallbackMessage)
        {
            var ev = (int)(lParam.ToInt64() & 0xFFFF);
            switch (ev)
            {
                case Win32.NIN_SELECT:
                case Win32.NIN_KEYSELECT:
                case Win32.WM_LBUTTONUP:
                    LeftClick?.Invoke();
                    break;
                case Win32.WM_CONTEXTMENU:
                case Win32.WM_RBUTTONUP:
                    RightClick?.Invoke();
                    break;
            }

            handled = true;
        }
        else if (msg == _taskbarCreated)
        {
            // Explorer restarted: put the icon back.
            Add();
        }

        return IntPtr.Zero;
    }

    /// <summary>Draws the tray glyph (a small island pill) for the current taskbar theme.</summary>
    private static IntPtr CreateIcon()
    {
        var scale = Win32.GetDpiForSystem() / 96.0;
        var size = (int)Math.Round(16 * scale);
        var light = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
            "SystemUsesLightTheme", 0) is 1;
        var ink = new SolidColorBrush(light ? Color.FromRgb(0x1A, 0x1A, 0x1A) : Colors.White);
        var s = size / 16.0;

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var pen = new Pen(ink, 1.25 * s);
            dc.DrawRoundedRectangle(null, pen, new Rect(1.5 * s, 4.5 * s, 13 * s, 7 * s), 2.5 * s, 2.5 * s);
            dc.DrawRoundedRectangle(ink, null, new Rect(4 * s, 7.25 * s, 5 * s, 1.5 * s), 0.75 * s, 0.75 * s);
        }

        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        var png = stream.ToArray();
        return Win32.CreateIconFromResourceEx(png, (uint)png.Length, true, 0x00030000, size, size, 0);
    }
}
