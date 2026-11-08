# MMO foundations: synthesis

The synthesis memo of [the MMO foundations round](../mmo-foundations.md). It
weighs the five research records and [the attack catalogue](attacks.md) into
the decisions the owner will rule on. Nothing here is decided. Each decision
names its options, the evidence for each, what each closes and leaves open,
which option the evidence favours, the strongest case against it, and the
plain question the owner must answer. A second attack pass
([attacks on the synthesis](synthesis-attacks.md)) and two adversarial reviews
read the first revision of this memo; [the review answers](#9-review-answers)
take every finding one by one, and the sections above them carry the changes
those findings forced.

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
the product calls. The favoured composition, as the attack pass and the
reviews left it: scarce value (currency, tradeable items, stakes) lives as
issuer-kept records behind one transactional settlement door per domain, at an
authority the player does not host; only that door creates units, every award
by another authority draws a prefunded budget it cannot exceed, every terminal
decision at the door is a signed receipt both parties hold, and no scarce right
crosses settlement domains until the owner says whether Puck has one economy,
sealed economies or exchange between them. Progress another world gates on
travels as a signed claim from the world that granted it, and a reward paid for
that progress is a redemption the door consumes once. Cosmetics and personal
saves stay the player's own unsigned data. A character's identity document is
the player's and holds references and receipts; its body moves under the
companion protocol's title and custody, and where a settlement domain requires
single presence the active seat is the domain's right, not home's. A visited
world writes a visitor only in its own package namespace, through the one write
door rule effects and the console share, never a home-scoped class, and what
it writes goes home as it is earned behind a durable award commit. Neutrality
(a dealer, a referee, a witness) is a role the settlement domain vouches, never
a property a world claims about itself. A seat's private view of a table is the
seat's own session with the table; a room's screen shows the public tier. Seams
are hidden for shards and layers behind an unobservability gate the layer
authority evaluates over its own state, and visible where the fiction is
travel; a frameless portal is authored, and population layers need resolver
and transfer-door work before the gate has anything to gate. Contested places
run on a vouched authority; the seam contact field is clamped, as a defect, and
what the clamp leaves open is named. Deadlines are recorded decisions by the
authority that owns the right. The order of work puts the write door,
write-through behind a durable award commit and identity recomposition ahead of
durable images, and puts the settlement door, the identity abstraction, remote
seating and per-seat table sessions ahead of any federation beyond loopback and
any table with stakes.

The owner's calls are [collected at the end](#7-owner-calls). The largest are:
whether Puck has one economy, many sealed economies or exchange between them;
who may be vouched to mint, deal, referee and witness; what happens when an
accepted issuer cheats or loses its state; what the casino fantasy is allowed
to be; whether a friend's machine may be a destination for a stranger's body
and what its awards count for; and whether raw PvP between two player hosts may
ever run without a neutral witness.

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
2. **`WorldRecipientView` was a plan when the records were written.** Lane
   C's options I1 and C1 lean on it; lane E and lane C's own citation check
   found it absent from `src/` ([seams, citation check](seams.md#7-citation-check)).
   Disclosure step 1 has since landed: `WorldRecipientView` and
   `WorldDeliveryCursor` exist and the session sink delivers through them
   **[opened]** (`src/Puck.World.Server/WorldSessionSink.cs:27-29`):

   ```csharp
       private readonly WorldDeliveryCursor m_cursor = new();
       private readonly WorldDeliveryRows m_rows = new();
       private readonly WorldRecipientView m_view = server.DeliveryView(observation.Session, observation.Tier, () => observation.CanObserve);
   ```

   Body disclosure still runs through `Redact` at the hub until I1. The order
   of work keeps step 1 as the floor the table and interest-management steps
   stand on ([decision 5](#decision-5-seams), [the order](#6-the-order-of-work)).
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
program), so it replays bit-identically **[inference]**. A conversion that
refunds a settled class (currency for a removed row) emits a settlement
operation keyed by (identity, conversion id) and writes no document fact, so a
document whose version is edited back, or a pre-conversion copy run at a second
home, gets the prior outcome and not a second refund
([B21](synthesis-attacks.md#b21-a-conversions-refund-re-run)).

**What 2b and 2c need built.** Neither is authoring alone. The identity
document is a whole copy of the template at seed (`Seed`, above) that the
catalog loads from disk as a self-contained definition
(`WorldOwnedWorlds.TryLoadOwned`) and sets aside whole into `unloadable/` when it
cannot read it; removing a row from the template leaves the row declared in
every stored document, and `identity.show` prints the identity's motion and look
and no record rows (`identity.state <id> <row>` reads one row) **[opened]**
(`src/Puck.World.Server/WorldOwnedWorlds.cs:27`, `:596`, `:691`;
`src/Puck.World/IdentityCommandModule.cs:22`). So 2b needs the stored identity
recomposed against the current standard identity template on load, with the
image ladder's row-by-row admission applied to identities, and 2c needs a
conversion phase that runs before that admission drops the rows the conversion
reads. Both are [steps in the order of work](#6-the-order-of-work).

**The two durable-image calls, one decided here.** The evidence favours
carrying saved state over an edit (every studied game carries by default) and
adds the third path the memo lacked: a row whose meaning changed is converted,
not carried or dropped (FFXIV's EXP reset is the cost of the missing path)
**[record]**. For custody an inexact image holds, every studied service froze
the doors that move the affected value until it was resolved (Diablo III's
auction house, New World's transfers), and the record says to review the frozen
door itself, because New World's freeze opened a new duplication path
([persistence, question 2](persistence.md#round-question-2-player-state-across-author-edits))
**[record]**. That is an engineering decision with evidence, and this memo
makes it: the world starts with the affected doors frozen and narrates why;
refusing to start is the stopgap. The freeze is reviewed as a door of its own
in the settlement step.

**Strongest case against 2b and 2c.** They make the template a shared point of
failure and add a language surface before the state-and-language plan has one.
The answer is that the failure exists today in a worse form (a whole character
set aside), and that the conversion surface is the author's program, which
AGENTS.md already asks for.

**Owner calls 2 and 3.** Does saved state carry over an edit that keeps a row's
declaration, with an authored conversion as the only path for a row whose
meaning changed? How long is set-aside data (a dropped row, a refused image, a
quarantined identity) kept, and who may restore it?

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
3b for scarce value; 3a for bodies (companions as built, characters at H14),
with two changes the attack pass forced. First, a scarce companion whose title
sits at the settlement domain has that domain as the custody protocol's home;
the player's host is a custodian, and recall is the issuer's decision at the
owner's request, never the host's, so one right is never split across two
authorities ([B17](synthesis-attacks.md#b17-two-title-holders-for-a-scarce-companion)).
Second, where a settlement domain requires single presence (ranked tables,
scarce drops, time-gated rewards), the active seat is a settlement-domain
right: one active incarnation per identity per domain, consumed at each landing
and released at departure, which is I3 moved from home to the domain, because a
home that stays the body's title authority under 3a is the first catalogue's A7
attacker and can confirm one incarnation into N worlds at once
([B4](synthesis-attacks.md#b4-parallel-embodiment-under-3a); Codex R5; Argon 5).
Outside any domain (a friends-only world under 3d) parallel presence is
accepted and earns nothing settled. 3c for every award by an authority that is
not the settlement door, and for offline play: the door removes a budget from
circulation before delegating it, a zone's awards are unique redemptions against
that budget, and a zone with no budget awards claims (row 5 of the table) and
nothing settled ([B5](synthesis-attacks.md#b5-award-ids-stop-a-retry-not-a-re-issue),
[F2](synthesis-attacks.md#f2-3c-is-placed-on-tables-and-the-tables-use-3b)).
3d for cosmetics and personal saves. An unreachable title authority has no
automatic substitute: a committed custodian keeps its rights within their
scope, a pending move stays pending, and a new exclusive grant waits
([custody, question 3](custody.md#round-question-3-custody-and-the-hypothesis))
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

**Recall is engineering, not an owner call.** A title whose custodian has
missed its lease by a margin the standard package authors is recallable by the
title authority. Under 10b the recalled body's possible twin at the dead
destination can earn nothing settled and issues claims only in that host's
namespace, so the second body costs nothing, and under "wait forever" a host
with a `*` admission row traps every character that visits it. The same rule
covers a home restarted without its checkpoint. The two questions the first
revision put to the owner (wait forever, or recall) are one mechanism, decided
here ([B16](synthesis-attacks.md#b16-hostage-bodies-under-wait-forever)).

**Value across domains.** A settlement domain is a key, and under the widest
issuer policy anyone with a few accounts can name one and mint honestly by its
own rules. So no scarce right issued in one domain is accepted in another at
this stage: a hand's or a trade's inputs are all reserved under one domain
before play, and a mixed-domain operation is refused by name at the door. The
Settlement abstraction guarantees one door per domain and nothing about two
domains; lane B names the three mechanisms an exchange would need (a common
escrow authority, independent ledgers with enforceable conditional exchange, or
deliberately limited bilateral credit, `custody.md:587-589`) and the condition
none of them survives ("either domain can invent balances", `custody.md:411`)
**[opened]**. Which, if any, Puck builds follows from the owner's answer to
what Puck's economy is ([owner call 4](#7-owner-calls))
([B1](synthesis-attacks.md#b1-the-sock-puppet-settlement-domain); Codex R1).

**Neutrality is a grant, not a property.** "An authority neither party
controls" is a phrase the first revision used as if something issued it, and
nothing does: a table authority, a referee world and a C2 witness are
namespaces under a root, and the identity abstraction's only verified fact
about an arrival is its namespace. A seated player with a second key file or
account hosts the "neutral" table and the door, trusting the table for that
hand id, moves a stranger's stake. So neutrality is a role the settlement
domain operates or vouches: the door accepts a hand's terminal decision, a
fight's outcome or a silence attestation only from an authority vouched for
that role, and the reservation names that authority before the hand or fight
begins, beside the roster, rules identity and recovery policy lane D already
freezes. A table no vouched authority runs is table-local score by
construction, whatever its fiction. Who may be vouched is the owner's
([owner call 5](#7-owner-calls)), and it decides who may deal, referee and
witness, not only who may mint
([B3](synthesis-attacks.md#b3-the-table-authority-settles-the-hand)).

**The invariant and what enforces it.** Within a settlement domain, every
scarce right is available, held for one unresolved transaction, transferred,
consumed or destroyed, and no two accepted effects consume the same right and
version ([custody, debug world](custody.md#how-the-debug-world-would-prove-it))
**[record]**. The one enforcement point is the settlement door's commit, which
authorises, compares the fence and deduplicates in one durable write, and
which alone creates units. Every terminal decision is a signed receipt
(issuer, subject, operation id, prior and next version, outcome) delivered to
each party and held in its identity document on a row that refuses when full
and never evicts; a reader may demand the receipt behind a reference; two
receipts for one right and version are proof of equivocation
([B2](synthesis-attacks.md#b2-the-dishonest-settlement-issuer-equivocates-or-restores);
Codex R2; Argon 7). Nothing enforces this today: the companion reducer fences
bodies, not value, and no spend door exists. The guarantee stops at authorities
the domain accepts, at effects that bypass the door, and at the issuer itself:
an issuer that equivocates or restores its own store is detected by the
receipts its counterparties hold, never prevented, because prevention needs a
common final decision outside the issuer, which this memo does not propose
(`custody.md:363-366`: receipts "do not automatically prevent double spending
before witnesses compare views") **[opened]**. When a domain's store is
restored, economic decisions, consumption and refusal high-water marks sit
outside gameplay rollback, and the issuer reconciles against its
counterparties' receipts or withdraws its namespace (`custody.md:97-99`)
**[opened]**; who bears a loss the receipts prove is the owner's
([owner call 6](#7-owner-calls)).

**Strongest case against 3b.** It reintroduces a centre in an engine whose
plain-terms page begins "Puck has no such centre" **[opened]**
(`docs/architecture/worlds.md:17`). The answer is that a settlement world is a
world: chosen per trust community, replaceable, and small, while zones,
characters and tables stay where their authors put them. What it does not
answer is the owner's taste: a friends-only economy may prefer 3d everywhere.

**Owner calls 4 to 7.** Does Puck have one economy, many sealed economies, or
exchange between them? Who may be vouched to mint, deal, referee and witness:
the studio's own authorities only, operators a root vouches for, or any
community that names them? When an accepted issuer cheats or loses its state,
what happens to the rights it issued and who bears the loss? Is a scarce item
tradeable by default, or bound to its holder unless its class opts into
transfer?

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
| 4b. Namespaced write-back: a visited world writes only facts and records in its own package namespace; the write door refuses others by name at the tick the rule fires (H9 on the one door rule effects use) | FFXIV's deep-dungeon leaderboard and company credits are not written while travelling **[record]** | A2's griefing of other namespaces | Bob writing Bob's namespace with anything; A1; A3 |
| 4c. Home-scoped value classes: value-bearing classes are readable abroad and never writable; a visited world issues a claim or a pending grant home applies by its own rule | FFXIV's travel restrictions and GW2's home-only World versus World, as engine policy over authored classes **[record]** | A2 for value | A1; progress classes |
| 4d. Signed write-back: the visited world signs what it writes; home stores the claim with its issuer; a reader applies its policy per issuer | The plain-terms page's worked example; no studied game needs it because one operator issues everything **[record]** | A1 for what others issue; A2's laundering becomes visible as Bob's claim | A1 for what only the player issues; A9 |
| 4e. Issuer-kept records: unique value stays at its issuer; the identity holds a reference | Every studied game's model **[record]** | A1 and A2 for that class | Offline spend; issuer inflation is a trust decision |
| 4f. Write-through as earned: each award is submitted home when granted, as a pending claim home decides at its tick boundary | RS3's telemetry at logout and GW2's queued messages made restore possible **[record]**; lane B favours durable write-through for anything the UI calls earned **[record]** | A3's crash case for progress | A3's dishonest case (the host withholds), unless the visitor holds the signed award as soon as it is issued (4d) |

**Favoured.** 4b, 4c and 4f together now, in process, at the one write door.
That door is not `TryAdopt`. After H2 one account catalog serves the whole
process and `Owns` answers true in every local instance, so a fact a rule
awards in the garden lands through `WorldOwnedWorlds.TrySetFact`, the door
`WorldRuleHost.PersistIdentityFact` and the console's `identity.fact.set` share,
which reads no source document and never consults `Decide`
([puck-today, section 1](puck-today.md#1-identity-worlds)) **[record]**;
reopened **[opened]** (`src/Puck.World.Server/WorldRuleHost.IdentityFacts.cs:214-216`):

```csharp
    private void PersistIdentityFact(WorldIdentity identity, CellName key, long value) {
        if (
            !Host.Profiles.TrySetFact(
```

and the hub plan says H2 ends in-process adoption on return ("a fact set in the
garden reads back in the boot row with no adoption", `docs/plans/hub-and-saves.md:989`)
**[opened]**. So the namespace rule is one authorisation on `TrySetFact` and
`Decide`, keyed by the executing world's document and its package; `TryAdopt`
stays the door for a body coming home from another host and applies the same
rule to what it carried (Argon 1; lane B's open question "one authorization
rule at all paths", [custody, open questions](custody.md#open-questions)). The
namespace is the package, as H9 says: one package's worlds share it by design,
which is also why the debug world's night field and garden, one package, cannot
be the pair that shows a refusal; the check uses a second package. 4f is split
by class: a progress award goes home as a pending claim with a unique award id;
a settled class goes to the settlement door as an operation and home holds the
receipt ([F1](synthesis-attacks.md#f1-favoured-4f-misstates-lane-b),
[B5](synthesis-attacks.md#b5-award-ids-stop-a-retry-not-a-re-issue)). Write-
through also needs a commit `Decide` does not have: it persists and answers
`Accepted: true` even when the save failed and was only narrated, and the
receipt's contract is "whether it applied", not whether it is recoverable
**[opened]** (`src/Puck.World.Server/WorldOwnedWorlds.cs:486-488`):

```csharp
        Persist(identity: owner);
        return new WorldDocumentSubmissionReceipt(
            Accepted: true,
```

where `Persist` narrates a failed save and returns (`src/Puck.World.Server/WorldOwnedWorlds.cs:1198-1201`):

```csharp
    private void Persist(WorldIdentity identity) {
        if (
            !TrySave(
                identity: identity,
```

and the receipt says (`src/Puck.World.Protocol/Protocol/WorldDocumentSubmission.cs:29`):

```csharp
/// <param name="Accepted">Whether it applied.</param>
```

So the write-through step introduces a durable award commit: award id, credited
effect and outcome in one write, and a receipt that distinguishes applied,
durably accepted, pending and uncertain (Codex R4). 4e for scarce classes
behind the settlement door ([decision 3](#decision-3-custody-of-owned-things)).
A reward paid for a claim is its own class, not progress: signed evidence plus
an issuer-kept redemption id the door consumes once within a named redemption
domain, so two places that both accept one signed clear cannot each credit it
(Codex R3; `custody.md:577`). 4d is the remote path: when federation adoption
is built, it is built as signed submissions and never as the unsigned adoption
that exists today for colocated seats, and the submission signs (authority,
package, fact), because the only verified fact about a federated arrival is its
authority namespace and a package name is an authored string; a reader's issuer
policy is a map from authority namespace to the packages it may write, a
package with no authority mapped is refused by name, and in process the map is
identity ([B6](synthesis-attacks.md#b6-a-package-namespace-is-a-string)). A
displayed award is pending until the accepting authority acknowledges it
([custody, question 2](custody.md#attack-question-2-when-progress-becomes-durable))
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

**Owner call 8.** Whose record are levels and experience: one issuer's (the
main game's, so a visited world submits an award the issuer applies by its own
rule or draws from a budget), a per-world claim readers filter (many games in
one engine), or the player's own number (single-player worlds)? The first
revision offered the first two as if they differed; at MMO scale the only
reader policy that holds for a per-world claim is "the studio's worlds and no
other", at which point it is the first option
([B8](synthesis-attacks.md#b8-levels-and-experience-as-every-worlds-claim)). The
engine supports all three; the standard package picks one.

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
| S1. Fiction-aligned split: visible portals for travel and scope changes; `WorldAdjacency` for continuous shards; population layers moved only under an unobservability predicate (no combat, no channel in progress, in a transition volume or by a prompt) with an increasing hop cooldown | Blizzard's own layering deferred transfers during combat and before loot, added a longer delay after PvP, and hotfixed an increasing hop cooldown **[record]** | Authoring transition corridors or the predicate; a last-hop stamp kept by the layer authority; layer ordinals and a deferral queue the engine lacks | Cannot hide a layer change in an open field while the player watches a node | Closes layer-hop farming beside A7; A10 goes to C3 |
| S2. Framed transitions everywhere; no silent layers | FFXIV's numbered instances (summary only) | Gives up re-layering an entered field | A flash mob in an entered field | Removes mid-cast vanishes by construction |
| S3. Reactive spatial splitting at runtime | None that held; contradicts "Destinations are boot-authored document data" and "do not run an authority boundary through a place designed to be contested" **[opened]** (`docs/architecture/worlds.md:1413-1414`) | Reactive topology | The crowd at a junction (M2-XFE) | None |
| F1. A frameless portal is a creation with an aperture face and no frame rings, placed with a `WorldPlacementFace` portal; a standard `threshold` template | The glass fills the aperture edge to edge with no bezel; the door and the screen are independent facts about one face **[record]** | A template in `worlds/standard/`; no C# | Nothing it claims | None |
| F2. An adjacency that falls back to a frameless `window` across scopes it cannot project directly | Adjacency requires a global persisted destination **[opened]** (`src/Puck.World.Schema/WorldAdjacencies.cs:149`) | Teaching adjacency to host a window | Mixed lighting and sky | None |
| I1. Fold spatial grid, hysteresis, an explicit unembodied-observer policy and distance-tiered cadence into the disclosure view and cursor | The disclosure plan watches this seam as the view's natural second occupant **[record]**; Unreal's actor priority gives each actor a fair share under saturation **[record]** | Replaces `Redact`; builds on disclosure step 1, which has landed | Nothing it claims | Closes A16; narrows A12 |
| I2. Keep `Redact`, add a grid and an `Unembodied` mode | Smaller diff | Stateless across ticks | The fog-of-war skirmish; hysteresis; priority | A16 only |

**Favoured.** S1 with F1 now, I1 after disclosure step 1, F2 only if an author
asks for it. F1 is authored: the aperture recipe derives a walkable aperture
from a `Box` face shape and nothing else **[opened]**
(`src/Puck.World.Schema/WorldFaceApertures.cs:29`):

```csharp
            [SdfSolidPrimitive.Box] = new WorldFaceApertureRecipe(
```

so the `threshold` template is a box face that draws no frame, and what its
check must show is that the glass fills the aperture inside the face, as the
architecture page says it does, with no proud slab. S1's predicate is authored,
but the mechanism it gates is not built, which the first revision overstated:
the resolver keys every global resolution to one scope key, a persisted
destination refuses two running instances of one origin as ambiguous, and the
transfer door aborts a transfer the leave rule refuses instead of deferring it,
with a retry ceiling of eight ticks **[opened]**
(`src/Puck.World.Schema/WorldSessionResolver.cs:65`):

```csharp
    public const string GlobalScopeKey = "global";
```

and (`src/Puck.World.Server/WorldInstanceHost.Transfers.cs:613-614`):

```csharp
            if (!standing.Allowed) {
                targetAuthority.Abort(
```

and (`src/Puck.World.Server/WorldInstanceHost.cs:75`):

```csharp
    private const int TransferRetryCeiling = 8;
```

So population layers need layer ordinals under one destination in the resolver
and the durable identity, and a deferral queue in the transfer door, before the
unobservability predicate has anything to gate (Argon 3). The hop cooldown is
kept by the layer set's authority per identity subject and never read from the
mobility record, which the source authority writes, as every reservation field
is; a party's hop is gated on its slowest member's stamp
([B11](synthesis-attacks.md#b11-the-hop-stamp-lives-on-the-attackers-record)).
Interest management maps onto the tiers as lane C states: `Frames` is the
camera; `Presentation` is the view's rows plus a body set under radius,
hysteresis and cadence tiers; `Replica` is the full disclosed document for
trusted shadows ([seams, question 5c](seams.md#question-5c-how-interest-management-maps-onto-disclosure-tiers))
**[record]**. I1's check must target A16's two defects and not what `Radius`
already does: under the default `All` an unembodied spectator receives every
body, and under `Radius` an observer anchored to a window or a camera receives
none, because `Redact` anchors only to a body **[opened]**
(`src/Puck.World.Schema/WorldObserverDisclosure.cs:12-13`):

```csharp
    /// <summary>Only bodies within <see cref="WorldObserverDisclosure.Radius"/> metres of the observer's own body,
    /// plus that body itself. An observer with no body of its own is delivered nothing.</summary>
```

and (`src/Puck.World.Server/WorldOutputHub.cs:374-375`):

```csharp
        var observerIndex = disclosure.ObserverBodyIndex;
        var observerPosition = Vector3.Zero;
```

(Argon 3 and 12). The deployed debug world authors `Replica` for `*`
**[opened]** (`worlds/debug/debug.puck:82-83`), deliberately, so a returning
traveller's windows observe it whole; that row is the A14 footgun and belongs
on the checklist of any world that admits strangers.

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

**Favoured.** 6b first. The rule "a table's authority is never a seated party,
and a non-seated authority controlled by a seated player's account is seated"
is the standard casino's trust policy and the precondition of its hidden-hand
guarantee, never an engine restriction: an open-hand, score-only game runs on
any host, 6c stays a candidate, and "authors rule" puts a rule of play in the
author's program (Codex R8). What the engine provides is the mechanism that
makes the policy checkable: the settlement door accepts a hand's terminal
decision only from a table authority the domain operates or has vouched as a
table role, named in the stake reservation before the hand
([decision 3, neutrality](#decision-3-custody-of-owned-things)). Without that
grant "no seat controls the dealer" is prose no reader can verify, since
nothing in `src/` binds an authority to an account or marks one neutral
([B3](synthesis-attacks.md#b3-the-table-authority-settles-the-hand)).

**The seat's view.** The first revision moved the host's view of a table from
the dealer to the room without noticing. A screen in a player's room observes
a nested destination as the room's authority, into the room's mirror
**[opened]** (`src/Puck.World.Server/WorldInstanceHost.ScreenSessions.cs:292-295`):

```csharp
        if (target!.Server.TryObserveAsSession(
            refusal: out var refusal,
            sink: session.Mirror,
            sourceAuthority: owner.Server.AuthorityIdentity
```

so a seat's permitted cards either pass through the host's process or never
reach the seat, and the room's screen cannot be the casino. A seat's private
view is a session the seat's own authority opens with the table (the player's
own process is an authority, so the mechanism exists; what is new is a screen
composed per viewer rather than per room), delivered to the seat's client by
the table and never through the room; the room's screen shows the table's
public tier. This is a step of its own in the order of work, and the 6b check
is rewritten around it: the room host's mirror of the table holds no cell of
any seat's hand while each seat's own client renders its hand
([B22](synthesis-attacks.md#b22-the-room-host-as-relay)).

Stakes are reservations at the settlement door
([decision 3](#decision-3-custody-of-owned-things)), all of a hand's inputs
reserved under one domain, frozen with roster, buy-ins, rules identity, the
table authority's identity and recovery policy before a hand starts, with one
terminal decision per hand id ([economy, stakes](economy.md#the-hypothesis-stakes-as-escrowed-custody))
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
as outside replay determinism **[opened]** (`src/Puck.World.Server/WorldAdjacencyContactField.cs:28-30`):

```csharp
/// <para><b>Replay boundary.</b> Neighbour poses presently arrive as presentation-float snapshot fields and are
/// converted to fixed point below at delivery timing. Ground and dynamic-contact correction are therefore not yet
/// replay-deterministic across network schedules.
```

Lane E found the missing per-tick cap. The seam clamp now admits a
neighbour's body pushes and terrain sweep through one per-body budget and keeps
them behind the body's ownership faces **[opened]**
(`src/Puck.World.Server/WorldAdjacencyContactField.cs:25-30`):

```csharp
/// <para><b>Untrusted neighbours.</b> A neighbour's bodies and solid geometry move a local body only through
/// <see cref="WorldPopulation.AdmitAdjacencyResponse"/>, which bounds their combined displacement per body per tick by
/// that body's locally owned reach and keeps it, and the body's outward speed, behind every ownership face the body is
/// still owned behind: a crossing comes from the body's own motion. A query naming no local body takes nothing from a
/// neighbour. Body contacts are settled by one responder per authority pair
/// (<see cref="WorldCrossAuthoritySettlement"/>).</para>
```

Tick-addressed neighbour records remain the
[replay packet](../open-items.md#transfers-federation-and-server).

**Options** ([seams, options](seams.md#3-options-for-puck)).

| Option | Honest between two player hosts | Feel | Hidden state safe | Scale | Attacks |
|---|---|---|---|---|---|
| C1. Co-location onto an authority neither combatant controls (a silo or a vouched referee world), bound before the first swing | Yes | Client-server round trip | Yes, on disclosure step 1's view | A single authority's budget | A3, A4, A5, A6, A8, A10 |
| C2. Mutual deterministic simulation: both peers step the encounter over signed per-tick inputs with rollback, compare state hashes, and a lazy referee replays the tape on a mismatch | Yes, with a witness for stalls and disputes | Best for 1 to 8 peers | Only by commit-reveal; both peers hold the arena | Stalls on the slowest of N | A3, A5, A6 |
| C3. Clamped, tick-addressed seam contact plus signed cross-seam proposals the target's authority applies under its own rules | Only between authorities that honour rules on their own bodies | One round trip per cross-seam effect | Yes | The border band | A10 and the replay boundary; nothing against a host that makes its body immortal |

**Favoured.** C3 as a defect fix, with what it closes and leaves stated
exactly. The clamp closes the launch and the forced handoff by one correction:
a neighbour's reported collider sizes, poses, body counts and speeds cannot
enlarge the local body's reach budget. The same door
(`WorldPopulation.AdmitAdjacencyResponse`) bounds the neighbour terrain sweep
in that one budget, and keeps every neighbour correction, and the body's
outward speed, behind each ownership face the body is still owned behind, so
repeated bounded corrections cannot herd a standing body across the deadband.
The pair hash reads the two authority identities alone, never a generation
the neighbour chooses (lane E's "a neighbour can choose generations that
assign a pair to the local side", [puck-today, premise verdicts](puck-today.md#premise-verdicts)
**[record]**, Codex R7, [B14](synthesis-attacks.md#b14-pinned-at-the-seam),
Argon 4). It does not prove a reported body exists, bound how long a visitor
is held by phantom bodies reported every tick (no decay rule), keep a
neighbour from carrying a still body toward the plane up to its threshold, or
stop a neighbour field's refusal pinning a body, and the remote identity stays
a claim until attested. So A10 is narrowed, not closed, until those land.
Taped neighbour records then close the replay boundary. C1 as the rule for any contested
place, which is the architecture page's existing rule, and the owner's "do not
run an authority boundary through a place designed to be contested"
**[opened]** (`docs/architecture/worlds.md:1413-1414`); the referee world is a
vouched role ([decision 3, neutrality](#decision-3-custody-of-owned-things)).
C2 as the Puck-native prototype for duels between friends, which no float-based
MMO can run, stated with what it removes: the host's simulation advantage, and
not the referee. Its peers must agree one history before a replay can name a
liar: inputs are either lockstep-committed (a hash commitment per tick, then
the reveal, so no peer reveals tick T before holding the other's commitment)
or ordered by a witness that signs receipt times and silence; an input past
the agreed horizon is refused and the held input substituted; a stall is
attributed by the witness and forfeits a match-entry reservation at the
settlement door; and a hash mismatch is "an invalid history" until the
referee's replay names the peer whose signed hash disagrees with the
deterministic run (Codex R6; [B12](synthesis-attacks.md#b12-the-c2-witness-has-no-step);
Argon 6; `economy.md:645-650`, "Evidence of agreement on one history ... A
signature alone proves no delivery time" **[opened]**). The witness is a
vouched role and a step of its own. Without one, two friends on a LAN duel
under a prior agreement that a stall is a draw. C2 cannot hold hidden state,
and its threat model (lag switches, deliberate desync) is inferred, not
documented ([seams, citation check](seams.md#7-citation-check)) **[record]**.

**Strongest case against C1.** It makes every trustless fight depend on
reachable neutral capacity and adds a hop; two friends on a LAN cannot duel
without it. C2 is the answer for that case and nothing larger, and only with
a witness or an agreed draw on stall.

**Owner call 13.** May two player hosts ever fight raw PvP without a vouched
witness, accepting that a stall or disconnect is then a draw by prior
agreement and nothing settled rides on the fight, or does every fight with
stakes or durable progress run on a vouched authority?

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
rotation must not revoke historical ownership **[record]**. A revocation entry
carries a not-after instant, so a key stolen on a date revokes what it signed
after that date and keeps the honest history before it; and a claim's
acceptance time is the receiver's recorded tick-boundary decision, never the
claim's own date, so a thief cannot back-date past the revocation
(`custody.md:459-460`, "a fresh signature after compromise cannot be made old
merely by claiming an earlier timestamp" **[opened]**;
[B10](synthesis-attacks.md#b10-the-snapshots-three-edges)). External status
becomes a recorded decision at a tick boundary so replay does not refetch
today's status to reinterpret yesterday's acceptance **[record]**. A stale
snapshot refuses new valuable actions (spends, stakes, title moves), never all
actions, so that a delivery failure degrades a world rather than stopping it.

**Strongest case against.** A snapshot is a bounded attack window by
construction, and the snapshot's delivery is a single point of denial for every
world in the domain: a world that fails closed on a late snapshot turns one
delivery outage into a domain outage. The owner names the window knowing that
trade; nothing closes it under partition while allowing unlimited offline
high-value use.

**Owner call 14.** How long may a world keep accepting new valuable actions on
a refusal snapshot older than the ban delay the owner tolerates, knowing that a
shorter window makes the snapshot's delivery a single point of denial for
every world in the domain?

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
of a body is the custody protocol's lease-missed recall
([decision 3, recall](#decision-3-custody-of-owned-things)). A lost local disk
reconciles from the surviving settlement authority; where no trusted record
survives, the rights are unresolved, never minted again from an image
**[record]**. What a player host may grant follows from the composition and
the first revision left it unsaid: its awards are claims in its own namespace,
which count where a reader's policy accepts that host, so by default in that
world and in worlds its author trusts, and never in the main game's gates; a
player-hosted world that feeds the main game does so through a budget and a
vouching, both the settlement domain's grants
([B15](synthesis-attacks.md#b15-a-player-host-decides-nothing-scarce-and-nothing-gated)).

One prerequisite for any check that seats a remote player in a spawned place
(the garden, a table room) is unbuilt today: a network peer can address only
the boot instance, and the debug world's admission rows are `FederatedAuthority`
only (`mode: FederatedAuthority` at `worlds/debug/debug.puck:96`,
`worlds/debug/garden.puck:50` and `worlds/debug/night.puck:44`), which the
admission door refuses for a direct peer join; the routing comment reads
**[opened]** (`src/Puck.World/WorldInstanceCommandModule.cs:30-31`):

```csharp
/// <para>A network peer entering a spawned instance — composing the existing peer-admission door with this seating
/// seam — is not built: the seam is <see cref="Server.WorldServer.ApplySession"/>
```

That routing is a step in the order of work before the table checks (Argon 8).

**Strongest case against.** 10b limits what a player-hosted world can be: it
cannot run its own economy. That is the owner's call, and 3d (forkable
personal worlds) is the honest name for a host that wants to.

**Owner call 15.** May a friend's machine be a destination for a stranger's
body at all; if so, is it the rule that nothing scarce is decided there; and
is it acceptable that what a player-hosted world grants counts only where a
reader trusts that host, so that player-hosted content feeds the main game
only through a budget and a vouching the main game's domain grants?

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
| Currency and stackable goods | Issuer-kept record moved by escrow; units are created only at the settlement door, and an award by any other authority is a unique redemption against a prefunded budget the door delegated (3c) | Amounts split and merge; one body per incarnation says nothing about supply; a signed balance is ruled out by the attestation reference; an award id deduplicates a retry and not a re-issue, so who may cause creation must be bounded | Closes A1 and A7 double-spend at that issuer and B5's minting by a non-issuer; A8 needs durable consumed ids; issuer inflation is a trust decision; a right never crosses domains until owner call 4 |
| Unique tradeable items | Issuer-kept record (title at the issuer) moved by escrow and custody; tradeable or bound by default is owner call 7 | A sale changes the owner, which companion custody never does; craft, split, destroy and restore must consume prior rights | Same as currency; A7 when crossing uniqueness is enforced |
| Table stakes | A reservation at the settlement door, all of a hand's inputs under one domain, one terminal decision per hand, accepted only from the vouched table authority the reservation names | A partial landing is wrong for an all-or-nothing wager; recall cannot refund a committed loss; a mixed-domain hand has no atomic commit | Closes A3's refund-by-restart, A7's reuse and B3's self-vouched dealer; not A4 or A5 |
| Reward paid for a claim (a first clear's chest, a quest's gold) | Signed evidence plus an issuer-kept redemption id, consumed once at the door within a named redemption domain; a deliberately repeatable reward is a new authorised event | Evidence is copyable and harmless; the payment is not, and two places that both accept one clear would each credit it (`custody.md:577`) | Closes R3's double redemption and B21's refund re-run; reusable access stays in the progress row |
| Appearance | Unsigned, cosmetic only; an issuer-kept entitlement reference when scarce or sold | Ordinary authored looks are freely copyable; exclusive custody of them adds outages and protects nothing | Leaves A1 within a non-economic scope by choice |
| Progress a world gates on (clears, unlocks, levels, experience) | Signed claim with provenance from the world that granted it, with an issuer-qualified predicate and a sequence; whose record a level is (one issuer's, a filtered per-world claim, or the player's own) is owner call 8 | A copied non-exclusive claim for the same subject is harmless; a reader applies its own policy per issuer | Closes A1 and A2 for what others issue; an authorised issuer can still lie within its predicate; A9 |
| Progress nothing gates on | Unsigned, cosmetic only | Nothing to protect | Leaves A1 by choice |
| Reputation, two kinds | Collections and achievements: signed claims per issuer, kept per issuer, merged monotonically (best value, union) by the reader over the issuers its policy accepts, never by the document; current standing: an issuer-versioned claim the reader replaces by the newest version per issuer, with a freshness rule, so a decrease takes effect; never spendable | The studied merge is one operator over its own characters (`persistence.md:138-140`); across issuers a best-value merge hands the value to the least trusted issuer, and a mandatory maximum lets a player keep an old high claim against a penalty | As progress; B7 and R9 closed by the reader's filter and the version rule |
| Machine save RAM | Unsigned, forkable personal data; a competitive result exists only when the run executes on an authority the player does not host, which issues the claim | A save is legitimately forkable; a tape the player signed proves nothing, since replay proves execution only when someone other than the authority holds the inputs | Local persistence addresses crash A3; B18 closed by moving the run, not by signing it |
| Companions | Issuer-kept (home's title) moved by custody, as built; a scarce companion's title and the protocol's home role both move to the settlement domain, the player's host becomes a custodian, and recall is the issuer's decision at the owner's request | The protocol fences bodies; home is the player's host, so trust stops there (A7); a title at one authority and a recall at another is one right split in two (B17) | Closes a sender's forgery; leaves home forging until the title moves |
| Characters | The identity document is the player's unsigned data, holding references to issuer-kept records, the signed claims and the receipts it carries; the body is a titled incarnation under the custody protocol (H14); where a settlement domain requires single presence, the active seat is that domain's right, consumed at landing and released at departure; outside a domain parallel presence is accepted and earns nothing settled | A human seat has control, reconnect and admission rules a companion lacks; a home that titles its own body is A7's attacker, so one seat across hostile homes cannot be home's rule (B4, R5) | A1 for the document's own numbers until every gated number is one of the classes above |

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
over the challenge **[inference]**; the abstraction wants a signature. A role
(a settlement issuer, a table dealer, a referee, a witness, the holder of a
delegated budget) is a grant from a settlement domain to a namespace, recorded
as data the door checks, never a property a namespace asserts about itself;
the abstraction never assumes that a second namespace is a second person
([decision 3, neutrality](#decision-3-custody-of-owned-things)).

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
outcome per operation id and the outbox that propagates it; that only the door
creates units, and any other authority's award is a unique redemption against
a budget the door removed from circulation first; that every terminal decision
is a signed receipt (issuer, subject, operation id, prior and next version,
outcome) delivered to each counterparty and held by it on a row that refuses
when full and never evicts, or pulled from the door when the holder is offline
(the silo hosts world rows and no account catalog, so there is no silo-side
inbox to deliver into); that an operation's inputs all belong to one domain, a
mixed-domain operation being refused by name; and that economic decisions,
consumption and refusal high-water marks live outside gameplay rollback, so a
restored store reconciles against its counterparties' receipts or withdraws its
namespace. Never assumes a signature is a debit, that an award id deduplicates
anything but a retry, or that the domain's operator is honest: equivocation is
detectable from two receipts for one right and version, never prevented, and
the loss policy is the owner's ([owner call 6](#7-owner-calls)). Today: none.

**Deadlines** (new; [F7](synthesis-attacks.md#f7-a-missing-abstraction-deadlines)).
Guarantees that a deadline on a right (a stake reservation, a custody lease, a
C2 input horizon, a table's timeout) is declared passed by the one authority
that owns the right, as a recorded decision taken at its own tick boundary from
a host clock it trusts, delivered to the simulation as an input and replayed
as data; that a party never declares its counterparty's deadline; and that the
record names which clock may not serve ("Simulation ticks on a paused player
host cannot serve as a shared economic deadline", `custody.md:415-416`;
"Penalties cannot safely depend on an opponent's unsupported assertion of a
timeout", `economy.md:634-636`) **[opened]**. Never assumes wall clock in
simulation state (rule 4) or a timeout as proof of abort. Today: the transfer
escrow's lease on the destination's own clock for bodies, and nothing for
value.

## 4. Puck-native combinations

What Puck can do that a server-authoritative MMO cannot, weighed against the
failure modes the records found.

| Combination | What it buys | Where it fails |
|---|---|---|
| Characters and players as worlds | A character is a document with the same row-by-row admission and conversion the world image gets, so one mechanism serves both ([persistence, combinations](persistence.md#combinations-puck-is-placed-to-make)); a league is a world the identity enters and leaves, and the merge is write-back; a settlement world is a world chosen per community | The document is the player's assertion until every gated number is a signed claim or an issuer's record (A1); a human seat across hostile homes needs an authority neither controls (I3) |
| Portals and hidden seams | A hidden seam does not need loose write rules: FFXIV's visit is invisible at the seam and strict at the economy; a hidden seam crosses a rendering or simulation boundary without moving spend authority, so not every step is a transaction ([economy, question 4](economy.md#question-4-and-attack-question-1-classes-of-value)); a frameless portal is authored, not built | A seam that moves under a live player; a seam hopped to farm; a seam that lets a neighbour pin or herd (A10) until step 2's residual rules land |
| Derived products as caches | Stats derived from template plus instance values are never stored, so a squish is a template edit; meshes, projections and images regenerate from inputs | Custody, consumed value, receipts, commitments and unrevealed secrets are evidence, never caches; a restored image must ask for current rights before it may act ([custody, combinations](custody.md#puck-native-combinations-and-remaining-attacks)) |
| Behaviour as authored `.puck` programs | Conversions, merges, the value-class table, wagers, side pots, rake, timeout and custody rules are the author's; the engine keeps transactions, verification, deadlines and receipts generic | A conversion that is wrong is a whole-population defect; a program's semantic identity must be pinned so a later edit does not resettle an earlier hand ([economy, fairness by replay](economy.md#the-hypothesis-fairness-by-replay)) |
| Deterministic replay of a fixed-point simulation | A tape reproduces an authority's run; a visitor holding its own inputs and the destination's signed snapshots holds something like RS3's telemetry for its own session | It proves something only when someone other than the authority holds the inputs; it cannot prove omitted inputs never existed, pick the honest fork, keep a hand from its host, or decide who wins a disconnect |
| Mutual simulation with hash comparison (C2) | Two peers each simulate the whole encounter and check the other's hash; once the peers hold one agreed input history (lockstep commitments or a witness's ordering), a mismatch is an invalid history a referee's replay attributes to the peer whose signed hash disagrees; no float-based MMO can do this | 1 to 8 peers; no hidden state in the slice; a vouched witness for stalls and ordering, or a draw on stall by agreement; a late or equivocated input is a dispute about history, not about the simulation; a threat model inferred rather than documented |

## 5. How the debug world proves each favoured option

The smallest check per decision, headless on a fresh `--state-dir` unless the
check is about reusing one, with what must be authored or built before it can
run as described. Each is a step another lane takes; none ran for this memo.
The reviews found the first revision's "author first" column short in eleven
rows; every row below names its prerequisite or states what exists.

| Decision | Smallest check | Decides it | Author or build first |
|---|---|---|---|
| 2 (2b) | Create a character whose template carries a tunable value, quit, edit the template's value, relaunch; `identity.state <id> <row>` reads the new value and the identity file's bytes are unchanged | Tuning is no longer copied into the character | Identity recomposition against the template on load ([step 6](#6-the-order-of-work)); today `Seed` copies the template whole and `TryLoadOwned` reads the copy |
| 2 (2c) | The garden renames a row and ships a conversion that maps it and refunds a removed row; set both, quit, edit, relaunch, travel: the new row holds the mapped value, the narration names the conversion, a second relaunch applies nothing, `puck test --reproduce` agrees; when the refund is a settled class, it reads at the settlement door under (identity, conversion id) and a document whose version is edited back gets the prior outcome | A conversion runs once and replays; a refund cannot re-run | The conversion phase before per-row admission (step 6); the settlement door for the refund half (step 9) |
| 2 (set-aside) | An identity gains a row the template no longer declares; the catalog loads it, drops that row by name, keeps the rest; `unloadable/` stays empty | One bad row no longer costs the character | Per-row admission for identities (step 6); today the whole file is quarantined |
| 3 (allocator) | Cross the garden seam; restart after each durable step and retry the same operation; one terminal outcome per operation, monotonic epoch, no duplicate credit, pending obligations retained | Epochs survive restart | H7's desktop log and image (step 7) |
| 3 (fence at effect) | Pause the old custodian after it prepares an effect; install the next grant; resume: the effect door rejects the old epoch | The comparison is at commit, not before | The settlement door (step 9) |
| 3 (seat) | A scripted hostile home confirms one incarnation to two destinations in one settlement domain; the second landing's seat consumption is refused by name, the first destination's awards settle, the second's are refused, and both destinations' receipts name the one seat | The seat is the domain's right, so home cannot farm in parallel | The settlement door with the seat right (step 9); the first revision's A7 check tested a door the favoured option did not give the right to |
| 3 (budget) | A zone with a delegated budget awards the same kill a thousand times under fresh ids: the door credits until the budget is exhausted and refuses the rest by name; a zone with no budget awards a claim and no currency | Only the door creates units | The settlement door (step 9) |
| 3 (receipts) | Two seats complete an operation at the door; each `identity.state` shows a receipt naming issuer, subject, operation id, versions and outcome; a scripted issuer emits a second receipt for the same right and version; a reader holding both reports equivocation and refuses the issuer's later references | Equivocation is detectable from receipts | The settlement door and the receipt row (step 9) |
| 3 (restore) | Complete a spend at the door, restore the door's store to before it, and relaunch: the door refuses to accept new operations until it reconciles against the counterparties' receipts or withdraws the namespace; the spent right does not reappear | Economic state sits outside gameplay rollback | The settlement door's restore contract (step 9) |
| 3 (domain) | A hand whose two seats reserve under two settlement domains is refused by name before play; the same hand under one domain starts | No scarce right crosses domains | The settlement door (step 9) |
| 4 (4b) | A rule in a world of a second package writes a debug-package fact onto a visitor through the rule effect: `TrySetFact` refuses it by name at the tick it fires and admits the world's own-namespace fact; the night field and the garden, one package, are shown sharing a namespace | One authorisation rule on the write door rules use | Rules in the test worlds (the night field and the garden author none today); a second package; the rule on `TrySetFact` and `Decide` (step 4) |
| 4 (4c) | A currency fact marked home-scoped in the standard template; a garden rule decrementing it on a visitor is refused at the write door at the tick it fires; it reads unchanged on return | Home scope holds abroad | The class marking on the template; a garden rule; step 4 |
| 4 (4f) | Travel to the garden, let a rule award a fact, inject a save refusal, then kill the process before return, relaunch: the receipt named the award uncertain, not accepted, and the relaunch resolves it one way; without the fault the fact is present and a replayed submission returns the prior award id with the count at one | Progress survives the host, in process; an acknowledged award is on disk | A garden rule; the durable award commit (step 5); the across-machines half waits for step 14 |
| 5 (S1) | Two layer instances of one global room with an interactable prop; start a channel and trigger a layer move: it defers until the channel ends; a second hop inside the cooldown is refused by name, and a home that resets its mobility record is refused all the same | No mid-cast vanish; no hop farming | Layer ordinals in the resolver and durable identity, a deferral queue in the transfer door, the authority-kept cooldown (step 15) |
| 5 (F1) | A `threshold` template beside the gallery paintings; walk through it; `world.portals` and a crossing screenshot show no frame, and the glass sits inside the aperture face rather than proud of it | A frameless portal is authored | The template |
| 5 (I1) | `population.disclosure` at `Radius 15`, 64 autonomous bodies crossing the radius, one spectator anchored to a window grant, one unanchored spectator and one embodied player: the anchored spectator receives the bodies within 15 m of its window; the unanchored one receives none under `All` as well as `Radius`; a body oscillating 0.1 m at 15.0 m does not flicker; `world.budget` shows redaction cost scaling with nearby bodies | A16's two defects closed; hysteresis; cost | Disclosure step 1 (step 1) and the window anchor (step 16) |
| 6 (seat view) | A player hosts the room; two remote seats sit at a table world no seat hosts; the room host's mirror of the table contains no cell of any seat's hand while each seat's own client renders its hand; changing one seat's hand changes no observation in the room's mirror (the disclosure harness) | A seat's view never passes through the room | A table world no seat hosts; disclosure step 1; remote seating into a spawned place (step 11); per-seat sessions (step 12) |
| 6 (stakes) | Two seats reserve chips; interrupt before and after each durable decision; retry and restore the player copy: available plus reserved plus settled is conserved, one terminal decision, repeated collection changes nothing; a terminal decision from a table authority the reservation did not name is refused | Stakes as reservations; outcomes only from the vouched dealer | The settlement door and the role list (steps 9 and 10) |
| 6 (draws) | Commit seeds, accept the stake, then try changed seeds, reused hand ids, omitted reveals and a host-selected retry: each refused; abort resolves without a free reroll | Commit-reveal binds | Per-participant entropy on a draw site |
| 8 (C3) | Across an authored debug-to-garden adjacency, inject neighbour snapshots with a huge overlap, a fast collider, a slow phantom blocker every tick for 600 ticks, two overlapping phantoms, a solid placement inside the strip, and generations chosen so the pair hash assigns locally, against a stationary local body: total displacement per tick stays within the body's reach, the body never crosses the deadband without its own intent, the phantom's hold decays to zero within the bounded tick count, and `replay.record` then `replay.verify` reproduces the contact tick for tick | A10 closed for launch, handoff, pinning and herding; the replay boundary closed | An `adjacencies` row (none exists); the residual clamp rules and tapes (step 2) |
| 8 (C2) | Two loopback hosts step a two-body duel for 300 ticks under lockstep commitments; fault legs: one position altered by 1 LSB at tick 150, one input revealed before the other's commitment, one input sent past the horizon, two inputs signed for one tick, one peer silent from tick 200: hashes agree on every tick without a fault; the first fault is attributed by replay to the altering host; the second, third and fourth are refused as history faults before any replay; the fifth forfeits the silent peer's reservation on the witness's attestation | Mutual simulation attributes a disagreement once the history is agreed | A duel arena; the witness role (step 10); the prototype (step 18) |
| 9 (9b) | Revoke a key on a live connection with a not-after instant; replay old status data after a restart: new valuable actions stop at the policy boundary, claims the key signed before the instant stay accepted, a claim dated before the instant but received after is refused on its recorded acceptance time; the older snapshot cannot replace the newer; title stays recoverable | Revocation as data, with the not-after edge | The snapshot format and door (step 10) |
| 10 (10a) | Commit a traveller into a desktop destination; kill it; resend the same reservation and commit: refused as consumed | A8's crash case closed on a desktop | H7's log and image (step 7) |
| 11 (4d) | A federated garden signs an award under the night field's package name; the reader's issuer policy maps the garden's authority to the garden package only; the award is refused by name | A package is bound to a key | Signed submissions with the authority-to-packages map (step 14) |

## 6. The order of work

How the hub steps, the custody protocol's steps 6 to 10, disclosure and
federation reorder under the favoured options. Each step names what it needs,
what it unblocks and the check in [section 5](#5-how-the-debug-world-proves-each-favoured-option)
that shows it worked. Steps marked "new" are not in any plan today.

1. **Disclosure step 1** (landed): `WorldRecipientView` and
   `WorldDeliveryCursor`, with step 0's noninterference harness as the check.
   The floor for I1, per-seat table sessions and any claim that a silo keeps
   hidden state from a visitor
   ([disclosure, build order](../disclosure.md#the-build-order)). Body
   disclosure still runs through `Redact` at the hub until step 16.
2. **C3's residual rules and taped neighbour records** (a defect fix). The
   aggregate bound across the dynamic and terrain contributions, no
   neighbour-induced crossing of the deadband (nor added outward speed) without
   the body's own motion, and a pair hash over the two authorities alone are
   implemented (`WorldPopulation.AdmitAdjacencyResponse`,
   `WorldCrossAuthoritySettlement`). Still open: neighbour corrections may
   still carry a still body toward the plane up to its threshold, a held
   correction does not decay, a neighbour field's refusal can pin a body; then
   the [neighbour replay packet](../open-items.md#transfers-federation-and-server).
   Closes A10 and the replay boundary; check 8 (C3). Independent of everything
   else.
3. **H1 and H2** unchanged: the standard's identity template and one account
   per process ([hub-and-saves, build order](../hub-and-saves.md#build-order)).
   H2 unblocks every later step and ends in-process adoption on return, which
   is why step 4 sits on the write door and not on `TryAdopt`.
4. **The one write door, with package namespaces and home-scoped classes**
   (4b, 4c; H9 relocated): one authorisation rule on `WorldOwnedWorlds.TrySetFact`
   and `Decide`, keyed by the executing world's document and its package, with
   `TryAdopt` applying the same rule to a carried projection from another host.
   Closes A2 in process; checks 4 (4b) and 4 (4c). Needs H2, rules authored in
   the test worlds and a second package for the refusal. Unblocks the
   value-class table on the template.
5. **The durable award commit and write-through as earned** (4f, new): award
   id, credited effect and outcome in one durable write, a receipt that
   distinguishes applied, durably accepted, pending and uncertain, and a
   progress award submitted home at the tick it is earned. A settled class
   waits for step 9 and goes to the door, not home. Closes A3's crash case for
   progress in process (across machines at step 14); check 4 (4f). Needs step
   4.
6. **Identity recomposition, per-row admission and the conversion surface**
   (2b, 2c; new): the stored identity recomposed against the current standard
   identity template on load, the image ladder's row-by-row admission applied
   to identities, and an authored conversion phase that runs before admission
   drops the rows it reads; a refund of a settled class is a settlement
   operation (step 9). Checks 2 (2b), 2 (2c) and 2 (set-aside). Needs H1 and
   owner call 2.
7. **H7a to H7e, with the desktop crossing log** (10a): durable images and the
   log on any desktop that is a destination, with the A13 constraint (a
   qualified generation identity when a host both restores and spawns). Closes
   A8's crash case; checks 3 (allocator) and 10 (10a). Unblocks custody steps
   6 to 8 on a desktop.
8. **Custody steps 6 and 7, then H14, with the lease-missed recall**:
   separation, shed, retire, recall and release for companions; the recall as
   the title authority's rule when a custodian misses its lease by the
   authored margin; then the identity title and recall for characters. Step 8
   (lifetime) follows. I3 across honest homes; across hostile homes it is the
   seat right of step 9, not this step.
9. **The settlement door** (3b, 3c; new): one transactional door per domain at
   an authority the player does not host, with issuer-kept records; unit
   creation at the door alone and delegated budgets for every other award; the
   seat as a domain right; signed receipts to counterparties on a non-evicting
   row, or pulled when offline; one-domain-per-operation refusal; the restore
   contract; the frozen-door review from decision 2; the Deadlines
   abstraction's first user; the outbox and the conserved-value checks. Checks
   3 (fence at effect), 3 (seat), 3 (budget), 3 (receipts), 3 (restore), 3
   (domain) and 6 (stakes). Needs owner calls 4 to 7. Unblocks currency,
   items, stakes, the refund half of step 6 and the tables.
10. **Identity, issuance, revocation and roles as abstractions** (section 3,
    decision 9; new): the signing oracle's remote shape, binding ceilings at
    the admission door, the refusal snapshot with a not-after per entry and the
    receiver's recorded acceptance time, the role list a domain vouches
    (issuer, dealer, referee, witness, budget holder), and the provider behind
    them. Check 9 (9b). Needs owner calls 5 and 14. Unblocks any federation
    beyond loopback, any vouched role and any table with stakes.
11. **A network peer into a spawned place** (new): `WorldPeerHost` routes a
    remote peer's session into a spawned instance, and the debug world's
    admission rows gain an attested-identity row beside `FederatedAuthority`.
    Unblocks every check that seats a remote player in the garden or a table
    room.
12. **Per-seat table sessions** (new): a seat's own authority opens a session
    with the table; the table delivers the seat's private view to the seat's
    client; a room's screen observes the public tier. Check 6 (seat view).
    Needs steps 1 and 11. Unblocks 6b as a product.
13. **The tables** (6b): the vouched table role, stakes at the door with the
    dealer named in the reservation, commit-reveal on draw sites, the
    transcript policy. Checks 6 (stakes) and 6 (draws). Needs steps 9, 10 and
    12 and owner calls 10 to 12.
14. **Federation adoption as signed submissions** (4d): remote write-back built
    on steps 4, 5 and 10, signing (authority, package, fact), with the
    reader's authority-to-packages map; never unsigned adoption. Closes A3's
    crash case across machines and A2's laundering remotely; check 11 (4d).
15. **S1's layer mechanism and gate** (decision 5): layer ordinals under one
    destination in the resolver and the durable identity, a deferral queue in
    the transfer door, the unobservability predicate and the authority-kept hop
    cooldown. Check 5 (S1). Independent of step 1; needs owner call 9.
16. **I1** (decision 5): the view's spatial grid, hysteresis, the unembodied
    policy with a window or camera anchor, and cadence tiers. Check 5 (I1).
    Needs step 1.
17. **Custody steps 9 and 10**: role drivers and the debug world's faerie trip,
    as planned.
18. **The C2 witness and prototype** (decision 8): a relay vouched as a witness
    role that signs receipt times and silence, lockstep commitments or
    witness-ordered inputs with a held-input horizon, a match-entry reservation
    that forfeits on an attested stall, and the loopback duel under the five
    fault legs. Check 8 (C2). Needs steps 9 and 10 and owner call 13.

What moved, and why: the write door and write-through go before durable
images, because every write-back check needs the door and write-through
closes the larger visible loss (progress) with a smaller change than the log,
once it has a durable commit to stand on; identity recomposition is a step
because 2b and 2c were favoured without one; the settlement door grows the
receipts, budgets, seat right, domain refusal and restore contract the attack
pass showed it needs, and precedes any federation beyond loopback because
nothing scarce should cross a machine boundary until it and the identity
abstraction exist; remote seating and per-seat sessions are new steps because
no table check can run without the first and 6b does not hold without the
second; the C2 witness is its own step because the identity step it was said
to depend on builds no witness; the layer gate no longer waits on the
disclosure view, and the layer mechanism it needs is named; H14 keeps its
place after custody step 7, as the hub plan already orders it, and gives up
the hostile-home half of I3 to the settlement door.

## 7. Owner calls

Each is a product or taste question, with the engineering that follows from
each answer. Engineering and security decisions the first revision put here
(the frozen-door start, the lease-missed recall, the seam clamp's shape) are
made in the decisions above and placed in the order of work.

1. ([Decision 1](#decision-1-the-engines-purpose)) Is this the purpose, in
   these words: Puck exists to make it possible to build an MMO at World of
   Warcraft's scale that redefines the genre; the emulators, nested screens,
   tables and portals are parts of that MMO; and every design round weighs its
   question at that scale? Yes writes the paragraph and the weighing rule into
   AGENTS.md; no changes how every decision below is ranked.
2. ([Decision 2](#decision-2-player-state-across-author-edits)) When you edit
   a template or a world, does a player's saved character keep what it had,
   with a conversion you write as the only way to change what a kept row
   means? Yes builds identity recomposition, per-row admission and the
   conversion surface (step 6); no keeps copy-at-seed, so the first retune
   leaves old characters on old values forever.
3. ([Decision 2](#decision-2-player-state-across-author-edits)) How long is
   set-aside data (a dropped row, a refused image, a quarantined identity)
   kept, and who may restore it: the player, an operator, or no one? The
   answer is a retention window and either a restore verb or a deletion.
4. ([Decision 3](#decision-3-custody-of-owned-things)) Does Puck have one
   economy, many sealed economies, or exchange between them? One economy means
   one settlement domain, the studio's, and no exchange code at all; sealed
   economies mean any community may run a door and nothing it issues is worth
   anything elsewhere; exchange means a later round picks one of lane B's
   three mechanisms, and the door refuses mixed-domain operations until it
   lands.
5. ([Decision 3](#decision-3-custody-of-owned-things)) Who may be vouched to
   mint value, deal a table, referee a fight and witness a duel: the studio's
   own authorities only, operators a root vouches for, or any community that
   names its own? The answer is the role list's policy (step 10); the wider
   the answer, the more a player's trust rests on reading who vouched whom.
6. ([Decision 3](#decision-3-custody-of-owned-things)) When an accepted
   issuer is caught cheating, or loses its state for good, what happens to the
   rights it issued: frozen, honoured to the last receipt the counterparties
   hold, or void; and who bears the loss? The answer is the reconciliation the
   door runs after an equivocation proof or a namespace withdrawal (step 9);
   signatures cannot choose it.
7. ([Decision 3](#decision-3-custody-of-owned-things)) Is a scarce item
   tradeable by default, or bound to its holder unless its class opts into
   transfer? Tradeable by default gives every item record an escrow transfer
   path; bound by default builds that path only for the classes the standard
   package names, and a sale of anything else is refused by name.
8. ([Decision 4](#decision-4-value-across-trust-boundaries)) Whose number is a
   level: the main game's (one issuer's record, which visited worlds feed
   through awards and budgets), each world's own (a claim readers filter, for
   many games in one engine), or the player's (single-player worlds)? The
   standard package authors the answer; the engine supports all three.
9. ([Decision 5](#decision-5-seams)) When a player joins a friend on another
   population layer, does the move wait until they leave combat and enter a
   transition volume, or happen in place after a visible countdown with an
   increasing cooldown on later hops? The first authors corridors; the second
   authors the countdown; both need the layer mechanism (step 15).
10. ([Decision 6](#decision-6-the-tables)) Which product is the casino
    fantasy: table-local score, earned chips, purchased chips, tradeable
    stakes, or real money; and for which territories and ages? Table-local
    score removes the settlement door from the tables' path; every other row
    needs it, and the last two need counsel before the schema.
11. ([Decision 6](#decision-6-the-tables)) Is a dealer the studio vouches
    acceptable, or must a table keep its secrets from every host, the dealer
    included? The first is 6b with per-seat sessions; the second funds the
    encrypted-dealing prototype (6c) and accepts its cost in proof work and
    abort handling.
12. ([Decision 6](#decision-6-the-tables)) Must mucked hands stay secret after
    the hand? Yes forbids the simplest public transcript and keeps a redacted
    one, which is not full replay verification.
13. ([Decision 8](#decision-8-where-a-contested-place-runs)) May two player
    hosts fight raw PvP without a vouched witness, accepting that a stall or
    disconnect is a draw by prior agreement and nothing settled rides on the
    fight, or does every fight with stakes or durable progress run on a
    vouched authority? Yes keeps the LAN duel as a friends' feature with no
    stakes; no routes every such fight through step 18's witness or a C1
    authority.
14. ([Decision 9](#decision-9-revocation-below-expiry)) How long may a world
    keep accepting new valuable actions on a refusal snapshot older than the
    ban delay you tolerate? A short window makes bans bite fast and makes the
    snapshot's delivery a single point of denial for every world in the
    domain; a long window lets a banned key act for that long.
15. ([Decision 10](#decision-10-what-a-player-host-may-be)) May a friend's
    machine be a destination for a stranger's body; if so, is it the rule that
    nothing scarce is decided there; and is it acceptable that what a
    player-hosted world grants counts only where a reader trusts that host, so
    that player-hosted content feeds the main game only through a budget and a
    vouching the main game's domain grants? No to the first removes 10a's
    desktop log from the near term; yes to all three is the favoured
    composition; wanting player-hosted content to feed the main game widens
    call 5.

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
  reorders steps 9 and 13; per-seat sessions (step 12) stay, because a
  score-only table with hidden hands still cannot show them through the room.
- **The owner choosing forkable personal worlds (3d) as the default.** That
  removes most of decisions 3, 4 and 10 from the near term and makes the
  write door and write-through the whole of the value work.
- **The owner choosing exchange between economies.** That reopens decision 3
  for a round of its own over lane B's three mechanisms, each with a finality
  and deadline contract, and the door's mixed-domain refusal becomes the
  placeholder that round replaces.
- **A reproduction of parallel seating against the seat right.** If a hostile
  home can land one incarnation in two places of one domain despite the seat
  consumption at the door (a race between two destinations' landings), the
  seat right needs a landing order the door serialises, and check 3 (seat)
  gains that race.
- **An ArenaNet statement on the 2020 duplication.** Press-reported today; a
  confirmation that separate trading-post and guild-bank stores caused it
  strengthens the rule that anything moved between a realm and a character
  settles against one record ([persistence, open questions](persistence.md#open-questions)).

## 9. Review answers

Three readers attacked the first revision of this memo: Fable's second attack
pass ([attacks on the synthesis](synthesis-attacks.md): eighteen attacks
numbered B1 to B22 with B9, B13, B19 and B20 unused, eleven text findings F1
to F11, a survive-or-change table and a regrade of the first catalogue), Codex
(R1 to R9) and Argon (findings 1 to 12 with audits of sections 4 and 5). Each
finding is answered below: **accepted** names what changed in the memo,
**accepted in part** names what changed and why not the rest, and **rejected**
quotes the evidence. Every code claim a review rested on was reopened at this
tree before it was accepted; one reviewer's quotations were checked against
the memo's own Git history as well, because Argon's review quotes memo text
("Offline inbox as a bounded evicting row", "Fixed-width `InstanceSeed`", "When
does federation switch to `Puck.Attestation`?", a "Decision 7" at lines
519-546) that no revision of this file has held: `git log --all` shows two
commits touching it, and neither contains those phrases **[opened]**. Argon's
code evidence was kept where it holds and is cited as such; its claims about
the memo's text are rejected where the text never existed. Where two reviews
raised one point it is answered once with every id.

### Fable's attacks

- **B1, with F9 and Codex R1** (a settlement domain is a key; value crossing
  domains is undecided). **Accepted.** [Decision 3](#decision-3-custody-of-owned-things)
  gains "Value across domains": no scarce right crosses domains at this stage,
  a mixed-domain operation is refused by name at the door, and whether Puck
  builds exchange is [owner call 4](#7-owner-calls); the Settlement
  abstraction and check 3 (domain) carry the refusal.
- **B2, with F8, Codex R2, Argon 7 and Argon 11's dropped call** (the issuer
  equivocates or restores; no receipt, no loss contract). **Accepted.** Every
  terminal decision is a signed receipt both parties hold on a non-evicting
  row; two receipts for one right and version prove equivocation; the
  abstraction states that this detects and never prevents, and names the
  restore contract (`custody.md:97-99`); who bears the loss is
  [owner call 6](#7-owner-calls). Checks 3 (receipts) and 3 (restore).
- **B3, with F10 on call 5** (neutrality is unissued). **Accepted.** [Decision 3](#decision-3-custody-of-owned-things)
  gains "Neutrality is a grant": the door accepts a hand outcome, a fight
  outcome or a silence attestation only from a role the domain vouches, named
  in the reservation; the Identity abstraction states a role is a grant;
  [owner call 5](#7-owner-calls) asks who may mint, deal, referee and witness;
  check 6 (stakes) refuses an unnamed dealer.
- **B4, with F3, Codex R5 and Argon 5** (bodies titled at home while value
  settles elsewhere leaves A7 open for characters; the A7 check tested a right
  the favoured option withheld). **Accepted.** The active seat is a
  settlement-domain right where the domain requires single presence
  (decision 3 favoured, the characters row); step 8 gives up the hostile-home
  half of I3 to step 9; the A7 check is rewritten as check 3 (seat), with a
  landing race named in [section 8](#8-what-would-change-the-answer).
- **B5, with F2** (award ids stop a retry, not a re-issue; 3c on the wrong
  thing). **Accepted.** Only the door creates units; every other authority's
  award is a unique redemption against a delegated budget (3c re-scoped in
  decision 3, the currency row, the Settlement abstraction); check 3 (budget).
- **B6** (a package namespace is a string). **Accepted.** 4d signs (authority,
  package, fact); the reader's issuer policy maps authority namespace to
  packages, identity in process (decision 4 favoured); step 14 and check 11
  (4d).
- **B7, with Codex R9** (a monotonic merge across issuers hands the value to
  the least trusted one; a mandatory maximum defeats a penalty). **Accepted.**
  The reputation row splits into collections (per issuer, merged by the reader
  after its filter) and current standing (issuer-versioned replacement with a
  freshness rule).
- **B8, with F10 on call 8** (two options that coincide at scale). **Accepted.**
  [Owner call 8](#7-owner-calls) offers one issuer's record, a filtered
  per-world claim or the player's own number, and says why the first two
  coincide at scale.
- **B10, with F10 on call 14** (the snapshot's three edges). **Accepted.** A
  revocation entry carries a not-after instant; acceptance time is the
  receiver's recorded decision; a stale snapshot refuses new valuable actions,
  not all actions; [owner call 14](#7-owner-calls) states the single-point-of-
  denial trade (decision 9; check 9 (9b)).
- **B11** (the hop stamp lives on the attacker's record). **Accepted.** The
  cooldown is kept by the layer set's authority per identity subject (decision
  5 favoured; check 5 (S1) adds a home that resets its record).
- **B12, with F5, Codex R6 and Argon 6** (the C2 witness has no step; the
  referee assumes an agreed history; late input and selective abort).
  **Accepted.** Decision 8 states what C2 removes (the host's simulation
  advantage, not the referee), requires lockstep commitments or a witness's
  ordering, a held-input horizon, a match-entry reservation that forfeits on
  an attested stall, and "invalid history" until the replay attributes; step
  18 is the witness's own step; check 8 (C2) gains the five fault legs; the
  LAN duel is a draw on stall by agreement, in [owner call 13](#7-owner-calls).
- **B14, with Codex R7 and Argon 4** (pinned at the seam; A10 claimed closed).
  **Accepted.** Decision 8 names exactly what the clamp closes and leaves
  (phantom pinning, multi-tick herding across the deadband, the terrain sweep,
  generation grinding), carries A10 as narrowed until the follow-up lane lands
  the aggregate bound, the no-advance rule, the decay and the authority-keyed
  pair hash (step 2); check 8 (C3) gains the sustained, multi-collider, solid-
  placement and grinding legs.
- **B15, with F10 on call 15** (a player host decides nothing scarce and
  nothing gated, and the call hid it). **Accepted.** Decision 10 states what a
  player host may grant and where it counts; [owner call 15](#7-owner-calls)
  carries the third clause.
- **B16, with F10 on calls 6 and 7** (hostage bodies under "wait forever"; two
  calls that are one mechanism). **Accepted.** The lease-missed recall is
  engineering in decision 3, with the margin authored; the two calls are gone;
  step 8 carries the recall.
- **B17** (two title holders for a scarce companion). **Accepted.** The
  protocol's home for a settlement-titled companion is the domain, the host is
  a custodian, recall is the issuer's (decision 3 favoured; the companions
  row).
- **B18** (a competitive save signed by its player). **Accepted.** The save RAM
  row: a competitive result exists only when the run executes on an authority
  the player does not host.
- **B21** (a conversion's refund re-run). **Accepted.** A refund of a settled
  class is a settlement operation keyed by (identity, conversion id) and no
  document fact (decision 2 favoured; check 2 (2c); step 6).
- **B22, with F10 on call 11** (the room host as relay). **Accepted.**
  Decision 6 gains "The seat's view" with the screen path quoted; per-seat
  sessions are step 12; check 6 (seat view) replaces the 6b check; [owner call
  11](#7-owner-calls) is answerable because the seat's view no longer passes
  through the room.

### Fable's text findings

- **F1** (4f misstates lane B). **Accepted.** 4f is split by class in decision
  4: progress home as a claim, a settled class to the door, home holding the
  receipt; the record's wording is cited.
- **F2.** Answered with B5. **F3.** Answered with B4. **F5.** Answered with B12.
  **F8.** Answered with B2. **F9.** Answered with B1.
- **F4** (step 5 closes the colocated crash case only). **Accepted.** Steps 5
  and 14 each say which half of A3 they close; check 4 (4f) says so too.
- **F6** (step 12's gate does not need step 1). **Accepted.** The layer gate
  is step 15, independent of the disclosure view; I1 is step 16 and needs it.
- **F7** (a missing abstraction: deadlines). **Accepted.** [Section 3](#3-abstractions-not-providers)
  gains Deadlines, with the records' two prohibitions quoted and today's
  provider named.
- **F10** (owner calls hiding engineering or a one-sided trade). **Accepted**
  for every item: call 3 (frozen doors) is decided in decision 2 as
  engineering with evidence; calls 5, 6, 7, 8, 11, 14 and 15 are answered
  above with their attacks.
- **F11** (provider questions). **Accepted, no change needed**: the snapshot's
  delivery path stays a host data path in the Revocation abstraction's "as
  data"; the memo asks no question of a provider's configuration.
- **The survive-or-change table** ([synthesis-attacks, section 4](synthesis-attacks.md#4-which-favoured-options-survive)).
  **Accepted, with two differences.** "3a for bodies changes" is taken as the
  seat right rather than named parallel farming; "C3 clamp now survives" is
  taken, and the first-catalogue regrade's "A10 closed" is not: A10 is
  narrowed until step 2 lands the residual rules (Codex R7). The regrade's
  "A7 open for characters" closes at step 9 for domains that require single
  presence and stays open, by choice, outside any domain.

### Codex

- **R1.** Answered with B1. **R2.** Answered with B2. **R5.** Answered with B4.
  **R6.** Answered with B12. **R7.** Answered with B14. **R9.** Answered with
  B7.
- **R3** (copyable progress treated as harmless without the reward-redemption
  class). **Accepted.** The classes table gains "Reward paid for a claim":
  signed evidence plus an issuer-kept redemption id consumed once within a
  named redemption domain, with `custody.md:577` cited; B21's refund takes the
  same path.
- **R4** (write-through can acknowledge an award that never reached disk).
  **Accepted.** Decision 4 quotes `Decide`'s accept-after-narrated-save path
  and the receipt's "whether it applied" contract; step 5 introduces the
  durable award commit with a receipt that distinguishes applied, durably
  accepted, pending and uncertain; check 4 (4f) injects a save refusal.
- **R8** (dealer independence as an unconditional engine rule). **Accepted.**
  Decision 6 states the rule as the standard casino's trust policy and the
  precondition of its hidden-hand guarantee, enforced through the generic
  vouched-role mechanism, with open-hand score-only games and the 6c prototype
  kept.

### Argon

- **1** (the write door, the H9 scope, the missing rules, and 4f before 4d).
  **Accepted in part.** Accepted: the door. `WorldRuleHost.PersistIdentityFact`
  calls `WorldOwnedWorlds.TrySetFact` (through `Host.Profiles`) and never `Decide`, as quoted in decision 4
  **[opened]**, and the hub plan's H2 row ends in-process adoption on return,
  so step 4 sits on `TrySetFact` and `Decide` and not on `TryAdopt`. Accepted:
  the night field and the garden are one package (`worlds/debug`, whose
  manifest is one `WorldPackageManifest`), so a per-package H9 cannot show a
  refusal between them; the 4b check uses a second package, and the hub plan's
  own H9 check, which expects that refusal between the two, is inconsistent
  with its H9 text and is recorded under **Found**. Accepted: neither
  `worlds/debug/night.puck` nor `worlds/debug/garden.puck` authors a `rules`
  or `state` block (`grep -c` returns 0 for both) **[opened]**, so "author
  first: nothing" was false in three rows. Rejected: "launders unverified
  foreign writes into the home document under 4f before 4d". Step 5 is in
  process only (F4), where the writer is a document the same operator loaded;
  no cross-process write-through is scheduled before step 14, and the memo's
  4f favoured text never sent remote writes home unsigned.
- **2** (decision 2's mechanisms absent from the order of work; its checks
  unrunnable). **Accepted in part.** The review quotes a favoured text ("2a
  now ... 2b as standard-library authoring discipline") and checks ("Edit a
  world's schema version and add a conversion rule") that this memo has never
  held. Its code evidence holds and is adopted: `Seed` copies the template
  whole, `TryLoadOwned` loads the stored copy, an unreadable file is
  quarantined whole, and `identity.show` prints no record rows **[opened]**
  (decision 2, "What 2b and 2c need built"). Step 6 is new for those
  mechanisms; the three checks name it and use `identity.state`, which exists.
- **3** (none of the seam checks runs; step 8 "needs nothing"). **Accepted in
  part.** Accepted for S1: `GlobalScopeKey`, the ambiguity refusal for two
  running instances of one origin, the abort-not-defer leave path and
  `TransferRetryCeiling = 8` all hold and are quoted in decision 5; step 15
  names the resolver and transfer-door work. Accepted for I1: `Radius` already
  delivers nothing to an unembodied observer and `Redact` anchors only to a
  body **[opened]**; check 5 (I1) now targets A16's two defects and step 16
  builds the window anchor. Rejected for F1: the memo's F1 is not "set
  `canvasRim = 0` in `paintings.puck`", and the review's
  `WorldScreenStamper.cs:20, 42-69` with `ScreenSlabHalfThickness = 0.02` does
  not exist in that file (a search for `slab|HalfThickness` in it finds a
  "sampled screen slab" and no such constant) **[opened]**; the review's one
  surviving point, that the aperture recipe needs a `Box` face shape, is
  consistent with F1 and is quoted in decision 5. The quoted step 8 text
  ("Needs nothing beyond today's transfer door") is not this memo's.
- **4** (the terrain sweep, herding and generation grinding). **Accepted.**
  Answered with B14; the brief names the follow-up lane. The review's "2 ticks
  (8.3 ms)" is its own arithmetic over an assumed walk speed and is carried as
  an inference, not a measurement.
- **5.** Answered with B4. **6.** Answered with B12.
- **7** (the dropped issuer-failure call; "3d" assumes an honest restorer).
  **Accepted in part.** The call is restored and the receipt is in the
  Settlement abstraction (with B2). The option the review quotes ("3d.
  Monotonic authority epoch and recovery hold") is not this memo's 3d (freely
  forkable personal worlds) and is not answered as such.
- **8** (a network peer cannot enter a spawned instance; admission rows are
  `FederatedAuthority` only; the ordering of steps 7, 9 and 11). **Accepted in
  part.** Accepted: the routing is unbuilt (`WorldInstanceCommandModule.cs:30-31`,
  quoted in decision 10), the three debug worlds' admission rows are
  `FederatedAuthority` and `WorldPeerHost` constructs its authenticator with
  no trusted issuers (`src/Puck.World.Server/WorldPeerHost.cs:109`)
  **[opened]**; step 11 is new and precedes the table checks. Rejected: the
  ordering argument is against steps ("H12 at step 9", "`Puck.Attestation` at
  step 11") this memo never listed, and H14's two-Worlds-on-loopback check runs
  over federation admission, which decodes every reservation as a peer
  admission today ([the weighing](#how-the-records-were-weighed), item 1), so
  it does not wait on the identity abstraction.
- **9** (`10b` fixed-width `InstanceSeed` and check `10 (10b)` are vacuous).
  **Rejected.** This memo has no option 10b of that shape and no such check;
  its 10b is "a desktop hosts visitors' bodies, but scarce value and earned
  progress settle at issuers not on that desktop". The memo cites
  `GeneratorEngine.Streams.cs:5-7` for the fold's inputs (document seed,
  instance identity, site descriptor), and the review's own evidence
  (`seams.md:363-367`) confirms that reading.
- **10** (an "offline inbox as a bounded evicting row" evicts rather than
  refuses; the silo has no account catalog). **Rejected as a finding against
  this memo**, which has no option 6c of that shape (its 6c is a participant
  relay with verifiable encrypted dealing). Two facts the review cites hold and
  are used anyway **[opened]**: `Decide` lands a text delivery only on a slot
  or a bounded evicting row (`src/Puck.World.Server/WorldOwnedWorlds.cs:411`),
  and `src/Puck.World.Silo/` references no `WorldOwnedWorlds`; so the
  Settlement abstraction puts receipts on a row that refuses when full and
  never evicts, or has the holder pull them from the door.
- **11** (calls #7, #8 and #11 are engineering; call #2 bundles; two product
  calls dropped). **Accepted in part.** The three calls as quoted ("When does
  federation switch to `Puck.Attestation`?", the C3 clamp, log compaction)
  have never been in this memo. Accepted: the two dropped calls are restored
  as [owner calls 6 and 7](#7-owner-calls); the second is asked because it is
  a product question with an engineering consequence, and not because
  `economy.md:575-590` raises it (that passage classes unique tradeable
  equipment and does not ask about binding; the persistence record's
  soulbound and account-bound citations are the nearest evidence). Call 2 now
  states the engineering each answer implies.
- **12** (citation accuracy). **Rejected in part.** The memo cites
  `src/Puck.World.Server/WorldInstanceHost.Stop.cs:33` with its project path;
  the `GeneratorEngine.Streams.cs:5-7` citation is the `DrawSeed` doc comment
  and the memo describes it as such. Accepted: the `Redact` body-only anchor is
  now quoted in decision 5 and targeted by check 5 (I1).

---

[Plans](../README.md) · [MMO foundations](../mmo-foundations.md) · [Attacks on the synthesis](synthesis-attacks.md) · [Attacks](attacks.md) · [Persistence](persistence.md) · [Custody](custody.md) · [Seams](seams.md) · [Economy](economy.md) · [Puck as it is](puck-today.md) · [The world model](../../architecture/worlds.md)
