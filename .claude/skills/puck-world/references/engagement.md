# Engagement — context routes: channel-masked taps on the one intent wire

A ROUTE diverts a body's intent onto a TARGET — a screen (a booted GamingBrick
cabinet, the classic UX) or another body (possession/co-drive). Machine
engagement is the historical special case: screen target, capture on. Files:
`src/Puck.World.Server/WorldEngagement.cs`, `WorldGrants.cs` (route storage),
`WorldMachineHost.cs`,
`src/Puck.World.Data/Protocol/WorldCommand.cs`, `WorldDefinition.cs`
(`WorldScreenRoute`), `src/Puck.World/PlayerCommandModule.cs`,
`WorldScreenBinder.cs`, `src/Puck.Abstractions/Machines/` (engine-neutral
machine contracts).

## Contents

- The primitive: `(target, capture, channelMask)`
- The command kinds
- Authority
- Possession swaps the perceived world, not just the driven body
- Route storage — three fields beside the Control subject
- `Engage` = check Control → resolve body → set latch + set route
- `WorldScreenRoute.EngageChannel` — the context-sensitive button
- Disengage and the three-way repair split
- The `FoldTick` fold
- Authored translation
- `player.engage` grammar
- Replay visibility
- Machines: server-authoritative
- Read-backs
- Verifying

## The primitive: `(target, capture, channelMask)`

- **target** — a `GrantSubject`: `Screen(index)` or `Body(index)`. The route
  IS the exclusive-per-principal `Control` grant over this subject — no
  parallel route table. `IsLegitimateSubject` admits `Control` over a `Body`
  subject, bounded by the population (mirroring Drive/Observe).
- **capture** — whether the source body IDLES (`true`, the classic behavior)
  or MIRRORS (`false` — the source keeps integrating its own pose while the
  same resolved intent also reaches the target every tick). `WorldBody.SetEngaged`
  is called with the route's OWN capture value, never unconditionally true.
- **channelMask** — a `ChannelReachMask` narrowing which ordinals the route
  reaches; a masked-out channel still drives the source body normally (under
  mirror) but never reaches the target. Default: every ordinal.

## The command kinds

`WorldCommand(WorldPrincipal Principal, int EntityIndex)` — the closed
drive-a-body hierarchy, 10 sealed cases; `Engage(Target: GrantSubject, Capture:
bool, TargetPrincipal)` and `Disengage(TargetPrincipal)` branch out of the
generic `Drive`-over-body gate first and run their own check. The channel mask
is NEVER carried on the wire — it is resolved SERVER-SIDE, deterministically,
from already-replayed document/grant state (a screen's authored
`WorldScreenRoute.Channels`, or every ordinal for a body target).

## Authority

`WorldEngagement.CheckEngage(target, actingPrincipal)` asks the ONE grant
table: `Control` over `target` — checked against the SUBMITTER (`Principal`),
never `TargetPrincipal` (seats are pre-seeded permissive `Control`/all, so
checking the target would pass unconditionally). A body target's Control check
is separate from Drive: **possession = Drive-over-the-target-body (a normal
grant, `world.grant seatN drive body:<n>`) + this route** — the route alone
confers no authority to actually move the target.

## Possession swaps the perceived world, not just the driven body

A seat whose route targets a BODY with `capture: true` perceives its ENTIRE
world from that body: camera
eye, spatial-audio listener, and every `seat.<n>.position.*` HUD binding —
never only the co-drive contribution above. `Client/WorldPerceptionAnchor.cs`
is the ONE resolution point every seat-relative presentation derivation
resolves through (`PerceivedBody(slot)`); `WorldSeatContextSync.Publish`
writes it every tick (and once at boot) from the SAME `ControlRoute`/
`RouteCapture` loopback read that publishes the `engagement` context family —
one read, two derived facts, never a second grant-table query. A mirror route
(`capture: false`) or a screen route never swaps the anchor: the seat is still
driving its own avatar (mirror) or has never left it (screen engagement), so
it keeps perceiving from its own bound body. `player.where`'s `anchor=body:<n>`
echo is the read-back (see [hud.md](hud.md) for the `seat.<n>.position.*`
consumer and `console.md`/this file's own Verifying section for the smoke
shape). Deliberately NOT swapped by possession: the body-index-band
presentation classifications (`WorldSceneEmitter`'s footstep cue and
seats-always-cast soft-shadow gates, both keyed on `index < LocalSeatCount`) —
a possessed body is not reclassified in v1, it keeps casting/cueing on its own
raw index band regardless of who perceives from it; and every SIM-side
seat→body resolution (`PlayerCommandModule.ResolveTarget`, `WorldEngagement`,
grant subjects, intent routing) — the ruling covers the perceived world only,
never authority or simulation.

