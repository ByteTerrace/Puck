# Players in a closed rewind group

A hosted deployment runs its silo as a closed rewind group, so that a release
can restore the whole group to a captured point. Today that group refuses every
player. This note decides what a player's party is to such a group, names the
one place that decides it, and shows why the
[issuer decision](../decisions/worlds-and-federation.md#players-are-admitted-through-their-hosts-issuer)'s
claim, that a closed group "keeps admitting players while it refuses every
other authority", can hold. It is written for review before any code
([open item](open-items.md#a-hosted-world-on-a-client)).

## What happens today

- A release deploys its silo with `ClosedGroupRewind` (`AzureWorld`).
  `WorldSiloReleaseManagement.ClosedGroupRewind`'s documentation promises that
  "ordinary authenticated player connections remain available".
- An activation installs the boundary
  (`WorldSiloHost.ActivationWiring` calls
  `WorldServer.ConstrainTransferAuthorities(ContainsRewindAuthority)`).
  `WorldTick.ReserveTransfer` and `WorldTick.CommitTransfer` then refuse any
  source authority whose name is outside the group's inventory.
- A `--connect` client is itself an authority. It joins by transferring its
  party from its own boot world, under a per-run session namespace
  `<domain>/<subject>/<session>`
  ([multi-seat client, decision 7](multi-seat-client.md#decision-7-what-names-a-clients-authority-and-when-its-travellers-lapse)). That name is never
  in the inventory, so the join is refused:
  `closed rewind group refuses an external transfer`. This was seen on the
  RTX 2060 against the hosted authority (play.puck.byteterrace.com, port 7825), after the issuer had
  vouched for the player.
- A capture or restore point also refuses any checkpoint holding a committed
  escrow row or mobility admission from outside the group
  (`WorldReleaseRewindBoundary.RequireContained`). So even if the join were
  admitted, a capture taken while anyone is playing would fail.

So the promise in the configuration's documentation has no mechanism behind it.
Every player join is an external transfer, and nothing tells a player's
transfer apart from another world's.

## Why a closed group refuses external transfers

A transfer is exactly-once on the destination's durable log. A key leases once,
`Missing` is final, and a committed key never answers `Missing` afterwards
(the exactly-once rules in [the world model](../architecture/worlds.md)). A
rewind breaks that for any source outside the group. The restored log forgets
commits made after the point, while the source remembers them:

- **Loss.** The source committed its leave and released the body. The restored
  destination no longer holds it, so the body exists nowhere.
- **Duplication.** A source that rewinds independently, or that re-asks about a
  forgotten key, can send a body again that the restored destination also still
  holds from before the point.

Inside the group both sides rewind together, so neither case arises. Refusing
every outside source keeps the group's restore sound without coordinating with
anyone.

## What a player's party is

A player's party differs from another world's traveller in three ways the code
already enforces:

1. **Its source authority lasts one process run.** The session id is minted at
   process start and signed over the connection's challenge
   (`WorldAttestedAuthenticator.VerifyAsync`). A new process is a new
   namespace. Nothing the source keeps survives the run, so no outside log can
   later contradict a restore.
2. **It carries no value of its own.** A visitor embodies the world's seat kit
   ([remote content](remote-content.md#the-build-order), step 2). The body, its pose and its seat are re-minted from
   the world's own definition at every join. A lost player body costs a rejoin,
   never an item.
3. **It already has a sanctioned way to disappear.** A traveller whose streams
   close is parked, then retired when `bodies.reconnectGraceSeconds` expires
   (`ReclaimExpiredParks`, `WorldTransferEscrow.PruneDepartedAdmissions`). The
   world already treats a player body that vanishes as ordinary.

Point 2 holds only while a party carries nothing durable. A companion travels
with an escrowed ticket whose home is another authority
(`WorldTransferReservationMember.Companion`). That is a real outside
obligation, and the group must keep refusing it.

## The ruling

**A closed rewind group admits a party only from a session traveller, and only
members that carry nothing escrowed. Its restore retires every session
traveller the point holds.**

- *The invariant.* After any capture and restore, every player body exists at
  most once, and no value whose only copy lives outside the group is lost or
  duplicated.
- *Its one door.* The transfer boundary in `WorldTick`
  (`ConstrainTransferAuthorities`, checked in `ReserveTransfer` and
  `CommitTransfer`), plus the one capture check that agrees with it
  (`WorldReleaseRewindBoundary.RequireContained`).
- *How the boundary changes.* The predicate is given the typed verified
  identity, not the bare name. `VerifiedPeerIdentity` gains the session id, set
  only on the session branch of `WorldAttestedAuthenticator.VerifyAsync`, so a
  name that merely contains slashes cannot pass as a session. The boundary
  admits a reservation when its source is in the inventory, or when its source
  is a verified session and no member carries a `Companion`. Anything else
  keeps the same terminal refusal, with the member that caused it named.
- *Capture.* `RequireContained` accepts committed rows and mobility admissions
  whose source is a session namespace. It still refuses in-doubt transfers and
  open leases from anyone, so a capture never pins a half-finished join. It
  still refuses forwarded bodies leaving the group.
- *Restore.* Applying a point retires every session traveller the point holds,
  on the restored world's first tick, through the same retirement that grace
  expiry uses. It also drops their escrow rows, so the restored world holds no
  player body and no key from any session.

### Why this keeps exactly-once

Take a session traveller and a restore, in each state the traveller's transfer
can be in at the restore point:

- **Committed before the point.** The restore retires the body and drops its
  key. If the session is still live, its observation stream finds no body and
  ends, and the client rejoins with a fresh transfer id above its own floor.
  The cost is one body, which carried nothing of value (point 2).
- **Committed after the point.** The restored log has no row for the key. A
  client that heard `Committed` released its body and never asks again, so the
  result is the same as the case above. A client that never heard the answer
  asks, gets `Missing`, and keeps its body. The destination does not hold it
  either, so exactly one copy remains.
- **In doubt at the point.** Capture refuses this state, so no point holds it.

Duplication would need the destination to hold a body that the source also
still holds. A restore only removes session bodies, and never adds one. So the
only failure left is loss, and point 2 is what makes loss acceptable.

### Why the issuer decision's claim holds

The issuer decision says a closed group trusts the issuer's root alone, so it
keeps admitting players while refusing other authorities. Under this ruling, a
player is a verified session whose key the issuer vouched for, and a session
can bring in only re-mintable state. The group still refuses every world
authority outside its inventory, every companion and every forwarded body
leaving the group. Players are the one exception, because a player's party is
the one kind of transfer a restore can always undo.

## Options weighed

- **Turn off `ClosedGroupRewind` on the deployment.** Players would join at
  once, but release restore would be lost on the one live deployment, and an
  unreached capability is not unwanted (AGENTS.md). Rejected.
- **A second join for players that is not a transfer.** For example, the
  authority mints the seat body on connection, with no party crossing. That is
  a second mechanism beside the party join that
  [multi-seat client, decision 1](multi-seat-client.md#decision-1-seats-travel-as-members-on-the-lanes-two-authorities-hold) settled on. Rejected
  as a duplicate.
- **Admit by name pattern** (a source containing `/`). This is the smallest
  change, but it makes the boundary depend on how a name is spelled, which any
  authority namespace could imitate. Rejected for the typed session flag.
- **Coordinate the restore with live clients.** A restore would tell every
  session to roll back its own party. That needs a protocol and client state
  the per-run design deliberately does not keep. Rejected: retirement already
  covers it.

## Attacks it closes

- *A world posing as a player to slip past the boundary.* Only the session
  branch of the authenticator sets the session id, and that requires a signature
  over this connection's challenge plus the session. A world's
  `host.authorityNamespace` never reaches that branch.
- *Value smuggled into a rewindable group.* A member with a companion is
  refused, so no escrowed ticket from another home enters a point.
- *A capture pinning a half-finished join.* Capture still refuses in-doubt
  transfers and open leases from any source.
- *A restored player resurrected next to its live body.* Restore removes session
  bodies and never adds one, and an old session's re-ask about a forgotten key
  is answered `Missing`.

## Still open

- When companions travel with players, a closed group needs companion
  obligations that a restore can reconcile (MMO foundations, attacks, question
  1). Until then a player's companion is refused by name, and the player stays
  behind until the companion is dismissed.
- A rejoin after a restore takes a new slot. The old slot was retired, so
  `networkPlayers` capacity is unaffected.

## Proof

A test boots a silo with `ClosedGroupRewind` and its issuer. It joins a
`--connect` client through the issuer and sees the seat's body arrive. It
captures a point with the player present, restores it, sees the body retired,
and sees the client rejoin. A second client whose party carries a companion is
refused by name. The live check is the RTX 2060's player install joining
the hosted authority (play.puck.byteterrace.com, port 7825).
