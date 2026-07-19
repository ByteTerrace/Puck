# World authoring, audio, and Forge implementation review — 2026-07-19

Static implementation review of the 63 commits from `e94060f` (exclusive) through
`0db38ba996e1baa4f26d353dc589b9e27089c4e7` (inclusive): 159 changed files,
approximately 29,380 insertions and 1,342 deletions. The reviewed work includes
World editor P3–P6, page-group/chord meanings, live cameras and grants, canonical
creation/sculpt authoring, the new `Puck.Forge` framework, the deterministic World
audio stack and Windows render device, speaker authoring/gizmos, and the overlay
closure work from the prior UI/editor review.

This is a source review, not a verification report. At the user's direction, no
build, test, proof script, demo, ROM, GPU workload, or audio device was run. The
checked-in proof sources were inspected for coverage, but their current runtime
result was not re-established.

## Executive outcome

The direction is coherent and worth preserving:

- World authoring remains client-local until one whole-row, grant-checked mutation
  crosses the server boundary.
- Creation, tune, and patch assets are becoming strict, canonical, hash-pinned data
  instead of private Demo objects.
- `Puck.Forge` lifts reusable cartridge construction out of Demo and starts a real
  linker/framework surface.
- Audio stays presentation-only, uses fixed sample/frame units, and keeps the mixer
  and synth deterministic and allocation-bounded.
- Speaker routing follows the existing camera/screen composition model, and the
  unified overlay remains a writer-based neutral decorator rather than multiplying
  render nodes.

The implementation is not ready to be treated as fully closed. Its largest common
problem is **capacity and acceptance drift**: the document validator accepts more
than several frozen runtime tables can represent. Depending on the path, accepted
data is silently omitted, falsely reported as live, or throws during render/audio
attachment. The second common problem is **transaction and lifetime drift**:
client-local work is marked committed before the server accepts it, and the Windows
audio owner disposes resources even when its worker did not stop.

The highest-priority closure order is:

1. make every accepted audio/creation document fully compilable and representable;
2. align all frozen capacities with the thick World validator and reclaim live
   registry slots;
3. make workbench commits and editor exit honest about pending/dirty state;
4. make audio initialization, shutdown, and partial-failure cleanup ownership-safe;
5. close multi-seat preview and overlay envelope holes;
6. finish exclusive edit ownership and independent Forge verification before more
   authoring surface builds on those contracts.

## Architecture trajectory

The change set is best understood as one converging authoring pipeline rather than
as 63 individual commits:

```text
puck.creation/audio/synth data
        │ strict canonical bytes + hash
        ▼
World rows ── mutation/grant/validation/journal ── delivered definition
        │                                              │
        ├─ SDF stamp / workbench preview               ├─ audio emitter plan
        ├─ overlay HUD / gizmos                         └─ screen/tune/machine sources
        ▼
observable pixels and samples on one session timeline
```

The desired endpoint is clear: the same canonical data edited in-session is the
data rendered, heard, saved, replayed, and forged. The findings below identify
places where a private runtime rule still sits outside that pipeline. They should
be fixed at the shared boundary rather than papered over in an individual caller.

## Triage

