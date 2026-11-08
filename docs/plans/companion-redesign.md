# Companion custody protocol

This is the specification the build of the [companion decision](../decisions/companions.md)
follows, and the model that checks it. Prose redesigns miss one interleaving at a
time, and three classes of defect recur (authority from a sender's claim, away
bookkeeping outside the authority record, a boot-random custody key). The protocol is
therefore stated as per-authority state, typed messages and durable records, with every transition's precondition, record and
acknowledgement, and it is checked by an executable small-scope model
(`tests/Puck.World.Protocol.Tests/CompanionCustodyModel.cs`,
`CompanionCustodyModelLawTests.cs`) that explores it under faults and asserts the
decision's invariants on every reachable state. The model is the build lane's oracle: a
protocol change is made in the model first, and the engine follows it.

The owner's rulings stand unchanged: companions are not networked players; a companion is
the player's and travels into other people's worlds; same behaviour by default;
separated or refused means home with a notice; federation stays in scope; a stranded
companion waits in custody and only home recalls it.

## 1. Vocabulary

An owned traveller has a **title** (that it exists and whose it is), an **image** (its
identity projection and registers) and **custody** (the authority embodying it now, at
which epoch). The **title authority** is the home that minted the incarnation; every
other authority that embodies the traveller or holds its detached image is a
**custodian**. A crossing moves custody, never title.

| Identifier | Definition | Engine home |
|---|---|---|
| Incarnation `I` | The companion's mobility incarnation, carrying the title mark | `WorldEntityAddress` minted by home, `Titled` set for an inhabitant of a role placement |
| Custody epoch `e` | The traveller's ownership epoch; every landing, the return home included, lands at `e + 1` | `WorldMobilityIdentity.Epoch`, advanced by the escrow's `Advance()` at each landing |
| Hop key `K` | The source-scoped transfer key of the crossing that moves custody, allocated durably before it is sent | `WorldTransferKey`; the allocation is the escrow-once lane's durable `NextTransferId` |
| Member id `m` | A request-local id the source assigns at enqueue; it never changes when the cohort is filtered; a companion's is its incarnation | `WorldTransferReservationMember.MemberId` and `WorldTransferCommitMember.MemberId` |
| Custody ticket | `(home, I, K, e')`: "home recorded hop `K` moving `I` to its destination at epoch `e'`" | `WorldTransferCompanion.Ticket`, on the reservation and the commit member |

