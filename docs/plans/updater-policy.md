# Updater policy: one durable release policy for the updater, the stub and the pruner

This memo specifies one durable policy mechanism that `Puck.Launcher`,
`Puck.Launcher.Stub` and `InstallPruner` all consult so that:

1. A payload that fails startup is rejected by its complete identity and stays
   rejected across rollout-metadata progression and fresh higher-sequence
   authorization, while a changed payload stays eligible.
2. Every accepted revocation is persisted and applied to launch, rollback and
   retention selection, so a revoked `last-good` is never published; rollback
   selects an allowed, state-compatible proven candidate or refuses by name.
3. `ReleaseManifest.MinimumSupported` remains update eligibility, never a
   persistent rollback floor, and durable rejection state stays separate from
   transient launch attempts in `state/health.json`.

The contract sits in `Puck.Confinement`, the BCL-only leaf project already
shared by `Puck.Launcher` and `Puck.Launcher.Stub`
(`src/Puck.Launcher.Stub/Puck.Launcher.Stub.csproj:13`), and names no
deployment or identity provider type ([AGENTS.md](../../AGENTS.md)).

## The two confirmed findings this design closes

From [open items](open-items.md#security-review):

1. **Rejected payload selection (verified finding 2.1).**
   `StubInstall.RevertToLastGood` (`src/Puck.Launcher.Stub/StubInstall.cs:49-65`)
   cancels the failed version's `install-intent.json` when its `manifest.version`
   matches `failedVersion`, then rewrites `current` to `lastGoodVersion`, but
   records no durable rejection of the failed payload. On the fallback launch,
   `StubHealth.IncrementAndFlush` (`src/Puck.Launcher.Stub/StubHealth.cs:43-56`),
   called from `src/Puck.Launcher.Stub/Program.cs:92-95`, overwrites the single
   `state/health.json` slot with the fallback version's attempt count.
   Subsequently, `AttestationReleaseVerifier.Verify`
   (`src/Puck.Launcher/Release/AttestationReleaseVerifier.cs:30-31`) admits a
   fresh higher-sequence manifest carrying the identical broken payload (for
   example, when staged rollout advances from `10%` to `50%` or `100%`, or when a
   re-signed release bumps the version label or release notes without changing
   the RID payload), and `InstallPruner.Prune`
   (`src/Puck.Launcher/Release/InstallPruner.cs:67-92`) ranks inactive versions
   in `InstallPruner.Act` (`src/Puck.Launcher/Release/InstallPruner.cs:191-231`)
   by `InstallRecord.Sequence` alone without excluding rejected installs from
   the `keepVersions` retention window.
2. **Revoked rollback candidate (verified finding 2.2).**
   `AttestationReleaseVerifier.Verify` checks only whether `manifest.Revoked`
   contains `manifest.Version` itself
   (`src/Puck.Launcher/Release/AttestationReleaseVerifier.cs:114-122`); it
   neither persists the remaining entries of `manifest.Revoked` nor consults
   previously accepted revocations. `FileUpdateApplier.Apply`
   (`src/Puck.Launcher/Release/FileUpdateApplier.cs:108-116`) promotes
   `previousVersion` to `last-good` whenever `state/health.json` is absent, even
   when the manifest being applied (or an earlier verified manifest) revokes
   `previousVersion`, and leaves an already-revoked `last-good` intact when
   `state/health.json` is present. `StubDecisionTable.Decide`
   (`src/Puck.Launcher.Stub/StubDecisionTable.cs:35-44`) checks only attempt
   counts and `state-generation`, and `StubInstall.RevertToLastGood`
   (`src/Puck.Launcher.Stub/StubInstall.cs:49-65`) publishes `lastGoodVersion`
   to `current` without revocation filtering.

## Invariants and the single door that enforces each

All durable release policy reads and mutations move behind one static door,
`ReleasePolicy` in `src/Puck.Confinement/` (alongside `InstallLayout`,
`InstallRegistration`, `ConfinedDirectory` and `AtomicFile` in
`src/Puck.Confinement/InstallRegistration.cs`). Neither `Puck.Launcher`,
`Puck.Launcher.Stub` nor `InstallPruner` interprets `last-good`, revocation
lists or payload rejection sets on its own.

| Invariant | Statement | Single enforcing door |
|---|---|---|
| **I1. Complete payload identity** | A RID payload's identity (`PayloadPin`) is the canonical SHA-256 content pin over the schema tag `puck.release.payload-identity.v1`, the target `Rid`, `StateGeneration`, and the ordinal-sorted list of `(Path, Hash, Size)` entries in `ReleasePayload.Files`. Rollout percentage, release notes, channel, `MinimumSupported`, `Revoked`, version string, optional transport `Archive` and `Signature` (claim, chain, epoch, sequence) are excluded from `PayloadPin`. | `ReleasePayloadIdentity` in `src/Puck.Confinement/`, called when `ReleaseCanonicalizer` / `FileUpdateApplier` binds a payload and when `InstallRecord` is written or validated. |
| **I2. Durable payload rejection** | Once a version exhausts its startup attempt budget (`attempts >= maxAttempts`), its `(Rid, PayloadPin)` is durably recorded in `state/release-policy.json` before any `install-intent.json` cancellation, pointer update or fallback health increment. No verification, intent recovery, staging, apply, stub launch, stub rollback or pruner retention slot admits a version or manifest whose target-RID `PayloadPin` is in the rejected set. A release whose files or `StateGeneration` change produces a distinct `PayloadPin` and remains eligible. | `ReleasePolicy` (`EvaluateManifest`, `SelectLaunch` and `RankRetainedVersions`) in `src/Puck.Confinement/`. |
| **I3. Monotonic revocation persistence and enforcement** | Every manifest that passes signature chain, subject, sequence high-water mark and canonical-hash verification merges its `Revoked` version strings into `state/release-policy.json` before sequence advancement or install reservation succeeds. No verification, intent recovery, apply, `last-good` promotion, stub launch, stub rollback or pruner retention slot selects a version in `revokedVersions`. | `ReleasePolicy` (`CommitVerifiedManifest`, `EvaluateManifest`, `SelectLaunch` and `RankRetainedVersions`) in `src/Puck.Confinement/`. |
| **I4. Proven-candidate rollback selection or named refusal** | Rollback never trusts a blind scalar `last-good` pointer. `state/release-policy.json` records the set of proven versions (versions that cleared `state/health.json`, with their install `Sequence`, `StateGeneration` and `PayloadPin`). When `current` is revoked or exhausts `maxAttempts`, `ReleasePolicy` selects the highest-`Sequence` proven version still present under `versions/` that is neither revoked nor payload-rejected and satisfies `candidate.StateGeneration >= requiredStateGeneration` (where `requiredStateGeneration` is the highest `StateGeneration` any launched version has reached). If one exists, it becomes both `last-good` and `current`; if none exists, the stub refuses launch and rollback by name (exit code `2`) and never falls through to launching a failed or revoked candidate. | `ReleasePolicy` (`SelectLaunch` and `CommitApply`) in `src/Puck.Confinement/`, replacing `StubDecisionTable.Decide` and `StubInstall.RevertToLastGood`. |
| **I5. Update-only `MinimumSupported`** | `ReleaseManifest.MinimumSupported` is checked only when evaluating whether an update may be admitted over `installedVersion` (`AttestationReleaseVerifier.Verify`) and `current` (`UpdateService.ApplyUnderLeaseAsync`). It is never written to `state/release-policy.json` and never disqualifies a proven rollback candidate. | `AttestationReleaseVerifier.Verify` and `UpdateService.ApplyUnderLeaseAsync`; `ReleasePolicy` has no `MinimumSupported` field. |
| **I6. Separation of transient attempts and durable policy** | `state/health.json` (`StubHealthRecord`) holds only the transient consecutive-attempt counter for the version currently being launched. Durable payload rejections, accepted revocations and proven-version records live exclusively in `state/release-policy.json`, so `StubHealth.IncrementAndFlush` overwriting `state/health.json` on a fallback launch never erases a rejection or revocation. | `StubHealth` (transient counter only) vs. `ReleasePolicy` (durable policy file `state/release-policy.json`). |

### Where the guarantee stops

In accordance with [the pruning and install-root trust decision](../decisions/runtime-and-delivery.md#releases),
the install root is owned by the local user and the launcher. A process running
with that user's write access outside the launcher contract can already replace
the stub executable or `StubInstall.StubConfigFileName` directly. The policy
mechanism guarantees safety against:

- untrusted or compromised release channels re-signing rejected payloads under
  higher sequences, wider rollout percentages or new version strings;
- manifests that revoke prior versions while failing startup themselves, or that
  arrive during read-only checks (`UpdateService.CheckAsync`) before an apply;
- process crashes, power loss or kill signals between any two filesystem writes
  across `UpdateService`, `AttestationReleaseVerifier`, `FileUpdateApplier`,
  `HealthGateFile`, `InstallPruner` and `Puck.Launcher.Stub`;
- unreferenced or rejected installs crowding proven rollback targets out of
  `keepVersions` during `InstallPruner.Prune`.

It does not defend against an local writer with install-root write permissions
tampering with `state/release-policy.json` and `state/sequence-mark` in tandem,
though an absent or corrupt `state/release-policy.json` in an initialized
install root (one where `current` exists) fails closed.

## Persisted record shape, location and atomic publication

### File locations under the install root

`InstallLayout` (`src/Puck.Confinement/InstallRegistration.cs:9-19`) gains the
shared policy constants so the stub, updater and pruner address the same paths
without duplicating string literals:

- `state/release-policy.json` (`StateDirectoryName` /
  `ReleasePolicyFileName` on `InstallLayout`): the durable policy record.
- `state/health.json` (`StateDirectoryName` /
  `HealthFileName` on `InstallLayout`): the single-slot transient launch attempt
  counter (`StubHealthRecord`), unchanged in role.
- `versions/<version>/install-record.json` (`InstallRecord.FileName`): extended
  to carry `StateGeneration`, `Rid` and `PayloadPin` alongside `Sequence` and
  `Files`, so the stub and pruner read a version's complete payload identity and
  generation through `ConfinedDirectory` in $O(1)$ without parsing
  `ReleaseManifest`.
- `last-good` (`LastGoodFileName` on `InstallLayout`): retained as a derived pointer
  projection naming the top-ranked allowed, state-compatible proven candidate
  (or deleted when no proven candidate is currently eligible), written only by
  `ReleasePolicy` whenever policy or pointers change.

### Canonical payload identity (`PayloadPin`)

For a validated `ReleasePayload` (`src/Puck.Launcher/Release/ReleaseManifest.cs:14-20`)
and `manifest.StateGeneration`, `ReleasePayloadIdentity` computes a
`sha256/<64 lowercase hex>` digest over the UTF-8 byte sequence:

```text
puck.release.payload-identity.v1\n
rid:<payload.Rid>\n
stateGeneration:<manifest.StateGeneration>\n
file:<file[0].Path>\t<file[0].Hash>\t<file[0].Size>\n
...
file:<file[n-1].Path>\t<file[n-1].Hash>\t<file[n-1].Size>\n
```

where `file[0..n-1]` are ordered by `Path` under `StringComparer.Ordinal` (the
same order `ReleaseCanonicalizer.Normalize` enforces). Because `PayloadPin`
commits to every file's relative path, SHA-256 content pin and byte size plus
`Rid` and `StateGeneration`, and omits `Version`, `Channel`, `Rollout`, `Notes`,
`MinimumSupported`, `Revoked`, `Archive` and `Signature`:

- advancing `rollout.percent` or issuing a new higher-sequence signature over
  the same files yields the exact same `PayloadPin`;
- renaming the release version (`1.2.0` to `1.2.1`) while keeping the RID's
  files and `StateGeneration` identical yields the exact same `PayloadPin`;
- modifying any file's bytes, adding or removing a file, changing a relative
  path or bumping `StateGeneration` yields a distinct `PayloadPin`.

### Schema of `versions/<version>/install-record.json`

`InstallRecord` moves to `Puck.Confinement` (or its record contract sits in
`Puck.Confinement` with `Puck.Launcher.Release.InstallRecord` forwarding to it)
with the following JSON shape:

```json
{
  "sequence": 3,
  "rid": "win-x64",
  "stateGeneration": 1,
  "payloadPin": "sha256/0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
  "files": [
    "sha256/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
    "sha256/bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
  ]
}
```

When `FileUpdateApplier.Apply` verifies the staged files in
`versions/<version>/`, it recomputes `PayloadPin` from the verified file entries
and writes both `versions/<version>/state-generation` and
`versions/<version>/install-record.json` before publishing `current`.

### Schema of `state/release-policy.json`

```json
{
  "schema": "puck.release.policy.v1",
  "maxLaunchedStateGeneration": 1,
  "revokedVersions": [
    "1.0.5",
    "1.1.0"
  ],
  "rejectedPayloads": [
    {
      "payloadPin": "sha256/0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
      "rid": "win-x64",
      "firstRejectedVersion": "1.2.0"
    }
  ],
  "provenVersions": [
    {
      "version": "1.0.0",
      "sequence": 1,
      "rid": "win-x64",
      "stateGeneration": 1,
      "payloadPin": "sha256/cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc"
    },
    {
      "version": "1.1.0",
      "sequence": 2,
      "rid": "win-x64",
      "stateGeneration": 1,
      "payloadPin": "sha256/dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd"
    }
  ]
}
```

Validation rules enforced by `ReleasePolicy` on every read:

- `schema` must equal `"puck.release.policy.v1"`.
- `maxLaunchedStateGeneration` must be $\ge 0$.
- Every version name in `revokedVersions`, `rejectedPayloads[*].firstRejectedVersion`
  and `provenVersions[*].version` must be a plain portable directory name
  (no `/`, `\`, `:`, `\0`, `.` or `..`).
- Every `payloadPin` must be a canonical `sha256/<64 lowercase hex>` pin.
- If `state/release-policy.json` is absent:
  - if `current` is also absent (an uninitialized install root before its first
    `Apply`), `ReleasePolicy` returns an empty policy instance;
  - if `current` exists on disk, a missing or corrupt `state/release-policy.json`
    throws `InvalidDataException` so callers fail closed rather than treating a
    deleted or truncated policy file as empty.

### Atomic publication protocol and safe states under interruption

Every mutation to `state/release-policy.json` uses `AtomicFile.StageAllBytes` /
`AtomicFile.WriteAllBytes` (`src/Puck.Confinement/AtomicFile.cs:193-251`), which
writes to a sibling temporary `release-policy.json.<guid>.tmp`, calls
`stream.Flush(flushToDisk: true)` (`src/Puck.Confinement/AtomicFile.cs:133`),
and renames over `state/release-policy.json`.

Policy writes occur at four boundaries, each ordered so that an interruption at
any instruction leaves a safe, fail-closed state:

1. **Manifest verification and sequence reservation (`AttestationReleaseVerifier.VerifyCore`).**
   - **Order of operations:**
     1. Verify signature chain, subject (`requirement.Subject == manifest.App`),
        sequence acceptability (`m_sequenceStore.IsAcceptable` or matching
        reservation `m_sequenceStore.IsReserved`), and canonical hash.
     2. Read `state/release-policy.json` and merge `manifest.Revoked` into the
        in-memory policy's `revokedVersions` union.
     3. If `manifest.Revoked` is non-empty, persist the merged `revokedVersions`
        to `state/release-policy.json` via `AtomicFile` (and refresh or clear
        `last-good` if it was revoked) **before** evaluating target eligibility
        or advancing `m_sequenceStore`.
     4. Refuse if `manifest.Version` is in the merged `revokedVersions` set.
     5. Refuse if the payload for the target RID (or every payload in the
        manifest when no RID is fixed) has a `PayloadPin` in
        `policy.RejectedPayloads`.
     6. Enforce `manifest.MinimumSupported` against `installedVersion` and
        strict version monotonicity (`manifest.Version > installedVersion`).
     7. When `advanceSequence` is `true`, commit or reserve the sequence in
        `m_sequenceStore`.
   - **Why interruption is safe:** Because revocations in an authentic,
     higher-sequence signed manifest are persisted in step 3 *before* step 7
     advances the sequence high-water mark, a crash between step 3 and step 7
     leaves the revocations durably recorded while keeping the sequence
     unconsumed so the next check/apply can re-verify the manifest idempotently.
     Even on a read-only `UpdateService.CheckAsync` (`advanceSequence: false`)
     or when the target version itself is refused in step 4 or 5, authentic
     revocations from a valid newer manifest are never lost.

2. **Update application (`FileUpdateApplier.ApplyCore`).**
   - **Precondition:** Runs under `update.lock` (`InstallLeases.UpdateLockFileName`).
   - **Order of operations:**
     1. Validate `manifest`, locate `payload` for `rid`, and verify every staged
        file in `versions/<manifest.Version>/` against `ContentPin.OfFile`.
     2. Compute `payloadPin` via `ReleasePayloadIdentity`.
     3. Load `state/release-policy.json` (merging `manifest.Revoked` idempotently)
        and refuse if `manifest.Version` is in `revokedVersions` or `payloadPin`
        is in `rejectedPayloads`.
     4. If `previousVersion` (`current`) exists and `state/health.json` is
        **absent** (meaning `previousVersion` completed startup and cleared its
        health gate), record `previousVersion` in `policy.ProvenVersions`
        (reading its `versions/<previousVersion>/install-record.json`) if not
        already present.
     5. Compute the best rollback candidate from `policy.ProvenVersions` whose
        version directory exists under `versions/`, is not in `revokedVersions`,
        is not in `rejectedPayloads`, and satisfies
        `candidate.StateGeneration >= policy.MaxLaunchedStateGeneration`.
     6. Stage and commit `state/release-policy.json` via `AtomicFile`, then
        write `last-good` to that candidate's version (or delete `last-good` if
        no proven candidate qualifies).
     7. Write `versions/<manifest.Version>/state-generation` and
        `versions/<manifest.Version>/install-record.json` (including `rid`,
        `stateGeneration` and `payloadPin`).
     8. Call `HealthGateFile.MarkPending(cacheRoot: root, version: manifest.Version)`.
     9. Write `current` to `manifest.Version` via `AtomicFile.WriteAllText`.
   - **Why interruption is safe:**
     - Interruption before step 6 changes nothing on disk.
     - Interruption after step 6 and before step 9 leaves `current` on
       `previousVersion` with `state/release-policy.json` and `last-good`
       already purged of any revoked candidate; `install-intent.json` remains on
       disk and resumes cleanly on the next `ApplyAsync`.
     - Interruption after step 8 and before step 9 leaves `state/health.json`
       present; `UpdateService` recovery re-enters `ApplyCore` and finishes
       writing `current`, and step 4 will not falsely promote an unproven
       version because `state/health.json` exists.

3. **Startup health clear (`HealthGateFile.ClearHealthy`).**
   - **Precondition:** Runs under `update.lock` once the running process signals
     `IHostApplicationLifetime.ApplicationStarted` and proves its
     `InstallRegistration` matches `root.Identity` and `current == version`.
   - **Order of operations:**
     1. Read `versions/<version>/install-record.json` and `state/release-policy.json`.
     2. Add or update `version` in `policy.ProvenVersions`, set
        `maxLaunchedStateGeneration = Math.Max(policy.MaxLaunchedStateGeneration, record.StateGeneration)`,
        recompute the top allowed proven candidate for `last-good`, and commit
        `state/release-policy.json` (and `last-good`) via `AtomicFile`.
     3. Delete `state/health.json`.
   - **Why interruption is safe:** If interrupted before step 2 commits, the
     version remains unproven and `state/health.json` remains intact. If
     interrupted between step 2 and step 3, `version` is recorded as proven (it
     did reach `ApplicationStarted` and passed identity verification); on the
     next launch or health clear, `state/health.json` is either cleared or
     incremented from its existing count without losing proven history.

4. **Stub launch, payload rejection and rollback (`Puck.Launcher.Stub`).**
   - **Precondition:** Runs under `update.lock` (`StubInstall.AcquireUpdateLease`).
   - **Order of operations in `ReleasePolicy` (`SelectLaunch`):**
     1. Read `current`, `state/health.json`, `state/release-policy.json`, and
        `versions/<current>/install-record.json` (plus `state-generation`).
     2. Check whether `current` has exhausted its attempt budget
        (`attempts >= configuration.MaxAttempts`).
     3. If `attempts >= configuration.MaxAttempts`:
        - Add `(record.Rid, record.PayloadPin, current)` to
          `policy.RejectedPayloads` and remove `current` from
          `policy.ProvenVersions` if present.
        - Persist `state/release-policy.json` via `AtomicFile` **first**, before
          touching `install-intent.json`, `current`, `last-good` or
          `state/health.json`.
     4. If `current` is in `policy.RevokedVersions` or `record.PayloadPin` is in
        `policy.RejectedPayloads` (which now includes step 3):
        - Cancel `install-intent.json` if its `manifest.version` equals
          `current`, is in `policy.RevokedVersions`, or carries a payload pin in
          `policy.RejectedPayloads`.
        - Select the highest-`Sequence` entry `candidate` in
          `policy.ProvenVersions` whose directory `versions/<candidate.Version>`
          exists on disk, is not in `policy.RevokedVersions`, whose
          `candidate.PayloadPin` is not in `policy.RejectedPayloads`, and whose
          `candidate.StateGeneration >= policy.MaxLaunchedStateGeneration`.
        - If no such `candidate` exists: delete `last-good` if it exists on
          disk, keep `current` unchanged (so subsequent stub launches observe the
          same rejected/revoked `current` and refuse identically), and return a
          named refusal (`Refuse` on `StubLaunchDecision`).
        - If `candidate` exists: write `last-good` to `candidate.Version` via
          `AtomicFile`, write `current` to `candidate.Version` via `AtomicFile`,
          and select `launchVersion = candidate.Version`.
     5. If `current` is neither revoked nor payload-rejected (meaning
        `attempts < configuration.MaxAttempts`):
        - Advance `policy.MaxLaunchedStateGeneration` to
          `Math.Max(policy.MaxLaunchedStateGeneration, currentGeneration)` and
          persist `state/release-policy.json` via `AtomicFile` if it increased
          (because once `current` is launched, it may write durable state at
          `currentGeneration`).
        - Select `launchVersion = current`.
     6. Hold `versions/<launchVersion>` (`StubInstall.HoldVersion`), then call
        `StubHealth.IncrementAndFlush(installRoot: installRoot, version: launchVersion)`.
   - **Why interruption is safe:**
     - If interrupted before step 3's `AtomicFile` commit, nothing has changed
       and `state/health.json` still holds `attempts == maxAttempts`; the next
       stub run immediately re-enters step 3.
     - If interrupted after step 3 commits `state/release-policy.json` (recording
       `current`'s `PayloadPin` as rejected) and before step 4 updates `current`
       or step 6 calls `StubHealth.IncrementAndFlush`, the rejection is already
       durable in `state/release-policy.json`. On the next stub run, even if
       `current` was not yet swapped or even if `state/health.json` was
       modified, step 4 immediately sees `record.PayloadPin` in
       `policy.RejectedPayloads`, cancels any matching intent, and completes the
       rollback to `candidate.Version` (or refuses by name).

## Every selection point that reads or writes the policy

Each citation below is pasted verbatim from the file opened in this worktree.

### 1. `AttestationReleaseVerifier.Verify` and `AttestationReleaseVerifier.ReserveInstall`

`src/Puck.Launcher/Release/AttestationReleaseVerifier.cs:30-35`

```csharp
    public ReleaseVerifyOutcome Verify(ReleaseManifest manifest, DateTimeOffset now, string installedVersion, bool advanceSequence) =>
        VerifyCore(advanceSequence: advanceSequence, installedVersion: installedVersion, manifest: manifest, now: now, reserveInstall: false);
    /// <inheritdoc/>
    public ReleaseVerifyOutcome ReserveInstall(ReleaseManifest manifest, DateTimeOffset verifiedAt, string installedVersion) =>
        VerifyCore(advanceSequence: true, installedVersion: installedVersion, manifest: manifest, now: verifiedAt, reserveInstall: true);
```

Inside `VerifyCore`, after signature chain, subject, sequence high-water mark and
canonical hash checks pass, the verifier currently checks only self-revocation
before advancing the sequence store:

`src/Puck.Launcher/Release/AttestationReleaseVerifier.cs:114-153`

```csharp
        if (
            (manifest.Revoked is { Count: > 0 } revoked) &&
            revoked.Contains(
            value: manifest.Version,
            comparer: StringComparer.Ordinal
        )
        ) {
            return ReleaseVerifyOutcome.Refuse(reason: $"version '{manifest.Version}' is revoked");
        }

        if (
            (manifest.MinimumSupported is { } minimumSupported) &&
            ReleaseVersion.IsStrictlyGreaterThan(
            left: minimumSupported,
            right: installedVersion
        )
        ) {
            return ReleaseVerifyOutcome.Refuse(reason: $"installed version '{installedVersion}' is below this release's minimumSupported floor '{minimumSupported}'");
        }

        if (!ReleaseVersion.IsStrictlyGreaterThan(
            left: manifest.Version,
            right: installedVersion
        )) {
            return ReleaseVerifyOutcome.Refuse(reason: $"version '{manifest.Version}' is not strictly greater than the installed version '{installedVersion}'");
        }

        if (advanceSequence) {
            var written = reserveInstall
                ? m_sequenceStore.TryReserve(manifestPin: manifestPin, requirement: requirement)
                : m_sequenceStore.TryAdvance(requirement: requirement);
            if (written == ReleaseDecision.TransientRefusal) {
                return ReleaseVerifyOutcome.Retry(reason: "the release sequence store is busy");
            }
            if (written == ReleaseDecision.TerminalRefusal) {
                return ReleaseVerifyOutcome.Refuse(reason: "the sequence was already committed by another verification or the stored mark is invalid");
            }
        }

        return ReleaseVerifyOutcome.Accept();
```

**Policy change at this selection point:** `AttestationReleaseVerifier` takes an
`IReleasePolicyStore` (backed by `ReleasePolicy` over `state/release-policy.json`
in production, and an in-memory store for isolated unit tests that use
`InMemoryReleaseSequenceStore`). Immediately after line 112 (once canonical hash
verification succeeds):

1. It calls `m_policyStore.CommitRevocations(revoked: manifest.Revoked)` so that
   every revocation in the authenticated manifest is durably unioned into
   `state/release-policy.json` before any refusal return or sequence advance.
2. It calls `m_policyStore.EvaluateManifest(manifest: manifest, rid: m_targetRid)`
   which refuses if `manifest.Version` is in the persisted `revokedVersions` set
   (covering both self-revocation and revocation by an earlier or later manifest)
   or if the target RID's `PayloadPin` (`ReleasePayloadIdentity`) is in
   `rejectedPayloads`.
3. `manifest.MinimumSupported` at lines 124–132 remains an update-eligibility
   check against `installedVersion` only.

### 2. `UpdateService.FetchAndVerifyAsync` and `UpdateService.ApplyUnderLeaseAsync`

`src/Puck.Launcher/Release/UpdateService.cs:104-117`

```csharp
        var verified = m_verifier.Verify(
            advanceSequence: false,
            installedVersion: m_options.InstalledVersion,
            manifest: manifest,
            now: now
        );

        if (!verified.Accepted) {
            return new UpdateCheckResult(
                Detail: (verified.RefusalReason ?? "refused"),
                Manifest: null,
                Outcome: UpdateCheckOutcome.Refused
            );
        }
```

And during intent recovery, on-disk `current` checks, reservation and apply in
`UpdateService.ApplyUnderLeaseAsync`:

`src/Puck.Launcher/Release/UpdateService.cs:188-246`

```csharp
            var resumed = m_verifier.ReserveInstall(manifest: intent.Manifest, verifiedAt: intent.VerifiedAt, installedVersion: intent.InstalledVersion);

            if (!resumed.Accepted) {
                return Refusal(detail: $"pending install refused: {resumed.RefusalReason}", decision: resumed.Decision);
            }
        } else {
            var check = await FetchAndVerifyAsync(cancellationToken: cancellationToken, now: now).ConfigureAwait(continueOnCapturedContext: false);

            if (check.Outcome != UpdateCheckOutcome.Available) {
                return new UpdateApplyOutcome(Applied: false, Detail: $"re-verification reported {check.Outcome}: {check.Detail}");
            }
            intent = new InstallIntent(Manifest: check.Manifest!, VerifiedAt: now, InstalledVersion: m_options.InstalledVersion, Rid: rid);
        }

        var manifest = intent.Manifest;
        var currentPath = Path.Combine(path1: root, path2: "current");
        var current = (File.Exists(path: currentPath) ? File.ReadAllText(path: currentPath).Trim() : null);
        var lastGoodPath = Path.Combine(path1: root, path2: "last-good");
        var lastGood = (File.Exists(path: lastGoodPath) ? File.ReadAllText(path: lastGoodPath).Trim() : null);
        var alreadyCurrent = (recovering && (current == manifest.Version));

        if (!alreadyCurrent && !string.IsNullOrEmpty(value: current)) {
            if ((manifest.MinimumSupported is { } minimumSupported) &&
                ReleaseVersion.IsStrictlyGreaterThan(left: minimumSupported, right: current)) {
                return Refusal(detail: $"current install '{current}' is below this release's minimumSupported floor '{minimumSupported}'");
            }
            if (!ReleaseVersion.IsStrictlyGreaterThan(left: manifest.Version, right: current)) {
                return Refusal(detail: $"version '{manifest.Version}' does not exceed the current install '{current}'");
            }
        }
        if ((!alreadyCurrent && !string.IsNullOrEmpty(value: current) && string.Equals(a: manifest.Version, b: current, comparisonType: StringComparison.OrdinalIgnoreCase)) ||
            (!string.IsNullOrEmpty(value: lastGood) && string.Equals(a: manifest.Version, b: lastGood, comparisonType: StringComparison.OrdinalIgnoreCase))) {
            return Refusal(detail: $"version '{manifest.Version}' collides case-insensitively with an active install directory");
        }

        // Never restage the running directory, including when an interrupted apply already swapped current.
        // The applier rehashes it below without modifying its files or last-good pointer.
        if (!alreadyCurrent) {
            var staged = await m_stager.StageAsync(cancellationToken: cancellationToken, manifest: manifest, rid: rid).ConfigureAwait(continueOnCapturedContext: false);

            if (!staged.Staged) {
                return new UpdateApplyOutcome(Applied: false, Detail: (staged.RefusalReason ?? "staging refused"));
            }
        }

        if (!recovering) {
            var reserved = m_verifier.ReserveInstall(manifest: manifest, verifiedAt: intent.VerifiedAt, installedVersion: intent.InstalledVersion);

            if (!reserved.Accepted) {
                return new UpdateApplyOutcome(Applied: false, Detail: $"install reservation refused: {reserved.RefusalReason}");
            }
            AtomicFile.WriteAllText(path: intentPath, contents: JsonSerializer.Serialize(value: intent, options: DocumentJsonOptions.Shared));
        }

        var applied = m_applier.Apply(cacheRoot: root, manifest: manifest, recoverInterruptedApply: recovering, rid: rid);

        if (!applied.Applied) {
            return Refusal(detail: (applied.RefusalReason ?? "apply refused"));
        }
```

**Policy change at this selection point:** Because `m_verifier.Verify` and
`m_verifier.ReserveInstall` consult `ReleasePolicy`, both fresh fetches
(`FetchAndVerifyAsync`, before rollout bucketing or staging) and resumed
intents (`ReserveInstall` at line 188) terminally refuse any manifest whose
version is revoked or whose RID `PayloadPin` is rejected. On terminal refusal,
`Refusal(...)` at lines 166–170 deletes `install-intent.json` without rewinding
the sequence mark.

### 3. `FileUpdateApplier.Apply` (`ApplyCore`) and `InstallRecord.Write`

`src/Puck.Launcher/Release/FileUpdateApplier.cs:84-131`

```csharp
        if (previousVersion is { Length: > 0 }) {
            var lastGoodPath = Path.Combine(path1: root, path2: "last-good");
            var lastGoodVersion = (File.Exists(path: lastGoodPath) ? File.ReadAllText(path: lastGoodPath).Trim() : null);

            if (recoverInterruptedApply && (previousVersion == manifest.Version)) {
                var generationPath = Path.Combine(path1: versionDirectory, path2: "state-generation");

                if (!File.Exists(path: generationPath) || (File.ReadAllText(path: generationPath) != manifest.StateGeneration.ToString(provider: CultureInfo.InvariantCulture))) {
                    return UpdateApplyResult.Refuse(reason: "the current install's state generation does not match its pending intent");
                }
                return new UpdateApplyResult(Applied: true, PreviousVersion: lastGoodVersion, RefusalReason: null);
            }
            // InstalledVersion belongs to the running process. A previous apply may already have advanced this
            // pointer without restarting it, so the on-disk version is the final downgrade gate.
            if (!ReleaseVersion.IsStrictlyGreaterThan(left: manifest.Version, right: previousVersion)) {
                return UpdateApplyResult.Refuse(reason: $"version '{manifest.Version}' does not exceed the current install '{previousVersion}'");
            }
            if (string.Equals(a: manifest.Version, b: previousVersion, comparisonType: StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrEmpty(value: lastGoodVersion) && string.Equals(a: manifest.Version, b: lastGoodVersion, comparisonType: StringComparison.OrdinalIgnoreCase))) {
                return UpdateApplyResult.Refuse(reason: $"version '{manifest.Version}' collides case-insensitively with an active install directory");
            }

            // The stub leaves this marker until the selected process reports healthy. A newer apply must
            // preserve the last proven version while the previous install has not completed startup.
            if (!File.Exists(path: Path.Combine(path1: root, path2: "state", path3: "health.json"))) {
                AtomicFile.WriteAllText(
                path: Path.Combine(
                    path1: root,
                    path2: "last-good"
                ),
                contents: previousVersion
                );
            }
        }

        AtomicFile.WriteAllText(
            path: Path.Combine(path1: versionDirectory, path2: "state-generation"),
            contents: manifest.StateGeneration.ToString(provider: CultureInfo.InvariantCulture)
        );
        _ = InstallRecord.Write(files: payload.Files.Select(selector: static file => file.Hash), installRoot: root, version: manifest.Version);

        // Publish pending health first: two updates before a stub launch must not promote an unstarted version.
        HealthGateFile.MarkPending(cacheRoot: root, version: manifest.Version);
        AtomicFile.WriteAllText(
            path: currentPath,
            contents: manifest.Version
        );
```

And `InstallRecord.Write`:

`src/Puck.Launcher/Release/InstallRecord.cs:53-74`

```csharp
    public static InstallRecord Write(string installRoot, string version, IEnumerable<string> files) {
        var latest = 0L;

        using (var root = ConfinedDirectory.OpenAnchor(create: false, path: installRoot))
        using (var versions = root?.Child(name: InstallPruner.VersionsDirectoryName)) {
            foreach (var entry in (versions?.Entries() ?? [])) {
                if (entry.Kind != ConfinedEntryKind.Directory) { continue; }
                using var directory = versions!.Child(name: entry.Name);

                if ((directory is not null) && (TryRead(version: directory) is { } existing)) {
                    latest = Math.Max(val1: latest, val2: existing.Sequence);
                }
            }
        }
        var record = new InstallRecord(Files: [.. files], Sequence: (latest + 1));

        AtomicFile.WriteAllText(
            contents: JsonSerializer.Serialize(options: DocumentJsonOptions.Shared, value: record),
            path: Path.Combine(path1: installRoot, path2: InstallPruner.VersionsDirectoryName, path3: version, path4: FileName)
        );
        return record;
    }
```

**Policy change at this selection point:**

- `FileUpdateApplier.ApplyCore` computes `payloadPin` from the verified on-disk
  files and calls `ReleasePolicy` (`CommitApply`).
- Instead of blindly writing `previousVersion` to `last-good` at lines 108–116,
  `ReleasePolicy` merges `manifest.Revoked`, refuses if `manifest.Version` is
  revoked or `payloadPin` is rejected, records `previousVersion` in
  `provenVersions` only when `state/health.json` is absent *and*
  `previousVersion` is neither revoked nor payload-rejected, and sets `last-good`
  to the highest-`Sequence` allowed, state-compatible proven version on disk (or
  deletes `last-good` if every prior proven version has been revoked).
- `InstallRecord.Write` records `Rid`, `StateGeneration` and `PayloadPin`
  alongside `Sequence` and `Files`.

### 4. `HealthGateFile.ClearHealthy`

`src/Puck.Launcher/Release/HealthGateFile.cs:83-108`

```csharp
    public static HealthClearResult ClearHealthy(string cacheRoot, string version, InstallRegistration registration, int keepVersions = AddSelfUpdateDefaults.KeepVersions) {
        ArgumentNullException.ThrowIfNull(argument: registration);
        var comparison = (OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

        if (!string.Equals(a: registration.Version, b: version, comparisonType: comparison)) {
            return new HealthClearResult(Cleared: false, Pruned: null);
        }
        try {
            using var root = ConfinedDirectory.OpenAnchor(create: false, path: cacheRoot);
            using var lease = root?.TryAcquireLock(name: InstallLeases.UpdateLockFileName);
            var currentPath = Path.Combine(path1: cacheRoot, path2: InstallLayout.CurrentFileName);

            if ((root is not null) && (lease is null)) { return new HealthClearResult(Cleared: false, Pruned: null, Retryable: true); }
            if ((root is null) || (registration.Root != root.Identity) || !File.Exists(path: currentPath) || !string.Equals(a: File.ReadAllText(path: currentPath).Trim(), b: version, comparisonType: StringComparison.Ordinal)) {
                return new HealthClearResult(Cleared: false, Pruned: null);
            }
            File.Delete(path: Path.Combine(path1: cacheRoot, path2: "state", path3: "health.json"));
        } catch (IOException error) when (FileLock.IsContention(exception: error)) {
            return new HealthClearResult(Cleared: false, Pruned: null, Retryable: true);
        } catch (Exception error) when ((error is IOException or UnauthorizedAccessException or PlatformNotSupportedException)) {
            // Permissions, unsupported hosts and permanent I/O failures cannot be repaired by polling.
            return new HealthClearResult(Cleared: false, Pruned: null);
        }
        // Pruning takes the publication lease before the update lease, so it runs after this one is released.
        return new HealthClearResult(Cleared: true, Pruned: InstallPruner.Prune(installRoot: cacheRoot, keepVersions: keepVersions));
    }
```

**Policy change at this selection point:** Before deleting `state/health.json` at
line 99, `HealthGateFile.ClearHealthy` calls `ReleasePolicy` (`RecordHealthy`)
under the held `update.lock` lease, recording `version` (with its
`InstallRecord`'s `Sequence`, `Rid`, `StateGeneration` and `PayloadPin`) in
`provenVersions`, updating `maxLaunchedStateGeneration` and refreshing
`last-good` to the highest-ranked allowed proven candidate before
`InstallPruner.Prune` runs at line 107.

### 5. `Puck.Launcher.Stub` (`Program.cs`, `StubDecisionTable.Decide`, `StubInstall.RevertToLastGood`, `StubHealth.IncrementAndFlush`)

`src/Puck.Launcher.Stub/Program.cs:27-95`

```csharp
var current = StubInstall.ReadPointer(path: Path.Combine(
    path1: installRoot,
    path2: StubInstall.CurrentFileName
));
if (current is null) {
    Console.Error.WriteLine(value: $"[stub: no '{StubInstall.CurrentFileName}' pointer under {installRoot} — install is empty]");

    return 2;
}
var lastGood = StubInstall.ReadPointer(path: Path.Combine(
    path1: installRoot,
    path2: StubInstall.LastGoodFileName
));
var attempts = StubHealth.AttemptsFor(
    record: StubHealth.Read(installRoot: installRoot),
    version: current
);
StubAction decision;
try {
    decision = StubDecisionTable.Decide(
        attempts: attempts,
        currentGeneration: StubInstall.ReadGeneration(
            installRoot: installRoot,
            version: current
        ),
        hasLastGood: (lastGood is not null),
        lastGoodGeneration: ((lastGood is null)
        ? 0
        : StubInstall.ReadGeneration(
                installRoot: installRoot,
                version: lastGood
            )),
        maxAttempts: configuration.MaxAttempts
    );
} catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
    Console.Error.WriteLine(value: $"[stub: state generation is unavailable — {exception.Message}]");
    return 2;
}
var launchVersion = current;
switch (decision) {
    case StubAction.RevertToLastGood:
        Console.Error.WriteLine(value: $"[stub: version {current} reached {attempts}/{configuration.MaxAttempts} attempts — reverting to last-good {lastGood}]");
        try {
            StubInstall.RevertToLastGood(failedVersion: current, installRoot: installRoot, lastGoodVersion: lastGood!);
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)) {
            Console.Error.WriteLine(value: $"[stub: could not cancel pending install and roll back — {exception.Message}]");
            return 2;
        }
        launchVersion = lastGood!;

        break;
    case StubAction.LaunchCurrentAnyway:
        Console.Error.WriteLine(value: $"[stub: stuck on version {current} — {attempts}/{configuration.MaxAttempts} attempts exhausted and its state-generation exceeds last-good's; launching it anyway rather than risk an incompatible read]");

        break;
}
Puck.Confinement.ConfinedDirectory heldVersion;
try {
    heldVersion = StubInstall.HoldVersion(installRoot: installRoot, version: launchVersion);
} catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException or ArgumentException or PlatformNotSupportedException)) {
    Console.Error.WriteLine(value: $"[stub: could not hold version {launchVersion} for its launch — {exception.Message}]");
    return 2;
}
// Held from before the child starts until it exits, so pruning never removes the version the child runs from.
using var heldVersionLifetime = heldVersion;
StubHealth.IncrementAndFlush(
    installRoot: installRoot,
    version: launchVersion
);
```

`src/Puck.Launcher.Stub/StubDecisionTable.cs:35-44`

```csharp
    public static StubAction Decide(int attempts, int maxAttempts, bool hasLastGood, int currentGeneration, int lastGoodGeneration) {
        if (attempts < maxAttempts) {
            return StubAction.LaunchCurrent;
        }

        return ((hasLastGood && (currentGeneration <= lastGoodGeneration))
            ? StubAction.RevertToLastGood
            : StubAction.LaunchCurrentAnyway
        );
    }
