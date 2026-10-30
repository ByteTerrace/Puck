# Companion custody protocol

This is the specification the build of the [companion decision](../decisions/companions.md)
follows, and the model that checks it. Two builds of the decision failed review on the
same three classes (authority from a sender's claim, away bookkeeping outside the
authority record, a boot-random custody key), and a prose redesign failed a third review
one interleaving at a time. The protocol below is therefore stated as per-authority
state, typed messages and durable records, with every transition's precondition, record
and acknowledgement, and it is checked by an executable small-scope model
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
| Incarnation `I` | The companion's mobility incarnation | `WorldEntityAddress` minted by home |
| Custody epoch `e` | The traveller's ownership epoch; advances by one on every committed custody hop | `WorldMobilityIdentity.Epoch` |
| Hop key `K` | The source-scoped transfer key of the crossing that moves custody | `WorldTransferKey` (source authority, transfer id) |
| Member id `m` | A request-local id the source assigns at enqueue; it never changes when the cohort is filtered | New field on `WorldTransferReservationMember` and `WorldTransferCommitMember` |
| Custody ticket | `(home, I, e', K)`: "home recorded hop `K` moving `I` to its destination at epoch `e'`" | `WorldTransferCompanion.Ticket`, carried on the reservation and the commit member |

The ticket is not a secret and is never verified by a custodian. Home verifies it when a
custodian cites it back: the authenticated channel identifies the custodian (in process, the
host; remotely, the federation lane's authenticated namespace), and home's own record
says which custodian holds the title at which epoch. Nothing a sender asserts is
evidence; a ticket from the wrong custodian or a superseded epoch is refused as `Stale`.

## 2. Per-authority state

Every custody fact is a function of the authority's durable records through one
reducer (`CustodyModel.Fold` in the model; the engine's checkpoint restore, crossing-log
recovery and tape re-drive all call the same function). The only volatile state is
leases, pre-departure attempts and retry counters, which a crash clears.

**Home** derives, per incarnation it minted:

| Custody | Meaning | Held slot |
|---|---|---|
| `Home` | Embodied at home, or detached by home's own in-doubt departure | Occupied or held |
| `Pending(K)` | A hop `K` is authorized and not yet settled | Held |
| `Away(X, e)` | Custodian `X` holds the title at epoch `e` | Held |
| `Released` | The author removed the placement's role (a taped install); the title has ended | Free |

plus the current epoch, the highest epoch ever issued, the pending hop, and the held
slot and placement. "No row" is never an answer: an incarnation home minted is always in
one of these four states, and an incarnation home did not mint is `NotMyTitle`, which
ends nothing (section 5.2).

**A custodian** derives: whether it embodies the companion and at which ticket, which
member it follows, its in-doubt departures (departures without a settlement), whether the
companion is stranded and whether the grace window has elapsed, and the settlements
home has not yet acknowledged.

## 3. Durable records

Each record is written by the deciding authority before the step becomes visible to a
peer, in the crossing log (`IWorldCrossingLog`), captured by every checkpoint and taped
for replay. The reducer is total over any prefix.

| Record | Authority | Written when | Engine form |
|---|---|---|---|
| `Hop(K, from, to, e, e')` | Home | It authorizes a hop | New `WorldCrossingRecord.Hop`; home's checkpoint custody row |
| `HopDone(K, companionArrived)` | Home | The hop settles, from the source's notice or home's own status query | New `WorldCrossingRecord.HopDone` |
| `Arrival(K, members, slots, outcome)` | Destination | Before it answers the commit | Existing `Arrival`; `WorldArrivalOutcome` gains `Readopted` (held slot, placement) and the companion bindings (`follows` by landed slot and generation) |
| `Departure(K, to, members with tickets, shed)` | Source | Before it sends the commit; a shed departure before any reservation | Existing `Departure`; `WorldLandedMemberCheckpoint` gains `Companion`, `PlacementId`, `Ticket`, `Shed` |
| `Settlement(K, arrived \| stayed \| retired, landedMemberIds)` | Source | Before it acknowledges, restores or retires | Existing `Settlement`, gaining the member ids the commit landed and the `retired` arm |
| `Leased(K)` | Destination | Before it answers `Reserved` the first time | The escrow's retained transaction row, made durable with the lease's creation |
| `Dead(K)` | Destination | Before it answers `Missing` to a commit or a status query, or refuses a reservation for a key it leased before | New `WorldCrossingRecord.Refused`; the key is never leased again |
| `Attempt(K', image K)` | Custodian | A shed image asks home again under a fresh key | The new in-doubt record names the departure it carries |
| `Retire(cause)` | Custodian | Home answered `Stale` or `Released` for its embodied copy | New `WorldCrossingRecord.Retire` |
| `HopAcked(K)` | Custodian | Home acknowledged its settlement notice | New; retires the notice obligation |
| `Recall(e'')` | Home | The player or an authored window recalls (section 5.5) | New `WorldCrossingRecord.Recall` |
| `Install` | Home | A definition install removes the placement's role | The taped mutation itself; the reducer reads it |
| `Stranded`, `GraceElapsed` | Custodian | The separation sweep finds no way home; the grace window's tick passes | New records with the deadline tick |
| `Notice(text)` | Either | The player must be told | The HUD notice, taped as narration |

Replay: `WorldReplayEntry.Departure` and `Arrival` carry their records as today (the
departure now carrying the tickets, the arrival outcome now carrying `Readopted` and the
bindings); a new `WorldReplayEntry.Custody` carries `Hop`, `HopDone`, `Recall`, `Retire`
and `HopAcked` encoded with the crossing-log leaf. `WorldReplayEntry.Transfer`'s narration
is replaced by the settlement record. The re-drive applies each through the reducer and
never re-decides: a replayed home arrival binds the recorded `Readopted`, a replayed
`HopDone` moves custody as recorded.

## 4. Messages

All messages are idempotent by key and may be lost, duplicated, delayed or reordered;
every one is answered, and a party that needs an answer re-sends the same request.

| Message | From → To | Key | Reply |
|---|---|---|---|
| `Reserve(K, members)` | Source → destination | `K` | `Reserved(K, acceptedMemberIds[, companionRefusal])` or `ReserveRefused(K, reason)` |
| `HopRequest(K, I, from, to, e)` | Source → home | `K` | `HopGranted(K, e')` or `HopRefused(K, Pending \| Stale \| Released \| NotMyTitle)` |
| `Commit(K, members with tickets)` | Source → destination | `K` | `Committed(K, landedMemberIds)` or `Missing(K)` (or `Uncertain`, as today) |
| `Status(K)` | Source or home → destination | `K` | `StatusReply(K, Committed:landedIds \| Reserved \| Missing)` |
| `HopSettled(K, companionArrived)` | Source → home | `K` | `HopSettledAck(K)` |

`Reserve`, `Commit` and `Status` exist; `Reserved` gains per-member verdicts and
`Committed` and `StatusReply` gain the landed member ids; the hop messages are new leaves
on the federation `Transaction` lane. A companion refusal reason is one of `Role` (no
declaration for the role), `Duplicate` (a body or a live lease here already names the
incarnation), `Pending`, `Stale`, `Released`, `NotMyTitle`; a whole reservation is refused
`Dead` when its key was leased or refused here before.

**A key is leased once.** A destination that leased `K` and lost the lease (a crash, a
deadline), or that answered `Missing` to a commit or a status query for `K`, records
`Dead(K)` before answering and refuses every later reservation and commit under `K`. This
makes `Missing` final: without it, a retried reservation re-leases `K` after a crash, a
duplicated commit then lands a body the source has already restored, and home's status
query resolves a hop as "not arrived" that later arrives. The source never reuses a key:
a restored body crosses next under a new transfer id, and a shed image's next attempt is a
fresh key (section 5.4).

## 5. Transitions

Each row: precondition → record → message → what acknowledges it. "Retry" means the same
request is sent again until its reply arrives; every retry is bounded in the engine by the
lease deadline it waits under or by the next drain.

### 5.1 Onward hop (the player crosses from X to Y with its companion)

1. **Enqueue.** Precondition: the player stands at X and the companion follows it there
   (home: the placement's followed seat; abroad: landed in the same arrival as the player
   and both still present). X mints `K`, lists the companion with `follows = m(player)`
   and its current ticket, and sends `Reserve`. The cohort is one request; the companion
   is never a separate transfer while following.
2. **Reserve at Y.** Per member: a player is admitted through its own door (local seat or
   peer admission, its own `networkPlayers` accounting) exactly as if alone; a companion is
   admitted when Y declares the role and no body or live lease at Y names its incarnation,
   else refused `Role` or `Duplicate`. Y leases the accepted members and answers
   `Reserved` with their ids. A duplicate `Reserve` under a live lease or a landed arrival
   answers the same `Reserved`.
3. **Companion refused.** X records a `Notice` to the player and continues with the
   accepted members by id. `Duplicate` is transient: X keeps the attempt and asks again
   when the lease expires. A companion left behind is separated (section 5.2).
4. **Authorize.** With the companion accepted and Y not its home: if X is home, X
   records `Hop(K, X, Y, e, e')` locally; otherwise X sends `HopRequest`. Home records
   `Hop` and answers `HopGranted(e')` when custody is `Away(X, e)` and the request comes
   from X; answers `Pending` when a hop is pending (and starts resolving it, section 5.3);
   `Stale` when custody is `Home` or names another custodian or epoch; `Released` when
   the title ended. A duplicate request for an existing `Hop(K)` is granted again.
   `Pending` is not a refusal: X keeps waiting and re-sends. `Stale` or `Released`: X
   records `Retire` for its embodied copy and crosses the player alone. A hop deadline
   (the lease's) crosses the player alone and records the notice; home's `Hop`, if it was
   granted late, settles `companionArrived = false` through section 5.3.
5. **Depart.** Precondition: every member's body still stands at X (an attempt whose
   body already departed under another attempt is dropped). X records `Departure` with
   the companion's new ticket `(home, I, e', K)` and sends `Commit` carrying it.
6. **Commit at Y.** With a live lease: Y lands the leased members, binds the companion to
   the player member landed in this same request (by id, after all members land; a
   companion whose followed member did not land follows no one), records `Arrival`, and
   answers `Committed(landedIds)`. Without a lease: record `Dead(K)`, answer `Missing`. A
   repeated commit of a landed `K`: `Committed`. A second companion body at Y is impossible
   by the lease rule and is an invariant violation.
7. **Settle at X.** `Committed`: record `Settlement(arrived, landedIds)`; if X is home,
   record `HopDone(K, companion ∈ landedIds)`; otherwise send `HopSettled` and keep it as
   an obligation until `HopAcked`. `Missing`: record `Settlement(stayed)`, restore the
   members (section 5.6 decides a restored companion's binding), and notify home the same
   way with `companionArrived = false`.
8. **Settle at home.** `HopSettled` records `HopDone` once (a second notice that disagrees
   is an invariant violation) and always answers `HopSettledAck`.

### 5.2 Return (a companion goes home)

The return is its own operation, bound by home's record, never by `follows`.

1. **Separation sweep** (each drain): a companion abroad that follows no one and has no
   attempt or departure in flight sends `Reserve(K, [companion with its ticket])` to home
   and records `Stranded` if not already. The same operation runs when the player leaves
   the game, when a further destination refused the companion, when the player's own
   crossing left it behind, and before an instance stops (section 5.8).
2. **Verify at home** — one function, `VerifyReturn(custodian, ticket)`, the only door that
   reads custody for an arriving companion:
   - `Released` → `ReserveRefused(Released)`;
   - `Pending(K0)` → `ReserveRefused(Pending)` and resolve `K0` (section 5.3);
   - `Home`, or `Away(X', e')` with `X' ≠ custodian` or `e' ≠ ticket epoch` → `Stale`;
   - an incarnation home did not mint → `NotMyTitle`;
   - `Away(custodian, e)` matching → accept.
   Home never refuses a verified return for capacity: the held slot is the lease. A live
   lease already naming the incarnation answers `Duplicate` until it expires.
3. **Depart and commit** as in 5.1 steps 5 and 6, with no hop request: the ticket already
   names the current epoch. At the commit home verifies again (the fence against a recall
   or install between lease and commit; a failed re-verification answers `Missing`), lands
   the companion in the held slot, and records `Arrival` with `Readopted(slot, placement)`.
   The reducer sets custody `Home` at the ticket's epoch and clears the hold. The
   companion's binding (role, kit, look, followed seat) is derived from the placement as
   installed now.
4. **Refusal at the custodian.** `Stale` or `Released` is home's definitive word about its
   own title: the custodian records `Retire` for an embodied copy, or `Settlement(retired)`
   for a shed image, and the body is gone. `Pending` and `Duplicate`: keep the attempt and
   ask again. `NotMyTitle`: the custodian is talking to the wrong authority (a home restarted
   without its checkpoint has a new lifetime identity); it keeps the image and keeps asking
   the authority the ticket names. Nothing is destroyed on silence.

### 5.3 Resolving a pending hop

Home resolves `Pending(K)` by asking the hop's destination `Status(K)`:
`Committed:ids` → `HopDone(K, companion ∈ ids)`; `Missing` → `HopDone(K, false)` (the
destination recorded `Dead(K)` as it answered, so it can never lease or commit `K` again);
`Reserved` → ask again later.
Home asks on every return or hop request that meets the pending hop, and on recovery.
The source's own `HopSettled` resolves it the same way; the two must agree.

### 5.4 Stranded, grace, shed

A companion is stranded when its return cannot be made: home unreachable, not running,
or answering `Pending` or `Duplicate` for longer than the grace window. The custodian keeps
it embodied for the authored grace window (`roles.strandedGraceSeconds`, its own ticks,
the deadline in the `Stranded` record so a restart keeps it), then **sheds**: it records
`Departure(K, home, [companion], shed)`, frees the slot, and treats the record as a durable
return obligation: `Reserve` until home answers, `Commit` on `Reserved`, and on `Missing`
or `Dead` a new attempt under a fresh key, recorded as `Attempt(K', image K)` so a crash
keeps the mapping (a shed image has no body to restore, and a spent key is never reused).
Only the latest attempt's answer counts. The custodian never drops custody on a timer; the
record outlives reaping and stop (section 5.8). A shed image's only ends are home's
`Committed` or home's `Stale`/`Released`.

### 5.5 Recall and release

Only home ends or recalls a title.

- **Recall.** Precondition: custody `Away` or `Pending`, no live return lease at home, and
  the owner reachable (the player embodied at home or at an authority home's link to is
  up; never on silence while the owner may share the companion's partition) and not
  standing at the authority that holds the title (a companion beside its owner is not
  stranded, and a recall there would show the player two). Home records
  `Recall(e'')` with `e''` above every epoch it ever issued, embodies the last committed
  image in the held slot, and records the player's `Notice`. Every pending hop is
  superseded: its later settlement moves no custody. A copy abroad at a lower epoch is
  stale; its next `Reserve` or `HopRequest` is refused `Stale` and it retires. Recall
  happens by the player's request or an author's declared recall window; the default is
  to wait.
- **Release.** Only the taped install that removes the placement's role releases a
  title. A custodian never releases; what it holds is retired on home's answer. There is
  no release message to deliver, so there is no release delivery to lose: the custodian's
  durable return obligation carries the question to home as many times as it takes. A
  copy abroad that still follows its player learns of the release on its next contact
  with home (its next hop request or return) and retires then, with the player's notice;
  until that contact it follows as before.
- **Refill.** A placement whose role is declared disposable spawns a new individual after a
  release or recall and names it to the player as such; a default role waits.

### 5.6 Install versus departure

The installed definition owns **bindings** (role, follows, kit, look); custody records own
**title, hold and epoch**. A restored or re-adopted companion derives its binding from the
placement as installed at that moment, never from the captured one. An install that
removes the placement's role records `Install`: custody becomes `Released`; a companion
embodied at home despawns; a later `Settlement(stayed)` for home's own in-doubt departure
records `retired` and restores no body; later returns and hop requests answer `Released`.
An install that changes the role or followed seat keeps the title and changes the binding
on the next embodiment at home.

### 5.7 Crash and recovery

A restart restores the checkpoint, redoes every record past it through the reducer, and
re-sends what the records say is unanswered: `Status(K)` for each in-doubt departure,
`Reserve` for each shed image without a lease answer, `HopRequest` for each attempt that
had reserved and awaits home (volatile attempts that had not reserved are simply lost;
their destination leases expire), `HopSettled` for each unacknowledged settlement, and
home's `Status` for each pending hop. Leases are volatile; a commit for an expired lease
is `Missing`, and the source restores or re-reserves. The departure redo installs the
tickets the record carries and mints nothing.

### 5.8 Reaping and stop

An authority holding a title away, a pending hop, an in-doubt departure or a shed image is
not reaped (the forwarding-route rule widened to these records). An explicit stop first
runs the separation sweep for visiting companions and completes after the drain, as an
emptied instance a queued transfer names does today; a return that is not settled by then
stays an in-doubt record in the final checkpoint, which the stop must write. Both are new
lifetime mechanisms the build adds.

## 6. Trust derivation

Every decision reads only the deciding authority's own facts:

- **Whom a companion follows**: the member id of a player in the same reservation, bound
  to the slot and generation that member landed at in the same commit; cleared when that
  slot's generation moves. Member ids, not positions, survive filtering.
- **What its driver may observe**: derived at every check (`WorldServer.TryRoleVerdict`
  before stored grants): Drive and Observe over the companion bodies bound to the driver at
  their landed generation, Observe over each followed slot while it holds the pinned
  generation, nothing else; `TryGrant` refuses body, Control, Mutate and Edit grants to a
  role driver. Runtime, authority and budget identity is (role, resolved driver pin): two
  roles naming one addon mount two runtimes.
- **Who holds the title**: home's own `Hop`/`HopDone`/`Arrival`/`Recall`/`Install` records,
  read through `VerifyReturn`, the single door for returns, hop requests and the commit
  re-check. No alias table, admission row or sender-declared membership enters it.
- **Every instance-targeted read** (`body.where … instance:`) answers through one
  disclosure door.

## 7. The held slot

While custody is `Pending` or `Away`, the held slot is reserved for the title: the census,
`ReconcileInhabitCounts`, admission allocation and `PreferredOrLowestFree` read one
predicate, `IsHeld(slot)`, derived from the custody records. A verified return's lease is
bound to that exact slot and never falls back. Home never refuses its own verified return
for capacity.

## 8. The model

`CompanionCustodyModel.cs` is the protocol above with one home, two custodians, one player
and one companion. Faults: message loss, duplication and reordering, crash-restart (volatile
state cleared, records kept), partition and heal, lease expiry, the author's install, the
player's recall, disconnect and reconnect, a hop deadline, and a stale custodian forging a
return with a ticket it once held. `CompanionCustodyModelLawTests` explores every reachable
state under a fault budget (breadth-first over canonical states), one scope per fault
class and route (loss and duplication; crash and expiry; partition and disconnect;
install, recall and disconnect; timeout, forge and disconnect; every class with one
fault; and the routes A→B, A→home and A→B→home), with two sound reductions: a retry
is a timeout and fires with nothing in flight (duplication covers a retry racing its
late reply), and the grace window passes only after a loss. It asserts on each state:

1. at most one embodiment at the current epoch (a lower-epoch copy is stale and retires on
   contact);
2. home embodies the companion only when its title says `Home`, and the held slot is free
   while the title is `Pending` or `Away`;
3. no lost title: a copy embodied abroad is the one the title names, or the title's own
   pending hop names it, or it is stale;
4. no silent re-issue: a recall carries a notice;
5. a companion follows only a player standing beside it;
6. replay equals live: the records alone fold to the live custody facts.

From every quiescent state it then runs a fair completion (heal, deliver everything, run
every protocol move, let deadlines pass) and requires: no in-doubt departure, attempt,
lease or pending hop remains; the title and the body agree (embodied exactly once, at home
or at the custodian the title names, or released and embodied nowhere); and a companion
abroad is following its player. A cycle without a new record is a livelock and fails.

Each review finding is also a named trace through the same model:

| Finding | Trace | What it shows |
|---|---|---|
| Design H1: no delivery path for the credential | `H1` | The ticket rides the commit; the return lands `Readopted` |
| Design H2, H10: admission semantics; filtering shifts references | `H2_H10` | Per-member verdicts; the player lands alone by id with the companion listed first |
| Design H3: return contradicts "never separate" | `H3` | The return is its own request, bound by home's record, after the player disconnected |
| Design H4: held slot unclaimable | `H4` | Home never refuses its own return; the player's crossing home re-adopts |
| Design H5: release without completion protocol | `H5` | Release is home's install record; the custodian retires on home's answer after a partition heals |
| Design H6: proof possession is not custody | `H6` | A stale ticket from A is refused while B keeps custody |
| Design H7: "no row" is not "custody ended" | `H7` | A lost settlement notice leaves a pending hop that home resolves; nothing retires |
| Design H8: install versus departure | `H8` | An install during an in-doubt departure retires the restored image |
| Design H9: settlement payload incomplete | `H9` | A late grant after a hop deadline settles `companionArrived = false`; custody stays with the source |
| Design M1: duplicate incarnations | `M1` | A second reservation for the incarnation is refused `Duplicate` |
| Design M2: stranding state disposable | `M2` | Grace and the shed return survive a crash |
| Decision: recall | `Recall_…` | The stale copy retires; the player is told |
| Round-2 highs 2–7 and round-1 highs 4, 5, 7, mediums 1–4 | the exhaustive theories | Alias and cohort trust, cleanup without custody, rollback versus re-adoption, recovery of held slots and away rows, cleanup without an authority input, stranding and remote return |

Round-2 high 1 (the numeric `body.where` arm), round-1 highs 1–3 (driver conflicts, grant
survival) and round-2 medium 5 (per-role budgets) are engine construction, not protocol:
they are built as section 6 states and keep their regression legs, which are not
evidence for this redesign.

Writing and running the model changed the protocol five times before the prose could
have: a `Pending` hop answer must be waited on, not treated as a refusal; a lease at home
left dangling by a crashed source blocks later returns as `Duplicate` until it expires, so
`Duplicate` is transient and deadlines are part of progress; an attempt must not depart a
body another attempt already moved; a destination must never lease a key twice or a
duplicated commit lands a body the source restored; and a status `Missing` is final only
because it tombstones the key. The dangling-lease and double-lease cases apply to the
existing transfer escrow for ordinary travellers too and are reported as findings.

Model simplifications the build must not read as protocol: one companion; the player's own
admission always succeeds; destination capacity beyond the role is not modelled; `Uncertain`
is not modelled (it is the existing escrow's and composes unchanged); lease expiry appears
as a bounded fault in exploration and as time in convergence.

## 9. Owner calls

- Recall is by the player's request or an author's recall window, default wait, exactly
  as the decision says; the spec adds no automatic recall.
- A home restarted without its checkpoint is a new authority (`NotMyTitle`): its
  companions abroad wait forever by the decision's rule, and its placement is empty until
  the author declares the role disposable or the owner restores the checkpoint. Whether the
  engine should offer the player a recall at that point is a product call.
