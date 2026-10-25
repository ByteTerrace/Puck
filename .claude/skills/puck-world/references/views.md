# Views — authored cameras and seat control

The `views` section owns camera structure. `seatDefaults.seatCameraFeel`
(`WorldSeatCameraFeel`) owns portable human input preference and the player's clearance choice. The split is
strict: pitch limits and axis framing belong to `views.seatControl` and the rig,
and the feel carries neither (strict parse refuses any unmapped member).

Primary code:

- `Puck.World.Schema/WorldViews.cs` — `WorldViewDefaults`,
  `WorldSeatViewControl`, layouts and slots.
- `Puck.World.Schema/WorldCameraProgram.cs` — the authored op vocabulary and
  its subject union. Authoring only: it parses and validates, and knows nothing
  about how a frame resolves.
- `Puck.SdfVm/Views/SdfCameraProgram.cs` — the compiled IR, the per-frame
  evaluator, the `ISdfCameraRig` adapter, and `SdfCameraBoomFollower` (the
  second-order boom ease, over `Puck.SdfVm/Views/SecondOrderFollower.cs`'s
  float twin). Parses no document and references no `Puck.World*` project.
- `Puck.World.Client/WorldCameraRigCompiler.cs` — the translation: authored ops
  to IR, authored subjects and `state.<row>[.<key>]` bindings to per-frame
  slots this rig refills from the live document.
- `Puck.World.Schema/WorldSeatCameraFeel.cs` — portable input preference.
- `Puck.World.Client/WorldSeatViewState.cs` — the one live state per occupied
  seat, including yaw/pitch, the cached compiled rig, and the boom follower.
- `Puck.World.Client/WorldCameraClearance.cs` and `WorldChaseBody.cs` — where the
  chase eye stands: a per-seat spring arm out of the static field and out of the
  framed body's kit collider and drawn shapes (see "Chase eye clearance" below).
- `Puck.World/WorldSeatViewInput.cs` — the pointer adapter that turns the seat camera; `WorldDragMotion.cs` decides what each pointer event's motion does to a drag (counted from the press, cut at the release).
- `Puck.World.Client/WorldFramePresenter.cs`, `WorldAdjacencySceneEmitter.cs`, and
  `WorldContinuum.cs` — local and neighbouring-authority render callers of the
  same seat state and generation-addressed continuum.
- `Puck.World/WorldViewCommandModule.cs` — read-back and composition verbs
  (`world.view.camera` itself lives in `WorldSeatCameraCommandModule.cs`).

## Document shape

The engine declares no rig of its own: `views` is REQUIRED exactly when the
census implies a body (`bodies.capacity > 0`), the same derived refusal
`kits` carries, and a seatless document may author none. The live seat camera
is authored in the Moth basis (`worlds/debug/avatars/moth.puck`),
which the debug world and the garden inherit:

```text
views {
  // The seat's own camera comes first, so it is what a boot shows; the named inspection cameras follow.
  layout "play" {
    seatCount: 0
    slots [ { height: 1, width: 1, x: 0, y: 0 } ]
    transitionRenderScale: 1
    transitionSeconds: 0
  }
  cameraLayout("three-quarter")
  // … more inspection layouts …

  seatControl {
    clearanceMode: Fade
    characterFade { startDistance: 1.25, endDistance: 0.45, minimumBoom: 0.35 }
    clearanceMaxElevation: 1.1
    maxPitch: 1.2
    minPitch: -0.5
    yawReference: "World"
  }

  seatRig "moth-camera" {
    version: "puck.camera.program.v1"
    operations [
      orbit(distance: 4.2, opcode: "orbit", pitch: 0.3, pivotOffset: [0, 1, 0], yaw: 0)
      lookAt(focusDistance: 4.2, opcode: "lookAt", subject: reference(), targetOffset: [0, 1, 0], worldAxes: true, closeTargetOffset: [0, 1.74, 0], closeTargetStartFraction: 0.6, closeTargetFullFraction: 0.2)
      fieldOfView(fieldOfViewRadians: 0.96, opcode: "fov")
      dynamics(row: `camera-boom`)
    ]
  }
}

seatDefaults {
  seatCameraFeel {
    invertPitch: false
    invertYaw: false
    pitchSensitivity: 0.001
    stickLookRate: 3.5
    yawSensitivity: 0.001
  }
}
```

The document members are `views.layouts`, `views.seatControl`, `views.seatRig`
and `seatDefaults.seatCameraFeel` (C# `WorldDefinition.PlayerDefaults.SeatLook`,
a `WorldSeatCameraFeel`). The console's row path for the feel is
`playerDefaults.seatLook` (`world.row.set playerDefaults.seatLook <json>`), and
`world.view.camera` reads the effective feel and control back. An absent feel
is `WorldSeatCameraFeel.Default`: zero pointer and stick response, so every look
input turns the camera by nothing. The camera boom's smoothing is the
`camera-boom` row of the world's `dynamics` table.

The first authored layout is what a boot shows; `view.override layout <name>`
selects another. Grammar for the `.puck` forms above belongs to `puck-dsl`.
`orbit`/`fieldOfView` accept the named arguments shown; `orbit`'s first three
positional arguments would name `distance`/`pitch`/`yaw` in that order. Every
other op is written by name only.