## Route storage — three fields beside the Control subject

`WorldGrants`' per-principal `PrincipalGrants` struct carries the route's
policy alongside its `Control` subject set (not through `TryGrant`'s general
Budget/Reach/Consent/Ceiling/VerbMask payload lanes, since a route is
single-per-principal and these values ride WITH it):

- `RouteTarget()` — the one `Control` subject whose kind is `Screen` or `Body`
  (never the wildcard/composition rows the same set holds).
- `m_routeMirror` (bool, STORED INVERTED) — `RouteCapture()` returns
  `!m_routeMirror`. A route NEVER established through `SetControlRoute` (a
  bare `world.grant … control screen:N`/`control body:N`) reads its bool
  zero-value as CAPTURED — the discriminator `ResolveDisengage` needs (below).
- `m_routeChannelMask` — `RouteChannelMask()`, defaulting to every ordinal
  when zero/unset.

`IWorldGrantsView`: `ControlRoute(principal) → GrantSubject?`,
`RouteCapture(principal) → bool` (default `true` with no route at all),
`RouteChannelMask(principal) → ChannelReachMask` (default all),
`SetControlRoute(principal, target, capture, channelMask)`,
`ClearControlRoute(principal)`, `CollectRouteHolders(target, into)`.
`SetControlRoute` drops any prior route the principal held (re-engage/re-possess
replaces).

The route doubles as the `engagement` context FAMILY for seat bindings:
`engaged|none` per seat is a loopback READ of `ControlRoute` over the seat's
acting principal (`WorldSeatContextSync`, every tick post-step), and a
`contexts` row in the composed binding document can flip the seat's active
group on it (see [documents.md](documents.md), "Context rows"; read back via
`player.bindings`).

## `Engage` = check Control → resolve body → set latch + set route

`WorldEngagement.Engage(entityIndex, target, capture, actingPrincipal,
targetPrincipal)`: `CheckEngage(target, actingPrincipal)` → resolve the live
body → `body.SetEngaged(engaged: capture)` → `SetControlRoute(targetPrincipal,
target, capture, channelMask)` (the mask resolved here, from document data). A
denial or a dead entity index mutates nothing.

## `WorldScreenRoute.EngageChannel` — the context-sensitive button (the RPG A-button)

`EngageChannel` is consumed; `CycleChannel` is not. A NAMED channel
(kebab-case only at parse time; `WorldDefinitionValidator.ValidateRoute` now
ALSO holds it to the same "must resolve against a declared channel" bar
`channels`/`translation` rows carry, since a dead name would otherwise be a
silent, permanent no-op) whose RISING EDGE, on a body within `EngageRadius`
of THIS screen, is intercepted server-side into an `Engage` instead of
reaching the body's own action track — the shared button that jumps away
from a cabinet and engages next to one.

`WorldServer.ResolveEngageProbes` runs every `Step`, BEFORE the population
advances (so it reads PRE-MOVE positions), over every active, un-routed
local seat: the first (document order) screen that is `Engageable`, carrying
a live machine (`Server.WorldMachineHost.HasMachine`, the actually booted
signal rather than the document-declared `WorldScreenSource.Machine`),
carrying no live occupant
(`PlayersOn(screenIndex)` empty), naming an `EngageChannel` this world's
channel table resolves, within radius, and that would pass `CheckEngage`.
The resolved `(screenIndex, channelOrdinal)` feeds `WorldBody.Advance`'s new
`engageProbeOrdinal` parameter: it tests the SAME rising-edge condition
`ProcessLaneActions` tests, ahead of any integration this tick, and returns
whether it fired. A fired probe skips this tick's movement/action-track
integration entirely (the identical idle no-op the `m_engaged` branch
already takes) — so the press can never ALSO fire a bound jump the instant
it engages — and `WorldServer.Step` then calls the ordinary `Engage` above
with `actingPrincipal = targetPrincipal = WorldPrincipal.Seat(slot)`,
printing `[world.engage: <principal> auto-engaged <target> — context
button]` on stderr (the same loud-narration convention a denied Drive
already uses).