```

`src/Puck.Launcher.Stub/StubInstall.cs:49-65`

```csharp
    public static void RevertToLastGood(string installRoot, string failedVersion, string lastGoodVersion) {
        var intentPath = Path.Combine(path1: installRoot, path2: "install-intent.json");

        if (File.Exists(path: intentPath)) {
            using var intent = JsonDocument.Parse(json: File.ReadAllText(path: intentPath));

            if ((intent.RootElement.ValueKind != JsonValueKind.Object) ||
                !intent.RootElement.TryGetProperty(propertyName: "manifest", value: out var manifest) || (manifest.ValueKind != JsonValueKind.Object) ||
                !manifest.TryGetProperty(propertyName: "version", value: out var version) || (version.ValueKind != JsonValueKind.String)) {
                throw new JsonException(message: "pending install intent has no manifest version");
            }
            if (string.Equals(a: version.GetString(), b: failedVersion, comparisonType: StringComparison.Ordinal)) {
                File.Delete(path: intentPath);
            }
        }
        WritePointerAtomic(fileName: CurrentFileName, installRoot: installRoot, value: lastGoodVersion);
    }
```

`src/Puck.Launcher.Stub/StubHealth.cs:43-56`

```csharp
    public static void IncrementAndFlush(string installRoot, string version) {
        var attempts = (AttemptsFor(
            record: Read(installRoot: installRoot),
            version: version
        ) + 1);

        Write(
            installRoot: installRoot,
            record: new StubHealthRecord(
                Attempts: attempts,
                Version: version
            )
        );
    }
