# Session lifecycle — join, leave, and reconnect

Local-seat and peer join/leave, and the park-with-grace reconnect primitive
layered onto them (reconnect-primitives wave, 2026-08-06). Read this before
touching `WorldPopulation.Entry`'s occupancy fields, `WorldServer.ApplySession`,
or the `$parked:` reserved rule channel.

## Contents

- The two join/leave doors
- Park-with-grace
- Body-resume (local seats only)
- The `$parked:<bodyRef>` reserved rule channel
- Read-back: `world.parked`
- Authoring reconnect policy over the primitive
- Verification gotchas specific to this primitive

## The two join/leave doors

- **Local seats** (`WorldPopulation.LocalSeatCount` = 4, indices `0..3`):
  `SessionRequest.Join`/`Leave` → `WorldServer.ApplySession`, applied
  SYNCHRONOUSLY (loopback delivers inline, no tick gating) →
  `WorldPopulation.ActivateSeat`/`DeactivateSeat`. `player.join`/`player.leave`
  are the console verbs; `PlayerRoster.JoinActive`/`Leave` are the client-side
  callers. A seat's `IdentityName` (a `WorldIdentity.Name`) is the durable
  identity a re-Join is matched against — see below.
- **Peers** (indices `4..Capacity-1`): `WorldServer.TryAdmitPeerConnection`/
  `DisconnectPeerConnection` → the ordered-domain `WorldServerEvent.PeerAdmitted`/
  `PeerDisconnected` → `ApplyServerEvent` → `WorldPopulation.ApplyPeerAdmitted`/
  `ApplyPeerDisconnected`. `Server.WorldTcpHost`'s Hello door is the one live
  caller. **The Hello handshake carries NO persistent identity** — only the
  wire-protocol key — which is exactly why peer body-resume (below) is not
  built: there is nothing to match a reconnecting peer against.

## Park-with-grace (deliverable: reconnect primitives)

Before this wave, both doors' leave/disconnect path nulled `Entry.Body`,
cleared `Entry.Active` (and, for a peer, `Entry.IsRemoteHuman`) IMMEDIATELY.
Now, when `definition.Population.ReconnectGraceTicks` is positive (the
authored document field; default 720 = 3s at 240 Hz; `0` keeps the exact
pre-park immediate-teardown behavior), the SAME call instead:

1. Sets `Entry.Parked = true` and `Entry.ParkedUntilTick = tick + ReconnectGraceTicks` (derived at compile from the document-authored `reconnectGraceSeconds`)
   (`tick` is `WorldServer.NextInputTick` at the synchronous call site, or the
   `Step`-local `tick` inside the tick loop — both name the same instant).
2. Leaves `Entry.Body`, `Entry.Active`, and (for a peer) `Entry.IsRemoteHuman`
   UNTOUCHED — the retained body keeps its pose and durable state, stays in the
   sim/collider set (still advanced by `AdvanceSeats`/`AdvanceSimulated`, still
   collidable, still a valid `$distance:`/`$argmax:` target), and
   **`WorldPopulation.IsHumanOccupied` keeps reading `true` through the whole
   grace window BY CONSTRUCTION** — it is defined purely in terms of
   `IsActive`/`IsAdmittedPeer`, which a park deliberately never flips. This is
   the owner's occupancy ruling: a parked body stays targetable and its
   CC/contribution pool keeps running offline; only the eventual teardown below
   removes it from the pool.

`WorldPopulation.ReclaimExpiredParks(tick)` — swept every `Step`, right beside
`WorldServer.ReclaimExpiredEscrows` (same tick-driven, no-wall-clock,
replay-deterministic shape `OwnershipEscrow.DeadlineTick` already established)
— tears down every entry where `Active && Parked && tick >= ParkedUntilTick`:
drops the body, clears `Active`/`Parked`/`IsRemoteHuman`, and reports the
reclaimed PEER generation through its `reclaimed` sink. **Grant revocation rides
the same deadline.** `ApplyPeerDisconnected` returns whether the entry parked;
`ApplyServerEvent`'s `PeerDisconnected` case revokes `RevokedGrants` immediately
only for the generations that did NOT park (an authored-zero grace, or no live
match), and `WorldServer.Step` revokes each reclaimed generation's rows —
through the ordinary `Revoke` door, off `m_grants.Rows` — right after the sweep.
One connection-loss event, one timing rule: a reconnect inside the grace window
resumes onto live authority instead of a re-mint. A local seat never held
generation-scoped grants to revoke, so this has no seat-side counterpart. It
stays replay-deterministic for the same reason the body half does: the deadline
is a pure function of the reproduced disconnect tick and the authored
`reconnectGraceSeconds`, so the revoke fires at the identical tick on a re-drive
with no separate tape entry.

