# Greenfield standard — the open register

**The codebase does not meet the greenfield standard: version drift and read-side tolerance
are clean, every subcommand passes, and the newest features now have checks that were PROVED
to fail when broken — but a zero-consumer 71 704-LOC `src/Puck.Demo` fork still ships and
most of the verification surface still cannot fail when the product breaks.**

Two structural items stand between the codebase and the standard:

1. **The `src/Puck.Demo` fork and its `Puck.Scene` node** (C-F1, C-F3). Arc 12's scheduled
   work, blocked behind Arcs 8–11. Not residue — do not delete ahead of those arcs.
2. **A verification surface that mostly does not discriminate.** Twelve of eighteen proof
   suites never settle `wire.errors`; whole landed verb families (`replay.*`, `screen.*`,
   `world.view.*`, the whole-row `.set` upserts, 36 of 55 `editor.sculpt.*`) still have zero
   scripted call sites.

**Do first:** V-4 (the twelve suites that never settle `wire.errors`), then V-1 and V-6 (the
`replay.*` and `screen.*` families) — the largest remaining blind spots.

This document lists what is open, plus a short closed-this-session ledger carrying the
falsification numbers. Every entry is anchored to `file:line` and is re-checkable by running,
not by reading.

---

## Standing rules this register depends on

- **R18 — goldens are not a landing gate.** Byte-identity and round-trip are diagnostics, not
  acceptance criteria. Never block work on re-goldening.
- **Supergreen — zero external consumers.** No compat aliases, no migration shims, no
  read-side tolerance for retired shapes. Migrate data once, delete the old path.
- **`Puck.World` is greenfield; Post gates the engine.** Verify World changes by RUNNING
  `Puck.World`. Never add a `--validate-*` flag or a Post stage for a game feature.
- **Every schema token under `src` is `.v1`** across twelve document families, validated by
  strict ordinal equality with loud rejection and no fallthrough. The single `puck.run.v0`
  (`Puck.Post/Stages/RunDocumentStage.cs:113`) is a negative fixture asserting rejection.
  Zero `[Obsolete]`, zero migrate/upgrade functions.
- **One verb-dispatch mechanism.** `CommandRegistry.Submit` has a single dispatch,
  `fast.WireArgsHandler!(…)`, and fast-path eligibility keys on `WireArgsHandler`. All 186
  argument-bearing `Puck.World` verbs register through `WithWireArgs`; the 24 surviving
  `CommandDefinition.Verb` registrations take no arguments. Quiet-mode suppression is the
  explicit opt-in `AcknowledgementOnly` (22 sites).

## Gotchas that cost a session

- **The build trap.** `dotnet build` fails while the app is running (locked exe). A
  subsequent `--no-build` run then silently executes the **stale** binary and every
  assertion is meaningless. Always confirm `Build succeeded` before running a suite, and
  never trust a `--no-build` run behind a failed build.
- **Verification lies silently.** A check that asserts on a narration string passes even when
  the feature it narrates is broken — the echo is emitted on the success path *and* is
  matched by the refusal path. Compressed to the rules that follow from it:
  - A check must FAIL when its feature is broken. If you have not seen it fail, you have not
    written a check.
  - Echo-checking is not checking. Read the mutated state back (pose, counter, committed row)
    and assert on the delta.
  - A predicate that is computed and discarded is not an assertion — e.g. `PollWhereUntil`
    returns the last pose on timeout and never null, so a null-check over it always passes.
  - Before blaming product code for a suite failure, build a control from the accused
    commit's **parent** and run the same suite. The dominant failure mode in this repo is a
    verification script asserting against a world that no longer boots that way (V-20, now
    closed, was a textbook instance).
- **Worktrees spawn from a stale base.** Verify the checked-out commit and reset before
  working; on a shared tree always `git commit -- <explicit pathspecs>`.

## The falsification standard

Five checks were proved to discriminate by breaking their feature in source, rebuilding to a
confirmed `Build succeeded`, re-running, and restoring: the engage proximity gate widened, the
`SetEngaged(false)` call dropped from `WorldEngagement.Disengage`, `m_drag.Move` handed a
zeroed delta, `WorldConsoleWaitGate.IsHolding` forced to `false`, and
`WorldJsonPayload.IsParseFailure` narrowed back to `JsonException`. The first three each
produced **exactly one** failure, in its own check, and nothing else — while the surrounding
echo and journal-counter checks all still PASSED. That last part is the point: the surrounding
checks were the ones that looked like coverage. The two `wire` breaks are recorded with their
numbers in the closed ledger below; the wait-gate break is the sharpest statement of the rule,
because the `world.wait` **echo** check passed under it while the behavioural walk went red.

