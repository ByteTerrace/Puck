# Puck.World spatial audio arc — the plan (2026-07-18)

Puck.World gains sound: the emitter graph. The world is silent today — every
machine World hosts already implements `IAudioMachine`, but World constructs
them at rate 0 (audio is never synthesized) and no playback device exists
anywhere in the repository (WASAPI code is capture-only). This plan is
grounded in a three-report recon and two-analyst design pass; the owner's
rulings, made in design conversation before this document existed, are its
spine.

The owner rulings (settled; the plan builds on them, never relitigates):

1. **The emitter graph.** The mixer consumes EMITTERS — a pose-or-extent
   plus a signal. Three data shapes produce them: speaker rows, emission
   facets on world rows, and ambient beds. A creek is NOT a speaker —
   transducers play signals; phenomena sound like themselves.
2. **Speakers are the camera family's sibling, never a screen facet** — the
   follow-speaker beside the follow-cam. Routing chiasmus: screens consume
   cameras; speakers consume audio sources.
3. **Feed = (source, channel, gain)** with channel selector
   `left | right | mix`; stereo is two independent rows sharing a source —
   separation produced by geometry, no group/attachment construct.
4. **Music is machine-backed** (tune carts through the emulated APU — the
   proven tracker path); **creature/phenomenon voices are a small native
   deterministic synth** (fixed-point, the `Puck.Maths` idiom).
5. Audio is presentation-only — it never feeds sim state (and the cores
   already guarantee it: audio is snapshot-excluded on both, by design).
6. No external proper nouns in identifiers.

One recon correction the whole plan inherits: **the sim steps at 240 Hz**
(stepTicks 210 of the 50400/s engine clock — `LauncherWindowHostedService`
pins `TargetUpdateRate = 240`); the "32" in circulation was a render-pacer
tier, not the fixed step.

## 1. The decisions

### A1 — The mixer runs at 48000 Hz, fixed-point end to end

Both cores resample internally to ANY host rate via exact-rational
accumulators (subtract-not-reset; zero drift, no float, cycle-paced) — so
32768's "exact divisor" property buys nothing. 48000 is device-native
(shared-mode mix format), gives **exactly 200 frames per 240 Hz sim step**,
and 21/20 engine ticks per audio frame. Machines configure to 48000
directly; the only resampler in the machine path is the core's own.
`LinearResampler` stays a recording-side capture adapter; the render stream
requests `AUTOCONVERTPCM` only as an exotic-endpoint safety net.

The mix path is **integer/fixed-point end to end**: s16 samples × Q16
composite gains → int32 accumulate → a deterministic polynomial soft-clip
(cubic-family, saturating knee) → s16. **Never `MathF.Tanh`** (the recording
graph's soft-clip precedent is libm-variant — it would break the PCM proof
hash). Distance falloff has finite support `(minRadius, maxRadius)` with a
Q16 squared-smoothstep curve — finite support IS the cull: per-block cost is
proportional to what is audible. Pan is equal-power L/R from
listener-relative azimuth via one `FixedQ4816.SinCos` per emitter per
block; coefficients ramp linearly across each block (that ramp, not
snapshot interpolation, is what kills zipper noise). Elevation ignored.
Tier 2 slots without reshaping: HRTF replaces the pan stage with a
fixed-point FIR pair; occlusion arrives as a scalar in the snapshot
(computed sim-side by a world query — the audio thread never queries the
world).

### A2 — The device: one cloned COM thread, event-driven, silent-degrade

No render-client code exists; `WasapiAudioCaptureSource` is the template to
clone exactly — one dedicated MTA COM thread owning
enumerator→Activate→Initialize(Shared, event-driven)→GetService
(`IAudioRenderClient` — two methods added to the existing interop),
`ManualResetEventSlim` init handshake, bounded `Join` on dispose. The pump
fills in **256-frame quanta (5.33 ms)**: snapshot poses → pull each active
source once → advance synth voices → gain/pan per emitter → accumulate →
soft-clip → `GetBuffer`/`ReleaseBuffer`. Latency budget ≈ 25–35 ms
end-to-end — right for ambience; no `IAudioClient3` heroics in v1. Clock
drift between device and pacer absorbs via a per-source ring-occupancy
servo (drop-oldest already bounds the worst case).

