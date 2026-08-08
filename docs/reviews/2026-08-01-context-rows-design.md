# Context rows: the design, post-attack

**Drafted 2026-08-01 for adversarial review; RATIFIED AS-IS and the SEAT SIDE
IMPLEMENTED 2026-08-02** (owner ruling, context-routes campaign lane R3): the
`contexts` document section, the roster/engagement family admission with the
published states, the context-row → requested-group → profile-default
derivation with authored-row-order precedence and shadow reporting, and the
`player.bindings` derivation read-back are built and verified by running
(`WorldSeatBindings`, `WorldContextFamilies`, `WorldSeatContextSync`,
`BindingContextDefinition`). The default-document rows that dissolve
`player.south` (the acceptance-test table below) are the input-model arc's
remaining half, not yet landed. This is the design for the owner's context
ruling — *"context dependent routing for buttons needs to be first class and
not just some magic for south; let designers choose"* — revised under the
orchestrating session's attack on the first sketch. The acceptance test is unchanged: **`player.south` dissolves entirely**,
no shipped command is named after a physical control, and the
confirm-vs-primary decision is visible and reorderable in a document.

## The attack, conceded

The first sketch derived a seat's active group from an ordered flat list of
facts (`seat.pending`, `editor.active`, …), first match wins. The attack: that
assumes the facts are mutually exclusive, and nothing enforces it — a second
matching row is silently *ignored*, and "ignored" is indistinguishable from
"didn't apply." The attack is correct, and my own examples already violated
the assumption: `sculpt.active` implies `editor.active` (sculpt is a mode
*within* the editor), and engagement is genuinely orthogonal to both. A flat
boolean fact list is a mechanism that reads like a decision — the
`_ => Simulation` family of defect.

## The shape: facts are (family, state), never booleans

Both proposed repairs are taken, with a division of labor:

**Structure (the repair): facts partition into families because each family IS
one engine state machine's published output.** A fact is a `(family, state)`
pair — `roster=pending`, `engagement=screen:3` — and the engine publishes
exactly ONE state per family per seat at any instant. Within-family
exclusivity is therefore not an authoring convention the document must honor;
it is a property of where the value comes from. A free-floating boolean fact
cannot be declared at all — the grammar has nowhere to put one.

The exclusivity argument, written down rather than assumed: a family is
admitted **only if** it is the output of a single per-seat state machine that
holds one value at a time. Roster lifecycle qualifies (one enum per seat,
mutated by join/confirm/claim/leave). Engagement qualifies (one Control screen
route per principal — `WorldGrants.SetControlRoute` drops any prior route
before recording the new one, so single-route is already enforced at the
table). "Editor-ness" does NOT qualify as a family — editor/sculpt are not
states beside the others, they are the mode pointer itself (next section). The
admission rule is the guard the attack demanded: someone adding a fact must
either find its family's machine or build one, and a boolean with no machine
has no home. This is true right up until someone adds a *family* whose states
overlap — which is why the rule is stated here as the review criterion for
admitting one, not as a property the resolver checks.

**Across families, precedence is authored row order — and shadowing is
reported, not silent (the second repair, kept as observability).** Two rows
from different families matching at once is the NORM (`roster=active` +
`engagement=screen:3`), and which wins is exactly the decision the owner said
designers must own. The resolver takes the first matching row in document
order. But the derivation is surfaced whole: the status echo
(`player.bindings` / the editor HUD line) shows every matched row with the
winner marked — `roster=active→(no row) | engagement=screen:3→engaged ✓ |
requested=play (shadowed)` — so an author sees the ranking they got, and
"applied and lost" is visibly different from "didn't apply."

## Mode is not a family — it is what the rows override

The existing `SetActiveGroup` requested-group pointer already IS the mode axis
(play/editor/sculpt), already data-shaped (group names are opaque strings),
already per-seat, and already flipped by verbs. Making "mode" a context family
whose rows map states to same-named groups would be a second spelling of the
same pointer. So the derivation is:

```
active group = first matching context row's group     (document order)
             ?? the seat's requested group             (SetActiveGroup — the mode)
             ?? the profile's default group            (first row's group, as today)
```

Context rows are the *overrides* — the states that today live in handler
if-chains (roster lifecycle, engagement) — while mode stays the pointer it
already is. A seat with no overriding context behaves byte-for-byte as today.

## Document grammar

One new optional section in `puck.bindings.v1`, composed across layers like
every other section (later layers override a `(family, state)` key they
re-declare; new keys append; row ORDER within the merged list is the base
layer's order with appended keys after — precedence is therefore authored
primarily by the layer that ships the vocabulary, deliberately):

