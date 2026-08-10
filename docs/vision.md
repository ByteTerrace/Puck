# Puck

Puck is a **notation** — a closed, versioned vocabulary in which a world, the machines inside it, its cameras, its bodies, their appearances, the cartridges they run, and the authority to change any of it are all rows in a document, moved by verbs — together with an interpreter that runs that document deterministically on two GPU backends, and a game whose job is to prove the notation expressive enough to be worth having.

That framing is the whole point. Puck is not a renderer with a scene format bolted on. The document is the primary artifact; the C# is an implementation of it. When you want the world to do something new, you do not reach for a new type — you reach for a new row, or a new verb over rows that already exist.

**This document says what Puck IS. It deliberately carries no status.** For what is built and what is next, read [the campaign](campaign.md), which names the check behind every claim it makes.

## The layers

**GPU backends.** Two, at parity: Vulkan (SPIR-V) and Direct3D 12 (DXIL). One HLSL source tree per kernel compiles to both, so the same engine renders the same scene on either API. Backend selection is a boot-time categorical choice, not a live lever — swapping compute APIs means rebuilding the render graph.

Parity between the backends is deliberately *relaxed* by default. Floating-point codegen differs between DXC's two outputs in ways that are benign and well understood; the default envelope shrugs at those, and an opt-in strict posture applies calibrated per-family thresholds backed by measured evidence. Parity numbers are drift tripwires, not acceptance criteria. A backend disagreement that clears the envelope is not a success to celebrate; a threshold that is met is not a proof of correctness.

**The SDF VM.** Everything visible is a signed-distance program — words and instances, never a mesh graph. Rendering is compute-shader sphere tracing: mask, beam, cull-args, views, composite. Hardware ray-query exists only as a parity probe against the primary march, never as the shipped path.

The VM is deliberately incurious about what it draws. A diegetic screen samples an opaque image handle; the VM has no idea whether that handle came from an emulator, a camera feed, a window capture, or another world entirely. That incuriosity is what makes hosting work.

**The world document.** `puck.world.def.v1` is what the running game boots from: one closed mutation vocabulary of whole-row upserts and whole-section replacements addressed by stable id, and one thick validator that runs over the *entire* composed candidate document — never a partial section check — before anything swaps in. Applied mutations append to a journal; the journal *is* the undo engine, replaying base-plus-history through the identical apply path rather than restoring stored snapshots. Saving compacts the journal against a new baseline, folding live session state back into its own section homes.

**The game.** `Puck.World` is the live composition root and the only thing you run. It is server-authoritative — the server owns the definition, the entity table and the journal; the client interpolates snapshots and submits intents, and never simulates. Local seats share a screen through data-driven layouts. Editing, sculpting, inhabitation, audio, cabinets and the console all live here.

## The discipline: refuse to grow a noun

The recurring engineering move in Puck is declining to add a type. Novelty goes into data.

The clearest instance: **there is no NPC and no player character.** The discriminator that classified agency was removed. What exists is a body, and a `Drive` grant over that body which is either claimed or unclaimed. A seat claims it, or a console script does, or a WASM addon does, or a deterministic wander producer fills the vacuum. The authorization table became the ontology — four principal kinds (seat, console, addon, peer) and five capabilities (Drive, Observe, Control, Mutate, Edit) over a subject taxonomy, arbitrating everything, with local play seeded permissive so nothing feels gated until someone chooses to narrow trust. That count moved once, from four to five, to model reading — never for a feature, which still earns a section or a subject. A sixth, `Present`, was declared and then deleted without ever gating a draw path.

The same idea sharpens once more at the trust boundary: for principals outside it the table is *materialized as handles they hold* rather than rows someone looks up, so an addon cannot name a subject it was not handed. Refusing to grow a noun, applied to who may say one.

The move recurs everywhere. A camera is an *anchor* (where it rides) and a *rig* (how it frames) — two orthogonal axes, no combinatorial camera classes. A screen is a slot with a producer, and adding an engine means implementing a machine contract, not touching the VM. A creature inhabiting a placement is the same body a player would drive, wearing a creation stamp.

Every enum carries an admission rule. Adding an opcode has a ritual that differs depending on whether it is an isometry. Adding an addon pad id is an ABI-version event. The vocabulary is closed on purpose, and opening it is a deliberate act rather than a convenience.

