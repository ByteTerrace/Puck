# Binding composition (`WorldBindingComposer.cs`)

Part of [`puck.world.def.v1`](documents.md). Field names and defaults are
generated (`puck schema`, or `Assets/worlds/schema/bindingOverlays.schema.json`);
this file is the decision/derivation prose the schema cannot state.

The composer is N-ary (`Compose(params ReadOnlySpan<BindingProfileDocument?>)`,
base-first, null layers skipped, mismatched `Version` throws). The four layer
CLASSES are assembled by `src/Puck.World.Client/WorldSeatBindings.cs`: engine
default → every world `bindingOverlays` row in order → the seat profile's
`bindings` → live session rebinds (freshest wins). A row's members are two
lists: `held` (a SET — down in any order) and `chord` (a SEQUENCE — pressed in
that order, tested with the held members removed from the press order); a
member is a modifier id, or a raw source id (`"held": ["mouse.button1",
"mouse.button2"]`) which becomes an implicit default-threshold modifier;
`modifiers` remains for thresholds and named multi-source groups. A page row
applies when its members are satisfied (deepest wins), a command row fires
when the down set is exactly its members; a row with neither list is the
group's resting page. A command row targeting a channel may author
`mode: Toggle`: each chord completion flips the input-side channel latch, and
breaking the physical chord leaves that latch untouched. This is the first-class
authoring model for auto-actions: auto-X toggles channel X without inventing a
bespoke command or simulation state. The standard profile uses held `look` (LT)
plus `gamepad.leftStickPress` to toggle `forward` for autorun, and held `look`
plus `gamepad.rightStickPress` to toggle `up` for auto-jetpack. Each command
chord consumes its stick press before the resting page's bare stick binding can
see it. Toggle contributions are owned by the compiled command destination,
not by the button that flipped them; synthesized chord edges carry that stable
logical source through press, reassertion, and release. Parallel auto-actions
therefore coexist, while several bindings for the same destination operate one
latch. Merge rules: the row key is
(group, sorted `held`,
ordered `chord`); a later layer's row for the same key overrides
WHOLESALE when the meaning differs (a `Command`, or a page under a different
id) and ENTRY-BY-SOURCE when both name the same page: a row's `sources` list
(a control can activate a destination from several physical sources, e.g. a
gamepad button AND a keyboard key) replaces the earlier layer's entries AT
EACH of its listed sources independently — an entry surviving at all of its
sources stays combined, one narrowed to fewer sources by a later layer keeps
only the ones still its own — first-touch-per-layer so a hold/release pair in
one layer accumulates.
The merged document compiles once per change through
`BindingProfile.Compile` in `Puck.Commands` — deliberately shared, never
copied. `WorldSeatBindings` compares the filtered composed document plus the
ordered channel-name map before compiling: a route that presents a new document
instance with identical effective content is a true no-op, preserving held
commands, chord/page state, and release latches. Live surface: `player.bind`,
`player.bindings`.

A page may name `inherits`, the profile-unique id of another page in the same
group. Compilation flattens the inherited page first, then replaces its entries
at every source or activator identity the child declares; untouched bindings
remain active with no runtime fallback lookup. Missing pages, cross-group
inheritance, empty ids, and cycles refuse by page name. The standard
`actionWheel` page inherits `base`, so its right-stick selector override does
not suspend left-stick or keyboard movement while the radial is open.

A chord row's, context row's, and wheel row's `group` may be a literal or a
`state.<row>[.<key>]` reference to a Text cell. All references to one cell
resolve together before the profile is composed, so changing that single cell
renames the relationship consistently instead of requiring a document-wide
search/replace. `standard.basis.json` is the worked example — its
`state.world.bindingGroups` row holds `defaultActionGroup`, and every chord and
wheel row names it through the reference.

Each `WorldBindingOverlay` may also carry `bindingBar`: the presentation policy
for the on-screen mapping bar. Absence anywhere in the resolved chain (no
identity row, no world row) resolves to `WorldBindingBarAuthoring.Absent` — NO
bar draws; there is no baked-in C# look any more. No shipped world or basis
authors `bindingBar` today — check `Assets/worlds/schema/bindingOverlays.schema.json`
for the shape before relying on a live example. The first world row, when one
is authored, supplies the world
floor; the selected identity's own first row may replace it for that seat,
matching the existing first-row binding-layer consumer in `WorldIdentity`.
`world.binding-bar [on|off|auto] [player]` reads the resolved policy and controls
its live visibility override.