```

**Policy change at these selection points:**

- `StubDecisionTable.Decide` and `StubInstall.RevertToLastGood` are replaced by
  `ReleasePolicy` (`SelectLaunch`), which combines pure candidate evaluation
  (`EvaluateLaunch`, directly unit-testable with zero I/O) and confined atomic
  transition execution under the stub's held `update.lock` lease.
- `StubAction.LaunchCurrentAnyway` is deleted. The three outcomes of
  `SelectLaunch` are:
  1. `LaunchCurrent(string Version)`: `current` is neither revoked nor
     payload-rejected and `attempts < maxAttempts`.
  2. `RolledBack(string FailedVersion, string SelectedVersion, string Reason)`:
     `current` was revoked or exhausted `maxAttempts` (and its `PayloadPin` was
     durably added to `rejectedPayloads`), any matching `install-intent.json`
     was deleted, and `SelectedVersion` is the highest-`Sequence` proven version
     on disk that is unrevoked, not payload-rejected, and satisfies
     `SelectedVersion.StateGeneration >= policy.MaxLaunchedStateGeneration`;
     both `last-good` and `current` have been atomically updated to
     `SelectedVersion`.
  3. `Refuse(string FailedVersion, string Reason)`: `current` is revoked or
     exhausted `maxAttempts` (with its `PayloadPin` durably added to
     `rejectedPayloads` and its intent cancelled), and no proven version on disk
     is both allowed and state-compatible. `Reason` names `FailedVersion` and the
     disqualification of each proven candidate (for example,
     `"version '1.2.0' exhausted 3/3 startup attempts (payload sha256/... rejected); rollback refused: candidate '1.1.0' is revoked; no allowed state-compatible proven candidate exists"`).
     The stub writes `Reason` to `Console.Error` and returns exit code `2`
     before `StubInstall.HoldVersion` or `StubHealth.IncrementAndFlush`.
- When `RolledBack` succeeds and `StubHealth.IncrementAndFlush` overwrites
  `state/health.json` with `SelectedVersion` (`Attempts: 1`), `FailedVersion`'s
  rejection remains permanently in `state/release-policy.json`.

### 6. `InstallPruner.Prune` (`InstallPruner.Read` and `InstallPruner.Act`)

`src/Puck.Launcher/Release/InstallPruner.cs:139-169`

```csharp
            foreach (var entry in (versions?.Entries() ?? [])) {
                if (entry.Kind == ConfinedEntryKind.Link) { return Failed(reason: $"{VersionsDirectoryName}/{entry.Name} is a link"); }
                if (entry.Kind != ConfinedEntryKind.Directory) { continue; }
                using var version = (versions!.Child(name: entry.Name) ?? throw new IOException(message: $"{VersionsDirectoryName}/{entry.Name} vanished"));
                var record = InstallRecord.TryRead(version: version);

                _ = names.Add(item: entry.Name);
                found.Add(item: new Version(
                    Name: entry.Name,
                    Objects: new HashSet<string>(collection: ((record is not null) ? PinHexes(pins: record.Files) : ObjectsHeld(version: version)), comparer: StringComparer.OrdinalIgnoreCase),
                    Sequence: record?.Sequence
                ));
            }
            var protectedNames = new HashSet<string>(comparer: NameComparer);

            foreach (var (pointer, required) in new[] { (InstallLayout.CurrentFileName, true), ("last-good", false) }) {
                var named = ReadPointer(name: pointer, root: root);

                if (named is null) {
                    if (required) { return Failed(reason: $"'{pointer}' names no version"); }
                    continue;
                }
                if (!IsPlainName(name: named) || !names.Contains(item: named)) { return Failed(reason: $"'{pointer}' names '{named}', which is not a version directory"); }
                _ = protectedNames.Add(item: named);
            }
            var (intentVersion, intentObjects) = ReadIntent(root: root);

            if (intentVersion is not null) {
                if (!IsPlainName(name: intentVersion)) { return Failed(reason: $"the pending install intent names '{intentVersion}', which is not a version name"); }
                _ = protectedNames.Add(item: intentVersion);
            }
