# World release management

Official server operators need to deploy a release, return to the previous
release when necessary, and know whether player progress will survive. This
plan proposes one release workflow with a controlled maintenance window, one
previous release, and durable recovery after an interrupted operation.

For example, release B replaces A on Saturday. Players then earn items and move
between worlds. On Sunday, an operator rolls back to A. The items and completed
transfers must remain. Restoring Saturday's checkpoint is a different operation:
it rewinds progress and must never happen as a hidden fallback for rollback.

This is proposed work. The [deployment guide](../development/ci.md#azure-production-deployment)
describes current deployment, and [Worlds and federation](../architecture/worlds.md)
owns current runtime architecture. Implementation should update those owners as
each contract lands. This plan owns the intended release policy and sequencing.

## Implementation status

The hosting foundation, retained archive, packaged qualification runner, and
operator commands exist, including managed deployment and rollback. Production
acceptance and explicit restore remain incomplete. Definition changes are limited
to metadata and require package-bound qualification.

**Implemented:**

- `WorldReleaseManifest` gives a release one immutable identity over its image
  digest, definition pins, artifact hashes, and persistence and peer contracts.
- `WorldReleaseGroupStore` persists the group root with guarded writes through
  the `Prepare`, `Drain`, `Activate`, `Verify`, `Commit`, `Recover`,
  `RecoverActivate`, and `Finalized` phases, one pending operation, retained
  recovery roots, rollback eligibility, and admission state.
- `WorldReleaseCoordinator` drives deployment, bootstrap, and rollback, requires
  qualification evidence, records each phase before its effect, and persists
  pre-commit recovery before restoring a source under fresh fences.
- Managed silos keep candidates private behind an all-row publication barrier,
  report `GET /private-healthz`, and freeze retiring activations.
- `WorldReleaseTransition` computes and reverses a definition delta. Changed
  definitions require the metadata coordinator contract and packaged qualification;
  changes outside metadata have no admitted preservation rule.
- `WorldReleaseMetadataTransition` prepares a metadata-only change over a full
  checkpoint and its undo base, preserving unrelated edits and refusing conflicts.
  Its laws cover continued gameplay, reverse application, custom values, and undo.
  Its atomic authority-root publisher is wired into Azure private activation and
  retains operation receipts for read-only retries. Directory-backed failure laws
  cover interrupted publication and competing authority. Packaged qualification
  uses the same publisher on forward and reverse fixture copies, comparing both
  images' complete imports after each transformation. The two-world metadata
  control passed on 2026-09-13: forward import at tick 0, continuation to tick 4,
  reverse import at tick 4, then continuation to tick 8. It used one engine image,
  so it proves the metadata workflow, not compatibility between engine builds.
- New prepared manifests bind the receipt-aware coordinator contract into their
  immutable identities, including the prior metadata requirement. The atomic
  publisher requires either known contract on one side of a metadata pair.
  Actual pre-contract and metadata-only archive-reader binaries accepted their
  supported controls and refused the new requirement without writing; the same
  guard protects rollback because resume loads both manifests before worker effects.
- The CLI `release` command offers `prepare`, `deploy`, `rollback`, `status`, `finalize`, `resume`, and
  `qualify`, with `exercise` as the packaged qualification leg. Resume loads the
  durable pending operation and pinned deployment configuration, including its
  compute template. Deploy, rollback, resume, and finalization acquire an Azure controller lease.
- `WorldReleaseArchive` retains exact manifests and package files. The Azure
  runtime adapter and loopback worker controls are connected to `azure deploy-world`.
  Official preparation binds and pins the complete composed inventory. Deploy
  retains versioned configuration and protects exact registry digests before
  qualification. Guest effects and bootstrap serialize under a VM lock and
  recheck the durable operation; bootstrap readiness remains private.
- Deploy and rollback export the source automatically. The host captures all rows
  at one pump boundary and retains exact checkpoints without draining or changing
  authority roots. Fixtures retain machine identity and use disposable signing
  keys. Release Azure prepares the package before entering managed deployment.
- Docker qualification compares full imports in both packages, then repeats
  the comparison on candidate-written continuation state. Four control legs
  using one packaged image passed; this verifies the runner, not compatibility
  between different releases.
- Law tests cover manifest identity, guarded group transitions, cutover and
  rollback across two rows, stale fences, lost commit responses, and pre-commit
  recovery restart.
- Store-level receipt snapshots retain the original index and complete chain
  selected by one root. Fixture construction can retain those bytes, sequence
  meaning and journal coverage alongside a complete checkpoint. Directory laws
  cover later publications, malformed graphs, duplicate decisions after gameplay,
  interrupted uploads and competing roots. Automatic export selects roots in each
  row's publication queue and retains their graphs with the checkpoint inventory;
  the CLI materializes them unchanged. Delayed-read and cancellation controls pass.
- Every packaged leg reads all original receipts after import and continuation,
  then checks duplicate and conflicting retries on its drained authority without
  changing the root. The runner independently computes the expected inventory
  hash and requires version-2 exercise reports. On 2026-09-13, eight same-image
  Docker legs passed across unchanged and metadata definitions; an actual older
  image with version-1 reports was refused without producing a qualification
  receipt. The selected world and CLI suites passed 113 tests with no skips.
- Full-inventory acceptance exposed preparation and boot gaps: published boot
  draws need composed-document parsing and byte-based retry checks; nested machine
  assets need relocation when hosted file names are flattened; colocated silo rows
  need signing identities without invented public endpoints. Both five-world
  forward imports passed; reverse qualification and ordinary worker startup hit
  timeouts. A saved-import profile identified eager navigation graph rebuilding
  during validation of empty shared-navigation checkpoints. Empty slots now retain
  lazy geometry; resident trees still validate their edges. Physics navigation
  laws passed 20 tests; World navigation passed 36 with three failures reproduced
  using the pre-change Physics binary. Full inventory reruns remain pending.
- The official image now publishes discoverable Azure, Gaming Brick and MCP
  extensions with their dependencies. Four packaged entry-point controls passed:
  the ordinary silo, silo MCP, CLI MCP, and a conflicting MCP listener that must
  fail the worker. Dynamic providers live for the process lifetime; optional
  dependencies resolve locally, and .NET hosted-service supervision is preserved.

**Remaining:**

- No restore CLI operation or group-scoped restore coordinator operation exists.
  Current escrow tables retire committed transactions, so an empty table is not
  proof that no external effect occurred after a recovery point. Restore needs
  durable evidence covering that interval, or an enforced closed-group boundary
  established before capture; it must not infer safety from current endpoints alone.
- Only metadata edits have a preservation rule. Further definition edits need
  their own reversible rule and packaged continuation evidence before admission.
- The legacy blob-version rewind has been removed. Automatic capture, fixture
  materialization and rollback are wired in. Registry protection, publication
  retry, and VM guards still need real cloud acceptance
  testing, including delayed operations after controller ownership changes.
- No qualification between different engine builds or operator exercise is recorded.
  The [maintenance and recovery runbook](../development/ci.md#world-maintenance-and-recovery)
  now describes the supported commands and phase-specific recovery; cloud acceptance
  must exercise it before production readiness is claimed.
- Receipt proof is now part of packaged qualification. Record it for the actual
  different-build release pair alongside its complete checkpoint evidence.
- Named handheld machine checkpoints now preserve core state and host pacing,
  with real-core and world continuation tests. Four same-image Docker legs over
  the official world, including its arcade machines, passed on 2026-09-13 with
  four steps per leg. This is a persistence control, not release-pair qualification.
  Addon guest state, applied screen
  operations, live coupled links, and enabled machine rewind history remain
  uncapturable; removing live features from a qualification fixture is not a
  solution.

## Operator contract

| Operation | Required result |
|---|---|
| Deploy a release | Preserve authoritative progress, apply an admitted definition change, and open admission only after the new release is durable and verified. |
| Roll back | Run the one retained previous release against the latest state, preserving progress. Refuse before changing the live service when that transition is unsupported. |
| Inspect status | Show active and previous release, operation phase, admission state, recovery point, rollback eligibility, and the next action after a failure. |
| Finalize | Explicitly close the previous release's rollback window. Finalization itself does not alter gameplay state. |
| Restore a recovery point | Explicitly rewind the declared deployment group, reporting its saved tick and capture time and the interval of progress being discarded. |

The normal operator path is prepare, deploy, and inspect. Rollback takes no
engine image, document path, schema number, or storage pointer arguments: it
selects the retained previous release. Restore is a separate recovery operation
requiring explicit acknowledgement of the target and affected scope.

Use the existing Puck CLI and Azure deployment entry point. Add release
preparation, status, rollback, finalization, and restore operations under the
existing World command family; settle exact command spelling while implementing
its command tree. Preparation, deployment, rollback, status, resume, and
finalization exist today; explicit restore remains proposed work.
Each mutation accepts or returns an operation identifier. Repeating the same
request resumes that operation; it does not start another deployment.

## Keep the first version small

The first supported unit is the official deployment's single worker and its
declared hosted worlds. All participating worlds enter maintenance together.
There is one active release and at most one retained previous release. Deploying
C while A is still the rollback target for B refuses until the operator finalizes
B. Returning from B to A records a new deployment operation; it never rewinds
release-operation sequence numbers or authority fencing epochs.

Use a maintenance window. Exclude rolling upgrades, simultaneous mixed-release
authorities, automatic time-based finalization, arbitrary downgrade targets,
world branches and merges, and a general bidirectional migration framework.
Do not add a new storage service, orchestration service, or package hierarchy.

Only release pairs qualified to preserve progress in both directions enter the
ordinary workflow. A breaking persistence change is blocked in this first
version. It needs a separately designed forward transition before it can ship
to an official persisted world. A forward repair using the existing contract
remains the preferred response when an older runtime cannot represent new state.

This deliberately bounds compatibility to deployed official artifacts and their
retained predecessor. It does not require preserving every development format,
old API, or historically incorrect simulation behavior. Implementation must
reconcile the relevant instructions in [CLAUDE.md](../../CLAUDE.md) and the world
skill with this official-release requirement, without introducing general
compatibility shims throughout the engine.

## Existing foundation and gaps

The [Azure deployment](../../src/Puck.Cli/Azure/AzureWorldReleaseDeploy.cs) now
enters the durable coordinator and resumes from retained inputs. Its predecessor
captured mutable blob versions in runner artifacts and restored them on failure;
that path has been removed. Completion still requires explicit restore,
definition-change admission, release-pair qualification, and an exercised cloud
recovery runbook.

The [silo](../../src/Puck.World.Silo/README.md) already owns drain, frozen final
checkpoints, readiness, and a serialized persistence queue. The
[authority store](../../src/Puck.World.Server/WorldAuthorityRoot.cs) publishes a
coherent root referencing immutable definition, checkpoint, journal, and receipt
blobs and fences obsolete writers. Reuse these mechanisms.

Three existing behaviors need deliberate changes:

- The [checkpoint codec](../../src/Puck.World.Server/WorldAuthorityCheckpointCodec.cs)
  admits one supported format. A release label or equal format number alone
  cannot prove that another binary can safely continue its state.
- [Published-content reconciliation](../../src/Puck.World.Silo/WorldSiloHost.Reload.cs)
  uses ordinary reload. [Reload](../../src/Puck.World.Server/WorldServer.MutationApply.cs)
  clears the mutation journal and resets some runtime state. It is not an
  operator upgrade contract.
- Release verification and public admission need separate gates. A candidate
  must be testable while real gameplay, federation ingress, and external writes
  remain closed. A process being healthy does not authorize it to serve players.

## Release identity and state continuity

A release is an immutable manifest identifying the exact engine image digest,
composed world definitions, required assets and extensions, and the persistence
and peer-protocol contracts. Include a source revision for diagnostics and a
human-readable release label. The immutable identity derives from canonical
manifest content; verify every referenced artifact before draining the server.
Use existing packaging and content verification, with full digests for new
release identities. Do not rebuild a candidate during deployment or recovery.

Keep release identity separate from authority identity and mutable state. The
manifest describes the published starting definitions. Runtime document edits,
inventory, identities, population, clocks, random state, grants, hosted-machine
state, and transfer receipts belong to the continuing world. Include every
durable dependency that can change future behavior in the preservation audit;
a matching document hash or successful deserialization is insufficient.

Before admitting a release pair, define its shared persistence contract and
prove both binaries can restore and continue representative states written by
the other. Journal and pending-operation encodings count too. Qualification
must exercise B-only reachable states; booting an unchanged empty world is not
evidence for Sunday rollback. Unsupported new values or state semantics block
the pair even if their encoded shape has not changed.

Definition changes need a small, explicit preservation policy. Compute the
authored A-to-B delta separately from live state. Apply only admitted changes
through the existing validation and authority pipeline. Retain state by stable
row and entity identities; refuse removed or retyped state and incompatible
runtime structures before cutover. Refuse a conflicting live edit instead of
silently overwriting it. Never infer disposable state from an empty value.

Start with engine-only releases and a narrow, tested set of definition edits.
Expand that set only with a preservation rule and an exercised rollback case.
The metadata rule is prepared over an isolated complete checkpoint, including
its undo base, before either binary imports it. Qualification makes both
images import the same transformed state, then apply the reverse authored delta
to the candidate-written continuation before both reverse imports. Keep evidence
for the original capture and both transformed copies; do not normalize away an
unexpected gameplay difference to make their hashes agree.

For production activation, publish the transformed checkpoint and new published
definition together in one guarded authority-root write, retaining all receipt
references and the protected source root. Bind that write to the exact drained
root and operation; retries must recognize the operation's completed publication
without transforming or rewinding it again. Separate definition and checkpoint
writes would leave an interruption window with mismatched authored state.

Rolling back reverses the admitted authored delta against current state, with
the same conflict checks. It does not install the old world definition wholesale
or run the generic reload/reset path. A conflict arising during gameplay leaves
the current release running and produces a named refusal.

Readiness must describe rollback eligibility accurately: the release pair can
be qualified while a current live edit makes its reverse delta conflict. Status
and rollback preparation report that distinction. Do not promise unconditional
rollback for a state the policy has explicitly excluded.

## One durable maintenance transaction

Persist an operation record in the existing private store before touching the
service. It records the operation identifier, deployment group, source and
target manifests, phase, captured recovery roots, and failure information.
Advance it with guarded writes. One operation owns the deployment group at a
time; the existing deployment lock and authority fences remain in force.

| Phase | Required action and recovery behavior |
|---|---|
| Prepare | Verify artifacts, pair qualification, retention, free capacity, and current state on an isolated copy with external effects disabled. Failure leaves the serving release untouched. |
| Drain | Close new admission and transfer initiation across the group, settle outstanding cross-boundary obligations, freeze at a pump boundary, and await final persistence. Record the coherent recovery roots. |
| Activate | Stop the old writer, acquire fresh fences, recheck the final frozen state, apply the admitted transition to a candidate recovery branch in storage, and boot the target privately. Preserve the recorded recovery roots. |
| Verify | Exercise recovery and continuation using the target image, persist its initial authoritative state, and check health and bindings. Verification writes must not manufacture player progress or external effects in the production state. |
| Commit | Publish the active release and its coherent state references durably, then open admission. An interrupted admission-open step resumes the committed release. |

The candidate storage branch here is a temporary set of private immutable
objects and guarded pointers owned by the operation, not a user-facing world
branching feature. Never let a candidate overwrite the only recoverable state.

Before commit, any failure keeps admission closed and returns to the recorded
source release and recovery roots under fresh authority fences. Since no new
public gameplay was accepted, that return discards no post-cutover player
progress. If recovery itself fails, remain in maintenance and expose the exact
failed step; never report a successful rollback or open an uncertain authority.

After commit, recovery follows the committed target. Never automatically restore
the pre-deployment recovery point after admission may have opened. A later
rollback is a new maintenance transaction using a fresh checkpoint of the
latest state. Commit is the conservative boundary even if a crash obscures
whether admission actually opened.

On coordinator restart, read the durable operation before doing anything else.
Resume idempotent steps or execute the recorded pre-commit recovery. Fresh
fences prevent a delayed source or candidate writer from publishing. A group
with several roots stays closed until all required publications complete;
do not claim a distributed atomic write from several independent root updates.

## Federation, restore, and retention

Use an explicit deployment-group inventory. Stop new transfers first and let
existing transfers settle before freezing the group. If a transfer to an
authority outside the group is unresolved, fail the preparation/drain with its
identity and reason. The first version does not attempt an independent rewind
of external obligations or mixed-version federation negotiation.

Restore requires a recovery point covering the affected group and proof that
effects outside that point's scope will not be duplicated or erased. Default
to refusing an in-place restore when later external transfers or durable effects
cannot be reconciled. Inspection on an isolated copy remains possible. The
initial official deployment can support in-place restore within a closed group;
general federation disaster recovery is separate work.

Retain the active and previous release artifacts, their contract qualification,
the operation records, and every blob reachable from protected recovery roots.
Include required credentials/configuration references in recovery validation
without copying secrets into manifests. Finalization removes rollback
eligibility; it does not delete backup recovery points. Configure backup
retention separately. Never expire an artifact referenced by an active recovery
operation, and fail preparation if required rollback artifacts are missing.

## Implementation sequence

Each phase extends existing owners and includes its documentation. Do not wait
for the broader world-runtime consolidation or introduce unrelated refactors.

1. **Release contract and packaging.** Extend World preparation and official
   image packaging in `src/Puck.Cli` with the immutable manifest and exact
   artifact verification. Define the first persistence contract and enumerate
   the supported definition edits. Add read-only preparation and status output.
   Completion: a mismatched image, missing asset, unsupported state contract,
   or definition conflict is refused before any live mutation.
2. **Durable operation and private activation.** Extend the existing authority
   store, `src/Puck.World.Silo`, and Azure deployment orchestration. Separate
   candidate health from admission, retain recovery roots, and persist the
   operation's phases. Completion: process or runner loss at every phase has
   one deterministic recovery action, with no simultaneous admitted writers.
3. **Preserving release transition.** Add an explicit release application path
   over existing checkpoint restore, definition composition, validation, and
   installation seams in `src/Puck.World.Server`. Completion: all admitted
   authoritative state survives A-to-B and B-to-A transitions; unsupported or
   conflicting edits leave the serving release unchanged.
4. **Operator rollback and retention.** Add the single-target rollback and
   finalization operations, protected artifact retention, and actionable
   status. Completion: roll back after real B gameplay using its latest state;
   restart during rollback and resume without reverting to the original
   deployment checkpoint. A third deployment cannot silently evict the target.
5. **Restore and official qualification.** Add explicit group-scoped restore,
   external-obligation refusal, and release-pair qualification using packaged
   images. Replace the current deployment fallback with the common operation
   workflow. Publish the maintenance and recovery runbook in the deployment
   guide. Completion: the operator acceptance exercise below passes against
   the candidate artifacts used for deployment.

Use `src/Puck.World.Schema` for document-facing contract vocabulary only where
needed; host release bookkeeping belongs to hosting/persistence. Azure remains
the deployment adapter. A local directory store should exercise the same state
machine without requiring cloud infrastructure for every failure case.

## Verification and completion

Extend the existing [silo lifecycle laws](../../tests/Puck.World.Tests/WorldSiloLifecycleLawTests.cs)
and [authority-store tests](../../tests/Puck.World.Tests/WorldAuthorityBlobStoreTests.cs),
plus CLI and schema tests where their contracts change. Exercise real packaged
silo processes and the existing public probe for deployment acceptance. A
same-build restore test cannot qualify two different releases. Keep current
runtime and federation gates, and update affected human and agent documentation.

The release qualification must demonstrate:

- A-to-B deployment, gameplay under B, B-to-A rollback, and continuation under
  A preserve expected inventory, identity, population, clocks, random state,
  supported machine state, live edits, and completed transfer obligations.
  Compare transferred state before the next tick; do not require different
  engine versions to produce identical future trajectories.
- Unsupported state, a conflicting authored row, missing predecessor image,
  unresolved external transfer, or stale fence produces a named refusal and
  no partial installation.
- Failure at each durable phase, checkpoint failure, concurrent requests, a
  delayed old writer, and a retry after runner loss preserve exclusive
  ownership and follow the recorded commit boundary.
- A failed candidate receives no public gameplay or external writes. A
  post-commit failure never triggers automatic recovery to an older save.
- An acknowledged rollback survives another restart, and its status names
  the actual running image, definitions, and recovery state.
- Finalization closes only the rollback window; backup retention remains
  effective. Restore explicitly identifies the rewind and refuses unsafe
  external obligations.

The final operator exercise is deliberately short: prepare B, deploy B, inspect
its status, play, roll back, reconnect, and verify that progress remains. Repeat
with an interrupted deployment and an interrupted rollback. Complete these
operations through the supported CLI with no manual blob edits, image selection,
schema repair, or shell recovery recipe. Archive the tested release identifiers
and results as dated qualification evidence rather than declaring the feature
proved merely because its unit tests pass.
