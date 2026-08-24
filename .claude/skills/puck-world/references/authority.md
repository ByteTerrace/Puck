# Authority — the grant table, principals, and the co-driving fold

ONE server-side table authorizes every write: `WorldGrants`
(`src/Puck.World.Server/WorldGrants.cs`). Protocol vocabulary:
`src/Puck.World.Schema/WorldGrant.cs`, `WorldPrincipal.cs`,
`ChannelPolicy.cs`, `WorldPrincipalMapping.cs`. The capability-channels campaign that designed this model was retired 2026-08-10 and its rulings moved into the code above — read the CODE for current rulings, and `docs/campaign.md` for what remains as work.

## Contents

- Vocabulary
- `Allows` returns a verdict, never a bool
- ONE admission predicate decides a mutation
- Trusted vs untrusted is two predicates, not one
- Subject legitimacy and grant-door rules
- One admission door, every ingress
- Boot seeding and document grants
- The co-driving fold
- Ingress stamping — the acting-principal rule
- Handles (untrusted designation)
- Budgets
- Refusals and read-backs
- Verifying

## Vocabulary

- `WorldCapability` (5 members): `Drive`, `Observe`, `Control`, `Mutate`,
  `Edit`. There is no `Present`; ABI capability bit 2 is a permanently
  reserved hole (see [addons.md](addons.md)).
- `WorldPrincipal` (`PrincipalKind`, 7 members): `Seat` (index 0–3), `Console`,
  `Addon(name)`, `Peer(index, generation)`, `Document(id)`, `World`, `Group(id)`
  (the group/ownership wave's addition — a group never ACTS: nothing stamps it
  as an ingress's acting principal, so it never reaches
  `WorldPrincipalMapping` or the wire codec as a submitter; it exists only as
  a grant TARGET and a membership-row value, expanded fresh on every
  `Allows` check — see "`Allows` returns a verdict" below). Admitted peers occupy indices
  4–127 and carry a positive generation. The shared console/JSON parser
  accepts any nonnegative peer index with a positive generation; authored
  world validation caps it below 128. Tokens: `seat1..seat4` (1-based),
  `console`, `addon:<name>`, `peer:<n>:<generation>`; `Describe()` emits the
  same generation-bearing peer token. `Document(id)` (`document:<id>`) is the
  fifth kind: ANOTHER world document asking this document's authority to act,
  and it only ever appears as a grant ROW in the `grants` section — the
  cross-document durable-state write-back channel reads its rows off the
  OWNER IDENTITY's own DOCUMENT (`WorldOwnedWorlds.Decide`/
  `TryReadDurableState`), never off the runtime table, and never off the visited
  world's `grants` either. It therefore never enters the LIVE table at all:
  both document-`grants` replays skip a `document:` row
  (`WorldServer.IsDocumentChannelRow`) and the grant door refuses one by name
  (`Conflicts` rule (-1b)), because a live row would be budget-less, mask-less,
  and read by nothing. The live wire refuses it BY NAME, pointing at
  the document's own authored `grants` section as where a `document:` capability
  lives — `world.grant.set`/`world.grant.remove` edit the VISITED world's own
  `grants`. But the cross-document write-back channel reads the RECIPIENT
  identity's OWN document `grants` (a separate owned-world file), which
  `identity.create` seeds `grants: []` and does not itself author. **CLOSED
  (2026-08-06, C-CHAT core lane):** `Puck.World`'s `ChatCommandModule`
  (`chat.inbox` declares a recipient's own bounded, evicting `chat-log`/
  `chat-inbox` state rows; `chat.allow`/`chat.block` grant/revoke a sender
  `document:<id>` Mutate+`state:chat-inbox`, Set-only) is the in-session door —
  gated OWNER-ONLY at that one door (`context.ActingPrincipal()` must hold
  `Drive` over the target player's body, never trusted from the verb's own
  arguments). `chat.whisper` is the real cross-document whisper verb (source id
  derived from the acting player's own identity, submitted through the same
  `WorldOwnedWorlds.Submit`/`Decide` pair `identity.deliver`'s dev harness
  exercised). A companion widening lets `Decide`'s text arm land in a bounded, evicting KEYED row, not just a slot. `world.grants` echoes the
  document-authored rows in their own `[world.grants.document: …]` group so the
  skip is not a disappearance. `World` (`world`) is the sixth kind: THE WORLD'S OWN
  AUTHORED PROGRAM — a `rules` effect, or a kit's `generate` effect. It holds no
  grant rows (the grant door refuses one by name, the DOCUMENT VALIDATOR refuses
  an authored one so a document cannot validate against itself, and the wire
  refuses one as a nested row: a row for it would be accepted-and-inert), it
  never crosses the wire as an actor, and
  `TryAdmitMutation` admits it STRUCTURALLY before consulting the table. That is
  the same standing a per-body `ActionEffect` always had — an authored program
  has no submitter — now named because these effects write the DOCUMENT, which
  has a door. `TryParse` accepts the `world` token so `world.why world …` can
  answer for it (`allowed (structural)`), never so an ingress can stamp it.
