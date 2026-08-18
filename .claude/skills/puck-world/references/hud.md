# The HUD — document-authored overlay panels

The HUD is a world-document section rendered through the banded overlay
pipeline. Document side: `src/Puck.World.Schema/WorldHud.cs` +
`HudValidation.cs` (schema details in [documents.md](documents.md)). Render
side: `src/Puck.World/WorldHudFeed.cs`, `WorldHudBindingResolver.cs`,
`src/Puck.Overlays/HudWriter.cs`, `UnifiedOverlayNode.cs`,
`OverlayChannels.cs`, `OverlayFrameBuilder.cs`. Verbs:
`src/Puck.World/WorldHudCommandModule.cs`. Mutation kinds: see
[mutations.md](mutations.md) (ordinals 41–45).

## Contents

- Schema and caps
- Templating (`WorldHudElement.Template`)
- The overlay reservation — what refuses at construction
- Bands — what `replace` replaces
- The reconcile split — structure vs values
- Verbs
- Verifying

## Schema and caps

`WorldHudSection(Defaults, Panels)`; `WorldHudDefaults(Enabled, Cursor?)` is
a world-level kill switch plus the drawn pointer cursor's per-world policy —
`WorldHudCursor(HoverRadius, SizePx, Role)` (hover reach in world units, ring
radius in px, the bare cursor's `WorldHudCursorRole` hue token; null falls
back to `WorldHudCursor.Default`, the optional-section convention; whole-row
replace semantics on `SetHudDefaults`). Validated by `hud.CursorInvalid`;
echoed RESOLVED by `world.hud`; the live pointer state (position, visibility
verdict, hover target) echoes through `world.view.pointer`
([views.md](views.md)). `WorldHudPanel(Id, Rect, Layer, Style, Elements)` —
rect normalized in SCREEN space, origin top-left, Y down.
`WorldHudElement(Id, Kind, Rect, Style, Text?, Binding?, Template?)` — rect
normalized in the OWNING PANEL's local space. `WorldHudElementKind`: `Rect`,
`Text`, `Gauge` (a bound Text's live value replaces the authored literal, or
a `Template`'s resolved string does; a bound Gauge's value drives its fill;
an unbound gauge draws empty).
`WorldHudLayer`: `Under`, `Over`, `Replace`. `WorldHudPanelStyle`:
`Panel`/`Strip`/`Chip`; `WorldHudStyleToken`: `Primary`/`Dim`/`Accent`/
`Positive`/`Warning`/`Danger`.

