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

Where the build differs from the file:

- `surface/island` is `#141414` at 97% rather than 92%, because the window can't apply the 60 px acrylic blur
  (see the README's known gaps).
- Shadows 08 and 28 are drawn as layered soft shadows, not a blur effect, to keep animation cheap.
