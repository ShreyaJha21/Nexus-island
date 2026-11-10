# Design

The source of truth is the [Nexus Figma file](https://www.figma.com/design/qYAkTFWMdY8sChyhQeVNjX/Nexus-island):
Fluent 2 tokens (dark), the compact island and panel for every mode, idle and launcher, edge cases,
flows and settings.

- `icons/` holds the 20 × 20 icon set exported from the file's **Icons** frame. The app draws them at runtime
  (`Nexus.Modes/Controls/NexusIcon.cs`) in any color. They are line icons made only of paths, so they scale
  cleanly to 16 px in the pill and 20 px in panels.
- Tokens live in `src/Nexus.Modes/Theme/Theme.xaml`, each one an alias of the Fluent 2 token named in the PRD.
- The motion spec (rise, sink, expand, collapse, moment, the "needs you" pulse, reduced motion) lives in
  `src/Nexus.Shell/Island/Motion.cs`.

The **Island · as built (v0.1)** section on the canvas shows the shipping look: floating in the middle above
the taskbar (the default), plus two optional settings: docking into the taskbar over maximized apps, and
top-centre placement (hanging flush from the screen edge with square top corners).

Where the build differs from the file:

- **Dynamic Island dynamics, Windows material and shape.** The surface keeps Windows' charcoal acrylic
  look (`surface/island` at 72% over the blur) and Fluent's 8 px overlay radius (4 px on controls). The
  motion borrows the Dynamic Island's behaviour: one shape that morphs between line, pill and panel; a small
  springy overshoot (about 4–5%, 380 ms) when opening something new, while content that just changes size
  (a new song) resizes calmly; new content blooms in from 96% with no overshoot.
- **Music.** A new song updates the pill in place instead of expanding the island. The pill carries back,
  play/pause and next buttons, and the level meter takes the service's brand colour (YouTube and YouTube
  Music red, Spotify green, Apple Music pink, SoundCloud orange; your accent colour otherwise). For browser
  playback the service is read from the browser's window titles.

- `surface/island` is `#141414` at 72% over a blur-behind backdrop rather than 92% over the 60 px Fluent
  acrylic, because Windows' acrylic goes flat grey on windows that never activate. Without blur it is 97%.
- The edge is white at 12% (`Brush.StrokeAcrylic`) instead of `stroke/surface`, which reads better over blur.
- The resting line is 112 × 10 instead of 64 × 6, with a larger hit area, because the spec size was hard to
  see and hit on a dark taskbar.
- Shadows 08 and 28 are drawn as layered soft shadows, not a blur effect, to keep animation cheap.