```

`src/Puck.Launcher/Release/InstallPruner.cs:190-231`

```csharp
        // Most recent install first; a version whose install order is unknown ranks nowhere and is kept.
        var ordered = plan.Versions.OrderByDescending(keySelector: static version => (version.Sequence ?? 0L)).ThenByDescending(keySelector: static version => version.Name, comparer: StringComparer.Ordinal).ToList();
        var kept = new List<Version>();
        var running = new List<string>();
        var deleted = new List<string>();
        var retained = 0;

        foreach (var version in ordered) {
            if (plan.Protected.Contains(item: version.Name) || (version.Sequence is null) || (versions is null)) {
                kept.Add(item: version);
                continue;
            }
            // A running version is kept for its process, never for the allowance: the allowance keeps the newest others.
            // No process registers meanwhile, because registration waits for the update lease this pass holds.
            switch (Probe(name: version.Name, refused: refused, versions: versions)) {
                case Removal.Running:
                    running.Add(item: version.Name);
                    kept.Add(item: version);
                    continue;
                case Removal.Refused:
                    kept.Add(item: version);
                    continue;
                case Removal.Gone:
                    continue;
            }
            if (retained++ < keepVersions) {
                kept.Add(item: version);
                continue;
            }
            switch (Remove(name: version.Name, refused: refused, trash: trash, versions: versions)) {
                case Removal.Removed:
                    deleted.Add(item: version.Name);
                    break;
                case Removal.Running:
                    running.Add(item: version.Name);
                    kept.Add(item: version);
                    break;
                case Removal.Refused:
                    kept.Add(item: version);
                    break;
            }
        }
