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
they sampled and take every step it covers. The offscreen loop's time is its
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
records each iteration in a `HostFrameTimeline`, which the windowed-only
`host.frames [frames] [budget-ms] [worst]` verb reads: the interval's average,
median, 99th percentile and maximum over the last iterations, the count over a
budget (one 60 Hz period by default), the most work any iteration did outside
the pacing wait, how late the timer woke (zero while the swap chain paces), the
garbage collections and their pause time, and the slowest iterations split into
input, commands, simulate (with its fixed steps), begin, produce, present and
wait.
`host.frames reset` starts a fresh window. It is the worst-frame reading a
frame-time bar is judged by; `world.fps` reads a two-second window and counts
the frame in progress at the moment it runs, so a script reads it once before
it measures, or the first call's compilation lands in the reading.

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

`UpdateService` holds the install root's persistent `update.lock` lease across
verification, staging and activation. Rollout exclusion and staging failure
do not consume a sequence. Before activation it writes `install-intent.json`
and reserves the replay mark with the full signed-manifest SHA-256 pin. An
interrupted install resumes that exact local intent; a network response cannot
replace it, and a different manifest at the same sequence or a newer replay
mark/current pointer refuses recovery. Recovery rehashes active content without
restaging over it. Low-level applier callers must hold the same external lease.

Recovery checks the current configured trust policy at the intent's original
verification instant: removing trust refuses recovery; signature expiry alone
does not cancel already-authorized local work. Newly published revocations do
not replace that intent. A malformed intent or changed trust/replay state fails
closed and may require operator inspection before further updates. Protect the
install root from untrusted local writers. Flushed atomic file replacement and
process-interruption recovery do not establish power-loss durability across
filesystems.

## Verification

The [launcher tests](../../tests/Puck.Launcher.Tests/README.md) exercise host
loop controls and the release/update path. A library test does not replace
running the selected platform presentation path on its intended hardware.

## Documentation

📚 [Engine architecture](../../docs/architecture/README.md) · 🛠️ [Development](../../docs/development/README.md)
