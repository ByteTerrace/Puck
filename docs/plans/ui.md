# The UI overhaul

The work behind [the UI decisions](../decisions/ui.md). Each step is usable on
its own, migrates what it replaces and deletes the old path.

| Step | Delivers | Check |
|---|---|---|
| 1 | **The remaining author leaks.** Remove the CRT glass inset and make overlay text use the font's advance; the neutral theme and authored standard skin already exist. | The debug world looks as before through its skin, its real CRTs keep their look, other screens show their picture plainly, and a world with no theme import renders and is usable. |
| 2 | **Text, scale and the renderer.** Proportional text through `Puck.Text` with glyph-run and cluster interfaces, a per-seat UI scale and safe area, and a renderer of bounded clipped geometry in place of the per-pixel element walk. | Existing surfaces draw proportional text at any scale; `world.gpu-timing` before and after on the RTX 4070 and the Deck. |
| 3 | **The tree.** Node kinds, flex layout, nested clips, runtime collections, per-seat and `shared` scopes, `.puck` authoring with expressions, `world.ui` read-back and the headless drive verbs; one interactive vertical slice (a settings panel exercising focus, scroll, scale, semantics and command dispatch); world HUDs migrate. | A split-screen HUD per seat, the settings panel driven headless by a test world, and the identity-document panel and panel ceilings gone. |
| 4 | **Interaction everywhere.** Focus, pad navigation, hit-testing, the interactive node kinds, grapheme-safe fields, announcements; toasts, inspector and history row move onto the tree, then bar and wheel, then the console. | Each writer is deleted as its surface lands, and each surface is driven by pad, mouse and keyboard. |
| 5 | **Mods.** The presentation entry, root handles and namespaces, the patch interface, events, budgets, the Rust SDK and the Mods surface with attribution; the HUD battery becomes a surface battery. | A Rust mod built as a player would build it, plus denial, overflow, stale-event and fault recovery cases. |
| 6 | **Diegetic surfaces.** The `screen:` scope through a pane, pick-mapped pointer and engagement focus. | An arcade cabinet's attract screen and an in-world terminal, interactive. |

Step 1 is its remaining author leaks, tracked in the
[standard-authoring packet](open-items.md#author-leaks-to-the-standard). Step 2's
renderer replacement follows a GPU-cost measurement of the existing element
walk. Steps 3 to 6 wait behind the frame-time, bake, build, and duplication
fundamentals. The debug world proves each step at highest quality: 60 FPS on
the RTX 2060 and RTX 4070, and 45 FPS (every frame under 22.2 ms) on Steam Deck.

Text shaping, bidirectional text, font fallback, input composition and
localization are a named step after 4. Fonts and specific values are the
owner's look calls.

## Open items

Step 1 retains the CRT inset and overlay text advance. Step 2 waits for the
element-walk GPU measurement; steps 3 to 6 follow the fundamentals. A world
with no `theme` draws the engine's neutral one (`WorldThemeSection.Default`), so
its console, toasts, bar, wheel and cursor stay usable, and the debug world and
the garden draw their on-screen controls through the authored standard
(`worlds/standard/skin.puck` and `worlds/standard/controls.puck`), with debug-only
pages in `worlds/debug/controls.puck`. The standard also authors the shared
input vocabulary, the sky and quality presets, the chase-camera template and the
editor's build page, bar and feel (`inputs`, `skies`, `quality`, `camera` and
`editor` beside them). The
[open items](open-items.md#author-leaks-to-the-standard) hold the packet that
finishes it. A line is deleted when its step lands, never ticked.

---

[Plans](README.md) · [The UI decisions](../decisions/ui.md)
