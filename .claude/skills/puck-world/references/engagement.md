# Engagement — control applications: the (target, kit) set a principal holds

A principal holds a SET of CONTROL APPLICATIONS. Each is a `(target, kit)` pair
saying where its resolved intent goes and what the channels MEAN when they get
there. That set is the whole of engagement: there is no route row, no capture
flag, and no latch beside it to disagree with. Files:
`src/Puck.World.Server/WorldEngagement.cs`, `WorldGrants.cs` (application
storage), `WorldMachineHost.cs`,
`src/Puck.World.Schema/ControlApplication.cs`,
`src/Puck.World.Protocol/Protocol/WorldCommand.cs`, `WorldScreen.cs`
(`WorldScreenRoute`), `WorldKit.cs` (the pad map),
`src/Puck.World/PlayerCommandModule.Engagement.cs`, `WorldScreenBinder.cs`,
`src/Puck.Abstractions/Machines/` (engine-neutral machine contracts).

## Contents

- The primitive: a set of `(target, kit)` applications
- One kit vocabulary, two destinations
- The command kinds
- Authority
- Possession swaps the perceived world, not just the driven body
- Application storage — one table, one derivation
- `Compose` = check Control → rebuild the set → sync the latch
- `WorldScreenRoute.EngageChannel` — the context-sensitive button
- `Dissolve` — three outcomes, no repair
- The one fold
- `player.engage` grammar
- Replay visibility
- Machines: server-authoritative
- Read-backs
- Verifying

## The primitive: a set of `(target, kit)` applications

`ControlApplication(GrantSubject Target, string? Kit, ChannelReachMask Reach)`.

- **target** — `Screen(index)` (a booted machine's pad) or `Body(index)` (its
  own body, or another body under possession).
- **kit** — the `WorldKit` name whose `pad` map assigns the delivered channels
  their meaning at the target, or `null` for passthrough (every reached ordinal
  arrives unchanged). A body target is always passthrough — the destination
  body's own kit already assigns meaning. A screen naming no kit falls back to
  the engine's baked two-movement-role pad (`MoveStrafe`→`LeftStickX`,
  `MoveAdvance`→`LeftStickY`, structural ordinals, never a channel name).
- **reach** — the ordinals this application delivers at all. A masked-out
  ordinal still drives the source body through its OWN-BODY application (when
  that member is present) but never reaches this target.

**Capture is set membership.** A participant that has composed nothing holds
exactly `ControlApplication.OwnBody(index)` — the avatar drives itself.
Composing exclusively REMOVES that member and adds the target's; composing
mirrored keeps both. So:

| Set | Meaning |
|---|---|
| `{ own-body }` | unengaged (the default; not stored — an absent row IS this) |
| `{ screen:0 }` | engaged, captured — the avatar idles |
| `{ own-body, screen:0 }` | mirrored — the avatar walks and the cabinet reads |
| `{ body:7 }` | possession — the seat drives body 7 and perceives from it |
| `{ own-body, body:7 }` | co-drive — the seat walks its avatar and contributes to 7 |