| ID | Priority | Area | Finding | Required closure |
|---|---:|---|---|---|
| LSR-1 | P1 | Audio capacity | Valid World data can exceed emitter, source, or patch tables, causing silent loss, false status, or an exception. | Validate the complete derived plan and reclaim retired registry slots. |
| LSR-2 | P1 | Audio lifetime | WASAPI initialization/shutdown is not actually bounded or ownership-safe. | Cancellable initialization, join-aware disposal, `SafeHandle`, and complete failure cleanup. |
| LSR-3 | P1 | Audio documents | Canonical/hash-pinned tunes can still wrap values, throw during lazy compilation, or kill the device governor. | Validate the real compiler grammar and precompile/preflight before runtime attachment. |
| LSR-4 | P1 | Tune semantics | `HOLD` does not preserve the previous note as the document contract promises. | Carry forward the live register/period state; only `OFF` mutes. |
| LSR-5 | P1 | Creation geometry | Negative scale and weak frame/chain invariants make canonical geometry bounds and playback disagree with rendering/editor preview. | Unify positive scale, complete-frame, quaternion, and chain invariants at canonicalization. |
| LSR-6 | P1 | Workbench envelope | Each new bench is measured alone, while every active bench renders together. | Charge all active benches in admission and re-check the composed program. |
| LSR-7 | P1 | Workbench transaction | Sculpt commit marks the model clean when it only enqueues a mutation. | Correlate apply/reject and advance the clean baseline only on the matching apply. |
| LSR-8 | P1 | Editor data loss | `editor.exit` silently discards a dirty sculpt. | Refuse, confirm, commit, or explicitly narrate discard on explicit exit. |
| LSR-9 | P1 | Overlay envelope | Unbounded speaker gizmos can consume the record table before the editor HUD. | Validate/cull/budget gizmos or reserve primary HUD capacity. |
| LSR-10 | P2 | Edit ownership | Selection reports manual section holds but never acquires/releases an edit hold; same-row work remains last-writer-wins. | Complete exclusive lifecycle and/or optimistic base-hash checks. |
| LSR-11 | P2 | Drag correlation | Applied-then-overwritten mutations in one server drain leave a stale frozen preview until timeout. | Correlate apply echoes, not only the final delivered row. |
| LSR-12 | P2 | Preview identity | Authored `workbench:<seat>` creation ids collide with internal preview ids. | Enforce a reserved namespace or use non-document internal identity. |
| LSR-13 | P2 | Split-screen overlay | Binding-bar chord hints claim confinement but do not emit clip-scoped records. | Clip/deduplicate per-seat rects and size the clip table for the real writer set. |
| LSR-14 | P2 | Glyph artifact | Pack staleness is repaired at runtime and the proof can rewrite source assets; no read-only currentness gate exists. | Make baking explicit and currentness validation read-only. |
| LSR-15 | P2 | Audio numerics/clocking | Extreme radii/fades overflow runtime math, and queued-machine clock drift has no occupancy servo. | Validate representable bounds and add a neutral occupancy/watermark policy. |
| LSR-16 | P2 | Canonical identity | Save can relabel foreign schemas; canonical results/options/extensions are not fully immutable/order-canonical. | Strict save, internal neutral serializer, frozen options, immutable results, recursive extension ordering. |
| LSR-17 | P2 | Forge robustness | PBAK length arithmetic/trailing bytes and zero-length block operations violate their stated contracts. | Harden parser arithmetic/full consumption and reject zero counts. |
| LSR-18 | P2 | Forge proof/layering | Tune verification shares its oracle constants and overstates observable coverage; Authoring pulls SDF into audio-only Forge. | Add independent behavior proofs, split neutral contracts/adapters, and correct docs. |

## Findings

### LSR-1 — Runtime audio ceilings are outside the document contract

**Status: FIXED — `006c84f` (Arc 7 leading commit, `claude/puck-realtime-world-editing-4fd13f`).**
`WorldAudioMixer.RegisterPatch`/`SetSource` now contain a full-table overflow (a loud
stderr drop + `DroppedRegistrationCount`) instead of throwing; `RetirePatches`
reclaims patch slots whose id left the derived plan; and the director validates the
whole derived plan's patch/source counts against the mixer caps at the compose
boundary. Carried in the port plan as `largechange-01`.

[`WorldAudioSnapshot`](../../src/Puck.World/Audio/WorldAudioSnapshot.cs) holds 32
emitters, with four slots reserved for transient cues. The director nevertheless
derives every speaker, emission facet, and creation sound. It only writes a console
message when the plan exceeds 28 rows, then ignores `TryAddEmitter` failure while
publishing. `speaker.state` computes `inMix` from resolved pose/support rather than
actual snapshot admission, and the emitter count reports the full derived plan.
Accepted later rows can therefore be absent from the mix while the control plane
claims they are present.

Two smaller tables are even more dangerous:

- [`WorldAudioMixer.MaxSources`](../../src/Puck.World/Audio/WorldAudioMixer.cs) is
  16. A valid world with 17 distinct tune sources throws during source binding.
- `MaxPatches` is 32. A valid world with 33 patch identities throws during mixer
  attachment.