Lifecycle: an **`IHostedService`** (the `GamepadHostedService` template) —
deterministic `StopAsync` ordering instead of inheriting the DI
reverse-creation-order teardown surprise (the audio cousin of the bug
`WorldRenderTeardown` exists for). Failure posture: any HRESULT (including
device invalidation mid-stream) → silent state + ~1 s default-endpoint
rebind loop — the "plays silent, never crashes" doctrine, plus rebind,
which the legacy path never had. Non-Windows: a null-returning platform
factory seam; the service simply doesn't start. Not built.

### A3 — Pose handoff: snapshot per produced frame, hold + ramp

A `WorldAudioSnapshot` (listener pose, fixed-capacity emitter table: id,
kind, pose-or-extent, base gain, channel, radii, seeded synth triggers)
publishes once per produced frame from the frame source's produce path —
where render poses are already resolved — over `PublishBuffer<T>` with a
≥4-deep preallocated slab rotation (the audio thread holds a reference for
one 5.33 ms block; the producer needs ≥33 ms to lap — safe by an order of
magnitude). The audio thread holds the latest snapshot and ramps derived
coefficients per block. No two-snapshot interpolation (frame and block
cadence are the same order; it buys nothing audible and costs a frame of
latency). No staleness fade: a stalled producer means a stalled sim (same
thread), machine rings stop filling, sources underrun to silence — the
correct degrade with zero special-case code.

### A4 — Machine audio: always-on at the mixer rate, one honest seam

`IScreenMachineEngine.Create` gains `int audioSampleRate = 0` (the flagged
engine seam — the rate is construction-fixed by design: it sizes the worker
ring and configures the core once). World passes 48000 for every screen
machine, **always-on**: speakers bind after boot and mutations add them at
any time; rebooting a machine when its first speaker appears is worse
product behavior than the measured cost (~192 KB ring + low-single-digit %
CPU per machine, ≤5 machines, drop-oldest bounded). Emulator snapshots are
provably unaffected — the cores' audio carries no state.
`IAudioMachine.ReadSamples` is already any-thread-safe (the worker locks
its own ring): the audio thread drains machines directly; the
`AdvanceMachines` seam comment supplies the *pose*, not the drain site.

### A5 — Listener policy: focus-seat, overridable by data

Blended multi-listener mixes are contradictory noise the moment two seats
face opposite ways; per-seat submixes need per-seat devices; there is one
endpoint. **v1 = focus-seat**: the active view camera's pose listens — the
editor rig when the editor owns the view (wind moves as you fly), seat 1's
chase cam in couch play. The data seam: the audio-defaults section carries
`listener: "focus" | <cameraName> | seat:<n>` (default `focus`) so a stage
or museum world can pin its listener without touching the runtime.

### A6 — The speaker row: camera template + the shared anchor union

`WorldSpeaker` mirrors `WorldCamera` exactly — abstract, name-keyed,
`$type`-discriminated, whole-row `UpsertSpeaker`/`RemoveSpeaker`:

- `Fixed(Name, Position, Feed, Attenuation?)`
- `Anchored(Name, Anchor, Offset, Feed, Attenuation?)`
- `Bed(Name, Center, Radius, InnerRadius?, FadeSeconds?, Feed)` — **beds
  are a speaker `$type`, not a fourth concept**: everything but the
  spatialization math is shared. Envelope-by-presence is those three
  numbers as data. Box extents become a later `$type` only if a sphere
  proves dishonest.
- `Feed(Source, Channel: mix|left|right, Gain)`; `Attenuation(Radius,
  Curve?)` nullable → audio defaults. Omni-only v1 (no cones — stereo
  separation is already the feed model; a directional `$type` is a free
  retrofit under supergreen).

**`WorldAnchor` is the new shared vocabulary** — one union both cameras and
speakers consume: `Entity(index)` | `EntityLeaf(index, roleToken)` |
`Placement(placementId, shapeId?)`. Leaf addressing uses the **closed
12-token humanoid role set** (`WorldAvatarCatalog.HumanoidAnchor`,
`role = bone % 12`, slot ranges frozen across population rebuilds) — never
a dynamic-transform slot (an engine packing detail) and never a bone
ordinal (counts vary per avatar). Placement leaves address by creation
`ShapeDocument.Id` (the creation-camera precedent). **`WorldCamera.Anchored`
migrates onto `WorldAnchor` in the same arc** — a speaker-only union would
fork the vocabulary the ruling says is shared; supergreen makes the
migration (camera JSON, default world, validator, reconcile) cheap.
The held brick is then literally: an `Anchored` speaker with
`EntityLeaf(entity, "left-hand")` and a small offset.