What arms a pointer drag is a binding, not a feel: `player.orbit` (held —
pointer motion orbits the camera) and `player.steer` (held — orbits AND the body
faces where the camera looks) are bound on both edges like any held command
(a press row plus an `activateOn: Completed` row on the same source). The engine
binds nothing by default: the Moth basis's `bindingOverlays` bind
`mouse.button1 → player.orbit`, `mouse.button2 → player.steer`, the right stick
to `player.look.steer` and R to `player.look.recenter` (the pad's recenter is
North on the bar's View page, `controls.puck`). `player.steer` writes the camera's
facing into channels claiming the `FaceX`/`FaceY`/`FaceZ` roles and the sim's
facing snap turns the body, so binding it needs those three channels declared and
`seatControl.yawReference: World` (the validator refuses otherwise).

`seatControl.follow` (`{ "rate": <per second>, "whileIdle": false }`, optional) makes
the camera yaw ease in behind the body's heading when there is no look input:
`rate` is the exponential closing rate (about 63% of the remaining angle per
`1/rate` seconds; larger is stiffer) and the validator refuses a rate that is not
positive. Any look input (a deflected look stick, a held orbit or steer) is
free look and the follow yields while it lasts. By default the follow runs only
while the body has movement input, so after a free look the camera stays where
it was left until the body moves; `whileIdle: true` also follows an idle body.
The validator refuses `follow` unless `yawReference` is `World`, because a
body-relative yaw already rides the body. Absent `follow` is a still camera that
goes only where look input sends it.

`seatControl.yawReference` is `World` for standard camera-relative movement or
`Body` for an explicitly body-relative camera. A mapped arrival turns a followed seat's
view by the door's turn (`WorldSeatViewState.Cross`): a `World` yaw turns, a
`Body` yaw already rides the turned heading, and the chase boom turns either way.
A traveler handed on by another process's authority turns the seat by the change
in its accumulated arrival turn (`WorldFrameIsometry.AccumulateTurn`), which
commits and routes carry and `WorldRoutedSeatTurns` holds per seat. Pitch values are radians,
finite, ordered, and within `[-pi/2, pi/2]`.

`seatCameraFeel` carries pointer radians-per-pixel, right-stick radians-per-second,
gyro projection, and inversion. `gyro.deadZone` and `invertX/Y/Z` act on each
physical axis independently; the dot products against the full 3D `yaw` and
`pitch` weight vectors then produce semantic look-right/look-up rates. Thus all
three axes can participate, be combined, or be remapped without code changes.
A joined identity's preference travels; otherwise the routed world's default
applies. Pitch limits and yaw reference remain the routed world's; the feel's
`clearanceMode: Auto|Fade|Cutaway` may override its clearance choice. `Auto`
inherits `views.seatControl.clearanceMode` (which admits `Fade` or `Cutaway`).
`player.camera.clearance fade|cutaway|auto` sets a temporary choice for the issuing
seat; no argument, or `toggle`, flips fade/cutaway. `auto` restores the document
or identity preference. It changes no logical yaw, pitch or movement state.

Motion input is a separate, generic toggled mode: `player.motion.controls`
toggles it (it stays active after the buttons release) and
`player.motion.angular` feeds the gyro's angular velocity through the feel's
gyro projection. The command is intentionally not gyro-named so a later
orientation/tilt-to-move adapter can share it. `player.look.free` holds free
look: right-stick yaw/pitch continues to orbit the camera, but yaw does not
write body heading and left-stick movement resolves against authoritative
character heading until it releases; the hold also suppresses automatic camera
follow. No live world binds these three; the quarantined standard profile bound
them to `LT` chords.

Binding contexts can derive a complete control group from gameplay state. A
family named `state:<row>` reads the routed world's declared state row after a
delivered revision: scalar rows publish one value to every seat, while keyed
rows read the controlled body's entity-index cell. Numeric states use their
canonical author-facing spelling (`0`, `1`, `12.5`, `true`, `false`); text uses
its exact value. Continuously advancing rows are refused as control contexts,
so a mapping changes only through an explicit, journaled state write. World
rules can therefore select controls with ordinary `setState`/`addState`
effects, and `player.bindings` reports the resulting family/state/group winner.
If a portable profile reaches a world that does not declare its referenced state
row, that family contributes no match and normal context precedence continues.
Changing the winning group is a complete input boundary: held commands, toggles,
chord arms, and page latches from the previous group are cleared before the new
group can accept input.

## Runtime ownership and order

`SeatController` constructs its `WorldSeatViewState`, so leaving a seat drops
the state and a later occupant cannot inherit it. There is no process-global
orbit table, binding-side feel cache, or renderer-local orbit/smoothing copy.

For each fixed tick:

1. Routed stick look and toggled gyro angular velocity are combined into one
   semantic look-rate sample and integrated once into the seat state. Gyro
   dead-zone, physical-axis inversion, and 3D projection happen before the
   existing final semantic yaw/pitch inversion.
2. `WorldClient` rotates left-stick movement through that logical yaw when the
   selected kit authors `moveFrame: World`.
3. Standard `player.move.strafe` preserves heading, and the kit disables
   movement-facing attitude snap, so lateral left-stick input is a true strafe.
   It resolves against live look yaw every tick, so forward travel turns with
   the right stick; `player.move` remains the latched movement-facing alternative.
4. `player.look.steer` writes camera yaw to `FaceX`/`FaceZ` after movement
   composition, turning an upright body while vertical input remains camera
   pitch; `player.look` remains camera-only free orbit.
5. The ordinary intent roles cross the wire; camera state never does.
6. Local or traveler rendering resolves the authored rig through the same
   seat state. Visual smoothing and the chase eye's clearance never feed
   movement.

## Chase eye clearance

### First person

First person is a seat-owned view state, independent of the fade/cutaway clearance
choice. `player.camera.view first|third|toggle|auto` requests an eased move into
the authored eye or back out; no argument toggles. `auto` restores the program's
request and the optional automatic policy. The composed controller-bar pages
have no free plate (Debug South is the Suit toggle), and every play-wheel ring
already fills its eight sectors, so this bindable verb has no default binding.
An author may assign it through the ordinary control vocabulary.

The camera program reports its destination through
`firstPerson(eyePoint: [0, 1.75, 0.22], active: 0, transitionSeconds: 0.5,
hideRadius: 0.45)`. The Moth basis owns this eye point. `active` may be a
`state.<row>[.<key>]` binding: zero requests third person, one first person.
An explicit player view choice overrides it. Selected programs carry the winning
destination; a blended program carries the dominant side's destination. The
point rides the presented body's orientation and live scale. The transition
migrates the chase pivot to that eye while retracting the boom, preserves yaw,
and keeps a nonzero forward sightline at the eye. Reversing the toggle continues
from the current fraction. The FOV stays the rig's authored FOV.

`WorldCameraViewTransition`, held by `WorldSeatViewState`, owns the request,
smoothstep transition and automatic dwell. The existing `SdfStampFade` hides
only the issuing seat's own body stamp within the authored head shell, fading
over the preceding shell radius. No shared stamp transform changes; other seats,
mirrors, portals and carried screens retain their own visibility. First-person
look rotates at the eye, keeps the body upright when pointer steering, and
suppresses follow; it uses the same authored look/orbit/steer inputs.

Automatic pull-in is **off by default**. Author
`views.seatControl.autoFirstPerson { enabled: false, entryDistance: 0.85,
entrySeconds: 0.2, exitDistance: 1.35, exitSeconds: 0.15 }`; distances scale with
the body. The nullable portable `seatCameraFeel.autoFirstPerson` overrides the
author's enabled choice. `player.camera.automatic on|off|auto|toggle` overrides
that choice for the session; `auto` removes the override. Explicit `view first`
and `view third` win over automatic pull-in. Use `view auto` to let it act.

The room probe starts at the authored eye and runs backward along the requested
view direction, independently of the current transition and chase target shift.
Entry needs continuously less than 0.85 m for 0.2 s; exit needs continuously more
than 1.35 m for 0.15 s. Intermediate clearance retains the current state and
interrupting either dwell resets its timer. `world.view.camera` echoes `view`,
`viewSource`, `transition`, `autoFirstPerson`, `room` and `dwell`, alongside the
actual presented eye/target, fade and third-person clearance result.

The debug world's default is **fade**, with **cutaway** one toggle away. Both
share target migration: the camera program's `lookAt.closeTargetOffset` names
the close target, in the same axes as `targetOffset`. As static geometry reduces
the authored boom from `closeTargetStartFraction` (0.6) to
`closeTargetFullFraction` (0.2), a smoothstep blend approaches that target and
eases on the program's `dynamics`. The Moth authors waist `[0, 1, 0]` and close
target `[0, 1.74, 0]`. The displacement, boom, collision radii and fade distances
scale with her live scale. Blend/select programs carry the winning look-at's
migration. Compression is measured at the rest pivot, independently of the
migrated target, to avoid a feedback oscillation under lintels.

**Fade keeps the room.** The boom shortens along the requested heading and pitch,
never rises automatically and never advances the near plane through a wall.
`seatControl.characterFade` authors `startDistance`, `endDistance` and
`minimumBoom` (1.25, 0.45 and 0.35 m full-size by default). The preferred minimum
boom yields to closer geometry. The own body becomes a 4×4 screen-door pattern
from solid at the start to hidden at the end. `SdfViewSnapshot.StampFade` carries
only that body's transform-slot range and opacity; the mesh and glass fragment
passes discard before writing depth or glass sums. Shared stamp transforms and
shadows stay intact. Mirrors, portals, named cameras and other seats construct
independent view snapshots, so they still see the whole Moth. Attached screens
own separate stamp slots and stay visible.

**Cutaway keeps the body.** The following keep-out/rise/near-plane procedure is
the cutaway branch. The Moth stays solid even when no room remains behind her.
`seatControl.clearanceMaxElevation` authors its elevation ceiling (1.1 rad by
default). The live read-back includes `mode=fade|cutaway` and `fade=0..1` (zero
means solid) alongside the eye, migrated target, boom, rise, near and clearance.

After the boom ease, a seat framing its own body through `views.seatRig` places
the eye on its own spring arm (`WorldCameraClearance`, owned by the seat's
`WorldSeatViewState`, `Clear`), in the world the seat is presented in only; a
seat presented elsewhere or framing through `views.cameraRig` keeps the eased eye
and drops the arm's state. The eye is a sphere of `EyeRadius` (0.3 m) kept out
of two things: the presenter's static query field (`WorldClient.StaticField`,
the solid placements, screens and adjacency, never a body), and the framed body
itself (`WorldChaseBody`): the perceived body's kit `collider`, compiled once per
delivered definition, and the bounding sphere of every shape its creation draws
that adds material (`WorldChaseBody.DrawnVolumes`, rest pose, a shape that only
cuts or clips adds none), both posed at the drawn pose and scaled by the body's
live scale. The drawn shapes count because her armour, helmet and pack reach
past the collider, and an eye inside one starts its view inside her. The target
sits inside the body, so the body's keep-out is how far along the boom the eye
must travel before it leaves the inflated volumes.