**Replay.** Nothing about this is taped. It is pure re-derivation from
tick-local sim state (position, channel bits, document/grant state) — a
shadow replay re-executes `ResolveEngageProbes` from the SAME taped inputs
and reaches the identical decision at the identical tick, the SAME shape
this file's own "Replay visibility" section already establishes for a
body-target route's per-tick passthrough. Verified live:
`replay.record`/`replay.stop`/`replay.verify` all `MATCH` spanning a full
boot→engage→disengage→jump sequence.

**Scope today**: local seats only (`AdvanceSeats`, indices 0..3) —
`AdvanceSimulated`'s peers/inhabitants are not probed.

## Disengage and the repair split — now THREE-way on the route-without-latch branch

`WorldEngagement.ResolveDisengage` returns a `DisengageOutcome` (5 members):

| State | Outcome | Actor check | Drops held device state |
|---|---|---|---|
| no live body / no route | `NotEngaged` (friendly no-op) | — | no |
| latch set, route missing (admin `world.revoke … control screen:N`/`control body:N`) | `RepairedLatch` — clears latch UNCONDITIONALLY | no | yes |
| route present, latch clear, route was NEVER established via `Engage` (`RouteCapture()` reads `true`, the bare-grant default) | `RepairedRoute` — Control-gated; `Denied` on failure | yes | no |
| route present, latch clear, route WAS established via `Engage(capture:false)` (`RouteCapture()` reads `false`) | `Disengaged` — an ORDINARY mirror disengage, never a repair | yes | yes |
| both agree (captured), Control held | `Disengaged` — clears both | yes | yes |
| both agree, Control missing | `Denied` | yes | no |

The THIRD row is the context-routes fix: without the inverted-storage
discriminator, a deliberate mirror route's ordinary disengage is
indistinguishable from a genuinely orphaned bare-grant route (both read as
`latch: false, route: Some`) and misreports as "inconsistent — repaired."
`PeekDisengage` is the read-only twin the client uses to format the echo and
decide whether to drop held state.

## The FoldTick fold — screens AND bodies, captured AND mirrored alike

`WorldEngagement.FoldTick`, run inside `WorldServer.Step` after the
population and addon read pump and before machine stepping, visits EVERY
routed body (not only `Engaged: true` ones; a mirrored body has a route with
no latch):

1. Read `body.EngagedIntent` — captured on EVERY `WorldBody.Advance` call now
   (moved out of the `if (m_engaged)` branch), so it is available whether or
   not the avatar is idle.
2. Apply the route's channel mask (zero every ordinal outside it).
3. Screen target: `Translate(masked, screenIndex)` through the screen's
   COMPILED translation table, merged into the screen's pad
   (`MachinePadState.Merge`: buttons OR, sticks sum+clamp). The server then
   passes `BuildPadSnapshot()` directly to `WorldMachineHost.Advance`; no
   snapshot or wire lane carries pads.
4. Body target: append a `BodyRouteContribution(TargetBody, Principal,
   Intent)` to `WorldEngagement.BodyContributions`.

`WorldServer.Step`, right after `FoldTick()`, drains `BodyContributions` and
`EnqueueIntent`s each as an ordinary `IntentSubmission` for the NEXT tick —
landing in `ApplyIntentSubmission`/`StageContribution` under the EXACT SAME
Drive-gated co-drive path an addon or a co-driving seat already uses. No
second write path for possession.

## Authored translation — `WorldScreenRoute.Translation`/`Channels`

