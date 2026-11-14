using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Nexus.Core;
using Nexus.Core.Settings;
using Nexus.Core.Shelf;

namespace Nexus.Modes.Shelf;

/// <summary>
/// The shelf: drop a photo, file, link or text on the island and it stays there for a while (an hour
/// by default), ready to drag into another app: from a Google image into Figma, from one browser tab's
/// upload box to another, from Explorer into a chat. Web images arrive as browser "virtual files" or
/// URLs, so Nexus saves them as real files first; that is what every app accepts when you drag out.
/// </summary>
public sealed class ShelfMode : IslandMode
{
    private const long MaxDownloadBytes = 50L * 1024 * 1024;

    private static readonly HttpClient Http = CreateHttp();

    private static readonly string[] AcceptedFormats =
    [
        DataFormats.FileDrop, "FileGroupDescriptorW", "FileContents", "PNG", DataFormats.Html,
        "UniformResourceLocatorW", DataFormats.UnicodeText, DataFormats.Text, DataFormats.Bitmap,
    ];

    private readonly ShelfStore _store;
    private readonly DispatcherTimer _expiry;
    private int _saving;

    public ShelfMode(NexusSettings settings, string? folder = null)
    {
        var keep = TimeSpan.FromMinutes(Math.Clamp(settings.Shelf.KeepMinutes, 1, 24 * 60));
        _store = new ShelfStore(
            folder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nexus", "Shelf"),
            keep, settings.Shelf.MaxItems);

        _expiry = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(30) };
        _expiry.Tick += (_, _) =>
        {
            if (_store.Expire())
            {
                Sync();
            }
            else
            {
                OnPropertyChanged(nameof(ExpiryText));
            }
        };

