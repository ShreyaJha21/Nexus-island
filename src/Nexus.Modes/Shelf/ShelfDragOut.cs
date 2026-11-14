using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Windows;
using ComDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;

namespace Nexus.Modes.Shelf;

/// <summary>
/// Drags shelf files into other apps the way each kind of app reads them best.
/// <para>
/// Apps built on Chromium (Figma's desktop app, browsers, WhatsApp, Slack, VS Code) are offered the
/// files as "virtual files": the bytes themselves, which Chromium saves to its own temporary folder
/// before the page reads them. That is exactly what a browser offers when you drag an image straight
/// into Figma. Offered a plain file path instead, Figma's desktop app accepts the drop and then fails
/// to read it ("Files failed to import… [object ProgressEvent]").
/// </para>
/// <para>
/// Every other app (Explorer, Photoshop, Office) is offered the real file path (CF_HDROP), plus the
/// same virtual files for the few that prefer them. Which one applies is decided from the window under
/// the pointer, checked just before the target asks what is on offer.
/// </para>
/// </summary>
internal static class ShelfDragOut
{
    private const int DRAGDROP_S_DROP = 0x00040100;
    private const int DRAGDROP_S_CANCEL = 0x00040101;
    private const int DRAGDROP_S_USEDEFAULTCURSORS = 0x00040102;
    private const uint MK_LBUTTON = 0x0001;
    private const int DROPEFFECT_COPY = 1;

    /// <summary>Runs the drag (blocking, like every OLE drag) and returns true when something accepted it.</summary>
    public static bool Run(IReadOnlyList<string> files, Action<string>? log)
    {
        var data = new ShelfDataObject(files);
        var source = new DropSource(data);
        var hr = DoDragDrop(data, source, DROPEFFECT_COPY, out var effect);
        log?.Invoke($"Shelf drag out of {files.Count}: hr=0x{hr:X} effect={effect} target={(data.OfferPaths ? "app (file paths)" : "Chromium (virtual files)")}");
        return hr == DRAGDROP_S_DROP && effect != 0;
    }

    [DllImport("ole32.dll")]
    private static extern int DoDragDrop(ComDataObject data, IOleDropSource source, int allowedEffects, out int effect);

    // ------------------------------------------------------------------ Who is under the pointer

    /// <summary>True when the window under the pointer belongs to a Chromium-based app.</summary>
    private static bool IsChromiumUnderPointer()
    {
        if (!GetCursorPos(out var point))
        {
            return false;
        }

        for (var hwnd = WindowFromPoint(point); hwnd != IntPtr.Zero; hwnd = GetParent(hwnd))
        {
            var name = new StringBuilder(64);
            GetClassName(hwnd, name, name.Capacity);
            if (name.ToString().StartsWith("Chrome_", StringComparison.Ordinal))
            {
                return true; // Chrome_WidgetWin_*, Chrome_RenderWidgetHostHWND: Chromium, Electron and WebView2
            }
        }

        return false;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X, Y;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);

    // ------------------------------------------------------------------ The drag source