The reference implementations for new checks are `CollisionProof` (every claim on a pose or a
counter, per-round `SettleWireErrors` with exact expected counts, terminal `expected: 0`) and
`GrantsProof` (pose deltas against `MovedEpsilon`/`FrozenEpsilon` with an ambient-drift null
control first).

**Two refusal mechanisms exist and must not be conflated.** `ProofApp.Guarded`
(`proof.cs:319-329`) counts `[wire.reject:` process-wide — **unknown verbs and parse errors
only**. A handler that parses fine and returns `IsError` (bad value, denied grant, validator
refusal) is caught **only** by the `wire.errors` verb. A suite that relies on `Guarded` alone
is blind to every refusal the product actually issues.

---

# The register

## Failing now

Nothing. All nineteen driving subcommands pass on a confirmed Release build.

## Closed this session

| # | Commit | What closed it, and the number that makes it discriminating |
|---|---|---|
| V-20 | `d634eec8` | `worlddoc` section (b) forced the in-code baked definition with `--world <nonexistent path>` and rode the silent fallback; the loud boot failure correctly ended that, so run B captured 0/8. The baked definition is a legitimate boot mode and now has an EXPLICIT request — the `--world baked` sentinel — instead of being reachable only by accident, and all three origins name themselves in the one boot line (`(shipped default)` / `(--world)` / `baked default (in-code; requested by --world baked)`). Reaching its comparison then exposed the *other* wrong thing: it demanded byte-identity between two DIFFERENT documents (the shipped world declares solidity facets and a collision response table the baked one does not, so 4 of 8 entities legitimately land up to 0.26 u apart). It now runs the baked document twice and asserts THOSE compare byte-identical — determinism is per-document — and reports the cross-document delta. **`worlddoc` PASS: (a) 5/5 worlds fixed points; (b) coverage 8/8 · 8/8 · 8/8, baked rerun byte-identical, loud line in the baked runs only.** |
| V-16 | `77ea9e76` | `world.wait` is covered behaviourally in the new `wire` suite, not by its echo: the same drive-then-read burst twice with a wait must land bit-for-bit on the same pose a real distance out, then a third time without it. **Held 9.150 u and 9.150 u; unheld 0.000 u.** *Falsified:* `WorldConsoleWaitGate.IsHolding` → `false` collapsed the held burst to **0.190 u**, and it stopped repeating (**0.190 vs 0.150**) — `waited-read-is-repeatable` and `the-wait-buys-a-real-span` went red while the release-tick **echo** check still PASSED. |
| V-17 | `77ea9e76` | `EditorCamerasProof` reads the `world.cameras` table instead of narration: the boot rows must parse (anchor keyword, a rig token from the closed `chase\|firstPerson\|orbit\|lookAt\|dolly` vocabulary, dimensions), a live add must APPEAR in the table, and `world.undo` must take it back out — asserted on both backends. A third section boots **all five** shipped worlds and reads each table against `world.status`'s declared count: **2 declared / 2 listed on every one, every row parsing** — the instrument OQ-12 never had. |
| V-18 | `77ea9e76` | Four union-taking verb families (`world.look.set`, `world.screen.set`, `world.camera.set`, `world.scene.row.set`) × four discriminator malformations (absent, unknown, duplicate, misplaced), at both nesting depths since a scene row's union IS its payload. **16 lines, each named and refused, `wire.errors` settling at exactly 16, then cleared**, with host liveness asserted at the wire afterwards. *Falsified:* narrowing `IsParseFailure` back to `JsonException` **killed the host on the first payload** (`Unhandled exception. System.NotSupportedException … must specify a type discriminator`), taking **12** checks red including `host-process-alive` and the fault sweep. |
| V-19 | `77ea9e76` | `wire` section (c) boots four times and asserts the exit code AND the naming line each time — either alone passes on the wrong thing. A bogus `--world` and a bogus `--recording` each **exit 1** naming themselves; a real `--world` path and `--world baked` each **exit 0** with their origin line. |

## Structural — Arc 12's scheduled work, blocked behind Arcs 8–11