        ClearCommand = new RelayCommand(() =>
        {
            _store.Clear();
            Sync();
            RequestCollapse();
        });
    }

    public override string Id => "shelf";
    public override string Name => "Shelf";

    /// <summary>Newest first.</summary>
    public ObservableCollection<ShelfItemView> Items { get; } = [];

    /// <summary>Up to three of the newest items, for the stacked thumbnails on the pill.</summary>
    public IEnumerable<ShelfItemView> Front => Items.Take(3);

    public ICommand ClearCommand { get; }

    public bool HasItems => Items.Count > 0;

    /// <summary>"1 photo", "4 photos", "3 items".</summary>
    public string CountText
    {
        get
        {
            var n = Items.Count;
            var allImages = n > 0 && Items.All(i => i.IsImage);
            return allImages ? (n == 1 ? "1 photo" : $"{n} photos") : (n == 1 ? "1 item" : $"{n} items");
        }
    }

    public bool IsSaving => _saving > 0;

    public string ExpiryText
    {
        get
        {
            if (_store.NextExpiry is not { } next)
            {
                return "";
            }

            var left = next - DateTimeOffset.UtcNow;
            var minutes = Math.Max(1, (int)Math.Ceiling(left.TotalMinutes));
            var when = minutes >= 90 ? $"{Math.Round(minutes / 60.0)} h" : $"{minutes} min";
            return Items.Count == 1 ? $"Clears in {when}" : $"Next clears in {when}";
        }
    }

    /// <summary>How long items stay, for the drop zone: "an hour", "30 minutes".</summary>
    public string KeepText => _store.KeepFor.TotalMinutes switch
    {
        60 => "an hour",
        < 60 => $"{(int)_store.KeepFor.TotalMinutes} minutes",
        _ when _store.KeepFor.TotalMinutes % 60 == 0 => $"{(int)_store.KeepFor.TotalHours} hours",
        _ => $"{(int)_store.KeepFor.TotalMinutes} minutes",
    };

    /// <summary>True while something is being dragged out of the shelf, so the island doesn't offer to take it back.</summary>
    public static bool IsDraggingOut { get; private set; }

    protected override FrameworkElement CreateCompactView() => new ShelfCompactView();
    protected override FrameworkElement CreatePanelView() => new ShelfPanelView();
    protected override FrameworkElement? CreateBubbleView() => new ShelfBubbleView();

    public void Load()
    {
        _store.Load();
        Sync();
        _expiry.Start();
    }

    /// <summary>True when a drag holds something the shelf can keep.</summary>
    public static bool CanAccept(IDataObject data)
    {
        try
        {
            return AcceptedFormats.Any(f => data.GetDataPresent(f));
        }
        catch (COMException)
        {
            return false;
        }
    }

    /// <summary>
    /// Keeps what was dropped. The drag's data is only readable during the drop, so it is copied out
    /// first; saving and downloading happen afterwards. Returns how many items were added.
    /// </summary>
    public async Task<int> AddAsync(IDataObject data)
    {
        var drop = DropContents.Read(data);
        DebugWrite?.Invoke($"Shelf drop: {drop.Describe()}");

        _saving++;
        OnPropertyChanged(nameof(IsSaving));
        try
        {
            var items = await Task.Run(() => MakeItemsAsync(drop));
            if (items.Count > 0)
            {
                _store.Add(items);
                Sync();
            }

            DebugWrite?.Invoke($"Shelf kept {items.Count}: {string.Join(", ", items.Select(i => i.Name))}");
            return items.Count;
        }
        finally
        {
            _saving--;
            OnPropertyChanged(nameof(IsSaving));
        }
    }

    /// <summary>Optional developer trace (the shell passes its debug log).</summary>
    public static Action<string>? DebugWrite { get; set; }

    /// <summary>Starts dragging items out into another app (as files, or as text when they are all text).</summary>
    public void DragOut(DependencyObject source, IReadOnlyCollection<ShelfItemView> items)
    {
        if (items.Count == 0)
        {
            return;
        }

        var files = items.Where(i => i.Item.Kind == ShelfKind.File && i.Item.Path is not null && File.Exists(i.Item.Path))
            .Select(i => i.Item.Path!).ToList();
        DebugWrite?.Invoke($"Shelf drag out starting: {files.Count} file(s) of {items.Count}");

        IsDraggingOut = true;
        try
        {
            if (files.Count > 0)
            {
                // Files go as real paths or as virtual files, depending on the app they are dropped on.
                ShelfDragOut.Run(files, DebugWrite);
            }
            else
            {
                var data = new DataObject();
                data.SetText(string.Join(Environment.NewLine, items.Select(i => i.Item.Text ?? i.Item.Path)));
                var result = DragDrop.DoDragDrop(source, data, DragDropEffects.Copy);
                DebugWrite?.Invoke($"Shelf drag out of {items.Count} text: {result}");
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or InvalidOperationException or ArgumentException
                                       or IOException or UnauthorizedAccessException or OutOfMemoryException)
        {
            DebugWrite?.Invoke($"Shelf drag out failed: {ex.GetType().Name} {ex.Message}");
        }
        finally
        {
            IsDraggingOut = false;
        }
    }

    public void Remove(ShelfItemView item)
    {
        _store.Remove(item.Item.Id);
        Sync();
        if (Items.Count == 0)
        {
            RequestCollapse();
        }
    }

    // ------------------------------------------------------------------ Turning a drop into items

    private async Task<List<ShelfItem>> MakeItemsAsync(DropContents drop)
    {
        var items = new List<ShelfItem>();

        // Files from Explorer or the desktop: keep a reference, never a copy.
        foreach (var path in drop.Files)
        {
            if (File.Exists(path) || Directory.Exists(path))
            {
                items.Add(_store.NewFile(path, Path.GetFileName(path.TrimEnd('\\')), owned: false));
            }
        }

        if (items.Count > 0)
        {
            return items;
        }

        // A web image: the browser's own copy of it, its PNG, the image in the dragged HTML, or its link.
        if (drop.VirtualBytes is { Length: > 0 } bytes)
        {
            var ext = DropParsing.SniffExtension(bytes) ?? Path.GetExtension(drop.VirtualName ?? "");
            items.Add(SaveBytes(bytes, drop.VirtualName, ext));
            return items;
        }

        if (drop.Png is { Length: > 0 } png)
        {
            items.Add(SaveBytes(png, ImageName(drop), ".png"));
            return items;
        }

        var source = DropParsing.FirstImageSource(drop.Html);
        if (DropParsing.TryDecodeDataUri(source, out var inline, out var inlineExt))
        {
            items.Add(SaveBytes(inline, ImageName(drop), inlineExt));
            return items;
        }

        foreach (var url in new[] { source, DropParsing.LooksLikeImageUrl(drop.Url) ? drop.Url : null })
        {
            if (DropParsing.IsWebUrl(url) && await DownloadAsync(url!, drop.VirtualName) is { } saved)
            {
                items.Add(saved);
                return items;
            }
        }

        if (drop.Bitmap is { Length: > 0 } bitmap)
        {
            items.Add(SaveBytes(bitmap, ImageName(drop), ".png"));
            return items;
        }

        // Text, or a link that isn't an image.
        var text = drop.Text ?? drop.Url;
        if (!string.IsNullOrWhiteSpace(text))
        {
            items.Add(_store.NewText(text.Trim()));
        }

        return items;
    }

    private static string? ImageName(DropContents drop) =>
        drop.VirtualName ?? (drop.Url is { } url && DropParsing.LooksLikeImageUrl(url) ? DropParsing.NameFromUrl(url) : null) ?? "Image";

    private ShelfItem SaveBytes(byte[] bytes, string? name, string extension)
    {
        var fileName = DropParsing.SafeFileName(name ?? "Image", extension);
        var path = Path.Combine(_store.NewFolder(), fileName);
        File.WriteAllBytes(path, bytes);
        return _store.NewFile(path, fileName, owned: true);
    }

    /// <summary>Downloads an image the user dragged in. Only the URL they dragged; nothing else is sent.</summary>
    private async Task<ShelfItem?> DownloadAsync(string url, string? name)
    {
        try
        {
            using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaxDownloadBytes)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync();
            using var memory = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(buffer)) > 0)
            {
                memory.Write(buffer, 0, read);
                if (memory.Length > MaxDownloadBytes)
                {
                    return null;
                }
            }

            var bytes = memory.ToArray();
            var ext = DropParsing.SniffExtension(bytes) ?? DropParsing.ExtensionFor(response.Content.Headers.ContentType?.MediaType);
            if (ext is null)
            {
                return null; // not an image after all
            }

            return SaveBytes(bytes, name ?? DropParsing.NameFromUrl(url), ext);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or InvalidOperationException or UriFormatException)
        {
            DebugWrite?.Invoke($"Shelf download failed: {ex.GetType().Name}");
            return null;
        }
    }

    private static HttpClient CreateHttp()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) Nexus/0.1");
        return client;
    }

    // ------------------------------------------------------------------ Views

    /// <summary>Rebuilds the item views from the store, reusing the ones that stayed (and their thumbnails).</summary>
    private void Sync()
    {
        var existing = Items.ToDictionary(i => i.Item.Id);
        var fresh = _store.Items.Select(i => existing.TryGetValue(i.Id, out var view) ? view : new ShelfItemView(i, this)).ToList();
        Items.Clear();
        foreach (var view in fresh)
        {
            Items.Add(view);
        }

        OnAllPropertiesChanged();
        UpdateActivity();
    }

    private void UpdateActivity()
    {
        if (Items.Count == 0)
        {
            SetActivity(null);
            return;
        }

        SetActivity(new Activity(Id, Priority.Ambient, Items[0].Item.AddedAt)
        {
            Reason = $"You dropped {CountText} on the island. They stay for {KeepText}, ready to drag into another app.",
        });
    }
}