    [ComImport, Guid("00000121-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IOleDropSource
    {
        [PreserveSig] int QueryContinueDrag(int escapePressed, uint keyState);
        [PreserveSig] int GiveFeedback(uint effect);
    }

    private sealed class DropSource(ShelfDataObject data) : IOleDropSource
    {
        // Called on every pointer move before the window under the pointer is asked about the drag,
        // so the data object always answers for the app it is over.
        public int QueryContinueDrag(int escapePressed, uint keyState)
        {
            data.OfferPaths = !IsChromiumUnderPointer();
            if (escapePressed != 0)
            {
                return DRAGDROP_S_CANCEL;
            }

            return (keyState & MK_LBUTTON) == 0 ? DRAGDROP_S_DROP : 0;
        }

        public int GiveFeedback(uint effect) => DRAGDROP_S_USEDEFAULTCURSORS;
    }
}

/// <summary>The data a shelf drag carries: file paths (CF_HDROP) and/or virtual files (FileGroupDescriptorW + FileContents).</summary>
internal sealed class ShelfDataObject : ComDataObject
{
    private const int CF_HDROP = 15;
    private const int DV_E_FORMATETC = unchecked((int)0x80040064);
    private const int DV_E_TYMED = unchecked((int)0x80040069);
    private const int DV_E_LINDEX = unchecked((int)0x80040068);
    private const int E_NOTIMPL = unchecked((int)0x80004001);
    private const int OLE_E_ADVISENOTSUPPORTED = unchecked((int)0x80040003);
    private const int DATA_S_SAMEFORMATETC = 0x00040130;
    private const uint FD_FILESIZE = 0x40;
    private const uint FD_PROGRESSUI = 0x4000;

    private static readonly short FileDescriptor = (short)DataFormats.GetDataFormat("FileGroupDescriptorW").Id;
    private static readonly short FileContents = (short)DataFormats.GetDataFormat("FileContents").Id;
    private static readonly short PreferredDropEffect = (short)DataFormats.GetDataFormat("Preferred DropEffect").Id;

    private readonly IReadOnlyList<string> _files;

    public ShelfDataObject(IReadOnlyList<string> files) => _files = files;

    /// <summary>Offer real paths (CF_HDROP). Off while the pointer is over a Chromium app.</summary>
    public bool OfferPaths { get; set; } = true;

    private IEnumerable<FORMATETC> Formats()
    {
        if (OfferPaths)
        {
            yield return Format(CF_HDROP, TYMED.TYMED_HGLOBAL);
        }

        yield return Format(FileDescriptor, TYMED.TYMED_HGLOBAL);
        yield return Format(FileContents, TYMED.TYMED_HGLOBAL | TYMED.TYMED_ISTREAM, lindex: -1);
        yield return Format(PreferredDropEffect, TYMED.TYMED_HGLOBAL);
    }

    private static FORMATETC Format(short cf, TYMED tymed, int lindex = -1) => new()
    {
        cfFormat = cf,
        dwAspect = DVASPECT.DVASPECT_CONTENT,
        lindex = lindex,
        ptd = IntPtr.Zero,
        tymed = tymed,
    };

    public void GetData(ref FORMATETC format, out STGMEDIUM medium)
    {
        medium = default;
        var hr = QueryGetData(ref format);
        if (hr != 0)
        {
            throw new COMException("Format not offered", hr);
        }

        if (format.cfFormat == FileContents)
        {
            var bytes = File.ReadAllBytes(_files[format.lindex < 0 ? 0 : format.lindex]);
            var global = Native.ToHGlobal(bytes);
            if ((format.tymed & TYMED.TYMED_ISTREAM) != 0)
            {
                Marshal.ThrowExceptionForHR(Native.CreateStreamOnHGlobal(global, true, out var stream));
                medium.tymed = TYMED.TYMED_ISTREAM;
                medium.unionmember = stream;
            }
            else
            {
                medium.tymed = TYMED.TYMED_HGLOBAL;
                medium.unionmember = global;
            }

            return;
        }

        medium.tymed = TYMED.TYMED_HGLOBAL;
        medium.unionmember = format.cfFormat switch
        {
            CF_HDROP => Native.ToHGlobal(DropFiles()),
            _ when format.cfFormat == FileDescriptor => Native.ToHGlobal(Descriptors()),
            _ => Native.ToHGlobal(BitConverter.GetBytes(1)), // Preferred DropEffect: copy, never move
        };
    }

    public int QueryGetData(ref FORMATETC format)
    {
        var cf = format.cfFormat;
        var offered = Formats().FirstOrDefault(f => f.cfFormat == cf);
        if (offered.cfFormat == 0)
        {
            return DV_E_FORMATETC;
        }

        if ((format.tymed & offered.tymed) == 0)
        {
            return DV_E_TYMED;
        }

        if (cf == FileContents && (format.lindex < -1 || format.lindex >= _files.Count))
        {
            return DV_E_LINDEX;
        }

        return 0;
    }

    public IEnumFORMATETC EnumFormatEtc(DATADIR direction)
    {
        if (direction != DATADIR.DATADIR_GET)
        {
            throw new COMException("Only reading", E_NOTIMPL);
        }

        return new FormatEnumerator(Formats().ToArray());
    }

    public void GetDataHere(ref FORMATETC format, ref STGMEDIUM medium) => throw new COMException("Not supported", E_NOTIMPL);

    public int GetCanonicalFormatEtc(ref FORMATETC formatIn, out FORMATETC formatOut)
    {
        formatOut = formatIn;
        formatOut.ptd = IntPtr.Zero;
        return DATA_S_SAMEFORMATETC;
    }

    /// <summary>Targets and the shell store notes here (drop descriptions, drag images); they aren't needed.</summary>
    public void SetData(ref FORMATETC formatIn, ref STGMEDIUM medium, bool release)
    {
        if (release)
        {
            Native.ReleaseStgMedium(ref medium);
        }
    }

    public int DAdvise(ref FORMATETC format, ADVF advf, IAdviseSink sink, out int connection)
    {
        connection = 0;
        return OLE_E_ADVISENOTSUPPORTED;
    }

    public void DUnadvise(int connection) => throw new COMException("Not supported", OLE_E_ADVISENOTSUPPORTED);

    public int EnumDAdvise(out IEnumSTATDATA enumAdvise)
    {
        enumAdvise = null!;
        return OLE_E_ADVISENOTSUPPORTED;
    }

    /// <summary>DROPFILES: a 20-byte header, then the paths in UTF-16, each ending in a null, then one more null.</summary>
    private byte[] DropFiles()
    {
        var paths = Encoding.Unicode.GetBytes(string.Concat(_files.Select(f => f + "\0")) + "\0");
        var bytes = new byte[20 + paths.Length];
        BitConverter.GetBytes(20).CopyTo(bytes, 0);   // pFiles: where the paths start
        BitConverter.GetBytes(1).CopyTo(bytes, 16);   // fWide: UTF-16
        paths.CopyTo(bytes, 20);
        return bytes;
    }

    /// <summary>FILEGROUPDESCRIPTORW: a count, then a 592-byte FILEDESCRIPTORW per file (flags, size, name).</summary>
    private byte[] Descriptors()
    {
        const int size = 592, flagsAt = 0, sizeHighAt = 64, sizeLowAt = 68, nameAt = 72;
        var bytes = new byte[4 + size * _files.Count];
        BitConverter.GetBytes((uint)_files.Count).CopyTo(bytes, 0);
        for (var i = 0; i < _files.Count; i++)
        {
            var at = 4 + size * i;
            var length = new FileInfo(_files[i]).Length;
            BitConverter.GetBytes(FD_FILESIZE | FD_PROGRESSUI).CopyTo(bytes, at + flagsAt);
            BitConverter.GetBytes((uint)(length >> 32)).CopyTo(bytes, at + sizeHighAt);
            BitConverter.GetBytes((uint)(length & 0xFFFFFFFF)).CopyTo(bytes, at + sizeLowAt);
            var name = Path.GetFileName(_files[i]);
            var nameBytes = Encoding.Unicode.GetBytes(name.Length > 259 ? name[..259] : name);
            nameBytes.CopyTo(bytes, at + nameAt);
        }

        return bytes;
    }

    private sealed class FormatEnumerator(FORMATETC[] formats, int position = 0) : IEnumFORMATETC
    {
        private int _position = position;

        public int Next(int celt, FORMATETC[] rgelt, int[]? pceltFetched)
        {
            var fetched = 0;
            while (fetched < celt && _position < formats.Length)
            {
                rgelt[fetched++] = formats[_position++];
            }

            if (pceltFetched is { Length: > 0 })
            {
                pceltFetched[0] = fetched;
            }

            return fetched == celt ? 0 : 1;
        }

        public int Skip(int celt)
        {
            _position = Math.Min(formats.Length, _position + celt);
            return _position < formats.Length ? 0 : 1;
        }

        public int Reset()
        {
            _position = 0;
            return 0;
        }

        public void Clone(out IEnumFORMATETC newEnum) => newEnum = new FormatEnumerator(formats, _position);
    }
}

/// <summary>The memory and stream calls drag data needs.</summary>
internal static class Native
{
    private const uint GMEM_MOVEABLE = 0x0002;

    public static IntPtr ToHGlobal(byte[] bytes)
    {
        var global = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)Math.Max(1, bytes.Length));
        if (global == IntPtr.Zero)
        {
            throw new OutOfMemoryException();
        }

        var target = GlobalLock(global);
        try
        {
            Marshal.Copy(bytes, 0, target, bytes.Length);
        }
        finally
        {
            GlobalUnlock(global);
        }

        return global;
    }

