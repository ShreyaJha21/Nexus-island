using System.Runtime.InteropServices;

namespace Nexus.Shell.Native;

/// <summary>
/// The island's acrylic. A transparent WPF window can't blur what is behind it, so this is a
/// separate, empty native window placed exactly under the island's shape; Windows blurs the desktop
/// behind it, and the island draws its translucent tint, border and content on top.
///
/// Two ways to get the blur:
/// 1. <c>SetWindowCompositionAttribute</c> blur-behind (default): an older, undocumented API that keeps
///    blurring while the window is inactive, which the island always is.
/// 2. <c>DWMWA_SYSTEMBACKDROP_TYPE = transient</c>: Windows 11's own acrylic (documented, 22H2+). It
///    shows a flat grey for inactive windows, so it is opt-in only (NEXUS_BACKDROP=acrylic).
/// The window is click-through and never activates.
/// </summary>
internal sealed class Backdrop : IDisposable
{
    public enum Kind { None, SystemAcrylic, Blur }

    private const string ClassName = "NexusBackdrop";
    private static WndProcDelegate? s_wndProc; // kept alive for the native window class
    private readonly IntPtr _hwnd;
    private bool _visible;
    private int _lastCorner = -1;

    private Backdrop(IntPtr hwnd, Kind kind)
    {
        _hwnd = hwnd;
        Mode = kind;
    }

    public Kind Mode { get; }

    /// <summary>Creates the backdrop, or returns null when blur isn't available (transparency off, Windows 10).</summary>
    public static Backdrop? TryCreate()
    {
        var requested = Environment.GetEnvironmentVariable("NEXUS_BACKDROP");
        if (requested == "off" || !SystemTheme.TransparencyEnabled)
        {
            return null;
        }

        var build = Environment.OSVersion.Version.Build;
        if (build < 22000)
        {
            return null; // Windows 10: no rounded corners to match the island; keep the solid fallback.
        }

        var hwnd = CreateNativeWindow();
        if (hwnd == IntPtr.Zero)
        {
            return null;
        }

        // Extend the (invisible) frame over the whole window so the backdrop shows through.
        var margins = new MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        DwmExtendFrameIntoClientArea(hwnd, ref margins);

        var dark = 1;
        Win32.DwmSetWindowAttribute(hwnd, Win32.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));

        // No DWM border line: the island draws its own edge.
        var noBorder = unchecked((int)0xFFFFFFFE); // DWMWA_COLOR_NONE
        Win32.DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref noBorder, sizeof(int));

        // Windows 11's own acrylic falls back to a flat grey whenever its window is inactive, and the
        // island is never the active window, so blur-behind is the default. NEXUS_BACKDROP=acrylic
        // opts into the system material for comparison.
        var kind = requested == "acrylic" && build >= 22621 ? Kind.SystemAcrylic : Kind.Blur;
        if (kind == Kind.SystemAcrylic)
        {
            var transient = 3; // DWMSBT_TRANSIENTWINDOW: acrylic
            if (Win32.DwmSetWindowAttribute(hwnd, Win32.DWMWA_SYSTEMBACKDROP_TYPE, ref transient, sizeof(int)) != 0)
            {
                kind = Kind.Blur;
            }
        }

        if (kind == Kind.Blur && !EnableBlurBehind(hwnd))
        {
            DestroyWindow(hwnd);
            return null;
        }

        return new Backdrop(hwnd, kind);
    }

    /// <summary>Places the blur at a screen rectangle (physical pixels) directly beneath <paramref name="above"/>.</summary>
    public void Place(int x, int y, int width, int height, IntPtr above, bool smallCorners)
    {
        if (width <= 0 || height <= 0)
        {
            Hide();
            return;
        }

        // Windows 11 rounds the window: 8 px for the island and panels, 4 px for the thin resting line.
        var corner = smallCorners ? DWMWCP_ROUNDSMALL : DWMWCP_ROUND;
        if (corner != _lastCorner)
        {
            Win32.DwmSetWindowAttribute(_hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
            _lastCorner = corner;
        }

        // Insert after the island window, so it sits directly beneath it and stays topmost.
        Win32.SetWindowPos(_hwnd, above, x, y, width, height,
            Win32.SWP_NOACTIVATE | Win32.SWP_NOOWNERZORDER | SWP_SHOWWINDOW);
        _visible = true;
    }

    public void Hide()
    {
        if (_visible)
        {
            Win32.ShowWindow(_hwnd, SW_HIDE);
            _visible = false;
        }
    }

    public void Dispose() => DestroyWindow(_hwnd);

    private static IntPtr CreateNativeWindow()
    {
        var instance = GetModuleHandle(null);
        s_wndProc ??= DefWindowProc;
        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(s_wndProc),
            hInstance = instance,
            hbrBackground = GetStockObject(BLACK_BRUSH), // black = transparent once the frame is extended
            lpszClassName = ClassName,
        };

        if (RegisterClassEx(ref wc) == 0 && Marshal.GetLastWin32Error() != ERROR_CLASS_ALREADY_EXISTS)
        {
            return IntPtr.Zero;
        }

        const long exStyle = Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE | Win32.WS_EX_TOPMOST | WS_EX_TRANSPARENT;
        return CreateWindowEx((uint)exStyle, ClassName, "Nexus backdrop", WS_POPUP, 0, 0, 1, 1,
            IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
    }

    private static bool EnableBlurBehind(IntPtr hwnd)
    {
        var accent = new AccentPolicy { AccentState = ACCENT_ENABLE_BLURBEHIND };
        var size = Marshal.SizeOf<AccentPolicy>();
        var ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(accent, ptr, false);
            var data = new WindowCompositionAttributeData { Attribute = WCA_ACCENT_POLICY, Data = ptr, SizeOfData = size };
            return SetWindowCompositionAttribute(hwnd, ref data) != 0;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    // ---- Interop ----
    private const uint WS_POPUP = 0x80000000;
    private const long WS_EX_TRANSPARENT = 0x00000020;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const int SW_HIDE = 0;
    private const int BLACK_BRUSH = 4;
    private const int ERROR_CLASS_ALREADY_EXISTS = 1410;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_BORDER_COLOR = 34;
    private const int DWMWCP_ROUND = 2;
    private const int DWMWCP_ROUNDSMALL = 3;
    private const int WCA_ACCENT_POLICY = 19;
    private const int ACCENT_ENABLE_BLURBEHIND = 3;

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MARGINS
    {
        public int Left, Right, Top, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public int AccentState;
        public int AccentFlags;
        public uint GradientColor;
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public int Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX wc);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("gdi32.dll")]
    private static extern IntPtr GetStockObject(int obj);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? name);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS margins);

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);
}