/// <summary>One item on the shelf, as the views show it.</summary>
public sealed class ShelfItemView : ObservableObject
{
    private readonly ShelfMode _shelf;
    private ImageSource? _thumbnail;
    private bool _thumbnailLoaded;

    public ShelfItemView(ShelfItem item, ShelfMode shelf)
    {
        Item = item;
        _shelf = shelf;
        RemoveCommand = new RelayCommand(() => _shelf.Remove(this));
    }

    public ShelfItem Item { get; }
    public ShelfMode Shelf => _shelf;
    public string Name => Item.Name;
    public bool IsText => Item.Kind == ShelfKind.Text;
    public bool IsImage => Item.Kind == ShelfKind.File && DropParsing.IsImagePath(Item.Path);
    public string ToolTipText => IsText ? Item.Text ?? "" : Item.Path ?? Item.Name;
    public ICommand RemoveCommand { get; }

    /// <summary>A small picture of the image, or the file's icon. Loaded once, on first use.</summary>
    public ImageSource? Thumbnail
    {
        get
        {
            if (!_thumbnailLoaded)
            {
                _thumbnailLoaded = true;
                _thumbnail = IsText ? null : IsImage ? LoadImage(Item.Path!) ?? FileIcon(Item.Path!) : FileIcon(Item.Path!);
            }

            return _thumbnail;
        }
    }

    public bool HasThumbnail => Thumbnail is not null;

    private static ImageSource? LoadImage(string path)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad; // don't keep the file open
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.DecodePixelHeight = 128;
            image.UriSource = new Uri(path);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UnauthorizedAccessException or ArgumentException or FileFormatException or COMException)
        {
            return null;
        }
    }

    /// <summary>The icon Windows shows for this file or folder.</summary>
    private static ImageSource? FileIcon(string path)
    {
        var info = new SHFILEINFO();
        if (SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_ICON | SHGFI_LARGEICON) == IntPtr.Zero || info.hIcon == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var icon = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            icon.Freeze();
            return icon;
        }
        catch (Exception ex) when (ex is COMException or ArgumentException)
        {
            return null;
        }
        finally
        {
            DestroyIcon(info.hIcon);
        }
    }

    private const uint SHGFI_ICON = 0x100;
    private const uint SHGFI_LARGEICON = 0x0;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string path, uint attributes, ref SHFILEINFO info, uint size, uint flags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);
}

