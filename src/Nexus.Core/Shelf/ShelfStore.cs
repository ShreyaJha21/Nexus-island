using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nexus.Core.Shelf;

public enum ShelfKind
{
    /// <summary>A file on disk: one you dropped from Explorer, or a web image Nexus saved for you.</summary>
    File,
    /// <summary>Text or a link.</summary>
    Text,
}

/// <summary>Something kept on the island's shelf.</summary>
public sealed record ShelfItem
{
    public required string Id { get; init; }
    public ShelfKind Kind { get; init; }

    /// <summary>The file, for <see cref="ShelfKind.File"/>.</summary>
    public string? Path { get; init; }

    /// <summary>The text or link, for <see cref="ShelfKind.Text"/>.</summary>
    public string? Text { get; init; }

    /// <summary>What the island calls it: the file name, or the start of the text.</summary>
    public string Name { get; init; } = "";

    /// <summary>True when the file lives in Nexus's own shelf folder (a saved web image) and is deleted with the item.</summary>
    public bool Owned { get; init; }

    public DateTimeOffset AddedAt { get; init; }
    public DateTimeOffset ExpiresAt { get; init; }
}

/// <summary>
/// The shelf's contents: newest first, at most <see cref="MaxItems"/>, each kept until it expires.
/// Saved as JSON next to the saved files, so the shelf survives a restart. Files Nexus saved itself
/// (web images) are deleted when their item goes; files you dropped from disk are never touched.
/// </summary>
public sealed class ShelfStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly List<ShelfItem> _items = [];
    private readonly Func<DateTimeOffset> _now;

    public ShelfStore(string folder, TimeSpan keepFor, int maxItems = 20, Func<DateTimeOffset>? now = null)
    {
        Folder = folder;
        KeepFor = keepFor;
        MaxItems = Math.Max(1, maxItems);
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>Where saved files and the index live.</summary>
    public string Folder { get; }

    public TimeSpan KeepFor { get; set; }
    public int MaxItems { get; }

    public IReadOnlyList<ShelfItem> Items => _items;

    private string IndexPath => System.IO.Path.Combine(Folder, "shelf.json");

    /// <summary>A new item, ready to <see cref="Add"/>: its id, and its expiry from now.</summary>
    public ShelfItem NewFile(string path, string name, bool owned) => Stamp(new ShelfItem
    {
        Id = NewId(),
        Kind = ShelfKind.File,
        Path = path,
        Name = name,
        Owned = owned,
    });

    public ShelfItem NewText(string text) => Stamp(new ShelfItem
    {
        Id = NewId(),
        Kind = ShelfKind.Text,
        Text = text,
        Name = Snippet(text),
    });

    /// <summary>Adds items at the front. The same file or text dropped again moves to the front instead of doubling up.</summary>
    public void Add(IEnumerable<ShelfItem> items)
    {
        foreach (var item in items.Reverse())
        {
            var duplicate = _items.FirstOrDefault(i =>
                i.Kind == item.Kind &&
                (item.Kind == ShelfKind.File
                    ? string.Equals(i.Path, item.Path, StringComparison.OrdinalIgnoreCase)
                    : i.Text == item.Text));
            if (duplicate is not null)
            {
                _items.Remove(duplicate);
            }

            _items.Insert(0, item);
        }

        while (_items.Count > MaxItems)
        {
            Discard(_items[^1]);
        }

        Save();
    }

    public void Remove(string id)
    {
        if (_items.FirstOrDefault(i => i.Id == id) is { } item)
        {
            Discard(item);
            Save();
        }
    }

    public void Clear()
    {
        foreach (var item in _items.ToList())
        {
            Discard(item);
        }

        Save();
    }

    /// <summary>Drops expired items, and items whose file has disappeared. Returns true when anything went.</summary>
    public bool Expire()
    {
        var now = _now();
        var gone = _items.Where(i => i.ExpiresAt <= now ||
            (i.Kind == ShelfKind.File && !File.Exists(i.Path) && !Directory.Exists(i.Path))).ToList();
        foreach (var item in gone)
        {
            Discard(item);
        }

        if (gone.Count > 0)
        {
            Save();
        }

        return gone.Count > 0;
    }

    /// <summary>When the next item expires, or null when the shelf is empty.</summary>
    public DateTimeOffset? NextExpiry => _items.Count == 0 ? null : _items.Min(i => i.ExpiresAt);

    /// <summary>Loads the saved shelf, drops whatever expired meanwhile, and tidies files no item refers to.</summary>
    public void Load()
    {
        _items.Clear();
        try
        {
            if (File.Exists(IndexPath))
            {
                _items.AddRange(JsonSerializer.Deserialize<List<ShelfItem>>(File.ReadAllText(IndexPath), Options) ?? []);
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _items.Clear(); // a broken index never stops the island; saved files are tidied below
        }

        Expire();
        TidyFolder();
    }

    /// <summary>A folder inside the shelf for a file Nexus saves (one per item, so names never clash).</summary>
    public string NewFolder()
    {
        var folder = System.IO.Path.Combine(Folder, NewId());
        Directory.CreateDirectory(folder);
        return folder;
    }

    private ShelfItem Stamp(ShelfItem item)
    {
        var now = _now();
        return item with { AddedAt = now, ExpiresAt = now + KeepFor };
    }

    private void Discard(ShelfItem item)
    {
        _items.Remove(item);
        if (item.Owned && item.Path is { } path)
        {
            DeleteOwned(path);
        }
    }

    /// <summary>Deletes a file Nexus saved, and its folder, but only inside the shelf folder.</summary>
    private void DeleteOwned(string path)
    {
        try
        {
            var full = System.IO.Path.GetFullPath(path);
            var root = System.IO.Path.GetFullPath(Folder) + System.IO.Path.DirectorySeparatorChar;
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (File.Exists(full))
            {
                File.Delete(full);
            }

            var dir = System.IO.Path.GetDirectoryName(full);
            if (dir is not null && dir.Length > root.Length && Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
            {
                Directory.Delete(dir);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // In use (an app is still reading it): it goes the next time the shelf is tidied.
        }
    }

    /// <summary>Removes saved files that no item refers to any more (after a crash, or a delete that failed).</summary>
    private void TidyFolder()
    {
        if (!Directory.Exists(Folder))
        {
            return;
        }

        var kept = _items.Where(i => i.Owned && i.Path is not null)
            .Select(i => System.IO.Path.GetFullPath(i.Path!))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in Directory.EnumerateDirectories(Folder))
        {
            foreach (var file in Directory.EnumerateFiles(dir))
            {
                if (!kept.Contains(System.IO.Path.GetFullPath(file)))
                {
                    DeleteOwned(file);
                }
            }

            try
            {
                if (!Directory.EnumerateFileSystemEntries(dir).Any())
                {
                    Directory.Delete(dir);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var temp = IndexPath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_items, Options));
            File.Move(temp, IndexPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static string NewId() => Guid.NewGuid().ToString("N")[..12];

    /// <summary>The first line of the text, shortened to fit a tile.</summary>
    public static string Snippet(string text)
    {
        var line = text.Trim().Split('\n', 2)[0].Trim();
        return line.Length <= 40 ? line : line[..39].TrimEnd() + "…";
    }
}
