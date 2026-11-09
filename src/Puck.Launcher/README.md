# Puck.Launcher

Puck.Launcher supplies the shared application loop and launcher services. It
connects fixed-step execution, console input and output, presentation pacing,
backend selection, and release updates to the host's dependency-injection
container. A composition root chooses the services it needs.

## Usage

`LauncherServiceRegistration` contains the shared registration entry points.
`PuckExtensionServiceRegistration.AddPuckExtensions` installs a host's composed
[extensions](../../docs/reference/extensions.md): the set, its contributed hosted
services, the optional hosted control, and `world.extensions.catalog`.
Platform presentation registration lives in
[Puck.Launcher.Windows](../Puck.Launcher.Windows/README.md) and
[Puck.Launcher.Linux](../Puck.Launcher.Linux/README.md). Keeping that registration
separate lets the composition root choose a platform without making this
library depend on a GPU backend.

`FixedStepPump` drives every host loop: windowed, headless, and offscreen. It
drains the console before each step. It holds the first step until a piped
script reaches its first tick wait, or standard input ends. The
[commands reference](../../docs/reference/commands.md#who-can-dispatch-a-command)
explains the timing a script can rely on. Windowed and offscreen pumps also hold
their clocks for owed frames: before each step they ask the simulation
(`IFixedStepSimulation.HoldsClock`), and while a frame a step owes is unserved
they withhold the step and spend the time rather than owing it. The headless
pump never asks. A loop that produces frames calls
`IFixedStepSimulation.SettleOwedFrames` in its teardown before it disposes its
render root.

The windowed and headless loops hand `FixedStepPump.Advance` the wall time
they sampled and take every step it covers, for at most one step's period of
wall time: once a call's steps have spent that long, the whole steps still due
are shed and the capture pin rebased by them, as the quarter-second clamp
(`maxFrameTicks`) does. So a step that costs more than its period slows the
World rather than the frame. The offscreen loop's time is its
tick count: it calls `FixedStepPump.TryStep`, which runs at most one step
whatever the interval was, and composes one frame for that step, so a slow
frame delays the next tick instead of bursting several (see
[host pacing](../../docs/reference/hosting.md#host-pacing)). An unpaced headless
run (`LauncherOptions.Unpaced`) also calls `TryStep`, and waits for nothing.
Each loop, its input capture clock, and device recovery use the same registered
`TimeProvider`, which defaults to the system clock. `OffscreenTickPacingLawTests`
makes its frames slow through that provider.

The windowed loop paces off its presenter's swap chain (`ISwapChainPacing`)
whenever the swap chain presents on vertical blank: after each present it
waits until the swap chain's queue has room, so each iteration starts on a
vertical blank and its interval is present to present. A timer paces the rest
(an explicit rate no whole number of refreshes reaches, a tearing or mailbox
present mode), and on a variable-refresh display it runs after the swap chain's
wait, since that display shows a frame as it arrives; [host
pacing](../../docs/reference/hosting.md#host-pacing) has the rule, and
`PresentPacingPolicyLawTests` holds the decision from the display's facts. The loop runs
at the highest thread priority, so it wakes promptly on either: at normal
priority the timer's wait returned up to 5.7 ms late on a quiet machine. It
records each iteration in a `HostFrameTimeline`, which the
`host.frames [frames] [budget-ms] [worst]` verb reads: the interval's average,
median, 99th percentile and maximum over the last iterations, the count over a
budget (one 60 Hz period by default), the most work any iteration did outside
the pacing wait, how late the timer woke (zero while the swap chain paces), the
garbage collections and their pause time, and the slowest iterations split into
input, commands, simulate (with its fixed steps), begin, produce, present and
wait.
The headless host records commands, simulate and wait through the same timeline; rendered phases stay zero. Its `--unpaced` mode records one fixed tick per iteration, so a cold write can be read separately from catch-up steps.
`host.frames reset` starts a fresh window. It is the worst-frame reading a
frame-time bar is judged by; `world.fps` reads a two-second window and counts
the frame in progress at the moment it runs, so a script reads it once before
it measures, or the first call's compilation lands in the reading.

Every terminal shape answers `host.log`, which reads the `HostLog` ring of the
process's stdout and stderr lines by sequence, tick, category, stream and text
([hosting](../../docs/reference/hosting.md)).

Each host loop runs on its own thread, and its hosted service's task faults
with whatever the loop threw, so a failing loop stops the host instead of
killing the process. A presenter whose `Activate` throws is reported as
`PresenterActivationException`. `LauncherHostRun.RunAsync` runs a built host
and returns the process exit code. A `HostResourceUnavailableException` — a
backend's `GpuDeviceUnavailableException` or a listener's
`ListenEndpointUnavailableException` — raised while the host started or inside a
loop returns 2 and prints one `[<label>.host: unsupported: …]` line. Any other loop failure is rethrown. The
first failure is the one the run reports. `LauncherHostRun.RunTeardown` runs a
loop's teardown steps after a fault and logs any step that fails, so a render
root that throws on disposal cannot mask the device failure. A later failure
while the host stops is written as a separate line.

`Release/` owns launcher-side release verification, staging, and update
application. A launcher-based program — the desktop client or a
player-hosted headless authority — updates itself from a signed
`puck.release.manifest.v1` manifest: per-RID file lists addressed by content hash (so
an unchanged file costs nothing to restage), a signature chain rooted at the
publisher, a deterministic staged rollout bucket, revocation, and a
minimum-supported version. `IUpdateStager` stages a new version side by side
with the running one; `IUpdateApplier` rechecks its files and swaps the current
pointer for the next launch. `AddSelfUpdate` selects `AttestationReleaseVerifier`
for a configured trust anchor; `PlaceholderReleaseVerifier` refuses every
manifest when the anchor is a placeholder. The
[launcher stub](../Puck.Launcher.Stub/README.md) selects the installed
version to start and handles the health-based rollback decision. The
[release guide](../../docs/development/ci.md) owns the shared publishing
workflow and its environment requirements.

`ReleaseDecision` separates terminal refusals from transient contention.
`UpdateService` retires an intent only after a terminal verification or applier
refusal; sequence-store contention and a backwards clock leave it resumable.
Both the hosted application and local MCP adapter use `HealthGateFile.ClearHealthyAsync`
to wait through lock contention; permanent I/O, permission and platform failures end clearing.

The signed attestation carries the canonical unsigned manifest's content pin,
which authenticates its complete file inventory without putting that inventory
inside the attestation's bounded payload. Development releases use a separate
local key and the same attestation verifier, stager and applier. They grant no
publisher trust. `SelfUpdateConfigFile.Channel` can select a local development
channel alongside its explicit source, trust anchor and install root.
`LocalBuildReleaseSource` publishes completed local application builds through
`DirectoryReleaseSource`'s content-addressed layout, signed by a development
key outside the payload. The operator MCP launcher uses this source and the
ordinary update chain; it has no separate application-copy staging path.

The `self-update` canary installs the complete prepared debug application,
including `puck world prepare`'s documents and assets. Its baseline and candidate
both pass through `UpdateService`; it never pre-seeds a version directory.
The positive leg relaunches the activated version. The other leg activates a
signed payload without its executable, observes the refused launch, and then
observes the stub reject that payload and roll back to the proven version.

Staging rebuilds the inactive version directory from the manifest's complete
file set, so a retry cannot inherit files omitted by a replacement manifest.
Cached objects are still reused and rehashed; a corrupt object is deleted so a
later attempt can download it again. Direct stager callers hold the
install lease and exclude the current version, as `UpdateService` does.

`UpdateService` holds the install root's persistent `update.lock` lease across
verification, staging and activation. Rollout exclusion and staging failure
do not consume a sequence. Before activation it reserves the replay mark with
the full signed-manifest SHA-256 pin, then writes `<cacheRoot>/install-intent.json`. A
refused reservation creates no intent. A crash between reservation and intent
consumes that sequence without authorizing recovery; a fresh signed release is
required. An
interrupted install resumes that exact local intent; a network response cannot
replace it, and a different manifest at the same sequence or a newer replay
mark/current pointer refuses recovery. Recovery rehashes active content without
restaging over it. Low-level applier callers must hold the same external lease.

Recovery checks the current configured trust policy at the intent's original
verification instant: removing trust refuses recovery; signature expiry alone
does not cancel already-authorized local work. Newly published revocations do
not replace that intent. A malformed intent or changed trust/replay state fails
closed and may require operator inspection before further updates. Protect the
install root from untrusted local writers.

### The release record

One record, `state/release-policy.json` (`ReleasePolicy` in
[`Puck.Confinement`](../Puck.Confinement/README.md#the-release-record)), holds
everything launch, rollback, admission and retention depend on: the current
version, the startup attempts counted per payload, the proofs of health, the
rejected payloads, the accepted revocations and the state generation floor.
Each version's `versions/<version>/install-record.json` carries its install
sequence, runtime, state generation, payload identity
(`ReleasePayloadIdentity`) and file pins. Every change to either goes through
`InstallTransaction`, which exists only while `update.lock` is held:
`UpdateService` begins one for an apply and one around a check's verification,
and hands it to `IReleaseVerifier` and `IUpdateApplier`, which never begin
their own. Verification merges an authentic manifest's revocations into the
record before judging the manifest, so a refused or read-only check still
records them; `ReleasePolicy.Refusal` then refuses a revoked version, a rejected
payload or a state generation below the floor. The applier flushes the staged
files, writes the install record durably and commits the record naming the
version current; nothing about the outgoing version changes. The selected
executable records its health after startup (`HealthGateFile`, through
`InstallTransaction.RecordHealthy`), only when its registration holds
`versions/<current>`: that proves its exact install and clears its payload's
attempts in one commit. The design and its failure schedules are in
[the updater policy plan](../../docs/plans/updater-policy.md).

### Pruning past versions

After recording health, `InstallPruner` deletes past versions. It keeps the
record's current version, its rollback target, the version a pending
`install-intent.json` names unless the record refuses it, every version a live
process holds, every version whose install order cannot be read, and the
`UpdateOptions.KeepVersions` others ranked proven first and then most recently
installed; a version the record refuses (revoked, rejected or below the floor)
takes none of those places. It then deletes the content-addressed objects
nothing kept references (a pending intent and a local release source's channel
manifests count as references). Install order is the `sequence` the applier
writes into `versions/<version>/install-record.json`, never version precedence;
a payload file or directory of that name is refused by manifest validation. A
version without a valid record (all its fields present, a positive sequence and
a non-empty inventory of well-formed pins) is kept and keeps the content pins of
the regular files it holds.

A pass first reads and validates everything it depends on and deletes nothing
while it does. The release record must validate and name a current version
that is an existing version directory. The pending intent's manifest must validate. Every
version's record is either valid or treated as absent and replaced by hashing
its files. Every store's layout and channel manifests must be free of links and
must validate. Any failure there skips the whole pass with its reason. `Prune`
never throws: whatever stops it is reported as the skip reason, so it cannot end
the MCP adapter's or a World's start or health check.

Every step acts through `Puck.Confinement`'s `ConfinedDirectory` handles opened
from the install root: nothing is looked up or deleted by a path checked
earlier, and no link is followed. These are the invariants a review checks:

- **Never outside the root.** Every lookup is relative to an open directory
  handle with no-follow semantics (`openat`/`O_NOFOLLOW` on Linux, `NtCreateFile`
  with a `RootDirectory` and `FILE_OPEN_REPARSE_POINT` on Windows). A link
  anywhere refuses that entry; renaming or replacing a directory the pass holds
  either fails (Windows: held without `FILE_SHARE_DELETE`) or leaves the pass
  acting on the held object (Linux).
- **Never a live version.** A version is removable only through
  `TryOpenChildForRemoval`, which fails while any process holds it: an
  exclusive `flock` against holders' shared locks on Linux, a delete-access open
  against holders' sharing mode on Windows. Holders are the stub (from before it
  starts the child until the child exits) and every process itself:
  `Puck.Confinement`'s `InstallRegistration.TryRegisterProcess` is the first
  statement of every entry point an installed payload carries (`Puck.Cli`,
  `Puck.Cli.Worlds`, `Puck.World`, `Puck.World.Silo` and `Puck.Actors`), and an
  entry point that cannot register refuses to start with the reason.
  Membership is decided by the process's own directory (`AppContext.BaseDirectory`)
  whatever arguments it was given. A directory directly under one named
  `versions` (or `trash`) belongs to the root above it, even before that root
  writes `current`. The image confirmed is the entry assembly's file, or the
  executable for a single-file app. A process under `versions/` that cannot
  confirm its image or root refuses to start. The kernel releases a hold when its
  process exits.
- **Registration is serialized with removal.** Registration waits for
  `update.lock` without a deadline; the pass holds `publication.lock` then
  `update.lock` throughout. A registering process then confirms by identity that
  `versions/<name>` is the directory its entry assembly was loaded from (the
  mapped file's inode and current path on Linux; on Windows the mapped file's
  open-time name, whose directory the runtime's open file pins), or fails its
  launch with the reason. Whether a process runs from the install at all is the
  identity of its image's parent directory, never a path spelling. Under the
  same lease the release record must admit the version
  (`InstallTransaction.AdmitProcess`): a version without a valid install
  record, revoked, rejected or below the state generation floor refuses to
  start, whoever started it, and a start raises the floor to its generation.
- **Crash-safe.** A version leaves `versions/` whole by one move into `trash/`
  (by handle on Windows, `renameat2` relative to held descriptors on Linux),
  then is deleted there through the same handle. An interrupted pass leaves
  every kept version whole, and the next pass empties what it left in `trash/`.
  Removal, link scanning and hashing walk a tree without recursion.
- **Fail closed.** The validation phase above skips the whole pass on anything
  it cannot read or validate, before any deletion: a pointer that is not the
  plain name of an existing version, a link among the versions or on a store's
  layout or channels, an install record that cannot be read, or a manifest that
  fails validation. A busy lease, a linked `trash/` or an unsupported platform
  (anything but Windows and Linux x64) skips it too. A version whose rank cannot
  be read is kept, and a running version never takes one of `KeepVersions`'
  places.

These defend against cooperating processes and crashes, not against the install
root's own user ([decision](../../docs/decisions/runtime-and-delivery.md#releases)). A
process with that user's write access can already replace the binaries the
launcher runs. So interleavings that need a hostile process renaming entries
inside the root during a pass are out of the model and get no defence beyond
link refusal. Two such cases are known:
- renaming a held version aside and moving another directory to its name
  between the pass's check and its Linux `renameat2`;
- renaming a `trash/` entry aside and moving a live version to its name between
  the listing and the cleanup, which then deletes by that name.

The stager replaces an earlier partial staging of the same version with the same
removal (`InstallPruner.RemoveVersion`), so it too is refused while a process
runs from it.
Document-authored `update.cacheRoot` is a relative subdirectory of the host's
updates directory; only trusted local update configuration may choose an
absolute install root.

## Verification

The [launcher tests](../../tests/Puck.Launcher.Tests/README.md) exercise host
loop controls and the release/update path. A library test does not replace
running the selected platform presentation path on its intended hardware.

## Documentation

📚 [Engine architecture](../../docs/architecture/README.md) · 🛠️ [Development](../../docs/development/README.md)