Each frame answers where the eye belongs:

1. The boom sweeps out from the target; geometry shortens it along its own
   direction, so long as the shortened eye still clears the body.
2. When no length of the boom clears both (a body backed into a wall or a
   corner, its boom pointing in), the boom rises toward the seat's up on the
   same heading: a coarse scan up to `LiftCeiling` (1.1 rad, short of an
   overhead view that turns her upside down on screen), then bisection, finds
   the lowest rise where it clears.
3. When no rise clears both (her back against a wall), the answer is up and
   over at the ceiling, just outside the body; the eye may then stand in or
   beyond the wall, so the camera's near plane (`WorldCameraClearance.Near`,
   composed into the snapshot's `Near` by `WorldFramePresenter`) begins the
   image at the wall's face on her side, a cutaway that stops short of every
   part of her in view (`WorldChaseBody.NearestDepth`, her volumes' nearest
   depth along the view, not only on the sightline). The same cutaway covers
   the frames an eased rise lags behind an obstruction.

The arm does not jump to that answer. It holds a fraction of the authored boom
and a rise, never a length, and eases both toward each frame's answer on the
chase rig's own `dynamics` row (the `camera-boom` row in the debug world; no
dynamics op lands on the answer at once). A pull-in and a rise are therefore
smoothed, and once the obstruction clears the arm eases back out to the authored
boom rather than keeping the pulled-in length. Two limits hold on the eased
value every frame: the eye's centre never comes nearer than `SurfaceSkin`
(0.1 m) to geometry along the boom, so a sudden obstruction cuts the eye in to
there and the ease carries it the rest of the way, and the eye never comes
within its radius of the body; the body wins where the two conflict.

