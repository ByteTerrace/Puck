# Companion custody: the redesign

The [companion decision](../decisions/companions.md) was built twice on branch
`worktree-agent-aefc6b37a2b3dc11c` and failed two cross-family reviews with the
same three classes each time: authority granted from a sender's or a stale
record's claim, away bookkeeping mutated by live paths that recovery and replay
never see, and a boot-random custody key outside every record. This memo
replaces the custody design. The owner's rulings stand: companions are not
networked players; a companion is the player's; same behaviour by default;
separated or refused means home with a notice.

The one principle: **custody state is derived from crossing-log records and taped
mutations, and nothing else writes it.** Live code, rollback, crash recovery and
replay read the same records through the same functions, so they cannot diverge.

## 1. One state machine

A companion is a body whose home placement's inhabit facet names a `role` and
the local seat it `follows`. Its custody is in exactly one of these states.

| State | Where the body is | The record that puts it there |
|---|---|---|
| **home** | Active in its home slot, `PlacementId` set, binding derived from the placement | The placement in the installed definition (taped mutation), or a durable return `Arrival` |
| **departing** | Detached at home; held in the in-doubt transfer | Home's `WorldCrossingRecord.Departure` (`WorldInDoubtTransferCheckpoint.Landed[]`, a `WorldLandedMemberCheckpoint` gaining `Companion`, `PlacementId`, `Custody`) |
| **away** | Active at a destination with a visiting binding (`Home`, `Role`, `Driver`, `Custody`, followed slot and generation); home holds its slot | Home's `Settlement(Arrived: true, Forwarded, Away)`: the settlement carries the away rows beside the forwarding routes, and the population checkpoint captures them |
| **returning** | Detached at the destination; in doubt toward home | Destination's `Departure` whose landed member carries the visiting binding |
| **stranded** | Active at the destination after a refused or undeliverable trip home | Destination's `Settlement(Arrived: false)` restores the body; the host remembers the retry tick (host memory only, nothing authoritative) |
| **released** | Nowhere: the destination despawned it, home forgot the row; the placement's census spawns a fresh companion with a new incarnation | New `WorldCrossingRecord.Release(Incarnation, Proof)` at each authority, and new `WorldReplayEntry.Release` on each tape |

Transitions, each caused by one authority input:

| Transition | Cause | Record |
|---|---|---|
| home → departing | Its player's transfer detaches it as a member of the **same cohort** (below) | Home `Departure`; tape `Departure`, now carrying the secret |
| departing → home | Commit refused, aborted or missing: rollback restores the landed member exactly (binding, placement, secret) | Home `Settlement(stayed)`; tape `Departure(Restored)` |
| departing → away | Commit confirmed | Home `Settlement(arrived, Away)`; tape `Transfer` |
| away → returning | Separation sweep (followed slot no longer holds the landed generation, player left), role refusal at a further destination, or the destination stopping | Destination `Departure` toward `TransferDestination.Existing(home)` |
| returning → home | Home verifies the proof at reservation, lands it in the held slot, makes the arrival durable, then re-adopts (row removed, placement rebind, kit, look, follows) | Home `Arrival`; `WorldArrivalOutcome.Readopted` records the held slot and placement per member, or null |
| returning → stranded | Home refuses or is not running | Destination `Settlement(stayed)` |
| stranded → returning | Retry every `roles.returnRetrySeconds` ticks (authored, default 10 s) | Destination `Departure` |
| stranded → released | Retries past `roles.returnGiveUpSeconds` (default 300 s), or home answered that it holds no row for the incarnation | Destination `Release`; home `Release` when reachable, verified by proof |
| away → released (home) | Verified release request, or a definition install that removes the placement or its role | Home `Release`; or the taped mutation |