## Determinism, precisely

Determinism pins the *mapping*, not the values. Same document plus same input yields bit-identical simulation state on every run, machine and backend, at a fixed code version. It is emphatically not output stability across versions: a deliberate correction to math is *expected* to change hashes, and the gates are self-referential so they pin no historical value. Simulation state carries no wall clock, no RNG, no float — fixed-point throughout, input arriving as per-tick command snapshots.

Presentation floats freely. Render scale, upscaling sharpness, interpolation, pacing and artistic choices sit outside the contract. Audio mixes in fixed point end to end and hashes reproducibly; the WASM addon substrate pins its runtime to an exact version because fuel is charged at basic-block granularity and a silent bump would move the exhaustion tick.

## Honesty as the tiebreaker

When principles collide in this repository, honesty wins. Puck does not present a capability it does not have, a number it did not measure, or a state it is not in.

This shows up as engineering, not as sentiment. Authoring acts are checked against a render envelope probed at boot; a placement that would exceed it is rejected loudly with the ceiling named, never silently clamped and never crashed. A budget gate whose ceiling is a catastrophic-regression tripwire says so rather than posing as a calibrated budget. The audio device stack plays silent and retries rather than pretending. When an engine cannot do something, it fails with the actual reason rather than a generic error.

There is a real gap between what is designed and what is landed, and Puck names it where a reader will hit it rather than blurring it. **What Puck deliberately does NOT keep is a per-capability status register.** One existed, asserted coverage nothing checked, and was deleted; a document consulted precisely to decide whether something is safe must not claim a verification it cannot back. Ask the code, or run the game.

## What Puck is not

**Not a general-purpose game engine, and not competing to be one.** No asset import pipeline, no material graph, no mesh rendering. The primitive is a distance field, and content is a program over it.

**Not backwards-compatible.** Nothing outside this repository consumes Puck. Renaming, reshaping and deleting are free, done in one change across every internal caller. No compat aliases, no deprecation ceremonies, no migration shims, no read-side tolerance for retired data shapes. Data migrates once and the old path is deleted.

**Not configured by environment variables.** Durable configuration is a document field; live operations are console verbs. The console is the control plane, driven both by an on-screen panel and by process stdin with results on stdout, so an agent or a scripted proof can drive the entire engine over a pipe.

**Not a menu of flags.** Every capability is reachable from inside one running session — a diegetic act, a pad chord, or a console verb — with no restart. Headless proofs are reflections of in-session capabilities, never separate products. Where a built capability has no in-session surface, that is a debt and is named as one.

**Not gated by tests for game work.** The game is verified by running it. Game features do not get validation flags or engine-gate stages.

**Decided against:** cross-backend document-level composition (a run cannot assemble a live Vulkan world with a live Direct3D world; the validator rejects it at preflight), pixel-perfect parity as a default posture, and per-copy audio emission on repeated placements in v1.

## Where this is going

*Everything in this section is intent, not present state. The [campaign](campaign.md) is what is actually sequenced.*

**The demonstration.** The destination is one unbroken session in which a person talks about Puck, plays it, edits it, generates content inside it, and captures the video of itself — and walks away with a replay tape that reproduces the run somewhere else. Every piece of that has a seam today and several have working implementations; none of it is stitched into a single continuous take.

**The creative loop.** From inside the hub you will sculpt a creature, animate it, bake it into a cartridge, and place it in a dungeon. The sculpt workbench, the timeline, the IK rig and the cartridge forge all exist; none is yet hosted in the running game. Boot loads into the hub, and later a diegetic moment is meant to hand you the editor, which stays always-on for developers and agents.

**The recursion.** A world will contain a screen that shows another world — genuinely simulated and rendered, not a camera trick. The engine already has the piece that does this. The questions it decides — whether a nested world gets a full server or a reduced one, how its tick relates to the host's, what it costs to draw — are open and unsurveyed. Puck already ships one weaker form: a screen inside the world showing a live capture of the very window it lives in, kept from exploding by a structural self-reference rule rather than by careful authoring.

**The horizon beyond that** is unfixed on purpose. Puck is a deliberately dumb terminal *beneath* engines. Where it ends up — a studio, a console, a substrate someone else builds on — is left open, and the notation is designed so that answer can arrive later without a rewrite.
