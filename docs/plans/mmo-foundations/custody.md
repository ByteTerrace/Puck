# Distributed custody and trust

**[inference]** Custody can preserve one accepted right to use or spend a thing,
but cannot prevent a hostile host from running a copy. The evidence favours a
durable settlement authority outside the player's control for scarce value,
with character worlds carrying references and evidence. Leases need fences at
the resource that accepts effects. Presumed abort needs a recovered decision,
not silence. Actor placement and signatures supply useful mechanisms but do
not make an issuer honest. The companion protocol is a strong starting point
for crash recovery under an honest home; its pending-hop fence, recall losses,
partial arrivals and bounded model prevent treating it as a general exchange
protocol. These are options for the [open round](../mmo-foundations.md), not rulings.

## Evidence and scope

**[opened]** The [round](../mmo-foundations.md),
`docs/plans/mmo-foundations.md:117`, calls custody a hypothesis:
"recovery by presumed abort and recall". This page answers round questions 3
and 4 and [attack questions](attacks.md#4-questions-for-the-research-lanes) 1, 2,
3 and 6. Its attack set is A1, A2, A3, A7, A8, A9, A13 and A15.

**[opened]** The [plain-terms model](../../architecture/worlds.md#the-idea-in-plain-terms)
says at `docs/architecture/worlds.md:95`: "A character's world runs on the player's own host". That is the starting
trust problem, not a proposed restriction on where worlds may run.

**[inference]** Evidence labels have these meanings throughout this page:

- **[opened]**: a file or page was opened; the adjacent short quotation is copied
  from it. Source links identify the document, not a search result.
- **[summary]**: a search extract was available but the underlying page could
  not be opened. It cannot establish a private implementation detail.
- **[inference]**: analysis or a proposed design, including the scenarios below.
  A scenario is not a claim that it ran. Unverified questions name an owner.

**[inference]** Separate four properties before choosing a protocol. Safety is
no conflicting accepted decisions. Durability is retaining accepted decisions.
Liveness is eventually settling a pending decision under stated recovery
assumptions. Availability is being able to act now. A title held durably in
escrow can satisfy safety without providing an available body. "Neither two
places nor none" obscures that distinction.

## Findings

### Leases, fencing and restart continuity

**[opened]** Chubby's paper says the recipient must test whether a sequencer is
"still valid and has the appropriate mode". The sequencer includes the lock
name, mode and generation. Its lock-delay fallback is explicitly imperfect.
The paper also reports clients tying their availability too closely to Chubby
and using outage postmortems to reduce that dependency. This is a primary
account of both the protection and its operational cost. [Chubby][s1]

**[opened]** Kleppmann's critique requires "a fencing token with every write
request". A paused lease holder can resume after a successor has acquired the
lease. The protected store must reject an older token; the lock service cannot
retract an already delayed request. The article points to HBase GC problems
and delayed GitHub packets as practical examples; their linked incident
reports were not independently opened here. [Fencing critique][s2]

**[opened]** Redlock's author replies: "I don’t agree with the analysis."
He argues for a bounded relative clock-rate model, disputes the network-delay
argument, and discusses resource-specific checks using unique tokens.
The disagreement is about assumptions and the protected operation, not whether
a signature makes a dishonest writer truthful. [Redlock response][s3]

**[inference]** Puck should evaluate a custody option against delayed messages,
unbounded process pauses and rollback, not assume a timeout proves departure.
A random request identifier is useful for deduplication. It is not an ordered
fence. A monotonic OS clock helps measure a lease within a process; it does not
make an ownership epoch durable across restarts or honest on an attacker host.

**[opened]** ZooKeeper's recipes describe a create that "succeeds on the server
but the server crashes before returning". Their lock recipe uses an ephemeral
sequential node and a GUID to recover that uncertain result. Ordered lock
allocation still needs a protected-resource check. [ZooKeeper recipes][s4]

**[opened]** The programmer's guide says the sequential counter is "unique to
the parent znode" and documents signed 32-bit overflow. Consequently, the
suffix of a recreated lock directory is not an eternal, globally ordered
custody epoch. A service's namespace and recovery contract matter as much as
the counter's spelling. [ZooKeeper programmer's guide][s5]

**[inference]** A viable epoch design has all of these obligations:

1. One trusted allocation order per title or settlement partition. Persist a
   new epoch before exposing its grant. Consensus replication or a conditional
   durable update can implement that order; independent counters cannot.
2. A qualified identity: issuer, title incarnation and authority incarnation.
   A reused number for a different authority must not authenticate an old grant.
3. A durable comparison at every accepted economic effect. Commit the effect,
   consumed operation ID and relevant fence together. A preflight check followed
   by an unguarded write leaves a race.
4. A handoff barrier. A store remembering its largest observed token rejects
   old writes only after observing the newer token. Immediate revocation needs
   an authoritative current-epoch check or installation of the new fence before
   the successor can use the right.
5. Restore continuity. An old gameplay checkpoint cannot replace current
   ownership, consumed-operation or revocation state. Recovery from a backup
   must reconcile with a surviving authority or withdraw the old namespace.
6. Explicit exhaustion and namespace replacement. Never wrap a fence counter
   and silently reuse old credentials. A new random authority identity is
   distinct, but is not by itself proof of succession to the old authority.

**[opened]** Puck already has a durable allocation mechanism to examine before
building another. `WorldAuthorityBlobStore.AcquireActivationAsync`,
`src/Puck.World.Server/WorldAuthorityBlobStore.cs:728`, computes
`Epoch = checked((current.Value.Root.Epoch + 1))` and writes against the root's
version token. A missing root instead creates epoch 1. This is an activation
fence, not a proof of cross-world title uniqueness. [Store source][p1]

**[opened]** `WorldSiloHost.RowCrossingLog`,
`src/Puck.World.Silo/WorldSiloHost.Crossings.cs:54`, passes
`fence: bookkeeping.Fence` to `AppendJournalAsync`. Its outcome mapping sends
`RecoveryRequired` to `Uncertain`. The current implementation therefore has
more than an Orleans placement promise guarding crossing publication. This
read does not establish every store backend's durability guarantees. [Silo source][p2]

**[opened]** `WorldSessionResolver`,
`src/Puck.World.Schema/WorldSessionResolver.cs:75`, declares
`private ulong m_nextGenerationId;`; its mint sites at lines 577 and 758 use
`var generationId = m_nextGenerationId++;`. This confirms a local counter,
not the attack catalogue's proposed end-to-end mislanding. [Resolver source][p3]

**[inference]** A13 therefore remains a lane E confirmation request. Generation,
activation fence, custody epoch and content identity must not be conflated.
The desired check is a restart with an old continuation followed by a fresh
resolution of the same scope: either the old target is recovered exactly or
the old continuation is refused, never silently redirected to a new instance.

### Commit, presumed outcomes, compensation and outbox

**[opened]** The R* paper names its recovery case precisely: "in the no
information case the transaction is presumed to have aborted". Two-phase
commit records prepared votes and the coordinator's decision. Presumed abort
reduces abort logging and acknowledgements. Presumed commit changes the
absence convention, requires a forced collecting record before prepare, and
retains abort information until acknowledged. These are different logging and
forgetting protocols, not selectable meanings for a timeout. [R*][s6]

**[inference]** A prepared participant has already promised not to decide alone.
It cannot infer abort from the coordinator being unreachable. The coordinator
may have committed and told another participant. Under presumed abort, an
authoritative recovered absence can mean abort only because the recording,
retention and transaction-ID rules make that absence conclusive. A deleted
log on a hostile home is not such evidence.

**[opened]** Gray and Lamport state: "The classic Two-Phase Commit protocol
blocks if the coordinator fails." Paxos Commit replicates the decision work
and progresses with a working majority of its coordinators, at additional
message cost. It does not make a deliberately dishonest participant follow
the protocol. [Consensus on transaction commit][s7]

**[opened]** Skeen's nonblocking commit paper assumes "the network never
fails" and that failures can be reliably reported. The extra buffer state
behind three-phase commit avoids particular blocking states in that model.
Its guarantee does not cover arbitrary network partitions. [Nonblocking commit][s8]

**[inference]** Adding a precommit message to Puck cannot turn partitioned peers
into reliable failure detectors. A side that interprets silence as permission
to abort can conflict with a side that has progressed toward commit. An agreed
decision service is a meaningful alternative; three-phase messaging alone is
not. This is a failure trace from the assumptions, not a reported production
Puck incident or an independently observed 3PC outage.

**[opened]** The sagas paper warns that "it may not be possible to undo this
action". It decomposes long transactions into committed subtransactions with
compensation for partial execution. Its examples distinguish a compensating
real-world action from restoration of a hidden database snapshot. [Sagas][s9]

**[inference]** A saga is useful for a journey's ancillary work: reserve a
destination, stage an image, update a location display and notify the player.
It is insufficient for giving away an item and then refunding its seller when
the buyer has already traded it again. Compensation cannot unlearn a revealed
card. Economic compensation must be a new authorized transaction with a named
loss bearer, not silent creation of replacement value. No primary production
saga incident was verified in this pass; this is an explicit semantic limit.

**[opened]** The transactional outbox pattern warns: "The Message relay might
publish a message more than once." It stores the business change and outgoing
message in one local transaction. A relay that crashes after sending and before
recording delivery sends again. Consumers must make retries idempotent.
[Transactional outbox][s10]

**[inference]** For earned progress, record an award and its delivery obligation
together at the issuer. At the accepting authority, commit the award ID and
the credited effect together. Keep retrying until an acknowledgement. This
closes the local commit/send gap; it does not turn two databases into one
transaction, validate an invented award, or recover an issuer's erased disk.
Retaining the signed receipt with the player or another authority supplies
additional recovery evidence, subject to its acceptance policy.

**[inference]** Mechanisms should have different jobs. Use atomic settlement for
the scarce right, an outbox for reliable propagation of that decision, and
sagas for compensable surrounding work. "Exactly once" should describe one
accepted state transition per operation ID, not one packet or one handler run.

### Virtual actors and the silo

**[opened]** The original Orleans paper says, under failures, single activation
"is only guaranteed eventually". Stale membership views could register the
same grain in two directory partitions; convergence deactivated one. This is
a documented failure mode of that design, not evidence that all Orleans
versions use it. [Original Orleans paper][s11]

**[opened]** Current Orleans documentation says that design "was superseded by
a strongly consistent directory in Orleans v9.0". Directory requests carry
view numbers; ownership changes transfer registrations before the new owner
serves requests. Crash recovery queries live silos to rebuild registrations.
This materially narrows what may be inferred from the original paper.
[Current directory documentation][s12]

**[opened]** Orleans persistence documentation exposes `InconsistentStateException`
and describes its in-memory ETag as "attempting to be updated". Persistence
providers implement conflict handling; grain state also has explicit storage
operations. Directory registration, persisted state and external side effects
are distinct mechanisms. [Grain persistence][s13]

**[opened]** Puck's project references
`<PackageReference Include="Microsoft.Orleans.Server" Version="[10.*, )" />`
at `src/Puck.World.Silo/Puck.World.Silo.csproj:14`. This verifies the declared
dependency range only. Exact resolved version, custom directory choices and
cluster configuration remain lane E's implementation check. [Project file][p4]

**[inference]** Do not claim temporary duplication in Puck merely by quoting
the old Orleans paper. Equally, one registered grain is not a transaction
across two worlds, two independent clusters or an external economic service.
A delayed effect from a retired activation still needs its resource fence.
Reentrancy, outgoing calls and retries need operation-level semantics even
when execution inside one activation is serialized.

**[opened]** Akka sharding lists "Mistakes in the deployment process leading
to two separate Akka Clusters" among reasons a shard can run twice. A lease
can gate entity creation. Its documentation also describes corrupted sharding
coordinator data after invalid partition handling. [Akka sharding][s14]

**[opened]** Akka's split-brain documentation explicitly discusses "JVM pauses
e.g. garbage collection". A paused node can resume with old actors briefly
running before learning that it was removed. Downing margins trade failover
speed against overlap risk. [Akka split-brain resolver][s15]

**[inference]** Virtual actors fit a world identity that outlives its current
process. They supply placement, routing and lifecycle. They do not eliminate
storage fences, accepted-operation deduplication, cross-cluster uniqueness or
the trust policy for a foreign issuer. Puck can retain its world abstraction
without making a grain's location the economic record of title.

### MMO transfer and failure evidence

**[opened]** CCP describes nodes that "can play host to one or more in-game
solar systems". Fleet notifications let operators allocate more resources to
expected battles. Reinforcement is capacity placement; the page does not
describe a transfer commit algorithm. [Fleet fight notification][s16]

**[opened]** In the M2-XFE report CCP records "duplicated ships/mods" and
"ghost killmails" despite reinforced systems. It describes a wreck with loot
at the destination and the fitted ship at the source. CCP chose limited
intervention, including leaving duplicated looted modules and insurance
payouts. That is direct evidence of transfer inconsistency and an operator's
repair policy, not evidence of perfect no-dupe custody. [M2-XFE report][s17]

**[opened]** CCP's Atioth incident announcement says the failed system "was
automatically moved to a new fleet fight node" and was still stressed during
recovery. Only the staff announcement is evidence here; player replies are
not an account of the database recovery algorithm. [Node failure notice][s18]

**[inference]** These sources do not disclose the exact EVE commit point,
inventory locking, journal flush policy or fencing during node replacement.
The answer to how EVE always prevents two copies is therefore: the opened
evidence does not establish that guarantee, and M2-XFE refutes an absolute
claim at the observable game level. Puck should borrow capacity isolation and
operational visibility without inventing EVE's private correctness protocol.

**[summary]** Blizzard's cross-realm zone announcement describes grouped
characters sharing a zone across home realms. Its paid Character Transfer
store page describes arrival on a new realm after completion. Both underlying
pages refused opening here. These describe different user operations; neither
search extract establishes their internal storage protocol. [Zone announcement][s19]
[Transfer product][s20]

**[opened]** Blizzard's Warband bank description says "only the character that
logs into the realm first will have access to the Warband Bank" for simultaneous
accounts. It is an observable restriction on concurrent access to shared
items and gold, not disclosure of a database lock implementation. [Warbands][s21]

**[opened]** A Blizzard support response to a stuck character transfer says:
"Our staff should be able to cancel the transfer so you can clear out the mail".
The thread's player report says login remained locked while processing. This
is a primary incident report and a staff recovery response; the player's
guess about the internal race is not treated as established cause.
[Transfer incident][s22]

**[opened]** Blizzard staff separately acknowledged "cross-realm trading has
been disabled for now while we work on a fix" in a failed-trade/login thread.
The post establishes an operational shutdown of the feature, not a confirmed
dupe exploit or its commit protocol. [Trade incident][s23]

**[inference]** WoW provides useful precedents for a transfer lock, explicit
repair and restricting access to a shared bank. The evidence does not reveal
whether a realm transfer uses 2PC, a workflow over one database, or another
scheme. A player-visible locked character can be unavailable while its record
still exists. No supported claim here says that a lock always prevents loss.

**[opened]** FFXIV's guide states that a travelling character can appear under
home but "you will be unable to log in with them from there". Login resumes
at the visited data center. Travel has explicit restrictions on facilities
such as retainers and mail. These are observable ways of limiting concurrent
access, not a published atomic transfer implementation. [FFXIV travel guide][s24]

**[opened]** Square Enix reports that "network connections along certain internet
routes were unstable" and that it suspended Home World Transfer and Data Center
Travel for the affected data center. This is evidence of
availability loss and conservative operations, not of a rollback or dupe.
[FFXIV network incident][s25]

**[inference]** FFXIV demonstrates that home identity and playable location
can differ, with one login location accepted by the operator. Its exact
record ownership and crash reconciliation remain unverified. All three MMOs
have an operator that can define accepted outcomes and repair accounts. None
of these sources demonstrates atomic exchange between mutually hostile
player-owned databases. Their restrictions cannot be silently promoted into
that stronger guarantee for Puck.

### Byzantine authorities, receipts and transparency

**[opened]** Castro and Liskov describe faults with "arbitrary behavior".
PBFT supplies replicated state-machine agreement under a bounded faulty
replica population, conventionally at least `3f + 1` replicas to tolerate `f`.
Its liveness requires progress conditions on communication; it does not promise
progress through every partition. [PBFT][s26]

**[opened]** A later application team's PBFT study reports "lack of robust
handling of non-determinism" and substantial integration work for an electronic
voting application. This is independent practical evidence about engineering
limits, not a disproof of the agreement theorem. [PBFT application study][s27]

**[inference]** An issuer running three replicas under one stolen credential
has not obtained three independent trust domains. A Puck BFT option needs
named replica operators, membership and key rotation rules, quorum evidence,
recovery continuity and a bound on correlated compromise. It still cannot
turn a fabricated but correctly authorized dungeon result into true gameplay.
Agreement protects a ledger's decisions; accepted inputs need their own policy.

**[inference]** A signed receipt proves who signed particular bytes. For custody
those bytes should bind the issuer, subject, asset namespace and incarnation,
operation ID, previous and next epoch, source, destination, rights or amount,
terms/program identity and outcome. A spendable claim additionally needs one
shared consumption order. Receiver-local replay protection stops reuse at that
receiver; it does not stop presenting the same claim at two isolated receivers.

**[opened]** Certificate Transparency's RFC says: "The logs do not themselves
detect misissued certificates". Inclusion and consistency proofs make an
append-only history auditable. The RFC explicitly considers conflicting log
views and comparison of signed tree heads to expose them. A signed promise
to include an entry is distinct from a proof that it has been included.
[Certificate Transparency][s28]

**[opened]** Signal's key-transparency account aims to make identifier/key
associations "globally consistent and transparent". It combines checks by
users, contacts and third-party auditors. The attack it addresses is a
directory presenting a substituted key; this page does not report that such
a compromise happened in production. [Signal key transparency][s29]

**[inference]** Receipts held by both counterparties and independently witnessed
logs make equivocation discoverable and recovery less dependent on home.
They do not automatically prevent double spending before witnesses compare
views. To prevent it, admission must wait for a common final decision or an
accepted quorum certificate. To detect it afterwards, the economy must name
who bears the loss and how disputes settle. Logging every reward publicly
also exposes activity; commitments and controlled evidence disclosure need
design, not a promise that hashing makes private facts secret.

**[inference]** An append-only local file is append-only only while its operator
obeys. A hostile home can fork it, erase it or sign incompatible histories.
Home is the attacker in A7. Higher epochs signed by that home merely number
its lies unless another authority limits what those epochs authorize.

### Payment channels and atomic swaps

**[opened]** The Lightning paper makes hostile-party contracts "enforceable
via broadcast over the bitcoin blockchain". Channels reduce ordinary
settlement traffic by retaining enforceable commitments and timelocks. Their
escape route is an independently enforced ledger, not either peer's promise
to return value. [Lightning paper][s30]

**[opened]** BOLT 3 specifies that "the HTLC-timeout transaction is timelocked"
and distinguishes it from the success transaction carrying the payment
preimage. These are concrete success/refund mechanisms, with additional
channel commitment and revocation rules. [BOLT transactions][s31]

**[opened]** Herlihy describes an escrow whose "Hashlock h" controls redemption
and whose "Timelock t" permits refund. His protocol explicitly budgets time
for publishing changes and observing them. Its atomicity protects conforming
parties under its ledger and timing assumptions; it does not compel a
nonconforming party to complete a trade. [Atomic swaps][s32]

**[inference]** The two-party precedent is useful. Alice escrows asset X for
Bob under a hash and a longer deadline. Bob verifies that lock and escrows Y
for Alice under the same hash and a shorter deadline. Alice redeems Y by
revealing the secret; Bob uses it to redeem X before the longer deadline.
Absent redemption, refunds become available. Both asset authorities must
enforce these transitions and finality independently of Alice and Bob.

**[opened]** Flood & Loot explores attacks that make participants "overload the
Bitcoin blockchain with requests" during forced settlement. Congestion can
defeat timely recovery before HTLC deadlines. The paper evaluates an attack;
it is not evidence that a theft of that size occurred in the wild.
[Flood & Loot][s33]

**[inference]** For Puck, HTLC-like authored contracts could exchange rights
issued by two trusted settlement domains whose users distrust each other.
They cannot exchange trustworthy value when either domain can invent balances,
rewrite expiry or refuse redemption. Timeouts also lock capital, give a party
an opportunity to abandon, and concentrate load during mass disconnects.
Puck need not use a blockchain; it needs an enforcement point neither trader
can rewrite and a precise finality/deadline contract. Simulation ticks on a
paused player host cannot serve as a shared economic deadline.

### Revocation before expiry

**[opened]** RFC 5280 defines a CRL number as a "monotonically increasing
sequence number" scoped to the CRL issuer and scope. Lists have issue/freshness
information and can be delivered ahead of verification. A list's absence of
a key is useful only when its scope and freshness are acceptable. [CRLs][s34]

**[opened]** RFC 6960 distinguishes the responder's known-status time from
signature time. It says responses with an elapsed `nextUpdate` "SHOULD be
considered unreliable". A cached OCSP good response is not proof that no
revocation happened afterwards. [OCSP][s35]

**[opened]** Let's Encrypt says "revocation is an unreliable system" when
explaining its short-lived certificate option. Shorter credentials reduce the
exposure window and require reliable automated renewal. They do not provide
instant revocation below expiry. [Short-lived certificates][s36]

**[opened]** Puck's attestation reference,
`docs/reference/attestation.md:261`, says: "Attestation performs no path
discovery and fetches no missing certificates or bindings."
It separately describes committing a sequence high-water mark at the receiver.
That supplies a useful pure-verification boundary, not a reason to omit
revocation data. [Attestation reference][p5]

**[opened]** `WorldAdmissionDoor.BuildTrustList`,
`src/Puck.World.Schema/WorldAdmissionDoor.cs:87`, passes `MaximumAge: null` for
entries and null default binding ages at lines 93 and 94. This read verifies
those parameters, not platform issuance lifetimes or every revocation-related
path. Lane E owns that broader audit. [Admission source][p6]

**[inference]** A root can publish signed, versioned refusal data through the
ordinary host data path. The verifier consumes a supplied snapshot and records
its version with the accepted decision. Define key, issuer and subject bans
separately: changing a compromised key must not evade an account ban, and a
key rotation must not silently revoke legitimate historical ownership.

**[inference]** The revocation choice includes existing sessions and outstanding
grants. Checking only the next handshake leaves an already connected attacker
active. New awards, spends and custody commits need the chosen policy version
or a bounded authorization lease. Revocation should block unauthorized new
actions without deleting title or confiscating valid escrow by accident.
Preserve evidence of already finalized decisions; a fresh signature after
compromise cannot be made old merely by claiming an earlier timestamp.

## What generalises from companion custody

**[opened]** The [protocol vocabulary](../companion-redesign.md#1-vocabulary)
states at `docs/plans/companion-redesign.md:25`: "A crossing moves custody, never title." Its image is separate from
the title and current custody. A ticket is verified with home, not trusted as
a bearer secret. Transfer keys, leases and tombstones use the crossing log.

**[opened]** Its [append contract](../companion-redesign.md#31-append-outcomes)
states at `docs/plans/companion-redesign.md:126`: "After `Uncertain` the authority latches". Recovery reads the log
before that authority can grant, embody or verify again. The specification's
`HopQuery` is phase-bound; a reservation confirmation cannot stand for a
commit confirmation.

**[opened]** Its [pending-hop resolution](../companion-redesign.md#53-resolving-a-pending-hop)
says at `docs/plans/companion-redesign.md:325`: "A hop whose destination cannot be reached stays pending". A destination
may already hold home's confirmation. Silence therefore cannot justify
recall from pending, even though a reservation lease has a deadline.

**[opened]** The [model](../companion-redesign.md#8-the-model),
`docs/plans/companion-redesign.md:467`, asserts
"at most one embodiment at the current epoch". Its crash fault clears
volatile state and retains records. Its forger is a former or current
custodian. It counts current-epoch embodiments when a forger is active, so
old puppets can still exist. The paper model's fair completion heals and
recovers authorities; permanent disappearance is not that completion.

**[opened]** The same section, `docs/plans/companion-redesign.md:546`,
states: "signed tickets are not modelled" and
limits the player's transfer model. Its refinement fixture uses retained
authorities and local seats, excludes some federation concurrency, and
defers unbuilt moves. The [build plan](../companion-redesign.md#9-build-plan)
says at `docs/plans/companion-redesign.md:551`: "Steps 1 to 5 are built." This lane did not run the model or turn those
documented bounds into new verification results.

**[inference]** The generalisable machinery is qualified identity, a durable
per-authority reducer, operation IDs, terminal negative outcomes, explicit
uncertainty, phase correlation, record-before-acknowledgement and restoration
of unresolved obligations. The parts to redesign are the meaning of title,
the commit unit, rights after recall and the parties trusted to answer queries.

| Subject | Reusable custody idea | What does not transfer unchanged |
|---|---|---|
| **[inference]** Character world | One accepted incarnation and active-session epoch; durable travel obligation; images do not grant presence | A human seat has control, reconnect and admission rules that differ from a companion. One seat across hostile homes needs an external session/title authority. |
| **[inference]** Unique item | Stable issuer-qualified title; exclusive accepted use or spend grant | A sale changes the owner, whereas a companion crossing preserves title. Inventory transformations need consume/create conservation and lineage, not just embodiment. |
| **[inference]** Currency | Exclusive spend authorization and consumed operation IDs | Amounts split and merge. One body per incarnation says nothing about total supply or two debits spending one balance. |
| **[inference]** Table stakes | Reserve value before play and retain unresolved obligations | All required stakes and the payout decision need a common atomic boundary. Partial companion landing is wrong for an all-or-nothing wager; recall cannot refund a committed loss. |
| **[inference]** Machine save RAM | One accepted writer/version if the cartridge or world requires it; durable checkpoint and delivery | A save may legitimately be forkable personal data. Competitive scores need a trusted input and execution history; unique bytes are not honest gameplay. Pin cartridge and interpreter semantics for replay. |
| **[inference]** Appearance | Identity and provenance when an entitlement matters | Ordinary authored looks may be freely copyable. Unnecessary exclusive custody adds outages without protecting any scarce right. |

**[inference]** Recall illustrates the boundary. Recovering a companion from
home's last committed image can discard newer abroad writes, as the model
allows. Recovering a wallet that way could restore money already paid to
someone else. For value, recall must revoke future rights through the accepted
settlement order and preserve finalized spends. Rendering an older image can
be harmless; authorizing effects from it is the dangerous step.

**[opened]** `WorldTransferEscrow`,
`src/Puck.World.Server/WorldTransferEscrow.cs:2455`, looks up consumed mobility
by `key: (sourceAuthority, mobility.Incarnation)` and compares epochs in the
destination's table. This is a local replay fence, not a global agreement
between two destination authorities. [Escrow source][p7]

**[inference]** The A7 counterexample needs no network failure. A hostile home
confirms the same title to B and C, or gives each a distinct increasing epoch
while concealing the other. Both destinations see internally plausible
records. Local maxima do not reveal the competing grant. Signed receipts can
later expose equivocation; only a shared acceptance order, constrained escrow
or refusal to honour that issuer closes it before value escapes.

## Options for Puck

### Round question 3: custody and the hypothesis

**[inference]** Refine the hypothesis to: title belongs to the authority whose
economic decisions recipients accept; a residence called home need not be that
authority. A crossing can move the currently accepted use grant. Gameplay
images are evidence for reconstruction, never an independent ownership grant.
Epochs work only with a durable allocator and effect-side enforcement.
Presumed abort applies only to protocol-defined recovered absence. Recall is
a new fenced decision with a declared state-loss policy, not rollback of
external obligations.

| Option | Invariant and proposed enforcement | Cost and failure boundary | Attack coverage |
|---|---|---|---|
| **[inference]** Trusted home per title, durable custodians | Home serializes grants; each custodian records arrival and consumes epochs before accepting effects | Extra home round trips and dependency. Crash recovery works only with retained logs; hostile home remains outside the guarantee. Pending destination can block forever. | Addresses A8 with durable hosts and A13 with separately fixed target identity. Leaves A7 and A15; A1/A2 need provenance policy. |
| **[inference]** Shared settlement world outside player control | One transactional door consumes the current right and creates its successor; all scarce effects use it | Service/quorum availability, storage and trust cost. Can keep settlement small while zones remain distributed. Cannot force a hostile renderer to erase a puppet. | Closes economic A7/A8 if every reward/spend uses the door. Bounds A3 loss after acknowledged settlement. Does not alone close A2 or A9. |
| **[inference]** Bounded escrow rights delegated to a world | Settlement removes a budget or set of assets from general circulation before delegation; redemption is unique | Locked value and delayed close. A dishonest delegate can distribute its budget unfairly; offline recipients cannot all be promised full redemption without an enforcement authority. | Bounds rather than eliminates A1/A3/A7 losses. A8 requires current escrow state. A15 becomes a settlement deadline question. |
| **[inference]** Freely forkable personal worlds | Receiving worlds treat copies as personal presentation or untrusted claims | Broad offline freedom; no shared scarcity or globally exclusive session promised | Accepts A1/A7 locally and contains their economic reach by refusing conversion. Does not establish custody of table stakes. |

**[inference]** An unreachable title authority has no automatic substitute.
An already committed custodian can retain its existing rights within their
declared scope. A pending move stays pending. A new exclusive grant waits for
the title service or its authorized successor. Predelegated bounded rights
can continue only if their enforcement does not depend on the missing home.
A product choice to restore availability by accepting conflicting copies must
name the loss and reconciliation rule; it is not presumed abort.

**[inference]** For A15 the leading alternatives are durable waiting, a shared
decision service that can settle without the destination, or time-bounded
rights whose expiry every accepted-effect service enforces. The third option
requires a trustworthy deadline authority and handling of pre-expiry in-flight
effects. Recalling a body while a disconnected custodian can still mint valid
rewards leaves the economic attack open.

### Round question 4 and attack question 1: classes of value

**[inference]** The following is a classification to discuss with lane D and
the owner, not an engine enum. Authors can program meanings while the engine
provides generic transactions, verification and durable state mechanisms.

| Value or claim | Leading representation | Cost and guarantee limit | Attacks |
|---|---|---|---|
| **[inference]** Currency, tradeable items, redeemable coupons | Issuer-kept balance/title or escrow, referenced from the character world | Spend requires accepted settlement. Copying the reference gains nothing; issuer inflation remains a trust decision. | Closes A1/A7 double-spend at that issuer; A8 needs durable consumed IDs. |
| **[inference]** Table stakes | Prefunded escrow with one finalized wager/payout decision | Locks liquidity; dishonest game outcomes still need lane D's fairness design. | Prevents A3 refund-by-restart and A7 reuse; does not hide cards or validate a dealer. |
| **[inference]** Achievement, dungeon completion, nonconsumable unlock | Subject-bound signed claim with issuer-qualified meaning and replacement/revocation rules | Offline verification possible inside a freshness policy. Copying the claim for the same subject may be harmless. A dishonest issuer can still lie. | Addresses A1/A2 forgery and laundering only where receiver policy accepts that issuer for that predicate. |
| **[inference]** Reward earned from an achievement | Signed evidence plus an issuer-kept redemption ID | Evidence is copyable; redemption is one accepted transaction. Audience alone is insufficient across multiple redeeming worlds. | Closes replay variants of A1/A7/A8 within the redemption domain. |
| **[inference]** Name, preferences, freely authored look | Unsigned owner data, sanitized by receiving policy | Cheap and portable; must never become scarce entitlement by accidental interpretation. | Contains rather than prevents A1/A2. |
| **[inference]** Save RAM or local progress | Forkable local save unless a specific world requires certified continuity | A competitive import needs trusted observations, code/input pins and acceptance rules. | Local persistence addresses crash A3; does not close dishonest A1/A3. |

**[inference]** A character world can compose these representations naturally:
personal preferences, signed achievements and references to settlement-held
assets can coexist. The whole document need not be either globally trusted or
globally rejected. A platform signature on an account authenticates the account;
it must not automatically license that account to issue valuable dungeon facts.

**[inference]** Across different trust domains, realistic exchange choices are
a common escrow authority, independent ledgers with enforceable conditional
exchange, or deliberately limited bilateral credit. Common escrow costs shared
trust and availability. Conditional exchange costs deadlines, collateral,
monitoring and compatible finality. Credit costs exposure to default and a
dispute policy. Signed promises alone leave A3/A7 open. A BFT settlement group
changes the trust threshold, at greater operational cost; it is not necessary
merely because two players distrust one another.

### Attack question 2: when progress becomes durable

**[opened]** `WorldIdentity.TryAdopt`,
`src/Puck.World.Schema/WorldIdentity.Records.cs:129`, iterates
`foreach (var cell in (carried.Facts?.Cells ?? []))` and routes facts and record
fields into local write methods. The opened method checks matching identity
IDs and write refusals, not a foreign issuer's signature. This is the adoption
door relevant to A2, not a complete audit of its callers. [Identity source][p8]

**[inference]** Three useful durability policies exist:

- Return-only adoption minimizes traffic, but acknowledged-looking progress
  can disappear with the destination. It leaves A3's session-loss case open.
- Issuer journal plus outbox permits asynchronous write-through. Display the
  award as pending until the accepted authority acknowledges it. Batching
  reduces requests but expands the explicitly uncommitted window.
- Synchronous acceptance before reporting an award as final gives the strongest
  player promise, at settlement latency and availability cost. It suits scarce
  rewards better than every movement tick.

**[inference]** Evidence favours durable write-through for value that the UI
calls earned. It must write to the accepted issuer/settlement authority, not
merely the player's home file. A foreign world submits an issuer-qualified
event with a unique award ID, subject, policy/program pin and causal evidence.
The receiver decides which namespaces and predicates that issuer may change.
Duplicate submission returns the prior outcome. Refused provenance is not
laundered through a valid home signature. This addresses A2 while the durable
acknowledgement bounds A3. It does not make a dishonest authorized issuer fair.

### Attack question 3: what a desktop must persist and what it proves

**[inference]** For crash recovery, a participating desktop needs a coherent
checkpoint and journal suffix; allocated IDs and current authority incarnation;
grants, arrivals, departures and terminal outcomes; consumed epochs and replay
floors; unresolved delivery/settlement obligations; and the accepted policy and
key versions needed to interpret them. Acknowledgements must follow the durable
decision, not an OS-buffered write that recovery cannot recover.

**[inference]** This proves continuity only under the honest-host storage model.
It cannot prove that an owner did not restore a backup or fork a second process
with the same keys. For scarce value, keep receipts and final acceptance outside
that owner's control. On a lost local disk, reconcile from that surviving
authority. If no trusted record survives, declare the rights unresolved or
unrecoverable; do not mint replacements from an image.

**[inference]** Allowing another player's body on a desktop remains an option.
Limit that host to local simulation and presentation while authoritative value
settles elsewhere, or require a trusted host for contested play. The first
preserves broad hosting but cannot guarantee fair outcomes from its inputs;
the second costs hosting capacity and constrains placement. Lane C owns the
contested-scene choice. Neither choice is decided by this page.

### Attack question 6: revocation choices

| Option | Enforcement and cost | Failure boundary and A9 |
|---|---|---|
| **[inference]** Short-lived bindings | Issuer renews only authorized accounts; verifier bounds lifetime and rejects expired authorization | Simple offline checking within the window, but stolen keys work until expiry and renewal outages deny service. |
| **[inference]** Signed refusal snapshot | Root distributes scoped sequence/version, issue time and freshness limit; host persists its newest accepted version | Efficient local checks and deterministic policy input. Stale snapshots leave a bounded attack window or must fail closed. |
| **[inference]** Fresh online status, possibly stapled | Root or delegated responder signs status bound to the credential; acceptance checks freshness | Narrower revocation delay, but status service availability and traffic matter. Cached good status is not instantaneous revocation. |
| **[inference]** Hybrid | Short bindings plus pushed refusal data, with fresh status for valuable actions | More machinery, but can separate low-risk observation from spends. Needs one policy for established sessions and outstanding escrow. |

**[inference]** None closes A9 during a partition while also allowing unlimited
offline high-value use. The owner chooses tolerated delay and service loss.
Lane E should locate the common admission and value-acceptance doors; revocation
must not become a check each caller can forget. External time/status observations
become recorded decisions at tick boundaries so deterministic replay does not
refetch today's status and reinterpret yesterday's accepted transaction.

### Puck-native combinations and remaining attacks

**[inference]** Put the narrow accepted-value state in a settlement world, with
authored `.puck` programs describing transfers, wagers and permitted claims.
Use generic engine machinery for durable atomic state changes, signature
verification, deadlines and receipts. Characters remain worlds, and zones can
remain independently hosted. A portal or hidden seam invokes the same custody
handoff; visual continuity must not create a second grant.

**[inference]** Derived meshes, projections and other caches can be regenerated
from their inputs. Current custody and consumed value cannot. A restored image
should request current rights before enabling economic actions. Deterministic
fixed-point replay can check a known program and input history, reduce the
cost of independent validation and reproduce disputed execution. It cannot
prove omitted inputs never existed, select the honest history from two forks,
keep a hand secret from its host, or decide who wins after a disconnect.

| Attack | What the leading combination can close | What remains |
|---|---|---|
| **[inference]** A1 | Scarce value and accepted progress require the relevant issuer's evidence and settlement | Personal assertions and dishonest accepted issuers remain possible. |
| **[inference]** A2 | Namespace/predicate authorization at one adoption door | An issuer authorized for a predicate can lie within that permission. |
| **[inference]** A3 | Durable write-through plus surviving settlement evidence retains finalized value | Pending progress, local position and availability can be lost; a hostile host can withhold service. |
| **[inference]** A7 | One external acceptance order prevents duplicate economic rights | Local puppets persist. Rewards must use the same fence or duplicates can farm them. |
| **[inference]** A8 | Durable replay consumption and restore continuity reject old operations | A hostile receiver can ignore them; trusted downstream services must reject its invalid effects. |
| **[inference]** A9 | Fresh revocation input bounds unauthorized acceptance | Partition availability, compromised roots and already finalized decisions need explicit policy. |
| **[inference]** A13 | Persisted/qualified target generations and stale-continuation refusal | The actual end-to-end exploit remains unconfirmed by this lane. |
| **[inference]** A15 | A shared decision authority or universally enforced expiring rights can settle without a vanished place | The existing pending-hop protocol blocks. Unenforced recall reopens duplicate rights. |

## How the debug world would prove it

**[inference]** These are proposed implementation checks, not completed runs.
Use the gallery's existing crossings, a character world, an authored item,
a card table and an emulated machine within `worlds/debug`. Use the existing
custody reducer and host fault seams where possible. A future implementation
lane should extend the small model for economic obligations and dishonest home
behaviour before claiming a broader invariant. This research brief adds no code.

| Step and smallest scenario | State reading or counter that decides it | Guarantee boundary |
|---|---|---|
| **[inference]** Define the accepted-right door. Give one item to a character and reference it from two places. | One current title/use grant; both renderings may exist, but only the named grant can spend or earn. Count rejected stale effects. | Observes accepted effects, not absence of a hostile memory copy. |
| **[inference]** Persist allocation and consumption. Cross the garden seam; restart after each durable step and retry the same operation. | One terminal outcome per operation, monotonic title epoch, no duplicate credit, and retained pending obligations until recovery. | Honest storage recovery; repeat with a lost checkpoint plus retained journal. |
| **[inference]** Fence stale work. Pause the old custodian after it prepares an effect; install the next grant; resume the old effect. | The effect door rejects the old epoch. Repeat with a delayed request that was already sent. | Proves the comparison occurs at effect commit rather than before it. |
| **[inference]** Restore an old image. Spend the item, then restore the character/place checkpoint from before the spend. | The display can rebuild; current settlement still says spent, so the old image cannot spend it again. | Title/receipt continuity must survive separately from gameplay rewind. |
| **[inference]** Exercise A7. A scripted hostile home confirms one incarnation to two destinations and signs both histories. | At most one recognized use grant; competing receipts are visible; accepted rewards cannot be collected from both copies. | Needs a settlement authority outside the scripted home. |
| **[inference]** Resolve uncertain commit. Lose the arrival acknowledgement, then partition the destination. | Source remains in doubt; it neither refunds nor creates a new current grant. On recovery it adopts the logged result. | No claim of availability during uncertainty. |
| **[inference]** Resolve A15. Make a pending destination permanently unavailable. | Waiting option shows retained title and a clear pending state; new decision-service option settles once without reviving old accepted rights. | Owner must select the availability and state-loss contract first. |
| **[inference]** Write progress through. Earn an award; crash after issuer commit before send, then after receiver credit before acknowledgement. | Outbox obligation survives, retry returns prior award ID, credit count stays one. A foreign namespace write is refused. | Distinguishes durable acceptance from provisional UI progress. |
| **[inference]** Stake and settle a table round. Fail after each player's funding and around payout, including disconnect by the loser. | No round starts with missing required stake; total available plus held value is conserved except authored mint/burn; payout finalizes once. | Fair dealing and hidden information remain lane D's separate proof. |
| **[inference]** Exercise conditional exchange. Stop either party before secret reveal and near each deadline. | Both conforming parties obtain the agreed rights or retain/refund them under the contract; no success/refund double acceptance. | Repeat under settlement backlog to expose deadline assumptions. |
| **[inference]** Revoke a key on a live connection and replay old status data after restart. | New valuable actions stop at the declared policy boundary; older policy cannot replace newer; accepted title remains recoverable. | Offline stale-status behaviour must match the selected policy. |
| **[inference]** Resolve A13. Retain a crossing continuation, restart target resolution, create another instance of the same scope. | The continuation identifies the original recovered generation or is refused. | Lane E first confirms the real path and selects the fixture. |
| **[inference]** Fork a cartridge save. Import the same save twice into personal and competitive contexts. | Personal forks work where allowed; competitive reward redemption uses a single accepted event/continuity policy. | Replay verifies execution only from its accepted initial state and inputs. |

**[inference]** The principal proposed invariant is: within each declared
settlement domain, every scarce right is available, held for one unresolved
transaction, transferred, consumed or explicitly destroyed, and no two accepted
effects consume the same right/version. Its proposed enforcement point is the
transactional settlement door, including authorization, fence comparison and
deduplication in the commit. This page does not claim such a generic door is
built. The invariant stops at authorities the domain accepts and at external
effects that bypass its settlement. A display count alone cannot prove it.

## Open questions

| Question | Who can settle it and what evidence is needed |
|---|---|
| Which value must be globally scarce, and which may fork freely? | Owner, with lane D's economy evidence. Decide per accepted economy, not one universal rule for all worlds. |
| Who may be a title/settlement issuer? | Owner after synthesis. Compare studio service, federation of accepted operators and player-local personal domains. |
| Is pending custody allowed to wait forever? | Owner; implementation lane models the selected recall/deadline rule, including accepted economic effects from stale copies. |
| What does the actual Orleans configuration promise? | Lane E opens resolved package/configuration, directory selection, store backend and activation lifecycle. Do not infer from a package range alone. |
| Can restoring or deleting a store root reuse externally accepted identity? | Lane E traces backup, restore, credential and authority-name lifecycles against `WorldAuthorityBlobStore.AcquireActivationAsync`. |
| Does the generation counter actually misroute a continuation? | Lane E traces and reproduces A13 with old continuation and fresh target resolution. Local counter evidence alone is insufficient. |
| Which adoption door will enforce provenance? | Lanes A and E trace carried-state and durable-slot writes; an implementation lane must show one authorization rule at all paths. |
| How long may acknowledged progress remain only at its issuer? | Owner selects the promise; measurement lane compares synchronous acceptance and batched outbox across the intended network. Measure backlog, lost provisional awards and request volume. |
| What happens when an accepted issuer cheats or loses all state? | Owner defines liability, issuer removal and treatment of existing assets; lane D supplies economic attack examples. Signatures cannot choose this policy. |
| How do revocation and account bans reach existing sessions? | Lane E traces issuance/binding lifetimes and live admission; owner sets maximum delay and outage behaviour. |
| Which deadline source can independently hosted authorities share? | Implementation/measurement lane exercises suspension, restart, skew and settlement backlog. Replay consumes recorded deadline decisions. |
| What are EVE, WoW and FFXIV's exact internal commit protocols? | Studio engineering talks or direct published implementation accounts. The sources opened here establish restrictions and failures, not those algorithms. |
| Can independent deterministic validation make contested peer play acceptable? | Lanes C and D plus an adversarial implementation pass; include missing inputs, equivocation, disconnect and hidden-state attacks, not only matching hashes. |

## Sources opened and consulted

The external evidence below is primary research, implementer documentation or
operator/staff incident reporting. The explicitly marked search-only pages
were not used to assert a private protocol. Read dates and the inspected tree
revision belong to this lane's commit message.

### Mechanisms and operational evidence

- [Chubby paper][s1]. Opened through USENIX; the Google archive URL and its
  [static redirect](https://static.googleusercontent.com/media/research.google.com/en//archive/chubby-osdi06.pdf)
  failed. [Original archive attempt](https://research.google.com/archive/chubby-osdi06.pdf).
- [Kleppmann's fencing critique][s2] and [Redlock author's response][s3]. Both opened.
- [ZooKeeper recipes][s4] and [programmer's guide][s5]. Both opened.
- [R* transaction management][s6], [Gray and Lamport][s7] and [Skeen][s8].
  Opened papers. The [Lamport-hosted PDF](https://lamport.azurewebsites.net/pubs/transaction-commit.pdf)
  failed before the arXiv copy was opened.
- [Sagas][s9]. Opened Cornell copy. The
  [Princeton copy](https://www.cs.princeton.edu/techreports/1987/070.pdf) refused access.
- [Transactional outbox][s10]. Opened pattern author's account.
- [Original Orleans paper][s11], [current directory][s12] and
  [grain persistence][s13]. Opened; version distinctions retained.
- [Akka sharding][s14] and [split-brain handling][s15]. Opened.
- [EVE fleet notification][s16], [M2-XFE report][s17] and
  [Atioth staff announcement][s18]. Opened.
- [WoW cross-realm zone announcement][s19] and [Character Transfer product][s20].
  Search-only summaries; opening failed. Restriction article attempts at
  [Battle.net](https://us.battle.net/support/en/article/34787),
  [US support](https://us.support.blizzard.com/en/article/34787) and
  [EU support](https://eu.support.blizzard.com/en/article/34787) redirected to login.
- [Warbands][s21], [transfer support incident][s22] and [trade support incident][s23].
  Opened. No unverified player workaround is adopted as engineering evidence.
- [FFXIV travel guide][s24] and [network incident][s25]. Opened.
- [PBFT paper][s26] and [application-team study][s27]. Opened. The
  [USENIX PDF attempt](https://www.usenix.org/legacy/events/osdi99/full_papers/castro/castro.pdf)
  failed before the MIT copy was opened.
- [Certificate Transparency][s28] and [Signal key transparency][s29]. Opened.
- [Lightning paper][s30], [BOLT transactions][s31], [atomic swaps][s32] and
  [Flood & Loot][s33]. Opened; research attacks distinguished from reported theft.
- [CRL profile][s34], [OCSP][s35] and [short-lived certificates][s36]. Opened.
  [ACME Renewal Information](https://www.rfc-editor.org/rfc/rfc9773.html) was
  also opened for renewal context; it is not a revocation mechanism.

### Repository sources

- [AGENTS.md](../../../AGENTS.md), [delegate](../../../.claude/skills/delegate/SKILL.md),
  [orchestration](../../../.claude/skills/orchestration/SKILL.md) and
  [lane work](../../../.claude/skills/orchestration/references/lane-work.md): scope and finish.
- [Documentation index](../../README.md),
  [documentation procedures](../../development/documentation.md),
  [verification](../../../.claude/skills/verification/SKILL.md): routing and link-check procedure.
- [World skill](../../../.claude/skills/puck-world/SKILL.md),
  [adjacency and federation](../../../.claude/skills/puck-world/references/adjacency-and-federation.md),
  [hosting and release](../../../.claude/skills/puck-world/references/hosting-and-release.md):
  opened relevant sections as code maps, not substitutes for source.
- [World model](../../architecture/worlds.md), [round](../mmo-foundations.md),
  [attacks](attacks.md), [companion protocol](../companion-redesign.md): opened
  context and protocol sections. Attack-catalogue code claims not independently
  reopened remain leads, not additional verified Puck facts.
- [Authority store][p1]: `WorldAuthorityBlobStore.AcquireActivationAsync` and
  searched publication/fence paths. [Store interface][p9]: searched outcome and method declarations.
- [Silo crossing log][p2]: `WorldSiloHost.RowCrossingLog`,
  `AppendCrossingAfterAsync` and `TryRecoverJournal`.
- [Session resolver][p3]: `WorldSessionResolver.m_nextGenerationId` and mint sites.
- [Silo project][p4]: declared Orleans dependency.
- [Attestation reference][p5] and [admission door][p6]: verification and trust-list parameters.
- [Transfer escrow][p7]: destination mobility-admission storage and comparison.
- [Identity records][p8]: `WorldIdentity.TryAdopt`.

## Citation check

A review lane from another family reopened a sample of this page's **[opened]**
web claims, weighted to those the options rest on (fencing, presumed abort,
blocking commit, outbox, the Orleans directory, the EVE duplication record,
conditional exchange, transparency logs and revocation freshness). Fetch dates
and the inspected revision are in the commit that added this section.

- **Checked:** 14: Chubby [s1], the fencing critique [s2], the Redlock reply
  [s3], R* [s6], Gray and Lamport [s7], the outbox pattern [s10], the original
  Orleans paper [s11], the current Orleans directory [s12], CCP's M2-XFE report
  [s17], the FFXIV travel guide [s24], Certificate Transparency [s28], Herlihy's
  atomic swaps [s32], OCSP [s35] and Let's Encrypt [s36].
- **Confirmed:** 14. Each quoted phrase is on the page in the stated sense
  (Certificate Transparency's in section 11.2; OCSP's in section 4.2.2.1).
- **Relabelled, corrected or refuted:** none. No option on this page needed a
  change.

[s1]: https://www.usenix.org/legacy/event/osdi06/tech/full_papers/burrows/burrows.pdf
[s2]: https://martin.kleppmann.com/2016/02/08/how-to-do-distributed-locking.html
[s3]: https://antirez.com/news/101
[s4]: https://zookeeper.apache.org/doc/current/recipes.html
[s5]: https://zookeeper.apache.org/doc/current/zookeeperProgrammers.html
[s6]: https://pages.cs.wisc.edu/~remzi/Classes/739/Papers/p378-mohan-2pc.pdf
[s7]: https://arxiv.org/pdf/cs/0408036
[s8]: https://pages.cs.wisc.edu/~remzi/Classes/739/Papers/Ske81.pdf
[s9]: https://www.cs.cornell.edu/andru/cs711/2002fa/reading/sagas.pdf
[s10]: https://microservices.io/patterns/data/transactional-outbox.html
[s11]: https://www.microsoft.com/en-us/research/wp-content/uploads/2016/02/Orleans-MSR-TR-2014-41.pdf
[s12]: https://learn.microsoft.com/en-us/dotnet/orleans/implementation/grain-directory
[s13]: https://learn.microsoft.com/en-us/dotnet/orleans/grains/grain-persistence/
[s14]: https://doc.akka.io/libraries/akka-core/current/typed/cluster-sharding.html
[s15]: https://doc.akka.io/libraries/akka-core/current/split-brain-resolver.html
[s16]: https://www.eveonline.com/news/view/fleet-fight-notification-tool
[s17]: https://www.eveonline.com/news/view/the-second-timer-in-m2-xfe
[s18]: https://forums.eveonline.com/t/20260406-tranquility-server-node-death-atioth/509468
[s19]: https://worldofwarcraft.blizzard.com/en-us/news/5393667/cross-realm-zones-coming-to-beta
[s20]: https://us.shop.battle.net/en-us/product/world-of-warcraft-service-character-transfer
[s21]: https://news.blizzard.com/en-us/article/24115313/get-the-band-together-for-warbands
[s22]: https://us.forums.blizzard.com/en/wow/t/character-transfer-stuck-and-unable-to-log-in-and-clear-mail/850515
[s23]: https://eu.forums.blizzard.com/en/wow/t/answered-a-character-with-that-name-already-exists-unable-to-login-after-a-failed-trade-in-a-dungeon/524438/24
[s24]: https://na.finalfantasyxiv.com/lodestone/playguide/contentsguide/datacentertravel/
[s25]: https://na.finalfantasyxiv.com/lodestone/news/detail/52c97652e6364b4e6e1733a5e5c36eb01acf3665
[s26]: https://pdos.csail.mit.edu/6.5840/papers/castro-practicalbft.pdf
[s27]: https://arxiv.org/pdf/1110.4854
[s28]: https://www.rfc-editor.org/rfc/rfc9162.html
[s29]: https://signal.org/blog/automatic-key-verification/
[s30]: https://lightning.network/lightning-network-paper.pdf
[s31]: https://github.com/lightning/bolts/blob/master/03-transactions.md
[s32]: https://arxiv.org/pdf/1801.09515
[s33]: https://www.avivz.net/pubs/2020/flood_loot.pdf
[s34]: https://www.rfc-editor.org/rfc/rfc5280.html
[s35]: https://www.rfc-editor.org/rfc/rfc6960.html
[s36]: https://letsencrypt.org/2026/01/15/6day-and-ip-general-availability
[p1]: ../../../src/Puck.World.Server/WorldAuthorityBlobStore.cs
[p2]: ../../../src/Puck.World.Silo/WorldSiloHost.Crossings.cs
[p3]: ../../../src/Puck.World.Schema/WorldSessionResolver.cs
[p4]: ../../../src/Puck.World.Silo/Puck.World.Silo.csproj
[p5]: ../../reference/attestation.md
[p6]: ../../../src/Puck.World.Schema/WorldAdmissionDoor.cs
[p7]: ../../../src/Puck.World.Server/WorldTransferEscrow.cs
[p8]: ../../../src/Puck.World.Schema/WorldIdentity.Records.cs
[p9]: ../../../src/Puck.World.Server/IWorldAuthorityStore.cs