The heading never changes: the frame the seat steers in stays the frame it sees.
A target inside geometry (a portal frame around the pivot) skips a 1.1 m shell
before sweeping and keeps the authored boom where that shell reaches past it.
The eye radius, skin and scan are engine constants; the ceiling is authored; the
body volume is the kit's authored collider and the creation's own shapes; the
ease is the rig's authored dynamics. `world.view.camera` echoes the arm's latest
frame (`eye=`, `target=`, `boom=<placed>/<authored>`, `rise=` in degrees,
`near=` in metres, `clearance=open|shortened|risen|crowded`). `WorldCameraClearanceLawTests` holds
the keep-out and the ease back out on the CPU.
   A frame between two ticks presents the last tick's look step at the
   fraction the body is drawn at (`WorldSeatViewState.PresentedLook`, fed by
   `BeginTick`/`CompleteTick` around step 1), so a steered body and the
   camera turn together instead of the camera leading by a tick in tick-sized
   steps. A pointer nudge between ticks shows at once; a recenter or crossing
   cuts the step. The chase anchor is the body's interpolated render pose, the
   pose a look without root `dynamics` draws the body at; a look that adds root
   dynamics draws the body behind the anchor the camera frames.

The standard right stick binds `player.look.steer`: horizontal input writes
`FaceX`/`FaceZ`, vertical input only changes camera pitch, and neither writes
`Turn`. Authors may bind `player.look` for free orbit, explicit bindings may
still write `Turn`, and a drive kit may interpret its left-stick roles
according to their own motion program. In the debug world the left-stick
press (and N) toggles the `run` channel, West and Left Shift hold it, and the
left trigger's page turns the stick presses into autorun and autofly
([the Moth's controls](../../../../worlds/debug/avatars/moth.md#controls)).

## Camera programs and layouts

A camera rig is an authored PROGRAM — `{ name, version, operations }`, an
ordered op list, the same shape `bodyMotionPrograms` uses for sim-side movement.
There is no motion/aim/lens kind union: a new framing is a different op list,
never a new engine type. `version` is `puck.camera.program.v1`
(`WorldCameraProgram.CurrentVersion`); the op-count ceiling is
`WorldCameraProgram.MaxOperations`.

| op | does |
|---|---|
| `anchor` | sets the current SUBJECT and re-seeds the eye at it. At most one, and it must lead. Subject is `reference` (the pose the caller hands in), `placement` (a stamped placement transform, position only), or `worldPoint`. |
| `offset` | places the eye at `value` from the subject — in the subject's own axes unless `worldAxes`. `spreadPullback` widens it by the group spread (only meaningful when the caller's reference is a `group` anchor). |
| `lookAt` | aims. `subject: null` looks along the current subject's forward at `focusDistance`; a subject aims at its pose plus `targetOffset`. |
| `orbit` | places the eye by orbiting the subject at `distance`/`yaw`/`pitch` about `pivotOffset`. At most one. On `views.seatRig` the seat's live look adds to yaw/pitch; everywhere else the authored angles render unchanged. |
| `path` | sets the current SUBJECT to a point sampled from a named `curves` row (see documents.md) by arc-length `fraction` (bindable), facing the curve's own tangent, and re-seeds the eye there. `anchor`'s subject-seeding role; at most one, and it must lead (refused together with `anchor`). No `rate` field — a constant-rate dolly binds `fraction` to a `state` row carrying the `advance` trait. |
| `clampPitch` | bounds the pitch a later `orbit` resolves with, live delta included. At most one, and it must precede the orbit. |
| `fieldOfView` | the rendered vertical FOV, radians (`fieldOfViewRadians`). Every program needs one (or a `blend`/`selectProgram` that reaches ones that do). Bindable: a literal, or `state.<row>[.<key>]`. |
| `dynamics` | names a `dynamics` row (see documents.md); the resolver REPORTS the response, the caller applies it as a second-order boom ease (`SdfCameraBoomFollower`). No op is no ease — the boom passes through untouched. At most one. |
| `blend` | lerps two other programs by NAME (eye, target, field of view, and dynamics — component-wise when both sides are live, otherwise whichever side is live) at `weight`, itself bindable. At most one. |
| `selectProgram` | evaluates exactly ONE of several other named programs, chosen by a live `key` (a literal, or the intended use, a `state.<row>[.<key>]` binding) rounded to the nearest whole number and matched against `cases` (each a `{value, program}` pair; at most one case per value); `default` names the program used when no case matches. The winning program's eye, target, field of view, and dynamics response pass through unchanged — no lerp, the discrete-switch counterpart of `blend`'s continuous mix: a rule-advanced integer row picks a whole framing outright rather than easing toward one. At most one per program; refused when it would create a reference cycle. |

The blend namespace is the whole document's program table: every
`cameras[].rig`, plus `views.seatRig` and `views.cameraRig`. `selectProgram`
resolves its `cases`/`default` names against the SAME namespace. A dangling
name, a reference cycle (carrying its trail — `blend` and `selectProgram`
share one cycle check), and a program name declared twice in that namespace
are all refused by name.

`views.seatRig` must contain an `orbit` op: `seatControl` declares a live
yaw/pitch band, and only an orbit can express it. `views.cameraRig` — the
first-person framing a camera control application resolves through — must author
none of `orbit`, `offset`, or `path`, because it sits exactly at the possessed
camera body's own pose.

Named `cameras` resolve through authored anchors independently of the seat
state. `views.layouts` maps normalized slots to joined seats, named cameras,
or `views.graphs` instances (`instance`; a slot names at most one of `camera`
and `instance`).
An empty list uses the built-in one-to-four-seat ladder. Layout transition
duration and the mid-transition render-scale dip (`transitionRenderScale`)
remain authored on each layout; a view's durable render quality is the
`views.quality` rows (`WorldViewQuality`: a render view's `name`, or `*` for the
player-view defaults, with `renderScale`, `renderScaleFloor` and `tier`; session
pins have no document member). A layout whose
`seatCount` no joined-seat count can reach (5+) is selectable only through
`view.override layout <name>` — the authoring shape for an override-only view.
Under a camera-only layout, a joined seat the layout binds no seat slot to
falls back to the first camera-bearing slot's region and camera
(`WorldFramePresenter`), so the cursor, the radial wheel, and pointer
unprojection ride the view the player is looking at. The composer's active
selection also publishes the `layout` context family every tick, so a world
can flip a seat's binding group with the view (see documents.md, context
rows).

