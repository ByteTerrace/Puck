# Greenfield refactor — the honest verdict

**Branch** `claude/puck-realtime-world-editing-4fd13f`. **Closeout commit** `c754e53`
(the sweep below), on top of `a092d48` (expo regenerated through the author path).
Three independent read-only audits were run against `a092d48` — compat/version
residue, golden-shaped design and defensive dead weight, verification integrity.
This document records what the refactor removed, what verification now exists
that did not before, and — at length, because it is the part worth reading —
**what remains**.

Line references into `src/Puck.World/scripts/proof.cs` are as of `c754e53` unless
noted; the sweep shifted the file by +14 lines above the audio suite.

---

## What the refactor removed

**Version drift and migration apparatus.** `4ac8494` retired goldens as a landing
gate (ruling R18). `31c78a0` collapsed the bindings profile from `puck.bindings.v8`
to `v1`; `e093e68` reduced `SnapshotRecording` to one magic and one version with an
`InvalidDataException` on either mismatch; `cde8982` deleted the `WorldProfileStore`
migration ladder. Verified at `a092d48` by sweep, not by assertion: **every** schema
token under `src` is `.v1` across twelve document families; the single `puck.run.v0`
(`Puck.Post/Stages/RunDocumentStage.cs:113`) is a negative fixture asserting
rejection. `rg '\.v([2-9]|\d\d)\b'` over `src` returns zero. `[Obsolete]` count
across `src` + `tools`: zero. Zero migrate/upgrade functions.

**Read-side tolerance.** All eight schema fields validate by strict ordinal equality
with loud rejection and no fallthrough — `WorldDefinitionLoader.cs:69`,
`WorldDefinitionValidator.cs:65`, `WorldPlayerDocumentValidator.cs:41`,
`RunDocumentValidator.cs:35`, `RecordingDocumentValidator.cs:41`,
`BindingProfile.cs:24`, `BenchReportComparer.cs:159`, and the shared
`DocumentCanonicalizer.SchemaViolationMessage`.

**Golden-shaped document design.** `e6ce86a` made all thirty `WorldDefinition`
sections present rather than absent-to-protect-bytes; `67be335` made an incomplete
document reject loudly; `6983803` and `c6562dd` finished the units and retired the
last golden. `c754e53` finished the residue: `WorldSpeaker.Bed.InnerRadius` was
nullable-coalescing-to-`0f` (two encodings of one value) and is now a plain `float`,
so the validator's inner-band rule applies unconditionally instead of only when the
member happened to be written.

**Dead versioning apparatus.** `c754e53` deleted `WorldQueryArtifact.CurrentSchema`
— a `puck.worldquery.v1` token whose only occurrence repo-wide was its own
declaration, on a type with no serializer.

**Stale labels.** `c754e53` corrected three comments that actively misdirect:
`CommandRegistry.cs` called the trailing-args dispatch path "Legacy" and "(Demo)"
when it is how 152 of the 184 live World verbs dispatch; `InputSignal.CaptureTick`
called its `0` sentinel "legacy" when it is a live optional value the router
attributes to the current tick. It also swept the six surviving "the ouroboros
**gate**" claims (`WorldDefinitionSerialization.cs:176,219`,
`WorldSessionCapture.cs:22`, `docs/capability-catalog.md`,
`docs/capability-register.md`, `.claude/skills/verifying-puck-changes/SKILL.md`) to
"round-trip" with the not-an-acceptance-criterion qualifier R18 requires, and
restated the five nullability docs that justified an optional facet with "every
existing row in every existing world" / "deserializes identically".

---

## What verification now exists that did not before

**The strictness surface is real, and one claim about it was falsified by running
rather than reading.** The golden/defensive audit hypothesised that
`WorldDefinition.Schema { get; init; } = SchemaVersion` (`WorldDefinition.cs:1637`)
would let a schemaless blob past the equality check. Stripping the `schema` key from
`default.world.json` and running the built exe printed
`[world] definition: baked default (…: schema '(absent)' is not puck.world.def.v1)`.
The `?? "(absent)"` coalesces are live, not dead weight.

