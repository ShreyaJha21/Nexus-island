using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Nexus.Core.Agent;

namespace Nexus.Core.Tests;

public class AgentTrackerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 9, 15, 0, 0, TimeSpan.Zero);

    private static AgentEvent Ev(string name, string? tool = null, string? input = null, string? type = null, string? error = null) =>
        AgentEvent.Parse(new JsonObject
        {
            ["hook_event_name"] = name,
            ["session_id"] = "s1",
            ["cwd"] = @"D:\code\nexus-island",
            ["tool_name"] = tool,
            ["tool_input"] = input is null ? null : JsonNode.Parse(input),
            ["notification_type"] = type,
            ["message"] = type == "permission_prompt" ? "Claude needs your permission to use Bash" : null,
            ["error"] = error,
        }.ToJsonString())!;

    [Fact]
    public void Working_shows_tool_and_file_name_only()
    {
        var tracker = new AgentTracker();
        tracker.Apply(Ev("UserPromptSubmit"), T0);
        tracker.Apply(Ev("PreToolUse", "Edit", """{"file_path":"D:\\code\\nexus-island\\src\\auth.ts","old_string":"secret"}"""), T0);

        var s = tracker.VisibleSessions.Single();
        Assert.Equal(AgentState.Working, s.State);
        Assert.Equal("Editing auth.ts", s.Detail);
        Assert.Equal("nexus-island", s.Project);
        Assert.DoesNotContain("secret", s.Detail);
    }

    [Fact]
    public void Permission_request_needs_you_and_raises_once()
    {
        var tracker = new AgentTracker();
        var raised = 0;
        tracker.NeedsYouRaised += _ => raised++;

        tracker.Apply(Ev("UserPromptSubmit"), T0);
        tracker.Apply(Ev("PreToolUse", "Bash", """{"command":"npm test -- --watch=false"}"""), T0);
        tracker.Apply(Ev("PermissionRequest", "Bash", """{"command":"npm test -- --watch=false"}"""), T0);
        tracker.Apply(Ev("Notification", type: "permission_prompt"), T0.AddSeconds(6));

        var s = tracker.VisibleSessions.Single();
        Assert.Equal(AgentState.NeedsYou, s.State);
        Assert.Equal("npm test -- --watch=false", s.PendingCommand);
        Assert.Equal("Allow Bash?", s.Detail);
        Assert.Equal(1, raised);

        tracker.Apply(Ev("PostToolUse", "Bash"), T0.AddSeconds(10));
        Assert.Equal(AgentState.Working, s.State);
    }

    [Fact]
    public void Done_shows_duration_and_fades_after_10_seconds()
    {
        var tracker = new AgentTracker();
        tracker.Apply(Ev("UserPromptSubmit"), T0);
        tracker.Apply(Ev("Stop"), T0.AddMinutes(4));

        var s = tracker.VisibleSessions.Single();
        Assert.Equal("Done in 4 min", s.Detail);
        Assert.False(tracker.Expire(T0.AddMinutes(4).AddSeconds(9)));
        Assert.True(tracker.Expire(T0.AddMinutes(4).AddSeconds(10)));
        Assert.Empty(tracker.VisibleSessions);
        Assert.Same(s, tracker.LastFinished);
    }

    [Fact]
    public void Failure_stays_until_dismissed()
    {
        var tracker = new AgentTracker();
        tracker.Apply(Ev("UserPromptSubmit"), T0);
        tracker.Apply(Ev("StopFailure", error: "rate_limit"), T0.AddMinutes(1));

        Assert.False(tracker.Expire(T0.AddHours(1)));
        var s = tracker.VisibleSessions.Single();
        Assert.Equal(AgentState.Failed, s.State);
        Assert.Equal("Rate limited", s.Detail);

        tracker.Dismiss("s1");
        Assert.Empty(tracker.VisibleSessions);
    }

    [Fact]
    public void Snoozed_needs_you_comes_back()
    {
        var tracker = new AgentTracker();
        tracker.Apply(Ev("PermissionRequest", "Bash", """{"command":"rm -rf build"}"""), T0);
        tracker.Snooze("s1", TimeSpan.FromMinutes(5), T0);
        Assert.False(tracker.WakeSnoozed(T0.AddMinutes(4)));
        Assert.True(tracker.WakeSnoozed(T0.AddMinutes(5)));
    }

    [Fact]
    public void Unknown_events_and_fields_are_tolerated()
    {
        var e = AgentEvent.Parse("""{"hook_event_name":"SomethingNew","session_id":"x","brand_new_field":{"a":1}}""");
        Assert.NotNull(e);
        new AgentTracker().Apply(e!, T0);
        Assert.Null(AgentEvent.Parse("not json"));
        Assert.Null(AgentEvent.Parse("""{"session_id":"no event"}"""));
    }
}

