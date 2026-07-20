# Puck

Puck is a **notation** — a closed, versioned vocabulary in which a world, the machines inside it, its cameras, its bodies, their appearances, the cartridges they run, and the authority to change any of it are all rows in a document, moved by verbs — together with an interpreter that runs that document deterministically on two GPU backends, and a game whose job is to prove the notation expressive enough to be worth having.

That framing is the whole point. Puck is not a renderer with a scene format bolted on. The document is the primary artifact; the C# is an implementation of it. When you want the world to do something new, you do not reach for a new type — you reach for a new row, or a new verb over rows that already exist.

## The layers

**GPU backends.** Two, at parity: Vulkan (SPIR-V) and Direct3D 12 (DXIL). One HLSL source tree per kernel is compiled to both, so the same engine renders the same scene on either API. Backend selection is a boot-time categorical choice, not a live lever — swapping compute APIs means rebuilding the render graph, and Puck.World registers exactly one backend descriptor per process. A live hot-switch mechanism exists at the engine tier and is proven end-to-end (a child process presents on Vulkan, switches to Direct3D 12 mid-run, re-targets its live content, and is verified by readback), but the game does not expose it. That gap is deliberate on the engine side and simply undecided on the product side.

Parity between the backends is deliberately *relaxed* by default. Floating-point codegen differs between DXC's two outputs in ways that are benign and well-understood; the default envelope shrugs at those, and an opt-in strict posture applies calibrated per-family thresholds backed by measured evidence. Parity numbers are drift tripwires, not acceptance criteria. A backend disagreement that clears the envelope is not a success to celebrate; a threshold that is met is not a proof of correctness.

**The SDF VM.** Everything visible is a signed-distance program: 22 live opcodes covering point transforms, field operations, domain folds, and a scoped accumulator; 17 shape primitives; 10 blend operations including the chamfer family. A program is words and instances, not a mesh graph. Rendering is compute-shader sphere tracing — mask, beam, cull-args, views, composite — with hardware ray-query present only as a parity probe against the primary march, never as the shipped path.

The VM is deliberately incurious about what it draws. A diegetic screen samples an opaque image handle; the VM has no idea whether that handle came from an emulator, a camera feed, a window capture, or another world entirely. That incuriosity is what makes hosting work.

**The world document.** `puck.world.def.v1` is what the running game boots from: 23 sections, one closed mutation vocabulary of whole-row upserts and whole-section replacements addressed by stable id, and one thick validator that runs over the *entire* composed candidate document — never a partial section check — before anything swaps in. Applied mutations append to a journal; the journal *is* the undo engine, replaying base-plus-history through the identical apply path rather than restoring stored snapshots. Saving compacts the journal against a new baseline, folding live session state back into its own section homes.

An older engine-tier document, `puck.run.v1`, still exists and is still schema-gated, but nothing renders from it today. It describes an engine composition graph independent of any product. Whether it stays as that or gets retired is genuinely undecided.

**The game.** `Puck.World` is the live composition root and the only thing you run. It is server-authoritative — the server owns the definition, the entity table, and the journal; the client interpolates snapshots and submits intents, and never simulates. Four local seats share a screen through data-driven layouts that fall back to a built-in ladder (fullscreen → side-by-side → big-top → quad) when nothing is authored. Up to 128 bodies exist in the entity table. Editing, sculpting, inhabitation, audio, cabinets, and the console all live here.

## The discipline: refuse to grow a noun

The recurring engineering move in Puck is declining to add a type. There is no `WorldCabinet`, no third motion model, no role enum. Novelty goes into data.

The clearest instance: **there is no NPC and no player character.** Arc 7 removed the discriminator that classified agency. What exists is a body, and a `Drive` grant over that body which is either claimed or unclaimed. A seat claims it, or a console script does, or a WASM addon does, or a deterministic wander producer fills the vacuum. The authorization table became the ontology — four principal kinds (seat, console, addon, peer) and four capabilities (Drive, Control, Mutate, Edit) over a subject taxonomy, arbitrating everything, with local play seeded permissive so nothing feels gated until someone chooses to narrow trust.

The same move recurs everywhere. A camera is an *anchor* (where it rides) and a *rig* (how it frames) — two orthogonal axes, five authored rig kinds, no combinatorial camera classes. A screen is a slot with a producer; five producer shapes fit it, and adding a sixth engine means implementing a machine contract, not touching the VM. A creature that inhabits a placement is the same body a player would drive, wearing a creation stamp.

Every enum carries an admission rule. Adding an opcode has a ritual that differs depending on whether it is an isometry. Adding an addon pad id is an ABI-version event. The vocabulary is closed on purpose, and opening it is a deliberate act rather than a convenience.

## Determinism, precisely

Determinism pins the *mapping*, not the values. Same document plus same input yields bit-identical simulation state on every run, machine, and backend, at a fixed code version. It is emphatically not output stability across versions: a deliberate correction to math is *expected* to change hashes, and the gates are self-referential so they pin no historical value. Simulation state carries no wall clock, no RNG, no float — fixed-point throughout, input arriving as per-tick command snapshots.