- `RemoveSource` nulls a value but never reuses its identity slot; patches have no
  removal. A long-running authoring session can exhaust either table through churn
  even when the current definition is small.

`WorldDefinitionValidator` validates row shape and references but not the derived
emitter/source/patch plan. This violates the repository's thick-validation posture:
accepted content must be renderable. Put simultaneous capacity computation at the
compose boundary, including creation-sound multiplicity and the four cue reserves.
Make registry removal reclaim slots. If overflow is a desired feature, it needs an
explicit deterministic priority policy in data and truthful admitted/dropped status.

### LSR-2 — WASAPI ownership can outlive the resources it uses

[`WasapiAudioRenderDevice`](../../src/Puck.Platform/Windows/Audio/WasapiAudioRenderDevice.cs)
waits indefinitely for its initialization signal. Its two-second `Dispose` join is
ignored; the code then disposes the signal and closes the raw event handle even if
the render thread is still executing and may wait on or fill through that handle.
That is a use-after-close risk, including Windows handle-value reuse aliasing.

Partial initialization is also not symmetrically owned. Exceptions before the
`Initialize` tuple returns leave the outer COM references null, so the render
thread's cleanup cannot release already-acquired enumerator/device/client objects.
A failure after `CreateEventW` leaks the raw handle because the failed constructor
is never disposed and has no finalizer/`SafeHandle`.

[`WorldAudioRenderService.StopAsync`](../../src/Puck.World/Audio/WorldAudioRenderService.cs)
has the same pattern: it ignores a five-second governor join, disposes `m_stop`, and
reports `stopped`. A governor stuck in the unbounded device constructor can later
touch the disposed event and fault on a background thread.

Make initialization cancellable and timed, acquire every COM object/handle under a
local `try/finally`, use a `SafeHandle`, and keep thread-owned resources alive unless
the owning thread actually joined. A timeout should become an observable silent/
rebind fault, never false `stopped` state.

### LSR-3 — Trusted tune data is not compiler-valid data

[`AudioCanonicalizer.Validate`](../../src/Puck.Authoring/AudioDocument.cs) checks
schema, order references, and blank notes, but not the compiler's real grammar:

- unknown note names pass canonical hashing and later throw in
  `ApuNotePeriod.MillihertzFor`;
- tempo values above 255 remain distinct canonical data but are silently clamped
  by the compiler;
- envelope integers are not byte-bounded and wrap through casts;
- unknown effect voices silently normalize to `pulse1`;
- null rows/effect values can dereference inside validation instead of producing
  collected validation errors;
- compiled stream/ROM feasibility is not checked at acceptance.

Compilation is lazy in `WorldAudioDirector.CreateTuneHost`. The render governor
marks an endpoint `playing` and calls `AttachMixer` without containing compile or
capacity exceptions, so one accepted tune can kill the governor and strand the
already-open device.

Validate note vocabulary, voice tokens, null graph members, byte-backed ranges, and
compiled size before hash-pinning or accepting the World row. Runtime attachment
should consume prevalidated/precompiled content and must still contain failures so
the device degrades to an honest fault state.

### LSR-4 — `HOLD` changes or silences the hardware voice

`AudioRowDocument.Hold` says the previous note continues without retriggering.
[`AudioDocumentCompiler`](../../src/Puck.Forge/AudioDocumentCompiler.cs) instead
writes zero period registers for a music hold. Pulse and noise effect holds share
the `OFF` branch and disable their envelopes/DAC. This is observable authoring
semantics, not a harmless encoding choice.

Track and re-emit the preceding register/period state for a hold without the trigger
bit. Only `OFF` should mute. Lock that vocabulary in the authoring validator and use
an output-level oracle rather than trusting the previous Demo implementation.

### LSR-5 — Canonical creation bounds and playback are not self-consistent

Creation validation accepts every finite scale. SDF emission turns scale components
into positive nonzero magnitudes, while
[`CreationGeometry.Reach`](../../src/Puck.Authoring/CreationGeometry.cs) uses the
signed maximum component. Negative scales can therefore render visible positive
geometry while producing a zero/negative reach used by the placement stamper and
animator for bounds, causing clipping or culling.