/// <summary>What a drop held, copied out while the drag's data is still readable.</summary>
internal sealed class DropContents
{
    public string[] Files { get; private init; } = [];
    public byte[]? VirtualBytes { get; private init; }
    public string? VirtualName { get; private init; }
    public byte[]? Png { get; private init; }
    public byte[]? Bitmap { get; private init; }
    public string? Html { get; private init; }
    public string? Url { get; private init; }
    public string? Text { get; private init; }
    public string[] Formats { get; private init; } = [];

    public static DropContents Read(IDataObject data)
    {
        var descriptor = Bytes(data, "FileGroupDescriptorW");
        var virtualName = descriptor is null ? null : DropParsing.FirstVirtualFileName(descriptor);
        return new DropContents
        {
            Formats = Safe(() => data.GetFormats(false)) ?? [],
            Files = Safe(() => data.GetData(DataFormats.FileDrop) as string[]) ?? [],
            VirtualName = virtualName,
            VirtualBytes = virtualName is null ? null : VirtualFile(data, 0) ?? Bytes(data, "FileContents"),
            Png = Bytes(data, "PNG"),
            Bitmap = Safe(() => data.GetData(DataFormats.Bitmap) is BitmapSource b ? EncodePng(b) : null),
            Html = Safe(() => data.GetData(DataFormats.Html) as string),
            Url = Utf16(Bytes(data, "UniformResourceLocatorW")),
            Text = Safe(() => data.GetData(DataFormats.UnicodeText) as string ?? data.GetData(DataFormats.Text) as string),
        };
    }

    public string Describe() =>
        $"formats [{string.Join(", ", Formats)}] files={Files.Length} virtual={VirtualName}({VirtualBytes?.Length}) png={Png?.Length} " +
        $"bitmap={Bitmap?.Length} html={Html?.Length} url={(Url is null ? "none" : "yes")} text={Text?.Length}";

    /// <summary>
    /// The bytes of virtual file number <paramref name="index"/>. Browsers only hand these out per file
    /// (lindex 0, 1…); WPF's own GetData asks for "all of them" (lindex -1) and gets nothing, so ask OLE directly.
    /// </summary>
    private static byte[]? VirtualFile(IDataObject data, int index) => Safe(() =>
    {
        if (data is not System.Runtime.InteropServices.ComTypes.IDataObject com)
        {
            return null;
        }

        var format = new System.Runtime.InteropServices.ComTypes.FORMATETC
        {
            cfFormat = (short)DataFormats.GetDataFormat("FileContents").Id,
            dwAspect = System.Runtime.InteropServices.ComTypes.DVASPECT.DVASPECT_CONTENT,
            lindex = index,
            tymed = System.Runtime.InteropServices.ComTypes.TYMED.TYMED_ISTREAM | System.Runtime.InteropServices.ComTypes.TYMED.TYMED_HGLOBAL,
        };
        com.GetData(ref format, out var medium);
        try
        {
            return medium.tymed switch
            {
                System.Runtime.InteropServices.ComTypes.TYMED.TYMED_HGLOBAL => Native.FromHGlobal(medium.unionmember),
                System.Runtime.InteropServices.ComTypes.TYMED.TYMED_ISTREAM =>
                    Native.FromStream((System.Runtime.InteropServices.ComTypes.IStream)Marshal.GetObjectForIUnknown(medium.unionmember)),
                _ => null,
            };
        }
        finally
        {
            Native.ReleaseStgMedium(ref medium);
        }
    });

    private static byte[]? Bytes(IDataObject data, string format) => Safe(() =>
    {
        if (!data.GetDataPresent(format))
        {
            return null;
        }

        return data.GetData(format) switch
        {
            MemoryStream m => m.ToArray(),
            Stream s => Copy(s),
            byte[] b => b,
            _ => null,
        };
    });

    private static byte[] Copy(Stream s)
    {
        using var m = new MemoryStream();
        s.CopyTo(m);
        return m.ToArray();
    }

    private static string? Utf16(byte[]? bytes)
    {
        if (bytes is null)
        {
            return null;
        }

        var text = Encoding.Unicode.GetString(bytes);
        var end = text.IndexOf('\0');
        return end >= 0 ? text[..end] : text;
    }

    private static byte[] EncodePng(BitmapSource bitmap)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var m = new MemoryStream();
        encoder.Save(m);
        return m.ToArray();
    }

    /// <summary>Apps put all sorts in a drag; one unreadable format must not lose the rest.</summary>
    private static T? Safe<T>(Func<T?> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is COMException or OutOfMemoryException or ArgumentException or InvalidOperationException
                                       or NotSupportedException or IOException or ExternalException)
        {
            return default;
        }
    }
}