### A7 — Sources are shared identities, never inline payloads

The load-bearing structural finding: `IAudioMachine.ReadSamples` is a
**destructive ring drain** — two rows draining one ring each get half the
samples. The settled "stereo = two rows sharing a source" therefore forces
source identity into the document: the runtime drains each source once per
block and fans out to every feed tapping it (`left`/`right`/`mix` taps are
free). The union, on the `WorldScreenSource` pattern:

- `None` — honest silence (`speaker.state` reads the fault).
- `Machine(ScreenIndex)` — screen index IS machine identity for
  screen-hosted machines. The validator checks only that the screen row
  exists — never that its declared source is `$type machine` (runtime
  inserts overlay declared sources). No live machine at drain time =
  silence + state echo, never a reject.
- `Tune(TuneId)` — references the new **`Tunes` asset section**
  (`WorldTune(Id, Document, Hash)`, inline-canonical, the P5 pattern
  verbatim). **Headless hosting is a runtime derivation, not a data
  concept**: a tune acquires one headless HGB machine only while a speaker
  references it, released when orphaned — the camera-view acquire/release
  pattern; the tracker's preview player is the proven pure-CPU shape. A
  jukebox with no screen needs no new hosting vocabulary.
- `Synth(PatchId)` — references the **`Patches` asset section**
  (`WorldPatch(Id, Document, Hash)`, same inline-canonical shape). Patches
  are mono by construction (channel selectors degenerate; documented, not
  rejected).

### A8 — Emission facets and creature voices