```

**Policy change at this selection point:**

- `InstallPruner.Read` opens `state/release-policy.json` through the confined
  `state` directory handle (`root.Child(name: InstallLayout.StateDirectoryName)`)
  and validates it through `ReleasePolicy`. As with every other metadata read in
  `InstallPruner.Read`, a corrupt or unreadable policy file fails the validation
  phase (`Failed(reason: ...)`) so the pass skips without deleting anything.
- `InstallPruner.Read` does not protect `last-good` or `intentVersion` if the
  policy marks that version revoked or its `PayloadPin` rejected; if `last-good`
  on disk is stale and names a revoked/rejected version, `ReleasePolicy` repairs
  `last-good` to the top allowed proven candidate (or removes `last-good` when
  none exists) under the pruner's held `update.lock`.
- In `InstallPruner.Act`, `ReleasePolicy` (`RankRetainedVersions`) partitions
  non-protected, non-running versions with known `Sequence`:
  1. **Disqualified versions** (any version in `policy.RevokedVersions` or whose
     `InstallRecord`'s `PayloadPin` is in `policy.RejectedPayloads`) get **zero**
     `keepVersions` allowance slots (`retained` is not incremented for them; they
     proceed directly to `Remove(...)` unless held by a live process).
  2. **Eligible versions** are ranked with allowed, state-compatible proven
     versions (`policy.ProvenVersions` with
     `StateGeneration >= policy.MaxLaunchedStateGeneration`) ordered by
     `Sequence` descending ahead of unproven versions, so a rejected or revoked
     install can never evict a proven rollback candidate from the `keepVersions`
     window, and pruning actively reclaims rejected/revoked version directories
     and their unreferenced content-addressed objects.

## Failure and interruption schedules

### Schedule 1: Signed release, failed startup, rollout progression and changed payload

1. **Initial state:** Version `1.0.0` (`Sequence = 1`, `StateGeneration = 1`,
   `PayloadPin = P_100`) is installed, launched by the stub, and clears
   `HealthGateFile.ClearHealthy`. `state/release-policy.json` holds
   `provenVersions = [1.0.0 (seq 1, gen 1, P_100)]`, `last-good = 1.0.0`,
   `current = 1.0.0`, and no `state/health.json`.
2. **Signed release `1.1.0` applied:** Manifest `M_1` (`Version = 1.1.0`,
   `Sequence = 10`, `Rollout.Percent = 10`, `StateGeneration = 1`,
   `PayloadPin = P_bad`) verifies and applies. `FileUpdateApplier.Apply` records
   `versions/1.1.0/install-record.json` with `PayloadPin = P_bad`, refreshes
   `last-good = 1.0.0`, writes `state/health.json` (`Version = 1.1.0, Attempts = 0`),
   and sets `current = 1.1.0`.
3. **Failed startup (`maxAttempts = 3`):** Three consecutive stub launches of
   `1.1.0` exit before `ApplicationStarted`. Before each launch,
   `StubHealth.IncrementAndFlush` advances `state/health.json` to `Attempts = 1`,
   `2`, `3`.
4. **Fourth stub launch triggers rejection and rollback:**
   - The stub acquires `update.lock` and calls `ReleasePolicy` (`SelectLaunch`).
   - `attempts (3) >= maxAttempts (3)`: `ReleasePolicy` adds
     `(rid, P_bad, "1.1.0")` to `rejectedPayloads` in `state/release-policy.json`
     and flushes it atomically.
   - `ReleasePolicy` deletes any matching `install-intent.json`, selects `1.0.0`
     from `provenVersions` (unrevoked, `P_100 != P_bad`, `gen 1 >= 1`), updates
     `last-good = 1.0.0` and `current = 1.0.0`, holds `versions/1.0.0`, and
     calls `StubHealth.IncrementAndFlush(installRoot, "1.0.0")`, overwriting
     `state/health.json` with `(Version = "1.0.0", Attempts = 1)`.
   - `1.0.0` starts, reaches `ApplicationStarted`, clears `state/health.json`,
     and `InstallPruner.Prune` removes `versions/1.1.0` because `P_bad` is in
     `rejectedPayloads`.
5. **Rollout progression / fresh higher-sequence authorization of the same payload:**
   - The channel publishes `M_2` (`Version = 1.1.0`, `Sequence = 11`,
     `Rollout.Percent = 50`, `PayloadPin = P_bad`) and later `M_3`
     (`Version = 1.1.1`, `Sequence = 12`, `Rollout.Percent = 100`, changed
     `Notes`, identical RID files so `PayloadPin = P_bad`).
   - Both `UpdateService.CheckAsync` and `UpdateService.ApplyAsync` call
     `AttestationReleaseVerifier.Verify`, which computes `P_bad` via
     `ReleasePayloadIdentity`, sees `P_bad` in `policy.RejectedPayloads`, and
     returns `ReleaseVerifyOutcome.Refuse("payload 'sha256/...' for rid 'win-x64' was rejected after failing startup in version '1.1.0'")`.
   - Neither `M_2` nor `M_3` is staged or applied.
6. **Changed payload remains eligible:**
   - The channel publishes `M_4` (`Version = 1.1.2`, `Sequence = 13`,
     `PayloadPin = P_fixed != P_bad` because the broken binary was rebuilt).
   - `P_fixed` is not in `policy.RejectedPayloads` and `1.1.2` is not in
     `policy.RevokedVersions`; `AttestationReleaseVerifier.Verify` accepts `M_4`,
     and `UpdateService.ApplyAsync` stages and activates `1.1.2`.

### Schedule 2: Signed release, failed startup, revoked `last-good`

#### Case 2A: `1.0.0` and `1.1.0` both proven; `1.2.0` revokes `1.1.0` and fails startup

1. `1.0.0` (`seq 1, gen 1, P_100`) and `1.1.0` (`seq 2, gen 1, P_110`) have both
   completed startup and cleared `state/health.json`. `provenVersions` holds
   both `1.0.0` and `1.1.0`; `current = 1.1.0`, `last-good = 1.0.0`.
2. Manifest `M_120` (`Version = 1.2.0`, `Sequence = 20`, `StateGeneration = 1`,
   `Revoked = ["1.1.0"]`, `PayloadPin = P_120`) is fetched and applied:
   - `AttestationReleaseVerifier.Verify` persists `"1.1.0"` into
     `policy.RevokedVersions` in `state/release-policy.json`.
   - In `FileUpdateApplier.Apply`, `previousVersion` (`1.1.0`) is in
     `policy.RevokedVersions`, so `ReleasePolicy` (`CommitApply`) does **not**
     promote `1.1.0` to `last-good`; instead it selects the highest-`Sequence`
     remaining allowed proven version on disk (`1.0.0`) and writes
     `last-good = 1.0.0` before setting `current = 1.2.0`.
3. `1.2.0` fails startup `maxAttempts` times.
4. On the next stub launch, `ReleasePolicy` (`SelectLaunch`) records `P_120` in
   `rejectedPayloads`, skips `1.1.0` because `"1.1.0"` is in `revokedVersions`,
   selects `1.0.0` (`seq 1, gen 1 >= 1`, unrevoked, `P_100` not rejected),
   writes `current = 1.0.0`, and launches `1.0.0`. The revoked `1.1.0` is never
   published to `last-good` or `current`, and `InstallPruner.Prune` deletes both
   `versions/1.2.0` (rejected) and `versions/1.1.0` (revoked).

#### Case 2B: Only `1.1.0` is proven (or `1.0.0` had an older incompatible `StateGeneration`); `1.2.0` revokes `1.1.0` and fails startup

1. `1.1.0` (`seq 2, gen 2, P_110`) is the only proven version compatible with
   `maxLaunchedStateGeneration = 2` (either `1.0.0` was never installed or had
   `StateGeneration = 1 < 2`).
2. Manifest `M_120` (`Version = 1.2.0`, `Sequence = 20`, `StateGeneration = 2`,
   `Revoked = ["1.1.0"]`, `PayloadPin = P_120`) verifies and applies:
   - `"1.1.0"` is persisted in `policy.RevokedVersions`.
   - `ReleasePolicy` (`CommitApply`) sees no allowed, state-compatible proven
     version in `policy.ProvenVersions` (`1.1.0` is revoked; `1.0.0` has
     `gen 1 < 2`), so it deletes `last-good` on disk before setting
     `current = 1.2.0`.
3. `1.2.0` fails startup `maxAttempts` times.
4. On the next stub launch, `ReleasePolicy` (`SelectLaunch`):
   - Durably records `P_120` in `policy.RejectedPayloads` and cancels any
     matching `install-intent.json`.
   - Evaluates `policy.ProvenVersions`: `1.1.0` is disqualified (`revoked`),
     `1.0.0` is disqualified (`state-generation 1 < required 2`).
   - Returns `Refuse` (`StubLaunchDecision`) naming `1.2.0` (attempts exhausted,
     payload rejected) and each disqualified candidate (`1.1.0` revoked, `1.0.0`
     state-generation incompatible).
   - The stub writes the refusal to stderr and exits with code `2`. Neither the
     revoked `1.1.0` nor the failed `1.2.0` is launched.

#### Case 2C: `ReleaseManifest.MinimumSupported` does not block rollback

1. `1.0.0` (`seq 1, gen 1`) and `1.1.0` (`seq 2, gen 1`) are proven.
2. Manifest `M_120` (`Version = 1.2.0`, `StateGeneration = 1`,
   `MinimumSupported = "1.1.0"`, `Revoked = []`) is applied over `1.1.0`
   (passing `MinimumSupported` because `installedVersion` is `1.1.0 >= 1.1.0`).
3. Later, `1.1.0` is revoked (or `1.2.0` was applied directly over `1.0.0` when
   `MinimumSupported` was `1.0.0`, and a subsequent check saw a higher
   `MinimumSupported`). Because `MinimumSupported` is never persisted into
   `state/release-policy.json`, `1.0.0` remains an eligible rollback candidate
   whenever it is unrevoked and state-compatible (`gen 1 >= 1`).

### Schedule 3: Interruption at every write boundary

| Write boundary | State on disk immediately before crash | Recovery on next stub launch or `UpdateService.ApplyAsync` |
|---|---|---|
| **W1.** `AttestationReleaseVerifier` writes `revokedVersions` to `state/release-policy.json`, crashes before `m_sequenceStore.TryReserve` / `TryAdvance`. | `state/release-policy.json` has the new revocations; `state/sequence-mark` is at the prior sequence; `install-intent.json` is absent. | Next `CheckAsync` / `ApplyAsync` re-verifies the manifest against the unadvanced sequence mark, re-merges `revokedVersions` idempotently, and proceeds. If the stub launches meanwhile, the new revocations are already enforced. |
| **W2.** `UpdateService` reserves sequence in `state/sequence-mark`, crashes before writing `install-intent.json`. | `state/release-policy.json` has any revocations; `state/sequence-mark` holds the reservation `(epoch, seq, manifestPin)`; `install-intent.json` is absent. | Existing contract (`src/Puck.Launcher/Release/UpdateService.cs:40-42`): sequence is consumed; next `ApplyAsync` requires a newer sequence from the channel. `current` and `last-good` are untouched. |
| **W3.** `FileUpdateApplier` writes `state/release-policy.json` and updates/clears `last-good`, crashes before writing `versions/<v>/install-record.json`, `state/health.json` or `current`. | `current` still names `previousVersion`; `install-intent.json` is present; `last-good` already excludes any version revoked by the pending manifest. | Next `ApplyAsync` recovers `install-intent.json` and completes `ApplyCore` idempotently. If the stub launches before `ApplyAsync` runs, it launches `previousVersion` (or rolls back to the already-filtered `last-good` if `previousVersion` itself was revoked). |
| **W4.** `FileUpdateApplier` writes `state/health.json` (`MarkPending`), crashes before writing `current`. | `state/health.json` names `<newVersion>` with `Attempts: 0`; `current` still names `previousVersion`; `install-intent.json` is present. | If the stub launches `previousVersion`, `StubHealth.AttemptsFor` returns `0` for `previousVersion` (version mismatch) and `previousVersion` runs normally. When `ApplyAsync` recovers `install-intent.json`, `state/health.json` exists so `ApplyCore` does not re-promote `previousVersion` over `last-good`, and finishes writing `current`. |
| **W5.** Stub writes `rejectedPayloads` to `state/release-policy.json` when `attempts >= maxAttempts`, crashes before deleting `install-intent.json` or writing `current`. | `state/release-policy.json` has `current`'s `PayloadPin` in `rejectedPayloads`; `current` still names `failedVersion`; `state/health.json` still has `Attempts = maxAttempts`. | Next stub launch reads `state/release-policy.json`, sees `current`'s `PayloadPin` is in `rejectedPayloads`, deletes the matching `install-intent.json`, writes `current` (and `last-good`) to the selected proven candidate, and launches it. If `UpdateService` ran first, `ReserveInstall` sees the rejected `PayloadPin` and terminally retires `install-intent.json`. |
| **W6.** Stub deletes `install-intent.json` and writes `current = candidate.Version`, crashes before `StubHealth.IncrementAndFlush(installRoot, candidate.Version)`. | `state/release-policy.json` holds the rejection; `current` and `last-good` name `candidate.Version`; `state/health.json` still holds `(failedVersion, maxAttempts)`. | Next stub launch reads `current = candidate.Version`; `StubHealth.AttemptsFor` sees `record.Version != candidate.Version` and returns `0` attempts for `candidate.Version`; stub increments `state/health.json` to `(candidate.Version, 1)` and launches `candidate.Version`. |
| **W7.** `HealthGateFile.ClearHealthy` writes `provenVersions` to `state/release-policy.json`, crashes before deleting `state/health.json`. | `version` is in `provenVersions`; `state/health.json` still exists with `Attempts: 1`. | Next stub launch sees `attempts (1) < maxAttempts`, increments to `2`, and launches `version`, which clears `state/health.json` on `ApplicationStarted`. |

## Options weighed and trade-offs

### Question 1: How to identify a rejected payload

| Option | Mechanism | Attacks / failure modes closed | Trade-offs |
|---|---|---|---|
| **Option 1A: Reject by `Version` string or full `ReleaseManifest` pin** | Record `manifest.Version` or `ContentPin` of the full unsigned manifest when startup fails. | Closes retrying the byte-identical manifest JSON. | **Fails finding 2.1.** Changing `rollout.percent` (`10` to `50`), `notes`, `minimumSupported` or `version` (`1.2.0` to `1.2.1`) changes the manifest pin and version string while shipping the exact same broken binaries, so a higher-sequence manifest re-installs the failed payload. |
| **Option 1B: Reject by canonical RID `PayloadPin` (`ReleasePayloadIdentity`) (Recommended)** | Hash the canonical tuple `(schema, rid, stateGeneration, sorted (path, hash, size)[])` of the target RID's `ReleasePayload.Files`. Persist in `versions/<v>/install-record.json` and `state/release-policy.json`. | Closes rollout progression, re-signing at a higher sequence, release-note edits, `minimumSupported` or `revoked` edits, and version-label bumps over identical RID files, while immediately admitting any build that changes at least one payload file or `StateGeneration`. | Requires computing one SHA-256 over the manifest's file inventory (~1–5 KB of text) during verification and apply, and storing one 71-character pin string in `InstallRecord`. |
| **Option 1C: Reject if any single file hash overlaps a failed version** | Record every `ReleasePayloadFile.Hash` from the failed version as rejected. | Closes any manifest sharing even one file with the failed release. | **Unusable.** Unchanged third-party runtime DLLs, shaders or assets shared across releases would poison every subsequent release. |

### Question 2: How to represent rollback candidates and revocations

| Option | Mechanism | Attacks / failure modes closed | Trade-offs |
|---|---|---|---|
| **Option 2A: Keep scalar `last-good` file + separate `state/revoked.json`, no proven history** | Persist `state/revoked.json` and check `!revoked.Contains(lastGood)` in the stub and applier. | Prevents launching a revoked `last-good` when only one prior version exists. | Loses multi-version fallback: if `1.0.0` and `1.1.0` were both proven and kept on disk (`keepVersions = 2`), and `1.2.0` revokes `1.1.0` then fails startup, a scalar `last-good` (`1.1.0`) forgets that `1.0.0` was also proven, forcing an unnecessary refusal. Also scatters policy across multiple files that cannot be updated in a single atomic rename. |
| **Option 2B: Derive rollback candidates by scanning `versions/*/install-record.json` without tracking proven status** | Roll back to the highest-`Sequence` unrevoked version in `versions/`. | Avoids maintaining a `provenVersions` list. | **Unsafe.** A version directory in `versions/` may have been staged but never launched, or launched once and superseded by a newer update before it ever reached `ApplicationStarted` (`FileUpdateApplier.cs:106-116`). Rolling back to an unproven version violates the health gate contract. |
| **Option 2C: One consolidated `state/release-policy.json` holding `revokedVersions`, `rejectedPayloads`, `provenVersions` and `maxLaunchedStateGeneration` (Recommended)** | One JSON document in `state/release-policy.json` read and written through `ReleasePolicy` in `Puck.Confinement`, with `last-good` maintained as a derived pointer to the top eligible proven candidate. | Closes both findings 2.1 and 2.2 in a single atomic file rename per transition; preserves fallback to earlier proven versions (`1.0.0`) when a newer proven version (`1.1.0`) is revoked; keeps `state/health.json` strictly transient; gives `InstallPruner` exact proven/rejected/revoked status for retention ranking. | Requires migrating `StubDecisionTable`, `StubInstall`, `FileUpdateApplier`, `HealthGateFile`, `AttestationReleaseVerifier` and `InstallPruner` to call `ReleasePolicy`. |

### Question 3: What the stub does when no allowed, state-compatible proven rollback candidate exists

| Option | Mechanism | Attacks / failure modes closed | Trade-offs |
|---|---|---|---|
| **Option 3A: Keep `StubAction.LaunchCurrentAnyway`** | If `attempts >= maxAttempts` and no valid rollback target exists, keep launching `current`. | Avoids a refusal exit code on first-install startup failure. | **Violates both findings.** If `current` is revoked or has failed startup `maxAttempts` times and `last-good` is revoked, `LaunchCurrentAnyway` executes a known-revoked or known-broken binary indefinitely. |
| **Option 3B: Refuse by name (`Refuse` on `StubLaunchDecision`, exit code `2`) (Recommended)** | Once `current` is revoked or reaches `maxAttempts` with no allowed, state-compatible proven fallback on disk, record the rejection, clear any revoked `last-good`, log the exact refusal reason naming `current` and each disqualified candidate to stderr, and exit `2`. | Guarantees a revoked or exhausted payload is never executed after its attempt budget expires and a revoked `last-good` is never published or launched. | An install whose only proven fallback was revoked and whose new update also fails startup requires an out-of-band update command or reinstall to recover — which is the intended security posture when every local candidate is either broken or revoked. |

## Recommendation and build order

Adopt **Option 1B + Option 2C + Option 3B**, implemented in `Puck.Confinement`
and wired through every selection point in a single change:

1. **Add `ReleasePolicy` and `ReleasePayloadIdentity` in `src/Puck.Confinement/` and extend `InstallLayout` in `src/Puck.Confinement/InstallRegistration.cs`.**
   - Define `StateDirectoryName` (`"state"`),
     `HealthFileName` (`"health.json"`),
     `ReleasePolicyFileName` (`"release-policy.json"`), and
     `LastGoodFileName` (`"last-good"`) on `InstallLayout`.
   - Move `InstallRecord` to `Puck.Confinement` with fields
     `(long Sequence, string Rid, int StateGeneration, string PayloadPin, IReadOnlyList<string> Files)`.
   - Implement `ReleasePayloadIdentity` (`Compute`) and `ReleasePolicy`
     (`CommitRevocations`, `EvaluateManifest`, `CommitApply`, `RecordHealthy`,
     `SelectLaunch`, `EvaluateLaunch`, and `RankRetainedVersions`).
   - **Mechanical check:** `Puck.Confinement` builds with zero project references
     (`src/Puck.Confinement/Puck.Confinement.csproj`); `StubDecisionTable.Decide`
     and `StubAction.LaunchCurrentAnyway` are deleted so any caller bypassing
     `ReleasePolicy` fails compilation.

2. **Wire `AttestationReleaseVerifier` and `UpdateService` in `src/Puck.Launcher/Release/`.**
   - Inject `IReleasePolicyStore` (defaulting to `FileReleasePolicyStore` over
     `Path.Combine(options.CacheRoot, "state", "release-policy.json")` in
     `LauncherServiceRegistration.AddSelfUpdate`, and `InMemoryReleasePolicyStore`
     in isolated verifier unit tests).
   - In `AttestationReleaseVerifier.VerifyCore`, persist `manifest.Revoked` via
     `CommitRevocations` immediately after canonical-hash verification, then call
     `EvaluateManifest` to refuse any manifest whose `Version` is in
     `revokedVersions` or whose RID `PayloadPin` is in `rejectedPayloads`.
   - Keep `manifest.MinimumSupported` in `AttestationReleaseVerifier.VerifyCore`
     and `UpdateService.ApplyUnderLeaseAsync` as update-eligibility checks only.
   - **Mechanical check:** Unit tests in `tests/Puck.Launcher.Tests/` proving
     that (a) a manifest with higher sequence and progressed `Rollout.Percent`
     or bumped `Version` over a rejected `PayloadPin` is refused by `Verify` and
     `ApplyAsync`; (b) a manifest with a changed file hash or `StateGeneration`
     is accepted; (c) a manifest revoking an older version persists that
     revocation even during `CheckAsync` (`advanceSequence: false`) or when the
     manifest's own version is refused.

3. **Wire `FileUpdateApplier`, `HealthGateFile` and `InstallPruner` in `src/Puck.Launcher/Release/`.**
   - In `FileUpdateApplier.ApplyCore`, verify staged files, compute `PayloadPin`,
     call `ReleasePolicy` (`CommitApply`) to update `state/release-policy.json`
     and `last-good` without ever promoting a revoked or unproven version, and
     write the extended `InstallRecord`.
   - In `HealthGateFile.ClearHealthy`, call `ReleasePolicy` (`RecordHealthy`)
     under `update.lock` before deleting `state/health.json`.
   - In `InstallPruner.Read` and `InstallPruner.Act`, read and validate
     `state/release-policy.json` through `ConfinedDirectory` and rank inactive
     versions through `ReleasePolicy` (`RankRetainedVersions`) so rejected and
     revoked versions consume zero `keepVersions` slots and are pruned when not
     held by a running process.
   - **Mechanical check:** Unit tests in `tests/Puck.Launcher.Tests/` proving
     that (a) `FileUpdateApplier.Apply` never writes a revoked `previousVersion`
     to `last-good`; (b) `InstallPruner.Prune` removes rejected and revoked
     inactive versions while preserving proven versions up to `keepVersions`;
     (c) a corrupt `state/release-policy.json` causes `InstallPruner.Prune` to
     skip without deleting anything.

4. **Wire `Puck.Launcher.Stub` (`src/Puck.Launcher.Stub/Program.cs`, `StubInstall.cs`, `StubDecisionTable.cs`).**
   - Replace `StubDecisionTable.Decide` and `StubInstall.RevertToLastGood` in
     `src/Puck.Launcher.Stub/Program.cs` with `ReleasePolicy` (`SelectLaunch`).
   - Delete `StubAction.LaunchCurrentAnyway`.
   - **Mechanical check:** End-to-end schedule tests in
     `tests/Puck.Launcher.Tests/` exercising Schedule 1 (signed release $\to$
     failed startup $\to$ fallback launch overwrites `state/health.json` $\to$
     fresh higher-sequence rollout progression refused $\to$ changed payload
     accepted), Schedule 2A/2B/2C (signed release $\to$ failed startup $\to$
     revoked `last-good` selects earlier compatible proven version or refuses by
     name; `MinimumSupported` does not block rollback), and Schedule 3
     (simulated interruption between each write boundary W1–W7).
