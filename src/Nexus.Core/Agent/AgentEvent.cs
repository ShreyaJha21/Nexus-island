using System.Text.Json;
using System.Text.Json.Nodes;

namespace Nexus.Core.Agent;

/// <summary>
/// One lifecycle event from a coding agent. The wire format is Claude Code's hook
/// input JSON (documented in docs/agent-events.md), so other agents can post the same
/// shape. Unknown fields are ignored so new agent versions do not break Nexus.
/// </summary>
public sealed record AgentEvent
{
    public required string EventName { get; init; }
    public required string SessionId { get; init; }
    public string? Cwd { get; init; }

    /// <summary>Display name of the agent. Defaults to "Claude".</summary>
    public string Agent { get; init; } = "Claude";

    public string? ToolName { get; init; }
    public JsonObject? ToolInput { get; init; }
    public string? Message { get; init; }
    public string? NotificationType { get; init; }
    public string? Error { get; init; }
    public string? ErrorDetails { get; init; }

    public static AgentEvent? Parse(string json)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }

        if (root is not JsonObject obj)
        {
            return null;
        }

        var eventName = Str(obj, "hook_event_name") ?? Str(obj, "event");
        var sessionId = Str(obj, "session_id");
        if (string.IsNullOrWhiteSpace(eventName) || string.IsNullOrWhiteSpace(sessionId))
        {
            return null;
        }

        return new AgentEvent
        {
            EventName = eventName,
            SessionId = sessionId,
            Cwd = Str(obj, "cwd"),
            Agent = Str(obj, "agent") is { Length: > 0 } agent ? agent : "Claude",
            ToolName = Str(obj, "tool_name"),
            ToolInput = obj["tool_input"] as JsonObject,
            Message = Str(obj, "message"),
            NotificationType = Str(obj, "notification_type"),
            Error = Str(obj, "error"),
            ErrorDetails = Str(obj, "error_details") ?? Str(obj, "last_assistant_message"),
        };
    }

    private static string? Str(JsonObject obj, string name) =>
        obj[name] is JsonValue value && value.TryGetValue<string>(out var s) ? s : null;
}