Proved by `tests/Puck.World.Tests/ParkedGrantReleaseLawTests.cs` — a positive
grace retains the rows across a step, an authored-zero grace releases them
immediately, and a one-tick grace releases them once the sweep crosses it.

Park state is **population state, not a mutation** — it carries no
`WorldMutation` ordinal (the catalog is 64/64 full; this was never a candidate
for a 65th kind) and is never journaled. It IS replay-deterministic on its own
terms: `ParkedUntilTick` is a pure function of the tick the disconnect landed
on (itself replay-reproduced) plus the document-authored `reconnectGraceSeconds`,
so `ReclaimExpiredParks` fires identically on replay with no separate tape
entry, the same way `ReclaimExpiredEscrows`'s mutation-shaped reclaim needs
none.

## Body-resume (local seats only)

A re-Join to a seat that `WorldPopulation.IsSeatParked` reports parked tries
`TryResumeParkedSeat` BEFORE falling back to `ActivateSeat`'s fresh-spawn path.
**The match rule:** the incoming `SessionRequest.Join.IdentityName`'s resolved
`WorldIdentity.Id` must equal the parked body's OWN retained
`WorldBody.Profile.Id` — read directly off the body the park never dropped, so
there is no separate "remembered identity" field to keep in sync. Both
`null` (an anonymous seat) counts as a match too. On a match: clears `Parked`,
leaves pose/durable state exactly as parked (no `ResetDurableState` — that
fires only on an ACTUAL id change, and this is the same id), and re-seats the
color if the caller resolved a different `WorldIdentity` object for the same
id (a profile reload between park and resume). On a mismatch: the parked body
is left completely untouched (so a later, correctly-identified re-Join can
still recover it before grace expires) and `WorldServer.ApplySession`'s `Join`
case refuses the request by name, distinct from an authority denial.

**Peer body-resume is not implemented.** See the Hello-door gap above — there
is no identity signal at peer-admission time to resume against, so a
reconnecting peer's TCP connection always claims a fresh slot via
`TryAdmitRemotePeer`'s `HighestFreeSlot`, which correctly SKIPS a still-parked
slot (its `Active` stays true) without ever reusing it.

## The `$parked:<bodyRef>` reserved rule channel

`WorldRuleFacts.ParkedPrefix` (`WorldRules.cs`, alongside `$tick`/`$population`/
`$region:`/`$machine:`/`$reduce:`/`$argmax:`/`$argmin:`/`$distance:`/`$los:`).
`<bodyRef>` is the SAME single-body-reference grammar `$distance:`/`$los:`
spend one half of theirs on — `body:<n>` (a literal 0-based index) or
`argmax:<row>`/`argmin:<row>` (an entity-addressable extremum) — so it
composes with both directly in one `all` gate:
`$parked:argmax:threat >= 1` asks "is the highest-threat body currently
parked", and it can sit beside a `$distance:`/`$los:` conjunct naming the same
body reference. **Value semantics:** the remaining grace ticks
(`ParkedUntilTick - tick`, floored at 0) — NOT 1/0 — so a gate can also ask
"parked with less than N ticks of grace left" for a reconnect-policy rule.
A body that is not parked, or a reference that resolves to no live body at
all, reads `0` (the ordinary "absent/inapplicable reads as the neutral falsy
value" convention `$region:`/`$machine:` already set — NOT the inverted
`s_noBodyDistance` sentinel `$distance:` uses, since `0` is the correct
"never gate open on a body that was never parked" answer here).
`WorldServer.ReadParkedRemaining`/`WorldPopulation.ParkedRemainingTicks` is
the runtime read; `WorldRuleCompiler`'s `ResolveOperand` is the compile-time
parse (`WorldRuleRefusal.ParkedChannelMalformed` on a bad spelling).