Two derived predicates replace every mutable list: `HeldSlots(population)` is the
slots of in-doubt departures whose landed member is a home companion, plus the
away rows; `IsHeld(slot)` is consulted by the census, `ReconcileInhabitCounts`,
admission allocation and `PreferredOrLowestFree`. Recovery redoing a `Departure`
or `Settlement`, a rollback restoring the landed member, and a replay re-driving
the tape all end with the same held set because none of them writes it: they
write the record and the predicate reads it. `MarkAway`, `ClearAway`,
`RestoreCompanion` and `ForgetCompanionsOf` are deleted.

A home with away rows or in-doubt departures is never reaped (the existing rule:
an empty authority with outgoing routes stays). An operator stop writes them to
the final checkpoint; a home restored from that checkpoint takes its companions
back by exact authority identity, and a fresh home holds no rows, so its
companions abroad are released and its placement spawns anew.

## 2. Trust derivation

The destination decides from facts it holds; a sender's statement is a request,
never evidence.

**Whom a companion follows.** The companion crosses in its player's reservation
as another member, carrying `WorldTransferCompanion(Role, FollowsMember, Home,
Driver, Proof)` where `FollowsMember` is an index into the same request's
members, never an incarnation. At the end of commit, after every member landed,
the destination binds `Follows = arrival.Slots[FollowsMember]` and pins the
generation that slot took in the outcome. A companion is never a separate
transfer, so no alias, admission row, `FindIncarnation` or cohort-order argument
enters the binding; `m_mobilityAdmissions` is never read by companion code.
The companion member is optional to the cohort: a role refusal names the member
in the reply, the source retries the reservation without it in the same drain,
the player lands and is told, and the companion stays (at home) or goes home.

**What its driver may observe.** Role authority is derived at every check
(`WorldServer.TryRoleVerdict` before stored grants): Drive and Observe over the
companion bodies bound to that driver at their landed generation, Observe over
each followed slot while it still holds the pinned generation, nothing else, and
`TryGrant` refuses any body, Control, Mutate or Edit grant to a role driver. A
followed slot whose generation moved is a separation, which sends the companion
home. Every instance-targeted `body.where` arm, numeric or `body:<n>`, answers
through `WorldServer.AnswerObservedWhere` and its disclosure check.

**Home re-adoption and every cleanup path.** One function:

```text
WorldCustody.Verify(awayRows, incarnation, proof) -> WorldAwayCompanion | null
```