`WorldBody.Engaged` is a DERIVED projection of one predicate ("the set omits the
own-body application"), written by `WorldEngagement.SyncLatch` alone and
re-asserted every tick at the top of `FoldTick`. One storage, one derivation —
so the latch/route desync class, and the repair machine that existed to detect
it, are both gone.

## One kit vocabulary, two destinations

`WorldKit` carries `actions` (channel name → `ActionSpec`, what a BODY does with
the channel) and `pad` (channel name → `WorldPadElement`, what a MACHINE does
with it). Same rows, same channel names, one kit. A screen names one by
`screens[*].route.kit`; the validator refuses a route naming a kit with no `pad`
map, and refuses a `pad` entry naming an undeclared channel or an undefined
element. There is no per-screen translation table any more.

Button elements compare the RAW `FixedQ4816` value against
`WorldChannelTable.DefaultBinaryThreshold`, never a float round-trip; stick axes
canonicalize to -1..1 and triggers to 0..1 in the fixed-point domain first.

None of the four shipped worlds authors an engaged screen, so there is no worked
example to cite — Play's three portal placements are inert in ENGAGEMENT scope
only: their faces render their resolved sources, nothing engages them.

## The command kinds

`WorldCommand(WorldPrincipal Principal, int EntityIndex)` — the closed
drive-a-body hierarchy, 10 sealed cases; `ComposeControl(Target: GrantSubject,
Exclusive: bool, TargetPrincipal)` and `DissolveControl(TargetPrincipal)` branch
out of the generic `Drive`-over-body gate first and run their own check. The kit
and reach are NEVER carried on the wire — they are resolved SERVER-SIDE,
deterministically, from already-replayed document state (a screen's authored
`route.kit`/`route.channels`, or passthrough/all for a body target).

## Authority

`WorldEngagement.CheckEngage(target, actingPrincipal)` asks the ONE grant table:
`Control` over `target` — checked against the SUBMITTER (`Principal`), never
`TargetPrincipal` (seats are pre-seeded permissive `Control`/all, so checking the
target would pass unconditionally). A body target's Control check is separate
from Drive: **possession = Drive-over-the-target-body (a normal grant,
`world.grant seatN drive body:<n>`) + the application** — the application alone
confers no authority to actually move the target.

**A Control grant never mints an application.** Authority to apply and the
application itself are separate storage, so a bare
`world.grant … control screen:0` no longer produces a phantom route. The reverse
coupling is enforced instead: `WorldGrants.Revoke` of any Control row re-tests
every application the principal stands on and dissolves each one whose target it
no longer holds Control over (which is what makes a WILDCARD revoke drop the
concrete applications it was the only basis for). The own-body member is never
dropped that way — a set with no own body IS capture, and losing an unrelated
grant must not capture an avatar.

## Possession swaps the perceived world, not just the driven body

A seat whose set OMITS its own-body application and names another BODY perceives
its ENTIRE world from that body: camera eye, spatial-audio listener, and every
`seat.<n>.position.*` HUD binding. `Client/WorldPerceptionAnchor.cs` is the ONE
resolution point every seat-relative presentation derivation resolves through
(`PerceivedBody(slot)`); `WorldSeatContextSync.Publish` writes it every tick (and
once at boot) from the SAME `Applications` loopback read that publishes the
`engagement` context family — one read, two derived facts, never a second
grant-table query. A set that RETAINS its own-body application never swaps the
anchor: the seat is still driving its own avatar. A screen application never
swaps it either. `player.where`'s `anchor=body:<n>` echo is the read-back (see
[hud.md](hud.md) for the `seat.<n>.position.*` consumer). Deliberately NOT
swapped by possession: the body-index-band presentation classifications
(`WorldSceneEmitter`'s footstep cue and seats-always-cast soft-shadow gates, both
keyed on `index < LocalSeatCount`) — a possessed body keeps casting/cueing on its
own raw index band regardless of who perceives from it; and every SIM-side
seat→body resolution (`PlayerCommandModule.ResolveTarget`, `WorldEngagement`,
grant subjects, intent routing) — the ruling covers the perceived world only,
never authority or simulation.

## Application storage — one table, one derivation

`WorldGrants` holds `Dictionary<WorldPrincipal, List<ControlApplication>>`,
separate from the five per-capability subject sets. An ABSENT row IS the
own-body default (`DefaultApplications`, a per-body-index cached single-element
list, so the per-tick fold read allocates nothing). A set composed back to the
default is stored as the default itself, never as a composition — so
`CollectApplicationHolders` can never report an uncomposed participant.

`IWorldGrantsView`: `Applications(principal) → IReadOnlyList<ControlApplication>`,
`SetApplications(principal, applications)`, `ClearApplications(principal) → bool`,
`CollectApplicationHolders(target, into)`. The transition hook that feeds
`WorldEventFeed.QueueRouteEngaged`/`QueueRouteDisengaged` fires per member
added/removed. The set is captured in `WorldGrantsPrincipalCheckpoint` and
round-trips through `WorldAuthorityCheckpointCodec.Grants`.

