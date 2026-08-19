# Puck.World.Client — the per-machine client half

This project is the client side of the world's client/server split: everything
that turns server snapshots into rendered frames and local devices into
submitted intents. Poses flow IN via per-tick `WorldSnapshot`s only; intents,
commands, and session requests flow OUT over the `IServerLink` — the client
never writes a pose. Everything here is presentation-side: floats are fine,
nothing in this project is simulation state, and nothing here feeds back into
the deterministic tick. References `Puck.World.Protocol` for the wire
vocabulary and the link-query seam; never `Puck.World.Server` — a live
`WorldServer` reference belongs in the composition root
([`../Puck.World/README.md`](../Puck.World/README.md)), not here.

`WorldClientSeats` (implements the Server seam `IWorldEmbodiedSeats`) and
`WorldAudioDirector` stay in `Puck.World` itself rather than living here: the
audio director imports `Puck.World.Audio` types directly. `WorldFrameSource`,
`WorldSceneEmitter`, and `WorldViewComposer` live here — their only
root-crossing dependency was the audio director, narrowed to
`IWorldAudioFrameFeed`/`IWorldAudioCueSink` below.

## Seats and input

- `PlayerRoster.cs` — seat metadata: which devices sit at which of the four
  local slots, each seat's selected or pending profile, and the join/confirm
  flow (a pending seat's inputs drive the profile picker, not locomotion).
- `SeatController.cs` — the per-seat device-intent producer: typed movement
  and look samples, toggled motion input, and held channel lanes folded into
  the seat's per-tick `PlayerIntent` submission.
- `WorldPerceptionAnchor.cs` — the per-seat perception anchor: the ONE body
  index all seat-relative presentation derives from — the chase-camera anchor
  pose and seat-join cue site (`WorldFrameSource`), the spatial-audio listener
  (through the seat's view-camera pose), the crowd soft-shadow centers
  (`WorldSceneEmitter`), and the
  `seat.<n>.position.*` HUD binding family. It resolves to the seat's bound
  body (slot n → body n) UNLESS the seat's Control route targets a body with
  capture on (possession), in which case it swaps to that body in this one
  place and every derivation follows together — a mirror route (capture off)
  or a screen route never swaps it. `WorldSeatContextSync.Publish` (in
  `Puck.World`) writes it every tick off the same grant-table read that
  publishes the `engagement` context family. `player.where` echoes it
  (`anchor=body:<n>`, 0-based) for local seats.
- `WorldGroupAnchors.cs` — resolves each group anchor's smoothed centroid and
  spread once per frame (the establishing-shot camera's live pose;
  presentation-only).

## The entity view

- `WorldClient.cs` — consumes each tick's snapshot into a double-buffered
  entity view (previous/current pose per entity) and resolves per-frame
  render poses: position lerp plus shortest-path orientation nlerp at the
  fixed-step accumulator's residual, with per-entity correction easers so an
  authority correction glides visually while the simulation pose snaps. A
  snapshot entry flagged as a teleport snaps both endpoints so nothing
  interpolates across a jump.
- `WorldFrameSource.cs` — composes the frame the SDF renderer draws: the
  avatar catalog's animated leaves, the static scene, placements, screens, and
  viewport layout; publishes the audio director's per-frame snapshot through
  `IWorldAudioFrameFeed`.
- `WorldSceneEmitter.cs` — the boot world's own document-to-geometry emission
  path for scene rows and `puck.creation.v1` placements.
- `WorldViewComposer.cs` — offscreen view composition (the diegetic world
  cameras): layout selection and eased transitions for the main window.
- `WorldOverlayCapacity.cs` — `FromSchema()`, the one bridge from
  `WorldPopulationLimits.LocalSeatCount` and `WorldHudCapacity` to the
  `Puck.Overlays.OverlayCapacity` a host constructs `UnifiedOverlayNode` with;
  the numbers cross the layering as constructor data, never restated.
- `WorldSeatCameraPose.cs` — one seat's resolved listener-policy camera pose,
  the frame source's own input to the audio director's `Publish`.
- `WorldPlacementStamper.cs`, `WorldStampPool.cs`, `WorldCreationFacets.cs` —
  the document-to-geometry emission path for scene rows and `puck.creation.v1`
  placements; `WorldSceneEmitter` drives them for the boot world.
- `WorldSessionSceneEmitter.cs`, `WorldAdjacencySceneEmitter.cs` — the session
  projection's and adjacency neighbour's own content emission, parallel to
  `WorldSceneEmitter`'s boot-world path.
- `WorldSdfDocumentEmitter.cs` — loads a decoded `puck.sdf.v1` document
  through `world.sdf.load` and composes it as its own `ISdfSceneEmitter`
  beside `WorldSceneEmitter`; static world-set geometry only (no dynamic
  transforms, screens, or instances), probed and composed-admission-checked
  against the same capacity envelope `WorldSceneEmitter` shares.
- `Sdf/` — the `puck.sdf.v1` document decode (`SdfDocumentDecoder.cs`,
  `SdfDocumentModel.cs`) and its declared refusals (`SdfRefusal.cs`).
- `WorldCompositionState.cs` — the delivered composition state
  `WorldViewComposer` writes and readers consume.
- `WorldSessionLeverSink.cs` — writes an accepted session lever onto the live
  presentation service it names (render settings, present pacing, audio mix
  gain via `IWorldAudioLever`) — the only write path for those knobs, reached
  only past the server's grant check.
- `WorldSessionRenderEnvelope.cs` — the session projection's joint
  word/instance capacity measurer, and the process-wide window-lease counter
  (`WorldSessionWindowLeases`) `world.faces` reads back.
- `WorldWindowFrustumFit.cs` — the border-pair isometry and asymmetric-frustum
  fit a cross-authority window projects through.
- `WorldAuthorityRoute.cs`, `WorldContinuum.cs`, `WorldSeatAuthorityRouter.cs`
  — per-seat cross-authority routing: the selected route, the per-seat
  continuity state a route swap preserves, and the router itself.
- `WorldSeatCameraResolver.cs`, `WorldSeatViewState.cs`, `WorldSeatViewports.cs`
  — the seat-owned camera rig cache, view/pointer/cursor state, and the
  published per-seat viewport + camera a pointer consumer reads.
- `WorldTextCatalog.cs` — deterministic glyph-atlas generation for a
  world-authored font.
- `WorldCameraRigCompiler.cs`, `WorldAnchorGeometry.cs`, `WorldAvatarCatalog.cs`,
  `WorldScreenTextDecal.cs` — compiled camera rigs, static placement/shape
  anchor geometry, the avatar instance layout, and screen decal text.
## The fly camera application

- `WorldSeatFlyRig.cs` — the per-seat fly control application `player.mode`
  activates when a mode state's `target` is `"camera"`: a free camera driven
  by the seat's own move/look samples and its `MoveUp`-role held channel,
  seeded from the seat's current chase framing (no pose pop) and resolved
  against the world-authored `views.flyRig`. Presentation-only client state,
  like everything else in this section.

## The binding-authoring layer

- `WorldSeatBindings.cs` — the per-seat compiled `IInputBindings`: engine
  default ⊕ world overlays ⊕ profile bindings ⊕ live session rebinds, and the
  context-derivation state machine that picks a seat's active group. Besides
  the built-in roster/engagement/layout families and a world's own AUTHORED
  `seatModes` families (`WorldSeatModeFamily`, flipped by `player.mode`), a
  `state:<row>` family reads the routed world's scalar value or the
  controlled body's keyed value, allowing gameplay-rule state writes to swap
  whole control groups.
- `WorldAffordances.cs` — the process command vocabulary check every binding
  document validates against.
- `CommandVocabulary.cs` — the command-name string constants (and the two
  pure functions `PlayerCommandNames.RoutedChannelCommandName` and
  `AddonSourceVocabulary.TryResolve`) nine root `*CommandModule` classes in
  `Puck.World` forward their own declarations to, single-sourced here since
  the binding-authoring files above cannot reference those root types.

## Audio and documents

- `IWorldAudioLever.cs` — the narrow write (`SetMasterVolume`) onto the root
  `WorldAudioDirector` that `WorldSessionLeverSink` holds instead of the
  concrete type, the same root-implements-a-Client-interface shape
  `IWorldSimulationClock`/`IWorldScreenPresenter` use.
- `IWorldAudioCueSink.cs` — the narrow write (`SubmitCue`) `WorldSceneEmitter`
  fires world-event cues through.
- `IWorldAudioFrameFeed.cs` — the narrow read/write `WorldFrameSource` drives
  every produced frame (`Publish`, `ReconcileSpeakers`,
  `TryResolveSpeakerPose`, the `MachineSourceResolver` binding); extends
  `IWorldAudioCueSink` rather than re-declaring `SubmitCue`, since a frame
  source's need is that seam's need plus these four. See
  [`../Puck.World/Audio/README.md`](../Puck.World/Audio/README.md) for the
  audio director itself.

## Verifying

Client behavior is verified by running the game and looking, plus the console
read-backs that echo client state (`world.players`, `player.bindings`,
`screen.state`, `player.mode`). See
[`../Puck.World/README.md`](../Puck.World/README.md) for the run recipe and
console contract.