```json
"contexts": [
  { "family": "roster",     "state": "unjoined", "group": "roster-join" },
  { "family": "roster",     "state": "pending",  "group": "roster-pending" },
  { "family": "roster",     "state": "claimed",  "group": "roster-claimed" },
  { "family": "engagement", "state": "engaged",  "group": "engaged" }
]
```

- `family` and `state` are validated against the engine's published registry
  of families and their state names (a closed, engine-published set — the
  moment these carry expressions, the document has grown a programming
  language; that guardrail is binding, per the orchestrator's ruling on the
  addendum).
- `group` must name a group the composed document declares — checked by the
  same thick gate that owns every other cross-reference, loudly, at compile.
- A `(family, state)` with no row contributes nothing (falls through to
  mode) — that is the *defined* composition, not silence: the echo still
  names the family's current state in the derivation line.
- `roster=active` deliberately ships with NO row: active is the state where
  mode owns the seat.

## The acceptance test, walked concretely

Engine work: the roster publishes `unjoined | pending | claimed | active` per
seat (values that already exist as `IsJoined`/`IsPending`/`IsClaimed` — the
family is the tuple those three booleans already encode, made one value);
engagement publishes `engaged | none` (a read over `WorldGrants.ControlRoute`,
already single-valued). The resolver in `WorldSeatBindings`/`PagedInputBindings`
consults the derivation above at the same points `SetActiveGroup` flips today.

Default-document rows then dissolve the magic:

| Today (code) | Becomes (document) |
|---|---|
| `SouthHandler` case unjoined → join ([PlayerCommandModule.cs:998](../../src/Puck.World/PlayerCommandModule.cs)) | `roster-join` page: South/Enter → `player.confirm` (whose handler already joins-then-confirms) |
| `SouthHandler` case pending ∧ ¬claimed → confirm (`:1008`) | `roster-pending` page: South/Enter → `player.confirm` |
| the claimed exclusion buried in two handlers (`:1011`, `:1169`) | `roster-claimed` page: **no confirm binding** — the exclusion is a visible, deletable row |
| `SouthHandler` case active → Primary lane (`:1017`) | play base page: South → `player.primary`, plainly |
| movement verbs cycling profiles while pending (`:284-288`) | `roster-pending` page: turn keys/D-pad → the profile-cycle affordance |
| `PrimaryHandler`/`SecondaryHandler` pending guards (`:960`, `:976`) | deleted — a pending seat resolves in `roster-pending`, which simply does not bind those commands |

`player.south` deletes. `player.confirm` and `player.primary` keep their
names. Both if-chains become rows an author can reorder or remove. The
mid-hold transition is already safe: a press that flips the derived group
resolves its release against what the press latched
([PagedInputBindings.cs:21-27](../../src/Puck.Commands/PagedInputBindings.cs)),
so the confirm press that activates a seat cannot leak a primary release.

**The one residue, decided rather than hidden:** the `AssignedSlot`
consume-first-edge case (`:992`, `:1175`) is a per-DEVICE attachment
transition, not a per-seat state — a context row cannot express "this signal
seated its pad," and a transient pseudo-state would be a boolean wearing a
family costume. It stays mechanism: the router/roster consumes the seating
edge during device attach, documented as such. Nothing about any button's
*meaning* is decided there — one edge is swallowed while a pad attaches — so
it does not offend the ruling, and saying so here is the record.

## What stays code, on purpose

- The family state machines themselves (roster, engagement) — they publish
  states; they do not choose groups.
- Mode side-effects: the editor session's camera rig, targeting, honest-idle
  ride the group-change edge as subscribers, exactly as they do today.
- The device-attach consume (above).
- Kind-2 interpretation (engagement's intent translation, intent-source
  masking, kits) — unchanged and deliberately outside the binding table, per
  the addendum's two-kinds split.

## Verification and falsification

By running `Puck.World`, over the pipe, no gates:

1. **Journey (green):** `player.signal` a pad South three times from cold —
   join pending, confirm, then jump — asserting the roster echoes and
   `player.where` movement, byte-shaped like today's behavior.
2. **Reorder (red→different green):** a test overlay re-declaring
   `roster=pending` to a group whose South is unbound → the second press must
   NOT confirm; `wire.errors`/echo assert the changed outcome. Proves row
   order is load-bearing.
3. **Delete the claimed row** → a claimed slot's South press must confirm
   (behavior change proves the row, not the handler, holds the exclusion).
4. **Shadow reporting:** engage a seat, read the derivation echo — the mode
   row must show as shadowed by the engagement row, distinguishable from
   absent.
5. The affordance gate (already landed) covers every new group's entries —
   a context row naming a group with dead commands fails at compile/boot.