`WorldSceneRow` and `WorldPlacement` gain a nullable
`Emission(PatchId, Level, Radius?)` facet (the repeat-facet precedent — a
facet edit is just the row's existing whole-row upsert; no new section, no
dangling row references, the shimmer's diff sees it for free). Pose rides
the row. Under `Repeat`, emission binds to the placement **root** only (an
8×8 lattice must not become 64 voices; a per-copy flag is a future facet
field, not a schema fork).

Creature voices live in the creation itself:
`CreationBehaviorDocument.Sounds` — `CreationSoundDocument(Name, ShapeId?,
Patch, Level?, Radius?)`, following `Faces`' named-wiring shape, with the
patch **inline** (creations stay portable; the existing creation hash
covers it — no new pin machinery). A placement of a sound-bearing creation
auto-surfaces emitters anchored `Placement(placementId, shapeId)` — zero
world-row authoring; the row's `Emission` facet remains the per-instance
override channel.

### A9 — The synth: 32 fixed-point voices, `puck.synth.v1`

`WorldVoiceSynth`: 32 voices in a fixed struct array, zero alloc. Voice =
2 oscillators (sine as a `FixedComplex` rotor — one complex multiply per
sample, no table; saw/tri/square as Q32 phase accumulators; noise as a
`Pcg32` stream + one-pole tilt), ADSR in sample units, pitch
envelope/LFO, one fixed-point state-variable filter (the one genuinely new
DSP element), gain. Pure `Render(Span<short>, frames)` surface — the same
code executes on the audio thread and the proof driver. Every trigger
carries a **seed** so noise-bearing voices reproduce exactly.

`puck.synth.v1` is its own small integer-deterministic document in
`Puck.Authoring` (a patch is used without any tune; folding into the tune
family would drag tracker baggage into a creek). Parameter space: osc kind
+ duty/polynomial, ADSR as frames/levels, `PitchMillihertz`, optional
sweep/vibrato in the same units, duration-or-loop. Tens of bytes of JSON,
runtime-unit fields, hardware-kin (compilable to APU register writes later
if ever wanted).

### A10 — Document maturation and the one big prerequisite

The canonicalize+hash core generalizes out of `CreationCanonicalizer` into
a document-neutral helper with per-family adapters — creation, synth, and
audio all meet the UIE-6 standard (one pipeline, canonical bytes + SHA-256,
doc+hash from the same result, foreign hashes rejected). `puck.audio.v1`
matures on the way: Extensions bag (unknown members are silently dropped
today — a real defect), canonicalizer, `Puck.Authoring` home.

**The arc's largest external dependency, scheduled explicitly:** the tune
compile chain (`AudioDocumentCompiler`, `TuneRom`, and their SM83
framework dependencies) lives in `Puck.Demo` — the `Tune` source cannot
ship until it lifts to a library. This lift also serves the demo-retirement
trajectory directly; the demo-port ledger gains the entry.

### A11 — Defaults, grants, reconcile, editor

- **`WorldAudioDefaults` section** (the render-defaults precedent):
  `MasterGain`, `DefaultSpeakerRadius`, `DefaultCurve`,
  `DefaultBedFadeSeconds`, `Listener` — document defaults with the
  absence-coalesce convention; live master volume is a session lever verb.
  These are NOT editor policy; `WorldAuthoringDefaults` gains nothing.
- **Grants:** `WorldSection` grows `Speakers`, `Tunes`, `Patches`, `Audio`;
  the permissive local seeds keep couch play frictionless; mutation gating
  applies unchanged.
- **Mutations:** the sanctioned coarse pairs — `UpsertSpeaker/RemoveSpeaker`,
  `UpsertTune/RemoveTune`, `UpsertPatch/RemovePatch`, `SetAudioDefaults` —
  with no-cascade guards (removing a referenced patch/tune, or a placement
  a speaker anchors to, rejects loudly naming the dependents).
- **`ReconcileSpeakers` runs AFTER `ReconcileScreens`** (the chiasmus
  inverts the cameras-before-screens ordering: speakers consume screen
  slots). Diff-by-name; property-write cheap edits; release+recreate on
  source-identity/`$type`/anchor-kind change with the **asset hash as the
  restart discriminator** (the placement animator's precedent — a tune
  content change restarts its machine honestly, a gain edit does not);
  self-heal when a machine boots late into a referenced slot.
- **Editor:** selection absorbs speakers as `(Speakers, name)`; Fixed and
  Bed rows drag through the P3 channel (`WithPosition`); Anchored rows edit
  `Offset` via numeric verbs in v1. Speakers have no geometry, so editor
  mode renders a small **gizmo glyph** at each resolved speaker pose (beds
  get a translucent radius shell) — selection, highlight, and the shimmer
  key off it. Verbs: `world.speakers`, `speaker.state` (live per-row status
  incl. faults), the HUD selection line, `audio.emitters` (the derived
  emitter-table dump — the document→emitter derivation made assertable).

### A11b — World events tie to audio as CUE DATA (owner-raised, post-AP1)

The trigger mechanism was built for events from the start (sequence-stamped
one-shot trigger records on the snapshot); the wiring gets its vocabulary:
a `cues` list in the Audio section — rows of `(event token, patchId, gain,
placement)` where placement is `at-site` (spatial, at the event's world
position — a mutation chime where the row changed, the shimmer's audio
twin), `listener` (UI feedback), or `emitter:<name>`. Event tokens are a
CLOSED, published vocabulary of engine mechanisms (the leaf-role
precedent): `mutation.applied`, `mutation.rejected`, `grant.denied`,
`player.jump`, `player.land`, `player.footstep` (derived from the
presentation gait phase), `screen.boot`, `screen.fault`, `seat.join`. The
§2.6 audit holds: a genre ships different cue rows; new tokens appear only
when the engine grows new mechanisms. Trigger production is one pluggable
"submit a trigger request" seam with multiple producers (arrival policy,
the edit-echo lane, gait derivation, screen lifecycle). Behavioral
creature-condition triggers (on-move, on-face-change) remain a later tier
on `CreationSoundDocument`. Lands in AP4.

### A12 — Verification: the pure core with two drivers

`WorldAudioMixer.MixBlock(in WorldAudioSnapshot, Span<short>)` is a
synchronous pure function owning no thread — the WASAPI thread and the
offline proof are two drivers of the same code (the `ReadOutputPixels`
analog). The offline proof steps **headless cores synchronously** (the
tracker-preview pattern — never `QueuedMachineWorker`, whose worker thread
is the one nondeterministic scheduling element), drives N engine ticks at
stepTicks 210, renders exactly 200 frames per tick against a scripted pose
table and seeded synth triggers, and **SHA-256-hashes the raw s16 PCM** —
machine-stable because every stage is fixed-point, self-referential per
the doctrine (a mix-law correction re-goldens it). A separate live smoke
asserts what the offline proof can't: the device opens, delivers frames
without error, and the no-device path runs silent without throwing. The
document-side proof asserts: the all-kinds round-trip (every speaker
`$type`, all source kinds, facets, creature sounds) byte-stable through
`world.save`; hash pins reject tampering loudly; the validator rejection
table; mutation/journal/undo/grants rounds; leaf-role→anchor stability
across boots; the `audio.emitters` listing pinned.

## 2. The phases

### AP0 — Documents and vocabulary (no runtime)
The generalized canonicalizer + adapters; `puck.audio.v1` maturation and
lift; `puck.synth.v1`; the tune-compile-chain lift out of Demo (the
scheduled prerequisite); `WorldAnchor` union + `WorldCamera.Anchored`
migration (worlds migrated once, worlddoc ouroboros green).
Exit: libraries build, camera proofs green, the library tune chain
byte-matches the oracle.

**Owner ruling (landed mid-AP0): Demo is fully untouched by this arc.** No
rewiring, not even the data-contract tier — the library copies mature IN
SPIRIT (Demo's originals stay and die with Demo), schema kinship holds by
name so Demo-authored files import through the strict pipeline, and Demo's
tune chain serves as the read-only behavioral ORACLE (the library's cart
output byte-compares against it). Where a shared surface would force a
Demo edit, the library keeps a source-compatible adapter instead.

### AP1 — The mixer core, the synth, and the offline proof
`WorldVoiceSynth` + `WorldAudioMixer.MixBlock` + the fixed-point
spatialization stages + `WorldAudioSnapshot`; the offline tick-driven
hash proof lands BEFORE any device code exists — verification-first.
Exit: the PCM hash proof passes and re-runs stable; synth voices
demonstrate the creature/phenomenon repertoire in proof fixtures.

### AP2 — The world data model
Sections (`Speakers`, `Tunes`, `Patches`, `AudioDefaults`), mutations,
validator, grants, emission facets, `CreationBehaviorDocument.Sounds`,
emitter derivation + `audio.emitters`, `ReconcileSpeakers`, the snapshot
publisher in the frame source. Exit: the document-side proof battery
passes; the offline hash proof now drives from a fixture *world document*.

### AP3 — The device
The WASAPI render thread + `IHostedService`; `IScreenMachineEngine.Create`
gains the rate (the flagged engine seam), World passes 48000 always-on;
headless tune hosting acquire/release. Exit: the live smoke passes both
present/absent-device paths; a booted cabinet is audible by proximity —
the first sound World has ever made.

### AP4 — Editor, UX, polish
Gizmos + bed shells, selection/drag/numeric verbs, `speaker.state`/HUD,
master-volume lever, the full placements-style proof; the battery re-run.
Exit: an author places, aims, and hears a speaker without leaving the
session; every proof green both backends.

## 3. Deferred ledger
**Adaptive/contextual music (owner-confirmed direction, post-AP1):** three
tiers, none needing new mechanism kinds. Tier 1 ships with this arc (spatial
context via beds/attenuation; event stingers via A11b cues). Tier 2 is one
thin data seam — a context-token→input-bit mapping on the Tune source, so a
composer authors a REACTIVE tune cart that reads KEYINPUT bits as context
flags (layer mutes, section jumps — the authentic chip-music technique);
the machine input surface already exists. Tier 3 is the music DIRECTOR: an
addon granted audio authority drives per-speaker stem gains/triggers/tune
inputs from world queries — capabilities-first-class, score logic stays
author-land; a data-only context→gain-curve shortcut may follow for simple
cases. Beat-synchronized transitions defer until tempo awareness is
demanded by a real composer.
HRTF + occlusion (tier 2 — slots into the pan stage and the snapshot);
clip/sample source kind (no decode infra exists; from-scratch when
wanted); per-copy repeat emitters; directional cones; box beds;
non-Windows device backend; recording the world mix (a tap on the mixer
output — the recording graph is format-aligned at 48k already); Doppler.

## 4. Owner-decision flags
None open — the rulings above were made by the owner in design review
before this plan was written. The one standing acceptance to reaffirm at
AP3: always-on machine audio's cost (~192 KB + low-single-digit % CPU per
booted machine) as the price of speakers binding at any time without
machine reboots.

## 5. LANDED — the arc shipped (2026-07-19)

All five phases are on `claude/puck-world-next-steps-c46511`, each as
scoped milestone commits; the batteries are self-referentially green.

- **AP0** (`ce9de2a`, `e905742`, `6131aa6`, `1a03b64`, `73bf3e6`): the
  document-neutral canonicalizer core + adapters, `puck.audio.v1` matured
  into `Puck.Authoring`, `puck.synth.v1`, the tune compile chain lifted to
  `Puck.Forge` (byte-matching the Demo oracle; Demo fully untouched per the
  mid-AP0 ruling), the `WorldAnchor` union with the camera migration.
- **AP1** (`e794b22`, `b61a580`, `e48aa36`): `WorldAudioSnapshot`, the
  32-voice fixed-point `WorldVoiceSynth`, `WorldAudioMixer.MixBlock`
  (integer end to end, the cubic soft-clip, finite-support cull, equal-power
  pan, block coefficient ramps), and the offline PCM hash proof
  (`audio-mix.cs`) landing BEFORE any device code.
- **AP2** (`7c4dee1`, `4c9db00`, `d6dd80f`, `e259e31`): the world data
  model (Speakers/Tunes/Patches/Audio + emission facets + creation sounds,
  hash-pinned inline-canonical assets), mutations/validator/grants,
  `WorldAudioDirector` (stable-id derivation, arrival triggers, tune
  acquire/release, the snapshot publisher), `audio.emitters`, the
  document battery, and the fixture-world golden PCM hash.
- **AP3** (`79b6a2d`, `3d6c0b3`, `66aaec4`, `cac8930`): always-on machine
  audio through the flagged `audioSampleRate` engine seam, the WASAPI
  render device + hosted governor (silent-degrade + ~1 s rebind), the
  per-frame machine-source self-heal, `audio.state`, and the live smoke
  (`audio-device.cs`) — the first sound World ever made.
- **AP4** (`83f14b2`, `b0610c0`, `49a9d87`, `2b9adb7`, `b8f6768`,
  `f70941a`, `739b3c7`, `d33c988`): **THE CUE TABLE** (§A11b) — the closed
  published token vocabulary, the validator table, the 4-deep reserved
  transient-emitter pool (patch-derived TTL, 2 s looping cap,
  nearest-expiry eviction, trigger+emitter in one snapshot), producers for
  mutation.applied (at-site where the payload carries a pose) /
  mutation.rejected / grant.denied / player.footstep (gait-phase wrap,
  local seats) / screen.boot / screen.fault / seat.join; speakers in the
  editor (selection/pick/drag/undo + the console-only `editor.speaker.*`
  numeric twins — every place-page chord slot was honestly spoken for);
  the overlay GIZMO layer (projected chips per the icon grammar: two new
  procedural icons + the RING element kind; accent = selection, held =
  shimmer; beds get radius rings); `speaker.state`; the `world.volume`
  session lever with the render-levers fold
  (`WorldSessionCapture` + the `audio` drift dimension).

**Proof evidence (all green, 2026-07-19):** `audio-mix.cs` — every battery
incl. the new (i) cue rounds; both golden PCM hashes reproduce unchanged
(no mix-law change; the run's world-document hash
`8EC7ED9E8D2D369754FC94C87C87E46FFA5170204336F941EE4FBF5DB79AE427`
self-verified across two fresh runs). `proof.cs audio` — the full AP2
battery + the AP4 cue/editor/volume rounds, the ouroboros now carrying the
cue table and the folded volume lever, the REBOOTED cue table still firing.
`ui-floor` — both backends, including the new gizmo round (accent
population ~660 in editor mode vs ~35–61 exited). `audio-device.cs` — the
device ladder, the audible cabinet, and the screen.boot cue on a real
cartridge boot.

**Honest opens:**
- `player.jump` / `player.land` are RESERVED tokens with no producer: the
  client view carries no grounded/airborne signal, and Y-heuristics would
  misfire on flying/swimming kits. Wiring them wants a small presentation
  flag on the snapshot (a sim-side fact surfacing, one honest seam).
- Anchored speakers edit their `Offset` numerically only (documented v1);
  a drag-in-anchor-space channel is a possible later editor tier.
- Constructor-time declared machine boots precede the binder lifecycle
  tap's wiring, so a DECLARED cartridge's boot fires no `screen.boot` cue
  (runtime inserts and reconcile-driven source changes all do).
- The bed gizmo's radius ring is a screen-space circle at the center's
  depth — a radius indicator, not a perspective-correct 3D circle
  (deliberate; documented at the projection helper).
- Cue TTL ages on the presentation clock (the frame source's delta); the
  offline drivers age at the sim cadence — equivalent in law, not in wall
  time, which is fine for a presentation-only transient.
- Deferred ledger §3 unchanged (adaptive-music tiers 2–3, HRTF/occlusion,
  clip sources, cones, box beds, non-Windows device, the mix-tap
  recording, Doppler).
