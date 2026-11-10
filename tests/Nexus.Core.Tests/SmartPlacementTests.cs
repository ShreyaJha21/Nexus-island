using System.Text.Json;
using System.Text.Json.Serialization;
using Nexus.Core.Positioning;
using Nexus.Core.Settings;

namespace Nexus.Core.Tests;

public class SmartPlacementTests
{
    private static readonly Placement[] Spots = [Placement.BottomRight, Placement.BottomLeft, Placement.Taskbar];

    [Fact]
    public void Stays_at_the_preferred_spot_when_it_is_clear()
    {
        var asked = new List<Placement>();
        var spot = SmartPlacement.Choose(Placement.AboveTaskbar, Spots, s => { asked.Add(s); return false; });

        Assert.Equal(Placement.AboveTaskbar, spot);
        Assert.Equal([Placement.AboveTaskbar], asked); // the slow check ran once, not for every spot
    }

    [Fact]
    public void Moves_to_the_first_clear_spot_in_order()
    {
        var blocked = new HashSet<Placement> { Placement.AboveTaskbar, Placement.BottomRight };
        Assert.Equal(Placement.BottomLeft, SmartPlacement.Choose(Placement.AboveTaskbar, Spots, blocked.Contains));
    }

    [Fact]
    public void Returns_null_when_nowhere_is_clear() =>
        Assert.Null(SmartPlacement.Choose(Placement.AboveTaskbar, Spots, _ => true));

    [Fact]
    public void Never_moves_to_custom_and_never_asks_twice()
    {
        var asked = new List<Placement>();
        Placement[] spots = [Placement.Custom, Placement.AboveTaskbar, Placement.BottomLeft, Placement.BottomLeft];
        var spot = SmartPlacement.Choose(Placement.AboveTaskbar, spots, s => { asked.Add(s); return true; });

        Assert.Null(spot);
        Assert.Equal([Placement.AboveTaskbar, Placement.BottomLeft], asked);
    }

    [Theory]
    [InlineData("Figma", "Nexus-island – Figma", true)]
    [InlineData("figma", "", true)]                                              // process name, any case
    [InlineData("chrome", "Nexus-island – Figma - Google Chrome", true)]         // Figma in a browser tab
    [InlineData("chrome", "Figma tips for beginners - YouTube - Google Chrome", false)]
    [InlineData("chrome", "Figma", false)]                                       // a page merely titled Figma
    [InlineData("Code", "IslandWindow.xaml.cs - Nexus-island - Visual Studio Code", false)]
    public void Finds_the_rule_for_the_app_in_front(string process, string title, bool expected)
    {
        var rules = new PositioningSettings().Apps;
        Assert.Equal(expected, SmartPlacement.FindRule(rules, process, title) is not null);
    }

    [Theory]
    [InlineData("brave", true)]
    [InlineData("Brave", true)]
    [InlineData("msedge", true)]
    [InlineData("chrome", true)]
    [InlineData("firefox", true)]
    [InlineData("Figma", false)]
    [InlineData("Code", false)]
    public void Knows_browsers(string process, bool expected) => Assert.Equal(expected, SmartPlacement.IsBrowser(process));

    [Fact]
    public void A_youtube_tab_in_brave_has_no_rule_so_the_browser_setting_applies() =>
        Assert.Null(SmartPlacement.FindRule(new PositioningSettings().Apps, "brave", "(20) Jhol | Coke Studio - YouTube - Brave"));

    [Fact]
    public void Figma_is_known_out_of_the_box()
    {
        var rule = SmartPlacement.FindRule(new PositioningSettings().Apps, "Figma", "")!;
        Assert.Contains(Placement.AboveTaskbar, rule.Avoid);
        Assert.Equal(Placement.BottomRight, rule.MoveTo);
    }

    [Theory]
    [InlineData("09:30-12:00", "09:30", true)]
    [InlineData("09:30-12:00", "11:59", true)]
    [InlineData("09:30-12:00", "12:00", false)]
    [InlineData("22:00-06:00", "23:30", true)]   // across midnight
    [InlineData("22:00-06:00", "05:00", true)]
    [InlineData("22:00-06:00", "12:00", false)]
    [InlineData("9:00-10:00", "09:15", true)]
    [InlineData("lunch", "12:30", false)]        // nonsense is ignored
    public void Knows_compact_hours(string range, string now, bool expected) =>
        Assert.Equal(expected, SmartPlacement.IsCompactTime([range], TimeOnly.Parse(now)));

    [Fact]
    public void Old_settings_files_still_load_and_get_the_new_defaults()
    {
        var options = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } };
        var old = """{ "Placement": "TopCenter", "DockOverApps": true, "HideShortcut": "Ctrl+Alt+J" }""";

        var settings = JsonSerializer.Deserialize<NexusSettings>(old, options)!;

        Assert.Equal(Placement.TopCenter, settings.Placement);
        Assert.Equal(PositionMode.Smart, settings.Positioning.Mode);
        Assert.True(settings.Positioning.Draggable);
        Assert.True(settings.Positioning.StayInBrowsers);
        Assert.Contains("Figma", settings.Positioning.Apps.Keys);
    }

    [Fact]
    public void Edited_lists_replace_the_defaults_instead_of_adding_to_them()
    {
        var options = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } };
        var json = """{ "Positioning": { "Mode": "AutoHide", "SafeSpots": ["Taskbar"], "Apps": {} } }""";

        var settings = JsonSerializer.Deserialize<NexusSettings>(json, options)!;

        Assert.Equal(PositionMode.AutoHide, settings.Positioning.Mode);
        Assert.Equal([Placement.Taskbar], settings.Positioning.SafeSpots);
        Assert.Empty(settings.Positioning.Apps);
    }
}