`views.graphs` rows are frame-graph instances (`WorldViewGraph`): a `name`, a
`source` (a `puck.render.graph.v1` document, a one-off `.hlsl` read as a
one-pass graph, or a `puck.shader.package.v1` directory) or an engine
`package` producer (such as `sdf.world`), a camera, a `refresh` of exactly one
positive `divisor` or `hertz`, `inputs` binding a graph's external version to
another row, the per-instance `timeScale`, `output` and `overrides`
(documents-render.md owns those three), and a `tier` (`low`, `medium`,
`high`, `QualityTier`, written bare in `.puck`), which selects the source's
package variant and never what it reads, so it moves no manifest, mirror or
state hash; a package row takes none. The validator
(`WorldDefinitionValidator.Graphs.cs`) refuses a loop of same-frame inputs
through `RenderGraphInstanceSet.TryCreate`, naming every instance; a self-input
and a `previousFrame` input are legal. `views.graphBudget.passPixelsPerFrame`
is the scheduler's price ceiling, and `bytesPerTick` and `bytesPerFrame` cap
the bytes every bound parameter together owes its pass (`WorldBindingCost`,
priced from the document alone), refused naming the graph and the binding that
crosses one. `world.budget` echoes every row with its
extent ceiling, rate and planned passes (`WorldPresentationCost`, priced by
`WorldPipelineSources.PlanGraph`) and every binding with its bytes per tick and
per frame, then the live budget: what the runtime's latest
schedule decided for every instance, with its extent, divisor, passes,
pass-pixels and its newest completed submission's counts (`RenderGraphLiveBudget`).
`views.root` names the row the display
shows; absent, the host synthesizes the root graph (`WorldRootGraph`), which
places every instance a layout slot names over the SDF world, then runs each
`views.post` row (`WorldViewPostPass`: `name`, `package`, `config`, a graph
document's package pass less its ports) as a pass of its post-process package
in order, and a windowed host draws the overlay in its own instance over
that scene (`WorldRootGraph.OverlayInstance`). A post row's `package` must be a post-process
package the host's catalog offers (`WorldPostProcessVocabularyHook`, such as
`sdf.film-grain`), its `name` must differ from every `views.graphs` row's, its
`config` binds against its package's schema when the document validates
(`WorldPostProcessVocabularyHook.PostProcessConfigCheck`, naming the row), so a
live edit is refused as a boot is, and a world naming `views.root` authors none.
`views.post` is edited live: `world.row.set views.post <json array>` sets the
whole ordered list (`WorldMutation.SetViewPost`), so a pass is added, reordered
or removed by writing the list, and the host recomposes the running root from
the rows the document names then. A probe parameter's `post` target
writes a row's float config field live (`WorldPostPasses`). A pane the
active layout does not show is not scheduled. The world's capture composes
cameras and places the current layout before scheduling that same frame. View
and pane allocations reserve the largest extent of the transition in flight when
its endpoints nest, including its spectator fallback, keep the start's extent when
they oppose (growth on one axis, shrinkage on the other), and retain reservations
through interruptions until the chain settles. Settled footprints request the
occupant's own rect, subject to scheduler quantization and shrink hysteresis.
Easing changes placement without rebuilding a node, and a transition allocates at
most once. The `rendering` skill owns the graph document, the scheduler
and the host (`WorldViewGraphHost`).

## Pointer, cursor, Free Cam

`WorldWindowInputFilters` is the one window input filter capability
(`IWindowInputFilter`): the pump offers it every raw event before the observer,
and it offers each event to `WorldAnnotationInput` and then
`WorldSourcePassthrough` until one consumes it. A consumed event (a pointer
button or relative motion in annotate mode, a key a focused passthrough source
takes, a click or wheel over a passthrough pane) never reaches the observer,
the held-input reassertion or the command router. Only the local user opens a
passthrough source: `source.passthrough open`, run as typed text from the
host's own console, or their own press on an engaged window capture's framed
picture (a physical click, or a local pad's bound press, which
`WorldPassthroughPad.IsLocalPad` admits); never add a document field, a
scripted door or another principal's door to it. A binding page may only name
which local pad control means which pointer act. A new raw-event consumer that must withhold events from the game
joins that fan-out rather than registering a second capability.

Annotate mode (`WorldAnnotations`, toggled by `view.annotate`) is a
window-wide presentation mode, not a seat mode: nothing reaches the
simulation. While it is on, `WorldAnnotationInput` consumes every pointer
button and relative motion, so no click reaches a binding or the pointer store
and no drag orbits or steers; a left drag keeps the ellipse fitting its box in
normalized client coordinates (the display stretches the client, so they are
also normalized overlay coordinates), a click keeps a dot ring, and a right
click clears. A release whose press it consumed is consumed even after the mode
ends, and one it did not see passes on, so no button sticks on either side.
The absolute position always passes on. `AnnotationWriter` draws the outlines
in the overlay's `Annotation` channel (after the editor, before the wheel and
cursor, unclipped), as ellipse records of the ring element kind
(`OverlayFrameBuilder.WriteEllipse`: word 3 the vertical radius, word 10 the
stroke half-width). `player.signal` hands a synthesized mouse event to the
observers, not the filters, so a scripted drag cannot draw; `view.annotate
add` keeps an outline from text instead.