`Translate(intent, screenIndex)` reads a table compiled ONCE in
`WorldEngagement`'s constructor from each screen's `WorldScreenRoute`:

- `Translation` (`IReadOnlyList<WorldScreenTranslationRow>`, channel name →
  `WorldPadElement`) — when a screen authors none, the compiled table covers
  the two movement ROLES only: `MoveStrafe`→`LeftStickX`,
  `MoveForward`→`LeftStickY`. The engine default names no gameplay channel, so
  a screen whose machine needs a face button must author the row itself (e.g.
  a `jump`→`South` row; none of the four shipped worlds author a screen today,
  so there is no worked example to cite — Play's three portal placements are
  inert picture frames, not engaged screens)
  (button elements compare the RAW `FixedQ4816` value against
  `WorldChannelTable.DefaultBinaryThreshold`, never a float round-trip).
- `Channels` (channel names the route reaches) — the mask; `null`/absent
  reaches every ordinal.

Both are document-only today: no `player.engage` verb override exists for
either (only `capture:on|off` has one), and a body target has no document row
to author a translation/mask from (translation is meaningless for a body
target — it is pure channel passthrough; the mask always reaches every
ordinal there).

## `player.engage` grammar

`player.engage <screen>|screen:<n>|body:<n> [player] [capture:on|off]`. The bare-integer
form is UNCHANGED (still a screen index, back-compatible with every existing
world/script); `body:<n>`/`screen:<n>` reuse `GrantSubject.TryParse`'s own
grammar (the same tokens `world.grant`/`world.revoke` accept).
`capture:on|off`, when present, is ALWAYS the trailing token — this keeps the
target and the optional player index at their historical fixed positions (0
and 1) so the classic two-token shape never has to disambiguate. A body target
skips every screen-only policy check (engageable, auto-insert, machine
presence, engage radius); it only needs a live body at that index. The player
defaults to 1 and is bounded to 1..128.

## Replay visibility