## Read-back: `world.parked`

`WorldPopulationCommandModule`'s `world.parked` (Immediate, no-arg) lists
every currently-parked entity: `body:<n> remaining=<ticks> deadline=<tick>
[profile=<name>] pos=(x, z) yaw=d°`. Empty when nothing is parked. This is the
`$parked:` channel's own read-back, and the cheapest way to prove a park
retained pose/state without fighting `player.where`'s `PlayerRoster.IsJoined`
gate (see the gotcha below).

## Authoring reconnect POLICY over the primitive — "everything else is rules"

Everything past park-with-grace/body-resume/`$parked:` above is an ordinary
authored `WorldRule` — no further engine surface exists or is needed.
`Assets/scenarios/reconnect.world.json` (the reconnect-policy wave,
2026-08-06) is the worked forcing-function demo, mirroring
`combat.world.json`'s role: a CC countdown (`stunRemaining`, a plain
`Level`-mode decrement rule) keeps ticking through a park because rule
evaluation never consults occupancy (see "Park-with-grace" above); a
periodic-attack rule gated on `$argmax:threat` resolving to the PARKED body
proves a parked body stays a valid effect target through the whole grace
window; and a `$parked:body:<n>` threshold gate (`Edge` mode, paired with
its own clear-on-resume `Edge` rule) demonstrates a rule reacting to the
LIVE remaining-grace value, not just its parked/unparked boolean. Nothing
here is a new predicate, effect, or reserved channel — it is the SAME
substrate `combat.world.json`'s `mob-target-mirror`/`mob-attacks-p1` rules
already exercise, aimed at `$parked:` instead of `$distance:`/`$argmax:`
alone. `reconnectGraceSeconds: 0` is the standing break-once control: the
SAME document with that one field zeroed tears the body down immediately on
`player.leave`, with `world.parked` staying empty the whole time — proving
the grace window (not merely the leave itself) is what the rules above ride.

## Verification gotchas specific to this primitive

- **`player.leave` clears the CLIENT roster slot regardless of server-side
  park.** `PlayerRoster.Leave` sets `m_slots[slot] = null` unconditionally, so
  `player.where <n>`/`player.channels <n>`/etc. (which gate on
  `PlayerRoster.IsJoined`) report "not joined" for a parked seat even though
  the SERVER still holds its body. Use `world.parked` to read a parked body's
  pose/state; use `player.where` only AFTER a resuming re-Join (which
  re-populates the client slot via `PlayerRoster.Fill`).
- **Player 1 (slot 0) never leaves** (`PlayerRoster.Leave` refuses `slot <= 0`)
  — pick slot 2..4 for any leave/park/resume script.
- **A shipped world's four identities are exactly its four auto-seated
  players** (`nexus.world.json`: amber/cobalt/moss/violet, one per seat — every
  shipped world authors the same four).
  `player.join <profile> <slot>` refuses BY NAME
  ("profile '<x>' is already in use") the instant `<profile>` is active
  ANYWHERE else — this fires regardless of park state, so testing a
  MISMATCHED-identity resume needs an identity that is not currently active
  elsewhere (free a second seat first, or add a spare identity to the
  document).
- **Grace-expiry teardown is population-internal, not a mutation** — `world.status`'s
  `dirty`/journal length does not move when `ReclaimExpiredParks` fires.
- Default `reconnectGraceSeconds` (3.0, which is 720 ticks at 240 Hz) is small enough that `world.wait 721`
  after a leave reliably crosses the deadline in a scripted verification run;
  `world.wait <2` plus `world.parked` reads the remaining-ticks countdown
  mid-window.
- A composed document may name a command a leaner boot shape (e.g. headless)
  never registers. `WorldSeatBindings`'s recompose SKIPS a page (or a mixed
  page's offending entries only, keeping its registered/resolvable rows)
  whose commands are not in the registered vocabulary, keyed on
  `WorldAffordances.IsCommandRegistered` (a registration FACT, never a
  headless boolean) and narrated ONCE per skipped page instead of once per
  entry. A genuine vocabulary mistake surviving the skip (bindability, value
  kind) still rejects the whole recompose. Windowed boot registers every
  command, so the skip predicate is always false there.
