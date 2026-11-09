using Nexus.Core.Settings;

namespace Nexus.Core.Tests;

public class ShortcutTests
{
    [Theory]
    [InlineData("Ctrl+Alt+Space", true, true, false, false, 0x20u)]
    [InlineData("ctrl + alt + f", true, true, false, false, 0x46u)]
    [InlineData("Win+Shift+N", false, false, true, true, 0x4Eu)]
    [InlineData("Alt+F9", false, true, false, false, 0x78u)]
    public void Parses_common_shortcuts(string text, bool ctrl, bool alt, bool shift, bool win, uint vk)
    {
        var s = Shortcut.Parse(text)!;
        Assert.Equal((ctrl, alt, shift, win, vk), (s.Ctrl, s.Alt, s.Shift, s.Win, s.VirtualKey));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Space")]          // no modifier would swallow typing
    [InlineData("Shift+A")]        // shift alone is not enough
    [InlineData("Ctrl+Alt+A+B")]   // two keys
    [InlineData("Ctrl+Alt+Banana")]
    public void Rejects_unusable_shortcuts(string text) => Assert.Null(Shortcut.Parse(text));
}