It returns the row for `incarnation` when `FixedTimeEquals(proof, row.Secret)`,
else null. It is the only reader of the secret and the only gate on an away row:
the return reservation (a traveller naming an away incarnation without a
verifying proof is refused), the commit (home forces `PreferredSlot = row.Slot`,
ignoring the sender's), the post-durability re-adoption, and the `Release`
request. The only other removal is a taped definition install that no longer
authors the placement's role. No path skips it because no other path exists.

## 3. The custody credential

The credential is 32 random bytes, `Custody`, minted once by the home's
`DetachForTransfer` beside `EnsureMobility` when a home companion departs. It
lives in:

- the `Departure` record's landed member (crossing log), so log-only recovery
  redoes the departure with the recorded secret rather than minting one;
- the tape's `Departure` entry, so a re-drive installs it rather than minting,
  and it is folded into the authoritative hash exactly as the mobility
  credential is;
- the away row (`Settlement`, population checkpoint) at home;
- the visiting binding at the destination (its checkpoint and its own
  `Departure` when it returns);
- `WorldTransferCompanion.Proof` on the return reservation, over the
  authenticated QUIC lane remotely or in process locally.

It is a bearer secret, single use: a durable return arrival removes the row, so a
replayed proof opens nothing. There is no per-authority key, so there is no key
to lose on a restart.

Why replay and recovery stay exact: the secret enters state only through records
that recovery redoes and the tape re-drives, and the home's decision on a return
is recorded in `WorldArrivalOutcome.Readopted`, so a replay shadow binds the
recorded result and never verifies. Determinism pins the mapping from records to
state; the entropy is an input the records carry, like a federated device image.

## 4. Scope cuts

1. **No companion crosses a federation hop.** A remote reservation drops the
   companion member; the player reads "your companion waits at home"; a
   companion away from home whose player goes remote returns home. This removes
   remote home resolution, remote content-store driver pins, cross-process
   release and the unverified remote incarnation claim. The proof already fits
   the member, so federation is later wiring, not a redesign.
2. **No separate companion transfer** (`CarryCompanions` after commit is
   deleted). Removes the alias and cohort-membership class entirely.
3. **No HMAC key.** Removes the key-lifetime class.
4. **No away mutators.** Removes the bookkeeping class.
5. **Stranded is bounded.** Cadence retry, then release; the player reads "your
   companion was lost; a new one waits at home", and home's placement spawns it.
6. **A destination stop sends companions home first**, completing after the
   drain like an emptied instance a queued transfer names; only a refusal then
   releases.

## 5. A law per finding

Laws are named tests in `CompanionCustodyLawTests` (server) and
`ObservedWhereLawTests` (console); every leg has a red control.

| Finding | Law | What it pins |
|---|---|---|
| R2 H1, R1 H6: numeric `body.where` bypasses Observe | `ObservedWhereOneDoor` | Both arms refuse an ungranted caller; a granted control passes |
| R2 H2, R1 H5: refreshed alias or forged follows authorizes a stranger | `FollowsIsSameReservation` | A companion naming a member outside its request binds nothing; alias tables are not consulted (`FollowsMember` is an index by type) |
| R2 H3, R2 M6: sender-declared membership; cohort order | `FollowsBindsLandedSlotOnly` | A higher-epoch duplicate of a present player binds nothing; a companion listed before its player binds after commit |
| R2 H4, R1 H4: cleanup or forged re-adoption without custody | `OneCustodyDoor` | A reservation or release without the verifying proof leaves the row and slot intact |
| R2 H5: destination rollback does not undo re-adoption | `ReadoptAfterDurability` | A rolled-back return leaves the away row; live and replay agree |
| R2 H6, R2 M4: recovery omits away bookkeeping; census takes the slot | `HeldSlotsAreDerived` | Recovery from a pre-departure checkpoint, and raising `world.population`, spawn nothing into the held slot |
| R2 H7, R1 M3: cleanup without an authority input; no terminal cleanup | `ReleaseIsRecorded` | Record home while away, release, reload: live and replay populations hash equal; placement removal frees the row |
| R2 M1: custody key lifetime | `SecretRecoversFromLog` | Restart home without a population checkpoint; log redo; the return lands |
| R2 M2, R1 M4: stranded forever, transient refusals | `StrandedRetriesThenReleases` | Fill the lease table, refuse, expire: retry lands; past the give-up window the body is released and home respawns |
| R2 M3: remote companions cannot return | `RemoteTakesNoCompanion` | A remote reservation drops the member with the notice; the companion stays or goes home |
| R2 M5, R1 H1: budgets collapse; pending drivers ignored | `OneDriverPerRolePin` | Two roles sharing a pin mount two drivers with their own budgets; pending reservations count |
| R1 H2, R1 H3: authority survives re-grant; Control survives | `RoleAuthorityIsDerived` | `TryGrant` refuses body rows to a role driver; `Allows` ignores stored rows |
| R1 H7: source rollback loses the binding | `RollbackRestoresTheDepartureRecord` | Restore uses the landed member's binding, placement and secret; replay equal |
| R1 M1: separation loses identity | `FollowGenerationPinned` | A replacement at the followed slot separates the companion, which goes home |
| R1 M2: reload leaves a stale binding | `HomeBindingFromPlacement` | An install re-derives role and follows for every inhabitant |

Build order once the design is reviewed: records and predicates (section 1),
`Verify` and the member shape (sections 2 and 3), the cuts (section 4), then the
laws, each with its control, before any debug-world wiring.
