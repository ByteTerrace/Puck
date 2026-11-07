# MMO foundations: synthesis

The synthesis memo of [the MMO foundations round](../mmo-foundations.md). It
weighs the five research records and [the attack catalogue](attacks.md) into
the decisions the owner will rule on. Nothing here is decided. Each decision
names its options, the evidence for each, what each closes and leaves open,
which option the evidence favours, the strongest case against it, and the
plain question the owner must answer. A second attack pass and two adversarial
reviews read this memo before the owner does.

Labels: **[record]** means the claim is taken from a named record's section;
**[opened]** means this memo reopened the code or document at this tree and
the words are pasted from it, with the `file:line` the tool printed (a lead at
this revision, as every plan's is); **[inference]** means this memo's reasoning.
Where records disagreed, lane E's code reading
([Puck as it is](puck-today.md)) won on what exists today, and
[the weighing](#how-the-records-were-weighed) says where that mattered.

## Summary

The model names three answers for anything a player would fight over: the
issuer keeps the record and escrow moves it; a claim travels signed with its
provenance; or an authority neither party controls decides. The code builds the
first for companions on a silo and none of the three for any other value. The
records agree on the shape of the fix and disagree only on sequence and on
three product calls. The favoured composition is: scarce value (currency,
tradeable items, stakes) lives as issuer-kept records behind one transactional
spend door at an authority the player does not host; progress another world
gates on travels as a signed claim from the world that granted it; cosmetics
and personal saves stay the player's own unsigned data; a character's identity
document is the player's, holds references and receipts to those records, and
its body moves under the companion protocol's title and custody once H14 lands.
A visited world writes a visitor only in its own namespace and never a
home-scoped value class, and what it writes goes home as it is earned, not on
return. Seams are hidden for shards and layers behind an unobservability gate
and visible where the fiction is travel, with no new mechanism needed for a
frameless portal. A table's authority is never a seated party. Contested places
run on an authority neither combatant controls, and the seam contact field is
clamped now, as a defect. The order of work moves the disclosure view, the
namespace door and write-through ahead of durable images, and puts a settlement
door and an identity abstraction ahead of any federation beyond loopback.

The owner's calls are [collected at the end](#7-owner-calls). The largest are:
who may be a title and settlement issuer; what the casino fantasy is allowed to
be; whether a friend's machine may be a destination for a stranger's body; and
whether raw PvP between two player hosts may ever run without a neutral witness.

## How the records were weighed

Lane E read the code; the other four read the world outside. Where they
disagreed, the code reading won on what exists, and these disagreements changed
the memo:

1. **Home adoption is colocated only.** The attack catalogue's A2 and A3 say a
   visited world's writes on a traveller are adopted at home. Lane E showed the
   adoption asks `HomeIdentity`, which answers only for a non-peer arrival, and
   every reservation decoded from the federation wire is a peer admission
   ([puck-today, section 2](puck-today.md#2-write-back-and-the-travelling-projection)).
   Reopened **[opened]** (`src/Puck.World.Server/WorldTransferEscrow.cs:1431-1432`):

   ```csharp
   private WorldIdentity? HomeIdentity(WorldCrossingArrival arrival, int index, string id) => ((
       !arrival.Request.PeerAdmission &&
   ```

   and (`src/Puck.World.Server/WorldFederationCodec.cs:1363`):

   ```csharp
           PeerAdmission: true,
   ```

   So A2 is live only between worlds in one process (the debug world's boot
   row and the garden it spawns), and A3's "home adopts on return" is false
   across machines: a federated destination's writes never reach the identity
   file, returned or not. This turns remote write-back from a hole into a fork:
   the mechanism is unbuilt, and the memo decides what to build, not what to
   patch ([decision 4](#decision-4-value-across-trust-boundaries)). The
   architecture page already says so **[opened]**
   (`docs/architecture/worlds.md:804-806`): "Remote round trips do not adopt
   ... Remote home adoption remains deferred."
2. **`WorldRecipientView` is a plan, not code.** Lane C's options I1 and C1
   lean on it; lane E and lane C's own citation check found it absent from
   `src/` ([seams, citation check](seams.md#7-citation-check)). Reopened: a
   search for `WorldRecipientView|WorldDeliveryCursor` over `src/` finds no
   file **[opened]**. The order of work therefore puts the disclosure plan's
   step 1 before interest management and before any claim that a silo keeps a
   visitor's hidden state from a host ([decision 5](#decision-5-seams),
   [the order](#6-the-order-of-work)).
3. **A13 is refuted for today's hosts.** Lane B asked for a confirmation; lane
   E traced it: a continuation's generation id is narration only, recovery
   binds by authority identity, and no host both restores rows and spawns
   resolver instances ([puck-today, premise verdicts](puck-today.md#premise-verdicts)).
   The memo carries A13 as a design constraint on durable images (a qualified
   generation identity when a desktop both restores and spawns), not as a
   decision.
4. **A12's bake question is moot until H10.** No avatar claim exists
   ([puck-today, premise verdicts](puck-today.md#premise-verdicts)); the
   decision records the ordering rule (measure before bake) for when it does.
5. **Lane C's external evidence against reactive splitting fell away.** Bossa
   declined to blame SpatialOS and Dual Universe's cause is unconfirmed
   ([seams, citation check](seams.md#7-citation-check)). The case against
   reactive topology (S3) rests on Puck's own rule and CCP's M2-XFE report.
6. **The Azure path has no binding-based identity in use.** A9 as written
   (one vouching root, 30-day bindings) describes an unwired path; API users
   carry a bearer token the silo validates and re-validates each second, and
   world authorities sign from a key file
   ([puck-today, section 3](puck-today.md#3-attestation)). This is why the memo
   states identity and revocation as abstractions first
   ([section 3](#3-abstractions-not-providers)): nothing is wired yet, so the
   question is what to wire, asked of no provider.
7. **Lane D relabelled two web sources** to summary after a failed fetch; no
   option changed ([economy, citation check](economy.md#citation-check)).

Two conventions this memo keeps from the records: a timeout is never proof of
abort ([custody, commit](custody.md#commit-presumed-outcomes-compensation-and-outbox)),
and replay proves execution from given inputs, never that the inputs were
honest or complete ([economy, fairness by replay](economy.md#the-hypothesis-fairness-by-replay);
[attacks, 1.3](attacks.md#13-conventions-nothing-enforces)).

## 1. The decisions

### Decision 1: the engine's purpose

**What must be decided.** How the owner's purpose is recorded, and how every
later design round weighs it. It matters because the round found that the
model's intent and the code differ in ways that only a stated purpose can
rank: an engine for friends' machines and an engine for a WoW-scale economy
make opposite calls on who may issue value.

**Options.**

| Option | Evidence | Cost | Fails at |
|---|---|---|---|
| 1a. Record the owner's words in AGENTS.md as a ruling, with one weighing rule: a design round asks what each established answer becomes when every zone, character and server is a world, and judges it at MMO scale | The round's own frame ([the round](../mmo-foundations.md#the-frame)) **[record]** | One paragraph | Nothing; it is prose |
| 1b. Record the purpose in a decision page only | Decision pages hold settled design | Same | A ruling in a decision page does not reach every agent's rules file |

**Favoured.** 1a. **Strongest case against:** a rules file that grows a purpose
statement invites every lane to argue from it; the weighing rule bounds that by
naming what a round asks, not what it concludes.

**Owner call 1.** Is this the purpose, in these words: Puck exists to make it
possible to build an MMO at World of Warcraft's scale that redefines the genre;
the emulators, the screen-within-a-screen, the tables and portals are parts of
that MMO; and every design round weighs its question at that scale?

### Decision 2: player state across author edits

**What must be decided.** What a character keeps when an author edits an
identity template, a zone or a shared world; where tuning lives; how an author
breaks continuity on purpose; and the two durable-image owner calls
([hub-and-saves, owner calls](../hub-and-saves.md#owner-calls)). At MMO scale a
template edit reaches every player at once, so the wrong answer is a
whole-population defect (Old School RuneScape's item-definition merge,
[persistence, section 1](persistence.md#1-storing-characters)) **[record]**.

**What exists.** An identity copies its template at seed **[opened]**
(`src/Puck.World.Server/WorldOwnedWorlds.cs:691`):

```csharp
    private static WorldDefinition Seed(WorldDefinition template, WorldIdentitySeed seed) => template with {
```

and an identity the catalog can no longer read is set aside whole, never
row by row ([persistence, Puck as it stands](persistence.md#options-for-puck))
**[record]**. No studied game does either.

**Options** ([persistence, question 2](persistence.md#round-question-2-player-state-across-author-edits)).

| Option | Evidence | Cost | Fails at | Attacks |
|---|---|---|---|---|
| 2a. Copy at seed, set aside whole (today) | None for it; every studied game keeps tuning in templates **[record]** | None to build | The first retune: old values forever, or the whole character lost | None closed |
| 2b. References plus per-row admission: the identity holds template ids and per-instance values; tuning is read from the template at the world that uses it; an undeclared row drops by name through the image ladder's tier 2 door | EVE's `typeID` against `itemID`, WoW's item id plus bonus list; the arena's door already partitioned the debug world's rows 15 kept and 454 dropped **[record]** | The template becomes load-bearing; ids never reused (EVE's junkyard) | Values whose meaning changes rather than disappears (a squish) | Substrate only |
| 2c. 2b plus an authored `.puck` conversion, keyed by the template version the document was written under, with refunds and a residue row | Every deliberate break studied is such a function (EVE's learning skills, WoW's level table, FFXIV's reallocation, GW2's tokens) **[record]**; "Puck code over engine code" puts it in the author's program | A version on every stored document; a conversion surface; a totality check | Conversions needing judgement (GW2 contacted over-cap holders by hand); Puck has no support desk | Narrows A1 only if it clamps to template bounds on the way in **[inference]** |
| 2d. Epoch realms: the clean break is a new world beside the permanent one, merged back by a conversion at its end | Path of Exile's leagues, Old School's separate launch **[record]** | The merge is a 2c program between two worlds | Nothing it claims; it is 2c applied across worlds | None |

**Favoured.** 2b now, 2c as the deliberate break, 2d as the only literal reset.
A conversion is a pure function of (stored document, template version,
program), so it replays bit-identically **[inference]**.

**The two durable-image calls.** The evidence favours carrying saved state over
an edit (every studied game carries by default) and adds the third path the
memo lacks: a row whose meaning changed is converted, not carried or dropped
(FFXIV's EXP reset is the cost of the missing path) **[record]**. For custody an
inexact image holds, the evidence favours starting the row and narrating the
loss, with one addition no studied service skipped: freeze the doors that move
the affected value until it is resolved (Diablo III's auction house, New
World's transfers), and review the frozen door itself, because New World's
freeze opened a new duplication path ([persistence, question 2](persistence.md#round-question-2-player-state-across-author-edits))
**[record]**.

**Strongest case against 2b and 2c.** They make the template a shared point of
failure and add a language surface before the state-and-language plan has one.
The answer is that the failure exists today in a worse form (a whole character
set aside), and that the conversion surface is the author's program, which
AGENTS.md already asks for.

**Owner calls 2 to 4.** Does saved state carry over an edit that keeps a row's
declaration, with an authored conversion as the only path for a row whose
meaning changed? When an inexact image holds titles abroad or transfers in
doubt, does the world start with the doors that move the affected value frozen,
or refuse to start? How long is set-aside data (a dropped row, a refused image,
a quarantined identity) kept, and who may restore it?

### Decision 3: custody of owned things

**What must be decided.** Which of the companion protocol's machinery
generalises to characters, items, stakes and machine saves; who is custodian
when the title authority is unreachable; how a crashed place or an inexact
image resolves custody; and how epochs stay monotonic across restarts. It
matters because a duplicate of anything scarce is the one MMO failure that
cannot be repaired locally (CCP's M2-XFE duplicated ships despite a reinforced
node, [custody, MMO transfer](custody.md#mmo-transfer-and-failure-evidence))
**[record]**.

**What exists.** Title, hops, epochs, leases, tombstones and the custody
reducer for companions, steps 1 to 5 of the protocol; steps 6 and 7 have
record kinds the codec decodes and nothing mints **[opened]**
(`src/Puck.World.Server/WorldCrossingRecord.cs:127`, `:136`):

```csharp
    public sealed record Recall(WorldEntityAddress Incarnation, ulong Epoch) : WorldCrossingRecord;
```

```csharp
    public sealed record Release(WorldEntityAddress Incarnation) : WorldCrossingRecord;
```

The durable crossing log exists on the silo alone **[opened]**
(`src/Puck.World.Silo/WorldSiloHost.cs:1474`):

```csharp
                    server.InstallCrossingLog(log: new RowCrossingLog(
```

and a desktop writes no durable image: `DurableImages` is declared and never
assigned (a search for `DurableImages =` over `src/` finds nothing) **[opened]**
(`src/Puck.World.Server/WorldInstanceHost.Stop.cs:33`):

```csharp
    public WorldDurableImageWriter? DurableImages { get; set; }
```

The destination's consumed-epoch fence is real and per destination
**[opened]** (`src/Puck.World.Server/WorldTransferEscrow.cs:2109-2111`):

```csharp
                (knownEpoch >= mobility.Epoch)
            ) {
                return $"traveler {(index + 1)} mobility epoch {mobility.Epoch} is stale; destination has consumed through {knownEpoch}";
```

**Lane B's refinement of the hypothesis** ([custody, question 3](custody.md#round-question-3-custody-and-the-hypothesis))
**[record]**: title belongs to the authority whose economic decisions
recipients accept, and the residence called home need not be that authority; a
crossing moves the accepted use grant; images are evidence for reconstruction,
never a grant; epochs work only with a durable allocator and a comparison at
every accepted effect; presumed abort applies only to a protocol-defined
recovered absence; recall is a new fenced decision with a declared state-loss
policy, never a rollback of external obligations.

**Options** ([custody, question 3](custody.md#round-question-3-custody-and-the-hypothesis)).

| Option | Invariant and enforcement | Cost and failure boundary | Attacks |
|---|---|---|---|
| 3a. Trusted home per title, durable custodians (the companion protocol generalised as it stands) | Home serialises grants; each custodian consumes epochs before accepting effects | Home round trips; crash recovery only with retained logs; a hostile home is outside the guarantee; a pending destination can block forever | A8 with durable hosts; A13 with a qualified target identity; leaves A7 (home is the attacker) and A15 |
| 3b. A settlement world outside the player's control, per trust community | One transactional door consumes the current right and creates its successor; every scarce effect uses it | A service's availability, storage and trust; settlement stays small while zones stay distributed; cannot erase a puppet on a hostile renderer | Closes economic A7 and A8 when every reward and spend uses the door; bounds A3 after acknowledged settlement; does not alone close A2 or A9 |
| 3c. Bounded rights delegated to a world: settlement removes a budget from circulation before delegation; redemption is unique | Locked value and delayed close; a dishonest delegate distributes its budget unfairly | Bounds rather than removes A1, A3 and A7 losses; A15 becomes a settlement deadline |
| 3d. Freely forkable personal worlds | Receivers treat copies as presentation or untrusted claims | Broad offline freedom; no shared scarcity | Accepts A1 and A7 locally and contains them by refusing conversion |

**Favoured.** A composition, by class of value ([section 2](#2-classes-of-value)):
3b for scarce value; 3a for bodies (characters at H14, companions as built),
with the title of a scarce companion moving to 3b; 3c for offline and table
play against a prefunded budget; 3d for cosmetics and personal saves. An
unreachable title authority has no automatic substitute: a committed custodian
keeps its rights within their scope, a pending move stays pending, and a new
exclusive grant waits ([custody, question 3](custody.md#round-question-3-custody-and-the-hypothesis))
**[record]**. A crashed place or an inexact image never asserts custody from
its image; it asks the title authority for current rights before enabling
economic actions, and the world starts with the affected value doors frozen
([decision 2](#decision-2-player-state-across-author-edits)). Epochs stay
monotonic through one durable allocator per title, which the authority store
already models for activations **[record]**
(`WorldAuthorityBlobStore.AcquireActivationAsync`,
[custody, leases](custody.md#leases-fencing-and-restart-continuity)), and
through a durable image and crossing log on any desktop that is a destination
(H7, [decision 10](#decision-10-what-a-player-host-may-be)).

**The invariant and what enforces it.** Within a settlement domain, every
scarce right is available, held for one unresolved transaction, transferred,
consumed or destroyed, and no two accepted effects consume the same right and
version ([custody, debug world](custody.md#how-the-debug-world-would-prove-it))
**[record]**. The one enforcement point is the settlement door's commit, which
authorises, compares the fence and deduplicates in one durable write. Nothing
enforces this today: the companion reducer fences bodies, not value, and no
spend door exists. The guarantee stops at authorities the domain accepts and at
effects that bypass the door.

**Strongest case against 3b.** It reintroduces a centre in an engine whose
plain-terms page begins "Puck has no such centre" **[opened]**
(`docs/architecture/worlds.md:17`). The answer is that a settlement world is a
world: chosen per trust community, replaceable, and small, while zones,
characters and tables stay where their authors put them. What it does not
answer is the owner's taste: a friends-only economy may prefer 3d everywhere.

**Owner calls 5 to 7.** Who may be a title and settlement issuer: the studio's
silo only, any operator a root vouches for, or any community that names one?
May a pending title whose destination is gone for good wait forever (the
protocol's current rule), or does the engine offer the player a recall that
accepts a possible second body at that destination? Does a home restarted
without its checkpoint stay a new authority whose companions abroad wait
forever, or may the player recall them?

### Decision 4: value across trust boundaries

**What must be decided.** Which classes an authoritative world keeps records
of, which travel as claims, where escrow and attestation must hold, and how a
visited world may write a visitor. It matters because today every fact a world
gates on is forgeable by its owner (A1) and writable by any colocated world
(A2), and a visitor's progress abroad dies with the host (A3)
([attacks, critical](attacks.md#a1-the-self-owned-character)) **[record]**.

**What exists.** `TryAdopt` writes every carried fact and record field through
the identity's own write door and nothing else **[opened]**
(`src/Puck.World.Schema/WorldIdentity.Records.cs:129-130`):

```csharp
        foreach (var cell in (carried.Facts?.Cells ?? [])) {
            if (!TrySetFact(key: cell.Key, value: cell.Value.AsInt, changed: out _, reason: out var factReason) && (refused.Length == 0)) {
```

`Decide` is the other door, deny by default on a grant naming the source
document, `Mutate` and the slot, with a required write mask **[opened]**
(`src/Puck.World.Server/WorldOwnedWorlds.cs:307-312`):

```csharp
        var principal = Grantee.Document(id: submission.SourceDocumentId);
        var subject = GrantSubject.State(name: submission.Slot);
        var grant = document.Grants.FirstOrDefault(predicate: candidate =>
            ((candidate.Grantee == principal) &&
            (candidate.Capability == WorldCapability.Mutate) &&
            (candidate.Subject == subject)));
```

The model's own answer is in prose **[opened]** (`docs/architecture/worlds.md:86-88`):
"Where something must stay unique or tradeable, such as currency or a stake at
a card table, the world that issued it keeps the record itself and escrow and
custody move it, so a copy gains nothing."

**Options for what a visited world writes** ([persistence, question 8](persistence.md#attack-question-8-what-a-visited-world-gets-to-write);
[custody, question 2](custody.md#attack-question-2-when-progress-becomes-durable)).

| Option | Evidence | Closes | Leaves open |
|---|---|---|---|
| 4a. Adopt everything (today, colocated) | The one partition no studied game uses **[record]** | Nothing | A1, A2, A3 |
| 4b. Namespaced write-back: a visited world writes only facts and records in its own package namespace; adoption refuses others by name (H9 extended from `Decide` to the crossing) | FFXIV's deep-dungeon leaderboard and company credits are not written while travelling **[record]** | A2's griefing of other namespaces | Bob writing Bob's namespace with anything; A1; A3 |
| 4c. Home-scoped value classes: value-bearing classes are readable abroad and never writable; a visited world issues a claim or a pending grant home applies by its own rule | FFXIV's travel restrictions and GW2's home-only World versus World, as engine policy over authored classes **[record]** | A2 for value | A1; progress classes |
| 4d. Signed write-back: the visited world signs what it writes; home stores the claim with its issuer; a reader applies its policy per issuer | The plain-terms page's worked example; no studied game needs it because one operator issues everything **[record]** | A1 for what others issue; A2's laundering becomes visible as Bob's claim | A1 for what only the player issues; A9 |
| 4e. Issuer-kept records: unique value stays at its issuer; the identity holds a reference | Every studied game's model **[record]** | A1 and A2 for that class | Offline spend; issuer inflation is a trust decision |
| 4f. Write-through as earned: each award is submitted home when granted, as a pending claim home decides at its tick boundary | RS3's telemetry at logout and GW2's queued messages made restore possible **[record]**; lane B favours durable write-through for anything the UI calls earned **[record]** | A3's crash case for progress | A3's dishonest case (the host withholds), unless the visitor holds the signed award as soon as it is issued (4d) |

**Favoured.** 4b, 4c and 4f together now, in process, through `Decide`: a
foreign write is a submission in its own namespace, a home-scoped class refuses
every foreign write, and an award goes home as it is earned with a unique award
id so a retry returns the prior outcome. 4e for scarce classes behind the
settlement door ([decision 3](#decision-3-custody-of-owned-things)). 4d is the
remote path: when federation adoption is built, it is built as signed
submissions and never as the unsigned adoption that exists today for colocated
seats. A displayed award is pending until the accepting authority acknowledges
it ([custody, question 2](custody.md#attack-question-2-when-progress-becomes-durable))
**[record]**.

**Where escrow and attestation must hold.** Escrow at every move of a scarce
right between authorities (a stake, a trade, a crossing with an item), with
one terminal decision per operation id and no refund path that does not read
that decision (the WoW cancelled-auction payout,
[economy, duplication history](economy.md#duplication-history)) **[record]**.
Attestation at every claim a reader gates on: issuer, subject, predicate, a
sequence with a replay horizon, and the receiver's policy over which issuer may
state which predicate ([custody, Byzantine authorities](custody.md#byzantine-authorities-receipts-and-transparency))
**[record]**. A signed claim is never a spendable balance
(`docs/reference/attestation.md` rules it out, [attacks, A1](attacks.md#a1-the-self-owned-character))
**[record]**.

**The known limits at the edge.** A copied claim gains nothing when the right
it names is issuer-kept, and gains a copy of a non-exclusive statement
otherwise, which is harmless by construction. A dishonest host cannot mint
value it does not issue, and can lie within the predicates it is trusted for;
trusting an issuer must include what it may mint, not only whether its key is
authentic ([economy, lessons](economy.md#duplication-history)) **[record]**. A
host that sees hidden state is [decision 6](#decision-6-the-tables). An owner
restoring an old copy restores a display; the settlement door still says
spent **[inference]**.

**Strongest case against.** Three mechanisms (namespace, home scope,
signatures) where one might serve. They answer different questions: a
namespace is policy over who may write what; home scope is policy over what
may be written abroad at all; a signature is provenance a third reader can
check. Dropping any one reopens an attack the other two do not reach.

**Owner call 8.** Is the classes-of-value table in [section 2](#2-classes-of-value)
the one the standard package authors, and in particular: are levels and
experience a signed claim from the world that granted them, or the player's
own number?

### Decision 5: seams

**What must be decided.** Which boundaries are visible portals and which are
hidden; how a portal runs without a frame; how interest management maps onto
disclosure tiers. It matters because every studied game's hidden seams failed
in two ways, a seam moving under a live player and a seam hopped to farm or
escape, and Puck's seam contact field is today a griefing lever
([seams, topic 1](seams.md#topic-1-world-of-warcraft-sharding-crz-layering-and-phasing);
[attacks, A10](attacks.md#a10-seam-shoving)) **[record]**.

**What exists.** Adjacency is topology with no furniture **[opened]**
(`src/Puck.World.Schema/WorldAdjacencies.cs:144-146`):

```csharp
/// One intentional adjacency between two authority-owned regions. It is topology, not visible furniture: no
/// placement, screen, or portal is required. The compiler derives the overlap needed for observation, interaction,
/// grounding, and handoff from the two delivered documents.
```

No `.puck` file under `worlds/` authors an `adjacencies` row (a search finds
none) **[opened]**, so every seam check must author one first. Body disclosure
is a two-scan filter at the hub with three modes and a flat cadence
**[opened]** (`src/Puck.World.Schema/WorldObserverDisclosure.cs:8-18`,
`src/Puck.World.Server/WorldOutputHub.cs:372`):

```csharp
    public static WorldSnapshot Redact(in WorldSinkDisclosure disclosure, in WorldSnapshot snapshot, ref EntitySnapshot[] scratch) {
```

**Options** ([seams, options](seams.md#3-options-for-puck)).

| Option | Evidence | Cost | Fails at | Attacks |
|---|---|---|---|---|
| S1. Fiction-aligned split: visible portals for travel and scope changes; `WorldAdjacency` for continuous shards; population layers moved only under an unobservability predicate (no combat, no channel in progress, in a transition volume or by a prompt) with an increasing hop cooldown | Blizzard's own layering deferred transfers during combat and before loot, added a longer delay after PvP, and hotfixed an increasing hop cooldown **[record]** | Authoring transition corridors or the predicate; a last-hop stamp on the mobility record | Cannot hide a layer change in an open field while the player watches a node | Closes layer-hop farming beside A7; A10 goes to C3 |
| S2. Framed transitions everywhere; no silent layers | FFXIV's numbered instances (summary only) | Gives up re-layering an entered field | A flash mob in an entered field | Removes mid-cast vanishes by construction |
| S3. Reactive spatial splitting at runtime | None that held; contradicts "Destinations are boot-authored document data" and "do not run an authority boundary through a place designed to be contested" **[opened]** (`docs/architecture/worlds.md:1413-1414`) | Reactive topology | The crowd at a junction (M2-XFE) | None |
| F1. A frameless portal is a creation with an aperture face and no frame rings, placed with a `WorldPlacementFace` portal; a standard `threshold` template | The glass fills the aperture edge to edge with no bezel; the door and the screen are independent facts about one face **[record]** | A template in `worlds/standard/`; no C# | Nothing it claims | None |
| F2. An adjacency that falls back to a frameless `window` across scopes it cannot project directly | Adjacency requires a global persisted destination **[opened]** (`src/Puck.World.Schema/WorldAdjacencies.cs:149`) | Teaching adjacency to host a window | Mixed lighting and sky | None |
| I1. Fold spatial grid, hysteresis, an explicit unembodied-observer policy and distance-tiered cadence into the disclosure view and cursor | The disclosure plan watches this seam as the view's natural second occupant **[record]**; Unreal's actor priority gives each actor a fair share under saturation **[record]** | Replaces `Redact`; waits on disclosure step 1 | Nothing it claims | Closes A16; narrows A12 |
| I2. Keep `Redact`, add a grid and an `Unembodied` mode | Smaller diff | Stateless across ticks | The fog-of-war skirmish; hysteresis; priority | A16 only |

**Favoured.** S1 with F1 now (no mechanism is new; a template and a predicate
are authored), I1 after disclosure step 1, F2 only if an author asks for it.
Interest management maps onto the tiers as lane C states: `Frames` is the
camera; `Presentation` is the view's rows plus a body set under radius,
hysteresis and cadence tiers; `Replica` is the full disclosed document for
trusted shadows ([seams, question 5c](seams.md#question-5c-how-interest-management-maps-onto-disclosure-tiers))
**[record]**. The deployed debug world authors `Replica` for `*` **[opened]**
(`worlds/debug/debug.puck:82-83`), deliberately, so a returning traveller's
windows observe it whole; that row is the A14 footgun and belongs on the
checklist of any world that admits strangers.

**Strongest case against S1.** The unobservability predicate is authored
complexity that S2 avoids by never moving a layer in place. The answer is that
Blizzard shipped S2's failure first (mid-cast vanishes) and then built S1's
predicate as the fix; S2 remains the right choice for a world that chooses it.

**Owner call 9.** When a player joins a friend on another population layer,
does the move wait until they leave combat and enter a transition volume, or
happen in place after a visible countdown, with an increasing cooldown on later
hops?

### Decision 6: the tables

**What must be decided.** Escrow for stakes; provable fairness; hidden
information; atomic wagers under rule faults; and the regulatory line. It
matters because the casino fantasy is the owner's stated reason for the card
and board systems, and today the host sees every hand and chooses the dice
([attacks, A4 and A5](attacks.md#a4-the-host-sees-every-hand)) **[record]**.

**What exists.** A draw's seed folds the document seed, the instance identity
and the site descriptor, and no participant contributes entropy **[opened]**
(`src/Puck.State.Generators/GeneratorEngine.Streams.cs:5-7`):

```csharp
/// <summary>A draw site's seed-ladder fold and stream id: everything a draw of the site seeks from besides its cursor.
/// Both depend only on the document seed, the running instance's identity and the site descriptor, so a host computes
/// the pair once per site and every draw it makes afterwards costs the same whatever those strings hold.</summary>
```

The authority reads its own arena whole, by design
([puck-today, section 7](puck-today.md#7-disclosure)) **[record]**.

**Options for who runs the table** ([economy, question 6](economy.md#question-6-and-attack-question-5-who-runs-the-table)).

| Option | Guarantee | Cost | Attacks |
|---|---|---|---|
| 6a. A participant's authority with plain replay | Deterministic reproduction | None | Leaves A4 and A5 |
| 6b. An independent dealer (a table world no seat hosts) plus accepted escrow | Disclosure fits a trusted authority; operator-mediated custody has live-game precedent | Hosting; dealer compromise; outage recovery | Closes seated-host A4 when no seat controls the dealer; A5 still needs entropy binding and witnessed inputs |
| 6c. A participant relay with verifiable encrypted dealing and separate escrow | Mental-poker papers and one implementation: shuffle proofs about 50 ms, verification under 1 ms, author-reported **[record]** | Proof work, authenticated broadcast, abort handling, key safety | Closes host-knowledge A4 and shuffle-substitution A5 under stated coalition assumptions; withholding and collusion remain |
| 6d. A threshold dealer committee plus escrow | Availability against coalition size | Membership, key refresh, operators | Moves A4 and A5 to a threshold |

**Favoured.** 6b first, with the rule stated as the engine's: a table's
authority is never a seated party, and a non-seated authority controlled by a
seated player's account is seated. Stakes are reservations at the spend
authority's door ([decision 3](#decision-3-custody-of-owned-things)), frozen
with roster, buy-ins, rules identity and recovery policy before a hand starts,
with one terminal decision per hand id ([economy, stakes](economy.md#the-hypothesis-stakes-as-escrowed-custody))
**[record]**. Public draws bind per-participant commitments before any is
opened, taped as inputs, with an abort policy that gives no free reroll to the
last revealer ([economy, commit-reveal](economy.md#randomness-and-provable-fairness))
**[record]**. 6c is a bounded prototype for the case the owner may want
(secrecy from a participant host), measured as lane D's packet asks, never
adopted from a library as the specification.

**Fairness by replay** needs the full list lane D gives: rules and terms
pinned, every accepted action in order, commitments witnessed before their
dependent actions, agreement on one history, and the settlement receipt with a
unique hand id; every seat keeps the public transcript and its own receipts
apart from the host ([economy, fairness by replay](economy.md#the-hypothesis-fairness-by-replay))
**[record]**. A redacted tape is not full replay verification.

**Atomic wagers.** Two debits and a pot update are one operation at the spend
door or none; a rule fault between them leaves no partial effect, and recovery
replays the same operation id ([economy, debug world](economy.md#how-the-debug-world-would-prove-it))
**[record]**.

**The regulatory line.** Lane D's product forks
([economy, product forks](economy.md#product-forks-to-put-before-the-owner))
**[record]** are the owner's to pick before an escrow schema picks one quietly:
table-local score; earned persistent chips; purchased chips or paid random
rewards; tradeable items as stakes; real-money deposit and cash-out. Belgium,
the Netherlands, the UK, China and Washington each draw the line in a
different place, and cryptographic fairness moves no product across it.

**Strongest case against 6b.** It trusts a dealer, which is the trust the
casino fantasy exists to question. The answer is that 6c removes the dealer's
knowledge and not the seated players' collusion, and that both options need the
same escrow and the same transcript policy; 6b proves those first.

**Owner calls 10 to 12.** Which product row is the casino fantasy: table-local
score, earned chips, purchased chips, tradeable stakes, or real money, and for
which territories and ages? Is an independent dealer acceptable, or must the
table keep its secrets from every host including a non-seated one? Must mucked
hands stay secret after the hand, which forbids the simplest transcript?

### Decision 7: the order of work

Stated in [section 6](#6-the-order-of-work).

### Decision 8: where a contested place runs

The round's question 5 asks about seams; the attack catalogue's question 4
asks where a duel between two player hosts runs, and the records show the
round would otherwise leave it unowned.

**What must be decided.** Who decides a hit between bodies under two
authorities, what each can lie about, and what invariant 1 (only a body's
authority writes it) implies. It matters because the owner names direct
player-to-player interaction as raw PvP, and the one cross-authority decision
in the code is an authority-pair hash for physical contact **[opened]**
(`WorldAdjacencyContactField.ResolveCore`):

```csharp
            var responds = (localIsSolid && WorldCrossAuthoritySettlement.LocalResponds(
                interaction: "physical-contact",
                localAuthority: localAddress.Authority,
                remoteAuthority: neighbour.Authority
            ));
```

over poses the neighbour delivered as presentation floats, which the code names
as outside replay determinism **[opened]** (`src/Puck.World.Server/WorldAdjacencyContactField.cs:25-27`):

```csharp
/// <para><b>Replay boundary.</b> Neighbour poses presently arrive as presentation-float snapshot fields and are
/// converted to fixed point below at delivery timing. Ground and dynamic-contact correction are therefore not yet
/// replay-deterministic across network schedules.
```

Lane E found the missing per-tick cap. The seam clamp now enforces a local
collider-reach budget and ownership-face containment over neighbour bodies and
geometry through `WorldPopulation.AdmitAdjacencyResponse`
([puck-today, premise verdicts](puck-today.md#premise-verdicts)); tick-addressed
neighbour records remain the [replay packet](../open-items.md#transfers-federation-and-server).

**Options** ([seams, options](seams.md#3-options-for-puck)).

| Option | Honest between two player hosts | Feel | Hidden state safe | Scale | Attacks |
|---|---|---|---|---|---|
| C1. Co-location onto an authority neither combatant controls (a silo or a vouched referee world), bound before the first swing | Yes | Client-server round trip | Yes, once disclosure step 1 lands | A single authority's budget | A3, A4, A5, A6, A8, A10 |
| C2. Mutual deterministic simulation: both peers step the encounter over signed per-tick inputs with rollback, compare state hashes, and a lazy referee replays the tape on a mismatch | Yes, with a witness for stalls and disputes | Best for 1 to 8 peers | Only by commit-reveal; both peers hold the arena | Stalls on the slowest of N | A3, A5, A6 |
| C3. Clamped, tick-addressed seam contact plus signed cross-seam proposals the target's authority applies under its own rules | Only between authorities that honour rules on their own bodies | One round trip per cross-seam effect | Yes | The border band | A10 and the replay boundary; nothing against a host that makes its body immortal |

**Favoured.** C3's clamp and taped neighbour records now, as a defect fix
(A10 is griefing and the replay boundary is a named hole). C1 as the rule for
any contested place, which is the architecture page's existing rule, and the
owner's "do not run an authority boundary through a place designed to be
contested" **[opened]** (`docs/architecture/worlds.md:1413-1414`). C2 as the
Puck-native prototype for duels between friends, which no float-based MMO can
run, with its honest limits: it needs a witness to attribute a stall, cannot
hold hidden state, and its threat model (lag switches, deliberate desync) is
inferred, not documented ([seams, citation check](seams.md#7-citation-check))
**[record]**.

**Strongest case against C1.** It makes every trustless fight depend on
reachable neutral capacity and adds a hop; two friends on a LAN cannot duel
without it. C2 is the answer for that case and nothing larger.

**Owner call 13.** May a player-hosted world ever host raw PvP without a
neutral witness, accepting that a disconnect then has no referee, or does
every fight with stakes or durable progress run on an authority neither
combatant controls?

### Decision 9: revocation below expiry

The round's questions do not ask it; A9 and attack question 6 do.

**What must be decided.** How a vouching root refuses a banned or stolen key
before its binding expires, at one door, as data. It matters because moderation
at MMO scale has no lever without it, and `Puck.Attestation` has none: a search
for `revok`, `revoc`, `denylist`, `blocklist` finds nothing
([puck-today, section 3](puck-today.md#3-attestation)) **[record]**. The
admission door's trust list sets no binding age and no replay horizon
**[opened]** (`src/Puck.World.Schema/WorldAdmissionDoor.cs:91-96`):

```csharp
        return new TrustList(
            defaultMaximumAge: defaultMaximumAge,
            defaultRootBindingMaximumAge: null,
            defaultSubjectBindingMaximumAge: null,
            entries: list,
            replayAcceptanceHorizon: null
```

**Options** ([custody, question 6](custody.md#attack-question-6-revocation-choices)).

| Option | Enforcement | Fails at |
|---|---|---|
| 9a. Short-lived bindings | The issuer renews only authorised accounts; the verifier bounds lifetime | A stolen key works until expiry; a renewal outage denies service |
| 9b. A signed refusal snapshot: the root publishes a scoped, versioned list; a host persists its newest and records the version with each accepted decision | Local checks; a deterministic policy input | A stale snapshot leaves a window or fails closed |
| 9c. Fresh online status, stapled | Narrow delay | Status availability; a cached good answer is not revocation |
| 9d. Hybrid: 9a plus 9b, with 9c for valuable actions | Separates observation from spends | More machinery; one policy for live sessions and outstanding escrow |

**Favoured.** 9a plus 9b, because attestation fetches nothing by rule
(`docs/reference/attestation.md`, [custody, revocation](custody.md#revocation-before-expiry))
**[record]**, so a list must arrive as data, and because the peer host already
re-verifies a live connection on every policy change and evicts within a
second ([puck-today, section 3](puck-today.md#3-attestation)) **[record]**,
which is the door a snapshot plugs into. Key, issuer and subject bans are
separate: rotating a compromised key must not evade an account ban, and a
rotation must not revoke historical ownership **[record]**. External status
becomes a recorded decision at a tick boundary so replay does not refetch
today's status to reinterpret yesterday's acceptance **[record]**.

**Strongest case against.** A snapshot is a bounded attack window by
construction. The owner names the window; nothing closes it under partition
while allowing unlimited offline high-value use.

**Owner call 14.** What delay between a ban and its effect is tolerable, and
does a world fail closed when its refusal snapshot is older than that?

### Decision 10: what a player host may be

Attack question 3's fork; the records answer it from three sides and the round
did not ask it.

**What must be decided.** Whether a desktop is a full authority with a durable
log and image, a host of worlds whose value is forkable, or never a destination
for another player's body. It matters because every player-hosted world is
today an undo button for its host and a trap for its visitors' session progress
(A3), and because "players hosting their own authoritative worlds is a goal"
([attacks, A3](attacks.md#a3-the-dishonest-or-vanishing-player-hosted-destination))
**[record]**.

**Options** ([custody, question 3](custody.md#attack-question-3-what-a-desktop-must-persist-and-what-it-proves)).

| Option | Closes | Leaves |
|---|---|---|
| 10a. A desktop destination writes a crossing log and durable images (H7), persisting allocated ids, authority incarnation, grants, arrivals, departures, terminal outcomes, consumed epochs, floors and unresolved obligations, acknowledging only after the durable write | A3's and A8's crash cases | A3's dishonest case: the host deletes its own log |
| 10b. A desktop hosts visitors' bodies, but scarce value and earned progress settle at issuers not on that desktop, as the visitor's own receipts | A3's dishonest case for value and acknowledged progress | Position and pending progress; the host can withhold service |
| 10c. A desktop never hosts another player's body; it renders only | A3 whole | The owner's goal of player hosting |

**Favoured.** 10a and 10b together: a friend's machine may host a world
strangers visit, it keeps a log and an image so a crash loses position and not
possessions, and nothing a stranger would fight over is decided there. Recovery
of a body is the custody protocol's recall (step 7, then H14). A lost local disk
reconciles from the surviving settlement authority; where no trusted record
survives, the rights are unresolved, never minted again from an image
**[record]**.

**Strongest case against.** 10b limits what a player-hosted world can be: it
cannot run its own economy. That is the owner's call, and 3d (forkable
personal worlds) is the honest name for a host that wants to.

**Owner call 15.** May a friend's machine be a destination for a stranger's
body at all, and if so, is it the rule that nothing scarce is decided there?

## 2. Classes of value

The table the attack catalogue asks for, each class mapped to one of the
model's three answers or to "unsigned, cosmetic only", with its reason. The
classification is by the consequence of copying or falsifying the value, not
by its schema name ([economy, question 4](economy.md#question-4-and-attack-question-1-classes-of-value);
[custody, question 4](custody.md#round-question-4-and-attack-question-1-classes-of-value))
**[record]**. It is a proposal for the standard package to author, never an
engine enum.

| Class | Answer | Reason | What it closes and leaves |
|---|---|---|---|
| Currency and stackable goods | Issuer-kept record moved by escrow | Amounts split and merge; one body per incarnation says nothing about supply; a signed balance is ruled out by the attestation reference | Closes A1 and A7 double-spend at that issuer; A8 needs durable consumed ids; issuer inflation is a trust decision |
| Unique tradeable items | Issuer-kept record (title at the issuer) moved by escrow and custody | A sale changes the owner, which companion custody never does; craft, split, destroy and restore must consume prior rights | Same as currency; A7 when crossing uniqueness is enforced |
| Table stakes | An authority neither party controls: a reservation at the spend door, one terminal decision per hand | A partial landing is wrong for an all-or-nothing wager; recall cannot refund a committed loss | Closes A3's refund-by-restart and A7's reuse; not A4 or A5 |
| Appearance | Unsigned, cosmetic only; an issuer-kept entitlement reference when scarce or sold | Ordinary authored looks are freely copyable; exclusive custody of them adds outages and protects nothing | Leaves A1 within a non-economic scope by choice |
| Progress a world gates on (clears, unlocks, levels, experience) | Signed claim with provenance from the world that granted it, with an issuer-qualified predicate and a sequence | A copied non-exclusive claim for the same subject is harmless; a reader applies its own policy per issuer | Closes A1 and A2 for what others issue; an authorised issuer can still lie within its predicate; A9 |
| Progress nothing gates on | Unsigned, cosmetic only | Nothing to protect | Leaves A1 by choice |
| Reputation | Signed claims per issuer, merged monotonically at account scope (best value, union), never spendable | Every studied game merges account-scope state upward | As progress |
| Machine save RAM | Unsigned, forkable personal data; a competitive import is a signed claim with execution evidence | A save is legitimately forkable; unique bytes are not honest gameplay | Local persistence addresses crash A3; a competitive score needs trusted inputs and a pinned interpreter |
| Companions | Issuer-kept (home's title) moved by custody, as built; a scarce companion's title moves to the settlement authority | The protocol fences bodies; home is the player's host, so trust stops there (A7) | Closes a sender's forgery; leaves home forging until the title moves |
| Characters | The identity document is the player's unsigned data, holding references to issuer-kept records and the signed claims it carries; the body is a titled incarnation under the custody protocol (H14); one seat across hostile homes is an authority neither party controls | A human seat has control, reconnect and admission rules a companion lacks; I3 waits on H14 and step 7 | A1 for the document's own numbers until every gated number is one of the classes above |

## 3. Abstractions, not providers

Each abstraction states what it guarantees, what it must never assume, and
today's provider as one implementation. No question in this memo depends on
one provider's configuration. The leak check for each is one search: a provider
type named outside the provider's project.

**Identity: who is speaking.** Guarantees that an arrival's source is a
namespace a chain under a trusted root vouched for, bound to this connection's
nonces, transport keys and direction, and that the namespace is the only
verified fact about the arrival ([puck-today, section 5](puck-today.md#5-federation))
**[record]**. Never assumes a name is a key or that a namespace is a person.
Today: `SignsDirectly` claims from a PKCS8 key file for world authorities, five
minutes long **[opened]** (`src/Puck.World.Protocol/Codecs/WorldAttestedAuthenticator.cs:38`):

```csharp
    public static readonly TimeSpan MaximumClaimAge = TimeSpan.FromMinutes(value: 5);
```

and, on the deployed silo, a bearer Entra token echoed beside the challenge
**[opened]** (`src/Puck.World.Azure/EntraWorldAuthenticator.cs:140`):

```csharp
        return Encoding.UTF8.GetBytes(s: (((((Prefix + Convert.ToBase64String(challenge)) + "\n") + m_session) + "\n") + ReadClientToken()));
```

The second is a bearer credential for the token's lifetime, not a signature
over the challenge **[inference]**; the abstraction wants a signature.

**Issuance: who mints a namespace's key and binding, with what validity.**
Guarantees that a namespace's key was issued under a root the verifier pins,
with a validity window the issuer chose and the verifier may shorten. Never
assumes the issuer is reachable at verification time. Today: the remote-issuer
shape is named and unimplemented **[opened]**
(`src/Puck.World.Protocol/Codecs/ISigningOracle.cs:9-11`):

```csharp
/// (<see cref="LocalKeySigningOracle"/>, a <see cref="AttestationTrustMode.SignsDirectly"/> claim with no chain) or
/// a remote issuer consulted per connection attempt (a <see cref="AttestationTrustMode.Vouches"/> claim with a
/// root-issuing-subject chain, obtained from the platform at connect time) both hand back the same wrapped
```

and the platform's binding service mints 30-day bindings that no world code
presents **[opened]** (`src/Puck.Azure.Functions/Services/IssuingKeyOptions.cs:28`):

```csharp
    public TimeSpan SubjectBindingValidity { get; set; } = TimeSpan.FromDays(value: 30);
```

**Key lifetime.** Guarantees that every claim and binding carries a window the
verifier enforces, and that a verifier may set ceilings per entry, per root
binding and per subject binding, plus a replay horizon. Never assumes a null
ceiling means safe. Today: all three ceilings and the horizon are null at the
admission door ([decision 9](#decision-9-revocation-below-expiry)).

**Revocation.** Guarantees that a root can refuse a key, an issuer or a subject
below expiry, as signed versioned data a host consumes offline, applied at one
door and re-checked on live connections and outstanding grants. Never assumes
a fetch at verification time. Today: none; the levers are expiry, an admission
edit and group membership on the deployed silo
([puck-today, premise verdicts](puck-today.md#premise-verdicts)) **[record]**.

**Storage.** Guarantees a durable, fenced, conditionally written store: a
write names the version it read, a stale writer is refused by name, an
allocation is persisted before it is exposed, and an append answers only once
the store knows the outcome. Never assumes an OS-buffered write is durable.
Today: the authority blob store's activation epoch against the root's version
token and the owned-world sync's `if-match`
([custody, leases](custody.md#leases-fencing-and-restart-continuity);
[puck-today, section 1](puck-today.md#1-identity-worlds)) **[record]**, on
Azure blobs.

**Hosting.** Guarantees a place for an authority to run with a durable crossing
log, a checkpoint cadence and a fenced activation so a superseded authority
cannot claim a step its successor will not recover. Never assumes one placement
system's single-activation promise is an economic record of title
([custody, virtual actors](custody.md#virtual-actors-and-the-silo))
**[record]**. Today: the silo on Orleans with `RowCrossingLog`; the desktop
with none.

**Settlement** (new, from [decision 3](#decision-3-custody-of-owned-things)).
Guarantees one transactional door per domain at which a scarce right is
authorised, fenced and deduplicated in one durable write, with one terminal
outcome per operation id and the outbox that propagates it. Never assumes a
signature is a debit. Today: none.

## 4. Puck-native combinations

What Puck can do that a server-authoritative MMO cannot, weighed against the
failure modes the records found.

| Combination | What it buys | Where it fails |
|---|---|---|
| Characters and players as worlds | A character is a document with the same row-by-row admission and conversion the world image gets, so one mechanism serves both ([persistence, combinations](persistence.md#combinations-puck-is-placed-to-make)); a league is a world the identity enters and leaves, and the merge is write-back; a settlement world is a world chosen per community | The document is the player's assertion until every gated number is a signed claim or an issuer's record (A1); a human seat across hostile homes needs an authority neither controls (I3) |
| Portals and hidden seams | A hidden seam does not need loose write rules: FFXIV's visit is invisible at the seam and strict at the economy; a hidden seam crosses a rendering or simulation boundary without moving spend authority, so not every step is a transaction ([economy, question 4](economy.md#question-4-and-attack-question-1-classes-of-value)); a frameless portal is authored, not built | A seam that moves under a live player; a seam hopped to farm; a seam that lets a neighbour shove (A10) until C3 lands |
| Derived products as caches | Stats derived from template plus instance values are never stored, so a squish is a template edit; meshes, projections and images regenerate from inputs | Custody, consumed value, receipts, commitments and unrevealed secrets are evidence, never caches; a restored image must ask for current rights before it may act ([custody, combinations](custody.md#puck-native-combinations-and-remaining-attacks)) |
| Behaviour as authored `.puck` programs | Conversions, merges, the value-class table, wagers, side pots, rake, timeout and custody rules are the author's; the engine keeps transactions, verification, deadlines and receipts generic | A conversion that is wrong is a whole-population defect; a program's semantic identity must be pinned so a later edit does not resettle an earlier hand ([economy, fairness by replay](economy.md#the-hypothesis-fairness-by-replay)) |
| Deterministic replay of a fixed-point simulation | A tape reproduces an authority's run; a visitor holding its own inputs and the destination's signed snapshots holds something like RS3's telemetry for its own session | It proves something only when someone other than the authority holds the inputs; it cannot prove omitted inputs never existed, pick the honest fork, keep a hand from its host, or decide who wins a disconnect |
| Mutual simulation with hash comparison (C2) | Two peers each simulate the whole encounter and check the other's hash; a mismatch is a provable lie once a referee replays the signed tape; no float-based MMO can do this | 1 to 8 peers; no hidden state in the slice; a witness for stalls; a threat model inferred rather than documented |

## 5. How the debug world proves each favoured option

The smallest check per decision, headless on a fresh `--state-dir` unless the
check is about reusing one, with what must be authored first. Each is a step
another lane takes; none ran for this memo.

| Decision | Smallest check | Decides it | Author first |
|---|---|---|---|
| 2 (2b) | Create a character, set a record that references a tunable standard template value, quit, edit the template, relaunch; `identity.show` reads the new derived value and the identity file's bytes are unchanged | Tuning is no longer copied into the character | A standard template row an identity record references |
| 2 (2c) | The garden renames a row and ships a conversion that maps it and refunds a removed row into a currency fact; set both, quit, edit, relaunch, travel: the new row holds the mapped value, the fact holds the refund, the narration names the conversion, a second relaunch applies nothing, `puck test --reproduce` agrees | A conversion runs once and replays | The conversion surface (a bounded prototype over the garden) |
| 2 (set-aside) | An identity gains a row the template no longer declares; the catalog loads it, drops that row by name, keeps the rest; `unloadable/` stays empty | One bad row no longer costs the character | Nothing |
| 3 (allocator) | Cross the garden seam; restart after each durable step and retry the same operation; one terminal outcome per operation, monotonic epoch, no duplicate credit, pending obligations retained | Epochs survive restart | H7's desktop log and image |
| 3 (fence at effect) | Pause the old custodian after it prepares an effect; install the next grant; resume: the effect door rejects the old epoch | The comparison is at commit, not before | The settlement door |
| 3 (A7) | A scripted hostile home confirms one incarnation to two destinations; at most one recognised use grant, competing receipts visible, rewards collected from one copy | Duplicate rights need an acceptance order outside home | The settlement door |
| 4 (4b) | A night-field rule writes a garden-namespace fact onto a visitor; adoption refuses it by name and adopts the night field's own fact | H9 extends from `Decide` to the crossing | Nothing (both worlds exist) |
| 4 (4c) | A currency fact marked home-scoped in the standard template; a garden rule decrementing it on a visitor is refused at the destination's write door; it reads unchanged on return | Home scope holds abroad | The class marking on the template |
| 4 (4f) | Travel to the garden, let a rule award a fact, kill the process before return, relaunch: the fact is present; a replayed submission returns the prior award id and the count stays one | Progress survives the host | Write-through through `Decide` |
| 5 (S1) | Two layer instances of a room with an interactable prop; start a channel and trigger a layer move: it defers until the channel ends; a second hop inside the cooldown is refused by name | No mid-cast vanish; no hop farming | The predicate and the hop stamp |
| 5 (F1) | A `threshold` template beside the gallery paintings; walk through it; `world.portals` and a crossing screenshot show no frame | A frameless portal is authored | The template |
| 5 (I1) | `population.disclosure` at `Radius 15`, 64 autonomous bodies crossing the radius, one unembodied spectator and one embodied player: the spectator receives zero bodies without a camera grant; a body oscillating 0.1 m at 15.0 m does not flicker; `world.budget` shows redaction cost scaling with nearby bodies | A16 closed; hysteresis; cost | Disclosure step 1 |
| 6 (6b) | A player hosts the room and sits at a remotely hosted table; the host sees only its permitted cards; changing another hand changes no forbidden observation (the disclosure harness) | A seated host sees nothing hidden | A table world a seat does not host; disclosure step 1 |
| 6 (stakes) | Two seats reserve chips; interrupt before and after each durable decision; retry and restore the player copy: available plus reserved plus settled is conserved, one terminal decision, repeated collection changes nothing | Stakes as reservations | The settlement door |
| 6 (draws) | Commit seeds, accept the stake, then try changed seeds, reused hand ids, omitted reveals and a host-selected retry: each refused; abort resolves without a free reroll | Commit-reveal binds | Per-participant entropy on a draw site |
| 8 (C3) | Across an authored debug-to-garden adjacency, inject a neighbour snapshot with a huge overlap or a fast collider against a stationary local body in the strip: single-tick displacement is clamped and the body never crosses the deadband; `replay.record` then `replay.verify` reproduces the contact tick for tick | A10 closed; the replay boundary closed | An `adjacencies` row (none exists) |
| 8 (C2) | Two loopback hosts step a two-body duel over the same taped inputs for 300 ticks exchanging hashes; a fault leg alters one position by 1 LSB at tick 150: hashes agree on every tick without the fault and diverge at 150 with it, and `replay.verify` names the host | Mutual simulation detects a lie | A duel arena |
| 9 (9b) | Revoke a key on a live connection; replay old status data after a restart: new valuable actions stop at the policy boundary; the older snapshot cannot replace the newer; title stays recoverable | Revocation as data | The snapshot format and door |
| 10 (10a) | Commit a traveller into a desktop destination; kill it; resend the same reservation and commit: refused as consumed | A8's crash case closed on a desktop | H7's log and image |

## 6. The order of work

How the hub steps, the custody protocol's steps 6 to 10, disclosure and
federation reorder under the favoured options. Each step names what it
unblocks. Steps marked "new" are not in any plan today.

1. **Disclosure step 1** (in flight; step 0 stands): `WorldRecipientView` and
   `WorldDeliveryCursor`. Unblocks I1, the trusted-dealer check and any claim
   that a silo keeps hidden state from a visitor
   ([disclosure, build order](../disclosure.md#the-build-order)).
2. **C3's taped neighbour records** (a defect fix; the correction cap and
   ownership-face containment are implemented). Build the [neighbour replay packet](../open-items.md#transfers-federation-and-server).
   The cap bounds A10's displacement and keeps shoves behind the ownership face
   but does not prove reported bodies exist; tapes close the named replay boundary.
   Independent of everything else.
3. **H1 and H2** unchanged: the standard's identity template and one account
   per process ([hub-and-saves, build order](../hub-and-saves.md#build-order)).
   H2 unblocks every later step.
4. **H9 extended to the crossing adoption, plus home-scoped classes** (4b, 4c):
   one authorisation rule at both doors, `Decide` and `TryAdopt`, or `TryAdopt`
   made a submission through `Decide`. Closes A2 in process. Unblocks the
   value-class table on the template.
5. **Write-through as earned** (4f, new): an award is a submission home decides
   at its tick boundary with a unique award id. Closes A3's crash case for
   progress. Needs H2 and step 4.
6. **H7a to H7e, with the desktop crossing log** (10a): durable images and the
   log on any desktop that is a destination, with the A13 constraint (a
   qualified generation identity when a host both restores and spawns). Closes
   A8's crash case. Unblocks the allocator check and custody steps 6 to 8 on a
   desktop.
7. **Custody steps 6 and 7, then H14**: separation, shed, retire, recall and
   release for companions; then the identity title and recall for characters.
   Unblocks I3 (one seat per identity across hosts). Step 8 (lifetime) follows.
8. **The settlement door** (3b, new): one transactional spend door at an
   authority the player does not host, with issuer-kept records, the outbox,
   and the conserved-value checks. Needs owner call 5. Unblocks currency,
   items, stakes and the A7 check.
9. **Identity, issuance and revocation as abstractions** (section 3, decision
   9, new): the signing oracle's remote shape, binding ceilings at the
   admission door, the refusal snapshot, and the provider behind them. Needs
   owner call 14. Unblocks any federation beyond loopback and any vouched
   admission row.
10. **The tables** (6b): a non-seated table authority, stakes at the settlement
    door, commit-reveal on draw sites, the transcript policy. Needs steps 1,
    8 and owner calls 10 to 12. Unblocks the casino fantasy as a product.
11. **Federation adoption as signed submissions** (4d): remote write-back
    built on steps 4, 5 and 9, never as unsigned adoption. Unblocks progress
    earned on another machine.
12. **S1's layer gate and I1** (decision 5): the unobservability predicate and
    hop cooldown; the view's spatial grid, hysteresis, unembodied policy and
    cadence tiers. Needs step 1. Unblocks population layers.
13. **Custody steps 9 and 10**: role drivers and the debug world's faerie trip,
    as planned.
14. **C2 as a bounded prototype** (decision 8): the loopback duel with hash
    comparison and a referee replay. Needs owner call 13 and step 9's witness.

What moved: disclosure step 1 and the namespace door go before durable images,
because every later check reads through the view and every write-back check
needs the door; write-through goes before H7 because it closes the larger
visible loss (progress) with a smaller change than the log; the settlement
door and the identity abstraction are new steps placed before any federation
beyond loopback, because the records agree that nothing scarce should cross a
machine boundary until both exist; H14 keeps its place after custody step 7,
as the hub plan already orders it.

## 7. Owner calls

1. ([Decision 1](#decision-1-the-engines-purpose)) Is this the purpose, in
   these words: Puck exists to make it possible to build an MMO at World of
   Warcraft's scale that redefines the genre; the emulators, nested screens,
   tables and portals are parts of that MMO; and every design round weighs its
   question at that scale?
2. ([Decision 2](#decision-2-player-state-across-author-edits)) Does saved
   state carry over an edit that keeps a row's declaration, with an authored
   conversion as the only path for a row whose meaning changed?
3. ([Decision 2](#decision-2-player-state-across-author-edits)) When an
   inexact image holds titles abroad or transfers in doubt, does the world
   start with the affected value doors frozen, or refuse to start?
4. ([Decision 2](#decision-2-player-state-across-author-edits)) How long is
   set-aside data kept, and who may restore it?
5. ([Decision 3](#decision-3-custody-of-owned-things)) Who may be a title and
   settlement issuer: the studio's silo only, any operator a root vouches for,
   or any community that names one?
6. ([Decision 3](#decision-3-custody-of-owned-things)) May a pending title
   whose destination is gone for good wait forever, or does the engine offer
   the player a recall that accepts a possible second body there?
7. ([Decision 3](#decision-3-custody-of-owned-things)) Does a home restarted
   without its checkpoint stay a new authority whose companions wait forever,
   or may the player recall them?
8. ([Decision 4](#decision-4-value-across-trust-boundaries)) Is the
   classes-of-value table the one the standard package authors, and are
   levels and experience a signed claim from the world that granted them?
9. ([Decision 5](#decision-5-seams)) Does a layer move wait for a transition
   volume out of combat, or happen in place after a visible countdown with an
   increasing cooldown?
10. ([Decision 6](#decision-6-the-tables)) Which product row is the casino
    fantasy: table-local score, earned chips, purchased chips, tradeable
    stakes, or real money, and for which territories and ages?
11. ([Decision 6](#decision-6-the-tables)) Is an independent dealer
    acceptable, or must the table keep its secrets from every host?
12. ([Decision 6](#decision-6-the-tables)) Must mucked hands stay secret after
    the hand?
13. ([Decision 8](#decision-8-where-a-contested-place-runs)) May a
    player-hosted world ever host raw PvP without a neutral witness, or does
    every fight with stakes or durable progress run on an authority neither
    combatant controls?
14. ([Decision 9](#decision-9-revocation-below-expiry)) What delay between a
    ban and its effect is tolerable, and does a world fail closed when its
    refusal snapshot is older than that?
15. ([Decision 10](#decision-10-what-a-player-host-may-be)) May a friend's
    machine be a destination for a stranger's body at all, and if so, is it
    the rule that nothing scarce is decided there?

## 8. What would change the answer

- **A measured settlement latency the owner will not accept.** If synchronous
  acceptance at the settlement door costs more at the intended network than
  the owner tolerates for an award to read as final, the favoured 4f moves to
  the batched outbox with a visible pending state, and 3c (prefunded bounded
  delegation) carries more weight ([custody, open questions](custody.md#open-questions)).
- **A working, reviewed encrypted-dealing prototype within the between-hand
  budget on bar devices.** Lane D's packet (two, six and ten seats; bytes,
  rounds, proof work, memory, latency with one slow seat) would move 6c from
  candidate to favoured for participant-hosted tables, and would weaken the
  case for a dealer the owner must trust ([economy, debug world](economy.md#how-the-debug-world-would-prove-it)).
- **A desktop host shown to recover crossings and images through a kill**
  (step 6 of the order). If H7 and a desktop log hold under fault injection,
  10a carries A8 on its own and the argument for 10b narrows to the dishonest
  case; if they do not, 10c returns as an option.
- **Confirmation that the adoption consequence of A13 is reachable** once a
  desktop both restores images and spawns resolver instances. Lane E's
  refutation is for today's hosts; a reproduction after H7 would make the
  qualified generation identity a precondition of H7 rather than a constraint
  on it ([puck-today, premise verdicts](puck-today.md#premise-verdicts)).
- **A primary source for continuous character saves during instance play** in
  WoW or FFXIV. Lane A could not find one; if instance servers save per event,
  the practice supports 4f more strongly; if they save at exit, the batched
  outbox is the practice ([persistence, open questions](persistence.md#open-questions)).
- **A documented threat model for peer duels** (lag switches, deliberate
  desync, disconnect attribution). Lane C's witness and referee steps answer
  inferred threats; a documented one would fix the witness's shape, and a
  refuted one would drop it ([seams, citation check](seams.md#7-citation-check)).
- **The owner choosing table-local score.** That removes the settlement door
  from the tables' path and leaves it for currency and items alone, which
  reorders steps 8 and 10.
- **The owner choosing forkable personal worlds (3d) as the default.** That
  removes most of decisions 3, 4 and 10 from the near term and makes the
  namespace door and write-through the whole of the value work.
- **An ArenaNet statement on the 2020 duplication.** Press-reported today; a
  confirmation that separate trading-post and guild-bank stores caused it
  strengthens the rule that anything moved between a realm and a character
  settles against one record ([persistence, open questions](persistence.md#open-questions)).

---

[Plans](../README.md) · [MMO foundations](../mmo-foundations.md) · [Attacks](attacks.md) · [Persistence](persistence.md) · [Custody](custody.md) · [Seams](seams.md) · [Economy](economy.md) · [Puck as it is](puck-today.md) · [The world model](../../architecture/worlds.md)
