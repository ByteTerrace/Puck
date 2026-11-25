# A multi-seat client

One client process can seat four players. Today those seats exist only when the
client is its own authority. Joined to another authority, a client is one peer
with one body. This memo designs four seats on one client, each admitted to a
remote authority under its own identity and each moving its own body, whether
the four stand together or each is in a different world on a different server.
It is the design for [a multi-seat client](open-items.md#a-multi-seat-client),
the packet milestone M2 rests on: four-player split screen on the RTX 4070 with
every seat joined to one remote host.

It follows [the MMO foundations memo](mmo-foundations/synthesis.md)'s manner:
each decision gives the options, the evidence, the attacks it closes, what it
costs and the ruling it rests on. Rendering four views inside the frame bar is
[the frame budget](frame-budget.md)'s work and holding one world resident once
is [streaming and memory](streaming-and-memory.md)'s; this memo owns the seats,
their identity, their admission and their input.

Words used throughout, as [a hosted world on a client](remote-content.md) uses
them: an **authority** simulates a world; a **client** presents a world another
authority simulates; a **stream** is one recipient's deliveries from one
authority. A **seat** is one player's place on a client: its devices, its
identity, its camera and the body it drives. A **member** is one traveller of a
transfer's reservation.

## Summary

- **A client's seats travel as members of one reservation.** A client is
  already an authority of its own, and its seats already cross to another
  authority as a party in one reservation over the lanes the two authorities
  hold. Joining a remote host with four seats is that crossing, not four
  connections.
- **Each member proves its own identity to the destination.** Today the
  destination learns only which authority handed the travellers over. A member
  gains a signed claim over a challenge the destination issues for it, verified
  by the same door a directly connecting peer passes. A member with no claim is
  a guest of the client's authority, admitted only where the author's row says
  guests may be.
- **One replica of a world for all the seats in it.** Today every seat opens
  its own stream and its own mirror of the same world. The stream belongs to
  the client: one stream for each world any of its seats is in, disclosing the
  union of what those seats may see, each row marked with the seats it is
  disclosed to.
- **A seat's input stays its own.** Each seat's batches already travel on the
  shared lane under that seat's route credential and are admitted under that
  seat's principal. Nothing is merged.
- **A seat that leaves takes only itself.** Each seat has its own route. A
  seat that crosses to a world on another server opens that world's stream and
  proves itself to the new authority; the other seats and their replica are
  untouched. Cost follows how far apart the party is.
- **Authors decide the range.** Whether a party is leashed, whether a door
  carries the whole party, whether close seats share one view, and whether a
  world admits unproven co-seats are authored. The engine supplies the
  mechanisms and the standard package the defaults.
- **A client's authority is a per-run session under its verified credential, and
  a departed client's travellers lapse on the world's reconnect grace.** A
  non-listening client boots an ephemeral seat container with no durable world
  authority to preserve across restarts. Its federation handshake signs a fresh
  per-run session nonce beneath its verified credential
  (`<domain>/<subject>/<session>` for a key, `<oid>/<session>` for Entra), so
  rejoins and co-running clients sharing a document namespace or key never
  collide on transfer ids, source floors or incarnations. When every stream of a
  transferred traveller ends, the authority parks the body and retires both the
  body and its mobility admission once `bodies.reconnectGraceSeconds` elapses
  without a resume.
- **Where the guarantee stops:** the destination knows that a holder of each
  seat's key was present at this client when the seat was admitted. It cannot
  know which person holds which controller, and a client that holds four keys
  can drive four bodies. Seats that share a screen share what the screen
  shows.

## What exists

Each line was read in this tree; a plan's citations are leads to recheck.

- **Seats and their routes.** A client has up to
  `WorldBodiesLimits.LocalSeatCount` seats. `WorldSeatAuthorityRouter` holds one
  authority claim per seat (`WorldAuthorityRoute`: an endpoint, an entity and
  an epoch), published by compare-and-swap, and rendering, input, audio and
  read-back all consume that claim. A seat's route can name any authority.
- **Input is submitted per seat and per authority.**
  `WorldClient.SubmitAuthorityIntents` walks every seat whose route names the
  endpoint being stepped and submits that seat's batch under
  `PlayerRoster.PrincipalOf` for its slot. `IServerLink.SubmitIntent` documents
  that a connection carries up to four submissions a tick, one per local seat.
- **A client is an authority.** A `--connect` client boots its own copy of the
  world and sends every seat that stands in it to the remote destination
  (`WorldInstanceHost.FollowRemoteJoin`): the seats the document seats at
  boot as one party in one reservation, and a seat that joins later by
  itself. The transfer runs over `WorldRemoteAuthority`'s ordered lanes
  (`WorldFederationLane`: one for the transaction, one for routed submissions),
  authenticated by the client authority's federation key
  (`--federation-key-file`).
- **A reservation already carries a party.** `TransferScope.Party` moves the
  source's whole active local-seat set. `WorldTransferReservationRequest` has
  `Members`, each a `WorldTransferReservationMember` with its principal, its
  identity projection and its mobility identity, and `PartyAllOrNothing` says
  whether a refused member refuses the rest. A member that keeps a live source
  lands as a remote human (`WorldTransferReservationMember.LandsHuman`) and
  counts against the destination's `networkPlayers`.
- **Admission of an arrival names the authority, never the traveller.**
  `WorldAdmissionDoor.TryAdmitArrival` takes the authenticated source
  authority's namespace and answers one `WorldAdmissionVerdict` whose identity
  fields name that authority. Every member of a reservation is admitted under
  it (`WorldGrants.TryAdmitVerifiedParticipant` with the lease's verdict). The
  member's identity projection is unsigned, and
  [worlds](../architecture/worlds.md#joining-authority-and-admission) records
  that a remote incarnation claim is unauthenticated.
- **A directly connecting peer does prove itself.** `WorldAdmissionDoor.TryAdmit`
  verifies a signed claim and its chain against the world's `admission` rows
  (`WorldAdmissionTrustMode.SignsDirectly` and `Vouches`), requires the claim to
  carry a challenge bound to both transport keys
  (`WorldAdmissionDoor.BindChallenge`), and answers a verdict naming the
  claim's own domain and subject. `WorldAdmissionDoor.TryMatchOAuthEntry`
  matches an issuer and subject a trusted host validated.
- **Every travelling seat opens its own stream.** A committed remote transfer
  builds one `WorldRemoteAuthority` for each local traveller, named
  `$traveler/<machine>/<slot>` (`WorldInstanceHost`'s transfer publication),
  with its own `WorldRemoteRouteCredential` and its own observation
  (`WorldFederationRequest.ObserveTraveler`). Four seats in one world are four
  streams and four mirrors of that world.
- **A seat's input is forwarded under its credential.**
  `WorldRemoteForwardedAuthority.TryForwardIntent` sends a submission with the
  traveller's route credential; the destination applies it under the admitted
  body's principal and its Drive grant (`WorldTick.ApplyFederatedIntents`), and
  answers per owner (`WorldInputAnswer`).
- **The leash and the party door exist.** The body step has a tether
  constraint ([physics](../reference/physics.md#the-tether-constraint)), a
  transfer has `partyAllOrNothing`, and split views are
  [panes](open-items.md#panes-split-screens-the-player-drives).

## Decision 1: seats travel as members, on the lanes two authorities hold

**Options.**

1. *A connection per seat.* Each seat dials the host as a peer
   (`WorldPeerHost`) and proves itself in that connection's handshake. Identity
   needs nothing new. It costs four transports, four handshakes and four
   streams for one screen, and a seat's connection has no relation to the
   client's own authority, so a crossing back home is a different mechanism
   from the crossing out.
2. *Members of one reservation.* The client's authority reserves for its whole
   party over the lanes it holds with the host, as it does for one seat today.
   Transport, ordering, custody and recovery are the transfer's, already built
   and already checkpointed. Identity per member is the gap, which
   [decision 2](#decision-2-a-member-proves-its-own-identity) closes.

**Ruling: members of one reservation.** A portal already carries a party
between worlds this way, and a seat that walks through a door to another server
later is the same crossing. One mechanism serves the join, the party door and
the onward handoff. The packet's direction says the same: one connection per
authority, not per seat.

What changes: a `--connect` client transfers its party
(`TransferScope.Party`), not seat 0, and a seat that joins the client later
crosses when it joins. The member count spends the destination's
`networkPlayers` one each, as it does today.

**Attacks it closes.** A client cannot open more streams than it has worlds to
show, since streams are per world ([decision 3](#decision-3-one-replica-of-a-world-for-the-seats-in-it)).
A refused member cannot wedge the rest: `PartyAllOrNothing` is the author's and
the standard package's default for a join is to admit who can be admitted.

## Decision 2: a member proves its own identity

**What must be decided.** Who the destination believes a member is, and on
what evidence.

**Options.**

1. *The client's authority vouches.* The destination keeps today's rule: the
   arrival verdict names the source authority and the member's identity is
   whatever that authority says. Any modified client can then arrive as any
   user. It is the right rule for a companion or a guest and the wrong one for
   a player's account.
2. *A claim signed by the seat's key, bound to the transfer alone.* The member
   carries a claim over the transfer's key (source authority, transfer id,
   member id) and the destination authority's identity. No extra round trip,
   since a transfer id is used once (`WorldDrainedTransferIds`). Its freshness
   rests on a second replay argument beside the door's own.
3. *A claim over a challenge the destination issues.* The reservation's reply
   carries a nonce for each leased member. The commit carries each member's
   signed claim over that nonce, bound to both authorities' transport keys and
   to the member's place in the transfer. The destination verifies each through
   `WorldAdmissionDoor.TryAdmit`, the door a directly connecting peer passes.

**Ruling: the challenge.** A reservation is already two steps, reserve then
commit, so the challenge costs no round trip, and the door's existing rule (a
claim must carry the exact endpoint-bound challenge this attempt was issued)
covers replay with no new argument.

**The design.**

- *The challenge.* `WorldTransferReservationReply` gains one nonce for each
  leased member (`WorldAdmissionDoor.NewChallenge`). The destination keeps them
  with the lease (`WorldTransferEscrow`'s lease record), so they are
  checkpointed with it and die with it.
- *The binding.* The claim's payload is
  `ChannelBinding.Bind` over the nonce, the two authorities' transport key
  hashes, and a label that names this use and carries the transfer key and the
  member id. A claim made for one member of one transfer verifies for nothing
  else.
- *The claim.* The commit carries, for each member, the claim and its chain
  (zero or two bindings, as the door's trust modes allow), or nothing.
- *The verdict.* A member with a claim that verifies is admitted under the
  verdict its own domain and subject earn from the `admission` rows. A member
  with no claim is a **guest**: admitted under the arrival verdict of the
  source authority, and only if that row says it admits guests
  (below). A member whose claim fails is refused by name; it is never demoted
  to a guest, so a failed proof cannot be used to probe for the guest row.
- *The key stays with the seat.* The client reaches a seat's key only through
  a seat credential source: given a challenge payload it returns a claim and
  chain, or declines. The engine's own source reads a key file for each seat,
  as `--federation-key-file` does for the authority; a platform identity
  provider is another implementation behind the same abstraction, selected as
  `WorldConnectionAuthentication` selects the connection's authenticator. No
  provider type crosses into the engine (AGENTS.md, provider independence).
- *An OAuth identity.* A member may instead carry a token for the
  destination's installed authentication provider, which validates it and
  yields an issuer and subject for `WorldAdmissionDoor.TryMatchOAuthEntry`. The
  token is bound to the challenge by the provider's own audience and nonce
  rules; a provider that cannot bind a token to a nonce is not accepted for
  members.

**The invariant.** A member is admitted under an identity only if a holder of
that identity's key answered this destination's challenge for this member of
this transfer. **What enforces it:** `WorldAdmissionVerdict` is produced only
by `WorldAdmissionDoor`, and the participant door
(`WorldGrants.TryAdmitVerifiedParticipant`) accepts nothing else, so no ingress
can assemble an identity; the member path gains exactly one call, to
`WorldAdmissionDoor.TryAdmit` with the lease's stored nonce. **Where it
stops:** the proof is of key possession at admission. It says nothing about
which person holds which controller afterwards, and a client holding four keys
drives four bodies.

**What holds today, before this decision is built.** Nothing enforces the
invariant yet: a member is admitted under the arrival verdict of the authority
that handed it over, so every seat of a client is that client's authority and
no more. Two things the build must keep from the path that exists. The
destination resolves a traveller's principal from its own transfer table
(`WorldServer.TryTransferredPrincipal`) and stamps it on every routed
submission and input (`WorldLocalForwardedAuthority.TryApplyIntent`,
`WorldLocalForwardedAuthority.TryApplySubmission`), so a client never names
the principal it acts as; a member's verdict must be bound to that table's
entry at the commit and nowhere else. And a connection's credential is
rechecked against the admission policy an install published
(`WorldServer.AdmissionPolicy`), on the connection's own thread; the member's
claim is verified the same way, against that list, and commits under the
authority gate only while the list is still the same instance.

**Attacks it closes.**

- *A client claims another user's account.* It holds no key that answers the
  challenge, and a failed claim is a refusal.
- *A recorded commit is replayed.* The nonce is the lease's and a lease is
  consumed by its commit.
- *A claim is moved to another seat of the same party* (to take the member
  with the better grants). The member id is inside the bound payload.
- *A claim made toward one host is forwarded to another.* Both transport key
  hashes are inside the bound payload.
- *A host farms proofs to use elsewhere.* A host can only obtain a claim over
  its own key hash, which no other host presents.

**Cost.** One signature per member per admission, verified off the tick thread
as a peer's is (`WorldPeerHost` verifies against a snapshot of the document and
commits on the tick thread only if the `admission` section is the same
reference). The reservation's commit handler does the same.

**The authored surface.** An `admission` row already says which arrivals it
admits by kind ([a hosted world on a client](remote-content.md#decision-5-a-remote-seat-plays)).
A federated-authority row gains an authored count of guests it admits per
arriving client (none unless authored). The standard package's visitor
template authors none, and a couch-play template authors three, so a signed-in
player can bring friends where the world's author allows it.

## Decision 3: one replica of a world for the seats in it

**What must be decided.** How many streams and mirrors a client holds for a
world that several of its seats are in, and what each seat may be shown.

**Options.**

1. *A stream per seat* (today). Four copies of one world on one client, four
   times the authority's egress for one screen, and four residencies for
   [streaming and memory](streaming-and-memory.md) to unite after the fact.
2. *One stream per client per world, carrying the union.* The authority
   composes one recipient view for the client's members in that world. The
   client holds one mirror, one residency and one mesh store for it, and its
   panes are views of that one world.

**Ruling: one stream per client per world.**

**The design.**

- *The recipient is the group.* An observation names the set of route
  credentials it serves (`WorldFederationRequest.ObserveTraveler` takes one
  today). The authority's recipient view for the group
  (`WorldRecipientView`) discloses a row if any member may see it.
- *Each disclosed row carries the members it is disclosed to,* as a mask over
  the group's members. The client presents a row in a seat's pane only when
  that seat's bit is set. A hand of cards dealt to seat 2 is in the client's
  mirror and is drawn in seat 2's pane alone.
- *Interest is the group's.* Under
  [replication](replication.md#decision-6-interest-and-priority-under-a-byte-budget),
  a body's priority for the group is the greatest of its priorities for the
  members, and the group spends one byte budget. A party standing together
  costs about one player's egress; a party spread across a zone costs what
  its union costs.
- *Answers stay per owner.* `WorldInputAnswer` already names its owner, so the
  client matches each seat's answered sequence on the one stream, which is
  what [replication](replication.md#decision-7-the-seats-own-body) needs to
  predict each seat's own body.
- *Membership follows the routes.* A seat joining or leaving the world adds or
  removes its credential from the group. The stream is cut and re-primed only
  for what the leaving member alone could see: rows whose mask becomes empty
  are withdrawn through
  [the withdrawal chokepoint](disclosure/withdrawal-chokepoint.md).

**The invariant.** A client is delivered a row only while at least one of its
admitted members in that world may see it, and presents it only in the panes
of members that may. **What enforces it:** the group view is built from the
members' own views by union, so it cannot contain a row no member's view
holds; the mask is written by the same pass. **Where it stops:** one process
holds the union, and one screen shows every pane. Two people on one couch can
read each other's panes, and a modified client can show any row it was
delivered in any pane. An author who needs a secret kept from a co-seat
authors that the secret's world admits no co-located members
(the guest count and a per-row cap on members per client), and the engine
refuses the second member rather than pretend to hide the row.

**Attacks it closes.** A seat cannot widen what the client receives beyond the
members' own disclosures by joining and leaving, since the mask is recomputed
and emptied rows are withdrawn. A client cannot hold a stream for a world none
of its seats is in: the group's last credential closes it.

## Decision 4: input is each seat's, on shared lanes

Nothing new is designed here; the rule is stated so the build keeps it.

- A seat submits to the authority its route names, under its own principal,
  once for each tick it has observed and at once when its input changes
  ([a hosted world on a client](remote-content.md#decision-5-a-remote-seat-plays),
  whose push shortfall is the delay this closes).
- The client forwards each seat's batches under that seat's route credential.
  The destination admits them through `WorldTick.ApplyFederatedIntents` under
  the member's own principal and Drive grant. A batch naming a body its
  credential does not hold is refused by name.
- Batches are never merged across seats: each owner has its own epoch and
  sequence (`WorldInputOwner`).

**The invariant.** A seat's input moves only a body that seat's admitted
principal may drive. **What enforces it:** the Drive check on the submitting
principal at the one door a remote batch enters. **Where it stops:** as
[decision 2](#decision-2-a-member-proves-its-own-identity) says, the client
process holds every seat's credential.

## Decision 5: a seat that leaves takes only itself

- Each seat's route is its own cell of `WorldSeatAuthorityRouter`, and an
  onward handoff retargets that cell alone
  (`WorldSeatAuthorityRouter.CompareExchangeEntity`).
- The client's replicas are the distinct worlds its seats' routes name. A
  replica is held while any seat is in it and released when the last leaves.
  Four seats in four worlds are four replicas and four streams; four together
  are one of each.
- *Identity at the next authority.* A handoff from host A to host B is between
  the two hosts, and the client is not on that lane. B admits the traveller as
  A's arrival, a guest of A, until the seat proves itself: B issues the
  member's challenge on the route update the client already receives, and the
  client answers on the routed lane it opens to B. Until then the traveller
  holds what B's row gives a guest of A. A claim is always made by the key's
  holder to the authority that will rely on it; a host never relays one.

**Attack it closes.** A host cannot vouch a traveller into an account at
another host: the second host believes only a claim over its own challenge.

## Decision 6: authors decide the range

The engine builds mechanisms; each of these is `.puck`, and the standard
package (`worlds/standard/`) chooses the defaults a world imports or
overrides.

| Policy | Mechanism it is authored over |
|---|---|
| A party is leashed | The tether constraint between the party's bodies, or a range rule that reads their poses |
| A door carries the whole party or one seat | The transfer's scope and `partyAllOrNothing` on the destination row |
| Close seats share one view | A camera program that frames the party, and the panes' layout rule |
| Unproven co-seats are admitted | The guest count on the federated-authority admission row |
| A secret is kept from co-seats | The cap on members per client on the row that admits to the secret's world |

## Decision 7: what names a client's authority, and when its travellers lapse

**What must be decided.** What federation `sourceAuthority` identity a
non-listening client (`--connect`) presents across restarts and across multiple
client processes that load the same world package or hold the same development
key, and how an authority reclaims a transferred human traveller when its client
exits without sending `SessionRequest.Leave`.

**Evidence from the tree.**

- A transfer is keyed by `WorldTransferKey(SourceAuthority, TransferId)`
  (`WorldTransferEscrow.Reserve`), guarded by a monotonic `SourceFloor` per
  `SourceAuthority`, and each arriving body is indexed by
  `WorldEntityAddress(Authority, Index, Generation)`.
- A client started without a persisted crossing log resets `NextTransferId` to
  `0` and seat `Generation` to `1` on every run. When `Program.cs` derived the
  signing oracle's subject solely from `host.authorityNamespace ?? "boot"` and
  `WorldAttestedAuthenticator.VerifyAsync` returned `claim.Header.Subject`
  alone, a second run of the same client was refused with
  `transfer 0 was already leased or answered here`, and two clients booting the
  same world package (even with distinct keys!) or sharing a pair key minted
  identical `sourceAuthority` strings and colliding incarnations.
- A signed-in player's client (`IssuedKeySigningOracle`) mints a per-process
  session and authenticates as `<issuer domain>/<subject>/<session>`, retaining
  that identity across socket reconnects within one run while isolating separate
  runs and windows.
- On `WorldPeerHost`, a transferred federated human (`IsRemoteHuman &&
  IsAuthorityTransferred`) was retired only on an explicit forwarded
  `SessionRequest.Leave`; a client that exited or crashed closed its
  `ObserveTraveler` and `IntentStream` lanes while leaving its traveller body
  active and holding a `networkPlayers` slot and exclusive `Drive` grant.

**Options.**

1. *A durable per-client authority identity across restarts, backed by a client
   crossing log.* The client persists `NextTransferId` and seat generations in
   its local state directory and signs as `<key-domain>`. Fails in three places
   at scale: two windows or machines holding the same player credential still
   collide live on `TransferId`, `SourceFloor` and `WorldEntityAddress`; a
   fresh install, browser host, wiped state directory or un-flushed crash loses
   its counter and is fenced out by the server's `m_answered` / `SourceFloor`
   until the server restarts; and it treats a client's transient `boot` launchpad
   as a persistent federated world, although player continuity belongs to
   seat/member identity ([decision 2](#decision-2-a-member-proves-its-own-identity)),
   never to the client's boot container.
2. *A per-run session-qualified client authority identity bound into the
   federation handshake proof, paired with stream-liveness parking and
   reconnect-grace retirement at the authority.* A listening world (`--listen`
   or a silo row) keeps its exact `host.authorityNamespace` so named
   `FederatedAuthority` rows match world peers unchanged. A non-listening client
   mints one 128-bit session id at process boot, signs `challenge || session`
   under its key in `LocalKeySigningOracle`, and authenticates as
   `<domain>/<subject>/<session>` in `WorldAttestedAuthenticator`, whose domain
   is the direct key's or the issuer root's fingerprint. Transport reconnects within
   that run keep the same identity, so in-doubt commit recovery,
   `ObserveTraveler` and `IntentStream` resume seamlessly. When all active
   traveller streams (`ObserveTraveler` and `IntentStream`) for a live local
   transferred human close (or all federation connections from `sourceAuthority`
   close before a stream attaches), `WorldPeerHost` parks the peer through
   `DisconnectPeerConnection`; re-attaching a stream before
   `bodies.reconnectGraceSeconds` expires unparks it and re-mints its `Visitor`
   grants, while expiry on the simulation tick (`ReclaimExpiredParks`) retires
   both the body and its `WorldTransferEscrow` mobility admission.

**Ruling: per-run session-qualified client authority identity with
stream-liveness grace.** It gives key-attested and signed-in clients one
namespace shape, requires no durable crossing log on disposable client
installs, isolates both sequential rejoins and concurrent clients sharing a
document namespace or key, and reclaims abandoned slots deterministically on the
authority's tick.

**The invariants.**

- *Session binding:* a connection authenticates as `<domain>/<subject>/<session>`
  only if the private key for `<domain>` (verified against an `admission` trust
  entry that admits `<subject>`) signed the exact `challenge || session` bytes
  issued on that connection, and every subsequent transfer, observation, intent
  and submission leaf on that connection must carry that exact `sourceAuthority`.
  **What enforces it:** `WorldAttestedAuthenticator.VerifyAsync` derives
  `VerifiedPeerIdentity.Authority` exclusively from the verified
  `SignedAttestation` header and signed payload bytes, and `WorldPeerHost`
  checks ordinal equality against the connection's authenticated
  `sourceAuthority` at every frame door.
- *Deterministic grace retirement:* whether a disconnected traveller is still
  embodied or retired at tick `T` is a pure function of the ordered
  `PeerDisconnected` / `PeerAdmitted` lifecycle events recorded on the tick
  thread and the world's compiled `PopulationReconnectGraceTicks` evaluated at
  `ReclaimExpiredParks(T)` — never wall-clock time on a socket thread. **What
  enforces it:** socket threads only enqueue tick-thread lifecycle transitions
  when a traveller's active stream count crosses zero or rises from zero while
  parked; `ApplyPeerDisconnected` stamps `ParkedUntilTick = tick + graceTicks`
  and `ReclaimExpiredParks` sweeps deadlines strictly by simulation `tick`,
  after which `WorldTransferEscrow.PruneDepartedAdmissions` drops departed
  mobility and border records.

**Attacks it closes.**

- *Cross-client namespace collision and denial of service:* another client
  booting the same `.puck` world cannot squat `sourceAuthority = "debug"` or
  advance its `SourceFloor` to fence other visitors out, because `<domain>` and
  `<session>` come from the verified signature.
- *Session hijacking across keys:* a holder of one trusted key cannot claim
  another key's `<domain>` or `<subject>`, nor forge an already-admitted
  client's `<session>` under that key.
- *Slot exhaustion by crashed clients:* a client that vanishes without sending
  `Leave` holds its `networkPlayers` slot at most for the world's authored
  `bodies.reconnectGraceSeconds`.

## Decision 8: a remote seat is judged by its authority's state

A remote seat met five failures on a loaded machine that it never met on a
quiet one: a seat delivered no body, a refused control line, a wall with the
stick held and the body still, a pose that did not land, and a lift step that
never settled. A loaded host and a wide-area link are the same condition to a
client: deliveries arrive late, in bursts, and with gaps. Each failure was the
code reading wall time, or an answer the authority had not yet given, as if it
were the authority's state.

**The reproduction is a maintained option of the pair.** Every peer stream
can run on a seeded delivery schedule (`PeerDeliverySchedule`): each message is
held up to the jitter, one in eleven reads is held two to four times as long so
the messages behind it arrive as a burst, and writes stall in windows. Order is
kept and steady throughput is not throttled. `Puck.World` takes
`--peer-jitter-ms` and `--peer-jitter-seed`; `puck benchmark --remote` takes
`--jitter-ms` and `--jitter-seed` and hands each process of the pair its own
seed derived from the run's, so a failure under a schedule reproduces.

**What each failure was, and the fix at its cause.**

- *No body delivered.* The client acknowledged each delivery by writing on the
  same loop that reads them, so a stalled write stopped reads, the authority's
  queue for that subscriber filled, and the authority detached the observer.
  The acknowledgement is cumulative (the latest stream stamp), so the client
  keeps only the latest and writes it from its own task
  (`WorldRemoteAuthority`'s observation loop). The authority's per-subscriber
  bound (`WorldFederationProjectionSink.PendingDeliveryLimit`) holds seconds of
  deliveries, not a fraction of one. A traveller's stream also ended whenever
  the authority's document changed, found by comparing whole serialized
  projections at every delivery; the feed sends such a change as a delta, so
  the stream no longer compares them. Prototype fetches take their own lane
  (`WorldFederationLane.Content`), so a large fetch never queues a seat's input
  behind it.
- *A refused control line, and a pose that did not land.* A remote seat's
  `body.pose spawn:<id>` is resolved by the authority that declares the spawn
  (`WorldCommand.PoseSpawn`), not by the client's copy of a document it may
  not stand in; a spawn only the host's boot world declares carries the body
  home there, as a local seat's does. The pose step counts as landed when a
  delivery shows the body's pose epoch, authority or body changed from the
  read before the pose, and waits for it in the authority's ticks.
- *A wall with the stick held.* The wall watch starts only once the authority
  has answered the push (`WorldInputAnswer`), and watches only a stick asking
  for enough speed to leave the watch's distance in its ticks.
- *A tight step that never settled.* Against a remote clock the stick scaled a
  delivered speed that was already a lag old, under a floor of a fifth of a
  walk, so a body at rest beside a 10 cm goal was sent off at full deflection
  and oscillated about it. The stick asks for an absolute speed instead. Toward
  a stop that speed follows the braking curve started a measured control lag
  late, and is at most half the speed that closes the remaining distance in one
  lag, so each overshoot is smaller than the distance it had to cover and the
  approach converges at any lag. A corner plans against the latest lag rather
  than the longest, since a turn taken late only widens it; braking for every
  corner against the longest lag left a narrow lane walked at a crawl, and not
  braking at all bounced the body between its walls.
  A stop plans against the longest round trip met lately, decaying eight
  ticks each second, since a burst after a held delivery is random. Running
  raises only the top speed the stick's speed is asked against, so a held run
  never outruns a stop; a dash, whose burst cannot be taken back, waits for
  room to stop from twice a run. The step lands once the body is inside its
  goal with the release answered and the body at rest.
- *A chapter's clock.* A chapter's time limit counts the authority ticks
  delivered for the seat's body, so a slow delivery spends none of it; the
  client's own ticks bound it only as a backstop several times longer.

**Invariants and what enforces each.**

| Invariant | Enforced by | Where it stops |
|---|---|---|
| A route step is judged from authority state the client was delivered | `WorldSeatDriver` reads the body only through `WorldSeatSenses`, which reads the delivered mirror and the authority's input answers | The client-tick backstop still ends a chapter whose authority never delivers |
| A delivery is never refused for a slow acknowledgement | The acknowledgement is one latest-stamp slot written by its own task; the read loop never writes | A consumer behind by more than the subscriber bound is still detached and re-primes |
| A remote spawn names the authority's own row | `PoseSpawn` is resolved in `WorldDocument.ApplyCommand` against the authority's definition | A spawn neither world declares is refused by name |
| A jittered run reproduces | Each stream's schedule is a `Pcg32XshRr` stream from the run seed, the side and the stream's ordinal | Ordinals follow connection order, which the host's scheduling decides |

**Where it stops: a seat that steers from delivered state is as slow as its
lag.** With the five failures fixed, the remote debug route arrives on every
chapter with no jitter, in both `--remote local` and `--remote issued`, and
under 250 ms jitter on one schedule. On others the city painting, the
route's longest walk, runs just past its 120 s: about 3,700 authority ticks
under jitter against about 1,900 quiet, since every corner and every tight
goal is approached a control lag late. At 500 ms jitter the lift also runs
past its limit, and four seats under jitter meet each other in the city's
terrace lane and at the lift's door. The structural answer is a seat that
steers from its own predicted body, the client running its sent input ahead
of the last delivery and correcting to it, so its control loop no longer
holds the round trip; lengthening the chapter's limit would only hide it.

## The steps

Each closes on a count or a state read from a headless pair on one machine:
two Worlds, one listening and one started with `--connect`, booted from one
staged document that authors four local seats, four visitor slots and the
admission rows the step needs. No check is a timing.

1. **The party joins.** `--connect` transfers the client's party, and a seat
   that joins later crosses when it joins. The second-seat defect the packet
   records is reproduced first with two local seats in one process.
   *Check:* the host's roster reads four remote humans from one source
   authority; the client's four routes name the host; a scripted input on each
   seat in turn moves that seat's body on the host and no other
   (`body.where` for the four); `world.tick.federated-batches` rises for four
   owners and `world.tick.federated-restarts` stays at zero.
   *Built.* The seats the document seats at boot cross as one party in one
   reservation (the join waits for every eager seat, and holds its slot for
   `WorldInstanceHost.RemoteJoinHoldSeconds`, since a host can take seconds
   over its first ticks and a lapsed reservation decides nothing). Each seat's
   input moves its own body: on a headless pair over `worlds/minimal`,
   `world.at <tick> seat <n>` held one stick on each of the four seats and the
   four travellers on the host moved along four different axes, and
   `puck benchmark --world worlds/minimal --route walk --remote local --seats 4
   --headless` reports all four seats arrived. What had stopped the bodies
   moving was not the seats: a connection thread on the host read the live
   document while a tick was prepared, which threw and closed the lane that
   carried a seat's input admission
   ([the packet](open-items.md#a-multi-seat-client) records it). Not checked
   here: `world.tick.federated-restarts` across a long run, and the debug
   world on four seats.
2. **A member proves itself.** The nonce in the reply, the claim in the
   commit, the member path through `WorldAdmissionDoor.TryAdmit`, the seat
   credential source and its key-file implementation, the guest count.
   *Check:* with four pinned rows the host's roster reads four identities; a
   seat with a key the host does not pin is refused by name while the others
   land; with no guest count authored a seat with no key is refused and with
   one authored it lands under the authority's verdict; a commit replayed
   against a second reservation is refused for its challenge; a claim moved
   between two members of one commit is refused for its challenge.
3. **One stream per world.** The group observation, the union view with its
   masks, the client's one mirror per world.
   *Check:* observations opened on the host equal the client's distinct
   worlds, one for four seats together; the client holds one mirror; the
   bytes of a delivery with four seats standing together are within a few
   percent of one seat's; a row disclosed to one seat is present in the
   mirror with that seat's bit alone.
4. **A seat leaves.** A seat crosses onward to a second host while three
   stay. The re-proof on the routed lane.
   *Check:* the client holds two streams; the three seats' routes and stream
   are unchanged (no re-prime, counted); the leaving seat is a guest at the
   second host until its claim lands, then holds its own identity's grants.
5. **Seat-masked presentation and the authored caps.** The pane filter, the
   members-per-client cap, the standard package's templates.
   *Check:* a windowed leg for the lead: a row disclosed to one seat draws in
   that seat's pane alone; a world capped at one member per client refuses
   the second seat by name.

Step 1 depends on nothing. Step 2 depends on step 1. Step 3 depends on step 1
and on [a hosted world on a client](remote-content.md#the-build-order)'s
manifest step for the debug world itself. Steps 4 and 5 depend on 2 and 3.

## How it behaves at scale

- *A town.* Four seats standing in a town of hundreds are one group, one
  stream and one byte budget at the host. The host's per-recipient work
  (the view, the priority fill) runs once for the client, not four times.
- *A party that scatters.* Each seat that crosses to another world or server
  adds one stream there. The first host's group shrinks by one credential and
  its stream continues for the rest.
- *A host that restarts.* The lease, its nonces and the admitted identities
  are in the host's checkpoint (`WorldTransferEscrowCheckpoint`,
  `WorldAdmissionVerdict.Restore`), so a commit in flight resolves as it does
  today and an admitted member keeps its identity without a new proof.

## Systems considered

- `WorldPeerHost` and its handshake: the door a member's claim reuses
  (`WorldAdmissionDoor.TryAdmit`), not the transport.
- `WorldTransferEscrow` and the reservation's two steps: carry the nonce and
  the claim; no new exchange.
- `WorldSeatAuthorityRouter`: already one route per seat; unchanged.
- `WorldRemoteAuthority`'s per-traveller observer: replaced by one observer per
  world, keyed by the client's authority and the world, with a set of
  credentials.
- `WorldRecipientView` and the disclosure plan: the group view is a union of
  member views; no second disclosure mechanism.
- `IAuthenticator` and `WorldConnectionAuthentication`: the pattern the seat
  credential source follows; it is not reused directly, since it authenticates
  a connection and a member is not one.
- The tether, `partyAllOrNothing` and panes: the mechanisms
  [decision 6](#decision-6-authors-decide-the-range) authors over.

## Open questions and their owners

- *Whether a platform token can be bound to a nonce* is each provider's
  property. The owner of a provider project answers it when that provider is
  added; until then members prove themselves with keys.
- *The four-view frame bar* is read by the benchmark's four-seat route
  ([the benchmark](open-items.md#the-benchmark)), which the measure lane owns.
- *Per-viewport screen destinations* (a screen that shows a different
  destination to each viewer) stay with
  [panes](open-items.md#panes-split-screens-the-player-drives); the seat mask
  of [decision 3](#decision-3-one-replica-of-a-world-for-the-seats-in-it) is
  the input that work needs.
- *A remote seat that predicts its own body*
  ([decision 8](#decision-8-a-remote-seat-is-judged-by-its-authoritys-state))
  is the next lane on this plan. Its check: `puck benchmark --route debug
  --remote local --headless --jitter-ms 250` arrives on every chapter for
  several seeds, the city painting in about its quiet tick count, and with
  `--seats 4`. The lane first searches the client for an existing correction
  easer or replay of sent input to build on (`WorldClient`'s per-entity
  correction easers are the lead).