Presentation floats freely. Render scale, upscaling sharpness, interpolation, pacing, and artistic choices sit outside the contract. Audio mixes in fixed point end to end and hashes reproducibly; the WASM addon substrate pins its runtime to an exact version because fuel is charged at basic-block granularity and a silent bump would move the exhaustion tick.

## Honesty as the tiebreaker

When principles collide in this repository, honesty wins. Puck does not present a capability it does not have, a number it did not measure, or a state it is not in.

This shows up as engineering, not as sentiment. Authoring acts are checked against a render envelope probed at boot; a placement that would exceed it is rejected loudly with the ceiling named, never silently clamped and never crashed. The GPU budget gate carries a comment saying its ceiling is a catastrophic-regression tripwire and *not* a calibrated budget. The audio device stack plays silent and retries rather than pretending. A recording verb's doc comment says a synchronous readback happens per frame and that no cost figure is claimed without measurement. When an engine cannot do something — live cable linking between two running cabinets, memory peek on the GBA core — it fails with the actual reason rather than a generic error.

There is a real gap between what is designed and what is landed, and Puck states it rather than blurring it. Carve-bake is engine-complete and gated but the game never routes through it. Time travel and rumble exist at the core and have no console verb. Rotation and align-to-shape snapping are finished math with no wiring. Guided binding sessions are proven deterministic and have no host. None of these are secrets; they are named where a reader will hit them. The per-capability status lives in the capability register, and that separation is intentional — this document explains what Puck is, not what shipped this week.

## What Puck is not

**Not a general-purpose game engine, and not competing to be one.** There is no asset import pipeline, no material graph, no mesh rendering. The primitive is a distance field, and content is a program over it.

**Not backwards-compatible.** Nothing outside this repository consumes Puck — no packages, no downstream repos, no API users. Renaming, reshaping, and deleting are free, done in one change across every internal caller. There are no compat aliases, no deprecation ceremonies, no migration shims, and no read-side tolerance for retired data shapes. Data migrates once and the old path is deleted.

**Not networked.** The client/server split is real and the transport interface is genuinely transport-agnostic — but the only transport that exists is in-process loopback, with no serialization, no deltas, and no authentication. The grant table authorizes identities that are already established; it has no opinion on how an identity would be established over a wire. Multiplayer today means four people on a couch. Whether a socket transport is *wanted* is an open product question, not a technical one — nothing in the roadmap commits to it.

**Not configured by environment variables.** Durable configuration is a document field; live operations are console verbs. The console is the control plane, driven both by an on-screen panel and by process stdin with results on stdout, so an agent or a scripted proof can drive the entire engine over a pipe.

**Not a menu of flags.** The unification contract holds that every capability is reachable from inside one running session — a diegetic act, a pad chord, or a console verb — with no restart. Headless proofs are reflections of in-session capabilities, never separate products. Where this contract is currently violated (a built capability with no in-session surface), that is a debt, and it is named as one.

**Not gated by tests for game work.** The engine contract — cross-backend rendering, the SDF ISA, the document schema, deterministic numerics — is gated by POST batteries. The game is verified by running it. Game features do not get validation flags or POST stages; POST is the gate for the shared engine, not a canary to architect around.

**Decided against:** cross-backend document-level composition (a run cannot assemble a live Vulkan world with a live Direct3D world; the validator rejects it at preflight), pixel-perfect parity as a default posture, a cloud storage tier (the seam exists and reports itself honestly as unwired), and per-copy audio emission on repeated placements in v1.

## Where this is going

*Everything in this section is intent, not present state.*

**The demonstration.** The destination is one unbroken session in which a person talks about Puck, plays it, edits it, generates content inside it, and captures the video of itself — and walks away with a replay tape that reproduces the run somewhere else. Every piece of that has a seam today and several have working implementations; none of it is stitched into a single continuous take yet.

**The creative loop.** The player's journey is meant to be a reveal ladder: boot immersed inside an intro cartridge that mirrors the arcade room the data file describes, win it, have the fourth wall break and load you into that same room standing at the machines with their screens already glowing, and later have a diegetic moment hand you the editor — which stays always-on for developers and agents. From inside that room you will sculpt a creature, animate it, bake it into a cartridge, insert the cartridge into a cabinet, and watch someone play it. The sculpt workbench, the timeline, the IK rig, and the cartridge forge all exist; the forge is not yet hosted in the running game, and the reveal ladder has no narrative gate built.

**The recursion.** A world will contain a screen that shows another world — genuinely simulated and rendered, not a camera trick. The engine already has the piece that does this and it works; the world document has no field and no verb that reaches it. The honest analogue at the game tier is a second world document with its own server, which is a capability rather than a camera kind, and the questions that decides — whether a nested world gets a full server or a reduced one, how its tick relates to the host's, what it costs to draw — are explicitly open and unsurveyed. Puck already ships one weaker form of this: a screen inside the world showing a live capture of the very window it lives in, kept from exploding by a structural self-reference rule rather than by careful authoring.

**The horizon beyond that** is unfixed on purpose. Puck is a deliberately dumb terminal *beneath* engines. Where it ends up — a studio, a console, a substrate someone else builds on — is left open, and the notation is designed so that answer can arrive later without a rewrite.