# Nexus

**Your system, at a glance.** An open-source, Fluent-native island for Windows 11 that rises out of the
taskbar and shows one thing at a time: your focus timer, a Claude Code agent that needs you, or the music
that's playing. It stays quiet otherwise.

- **Out of your way.** Floats in the middle of the screen, just above the taskbar. When the app in front
  has a toolbar, text box or status bar where the island sits (Figma's bottom toolbar, for one), it glides
  to a clear spot and comes back when the conflict is gone. You can also drag it anywhere, pick another
  spot, or have it hide or stay compact instead. See [Staying out of the way](#staying-out-of-the-way).
- **Context-aware.** A fixed priority ladder decides what shows. The next activity becomes a 32 px bubble,
  the rest a `+N` chip. A focus session turns everything below it into dots (Focus Guard).
- **Fluent-native.** Fluent 2 tokens, Segoe UI Variable, the Windows accent colour, 8 px corners and
  Windows-style motion: the island rises from the taskbar and settles back into it.

Design: [Nexus Figma file](https://www.figma.com/design/qYAkTFWMdY8sChyhQeVNjX/Nexus-island). PRD: *Nexus — PRD: A Context-Aware Island for Windows*.

## What's in this build (MVP)

| Area | Status |
|---|---|
| Taskbar placement, rise/sink, expand/collapse motion, reduced motion | ✅ |
| Smart positioning: collision detection, safe spots, per-app rules, drag to move, auto-hide, always compact | ✅ |
| Priority engine, Focus Guard, bubble and `+N` chip, scroll to cycle, pin, pause 1 h, "Why am I seeing this?" | ✅ |
| Idle: resting line, hover peek, launcher | ✅ |
| F1 Focus timer (25/5, 50/10, custom), session-done banner, break | ✅ |
| F3 Agent status for Claude Code (working, needs you, done, failed, several sessions) | ✅ |
| F4 Music (any app that reports to Windows media controls), level bars in the service's colour | ✅ |
| WhatsApp calls: ringing banner with decline and accept, call timer, mute, end, call ended, missed call | ✅ |
| Shelf: drop photos, files, links or text on the island, drag them into any app within the hour | ✅ |
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
| Drag the island | Move it anywhere; it remembers the spot. Drop it near the bottom centre to snap back |
| Right-click → *Position…* (also in the tray menu) | Choose a spot, what happens when an app is in the way, dragging, always compact |
| `Ctrl+Alt+Space` | Hide or show Nexus (configurable) |
| `Ctrl+Alt+F` | Start or stop a focus session (configurable) |
| `Esc`, click away, or 8 s without the pointer | Close a panel |

If another app already owns a shortcut, Nexus says so in a tray notification. Change it in the settings file
(`%AppData%\Nexus\settings.json`, also under tray menu → *Open settings file*), for example
`"HideShortcut": "Ctrl+Alt+J"`, then restart Nexus.

### Staying out of the way

Nexus watches which app is in front and where its window is. The island has a preferred spot (bottom
centre unless you chose or dragged another). What it does when that app has controls there is the mode
under *Position…*:

| Mode | What the island does |
|---|---|
| **Move out of the way of apps** (default) | Glides to the first clear spot and returns when the conflict is gone. If nowhere is clear it stays, but as a thin line. |
| **Hide when an app covers it** | Hides while a window covers its spot. "Needs you" and a click on the tray icon still bring it out. |
| **Always stay put** | Never moves. |

How it decides a spot is taken, in this order:

1. **Window bounds.** A window that doesn't reach the island's spot can't be in the way.
2. **Rules per app**, in the settings file. Figma ships with one: its toolbar sits at the bottom centre, so
   the island goes to the bottom right there. A rule is the whole truth for its app.
3. **UI Automation.** For every other app, Nexus asks Windows which buttons, toolbars, text boxes, tabs,
   menus and scroll bars lie under the island. Scrollable content (text, lists, links) doesn't count.

Web browsers never move it: pages scroll under the island and their controls change with every site, so it
would just jump around (*Stay put in browsers* under *Position…*). Figma in a browser tab still follows the
Figma rule. It checks when you switch apps or move a window, not continuously, so the island doesn't wander
while you scroll. If you drag it somewhere while an app is in front, it stays there for that app. It never moves
while a panel is open.

**Always compact** keeps activities as the thin line: point at it to see the pill, click to open the panel.
Nothing opens on its own except "needs you".

All of it lives under `Positioning` in the settings file. A fuller example than the defaults:

```json
"Placement": "AboveTaskbar",
"Positioning": {
  "Mode": "Smart",
  "Draggable": true,
  "AlwaysCompact": false,
  "StayInBrowsers": true,
  "DetectControls": true,
  "SafeSpots": ["BottomRight", "BottomLeft", "Taskbar"],
  "CompactHours": ["09:30-12:00"],
  "Apps": {
    "Figma": { "Avoid": ["AboveTaskbar"], "MoveTo": "BottomRight" },
    "Code": { "Compact": true },
    "Photoshop": { "Hide": true }
  }
}
```

- Spots: `AboveTaskbar` (bottom centre), `BottomLeft`, `BottomRight`, `TopCenter`, `Taskbar` (free space
  inside the taskbar) and `Custom` (where you dragged it).
- `Mode`: `Smart`, `AutoHide` or `Fixed`. `SafeSpots` are tried in order.
- `Apps` is keyed by process name, or by the app name at the end of a browser tab's title ("File – Figma").
  `Avoid` lists the spots the app uses, `MoveTo` is tried first, `Compact` and `Hide` apply while the app
  is in front.
- `CompactHours` are times of day to stay compact. `DetectControls: false` turns UI Automation off and
  leaves only your rules.

### The shelf

Drag a photo from a web page, a file from Explorer, a link or some text over the island: it opens a
drop zone. Let go, and the item stays on the island for an hour. Then drag it into any app: Figma,
another site's upload box, a chat, a folder. Drag the pill to take everything at once, or click it to
see each item and drag just one (hover a tile to remove it).

- Web images are saved as real files (in `%LocalAppData%\Nexus\Shelf`), because browsers hand over a
  dragged image as a "virtual file" or a link that other apps can't open later. Google Images thumbnails
  (inline data) work too; for a plain image link Nexus downloads just that one image.
- Files you drop from disk are kept as references, never copied, and never deleted.
- Items survive a restart. Up to 20 are kept; change `"Shelf": { "KeepMinutes": 60, "MaxItems": 20 }` in
  the settings file.

### WhatsApp calls

A ringing call grows the island into a banner with the caller, **Decline** and **Accept**. It stays up
until the call is answered, declined or missed, and comes out even in a focus session or compact mode.
On the call, the pill shows a green timer, the caller, live level bars, and **mute** and **end call**
buttons; muted, it says so in red. Afterwards it shows "Call ended" with the length for a few seconds,
or "Missed call · Call back" until you call back or dismiss it. Click the pill for the call panel.

WhatsApp has no API for this, so Nexus reads what WhatsApp itself shows (its call window, call bar and
call notifications) through Windows' accessibility API, and the island's buttons press WhatsApp's own
buttons. Nothing is sent anywhere and Nexus never touches call audio. It only looks while WhatsApp is
playing sound, using the microphone, or showing a call window, so it costs nothing the rest of the time.

### Connect Claude Code

Tray menu → **Connect Claude Code…** (or *Set up* in the launcher). Nexus shows the exact hook entries it
will add to `~/.claude/settings.json` and writes them only after you approve, keeping a backup. The hooks
post status events to `127.0.0.1` with a private token. Nexus never sends anything back to the agent and
shows only tool and file names, never file contents or prompts. Any agent can post the same events; see
[docs/agent-events.md](docs/agent-events.md).

## How it's built

```
src/Nexus.Core    Priority engine, focus timer, agent tracker, local event endpoint, hook installer, settings,
                  and the smart-positioning decisions. No UI and no Windows APIs, so it is fast to unit test.
src/Nexus.Modes   The modes (Focus, Music, Agent): state, compact pill, expanded panel, bubble, banner.
                  Also the Fluent theme and shared controls. Icons are drawn from design/icons.
src/Nexus.Shell   The island window: placement, motion, the controller that applies the priority rules,
                  shortcuts, tray, fullscreen and collision detection, Claude Code setup.
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

Developer switches: `NEXUS_DEBUG=1` writes a trace to `%TEMP%\nexus-debug.log` (including what WhatsApp's call
UI exposes); `NEXUS_REDUCED_MOTION=1` forces the reduced-motion crossfades; `NEXUS_CALL_APPS=myapp` watches a
stand-in app instead of WhatsApp, for testing calls without touching real ones.

## Known gaps

- **Blur, not Windows' acrylic material.** A small native window under the island blurs the desktop and
  the island draws a 72% tint over it, so colours behind show through like the Start menu. Windows 11's
  own acrylic (`NEXUS_BACKDROP=acrylic`) turns flat grey for inactive windows, and the island never takes
  focus. With transparency effects off, or `NEXUS_BACKDROP=off`, the island is nearly opaque instead.
  Very bright backgrounds lower the contrast of secondary text; `NEXUS_TINT=CC` (hex alpha) raises the tint.
- **Resting line is larger than the Figma spec** (112 × 10 instead of 64 × 6) with a much larger hover and
  click area that reaches down to the taskbar, so it's easy to find and hit.
- **Music colours.** The level bars take the playing service's colour: YouTube red, Spotify green, Apple
  Music pink, SoundCloud orange. Windows only says *a browser* is playing, so Nexus finds the tab playing
  the track by its title, in the window title or the browser's tab list (background tabs too). Spotify's web
  player is recognised by its "song • artist" tab title. A site it can't recognise uses your accent colour.
- **Claude's colour.** Claude Code's working and "needs you" states (icon, edge, pulse, button) use Claude's
  coral. Done stays green and failed red, since those colours carry meaning. Other agents keep the accent
  and attention colours.
- **The shelf takes what the dragging app offers.** Most browsers and Explorer work; an app that only
  offers its own private format can't be kept. Dragging out adapts to the app under the pointer: apps built
  on Chromium (Figma's desktop app, browsers, WhatsApp, Slack) get the file the way a browser hands over a
  dragged image, because Figma's desktop app can't read a plain file path dropped from another app; every
  other app (Explorer, Photoshop) gets the real file.
- **WhatsApp calls depend on WhatsApp's interface.** Nexus looks for its English button names
  ("Accept", "Decline", "Mute microphone", "End call"); another language or a redesign may need new names
  in `CallReader`. A call ringing under 20 seconds and then stopping isn't counted as missed, because
  declining on your phone looks the same. Other calling apps (Teams, Zoom) aren't covered yet.
- **Accent colour follows Windows.** With a red or orange accent, primary buttons look red, and orange is
  also the "needs you" colour. Set `"UseSystemAccent": false` for Fluent blue.
- **Collision detection is only as good as what apps tell Windows.** An app that doesn't describe its
  controls to UI Automation looks clear; give it a rule. Chromium-based apps (browsers, VS Code, Electron
  apps) answer only a moment after first being asked, so Nexus looks twice, about a second apart. Asking
  also switches on their accessibility support, which costs them a little performance.
- **Only the app in front counts**, and web pages are checked when you switch to the window, not on every
  scroll or tab change.
- **Rules and compact hours are JSON** for now, and a dragged position is one global spot, not one per app.
- **Dark theme only** for now; light and high-contrast are planned.
- Settings are edited as JSON until the Settings window lands in v1.

## License

[MIT](LICENSE)
