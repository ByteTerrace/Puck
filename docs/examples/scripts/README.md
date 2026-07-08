# Console-driving scripts — the agent-facing control surface

`Puck.Demo` is one cohesive session driven entirely by **console verbs**, and
the same command registry that backs the on-screen backtick console is fed by
process **stdin** (the launcher's `StandardInputReaderService` → `TextCommandSource`).
Every verb's result echoes to **stdout**. So an agent — or a deterministic test —
drives the whole engine by piping a list of verbs in and reading the echoed
results back, no GUI or controller required. This is the demo's verification
story (the demo is greenfield; verify by RUNNING it, now reproducibly), per the
[unification contract](../../overworld-demo-plan.md#the-unification-contract).

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

Scripted determinism hinges on three verbs (added with the unification arc):

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
| [`editor-gate.console`](editor-gate.console) | Rung 3: the meta-victory group that unlocks the editor. | `--run docs/examples/overworld-editor-gate.json` |
| [`condition-recursion.console`](condition-recursion.console) | The recursion: re-forge a cabinet's win/reveal conditions live. | `--run docs/examples/overworld-editor-gate.json` |
| [**`grand-tour.console`**](grand-tour.console) | **The whole arc in one narrated session**: cart-cycle the games, preview the recursion, complete the arcade to open the editor (a dark→lit capture montage), then forge a tune + a scene cart. | `--run docs/examples/overworld-editor-gate.json --exit-after-seconds 30` |
| [`robustness.console`](robustness.console) | **The UNHAPPY paths**: malformed document loads, unwritable capture paths, and out-of-range/garbled verb args — asserts the control plane echoes a clean error and SURVIVES every one (the crash class the pump used to let through). The final `state` printing is the pass condition. | `--run docs/examples/overworld-editor-gate.json` |