The set doubles as the `engagement` context FAMILY for seat bindings:
`engaged|none` per seat is a loopback READ of whether `Applications` names
anything beyond the seat's own body (`WorldSeatContextSync`, every tick
post-step), and a `contexts` row in the composed binding document can flip the
seat's active group on it (see [documents.md](documents.md), "Context rows"; read
back via `player.bindings`).

## `Compose` = check Control → rebuild the set → sync the latch

`WorldEngagement.Compose(entityIndex, target, exclusive, actingPrincipal,
targetPrincipal)`: `CheckEngage` → resolve the live body → rebuild the set
(everything already applied except this target, minus the own-body member when
`exclusive`, plus the own-body member when mirroring a non-own target, plus the
new member with its resolved kit/reach) → `SetApplications` → `SyncLatch`. A
denial or a dead entity index mutates nothing. Re-composing onto a target already
applied REPLACES that member rather than stacking a duplicate.

## `WorldScreenRoute.EngageChannel` — the context-sensitive button (the RPG A-button)

`EngageChannel` is consumed; `CycleChannel` is not. A NAMED channel (kebab-case
only at parse time; `WorldDefinitionValidator.ValidateRoute` holds it to the same
"must resolve against a declared channel" bar `channels` rows carry, since a dead
name would otherwise be a silent, permanent no-op) whose RISING EDGE, on a body
within `EngageRadius` of THIS screen, is intercepted server-side into a
`ComposeControl` instead of reaching the body's own action track.

`WorldServer.ResolveEngageProbes` runs every `Step`, BEFORE the population
advances (so it reads PRE-MOVE positions), over every active local seat that has
COMPOSED NOTHING beyond its own body (`HasComposedApplication`): the first
(document order) screen that is `Engageable`, carrying a live machine
(`Server.WorldMachineHost.HasMachine`, the actually booted signal rather than the
document-declared `WorldScreenSource.Machine`), carrying no live occupant
(`PlayersOn(screenIndex)` empty), naming an `EngageChannel` this world's channel
table resolves, within radius, and that would pass `CheckEngage`. The resolved
`(screenIndex, channelOrdinal)` feeds `WorldBody.Advance`'s `engageProbeOrdinal`
parameter: it tests the SAME rising-edge condition `ProcessLaneActions` tests,
ahead of any integration this tick, and returns whether it fired. A fired probe
skips this tick's movement/action-track integration entirely (the identical idle
no-op the latched branch already takes) — so the press can never ALSO fire a
bound jump the instant it engages — and `WorldServer.Step` then calls the
ordinary `Compose` with `actingPrincipal = targetPrincipal =
WorldPrincipal.Seat(slot)`, printing `[world.engage: <principal> auto-engaged
<target> — context button]` on stderr.

**Replay.** Nothing about this is taped. It is pure re-derivation from tick-local
sim state (position, channel bits, document/grant state) — a shadow replay
re-executes `ResolveEngageProbes` from the SAME taped inputs and reaches the
identical decision at the identical tick.

**Scope today**: local seats only (`AdvanceSeats`, indices 0..3) —
`AdvanceSimulated`'s peers/inhabitants are not probed.

## `Dissolve` — three outcomes, no repair

`WorldEngagement.ResolveDissolve` returns a `ControlOutcome` (3 members):

| State | Outcome | Actor check |
|---|---|---|
| no live body, or the set is already the own-body default | `NotApplied` (friendly no-op) | — |
| the actor lacks Control over at least one applied target | `Denied` | yes |
| every applied target checks out | `Dissolved` — the set returns to the default, the latch releases | yes |

There is no repair arm. The `RepairedLatch`/`RepairedRoute` outcomes described
states that cannot be represented any more: the latch is derived from the set,
and a bare grant mints no application. `PeekDissolve` is the read-only twin the
client uses to format the echo and decide whether to drop held state (only
`Dissolved` does).