| # | Size | Where | What is wrong / what closes it |
|---|---|---|---|
| C-F1 | L | all of `src/Puck.Demo` (255 files, 71 704 LOC), referenced only by `Puck.slnx:13` | A zero-consumer fork. The `Puck.Post`, `Puck.Scene`, and `Puck.World` csproj hits for "Puck.Demo" are comments, not `ProjectReference`s. It does **not** reference `Puck.Forge`; it carries drifted copies of 34 files now living in Forge/Authoring/World (`Framework/SoundTables.cs` differs by 16 lines, `Sm83Emitter.cs` by 7; `AudioDocument.cs` exists as two distinct CLR types both declaring `puck.audio.v1`). Zero byte-identical duplicate pairs — every duplicate has drifted. Arc 12 owns the deletion and the Arc 8/9/10 hub still consumes the pinned survivors. **Do not delete ahead of those arcs.** |
| C-F3 | M | `Puck.Scene/NodeDocument.cs:14,55` | `OverworldNode` is a dead engine-tier run-document node whose `World` handle is documented as a `puck.world.v1` (Demo's retired format; the live game is `puck.world.def.v1`). Consumers are `Puck.Scene` self-references plus a Post negative fixture. Closes with Arc 12's `Puck.Scene` Demo-only surface deliverable. |

## Verification surface

| # | Size | Where | What is wrong / what closes it |
|---|---|---|---|
| V-1 | L | `WorldReplayCommandModule.cs:23,28,33,38,43,48`, wired `Program.cs:446` | All six `replay.*` verbs have **zero** call sites. The only evidence they work is hand-pasted transcripts in `docs/demo-to-world-port-plan.md:8479-8485`. Determinism is a core product claim; a regression in tape capture, boot-image rehydration, or tail-hash comparison ships silently. **Close:** a new `replay` subcommand with a doctored-tape negative control. |
| V-6 | L | `ScreenCommandModule.cs:51,56,61,66,76,81,86,91,96` | Nine `screen.*` verbs with zero call sites, including `screen.options` (the live dmg↔cgb↔agb device swap — a headline capability) and the whole `screen.link`/`unlink`/`links` cable group. `ScreensProof` covers only `insert`/`eject`/`peek`/`state`. |
| V-4 | M | `proof.cs` — twelve of eighteen driving suites | Only `Expo`/`Grants`/`EditorCameras`/`Population`/`Collision`/`Wire` settle `wire.errors` — 6 of 18. The house rule is stated verbatim inside the file and applied to 6 of 18. `ProofApp.Guarded` does not cover this class. Worst exposure: `ScreensProof`, 29 fire-and-forget `Send` calls, no settle. Mechanical but 12 sites; the suites with deliberate refusals (`Placements`, `Audio`) need the per-round settle-and-clear discipline `CollisionProof` models, not a bare terminal zero. |
| V-5 | M | `proof.cs` (`EditorCamerasProof`) | **Partly closed by V-17.** The DOCUMENT side is now read off the `world.cameras` table, so an add/undo is measured on state rather than narration. What remains is the RENDER side: the reconcile claims (`"pose updated live"`, `"showing camera 'birdseye'"`, `"recreated live (WxH)"`) and `world.view-refresh`'s count are still narration strings, and nothing measures a pixel or a produced-frame counter — in the one suite whose subject is *only* observable in pixels. |
| V-7 | M | `proof.cs:7368` (`flyDiff > 8.0`), `proof.cs:7489` (`seamDiff > 8.0`) | `EditorModeProof` uses two absolute pixel floors with **no control pair** and never zeroes the census, over a band the autonomous crowd occupies and moves through between shots. Its two relative checks are anchored to `flyDiff`, so noise inflating it loosens them too. Every other pixel suite bounds a control pair and requires `> 4×` noise. |
| V-8 | M | `EditorSculptStyleCommandModule.cs:64,84,89,94,99,104,109`, `EditorSculptShapeCommandModule.cs:96,116`, `EditorSculptRigCommandModule.cs:65,105`, `EditorSculptCommandModule.cs:91,106` | **55** `editor.sculpt.*` verbs are registered and **19** are driven; two whole modules are unexercised — even though `SculptProof` is otherwise the strongest suite in the file. |
| V-9 | M | `WorldViewCommandModule.cs:24,39,52,63,76,89` | The entire `world.view.*` family (six verbs) is unexercised; `proof.cs` touches only `world.view-refresh`. |
| V-10 | M | `WorldMutationCommandModule.cs:29,46,119,125,153,182,188,209,221,289`; `WorldHostCommandModule.cs:33`; `WorldCollisionCommandModule.cs:39` | The whole-row `.set` upsert verbs and their parser are never driven — only the field-level twins are. `world.load`, the read counterpart of the heavily-proven `world.save`, is completely untested. |
| V-11 | M | `proof.cs:6500` and the `MiniEbml` walker | `RecordProof` cannot distinguish a stalled encoder from a real capture: it asserts `bytes.Length > 8000` plus EBML docType and track *presence*. `MiniEbml` parses no clusters, no timecodes, no frame count, so a capture that writes headers and delivers one frame passes everything. It fails only when the container is malformed — not when the feature breaks. |
| V-14 | M | `PlayerCommandModule.cs:187,192,258,271,272,273,274,275` | Eight `player.*` verbs with zero call sites, including the four directional twins, `player.sticks` (the analog lanes the tape/corpus system rests on), and `player.assign`/`player.profile` (the seat↔profile binding `BindingsProof` and `StorageProof` both depend on indirectly and never call). |
| V-13 | S | `proof.cs:221` header claim; `EditorSpeakerCommandModule.cs:33,46,54` | `editor.speaker.move`/`channel`/`radius` are never sent, yet the header advertises "the `editor.speaker.*` numeric twins". Adding the three `Mutate` lines reshuffles the dirty counts of every subsequent assertion in the block. Either land the verbs and re-baseline the block, or shrink the header claim. |

## Validators that accept what they cannot render

| # | Size | Where | What is wrong / what closes it |
|---|---|---|---|
| G-M1 | M | `Puck.Authoring/CreationCanonicalizer.cs:93-103,185,186,214-215,231-234,284` | The class doc claims "strict schema, no silent relabel"; `Normalize` clamps Material/Bend/Dilate/Onion/Smooth/Twist/Level/Fov/Pitch and `_ =>`-relabels unknown `mode`/`locomotion` tokens. `Validate` collects only non-finite and duplicate-id violations, so `material: 99`, `mode: "etch"`, `locomotion: "burrow"` all land as accepted documents with silently different meaning — and the world validator then re-verifies the hash against `Canonicalize`, blessing the mangled value as canonical. Zero `IsError`, nothing for `wire.errors` to count. **Fix:** move the range/token checks into `Validate`; delete the clamps and catch-alls. |
| G-M2 | M | `Puck.World/Client/WorldAudioDirector.cs:235,243,249` | Derived-plan emitter/patch/source overflow is a `Console.Error` warn plus "the overflow renders silent". `WorldDefinitionValidator` has no ceiling on speakers/patches/emissions (its only section ceiling is `MaxCameras = 64`). A world that cannot play correctly is accepted **with a zero rejection count**. **Fix:** three collected validator errors, derivable statically. |
| G-M3 | M | `Puck.World/Client/WorldPlacementStamper.cs:244` | Clamps to `paletteIds.Length - 1`, but `RegisterPalette:153-175` sizes that `max(min(palette.Count, 16), 1)`. A 3-entry palette silently renders `material: 9` as slot 2, with no diagnostic. S-sized in isolation, but the correct bound belongs in `CreationCanonicalizer.Validate` — land it **with** G-M1, not before. |

## Recorded, blocked on a question

| # | Size | Where | What is wrong / what closes it |
|---|---|---|---|
| G-S1 | S | `Puck.World/WorldSpeaker.cs:185` (`WorldAudioCue.GainThousandths`) | Two encodings of one value, same shape as the `InnerRadius` fix — but `null` here coalesces to **1000** (unity) while `default(int)` is `0` (silent). Removing the nullable depends on whether the STJ source-gen honours a constructor parameter's default value for an absent member; if it does not, a hand-authored cue that omits `gainThousandths` silently goes mute. **Settle the STJ question by running before touching it.** |
| C-F7 | S | `Puck.World/Server/WorldServer.cs:314` | `join.ProtocolVersion != WorldProtocol.Version` cannot be taken over today's in-process loopback link — both `Join` construction sites pass the literal constant. **Deliberately not removed:** the loopback stands in for a real wire, the field is a protocol-frame member rather than a document-schema field, and it must exist the day the link crosses a process boundary. Recorded, not recommended. |
