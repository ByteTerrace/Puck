# Manual pointer gesture checks

These PowerShell harnesses exercise Puck.World's pointer, cursor, drag-and-drop,
and camera-focus behavior by injecting operating-system input through `SendInput`.
They provide repeatable manual evidence for interactions that require the actual
window and input path. No build or CI job runs them automatically.

## Desktop requirements

`SendInput` delivers input to whichever window has operating-system focus.
Another application, another World process, or a second harness can therefore
receive input intended for the test window. The scripts check foreground
ownership before each injection, but focus can still change between checks.
This dependence on exclusive desktop access prevents reliable unattended runs.

## Run a check

Each script:

- **Requires exclusive desktop foreground for its whole run.** Close or
  park other windows first. Every script gates on no foreign `Puck.World`
  process being alive before it injects anything, and asserts
  `GetForegroundWindow()` equals its own window before every injection—
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
  load on a shared machine—a fixed sleep schedule races the very state
  it is trying to observe).
- Writes its scratch state (process transcripts, saved-world snapshots,
  driver logs) under `$env:TEMP`, never into the repository tree.

Run one directly from a PowerShell prompt at the repository root:

```powershell
pwsh -File docs/verification/manual/pointer-cross-slot-latch.ps1
```

Each script's own header comment describes the behavior it checks. Re-run the
relevant one whenever the pointer/cursor/camera-orbit gesture grammar changes
enough that its claim might no longer hold—there is no automated trigger
to remind you.

## Available checks

| Script | Checks |
|---|---|
| `pointer-cross-slot-latch.ps1`—**verified from this location** | A held pointer button cannot survive a keyboard-seat reassignment as a phantom drag (`WorldPointer.ReleaseAllButtons`), `SystemReleaseCount` advances only on a force-release and never on a genuine one, and a wheel burst with no registered consumer drains cleanly. |
| `camera-orbit-focus-loss.ps1` (+ `camera-orbit-focus-loss-sink.ps1`, its inert Alt-away target)—**verified from this location** | An armed camera-orbit drag stops responding to motion the instant OS focus is lost mid-drag, and resumes normally once re-armed and refocused—the orbit path is provably alive, not just silent by coincidence. |