## The one fold

`WorldEngagement.FoldTick`, run inside `WorldServer.Step` after the population
and addon read pump and before machine stepping, visits EVERY live body once:

1. Read the body's principal's application set.
2. Re-assert the capture latch from it (`SetEngaged` short-circuits an unchanged
   value, so this costs nothing).
3. For each member: the own-body member routes nowhere (the avatar's own
   integration in `WorldBody.Advance` IS its delivery, which the latch just
   enabled); a screen member masks `body.EngagedIntent` by `Reach`, translates
   through the kit's compiled pad map, and merges into the screen's pad
   (`MachinePadState.Merge`: buttons OR, sticks sum+clamp); a body member appends
   a `BodyRouteContribution(TargetBody, Principal, Intent)`.

`body.EngagedIntent` is captured on EVERY `WorldBody.Advance` call, so it is
available whether or not the avatar is idle. The server passes
`BuildPadSnapshot()` directly to `WorldMachineHost.Advance`; no snapshot or wire
lane carries pads.

`WorldServer.Step`, right after `FoldTick()`, drains `BodyContributions` and
`EnqueueIntent`s each as an ordinary `IntentSubmission` for the NEXT tick —
landing in `ApplyIntentSubmission`/`StageContribution` under the EXACT SAME
Drive-gated co-drive path an addon or a co-driving seat already uses. No second
write path for possession.

## `player.engage` grammar

`player.engage <screen>|screen:<n>|body:<n> [player] [capture:on|off]`. The
bare-integer form is a screen index; `body:<n>`/`screen:<n>` reuse
`GrantSubject.TryParse`'s own grammar (the same tokens `world.grant`/
`world.revoke` accept). `capture:on|off`, when present, is ALWAYS the trailing
token, and maps to `ComposeControl.Exclusive` — on drops the own-body
application, off retains it. A body target skips every screen-only policy check
(engageable, auto-insert, machine presence, engage radius); it only needs a live
body at that index. The player defaults to 1 and is bounded to 1..128.
`player.disengage [player]` submits `DissolveControl`.

## Replay visibility

- The `ComposeControl`/`DissolveControl` COMMANDS themselves ARE taped (ordinary
  Command leaves).
- A body member's per-tick channel PASSTHROUGH is synthesized directly into
  `WorldServer`'s own intent queue (`EnqueueIntent`), never through
  `LoopbackTransport`'s `IntentTap` — structurally EXCLUDED from the tape's
  recorded intent list, exactly like a mounted addon's driving. It is RE-DERIVED
  at replay from the taped `ComposeControl` command (which fixes the
  target/exclusivity/kit/reach) plus the source seat's own taped submissions.
- The pose hash needs NO format change: it already hashes every active body's
  position/orientation each tick, so a possessed body's motion and a captured
  source's stillness are both covered.

## Machines: server-authoritative

A booted `IScreenMachine` is CORE state, not presentation-fed. Machine engines are
engine-neutral (`Puck.Abstractions.Machines`): `IScreenMachineEngine` is a factory
keyed by a kebab-case id, DI-collected into `Server.WorldMachineHost` (a peer
singleton `WorldServer` takes as a constructor parameter — `WorldBootComposition`
registers `gaming-brick` (SM83 family) and `advanced-gaming-brick` (ARM7TDMI)).
`WorldMachineHost` owns boot/step/cable-link/reconfigure/memory-peek for every
declared screen's machine in EVERY boot shape, headless included; stepping happens
inside `WorldServer.Step`, immediately after `FoldTick`, fed the tick's per-screen
pads directly from `WorldEngagement.BuildPadSnapshot()` (in-process, no
client/wire round-trip). For machine output, `WorldScreenBinder` is a read-only
facade over the host. Screen index IS machine identity for screen-hosted machines.

