using System.Text;
using Nexus.Core.Shelf;

namespace Nexus.Core.Tests;

public sealed class ShelfTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "nexus-shelf-test-" + Guid.NewGuid().ToString("N"));
    private DateTimeOffset _now = new(2026, 11, 11, 10, 0, 0, TimeSpan.Zero);

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private ShelfStore Store(int max = 20) => new(_folder, TimeSpan.FromHours(1), max, () => _now);

    private string OwnedFile(ShelfStore store, string name = "photo.png")
    {
        var path = Path.Combine(store.NewFolder(), name);
        File.WriteAllBytes(path, [1, 2, 3]);
        return path;
    }

    [Fact]
    public void Newest_items_come_first()
    {
        var store = Store();
        store.Add([store.NewText("first")]);
        store.Add([store.NewText("second")]);

        Assert.Equal(["second", "first"], store.Items.Select(i => i.Text));
    }

    [Fact]
    public void Dropping_the_same_thing_again_moves_it_to_the_front()
    {
        var store = Store();
        store.Add([store.NewText("a")]);
        store.Add([store.NewText("b")]);
        store.Add([store.NewText("a")]);

        Assert.Equal(["a", "b"], store.Items.Select(i => i.Text));
    }

    [Fact]
    public void Items_expire_after_the_keep_time_and_saved_files_go_with_them()
    {
        var store = Store();
        var path = OwnedFile(store);
        store.Add([store.NewFile(path, "photo.png", owned: true)]);

        _now += TimeSpan.FromMinutes(59);
        Assert.False(store.Expire());

        _now += TimeSpan.FromMinutes(2);
        Assert.True(store.Expire());
        Assert.Empty(store.Items);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Files_you_dropped_from_disk_are_never_deleted()
    {
        var store = Store();
        Directory.CreateDirectory(_folder);
        var yours = Path.Combine(Path.GetTempPath(), "nexus-shelf-yours-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(yours, "keep me");
        try
        {
            store.Add([store.NewFile(yours, "notes.txt", owned: false)]);
            store.Clear();
            Assert.True(File.Exists(yours));
        }
        finally
        {
            File.Delete(yours);
        }
    }

    [Fact]
    public void An_owned_path_outside_the_shelf_folder_is_never_deleted()
    {
        var store = Store();
        var outside = Path.Combine(Path.GetTempPath(), "nexus-shelf-outside-" + Guid.NewGuid().ToString("N") + ".png");
        File.WriteAllBytes(outside, [1]);
        try
        {
            store.Add([store.NewFile(outside, "x.png", owned: true)]);
            store.Clear();
            Assert.True(File.Exists(outside));
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [Fact]
    public void Keeps_at_most_the_limit_dropping_the_oldest()
    {
        var store = Store(max: 3);
        foreach (var t in new[] { "1", "2", "3", "4" })
        {
            store.Add([store.NewText(t)]);
        }

        Assert.Equal(["4", "3", "2"], store.Items.Select(i => i.Text));
    }

    [Fact]
    public void The_shelf_survives_a_restart_and_tidies_stray_files()
    {
        var store = Store();
        var kept = OwnedFile(store, "kept.png");
        var stray = OwnedFile(store, "stray.png");
        store.Add([store.NewFile(kept, "kept.png", owned: true), store.NewText("hello")]);

        var again = Store();
        again.Load();

        Assert.Equal(2, again.Items.Count);
        Assert.True(File.Exists(kept));
        Assert.False(File.Exists(stray));
    }

    [Fact]
    public void A_file_that_disappeared_leaves_the_shelf()
    {
        var store = Store();
        var path = OwnedFile(store);
        store.Add([store.NewFile(path, "photo.png", owned: true)]);
        File.Delete(path);

        Assert.True(store.Expire());
        Assert.Empty(store.Items);
    }

    // ------------------------------------------------------------ Reading drags

    [Fact]
    public void Reads_the_name_of_a_browser_virtual_file()
    {
        var descriptor = new byte[4 + 592];
        BitConverter.GetBytes(1u).CopyTo(descriptor, 0);
        Encoding.Unicode.GetBytes("cute-cat.jpg").CopyTo(descriptor, 4 + 72);

        Assert.Equal("cute-cat.jpg", DropParsing.FirstVirtualFileName(descriptor));
    }

    [Fact]
    public void No_virtual_file_means_no_name() =>
        Assert.Null(DropParsing.FirstVirtualFileName(new byte[600]));

    [Theory]
    [InlineData("<img src=\"https://example.com/a.png\" alt=x>", "https://example.com/a.png")]
    [InlineData("<html><body><!--StartFragment--><IMG class='x' SRC='data:image/png;base64,AAAA'>", "data:image/png;base64,AAAA")]
    [InlineData("<img alt=\"x\" src=https://e.com/b.jpg?w=1&amp;h=2>", "https://e.com/b.jpg?w=1&h=2")]
    [InlineData("<p>no image</p>", null)]
    public void Finds_the_image_in_dragged_html(string html, string? expected) =>
        Assert.Equal(expected, DropParsing.FirstImageSource(html));

    [Fact]
    public void Decodes_a_google_style_data_uri()
    {
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        Assert.True(DropParsing.TryDecodeDataUri("data:image/png;base64," + Convert.ToBase64String(png), out var bytes, out var ext));
        Assert.Equal(png, bytes);
        Assert.Equal(".png", ext);
    }

    [Fact]
    public void Works_out_the_type_of_an_unlabelled_image()
    {
        var jpeg = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0, 0 };
        Assert.True(DropParsing.TryDecodeDataUri("data:;base64," + Convert.ToBase64String(jpeg), out _, out var ext));
        Assert.Equal(".jpg", ext);
    }

    [Theory]
    [InlineData("a:b*c?.png", "", "a_b_c_.png")]
    [InlineData("photo", ".jpg", "photo.jpg")]
    [InlineData("", ".png", "Dropped item.png")]
    [InlineData("report.pdf", ".png", "report.pdf")]
    public void Makes_safe_file_names(string name, string ext, string expected) =>
        Assert.Equal(expected, DropParsing.SafeFileName(name, ext));

    [Theory]
    [InlineData("https://images.example.com/cats/tabby%20cat.webp?size=large", "tabby cat.webp")]
    [InlineData("https://example.com/", "Image")]
    public void Names_downloads_after_the_url(string url, string expected) =>
        Assert.Equal(expected, DropParsing.NameFromUrl(url));

    [Theory]
    [InlineData("https://e.com/a/b.JPG", true)]
    [InlineData("https://e.com/page", false)]
    [InlineData("C:\\a.png", false)]
    public void Spots_links_to_images(string url, bool expected) => Assert.Equal(expected, DropParsing.LooksLikeImageUrl(url));

    [Fact]
    public void Shortens_long_text_for_the_tile()
    {
        Assert.Equal("Short note", ShelfStore.Snippet("  Short note \nsecond line"));
        Assert.EndsWith("…", ShelfStore.Snippet(new string('x', 80)));
    }
}
