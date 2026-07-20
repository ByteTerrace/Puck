# Console-driving scripts — the agent-facing control surface

`Puck.Demo` is one cohesive session driven entirely by **console verbs**, and
the same command registry that backs the on-screen backtick console is fed by
process **stdin** (the launcher's `StandardInputReaderService` → `TextCommandSource`).
Every verb's result echoes to **stdout**. So an agent — or a deterministic test —
drives the whole engine by piping a list of verbs in and reading the echoed
results back, no GUI or controller required. This is the demo's verification
story (the demo is greenfield; verify by RUNNING it, now reproducibly), per the
[experience contract](../../overworld-demo-plan.md#experience-contract).

The scripts here are runnable, self-documenting examples of that.

## Running one

A script is a plain list of console verbs, one per line. Blank lines and lines
beginning with `#` are comments (skipped), so a script explains itself.

```bash
# bash / Git Bash — redirect the file into stdin:
dotnet run --project src/Puck.Demo -c Release -- --exit-after-seconds 10 < docs/examples/scripts/smoke.console
```

```powershell
# PowerShell — pipe the file's lines in:
Get-Content docs/examples/scripts/smoke.console | dotnet run --project src/Puck.Demo -c Release -- --exit-after-seconds 10
```

`--exit-after-seconds <n>` bounds the run; give the script enough seconds for its
`step`/`settle` waits to complete. Read the bracketed `[verb: …]` lines on stdout
to assert what happened.

## Discovering verbs

Pipe `help` (a built-in registry verb) to list every available command — the
overworld control verbs (`reveal`, `boot`, `cart`, `player.add`, `link`, `state`,
`capture`, `step`, `settle`), the authoring verbs (`creator`/`creator.*`,
`world`/`world.*`, `tracker`/`tracker.*`, `companion.*`), and the console/debug
verbs.

## The observability + sequencing verbs

Scripted determinism hinges on three verbs:

- **`state`** — echoes a one-line summary (`hash`, layout `mode`, `booted` mask,
  `players`, `frame`/`tick`) for assertions.
- **`step <n>`** — defers the *rest of the script* by `n` produced frames, so a
  `boot 0` / `step 30` / `capture` sequence lands its shot on the frame it asked
  for rather than all running the instant they arrive.
- **`settle`** — defers until the screen-layout / reveal transitions quiesce
  (no active easing), so a capture lands on a still frame.

## Driving the self-editing arcade

Two more verbs drive the reveal ladder and its recursion end to end:

- **`win <i>`** — force console `i`'s game to its win (writes its authored victory
  bytes into the 128-bit win region). Win a whole meta group and the room's REAL
  XOR opens the editor — the one verb that drives "complete X games → the editor
  reveals" without gameplay input.
- **`condition.show/set <i> …`** — read and re-forge a cabinet's win/reveal
  conditions live (the recursion: edit the very rules that gated the editor;
  it warns but never soft-locks).

## The scripts

| Script | What it drives | Run with |
|---|---|---|
| [`smoke.console`](smoke.console) | The basic control loop: `help`, `state`, boot a cabinet, `step`, `capture`. | *(the default run)* |
| [`reveal-ladder.console`](reveal-ladder.console) | Rungs 1→2 of the reveal ladder: open immersed, then break the fourth wall and reveal INTO the data-file world (Puckton). | `--run docs/examples/overworld-town.json` (materialize the town first: `--forge-town`) |
| [`author-forge.console`](author-forge.console) | In-game authoring over stdin: enter the creator, sculpt SDF primitives, forge a cartridge. | *(the default run)* |
| [`creator-modifiers.console`](creator-modifiers.console) | Every per-shape modifier verb (`creator.mirror`/`.twist`/`.onion` plus the new `.dilate`/`.bend`), on both the ghost and a selected shape, then a save/reload round-trip. | *(the default run)* |
| [`editor-gate.console`](editor-gate.console) | Rung 3: the meta-victory group that unlocks the editor. | `--run docs/examples/overworld-editor-gate.json` |
| [`condition-recursion.console`](condition-recursion.console) | The recursion: re-forge a cabinet's win/reveal conditions live. | `--run docs/examples/overworld-editor-gate.json` |
| [**`grand-tour.console`**](grand-tour.console) | **The whole experience in one narrated session**: cart-cycle the games, preview the recursion, complete the arcade to open the editor (a dark→lit capture montage), then forge a tune + a scene cart. | `--run docs/examples/overworld-editor-gate.json --exit-after-seconds 30` |
| [`robustness.console`](robustness.console) | **The unhappy paths**: malformed document loads, unwritable capture paths, and out-of-range or malformed verb arguments. The script asserts that each operation reports a clean error and the control plane remains responsive; the final `state` output is the pass condition. | `--run docs/examples/overworld-editor-gate.json` |
| [`sdf-debug.console`](sdf-debug.console) | Tours the fullscreen SDF debugger: pick shapes, stack modifier ops, the termination/slice views, the scoped-vs-flat field accumulator contrast. | *(the default run; `gpu.timing on` for GPU ms)* |
| [`notch-repro.console`](notch-repro.console) | Reproduces the SDF ground-notch defect with grazing debug scenes and the overworld far-ground case where 16 px tile quantization exposes it. Uses the `sdf.cam` pose verb and captures depth and termination views. | *(the default run)* |
| [`world-camera.console`](world-camera.console) | Exercises `world.camera add`/`list`/`del` and `world.wire` with the `guest:N`/`camera:N` wiring grammar. | *(the default run)* |
| [`museum-tour.console`](museum-tour.console) | The Replay Museum + the Droste door: wires two exhibits' real `NestedWorldView` content, walks the player up to each with `player.move` for a real establishing/close shot, and captures both (`museum-tour-hall.png`/`-door.png`). | *(the default run)* |
| [**`planetoid-proof.console`**](planetoid-proof.console) | `planet.spawn` seats a walker on a small planetoid (down = `-grad(SDF)` from the live `SdfFieldEvaluator`); `planet.walk` circumnavigates it in quarter-turn stages, `planet.list` reports Up through all four quadrants, and the script captures each stage. | *(the default run)* |
| [`carve-bake-proof.console`](carve-bake-proof.console) | The carve-bake pipeline (`docs/carve-bake-plan.md`): spawns a settled 20-carve cluster, flips `sdf.carve-bake on`, forces an immediate settle with `sdf.bake now`, verifies the bin swaps to a baked `SampledRegion` brick with `sdf.bake status`, then proves the invalidation (an edit falls back to analytic) and the disable round-trip (back to the identical analytic field). The throughput gate itself (`sdf.carves` >= 60 fps) is `bench.sweep sdf.carve-bake=off,on`, a separate headless proof. | *(the default run)* |

## Smoke scripts

A starter set that checks the engine-library surfaces behaviorally rather than by hash: each script verifies the
expected stdout responses, with no captures required.

| Script | What it drives | Run with |
|---|---|---|
| [`smoke-sdf-debug.console`](smoke-sdf-debug.console) | Enters the SDF debugger, exercises `sdf.shape`/`sdf.op`/`sdf.shape2`/`sdf.blend`, tours the gallery (list + a jump straight to `drift-monolith`, exhibit 8), touches `sdf.bench`, then exits the mode cleanly. | *(the default run)* |
| [`smoke-overworld-control.console`](smoke-overworld-control.console) | The scripted-control basics: `state`, `terminal`/`ui.diegetic` toggles, `cart`, `boot`, `step`/`settle`, and a `press` tape — on console 1, since a connected pad auto-takes-over console 0 on boot and `press` refuses an owned cabinet. | *(the default run)* |
| [`smoke-help-surface.console`](smoke-help-surface.console) | Proves the verb-registry surface itself: the built-in `help` listing, plus one demonstration that `help <verb>` is NOT supported (a clean parse error, not a crash). | *(the default run)* |

**Run and check**: pipe a script into the demo exactly like any other script here (see [Running one](#running-one)
above), give `--exit-after-seconds` enough headroom for its `step`/`settle` waits, and watch stdout for the bracketed
`[verb: …]` echoes — one per line the script drives. A clean run prints an echo for every verb (including a `state`
as the final line) and the process exits 0; a non-zero exit, an unhandled exception, or a bare `Unrecognized command`
/ `unavailable` line where a verb was expected to succeed means the surface it protects broke.