The ticket is not a secret and is never trusted on its own. A destination verifies it with
home before leasing the companion (section 5.1); home verifies a return against its own
record (section 5.2). The authenticated channel identifies the party speaking (in process,
the host; remotely, the federation lane's authenticated namespace), and home's record says
which custodian holds the title at which epoch. Nothing a sender asserts is evidence.

**The title mark is part of the incarnation.** Home mints a titled incarnation
(`WorldEntityAddress.Titled`) for every body it spawns into a placement whose role makes
it an owned traveller, and the mark takes part in the identity. A destination refuses a
titled incarnation that arrives without its companion leaf, so a source that strips the
leaf cannot land home's companion as an ordinary traveller; an address stripped of the
mark is a different incarnation, of which home holds no title (`NotMyTitle`), so it moves
no custody and is refused on any return. Players and other untitled travellers cross as
before.

**Keys, leases and tombstones use the existing crossing log**
(`WorldCrossingRecord.cs`: `Allocation(Next)`, `Lease(Key, Floor)`, and
`Tombstone(Key)`, with the reservation's `SourceFloor`). For every
traveller: transfer ids are allocated durably before use (`Allocation` advances `Next`
ahead of the first send, so a crash after a reservation and before the departure never
reuses the id); a key is leased at most once at a destination (`Lease`, with the source's
settled floor); and a tombstone is written before a destination answers `Missing` to a
commit or a status query or refuses a reservation for a key it leased before, so `Missing`
is final and a later reservation or commit under the key is refused. This protocol relies
on those records and does not restate them in the engine; the model carries them as
`Minted`, `Leased` and `Dead` because its invariants depend on them, and a shed image's
later attempts are new allocations (section 5.4).

## 2. Per-authority state

Every durable fact is a function of the authority's records through one reducer
(`CustodyModel.Fold`; the engine's checkpoint restore, crossing-log recovery and tape
re-drive call the same function), and the model checks that reducer against an
independent derivation (`CustodyModel.Shadow`). The only volatile state is leases,
pre-departure attempts, verification answers, retry counters and fault marks, which a
crash clears.

**Home** derives, per incarnation it minted:

| Custody | Meaning | Held slot |
|---|---|---|
| `Home` | Embodied at home, or authorized to depart but not yet departed, or detached by home's own in-doubt departure | Occupied by the companion or held |
| `Pending(K)` | Hop `K` for this incarnation is authorized and not yet settled | Held |
| `Away(X, e)` | Custodian `X` holds the title at epoch `e` | Held |
| `Released` | The author removed the placement's role (a taped install); the title has ended | Free |

plus the current epoch, the highest epoch ever issued, the pending hop, the held slot and
placement, the last committed image, and whether a recall or release has been noticed.
"No row" is never an answer: an incarnation home minted is always in one of these four
states, and an incarnation home did not mint is `NotMyTitle`, which ends nothing.

**A custodian** derives: the bodies it embodies (slot, generation, epoch, ticket, image,
and for a companion the member id it follows and the slot that member landed at), its
in-doubt departures, the attempts a shed image has made, the keys it minted, leased and
tombstoned, which companions are stranded and past their grace window, and the settlement
notices home has not acknowledged.

## 3. Durable records

Every record is written by the deciding authority before the step becomes visible to a
peer, in the crossing log (`IWorldCrossingLog`), captured by every checkpoint and taped for
replay. **Every handler appends at most one record**, so recovery can find the state
between any two of them; the explorer crashes there. The reducer is total over any prefix.

| Record | Authority | Written when | Engine form |
|---|---|---|---|
| `Minted(K, to, memberIds[, image])` | Source | Before the first message of a crossing; a shed image's later attempt names the departure it carries | `Allocation(Next, Mint(K, to, companions))`: the companions with their title authorities, from which the source's owed notices and its abandons derive |
| `Hop(K, from, to, [(I, e, e+1)…])` | Home | It authorizes the hop of every companion it grants under one key; each entry is that title's own fact | `WorldCrossingRecord.Hop` |
| `Leased(K, from, members with slots)` | Destination | Before it answers `Reserved` | The escrow-once lane's `Lease(Key, Floor)` |
| `Departure(K, to, members with tickets, slots and images, shed)` | Source | Before it sends the commit; a shed departure before any reservation | `Departure`; `WorldLandedMemberCheckpoint` carries `Companion`, `Ticket`, `Image`, `Shed` |
| `Arrival(K, from, members with slots, generations and epochs, readopted ids)` | Destination | Before it answers the commit | `Arrival`; `WorldArrivalOutcome` carries `Readopted` and each companion's `follows` member id |
| `Dead(K[, burned slots])` | Destination | Before it answers `Missing` or refuses a leased key; a commit that landed members and rolled them back names their slots, whose generations outlive the rollback | The escrow-once lane's `Tombstone(Key)`; the burned generations ride the arrival outcome |
| `Settlement(K, landedIds, retiredIds)` | Source | Before it acknowledges, restores or retires; every member not landed or retired is restored | Existing `Settlement`, keyed per member |
| `HopDone(K, I, arrived)` | Home | One companion's hop settles, or its settlement notice arrives before its hop (which then cancels the hop on arrival) | `WorldCrossingRecord.HopDone` |
| `HopAcked(K, I)` | Custodian | Home acknowledged one settlement notice | `WorldCrossingRecord.HopAcked` |
| `Retire(I, cause)` | Custodian | A step of its own after home answered `Stale` or `Released` for an embodied copy (two refused companions retire in two steps; the answer itself is volatile and is given again on the next contact) | `WorldCrossingRecord.Retire` |
| `Recall(I, e'')` | Home | The player or an authored window recalls | `WorldCrossingRecord.Recall` |
| `Noticed(I, recalled@e'' \| released)` | Home | The player has been told; every recall owes its own notice, named by its epoch | The HUD notice, recorded so the obligation survives a crash |
| `Install(I)` | Home | A definition install removes the placement's role | The taped mutation; the reducer reads it |
| `Stranded(I, v)`, `GraceElapsed(I, v)` | Custodian | The sweep finds no way home during the player's `v`th visit here; the window's tick passes | `WorldCrossingRecord.Stranded` and `GraceElapsed`, with the visit and deadline tick |
| `Write(I)` | Custodian | The driver writes the companion's durable state | The register write, already taped as state |

Replay: `WorldReplayEntry.Departure` and `Arrival` carry their records (the departure with
tickets and images, the arrival outcome with `Readopted`, epochs and bindings);
`WorldReplayEntry.Custody` carries `Minted`, `Hop`, `HopDone`, `HopAcked`, `Recall`,
`Noticed`, `Retire`, `Attempt`, `Stranded` and `GraceElapsed` with the crossing-log leaf;
`WorldReplayEntry.Transfer`'s narration is replaced by the settlement record. The re-drive
applies each through the reducer and never re-decides.

### 3.1 Append outcomes

An append answers `Durable`, `Refused` or `Uncertain`. After `Uncertain` the authority
latches: it appends nothing until it recovers (checkpoint plus log redo), it acts on
nothing from the uncertain record on (a landed record is suspended, as the escrow suspends
an uncertain arrival: it embodies nothing, grants nothing and verifies nothing until
recovery reads the log), and about that record it answers `Uncertain`. Home's verification
is fenced by the whole latch, not only by the key the uncertain record names: a release
or a recall carries no key and may still end the title, so a latched home confirms no
grant at all.

| Record | Refused | Uncertain |
|---|---|---|
| `Minted` | No crossing starts; the player tries again later | No crossing starts; recovery reads whether it landed; an allocated key never departed is abandoned on record (5.1 step 9) |
| `Hop` | `HopRefused(Unavailable)`; the source asks again | `HopRefused(Unavailable)`; the source asks again; recovery answers from the record if it landed |
| `Leased` | `ReserveRefused(Unavailable)`; the source asks again | `ReserveRefused(Unavailable)`; a landed `Leased` with no live lease answers `Dead` after recovery |
| `Departure` | Nothing departs; the attempt is dropped and the lease expires | As refused; a landed record is an in-doubt departure recovery reconciles |
| `Arrival` | `Missing` with `Dead(K)` | `Uncertain` to the commit, a retried commit, a status or custody query; only the destination's recovery decides (lands again or answers `Dead`) |
| `Dead` | The answer is still a refusal; the lease rule fences the key | As refused |
| `Settlement` | Stays in doubt; retried | Stays in doubt; recovery redoes it |
| `HopDone` | No acknowledgement; the source re-sends the notice | No acknowledgement; recovery resolves (5.3) |
| `Recall`, `Install`, `Noticed`, `Retire`, `Attempt`, `Stranded`, `GraceElapsed`, `Write` | The step did not happen; the move is enabled again | The step is in doubt until recovery reads the log |

## 4. Messages

All messages are idempotent by key and member, and may be lost, duplicated or delayed;
every one is answered, and a party that needs an answer re-sends the same request. A lane
(one sender, one receiver, one connection generation) delivers in order, as the engine's
request lanes do; a reconnect after a timeout starts a new generation on that lane, and a
crash starts a new generation on every lane, each independent of the old one, so a
request still executing at the receiver from the old connection may be overtaken by
anything the sender says on the new one.

| Message | From → To | Reply |
|---|---|---|
| `HopRequest(K, from, to, [(I, e)…])` | Source → home | `HopAnswer(K, per companion: e+1 \| Pending \| Stale \| Released \| NotMyTitle \| Unavailable)` |
| `Reserve(K, members with tickets)` | Source → destination | `Reserved(K, accepted members with slots, refusals per member)` or `ReserveRefused(K, Player \| Dead \| Unavailable \| …)` |
| `HopQuery(K, phase, [I…])` | Destination → home | `HopQueryReply(K, phase, per companion: e+1 \| NotGranted)` |
| `Commit(K, members with tickets)` | Source → destination | `Committed(K, landedIds)`, `Missing(K)` or `Uncertain(K)` |
| `Status(K)` | Source → destination | `StatusReply(K, Committed:landedIds \| Reserved \| Missing \| Uncertain)` |
| `CustodyQuery(K, I)` | Home → hop destination | `CustodyReply(K, I, Landed \| NotLanded \| Dead \| Reserved \| Uncertain \| Unauthorized)` |
| `HopSettled(K, I, arrived)` | Source → home | `HopSettledAck(K, I)` |

A `HopQuery` names its phase, the reservation's or the commit's, and its reply echoes it. An answer
counts only for the request of its own phase that is waiting for it at the destination; one that
arrives when no such request waits is spent, so neither a late nor a duplicated answer to the
reservation's question can stand for the commit's. A decision spends the answers it used, whatever it
decided, so a reservation sent again after a refusal the destination could not make durable asks home
again. Each question over federation is its own request, correlated with its key, its phase and the
incarnations it names, so two questions in flight never share an answer.

A reservation the lane lost (request or answer), or one the destination refused `Unavailable` (latched,
or its `Leased` record did not land), decided nothing: the refusal is transient, and the source sends
the same request again, unchanged, so a lease the destination already holds answers it and no hop is
asked for twice.

A request counts as already sent only on the sender's current connection to its receiver: after a
reconnect, a request still queued on the replaced connection may yet execute, but nothing will answer
it on the new one, so the sender sends it again.

`Reserve`, `Commit` and `Status` exist; `Reserved` gains per-member verdicts and slots,
`Committed` and `StatusReply` the landed member ids. `HopRequest`, `HopQuery`, `CustodyQuery`
and `HopSettled` are new leaves on the federation `Transaction` lane. A companion refusal
reason is `Role`, `Duplicate` (a body or live lease here names the incarnation), `Full`,
`Unverified` (home did not confirm the hop), `Pending`, `Stale`, `Released`, `NotMyTitle`;
a whole reservation is refused `Player` (the player's own admission or cap), `Dead` or
`Unavailable`. `Pending`, `Duplicate` and `Unavailable` are transient: the requester keeps
its attempt and asks again.

**`CustodyQuery` is an authorized operation**, not the transfer status query (which a
foreign authority may not ask about a key it did not mint). The destination serves it only
to the authority that minted the incarnation (`I.Authority` equals the authenticated
namespace) and answers from durable records: its arrival for `K` (landed, with or without
`I`), its live lease, its latch, or `Dead`, writing the tombstone first for a key it leased
and lost or never leased at all (home asks only once the source has given the key up, so
the tombstone makes the answer final and a late reservation under `K` is refused). It
never answers from retired transaction outcomes, so acknowledgement or supersession of the
arrival does not change the answer.

## 5. Transitions

Each row: precondition → record → message → what acknowledges it.

### 5.1 Onward hop (the player crosses from X to Y with its companions)

1. **Allocate.** Precondition: the player stands at X, and each companion listed follows
   it there (home: its placement's seat; abroad: bound by member id to the player's slot
   from the same arrival, and both still present). X records `Minted(K, Y, memberIds)`.
   Companions are listed before the player; binding is by id, never by position.
2. **Authorize.** X at home: records one `Hop(K, H, Y, …)` naming every companion whose
   title is home (a refused append is tried again; an uncertain one waits for recovery). X
   abroad: sends one `HopRequest(K, X, Y, …)` for the cohort's companions; home decides
   each on its own title, records one `Hop` naming the ones it grants, and answers per
   companion: `e+1` when custody is `Away(X, e)` and the request comes from X; `Pending`
   starts resolution (5.3) and the source asks again; `Stale` or `Released` makes the
   source retire its embodied copy and drop the companion; a deadline drops the companion
   with a notice. A repeated request for a companion already in `Hop(K)` is granted again.
3. **Reserve.** With every companion answered, X sends `Reserve(K, members with tickets)`.
   Y decides each member on its own: the player under its own door and `networkPlayers`
   cap (refused → the whole reservation is refused `Player`, nothing departs); a companion
   for a declared role, when no body or live lease at Y names its incarnation, when a slot
   is free, and when home confirms the hop: Y sends one `HopQuery(K, …)` for the companions it
   would otherwise take (a declared role, no body or live lease naming them; none when a lease, an
   arrival or a death under `K` already answers) and holds the reservation until `HopQueryReply` confirms a **currently
   valid** grant: `Hop(K)` names that companion to Y at `e+1`, it is that title's pending
   hop now (not settled, not superseded by a recall, not cancelled by a settlement that
   arrived first, not released), and home can call the record durable. A historical record is not a
   grant, so an offline signature alone cannot replace this query; a signed ticket may
   carry the grant, but the destination still confirms it is current. An unconfirmed
   companion is refused `Unverified`. Y records `Leased` and answers `Reserved` with the
   accepted members and their slots.
4. **Depart.** Precondition: every accepted member's body still stands at X (an attempt
   whose body another attempt already moved is dropped). X records `Departure` with each
   companion's ticket `(home, I, K, e+1)` and image, and sends `Commit`.
5. **Commit.** With a live lease: Y first asks home again (`HopQuery` in the commit's phase)
   for every leased companion, because the reservation's answer is spent: a companion lands
   only under the grant as it stands at the commit (still that title's pending hop, at the
   leased epoch, durable), confirmed by an answer to this commit's own question; one whose
   grant is gone (settled, superseded by a recall, released) is not landed. A commit that
   lands members and then cannot make its arrival durable rolls them back, and the slots
   keep the generations those landings took (the tombstone names them), so the next
   occupant of such a slot is a new generation. Y then lands the leased members it still admits (a member refused at the
   landing is left out; the player refused means the whole commit is `Missing`), each at
   its leased slot and that slot's next generation, each companion at `e+1` and bound by id
   to the player member landed in this same commit, recording the slot and generation that
   member took (one not landed leaves `follows` empty), records `Arrival`, and answers
   `Committed(landedIds)`. Without a lease: `Dead(K)`, `Missing`. Latched: `Uncertain`. A
   held commit is asked again by the source's resent commit. A destination that stops waiting for
   home's answer to the commit's question lands what home confirmed and leaves every unconfirmed
   companion out, as it leaves out one whose grant is gone.
6. **Settle at X.** `Committed(landedIds)`: record `Settlement(K, landedIds, retired)`,
   `retired` naming the unlanded companions whose title home has released meanwhile (5.6);
   every other member not landed is restored to its slot with its binding (a restored
   companion abroad is separated unless its player was restored too). `Missing`:
   `Settlement(K, [], retired)` the same way. `Uncertain`: stay in doubt and ask `Status`
   later. A settlement that does not land leaves the departure in doubt, and the source asks
   `Status` again rather than settling from an answer it holds only in memory.
7. **Notify home**, per companion the key was minted for: `HopSettled(K, I, I ∈ landedIds)`,
   kept as an obligation until `HopAcked(K, I)`, whether or not the companion departed (one
   home refused, or the destination left out, is notified as not arrived, which cancels any
   grant a late `HopRequest` would otherwise meet). At home the same step is a local record:
   `HopDone(K, I, I ∈ landedIds)` from its own settlement.
8. **Settle at home.** The hop's destination is the only oracle for a hop's settlement. A
   source's `HopSettled` is a request: home asks the destination `CustodyQuery(K, I)` and
   records `HopDone(K, I, arrived)` from that authenticated answer alone (`Landed` →
   arrived; `Dead` or `NotLanded` → not arrived, and the destination has tombstoned the key
   as it answered; `Reserved` or `Uncertain` → ask again), then answers the
   source `HopSettledAck`. A destination that holds a verified lease therefore never sees
   its grant cancelled under it: the cancellation can only come from its own tombstone.
   A notice that arrives before its hop is recorded as `HopDone(K, I, false)` all the
   same, since no destination can hold a lease on a hop home never granted; the delayed
   `HopRequest` is then answered `Dead` for that companion and the reducer gives a hop
   recorded after its settlement no custody effect, so a settlement acknowledged ahead of
   its authorization can neither disappear nor leave the title pending.

**The epoch is the grant's, never the sender's.** A destination accepts a companion's
ticket only when its epoch is the one home's grant names (`e`), lands it at `e + 1`, and
lands a commit member only when its ticket equals the verified reservation's; a sender
naming any other epoch is refused `Unverified` or not landed. The engine's `Advance()`
therefore runs on the verified reservation mobility, not on the commit's carried value.
9. **Abandon.** A key minted but never departed, whose attempt a crash lost, is settled on
   record as `Settlement(K, [], [])` and notified `HopSettled(K, I, false)` for each
   companion it was minted for; its destination lease expires and the key dies there.

### 5.2 Return (a companion goes home)

The return is its own operation, bound by home's record, never by `follows`.

1. **Separation sweep** (each drain): a companion abroad that follows no one and has no
   attempt or departure in flight records `Minted(K, H, [I])` and sends `Reserve` to home
   with its ticket. The same runs when the player leaves the game, when a destination
   refused or did not land the companion, and before an instance stops (5.8).
2. **Verify at home**, one function, the only door that reads custody for an arriving
   companion: `Released` → `Released`; `Pending` → `Pending` and resolve (5.3); `Home`, or
   `Away(X', e')` with `X' ≠ custodian` or `e' ≠ ticket epoch` → `Stale`; an incarnation home
   did not mint → `NotMyTitle`; `Away(custodian, e)` → accept, binding the lease to the held
   slot. Home never refuses a verified return for capacity. A live lease naming the
   incarnation answers `Duplicate` until it expires.
3. **Depart and commit** as in 5.1 steps 4 and 5, with no hop request. At the commit home
   verifies again (the fence against a recall or install between lease and commit; a
   failed re-verification answers `Missing` with `Dead(K)`), lands the companion in the
   held slot at epoch `e+1`, and records `Arrival` with `Readopted = [I]`. The reducer sets
   custody `Home` at `e+1`, clears the hold, and derives the binding (role, kit, look,
   followed seat) from the placement as installed now.
4. **Refusal at the custodian.** `Stale` or `Released` is home's definitive word: record
   `Retire(I)` for an embodied copy, or `Settlement(K, [], [I])` for a shed image. `Pending`,
   `Duplicate`, `Unavailable`: keep the attempt and ask again. `Dead`: a shed image tries a
   fresh key (5.4). `NotMyTitle`: keep the image and keep asking the authority the ticket
   names. Nothing is destroyed on silence.

### 5.3 Resolving a pending hop

Home resolves `Pending(K)` for `I` by `CustodyQuery(K, I)` to the hop's destination:
`Landed` → `HopDone(K, I, true)`; `NotLanded` or `Dead` → `HopDone(K, I, false)`; `Reserved`
or `Uncertain` → ask again later. Home asks on recovery, whenever the source's
`HopSettled` arrives, and on a return or hop request from a party to the pending hop (its
source or its destination); a request from anyone else is answered `Stale` and resolves
nothing, so no third party can make home tombstone a key the legitimate reservation has
not reached yet. The source's notice never settles a hop by itself. Home's own hops are the exception: they settle only from
home's own settlement record, never from the destination's answer, because home's
departure may still be in doubt when the destination has already answered `Dead`, and the
body it restores must find its slot held. A hop whose destination cannot be reached stays
pending, and the title waits in durable custody: home neither recalls nor releases it from
under a destination that may hold home's confirmation for its commit (5.5). A destination
gone for good therefore leaves the title pending (section 10).

### 5.4 Stranded, grace, shed

A companion is stranded when it is separated and its return has not landed: the sweep
records `Stranded(I, v)`; the authored grace window (`roles.strandedGraceSeconds`, the
custodian's own ticks, the deadline in the record so a restart keeps it) records
`GraceElapsed(I, v)` when it passes. Both belong to the player's `v`th visit to the
custodian (a join, a landing, or a rollback that restores the player there): a later visit
opens a new episode, which strands and waits its own window before any shed; then the custodian **sheds**, whatever attempt is in
flight: it records `Departure(K, H, [I], shed)`, drops the embodied attempt, frees the slot,
and treats the record as a durable return obligation: `Reserve` until home answers,
`Commit` on `Reserved`, and on `Missing` or `Dead` a new attempt under a fresh key, itself a
durable allocation (`Minted(K', H, [I], image K)`) so no attempt key is ever reused and a
crash keeps the mapping. The shed key itself comes from the same allocator, in a step of
its own before the shed departure (`Minted(K, H, [I], shed)`, then `Departure(K, …, shed)`):
one authenticated source namespace, one ordered `ulong` domain, floor-safe, no second key
space. A shed key allocated but not yet departed (a crash between the two steps) waits
while the companion still stands here and is allocated again if it was abandoned. Only the
latest attempt's answer counts. The custodian never drops custody on
a timer; the record outlives reaping and stop (5.8). A shed image's only ends are home's
`Committed` or home's `Stale`/`Released`.

### 5.5 Recall and release

Only home ends or recalls a title.

- **Recall.** Precondition: custody `Away` (a `Pending` title is resolved first, since the
  pending hop's destination may hold a verified lease or a delayed verification answer; a
  destination that cannot be reached keeps the title pending, and the companion waits in
  custody), no live return lease at home, the owner reachable (the player embodied at home
  or at an authority whose link to home is up; never on silence while the owner may share
  the companion's partition) and not standing at the custodian (a companion beside its
  owner is not stranded). Home records `Recall(I, e'')` with `e''` above every epoch it ever
  issued and embodies its last committed image in the held slot; the notice is a separate
  durable obligation (`Noticed(I, recalled@e'')`, one per recall) the next drain records, so a
  crash between the two still tells the player, and a second recall of the same title owes a
  second notice. Every pending hop of `I` is superseded. A copy abroad at
  a lower epoch is stale; its next `Reserve` or `HopRequest` is refused `Stale` and it
  retires. Recall happens by the player's request or an author's declared recall window;
  the engine's default is to wait, and the standard package declares the window as the
  margin by which a custodian may miss its lease
  ([lease-missed recall](../decisions/worlds-and-federation.md#recall-after-a-missed-lease)).
- **Release.** Only the taped install that removes the placement's role releases a
  title (`Install(I)`); `Noticed(I, released)` follows the same way. An install honours the
  pending-authorization fence: a title whose hop is pending stays pending, home confirms no
  further grant for it, and the hop's resolution releases it. A destination that home
  confirmed the commit to before the install lands the companion, and that copy learns of
  the release on its next contact and retires then. A custodian never
  releases; what it holds is retired on home's answer, so there is no release delivery to
  lose. A copy abroad that still follows its player learns of the release on its next
  contact with home and retires then; until that contact it follows as before.
- **Refill.** A placement whose role is declared disposable spawns a new individual after
  a release or recall and names it to the player as such; a default role waits.

### 5.6 Install versus departure

The installed definition owns **bindings**; custody records own **title, hold and epoch**.
A restored or re-adopted companion derives its binding from the placement as installed at
that moment. An install that removes the placement's role records `Install(I)`: custody is
`Released`; a companion embodied at home despawns; a later `Settlement` for home's own
in-doubt departure retires it instead of restoring it; later returns and hop requests
answer `Released`.

### 5.7 Crash and recovery

A restart restores the checkpoint, redoes every record past it through the reducer, and
re-sends what the records say is unanswered: `Status(K)` for each in-doubt departure,
`Reserve` for each shed image, `HopRequest` for each attempt authorized but unanswered,
`HopSettled` for each unacknowledged settlement, home's `CustodyQuery` for each pending
hop; it abandons each minted key that never departed (5.1 step 9). Leases, held
reservations and verification answers are volatile: a commit for an expired lease is
`Missing` with `Dead(K)`, a held reservation is asked again. The departure redo installs
the tickets the record carries and mints nothing.

### 5.8 Reaping and stop

An authority holding a title away, a pending hop, an in-doubt departure or a shed image is
not reaped (the forwarding-route rule widened to these records). An explicit stop first
runs the separation sweep for visiting companions and completes after the drain; a return
not settled by then stays an in-doubt record in the final checkpoint, which the stop must
write. A host that keeps no durable image cannot write one, so it refuses to stop a home
holding a title abroad and waits while a row owes steps its drain makes. Reaping and stop
read one guard (`WorldInstanceHost.CustodyHeldBy`). The accepted stop is part of the row's
image, so a restart resumes it; the sweep repeats at every drain; and the row retires only
when, in the same hold of its authority gate that closes admission, it embodies no visitor
and holds no live lease. A visitor whose home runs nowhere the stop can reach makes the stop
refused, since no return obligation could be recorded for it.

## 6. Trust derivation

- **Whom a companion follows**: abroad, the id of a player member in the same commit, bound
  to the slot and generation that member landed at; valid while that slot holds that member
  at that generation (a rollback restores the same generation; a new landing of the same
  player is a new generation and does not rebind); cleared otherwise. Ids, not positions,
  survive filtering. At home the binding is the placement's: it follows whoever holds the
  home seat, at any generation, so a player who reconnects at home after the companion came
  back still has it (the visiting binding stays generation-bound).
- **What its driver may observe**: derived at every check (`TryRoleVerdict` on `lane/companion-v1`
  before stored grants): Drive and Observe over the companion bodies bound to the driver at
  their landed generation, Observe over each followed slot while it holds the followed
  member, nothing else; `TryGrant` refuses body, Control, Mutate and Edit grants to a role
  driver. Runtime, authority and budget identity is (role, resolved driver pin).
- **Who holds the title**: home's own records, read through one verification door for
  returns, hop requests and the commit re-check. A destination lands a companion only
  after home confirms the hop is currently granted (`HopQuery`), so an authenticated
  former custodian cannot place a fabricated ticket anywhere. The current custodian can
  do what any custodian can: return the companion, or move it on under a granted hop. If
  it keeps a body of its own while doing so, that body is a puppet at the epoch the title
  left behind (the hop's old epoch while the hop is pending), refused on any contact with
  home or another world and retired then; the title, the held slot and the epoch home
  issued are the companion's. The model counts embodiments at the highest epoch home has
  issued once a forger is in play, and requires every such puppet to retire or to be still
  following its player at convergence.
- **Every instance-targeted read** answers through one disclosure door.

## 7. The held slot

While custody is `Pending` or `Away`, or while a departure in doubt names the companion,
the held slot is reserved for the title: the census, `ReconcileInhabitCounts`, admission
allocation and `PreferredOrLowestFree` read one predicate, `IsHeld(slot)`, derived from the
custody records and the in-doubt departures. At every authority a slot a departure in
doubt left is held for the body a rollback may restore to it. A verified return's lease is
bound to that exact slot and never falls back. Home never refuses its own verified return
for capacity.

## 8. The model

`CompanionCustodyModel.cs` is the protocol above with one home, two custodians, one player
and one or two companions, each authority a list of records with one append per handler.
Faults: message loss, duplication and reordering; crash-restart (volatile state cleared,
records kept); partition and heal; deadlines (lease expiry, grace windows, hop and
verification waits); refused and uncertain appends (lost or landed); the author's install;
the player's recall, disconnect and reconnect; a former or current custodian forging a
reservation toward home or the other custodian; another human filling the destination's
cap so the player's own admission fails; a destination refusing one leased companion at
the landing; the driver writing durable state; home's census. `CompanionCustodyModelLawTests`
explores every reachable state under a fault budget, one scope per fault class and route,
and asserts on each state:

1. the reducer and the independent shadow derivation agree on every body, epoch and title;
2. at most one embodiment at the current epoch;
3. home embodies a title only when it says `Home` or has authorized but not yet departed
   it; the held slot holds no other body while the title is away or pending;
4. no lost title: a copy abroad is the one the title names, the title's pending hop names
   it, or it is stale;
5. fencing: every landing at a custodian rides a `Hop` home granted to that custodian at
   that epoch, and every re-adoption lands one epoch above the custody it left;
6. follows, read from the recorded member id, names a player standing at the recorded
   slot; humans within the cap and companions never counted; no two bodies share a slot;
7. a dead key never lands and a key is leased once; a recall's epoch exceeds every issued
   epoch; durable state never regresses below home's last committed image except by
   recall.

From every quiescent state it runs a fair completion (heal, recover latched authorities,
deliver everything, run every protocol move, let deadlines pass when nothing else moves)
and requires: no departure, attempt, lease, held reservation or pending hop remains; every
recall and release is noticed; each title is embodied exactly once where the title says,
at the title's epoch, following its player or at home, or released and embodied nowhere;
and every write reached the embodied copy unless a recall discarded what left with it. A
cycle without a new record is a livelock.

Every transition is also checked for the append boundary (at most one record per authority
per step), and the shadow derivation covers each body's epoch, slot, generation, image and
recorded binding with its followed generation, not only presence. The canonical state text
keeps each lane's message order (lanes themselves sort, being independent), so two
schedules with different lane heads are different states. The explorer is depth-first over
the fault budget with the visited set held as 128-bit digests of that text (two independent
64-bit folds), reports states, quiescent states converged, depth, wall time and peak
managed memory per scope, and caps each scope (1.5 million states by default, and 4 to 6 million for the
combined scopes: the two-companion four-fault scope reaches about 4.4 million and the
onward log-and-crash scope about 2.8 million; the cost is about 100 MB of managed
memory per million states).

The traces and the small scopes (under about 100,000 states) run with the ordinary suite.
The large scopes are the **model check**, explicit tests carrying `Category=ModelCheck`:

```text
dotnet build tests/Puck.World.Protocol.Tests/Puck.World.Protocol.Tests.csproj -c Release -nodeReuse:false
dotnet test tests/Puck.World.Protocol.Tests/Puck.World.Protocol.Tests.csproj -c Release --no-build --explicit on --filter-trait Category=ModelCheck
```

The build lane uses the model check to explore each protocol transition and
its failure schedules. A protocol change is expressed in the model first; the
check supplies evidence for that lane and is not a landing gate. It takes several minutes and a few hundred megabytes; the ordinary suite stays
under a minute. Scopes combine fault classes
(two companions with admission failure, partial landing, crash and loss in one scope; log
failures with crashes on the onward route; repeated deadlines so a shed image fails more
than once; a custodian with one slot fewer than home so a cohort exhausts its companion
capacity), bounded by count per class, never by forbidding interleavings: retries fire
only after a loss (a retry with nothing lost is the duplicate the `Dup` fault produces), a
status query may race the delayed commit it asks about, and shedding runs whatever
attempt is in flight.

**The built crossing paths are checked against the model.** `CompanionCustodyRefinementLawTests`
(`tests/Puck.World.Server.Tests`, in the ordinary suite) runs real authorities (`CompanionRows`:
home and two custodians in one host, each with a durable crossing log) while the model follows:
every peer call at the `IWorldPeerCall` and `IWorldCustodyPeer` seams and every crossing-log append
must be a transition the model enables from its current state, records are compared at every
append, and the derived state (titles, embodiments and epochs, bindings, slot generations, leases,
in-doubt departures, owed notices) at every drain boundary and after each authority restarts from
its image plus its log. Faults are every single-fault schedule over the decision points a fault-free
run finds: loss and duplication of any call, a crash before or after any append (with and without a
checkpoint ahead), refused and uncertain appends, an install, a stranger filling the cap, a forged
reservation and a refusal at the landing; a latched authority recovers at the next drain boundary,
as the model's fair completion recovers it. A model move the engine has not built yet (sweep,
strand, shed, retire, resolve) is reported as deferred, not as a failure, and so is a retry past the
model's per-obligation retry count, which bounds the model's state space where the engine retries
every drain.

The fixture retains all three authorities and uses local player seats. Its result
therefore excludes automatic reaping, explicit stop, and remote-player forwarding;
the adversarial crossing laws separately exercise reaping and a partial onward
remote-player crossing. It also does not cover two concurrent federation questions
or sustained transient refusals through the engine's retry ceiling. The deferred
moves and these exclusions are limits of the refinement claim, not successful
checks of those behaviours. Incoming federation actors still cannot claim `World`.

Model simplifications the build must not read as protocol: the player's own transfer
beyond the cap and one free slot is not modelled; `Uncertain` is modelled at the record
level, not as the escrow's full suspension; signed tickets are not modelled (verification
is the query).

## 9. Build plan

Steps 1 to 5 are built. Durable record kinds for later steps already exist in
`WorldCrossingRecord.cs`; their presence does not complete the transitions
below. The remaining build work is steps 6 to 10, using the existing model and
real authority fixtures as evidence:

6. **Separation, stranding, shed and retire.** The drain's sweep; `Stranded` and
   `GraceElapsed` with authored windows; the shed as allocate-then-depart under the common
   allocator; fresh-key attempts; the `Retire` step on home's `Stale`/`Released`; the
   generation-bound follows binding. Laws: `AnUnansweredReturnStillShedsAfterTheGraceWindow`,
   `ShedAttemptsNeverReuseAKey`, `ShedKeysComeFromTheCommonAllocator`, `M2_TheGraceWindowAndTheShedReturnSurviveACrash`,
   `ABindingNamesTheFollowedGeneration`, `AStrandingDoesNotCarryIntoALaterVisit`, `ReleaseIsHomesRecordAndTheCustodianRetiresOnContact`,
   `AForgedReturnToHomeIsStaleWhileTheCurrentCustodianKeepsCustody`.
7. **Recall and release.** `Recall` from `Away` only, under its preconditions, with the
   `Noticed` obligation; `Install` as release; the player's notices. The lease-missed
   recall: a title whose custodian has missed its lease by the authored margin is
   recallable by home, and the same rule covers a home restarted without its checkpoint,
   whose `NotMyTitle` answer this step must turn into a recallable title rather than a
   wait (the MMO round's step 8; [the decision](../decisions/worlds-and-federation.md#recall-after-a-missed-lease)).
   A scarce companion whose title sits at a settlement domain takes that domain as home
   and the player's host as custodian, which waits for
   [the settlement door](open-items.md#settlement-trust-roles-and-revocation). Laws:
   `Recall_SupersedesTheAwayCopyWhichRetiresOnReappearance`, `ALaterHopDoesNotContradictAnEarlierRecall`,
   `EveryRecallIsNoticedOnItsOwn`, `APendingTitleWaitsForItsHopBeforeARecall`, `AReleaseDuringAPendingHopWaitsForTheHopToResolve`,
   `H5_ReleaseIsHomesRecordAndTheCustodianRetiresOnIt`.
8. **Lifetime.** Reaping widened to titles away, pending hops, in-doubt departures and shed
   images; the explicit stop's sweep and final checkpoint (built: `WorldInstanceHost.Stop`,
   `CompanionStopLawTests`; the silo's deactivation writes its final checkpoint but does not
   sweep first, and shed images wait for step 6); retries per obligation bounded
   by the lease deadline and the drain; status racing a delayed commit across a reconnect.
   Laws: `AStatusQueryMayPassADelayedCommitAfterAReconnect`, `AReconnectResendsADelayedReservation`, `ARefusedAppendIsATransientRefusalTheRequesterRetries`,
   `AnUncertainArrivalIsResolvedOnlyByTheDestinationsRecovery`.
9. **Role drivers and disclosure.** `TryRoleVerdict` before stored grants, (role, pin)
   runtimes, the one `body.where` door; `worlds/standard/` declaring `accept`; the debug
   world's faerie moved to registers. These are engine construction with their existing
   regression legs, not model traces.
10. **The debug world.** The faerie crosses into the night painting and the garden and
    comes home; the sweep's companion signals; the player's notices on the HUD.

The lane uses the model and real-instance checks to examine each step, and
amends the model first when a transition needs correction. These checks are
the lane's tools, not preconditions for landing. The debug-world trip and its
captures demonstrate the composed behavior.

## 10. Owner calls

- Decided by the MMO foundations round
  ([lease-missed recall](../decisions/worlds-and-federation.md#recall-after-a-missed-lease)):
  waiting forever and recalling are one mechanism. A title whose custodian, or whose
  pending hop's destination, has missed its lease by the margin the standard package
  authors is recallable, and so is a title of a home restarted without its checkpoint. A
  second embodiment left at the dead destination earns nothing settled and issues claims
  only in that host's namespace, so it costs nothing; waiting forever would let a host with
  a `*` admission row trap every companion and character that visits it. Step 7 builds it.
- A companion that follows its player into a world reachable from home but whose hop home
  refuses as `Pending` for longer than the destination holds the lease crosses later or not
  at all (the player goes on alone and is told). Whether the player should be able to wait
  at the door for the hop is a product call.