Frame and chain validation is also too weak for a strict canonical boundary:

- one-member chains, repeated members, and non-finite goals/poles are accepted;
- the sculpt loader silently drops a one-member chain, so canonical load/save is
  lossy;
- zero frame quaternions are accepted and normalized into NaNs by playback;
- frame scale is applied by the sculpt preview but ignored by World animation;
- incomplete frames are accepted, so a frame result depends on the preceding frame
  despite `FrameDocument` describing a full snapshot.

Normalize scale to one positive, bounded domain shared by editor, bounds, and SDF
emission. Require chains of at least two unique existing shapes with finite solver
inputs. Either require exactly one valid transform per shape per frame, or formally
adopt sparse-frame semantics and implement them identically in preview and playback.

### LSR-6 — Concurrent benches bypass the render-envelope admission check

**Status: FIXED — `16ce575` (Arc 6 leading commit).** `ComposeCandidate` folds every
currently-active bench onto the admission candidate, so the frozen floor honestly
bounds the composed program and no `Build` overflow is reachable (the non-throwing
capacity-refusal clause is subsumed). Carried as `largechange-06`.

[`WorldWorkbench.TryEnter`](../../src/Puck.World/Client/WorldWorkbench.cs) measures
the delivered definition, current drag ghosts, and only the bench being opened. It
does not include benches already open for other seats. `ComposeCreations` and
`ComposePlacements` later include every active bench, and
[`WorldFrameSource`](../../src/Puck.World/Client/WorldFrameSource.cs) builds the
combined program without a second capacity check. Near the frozen program-word
ceiling, two benches can each pass against the same baseline and exceed capacity
together; `SdfWorldEngine.UploadProgram` then throws.

Compose all active benches into every admission candidate. The final composed
program should also have a non-throwing capacity refusal path so client-local
preview state cannot take down presentation if policy changes between admission and
render.

### LSR-7 — Enqueued sculpt work is reported as committed

[`EditorSculptCommandModule.CommitHandler`](../../src/Puck.World/EditorSculptCommandModule.cs)
submits an `UpsertCreation` and immediately calls `WorldWorkbench.NoteCommitted`.
The loopback transport only enqueues; the server can later deny the grant, reject
validation/envelope checks, or apply another edit first. Rejection correlation is
sent only to the drag channel.

After a failed sculpt commit, the HUD reports zero uncommitted edits and subsequent
close/exit can discard the model without warning even though no creation landed.
Give workbench commits an operation identity and frozen expected hash. Advance
`CommittedRevision` only on the matching apply, and restore/narrate dirty state on
rejection or supersession.

### LSR-8 — Explicit editor exit silently loses dirty sculpt work

`WorldEditorSession.Deactivate` correctly owns complete cleanup for controller
departure and slot reuse, but `editor.exit` uses that same unconditional path.
It drops the workbench and reports only that the chase camera was restored. The
dedicated `editor.sculpt.exit` path, by contrast, counts and narrates discarded
edits.

Departure must remain unconditional. Explicit user exit should refuse while dirty,
offer/perform a deliberate commit or discard action, or at minimum state exactly
how many edits were discarded. This must be coordinated with LSR-7 so a submitted
but unacknowledged commit is not mistaken for clean state.

### LSR-9 — Speaker gizmos can starve the primary editor HUD

**Status: FIXED — `da63bfe` (Arc 6 leading commit).** `ComposeGizmoSeat` now carries a
per-seat `MaxGizmoChipsPerSeat = 16` budget, nearest-to-camera kept (farthest evicted
in place), so gizmo admission is a bounded, documented fraction of the 192-record
table. Carried as `largechange-09`.

The overlay record table has 192 elements. Speaker count has no document ceiling,
and `WorldFrameSource.ComposeGizmoSeat` projects every composed speaker for every
editing seat. `EditorGizmoWriter` emits one icon per point speaker and a ring plus
icon per bed. Gizmos run before the editor HUD; only the final toast has reserved
capacity.

