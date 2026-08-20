# Manual pointer-gesture harnesses

These scripts are **hand-run SendInput evidence harnesses, NOT batteries and
NOT gates**. Nothing under `docs/verification/` runs them automatically, no
build step depends on them, and no CI-equivalent sweep invokes them. They
exist because `Puck.World`'s pointer/cursor/drag-drop behavior is provable
only by injecting real OS input (pointer/cursor latch and camera-orbit focus
loss), and that proof is worth being able to
re-run by hand — the alternative is losing the evidence entirely once the
session that wrote it ends.

## Why not a committed battery

A committed battery under `docs/verification/<name>/run.ps1` is expected to
run unattended and reliably. `SendInput` cannot offer that on this
repository's machines: it is a **global** OS call — synthetic input lands on
whatever window currently has OS focus, not necessarily the one the script
launched — so these harnesses are flaky-red the moment another windowed
session, a foreign `Puck.World` process, or an unrelated foreground window
shares the desktop. That was judged not battery-worthy. It is still real
evidence, worth keeping in a form a human (or an agent with the desktop to
itself) can re-run on demand.

## Running one

Each script:

- **Requires exclusive desktop foreground for its whole run.** Close or
  park other windows first. Every script gates on no foreign `Puck.World`
  process being alive before it injects anything, and asserts
  `GetForegroundWindow()` equals its own window before every injection —
  but it cannot protect itself from a human moving focus away mid-run, or
  from a *second* manual harness running at the same time.
- **Injects global mouse/keyboard input.** Don't touch the mouse or
  keyboard while one is running.
- **Cannot run concurrently** with another instance of itself, another
  manual harness here, or any other windowed `Puck.World` process on the
  same machine.
- Builds `Puck.World` itself if needed, then drives it over stdin plus
  `SendInput`, polling the transcript for each expected line rather than
  guessing wall-clock timing (tick pacing is not reliable under concurrent
  load on a shared machine — a fixed sleep schedule races the very state
  it is trying to observe).
- Writes its scratch state (process transcripts, saved-world snapshots,
  driver logs) under `$env:TEMP`, never into the repository tree.

Run one directly from a PowerShell prompt at the repository root:

```
pwsh -File docs/verification/manual/pointer-cross-slot-latch.ps1
```

Each script's own header comment names exactly what it proves. Re-run the
relevant one whenever the pointer/cursor/camera-orbit gesture grammar changes
enough that its claim might no longer hold — there is no automated trigger
to remind you.

## What's here

| Script | Proves |
|---|---|
| `pointer-cross-slot-latch.ps1` — **verified from this location** | A held pointer button cannot survive a keyboard-seat reassignment as a phantom drag (`WorldPointer.ReleaseAllButtons`), `SystemReleaseCount` advances only on a force-release and never on a genuine one, and a wheel burst with no registered consumer drains cleanly. |
| `camera-orbit-focus-loss.ps1` (+ `camera-orbit-focus-loss-sink.ps1`, its inert Alt-away target) — **verified from this location** | An armed camera-orbit drag stops responding to motion the instant OS focus is lost mid-drag, and resumes normally once re-armed and refocused — the orbit path is provably alive, not just silent by coincidence. |