- `GrantSubject` (`GrantSubjectKind`): `all`, `body:<n>` (0-based entity
  index), `screen:<n>`, `section:<name>`, `state:<name>` (string-keyed,
  naming a state row — there is no `profile:<id>` kind; `GrantSubjectKind`
  declares All/Body/Screen/Section/Composition/State/Region/Seat/Creation/
  Placement/Adjacency and nothing else), `creation:<id>`/`placement:<id>` (one
  creations/placements row apiece — the ROW-SCOPED `Mutate` subjects, an
  ALTERNATIVE to the section hold rather than a narrowing beneath it; the id
  is shape-checked, never bound-checked, because authoring a row that does
  not exist yet is what the grant confers, and an `Addon` principal is
  refused one by name since its mutation seam designates a section handle),
  `region:<name>`
  (a placement's `WorldPlacementRegion` facet, Observe-only), `seat:<n>`
  (0-based local seat, Observe-only), `adjacency:<name>` (an authored
  `adjacencies` row — Region's federation-seam twin, Observe-only, the
  `linkEstablished`/`linkDropped` event family's gate; like `region:` it is
  never bound-checked — an unknown name simply never fires), plus
  `Composition` — write-only (echoed by `world.grants`, no parse token; only
  the boot seed constructs it). `section:` must be alphabetic and
  `Enum.IsDefined` — a numeric `section:5` is refused. `region:`/`seat:`/
  `adjacency:` are
  legitimate for UNTRUSTED principals only (see references/addons.md's
  "World events" section) — no trusted principal reads the Observation
  cells they gate.
- A grant row is `WorldGrant(Principal, Capability, Subject, Exclusive,
  Budget?, Reach?, Consent?, Ceiling?, VerbMask?, EventBudget?)`. The key is
  the triple; the rest is payload. A `VerbMask` narrows a concrete-section
  Mutate row to declared mutation-kind ordinals; `EventBudget` is the world-
  events feed's nonzero admission gate, a sibling of `Budget` (dispatch)
  over the same row. Its numeric value is not consumed as a rate. See
  [addons.md](addons.md).

## `Allows` returns a verdict, never a bool

`WorldGrants.Allows(principal, capability, subject)` → `GrantVerdict(Rule,
Reserver?, Group?, GateRow?)`; `GrantRule` (8 members) = `NoHold`,
`BeatenByReserver` (both denials in the `grant.authority` refusal door),
`ReserverMatch`, `ConcreteHold`, `WildcardHold`, `GroupHold`, `OwnershipHold`
(these five are the ALLOWING rules — `GrantVerdict.IsAllowed`), plus
`DriveGated` — a THIRD `grant.authority` denial, but a state FACT rather than
an `Allows` outcome: a nonzero cell on a `GatesDrive` state row, checked only
at the intent-admission door (`WorldServer.ApplyIntentSubmission`) and its
`world.why` read-back, never returned by `Allows` itself. Check order inside
`Allows`: exclusivity reservation (exact, then wildcard) → concrete hold →
`all` hold → group hold (the caller's CURRENT membership in a group whose own
row names the subject, resolved fresh every check, never cached at grant
time — `GrantVerdict.Group` names which group decided it) → ownership hold
(a group the caller OWNS, directly or transitively, whose own row names the
subject — same fallback shape as group hold, checked after it) → `NoHold`.
The verdict is produced INSIDE the decision (never derived after the fact)
and carries which rule fired — `world.why` echoes it. `AllowsAllSections`
reports the first refusing
section; it gates a whole-document rebuild (`world.reset`/`world.load`/
`world.reload`) and `world.undo`. A rebuild additionally wipes and re-seeds
the ENTIRE runtime grant table (`WorldGrants.Reset` — "runtime grants
otherwise drop; document grants re-apply as at boot"), replaying only the
NEW candidate document's own `Grants` section plus every currently-admitted
peer connection's re-minted admission grant; every other live
`world.grant`/`world.revoke` acquisition is gone after a rebuild, by
design — see [mutations.md](mutations.md).
`HoldsForAdministration` (who may grant/revoke FOR another principal) is a
separate door: `Console` unconditionally; a `Seat` only over its OWN body
subject; `Addon`/`Peer` only what they themselves hold (ignoring
exclusivity, so an actor can always revoke an exclusive hold it authorized).

## ONE admission predicate decides a mutation

`WorldServer.TryAdmitMutation(principal, section, kindOrdinal,
rowScopedEditSubject, meter, out admission)` owns the WHOLE authority decision
for a document write. ONE structural exemption runs first — a `World` principal
is admitted (`WorldMutationAdmissionRule.Structural`) without any lookup; there
is no bypass parameter and nothing else may exempt. Then four gates, in order:

1. `Allows(Mutate, section:<name>)` — the coarse section hold — OR, when
   the mutation names one concrete creations/placements row and the section
   check missed, `Allows(Mutate, creation:<id>|placement:<id>)`. A
   DISJUNCTION, unlike gate 3: a section grant admits every row, a row grant
   admits only its own. That scoping is also the whole cure for the compose
   arms' replace-by-key — a row grantee cannot name another row to collide
   with, so no ownership check belongs on the compose arm.
2. The DECIDING Mutate row's `MutationKindMask` (the row's own when a row
   hold decided, the section's when the section did).
3. For a `State` mutation only: `Allows(Edit, state:<name>)`, then THAT
   deciding row's own kind mask.
4. For an UNTRUSTED principal: the per-tick dispatch budget, charged through
   one `WorldMutationBudgetMeter` keyed `(principal, section)` and reset at the
   top of `WorldServer.Step`.

"Deciding row" always means the rule the verdict itself reports —
`ConcreteHold` beats `WildcardHold` — never a union of a concrete and a
wildcard row's masks.

**An ABSENT kind mask is FULL REACH at this predicate.** A mask is opt-in
narrowing beneath an already deny-by-default capability, never a second
authority check. Untrusted strictness lives at the GRANT door instead
(`Conflicts` refuses a maskless untrusted `Mutate`/`section:<name>` row), so an
unmasked untrusted row is unreachable rather than permissive. `world.why`'s
`verbs:` diagnosis states exactly this rule and now agrees with every door.

**Two call sites, one rule.** `WorldServer.TryApplyMutation` covers the whole
ordered domain — loopback, console, and the `WorldTcpHost` peer door all
converge there, so a peer gets the same masks and metering an addon does, from
the same code. The addon seam
(`WorldAddonRuntime.ResolveMutations`) keeps its own EARLIER call site: it
refuses before decode so a guest cannot probe the decoder for free. It passes
`rowScopedEditSubject: null` (a row name is only knowable after decode, so gate
3 runs later at apply) and `meter: true`; the apply path then passes
`preMetered: true` for that op (`PendingOp.Mutate.SourceAddonInstanceId`) so one
guest dispatch is never charged twice. Call-site duplication is fine; rule
reimplementation is the defect class this predicate closed.

`WorldMutationAdmission.Describe()` produces the refusal sentence BOTH doors
print, so a narration can never disagree with the decision.

## Trusted vs untrusted is two predicates, not one

There are two DIFFERENT trust boundaries in this file, and they diverge on
`Addon`. Conflating them
is the wrong-answer trap.

**Administration/metering trust** (`WorldGrants.Conflicts`'s untrusted-budget
requirement, `IsLegitimateSubject`'s wildcard gate, `HoldsForAdministration`):
`Console` and `Seat` are trusted; the COMPLEMENT (`Addon`, `Peer`, any future
kind) is untrusted, computed by predicate rather than by name:
every mounted addon is still `PrincipalKind.Addon`, still budgeted
(`budget:<n>` required on its Drive/Observe/Mutate rows), still handle-mediated,
still never wildcarded, still never ceiling-carrying (a `Ceiling` may only
ride a `Seat`'s own body row — see `IsOwnSeatBody`).

**The fold's OWN contributor-trust predicate** (`WorldServer.StageContribution`,
`WorldAddonRuntime.ContributionAccepted`) **keys on HOST LOCUS, not principal
kind by coincidence of vocabulary.** `Console` and `Seat` are trusted exactly
as before — a human's own tool, added outside the pool, wholly unmasked. A
document-mounted `Addon` is ALSO trusted-in-fold. It is
WORLD LOGIC authored by the world itself, kept deterministic with a known fuel
budget precisely so it could be trusted; consent does not apply to world logic
(a world doesn't ask permission to apply wind) — but UNLIKE Console/Seat, its
term still respects its OWN declared `ChannelReachMask` (data describing which
channels the world logic touches), never a seat-authored ceiling: an addon
that declares no reach still contributes nothing. `PrincipalKind` carries no
host-locus field, so `PrincipalKind.Addon` alone stands in for "server-mounted"
only because no OTHER host locus exists yet — a future client-hosted addon
needs its OWN kind here, never a silent share of this one. `Peer` (and that
future client-hosted addon) stay pooled under `Reach ∧ Consent`, unaffected.

## Subject legitimacy and grant-door rules

`IsLegitimateSubject` is a POSITIVE per-capability rule — a new subject
shape is refused by default: Drive takes `body:<n>` (bounded by the
population) or `all` (trusted only); Observe takes `body:<n>`, plus
`screen:<n>`/`region:<name>`/`seat:<n>`/`adjacency:<name>` for untrusted event
consumers, or
`all` for trusted principals; Control takes `screen:<n>` (any),
`body:<n>` (any, bounded by the population — a control-application possession
target, [engagement.md](engagement.md)), `composition` (trusted), `all` (trusted
or Peer); Mutate takes `section:<name>`/`creation:<id>`/`placement:<id>` (the DISPATCH lane) or `state:<name>`
(the CROSS-DOCUMENT write-back lane) or `all` (trusted); Edit takes
`state:<name>` or `all` (trusted). The four State mutation kinds
(`UpsertStateRow`/`RemoveStateRow`, `UpsertStateCell`/`RemoveStateCell`, plus
`Generate`, which names the row it WRITES) are the one set checked against BOTH a
section-Mutate hold AND a row-concrete Edit hold (see [mutations.md](mutations.md)'s "Double authority check") — every
other section's mutation checks Mutate alone.

`TryGrant` rule ladder (highlights): a `world` or `document:` principal holds no
live row at all (rules (-1)/(-1b), above); an UNTRUSTED principal is REFUSED
`Mutate`/`section:rules` outright (owner ruling — a rule's EFFECTS act as
`WorldPrincipal.World`, which the admission door admits structurally and never
meters, so one gated authoring act launders every budget and verb mask the row
carries, and a verb mask cannot bound what the row does not dispatch; trusted
principals are unaffected); exclusive-over-`all` refused outright;
`budget:<n>` is REQUIRED on an untrusted principal's Drive/Observe row and on
its `Mutate` dispatch row (`section:`/`creation:`/`placement:`), and refused everywhere else — including an
untrusted `Mutate`/`state:<name>` row, which is the cross-document write-back
channel and has no dispatch door to meter (`budget:0` refused — omit the token
instead; a re-grant IS the budget update). `verbs:<name,...>` is likewise
REQUIRED on an untrusted `Mutate` dispatch row: the admission
predicate reads an ABSENT mask as FULL REACH (Console's boot seed holds
maskless Mutate rows, so refuse-all there would deny every trusted mutation),
so the strictness lives at the grant door and a maskless untrusted row is
refused before it can exist. `events:<n>` is Observe-only, required for
untrusted `screen:`/`region:`/`seat:`/`adjacency:` rows, optional for an
untrusted
`body:` row, and refused everywhere else; every such untrusted Observe row
still also requires `budget:<n>`. Co-drive payloads (`Reach`/`Consent`/
`Ceiling`) are Drive-only — a `Ceiling` must ride the seat's OWN body row,
must name `channels:`, must not carry reach, and must be in raw
`(0, One]`; a bare `Reach` on a trusted row is refused (a trusted
contributor is never masked). Exclusivity conflicts block in both
directions, except boot-seeded per-section Mutate rows never block an
exclusive acquisition. Revoking the seat's own Drive row is the only way to
clear authored ceilings.

## One admission door, every ingress

`WorldServer.TryAdmitVerifiedParticipant` is the only path from an ingress to a
population body plus grant rows, and it takes a `WorldAdmissionVerdict` — never
raw `WorldGrant` rows. Only `Protocol.WorldAdmissionDoor` mints a verdict:
`TryAdmit` (a verified attestation claim at the TCP hello), `TryMatchEntry` (an
already-verified identity re-matched against a rebuild candidate), and
`TryAdmitArrival` (an authenticated federation authority's namespace). No
verdict means a named refusal, never a default seed.

The `admission` section therefore governs transfers too. A row in
`WorldAdmissionTrustMode.FederatedAuthority` mode carries no key: its `domain`
is the authenticated source-authority namespace, or
`WorldAdmissionEntry.AnyAuthority` (`*`) for any authority that completes
`Puck.Networking.IAuthenticator`'s signed-claim challenge/proof handshake
(`WorldAttestedAuthenticator`); a named row beats the wildcard in either
authored order. The door skips such rows when building its attestation
trust list, so a document authoring arrivals alone still admits no
connecting peer, and `TryAdmit` answers `NoAdmissionEntries` there.
`WorldAdmissionRefusal.NoArrivalAuthority` names the arrival-side miss. Every
shipped world authors a `*` arrivals row, because the authenticated namespace
is `WorldAttestedAuthenticator`'s own verified claim subject — the document's
own `host.authority` when authored, else the boot instance identity — never a
label a connecting peer merely claimed.

A template's `Subject` is NULLABLE and means "the body this admission assigns"
when absent — `WorldAdmissionGrant.SubjectFor(bodyIndex)` is the one resolution
point, used by the mint, the rebuild re-mint, and the escrow's reserve-time
check alike. That is how an authored row can confer `Drive` over a body index
nobody knows until admission runs.

`WorldTransferEscrow.Reserve` runs the arrival door ONCE and carries the
verdict on the lease to `Commit`, so reserve and commit cannot disagree; its
per-slot authorization asks the same question of whichever authority will drive
the body — the live grant table for a colocated traveller keeping its own
principal, the verdict's templates for a live peer arrival, and source support
alone for an autonomous traveller, which has no driver at all.

A body that is neither a local seat nor an admitted peer travels as
`WorldPrincipal.World` (`WorldInstanceHost.TravelPrincipal`), which holds no
grant row and is admitted structurally only over a body the world's own program
authors — never `Console`, whose `Drive/all` would also cover every seat and
peer.

## Boot seeding and document grants

Constructor seed: each seat gets `Drive` over its OWN body only, plus the
domain seed (`Observe/all`, `Control/all`, `Edit/all`,
`Control/composition`, `Mutate/section:<s>` for every section EXCEPT
`grants`); Console gets the domain seed including `Mutate/section:grants`
plus the table's only `Drive/all`; addons get NOTHING. Peers are not seeded by
index: each `PeerAdmitted` event mints the admission verdict's own authored
templates for that exact generation, and disconnect/reactivation revokes
stale-generation rows before re-minting. **A generation's rows die with its
connection**, at the `PeerDisconnected` event itself — never at the reconnect
grace deadline, which governs only the parked BODY. A verified-identity
reconnect that resumes the parked body re-mints its admission templates
through the ordinary `PeerAdmitted` event, so only live acquisitions beyond
the templates fail to survive the gap (see
[session-lifecycle.md](session-lifecycle.md)); a checkpoint restore releases a
restored park's rows the same way, at `RestoreCheckpoint` itself. An
`Exclusive` subject a peer reserved is therefore acquirable by another
principal immediately after it drops, with no tick in between — and stays
with whoever took it (the template's re-mint refuses loudly). A census/inhabitant activation, which
verifies no identity at all, still mints the `Control/all` seed
(`BuildDefaultPeerControlGrants`) — population housekeeping, not an admission.
A world document's `grants` section applies in the `WorldServer`
constructor, in document order, under the Console actor, through the same
`Grant` path `world.grant` uses — same loud accept/reject lines.
`WithoutAuthoredConsent` strips `Reach`/`Consent`/`Ceiling` from any
document row carrying a ceiling, prints the withholding loudly, and applies
the row with no pool: consent is authored LIVE by the seated human, never
shipped in a document. (A document contributor row carrying only `Reach`
passes through untouched.) `UpsertGrant`/`RemoveGrant` mutations edit the
DOCUMENT rows only — next boot, never the live table.

## The co-driving fold

Masks (`ChannelPolicy.cs`, four of them, all `ulong`-bit
`readonly record struct`s):

| Mask | Gates |
|---|---|
| `ChannelReachMask` | which ordinals a Drive row may reach (`channels:` without `ceiling:`) — a genuinely untrusted contributor's manifest, OR a document-mounted addon's own declaration of which channels its world logic touches |
| `ChannelConsentMask` | which ordinals the OCCUPYING SEAT authored a positive pool ceiling for — consulted ONLY for a genuinely untrusted (pooled) contributor |
| `ChannelHeldMask` | ordinals admitted into this tick's fold — either the pooled meet (`Reach ∧ Consent`) or a trusted addon's own Reach directly |
| `ChannelDeclaredMask` | the ordinals a mounted guest declares (handshake) |

`ChannelReachMask.Meet(consent)` (routed through `Puck.Maths.MeetMask64`) is
the pooled `Reach ∧ Consent` narrowing — live ONLY for a genuinely untrusted
contributor (`Peer` today). Ceilings are per-ordinal (`ChannelCeilings`,
support-masked), read from the SEAT'S OWN row (`PoolCeilings(seat,
body:seat)`), never derived from contributor rows, and are simply never
consulted for a trusted contributor's term (Console/Seat/document-mounted
Addon).

Routing in `ApplyIntentSubmission`: the OWNING seat (or any principal on an
unoccupied body — a bot at full authority by construction) overwrites
directly with contention tracking. Everything else stages
(`WorldServer.StageContribution`), three ways: Console/Seat deltas sum unmasked and unpooled
(`outsidePoolDeltaRaw` — a human's own tool is never bounded by consent); a
document-mounted Addon's deltas ALSO sum into `outsidePoolDeltaRaw` (trusted,
outside the pool) but only for ordinals its OWN declared Reach contains — no
seat consent needed or consulted; a genuinely untrusted contributor's deltas
(`Peer` today) are admitted per ordinal only where `reach.Meet(ceilings.Support)`
contains it — no consent authored means the delta is refused AT STAGING and
never reaches the fold. Only the THIRD (pooled) branch latches
`m_untrustedAcceptedMask`, so `body.channels` can prove the pool ran;
`body.channels`'s `trusted=[...]`/`untrusted=[...]` contributor tags follow
the SAME three-way split (a document-mounted addon now lists under
`trusted=[...]`).

`FoldChannelContributions` computes, per
occupied seat and ordinal, `Puck.Maths.FixedContributionFold.Evaluate`:
`quantize(clamp(clamp(h + poolDelta, h ± radius) + trustedDelta, min, max))`
— raw `long` accumulation, `Int128` intermediates, ONE end clamp (a
saturating per-add would be order-dependent), pool radius from the seat's
ceiling (raw 0 sentinel → null → no pool), binary channels quantized at the
threshold. The result replaces the pass-through write via one
`body.SubmitIntent`. With every raw term bounded by `FixedQ4816.One`, the
generic signed-`long` accumulator admits at most `2^47 - 1` terms independent
of sign; World's concrete contributor set is much smaller.

## Ingress stamping — the acting-principal rule

Every submission carries its acting principal: envelopes carry
`SubmissionEnvelope.Principal`; console text carries
`CommandContext.Principal`, stamped `CommandPrincipal.Console` by the text
door in `Puck.Commands.CommandRegistry` (both the fast path and the full
parse), by the snapshot mixer for pad lanes, and by injection sinks with
their constructed identity. **Handlers READ the stamp via
`context.ActingPrincipal()`** (`WorldPrincipalMapping` — the one seam that
mints a `WorldPrincipal` from anything other than a read; it throws on
`Unspecified` because that means a dispatch skipped a door). A handler that
constructs a principal is asserting an identity rather than carrying one —
the laundering defect class. `WorldPrincipal.Seat(n)`'s doc enumerates its
only legitimate direct callers; do not add one to attribute an action.
Client code never mutates local state before the server's verdict —
completions, not discarded replies.

## Handles (untrusted designation)

`WorldHandleTable` (`src/Puck.World.Server/WorldHandleTable.cs`) projects a
principal's grant rows into per-instance slots — `WorldHandle(Index,
Generation, TablePrincipal, TableCapability)`, host-stamped. Construction
throws for trusted principals (handing Console/Seat a handle table is
ceremony, not security). Refresh is revision-driven and KEEPS a slot's
generation when the subject is unchanged (the global revision moves on any
grant activity, so re-minting everywhere would fake revocations).
`TryResolve` refuses a foreign-table handle, an out-of-range index, or a
generation mismatch (revoked/re-sorted → the guest gets `StaleHandle`,
distinct from a denial: withdrawn and never-granted are different states).
A resolve yields a DESIGNATION only — the caller still asks `Allows`.
Wildcard subjects (`all`, `composition`) are never projected into handles.

## Budgets

`Budget` (ushort, `budget:<n>`, 1–65535) meters an untrusted principal's
per-tick dispatch. Consumed in `WorldAddonRuntime`: the Drive lane charges
BEFORE the authority fold (the budget meters compute already spent); the
Observe lane charges AFTER the authority verdicts (a denial stays precise
and costs no budget). Exhaustion answers `QuotaExhausted` with a
once-per-episode stderr line. Decode is NOT metered — it happens at
`TickAddons` against structural validation.

## Refusals and read-backs

- Denials are loud, by name, on stderr: `[world.grant denied: …]`
  (actor/Drive/Mutate denials), `[world.grant rejected: …]` (table refused
  the row), `[world.mutation rejected: …]`, contention
  `[world.grant: body:<n> driven by both … this tick — …]`.
- `world.refusals [door]` prints the DECLARED refusal catalog
  (`RefusalTaxonomy.cs` + `RefusalCatalog.cs`): 97 declarations across eight
  doors today: `addon.mutate` (11), `grant.authority` (3), `hud.validate`
  (11), `replay.tape` (9), `sdf.decode` (34), `world.rule.compile` (27),
  `world.interaction.compile` (1), `world.rule.effect` (1) — re-run the count
  (`puck search "\[Refusal\(" src -M 0`) rather than trusting this list once
  the surface moves again. It does NOT cover console-tier text refusals (parse
  errors, `Conflicts` reasons, module refusals) — never claim that
  coverage. Tagging is one-directional: it proves a door cannot refuse with
  an unlisted reason, not that every listed reason has a live call site.
- `wire.errors [reset]` counts rejections: synchronous text-path errors,
  snapshot re-parse mismatches, and deferred `WorldEditEcho` rejections
  (via `NoteDeferredRejection` in `Program.cs`). The per-tick drive denial
  in `ApplyIntentSubmission` is stderr-only and NOT counted.
- Read-backs: `world.grants [principal]` (echoes rows with `(x)` exclusive,
  `budget:`, `channels:0x…`, per-ordinal ceilings), `world.why <principal>
  <capability> <subject>` (allowed/denied + which rule + detail),
  `body.channels [player]` (per-declared-channel fold breakdown; the
  ceiling prints only when this tick's contribution set actually reached
  that ordinal through the untrusted path — proof the fold RAN, not that a
  grant exists; honest refusals for inactive or non-occupied bodies).
- `world.why` answers a row-scoped `mutate creation:<id>`/`placement:<id>`
  query in the door's OWN order — the owning section first, the row only when
  that misses — and says which carried it (`via mutate section:…` vs `via the
  row hold alone`). `world.why document:<id> …` answers `not-in-this-table`
  with where the capability actually lives, the sibling of the `world`
  principal's `allowed (structural)` branch.
- `world.grant` grammar: `<principal> <capability> <subject> [exclusive]
  [budget:<n>] [events:<n>] [channels:<name,...>] [ceiling:<f>]
  [verbs:<name,...>]`; trailing tokens may appear in any
  order, each at most once. `channels:` with `ceiling:` authors consent;
  `channels:` alone authors reach. `world.revoke` takes the bare triple and
  clears budget/reach/ceilings with it. Both verbs are `Simulation`-routed,
  return `CommandResult.None`, and let the SERVER print the loud line.
- **No new decision surface lands without its read-back verb, in the same
  change** (standing rule, restated in the state doc).

## Verifying

The acting-principal/administration contract is proved by
`AuthorityAdministrationLawTests` and the compose/dissolve-authority contract by
`EngageAuthorityLawTests` and `ControlApplicationLawTests`, both in `tests/Puck.World.Tests` with code-built
furniture. For ad-hoc work: every denial case
needs a control (actor holding the grant succeeds), keep actor ≠ target
(every seat is seeded wide, so self-targeting discriminates nothing), and
prove a new assertion once by breaking it.
