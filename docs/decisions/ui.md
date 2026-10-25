# UI decisions

The owner asked the lead and its partners (Codex, Antigravity and Fable, two
rounds of independent review) to reconsider Puck's UI from the ground up,
allowing a complete overhaul, and to keep one feature's look from leaking into
the design language. The work is [the UI plan](../plans/ui.md). The owner's
ruling that the engine is generic is in [AGENTS.md](../../AGENTS.md).

## One tree, as presentation

- **One retained UI tree with host-owned layout draws everything**: a world's
  HUD, the engine's console, bar, wheel, toasts, inspector and history row, a
  mod's panels and a diegetic screen's face. The bespoke overlay writers go as
  each surface moves onto the tree. Immediate mode is not the model for the
  mod interface or for player-facing focus and layout; an SDK may offer a
  builder that diffs into patches.
- **A UI instance is presentation state.** Its tree, layout, focus, hover,
  scroll, drafts and animation are never hashed or journaled; the authored UI
  definition stays document data. UI reads the simulation through bindings and
  changes it only by dispatching the commands a key would, which replay records.
- **A command a UI dispatches carries its whole cause**: the seat, the package
  that built the control (engine, world or mod) and the action's capability,
  authorized as their intersection when it is dispatched and when it applies.
  A mod's control never borrows the seat's wider rights, and a synthetic event
  is never promoted into a trusted gesture.
- **Each local seat owns a root** with its own focus, scale and safe area; a
  world surface is instanced per seat by default, and a whole-window surface
  says `shared`, still naming the seat that interacts.
- **The UI runs headless.** Layout, focus and hit-testing are CPU work with
  stable node addresses, `world.ui` reads the tree back, and console verbs press,
  focus, point and type, so a test world and an agent drive it with
  `--devices none`.

## Layout, text and interaction

- **Flex layout** (row, column, stack, grid; fit, fill, fixed; pad, gap, align,
  wrap; anchors) in logical units scaled per seat, with nested clips. One
  geometry result drives paint, clipping and hit-testing, and layout
  invalidates on changed text, values and styles as well as structure.
- **Runtime collections**: a node repeats over a keyed state row or the joined
  seats, instancing its children as presentation state when keys change.
- **Proportional text through `Puck.Text`**, one text path for every node,
  monospace included. Glyph-run and cluster interfaces are defined from the
  first step; grapheme-safe editing precedes the first text field; shaping,
  bidirectional text, fallback, input composition and localization are
  scheduled steps.
- **Interaction**: per-seat focus, pointer hit-testing over laid-out rects,
  spatial pad navigation with explicit overrides, and `button`, `toggle`,
  `slider`, `list`, `field` nodes. A focused surface holds a binding page, but a
  page never bypasses pointer capture, text composition, modal routing or
  release cancellation. A control that changes the simulation is disabled while
  its seat views a past tick or a replay.
- **Accessibility arrives with the first interactive control**: every
  interactive node has a role and a label, status is never colour alone, and
  focus is announced intelligibly, apart from the expressive text voices. UI
  scale, contrast, reduced motion and colour-vision support are player
  preferences applied last. High contrast substitutes contrasting colour pairs
  and backplates; it is not an opacity floor.

## A neutral design language

- **The engine's design language is neutral and semantic.** Core tokens name a
  role (surface, text, line, accent, status, state, space, radius, type,
  motion), never a feature, a device or a material. The engine ships a complete
  neutral default theme, so an unthemed world renders and is usable.
- **Looks are layers on top**: component styles that reference core tokens and
  their own typed literals, a world's skin, a mod's styles scoped to its own
  subtree, and the player's preferences last. A world or mod cannot restyle the
  host's attribution and permission UI.
- **How it stays neutral**: the document validator admits only the core
  vocabulary and refuses a component style that reaches outside it; a new core
  token is reviewed against a neutral, a high-contrast and an instrument skin at
  small and large scale; the UI inspector shows each property's source; and a
  leak is fixed when found. No structural gate counts unread tokens;
  `puck references` answers that on demand.
- **A screen shows its picture plainly by default.** The CRT treatment (bezel,
  grille, scanlines, vignette, glint) is a screen material a world opts into for
  a real CRT; it belongs to the screen, never to the UI.

## Drawing, screens and mods

- **Custom drawing** is a bounded canvas command list (rects, rounded rects,
  segments, polylines, rings, arcs, wedges, text, images and frames) with
  semantic alternatives and explicit interactive regions. A mod gets no shader,
  GPU handle or render-graph access.
- **The renderer draws bounded, clipped geometry with cached paint** instead of
  walking every element per pixel, measured on the RTX 4070 and the Steam Deck
  from its first step. A surface redraws when its tree, a binding, a source
  publication or an animation deadline changes.
- **A diegetic screen renders the same tree** into the target its screen
  samples; pointer input maps through the screen's inverse source mapping, and
  pad input arrives when a seat engages the screen.
- **A mod's UI runs in a presentation entry** of its package, apart from its
  simulation guest. It receives its own nodes' events locally, never hashed,
  and changes the simulation only through a granted action that becomes a
  recorded command; its simulation guest may publish state the presentation
  entry reads. It gets a root handle per seat, ids under `mod.<name>/`, a binary
  patch interface, per-mod and global budgets with atomic refusal that keeps
  the last good tree, and a generated Rust SDK. UI grants are separate from
  observing or changing state.
- **A host menu is always reachable** from a reserved input and from the bar:
  it holds escape, the Mods surface, settings and focus recovery, draws only
  while open, and no world or mod can cover, restyle or intercept it (the lead's
  call, which the owner deferred).
- **The player sees what each mod does**: a Mods surface lists each mod's
  permissions and use with per-mod show-UI, input and sound toggles, and a held
  key shows who drew anything on screen.
- **Authoring follows [the language consensus](state-and-language.md#the-authoring-consensus)**:
  expressions for visibility and values, modules, and a compile-and-print round
  trip; no string predicate language.

---

[Decisions](README.md) · [The UI plan](../plans/ui.md)