public class HookInstallerTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("nexus-hooks-").FullName;
    private string Path => System.IO.Path.Combine(_dir, "settings.json");
    private const string Url = "http://127.0.0.1:50123/v1/events";

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Install_merges_without_touching_other_settings_and_uninstall_restores()
    {
        const string original = """
            {
              "model": "opus",
              "hooks": {
                "PreToolUse": [ { "matcher": "Bash", "hooks": [ { "type": "command", "command": "lint.sh" } ] } ]
              }
            }
            """;
        File.WriteAllText(Path, original);

        HookInstaller.Install(Url, "tok", Path);
        Assert.True(HookInstaller.IsInstalled(Url, Path));
        var installed = JsonNode.Parse(File.ReadAllText(Path))!;
        Assert.Equal("opus", installed["model"]!.GetValue<string>());
        Assert.Equal(2, installed["hooks"]!["PreToolUse"]!.AsArray().Count);
        Assert.Equal("Bearer tok", installed["hooks"]!["Stop"]![0]!["hooks"]![0]!["headers"]!["Authorization"]!.GetValue<string>());

        // Re-installing with a new port replaces, never duplicates.
        HookInstaller.Install("http://127.0.0.1:50999/v1/events", "tok2", Path);
        Assert.Equal(2, JsonNode.Parse(File.ReadAllText(Path))!["hooks"]!["PreToolUse"]!.AsArray().Count);

        HookInstaller.Uninstall(Path);
        var restored = JsonNode.Parse(File.ReadAllText(Path))!;
        Assert.Single(restored["hooks"]!.AsObject());
        Assert.Equal("lint.sh", restored["hooks"]!["PreToolUse"]![0]!["hooks"]![0]!["command"]!.GetValue<string>());
        Assert.True(File.Exists(Path + ".nexus-backup"));
    }

    [Fact]
    public void Install_creates_the_file_when_missing()
    {
        HookInstaller.Install(Url, "tok", Path);
        Assert.True(HookInstaller.IsInstalled(Url, Path));
        Assert.True(HookInstaller.HasAnyNexusHook(Path));
    }
}

public class AgentServerTests
{
    [Fact]
    public async Task Accepts_events_with_the_token_and_rejects_others()
    {
        using var server = new AgentServer(0, "secret-token");
        Assert.True(server.TryStart());
        var received = new TaskCompletionSource<AgentEvent>();
        server.EventReceived += e => received.TrySetResult(e);

        using var http = new HttpClient();
        var body = new StringContent("""{"hook_event_name":"Stop","session_id":"abc","cwd":"C:\\p"}""", Encoding.UTF8, "application/json");

        var unauthorized = await http.PostAsync(server.EventsUrl, body);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);

        var request = new HttpRequestMessage(HttpMethod.Post, server.EventsUrl)
        {
            Content = new StringContent("""{"hook_event_name":"Stop","session_id":"abc","cwd":"C:\\p"}""", Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "secret-token");
        var ok = await http.SendAsync(request);

        // Empty 2xx means "no decision" to Claude Code: Nexus never sends anything back.
        Assert.Equal(HttpStatusCode.NoContent, ok.StatusCode);
        Assert.Empty(await ok.Content.ReadAsByteArrayAsync());

        var e = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("Stop", e.EventName);
        Assert.Equal("abc", e.SessionId);
    }

    [Fact]
    public async Task Handles_chunked_bodies()
    {
        using var server = new AgentServer(0, "t");
        server.TryStart();
        var received = new TaskCompletionSource<AgentEvent>();
        server.EventReceived += e => received.TrySetResult(e);

        using var http = new HttpClient();
        var request = new HttpRequestMessage(HttpMethod.Post, server.EventsUrl)
        {
            Content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes("""{"hook_event_name":"SessionStart","session_id":"c"}"""))),
        };
        request.Headers.TransferEncodingChunked = true;
        request.Headers.Add("X-Nexus-Token", "t");
        var response = await http.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("SessionStart", (await received.Task.WaitAsync(TimeSpan.FromSeconds(5))).EventName);
    }
}
