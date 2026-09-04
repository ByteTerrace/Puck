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
audio director imports `Puck.World.Audio` types directly. `WorldFramePresenter`,
`WorldSceneEmitter`, and `WorldViewComposer` live here — their only
root-crossing dependency was the audio director, narrowed to
`IWorldAudioFrameFeed`/`IWorldAudioCueSink` below.

## Seats and input

- `PlayerRoster.cs` — seat metadata: which devices sit at which of the four
  local slots, each seat's selected or pending profile, and the join/confirm
  flow (a pending seat's inputs drive the profile picker, not locomotion).
  `PlayerRoster.Devices.cs` (a sibling partial) carries the per-kind device
  vocabulary: a keyboard, mouse, or gamepad is learned through the router's own
  first-touch discovery (`InputRouter.ObserveDeviceKind` classifies the kind
  from the signal's source family before the roster ever resolves a slot for
  it), while a camera is recorded explicitly via `ObserveDevice` and seated by
  its own default policy (the lowest occupied, camera-less slot, player 1
  first — never minting a player). Tokens are minted per kind (`keyboard<N>`,
  `mouse<N>`, `gamepad<N>`, `camera<N>`) and `TryGetSeatDevice`/`AssignDevice`
  move a camera between occupied seats; assigning one to an empty slot is
  refused because a camera never creates or counts toward player presence.
  When several devices of one
  kind share a seat, `TryGetSeatDevice` resolves whichever was assigned to it
  most recently (`world.devices` marks that one `*`).
- `SeatController.cs` — the per-seat device-intent producer: typed movement
  and look samples, toggled motion input, and held channel lanes folded into
  the seat's per-tick `PlayerIntent` submission.
- `WorldPerceptionAnchor.cs` — the per-seat perception anchor: the ONE body
  index all seat-relative presentation derives from — the chase-camera anchor
  pose and seat-join cue site (`WorldFramePresenter`), the spatial-audio listener
  (through the seat's view-camera pose), the crowd soft-shadow centers
  (`WorldSceneEmitter`), and the
  `seat.<n>.position.*` HUD binding family. It resolves to the seat's bound
  body (slot n → body n) UNLESS the seat's Control route targets a body with
  capture on (possession), in which case it swaps to that body in this one
  place and every derivation follows together — a mirror route (capture off)
  or a screen route never swaps it. `WorldSeatContextSync.Publish` (in
  `Puck.World`) writes it every tick off the same grant-table read that
  publishes the `engagement` context family. `body.where` echoes it
  (`anchor=body:<n>`, 0-based) for local seats.
- `WorldGroupAnchors.cs` — resolves each group anchor's smoothed centroid and
  spread once per frame (the establishing-shot camera's live pose;
  presentation-only).

## The entity view

Catalog appearance and body identity are independent. `WorldRigCatalog` holds
128 reusable looks; each body has a separate transform range large enough for
any of them. A pinned or transferred look therefore keeps every leaf, even when
its destination slot originally wore a smaller rig. Restyling one body does not
move another body's transforms or attachment slots. Render-capacity probes
reserve the largest rig for each of the lowest `WorldBodiesLimits.DetailedRenderBand`
(128) detailed bodies and one coarse capsule for every remaining body;
repeated looks do not change that hybrid ceiling, and the number of catalog
entries is not the population limit.
This is a buffer and input-complexity bound, not a frame-rate guarantee. A dense
crowd with a hard presentation target needs a non-per-creature SDF lane such as
raster impostors or an authored aggregate representation.

Each catalog leaf retains its own culling instance and animated transform. Its
sphere fits the emitted primitive, including the unscaled authored offset, so
small looks stay enclosed and a tile touching one hand need not evaluate the
whole creature. `world.population` reports leaves, culling instances, and
authored instructions separately. The renderer's instance ceiling remains a
separate constraint on dense populations; reusable appearances do not remove it.

- `WorldClient.cs` — consumes each tick's snapshot into a double-buffered
  entity view (previous/current pose per entity) and resolves per-frame
  render poses: position lerp plus shortest-path orientation nlerp at the
  fixed-step accumulator's residual, with per-entity correction easers so an
  authority correction glides visually while the simulation pose snaps. A
  snapshot entry flagged as a teleport snaps both endpoints so nothing
  interpolates across a jump.
- `WorldFramePresenter.cs` — composes the frame the SDF renderer draws: the
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
- `WorldPlacementStamper.cs`, `WorldStampPool.cs`, `WorldPrototypeFacets.cs` —
  the document-to-geometry emission path for scene rows and `puck.creation.v1`
  placements; `WorldSceneEmitter` drives them for the boot world. A
  body-rooted `BodyStamp` carrying a `WorldLookMotion` with `Dynamics`/
  `PartDynamics` gets a root `SecondOrderFollower3`/`4` (position/orientation)
  plus one per-part position follower per named part — the presentation-only
  float twin of `Puck.Maths.SecondOrderDynamics`, stepped once per frame in
  `PackTransforms` off a `Tick`-latched delta; `WorldSceneEmitter`'s
  catalog-avatar-root path carries the identical root follower for a
  catalog-sourced look. `WorldGaitDrivers.cs` is the per-body animation-driver
  runtime beside them: a stamped creation's declared `drivers` yield a phase and
  an eased weight each, advanced once per `PackTransforms` from the body's
  rendered pose delta and its `WorldClient.Facts`, gated by a conjunction of
  tokens that may include the client-derived `moving`/`still` (an eased rendered
  speed against `WorldGaitDrivers.MovingSpeed`, so a stride releases on a stop
  with no sim fact involved); the shapes' `swings`/`slides` compose off them —
  presentation-only, written into the dynamic transform buffer and read nowhere
  else. Both reseed on `WorldClient.PoseEpoch` moving (an
  activation, a teleport, an over-threshold correction) rather than streak a
  discontinuous pose.
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
- `WorldSessionLeverSink.cs` — the name-keyed applier: writes an accepted
  session lever onto whichever live presentation service was registered under
  its token, the only write path for those knobs, reached only past the
  server's grant check. An unregistered token is refused by name.
- `WorldSessionLevers.cs` — the knob vocabulary (the `world.<knob>` verb names
  without their prefix) and the composition-time registration binding each to
  render settings, present pacing, the audio mix gain (`IWorldAudioLever`), or
  the binding-bar visibility.
- `WorldBindingBarVisibility.cs` — the live per-seat binding-bar visibility
  override the `binding-bar` lever writes and the root's bar-policy resolver
  reads.
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
- `WorldCameraRigCompiler.cs`, `WorldAnchorGeometry.cs`, `WorldRigCatalog.cs`,
  `WorldScreenTextDecal.cs` — the camera-program translation, static
  placement/shape anchor geometry, the avatar instance layout, and screen decal
  text.

## Camera programs

- `WorldCameraRigCompiler.cs` translates an authored `WorldCameraProgram` into
  the document-blind IR in `Puck.SdfVm.Views` and returns an
  `IWorldCameraProgramRig`: authored subjects and `state.<row>[.<key>]` bindings
  become per-frame slots the rig refills from the live document inside
  `Resolve`, so no caller can evaluate against a stale binding by missing an
  ordering step. `Retarget` repoints a cached rig at a newly delivered document;
  `Look` carries the seat's live orbit delta (inert on a program compiled
  non-interactive); `Spread` feeds an authored `spreadPullback`.
  `WorldCameraRigCompiler.Cache` is the one compiled-rig cache slot every
  caching call site holds: it recompiles when the authored program instance or
  any definition collection `Compile` reads (`cameras`, `curves`, `dynamics`,
  `views`) has been replaced, and retargets otherwise.
- Free Cam is a possession, not a second integrator: a `seatModes` state
  targeting `"camera"` possesses the seat's `camera-seat-<slot>` inhabited
  placement through the ordinary Engage door, and the seat's view resolves
  through `views.cameraRig` on this same pipeline.

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
- `PlayerAssignmentCommand.cs` — the shared `player.assign` definition and
  outcome narration over `PlayerRoster`; the root module registers this exact
  definition and command-level laws drive it through `CommandRegistry`.

## Audio and documents

- `IWorldAudioLever.cs` — the narrow write (`SetMasterVolume`) onto the root
  `WorldAudioDirector` that `WorldSessionLeverSink` holds instead of the
  concrete type, the same root-implements-a-Client-interface shape
  `IWorldSimulationClock`/`IWorldScreenPresenter` use.
- `IWorldAudioCueSink.cs` — the narrow write (`SubmitCue`) `WorldSceneEmitter`
  fires world-event cues through.
- `IWorldAudioFrameFeed.cs` — the narrow read/write `WorldFramePresenter` drives
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
