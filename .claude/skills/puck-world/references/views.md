# Views — camera rigs, the seat chase, and mouse look

The `views` document section owns every presentation camera: the one shared
seat-chase rig every joined seat frames through, the named authored cameras a
window layout can show instead of a seat, and the local seat's live
mouse-look orbit policy. Document side: `src/Puck.World.Data/WorldViews.cs`
(`WorldViewDefaults`, `WorldViewLayout`, `WorldViewSlot`, `WorldSeatLook`,
`WorldSeatLookArming`), `src/Puck.World.Data/WorldCameraRig.cs`
(`WorldCameraRig`, the `WorldCameraMotion`/`WorldCameraAim` unions,
`WorldCameraLens`, the track records), `src/Puck.World.Data/WorldAnchor.cs`
(the anchor union a named `WorldCamera` row places against — see
[documents.md](documents.md) for the `state`/other sections; `WorldCamera`
itself is declared in `WorldDefinition.cs`). Compile/resolve side:
`src/Puck.World/WorldCameraRigCompiler.cs` (authored rig → `ISdfCameraRig`),
`src/Puck.World/Client/WorldFrameSource.cs` (the seat camera and named-camera
paths), `src/Puck.World/Client/WorldEditorSession.cs` (the per-seat rig
override), `src/Puck.World/Client/WorldViewComposer.cs` (layout selection and
transitions), `src/Puck.World/WorldCameraOrbit.cs` +
`WorldCameraOrbitDrag.cs` (the live mouse-look state and the pointer consumer
that drives it), `src/Puck.World/WorldPointer.cs` + `WorldPointerSink.cs`
(the per-seat pointer store and the one window-input observer feeding it).
Engine vocabulary: `src/Puck.SdfVm/Views/SdfCameraRig.cs` (`ISdfCameraRig`,
`OrbitRig`/`FollowRig`/`OrientedFollowRig`/`FirstPersonRig`/`FixedRig`),
`src/Puck.SdfVm/Views/ViewStack.cs` (a SEPARATE registration vocabulary — see
Capacities below). Verbs: `src/Puck.World/WorldViewCommandModule.cs`.
Mutation kinds: `SetViewDefaults` 31, `UpsertViewLayout` 32,
`RemoveViewLayout` 33, all under `WorldSection.Views` — see
[mutations.md](mutations.md).

## Contents

- The `views` section shape
- The rig: Motion / Aim / Lens, plus rig-level `SmoothRate`
- The motion arms
- The seat camera path
- Mouse look (`playerDefaults.seatLook`)
- The verbs
- Capacities and refusals
- Verifying

## The `views` section shape

`WorldViewDefaults(SeatRig, Layouts, SeatLook)` — a REQUIRED section every
document carries.

- **`SeatRig`** (`WorldCameraRig`) — the ONE chase framing every local seat
  wakes on. There is exactly one authored seat rig for the whole document,
  shared by all four seats; a live per-seat orbit drag (see Mouse look below)
  cannot live inside it, because it is one row, not four. `RebuildSeatRigs`
  compiles the SAME authored rig into every slot of `WorldFrameSource`'s
  per-seat rig cache on construction and on any `views`-section delivery
  (`world.row.set views.seatRig` live).
- **`Layouts`** (`IReadOnlyList<WorldViewLayout>`) — authored named window
  compositions; an empty list (the shipped default) falls the composer
  through to the built-in seat ladder (fullscreen → side-by-side →
  big-top/two-bottom → 2×2, `WorldFrameSource.LayoutRegion`). No cap exists on
  layout count or slots per layout — only per-field shape is validated (see
  Capacities).
- **`SeatLook`** (`WorldSeatLook?`) — the local seat's live mouse-look orbit
  policy, or `null` to fall back to `WorldSeatLook.Default`. One policy for
  the whole document, like `SeatRig` — not per-seat.

`WorldViewDefaults.Default`: `SeatRig` is an `Orbit` motion (`Distance:
5.4626001f, Yaw: 0f, Pitch: 0.4145069f, PivotOffset: Vector3.Zero`), `Aim` is
`Anchor(Offset: (0,1,0), WorldAxes: false)`, `Lens.FieldOfViewRadians` is the
engine default 55° (`WorldViewDefaults.EngineDefaultFieldOfViewRadians`,
pinned in `WorldViews.cs` because `Puck.World.Data` cannot reference
`Puck.SdfVm` — it mirrors `OrbitRig.DefaultFieldOfViewRadians`), `SmoothRate`
is `6f`. `Layouts` is empty. `SeatLook` is `WorldSeatLook.Default`. The
default `Orbit`'s `Distance`/`Pitch` numerically match
`hypot(2.2, 5)`/`atan2(2.2, 5)` — `OrientedFollowRig`'s own default
`EyeOffset (0, 2.2, 5)` — to five decimal places; nothing in source spells out
that derivation (no `hypot`/`atan2` call exists in `WorldViews.cs`), so treat
it as the apparent authoring recipe for converting a chase offset into an
equivalent orbit distance/pitch, not a documented contract.

