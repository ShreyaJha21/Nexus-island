# Agent event format

Nexus shows what a coding agent is doing and the moment it needs you. Agents report to a small local
endpoint. The format is Claude Code's hook input, so Claude Code needs no adapter, and any other agent can
post the same JSON.

## Endpoint

```
POST http://127.0.0.1:<port>/v1/events
Authorization: Bearer <token>
Content-Type: application/json
```

- `port` and `token` are created on first run and stored in `%AppData%\Nexus\settings.json` (`Agent.Port`,
  `Agent.Token`). The endpoint listens on `127.0.0.1` only.
- Requests without the token get `401`. Accepted events get an empty `204`, which Claude Code treats as
  "no decision". **Nexus never approves, denies or sends anything back to an agent.**
- `GET /v1/health` returns `204` (no token needed, no data).
- Bodies up to 256 KB; `Content-Length` and chunked bodies are both accepted.

## Body

```json
{
  "hook_event_name": "PreToolUse",
  "session_id": "abc123",
  "cwd": "D:\\code\\nexus-island",
  "tool_name": "Edit",
  "tool_input": { "file_path": "D:\\code\\nexus-island\\src\\auth.ts" },
  "agent": "Claude"
}
```

| Field | Required | Meaning |
|---|---|---|
| `hook_event_name` (or `event`) | yes | One of the events below |
| `session_id` | yes | Stable id for one agent session |
| `cwd` | no | Working folder. Its last segment is shown as the project name |
| `tool_name`, `tool_input` | no | The tool in use. Only the tool and file name are shown, never contents |
| `notification_type`, `message` | no | For `Notification` |
| `error`, `error_details` | no | For `StopFailure` |
| `agent` | no | Display name, default "Claude" |

Unknown fields and events are ignored, so newer agent versions don't break Nexus.

## Events and states

| Event | Island state |
|---|---|
| `SessionStart` | Session known, nothing shown |
| `UserPromptSubmit` | **Working** · "Thinking" |
| `PreToolUse` | **Working** · "Editing auth.ts", "Running tests", "Searching the code"… |
| `PermissionRequest` | **Needs you** · expands, even during focus, with one orange pulse |
| `Notification` with `permission_prompt` | **Needs you** (permission) |
| `Notification` with `idle_prompt` / `agent_needs_input` | **Needs you** (waiting for input) |
| `PostToolUse`, `PostToolUseFailure`, `PermissionDenied` | Back to **Working** |
| `Stop` | **Done** · "Done in 4 min", fades after 10 s |
| `StopFailure` | **Failed** · stays until dismissed |
| `SessionEnd` | Session removed |

Several sessions show as "Claude × 3" with a "1 needs you" chip; the panel lists each by project folder.

## Claude Code hooks

The setup screen writes HTTP hooks like this for each event above (tool events use `"matcher": "*"`):

```json
{
  "hooks": {
    "PermissionRequest": [
      {
        "matcher": "*",
        "hooks": [
          {
            "type": "http",
            "url": "http://127.0.0.1:54260/v1/events",
            "timeout": 2,
            "headers": { "Authorization": "Bearer <token>" }
          }
        ]
      }
    ]
  }
}
```

The 2-second timeout means a closed Nexus never slows the agent down. Removing the hooks from the setup
screen deletes only entries that point at a Nexus URL.

## Posting from anything else

```powershell
$s = Get-Content "$env:APPDATA\Nexus\settings.json" -Raw | ConvertFrom-Json
Invoke-RestMethod -Method Post -Uri "http://127.0.0.1:$($s.Agent.Port)/v1/events" `
  -Headers @{ Authorization = "Bearer $($s.Agent.Token)" } -ContentType "application/json" `
  -Body '{"hook_event_name":"PermissionRequest","session_id":"demo","cwd":"C:\\code\\app","tool_name":"Bash","tool_input":{"command":"npm test"}}'
```
