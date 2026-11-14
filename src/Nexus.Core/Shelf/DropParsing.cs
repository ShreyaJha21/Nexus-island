using System.Text;
using System.Text.RegularExpressions;

namespace Nexus.Core.Shelf;

/// <summary>
/// Reading what other apps put in a drag. Browsers don't hand over a dragged web image as a file on
/// disk: Chromium (Chrome, Edge, Brave) offers a "virtual file" (a name in FileGroupDescriptorW and
/// the bytes in FileContents), and every browser offers the page's HTML for the image and its URL.
/// These helpers turn those into a file name and bytes, without any Windows calls.
/// </summary>
public static partial class DropParsing
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".avif", ".svg", ".ico", ".tif", ".tiff", ".heic",
    };

    /// <summary>True for files the island can show a picture of.</summary>
    public static bool IsImagePath(string? path) =>
        path is not null && ImageExtensions.Contains(Path.GetExtension(path));

    /// <summary>
    /// The first file name in a FILEGROUPDESCRIPTORW: a 4-byte count, then 592-byte descriptors whose
    /// UTF-16 name starts 72 bytes in. Null when there is none.
    /// </summary>
    public static string? FirstVirtualFileName(byte[] descriptor)
    {
        const int countSize = 4, nameOffset = 72, nameBytes = 520;
        if (descriptor.Length < countSize + nameOffset + 2 || BitConverter.ToUInt32(descriptor, 0) == 0)
        {
            return null;
        }

        var start = countSize + nameOffset;
        var length = Math.Min(nameBytes, descriptor.Length - start);
        var name = Encoding.Unicode.GetString(descriptor, start, length);
        var end = name.IndexOf('\0');
        name = end >= 0 ? name[..end] : name;
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    /// <summary>The src of the first image in a piece of HTML (as browsers put it in "HTML Format").</summary>
    public static string? FirstImageSource(string? html)
    {
        if (string.IsNullOrEmpty(html))
        {
            return null;
        }

        var match = ImgSrc().Match(html);
        return match.Success ? System.Net.WebUtility.HtmlDecode(match.Groups["src"].Value) : null;
    }

    /// <summary>Decodes a base64 data: URI (Google Images thumbnails are these). Returns the bytes and a file extension.</summary>
    public static bool TryDecodeDataUri(string? uri, out byte[] bytes, out string extension)
    {
        bytes = [];
        extension = "";
        if (uri is null || !uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var comma = uri.IndexOf(',');
        if (comma < 0)
        {
            return false;
        }

        var header = uri[5..comma];
        if (!header.EndsWith(";base64", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            bytes = Convert.FromBase64String(uri[(comma + 1)..].Trim());
        }
        catch (FormatException)
        {
            return false;
        }

        extension = ExtensionFor(header[..^";base64".Length]) ?? SniffExtension(bytes) ?? ".bin";
        return bytes.Length > 0;
    }

    /// <summary>File extension for a content type such as "image/jpeg".</summary>
    public static string? ExtensionFor(string? contentType) => contentType?.Split(';')[0].Trim().ToLowerInvariant() switch
    {
        "image/png" => ".png",
        "image/jpeg" or "image/jpg" or "image/pjpeg" => ".jpg",
        "image/gif" => ".gif",
        "image/webp" => ".webp",
        "image/bmp" => ".bmp",
        "image/avif" => ".avif",
        "image/svg+xml" => ".svg",
        "image/x-icon" or "image/vnd.microsoft.icon" => ".ico",
        "image/tiff" => ".tif",
        _ => null,
    };

    /// <summary>Recognises common image formats from their first bytes.</summary>
    public static string? SniffExtension(ReadOnlySpan<byte> b)
    {
        if (b.Length >= 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) return ".png";
        if (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return ".jpg";
        if (b.Length >= 6 && b[0] == 'G' && b[1] == 'I' && b[2] == 'F') return ".gif";
        if (b.Length >= 12 && b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F' && b[8] == 'W' && b[9] == 'E' && b[10] == 'B' && b[11] == 'P') return ".webp";
        if (b.Length >= 2 && b[0] == 'B' && b[1] == 'M') return ".bmp";
        if (b.Length >= 12 && b[4] == 'f' && b[5] == 't' && b[6] == 'y' && b[7] == 'p' && b[8] == 'a' && b[9] == 'v' && b[10] == 'i' && b[11] == 'f') return ".avif";
        return null;
    }

    /// <summary>A name that is safe on Windows, keeps its extension, and is never empty.</summary>
    public static string SafeFileName(string? name, string fallbackExtension = "")
    {
        var cleaned = string.Concat((name ?? "").Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim().TrimEnd('.');
        if (cleaned.Length == 0)
        {
            cleaned = "Dropped item";
        }

        if (Path.GetExtension(cleaned).Length == 0 && fallbackExtension.Length > 0)
        {
            cleaned += fallbackExtension;
        }

        if (cleaned.Length > 120)
        {
            var ext = Path.GetExtension(cleaned);
            cleaned = cleaned[..(120 - ext.Length)] + ext;
        }

        return cleaned;
    }

    /// <summary>A file name for an image URL: the last part of its path, or "Image" when it has none.</summary>
    public static string NameFromUrl(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var last = Uri.UnescapeDataString(uri.Segments.LastOrDefault() ?? "").Trim('/');
            if (last.Length > 0)
            {
                return last;
            }
        }

        return "Image";
    }

    /// <summary>True for http(s) links.</summary>
    public static bool IsWebUrl(string? text) =>
        Uri.TryCreate(text?.Trim(), UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    /// <summary>True when a link points straight at an image file.</summary>
    public static bool LooksLikeImageUrl(string? url) =>
        IsWebUrl(url) && IsImagePath(new Uri(url!.Trim()).AbsolutePath);

    [GeneratedRegex("""<img\b[^>]*?\bsrc\s*=\s*(?:"(?<src>[^"]*)"|'(?<src>[^']*)'|(?<src>[^\s>]+))""", RegexOptions.IgnoreCase)]
    private static partial Regex ImgSrc();
}