The four shipped worlds each author their OWN `seatRig`/`seatLook` rather than
inheriting `WorldViewDefaults.Default` verbatim (read directly from
`src/Puck.World/Assets/worlds/*.world.json`) — the concrete instance of
CLAUDE.md's "would each world want this different" test: `play`/`jump` match
the type default's `Orbit` exactly (`distance 5.4626001`, `pitch 0.4145069`,
`smoothRate 6`, FOV 55°); `kart` widens to `distance 6.5`, `pitch 0.3947911`,
FOV ≈60° (`1.0471976`), `smoothRate 6`; `dive` pulls in to `distance
5.2497619`, `pitch 0.3097029`, an `aim.offset` of `(0, 0.6, 0)` (lower than
the other three's `(0, 1, 0)`), and `smoothRate 4` — the one shipped world
NOT using `6`. All four author `seatLook.yawSensitivity`/`pitchSensitivity`
at `0.001` (not the type default's `0.005`) and otherwise match
`WorldSeatLook.Default`'s pitch clamp/arming/worldAxes exactly.

## The rig: Motion / Aim / Lens, plus rig-level `SmoothRate`

`WorldCameraRig(Motion, Aim, Lens, SmoothRate = 0f)` composes three
independent presentation axes plus one ease knob:

- **`Motion`** (`WorldCameraMotion`: `Follow`/`Orbit`/`Static`/`Track`) —
  camera-local eye motion relative to the resolved reference frame.
- **`Aim`** (`WorldCameraAim`: `Anchor`/`Forward`/`WorldPoint`) — the framing
  policy that picks a look-at target from the resolved eye and reference
  frame, independent of `Motion`.
- **`Lens`** (`WorldCameraLens(FieldOfViewRadians)`) — vertical FOV, radians;
  validated finite and in `(0, π)`.
- **`SmoothRate`** (`float`, default `0f`) — an exponential low-pass on the
  RESOLVED eye/target, applied AFTER `Motion`/`Aim` resolve, regardless of
  which motion kind the rig carries. It is RIG-level, not per-motion-arm — it
  moved there 2026-08-07 precisely because only `Follow` had an equivalent
  ease before, so converting a seat rig's motion to `orbit` used to silently
  lose it. `WorldFrameSource` applies it only to the seat's plain (non-editing)
  chase rig — `ReferenceEquals(rig, m_cameraRigs[slot])` is the discriminator,
  true only while `WorldEditorSession.ResolveRig` returns the unedited chase
  instance back unchanged. The exact form (`WorldFrameSource.ResolveCamera`):
  `alpha = 1 - MathF.Exp(-SmoothRate * deltaSeconds)`, then `eye`/`target`
  each `Vector3.Lerp` toward the raw resolved pose by `alpha` — seeded
  UN-smoothed on the first resolve after entering the plain-chase branch
  (`m_cameraRigSmoothSeeded`), so a camera never flies in from zero. `0`
  (the type's own default, and the `WorldCameraRig` record default) skips the
  ease entirely — eye/target pass through byte-for-byte raw; every shipped
  world instead authors a non-zero rate (3 of 4 use `6f`, matching
  `WorldGroupAnchors`' own establishing-shot centroid rate — see the shipped
  values below).
  `WorldDefinitionValidator.ValidateRig` refuses a non-finite or negative
  `SmoothRate` by message, not a named enum door (see Capacities).

`ValidateRig` is the ONE validator both `views.seatRig` and every authored
`WorldCamera.Rig` row run through — a named camera's rig shares the exact
same shape and refusal rules as the seat rig.

## The motion arms

`WorldCameraRigCompiler.Compile` wraps an authored `WorldCameraRig` in a
private `ComposedWorldCameraRig : ISdfCameraRig`. Its `Resolve(in SdfAnchor
anchor, in SdfCameraClock clock)` first derives `referencePosition = anchor
.Position + Transform(referenceOffset, anchor.Orientation)` (a caller-supplied
extra local offset, zero for the seat path), then dispatches on `Motion`:

- **`Follow(Offset, WorldAxes, SpreadPullback)`** — `ResolveFollow` scales
  `Offset` by `1 + SpreadPullback * max(spread, 0)` (a group-spread pullback,
  zero for the seat path), then `ResolveOffset`: `referencePosition +
  (WorldAxes ? offset : Transform(offset, anchor.Orientation))`. Orientation
  IS applied here (unless `WorldAxes`).
- **`Orbit(Distance, Yaw, Pitch, PivotOffset)`** — `ResolveOrbit`: `pivot =
  referencePosition + PivotOffset` (added UNROTATED — `PivotOffset` is never
  transformed by `anchor.Orientation`, not even along the seat's live
  composition path below), then `eye = pivot + OrbitRig.Offset(Yaw, Pitch,
  Distance)` (`src/Puck.SdfVm/Views/SdfCameraRig.cs`:
  `(sin(yaw)*cos(pitch), sin(pitch), cos(yaw)*cos(pitch)) * distance`, 0 = +Z,
  positive pitch = up). **`ResolveOrbit` reads `anchor.Position` through
  `referencePosition` but NEVER reads `anchor.Orientation`.** An `Orbit`
  motion is therefore WORLD-ABSOLUTE — `Yaw: 0` always points toward world
  +Z — unless the CALLER folds the subject's own heading into the authored
  `Yaw` before compiling. `WorldFrameSource.ResolveCamera` is the one caller
  that does this for the live seat rig (see below); `ResolveNamedCamera`
  (named `WorldCamera` rows) does NOT — an authored camera using `Orbit`
  motion is pure world-absolute orbit, full stop, with no body to ride.
- **`Static(Position, WorldAxes)`** — `ResolvePosition`: `WorldAxes ?
  Position : referencePosition + Transform(Position, anchor.Orientation)`.
- **`Track(Definition, Playback, WorldAxes)`** — `EvaluateTrack` samples
  `Definition.Keyframes` (strictly increasing `Tick`, ≥ 2 rows, validated)
  against either `PresentationTime` (`clock.PresentationSeconds * 240.0`) or
  `AuthoritativeTick`, offset by `Playback.StartTick`, looped per
  `LoopMode` (`Once` clamps, `Loop` wraps, `PingPong` reflects), then finds
  the bracketing keyframe pair and either holds the left one (`Step`) or
  `Vector3.Lerp`s between them (`Linear`) — then feeds the result through the
  SAME `ResolvePosition` as `Static`.

`Aim` resolves independently of `Motion` and DOES read orientation for
`Anchor`/`Forward` (never for `WorldPoint`, a fixed world point):
`Anchor(Offset, WorldAxes)` → `ResolveOffset` (same shape as `Follow`'s);
`Forward(FocusDistance)` → `eye + Transform(-UnitZ, anchor.Orientation) *
max(FocusDistance, 0.01)`; `WorldPoint(Target)` → `Target` verbatim.

**Follow → Orbit conversion.** For a pure `Anchor`-aim, `PivotOffset: 0` rig,
an `OrientedFollowRig`-shaped chase offset `(0, y, z)` (no X component,
anchor-local up-and-back) converts to an equivalent `Orbit` via `Distance =
hypot(y, z)`, `Pitch = atan2(y, z)`, `Yaw = 0`. This is exact only when the
subject's own orientation is PURE YAW (no pitch/roll) — `Orbit`'s eye ignores
orientation entirely, so on a pitched/rolled body (a slope, a planetoid's far
side) the two shapes diverge; `Follow`'s offset stays anchor-local via
`Transform(offset, anchor.Orientation)` and tracks the subject's actual up,
while `Orbit`'s does not.

## The seat camera path

`WorldFrameSource.ResolveCamera(slot, region, width, height, deltaSeconds,
out eye, out target)`:

1. `body = m_anchor.PerceivedBody(slot)` — the per-seat PERCEPTION ANCHOR
   (`Client/WorldPerceptionAnchor.cs`, shared with the audio listener and
   `seat.<n>.position.*` HUD bindings — see
   [engagement.md](engagement.md#possession-swaps-the-perceived-world-not-just-the-driven-body)
   and [hud.md](hud.md)): the seat's bound body, or the routed body while
   possessing.
2. `chase = m_cameraRigs[slot]` (the compiled `views.seatRig`, rebuilt by
   `RebuildSeatRigs` on construction and on any views delivery).
3. **Live orbit composition** (the ONE live camera mechanism this type
   carries — no other motion kind gets one): only when `views.seatRig.Motion`
   is `Orbit`. `seatLook = playerDefaults.seatLook ?? WorldSeatLook.Default`;
   `yaw = orbit.Yaw + liveYaw + (seatLook.WorldAxes ? 0 : BodyYaw(bodyOrientation))`;
   `pitch = orbit.Pitch + livePitch`. `BodyYaw` recovers the body's heading
   as `atan2(behind.X, behind.Z)` (`behind = Transform(UnitZ, orientation)`) —
   the SAME convention `OrbitRig.Offset`'s yaw expects. `seatLook.WorldAxes`
   is the switch: `false` (the shipped default) composes the body's own yaw
   in, so turning the avatar swings the camera with it; `true` drops it, an
   absolute orbit independent of facing. `ResolveLiveOrbitRig` recompiles a
   per-slot cached rig only when the authored `Orbit` instance or the
   composed yaw/pitch actually changed since last frame (a moving body still
   invalidates every frame, since `BodyYaw` moves with it).
   `Follow`/`Static`/`Track` motions render through the plain compiled chase
   untouched — deliberately no live composition for them.
4. `anchor = SdfAnchor(Position: m_client.Position(body), Orientation:
   m_client.Orientation(body))`.
5. `rig = m_editor.ResolveRig(slot, chase, in anchor, m_elapsedSeconds,
   deltaSeconds)` — **`WorldEditorSession.ResolveRig` is THE per-seat live
   rig-OVERRIDE seam.** While the seat is not editing, it returns the SAME
   `chase` instance unchanged (the `ReferenceEquals` check `SmoothRate`
   gates on, above). While editing, it returns a DIFFERENT rig — a
   drag-steered frame (`AdvanceDrag`), an open sculpt workbench
   (`AdvanceWorkbench`), free-fly (`AdvanceFly`), or an orbit around the
   current selection/avatar (`AdvanceOrbit`) — already the seam the editor's
   own drag/workbench modes use; no second override mechanism exists.
6. `rig.Resolve(in anchor, clock)` yields `(eye, target, fieldOfView)`, then
   `SmoothRate` eases it (see above), then `CameraSnapshot.LookAt(...)`.

`m_seatCameraPoses[slot] = WorldSeatCameraPose(Joined: true, Eye: eye,
Forward: target - eye)` is filled from this SAME resolved rig — editor rig
included — right after `ResolveCamera` returns, and feeds
`m_audio.Publish(transforms, seats: m_seatCameraPoses, deltaSeconds)`: the
spatial-audio listener always listens from where the active view actually
looks, never a second derivation.

**Named cameras** (`ResolveNamedCamera`, for a layout slot's `Camera` field):
resolves the `WorldCamera` row by `Name` against `m_client.Definition
.Cameras`, resolves its `WorldAnchor` via `ResolveCameraAnchorPose`
(`Entity`/`EntityPart`/`Placement`/`Group` — the Group case rides
`WorldGroupAnchors`' own smoothed centroid+spread, feeding `Follow`'s
`SpreadPullback`; a `null` anchor resolves the world origin, identity
orientation), compiles `WorldCameraRigCompiler.Compile(cameraRow.Rig,
spread: spread)`, and resolves it — no live orbit composition, no editor
override; a faulted name (resolves no row) renders nothing for that slot
rather than a bogus view. `ResolveSpectatorCamera` is the no-local-seats
safety net: a fixed pull-back over the centroid of the world's authored
`population.seatSpawns`, engaged only when `m_views` would otherwise be
empty (every local seat departed with none rejoined).

## Mouse look (`playerDefaults.seatLook`)

`WorldSeatLook(YawSensitivity, PitchSensitivity, InvertYaw, InvertPitch,
MinPitch, MaxPitch, Arming, WorldAxes)` — presentation-only, one policy for
the whole document (not per-seat). Sensitivities are RADIANS of orbit per
PIXEL of raw pointer motion. `MinPitch`/`MaxPitch` are RADIANS, validated
finite and within `[-π/2, π/2]` with `MinPitch < MaxPitch`. `Arming`
(`WorldSeatLookArming`: `None`/`Always`/`LeftButton`/`RightButton`/
`MiddleButton`) selects what starts/stops the drag; `None` disables orbiting
outright. `WorldSeatLookArming` carries `[JsonConverter(typeof(
StrictEnumConverter<WorldSeatLookArming>))]` at its own declaration — that
converter's `namingPolicy: null` means the context's camelCase
`PropertyNamingPolicy` (which only touches PROPERTY names) does not touch
this enum's VALUE: the wire token is the exact declared member name,
PascalCase (`"RightButton"`, not `"rightButton"`) — confirmed against every
shipped world's `playerDefaults.seatLook.arming`, all `"RightButton"`.
`WorldRowCommandModule`'s own `playerDefaults.seatLook` payload description quotes a
lowercase-first form (`leftButton`/`rightButton`/`middleButton`); treat the
shipped-document casing as authoritative over that description string.
`WorldAxes` is the SAME switch `ResolveCamera` step 3 reads —
selects whether the live orbit composes onto world axes (`true`) or the
seat body's own facing (`false`, the shipped default). `WorldSeatLook
.Default`: `YawSensitivity`/`PitchSensitivity` `0.005f`, no inversion,
`MinPitch: -0.35f` (≈ -20°), `MaxPitch: 1.2f` (≈ 69°), `Arming
.RightButton`, `WorldAxes: false`.

`WorldCameraOrbit` (`src/Puck.World/WorldCameraOrbit.cs`) holds each seat's
LIVE accumulated yaw/pitch offset — `float[PlayerRoster.MaxSlots]` pairs,
`Volatile` read/write, no lock (independent scalars, no cross-field
invariant, distinct slots never alias). Never rides `CommandSnapshot` or
feeds the simulation; `WorldClient.Orientation` (the sim body orientation) is
never written by it. `Nudge` wraps yaw to `[-π, π]` and clamps pitch to the
CALLER-supplied `minPitch`/`maxPitch` — the caller passes the LIVE
`WorldSeatLook` values, so a `world.row.set playerDefaults.seatLook` edit takes effect on the very
next drag.

`WorldCameraOrbitDrag` (`src/Puck.World/WorldCameraOrbitDrag.cs`), an
`IWorldPointerConsumer`, turns a drag into `Nudge` calls: it reads
`m_client.Definition.playerDefaults.seatLook ?? WorldSeatLook.Default` FRESH ON EVERY
EVENT (never cached), so a live `world.row.set playerDefaults.seatLook` mutation is
picked up immediately. Both halves of its answer come from `WorldPointer` —
arming is that seat's live held-button state, the drag distance is that
seat's DRAINED motion — so it tracks no held state of its own. When the drag
is not armed (or `Arming.None` disables it) it still drains the motion and
discards it, so free-cursor browsing is never banked and applied in one jump
the moment a press or a live re-arm lands.

`WorldPointerSink` (`src/Puck.World/WorldPointerSink.cs`) is the ONE
`IWindowInputObserver` the pointer has: it writes every raw pointer event
into `WorldPointer` (position, drainable motion, held buttons, drainable
wheel — per seat) and then drives each registered `IWorldPointerConsumer`. A
new pointer-driven feature registers a consumer; it does NOT add a second
window-input observer. `FocusLost` drops the seat's held buttons (a press
whose release goes to another window would otherwise arm a drag forever) but
never clears the accumulators — motion already reported really happened.
**The mouse rides the KEYBOARD's seat** — the pointer carries no
`InputDeviceId` of its own, so the sink resolves
`PlayerRoster.DeviceSlot(PlayerRoster.KeyboardDevice)` — re-resolved every
event — and `PlayerRoster.KeyboardDevice => default(InputDeviceId)` (the
keyboard is fixed to slot 0 from boot and never leaves), falling back to
slot 0 only in the unreachable case the keyboard is itself unmapped.

Mouse buttons and the wheel are NOT in the binding vocabulary and must not
be added to it (`InputSources` names the keyboard and the gamepad only). The
pointer is a composer of verbs: a pointer act reaches the simulation when a
consumer dispatches an ordinary console verb, never through a private
channel. The wheel accumulator's one registered reader is `WorldWheelFeed`
(the radial action menu, below) — it implements `IWorldWheelConsumer`, which
is what stops `WorldPointerSink`'s own drain-and-discard, and
`WorldPointer.SetWheelConsumerRegistered` REFUSES a second declaration so
the single-drainer contract is enforced, not merely documented.

The DRAWN cursor (`src/Puck.World/WorldCursorFeed.cs` → `Puck.Overlays`
`CursorStore`/`CursorWriter`, `OverlayChannel.Cursor` — the overlay's last,
topmost channel scope, deliberately outside the replace-band suppression) is
the pointer's on-screen echo: per-seat-capable in the store and writer,
single-seat-fed today (the mouse rides the keyboard's slot). The feed reads
ONLY the store's non-destructive state (`Position`/`HasPosition`/
`IsButtonDown`) — the drained motion/wheel accumulators are SINGLE-CONSUMER
and belong to `WorldCameraOrbitDrag`; a second drainer starves it. Visibility
is ONE rule in `WorldCursorFeed.Decide` (known position ∧ inside the seat's
viewport ∧ the seat-look drag not steering). Hover is two tests in draw
order — published HUD panel rects first, then `WorldEditorPicker.TryPick`
aimed down the cursor ray (the same pick program, reused with the
world-authored `hud.defaults.cursor.hoverRadius` reach — see
[hud.md](hud.md) for the authored policy row). All presentation/session
state; echoed by `world.view.pointer`.

**The radial action menu** (`src/Puck.World/WorldWheelFeed.cs` →
`Puck.Overlays` `WheelStore`/`WheelWriter`, `OverlayChannel.Wheel` — drawn
immediately under the cursor, outside the replace-band suppression) is held
binding pages presenting themselves: while the seat's ACTIVE page is some
wheel's hold page (the document's `wheels` rows — see
[documents.md](documents.md)'s "Wheel rows"; the engine default chords every
hold page on `[tab]`, so holding Tab opens the active GROUP's wheel and
letting go closes it), the feed presents that wheel's rings as concentric
shells anchored at the cursor's position at open. The mouse wheel (or the
hold page's bound `player.wheel.ring` rows — Arrow Up/Down, D-pad Up/Down,
the mouse-less parity) cycles which ring is ACTIVE; the cursor's ANGLE from
the hub picks the sector within the active ring (sector 0 at twelve o'clock,
clockwise — radius only decides the hub dead zone and the outside-the-outer-
ring cancel band); Tab's RELEASE edge — bound on the hold page to
`player.wheel.commit`, latched by the very press that turned the page —
commits: the hovered sector's command dispatches through the console door
(`TextCommandSource`, Console-identified, echoing like a typed line, `[player.wheel]`
narration on stderr), and a release over the hub or past the outer ring
cancels (so a bare Tab tap is a cancel). Focus loss mid-hold cancels through
the router's synthesized `Canceled` edge — never commits. Everything is
presentation/session state; the wheel CONTENT is authored document data, and
every sector stays console-dispatchable, so nothing is reachable only by
wheel. Read back with `world.view.wheel`.

The editor's MOUSE manipulation (`src/Puck.World/WorldEditorMouse.cs`, ticked
in the FeedTick chain immediately after the cursor feed) composes that
published decision into click-select and drag-and-drop, and is INERT while
the seat is not editing. It polls the store per frame and derives left-button
edges from per-slot held-state memory — no observer, no consumer, no drain
(a press+release both landing between two produced frames is never observed).
A press dispatches the EXISTING `editor.select` verb through
`TextCommandSource` (the console door — Console-identified, echoing like a
typed line): the hovered row's section+key, or `none` on empty space; a HUD
panel under the cursor makes the press inert. A press on a screen, placement,
or fixed/bed speaker also grabs the row into the EXISTING `WorldEditorDrag`
channel; while held, the pending pre-snap intent follows the cursor's plane
point (the horizontal plane through the grabbed position — world-space
anchors only, the client→frame mapping re-derived every frame via
`WorldCursorFeed.RayDirection`), and a real in-viewport release commits the
channel's ONE whole-row mutation under `PlayerRoster.PrincipalOf(slot)` — the
seat's own acting identity, so grant denials land on the seat and
`NoteRejected` snap-back correlates. A release CANCELS instead when it is
synthetic (`WorldPointer.SystemReleaseCount` advanced since the press — focus
loss, keyboard reassignment), when the cursor stands outside the seat's
viewport (releasing over nothing commits nothing), or when the snapped
position never moved (a click, already answered by its selection). Acts
narrate on stderr as `[editor.mouse] …`; `editor.cancel`/its chord and editor
exit retire the drag externally, and the policy stands down on the next tick.

## The verbs

All in `WorldViewCommandModule` (unbindable) except the wheel verbs, which
live in `WorldWheelCommandModule`. `player.wheel.select` (Axis2D),
`player.wheel.ring`, `player.wheel.commit`, and `player.wheel.cancel` are
Bindable ordinary hold-page destinations; selection/ring/commit/cancel
sources are author data, not hard-coded devices. An explicit cancel latches
for the rest of the open gesture: later frames cannot re-arm it, the hover
accent is suppressed, and release cannot dispatch a sector.

`world.view.wheel [player]` is the Immediate read-back. With no argument it
reads the pointer's seat; `player` selects seat 1–4. An open echo reports
`id=`, group, ring count, active ring and label, authored
`pointer=<Disabled|Angle|HitTarget>` and
`placement=<Pointer|ViewportCenter>` plus
`ringSelection=<Explicit|Excursion>`, hub center in frame pixels, and the
hovered sector with the command it would commit. Excursion is radial distance
from each input device's neutral: Axis2D uses its native magnitude; a spatial
input captures its first available position in the gesture (including after
the opening frame) and normalizes travel using the authored
`spatialTravelFraction`. Hub placement never changes that neutral. A
`HitTarget + Excursion` pointer chooses its ring from neutral-relative travel
but still chooses/qualifies its sector against the displayed annulus. When no
sector is armed, `hover=` carries a stable reason: `no-selection` (no current
selector), `disabled` (pointer selection author-disabled while a pointer
location is available), `dead-center`, `outside`, or `cancelled` (the
gesture's explicit or focus-loss cancellation latch). A closed echo reports
only the selected player and `open=false`.

| Verb | Routing | Effect |
|---|---|---|
| `world.row.set views.seatRig <rig-json>` | Simulation | Replaces `views.seatRig` from an inline `WorldCameraRig`. Applies live: every seat re-frames next frame. |
| `world.row.set playerDefaults.seatLook <look-json>` | Simulation | Replaces the WORLD's control feel from an inline `WorldSeatLook`. Applies live on the next pointer motion — but only for seats sitting at the world's floor: a seat carrying a profile keeps its own feel, which is the point of the per-seat split. |
| `world.row.set views.layouts <layout-json>` | Simulation | Upserts one named `WorldViewLayout` (whole-row, keyed by name). |
| `world.row.remove views.layouts <name>` | Simulation | Removes a named layout — always allowed; the composer falls back to authored/built-in selection. |
| `view.override layout <name\|auto>` | Simulation | LIVE composition override forcing the active layout for every seat; `auto`/`-` clears it. Gated `Control` over `GrantSubject.Composition`. |
| `view.override camera <name\|auto>` | Simulation | LIVE composition override resolving every camera-bearing slot to one camera; `auto` clears it. Same gate as the layout arm. |
| `world.view.state` | Immediate | Read-back: active layout name, selection reason (`override`/`authored`/`builtin`), transition progress, each slot's rect + occupant (`seat<order>`/`cam:<name>`). |
| `world.view.orbit [player]` | Immediate | Read-back of ONE local seat's live orbit: THAT SEAT's control feel in force (its profile's, or the world's when it carries none) PLUS its live drag. `player` is 1-based, default 1, range `1..PlayerRoster.MaxSlots` (4). Two seats can answer differently. |
| `world.view.pointer` | Immediate | Read-back of the drawn cursor's last composed frame: the seat the pointer rides (the keyboard's), position in CLIENT px (`position=`) and mapped into the fixed FRAME extent (`frame=` — the spaces diverge on a window resize; `WorldCursorFeed.Decide` owns the client→frame scale, the inverse of the presenter's stretch blit) and normalized within the seat's viewport (`local=`), the viewport rect, the visibility verdict (`visible`/`no-position`/`no-view`/`outside-viewport`/`orbit-drag` — `WorldCursorFeed`'s one visibility rule), the held pointer buttons (`buttons=` — `L`/`R`/`M` in that order, or `-`), the live hover target (`hover=none`, or the hovered panel/world row's label), and the seat's system-release generation (`syscount=` — `WorldPointer.SystemReleaseCount`, the synthetic-release discriminator). |

**`world.view.orbit` echoes `minPitch`/`maxPitch` and the live `yaw`/`pitch`
IN DEGREES** (`WorldViewCommandModule.DescribeOrbit`, `RadiansToDegrees =
180/π`) — every one of these is held in RADIANS everywhere it is authored or
computed: `minPitch`/`maxPitch` are the document's own `WorldSeatLook.MinPitch`/
`MaxPitch` fields, and the live `yaw`/`pitch` are `WorldCameraOrbit.Yaw(slot)`/
`Pitch(slot)`, radians throughout (not persisted, but never converted before
this one echo). A script that reads this echo and feeds it straight back into
`world.row.set playerDefaults.seatLook`'s inline JSON will be off by a factor of `π/180` — convert
explicitly.

The mutation paths (`world.row.set views.seatRig`, `playerDefaults.seatLook`, `views.layouts`, `world.row.remove views.layouts`,
`world.row.set playerDefaults.seatLook`) carry the acting principal and pass `WorldServer`'s
per-section `Mutate` grant check over `WorldSection.Views` like any other
mutation kind (ordinals 31–33, see [mutations.md](mutations.md)); the LIVE
override verb (`view.override layout|camera`) instead checks `Control` over
`GrantSubject.Composition` — Console can never be denied there today (no
`world.grant`/`world.revoke` grammar names the composition subject, and
Console separately holds `Control` over `GrantSubject.All`), so treat the
check as real for any OTHER principal and inert for Console until the
grammar can name the subject. `world.view.state` and `world.view.orbit`
carry no principal at all — direct reads of live presentation state.

## Capacities and refusals

- `WorldDefinitionValidator`'s private `MaxCameras = 64` bounds the
  document's top-level `Cameras` collection (the named-camera row set a
  layout slot's `Camera` field, and a screen's `WorldScreenSource.View
  (CameraName)`, both resolve against by name) — refused by aggregated
  message (`"cameras count {n} exceeds the maximum of {MaxCameras}."`), not a
  named enum door.
- `src/Puck.SdfVm/Views/ViewStack.cs`'s `MaxRegisteredViews = 64` is a
  DIFFERENT, runtime ceiling — the shared registration pool for offscreen
  content producers a diegetic SCREEN surface samples (`SdfCameraView`,
  `GuestSurfaceView`, `NestedWorldView` — registered by `WorldScreenBinder.cs`,
  reached through `ScreenCommandModule`'s `screen.source <index> view <camera-name>`
  verb). **The seat's own window-layout camera slots
  never touch `ViewStack` at all** — `ResolveNamedCamera` resolves them
  inline every frame. A camera row named in a layout slot and the SAME name
  wired to a screen surface's `View` source are two independent consumers of
  one authored row; only the screen path spends a `ViewStack` registration.
  `ViewStack.RefreshBudget = 4` further caps how many BUDGETED registrations
  actually re-render on any one produced frame (round-robin beyond that) —
  informational here, not a `views`-section cap.
- No cap exists on `Layouts.Count` or a single layout's `Slots.Count` — only
  per-field shape and the referenced camera's existence are validated.
- `ValidateRig` (shared by `views.seatRig` and every `WorldCamera.Rig`)
  refuses, by aggregated message (no `HudRefusal`-style enum door exists for
  this section): a null rig; a non-finite/out-of-`(0,π)` lens FOV; a
  non-finite or negative `SmoothRate`; per motion kind — `Follow`: a
  non-finite offset or `SpreadPullback`; `Orbit`: a non-positive/non-finite
  `Distance`, or a non-finite yaw/pitch/pivot; `Static`: a non-finite
  position; `Track`: missing definition/playback, an undefined clock/
  interpolation/loop enum, fewer than 2 keyframes, a non-finite keyframe
  position, or a non-strictly-increasing tick; an unknown motion kind — and
  per aim kind — `Anchor`: a non-finite offset; `Forward`: a non-finite or
  negative focus distance; `WorldPoint`: a non-finite target; an unknown aim
  kind.
- `ValidateSeatLook` (`playerDefaults.seatLook`, REQUIRED — an absent member is
  refused by name; there is no engine default to fall back on) refuses: a
  non-finite/negative yaw or pitch sensitivity; a non-finite `MinPitch`/
  `MaxPitch`, either outside `[-π/2, π/2]`, or `MinPitch >= MaxPitch`; an
  undefined `Arming` value.
- `ValidateViews` (layouts) refuses: a null layout row; a missing or
  duplicated layout `Name`; a negative `SeatCount`; a non-finite/negative
  `TransitionSeconds`; a non-finite `TransitionRenderScale` outside `(0,1]`;
  zero slots; a slot rect outside `[0,1]` with non-positive extent (1.0001
  tolerance on the upper bound); a slot's `Camera` naming no row in the
  document's own `Cameras` set.

## Verifying

Presentation-only — verify by RUNNING `Puck.World` (`CLAUDE.md` rule 3),
never a gate. `world.row.set views.seatRig`/`world.row.set playerDefaults.seatLook` apply live and echo through
`world.view.state`/`world.view.orbit` on the next tick (the stdin drain
barrier serializes a following Immediate read, no polling needed). A minimal
session:

```
world.row.set views.seatRig {"motion":{"$type":"orbit","distance":6,"yaw":0,"pitch":0.3,"pivotOffset":[0,0,0]},"aim":{"$type":"anchor","offset":[0,1,0],"worldAxes":false},"lens":{"fieldOfViewRadians":0.96},"smoothRate":6}
world.wait 1
world.view.state
world.row.set playerDefaults.seatLook {"yawSensitivity":0.005,"pitchSensitivity":0.005,"invertYaw":false,"invertPitch":false,"minPitch":-0.35,"maxPitch":1.2,"arming":"RightButton","worldAxes":false}
world.wait 1
world.view.orbit 1
```

Every `Vector3` field (`pivotOffset`, `offset`, `position`, `target`) is a
three-element `[x, y, z]` ARRAY (`Vector3JsonConverter`), never an `{x,y,z}`
object — the converter throws `"a Vector3 must be a three-element [x, y, z]
array."` on the object form.

Confirm `world.view.orbit`'s `minPitch`/`maxPitch`/`yaw`/`pitch` are DEGREES
(divide by ~57.3 to compare against the radians just authored). Exercise a
refusal control: `world.row.set views.seatRig` with a negative `smoothRate` or a
`distance` of 0 refuses and leaves the document unchanged (re-read
`world.view.state`/screenshot to confirm nothing moved); a layout slot naming
an undeclared camera refuses the same way. For the orientation trap, author
an `Orbit` seat rig with `worldAxes:false`, turn the avatar (`player.press`
a turn channel or drive it), and screenshot before/after — the chase should
swing WITH the turn (body yaw composed in); a `WorldCamera` row using
`Orbit` motion, by contrast, never moves when the anchored entity turns,
because `ResolveNamedCamera` never composes body yaw at all.