`WorldPointerSink` is the one window observer. `WorldSeatViewInput` drains
motion only for camera steering, through `WorldDragMotion`: the router applies
a drag's press and release at the next fixed tick, so the gate keeps the motion
since the latest pointer press while a button stays down, the
`player.orbit`/`player.steer` press edge turns the camera by it
(`WorldDragMotion.Hold`), and a release that leaves no button down cuts the
motion before the router applies it. `WorldCursorFeed` asks that same adapter whether steering is
active, so pointer consumption and cursor visibility cannot disagree. This is
the presentation projection: the same relative motion, wheel, and button
events independently enter `Puck.Commands` through `InputSources.Mouse`. The
absolute cursor position has no command source of its own; it reaches the
simulation only as the pointer ray `WorldPointerRayCapture` casts from it
through the seat's camera, sustained as the `source.pointer.*` commands on the
lane of the seat that holds the mouse which moved it. The window attributes
each position to the last physical mouse that raw input saw move (the aggregate
cursor's default id when raw input names none), `WorldPointer` records that
seat and device, and a seat that does not hold that mouse, a device moving
seats (`PlayerRoster.DeviceSlotChanging`) and the pointer leaving the window
(`WindowInputKind.PointerLeft`) all end the ray and hide the cursor until the
next reported position.

A held `player.orbit`/`player.steer` therefore turns the camera from the
pointer store, never from a routed `mouse.motion` command — no shipped page
binds `mouse.motion` outside the radial. The scripted twin honours that split:
`player.signal mouse.motion <dx> <dy>` (pixels, unclamped), `mouse.wheel`, and
`mouse.button<n> press|release` synthesize the raw `WindowInputEvent` and hand
it to the window-input observers before the router, exactly as the window pump
does, so a piped drag orbits and steers like a physical one; every other source
enters the router alone. The router half addresses the issuing seat's lane
(`InputSignal.Slot`, seat 1 from stdin or MCP) instead of resolving one from a
device: every synthesized signal shares the default device id, and the roster's
seating policy would put that device on the first seat holding no device of its
kind, so a seated physical pad would send a scripted stick to seat 2. A held
synthesized stick or key is carried until its zero or release sample, or until
OS window focus loss releases every held input. `world.view.camera` echoes `orbit=`/`steer=` for the
two holds. A synthesized pointer never reports an absolute position, so
`world.view.pointer` keeps answering `reason=no-position` under a scripted drag.

Free Cam is a POSSESSION, not a second camera integrator. A `seatModes` state
whose `target` is `"camera"` makes the seat possess its own authored
`camera-seat-<0-based slot>` inhabited placement through the ordinary Engage
door: the seat's own body intent diverts to Idle, and the camera body's pose
becomes what the seat perceives, sees, and hears through. Its view resolves
through `views.cameraRig` — the same compiler and evaluator every other program
uses — and leaving the state disengages, restoring the seat's own body. A
document authoring a camera-targeting state without a `views.cameraRig` or
without an inhabited `camera-seat-` placement is refused by name.

`player.camera [seat]` is the bindable no-token toggle a wheel sector or a pad
chord fires: it resolves the seat's own camera-targeting family/state from the
routed document and flips between it and the family's default, running the same
authority check and Engage/Disengage path `player.mode` does. Named cameras and
Free Cam do not alter the logical movement basis.

## Verbs

- `world.view.camera [player]` — reads the routed structure, portable
  preference, held free-look state, motion-control gate/sample, the exact
  live yaw/pitch state used by movement/rendering, and where the chase eye's
  arm last placed the eye (see "Chase eye clearance").
- `player.camera.clearance [fade|cutaway|auto|toggle]` — changes the issuing
  seat's temporary clearance preference; no argument toggles the two modes.
- `world.view.state` — reads active layout, selection reason, transition, and
  slot occupants; an instance slot prints as `instance:<name>`, or
  `instance:<name>:missing` when the runtime has no such instance.
- `world.view.pointer` — reads the seat the pointer rides (the seat of the
  device the latest position is attributed to, `WorldPointer.Positioned`,
  seat 1 with `reason=no-position` after the pointer left the window or a
  device changed seats), pointer position,
  viewport mapping (`WorldSeatViewports.Locate`, which the pointer-ray capture
  shares), visibility, arming reason, buttons, hover (a HUD panel, else the
  display pane the picker hovers), and system-release generation.
  The host Console can supply `<x> <y>` or `clear` for inspection. Both GPU
  shapes resolve that point through the published panes or whole-display view,
  even without a drawn HUD cursor; offscreen coordinates are display pixels.
- `world.view.pick <instance> [<x> <y>]` — the presentation automation seam to
  an SDF view's GPU pick. With normalized coordinates in [0, 1) it requests a
  pick at that point of the instance's image and echoes the request id as
  `pending`; without them it echoes the latest result (`request`, `pixel`,
  `kind`, `source`, `material`, and the hit's `placement`, `body` and `slot`,
  each `none` when absent) or `pending`. It refuses an instance that is not a
  rendered SDF view. A mesh pixel's source indexes the dressed draw list
  (`SdfMeshPathBake.Dress`: the host's draws, one draw per baked group, then the
  portal-straddle copies), which `SdfDressedPickMap` resolves: a baked group's draw
  names its first instance's placement or body, a copy names what it was copied from.
- `world.patrol <x0> <z0> <x1> <z1> <step> [yawDegrees|-] [settleTicks] [y|-]` — walks body 0
  over a grid (`WorldPatrolCommandModule`, a `world.at` script of `body.pose`,
  a settle, `world.patrol ask <i>` and `world.patrol read <i>`), picks the centre of
  the first seat's view at each point (five more pixels, its head, feet and shoulders, about it where the centre
  misses the body) and reads it back with `world.patrol` / `world.patrol all`:
  `OFF BODY` where no pick names `body:0`, `OCCLUDED` where only the centre misses
  (an exhibit stands between the camera and the body), `NO ANSWER` where no pick
  completed, `STALL` where a host iteration's work passed 16.7 ms.
