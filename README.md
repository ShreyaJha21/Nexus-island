# Nexus

**Your system, at a glance.** An open-source, Fluent-native island for Windows 11 that rises out of the
taskbar and shows one thing at a time: your focus timer, a Claude Code agent that needs you, or the music
that's playing. It stays quiet otherwise.

- **Taskbar-anchored.** Floats in the middle of the screen, just above the taskbar, so it never covers tabs
  or title bars. Two optional settings: `"DockOverApps": true` tucks it into empty taskbar space when a
  maximized app would be covered, and tray menu → *Move to top center* hangs it from the top edge of the
  screen, like a MacBook's notch.
- **Context-aware.** A fixed priority ladder decides what shows. The next activity becomes a 32 px bubble,
  the rest a `+N` chip. A focus session turns everything below it into dots (Focus Guard).
- **Fluent-native.** Fluent 2 tokens, Segoe UI Variable, the Windows accent colour, 8 px corners and
  Windows-style motion: the island rises from the taskbar and settles back into it.

Design: [Nexus Figma file](https://www.figma.com/design/qYAkTFWMdY8sChyhQeVNjX/Nexus-island). PRD: *Nexus — PRD: A Context-Aware Island for Windows*.

## What's in this build (MVP)

| Area | Status |
|---|---|
| Taskbar placement, rise/sink, expand/collapse motion, reduced motion | ✅ |
| Priority engine, Focus Guard, bubble and `+N` chip, scroll to cycle, pin, pause 1 h, "Why am I seeing this?" | ✅ |
| Idle: resting line, hover peek, launcher | ✅ |
| F1 Focus timer (25/5, 50/10, custom), session-done banner, break | ✅ |
| F3 Agent status for Claude Code (working, needs you, done, failed, several sessions) | ✅ |
| F4 Music (any app that reports to Windows media controls) | ✅ |
| F6 Show/hide shortcut, hide for 1 h / until tomorrow, auto-hide over fullscreen apps | ✅ |
| Tray menu, start with Windows | ✅ |
| Cricket, Swatch, Calendar, system HUDs, settings UI, onboarding, installer | v1 (shown as disabled tiles in the launcher) |

## Run it

Requires Windows 10 2004+ (Windows 11 recommended) and the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```bash
dotnet run --project src/Nexus.Shell -c Release
```

Run the tests:

```bash
dotnet test
```

## Using it

| Do this | To |
|---|---|
| Hover the resting line | Peek at the time and date |
| Click the island | Open the current mode's panel, or the launcher when nothing is active |
| Scroll on the island | Switch between active modes |
| Right-click the island | Pin or pause a mode, see why it's showing, hide Nexus |
| `Ctrl+Alt+Space` | Hide or show Nexus (configurable) |
| `Ctrl+Alt+F` | Start or stop a focus session (configurable) |
| `Esc`, click away, or 8 s without the pointer | Close a panel |

If another app already owns a shortcut, Nexus says so in a tray notification. Change it in the settings file
(`%AppData%\Nexus\settings.json`, also under tray menu → *Open settings file*), for example
`"HideShortcut": "Ctrl+Alt+J"`, then restart Nexus.

### Connect Claude Code

Tray menu → **Connect Claude Code…** (or *Set up* in the launcher). Nexus shows the exact hook entries it
will add to `~/.claude/settings.json` and writes them only after you approve, keeping a backup. The hooks
post status events to `127.0.0.1` with a private token. Nexus never sends anything back to the agent and
shows only tool and file names, never file contents or prompts. Any agent can post the same events; see
[docs/agent-events.md](docs/agent-events.md).

## How it's built

```
src/Nexus.Core    Priority engine, focus timer, agent tracker, local event endpoint, hook installer, settings.
                  No UI and no Windows APIs, so it is fast to unit test.
src/Nexus.Modes   The modes (Focus, Music, Agent): state, compact pill, expanded panel, bubble, banner.
                  Also the Fluent theme and shared controls. Icons are drawn from design/icons.
src/Nexus.Shell   The island window: placement, motion, the controller that applies the priority rules,
                  shortcuts, tray, fullscreen detection, Claude Code setup.
tests/            Unit tests for Nexus.Core.
design/icons      Icons exported from the Figma file.
docs/             The agent event format.
```

C# on .NET 10 with WPF, as the PRD proposes: every feature needs Windows APIs that C# reaches directly.

### Performance (measured on the author's laptop, Release build)

| Budget (PRD) | Measured |
|---|---|
| Idle CPU under 1% | ~0.01% |
| Memory under 100 MB | ~65 MB private |
| No polling for music or agent state | Both event-driven |

The island renders in software. A transparent, always-on-top WPF window on the GPU keeps a Direct3D
device alive (~210 MB). In software it stays around 65 MB, and an expand-and-collapse costs about 0.6 CPU-s,
including keeping the blur window under the island while it animates. Set `NEXUS_RENDER=hardware` to compare.

Developer switches: `NEXUS_DEBUG=1` writes a trace to `%TEMP%\nexus-debug.log`; `NEXUS_REDUCED_MOTION=1`
forces the reduced-motion crossfades.

## Known gaps

- **Blur, not Windows' acrylic material.** A small native window under the island blurs the desktop and
  the island draws a 72% tint over it, so colours behind show through like the Start menu. Windows 11's
  own acrylic (`NEXUS_BACKDROP=acrylic`) turns flat grey for inactive windows, and the island never takes
  focus. With transparency effects off, or `NEXUS_BACKDROP=off`, the island is nearly opaque instead.
  Very bright backgrounds lower the contrast of secondary text; `NEXUS_TINT=CC` (hex alpha) raises the tint.
- **Resting line is larger than the Figma spec** (112 × 10 instead of 64 × 6) with a much larger hover and
  click area that reaches down to the taskbar, so it's easy to find and hit.
- **Accent colour follows Windows.** With a red or orange accent, primary buttons look red, and orange is
  also the "needs you" colour. Set `"UseSystemAccent": false` for Fluent blue.
- **Dark theme only** for now; light and high-contrast are planned.
- Settings are edited as JSON until the Settings window lands in v1.

## License

[MIT](LICENSE)
