# CLAUDE.md

## Start here

- [docs/capability-catalog.md](docs/capability-catalog.md) — what Puck can do,
  with verification status per capability.
- [docs/project-map.md](docs/project-map.md) — what each `Puck.*` project is
  for, layering, and dependency rules.
- [docs/agent-guide.md](docs/agent-guide.md) — how to verify changes
  (`Puck.Post` and the emulator batteries), env vars, hardware gotchas,
  conventions. **Read this before touching GPU or emulator code.**
- [docs/README.md](docs/README.md) — the docs index (living vs. historical).

## The old monolith is gone

The original `src/Puck` / `src/Puck.Avatars` projects were inspiration-only
and have been **deleted from the tree** (git history has them). All
functionality lives in the split `Puck.*` projects. Never reintroduce
references to the old paths.

## The demo is the overworld

Puck.Demo's default run IS the demo — the OVERWORLD: a player runs around a room
with three console stands (the showcase cartridge on the dmg/cgb/agb costumes
of the one GamingBrick machine); booting a stand (interact = North button)
lights its pane and the screen layout eases fullscreen → side-by-side →
big-top/two-bottom → 2×2 quad. "MiniAction" is a defunct name — that game was
the foundation and is now simply the demo (`src/Puck.Demo/Overworld/`). The demo
is a game prototype, not a test suite: it was **purified 2026-07-03** — every
legacy `--world*`/`--validate*` mode was deleted (that coverage is Post
stages now), and the only gate the demo keeps is its own `--validate-overworld`.
Console mode is MULTIPLAYER (2026-07-04): extra pads join as world players
with their own binding bars, and proximity takeover lets any player claim a
booted brick off the shared timeline (host-side input routing, never sim
state). `--rom <path>` boots straight into a cartridge as a fullscreen world
document, with data-driven fourth-wall `exit` conditions on gaming-brick
sources; battery saves persist to `<romPath>.sav` (SRAM + RTC footer,
deterministic resume). A gaming-brick source also takes an optional 128-bit
`victory` condition (`BrickVictoryCondition`): the host reads the top 16 bytes
of the cartridge's highest SRAM address (bank `0x0F` of a 128 KiB MBC5 cart —
read bank-independently via `ICartridge.ReadExternalRam`) after each stepped
frame. `solo` wins a cabinet alone when its region reaches the gate constant;
`meta` wins the room when the XOR of a group's cabinets reaches the target
(shares authored so no cabinet wins alone). Both break the fourth wall like
`exit`. Proven by the `victory-gate` (gate math: order-independent bit-fill,
subset-proof XOR) and `victory-region` (highest-address / bank-independence)
Post stages; see `docs/examples/overworld-victory.json`.

The prototype-arc workstreams (diegetic brick screens, the cartridge shelf's
pick-up → carry → insert → boot loop, animated brick controls) have LANDED,
as has the LIVE device swap (dmg↔cgb↔agb with NO boot and no lost progress).
Diegetic screens now sit behind a CRT glass face and spill their framebuffer's
average color as light into a dimmed room (the overworld mood), via a per-frame
screen-light seam (the first producer of a general lights-as-data buffer).
With a per-ROM recipe it is a GENUINE hardware swap: the emulated model is now
runtime-mutable (an `IModeSwitchable`/`ModelState` seam in Puck.HumbleGamingBrick,
snapshotted), `Machine.SwitchModel` re-gates the color path + repages the
switchable RAM / drops double speed on a demote, and POKES the game's cached
hardware-detection flag (the "boot shim" — a differential-boot finder locates
it; Pokémon Gold's `hCGB` = HRAM 0xFFE8) so the running game re-detects and
re-renders in the new mode's own authored art (proven: CGB→DMG→CGB, exactly
four DMG shades ↔ color, reversible, survives Fork). No recipe → a
presentation-only desaturation. Next: the PC camera as a GB-camera peripheral,
which rides a future cartridge-sensor seam (a `peripheral` field on a library
entry), NOT another viewport pane — its ROM side is deliberately not designed
yet. Plan of record:
[docs/overworld-demo-plan.md](docs/overworld-demo-plan.md). The machine-fleet
performance work (many machines at once — lockstep, independent, live
promote/demote, diegetic world-lens machines) starts from
[docs/machine-fleet-briefing.md](docs/machine-fleet-briefing.md).

## Artifacts are evidence, not law

The user's current instruction outranks every written artifact — docs,
skills, gates, comments, precedent. If an artifact argues against a demanded
change, the artifact is stale: update it in the same change; never resist or
water down the change to protect it. Validation asserts observable behavior
(POST stages: pixels, hashes, parity, determinism), never internal structure
— no mocks, no call-shape pins. Full doctrine:
[docs/agent-guide.md](docs/agent-guide.md#anti-calcification-doctrine).

## Verification

The default gate for engine changes is the POST battery:
`dotnet run --project src/Puck.Post -c Release` (32 stages, tiered A–D;
`--tier`/`--stage` filter, `--fuzz-seed` overrides the fuzz stage's seeds).
Don't add new `--validate-*` flags to Puck.Demo — add a Post stage instead.
Emulator changes use the mirrored batteries under `experimental/*.Post`.

## Controller input

The controller-input subsystem (Switch Pro / Xbox Series / PlayStation 5 DualSense, all flowing through
`Puck.Commands`) lives in `src/Puck.Input`. See [`src/Puck.Input/README.md`](src/Puck.Input/README.md)
for its architecture, the cross-family feature matrix, hardware-verified status, deferred work,
and debugging notes — it is the handoff doc for picking the work up on another machine.