`bindingBar.slotSet` (required, non-empty) names the physical controls the bar
shows by INPUT SOURCE ID (`gamepad.buttonSouth`, `gamepad.leftTrigger`,
`mouse.button1`, …) — the same vocabulary a binding entry's `sources` speak,
validated against `Puck.Input.InputSourceVocabulary` through
`InputSourceVocabularyHook` (the `Puck.Input`-vocabulary seam Schema reaches the
same way it reaches command/channel vocabulary), refusing an unknown id by it, a
duplicate by index, and the whole list past `WorldBindingBarCapacity.MaxSlots`
(32 — a declared document ceiling now that no device enum bounds the vocabulary).
The classic twelve (`BindingBarLayout.SlotSources`) render in their fixed
compass-diamond positions regardless of authored order; `gamepad.back`/
`gamepad.guide`/`gamepad.start` (`CenterSources`) render as a fixed three-slot
row above the anchor, left to right in that real-controller order regardless of
authored order; every other id (touchpad, mute, the grips, a mouse button, …)
renders in a row further above, left to right in AUTHORED order —
`BindingBarLayout.Categorize`/`Place`'s documented placement rule.

`bindingBar.banks` (required, 1..`WorldBindingBarCapacity.MaxBanks` = 5 — the
WoW-addon original's five chord states: resting/LT/RT/LT>RT/RT>LT) is a keyed
list of `(id, pageId, order, alpha, activeAlpha?, offsetX?, offsetY?)` rows: each
bank renders the WHOLE authored `slotSet` against its OWN named page (a
`BindingPageDefinition.Id` — validated to exist somewhere in the COMPOSED
binding profile, checked after `BindingProfile.Compile` succeeds, since only the
whole overlay stack's result can answer that), displaced from the bar's shared
anchor by an arrangement the ENGINE derives from `order` alone (unique per row;
`BindingBarLayout.BankOffset` uses a fixed nested-cross table in button pitches:
order 1 nests up and inward, 2 down and inward, and 3/4 sit straight above/below;
later orders alternate farther above and below) — `offsetX`/
`offsetY` are optional per-axis overrides for a world that wants one bank placed
by hand. Each draws at its authored `alpha` — or `activeAlpha`
(default 1.0) when that bank's page is the seat's CURRENTLY active one. A
player's own `BindingProfileDocument.BindingBar` (stored in the identity
document's `bindingOverlays` section)
(`Puck.Commands.BindingBarPreferences`: `hideUnbound`/`stacked`/`scale`, all
nullable, LOOK only — never a binding) overrides the world's `hideUnbound` and
adds a `stacked` toggle (render every bank vs. only the seat's active one — falling
back to every bank when none of them actually names the active page, rather than
drawing nothing) and a `scale` override, resolved in `WorldBindingBarControl.Status`.

`bindingBar.text` (default `true`) is the bar's ATLAS-TEXT switch: `false` drops
every text run the bar writes — every badge whose authored icon row carries a
`label` (`LB`/`RB`, `LT`/`RT`, `LS`/`RS`, the menu trio, the exotics), the
active page's name under the modifier
indicators, and the chord-hint lines above them — leaving a purely pictographic
bar: the plates, the PROCEDURAL badge glyphs (the d-pad arrows and the
face-position diamonds), the bound actions' icons, and the indicators all still
draw. The policy resolves ONCE
per seat in `WorldOverlayFeed.Tick` and shapes what it publishes (a suppressed
badge is `OverlayResolvedGlyph.None`, a suppressed label the empty string, suppressed
hints an empty span — each already a case `BindingBarWriter` draws nothing for),
so the writer carries no text policy of its own.

Every rendered slot's `Pressed` state reflects the PHYSICAL control's live carry
(`BindingBarSeatComposer.IsPhysicallyPressed`, resolved once from the seat's
ACTIVE page view by input source id and reused across every bank showing that
control — a control's momentary press state does not depend on which bank/page is
drawing it). Badge content comes from `icons.badges`, keyed by the SAME input
source id: `WorldIconTable.ResolveBadge` is the one door a slot, a modifier
indicator, and a chord hint all go through, checking the row's per-family
override (`Puck.Input.Devices.GamepadType` member name) before its default icon.
A source with no badge row simply draws no badge, so badging a control the
gamepad vocabulary never named (`mouse.button1`) is an authoring act, not a code
change.

**Overlay visibility (`visible`).** Every overlay element — a `hud.panels` row, a
seat's player-scope panel, `hud.defaults` (the gate over every world panel),
`hud.defaults.cursor`, and `bindingBar` — takes an optional `visible` predicate
over per-seat presentation facts (`OverlayPredicate` / `OverlayFact`,
`WorldOverlayVisibility.cs`; evaluated by `WorldOverlayFacts`): `now {fact}`,
`recently {fact, windowSeconds}`, `all`, `any`, `not`. Facts: `SeatInput` (a
routed signal this tick), `PointerMotion`, `WheelOpen`, `ConsoleOpen`,
`SeatCameraApplication` (the seat's camera control application is active —
`WorldSeatBindings.IsCameraModeActive`).
Absent = always visible. `{ "$type": "recently", "fact":
"SeatInput", "windowSeconds": 3 }` hides an element three seconds after the
seat's last input and shows it on the next; a world-scope panel reads a fact as
true when it holds for any joined local seat. Windows compile through the world's
simulation rate; nothing here enters the simulation.

**Context rows.** `puck.bindings.v1` carries an optional `contexts` section:
`{family, state, group}` rows (`BindingContextDefinition`), merged across
layers on `(family, state)` — a later layer overrides a re-declared key IN
PLACE, new keys append, so precedence order is authored primarily by the
layer that ships the vocabulary. The seat's ACTIVE group derives as: first
matching context row's group (document order) → the seat's requested group
(`WorldSeatBindings.SetActiveGroup`, the mode pointer) → the profile default.
A family is one of three kinds: a BUILT-IN engine family
(`WorldContextFamilies` — the output of one per-seat single-valued state
machine): `roster` publishes `unjoined|claimed|pending|active`, `engagement`
publishes `engaged|none` (a loopback read of whether the grant table's
control-application set names anything beyond the seat's own body, synced once
at post-build wiring and every tick post-step via
`WorldSeatContextSync.Publish`), and `layout` publishes the window composer's
active layout selection (an authored `views.layouts` name, or `builtin`) — an
OPEN-states family (`WorldContextFamilies.IsOpenStates`): any state token is
admitted, and a token matching no authored layout simply never matches; a
`state:<row>` family (`WorldStateBindingContext`) reading the routed world's
scalar/keyed state; or an AUTHORED `seatModes` family (`WorldSeatModeFamily`,
document top-level `seatModes`) — a world-declared name plus its admitted
states, flipped by `player.mode <family> <state> [seat]` and validated
strictly (unknown states refused, the name may not collide with a built-in
family or the `state:` prefix). A state whose `target` is `"camera"` composes the camera control application:
the seat possesses its authored `camera-seat-<slot>` inhabited placement
through the ordinary Engage door, its own body intent diverting to
`body.control`'s idle contract, and its view resolving through
`views.cameraRig` (see views.md). `player.camera [seat]` is the bindable
no-token toggle onto the same state. Compile refuses a malformed/duplicate row or
an undeclared group; the vocabulary gate refuses an unadmitted family/state —
all by row. `player.bindings` leads with the derivation echo:
`group=<active> (<step>)`, per-family `<family>=<state>→<group>
(wins)|(shadowed)|(no row)`, and `requested=<group>` marked `(shadowed)` when
a row overrides it — a matching row that lost to an earlier row is reported,
never silent. No shipped world authors `seatModes` today — the mechanism above
(a `{layout, <name>, <name>}` context row flipping the seat onto a
wheel-restricted group while a matching layout is active) is the shape to
follow; check `Assets/worlds/schema/seatModes.schema.json` for the current
field names before authoring one. Every group needs a resting (empty-chord)
page — a blank-slate group authors one with an empty `entries` list.

**Wheel rows.** `puck.bindings.v1` also carries an optional `wheels` section
(`BindingWheelDefinition`: `{id, group, holdPages, rings, style}`). `id` is
profile-unique and is the merge/runtime-continuity identity: several radials
may share one binding GROUP, and a later layer replaces a re-declared radial
WHOLESALE by id. `holdPages` is a non-empty list of distinct chord-row page
ids from that same group. Any one of those pages presents the radial, so an
author may bind several physical openers to one wheel (Tab and LT, for
example), while other hold pages in the group present different wheels (LT
for wheel A and RT for wheel B). Releasing one opener defers commit while
another hold page still presents the same radial.

Rings are ordinary `BindingPageDefinition`s worn as concentric shells
(`BindingProfile.Compile` bounds: 1–3 rings per wheel, 2–8 sectors per ring,
ring page ids sharing the document-wide page-id namespace). A SECTOR row
narrows the page-entry shape to a command destination plus label/icon and an
optional constant `value`/`activateOn`; `source`, `activator`, `channel`,
`scale`, and a non-default `mode` refuse by name. The compiler mints an opaque
`BindingActivation` for each sector, and commit returns it through
`InputRouter.Activate` in the originating seat's deterministic lane. The
vocabulary gate therefore requires every sector command to exist, be
Bindable, and accept the authored value kind — sectors are not console
lines.

`style` is optional authoring policy. `pointerSelection` is `Angle` (direction
alone beyond the dead zone), `HitTarget` (the pointer must remain in the
authored annulus; reusable by a future touch adapter), or `Disabled`;
`placement` is `Pointer` (opening pointer position, with viewport-center
fallback) or `ViewportCenter`. Authors also control dead-zone/ring/grace
fractions, rotation, clockwise ordering, and the initial ring. `axisDeadZone`
is the normalized explicit-ring Axis2D neutral threshold, independent of the
visual/spatial `deadZoneFraction`; excursion-controlled wheels instead use
`excursion.deadZone`. `selectionGraceSeconds` is the neutral dwell before an
empty commit becomes a cancel: a quick throw remains selected during that
window, while holding the selector centered beyond it clears the command. Each
return to neutral completes one selector excursion, so another flick in the
same direction begins a fresh excursion even when its peak is weaker than the
last. Direction remains live throughout an active excursion, including a
constant-radius rotation whose magnitude never sets another peak. On return to
neutral, the wheel retains the last direction at or above `switchFraction`, or
the excursion's peak when a short throw never reached it. `switchFraction` is
also the magnitude an opposite-side excursion must reach and the magnitude a
different sector must reach while grace holds the prior sector; raise it to
reject stronger spring rebound, or lower it to admit lighter direction changes.

`ringSelection` is `Explicit` (the default: `player.wheel.ring` bindings and
pointer-wheel notches step the active ring) or `Excursion` (neutral-relative
selector magnitude chooses it). Excursion requires an `excursion` object:
`deadZone` is the inclusive magnitude that selects no ring; `thresholds`
contains exactly N-1 ascending boundaries for N rings; `hysteresis` supplies
the retained band on both sides of each boundary; and
`spatialTravelFraction` says what fraction of the seat viewport's smaller
extent equals pointer/touch magnitude 1. Axis2D magnitude is already
normalized. The final ring has no outer bound. Each spatial gesture captures
the first available device position as neutral—even when that position arrives
after the opening frame—and never moves that origin until close. Placement is
therefore visual only. With `HitTarget + Excursion`, ring choice uses distance
from captured device neutral while sector eligibility/direction remains
relative to the displayed hub; this deliberately permits direct mouse/touch
targeting and gamepad excursion on the same authored radial.

Selection, ring navigation, commit, and cancel sources are ordinary entries
on each hold page. The engine default uses Tab and authors right-stick
selection; the four shipped worlds currently replace the `play-primary`
radial with one six-sector action ring. `WorldWheelFeed` owns presentation,
and `world.view.wheel` reports the live wheel, hover, effective selector dead
zone, and neutral-grace duration.
