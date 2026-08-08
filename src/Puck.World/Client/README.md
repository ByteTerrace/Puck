# Client/ — the per-machine client half

This folder is the client side of the world's client/server split: everything
that turns server snapshots into rendered frames and local devices into
submitted intents. Poses flow IN via per-tick `WorldSnapshot`s only; intents,
commands, and session requests flow OUT over the `IServerLink` — the client
never writes a pose. Everything here is presentation-side: floats are fine,
nothing in this folder is simulation state, and nothing here feeds back into
the deterministic tick.

## Seats and input

- `PlayerRoster.cs` — seat metadata: which devices sit at which of the four
  local slots, each seat's selected or pending profile, and the join/confirm
  flow (a pending seat's inputs drive the profile picker, not locomotion).
- `SeatController.cs` — the per-seat device-intent producer: held keys,
  stick values, and held channel lanes folded into the seat's per-tick
  `PlayerIntent` submission.
- `WorldPerceptionAnchor.cs` — the per-seat perception anchor: the ONE body
  index all seat-relative presentation derives from — the chase-camera anchor
  pose and seat-join cue site (`WorldFrameSource`), the spatial-audio listener
  (through the seat's view-camera pose), the crowd soft-shadow centers
  (`WorldSceneEmitter`), and the `seat.<n>.position.*` HUD binding family. It
  resolves to the seat's bound body (slot n → body n) UNLESS the seat's
  Control route targets a body with capture on (possession), in which case it
  swaps to that body in this one place and every derivation follows together —
  a mirror route (capture off) or a screen route never swaps it.
  `WorldSeatContextSync.Publish` writes it every tick off the same grant-table
  read that publishes the `engagement` context family. `player.where` echoes
  it (`anchor=body:<n>`, 0-based) for local seats.
- `WorldGroupAnchors.cs` — resolves each group anchor's smoothed centroid and
  spread once per frame (the establishing-shot camera's live pose;
  presentation-only).

## The entity view and frames

- `WorldClient.cs` — consumes each tick's snapshot into a double-buffered
  entity view (previous/current pose per entity) and resolves per-frame
  render poses: position lerp plus shortest-path orientation nlerp at the
  fixed-step accumulator's residual, with per-entity correction easers so an
  authority correction glides visually while the simulation pose snaps. A
  snapshot entry flagged as a teleport snaps both endpoints so nothing
  interpolates across a jump.
- `WorldFrameSource.cs` — composes the frame the SDF renderer draws: the
  avatar catalog's animated leaves, the static scene, placements, screens,
  and viewport layout.
- `WorldSceneEmitter.cs`, `WorldPlacementStamper.cs`, `WorldStampPool.cs`,
  `WorldCreationFacets.cs` — the document-to-geometry emission path for scene
  rows and `puck.creation.v1` placements.
- `WorldViewComposer.cs`, `WorldCompositionState.cs` — offscreen view
  composition (the diegetic world cameras) and the delivered composition
  state.
- `WorldChangeShimmer.cs` — the delivery-time highlight on changed rows.
- `WorldSessionLeverSink.cs` — writes an accepted session lever onto the live
  presentation service it names (render settings, present pacing, audio mix
  gain) — the only write path for those knobs, reached only past the server's
  grant check.
- `FiniteGuard.cs` — the editor state-setters' finite guard (rejects NaN and
  infinite values before they enter client state).

## The editor

The in-session editor is client state end to end; its committed acts are
ordinary protocol mutations submitted with the acting seat's principal.

- `WorldEditorSession.cs` — editor-mode tenancy per seat: binding-group flip,
  camera-rig swap, and the layout seam.
- `WorldEditorTargeting.cs` — the selection (`section`, id-or-index), pure
  client state that self-heals against every delivered definition.
- `WorldEditorPicker.cs` — the look-ray pick: a fixed-point picking program
  built from the document, rebuilt only on a definition delivery.
- `WorldEditorDrag.cs` — the pending-row preview channel: a drag composes its
  preview over the delivered definition and commits exactly one whole-row
  mutation on release (one journal entry, one undo step).
- `WorldWorkbench.cs` — the sculpt sub-editor: a client-local
  `Puck.Forge.Authoring.SculptModel` bench whose live preview renders through
  the same stamping path a committed creation uses.

**Known limitations worth knowing before debugging them.** Authoring gestures
sit outside the simulation-replay contract by design: a stick drag integrates
presentation `deltaSeconds` and persists the resulting float row, so replaying
identical command snapshots need not reproduce authored coordinates (the
committed mutation and the journal are deterministic once the row exists). A
new controller's first South press can both seat the player and fire its bound
action, because seating and command dispatch consume the same snapshot. Losing
window focus can strand a held edge (a release that never reaches the input
router) until another edge clears it, and a signal from an unbound control can
reserve an input slot that dispatches nothing until the mapping is replaced.

## Audio and documents

- `WorldAudioDirector.cs` — derives the emitter table from the delivered
  definition with stable ids, resolves emitter poses per produced frame, and
  publishes `WorldAudioSnapshot`s to the mixer (see
  [`../Audio/README.md`](../Audio/README.md)).
- `Sdf/` — the `puck.sdf.v1` document decode (`SdfDocumentDecoder.cs`,
  `SdfDocumentModel.cs`) and its declared refusals (`SdfRefusal.cs`).
- `WorldSdfDocumentEmitter.cs` — loads a decoded `puck.sdf.v1` document
  through `world.sdf.load` and composes it as its own `ISdfSceneEmitter`
  beside `WorldSceneEmitter`; static world-set geometry only (no dynamic
  transforms, screens, or instances), probed and composed-admission-checked
  against the same capacity envelope `WorldSceneEmitter` shares.

## Verifying

Client behavior is verified by running the game and looking, plus the console
read-backs that echo client state (`world.players`, `player.bindings`,
`screen.state`, `editor.status`). See the parent
[`README`](../README.md) for the run recipe and console contract.