**Refusals are counted, synchronously and deferred.** `079075a` taught `wire.errors`
to count deferred refusals through the edit-echo tap; `18d128a`/`3a7aa0d`/`da464d7`
made a refused line detectable as a refusal rather than merely differently worded.
Two mechanisms now exist and must not be conflated: `ProofApp.Guarded`
(`proof.cs:319-329`) counts `[wire.reject:` process-wide — **unknown verbs and parse
errors only** — while a handler that parses fine and returns `IsError` (bad value,
denied grant, validator refusal) is caught only by the `wire.errors` verb. Five
suites settle it today (see the remainder section).

**New behavioural scenarios, with the numbers that make them discriminate.**
`3b87cc6` added the crowd-shape checks, `a5d53c0` the solidity/kit-facet/host checks,
`0e335b2` the checks that would notice. `c754e53` repaired three that would not have:

| Check | Before | After — measured |
|---|---|---|
| `engage-out-of-range-errors` (`proof.cs:3557`) | needled `"engage"`, which the *successful* echo `[player.engage: p1 engaged screen 0]` also satisfies | needles the refusal's own text; run prints `p1 is 32.5u from screen 0 — within 2.5u to engage (player.warp closer)` |
| `avatar-moves-after-disengage` (`proof.cs:3595`) | `poseBefore is not null && poseAfter is not null`, but `PollWhereUntil` returns the last pose on timeout, never null — the `> 0.3` displacement predicate was computed and discarded | the displacement **is** the assertion; run prints `z -0.82 -> -2.27` |
| `speaker-drag-moves-the-row` (new, audio suite) | the round asserted only echoes and journal counters; `editor.release` commits the pending row whether or not the delta landed, so a no-op drag handler passed all three checks | reads the committed centre back through `editor.select`; run prints `x -1.50 -> -0.50 (drag dx 1)` |

Each fails if its feature is reverted. The reference implementations for this
standard are `CollisionProof` (every claim on a pose or a counter, per-round
`SettleWireErrors` with exact expected counts, terminal `expected: 0`) and
`GrantsProof` (pose deltas against `MovedEpsilon`/`FrozenEpsilon` with an
ambient-drift null first).

**Verification of this change.** Full Release build clean (0 warnings, 0 errors).
`Puck.World` boots the default world. `proof.cs screens` PASS, `proof.cs audio`
PASS, `proof.cs worlddoc` section (a) save-idempotence PASS on all three checked-in
worlds.

---

## What remains

Nothing here is fixed. Every item is a live finding, sized as the audit sized it.

### Deferred because it is an L-sized redesign

| # | Size | Where | Why deferred |
|---|---|---|---|
| C-F2 | L | `Puck.Commands/CommandDefinition.cs:127` (`WithTrailingArgs`) vs `:171` (`WithWireArgs`); dispatch fork `CommandRegistry.cs:716-741` | A half-migrated mechanism: **152** trailing-args registrations across 24 World modules vs **32** wire-native (~17 % migrated). `WithWireArgs` is a strict functional superset that additionally avoids a `string[]` allocation per dispatch on the scripted stdin path. Converting 152 sites and deleting the fork is mechanical but L. Actionable now — touches nothing the port plan owns. |
| V-1 | L | `Puck.World/WorldReplayCommandModule.cs:23,28,33,38,43,48`, wired `Program.cs:446` | All six `replay.*` verbs have **zero call sites** in `proof.cs`. The only evidence they work is hand-pasted transcripts in `docs/demo-to-world-port-plan.md:8479-8485`. Two of the three most recent pre-closeout commits are replay commits and determinism is a core product claim; a regression in tape capture, boot-image rehydration, or tail-hash comparison ships silently. Needs a new `replay` subcommand with a doctored-tape negative control. |
| V-6 | L | `Puck.World/ScreenCommandModule.cs:51,56,61,66,76,81,86,91,96` | Nine `screen.*` verbs with zero call sites, including `screen.options` (the live dmg↔cgb↔agb device swap — a headline capability) and the whole `screen.link`/`unlink`/`links` cable group. `ScreensProof` covers only `insert`/`eject`/`peek`/`state`. |

### Deferred because it is Arc 12's scheduled work, blocked behind Arcs 8–11

