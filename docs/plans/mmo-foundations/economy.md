# Economy and table integrity

This is evidence and options for the [MMO foundations round](../mmo-foundations.md),
not a decision. **[inference]** Stakes need an enforceable spend record outside a
dishonest player's rollback domain. Replay establishes execution from given
inputs; it does not establish honest inputs, hidden cards, delivery of evidence,
or payment. A participant can host a table's presentation without being its
dealer or its banker. A separate trusted dealer is the simpler option; verifiable
encrypted shuffles can remove that dealer's knowledge, at the cost of more
protocol work and harder disconnect recovery. Neither option prevents players
sharing their own cards. Regulatory boundaries depend on the actual purchase,
play, transfer, and redemption paths, not the name given to a token.

## Scope and evidence

This lane owns round question 6, the table hypothesis, attack questions 1 and 5,
and attacks A4 and A5. It shares question 4's value classes with lane B.
The [attack catalogue](attacks.md#4-questions-for-the-research-lanes) supplies
the attack identifiers. No option below is an owner ruling.

- **[opened]** identifies text actually opened, with a short exact quotation and
  its source. A studio statement, a player's bug report, and a research paper
  are different evidence even when all three were opened.
- **[summary]** identifies a search excerpt or an inaccessible source's secondary
  account. It is a lead, not proof of a technical root cause.
- **[inference]** identifies reasoning, proposed protocols, and proposed checks.
  These are not claims that Puck implements the mechanism.

Read dates and tree revision belong in the commit. File line numbers below are
leads at that tree; symbols name the durable source. A failed fetch is recorded
in the source list, never promoted to an opened quotation.

### The Puck boundary this page relies on

**[opened]** The [world model](../../architecture/worlds.md#the-idea-in-plain-terms)
states: "An authority sees all of its own state" and "a host sees every hand at
a table it runs." Its intended federation model does not make a player's
process trustworthy. The same section distinguishes implemented companion
custody from the intended treatment of other value.

**[opened]** The [disclosure trust statement](../disclosure.md#what-is-guaranteed-and-to-whom)
says: "The engine's own C# is trusted code". Its guarantee concerns excluded
recipients, with machine-resource channels outside the guarantee. The plan
also says "Local presentation is unfiltered by design". These are explicit
limits, not defects that a stronger row visibility policy alone can remove.

**[opened]** Two current source doors confirm the narrow host premise:
`WorldServer.Definition`, `src/Puck.World.Server/WorldServer.cs:275`, is
`public WorldDefinition Definition => m_document.Definition;`.
`WorldServer.Arena`, `src/Puck.World.Server/WorldServer.Arena.cs:144`, is
`public StateArena Arena => m_arena;`.
`WorldSinkDisclosure.Full`, `src/Puck.World.Server/WorldOutputHub.cs:13-14`,
documents "colocated trust is home trust." These members were read directly.
This lane did not audit all ingress, persistence, randomness, or settlement code;
claims about those implementations await lane E.

**[opened]** The disclosure plan's recipient view distinguishes `Replica`,
`Presentation`, and `Frames`. It specifies a filtered document, a hydrated
projection, and no definition respectively. **[inference]** All three are
delivery choices. Sending only frames can protect a remote viewer from a
trusted dealer's hidden state; it cannot protect that state from the dealer.
A replica permitted to hold the complete secret tape can calculate the future.

**[opened]** The [card table packet](../open-items.md#card-tables-in-the-board-game-room)
says "Poker: seats, computer players and the hand judge; its own step."
It describes physical cards driven by rules and names the four disclosure
fixture games. **[inference]** The next poker content step is not evidence that
adversarial multiplayer dealing or economic settlement already exists.

## Duplication history

### What the incidents establish

**[inference]** There are at least five distinct failure classes. A transfer can
credit a destination without durably removing the source. Recovery can restore
one side of a completed exchange. A stale production flag can create goods
without consuming inputs. A client or untrusted issuer can assert invented
value. Finally, a display can show a duplicate that does not exist in the
authoritative inventory. Calling all five "dupes" obscures the repair.

The records below separate a known incident, its supported cause, and the fix
actually reported. Where the code-level fix is undisclosed, it remains unknown.

### Diablo II

**[opened]** Blizzard's [Realm FAQ][d2-realms] says "Realm Characters are stored
on a Diablo II Realm". This is the relevant client-trust distinction: closed
realm storage limits what a locally edited character can import. It is not a
proof that every server-side transfer is atomic.

**[summary]** The [archived Blizzard statement][d2-statement] search excerpt attributes
realm lag to "massive user duplication efforts". Community histories describe
lag and synchronization exploits and later removal of duplicated items. The
related [archive copy][d2-hacks] refused opening. This research did not verify a specific Diablo II
save interleaving, item-identity scanner, or the details popularly called
Rust Storm from a surviving primary postmortem.

**[opened]** The [official patch page][d2-patch] was opened; it is headed
"Diablo II Patch 1.10 Beta". Searches of that page found no duplication or
hack description. It cannot substantiate a claim that this patch fixed a
particular dupe. **[inference]** Keep realm storage, duplicate cleanup, and a
transaction repair separate. Cleanup can remove a victim's purchased copy;
it does not by itself prevent another duplicated transfer.

### World of Warcraft

**[opened]** A [first-person auction bug report][wow-bug] states "Gold being paid
out for cancelled auctions." This is a player report, not Blizzard's root-cause
analysis. The author describes receiving both a returned commodity and money.

**[opened]** Blizzard's [auction closure notice][wow-fix] confirms a critical
auction bug, a fix, and account repair: "return player accounts to their previous
condition". The notice says some characters remained unavailable during repair.
It does not publish the database transaction or explain the exact race.

**[inference]** Cancellation and sale are competing terminal outcomes. An
economy must record which one won before it issues a refund or proceeds, and
retries must return that same outcome. That is a proposed invariant derived
from the symptom, not an assertion about Blizzard's internal implementation.
An auction service can be centrally authoritative and still violate it.

### EVE Online

**[opened]** CCP's [POS exploit postmortem][eve-exploit] gives a concrete cause
and repair: "we merely evaluate reactors normally, even if they have no links."
An optimization skipped inputless reactors. Their earlier production flag
remained set, so output silos kept adding material without fresh consumption.
CCP investigated affected reactors and economic flows and sanctioned exploiters.

**[inference]** This was a production-state defect, not proof of a non-atomic
player trade. Atomic transport of counterfeit goods would faithfully spread
them. Puck needs conservation at minting and crafting as well as custody at
crossings. Authored production rules need an explicit source or approved mint
operation for every increase; a cache of prior eligibility cannot authorize
a new economic effect.

### RuneScape

**[summary]** The [coin-pouch incident account][rs-pouch] describes a death-code
change mishandling stacks and producing maximum-size coin stacks. The
[contemporaneous rollback report][rs-rollback] describes taking Old School
RuneScape offline and rolling it back to before the update. The official status
URL and its archive copies could not be opened here. The precise code repair
and rollback consistency boundary are therefore unverified.

**[summary]** The older party-hat duplication incident appears in the
[duplication history search result][duping-history]. This lane has no opened
Jagex postmortem establishing its packet validation failure or exact patch.
Do not use community descriptions of that exploit as a protocol specification.

**[inference]** The coin-pouch account illustrates a different concern from
transfer races: a legal-looking server mint caused by a conversion defect.
A full rollback can remove the mint within one rollback domain. If value has
already been accepted in an independent world, restoring the source alone
cannot revoke that world's spend history. Lane B must define that boundary.

### Ultima Online

**[opened]** Origin Research Fellow Zachary Booth Simpson's [economy paper][uo],
available as a mirror, describes "an obscure server fault" that cloned gold
and reagents. It says repair took time and counterfeit supply displaced normal
exchange. It reports the cheats were corrected, but gives no source patch or
exact crash or server-boundary interleaving for this incident.

**[inference]** It is sound evidence of counterfeiting and economic recovery
cost, not evidence for the often-repeated claim that every UO dupe was a
server-boundary race. Item deletion and economic drains have different jobs:
one removes invalid stock, the other changes demand or supply over time.
Neither makes an ambiguous transfer commit unambiguous.

### Albion Online

**[summary]** The [official developer statement][albion] was available in the
search result, but direct opening returned a refusal. It denies a known actual
dupe in the reports under discussion, explains stale inventory slots that
resolve when a container reloads, and says an alleged video did not match the
recorded transactions. This is a denial of those reports, not a claim Albion
has never had a duplication bug.

**[inference]** There is no substantiated economy exploit or repair to list
for that incident. A debug check should compare authoritative custody and
spendability, not count rendered icons. A visual copy and two spendable copies
must produce different diagnoses. Inventing a rollback fix for this case would
erase the most useful finding.

### New World

**[opened]** Amazon's [update notes][nw-fix] report fixes for item duplication
through storage and crafting, and for "gold duplicating on territory projects
we introduced by disabling trading". The latter is a confirmed failure of the
mitigation itself. The notes do not disclose the underlying transaction code.

**[summary]** The [wealth-transfer shutdown account][nw-freeze] reports disabling
currency sends, guild treasury, market, and direct trade after trophy duplication.
It also records an earlier shutdown. Treat its technical explanations as
secondary; it does not prove that the client had authoritative inventory access.

**[inference]** A freeze must cover every economic door, including construction,
rewards, refunds, mail, and settlement. Refusing a debit while continuing its
refund is worse than leaving one trade UI available. Recovery needs a durable
incident boundary and a policy for innocent downstream holders. Bans are not
an atomicity mechanism.

### Lessons to carry into custody

**[inference]** The evidence favours one economic write path, with author-defined
rules around it, over separately maintained trade, auction, mail, and wager
mutations. Each operation needs a durable identity, exact accepted terms, the
assets or quantities reserved, and a unique terminal decision. A response lost
after commit must be safely repeatable. A cancel racing with commit must choose
one result. A restore must not erase evidence another authority already acted on.

**[inference]** A signature authenticates an issuer's statement. It neither
proves that the issuer debited a balance nor stops the same signed claim being
spent twice. Conservation requires the issuer or an accepted spend authority
to remember consumption. A dishonest issuer can still mint; trusting an issuer
must include what it may mint, not merely whether its key is authentic.

## Escrow and atomic exchange

### What live-game interfaces promise

**[opened]** EVE's [direct-trade documentation][eve-trade] says a trade finishes
"once both pilots have clicked \"Accept\"" and warns about scams and last-minute
changes. **[inference]** The operator arbitrates the exchange. A confirmation
must refer to one immutable offer revision; editing the offer invalidates prior
consent. The documentation does not reveal whether items are physically moved
into an escrow row while the window is open or reserved in their original rows.
Either implementation can work if no concurrent action can spend them.

**[opened]** EVE's [market documentation][eve-market] describes "a brokerage
system" and order matching, including partial fills. **[inference]** Listed
inventory and committed buying power must remain unavailable to incompatible
spends. The broker can commit a fill and leave delivery for later collection.
This is logical custody by the operator, not possession by the buyer's client.
The document does not establish CCP's physical database locking scheme.

**[opened]** EVE's [auction contract documentation][eve-auction] says both parties
"will have to claim money and item respectively" after completion. It also
specifies cancellation, refund, and asset-safety behavior when a location is
destroyed. **[inference]** Award, collection, and physical location are separate
states. Claiming twice must not pay twice. A settlement can be final before
the recipient's inventory UI has collected it.

**[opened]** EVE's [courier documentation][eve-courier] says items become a
"Plastic Wrap" package and collateral is returned on delivery or paid to the
issuer on failure. It documents lost packages, broken packages, destroyed
destinations, and an offline delivery hangar. **[inference]** This is secured
performance, not an atomic swap of identical goods. The courier holds the
package; the operator holds conditional collateral and the contract record.
The issuer may receive compensation rather than recover the original goods.

**[summary]** Blizzard's [manual search excerpt][wow-manual] describes cash on
delivery packages; the PDF fetch timed out. **[inference]** A COD design holds
the attachment in the mail service while payment is pending, and commits
payment and recipient entitlement together. Expiry or rejection returns the
attachment only if collection has not committed. Ordinary gift mail has no
reciprocal payment condition. This is a design description, not a verified
claim about WoW's internal mail tables.

### What escrow does not promise

**[inference]** The WoW cancellation incident shows why asynchronous payout and
refund paths need a common terminal decision. EVE's delivery failure cases show
why custody alone cannot ensure the destination remains usable. Its trade
warnings show that an atomic transaction can faithfully execute a deceptive
offer. New World's mitigation failure shows that a freeze must preserve the
relationship between a debit and every compensating credit.

**[inference]** A Puck escrow world can be selected per trust community. It need
not be the host drawing the table, the character's home, or a universal bank.
But both sides must accept its spend decisions. If neither accepts a common
authority, protocol messages alone cannot force a malicious host to relinquish
its own locally invented currency.

**[inference]** Cross-issuer swaps require coordinated reservations and a shared
decision or a carefully specified conditional exchange. A timeout is not proof
of abort when commit may already exist elsewhere. Compensation is not equivalent
to undo after a sword has been sold or a hand has revealed information. Lane B
owns the durable recovery and fencing protocol; this lane supplies the table's
requirements on it.

## Randomness and provable fairness

### Commit-reveal

**[opened]** Stake's [published implementation][stake] names "a client seed, a
server seed, a nonce and a cursor" and publishes an HMAC-based calculation.
Its server seed is committed before betting and disclosed after rotation.
This documents one operator's scheme, not an independent audit of its service.
The page calls a hash encrypted; a hash commitment is not encryption.

**[inference]** A multiparty candidate would bind each commitment to the table,
hand, seat roster, rules identity, protocol, and fresh secret contribution.
All commitments must be fixed before any contribution is opened. Encode the
ordered contributions unambiguously and derive a separate stream per purpose.
At least one unpredictable honest contribution can prevent a coalition choosing
the combined seed, conditional on completing the protocol correctly.

**[inference]** That last qualification matters. The last revealer may see an
unfavourable result and refuse to reveal. Replacing an absent seed with zero,
or drawing again, grants a choice of outcomes. Commitments stop changing a
contribution; they do not force its release. Escrow penalties, recoverable
encrypted contributions, or an independently available randomness service can
change that incentive or availability assumption. Each needs its own proof.

**[inference]** For public dice, revealing contributions before calculation is
acceptable if wagers and relevant actions are already fixed. For poker, revealing
the shared seed at the start gives everyone the deck. Revealing it after the hand
supports auditing but requires someone else to know the deck during play, unless
the deck is constructed and dealt cryptographically. Commit-reveal alone cannot
provide a secret deck to an untrusted participant-host.

### Verifiable random functions

**[opened]** [RFC 9381][vrf] says "Only the holder of the secret key can compute
the hash" and anyone with the public key can verify it. It distinguishes
uniqueness, pseudorandomness, and malicious key-generation concerns.

**[inference]** With a fixed accepted key and fixed input, a VRF gives a
verifiable unique output. It does not force the holder to publish the result,
prevent choosing among inputs or keys before acceptance, or hide the output
from the key holder. Pin the key before the relevant input becomes predictable;
bind the input to a non-reusable hand identity and already accepted terms.
Do not let the host choose a tick or nonce after inspecting possible outputs.

**[inference]** A VRF can serve public draws or an independent dealer's entropy.
A participant with the VRF secret cannot fairly deal hidden cards to opponents
merely by publishing a valid proof afterward. Threshold VRFs move trust to a
committee and add setup, membership, and availability questions. No Puck
threshold-VRF implementation or measured deployment is asserted here.

### Failures that a fairness label misses

**[opened]** Arkin and colleagues' [online poker investigation][poker-bug]
describes "an off-by-one error", unequal shuffle probabilities, and predictable
random generation. They report the supplier changed its algorithm and expressly
say they did not examine the replacement. Publishing the original algorithm
enabled the attack; publication did not make it correct.

**[inference]** A modern commitment scheme still fails if it commits after a
predictable client contribution, reuses an exposed seed, selects which accepted
bets to execute, omits losing or aborted hands from the evidence, or maps random
bytes to cards with biased selection. These are protocol failure modes, not
allegations that Stake performs them. Modulo reduction requires attention to
range bias; a shuffle needs a correct permutation algorithm and unbiased choices.

**[inference]** Deterministic random generation is not necessarily cryptographic
random generation. A stream useful for simulation must not be assumed safe for
secrets after adversaries observe outputs. Public fair-dice rules and secret
dealing need separate security arguments even if both replay deterministically.
External entropy and cryptographic responses can enter as recorded inputs;
the simulation need not read ambient randomness or wall clock.

**[inference]** A valid outcome proof says nothing about the advertised odds,
house edge, permitted actions, rake, chargebacks, or ability to withdraw. Bind
the actual rules and payout terms before accepting a stake. Show exactly what
was verified, rather than a single "fair" badge implying all these properties.

## Hidden information without a trusted dealer

### From mental poker to verifiable shuffles

**[opened]** The original [Shamir-Rivest-Adleman paper][sra] was retrieved as a
scanned PDF; text extraction supplied no readable body. Its authorship and topic
are corroborated by [Mohnblatt's implementation account][mental-one], which
calls the problem "Mental Poker". This lane does not claim a line-by-line
security review of the original construction.

**[inference]** The classic SRA construction is the historical starting point:
commuting encryption lets players independently mask and permute cards, then
remove layers to deal. Treat that outline as context, not a deployment recipe.
An encryption scheme must hide partial information as well as complete card
identity; malicious keys, substituted cards, and missing participants require
more than the algebraic ability to decrypt in either order.

**[opened]** Barnett and Smart's [author abstract][mental-paper] requires "a
broadcast channel between all players" and describes card representations whose
size does not grow with player count. [Mohnblatt's technical account][mental-two]
uses threshold ElGamal, proofs of key knowledge, and proofs of correct partial
decryption. It explicitly requires protection against "rogue key attacks".

**[inference]** A candidate deck begins with agreed distinct card encodings.
Each shuffler secretly permutes and re-randomizes encrypted cards, proving the
output is a permutation of the same deck. Merely permuting ciphertexts leaves
trackable labels. Correctness of a permutation is different from uniform choice
of it: at least one honest shuffler must choose a private random permutation.
Every participant verifies each accepted proof, or accepts an identified
verifier's authority. A malicious relay must not show different deck histories
to different seats without detection.

**[inference]** For a private deal, the other players supply verifiable partial
decryptions while the recipient retains the last secret needed to read the card.
For a public reveal, the remaining share is supplied. A proof ties the revealed
card to its committed deck position, so a player cannot substitute a better card.
Do not disclose all key shares merely to simplify the post-hand replay: that can
reveal mucked hands and undealt cards that the rules promised to keep private.

### Dropouts and collusion

**[opened]** Kurosawa, Katayama, and Ogata's [paper abstract][mental-lazy] states
"if more than half of the players are currently active, they can play the game."
It explicitly discusses honest active and nonactive players. This abstract is
not evidence of arbitrary Byzantine dropout tolerance under every coalition.

**[inference]** An all-player threshold maximizes resistance to coalitions
decrypting an undealt deck, but one absent key holder can stop progress.
A lower threshold tolerates some absence while allowing a coalition of that
threshold to decrypt. Two-player play has no honest-majority availability
escape: the other player can stop sending. A separate recovery committee can
help, but then the protocol has trusted or threshold-trusted recovery parties.
Specify who they are and what they can learn before calling it dealerless.

**[inference]** No shuffle protocol stops two seated people sharing the hands
they legitimately received, playing softly against each other, transferring
chips through deliberate losses, or using off-platform advice. Identity checks,
table admission, collusion investigation, and payout policy remain separate.
A player-controlled host that supplies every bot opponent controls a coalition,
even when every bot has its own key.

### What is practical for two to ten players

**[opened]** Mohnblatt's [implementation account][mental-one] reports proof
generation just over 50 ms for a 52-card shuffle and verification below 1 ms on
its laptop. The [implementation repository][mental-code] is available and names
Barnett-Smart and Bayer-Groth. These are author-reported primitive measurements,
not this lane's benchmark or a ten-player end-to-end service guarantee.

**[summary]** Castellà-Roca's [thesis excerpt][mental-thesis] criticizes the
original Barnett-Smart presentation for implicit protocol details and missing
timing and proof material. Its full PDF timed out. **[inference]** That is a useful
counterweight to implementation optimism, not proof that the later implementation
is broken. Independent protocol review remains necessary.

**[inference]** A 52-card deck and two to ten participants are small enough to
justify a bounded prototype. Sequential shuffle phases cost network waits as
well as cryptographic work. Deal shares can be batched; verification can be
parallelized when independent. Hand setup should run between hands, not block
the render loop or execute an unbounded proof inside a simulation tick.
Ten participants also mean more opportunities for one slow or departing seat.

**[inference]** Three practical candidates remain: a trusted remote dealer for
ordinary interactive tables; all-player verifiable dealing with abort settlement;
or a committee that trades some collusion resistance for recovery. The sources
do not settle which meets Puck's device, latency, or operational budgets. A
no-code research brief cannot honestly substitute a new cryptographic prototype
for those measurements; the debug-world measurement packet below assigns them.

## Regulatory context for an owner call

This section is context, not legal advice or permission to operate gambling.
It records particular primary positions and judgments, not a complete current
law survey or a conclusion that one rule applies across jurisdictions.

### Belgium

**[opened]** The Gaming Commission's [research report, mirrored English
translation][belgium], concludes that paid loot boxes in the examined games are
illegal and recommends prosecution if they continue. Its short conclusion is
"paid loot boxes in the examined video games are illegal". The translation
identifies the Dutch text as official. Its tests consider a game, a stake,
chance, and a possibility of gain or loss; they are not limited to cash prizes.

**[opened]** The [Belgian parliamentary record][belgium-parliament] also discusses
"betalende lootboxen" and the four elements. **[inference]** This supports
treating paid random rewards as a serious product boundary. It is neither a
claim that every random free reward is prohibited nor a replacement for checking
the specific current Belgian product and enforcement position with counsel.

### The Netherlands

**[opened]** The [Raad van State judgment][netherlands] rejects treating FIFA's
packs as a separate game on the evidence before it: "De packs voegen een
kanselement toe aan het spel." In context, the court treats them as a chance
element within the broader FUT mode and reverses the enforcement decision.
The judgment does not say that all loot boxes are lawful; its analysis expressly
allows that a loot-box implementation could create a regulated chance game.

**[inference]** A separate poker table taking stakes is materially different
from packs mainly earned and used within football play. Putting that table
inside an RPG or calling it a world does not establish the same legal result.
The older shorthand "the Netherlands banned loot boxes" is also inadequate.

### United Kingdom

**[opened]** The [Gambling Commission's virtual-currency guidance][ukgc] treats
convertible items as money or money's worth and says "a licence is required"
when gambling facilities use such items. It includes trading for other items
of value, not just an operator-operated cash-out button.

**[opened]** The [DCMS evidence response][dcms] favours "an industry-led approach"
and parental authorization for children's purchases, alongside spending controls,
transparent information, and research. It distinguishes many loot boxes from
existing gambling definitions while recognizing convertible prizes can cross
that boundary. Policy proposals and voluntary controls are not identical to
statutory licensing tests.

**[inference]** A tradeable stake, a prize convertible through another world,
or a supported secondary market changes the analysis. A prohibition written in
terms of service does not establish that the actual product prevents conversion.
Counsel must examine the complete path and the operator's role.

### China

**[opened]** The [Ministry of Culture notice on the CAC site][china] requires
disclosure of "抽取或者合成概率"—draw or synthesis probabilities, translated here.
The same notice requires accurate information, published draw results with
privacy protection, retained records, and alternative ways to obtain equivalent
items. It also limits direct monetary participation and currency redemption.

**[inference]** Odds disclosure is not an approval to offer every paid lottery.
For a Puck option, publish the actual authored distribution, including conditional
odds and guarantees if present, and retain the terms that applied to each draw.
This lane did not establish the notice's complete interaction with subsequent
agency changes and current licensing rules. Chinese counsel owns that check.

### United States and Washington social casinos

**[opened]** In [Kater v. Churchill Downs][kater], the Ninth Circuit held Big
Fish virtual chips a "thing of value" because they extended the privilege of
play under Washington's definition. It reversed dismissal of the complaint.
The reasoning does not depend on an authorized cash redemption feature.
This is a Washington-law case, not a national rule for every social game.

**[inference]** "Free to download", "virtual only", and "no cash-out" are
insufficient universal legal categories. Paid replenishment of chips that permit
continued chance play deserves separate review. Cash-out, tradeable stakes,
purchased entries, and player-to-player wagers require jurisdiction-specific
analysis of consideration, chance, prize, licensing, and consumer protections.
The exact role of skill varies; poker's skill component is not a universal
exemption. No fifty-state or federal launch analysis was performed here.

### Product forks to put before the owner

**[inference]** Keep these forks explicit rather than quietly choosing them in
an escrow schema:

| Product option | Economic boundary | What remains to settle |
|---|---|---|
| Table-local score, no purchase or export | Score cannot fund another game or buy an item | Access fees, prizes, minors, and presentation still need review |
| Earned persistent chips, no purchase or redemption | Creates valuable progress and possible informal transfer incentives | Whether play privileges or transferable rewards count as value locally |
| Purchased chips or paid random rewards | Money enters before chance play | Consumer controls and gambling classification by territory |
| Tradeable items as stakes | Stakes may obtain market value outside the table | Secondary markets, issuer policies, and laundering through deliberate losses |
| Real-money deposit and cash-out | Explicit financial stake and redemption | Licensed operation, identity, age, payments, AML, disputes, and geographies |

**[inference]** Cryptographic fairness reduces one cheating risk in every row.
It does not move a product into a different legal row by itself. The owner and
qualified counsel decide the intended product before implementation fixes the
purchase, transfer, or redemption contract.

## Options for Puck

All guarantees in this section are proposed invariants. None is represented as
currently enforced. Lane E must map them to actual implementation gaps.

### Question 4 and attack question 1: classes of value

**[inference]** Classify by the consequence of copying or falsifying a value,
not just whether its schema calls it currency, an item, or appearance.

| Value | Realistic representation | Cost and failure boundary | Attacks affected |
|---|---|---|---|
| Scarce currency and stackable goods | Issuer-kept balances with durable reservations and spend decisions | Reachable spend authority or pre-funded limited delegation; compromised issuer can still mint | Closes A1/A2 counterfeit acceptance only for trusted issuers and checked writes; addresses A3/A8 with lane B's persistence |
| Unique tradeable equipment | Issuer-qualified identity plus one current spend/custody record | Split, merge, craft, destroy, and restoration must consume prior rights | Same as currency; A7 if crossing uniqueness is actually enforced |
| Table stakes | Reservation of existing spend rights in accepted escrow | Stake unavailable elsewhere; disconnect can block until a recorded decision | Economic part of A5, A3/A8; does not close A4 or seed manipulation |
| Achievement or dungeon progress | Signed issuer-scoped statement with explicit acceptance policy | Authentic issuer can lie; copied non-exclusive achievement may be harmless | Addresses A1/A2 provenance; A9 remains a trust-root problem |
| Cosmetic appearance | Unsigned preference when copying is harmless; entitlement when scarce or sold | One appearance field may reference both classes; never infer ownership from a rendered body | Unsigned facts intentionally leave A1 available within a non-economic scope |
| Local sandbox inventory | Player-owned mutable fact | Easy offline play; rejected as spendable foreign value | Leaves A1/A2 by choice; cannot enter accepted stakes without an issuer decision |

**[inference]** Offline signed bearer claims are useful evidence but not an
offline double-spend cure. A pre-funded allowance can cap exposure if its
spending is controlled by an accepted authority outside the player's restore
domain. A player-local wallet with a copyable private key does not gain that
property merely by signing every spend.

**[inference]** A character world can carry references and receipts for several
issuers. A receiving world can accept one issuer's gold and reject another's
without inventing another character model. A hidden seam may cross a rendering
or simulation boundary without moving spend authority. That avoids making
every spatial step an economic transaction.

### Question 6 and attack question 5: who runs the table

| Option | Evidence and guarantee | Cost and failure boundary | Attack result |
|---|---|---|---|
| Participant authority with plain replay | Simple deterministic reproduction | Host sees all hands and can choose inputs; other seats may lack evidence | Leaves A4/A5 open; unsuitable as an honesty claim |
| Independent trusted dealer plus accepted escrow | Disclosure fits a trusted authority; operator-mediated custody has live-game precedent | Hosting cost, dealer compromise or collusion, outage recovery | Closes seated-host A4 only when no seat controls dealer; A5 still needs entropy binding and witnessed inputs |
| Participant relay with verifiable encrypted dealing and separate escrow | Mental-poker papers and implementation support secret permutations and selective reveal | Proof work, authenticated broadcast, abort handling, client key safety | Can close host-knowledge A4 and shuffle-substitution A5 under stated coalition assumptions; withholding and collusion remain |
| Threshold dealer/recovery committee plus escrow | Threshold cryptography trades availability against coalition size | Membership, key refresh, independent operators, and incident procedures | Moves A4/A5 trust to a threshold; does not eliminate it |

**[inference]** The evidence favours prototyping the independent dealer first
if the owner accepts dealer trust, while keeping encrypted dealing as a distinct
candidate if participant-host secrecy is required. This is sequencing evidence,
not a selection. A non-seated dealer controlled by a seated player's account
is not independent. Even a genuinely separate dealer may collude; the product
must name that trust assumption.

**[inference]** Puck's nested worlds make a useful combination possible: the
gallery and table furniture stay locally presented; a remotely authoritative
table supplies each seat's permitted view; a distinct escrow world settles
issuer-backed chips. A portal or screen can expose that table without copying
the dealer's hidden arena into the room host. Authored `.puck` programs own
betting, legal moves, side pots, rake, and timeout policy. Generic engine
mechanisms own authenticated input, proof verification, resource bounds, and
durable economic effects. This is a proposed composition, not a code claim.

### The hypothesis: stakes as escrowed custody

**[inference]** The useful invariant is: a reserved unit is spendable by neither
its prior holder nor any second table until exactly one terminal settlement or
release decides its next spend right. Title may remain at home while custody
is reserved, but a lost wager deliberately changes entitlement. Companion
custody, which preserves title, is not by itself the complete wager model.

**[inference]** The enforcement point would be one accepted spend authority's
durable transaction door. It validates all input reservations and the settlement
certificate, then records consumption and new entitlements atomically. For
different issuers, lane B must specify the shared decision and recovery rules.
The table cannot mint a payout merely by writing a character's carried fact.

**[inference]** Freeze the roster, buy-ins, rules identity, house terms, and
recovery policy before a hand starts. A fold, a disconnect, an invalid proof,
a dealer outage, and an ordinary timeout are different events. A blanket refund
after a losing player disconnects gives that player a free option. Penalties
cannot safely depend on an opponent's unsupported assertion of a timeout.
Use an accepted ordering/witness service or explicitly accept its absence.

### The hypothesis: fairness by replay

**[inference]** A verifier needs more than the seed and a final state hash:

1. The exact rules, deck encoding, payout terms, seats, keys, and starting escrow
   reservations everyone accepted, plus a pinned interpreter or retained means
   of executing those semantics.
2. All accepted actions in their agreed order, including folds, rejected protocol
   steps where material, timeout decisions, and disconnect/recovery decisions.
3. Commitments witnessed before their dependent actions, seed openings or shuffle
   proofs, and proofs tying dealt/revealed cards to the accepted deck.
4. Evidence of agreement on one history: signed receipts, ordered acknowledgements,
   or an accepted ordering service. A signature alone proves no delivery time.
5. The final settlement decision and its durable escrow receipt, including a
   unique hand identity so a valid transcript cannot be paid twice.

**[inference]** Every seat should retain the public transcript and its own
signed actions and receipts independently of the host. An escrow authority or
agreed archive must retain the material needed to settle when the host vanishes.
Commitment hashes without available openings or proofs are not enough. Downloading
the only copy from an accused host after a dispute is not an evidence policy.

**[inference]** Hidden inputs cannot all be public during play. There are three
honest replay promises: reveal the full transcript after the hand; give a trusted
auditor the full transcript while players see a redacted one; or retain private
hands and verify cryptographic proofs of legal transitions. The first sacrifices
post-hand secrecy. The second trusts an auditor. The third requires proofs for
the actual rules, not just the shuffle. A redacted tape alone cannot reproduce
secret-dependent state and must not be described as full replay verification.

**[inference]** Derived products can remain caches, but signed receipts, committed
inputs, and unrevealed secret material are evidence, not regenerable caches.
Keep the authored program and accepted semantic identity needed to interpret a
hand. Later author edits must not silently change settlement of an earlier hand.
Replay can reproduce cryptographic proof-verification results deterministically
without reproducing participants' private key generation.

### Attack coverage limits

**[inference]** A1 and A2 require issuer-scoped acceptance, regardless of the
dealing option. A3, A7, A8, A13, and A15 require lane B's durable custody and
recovery work; table proofs alone close none of them. A9 requires trustworthy
issuer authorization, not just verified signatures. A11 and A12 remain relevant
because proof verification and reservation spam consume resources: bound bytes,
proof work, outstanding hands, and reservations before expensive processing.
A6 and A10 concern cross-authority interaction outside this table settlement;
no option here claims to fix them. A14 and A16 concern sanctioned disclosure and
observation; keep their intended behavior rather than treating every copy or
spectator as an attack. Every table option still admits ordinary player collusion.

## How the debug world would prove it

These are **[inference]** implementation and measurement packets, not completed
checks. They use the gallery's card room, real table rules, and actual custody
doors. No new experimental world or hand-written fake banker is the proof.

| Step and owner | Smallest scenario in `worlds/debug` | Reading that decides it |
|---|---|---|
| Value classes: lane B with lane E | One character carries an unsigned score, a signed achievement, and a funded chip receipt through the same seam | Only the funded receipt can reserve chips; replaying it at two tables yields one accepted reservation |
| Escrow: custody implementation lane | Two seats reserve chips; interrupt before and after each durable decision, then retry and restore the player copy | Available plus reserved plus settled value is conserved; one terminal decision; repeated collection changes no balance |
| Atomic hand: rule/economy lane | An authored wager attempts two debits and a pot update, with a deliberate rule fault between them | No partial economic effect; a recovery replay commits the same unique operation or none |
| Trusted dealer: disclosure lane | A player hosts the room and sits at a remotely hosted table; inspect room state, views, diagnostics, and received traffic | Host sees only its permitted cards; dealer still sees all cards, as documented; changing another hand changes no forbidden recipient observation |
| Public draws: table protocol lane | Commit seeds, accept stake, then try changed seeds, reused hand IDs, omitted reveals, and a host-selected retry | Invalid changes refused; abort policy resolves without a free reroll; peers retain the pre-draw commitments |
| Encrypted deal: cryptography lane | Two, six, then ten seats shuffle and deal a standard deck; inject a substituted card, invalid key proof, and invalid decryption share | Each invalid proof is refused before play/payout; valid deck membership and selective reveal verify independently |
| Withholding: cryptography and custody lanes | Drop each seat after commitment, shuffle, private deal, and final bet | Named recovery or abort outcome; no unauthorized card reveal; no refund that reverses an already final loss |
| Replay availability: table protocol lane | Stop the participant host after settlement proposal; a different verifier uses retained peer/escrow evidence | Same settlement verdict without contacting that host; missing evidence yields an explicit unresolved state, not invented inputs |
| Author edit: rule/economy lane | Change table rules while one hand is funded and start a second hand | Each settles under its own accepted terms; old evidence remains interpretable |
| Presentation versus custody: content lane | Force a stale double chip icon and reload its view | Inventory reading still has one spend right; the diagnostic distinguishes rendering from economic duplication |

**[inference]** The cryptography measurement uses a fixed candidate suite and
published security parameters, with two, six, and ten independent processes.
Record bytes, message rounds, proof generation/verification work, peak memory,
and end-to-end deal latency on bar devices under representative network delay
and one slow seat. The lead owns quiet timing runs; this lane reports no local
latency result. Decide whether setup fits the owner's between-hand delay budget,
whether per-tick admission remains bounded, and whether recovery meets the
chosen coalition and outage model. A primitive microbenchmark alone cannot pass.

**[inference]** The owner can choose table-local score while these mechanisms
are proved. That preserves the casino experience without quietly promising
exportable economic integrity. It is an option, not permission to delete
existing table or disclosure capabilities.

## Open questions and owners

- **Owner:** Is the casino fantasy about table-local score, persistent earned
  chips, tradeable goods, purchased play, or cash-out? Which territories and
  ages are intended? The answer changes both authority and legal work.
- **Owner:** Is an independent dealer acceptable? Must mucked hands remain secret
  after play? What waiting time and disconnect outcome fit the experience?
- **Lane B:** Which spend authority survives character and place rollback? How
  do issuer reservations, multi-issuer decisions, fencing, and incident recovery
  compose? A timeout cannot independently justify both refund and payout.
- **Lane E:** Confirm the current random-stream, rule-fault atomicity, tape,
  signature, and economic write paths. This page only independently checked the
  host's definition/arena access and local output trust.
- **Cryptography implementation lane:** Select and review a complete malicious-
  participant protocol, including authenticated broadcast, proof encoding,
  key validation, private dealing, rejoin, and selective abort. Measure it with
  the debug packet; do not adopt a library as the specification.
- **Lead and counsel:** Establish current jurisdiction-specific treatment of the
  exact proposed product, including cross-world convertibility and downstream
  markets. The opened historical rulings are not a legal currency guarantee.
- **Research follow-up, lead:** Recover primary technical postmortems for Diablo
  II and RuneScape, and the precise UO repair if available. Current records do
  not support assigning a specific database race to each. Albion's cited report
  is a denied dupe allegation, not an unresolved confirmed exploit.

## Source list

The following sources were opened unless explicitly marked as summary-only or
failed. Linked excerpts support only the scope stated above. Mirror provenance
is identified; inaccessible sources are not treated as read.

### Repository sources

- [Agent rules](../../../AGENTS.md), scope, determinism, and lane limits.
- [Delegate skill](../../../.claude/skills/delegate/SKILL.md), evidence and finish.
- [Verification skill](../../../.claude/skills/verification/SKILL.md), private CLI.
- [Orchestration skill](../../../.claude/skills/orchestration/SKILL.md), branch work.
- [World skill](../../../.claude/skills/puck-world/SKILL.md), area routing.
- [Gaming bricks skill](../../../.claude/skills/gaming-bricks/SKILL.md), opened for
  routing only; its emulator scope does not own card-table cryptography.
- [Documentation manual](../../README.md), area routing.
- [Documentation procedures](../../development/documentation.md), prose and skills.
- [World model](../../architecture/worlds.md#the-idea-in-plain-terms).
- [Round questions](../mmo-foundations.md).
- [Attack catalogue](attacks.md).
- [Disclosure plan](../disclosure.md).
- [Card table packet](../open-items.md#card-tables-in-the-board-game-room).
- [WorldServer](../../../src/Puck.World.Server/WorldServer.cs), `Definition`.
- [WorldServer arena](../../../src/Puck.World.Server/WorldServer.Arena.cs), `Arena`.
- [World output hub](../../../src/Puck.World.Server/WorldOutputHub.cs), `WorldSinkDisclosure.Full`.
- [Deal placement](../../../src/Puck.World.Server/WorldTick.Deals.cs), searched
  for seed references only; not evidence about the rule draw algorithm.

### Economy and trade sources

- [Diablo II Realm FAQ][d2-realms], primary Blizzard documentation.
- [Diablo II beta patch notes][d2-patch], opened, no dupe fix established.
- [Diablo II item basics][d2-items], opened, no root-cause claim taken from it.
- [Blizzard versus hacked items archive][d2-hacks], summary-only; opening refused.
- [Blizzard statement search excerpt][d2-statement], summary-only, unopened archive.
- [WoW cancellation report][wow-bug], primary player observation, not a studio diagnosis.
- [WoW closure and repair][wow-fix], primary Blizzard response.
- [WoW linked reply][wow-reply], opened; no separate technical claim used.
- [WoW manual][wow-manual], summary-only; opening timed out.
- [EVE POS exploit postmortem][eve-exploit], primary engineering and economy account.
- [EVE direct trade][eve-trade], primary interface contract.
- [EVE buy and sell orders][eve-market], primary matching contract.
- [EVE auction contracts][eve-auction], primary award and collection contract.
- [EVE courier contracts][eve-courier], primary custody and failure contract.
- [RuneScape coin-pouch account][rs-pouch], summary-only secondary account.
- [RuneScape rollback report][rs-rollback], summary-only contemporary reporting.
- [Duplication history][duping-history], summary-only; party-hat lead, not a code source.
- [UO economy paper mirror][uo], Origin Research Fellow's paper.
- [Albion developer statement][albion], search text only; opening refused.
- [New World official fix notes][nw-fix], primary mitigation and repair evidence.
- [New World shutdown report][nw-freeze], summary-only secondary incident context.

### Cryptography sources

- [Shamir, Rivest, and Adleman: Mental Poker][sra], scanned primary paper;
  retrieved but body text not extracted, not used for a security claim.
- [Barnett and Smart author abstract][mental-paper], primary abstract only.
- [Mohnblatt implementation account][mental-one], primary implementation report.
- [Mohnblatt technical explanation][mental-two], primary implementation explanation.
- [Geometry mental-poker repository][mental-code], opened README; not audited or run.
- [Kurosawa, Katayama, and Ogata][mental-lazy], primary abstract only.
- [Castellà-Roca thesis][mental-thesis], summary-only excerpt; opening timed out.
- [RFC 9381][vrf], primary cryptographic specification.
- [Stake implementation][stake], primary operator description, not certification.
- [Arkin and colleagues' investigation mirror][poker-bug], primary researcher paper.

### Regulatory sources

- [Belgian Gaming Commission report translation][belgium], primary text mirrored
  by a trade publication; the document says Dutch is official.
- [Belgian parliamentary discussion][belgium-parliament], primary record.
- [Dutch Raad van State judgment][netherlands], primary court judgment.
- [DCMS evidence response][dcms], primary policy response.
- [Gambling Commission currency guidance][ukgc], primary regulator interpretation.
- [Chinese Ministry of Culture notice][china], primary notice on a government site.
- [Kater opinion][kater], primary appellate opinion.

### Other attempted sources

These attempts supplied no usable body and support no independent conclusion:
[UO original](https://www.mine-control.com/zack/uoecon/uoecon.html),
[RuneScape official status](https://secure.runescape.com/m=news/old-school-runescape-status-update?oldschool=1),
[RuneScape wiki copy](https://oldschool.runescape.wiki/w/Update:Old_School_RuneScape_Status_Update),
[RuneScape Fandom copy](https://oldschoolrunescape.fandom.com/wiki/Update:Old_School_RuneScape_Status_Update),
[Schneier URL](https://www.schneier.com/academic/archives/1999/09/online_poker.html),
[Developer.com original](https://www.developer.com/guides/how-we-learned-to-cheat-at-online-poker-a-study-in-software-security/),
[New World current-issues URL](https://www.newworld.com/en-us/news/articles/update-on-current-issues-1),
and [Diablo character FAQ URL](https://classic.battle.net/diablo2exp/faq/char.shtml).

[d2-realms]: https://classic.battle.net/diablo2exp/faq/realms.shtml
[d2-patch]: https://classic.battle.net/diablo2exp/beta/patchchanges.shtml
[d2-items]: https://classic.battle.net/diablo2exp/items/basics.shtml
[d2-hacks]: https://www.purediablo.com/d2wiki/Blizzard_vs._Hacked_Items
[d2-statement]: https://diablo2.diablowiki.net/Blizzard_vs._Hacked_Items
[wow-bug]: https://us.forums.blizzard.com/en/wow/t/bug-receiving-payout-for-cancelled-auctions/1308310
[wow-fix]: https://us.forums.blizzard.com/en/wow/t/927-auction-house-is-temporarily-closed/1308442
[wow-reply]: https://us.forums.blizzard.com/en/wow/t/bug-receiving-payout-for-cancelled-auctions/1308310/20
[wow-manual]: https://us.media.blizzard.com/manuals/wow/wow-classic-manual-enUS.pdf
[eve-exploit]: https://www.eveonline.com/news/view/war-makes-thieves-and-peace-hangs-them
[eve-trade]: https://support.eveonline.com/hc/en-us/articles/14142146096412-Direct-Trade
[eve-market]: https://support.eveonline.com/hc/en-us/articles/203218932-Buy-and-Sell-Orders
[eve-auction]: https://support.eveonline.com/hc/en-us/articles/203280371-Auction-Contracts
[eve-courier]: https://support.eveonline.com/hc/en-us/articles/203218982-Courier-Contracts
[rs-pouch]: https://osrsindex.com/wiki/coin-pouch-glitch?site=osrs_wiki
[rs-rollback]: https://www.pcgamesn.com/old-school-runescape/old-school-runescape-max-cash-bug
[duping-history]: https://en.wikipedia.org/wiki/Duping_%28video_games%29
[uo]: https://nelderim.pl/wp/wp-content/uploads/2020/05/The-In-game-Economics-of-Ultima-Online.pdf
[albion]: https://forum.albiononline.com/index.php/Thread/188333-Statement-regarding-the-Rumors-of-Duplication-Bugs/
[nw-fix]: https://www.newworld.com/en-gb/news/articles/new-world-update-1-0-5
[nw-freeze]: https://arstechnica.com/gaming/2021/11/new-world-shuts-its-economy-again-amid-widespread-item-duplication-glitch/
[sra]: https://people.csail.mit.edu/rivest/pubs/SRA81.pdf
[mental-paper]: https://research-information.bris.ac.uk/en/publications/mental-poker-revisited/
[mental-one]: https://nmohnblatt.me/mental-poker-1/
[mental-two]: https://nmohnblatt.me/mental-poker-2/
[mental-code]: https://github.com/geometryxyz/mental-poker
[mental-lazy]: https://globals.ieice.org/en_transactions/fundamentals/10.1587/e80-a_1_72/_p
[mental-thesis]: https://crises-deim.urv.cat/web/docs/publications/theses/jCastella.pdf
[vrf]: https://www.rfc-editor.org/rfc/rfc9381.html
[stake]: https://stake.com/provably-fair/implementation
[poker-bug]: https://gwern.net/doc/cs/cryptography/2006-arkin.pdf
[belgium]: https://www.azarplus.com/fotos/file/11_05_18/onderzoeksrapport-loot-boxen-Engels-publicatie.pdf
[belgium-parliament]: https://www.dekamer.be/doc/CCRI/html/56/ic107x.html
[netherlands]: https://www.raadvanstate.nl/uitspraken/@130150/202005769-1-a3/
[dcms]: https://www.gov.uk/government/calls-for-evidence/loot-boxes-in-video-games-call-for-evidence/outcome/government-response-to-the-call-for-evidence-on-loot-boxes-in-video-games
[ukgc]: https://www.gamblingcommission.gov.uk/authorities/codes-of-practice/guide/page/digital-and-virtual-currencies
[china]: https://www.cac.gov.cn/2016-12/06/c_1120060575.htm
[kater]: https://cdn.ca9.uscourts.gov/datastore/opinions/2018/03/28/16-35010.pdf

[MMO foundations](../mmo-foundations.md) · [Plans](../README.md)