In four-seat editing, 18 visible beds already produce 144 gizmo records. With the
binding bars, that can consume the frame before any HUD text lands. The existing
`overlay-envelope.cs` source saturates synthetic records and exercises four real
HUDs, but it does not compose the actual binding/gizmo/HUD writer set or a large
speaker document.

Bound speaker/gizmo admission, cull by a declared distance/density policy, assign a
per-seat gizmo budget, or reserve the editor HUD before lower-priority decoration.
The proof must exercise the real writer order at the declared maximum.

### LSR-10 — Exclusive edit ownership is surfaced, not implemented

Selection remains client-local. The only path that submits an exclusive grant is
the explicit `world.grant` verb; selection/workbench entry does not acquire one and
exit does not release one. The HUD merely reads and displays a manually established
section hold. A workbench also records no base creation hash/revision, while the
server unconditionally replaces the keyed creation.

Two editors can therefore load the same row and commit in either order; later
delivery wins. Complete the planned acquire/deny/release lifecycle and add base-hash
conflict checking or make an enforced hold the sole commit prerequisite. Do not
advertise coherent concurrent editing until this closes.

### LSR-11 — Apply correlation misses a same-drain overwrite

The server drains all queued edits in FIFO order, emits an apply echo for each, and
delivers the definition once with the final result. Drag retirement uses rejection
echoes but treats an apply as complete only when the expected row survives in the
delivered definition. If A applies and B overwrites the same key during one drain,
A's frozen preview remains visible until its deadline.

Forward matching apply echoes to the drag/workbench channels using operation
identity. The final definition remains truth, but an already-acknowledged preview
must not masquerade as it.

### LSR-12 — The workbench preview namespace is reserved only by comment

Internal preview ids use `workbench:<seat>`, but creation validation/import/sculpt
verbs accept those ids. `ComposeCreations` appends the preview to the delivered
list; `WorldPlacementStamper.FindCreation` returns the first matching row. Editing
an authored `workbench:1` therefore stamps the delivered asset instead of the live
preview.

Reject reserved prefixes at every authoring/import boundary, or stop expressing
preview identity as an author-reachable document id.

### LSR-13 — Binding hints are positioned in a seat but not clipped to it

[`BindingBarWriter`](../../src/Puck.Overlays/BindingBarWriter.cs) claims the bar is
confined to its viewport, but it never opens a clip scope. The newly data-defined
chord hint is centered from the seat origin and its modifier labels/command label
have no rendered-length ceiling. It can cross a split-screen seam and can consume a
large fraction of the shared text budget.

Simply adding one clip per bar is insufficient: four gizmo scopes plus four HUD
scopes already consume the eight-entry clip table. Deduplicate identical seat clip
rectangles across writers, group all per-seat writes under one scope, or resize the
table from the real maximum. Then exercise long data-defined hints in a narrow
four-seat proof.

### LSR-14 — The prepacked glyph artifact has no read-only currentness gate

The committed glyph pack is hash-keyed and fixes the ordinary warm-start cost.
When it is missing or stale, however, `OverlayGlyphAtlasSet.LoadOverlayPack` decodes
the full atlas and rewrites the pack in place. `overlay-envelope.cs` points that API
at the repository source asset directory, so a proof can silently mutate source
content. The build validates shader source/bytecode pairing but not glyph-pack
currentness; a stale committed pack is masked by an expensive first-run rebuild in
the output directory.

Make pack generation an explicit bake operation. Add a read-only gate that compares
the committed header hashes to the PNG/JSON and fails with the exact bake command.
Runtime fallback may use a disposable/cache directory, but proofs should never
repair their own source evidence.

### LSR-15 — Audio representability and source-clock policy are incomplete

**Status: PARTIAL — `006c84f` (Arc 7 leading commit).** The per-emitter radius squares
now saturate through `Int128` (`SaturatingSquareQ16`), closing the ~46 340-unit
overflow. **Still open:** the validator-side representable-radius bounds (a clean
follow-up) and the queued-machine ring-occupancy watermark/servo seam. Carried as
`largechange-15`.

The mixer uses `Int128` for position-distance math but squares Q16 radii in `long`.
An otherwise valid radius above roughly 46,340 world units overflows and commonly
silences its emitter. Very large fade seconds can likewise overflow conversion to
`int` frames. Validate values against runtime representation and perform the square
and conversion with `Int128`/saturation.