| # | Size | Where | Note |
|---|---|---|---|
| C-F1 | L | all of `src/Puck.Demo` (255 files, 71 704 LOC), referenced only by `Puck.slnx:13` | A zero-consumer fork. It does **not** reference `Puck.Forge`; it carries its own drifted copies of 34 files now living in Forge/Authoring/World (`Framework/SoundTables.cs` differs by 16 lines, `Sm83Emitter.cs` by 7; `AudioDocument.cs` exists as two distinct CLR types both declaring `puck.audio.v1`). Zero byte-identical duplicate pairs — every duplicate has drifted. Arc 12 owns the deletion and the Arc 8/9/10 hub still consumes the pinned survivors. **Do not delete ahead of those arcs.** |
| C-F3 | M | `Puck.Scene/NodeDocument.cs:14,55` | `OverworldNode` is a dead engine-tier run-document node whose `World` handle is documented as a `puck.world.v1` (Demo's retired format; the live game is `puck.world.def.v1`). Consumers are `Puck.Scene` self-references plus a Post negative fixture. Arc 12's deliverable list names the `Puck.Scene` Demo-only surface explicitly. |

### Deferred because it needs design judgement (M), not a mechanical edit

| # | Size | Where | Finding |
|---|---|---|---|
| G-M1 | M | `Puck.Authoring/CreationCanonicalizer.cs:93-103,185,186,214-215,229-233,284` | The class doc claims "strict schema, no silent relabel"; `Normalize` clamps Material/Bend/Dilate/Onion/Smooth/Twist/Level/Fov/Pitch and `_ =>`-relabels unknown `mode`/`locomotion` tokens. `Validate` collects only non-finite and duplicate-id violations, so `material: 99`, `mode: "etch"`, `locomotion: "burrow"` all land as accepted documents with silently different meaning — and the world validator then re-verifies the hash against `Canonicalize`, blessing the mangled value as canonical. Zero `IsError`, nothing for `wire.errors` to count. Fix: move the range/token checks into `Validate`; delete the clamps and catch-alls. |
| G-M2 | M | `Puck.World/Client/WorldAudioDirector.cs:234-250` | Derived-plan emitter/patch/source overflow is a `Console.Error` warn plus "the overflow renders silent". `WorldDefinitionValidator` has no ceiling on speakers/patches/emissions (its only section ceiling is `MaxCameras = 64`). A world that cannot play correctly is accepted **with a zero rejection count** — the textbook warn-instead-of-reject. Fix: three collected validator errors, derivable statically. |
| G-M3 | M | `Puck.World/Client/WorldPlacementStamper.cs:244` | Clamps to `paletteIds.Length - 1`, but `RegisterPalette:153-175` sizes that `max(min(palette.Count, 16), 1)`. A 3-entry palette silently renders `material: 9` as slot 2, with no diagnostic. The audit sizes the fix S in isolation, but the correct bound lives in `CreationCanonicalizer.Validate` — it is entangled with G-M1 and must land with it, not before. |
| V-4 | M | `proof.cs` — twelve of seventeen driving suites | Only `Expo`/`Grants`/`EditorCameras`/`Population`/`Collision` settle `wire.errors`. The house rule is stated verbatim inside the file at `proof.cs:8267-8269` and applied to 5 of 17. `ProofApp.Guarded` does not cover this class. Worst exposure: `ScreensProof` with 29 fire-and-forget `Send` calls and no settle. Mechanical but 12 sites, and the suites with deliberate refusals (`Placements`, `Audio`) need the per-round settle-and-clear discipline `CollisionProof` models, not a bare terminal zero. |
| V-5 | M | `proof.cs:8145-8219` (`EditorCamerasProof`) | 100 % echo-only: every claim is a narration string (`"pose updated live"`, `"showing camera 'birdseye'"`, `"2 camera view(s) registered"`). Nothing measures a pixel or a produced-frame counter — in the one suite whose subject is *only* observable in pixels. Structurally identical to the `world.collision.probe` failure mode this session already caught. |
| V-7 | M | `proof.cs:7369` (`flyDiff > 8.0`), `proof.cs:7482` (`seamDiff > 8.0`) | `EditorModeProof` uses two absolute pixel floors with **no control pair** and never zeroes the census, over a band the autonomous crowd occupies and moves through between shots. Its two relative checks are anchored to `flyDiff`, so noise inflating it loosens them too. Every other pixel suite bounds a control pair and requires `> 4× noise`. |
| V-8 | M | `EditorSculptStyleCommandModule.cs:64,84,89,94,99,104,109`, `EditorSculptShapeCommandModule.cs:96,116`, `EditorSculptRigCommandModule.cs:65,105`, `EditorSculptCommandModule.cs:91,106` | Thirteen `editor.sculpt.*` verbs with zero call sites — two whole modules unexercised — even though `SculptProof` is otherwise the strongest suite in the file. |
| V-9 | M | `Puck.World/WorldViewCommandModule.cs:24,39,52,63,76,89` | The entire `world.view.*` family (six verbs) is unexercised; `proof.cs` touches only `world.view-refresh`. |
| V-10 | M | `Puck.World/WorldMutationCommandModule.cs:29,46,119,125,153,182,188,209,221,289`; `WorldHostCommandModule.cs:33`; `WorldCollisionCommandModule.cs:39` | The whole-row `.set` upsert verbs and their parser are never driven — `proof.cs` exercises only the field-level twins. `world.load`, the read counterpart of the heavily-proven `world.save`, is completely untested. |
| V-11 | M | `proof.cs:6501` and the `MiniEbml` walker | `RecordProof` cannot distinguish a stalled encoder from a real capture: it asserts `bytes.Length > 8000` plus EBML docType and track *presence*. `MiniEbml` parses no clusters, no timecodes, no frame count, so a capture that writes headers and delivers one frame passes everything. The check fails only when the container is malformed — not when the feature breaks. |
| V-14 | M | `Puck.World/PlayerCommandModule.cs:187,192,258,271,272,273,274,275` | Eight `player.*` verbs with zero call sites, including the four directional twins and `player.sticks` (the analog lanes the tape/corpus system rests on) and `player.assign`/`player.profile` (the seat↔profile binding `BindingsProof` and `StorageProof` both depend on indirectly and never call). |

### Deferred S-sized items, with the specific reason each was not mechanical

| # | Size | Where | Why not applied |
|---|---|---|---|
| G-S1 | S | `Puck.World/WorldSpeaker.cs:185` (`WorldAudioCue.GainThousandths`) | Same two-encodings shape as the `InnerRadius` fix that *did* land — but `null` here coalesces to **1000** (unity), and `default(int)` is `0` (silent). Removing the nullable therefore depends on whether the STJ source-gen honours a constructor parameter's default value for an absent member; if it does not, a hand-authored cue that omits `gainThousandths` silently goes mute. `InnerRadius` carried no such trap (`0f` *is* the coalesce target), which is why it landed and this did not. Settle the STJ question by running before touching it. |
| V-13 | S | `proof.cs:221` header claim; `EditorSpeakerCommandModule.cs:33,46,54` | `editor.speaker.move`/`channel`/`radius` are never sent, yet the header oversells "the `editor.speaker.*` numeric twins". Adding the three `Mutate` lines reshuffles the dirty counts of every subsequent assertion in the block — not a mechanical edit, and the file's own doctrine against overselling means the header claim should shrink if the verbs do not land. |
| V-15 | — | — | **Applied** in `c754e53`: `screens` and `sculpt` now have header entries. |
| C-F7 | S | `Puck.World/Server/WorldServer.cs:314` | `join.ProtocolVersion != WorldProtocol.Version` cannot be taken over today's in-process loopback link — both `Join` construction sites pass the literal constant — so by the strict reading it is not a check. **Deliberately not removed**: the loopback is a stand-in for a real wire, the field is a protocol-frame member rather than a document-schema field, and it would have to be re-added the day the link crosses a process boundary. Recorded, not recommended. |

### Known-failing, pre-existing

`proof.cs worlddoc` section (b) baked-default parity fails, and did so identically at
`a092d48` with the closeout changes stashed. It is shipped-default vs in-code-baked-default
divergence, not writer drift. Section (a) save-idempotence passes on all three worlds.

---

## Verdict

**The codebase does not yet meet the greenfield standard: document versioning and
read-side tolerance are genuinely clean, but three structural items stand between it
and the standard — the zero-consumer `src/Puck.Demo` fork and its `Puck.Scene` node
(Arc 12's scheduled work, blocked behind Arcs 8–11), the 152-of-184 half-migrated
`WithTrailingArgs`/`WithWireArgs` verb mechanism, and a verification surface where
whole landed verb families (`replay.*`, `screen.*`, `world.view.*`, the sculpt style
and rig modules) have zero scripted call sites and twelve of seventeen suites never
settle `wire.errors`.**