- `world.view.panes [<x> <y>]` — reads the panes the root's `place` passes
  draw, in drawing order, each as its published `SourceMapping`
  (`WorldViewGraphHost.PublishPanes`); given a display point, it echoes the
  presentation picker's answer (`pick=instance:<name> pixel <x>,<y>` or
  `pick=none`) and how the hit walk through the live instance set ends
  (`walk=<end> steps=<n> in <instance> last <kind>:<source> pixel <x>,<y>`).
  Each view's world producer reports the screens standing in the world it
  renders (`WorldViewGraphHost.Screens`, the binder's `WorldScreenMappingSet`,
  or, for a seat presented in another world and for a session, that world's
  own, `IWorldViewScenes.TryPlacements`), so a walk from a view's pane
  continues through a screen, and through portals inside a destination to
  `views.nestingDepth` screens: `Producer` on a producer, machine or probe
  source's pixel (a fallback colour's past the depth), `DepthLimit` past the
  set's nesting depth, `Unread` on a screen showing a camera view, which is no
  live instance yet. It ends with the pane the pointer hovers
  (`hovered=pane<i> <kind>:<name> pixel <x>,<y>` or `hovered=none`): each
  frame `WorldCursorFeed` asks the host's picker through
  `WorldViewGraphHost.Hover` for the pointer's display point, whenever the
  pointer is on the window, not steering, and the cursor policy shows it,
  inside its seat's viewport or not, and the overlay's `CursorWriter` outlines
  the hovered pane's rect in the accent hue. Offscreen, nothing asks, so it
  reads `none`. The drawn cursor's `hover=` names a hovered pane
  (`pane '<name>'`) when no HUD panel is under it. It refuses by name in a
  boot with no GPU presentation.
- `world.screens` — lists the declared screens, then the creation faces showing
  a source, each ending in `mapping <SourceMapping.Describe()>` (the line
  `world.view.panes` prints for a pane) or `mapping none (<reason>)`. A boot
  that presents nothing publishes no screen mapping (`none (not published)`).
  It, `world.nesting` and `world.view-refresh` live in the core
  `ScreenCommandModule`, so every boot shape answers them, offscreen and
  headless included.
