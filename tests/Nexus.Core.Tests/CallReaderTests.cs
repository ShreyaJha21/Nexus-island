using Nexus.Core.Calls;

namespace Nexus.Core.Tests;

public class CallReaderTests
{
    private static CallUiItem B(string name) => new(true, name);
    private static CallUiItem T(string name) => new(false, name);

    [Fact]
    public void Reads_an_incoming_voice_call()
    {
        var call = CallReader.Read([T("Priya Sharma"), T("Incoming voice call."), B("Decline"), B("Accept")], "WhatsApp", micInUse: false)!;

        Assert.Equal(CallState.Incoming, call.State);
        Assert.Equal("Priya Sharma", call.Caller);
        Assert.False(call.IsVideo);
        Assert.True(call.CanAnswer);
    }

    [Fact]
    public void Reads_an_incoming_video_call_from_a_notification()
    {
        var call = CallReader.Read([T("WhatsApp"), T("Arjun Kapoor"), T("Incoming video call."), B("Accept"), B("Decline")], null, false)!;

        Assert.Equal(CallState.Incoming, call.State);
        Assert.Equal("Arjun Kapoor", call.Caller);
        Assert.True(call.IsVideo);
    }

    [Fact]
    public void Reads_an_ongoing_call_with_its_timer_and_mute_state()
    {
        var call = CallReader.Read([T("Priya Sharma"), T("12:48"), B("Mute microphone"), B("End call")], "Priya Sharma", true)!;

        Assert.Equal(CallState.Ongoing, call.State);
        Assert.Equal(TimeSpan.FromSeconds(12 * 60 + 48), call.Duration);
        Assert.False(call.IsMuted);
        Assert.True(call.CanMute);
        Assert.True(call.CanEnd);
    }

    [Fact]
    public void Knows_when_the_microphone_is_muted() =>
        Assert.True(CallReader.Read([T("Priya"), B("Unmute microphone"), B("End call")], null, true)!.IsMuted);

    [Fact]
    public void Reads_hours_long_calls() =>
        Assert.Equal(new TimeSpan(1, 2, 5), CallReader.Read([T("1:02:05"), B("End call")], "Mum", true)!.Duration);

    [Theory]
    [InlineData("Calling...")]
    [InlineData("Ringing…")]
    [InlineData("Ringing")]
    public void Reads_an_outgoing_call_still_ringing(string status)
    {
        var call = CallReader.Read([T("Arjun Kapoor"), T(status), B("End call")], "WhatsApp", false)!;

        Assert.Equal(CallState.Outgoing, call.State);
        Assert.Equal("Arjun Kapoor", call.Caller);
        Assert.Null(call.Duration);
    }

    [Fact]
    public void A_microphone_in_use_alone_still_counts_as_a_call() =>
        Assert.Equal(CallState.Ongoing, CallReader.Read([], "WhatsApp", micInUse: true)!.State);

    [Fact]
    public void No_call_buttons_and_no_microphone_means_no_call() =>
        Assert.Null(CallReader.Read([T("Priya Sharma"), B("Send"), B("Attach")], "WhatsApp", false));

    [Theory]
    [InlineData("WhatsApp voice call", null)]
    [InlineData("End-to-end encrypted", null)]
    [InlineData("Ringing...", null)]
    [InlineData("0:42", null)]
    [InlineData("Priya Sharma", "Priya Sharma")]
    [InlineData("Mum ❤️", "Mum ❤️")]
    public void Picks_a_name_not_a_status_label(string text, string? expected) =>
        Assert.Equal(expected, CallReader.CallerName([text], "WhatsApp"));

    [Theory]
    [InlineData("Priya Sharma", "PS")]
    [InlineData("priya", "P")]
    [InlineData("Arjun Kumar Kapoor", "AK")]
    [InlineData("+91 98765 43210", "#")]
    [InlineData("", "")]
    public void Makes_initials_for_the_avatar(string name, string expected) => Assert.Equal(expected, CallReader.Initials(name));

    [Theory]
    [InlineData(7, "0:07")]
    [InlineData(768, "12:48")]
    [InlineData(3725, "1:02:05")]
    public void Formats_call_time(int seconds, string expected) =>
        Assert.Equal(expected, CallReader.FormatDuration(TimeSpan.FromSeconds(seconds)));
}