The engagement LATCH (a screen route's `WorldBody.Engaged`) was already
deliberately outside hashed determinism state — engaged pose unchanged. The
widened primitive re-decided what is replay-visible:

- The `Engage`/`Disengage` COMMANDS themselves ARE taped (ordinary Command
  leaves, re-keyed `PKRL → PKRN` for the widened `Engage` shape — a `Target:
  GrantSubject` union plus a `Capture: bool` replacing the bare
  `ScreenIndex`).
- A body-target route's per-tick channel PASSTHROUGH is synthesized directly
  into `WorldServer`'s own intent queue (`EnqueueIntent`), never through
  `LoopbackTransport`'s `IntentTap` — structurally EXCLUDED from the tape's
  recorded intent list, exactly like a mounted addon's driving. It is
  RE-DERIVED at replay from the taped `Engage` command (which fixes the
  route/capture/mask) plus the source seat's own taped submissions — the
  stronger property the addon precedent already established.
- The pose hash needed NO format change: it already hashes every active
  body's position/orientation each tick, so a possessed body's motion and a
  captured source's stillness are both covered by the EXISTING hash.

## Machines: server-authoritative

A booted `IScreenMachine` is CORE state now, not presentation-fed. Machine
engines are engine-neutral (`Puck.Abstractions.Machines`): `IScreenMachineEngine`
is a factory keyed by a kebab-case id, DI-collected into
`Server.WorldMachineHost` (a peer singleton `WorldServer` takes as a
constructor parameter — `WorldBootComposition` registers `gaming-brick` (SM83
family) and `advanced-gaming-brick` (ARM7TDMI)). `WorldMachineHost` owns
boot/step/cable-link/reconfigure/memory-peek for every declared screen's
machine in EVERY boot shape, headless included; stepping happens inside
`WorldServer.Step`, immediately after `FoldTick`, fed the tick's per-screen
pads directly from `WorldEngagement.BuildPadSnapshot()` (in-process, no
client/wire round-trip — the former `WorldSnapshot.EngagedPads` wire lane and
`Client.IEngagedPadSource` are both deleted. For machine output,
`WorldScreenBinder` is now a read-only facade over the host. Screen index IS
machine identity for screen-hosted machines, unchanged.

`screen.insert`/`.eject`/`.select`/`.options`/`.link`/`.unlink` submit a
`WorldScreenOp` through the ordered submission domain
(`IServerLink.SubmitScreenOp`), applied SYNCHRONOUSLY like `Command`/`Grant`/
`Revoke` (never buffered — a `player.engage` auto-insert precheck submits a
`Select` immediately ahead of the `Engage` that follows it in the same batch,
and the second submission must observe the first's effect). Both `Insert` AND `Select` (when the entry resolves to a Machine row) are
CAS-pinned identically — a magazine entry's document-declared path is not
immune to on-disk drift either — through the shared `WorldMachineHost
.TryBootMachine` sequence: the SIGNATURE actually observed (a real
`sha256-64` hash, or `WorldMachineHost.ContentAbsentSignature` when the file
could not be read at all) rides the tape REGARDLESS of whether the op
succeeded, so a FAILED insert/select reproduces the identical failure on
replay rather than silently retrying unpinned, and refuses BY NAME
(`ScreenOpContentMismatch`) the moment the file's on-disk state has since
changed in EITHER direction. Tape-covered: `WorldReplayEntry.ScreenOp`,
discriminant 7; the tape magic is `PKRX`. Camera/capture/
window-capture/jumbotron-view/test-pattern/QR screen sources remain
presentation-only; `ScreenCommandModule`'s
camera/capture/desktop/view/qr handlers eject a present machine through a
`WorldScreenOp.Eject` submission first, then call `WorldScreenBinder` directly
as before.

Exact machine-op grammars: `screen.insert <index> <contentPath> [engine]
[options…]`; `screen.eject <index>`; `screen.select <index>
[next|prev|<entry>]` (index alone reads the current selection);
`screen.options <index> [options…]` (index alone reads current options);
`screen.link <name> <index> <index> [index…]`; `screen.unlink <name>`.
`ScreenCommandModule` routes write forms as `Simulation`; when each handler
runs, its `WorldScreenOp` applies synchronously in the ordered domain. The
`screen.state`, `screen.peek`, and `screen.links` read-backs are Immediate.

## Read-backs

`screen.state`/`world.screens` tag a mirrored participant `p<n>(mirror)`
(both read `WorldEngagement.PlayersOn(screenIndex)`, now
`IReadOnlyList<(int Display, bool Capture)>` — no longer filtered to captured
participants only). `player.channels` gained a `route=<target>(capture|mirror,
mask=0x…)` / `route=none` segment ahead of the per-channel fold breakdown.

## Verifying

Run the game; drive `player.engage <target> [player] [capture:on|off]` with a
control pair: an actor holding `Control` over the target succeeds, a revoked
actor refuses loudly. For possession, grant Drive over the target body first
(`world.grant seatN drive body:<n>`) — Control alone moves nothing.
`docs/verification/engagement-dissolution/` was the committed battery for the
envelope-command dissolution — QUARANTINED (2026-08-06): it needs a screen at
index 0, and no shipped world declares one under the four-world charter.
Validate live instead (see its stub's header). Remember actor ≠ target: every seat holds wide
grants by default, so self-targeting discriminates nothing — revoke first,
then prove the denial, then re-grant and prove success. Remember body index
vs. player index: `world.grant … control body:<n>`/`drive body:<n>` is
0-based; `player.*` verbs are 1-based (`body:1` is "player 2").

**The context-button (`EngageChannel`) shape**: a world with a ZERO-SCREEN
boot cannot gain a screen live for this — the render-provider capacity is
probed and frozen at boot, so `world.row.set screens` followed by a
same-session `screen.insert` refuses `no screen 0 declared` even after the
mutation echoes applied. Boot with the screen already declared (a scratch
world copy, or `screen.insert` alone against a world that ships one), warp
within `engageRadius`, `player.press <channel>` — assert `screen.state`
reads `engaged=p<n>` and `world.contacts <n>` stays `grounded=true` (no
jump); warp away, press again — assert `world.contacts` reads
`grounded=false` instead.