The audio arc also calls for a queued-machine ring-occupancy servo, but
`IAudioMachine` exposes only sample rate and destructive reads. Worker occupancy is
private and `MachineBlockSource` always drains exactly the device request. The
one-second drop-oldest ring bounds damage, but independent 240 Hz pacer and device
clocks can still underrun/drop over time without a metric or correction. Add a
neutral occupancy/watermark seam, deterministic elastic consumption policy, and a
long-horizon proof. The existing zero-drift fixture covers synchronous tune hosting,
not queued machines.

### LSR-16 — Strict canonical identity is still mutable at its edges

Load and World hash verification now cross family canonicalizers, but save methods
for creations and audio overwrite the schema before validation. A foreign in-memory
document is silently relabeled at the persistence boundary—the exact behavior the
strict contract rejects on load.

Additional identity hazards remain:

- public `DocumentCanonicalizer.Canonicalize<T>` can mint canonical-looking bytes
  and hashes without family validation;
- `CanonicalDocument<T>.Bytes` is a mutable array and returned documents can retain
  mutable extension dictionaries;
- `DocumentJsonOptions.Shared` is publicly mutable until first serialization;
- extension dictionaries/nested `JsonElement` property order is not recursively
  canonical, so semantically equivalent extension content can hash differently.

Do not stamp schema during save. Keep the neutral serialization primitive internal,
freeze options eagerly, copy/protect canonical bytes and document state, and sort
extension JSON recursively—or explicitly declare extension byte order identity-
bearing.

### LSR-17 — Forge binary helpers violate their own total-validation contracts

[`PbakBundle.ReadChunks`](../../src/Puck.Forge/Framework/PbakBundle.cs) checks
`offset + (int)byteLength` after accepting values through `int.MaxValue`. The addition
can overflow negative, bypass the bounds check, and drive a huge allocation from a
short malformed blob. It also returns after the declared chunk count without
requiring the input to be fully consumed, despite promising “consumed whole or
rejected.” Compare length to remaining bytes and reject trailing data.

`FrameworkKernel.EmitBlockCopy` and `EmitBlockFill` document a byte count of at
least one but do not enforce it. A zero BC value performs one operation, wraps to
`0xFFFF`, and walks the 16-bit address space. Add build-time guards before this
framework becomes a wider reusable surface.

### LSR-18 — Forge verification and layering lag the lifted architecture

`TuneVerify` reads the same `TuneProtocol` constants as the code it verifies and
checks WRAM flags/counters/pointers. It does not independently observe waveform,
framebuffer, save, or control behavior. That conflicts with the independent-oracle
rule and with the capability catalog's stronger verification claim. There is also
no maintained lifted-library-versus-Demo byte-match proof in the current tree.

Add an independent verifier whose expected facts do not share implementation
constants, and observe the cartridge's real outputs. Keep a byte comparison only as
a migration witness, not the behavioral oracle.

Layering should move in the same direction. `Puck.Authoring` references `Puck.SdfVm`
because `CreationGeometry` publicly exposes `SdfProgramBuilder`, so audio-only
`Puck.Forge` inherits the SDF renderer graph. Forge also brings the full Humble
machine through compilation and verification in one project. Split neutral document
contracts/canonicalizers from creation render adapters, and consider separating
Forge compilation from machine verification. Update `game-studio-plan.md`, which
still locates the framework under Demo, and narrow the capability claims to the
evidence actually present.

## Prior UI/editor finding re-audit

