# Updater policy: one durable install record for the updater, the stub, registration and the pruner

This memo specifies the one durable mechanism that `Puck.Launcher`'s updater,
`Puck.Launcher.Stub`, every installed entry point's registration and
`InstallPruner` consult, so that:

1. A payload that fails startup is rejected by its complete identity and stays
   rejected across rollout-metadata progression and fresh higher-sequence
   authorization, while a changed payload stays eligible.
2. Every accepted revocation is persisted and applied to admission, launch,
   rollback and retention, so a revoked version is never selected; rollback
   selects an allowed, state-compatible proven install or refuses by name.
3. No process runs from an install at a state generation below the generation
   some earlier launch from that install may already have written.
4. `ReleaseManifest.MinimumSupported` remains update eligibility, never a
   persistent rollback floor.

The contract sits in `Puck.Confinement`, the base-class-library leaf already
shared by `Puck.Launcher` and `Puck.Launcher.Stub`, and names no deployment or
identity provider type ([AGENTS.md](../../AGENTS.md)).

This revision answers the design review's eleven findings (U01 to U11, listed
under [the review's findings](#the-reviews-findings-answered)). Against the
first revision it moves every piece of selection state (the current pointer,
the startup-attempt counts and the proofs of health) into the one record, so
each transition is one durable replacement of one file, deletes the `last-good`,
`state/health.json` and `versions/<v>/state-generation` files, and makes process
registration the launch-policy door every installed entry point already passes.

## The two confirmed findings this design closes

From [open items](open-items.md#security-review):

1. **Rejected payload selection (verified finding 2.1).** The stub's rollback
   to its `last-good` pointer recorded no durable rejection of the failed
   payload, the fallback launch overwrote the single `state/health.json`
   attempt slot, verification admitted a fresh higher-sequence manifest
   carrying the identical payload (a rollout advanced from 10% to 50%, or a
   re-signed release with a new version label), and the pruner ranked inactive
   versions by install order alone.
2. **Revoked rollback candidate (verified finding 2.2).** Verification refused a
   manifest that revoked itself but persisted none of its other revocations;
   the applier promoted the outgoing version to `last-good` whenever
   `state/health.json` was absent, revoked or not; the stub's decision table
   checked only attempts and generations.

## The record

One file, `state/release-policy.json` (`InstallLayout.ReleasePolicyFileName`
under `InstallLayout.StateDirectoryName`), holds every fact selection depends on:

```json
{
  "schema": "puck.release.policy.v1",
  "current": "1.1.0",
  "lastSequence": 3,
  "stateGenerationFloor": 1,
  "revokedVersions": ["1.0.5"],
  "rejectedPayloads": [
    { "payloadPin": "sha256/0123…", "version": "1.2.0" }
  ],
  "provenInstalls": [
    { "version": "1.0.0", "sequence": 1, "payloadPin": "sha256/cccc…" },
    { "version": "1.1.0", "sequence": 3, "payloadPin": "sha256/dddd…" }
  ],
  "launchAttempts": [
    { "payloadPin": "sha256/dddd…", "attempts": 1 }
  ]
}
```

- `current` names the version the stub launches. It replaces the `current`
  pointer file; nothing else names the selected version.
- `lastSequence` is the install order's high-water mark. An apply takes
  `lastSequence + 1`, so a sequence is never reused, even after the version that
  held it was pruned (U10).
- `stateGenerationFloor` is the highest state generation any launch from this
  install was admitted at.
- `revokedVersions` holds every accepted revocation still able to name a
  selectable or admissible version ([bounds](#bounds-u08)).
- `rejectedPayloads` holds each payload identity that exhausted its startup
  attempts, with the version it failed as (for messages).
- `provenInstalls` holds each install that reached startup health, by its
  complete installed identity: version, install sequence and payload pin.
- `launchAttempts` counts stub launches per payload identity since that payload
  last proved healthy. It replaces `state/health.json`, and is keyed by payload,
  never by version, so an update carrying the same payload under another version
  inherits the count (U06).

Each version directory keeps one immutable record, `versions/<v>/install-record.json`
(`InstallRecord`, moved to `Puck.Confinement`), written once by the apply that
installs it, before that apply's record commit:

```json
{ "sequence": 3, "rid": "win-x64", "stateGeneration": 1, "payloadPin": "sha256/dddd…", "files": ["sha256/aaaa…"] }
```

It replaces `versions/<v>/state-generation`, which said the same generation a
second time. A record lacking any field is invalid: its version has no known
identity, so it is never selected, admitted at registration or ranked as proven.

### Payload identity (`PayloadPin`)

`ReleasePayloadIdentity.Of` (in `Puck.Launcher.Release`, the only producer)
computes `ContentPin.Compute` over the UTF-8 text

```text
puck.release.payload-identity.v1\n
rid:<payload.Rid>\n
stateGeneration:<manifest.StateGeneration>\n
file:<path>\t<hash>\t<size>\n   (one line per file, ordered by path, ordinal)
```

The manifest validator refuses control characters in a path
(`ReleaseCanonicalizer.IsPortablePath`), so the tab and newline separators are
unambiguous. Rollout, notes, channel, `MinimumSupported`, `Revoked`, the version
label, the archive and the signature are excluded: re-signing, advancing a
rollout or relabelling identical files yields the same pin; changing any file's
bytes, path or size, adding or removing a file or changing the generation
yields another. The pin includes the RID, so a pin names one RID's payload and
the policy compares pins alone.

### Reading the record

- An absent `state/release-policy.json` is the empty record: no current, floor
  0, empty sets. That is a new install root's state, and (see
  [durability](#durable-publication-u04-u05)) a crash cannot produce it once the
  record exists, because the record is only ever replaced, never deleted. A
  deleted record is the install root's own user acting outside the launcher,
  which [the releases decision](../decisions/runtime-and-delivery.md#releases)
  leaves outside the threat model. (The first revision failed closed on absence
  when `current` existed; with `current` inside the record that rule has nothing
  left to test.)
- A present record that does not parse, names another schema, holds a negative
  number, a version that is not a plain directory name, an empty pin, a
  duplicate entry or more entries than [the bounds](#bounds-u08) allow throws
  `InvalidDataException`: every caller fails closed (the stub refuses launch,
  registration refuses start, the updater refuses, the pruner skips).

## The invariants and their doors

| Invariant | Statement | The one door |
|---|---|---|
| **I0. Serialized transitions (U01)** | Every read-modify-write of the record, and every write of a version's install record, happens while holding the install root's `update.lock`. | `InstallTransaction` (`Puck.Confinement`): the only type with the record's writers, and it can be obtained only by acquiring `update.lock`. Its writers are private; callers get named operations. |
| **I1. Complete payload identity** | A payload's identity is its `PayloadPin`. | `ReleasePayloadIdentity.Of`; the applier is its only caller that persists a pin. |
| **I2. Durable rejection** | A payload whose launch attempts reach the stub's `MaxAttempts` is in `rejectedPayloads`, committed in the same replacement that moves selection away from it; no admission, launch, registration, rollback or retention slot accepts a rejected pin. | `InstallTransaction.SelectLaunch` writes it; `ReleasePolicy.Refusal` (one predicate) is the check every admission path calls. |
| **I3. Persistent revocation** | Every authentic manifest's valid revocations are merged before the manifest's own eligibility is judged and before its sequence can advance; no admission, launch, registration, rollback or retention slot accepts a revoked version. | `InstallTransaction.MergeRevocations`, called only from `AttestationReleaseVerifier.VerifyCore`; the same `Refusal` predicate. |
| **I4. Generation compatibility (U02)** | No version whose state generation is below `stateGenerationFloor` is admitted as an update, selected by the stub (current included) or registered as a running process; the floor is raised to the final launch choice's generation in the selection commit, before the stub starts the process (the lead's U03 ruling), and again at registration for a process not started by the stub. | `Refusal` for the check; one private `RaiseFloor` used by `SelectLaunch` and `AdmitProcess`. |
| **I5. Proof bound to identity (U07, U10)** | An install is proven only when the process registered from it reached `ApplicationStarted`; the proof is its exact `(version, sequence, payloadPin)`, and a proof counts at selection only while the version's on-disk record has that exact sequence and pin. Proving clears that pin's attempt count in the same replacement. | `InstallTransaction.RecordHealthy`; `RollbackCandidates` for the match. |
| **I6. Proven rollback or named refusal** | When `current` is disqualified, the stub selects the highest-sequence proven install that matches its record, is unrevoked, not rejected and meets the floor, or refuses by name (exit 2). It never launches a failed, revoked or incompatible version. | `InstallTransaction.SelectLaunch`. |
| **I7. Update-only `MinimumSupported`** | Checked only when admitting an update (`AttestationReleaseVerifier.VerifyCore`, `UpdateService.ApplyUnderLeaseAsync`); never persisted. | Those two checks; the record has no such field. |
| **I8. Durable order** | A version's payload files and install record are durable before the record commit that makes it current; a record commit is durable before the transaction returns. | `AtomicFile.WriteAllBytesDurably` for every record write; `FileUpdateApplier`'s verification pass flushes each staged file and directory. |

### Where the guarantee stops

As [the releases decision](../decisions/runtime-and-delivery.md#releases)
settles for pruning, the install root belongs to its user and the launcher: a
process with that user's write access can already replace the binaries or the
record itself, so tampering with the install root is outside the model. Inside
it, the mechanism holds against:

- a release channel (or its signing key) re-signing a rejected payload under
  higher sequences, wider rollouts or new version labels;
- manifests that revoke versions while failing themselves, or that arrive
  through a read-only check;
- process kills and power loss at any point, on the supported file systems
  ([durability](#durable-publication-u04-u05));
- any interleaving of cooperating launcher processes: checks, applies, stub
  launches, registrations, health clears and pruning passes.

It does not stop a process that is already running when its version is later
revoked or rejected; it stops the next start from that version (U11).

## The transaction (U01)

`InstallTransaction` holds `update.lock` (`InstallLayout.UpdateLockFileName`)
for its lifetime, with the confined root it was opened from. It is the one lock
for the record because the record and the version directories it names were
already serialized by that lock: the stub, the updater, health clearing,
registration and pruning all held it. A separate policy lock would need an order
with `update.lock` at every site that touches both; one lock has no order.

- **Acquisition.** `InstallTransaction.TryBegin` (no wait: the updater's apply,
  health clearing and pruning, which already gave up on contention) and
  `InstallTransaction.Begin` (a bounded wait: a check, the stub) and the
  registration's unbounded wait. The lock is an exclusive open on Windows and an
  exclusive `flock` on Linux; neither is reentrant, even within a process.
- **Ownership.** Whoever begins a transaction owns it and disposes it. Nothing
  that receives one begins another: `IReleaseVerifier.Verify` and
  `IReleaseVerifier.ReserveInstall` take the caller's transaction as a
  parameter, and `IUpdateApplier.Apply` takes it in place of a root path. So
  `Verify` inside `ApplyAsync` runs under the transaction `ApplyAsync` began;
  no reentrancy exists to define.
- **CheckAsync.** `UpdateService.CheckAsync` fetches and parses without the
  lock (a network wait must not block the stub), then begins a transaction for
  verification, so its revocation merge is serialized with the stub and every
  other writer, and then releases it before rollout bucketing. A check that
  cannot get the lock within its wait reports `Refused` and writes nothing.
- **Sequence acceptability.** `VerifyCore` runs entirely inside the caller's
  transaction, so its high-water-mark comparison, the revocation merge and the
  final reserve or advance are one serialized step; `ReserveInstall` repeats the
  comparison inside the apply's transaction before reserving.
- **Reads.** A transaction loads the record once and serves every read in it
  from that snapshot, which no other writer can change while the lock is held.
  A display-only reader outside any transaction (`update.status`, the operator
  adapter's version check) reads the whole file at once through
  `ReleasePolicy.ReadUnlocked`, which opens it sharing delete access so it never
  blocks a replacement.

## Durable publication (U04, U05)

Every write of `release-policy.json`, of an install record, of the replay mark
(`state/sequence-mark`) and of the install intent goes through one primitive,
`AtomicFile.WriteAllBytesDurably`:

1. Write a sibling temporary (`<name>.<random>.tmp`) and flush it to stable
   storage (`FileStream.Flush(flushToDisk: true)`: `fsync` on Linux,
   `FlushFileBuffers` on Windows).
2. Replace the destination by renaming the temporary over it:
   - **Linux:** `rename(2)`, which replaces atomically, then `fsync` of the
     containing directory, because the Linux contract requires a separate
     directory `fsync` to make the entry durable
     ([fsync(2)](https://www.man7.org/linux/man-pages/man2/fsync.2.html)).
   - **Windows:** `MoveFileExW(MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH)`,
     which "does not return until the file is actually moved on the disk"
     ([MoveFileExW](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-movefileexw)).
     .NET's `File.Move` omits `MOVEFILE_WRITE_THROUGH`
     ([the runtime's interop](https://github.com/dotnet/runtime/blob/main/src/libraries/Common/src/Interop/Windows/Kernel32/Interop.MoveFileEx.cs)),
     so the primitive calls `MoveFileExW` itself.
3. Never fall back to `ReplaceFile`. `ReplaceFileW` documents a failure that
   leaves the old file under the backup name and the new one under its
   temporary name
   ([ReplaceFileW](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-replacefilew)),
   and says its write-through flag is not supported: that is U05's unrecoverable
   state. `AtomicFile` falls back to it only to replace a memory-mapped image;
   no record is ever mapped, so the durable primitive refuses instead: a failed
   rename leaves the destination whole, deletes the temporary and throws, and
   the transition did not happen. A killed process leaves at most a stray
   `*.tmp`, which no reader opens.

Payload files: the stager writes them with ordinary writes. Before the record
commit that makes a version current, `FileUpdateApplier` re-reads every staged
file to verify its pin (it already did) and now opens each for writing and
flushes it to stable storage, and on Linux `fsync`s every directory of the
version tree (`ConfinedDirectory.Flush`). The install record is then written
with the durable primitive, and only then is the record committed.

Supported file systems are those whose rename replaces atomically and whose
flush calls above are honoured: ext4, XFS and Btrfs on Linux, and NTFS on
Windows. That a newly created payload file's directory entry is durable on NTFS
once its data is flushed and a later write-through move commits rests on NTFS
journalling its metadata in order; that is an inference, not verified on a
power-cut rig. A power-cut reproduction is out of this lane's reach and is
recorded under [open items](open-items.md#security-review).

With every selection fact in one file, a transition is one durable
replacement: before it returns nothing has changed, after it everything has.
The interruption schedules below therefore reduce to the order of the
immutable writes that precede each commit.

## Every transition

Each operation below is one method of `InstallTransaction`, computes its new
record in memory from the transaction's snapshot, and commits once.

### Admission (`ReleasePolicy.Refusal`)

`ReleasePolicy.Refusal(version, stateGeneration, payloadPins)` returns the reason a version
may not be admitted, or none: the version is revoked (ordinal, ignoring case,
since version directories collide that way on Windows), any pin is rejected, or
the generation is below the floor. Every admission path calls it: the
verifier's evaluation, the apply's commit, the stub's current and candidates,
registration.

### Verification (`AttestationReleaseVerifier.VerifyCore`)

Under the caller's transaction, in order:

1. Signature chain, subject, replay comparison (or the matching reservation
   for `ReserveInstall`) and canonical hash: nothing in the manifest is trusted
   before these pass.
2. `MergeRevocations(manifest.Revoked, ReleaseVersion.Compare)`: entries that
   are not a valid version directory name (`ReleaseCanonicalizer.IsVersionName`)
   cannot name any install and are dropped before anything is written (U09);
   the rest are unioned, retired entries are removed ([bounds](#bounds-u08)),
   and the record is committed when it changed. A merge that would exceed the
   bound refuses the manifest and writes nothing.
3. Admission: `Refusal(manifest.Version, manifest.StateGeneration, every payload's pin)`.
   Checking every payload's pin rather than the target RID's needs no RID: a
   pin names one RID, so only this install's RID can be in its rejected set.
4. `MinimumSupported` and strict version precedence over the installed version.
5. Reserve or advance the replay mark when asked.

The revocations of an authentic manifest are therefore durable even when its
own version is refused, even on a read-only check, and always before its
sequence is consumed.

### Apply (`CommitApply`, from `FileUpdateApplier`)

`FileUpdateApplier.Apply(manifest, rid, transaction, recover)` validates the
manifest, verifies and flushes every staged file, computes the pin, and calls
`CommitApply(version, rid, stateGeneration, payloadPin, files, versionPrecedence)`:

1. `Refusal` for the new version; the downgrade and case-insensitive collision
   checks against `current` and the rollback target (`RollbackTarget`, the protection the
   `last-good` file gave).
2. Retire revocations; check [the bounds](#bounds-u08).
3. Write the version's install record durably with sequence `lastSequence + 1`.
4. Commit the record: `current` = the new version, `lastSequence` advanced.

Nothing about the outgoing version changes: its attempts stay counted under its
pin (U06), and it is proven only if `RecordHealthy` proved it. (The first
revision proved the outgoing version at apply when `state/health.json` was
absent; that inferred proof is gone with the file.) A recovering apply whose
version is already current checks that its install record's generation and pin
equal the manifest's and commits nothing.

### Stub selection (`SelectLaunch`, the stub's only policy call)

Under the stub's transaction:

1. Read `current`; refuse by name when there is none, or when its install
   record is missing or invalid (its generation is unknown).
2. If `launchAttempts[current pin] >= MaxAttempts`, add the pin to
   `rejectedPayloads` and drop its attempt entry.
3. Current is disqualified when it is revoked, its pin is rejected, or its
   generation is below the floor (`Refusal`).
4. If disqualified, choose the rollback candidate: the highest-sequence proven
   install whose on-disk record has the same sequence and pin, not revoked, pin
   not rejected, generation not below the floor. If none exists, commit the
   record (step 2's rejection is durable) and return `Refuse`, naming current's
   disqualification and each candidate's.
5. Raise the floor to the chosen version's generation, count one attempt under
   its pin, set `current` to it, and commit once.
6. Return `Launch(version)` with what happened (`RolledBack` from which
   version and why).

The stub then holds the version directory, checks the executable, releases the
lock and starts the process, which registers ([U03](#u03--revocation-racing-process-creation)).
The stub's decision table, its launch-anyway branch, its pointer revert and its
health-file writer are deleted.

The stub no longer cancels a pending install intent: a recovering apply passes
`ReserveInstall`, whose admission (`ReleasePolicy.Refusal`) refuses a revoked or rejected
version terminally and retires the intent. The stub thus parses nothing the
updater writes except the record.

### Registration (`AdmitProcess`, from `InstallRegistration.Register`)

Registration already waits for `update.lock` and holds the process's own
version directory. Under that same lock it now begins the transaction and calls
`AdmitProcess(version)`: the version's install record must be valid, and
`Refusal` must pass (not revoked, not rejected, generation not below the
floor); then the floor is raised to its generation and committed if it rose.
A refusal throws `InstallRegistrationException`, and the entry point refuses to
start, as it already does for an unconfirmed directory.

### Health (`RecordHealthy`, from `HealthGateFile.ClearHealthy`)

Under the transaction, when the registration's root identity matches and
`current` names the registered version: read that version's record, add
`(version, sequence, pin)` to `provenInstalls` unless it is revoked or rejected,
raise the floor to its generation, remove the pin's attempt entry, and commit
once. Proof and the clearing of its attempts are one replacement (U07).

### Pruning (`InstallPruner`)

The pass takes `publication.lock`, then begins the transaction (in place of
taking `update.lock` itself). It protects `current`, the top rollback
candidate, and a pending intent's version unless the intent's version is
revoked or its pins rejected. Ranking gives revoked and rejected installs no
`keepVersions` slot, ranks matching proven installs ahead of unproven ones, and
then by sequence. A record that does not read fails the validation phase, so
the pass skips without deleting.

## Bounds (U08)

Every list is bounded, and no retirement can readmit a rejected payload or a
revoked version:

- **`provenInstalls`**: an entry is retired at every commit once its version
  directory no longer holds a record with its exact sequence and pin. That is
  safe because such a proof can never match again (sequences are never reused),
  so it is bounded by the installed versions.
- **`revokedVersions`**: a revoked version `V` is retired when no version
  directory is named `V` and `V`'s precedence is at or below that of every
  version that could become current: `current` and every installed version.
  Proof that it cannot return: admission requires a version strictly above the
  current one, rollback selects only installed versions, and every later install
  is above some later current, so every future current is at or above today's
  minimum; `V` can never again be admitted or selected. Precedence is
  `ReleaseVersion.Compare`, the same order admission uses, passed in by the
  launcher. After retirement, a merge that would hold more than 4096 entries
  refuses its manifest and writes nothing.
- **`rejectedPayloads` and `launchAttempts`**: never retired by time or
  directory (a rejected payload can return under any label). Each new entry
  needs an applied install and a stub launch of it, so `CommitApply` refuses a
  new install while the two lists together hold 4096 entries; the stub can then
  add at most the one pin it is launching. An attempt entry is removed when its
  pin is proven or rejected.

A full history is reported by name; the remedy is a reinstall. At these bounds
the file stays under a megabyte, and the reader refuses a larger one.

## The review's findings answered

### U01 — CheckAsync as an unlocked writer

Closed by I0. Every record mutation is a method of `InstallTransaction`, which
exists only while `update.lock` is held; `CheckAsync` begins one around
verification; the verifier and applier receive their caller's transaction
instead of acquiring one. The law: a check started while another holder has the
lock and commits a rejection waits, then merges its revocation into the record
that holds that rejection.

### U02 — the current launch path ignored the floor

Closed by I4. `Refusal` includes `stateGeneration < floor` and is called by
admission (so a signed higher version declaring a lower generation is refused
before it becomes current), by the stub for current and for every candidate,
and by registration. The law applies a generation-1 manifest after a
generation-2 launch and sees it refused before `current` moves.

### U02's companion — the floor follows the final choice

The lead's U03 ruling: `SelectLaunch` raises the floor from whichever version
it chose, current or a rollback candidate, in the same commit that counts the
attempt, before the stub starts the process. On the rollback branch the raise
never moves the floor: `RecordHealthy` raises the floor to every install it
proves, and a candidate must meet the floor, so a proven candidate's generation
equals it. The raise matters on the current branch, the first launch of a
newly applied higher generation; the law
(`TheFloorFollowsTheLaunchChoiceBeforeTheProcessStarts`) reads the raised floor
from the committed record when selection returns, then sees the older proven
install refused as below it and a lower-generation update refused.

### U03 — revocation racing process creation

Decision: the launched process re-checks under the lock, at registration,
rather than the stub holding the lock through process creation. Registration is
the first statement of every installed entry point, already waits for
`update.lock`, and already holds the version directory; checking the record
there makes the registration the linearization point. A revocation or
rejection committed before the child registers refuses its start; one committed
after it reaches only later starts. Holding the lock through `Process.Start`
alone would not cover U11's direct launches, and would still leave the window
between `Process.Start` returning and the child's application code; the
registration check covers both, so it is the one door. The stub keeps releasing
the lock before starting the child (the child's registration needs it). An
attempt counted for a child that registration then refuses is a failed attempt
of a version that is revoked anyway.

### U04 — flush before rename is not durable

Closed by [durable publication](#durable-publication-u04-u05): one primitive
with a post-rename directory `fsync` on Linux and `MOVEFILE_WRITE_THROUGH` on
Windows, payload files and directories flushed before the commit that selects
them, and every selection fact in one file so no two independently persisted
facts can disagree. Not verified by a power cut; recorded as open.

### U05 — ReplaceFile recovery depended on a finally block

Closed: the durable primitive never uses `ReplaceFile`, so no state exists in
which the record is displaced to a backup name.

### U06 — an update erased exhausted startup history

Closed by keying attempts by payload pin inside the record. Applying B.1 with
B's payload leaves B's count in `launchAttempts`; the next stub launch finds it
at `MaxAttempts`, rejects the pin and rolls back. A different payload has its
own count, and the old one stays until proven or rejected. The law replays the
review's schedule.

### U07 — proof committed, attempts not cleared

Closed by I5: `RecordHealthy` adds the proof and removes the attempt entry in
one replacement, so no state holds both a proof and the attempts it cleared.

### U08 — unbounded growth

Closed by [the bounds](#bounds-u08).

### U09 — a valid manifest could write an unreadable record

Closed: `MergeRevocations` drops entries that are not version directory names
before writing (they can name no install, so dropping loses nothing), and the
record's own writer validates its whole new state before the temporary is
written, so a record that would not read back is never published.

### U10 — a recreated directory inherited an old proof

Closed by I5 and `lastSequence`: a proof is `(version, sequence, pin)`, a
sequence is never reused, and a candidate counts only while its on-disk record
matches all three. A restaged directory has no record until its apply writes
one with a new sequence, so neither a partial nor a completed restaging matches
an old proof.

### U11 — installed entry points bypassed the launch policy

Closed by registration's `AdmitProcess`: the CLI, `Puck.Cli.Worlds` (the parity
verb's sibling), `Puck.World`, `Puck.World.Silo` and `Puck.Actors` all call
`InstallRegistration.TryRegisterProcess` first, so a start from a revoked,
rejected or incompatible version refuses whether the stub or anything else
started it. Startup attempts are counted only for stub launches: a direct
launch is not health-gated, so it cannot exhaust a payload's budget or prove it
unless `current` names it.

## Failure schedules

### Schedule 1: signed release, failed startup, rollout progression, changed payload

1. `1.0.0` (sequence 1, generation 1, pin `P100`) is current and proven.
2. `M1` (`1.1.0`, signature sequence 10, rollout 10%, pin `Pbad`) is applied:
   `current = 1.1.0`, record sequence 2.
3. Three stub launches count `launchAttempts[Pbad]` to 3; each child exits
   before `ApplicationStarted`.
4. The fourth launch's `SelectLaunch` rejects `Pbad`, selects `1.0.0` (proof
   matches its record, unrevoked, generation 1 ≥ 1), counts one attempt under
   `P100`, sets `current = 1.0.0`, commits once and launches it.
5. `M2` (`1.1.0`, sequence 11, rollout 50%) and `M3` (`1.1.1`, sequence 12,
   rollout 100%, new notes, the same files) both compute `Pbad` and are refused
   by admission (`ReleasePolicy.Refusal`), in `CheckAsync` and `ApplyAsync`.
6. `M4` (`1.1.2`, sequence 13, rebuilt binary, pin `Pfixed`) is accepted and
   applied.

### Schedule 2: signed release, failed startup, revoked last proven install

- **2A.** `1.0.0` and `1.1.0` proven, current `1.1.0`. `M120` (`1.2.0`,
  `Revoked = ["1.1.0"]`) merges the revocation at verification and is applied.
  `1.2.0` exhausts its attempts; `SelectLaunch` rejects its pin, skips `1.1.0`
  (revoked) and selects `1.0.0`.
- **2B.** Only `1.1.0` (generation 2) is proven compatible; `1.0.0` has
  generation 1 and the floor is 2. `1.2.0` revokes `1.1.0` and fails.
  `SelectLaunch` rejects `1.2.0`'s pin, commits, and refuses naming `1.2.0`
  (exhausted), `1.1.0` (revoked) and `1.0.0` (generation 1 below the floor 2).
  Exit 2; nothing is launched.
- **2C.** `MinimumSupported` is never persisted, so a proven `1.0.0` stays a
  rollback candidate after a release that requires `1.1.0` for updates.

### Schedule 3: interruption

Each transition is one durable commit preceded by immutable writes, so an
interruption anywhere leaves either the old record or the new one:

| Interrupted | On disk | Next run |
|---|---|---|
| Verification, after the revocation commit, before the replay mark advances | Revocations durable; mark unadvanced; no intent | Re-verification merges idempotently and proceeds; a stub run meanwhile already enforces them. |
| Apply, after reservation, before the intent | Mark consumed; no intent | The existing contract: a fresh signed release is needed. |
| Apply, after the payload flush or the install record, before the commit | A version directory with a record no proof matches; `current` unchanged; intent present | The recovering apply re-verifies the intent, rewrites the record (the version is not current) and commits; a stub run meanwhile launches the old current. |
| Apply, after the commit, before deleting the intent | `current` is the new version; intent present | The recovering apply finds it already current, checks its record and deletes the intent. |
| Stub, before its commit | The old record | The next run computes the same choice. |
| Stub, after its commit, before starting the child | Rejection, floor, attempt and `current` durable | The next run counts one more attempt for the chosen version, which is correct: an unobserved launch counts. |
| Health clear, before or after its commit | Either unproven with the attempt, or proven without it | Never both: no proven payload is rejected for the attempts its proof cleared (U07). |
| Registration, after raising the floor | Floor durable | A process that then dies has still been admitted at that generation; the floor stays raised, which is the conservative direction. |

## Options weighed

### How to identify a rejected payload

| Option | Closes | Cost or failure |
|---|---|---|
| Version string or whole-manifest pin | Retrying byte-identical manifest JSON | **Fails 2.1**: rollout, notes or label changes ship the same broken files under a new identity. |
| **Canonical RID payload pin (chosen)** | Rollout progression, re-signing, relabelling identical files; admits any changed file or generation | One SHA-256 over the file inventory per verification and apply. |
| Any overlapping file hash | Any manifest sharing one file with the failed release | **Unusable**: unchanged runtime files would poison every later release. |

### Where selection state lives

| Option | Closes | Cost or failure |
|---|---|---|
| `last-good` pointer plus a separate revocation file | A revoked single fallback | Loses earlier proven fallbacks; several files that cannot change in one replacement. |
| Proven history derived from `versions/*` records | Avoids a proven list | **Unsafe**: a directory may never have started. |
| One policy file beside `current`, `last-good` and `health.json` (the first revision) | Findings 2.1 and 2.2 | Four independently persisted files: U04's ordering, U05's backup state, U06's overwritten slot and U07's split proof all live in the gaps between them. |
| **One record holding current, attempts, proofs, rejections, revocations and the floor (chosen)** | Both findings and U04 to U07 by construction; one durable replacement per transition | Every caller of the deleted files moves to the record. |

### What the stub does with no allowed candidate

| Option | Closes | Cost or failure |
|---|---|---|
| Keep launching the failed current version anyway | A refusal on a first install's failure | **Violates both findings**: launches a known-broken or revoked payload. |
| **Refuse by name, exit 2 (chosen)** | No revoked, rejected or incompatible payload ever starts | An install whose every local candidate is broken or revoked needs a fresh release or a reinstall, the intended posture. |

### Where the launched process is checked (U03, U11)

| Option | Closes | Cost or failure |
|---|---|---|
| The stub holds `update.lock` through `Process.Start` | U03 for stub launches | Not U11; the child must not need the lock to register, so the window after `Process.Start` remains a design constraint. |
| **Registration re-checks under the lock (chosen)** | U03 and U11 at one door every installed entry point already passes | Every installed start reads the record once under a lock it already takes. |

## Build order

1. **`Puck.Confinement`**: `InstallLayout` names; `InstallRecord`; the record
   type and its source-generated JSON context; `InstallTransaction` with
   `SelectLaunch`, `CommitApply`, `MergeRevocations`, `RollbackTarget`, `Compact`,
   `RecordHealthy`, `AdmitProcess`, `RollbackCandidates`; `ReleasePolicy.Refusal` and `ReadUnlocked`;
   `AtomicFile.WriteAllBytesDurably`; `ConfinedDirectory.Flush`; registration's
   `AdmitProcess` call. Check: the project builds with no project references and
   zero warnings (its AOT analysis included).
2. **`Puck.Launcher`**: `ReleasePayloadIdentity`; the verifier and applier take
   the transaction; `UpdateService` begins it for check and apply;
   `HealthGateFile`, `InstallPruner`, the sequence store and the intent use the
   record and the durable writer. Check: no `File` write of `current`,
   `last-good`, `state/health.json` or `state-generation` remains
   (`puck search`).
3. **`Puck.Launcher.Stub`**: `Program` calls `SelectLaunch` only. Check:
   the stub's decision table and its launch-anyway branch no longer exist, so
   a caller bypassing the transaction does not compile.
4. **Laws** in `tests/Puck.Launcher.Tests/Release`: schedules 1 and 2, the U01
   race, U02's lower-generation update, the higher-generation rollback's floor,
   U06's same-payload update, U07's single-commit proof, U10's restaged
   directory, U11's refused registration, and interruption at each write
   boundary (an injected failure in the durable writer between the immutable
   writes and the commit). Then `puck canary` over its stub legs.
