# MMO foundations: attacks on the synthesis

The second attack pass of [the MMO foundations round](../mmo-foundations.md).
[The first pass](attacks.md) attacked the world model as the code builds it;
this one attacks [the synthesis memo's](synthesis.md) favoured composition as
if it were built: the settlement commit and its domains, issuers, signed
claims, the classes-of-value table, hidden layer moves, neutral witnesses for
contested places, revocation snapshots, and what a player host may be. Every
world is treated as possibly hostile, including a peer that is both client and
server, two player-hosted authorities in direct contact, a dishonest settlement
issuer and an operator restoring old state. It decides nothing; the memo's
author answers each finding, and the owner rules after.

Labels follow the records. **[opened]** means the words are pasted from the
code or document at this tree, with the `file:line` the tool printed (a lead at
this revision, as every plan's is); **[summary]** means a record's summary of a
source this pass did not reopen; **[inference]** means this pass's reasoning.
Severity is at MMO scale: **[high]** changes a favoured option or leaves a
critical attack open, **[medium]** narrows one or leaves griefing with a
bounded blast radius, **[low]** is a wording, ordering or footgun finding.

## Summary

The composition holds where the memo says it holds: one transactional door for
scarce value, namespaced and home-scoped writes in process, issuer-kept records
behind references, a clamped seam, revocation as data. Where it breaks, it
breaks at the words "an authority neither party controls" and "an authority the
player does not host": the memo uses them as if something issued that property,
and nothing does. A table authority, a settlement issuer, a referee world and a
C2 witness are all namespaces under a root; the identity abstraction itself
says a namespace is never a person, so "no seat controls the dealer" and "a
community names its issuer" are claims no reader can verify and no door can
enforce. The second break is that the memo moves the host's view of a table
from the dealer to the room without noticing: a screen in a player's room
observes the table as the room's authority, into the room's mirror, so a
seat's permitted cards either never reach the seat or pass through the host.
The third is that bodies stay titled at the player's home (3a) while value
settles elsewhere (3b), which leaves the first catalogue's A7 open for
characters as parallel award farming, and the memo's own A7 check assumes the
opposite. The remaining findings are narrower: a package namespace is an
authored string that no key binds across federation; a max-merge of reputation
across issuers hands the value to the least trusted one; award-id deduplication
stops a retry and not a re-issue; the C2 witness has no step in the order of
work; and five owner calls hide an engineering call or a one-sided trade.

The five that matter most, in order: [B3](#b3-the-table-authority-settles-the-hand)
(neutrality is unissued), [B22](#b22-the-room-host-as-relay) (the room host
relays seat views), [B1](#b1-the-sock-puppet-settlement-domain) (a settlement
domain is a key, and value crossing domains is undecided), [B4](#b4-parallel-embodiment-under-3a)
(bodies at home, value at settlement), [B6](#b6-a-package-namespace-is-a-string)
(package namespaces are unbound to keys).

## 1. Code claims reopened

Every code citation this pass reopened holds at this tree **[opened]**:
`WorldTransferEscrow.HomeIdentity` refuses a peer admission
(`src/Puck.World.Server/WorldTransferEscrow.cs:1431-1432`); every decoded
reservation is `PeerAdmission: true`
(`src/Puck.World.Server/WorldFederationCodec.cs:1363`); the consumed-epoch
fence (`src/Puck.World.Server/WorldTransferEscrow.cs:2109-2111`); `Decide`'s
grant lookup (`src/Puck.World.Server/WorldOwnedWorlds.cs:307-312`); the null
binding ceilings and horizon (`src/Puck.World.Schema/WorldAdmissionDoor.cs:91-96`);
the Entra proof concatenation (`src/Puck.World.Azure/EntraWorldAuthenticator.cs:140`);
`MaximumClaimAge` (`src/Puck.World.Protocol/Codecs/WorldAttestedAuthenticator.cs:38`);
`Recall` and `Release` (`src/Puck.World.Server/WorldCrossingRecord.cs:127`,
`:136`); `InstallCrossingLog` on the silo (`src/Puck.World.Silo/WorldSiloHost.cs:1474`);
`DurableImages` (`src/Puck.World.Server/WorldInstanceHost.Stop.cs:33`); the
adjacency summary (`src/Puck.World.Schema/WorldAdjacencies.cs:144-146`); the
contact field's replay boundary and `LocalResponds` call
(`src/Puck.World.Server/WorldAdjacencyContactField.cs:25-27`, `:106-110`);
`Seed` (`src/Puck.World.Server/WorldOwnedWorlds.cs:691`); `TryAdopt`'s fact
loop (`src/Puck.World.Schema/WorldIdentity.Records.cs:129-130`);
`SubjectBindingValidity` (`src/Puck.Azure.Functions/Services/IssuingKeyOptions.cs:28`);
`Redact` (`src/Puck.World.Server/WorldOutputHub.cs:372`); and the debug world's
`Replica` row for `*` (`worlds/debug/debug.puck:80-83`). No code claim in the
memo is wrong at this tree.

Two facts the memo does not state, both load-bearing below **[opened]**. The
source of a durable-slot submission is the submitting world's own authored
document id (`src/Puck.World.Server/WorldTick.Step.cs:484`):

```csharp
                    SourceDocumentId: (Host.Document.Definition.DocumentId ?? string.Empty),
```

and a screen onto a nested destination observes as the owning world's
authority, into the owning world's mirror
(`src/Puck.World.Server/WorldInstanceHost.ScreenSessions.cs:292-295`):

```csharp
        if (target!.Server.TryObserveAsSession(
            refusal: out var refusal,
            sink: session.Mirror,
            sourceAuthority: owner.Server.AuthorityIdentity
```

where the parameter is documented as "The authority the viewer observes from"
(`src/Puck.World.Server/WorldServer.Sessions.cs:51`).

## 2. Attacks on the favoured composition

Each attack names the attacker and what they control, the steps, the gain,
what in the favoured design stops it or the gap, the severity, and what would
answer it. Attacks are grouped by the component the brief names.

### 2.1 The settlement commit and its domains

#### B1. The sock-puppet settlement domain

- **Memo section:** [decision 3](synthesis.md#decision-3-custody-of-owned-things),
  owner call 5; [section 3, Settlement](synthesis.md#3-abstractions-not-providers).
- **Attacker and control:** a party holding a few platform accounts, under
  owner call 5's third option ("any community that names one"). It controls
  a settlement world's key, its storage and its build.
- **Steps:** name a settlement issuer for a "community" of alts; mint any
  balance at its door, honestly by its own rules; carry the identity's
  references and receipts to a world in another trust community; present the
  balance or trade an item it issued for one the other domain issued.
- **Gain:** value in a domain the attacker does not control, bought with
  nothing. If instead no value crosses domains, the gain is zero and the
  attack is a harmless private economy (3d by another name).
- **What stops it, or the gap:** the memo's only answer is reader policy
  ("trusting an issuer must include what it may mint, not only whether its key
  is authentic", [decision 4](synthesis.md#decision-4-value-across-trust-boundaries))
  **[record]**. Lane B says the same and then names the three mechanisms a
  cross-domain exchange needs **[opened]** (`custody.md:587-589`): "Across
  different trust domains, realistic exchange choices are a common escrow
  authority, independent ledgers with enforceable conditional exchange, or
  deliberately limited bilateral credit", and that an HTLC-style exchange
  "cannot exchange trustworthy value when either domain can invent balances"
  (`custody.md:411`). The memo picks none, so the Settlement abstraction
  guarantees one door per domain and says nothing about two domains.
- **Severity:** **[high]** under call 5's third option if value may cross
  domains; closed by construction if it may not. The memo does not say which.
- **Answer:** a decision the round misses: may a scarce right issued in one
  settlement domain ever be accepted in another, and through which of lane B's
  three mechanisms. Owner call 5 as phrased ("who may be an issuer") hides it;
  the owner's question is whether Puck has one economy, many sealed economies,
  or exchange between them. The engineering follows from that answer.

#### B2. The dishonest settlement issuer equivocates or restores

- **Memo section:** [decision 3](synthesis.md#decision-3-custody-of-owned-things),
  "The invariant and what enforces it"; [decision 4](synthesis.md#decision-4-value-across-trust-boundaries),
  "An owner restoring an old copy restores a display; the settlement door still
  says spent" **[inference]** in the memo.
- **Attacker and control:** the operator of an accepted settlement world (the
  brief's dishonest issuer), or an honest operator restoring a backup. It
  controls the door, its log and its build, so `WorldReleaseRewindBoundary`
  and the fence are its own code to keep or drop.
- **Steps:** accept a spend and emit its outcome; restore the store to before
  the spend, or sign a second history in which the right was never consumed;
  let the holder spend again, or present two receipts for one right to two
  readers.
- **Gain:** duplication of every right that domain issues, with the issuer as
  the beneficiary or the accomplice; or, for the honest restore, the GW2
  two-stores failure the memo cites, at the one store meant to prevent it.
- **What stops it, or the gap:** nothing in the memo's Settlement abstraction.
  Its guarantee is "one terminal outcome per operation id and the outbox that
  propagates it"; it names no receipt to the counterparties and no consistency
  check. Lane B's record says what makes this detectable **[opened]**
  (`custody.md:363-366`): "Receipts held by both counterparties and
  independently witnessed logs make equivocation discoverable and recovery
  less dependent on home. They do not automatically prevent double spending
  before witnesses compare views", and (`custody.md:372-373`) "An append-only
  local file is append-only only while its operator obeys. A hostile home can
  fork it, erase it or sign incompatible histories." The memo carries the
  identity document as holding "references and receipts" but never says the
  settlement door must emit one.
- **Severity:** **[high]**. The memo calls issuer inflation "a trust decision",
  which is fair; undetectable equivocation is not a trust decision, it is a
  missing receipt.
- **Answer:** the Settlement abstraction adds: every terminal decision is a
  signed receipt (issuer, subject, operation id, prior and next version,
  outcome) delivered to each party and held in its identity document; a reader
  may demand the receipt behind a reference; and two receipts for one right
  and version are proof of equivocation. What follows (issuer removal, who
  bears the loss) is lane B's open question the memo dropped
  ([finding F8](#f8-the-dropped-owner-call-issuer-failure)).

#### B5. Award ids stop a retry, not a re-issue

- **Memo section:** [decision 4](synthesis.md#decision-4-value-across-trust-boundaries),
  favoured 4f: "an award goes home as it is earned with a unique award id so a
  retry returns the prior outcome"; [classes of value](synthesis.md#2-classes-of-value),
  currency row.
- **Attacker and control:** any world that may award currency or stackable
  goods to a visitor and submit the award to settlement: a player-hosted zone
  under 10b, or any zone under owner call 5's wider options. It controls what
  it awards and the ids it mints.
- **Steps:** award the same kill a thousand times with a thousand fresh ids;
  or award once per tick. Each is a new operation id; deduplication by id
  accepts every one.
- **Gain:** unbounded minting of an issuer-kept currency by a world that is
  not its issuer, through the very door meant to conserve it.
- **What stops it, or the gap:** the memo's currency row says the issuer
  keeps the record and escrow moves it, which answers a *transfer*, not an
  *award*. Who may cause settlement to create new units is unstated. Lane B's
  3c is the answer the memo has and places elsewhere ("3c for offline and
  table play against a prefunded budget", [decision 3](synthesis.md#decision-3-custody-of-owned-things)):
  "Settlement removes a budget from circulation before delegation; redemption
  is unique" **[record]**. Lane D's lesson is the rule **[opened]**
  (`economy.md:216-217`): "A dishonest issuer can still mint; trusting an
  issuer must include what it may mint, not merely whether its key is
  authentic."
- **Severity:** **[high]** wherever a world that is not the settlement
  authority may award a settled class; **[medium]** if only the settlement
  domain's own worlds award, which the memo does not state either.
- **Answer:** the composition states that only the settlement door creates
  units, and a zone's rewards are drawn from a prefunded budget (3c) delegated
  to that zone's authority and exhausted by unique redemption; a zone with no
  budget awards progress claims (row 5) and nothing settled. 3c moves from
  "offline and table play" to "every award by a non-settlement authority".

### 2.2 Issuers and neutrality

#### B3. The table authority settles the hand

- **Memo section:** [decision 6](synthesis.md#decision-6-the-tables), favoured
  6b: "a table's authority is never a seated party, and a non-seated
  authority controlled by a seated player's account is seated"; [decision 8](synthesis.md#decision-8-where-a-contested-place-runs),
  C1's "a vouched referee world"; [decision 3](synthesis.md#decision-3-custody-of-owned-things),
  "an authority the player does not host".
- **Attacker and control:** a seated player who also holds a second
  authenticated namespace (a second key file, a second platform account, or
  under the deployed provider a second process, since the Entra namespace is
  `"{oid}/{session}"` with a per-process session **[record]**,
  [puck-today, federation](puck-today.md#5-federation)). It controls a world
  that presents itself as a table no seat hosts.
- **Steps:** host the table under the second namespace; sit at it under the
  first; let a stranger sit; the table reports the stranger's loss to the
  settlement door as the hand's terminal decision; settlement, which accepts
  that table authority for that hand id, moves the stake.
- **Gain:** every stake a stranger brings to the table. The same move makes
  the attacker's own world the "neutral" referee of a C1 fight, or the C2
  witness that attests the opponent went silent.
- **What stops it, or the gap:** the rule exists only as prose. The identity
  abstraction's own words are that a namespace "is the only verified fact
  about the arrival" and it "never assumes a name is a key or that a namespace
  is a person" ([section 3](synthesis.md#3-abstractions-not-providers))
  **[record]**; lane B adds **[opened]** (`custody.md:584-585`) "A platform
  signature on an account authenticates the account; it must not automatically
  license that account to issue valuable dungeon facts." Lane D states the rule
  and its limit **[opened]** (`economy.md:604-606`): "A non-seated dealer
  controlled by a seated player's account is not independent. Even a genuinely
  separate dealer may collude; the product must name that trust assumption."
  Nothing in `src/` binds authorities to accounts or marks one neutral: a
  search for `seated` over `src/` finds seat mechanics (`WorldGrant.cs`,
  `SeatCommandArgs.cs`) and no account-to-authority binding **[opened]**. So
  "no seat controls the dealer" is a fact no reader can check, and the
  settlement door's acceptance of a hand outcome rests on it.
- **Severity:** **[high]**, critical under owner calls 10 to 12 beyond
  table-local score. 6b's whole guarantee is this predicate.
- **Answer:** neutrality is not a property of a world; it is a grant from the
  settlement domain. The composition states that the settlement door accepts
  a hand's terminal decision only from a table authority the domain itself
  operates or has vouched as a table role, and that the stake reservation
  names that authority before the hand (lane D's frozen roster already lists
  "rules identity and recovery policy"; the dealer's identity joins it). The
  same vouching covers C1's referee world and C2's witness. Who may be vouched
  is then owner call 5's question, which is why call 5 is the largest call and
  should say so: it decides who may deal, referee and witness, not only who may
  mint. A table that no vouched authority runs is table-local score by
  construction, whatever its fiction.

### 2.3 Signed claims and namespaces

#### B6. A package namespace is a string

- **Memo section:** [decision 4](synthesis.md#decision-4-value-across-trust-boundaries),
  4b ("a visited world writes only facts and records in its own package
  namespace") and 4d ("A2's laundering becomes visible as Bob's claim");
  [order of work](synthesis.md#6-the-order-of-work) steps 4 and 11.
- **Attacker and control:** a federated world's author (step 11, remote
  write-back). It controls its document, including its package name and
  document id.
- **Steps:** author a world whose package is the studio dungeon's, or
  `standard`; award the visitor the dungeon's clear in that namespace; sign it
  with the world's own key, as 4d requires.
- **Gain:** a fact in the studio's namespace signed by Bob. Whether a reader
  is fooled depends on whether it checks the signer against the namespace,
  which nothing in the memo says it does.
- **What stops it, or the gap:** H9's namespace is the package, and `Decide`
  checks it "against the source document's package" **[opened]**
  (`docs/plans/hub-and-saves.md:315-316`), where the source is the submitting
  world's own authored `DocumentId` (`WorldTick.Step.cs:484`,
  [section 1](#1-code-claims-reopened)). In one process every document is the
  operator's and this is sound, which is why 4b "closes A2 in process". Across
  federation the only verified fact is the authority namespace, the claim's
  subject under a root (`WorldAdmission.cs`, [puck-today, federation](puck-today.md#5-federation))
  **[record]**; a package name is not that subject and nothing maps one to the
  other. 4d as written signs the write and says nothing about signing the
  namespace or about the reader's map from authority to permitted packages.
- **Severity:** **[medium]** now (step 11 is far down the order);
  **[high]** at step 11 if built as described, because it reopens A2's
  laundering in the studio's own namespace.
- **Answer:** 4d signs (authority, package, fact) and a reader's issuer policy
  is a map from authority namespace to the packages it may write; a package
  with no authority mapped is refused by name. In process the map is identity.
  The debug check for step 11 is a garden signed under the night field's
  package name, refused.

#### B7. Monotonic merge across issuers

- **Memo section:** [classes of value](synthesis.md#2-classes-of-value),
  reputation row: "Signed claims per issuer, merged monotonically at account
  scope (best value, union), never spendable"; reason: "Every studied game
  merges account-scope state upward".
- **Attacker and control:** any issuer a reader accepts for the reputation
  predicate, at minimum the player's own worlds and any friend's.
- **Steps:** issue the maximum value, once. A best-value merge takes it; a
  union keeps it.
- **Gain:** maximum standing with every reader that merges before it
  filters.
- **What stops it, or the gap:** the cited practice is one operator merging
  its own characters **[opened]** (`persistence.md:138-140`): "The character
  holds its progress, its carried items and references to templates. The
  account holds collections and unlocks, merged monotonically (best value,
  union)." Nothing there is about many issuers. The progress row applies "a
  reader applies its own policy per issuer" and the reputation row drops it.
- **Severity:** **[medium]**; changes a row of the table.
- **Answer:** the row reads "signed claims per issuer, kept per issuer; a
  reader merges monotonically over the issuers its policy accepts". The merge
  is the reader's, after its filter, never the document's.

#### B8. Levels and experience as every world's claim

- **Memo section:** owner call 8: "are levels and experience a signed claim
  from the world that granted them, or the player's own number?"
- **Attacker and control:** any world the player visits, including their own.
- **Steps:** grant level 60.
- **Gain:** level 60 at every reader whose policy accepts that issuer for the
  level predicate.
- **What stops it, or the gap:** the progress row's "reader applies its own
  policy per issuer" **[record]**. At MMO scale the only policy that holds is
  "the studio's worlds and no other", at which point experience is one
  issuer's record (4e) and the phrase "the world that granted them" is a
  distraction: a visited world submits an award the studio's issuer applies by
  its own rule (4f) or draws from a budget (B5). The call as phrased offers the
  owner a choice between two things that are the same at scale and hides the
  third (whose record is it).
- **Severity:** **[medium]**; a mis-phrased owner call.
- **Answer:** owner call 8 asks whether levels and experience are a single
  issuer's record (the main game's), a per-world claim readers filter (many
  games in one engine), or the player's own number (single-player worlds).
  The engine supports all three; the standard package picks one.

### 2.4 The classes-of-value table

#### B4. Parallel embodiment under 3a

- **Memo section:** [decision 3](synthesis.md#decision-3-custody-of-owned-things),
  favoured "3a for bodies (characters at H14, companions as built)";
  [classes of value](synthesis.md#2-classes-of-value), characters row: "one
  seat across hostile homes is an authority neither party controls";
  [debug-world check 3 (A7)](synthesis.md#5-how-the-debug-world-proves-each-favoured-option).
- **Attacker and control:** a player hosting their own home, which under 3a
  stays the title authority of their character's body (the first catalogue's
  A7: "home is the attacker and grants itself").
- **Steps:** confirm the same incarnation into two or more visited worlds at
  once; play all of them. Under 4f each world submits awards as earned; under
  B5's budgets each draws from its own budget; under the progress row each
  issues clears.
- **Gain:** N streams of settled awards and progress for one character, bounded
  by the number of worlds the player can drive at once. Time-gated rewards
  (dailies, first clears) multiply exactly.
- **What stops it, or the gap:** the memo says 3b "closes economic A7 ... when
  every reward and spend uses the door", and its A7 check expects "at most one
  recognised use grant" at the settlement door. But under 3a the body's use
  grant is home's, not settlement's; the door sees awards for one subject from
  two worlds and has no right to compare them against. The characters row
  defers the one-seat rule to I3, "waits on H14 and step 7", and H14 makes
  home the title authority, which is the attacker. The check and the favoured
  option disagree.
- **Severity:** **[high]**; the memo claims A7's economic case closed and it is
  open for characters.
- **Answer:** either the character's seat is a settlement-domain right (one
  active incarnation per identity per domain, consumed at each landing and
  released at departure, which is I3 moved from home to settlement), or the
  composition names parallel farming as accepted and bounds it by budgets (B5)
  and per-subject rate rules the settlement domain authors. The debug check
  for A7 is rewritten to match whichever is chosen; as written it tests a door
  the favoured option does not give the right to.

#### B17. Two title holders for a scarce companion

- **Memo section:** [classes of value](synthesis.md#2-classes-of-value),
  companions row: "a scarce companion's title moves to the settlement
  authority"; [order of work](synthesis.md#6-the-order-of-work) step 7 (custody
  steps 6 and 7 at home).
- **Attacker and control:** the player's home, which keeps the custody
  protocol's grant, hop and recall while ownership sits at settlement.
- **Steps:** sell the companion at the settlement door; recall it (step 7's
  recall is home's fenced decision) from the buyer's world.
- **Gain:** the sold companion, back, with the price kept; or the GW2 failure
  the memo cites, two stores each right about half.
- **What stops it, or the gap:** nothing stated. The protocol's recall is home's
  (`WorldCrossingRecord.Recall`, [section 1](#1-code-claims-reopened)); moving
  "title" to settlement without moving the protocol's home role splits one
  right across two authorities.
- **Severity:** **[medium]**.
- **Answer:** for a settlement-titled companion the custody protocol's "home"
  is the settlement domain and the player's host is a custodian; recall is the
  issuer's decision at the owner's request, never the host's. One sentence in
  the row.

#### B18. A competitive save signed by its player

- **Memo section:** [classes of value](synthesis.md#2-classes-of-value),
  machine save RAM row: "a competitive import is a signed claim with execution
  evidence".
- **Attacker and control:** the player, who hosts the machine and produces the
  evidence.
- **Steps:** run the cartridge on a build whose tape says what the player
  wants; sign it.
- **Gain:** any score. The row describes A1 and nothing in it closes A1.
- **What stops it, or the gap:** lane B's own row is narrower **[opened]**
  (`custody.md:579`): "A competitive import needs trusted observations,
  code/input pins and acceptance rules", and lane D's replay rule: a tape
  proves execution only when someone other than the authority holds the
  inputs.
- **Severity:** **[low]**; the row over-claims.
- **Answer:** the row reads "unsigned, forkable; a competitive result exists
  only when the run executes on an authority the player does not host, which
  issues the claim".

#### B21. A conversion's refund re-run

- **Memo section:** [decision 2](synthesis.md#decision-2-player-state-across-author-edits),
  2c: "an authored `.puck` conversion, keyed by the template version the
  document was written under, with refunds and a residue row".
- **Attacker and control:** the player, whose identity document records the
  template version it was written under.
- **Steps:** after a refunding conversion runs, edit the document's version
  back; relaunch; the conversion runs again and refunds again. Or restore the
  pre-conversion copy and run it at a second home.
- **Gain:** the refund, N times, when the refund is a document fact. Nothing,
  when the refund is a settlement operation keyed by (identity, conversion
  id), which the memo does not say it is.
- **What stops it, or the gap:** 4e for currency, if the refund is written as a
  settlement operation. The memo's 2c and its debug check ("the fact holds the
  refund") write the refund to the document.
- **Severity:** **[medium]** if refunds are currency; **[low]** otherwise.
- **Answer:** a conversion that refunds a settled class emits a settlement
  operation with a unique (identity, conversion id) and no document fact; the
  check reads the refund at the door, and a second run returns the prior
  outcome.

### 2.5 Hidden layer moves

#### B11. The hop stamp lives on the attacker's record

- **Memo section:** [decision 5](synthesis.md#decision-5-seams), S1: "a
  last-hop stamp on the mobility record"; owner call 9.
- **Attacker and control:** a player whose home is the source authority of
  their mobility record, where layers are worlds of another authority (a
  silo's population layers visited from a player host), or a party of N
  accounts.
- **Steps:** hop to a friend's layer; reset the stamp at home (the mobility
  record is the source's claim, as every reservation field is: "An arriving
  traveler's body index, profile id, and display name are all supplied by the
  handing authority", [puck-today, federation](puck-today.md#5-federation))
  **[record]**; hop again. Or rotate N accounts so each hop is a first hop.
- **Gain:** the layer-hop farm S1 claims to close, at the cost of a home
  edit or N accounts.
- **What stops it, or the gap:** lane C places the stamp on "the traveler's
  mobility credential" (`seams.md:265`) **[opened]**, which is the source's.
  Within one operator's silo the source and the layers are one operator and
  the stamp holds; the memo's S1 does not say the gate is per operator.
- **Severity:** **[medium]**; griefing and farming with a bounded blast
  radius, and the fix is a sentence.
- **Answer:** the cooldown is kept by the layer set's authority per identity
  subject, never read from the mobility record; a party's hop is gated on its
  slowest member's stamp. The unobservability predicate stays the layer
  authority's, evaluated over its own state, which already holds.

### 2.6 Neutral witnesses and contested places

#### B12. The C2 witness has no step

- **Memo section:** [order of work](synthesis.md#6-the-order-of-work) step 14:
  "C2 as a bounded prototype ... Needs owner call 13 and step 9's witness";
  [decision 8](synthesis.md#decision-8-where-a-contested-place-runs),
  "Strongest case against C1 ... two friends on a LAN cannot duel without it.
  C2 is the answer for that case".
- **Attacker and control:** either peer in a C2 duel; it controls when it stops
  sending.
- **Steps:** fall behind; stop. Without a witness the stall is unattributable,
  and lane C's C2 says so: "If X stops sending inputs at tick $T$, the witness
  attests that X went silent, so X forfeits rather than causing an
  unattributable draw" (`seams.md:351`) **[opened]**.
- **Gain:** a free draw from any losing fight, which is the first catalogue's
  A3 for duels.
- **What stops it, or the gap:** step 9 is "Identity, issuance and revocation
  as abstractions": the signing oracle's remote shape, binding ceilings, the
  refusal snapshot. It builds no relay, no silence attestation and no referee.
  The dependency names a step that does not contain the thing. And a witness
  is an authority neither peer controls, which is C1's requirement in
  miniature: the memo's claim that C2 answers "two friends on a LAN" is true
  only if both accept a draw on stall.
- **Severity:** **[medium]**; an order-of-work dependency that is wrong and a
  claim that overstates C2.
- **Answer:** step 14 names its own witness (a relay that signs receipt times
  and silence, under the same vouching as B3) or states that a LAN duel ends
  in a draw on stall by both peers' prior agreement. Decision 8 says plainly
  that C2 removes the host's simulation advantage and not the referee.

#### B14. Pinned at the seam

- **Memo section:** [decision 8](synthesis.md#decision-8-where-a-contested-place-runs),
  C3's clamp; debug check 8 (C3).
- **Attacker and control:** an adjacency neighbour; it controls the bodies its
  snapshots report and the generations in their addresses.
- **Steps:** report a phantom solid body overlapping the local body each tick,
  choosing generations so the pair hash assigns the pair locally (lane E:
  "a neighbour can choose generations that assign a pair to the local side",
  [puck-today, premise verdicts](puck-today.md#premise-verdicts)) **[record]**;
  the clamp bounds each tick's displacement and refuses the deadband, so the
  body is held in place instead of launched.
- **Gain:** a visitor pinned in the strip indefinitely; a mob of phantoms
  walls the seam.
- **What stops it, or the gap:** C3's two rules bound magnitude and crossing
  and say nothing about duration or plausibility; the first catalogue already
  noted "Nothing checks that the bodies a neighbour reports are plausible".
- **Severity:** **[low]**; griefing bounded to the strip, and C3 is still the
  right defect fix.
- **Answer:** the clamp's third rule: a neighbour-induced correction with no
  local intent toward the neighbour decays to zero over a bounded number of
  ticks, and the pair hash is over the neighbour's authority and local
  address, not the neighbour's chosen generation. Both are engine mechanics
  for the C3 lane, not owner calls.

### 2.7 Revocation snapshots

#### B10. The snapshot's three edges

- **Memo section:** [decision 9](synthesis.md#decision-9-revocation-below-expiry),
  favoured 9a plus 9b; owner call 14.
- **Attacker and control:** (a) anyone who can delay a snapshot's delivery to a
  world (a partition, a hosting outage, the root itself); (b) the thief of a
  key the root later revokes, who controls the clock on the claims it signed;
  (c) a player host that applies the snapshot it likes.
- **Steps:** (a) withhold the snapshot past the age the owner names; every
  world that fails closed stops. (b) sign claims dated before the theft; a
  snapshot that names a key and no not-after time revokes them all, or none.
  (c) keep an old snapshot; admit the banned player; submit their awards.
- **Gain:** (a) a global outage from one delivery failure, the lever the
  "strongest case against" names only as a window; (b) either the honest
  pre-theft history is lost ("a rotation must not revoke historical ownership"
  **[record]**) or the thief's post-theft claims survive; (c) nothing at
  settlement, which applies its own snapshot, and a banned player who keeps
  playing on a friend's host, which 3d already allows.
- **What stops it, or the gap:** the memo's snapshot is "scoped, versioned"
  and recorded per decision; it has no per-key not-after, and the fail-closed
  rule is put to the owner as a tolerable delay without the outage it implies.
  Lane B names both halves **[opened]** (`custody.md:459-460`): "Preserve
  evidence of already finalized decisions; a fresh signature after compromise
  cannot be made old merely by claiming an earlier timestamp."
- **Severity:** **[medium]**; (c) is already accepted, (b) is a schema line,
  (a) is an owner call phrased without its cost.
- **Answer:** a revocation entry carries a not-after instant, and a claim's
  acceptance time is the receiver's recorded tick-boundary decision, never the
  claim's own date (the memo's "recorded decision at a tick boundary" already
  gives the receiver's side). Owner call 14 is re-phrased as the trade it is:
  how long a world may run on a stale snapshot before it refuses new valuable
  actions (not all actions), knowing that the snapshot's delivery is then a
  single point of denial for every world in the domain.

### 2.8 What a player host may be

#### B15. A player host decides nothing scarce, and nothing gated

- **Memo section:** [decision 10](synthesis.md#decision-10-what-a-player-host-may-be),
  favoured 10b: "nothing a stranger would fight over is decided there"; owner
  call 15.
- **Attacker and control:** none needed; this is the composition's own
  consequence.
- **Steps:** a player-hosted world grants a visitor a clear in its own
  namespace, signed (row 5). A studio gate reads it. The gate's policy, at
  scale, accepts the studio's issuers and no player host (B8).
- **Gain:** none; the loss is the product's. Under 10b plus the progress row
  as filtered at scale, a player-hosted world cannot grant anything the main
  game gates on, cannot award a settled class without a budget (B5), and
  cannot run a table with stakes (B3). What it may be is a place to visit and
  a place whose own progress counts in itself and in worlds that choose to
  trust it.
- **What stops it, or the gap:** nothing is wrong; the owner call does not say
  it. Call 15 asks whether a friend's machine may host a stranger's body and
  whether nothing scarce is decided there, and leaves out that gated progress
  is decided there and discounted everywhere else.
- **Severity:** **[medium]**; an owner call that hides its consequence.
- **Answer:** owner call 15 adds its third clause: a player-hosted world's
  awards count only where a reader's policy accepts that host, so by default
  they count in that world and in worlds its author trusts, and never in the
  main game's gates. If the owner wants player-hosted content to feed the main
  game, that is a budget (B5) and a vouching (B3), both the settlement
  domain's grants, and the call should say so.

#### B16. Hostage bodies under "wait forever"

- **Memo section:** [decision 3](synthesis.md#decision-3-custody-of-owned-things),
  owner call 6 ("May a pending title whose destination is gone for good wait
  forever ... or does the engine offer the player a recall that accepts a
  possible second body"); [decision 10](synthesis.md#decision-10-what-a-player-host-may-be),
  10a: "acknowledging only after the durable write".
- **Attacker and control:** a player host with a `*` admission row (the debug
  world's own row, `worlds/debug/debug.puck:80-83` **[opened]**); it controls
  whether it writes the log it acknowledged and whether it ever answers again.
- **Steps:** admit visitors; acknowledge each landing; stop. Or acknowledge
  without writing, then crash honestly. The source has recorded `arrived` and
  released the body (the first catalogue's A3). Under "wait forever" every
  visitor's character is pending at a destination that will never answer.
- **Gain:** a griefer traps every character that visits, at the cost of a
  world and an admission row; at MMO scale, one popular player-hosted hub traps
  every character that visits it.
- **What stops it, or the gap:** 10a's log is the attacker's file (the first
  catalogue said so); 10b makes the body economically worthless at the host,
  which is exactly why the recall's "possible second body" costs nothing
  under the favoured composition. Owner call 6 was phrased for companions
  under the unbuilt A15 fence; under 10b the trade is one-sided, and under
  "wait forever" with a `*` row it is a critical griefing attack.
- **Severity:** **[high]** under "wait forever" with open admission; the
  favoured composition removes the reason to wait.
- **Answer:** the engine offers the recall, as engineering: a title whose
  custodian has missed its lease by a stated margin is recallable by the
  title authority, and the recalled body's possible twin at the dead
  destination can earn nothing settled (10b) and issues claims only in that
  host's namespace (B15). Owner call 6 shrinks to the margin, and owner call 7
  (a home restarted without its checkpoint) gets the same recall by the same
  rule. Both are the one mechanism; the memo should present them as one.

### 2.9 The tables

#### B22. The room host as relay

- **Memo section:** [decision 6](synthesis.md#decision-6-the-tables), favoured
  6b and its check "6 (6b): A player hosts the room and sits at a remotely
  hosted table; the host sees only its permitted cards"; [Puck-native
  combinations](synthesis.md#4-puck-native-combinations), "a frameless portal
  is authored, not built".
- **Attacker and control:** the player hosting the room whose furniture (a
  screen, a portal, a nested table surface) shows the table. It controls the
  room world's process and every mirror in it.
- **Steps:** host the room; seat strangers at the remote table; read the
  room's mirror of the table.
- **Gain:** whatever the table discloses to the room's session. Lane D's
  composition expects per-seat views **[opened]** (`economy.md:608-612`): "the
  gallery and table furniture stay locally presented; a remotely authoritative
  table supplies each seat's permitted view ... A portal or screen can expose
  that table without copying the dealer's hidden arena into the room host."
  The code's screen does the opposite: it observes as the room's authority,
  into the room's mirror (`WorldInstanceHost.ScreenSessions.cs:292-295`,
  [section 1](#1-code-claims-reopened)). So the table has two choices: disclose
  a seat's cards to the room's session, which is the host's process (A4 moves
  from dealer to room); or disclose only the public tier, in which case no
  seat sees its own hand through the room's furniture and the casino fantasy
  does not render.
- **What stops it, or the gap:** nothing in the memo. Its check passes
  trivially in the second case (the host sees only public cards, and so does
  everyone) and misses the first. Disclosure step 1's `WorldRecipientView` is
  per recipient; the recipient of a screen is the room. The order of work has
  no step that gives a seat its own session onto the table from its own
  process.
- **Severity:** **[high]**; it is the first catalogue's A4 in the one place
  the memo says 6b closes it.
- **Answer:** a seat's private view is a session the seat's own authority
  opens with the table (the player's own process is an authority, so the
  mechanism exists; what is new is composing a screen per viewer rather than
  per room), delivered to the seat's client by the table and never through the
  room; the room's screen shows the table's public tier. This is a step in the
  order of work between steps 1 and 10, and the 6b check is rewritten: the
  room host's mirror of the table contains no cell of any seat's hand while
  each seat's own client renders its hand.

## 3. Findings against the memo's text

Findings on what the memo says, as distinct from what the composition does.

#### F1. Favoured 4f misstates lane B

**[medium]** [Decision 4](synthesis.md#decision-4-value-across-trust-boundaries), option 4f:
"lane B favours durable write-through for anything the UI calls earned
**[record]**", and the favoured text sends the award "home ... through
`Decide`". The record says **[opened]** (`custody.md:616-618`): "Evidence
favours durable write-through for value that the UI calls earned. It must
write to the accepted issuer/settlement authority, not merely the player's
home file." The memo's 4f writes the player's home file. For progress claims
(row 5) home is the right store, because the claim's value is its issuer's
signature once 4d lands; for a settled class it is the wrong store and B5 is
the consequence. **Answer:** 4f is split by class in the text: progress goes
home as a claim; a settled class goes to the settlement door as an operation,
and home gets the receipt.

#### F2. 3c is placed on tables and the tables use 3b

**[low]** [Decision 3](synthesis.md#decision-3-custody-of-owned-things) favours "3c for
offline and table play against a prefunded budget"; [decision 6](synthesis.md#decision-6-the-tables)
says "Stakes are reservations at the spend authority's door", which is 3b, and
lane D's stakes row is "Reservation of existing spend rights in accepted
escrow" (`economy.md:575`) **[opened]**. **Answer:** decision 3 reads "3c for
awards by any authority that is not the settlement door (B5) and for offline
play; tables are 3b reservations".

#### F3. The A7 check tests a right the favoured option withholds

**[high]** Covered by [B4](#b4-parallel-embodiment-under-3a): debug check "3 (A7)" expects
"at most one recognised use grant" at the settlement door while 3a keeps a
body's grant at home.

#### F4. Step 5 closes the colocated crash case only

**[low]** [Order of work](synthesis.md#6-the-order-of-work) step 5: "Closes A3's crash
case for progress." In process, the visited garden and home share the process
that crashes; write-through makes the earned fact durable before return, which
is real. A3 as catalogued is a destination on another machine; that case waits
for step 11 (signed submissions over federation) and step 11's text should
say it closes A3's crash case across machines. **Answer:** two words in each
step.

#### F5. Step 14 depends on a witness no step builds

**[medium]** Covered by [B12](#b12-the-c2-witness-has-no-step).

#### F6. Step 12's gate does not need step 1

**[low]** The unobservability predicate and hop cooldown are the layer authority's
decision over its own state and the destination-kept stamp (B11); only I1
needs the disclosure view. **Answer:** split step 12: the gate is independent
and can go beside step 2; I1 keeps "needs step 1".

#### F7. A missing abstraction: deadlines

**[medium]** [Section 3](synthesis.md#3-abstractions-not-providers) lists identity, issuance,
key lifetime, revocation, storage, hosting and settlement. The settlement
door's reservations, the companion leases, the C2 input horizon and every
table timeout need a clock neither party controls, and the records say which
clock will not do **[opened]** (`custody.md:415-416`): "Simulation ticks on a
paused player host cannot serve as a shared economic deadline"; and
(`economy.md:634-636`) "Penalties cannot safely depend on an opponent's
unsupported assertion of a timeout. Use an accepted ordering/witness service
or explicitly accept its absence." Rule 4 forbids wall clock in simulation
state, so the deadline is a recorded decision by the authority that owns the
right (the settlement door for reservations, the title authority for leases),
taken at its own tick boundary from a host clock it trusts, and delivered as
an input. The memo says this for revocation status and nowhere else.
**Answer:** a Deadlines abstraction in section 3: who may declare a deadline
passed for which right, how the declaration is recorded and replayed, and that
a party never declares its counterparty's deadline. Today: `LeaseSeconds` on
the destination's own clock and nothing for value.

#### F8. The dropped owner call: issuer failure

**[medium]** Lane B's open questions include **[opened]** (`custody.md:738`): "What happens
when an accepted issuer cheats or loses all state? | Owner defines liability,
issuer removal and treatment of existing assets". The memo's fifteen calls do
not carry it, and B2 makes it concrete: once equivocation is detectable, who
bears the loss and whether the issuer's outstanding records survive its
removal are product calls. **Answer:** owner call 16.

#### F9. The missing decision: value across domains

**[high]** Covered by [B1](#b1-the-sock-puppet-settlement-domain).

#### F10. Owner calls that hide an engineering call or a one-sided trade

- Call 5 decides who may deal, referee and witness, not only who may mint
  ([B3](#b3-the-table-authority-settles-the-hand)), and whether domains
  exchange ([B1](#b1-the-sock-puppet-settlement-domain)) **[high]**.
- Call 6 and call 7 are one mechanism (a lease-missed recall) whose cost the
  favoured composition removes ([B16](#b16-hostage-bodies-under-wait-forever))
  **[high]**.
- Call 8 offers two options that coincide at scale and omits the third
  ([B8](#b8-levels-and-experience-as-every-worlds-claim)) **[medium]**.
- Call 14 names a delay without the outage fail-closed implies
  ([B10](#b10-the-snapshots-three-edges)) **[medium]**.
- Call 15 omits that a player host's gated progress counts nowhere by default
  ([B15](#b15-a-player-host-decides-nothing-scarce-and-nothing-gated)) **[medium]**.
- Call 3 (frozen doors or refuse to start): every studied service froze and
  the record says to review the frozen door; this is an engineering decision
  with evidence, not a taste call, and the lane can make it **[low]**.
- Call 11 (independent dealer or secrecy from every host) is a product call,
  but it cannot be answered before B22 is, because under the code's screen
  the "independent dealer" option still shows the room host the hands
  **[medium]**.

#### F11. Provider questions

**[low]** No question in the memo is asked of one provider's configuration. The one
place to watch is decision 9's reliance on `WorldPeerHost`'s per-second
re-verification as "the door a snapshot plugs into": that is a mechanism, not
a provider, and it is right; the snapshot's *delivery* path is the thing to
keep abstract (a host data path, never a provider's feed), and the memo's
"as data" already says so.

## 4. Which favoured options survive

| Favoured option | Verdict | Change needed |
|---|---|---|
| 1a | Survives | None |
| 2b, 2c, 2d | Survive | 2c's refunds of a settled class are settlement operations, not document facts (B21) |
| 3b for scarce value | Survives as the door; changes as an abstraction | Receipts to counterparties (B2); only the door creates units, every other award draws a 3c budget (B5); a cross-domain decision (B1); neutrality as the domain's grant (B3) |
| 3a for bodies | Changes | Either the seat is a settlement-domain right or parallel farming is named and bounded (B4); a settlement-titled companion's protocol home is settlement (B17) |
| 3c | Survives, re-scoped | From "offline and table play" to "every award by a non-settlement authority, and offline play" (B5, F2) |
| 3d | Survives | None; B15 names what it is for |
| 4b, 4c in process | Survive | None |
| 4f | Survives, split by class | Progress home as a claim; settled classes to the door (F1, B5) |
| 4d (remote) | Survives, with a binding | Signs (authority, package, fact); readers map authority to packages (B6) |
| 4e | Survives | None |
| Classes table | Changes four rows | Reputation per issuer, merged by the reader (B7); progress names whose record levels are (B8); companions (B17); save RAM (B18) |
| S1 with F1 | Survives | Destination-kept hop stamp (B11) |
| I1 | Survives | None |
| 6b | Changes | Per-seat sessions onto the table from the seat's own authority; the room's screen is public tier (B22); the settlement domain vouches table authorities (B3) |
| Commit-reveal on draws | Survives | None |
| C3 clamp now | Survives | A decay rule and an authority-keyed pair hash (B14) |
| C1 for contested places | Survives | The referee is a vouched role (B3) |
| C2 prototype | Survives, honestly bounded | A witness step or an agreed draw on stall (B12) |
| 9a plus 9b | Survive | A not-after per revoked key; the receiver's acceptance time is the one that counts (B10) |
| 10a plus 10b | Survive | Owner call 15 states what a player host may grant (B15); the recall is engineering (B16) |

## 5. The first catalogue under the composition

What the favoured composition, with the changes above, does to each attack of
[the first pass](attacks.md).

| Attack | Verdict | Why |
|---|---|---|
| A1 | Narrowed | Closed for settled classes (4e) and for claims others issue (4d); open for the player's own numbers by design (row 7, appearance) |
| A2 | Closed in process; narrowed remote | Step 4 closes it in process; across machines only with key-bound namespaces (B6) |
| A3 | Narrowed | Crash case for progress: in process at step 5, across machines at step 11 (F4); dishonest case for value: 10b; hostage case: open until the recall is engineering (B16) |
| A4 | Moved, not closed | 6b moves the host's view from dealer to room unless seats have their own sessions (B22); on a vouched dealer with per-seat sessions, closed |
| A5 | Closed for public draws | Commit-reveal with taped commitments; secret dealing from a participant host remains 6c's question |
| A6 | Closed on a neutral authority; open between player hosts | C1 where the owner sends fights; C2 without a witness is a draw on stall (B12); the referee itself needs vouching (B3) |
| A7 | Open for characters | 3a keeps the body's grant at home (B4); closed for settled value at the door once awards are budgeted (B5) |
| A8 | Closed on durable hosts | The silo today; a desktop at step 6; a dishonest desktop's effects are refused downstream by the door |
| A9 | Narrowed to the window | 9a plus 9b; the not-after and the outage trade (B10) |
| A10 | Closed | C3's clamp; pinning remains as low griefing (B14) |
| A11 | Open, unchanged | The memo does not mention it; per-source shares remain the answer and a vouched namespace still costs an account |
| A12 | Open, unchanged | The memo carries only the bake-ordering rule; lease and backpressure vectors stand as catalogued |
| A13 | Carried as a constraint | On step 6, as the memo says |
| A14 | Carried as a checklist | The `Replica` row for `*` in the debug world and its siblings |
| A15 | Becomes engineering | The lease-missed recall under 10b (B16) |
| A16 | Closed | I1's unembodied policy after step 1 |

New under the composition, not in the first catalogue: the sock-puppet domain
(B1), issuer equivocation (B2), the self-vouched table or referee (B3),
re-issued awards (B5), the unbound package namespace (B6), the max-merge (B7),
the attacker-kept hop stamp (B11), the room host as relay (B22), and the
refund re-run (B21).

---

[Plans](../README.md) · [MMO foundations](../mmo-foundations.md) · [Synthesis](synthesis.md) · [Attacks](attacks.md) · [Custody](custody.md) · [Economy](economy.md) · [Persistence](persistence.md) · [Seams](seams.md) · [Puck as it is](puck-today.md)