`screen.insert`/`.eject`/`.select`/`.options`/`.link`/`.unlink` submit a
`WorldScreenOp` through the ordered submission domain
(`IServerLink.SubmitScreenOp`), applied SYNCHRONOUSLY like `Command`/`Grant`/
`Revoke` (never buffered — a `player.engage` auto-insert precheck submits a
`Select` immediately ahead of the `ComposeControl` that follows it in the same
batch, and the second submission must observe the first's effect). Both `Insert`
AND `Select` (when the entry resolves to a Machine row) are CAS-pinned identically
through the shared `WorldMachineHost.TryBootMachine` sequence: the SIGNATURE
actually observed (a real `sha256-64` hash, or
`WorldMachineHost.ContentAbsentSignature` when the file could not be read at all)
rides the tape REGARDLESS of whether the op succeeded, so a FAILED insert/select
reproduces the identical failure on replay rather than silently retrying
unpinned, and refuses BY NAME (`ScreenOpContentMismatch`) the moment the file's
on-disk state has since changed in EITHER direction. Tape-covered:
`WorldReplayEntry.ScreenOp`, discriminant 7. Camera/capture/window-capture/
jumbotron-view/test-pattern/QR screen sources remain presentation-only;
`ScreenCommandModule`'s camera/capture/desktop/view/qr handlers eject a present
machine through a `WorldScreenOp.Eject` submission first, then call
`WorldScreenBinder` directly.

Exact machine-op grammars: `screen.insert <index> <contentPath> [engine]
[options…]`; `screen.eject <index>`; `screen.select <index>
[next|prev|<entry>]` (index alone reads the current selection); `screen.options
<index> [options…]` (index alone reads current options); `screen.link <name>
<index> <index> [index…]`; `screen.unlink <name>`. `ScreenCommandModule` routes
write forms as `Simulation`; when each handler runs, its `WorldScreenOp` applies
synchronously in the ordered domain. The `screen.state`, `screen.peek`, and
`screen.links` read-backs are Immediate.

## Read-backs

`screen.state`/`world.screens` tag a mirrored participant `p<n>(mirror)` (both
read `WorldEngagement.PlayersOn(screenIndex)`,
`IReadOnlyList<(int Display, bool Capture)>` — `Capture` is derived from own-body
membership). `player.channels` prints an
`applications=<target>[/<kit>](mask=0x…),…` segment ahead of the per-channel fold
breakdown; the own-body member is listed like any other, so its ABSENCE (capture)
is legible rather than inferred.

## Verifying

Run the game; drive `player.engage <target> [player] [capture:on|off]` with a
control pair: an actor holding `Control` over the target succeeds, a revoked
actor refuses loudly. For possession, grant Drive over the target body first
(`world.grant seatN drive body:<n>`) — Control alone moves nothing. Exercising
the screen path needs a screen at index 0, and no shipped world declares one
under the four-world charter, so validate a screen application against a scratch
world copy. Remember actor ≠ target: every seat holds wide grants by default, so
self-targeting discriminates nothing — revoke first, then prove the denial, then
re-grant and prove success. Remember body index vs. player index:
`world.grant … control body:<n>`/`drive body:<n>` is 0-based; `player.*` verbs are
1-based (`body:1` is "player 2").

In-process laws: `tests/Puck.World.Tests/EngageAuthorityLawTests.cs` (the
compose and dissolve authority pairs) and `ControlApplicationLawTests.cs`
(default set, capture-as-membership, mirror, dissolve-restores-default,
revoke-driven dissolution, two-seat pad merge).

**The context-button (`EngageChannel`) shape**: a world with a ZERO-SCREEN boot
cannot gain a screen live for this — the render-provider capacity is probed and
frozen at boot, so `world.row.set screens` followed by a same-session
`screen.insert` refuses `no screen 0 declared` even after the mutation echoes
applied. Boot with the screen already declared (a scratch world copy, or
`screen.insert` alone against a world that ships one), warp within
`engageRadius`, `player.press <channel>` — assert `screen.state` reads
`engaged=p<n>` and `world.contacts <n>` stays `grounded=true` (no jump); warp
away, press again — assert `world.contacts` reads `grounded=false` instead.