| Prior ID | Status at reviewed `HEAD` | Notes |
|---|---|---|
| UIE-1 | Closed, with new LSR-8 | Drag/frozen preview/selection/workbench teardown is complete; explicit dirty-sculpt UX is not. |
| UIE-2 | Closed | Typed editor float boundaries and public setters reject non-finite values. |
| UIE-3 | Partial | Expected-row/rejection correlation works normally; LSR-11 covers the same-drain overwrite edge. |
| UIE-4 | Closed for HUD/gizmos | Clip records and shader rejection exist; binding hints remain LSR-13. |
| UIE-5 | Measurement gate closed | The retained rebuild path and recorded measurement verdict are present. Runtime numbers were not rerun. |
| UIE-6 | Partial | Strict load/world hashing landed; LSR-5 and LSR-16 show validation/save/identity holes. |
| UIE-7 | Partial | The committed pack fixes ordinary warm start; LSR-14 covers bake/currentness ownership. |
| UIE-8 | Partial | Overflow counters and toast reservation landed; LSR-9 shows the primary HUD remains starveable. |
| UIE-9 | Measurement gate closed | Timestamp instrumentation and retained linear-scan verdict are present. Runtime numbers were not rerun. |
| UIE-10 | Closed | Candidate radius/cap are live authoring data and gathering is bounded before sorting. |
| UIE-11 | Open | Manual holds are visible; editor-driven acquisition/release/conflict avoidance is absent. |
| UIE-12 | Closed | Capture uses the neutral presentation contract; Overlays no longer references SdfVm. |
| UIE-13 | Static surface closed | Proof dispatch/source and current World/editor docs exist, subject to the gaps below. |

## Missing proof coverage

The checked-in proof sources do not establish:

- emitter/source/patch ceilings, truthful overflow status, or add/remove churn;
- invalid notes, null tune graph members, byte-bound overflow, compiled-size failure,
  or `HOLD` waveform semantics;
- hung/partial WASAPI initialization, post-event-handle failure, join timeout, or
  actual invalidation cleanup;
- long-horizon queued-machine/source clock drift;
- extreme audio radii/fade conversions;
- negative creation scale, zero frame quaternion, incomplete frames, invalid chains,
  or frame-scale preview/playback parity;
- two simultaneous benches near the render ceiling;
- rejected/denied sculpt commit dirty-state restoration;
- `editor.exit` with dirty or acknowledgement-pending sculpt work;
- automatic selection hold acquisition/release and concurrent same-row commits;
- two same-key applies in one drain and preview retirement;
- authored/imported `workbench:<seat>` ids;
- real writer-order saturation with binding bars, speaker gizmos, HUD, and toast;
- long binding hints in narrow split-screen viewports;
- read-only glyph-pack currentness;
- malformed PBAK arithmetic/trailing bytes and zero-length framework operations;
- independent tune framebuffer/audio/save behavior or a maintained lift witness.

## Actionable phase plan

### Phase A — Restore trust boundaries

1. Expand audio and creation canonical validation to every runtime/compiler invariant.
2. Make save as strict as load and make canonical results genuinely immutable.
3. Add complete derived audio capacity validation and reusable registry slots.
4. Preflight/compile tune assets before endpoint attachment and contain all governor
   failures.

### Phase B — Restore transactional/lifetime honesty

1. Make WASAPI init/stop/disposal cancellation- and join-aware.
2. Give drag/workbench submissions operation identities and matching apply/reject
   acknowledgements.
3. Guard/narrate explicit exit with dirty or pending work.
4. Implement exclusive edit acquisition/release plus base-hash conflict detection.

### Phase C — Close composed presentation envelopes

1. Charge every active workbench together.
2. Define speaker/gizmo admission and reserve primary HUD capacity.
3. Deduplicate per-seat clip scopes and clip binding hints.
4. Make the glyph pack an explicit, read-only-validated build artifact.

### Phase D — Harden and prove the lifted Forge

1. Correct `HOLD`, PBAK arithmetic/full consumption, and zero-length memory ops.
2. Add independent output-level cartridge verification and a maintained migration
   comparison.
3. Split neutral authoring contracts from SDF adapters and compilation from machine
   verification where useful.
4. Update the project map, game-studio plan, capability catalog, and subsystem docs
   to the resulting current architecture and evidence.

## Review limitations

- The review boundary was a moving development branch before this pass; findings
  are pinned to committed `0db38ba`.
- No runtime claim was independently verified in this pass.
- `rom-forge` and `gaming-bricks` skills named by repository guidance were not
  available in this session. Forge/emulator conclusions are therefore based on
  source contracts and current repository documentation, not those specialist
  handbooks.
- Existing uncommitted user/agent files were not modified.
