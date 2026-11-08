# Attacks on the world model

A critique and attack catalogue of the world model, written for a reader who
has read [the idea in plain terms](../../architecture/worlds.md#the-idea-in-plain-terms)
and nothing else. It feeds [the MMO foundations round](../mmo-foundations.md);
it decides nothing and proposes no final design. Every world is treated as
possibly hostile, including a peer that is both client and server and two
player-hosted authorities interacting directly.

Every claim is marked. **[checked]** means the claim was read from the code or
document cited beside it, at this tree; a `file:line` here is a lead a reader
reopens before relying on it, as every plan's is. **[inferred]** means the
claim follows from checked facts but was not itself observed running.
**[general]** means general knowledge about distributed systems or online
games, not a fact about Puck. Severity is at MMO scale: **critical** breaks the
economy or every player's identity, **high** breaks one world's players or one
class of value, **medium** is griefing or denial of service with a bounded
blast radius, **low** is an authoring footgun or a product question.

## The one-paragraph summary

The model is sound where it is built and dangerous where the plain-terms page
describes it as built. Custody, epochs, leases and tombstones hold for
companions on a silo with a durable crossing log. Everything else that the
plain-terms page says moves "between parties that do not trust each other" is,
today, a value the player's own machine asserts and every other machine
believes: a character's facts and records are a file the player hosts, no fact
or record carries a signature, a world a player visits writes onto the
traveller and home adopts every write, a player-hosted authority keeps no
durable record of anything and can be restarted into any past, and the
authority of a hidden-information table sees every hand. Raw PvP between two
player-hosted authorities has no mechanism at all: the one cross-authority
decision in the code is who answers a physical contact at a seam. The round's
first job is to choose, per class of value, which of the model's own answers
(issuer-kept records moved by escrow; signed claims with provenance; an
authority neither party controls) applies, because the model names all three
and the code builds none of them for any value a player would fight over.

## 1. Critique

### 1.1 Where the plain-terms page misstates or oversimplifies

**"The dungeon writes the sword back to the player's character as a claim the
dungeon signed."** No signed write-back exists. What a visited world writes
onto a traveller is adopted at home unsigned, and the architecture page says so
further down: "Facts another world wrote are unsigned: provenance attestation
for carried state does not exist yet, so an owner adopts whatever a visited
world wrote onto its traveler" (`docs/architecture/worlds.md:785-786`)
**[checked]**. The adoption door applies every carried fact and every owned
record field through the identity's own local write door and nothing else
(`src/Puck.World.Schema/WorldIdentity.Records.cs:121-152`; the catalog's
`WorldOwnedWorlds.TryAdopt` at `src/Puck.World.Server/WorldOwnedWorlds.cs:1341-1364`)
**[checked]**. `Puck.Attestation` can sign and verify a claim, and the only
claims the world code verifies today are the federation identity proof
(`src/Puck.World.Protocol/Codecs/WorldAttestedAuthenticator.cs:79-150`) and a
counterpart's border terms (`src/Puck.World.Schema/WorldCounterpartAttestation.cs:10-31`)
**[checked]**. The worked example is the design intent, and the page presents
it as the mechanism.

**"Title: whose a thing is."** Title exists for companions only: home mints a
titled incarnation (`WorldEntityAddress.Titled`), and "Players and other
untitled travellers cross as before" (`docs/plans/companion-redesign.md:41-48`)
**[checked]**. Items have no title because "the engine never learns what an
item is" (`docs/architecture/worlds.md:390`) **[checked]**. A reader of the
plain-terms page would take title and custody to cover every owned thing; today
they cover one kind of body.

**"A crash or a lost connection cannot duplicate it or lose it."** This holds
where a durable crossing log exists. The one `IWorldCrossingLog` implementation
is the silo's `RowCrossingLog` (`src/Puck.World.Silo/WorldSiloHost.Crossings.cs:12`),
and "A desktop process installs no log, so its crossings live only as long as
the process" (`docs/architecture/worlds.md:1278-1279`) **[checked]**. Every
player-hosted authority is a desktop process. The exactly-once story the page
tells is a silo property, not a property of "a friend's machine".

**"The host that holds the player's account runs it; today that is the Puck
process the player launches."** True, and its consequence is left unsaid: the
player is the title authority of their own character, so every fact, record,
claim and preference on it is the player's assertion. The identity documents
are plain `.world.json` files in the account directory, and
`WorldOwnedWorlds.cs` contains no signing or sealing of them (a search for
`Sign`, `Seal` and `signature` in that file finds nothing) **[checked]**. The
page's "nothing a host merely asserts moves it" is true of custody and false of
content.

**"Exactly one authority simulates a given body at a time."** True as a
simulation rule. The page does not say what happens when two bodies under two
authorities act on each other, which is the raw-PvP question. The code's one
answer is `WorldCrossAuthoritySettlement.LocalResponds`, a deterministic
authority-pair hash that picks which authority answers one named interaction, and it has
exactly one caller, for `"physical-contact"`
(`WorldCrossAuthoritySettlement.LocalResponds`,
`src/Puck.World.Server/WorldAdjacencyContactField.cs:106-110`) **[checked]**.
Interactions (`WorldInteraction`) are property co-occurrence rows one authority
evaluates over its own bodies (`src/Puck.World.Schema/WorldInteractions.cs:57-66`)
**[checked]**; a body in another world is never a carrier. The architecture
page's answer is co-location of the whole cluster under one authority, chosen
"at formation" by "acceptance plus a deterministic tie-break"
(`docs/architecture/worlds.md:1384-1397`, `:1444`). No formation or tie-break
code was found in `Puck.World.Server` (a search for `cluster` finds one
unrelated comment) **[checked]**: the hit-decision mechanism is unbuilt.

**Hidden information.** The page says a world "decides what an observer
receives" and never says that the authority's own host sees everything,
including every hand at a table it hosts: "Local presentation is unfiltered by
design" (`docs/plans/disclosure.md:182-188`) **[checked]**. The structural
disclosure fix is a plan whose chokepoint does not exist yet (`WorldRecipientView`
is not in the tree; `WorldStateDisclosure` and `WorldStateReadView` still are,
`src/Puck.World.Schema/WorldStateDisclosure.cs:61`,
`src/Puck.World.Server/WorldStateReadView.cs:10`) and whose harness is red at
named doors (`docs/plans/disclosure.md:797-811`) **[checked]**.

### 1.2 Where the documents and the code disagree

- **Generation ids are "recorded before they are exposed"**
  (`docs/architecture/worlds.md:1281-1284`) versus the hazards register:
  `WorldSessionResolver.m_nextGenerationId` "mints the generation ids a
  transfer continuation's checkpoint carries, and nothing captures the counter"
  (`docs/development/hazards.md:225-228`;
  `src/Puck.World.Schema/WorldSessionResolver.cs:75`, `:577`, `:758`)
  **[checked]**. The hazards page marks the consequence unconfirmed.
- **"Remote evidence carries issuer-qualified authority, document, user and
  group identities plus audience, expiry, replay protection or channel binding,
  and membership proof"** (`docs/architecture/worlds.md:739-741`). What is
  built: one directed identity claim over a challenge nonce, bound to both
  transport key hashes and the direction (`WorldFederationCodec.BindProofChallenge`,
  `src/Puck.World.Server/WorldFederationCodec.Authority.cs:10-21`), whose
  verified subject becomes the source-authority namespace
  (`src/Puck.World.Protocol/Codecs/WorldAttestedAuthenticator.cs:147`)
  **[checked]**. Group membership proof over federation is unverified by this
  pass (`membership` appears in schema and validator files; no federation-wire
  proof was read) **[unverified]**.
- **"domain ... *is* its root key's fingerprint, never a name"**
  (`docs/architecture/worlds.md:403`). For a `FederatedAuthority` admission row
  the domain "is not a key id at all: it names the authenticated
  source-authority namespace" (`src/Puck.World.Schema/WorldAdmission.cs:127-129`),
  which is the claim's subject string; for `SignsDirectly` the domain "need not
  equal their own key's hash" (`:125-126`) **[checked]**. The fingerprint rule
  is for roots; an authority namespace is a subject under a root.
- **Epochs "stay monotonic across restarts"** is the question the round asks
  (`docs/plans/mmo-foundations.md:56-59`). The destination's consumed epochs
  (`m_mobilityAdmissions`) and leases are checkpointed in the escrow section
  (`src/Puck.World.Server/WorldAuthorityCheckpointCodec.Escrow.cs:308-314`)
  **[checked]**, but a desktop host writes no durable image:
  `WorldInstanceHost.DurableImages` is "unassigned anywhere in `src/` today"
  (`docs/plans/hub-and-saves.md:289-290`; a search for `DurableImages =` finds
  no assignment) **[checked]**. On a player host, monotonicity ends at process
  exit.
- **The custody model's coverage.** The companion protocol is the model the
  round wants to generalise, and its own limits say "the player's own transfer
  beyond the cap and one free slot is not modelled" and "signed tickets are not
  modelled" (`docs/plans/companion-redesign.md:544-547`); steps 6 to 10
  (separation, shed, recall, release, lifetime) are unbuilt
  (`docs/plans/companion-redesign.md:549-554`) **[checked]**. The hub plan's I3
  (one seat per identity across hosts) waits on H14 and the protocol's step 7
  (`docs/plans/hub-and-saves.md:63`, `:1004-1007`) **[checked]**.

### 1.3 Conventions nothing enforces

- **Invariant 5 at the identity.** "The engine never adjudicates trust ...
  whether a claim *counts* is the receiving world's policy"
  (`docs/architecture/worlds.md:415-417`). At home the engine does adjudicate:
  every fact a visited world wrote is adopted, and the identity has no policy
  over which world may write which fact; the package-namespace check is H9,
  unbuilt (`docs/plans/hub-and-saves.md:64`, `:996`) **[checked]**. The
  convention "a world writes only its own facts" is held by nothing.
- **Trust at a seam.** The adjacency contact field reads the neighbour's body
  positions, orientations and colliders out of the session mirror into local
  arrays (`src/Puck.World.Server/WorldAdjacencyFields.cs:486-498`) and applies
  the resulting correction to the local body
  (`src/Puck.World.Server/WorldAdjacencyContactField.cs:119-144`) **[checked]**.
  Nothing bounds what a neighbour may report; the convention is that a
  neighbour runs the same engine honestly.
- **Trusted C# at the disclosure boundary.** The disclosure plan's own
  statement: door-by-door enforcement "does not converge, because every verb,
  sink, codec and admission path that reaches `WorldServer.Definition` or
  `WorldServer.Arena` is a door, and a new one leaks by forgetting a check it
  never knew it owed" (`docs/plans/disclosure.md:9-13`) **[checked]**. Until
  step 5's `internal` wall lands, the guarantee is review.
- **Replay as audit.** Determinism makes a tape reproduce an authority's run,
  but the authority chooses what it tapes and holds the tape. A tape proves
  something to a third party only when the third party holds the inputs
  independently **[general]**. "Fairness by replay" (the round's hypothesis,
  `docs/plans/mmo-foundations.md:118`) is a convention until the inputs are
  held by someone other than the authority.

## 2. Attacks, ranked by severity at MMO scale

Each attack names the attacker and what they control, the steps, the gain,
what stops it today or the gap, and the severity. Attacks that share a cause
are grouped; the first in each group carries the citations.

### Critical

#### A1. The self-owned character

- **Attacker:** any player; they control their own host, their account
  directory and the Puck build they run.
- **Steps:** stop the game and edit `<id>.world.json` in the account
  directory, or run a build whose `TrySetFact` accepts anything, or simply
  restore yesterday's copy. Launch. Every world the character enters reads its
  projection (facts and selected records, `docs/architecture/worlds.md:766-770`)
  and gates on it, as the port inventory's first fact-gated door does
  (`docs/plans/hub-and-saves.md:597-601`).
- **Gain:** any fact or record value the identity template declares: gold,
  level, raid clears, inventory. A stake at a table is a fact decremented on
  the identity, so a stake is free. An owner restoring an old copy duplicates
  anything spent since.
- **Today:** nothing. `TrySetFact` checks the row is a keyed int row and
  writes (`src/Puck.World.Schema/WorldIdentity.cs:497-522`) **[checked]**; the
  cloud sync's stale-writer refusal protects the pushed copy, not play
  (`src/Puck.World.Server/WorldOwnedWorldSync.cs:17`) **[checked]**. The model's
  own answer exists in prose: "Where something must stay unique or tradeable
  ... the world that issued it keeps the record itself and escrow and custody
  move it, so a copied claim gains nothing" (`docs/architecture/worlds.md:84-88`)
  and `Puck.Attestation`'s rule "Carrying mutable balances as signed claims"
  is ruled out (`docs/reference/attestation.md:291`) **[checked]**. Neither an
  issuer-kept record nor a signed fact is built for any value class.
- **Severity:** critical. Every number another player can see is forgeable by
  its owner, and every fact-gated door is a formality.

#### A2. A visited world writes your character

- **Attacker:** any world the player visits (a friend's machine, a stranger's
  hub); it controls its own authority and every rule in it.
- **Steps:** a rule or the console sets facts and records on the visitor's
  travelling projection ("A destination's `setIdentityFact` or record write
  changes that copy, and the next crossing carries it on",
  `docs/architecture/worlds.md:778-781`). The player goes home.
  `WorldTransferEscrow.AdoptHomeProfiles` calls `WorldOwnedWorlds.TryAdopt`,
  which calls `WorldIdentity.TryAdopt`, which writes every carried fact and
  every owned record field (`src/Puck.World.Server/WorldTransferEscrow.cs:1357-1409`,
  `src/Puck.World.Schema/WorldIdentity.Records.cs:129-148`) **[checked]**.
- **Gain:** a fact-laundering service ("visit Bob's world and come back with
  the raid cleared"), or griefing (zero a visitor's progress, fill their
  inventory with junk, write the studio dungeon's own namespace since H9 is
  unbuilt).
- **Today:** the adoption refuses only what the identity's own declarations
  refuse (`WorldIdentity.Records.cs:112-114`) **[checked]**; the catalog
  refuses to adopt into an identity it does not own
  (`src/Puck.World.Server/WorldOwnedWorlds.cs:1344-1348`) **[checked]**, which
  stops a destination saving a visitor, not a visitor's home believing a
  destination. The plan's I4 (a foreign document writes only through the
  identity's grants and in its own namespace) is enforced for the tick-boundary
  durable-slot path, `WorldOwnedWorlds.Decide`
  (`src/Puck.World.Server/WorldOwnedWorlds.cs:284-304`), and not for the
  projection carried through a crossing **[checked]**.
- **Severity:** critical, and worse than A1 because it reaches honest players.

#### A3. The dishonest or vanishing player-hosted destination

- **Attacker:** a player hosting a world others visit; also an honest crash.
  They control the process lifetime and the state directory.
- **Steps:** visitors arrive and play; their progress is written onto their
  travelling projections, which live at the destination while they are there
  (`docs/architecture/worlds.md:778-781`). The host kills the process (or
  loses a duel in their own world and restarts it). The desktop has no crossing
  log (`docs/architecture/worlds.md:1278-1279`) and writes no durable image
  (critique 1.2), so every committed arrival, every consumed epoch, every tombstone
  and every visitor projection is gone.
- **Gain:** the host undoes any loss it suffered as a player in its own world
  (its own body, its own stakes, its own facts are all local state); every
  visitor loses everything written since departure, because home adopts only
  on return and nothing returned. The source has already recorded `arrived`
  and released the body (`docs/architecture/worlds.md:1160-1170`), so the
  visitor has no body anywhere.
- **Today:** "What dies with a host is in-world body state only: identity and
  its facts ... live on the owned identity document, which adopts a traveler's
  carried facts when its seat comes home, so a player loses position rather
  than possessions" (`docs/architecture/worlds.md:1358-1361`). That is true of
  the facts the player left with and false of the facts earned abroad, which
  are on the projection at the destination **[checked]**. Recovery of the body
  is the custody protocol's recall, which exists for companions on paper
  (step 7, unbuilt: `docs/plans/companion-redesign.md:563-567`) and for
  characters at H14 (`docs/plans/hub-and-saves.md:1001`) **[checked]**. A
  durable log the attacker owns would stop the crash case and not the
  dishonest one: the host can delete its own log **[inferred]**.
- **Severity:** critical. Every player-hosted world is an undo button for its
  host and a trap for its visitors' session progress.

#### A4. The host sees every hand

- **Attacker:** the authority of any hidden-information world (a card table a
  player hosts); it controls the process, so it controls the shared screen,
  the console and the arena.
- **Steps:** host the table; read the arena. Nothing more is needed.
- **Gain:** every sealed bid, every hand, every hidden unit, every seed.
- **Today:** by construction the authority reads its own state; the disclosure
  plan protects recipients from the authority, never the authority from itself
  ("Who is trusted. The engine's own C# is trusted code: the authority's
  program", `docs/plans/disclosure.md:56-58`) **[checked]**. The model's own
  escape is choosing the authority: "Video remains correct wherever hidden
  information forbids handing over the tape" (`docs/architecture/worlds.md:1480`)
  presumes a trusted authority exists to render the video. For two players at a
  table neither hosts, a platform silo is that authority; for two players alone,
  only cryptographic dealing (mental poker, commit-reveal) removes the trusted
  party **[general]**.
- **Severity:** critical for the casino fantasy wherever a participant is the
  host; low on a studio silo once the disclosure plan lands (its doors are red
  today, `docs/plans/disclosure.md:797-811`).

#### A5. The authority chooses the dice

- **Attacker:** the authority of a world with draws (a loot table, a shuffle);
  it controls the document and the tick at which a draw fires.
- **Steps:** a draw samples "a real `Pcg32XshRr` stream whose position lives
  in the document" (`docs/architecture/worlds.md:1455`) **[checked]**. The
  authority authored the seed, can edit the stream position live (the document
  is its own), and decides when the drawing rule fires. A replay reproduces
  exactly the draw the authority wanted.
- **Gain:** a rigged shuffle that passes "fairness by replay".
- **Today:** nothing binds a seed to anything a participant chose. Replay
  proves determinism given the seed, not that the seed was fair
  (critique 1.3) **[inferred]**. Commit-reveal of per-participant entropy
  before the draw, with the committed values taped as inputs, is the standard
  answer **[general]**; the attestation library's sequence and audience
  machinery could carry it, and nothing does today.
- **Severity:** high for tables and loot; critical if stakes ever touch real
  money (the owner's regulatory line, `docs/plans/mmo-foundations.md:72-75`).

### High

#### A6. Raw PvP between two player-hosted authorities

The brief asks for this explicitly: who decides a hit, what each side can lie
about, and what invariant 1 implies.

- **Attackers:** X and Y, each hosting their own world and embodied in it,
  each the source authority of their own body.
- **What exists:** nothing that lets a body under X act on a body under Y.
  Interactions are evaluated by one authority over its own carriers
  (`src/Puck.World.Schema/WorldInteractions.cs:57-66`); the one cross-authority
  decision is `LocalResponds` for `"physical-contact"` at a seam
  (`src/Puck.World.Server/WorldAdjacencyContactField.cs:100-113`)
  **[checked]**. So a hit across a seam is not yet a vulnerability; it is a
  missing feature, and the model's intended answer is co-location.
- **Who decides a hit under co-location:** Y's body transfers into X's world
  and X decides everything: X's rules, X's tick, X's view of Y's intents. Y can
  lie about its inputs only, and its inputs are intents the authority simulates
  (`WorldAcceptedInput`, `src/Puck.World.Server/WorldAcceptedInput.cs:6-9`)
  **[checked]**; Y's claimed speed is clamped by X's kit envelope
  (`src/Puck.World.Server/WorldBody.Step.cs:1189-1194`) **[checked]**; Y's
  facts are whatever Y wrote (A1). X can lie about everything, sees Y's hidden
  state (A4), has zero latency to its own body **[general]**, and can roll back
  the fight (A3). Co-location onto a participant is the worst case of every
  attack above at once.
- **Who decides a hit under a seam settlement like `LocalResponds`:** each
  authority pair's responder is chosen by hash, so one side adjudicates every
  contact between two authorities, over positions and colliders the other side
  reported off the mirror
  (`src/Puck.World.Server/WorldAdjacencyFields.cs:486-498`) **[checked]**.
  Each side can therefore lie about its own body (position, collider, contact
  mode) in every pair, and about the outcome in the pairs it answers. A hit so
  decided is a claim by one authority about a body it does not own.
- **What invariant 1 implies:** an effect on a body is a write only its
  authority may make. A hit between bodies of two authorities is therefore
  either (a) a transfer first, so one authority owns both (the model's choice,
  which hands one player the other), (b) a proposal by one authority that the
  other applies to its own body under its own rules (the "transactional
  interaction resolution" the architecture page names and rejects as slower,
  `docs/architecture/worlds.md:1390-1393`), or (c) both authorities running the
  same deterministic simulation over both bodies' inputs and comparing state
  hashes each tick, so a disagreement is a provable lie by one of them (the
  lockstep the page also names). Puck's determinism (rule 4) makes (c) unusual
  among engines: two authorities can each simulate the whole encounter and
  check the other's hash, which is how fighting-game rollback netcode works
  between two peers with no server **[general]**. Under (c) neither side can
  lie about simulation; each can still lie about its inputs' timing and can
  stop responding, and the fight has no referee for the stop.
- **Gain today:** none, because nothing crosses. Gain under co-location: the
  host wins every fight it wants to.
- **Severity:** high: the feature the owner names as "in effect raw PvP" has
  no honest mechanism between two player hosts under the model's chosen rule.

#### A7. Body duplication by a hostile source

- **Attacker:** a federated source authority (a player host); it controls when
  it detaches.
- **Steps:** reserve and commit its own body into destination B, and never
  record the departure or detach at home; or commit the same incarnation at
  epoch `e+1` into B and into C. Only the source writes `Departure`
  (`docs/architecture/worlds.md:1218-1226`) **[checked]**. A destination's
  epoch compare-and-set is per destination (`m_mobilityAdmissions`, keyed by
  source authority and incarnation, `src/Puck.World.Server/WorldTransferEscrow.cs:2452-2465`)
  **[checked]**, so two destinations cannot see each other's admissions
  **[inferred]**.
- **Gain:** one character embodied in two or three worlds, each earning facts
  the home will adopt (A2) and each eligible for a table seat.
- **Today:** for a companion, home's `Hop` grant and the destination's
  `HopQuery` fence it, but home is the attacker and grants itself
  (`docs/plans/companion-redesign.md:213-234`) **[checked]**. For a player body
  there is no title at all (critique 1.1). The companion model's forger
  faults cover a former or current custodian forging toward home or the other
  custodian (`docs/plans/companion-redesign.md:460-461`, the law
  `AForgedReturnToHomeIsStaleWhileTheCurrentCustodianKeepsCustody` at `:562`),
  not home forging **[checked]**.
- **Severity:** high. On its own a duplicate body is a curiosity; with A1 and
  A2 it is parallel farming.

#### A8. Epoch and key replay against a restarted player host

- **Attacker:** a federated source holding a recorded reservation and commit.
- **Steps:** commit a traveller into Alice's world; wait for Alice's process to
  restart (or crash it, A12); resend the same reservation and commit. With no
  durable log and no image, Alice's destination has no lease record, no
  tombstone and no consumed epoch; the key is leased afresh and the body lands
  again.
- **Gain:** a second body, or a landing the source already settled as
  `stayed`, so the body exists at home and at Alice's.
- **Today:** the fences are real and durable only where the log is: "a key is
  leased at most once, durably", "`Missing` is final", tombstones
  (`docs/architecture/worlds.md:1180-1200`), the consumed-epoch check
  (`src/Puck.World.Server/WorldTransferEscrow.cs:2105-2111`) **[checked]**, all
  in memory on a desktop (A3's citations).
- **Severity:** high on player hosts; unattackable on the silo (section 3).

#### A9. Cross-platform trust list: one root, every account an authority

- **Attacker:** any platform account holder, or anyone holding a compromised
  account key.
- **Steps:** a world that wants to admit any player's host authors a `Vouches`
  row for the platform root and a `FederatedAuthority` row with `*`
  (`WorldAdmissionEntry.AnyAuthority`, `src/Puck.World.Schema/WorldAdmission.cs:165-167`)
  **[checked]**. Every subject the root vouches for is then an authenticated
  namespace that may land travellers (`WorldAdmissionDoor.TryAdmitArrival`,
  `src/Puck.World.Schema/WorldAdmissionDoor.cs:206-260`) **[checked]**. A
  banned or stolen key stays valid until its binding expires: attestation
  "performs no path discovery and fetches no missing certificates"
  (`docs/reference/attestation.md:261-262`), the connection door builds its
  trust list with `MaximumAge: null` per entry
  (`src/Puck.World.Schema/WorldAdmissionDoor.cs:87`), and the 5-minute
  `MaximumClaimAge` bounds the challenge claim, not the binding
  (`src/Puck.World.Protocol/Codecs/WorldAttestedAuthenticator.cs:36-38`)
  **[checked]**.
- **Gain:** a banned player keeps hosting and visiting until the issuing
  binding's own window closes; the length of that window is the issuer's
  issuance policy (a property of the issuer abstraction, whichever provider
  implements it today), not read here **[unverified]**.
- **Severity:** high at scale (moderation has no revocation lever below expiry);
  the alternative of short-lived bindings is an issuance policy, not an engine
  change **[general]**.

### Medium

#### A10. Seam shoving

- **Attacker:** an adjacency neighbour (a player host whose world borders
  yours); it controls the snapshots its mirror delivers.
- **Steps:** report phantom solid bodies, or place solid geometry, at the
  boundary. When `LocalResponds` picks the local authority for the pair, the
  local body is corrected out of each phantom; the neighbour's solid field is
  consulted in the band either way (`WorldAdjacencyContactField`) **[checked]**.
  Report them moving into the seam each tick.
- **Gain:** repeated phantom contacts can still shove a visitor within the seam
  strip. `WorldPopulation.AdmitAdjacencyResponse` bounds the bodies' and the
  geometry's combined displacement per body per tick by its local collider
  reach and the overlap depth, and keeps it and the body's outward speed behind
  every ownership face the body is still owned behind, so a shove cannot cross
  an ownership plane. The responder is chosen by the authority pair alone, so
  reissuing bodies under new generations cannot make the visitor always yield.
  `WorldAdjacencyCorrectionLawTests` witnesses each. Open: a neighbour field's
  sweep refusal can still pin a body, and the remote identity is a claim until
  attested ([open items](../open-items.md#transfers-federation-and-server)).
- **Today:** the overlap depth takes the larger of both sides' reaches and the
  sum of both speed ceilings (`src/Puck.World.Schema/WorldAdjacencies.cs:649-657`)
  **[checked]**, so a neighbour can widen the shared strip but never shrink
  it; its attested terms are "believed on the same keys a connection is"
  (`src/Puck.World.Schema/WorldAdmissionDoor.cs:262-263`). Nothing checks that
  the bodies a neighbour reports are plausible.
- **Severity:** medium: griefing bounded to the seam strip, escalating through
  A2 and A3.

#### A11. Sybil pressure on the transfer ledger

- **Attacker:** a party holding several authenticated namespaces (platform
  accounts under A9).
- **Steps:** each namespace reserves under keys it never settles and never
  raises its floor. The destination keeps `TransferKeysPerSource` (4096) rows
  per source and `TransferKeys` (65536) in all, never evicts, and "refuses
  reservations ... until a rising floor frees it"
  (`src/Puck.World.Server/WorldTransferLimits.cs:13-21`,
  `docs/architecture/worlds.md:1202-1213`) **[checked]**. Sixteen namespaces
  fill the ledger.
- **Gain:** nobody can enter the world until the leases expire (120 seconds,
  `WorldTransferLimits.LeaseSeconds`) and the attacker stops.
- **Today:** per-source shares bound one namespace; `MobilityAdmissions` and
  the federation connection slots are bounded the same way
  (`src/Puck.World.Server/WorldTransferLimits.cs:10-12`,
  `src/Puck.World.Server/WorldPeerHost.cs:491-499`) **[checked]**. A namespace
  costs an account, not a key, under a vouching root.
- **Severity:** medium: a denial of entry that costs the attacker accounts and
  is visible in counters.

#### A12. Resource denial at admission

- **Attacker:** any admitted peer.
- **Steps:** arrive claiming an avatar whose creation is enormous; the
  destination bakes the look from the content identity at arrival
  (`docs/plans/hub-and-saves.md:225-230`, H10 unbuilt) **[checked]**; or hold
  a reservation to the lease deadline repeatedly; or open projection streams
  to the backpressure limit (`WorldFederationProjectionSink.PendingDeliveryLimit`,
  `docs/architecture/worlds.md:832-836`) **[checked]**.
- **Gain:** a host's CPU and memory, on a machine that is also a player's.
- **Today:** the plan says the destination "measures the creation's cost
  itself rather than trusting a claimed figure" (`docs/plans/hub-and-saves.md:246-248`);
  whether the measurement precedes the bake is P2
  (`docs/plans/hub-and-saves.md:1016-1019`) **[checked]**.
- **Severity:** medium.

#### A13. Generation confusion after a host restart

- **Attacker:** none needed; a restart suffices, and a party that can force
  one (A12) can time it.
- **Steps:** a host restarts; `m_nextGenerationId` restarts with it
  (`docs/development/hazards.md:225-228`); a continuation checkpoint carries
  generation ids minted before the restart; a reservation addressed to one may
  land in a different ephemeral world than the display resolved.
- **Gain:** "show dungeon A and enter dungeon B", the exact split the model
  resolves once to avoid (`docs/architecture/worlds.md:589-593`) **[checked]**.
- **Today:** the hazards page marks the adoption consequence unconfirmed
  **[unverified]**.
- **Severity:** medium until confirmed.

### Low

#### A14. The sanctioned download

- A world that authors `Disclosure: Replica` on an `AnyAuthority` row hands its
  whole document, hidden seeds and rules included, to every authenticated peer.
  The default is `Presentation`, and `Replica` "is reachable only by authoring
  it" (`src/Puck.World.Schema/WorldAdmission.cs:147-150`) **[checked]**. An
  authoring footgun, not a hole.

#### A15. Stale custody by a vanished companion custodian

- A destination gone for good leaves a companion's title pending forever, by
  the fence that forbids recalling it from under a destination that may hold
  home's confirmation (`docs/plans/companion-redesign.md:322-328`, `:589-594`)
  **[checked]**. A hostile custodian can therefore hold a player's companion
  hostage by never answering. The owner has left the product call open; the
  cost is one companion per malicious host visited.

#### A16. Watching without a body

- Joining discloses the `Presentation` projection of "the visitor's rendered
  and embodied-from state" to anyone the admission row admits
  (`docs/architecture/worlds.md:753-757`) **[checked]**; a PvP world whose row
  admits observers lets a rival scout positions with no body. A per-world
  policy question, not a defect.

## 3. Unattackable by construction

These hold because a type, one door or a cryptographic check enforces them,
so the round need not re-litigate them. Each names where the guarantee stops.

- **A remote submission cannot name a session principal.** Both federated
  ingress paths decode through one step that refuses it
  (`WorldCodecRefusal.SessionPrincipalRemote`,
  `src/Puck.World.Server/WorldPeerHost.cs:1925-1957`) **[checked]**. Stops at
  the authority: a session is the admitting world's own.
- **A federation namespace is the claim's chain-verified subject, never
  asserted bytes**, and the proof is bound to both nonces, both transport key
  hashes and the direction (`src/Puck.World.Protocol/Codecs/WorldAttestedAuthenticator.cs:11-14`,
  `:147`; `src/Puck.World.Server/WorldFederationCodec.Authority.cs:10-21`)
  **[checked]**. The QUIC layer accepts any presented certificate on purpose
  (`src/Puck.Networking/Peers/QuicPeerTransport.cs:91`, `:113`, `:154`) and the
  binding is the handshake's job **[checked]**; a connection cannot be steered
  onto another namespace by a middlebox. Stops at the key: whoever holds a
  vouched key is that namespace (A9).
- **A sender cannot exempt a body from the `networkPlayers` cap.** A traveller
  that keeps the `Live` source or carries the human statement lands as a human;
  a claim can add a human and never remove one
  (`src/Puck.World.Server/WorldTransferEscrow.cs:19-22`, `:34-40`;
  `docs/development/security-review-reference.md:143`) **[checked]**. Stops at
  companions, which land autonomous with no `Drive` and no route.
- **A claimed speed cannot exceed the destination's kit envelope.** The clamp
  lands before the motion program reads the value
  (`src/Puck.World.Server/WorldBody.Step.cs:1189-1194`) **[checked]**. Stops at
  values the envelope does not cover.
- **A neighbour cannot shrink your deadband.** Overlap depth takes the larger
  reach of either side and the sum of both speed ceilings
  (`src/Puck.World.Schema/WorldAdjacencies.cs:649-657`) **[checked]**. Stops at
  plausibility of reported bodies (A10).
- **A companion's title cannot be stripped or forged by a sender.** The title
  mark is part of the incarnation; a stripped address is a different
  incarnation home holds no title of; a titled arrival lands only under a hop
  home confirms at the commit (`docs/plans/companion-redesign.md:41-48`,
  `:238-253`; `src/Puck.World.Server/WorldTransferEscrow.cs:1866-1872`,
  `:1979-1982`) **[checked]**; the model check explores a former or current
  custodian forging a reservation (`docs/plans/companion-redesign.md:460-461`).
  Stops at home: home is the player's own host (A7).
- **A destination never saves a visitor into its catalog.** `TryAdopt` refuses
  an identity the catalog does not own
  (`src/Puck.World.Server/WorldOwnedWorlds.cs:1344-1348`) **[checked]**.
- **A fetched prototype body must hash to its pin before it enters the cache
  or a projection** (`src/Puck.World.Schema/WorldProjection.cs:1072-1078`)
  **[checked]**, and the fetch is authorised against the recipient's current
  projection (`docs/architecture/worlds.md:888-895`). Stops at content the
  authority deliberately discloses.
- **Transfer keys are exactly-once on a durable log.** Lease once, `Missing`
  is final, tombstone before the first `Missing`, a committed key never
  answers `Missing` after `Committed`, and the ledger is bounded without
  eviction (`docs/architecture/worlds.md:1172-1213`) **[checked]**. Stops at
  the log's existence: the silo has one, a desktop does not (A3, A8).
- **A silo release rewind refuses a checkpoint with obligations outside its
  closed group** (`src/Puck.World.Server/WorldReleaseRewindBoundary.cs:30-46`)
  **[checked]**. Stops at the silo: a desktop host rewinds by restarting.

## 4. Questions for the research lanes

Each question names the attack that raises it and the lane the round assigned
(`docs/plans/mmo-foundations.md:79-88`).

1. **Which classes of value become issuer-kept records moved by escrow, which
   travel as signed claims with provenance, and which stay unsigned facts?**
   (A1, A2, A5's stakes.) Lane B and lane D. The model names all three answers;
   the code builds the first for companions only. A signed fact needs the
   issuer's key pinned or vouched at every reader and a sequence with a replay
   horizon (`docs/reference/attestation.md:605-657`); an issuer-kept record
   needs the issuer reachable at spend time. Lane D's dupe history should say
   which failures each answer has had elsewhere.
2. **Should progress earned abroad write through to home as it is earned,
   rather than ride the projection until return?** (A3.) Lane B. The
   companion protocol's `Write(I)` record is a custodian's taped write
   (`docs/plans/companion-redesign.md:115`); a write-through would make the
   custodian's write a submission home decides, which is what `WorldOwnedWorlds.Decide`
   already does for durable slots at a tick boundary. The question is latency
   and what a visited world may write.
3. **What must a player-hosted authority persist, and what does it prove when
   it does?** (A3, A8.) Lane B. A crossing log and a durable image on the
   desktop close the crash case; the dishonest case needs the counterparty to
   hold the evidence (the source keeps the departure, home keeps the title).
   Whether a player host should ever be a destination for another player's
   body, or only a renderer of one, is the design fork.
4. **Where does a contested place run?** (A6.) Lane C. "Do not run an authority
   boundary through a place designed to be contested" and "players hosting
   their own authoritative worlds is a goal" collide at a duel between two
   player hosts. Lane C's seam research should weigh (a) co-location onto a
   platform silo for every fight, (b) mutual deterministic simulation with
   hash comparison between the two peers, which Puck's determinism makes
   available and no server-authoritative MMO can use, and (c) a transactional
   proposal protocol, against what fighting games and peer-to-peer RTS lockstep
   have learned about stalls and desync referees.
5. **How is a table honest when a participant hosts it?** (A4, A5.) Lane D.
   Commit-reveal for seeds, mental-poker dealing, or the rule that a table's
   authority is never a seated party; and what provable fairness means when the
   verifier must hold the inputs, which Puck's taped inputs could be published
   as.
6. **What revocation does a vouching root need below expiry?** (A9.) Lane B
   and lane E. Short-lived issuing bindings versus a refusal list a world
   consults offline; the attestation reference rules out fetching during
   verification, so a list has to arrive as data.
7. **Confirm the unverified premises.** Lane E. The generation-counter
   consequence (A13); whether a seam correction is bounded per tick (A10);
   whether group membership proof exists on the federation wire (critique 1.2);
   whether the avatar cost measurement precedes the bake (A12); and how the
   issuer issues identity keys and sets binding lifetimes. The question is
   asked of Puck's issuer abstraction (a key's lifetime and revocation), not
   of one provider's settings: Azure and Entra are today's implementation
   (`src/Puck.World.Azure/EntraWorldAuthenticator.cs`,
   `src/Puck.World.Azure/WorldApiCounterpartResolver.cs`, not read by this
   pass), and whatever the answer requires must hold for any replacement.
8. **What does a visited world get to write?** (A2.) Lane A. How WoW, FFXIV
   and EVE partition character state between the realm that earned it and the
   character record, and what they do when a realm is rolled back after the
   character left it.

---

[Plans](../README.md) · [MMO foundations](../mmo-foundations.md) · [The world model](../../architecture/worlds.md)