`WorldHudCapacity` (the DOCUMENT contract): `MaxWorldPanels = 4`,
`MaxElementsPerPanel = 24`, `MaxSeatPanels = 1`, `MaxElementsPerSeatPanel =
12`. The render cost an authored element expands into is the WRITER's own
constant in `Puck.Overlays` (`HudWriter.GaugeElementCost = 3` records,
`HudWriter.TextRunChars = 64` glyph words — the latter enforced as a
`WriteText` `maxChars` clamp, the way `GaugeLabelChars` always was for a gauge
label, so a template resolving many long `state` cells clips as the writer's
own attributed refusal rather than over-running the Hud reservation and
DROPPING element records). Schema declares no render cost.
Enforced by
`WorldDefinitionValidator.ValidateHudCore` throwing `HudValidationException`
with an enum `HudRefusal` (`TooManyPanels`, `DuplicatePanelId`,
`TooManyElements`, `DuplicateElementId`, `InvalidRect`, `UnknownBinding`,
`SeatPanelReplaceRefused`, `MalformedTemplate`, `UnknownTemplatePlaceholder`,
`TemplateBindingConflict`, under door `hud.validate` in `world.refusals`; a blank
id folds into the duplicate reason). `ValidateHudCore` takes an `isIdentityScope`
flag (`definition.Identity is not null` at its one call site) that swaps
`MaxWorldPanels`/`MaxElementsPerPanel` for the tighter
`MaxSeatPanels`/`MaxElementsPerSeatPanel` (a second seat panel refuses as
`TooManyPanels`, naming `MaxSeatPanels`) and refuses
`WorldHudLayer.Replace` with `SeatPanelReplaceRefused` — applied to EVERY document
carrying an `Identity` section (an owned world's boot load, a sync pull, and
`identity.hud`'s own candidate check below), never hand-rolled per door.

**Bindings are a CLOSED vocabulary** (`HudBindingVocabulary`): `world.tick`,
`world.fps`, `population.active`, `seat.<n>.position.{x,y,z}` with `<n>`
1-based in 1..4, `state.<row>` (a `state`-section row's own SLOT cell), and
`state.<row>.<key>` (one named cell in ANY row shape — see
[documents.md](documents.md)'s `state` section). The split on the FIRST dot
after `state.` is unambiguous because a row/cell name can never itself hold a
dot (`WorldCellName`). Refused by name at validation (`UnknownBinding`); an
empty-string binding reads as unbound rather than refused. The SAME `TryParse`
serves the validator and the render resolver, so a document can never carry a
binding the renderer silently treats as unbound. The `seat.<n>.position.*`
family resolves its body index through the per-seat PERCEPTION ANCHOR
(`Client/WorldPerceptionAnchor.cs` — the seat's bound body, slot n-1, or the
routed body while possessing), the same resolution point the camera anchor
pose and the audio listener derive through, so all three follow a possession
anchor swap together, published every tick by `WorldSeatContextSync.Publish`
off the SAME grant-table Control-route read the `engagement` context family
already performs (see [engagement.md](engagement.md)); `player.where` echoes
the anchor (`anchor=body:<n>`, 0-based) for local seats.

A `state.<row>`/`state.<row>.<key>` binding's EXISTENCE — the row, and for the
cell form the key — is validated wherever a real `state` section exists to
check against: `WorldDefinitionValidator.ValidateState` now returns the
declared rows BY NAME (not just the name set) and threads that into the HUD
checks (`HudRowValidation.ValidateElement`'s `stateRows` parameter), so an
unknown row OR an unknown key on a real row refuses by the SAME `UnknownBinding`
reason a malformed token does. An identity-owned world's own HUD panel
(`WorldIdentity.Hud`, read from that document's OWN `Hud.Panels`) validates
through this identical path against that document's OWN `state` section — it
is a full `WorldDefinition`, not a separate document family, so there is no
distinct "cannot verify existence" scope in this codebase today;
`HudRowValidation`'s `stateRows: null` parameter default exists for a caller
with no such document to check against, but nothing currently calls it that
way. Render-side, `state.<row>`/`state.<row>.<key>` reads
`WorldClient.Definition.State` directly (no engine fact backs it — the
document holds the value): a bound TEXT element renders the resolved cell's
value; a bound GAUGE element's fraction is `(value − min) / (max − min)`
clamped to `[0, 1]` off the ROW's OWN `min`/`max` (cells carry no envelope of
their own) when the row is `int`/`fixed` AND declares BOTH (`WorldStateRow`'s
both-or-neither rule); otherwise (a `bool`/`text` row, a numeric row with no
declared range, or a plain `state.<row>` binding on a KEYED row with no single
cell to show) the gauge draws EMPTY — the same "unbound gauge draws empty"
rule an unbound binding already follows, not a new validation-time refusal
(only existence is checked).

## Templating (`WorldHudElement.Template`)

A `Text` element's `Template` string interleaves literal text with `{token}`
placeholders — each token the SAME closed `HudBindingVocabulary` a plain
`Binding` speaks, resolved through the SAME `IHudBindingResolver`/`TryResolve`
call — so many live facts compose into ONE string (`"Score: {state.score} -
{state.greeting.morning}"`) instead of one bound value replacing the whole
element. `Binding` and `Template` are refused together on one element
(`TemplateBindingConflict`) — exactly one live-value source, never a race
between two. Meaningful only for `Text`; ignored (like `Text`/`Binding`
already are) for `Rect`/`Gauge` — a gauge's fill is one fraction, not a
composed string.

**Grammar** (`HudTemplate` in `WorldHud.cs`, the ONE parser both the validator
and the console verb below call): `{{` and `}}` escape a literal brace (the MS
composite-format-string convention, not a bespoke one); `{token}` is a
placeholder. An unterminated `{`, an empty `{}`, or a lone unescaped `}` is
MALFORMED and refused by name (`MalformedTemplate`) rather than guessed at.
Every placeholder is additionally resolved against `HudBindingVocabulary` and,
for a `state.*` token, the document's own `state` section — the SAME
existence check a plain `Binding` gets — refusing by name
(`UnknownTemplatePlaceholder`) at the FIRST bad placeholder, never a partial
or silently-empty interpolation.

**`HudTemplate.TryParse` is the ONLY parse of this grammar anywhere, and the
render path is not one of its callers.** `Puck.Overlays` cannot reference
`Puck.World.Schema` (the architecture boundary), so `WorldHudFeed` parses on the
structure rebuild and hands the overlay PRE-PARSED runs
(`OverlayHudTemplateSegment`: literal-or-placeholder); `HudWriter` only
substitutes, and carries no grammar at all — the same direction the HUD
ceilings travel (as an `OverlayCapacity` the composition root builds from
Schema; nothing in `Puck.Overlays` restates a World number). Parsing on the structure rebuild
also moves the cost off the per-frame path: world scope parses per revision,
seat scope per identity edit (each seat's built panel is memoized against the
document row INSTANCE it came from, so the unconditional per-frame seat walk
stays allocation-free).

A template's `state.*` placeholders participate in whole-document
revalidation exactly as a plain `Binding` does, so `world.row.remove state` of a
row a live template names REFUSES (`hud.UnknownTemplatePlaceholder`, against
`hud.UnknownBinding` for the binding form) and the element can never go
silently stale; drop the element or the panel first. Measured on both forms
against a control (the same row removes cleanly once nothing names it).

**`world.hud.template <text...>`** (Immediate, `WorldHudCommandModule`)
resolves an AD HOC template against the LIVE document on demand — never
authored, never stored on any row. Every placeholder is validated (grammar,
vocabulary, and — for `state.*` — the live document's row/cell existence)
BEFORE anything resolves, so a bad placeholder refuses the whole call by name.
This is the read-back rule's second half for templating: `world.state`
already echoed an authored text table (see [documents.md](documents.md)'s
`state` section — a `text`-kind row IS the table substrate, keyed cells and
all); `world.hud`'s existing per-element echo now ALSO resolves a document
Template (reusing `HudTemplate.TryParse`, no second parser needed inside
`Puck.World`); `world.hud.template` covers the one case neither already did —
a template with no document row behind it at all.

## The overlay reservation — what refuses at construction

`OverlayChannel` has EIGHT members, value = draw priority for the first five:
`Console = 0`, `BindingBar = 1`, `Gizmos = 2`, `EditorHud = 3`, `Toast = 4`,
`Hud = 5`, `Cursor = 6`, `Wheel = 7`. The FIVE first-party writers are
`ConsolePanelWriter`, `BindingBarWriter`, `EditorGizmoWriter`,
`EditorHudWriter`, `ToastWriter` (`FirstPartyChannelCount = 5`); `HudWriter`
is the sixth channel, banded, not one of the five; `WheelWriter` and
`CursorWriter` are the frame's last two scopes (wheel drawn first, cursor on
top), outside the replace-band suppression — see [views.md](views.md).

`OverlayChannelLeases` (`src/Puck.Overlays/OverlayChannels.cs`) is an
INSTANCE built from an `OverlayCapacity` — the host's declared counts
(`Seats`, `HudPanels`, `HudElementsPerPanel`, `HudSeatPanelsPerSeat`,
`HudElementsPerSeatPanel`). `Puck.Overlays` restates no World number: the
composition root (`WorldBootComposition`'s one `new UnifiedOverlayNode`)
supplies `Puck.World.Client.WorldOverlayCapacity.FromSchema()` — `Seats =
WorldPopulationLimits.LocalSeatCount`, the four HUD ceilings from
`WorldHudCapacity` (`MaxSeatPanels` is the seat-panel count). Render costs stay
the writers' own (`HudWriter.GaugeElementCost`, `HudWriter.TextRunChars`, the
per-seat writers' caps — the `CursorWriter` discipline). The runtime guard is
`builder.Leases.EnsureSeatCapacity`, which throws on a roster/capacity
mismatch. The Hud reservation covers world scope (panels × elements) PLUS the
seat scope (seats × seat panels × elements), each resource at its own worst
case (`Elements` at the gauge cost, `TextWords` at the text-run clamp, one
clip per panel).

The table REFUSES AT CONSTRUCTION: its summed totals are checked against
`OverlayFrameBuilder`'s four backstops (`MaxPanels`, `MaxElements`,
`MaxClips`, `TextWordCapacity` — the GPU region the shader addresses) and an
over-subscription throws `ArgumentOutOfRangeException` naming the resource,
the total, and the backstop, on every boot. The cross-assembly proof that the
Schema-derived capacity fits is
`tests/Puck.World.Tests/OverlayLeaseTableFitsBackstopsLawTests.cs`.
Runtime overflow is per-channel and attributed (a channel clips at
its own boundary, never costs another channel), with two separately-latched
narrations: reservation overflow vs a writer's own declared cap refusal.
Binding-bar visibility, layout, and scale do not change this arithmetic: the
writer still emits at most the same twelve slots, eight modifiers, one label,
and eight hint lines per seat.

## Bands — what `replace` replaces

`UnifiedOverlayNode.ProduceFrame`, per frame: feed tick → `RefreshFrame`
(snapshot the structure once) → UNDER band (its own
`BeginChannel(Hud)` scope) → BASE slot → OVER band. The base slot is: if any
live panel declares `Replace`, the replace panels draw IN DOCUMENT ORDER
INSTEAD OF the five first-party writers — exactly those five, nothing else;
otherwise the five run in enum order. `HasReplace` recomputes from the
fresh snapshot every frame, so removing the last replace panel restores the
writers the next produced frame. The stdin/stdout control plane is
untouched by `Replace` — the console MIRROR merely stops being drawn.
Document row order within a band is the author's ordering control. World
scope opens up to three `BeginChannel` scopes per frame; the seat-panel pass
can open a fourth. All four charge the one Hud reservation.

## The reconcile split — structure vs values

- **Structure.** World-scope panels reconcile only when
  `WorldClient.DefinitionRevision` moves: `WorldHudFeed.Tick` maps document
  panels to `OverlayHudPanel` rows; `Defaults.Enabled: false` publishes an
  empty panel set. Seat-scope panels (`WorldIdentity.Hud`, ONE
  optional panel per identity, `WorldHudCapacity.MaxElementsPerSeatPanel`
  elements, edited through `identity.hud <panel-json> [player]` and read back
  with `world.hud seat:<n>` or `identity.show`) recompose EVERY produced frame
  from `PlayerRoster` + each joined seat's live identity handle instead — no
  cheap revision to key off, and no extra propagation seam for a write to
  reach the render path — into a preallocated array, then both halves publish
  together in one `HudStore.Publish` call so neither can lag the other.
  `HudWriter.EmitSeatPanels` draws them, one `BeginClip` per seat viewport
  (the `EditorHudWriter` per-seat precedent; a seat panel's own rect is LOCAL
  to that viewport, not the whole screen), as a fourth `OverlayChannel.Hud`
  pass after the world-scope under/base/over sequence — unbanded, since a
  panel confined to one seat has no base slot to take over
  (`WorldHudLayer.Replace` refuses there).
- **Values** resolve render-side EVERY FRAME at emission time
  (`HudWriter.EmitText`/`EmitGauge` → `IHudBindingResolver.TryResolve`,
  never cached across frames). Presentation float is fine here — nothing
  feeds sim state. `WorldHudBindingResolver` normalization constants:
  `TickCycleLength = 256`, `FpsNormalizerCeiling = 240f`,
  `PositionNormalizerHalfRange = 50f`; seat n (1-based) maps to body index
  n−1.
- Both `UnifiedOverlaySources.Hud` and `.HudBindings` must be wired or the
  HUD silently draws nothing (no throw) — the wiring is in `Program.cs`.

## Verbs

All in `WorldHudCommandModule`, unbindable. Writes are `Simulation`-routed,
submit a mutation, and return `CommandResult.None` — NO synchronous echo;
the outcome arrives a tick later through `WorldServer.EchoTap` (toast,
console mirror, stderr), and a rejection increments `wire.errors`.

- `world.row.set hud.panels <panel-json>` → `UpsertHudPanel` (whole row,
  elements included — the cross-row transaction boundary).
- `world.row.remove hud.panels <id>` → `RemoveHudPanel`.
- `world.row.set hud.defaults <json>` → `SetHudDefaults`.
- There is NO per-element verb: elements ride their panel row, so a panel upsert
  is the whole edit.
- `world.hud [seat:<n>]` — the `Immediate` read-back. No argument: `[world.hud:
  enabled <b> panels N/4]` plus per-panel and per-element lines. `seat:<n>`
  (1..4): that LOCAL seat's PRIVATE player-scope panel instead (or a refusal
  naming why there is none — unjoined vs. no panel authored). Either form
  resolves bound values through the SAME resolver singleton the renderer
  uses, and a `Template` element's placeholders through the SAME resolver,
  so read-back and screen cannot disagree.
- `world.hud.template <text...>` — `Immediate`, no document row. Resolves an
  AD HOC template against the live document (see "Templating" above);
  refuses by name at the first bad placeholder rather than resolving any of
  it.

Inline JSON is reconstructed from the raw command text (the tokenizer would
destroy quotes) and parsed through `WorldJsonPayload.TryParse` against
`WorldJsonContext` — the exact wire shape of the document row; a parse
failure echoes inline (`IsError`) and submits nothing.

**The player-scope panel is edited elsewhere.** `world.hud.*` writes only the
WORLD-scope section; an identity's private panel is edited through the owned
identity's own door — `identity.hud <panel-json> [player]`
(`IdentityCommandModule`) — not through this module and not through a
`WorldMutation`. UNGATED, like `identity.motion`: an owned world is edited by
its own door with no `Edit`/subject grant check (the player-document family
and its grant subjects were deleted in `ad5935ae` — there is no `profile:<id>`
subject any more). It validates the candidate document through the SAME
`WorldDefinitionValidator.TryValidate` any owned world loads and saves
through, then fires INLINE over loopback (unlike the writes above): the
verb's own `CommandResult` carries the applied/refused outcome synchronously,
no `EchoTap` tick-later arrival, and a refusal leaves the document untouched.

## Verifying

No committed battery covers the HUD document. Validate by RUNNING THE APP;
the ad hoc recipes below are the live method.
Ad hoc, world scope: `world.row.set hud.panels` a panel with a bound gauge,
`world.wait`, read `world.hud`, screenshot for the pixel assertion, then
exercise one refusal (a fifth panel, or an unknown binding) and confirm the
enum-named rejection. A `state.<row>` binding: `world.row.set state` a row (see
`references/mutations.md`'s State row), bind a text/gauge element to it,
`world.hud` shows the live value/fraction; `world.row.set state` the SAME row to
a new value and re-read `world.hud` — no restart needed, the resolver reads
the delivered definition every frame. Upsert (via `world.row.set hud.panels`) a binding
naming an undeclared row refuses (`hud.UnknownBinding`, naming the row). A
`state.<row>.<key>` binding: `world.row.set state` a KEYED row (declared
`capacity`, or `cells` carrying more than one entry), `world.state.cell.set`
one cell, bind a text/gauge element to `state.<row>.<key>`, `world.hud` shows
the cell's live value/fraction (the gauge reading the ROW's own `min`/`max`,
not a per-cell range); binding to a real row but an undeclared key refuses
the same way (`hud.UnknownBinding`, naming the row AND the key).
A `Template`: `world.row.set state` a `text`-kind row with a keyed cell (a
generator, `world.generate`, or a plain `world.row.set state` cells array all
land the same shape), upsert a panel carrying a `Text` element with
`"template":"...{state.row}...{state.row.key}..."`, `world.hud` echoes both
the authored template AND its live-resolved string; change the underlying row
and re-read `world.hud` — the resolved string moves with no restart, same as
a plain binding. `world.hud.template <text>` resolves the identical grammar ad
hoc, against the live document, with no document row at all. Refusal
controls: a template naming an unknown token or an undeclared row/cell
(`hud.UnknownTemplatePlaceholder` at the document; a named CommandResult error
at `world.hud.template`), a malformed brace/escape sequence
(`hud.MalformedTemplate`), and both `binding`+`template` on one element
(`hud.TemplateBindingConflict`) — each control confirmed against the SAME
element succeeding once the conflict is removed.
Ad hoc, seat scope: `identity.hud <panel-json> [player]` a panel with a
bound gauge, `world.wait`, read `world.hud seat:<n>` (or `identity.show
[player]` for the one-line summary), screenshot for the pixel assertion
(confined to that seat's split — an adjacent seat's half must stay clean),
restart the process against the SAME `--state-dir` and re-read
`world.hud seat:<n>` for the persistence round-trip. There is no grant to
narrow (the door is ungated, like `identity.motion`) — the refusal control
instead is a malformed or over-cap panel: an element count over
`WorldHudCapacity.MaxElementsPerSeatPanel` or a `WorldHudLayer.Replace` panel
both refuse by name (`hud.TooManyElements` / `hud.SeatPanelReplaceRefused`)
with the document left unchanged, confirmed against an at-cap panel that
still succeeds.
