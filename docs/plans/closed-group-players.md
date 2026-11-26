# Players in a closed rewind group

A hosted deployment runs its silo as a closed rewind group, so that a release
can restore every world of the group to one captured point. A group built
that way refuses every transfer from outside itself, and a player's party
comes from outside. This note says what a player's party is to such a group,
names the one place that decides it, and answers the review of the first
design finding by finding. The settled decision is recorded in
[worlds and federation](../decisions/worlds-and-federation.md#a-closed-rewind-group-admits-players-as-guests).

## Summary

A player's party enters a closed group as a **guest**: a traveller whose home
is a player's own session, admitted only when the host's own issuer vouched
for that session, and only in the shape of a seat that lands at the world's
spawn carrying nothing the group would have to give back. The group keeps the
ledger a transfer needs while the guest is there, and forgets the session the
moment nothing of it is left.

A recovery point is captured as a **baseline**: its bytes say that every
traveller from outside the group ends when the point is restored. Whoever
restores those bytes (a candidate silo, a crash recovery, a replay) gets the
same world with no guest in it, before its first tick.

A client whose session the group no longer knows is told so with a terminal
answer, `Ended`. It takes back every seat it had sent, and its ordinary join
sends them again.

## The problem

- A release deploys its silo with `ClosedGroupRewind` (`AzureWorld`), and its
  documentation promises that "ordinary authenticated player connections
  remain available".
- The transfer boundary an activation installed was a predicate over names. A
  `--connect` client joins by transferring its party from its own boot world,
  whose authority is a per-run session namespace
  `<issuer root>/<subject>/<session>`
  ([multi-seat client, decision 7](multi-seat-client.md#decision-7-what-names-a-clients-authority-and-when-its-travellers-lapse)).
  That name is never in the group's inventory, so every join was refused:
  `closed rewind group refuses an external transfer`. The RTX 2060's player
  install met that refusal against the hosted authority, after the issuer had
  vouched for the player.
- A capture refused any checkpoint holding a committed transfer or a traveller
  credential from outside the group, so a point taken while anyone played
  would have failed as well.

## Why a closed group refuses outside transfers

A transfer is exactly-once on the destination's durable ledger: a key is
leased once, and a key answered "committed" never answers "missing"
afterwards. A rewind breaks that for any source outside the group, because the
restored ledger forgets what the source remembers. The source may have let its
body go on a commit the restored group no longer holds (loss), or may send
again a body the restored group still holds from before the point
(duplication). Inside the group both sides rewind together, so neither
happens. The group stays sound by owing nothing to anyone outside it.

A player can be admitted only if the same sentence stays true: after a
restore, the group owes a player nothing, and the player knows it.

## The design

### The invariant

In a closed rewind group:

1. Every row a world holds about an authority outside the group (a body, a
   transfer key, a traveller credential, a forwarding route) belongs to a
   guest the guest door admitted.
2. A guest is a seat and nothing else. It holds no value whose only other
   copy is outside the group, and nothing it holds ever leaves the group.
3. A restored point holds no guest, in any world of the group, before any
   tick runs or any connection is admitted.
4. A world answers a guest about a transfer or a traveller only while it holds
   something live of that guest's session. Otherwise it answers `Ended` and
   writes nothing.

### The one door

`WorldTransferEscrow.Admit` classifies the caller of every operation that can
change a world's crossing ledger: reserve, commit, abort, acknowledge and
status. The escrow is the only owner of that ledger, and each of those
operations runs behind the value `Admit` returns (`WorldTransferAdmission`,
whose default refuses), so a new caller cannot reach the ledger around it.
The classification is a `WorldTransferParty`:

| Party | Who | What it may do |
|---|---|---|
| Member | A world of the group, calling from inside the silo's process (`WorldTransferCaller.Colocated`) under a name the inventory holds | Everything, as before |
| Guest | A network caller whose verified identity carries a session vouched for under the host's own issuer root (`VerifiedSession`) | Reserve and commit a party in the guest shape; ask about, abort and acknowledge its own transfers |
| Outside | Anyone else, a caller that proves nothing included | Nothing: refused by name, and nothing is written |

The boundary a silo installs (`WorldTransferBoundary`, through
`WorldServer.ConstrainTransferAuthorities`) carries the inventory and the
issuer's root fingerprint. A world with no boundary is not a closed group and
behaves as it did.

Recovery has its own door. A crossing-log record or a taped arrival was
decided live, behind `Admit`, and is redone through `TryReland`, `RedoLease`
and `RedoTombstone` without being decided again. The custody operations a
companion's home and custodian exchange (a hop request, its confirmation, its
settlement, a custody question) are refused to every network caller of a
closed group in `WorldPeerHost`, before the server is asked anything: a guest
carries no title, and nobody else is admitted.

### What a guest may be

A guest's reservation is refused by name unless every member is a human seat
(`WorldTransferReservationMember.LandsHuman`) with no companion, and its
traveller identity was minted in the guest's own session: the incarnation's
authority and the slot it departed from both name the session, and it carries
no title. Its commit is refused unless every member lands at the destination's
own seat spawn (`WorldTransferArrival.Spawn`), with no action registers, no
carried motion, no companion and no body to drive.

So a guest brings a copy of its identity projection (a name, a colour, its
speeds, its facts and records) and nothing else. The original stays in the
player's own identity document. What the hosted world's rules write on the
copy is the group's progress, tied to the body: it ends when the body is
retired, by the player leaving, by the reconnect grace running out, or by a
restore. Nothing of it flows back, because a closed group sends no body
outside itself (`WorldInstanceHost.ConstrainTransferInventory`, and its
network refuses outbound connections). Player progress that outlives a visit
needs a record the group owns, which is not built
([open item](open-items.md#a-hosted-world-on-a-client)).

### How long a session lasts in the ledger

A world keeps rows for a guest's session (its transfer keys, its settled
floor, the credentials of its travellers) only while the session holds a live
reservation or a landed traveller there. When the last one goes,
`WorldTransferEscrow.ReclaimGuest` drops every row of the session. The same
rule runs when a crash recovery redoes the crossing log. A guest's commit that
is refused when the session holds nothing else is answered at once, with the
refusal's own name, and writes no tombstone: the key died with the session.

Three things follow.

- The ledger a guest can occupy is bounded by the bodies and reservations it
  holds, which the world's `networkPlayers` cap and its lease limits bound.
- A session the world holds nothing of looks exactly like a session it never
  saw. Its next reservation is a first entry. That is safe, because the world
  retired every traveller of the session before it forgot the session, so
  nothing remains to duplicate.
- The session is reusable for as long as the issuer keeps vouching for the
  key that signs it. The design assumes nothing about how many times a client
  uses a namespace or whether it kept its key.

### A recovery point is a baseline

A capture of a closed group stores each world's checkpoint with one more fact
in it: `WorldTransferEscrowCheckpoint.RewindBaseline`, the group's authority
names (`WorldReleaseRewindBoundary.Baseline`). Restoring a checkpoint that
carries it ends every traveller whose origin is outside those names, inside
the restore itself (`WorldPersistence.EndRewoundGuests`):

- each such body is removed the way a rolled-back arrival is
  (`WorldGrants.RollbackTransferredEntity`), with its grants and its held
  input;
- the escrow drops every row of an outside source or an outside incarnation
  (`WorldTransferEscrow.EndOutside`);
- the host drops every forwarding route of an outside incarnation.

The restore function is the same one a candidate silo, a crash recovery, a
history seek and a replay shadow call, and what it does depends only on the
checkpoint's bytes. So every reader of a baseline derives the same guest-free
world, and no tick, rule or admission ever sees a restored guest. A world that
never steps is covered, because nothing waits for a tick. The first checkpoint
the restored world writes is an ordinary one: it holds no guest and no
baseline mark.

The discarded history cannot come back through storage either. A restore
publishes the baseline under a new writer epoch with an empty journal
(`WorldAuthorityBlobStore.PrepareIntentionalRestoreAsync`), so no crossing
record written after the point is ever redone.

A traveller is classified by where its incarnation was minted
(`WorldMobilityIdentity.Incarnation`), which every hop carries unchanged and
every world persists. A guest that entered world A and walked on to world B is
ended at B by B's own restore, and A drops its forwarding route in A's own
restore, with no coordination between them. The classification does not read a
name's spelling and does not depend on which issuer root is in use now.

### What a capture requires

`WorldReleaseRewindBoundary.RequireContained` proves the part of a checkpoint
the baseline keeps:

- the world's own departures are all settled, and no reservation from a
  member of the group is open;
- every forwarding route stays inside the group;
- every body minted outside the group is a transferred human seat, which is
  what the restore knows how to end.

Rows of outside sources are no longer a reason to refuse, because the
baseline ends them. A guest in the middle of joining does not block a
capture: its open reservation is one of the rows the restore drops.

Capture and restore inspection now read one inventory,
`WorldReleaseRewindBoundary.Authorities`: each row's name and the authority
namespace its definition declares.

### What the client does

A client learns its session ended in two ways, and both lead to its seats
standing in its own boot world again, where the ordinary join sends them.

- **A transfer it still holds.** The destination answers a status question or
  a commit with `WorldTransferStatus.Ended`. The client returns the cohort to
  its boot world whether or not it had heard `Committed`, because `Ended` is
  the destination's own authenticated statement that it holds nothing of the
  session. Each returned traveller comes back two ownership epochs ahead, so
  it can never be mistaken for the traveller the ended entry held.
- **A seat it had already handed over.** The traveller's observation is
  refused with `WorldFederationRefusal.SessionEnded`, which a world of a closed
  group answers when it holds no credential for the traveller a guest asks
  after (`WorldTransferEscrow.TravelerEnded`). The client retires the route
  and seats the player in its boot world again as a new body
  (`WorldInstanceHost.ReturnSeatHome`).

`Ended` differs from the two answers a client already handled. `Missing` says
one key is dead and leaves a confirmed commit standing. A lost connection says
nothing and is retried. `Ended` is terminal for everything the session had at
that destination.

## The review's findings

The first design was reviewed and rejected. Each finding is answered here.

**CG-01, a session suffix proves only a choice of namespace.** Accepted. The
design no longer assumes a namespace belongs to one run. The door requires a
typed proof: `WorldAttestedAuthenticator.VerifyAsync` sets
`VerifiedPeerIdentity.Session` only for a claim that carried the session
bytes, and marks it vouched only when the chain ran through a root. The
boundary admits it as a guest only when that root is the host's own issuer.
A directly pinned key that signs a session suffix is not a guest. Renewal
keeps the key and so the namespace. A binding that runs out ends the
connection's credential, the traveller parks, and the reconnect grace retires
it. Revocation below expiry is the refusal snapshot the issuer decision
already leaves unbuilt. Reuse is a first entry, as above.

**CG-02, forgetting a transfer row does not settle the key.** Accepted. The
design does not forget single rows. It forgets a whole session, and only once
the world holds nothing live of it. From then on reserve, commit and status
agree: a reservation is a first entry, and commit and status answer `Ended`
without writing a tombstone, so a full ledger cannot leave the question open
and a compacted "committed" row cannot outlive the body it described. The
client's side is the terminal handling above, which does not raise a floor
over unsettled keys: it settles each key first.

**CG-03, state beyond the companion was unrestricted.** Accepted. The guest
shape is checked at reserve and at commit: human seats only, a spawn arrival
only, no carried motion or registers, no drive reference
(`WorldTransferEscrow.ResumeDrive` is never reached for a guest). The identity
copy is allowed, and who owns writes to it is stated above: the group, for as
long as the body lasts.

**CG-04, mobility aliases need ownership, and a restore must follow the
traveller.** Accepted. A guest may name only an incarnation minted in its own
authenticated session, so it cannot present another traveller's incarnation.
Provenance is the incarnation's origin, carried through every hop and
persisted with the body, the credential and the forwarding route, and each
world's restore ends what it holds by that provenance. A transfer inside the
group that mixes a guest with a native keeps its key and loses only the
guest's incarnation.

**CG-05, first-tick retirement needs a durable start state.** Accepted, and
the first-tick retirement is gone. Guests end inside the restore function,
from a mark the checkpoint's own bytes carry, before events, rules and input.

**CG-06, the client had no terminal outcome.** Accepted. `Ended` settles a
confirmed and an unconfirmed departure alike, and `SessionEnded` returns a
seat that was already handed over. A departure whose commit was confirmed but
whose route was never published is covered by the first case.

**CG-07, reserve and commit alone do not guard crossing state.** Accepted.
Status, abort and acknowledge pass the same door, and status writes no
tombstone for a session the world does not hold. Custody operations are
refused at the network door of a closed group. Recovery uses its own named
path. A caller inside the process must present `WorldTransferCaller.Colocated`
to be a member, so a network caller whose sign-in subject happens to spell a
world's name is not one.

**CG-08, capture and restore used different inventories.** Accepted. Both
read `WorldReleaseRewindBoundary.Authorities`.

**CG-09, departed sessions kept transfer-key capacity.** Accepted. A session
is reclaimed whole when its last reservation or traveller goes, during play
and not only at a restore.

**CG-10, an issuer root rotation moves a live client's namespace.** Accepted
in part. A session is bound to the namespace it began under:
`IssuedKeySigningOracle` refuses to sign once the issuer vouches for its key
under another root or subject, and says so. The group retires that session's
travellers when their grace runs out and reclaims its rows. Provenance is the
old namespace itself, so capture and restore still classify those bodies after
the rotation. What is not built is carrying the running client over to a new
session in place: the player starts the client again
([open item](open-items.md#a-hosted-world-on-a-client)).

## Options weighed

- **Turn off `ClosedGroupRewind` on the deployment.** Players would join at
  once, and release restore would be lost on the one live deployment.
  Rejected: an unreached capability is not unwanted.
- **A join for players that is not a transfer.** The authority would mint the
  seat on connection. That is a second mechanism beside the party join
  [multi-seat client, decision 1](multi-seat-client.md#decision-1-seats-travel-as-members-on-the-lanes-two-authorities-hold)
  settled on, and it would need its own answers to every question here.
  Rejected.
- **Admit by the shape of the name.** Rejected in review: any namespace can
  imitate a spelling.
- **End guests on the restored world's first tick** (the first design).
  Rejected in review: the restored body takes part in that tick, a replay from
  the same bytes keeps it, and a world that never steps keeps it for ever.
- **A fence held in the store's root.** The root would count restores and each
  activation would compare the count with its checkpoint. It works, but the
  restore would then depend on something outside the checkpoint, and a replay
  shadow has no root. Rejected for the mark in the bytes.
- **Remember ended sessions.** A tombstone per session would let the group
  answer an old session differently from an unknown one. It grows without
  bound, and the client does the same thing with either answer. Rejected.
- **Coordinate the restore with live clients.** A restore would tell each
  session to roll back. That needs state the per-run client deliberately does
  not keep. Rejected: `Ended` covers it without the group knowing who was
  connected.

## Attacks it closes

- *A world posing as a player.* A guest needs a session vouched for under the
  host's own issuer root. A closed group trusts no other key.
- *A player posing as a world of the group.* Membership needs a caller inside
  the process, never a name.
- *Value carried into a group that can rewind.* Companions, titled travellers,
  autonomous members, carried motion and drive references are refused by name.
- *One player moving another player's body.* A guest names only incarnations
  its own session minted.
- *A restored player standing beside its live self.* A restore removes guests
  and adds none, and an old session is answered `Ended` whatever it asks.
- *A ledger filled by players who left.* A session with nothing live holds no
  row.

## Where the guarantee stops

- A player is who the issuer's sign-in says, for as long as its binding
  lasts. A stolen sign-in is that player.
- What the group's rules gave a guest ends with its body. A restore discards
  it with the rest of the progress the operator acknowledged discarding.
- A client that is offline longer than the reconnect grace comes back to a
  new body at the world's spawn.
- A restore taken while a guest is in the middle of a hop between two worlds
  of the group is refused at capture, as any unsettled hop inside the group
  already was.
- The process is trusted. A caller inside the silo that presents
  `WorldTransferCaller.Colocated` is a member.

Four things are designed here and not shown by a running check, or not built.
Each is an [open item](open-items.md#a-hosted-world-on-a-client) with its own
check: the join on the deployment itself; a restore after a guest walked from
one world of the group to another; a running client carried across an issuer
root replacement; and a crash recovery of a journal tail in which the tick
retired a traveller between two arrivals, which is older than this design and
is reached by a guest's rejoin as it is by any leave and rejoin.

## Proof

`WorldReleaseCutoverLawTests.AClosedGroupAdmitsAPlayerAsAGuestAndARestoreEndsIt`
boots a two-row silo with `ClosedGroupRewind` and an issuer, and a client host
that signs in through the issuer and joins over QUIC on loopback. In order, it
checks that:

1. the seat's body arrives in the entry row, minted in the client's session;
2. a companion, a member that is not a seat and an incarnation another session
   minted are each refused by name; a session no root vouched for, a session
   another root vouched for, a network caller named after a world of the group
   and a caller that proves nothing are refused as outside; a status question
   from a session the row holds nothing of answers `Ended`; and none of these
   adds a row to the ledger;
3. a guest's commit that carries a mapped arrival is refused by name, and the
   session is then forgotten;
4. a capture with the player present succeeds, and its checkpoint is a
   baseline that still holds the player's rows;
5. the restored rows hold no guest and an empty ledger, at the point's tick;
6. the client's seat comes home and joins the restored group again;
7. once the client is gone and its reconnect grace runs out on the tick, the
   ledger is empty again.

The live check is the RTX 2060's player install joining the hosted authority.