    /// <summary>Copies an HGLOBAL's contents out (for reading drops).</summary>
    public static byte[] FromHGlobal(IntPtr global)
    {
        var size = (int)GlobalSize(global);
        var source = GlobalLock(global);
        try
        {
            var bytes = new byte[size];
            Marshal.Copy(source, bytes, 0, size);
            return bytes;
        }
        finally
        {
            GlobalUnlock(global);
        }
    }

    /// <summary>Reads a COM stream to the end (for reading drops).</summary>
    public static byte[] FromStream(IStream stream)
    {
        using var memory = new MemoryStream();
        var buffer = new byte[81920];
        var readPtr = Marshal.AllocHGlobal(sizeof(int));
        try
        {
            while (true)
            {
                stream.Read(buffer, buffer.Length, readPtr);
                var read = Marshal.ReadInt32(readPtr);
                if (read <= 0)
                {
                    break;
                }

                memory.Write(buffer, 0, read);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(readPtr);
        }

        return memory.ToArray();
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalLock(IntPtr global);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(IntPtr global);

    [DllImport("kernel32.dll")]
    private static extern UIntPtr GlobalSize(IntPtr global);

    [DllImport("ole32.dll")]
    public static extern int CreateStreamOnHGlobal(IntPtr global, [MarshalAs(UnmanagedType.Bool)] bool deleteOnRelease, out IntPtr stream);

    [DllImport("ole32.dll")]
    public static extern void ReleaseStgMedium(ref STGMEDIUM medium);
}