- `world.nesting` — lists every level of nested worlds the presentation shows:
  `depth <n>` (the boot world's `views.nestingDepth`), each world a seat is
  presented in (`routed$<digest> depth 0 world <authority>`, the digest 16 hex characters of the identity's SHA-256), then each session view by name
  (`session$<screen>`, one `$<screen>` more a level beneath it, or
  `routed$<digest>$<screen>…` beneath a routed world) with its depth, its
  destination, `via endpoint:<authority>` (the residency every seat and fully
  disclosed window presenting that world shares) or `via own`, and
  `screens <index>:<instance>…`: what each of its world's screens shows, a
  session one level deeper, a camera view of that world
  (`<level>$camera$<camera>`), a source instance (a machine's or probe's of that
  world's own host, followed by the fault that leaves it dark in parentheses),
  `source$color$<digest>` past the depth, `text`, or `none`; then
  `text-fault <why>` when that world's fonts do not resolve.
- `views.nestingDepth` (document) — how many observations deep a world shown
  through screens or named infinity layers renders: 3 unauthored, 0 through 8,
  refused by name past either end. The boot world's governs the presentation
  and which worlds its authority opens observations for. It edits live through
  the whole-row `views` section (`world.row.set views nestingDepth <n>`,
  `world.row.step views.nestingDepth <delta>`), which submits `SetViewDefaults`;
  `world.nesting` reads the live depth. `WorldObservationSite`
  distinguishes physical screen indices from named sky layers; both use the
  same destination admission, re-admission and release path in
  `WorldInstanceHost`. Presentation reads `ScreenSession` or `InfinitySession`
  and never opens an observation itself. Only physical portal faces forward
  input. A session screen at the depth shows its session's
  `fallback` colour (`#RRGGBB`, black unauthored). Every other screen of a
  world shown through a screen, or of a world a seat is presented in, shows
  that world's own source: its machines from its own host (stepped on its own
  ticks, and read only while the delivery declares them), its cameras as views
  of it filmed under the level, its text through
  its own fonts, and producers whose content is a function of their settings
  (`testPattern`, `qr`, `color`) through the shared instance. A probe shows
  nothing (only the boot world runs a probe host), nor does a local-device
  producer. A world another authority runs shows neither its machines nor its
  text (its definition carries no document directory) nor its session screens
  (no message carries a remote screen session); its cameras and producers
  show.
- `view.annotate [on|off|toggle|clear|undo|list|add <cx> <cy> <rx> [<ry>]]` —
  the on-screen annotation outlines (`WorldAnnotationCommandModule`, core
  registered, Immediate, bindable; the debug world binds F3 to `toggle` in
  `avatars/moth.puck`, through the row's `text` payload). Bare lists. `list`
  echoes `[view.annotate: on|off circles=N/16]` and one
  `[view.annotate: <i> center=<x>,<y> radius=<rx>,<ry>]` line per outline,
  fractions of the window's width and height, origin top-left, numbered as the
  overlay labels them. `add` keeps one (ry defaults to rx) and refuses a
  center outside [0, 1], a radius outside (0, 1] and a seventeenth outline.
  Headless it keeps and lists; only a windowed overlay draws.
- `view.override camera|layout <name|auto>` — live composition override;
  `layout toggle` and `layout next` cycle the authored layouts. It is
  bindable: a bound dispatch (wheel sector / chord row, no tokens) selects the
  LAYOUT override by its constant Axis1D value — -1 toggles, -2 selects the
  next, n selects the nth authored `views.layouts` row (document order,
  1-based), and any other value clears to auto. It echoes what it submitted on
  stdout (`[view.override: layout <name|auto>]`, `[view.override: camera
  <name|auto>]`) and refuses by name, submitting nothing, a layout or camera
  the live document does not author, an ordinal past its layouts, and a cycle
  with no authored layout; a composition-grant denial prints on stderr.
- `view.override camera pose|look|orbit|turn|swing|move|fov <numbers>` — the
  OPERATOR CAMERA, the lever for aiming a view from the console. Seat look
  sensitivity (`seatCameraFeel`, zero unless a world authors it) is a player input
  setting, so `player.look` is the wrong tool and stays as it is. `pose <x> <y>
  <z> <yaw> <pitch> [<fov>]`, `look <x> <y> <z> <tx> <ty> <tz> [<fov>]` and
  `orbit <tx> <ty> <tz> <distance> <yaw> <pitch> [<fov>]` set an absolute pose;
  `turn <dYaw> <dPitch>`, `swing <dYaw> <dPitch> [<dDistance>]` (about the
  pivot, the point `distance` metres ahead of the eye), `move <dRight> <dUp>
  <dForward>` (camera axes) and `fov <deg>` edit the pose already set and are
  refused by name when none is. Degrees and metres; yaw 0 faces -Z and positive
  turns left; pitch is limited to 89; fov 1..170, default 60. Each call echoes
  `[view.override: camera operator eye=(…) yaw= pitch= fov= distance=
  forward=(…) pivot=(…)]`, the absolute pose submitted, and `world.view.state`
  repeats it as `camera=operator …`, headless included. Code:
  `WorldOperatorCamera` (Protocol: the pose math and the one read-back
  format), `WorldComposition.SetOperatorCamera` (the grant-checked composition
  leaf), `WorldCompositionState.OperatorCamera` (the store),
  `WorldViewCommandModule.OperatorCamera.cs` (the verb). While set,
  `WorldViewComposer.OperatorCameraName` replaces every non-instance slot,
  seat slots included, and `WorldFramePresenter` resolves that name from the
  pose, cutting temporal history on every change. `view.override camera auto`
  or a named camera clears it. Nothing reaches the document, journal or
  simulation.
- `view.isolate <prototype> <shape-name>... | off` — the live SHAPE ISOLATION,
  for inspecting part of a creation alone (`view.isolate moth thigh-left
  thigh-right` shows both legs). It keeps the named shapes and every shape
  parented beneath them, transitively; a name ending in `*` is a prefix
  (`thigh-*`). Every other shape of that prototype emits nothing wherever the
  boot world renders it (body stamps through `WorldStampPool`, and animated,
  attached and static placements through `WorldPlacementStamper`), so it bakes
  into no mesh and casts no shadow; paints and volumes parented to a hidden
  shape, the prototype's mesh and its text go with it. Hidden shapes' slots
  still pack, so a hidden parent still poses the kept shapes and swings,
  effectors and gait still animate them. It echoes `[view.isolate: <prototype>
  kept=<n> hidden=<n> names=<names>]`; `off` restores every shape, and no
  arguments echo the live isolation or `[view.isolate: off]`. An unknown
  prototype or a name matching no shape is refused by name and changes
  nothing. One isolation at a time. Code: `WorldShapeIsolation` (the
  selection, the descendant resolution and its revision, owned by the pool as
  `WorldStampPool.Isolation`), `WorldViewCommandModule.Isolate.cs` (the verb).
  `WorldSceneEmitter.WriteRevision` carries the isolation's revision, so a
  change rebuilds the program; the changed bake groups hash new geometry, so
  they bake or load their own meshes and never take the whole body's. Until a
  changed group's mesh lands, the bake keeps drawing its previous mesh. The
  session and adjacency emitters, the presentation query field and the limb
  probes ignore it. Session presentation only: no document field, nothing the
  simulation or a replay reads. Headless it records and echoes the isolation
  with nothing to draw.
- `view.reseed [<body>]` — restarts a body's presentation-only animation (gait
  driver phases and weights, foot plants, root and part followers) as for a new
  occupant (`WorldStampPool.Reseed`). A `body.pose` teleport of the same
  occupant keeps its gait phases so it never pops; pin `view.reseed` beside it
  when a scripted motion must look the same on a repeat
  ([tick-exact debug capture](schedules-and-tests.md#tick-exact-debug-capture)).
- `player.camera [seat]` — toggles Free Cam (bindable, no tokens).
- `world.row.set views.seatRig <json>` — replace seat framing (a whole camera
  program).
- `world.row.set views.seatControl <json>` — replace yaw reference/pitch band.
- `world.row.set playerDefaults.seatLook <json>` — replace world-floor input
  preference.
- `world.row.set views.layouts <json>` / `world.row.remove views.layouts <name>`
  — mutate layouts.

All mutations run through the ordinary authority, tick-boundary, validation,
and replay paths. Keyless rows cannot be removed.

## Verification

Use a writable state directory when booting locally:

```text
world.view.camera 1
world.view.state
world.view.pointer
world.view.panes 8.5 40.5
```

The camera read-back reports pitch limits and live angles in degrees; authored
payloads remain radians. A discriminating control must move right-stick X and
observe camera yaw plus upright body facing change while `Turn` remains zero;
right-stick Y must change pitch without tilting the body. Then move left-stick X
and prove lateral translation without a facing change; while holding left-stick
forward, move right-stick X and prove the trajectory turns with heading. Repeat across
a traveler crossing to prove the same seat state and destination structure are
used. Refusal controls: omit `views.seatControl`, author
`minPitch`/`maxPitch`/`worldAxes` under `seatCameraFeel`, invert the pitch interval, or name an unknown yaw reference.

For a camera program: author an unknown op `$type`, put `anchor` anywhere but
first, put `clampPitch` after its `orbit`, omit `fieldOfView`, name an
undeclared program from a `blend`/`selectProgram`, or point two programs'
blends (or select cases) at each other. Each is refused by name at load, the
cycle carrying its trail. `world.view.camera`
reports `cameraApplication=true` while a seat is in Free Cam.
