# Engagement — control applications: the (target, kit) set a principal holds

A principal holds a SET of CONTROL APPLICATIONS. Each is a `(target, kit)` pair
saying where its resolved intent goes and what the channels MEAN when they get
there. That set is the whole of engagement: there is no route row, no capture
flag, and no latch beside it to disagree with. Files:
`src/Puck.World.Server/WorldEngagement.cs`, `WorldGrants.cs` (application
storage), `src/Puck.World.Machines/WorldMachineHost.cs` (plus its
`.Bindings.cs`/`.Instances.cs` partials — pad/instance resolution),
`src/Puck.World.Schema/ControlApplication.cs`,
`src/Puck.World.Protocol/Protocol/WorldCommand.cs`, `WorldScreen.cs`
(`WorldScreenRoute`), `WorldKit.cs` (the pad map),
`src/Puck.World/PlayerCommandModule.Engagement.cs`, `WorldScreenBinder.cs`,
`src/Puck.Abstractions/Machines/` (engine-neutral machine contracts).

## Contents

- The primitive: a set of `(target, kit)` applications
- One kit vocabulary, two destinations
- The command kinds
- Authority
- Possession swaps the perceived world, not just the driven body
- Application storage — one table, one derivation
- `Compose` = check Control → rebuild the set → sync the latch
- `WorldScreenRoute.EngageChannel` — the context-sensitive button
- Tabletops — the context button sits a seat at a board
- The framed screen — the camera frames the engaged screen's picture as an emulator shows it
- `WorldScreenRoute.Input` — where a pointer hit on the screen goes
- `Dissolve` — three outcomes, no repair
- The one fold
- `body.engage` grammar
- Replay visibility
- Machines: server-authoritative
- Read-backs
- The device frame — a device's controls are the seat's own
- Observation delivery and session query disclosure
- Verifying

## Observation delivery and session query disclosure

`WorldOutputHub` delivers borrowed snapshots synchronously. The federation projection sink retains at most
`WorldFederationProjectionSink.PendingDeliveryLimit` (eight) encoded records per subscriber. Overflow detaches
with `world.observation.backpressure`; the peer reopens for a fresh primer because definition and route records
cannot be recovered from the latest snapshot alone. Session queries use `WorldSessionSink.AllowsQuery`, the
same tier and observer-disclosure decision as deliveries: Frames reads nothing, Presentation reads only
recipient-filtered state observations, and authoritative readbacks require a full Replica. Test these with
`SessionObservationLawTests` and `WorldProjectionBackpressureLawTests`.

## The primitive: a set of `(target, kit)` applications

`ControlApplication(GrantSubject Target, string? Kit, ChannelReachMask Reach)`.

- **target** — `Screen(index)` (a booted machine's pad), `Placement(id)` (a tabletop) or `Body(index)` (its
  own body, or another body under possession).
- **kit** — the `WorldKit` name whose `pad` map assigns the delivered channels
  their meaning at the target, or `null` for passthrough (every reached ordinal
  arrives unchanged). A body target is always passthrough — the destination
  body's own kit already assigns meaning. A screen naming no kit falls back to
  the engine's baked two-movement-role pad (`MoveStrafe`→`LeftStickX`,
  `MoveAdvance`→`LeftStickY`, structural ordinals, never a channel name).
- **reach** — the ordinals this application delivers at all. A masked-out
  ordinal still drives the source body through its OWN-BODY application (when
  that member is present) but never reaches this target.

**Capture is set membership.** A participant that has composed nothing holds
exactly `ControlApplication.OwnBody(index)` — the avatar drives itself.
Composing exclusively REMOVES that member and adds the target's; composing
mirrored keeps both. So:

| Set | Meaning |
|---|---|
| `{ own-body }` | unengaged (the default; not stored — an absent row IS this) |
| `{ screen:0 }` | engaged, captured — the avatar idles |
| `{ own-body, screen:0 }` | mirrored — the avatar walks and the cabinet reads |
| `{ body:7 }` | possession — the seat drives body 7 and perceives from it |
| `{ own-body, body:7 }` | co-drive — the seat walks its avatar and contributes to 7 |

`WorldBody.Engaged` is a DERIVED projection of one predicate ("the set omits the
own-body application"), written by `WorldEngagement.SyncLatch` alone and
re-asserted every tick at the top of `FoldTick`. One storage, one derivation:
the latch cannot disagree with the set, so nothing detects or repairs a desync.

## One kit vocabulary, two destinations

`WorldKit` carries `actions` (channel name → `ActionSpec`, what a BODY does with
the channel) and `pad` (channel name → `WorldPadElement`, what a MACHINE does
with it). Same rows, same channel names, one kit. A screen names one by
`screens[*].route.kit`; the validator refuses a route naming a kit with no `pad`
map, and refuses a `pad` entry naming an undeclared channel or an undefined
element.

Button elements compare the RAW `FixedQ4816` value against
`WorldChannelTable.DefaultBinaryThreshold`, never a float round-trip; stick axes
canonicalize to -1..1 and triggers to 0..1 in the fixed-point domain first.

The quarantined arcade district
(`experimental/src/Puck.World/Assets/worlds/modules/arcade.puck`) is the worked
example: its cabinet screens route engageable with kit `arcadePad`. The live
debug world's `portal` placement is the worked portal face (its glass is a
derived face screen onto the garden); a portal face shows its destination
without being engageable.

## The command kinds

`WorldCommand(Principal Principal, int EntityIndex)` — the closed
drive-a-body sealed hierarchy (read `WorldCommand.cs` for the cases);
`ComposeControl(Target: GrantSubject,
Exclusive: bool, TargetPrincipal)` and `DissolveControl(TargetPrincipal)` branch
out of the generic `Drive`-over-body gate first and run their own check. The kit
and reach are NEVER carried on the wire — they are resolved SERVER-SIDE,
deterministically, from already-replayed document state (a screen's authored
`route.kit`/`route.channels`, or passthrough/all for a body target).

## Authority

`WorldEngagement.CheckEngage(target, actingPrincipal)` asks the ONE grant table:
`Control` over `target` — checked against the SUBMITTER (`Principal`), never
`TargetPrincipal` (seats are pre-seeded permissive `Control`/all, so checking the
target would pass unconditionally). A body target's Control check is separate
from Drive: **possession = Drive-over-the-target-body (a normal grant,
`world.grant seatN drive body:<n>`) + the application** — the application alone
confers no authority to actually move the target.

**A Control grant never mints an application.** Authority to apply and the
application itself are separate storage, so a bare
`world.grant … control screen:0` produces no route. The reverse
coupling is enforced instead: `WorldGrants.Revoke` of any Control row re-tests
every application the principal stands on and dissolves each one whose target it
no longer holds Control over (which is what makes a WILDCARD revoke drop the
concrete applications it was the only basis for). The own-body member is never
dropped that way — a set with no own body IS capture, and losing an unrelated
grant must not capture an avatar.

## Possession swaps the perceived world, not just the driven body

A seat whose set OMITS its own-body application and names another BODY perceives
its ENTIRE world from that body: camera eye, spatial-audio listener, and every
`seat.<n>.position.*` HUD binding. `src/Puck.World.Client/WorldPerceptionAnchor.cs` is the ONE
resolution point every seat-relative presentation derivation resolves through
(`PerceivedBody(slot)`); `WorldSeatContextSync.Publish` writes it every tick (and
once at boot) from the SAME `Applications` loopback read that publishes the
`engagement` context family — one read, two derived facts, never a second
grant-table query. A set that RETAINS its own-body application never swaps the
anchor: the seat is still driving its own avatar. A screen application never
swaps it either. `body.where`'s `anchor=body:<n>` echo is the read-back (see
[hud.md](hud.md) for the `seat.<n>.position.*` consumer). Deliberately NOT
swapped by possession: the body-index-band presentation classifications
(`WorldSceneEmitter`'s footstep cue and seats-always-cast soft-shadow gates, both
keyed on `index < LocalSeatCount`) — a possessed body keeps casting/cueing on its
own raw index band regardless of who perceives from it; and every SIM-side
seat→body resolution (`PlayerCommandModule.ResolveTarget`, `WorldEngagement`,
grant subjects, intent routing) — the ruling covers the perceived world only,
never authority or simulation.

## Application storage — one table, one derivation

`WorldGrants` holds `Dictionary<Principal, List<ControlApplication>>`,
separate from the five per-capability subject sets. An ABSENT row IS the
own-body default (`DefaultApplications`, a per-body-index cached single-element
list, so the per-tick fold read allocates nothing). A set composed back to the
default is stored as the default itself, never as a composition — so
`CollectApplicationHolders` can never report an uncomposed participant.

`IWorldGrantsView`: `Applications(principal) → IReadOnlyList<ControlApplication>`,
`SetApplications(principal, applications)`, `ClearApplications(principal) → bool`,
`CollectApplicationHolders(target, into)`. The transition hook that feeds
`WorldEventFeed.QueueRouteEngaged`/`QueueRouteDisengaged` fires per member
added/removed. The set is captured in `WorldGrantsGranteeCheckpoint` and
round-trips through `WorldAuthorityCheckpointCodec.Grants`.

The set doubles as the `engagement` context FAMILY for seat bindings:
`engaged|none` per seat is a loopback READ of whether `Applications` names
anything beyond the seat's own body (`WorldSeatContextSync`, every tick
post-step), and a `contexts` row in the composed binding document can flip the
seat's active group on it (see [documents.md](documents.md), "Context rows"; read
back via `player.bindings`).

## `Compose` = check Control → rebuild the set → sync the latch

`WorldEngagement.Compose(entityIndex, target, exclusive, actingPrincipal,
targetPrincipal)`: `CheckEngage` → resolve the live body → rebuild the set
(everything already applied except this target, minus the own-body member when
`exclusive`, plus the own-body member when mirroring a non-own target, plus the
new member with its resolved kit/reach) → `SetApplications` → `SyncLatch`. A
denial or a dead entity index mutates nothing. Re-composing onto a target already
applied REPLACES that member rather than stacking a duplicate.

## `WorldScreenRoute.EngageChannel` — the context-sensitive button (the RPG A-button)

`EngageChannel` is consumed; `CycleChannel` is not. A NAMED channel (kebab-case
only at parse time; `WorldDefinitionValidator.ValidateRoute` holds it to the same
"must resolve against a declared channel" bar `channels` rows carry, since a dead
name would otherwise be a silent, permanent no-op) whose RISING EDGE, on a body
within `EngageRadius` of THIS screen, is intercepted server-side into a
`ComposeControl` instead of reaching the body's own action track. The edge
compares against the channel's own declared threshold whether or not the seat's
kit binds an action to it (`FixedWorldKit.ActionThresholds` carries every
declared ordinal's threshold).

`WorldTick.ResolveEngageProbes` runs every `Step`, BEFORE the population
advances (so it reads PRE-MOVE positions), over every active local seat that has
COMPOSED NOTHING beyond its own body (`HasComposedApplication`): the first
(document order) screen that is `Engageable`, carrying a live machine
(`IWorldMachineHost.HasMachine`, the actually booted signal rather than the
document-declared `WorldScreenSource.Machine`), carrying no live occupant
(`PlayersOn(screenIndex)` empty), naming an `EngageChannel` this world's channel
table resolves, within radius, and that would pass `CheckEngage`. The resolved
`(screenIndex, channelOrdinal)` feeds `WorldBody.Advance`'s `engageProbeOrdinal`
parameter: it tests the SAME rising-edge condition `ProcessLaneActions` tests,
ahead of any integration this tick, and returns whether it fired. A fired probe
skips this tick's movement/action-track integration entirely (the identical idle
no-op the latched branch already takes) — so the press can never ALSO fire a
bound jump the instant it engages — and `WorldServer.Step` then calls the
ordinary `Compose` with `actingPrincipal = targetPrincipal =
Principal.Seat(slot)`, printing `[world.engage: <principal> auto-engaged
<target> — context button]` on stderr.

**The same button leaves.** A local seat CAPTURED on a screen (its body's
`Engaged` latch set) whose route names an `EngageChannel` the channel table
resolves is probed on that channel too (`ResolveLeaveProbe`); its rising edge
runs the ordinary `Dissolve` and narrates `[world.engage: <principal> left
<target> — context button]`. A held button presses once: after the leave the
seat's previous channel bit is high, so the next tick's engage probe sees no
edge. A mirrored seat (own body still applied) is not probed for leaving.
`WorldDefinitionValidator.ValidateRoute` refuses an `engageChannel` the route's
kit `pad` maps (`… is a pad button of kit …`), so the leave press is never also
a machine button. `ContextButtonLeaveLawTests` holds both.

**Replay.** Nothing about this is taped. It is pure re-derivation from tick-local
sim state (position, channel bits, document/grant state) — a shadow replay
re-executes `ResolveEngageProbes` from the SAME taped inputs and reaches the
identical decision at the identical tick.

**Scope today**: local seats only (`AdvanceSeats`, indices 0..3) —
`AdvanceSimulated`'s peers/inhabitants are not probed.

## Tabletops — the context button sits a seat at a board

A placement's board facet may carry an `engage` block (`WorldTabletopEngage`:
`channel`, `radius`, `pitch`, `margin`, `surface`). The same probe
(`ResolveEngageProbes`) considers it beside the screens and takes the nearest
target on the ground plane (screens first on a tie): the board's centre
(`WorldTabletopFrame.Centre`, the anchored topology's footprint) within
`radius`, the channel declared, and `CheckEngage` passing for
`GrantSubject.Placement(<id>)`. The probe composes a CAPTURING application onto
`placement:<id>` (Control over a placement is a legitimate subject,
`WorldGrants.IsLegitimateSubject`; seats hold it through their seeded
`control all`), so the avatar idles exactly as on a screen, and
`ResolveLeaveProbe` leaves on the same channel. A table takes no occupancy
check: several seats may sit at one. `body.engage placement:<id>` composes the
same application by hand, under the same policy (`CheckTabletopEngagePolicy`).
`FoldTick` routes a placement member nowhere: the game is the world's rules,
which read the seat's channels (`channel(<seat>, <name>)`) and three fixed-point
seat operands — `board(engaged, <row>, <seat>)` (the set names the placement),
`board(pointer, <row>, <seat>)` (the cell `PlayerIntent.SourceRay` meets on the
plane `surface` above the topology origin, `WorldTabletopFrame.TryPointerCell`)
and `board(side, <row>, <seat>)` (the board edge the body stands nearest,
`SideOf`, 0 -Z, 1 +X, 2 +Z, 3 -X) — through `BoardSeatOperand`
(`WorldRuleHost.ReadBoardSeat`). The windowed host casts the OS pointer whenever
the world declares an engageable tabletop (`WorldPointerRayCapture`), so a
seated seat's ray reaches the rules every tick.

Presentation: `WorldSeatContextSync` publishes the seated placement into
`WorldPerceptionAnchor.EngagedTabletop` and the `engagement` context family's
`tabletop` state (instead of `engaged`), so a binding overlay's context row
`{engagement, tabletop, <group>}` swaps the seat to the table's controls; the
seat camera eases to `WorldTabletopFraming`'s board view (the whole board from
the seat's side at `pitch`, near plane at the board) on the rig's dynamics
through `WorldFramingEase`. `TabletopEngagementLawTests` (Server) and
`WorldTabletopFramingLawTests` (Client) hold the probe, the reads and the view;
the debug world's chess table (`worlds/debug/chess.puck`, `chess.md`) is the
worked game, and `chess-tests.puck`'s seated-player test plays it headless with
`body.press` and `source.pointer.*` steps. A tabletop has no matte or screen
fidelity: the board view stays the in-world look.

## The framed screen — the camera frames the engaged screen's picture as an emulator shows it

The engaged view is diegetic: the seat's own camera on the in-world screen,
panned and zoomed until the window shows the console's picture the way a desktop
emulator does, and easing out reveals the room around it. There is no separate
2D image.

A seat captured on a screen frames it: `WorldSeatContextSync.Publish` publishes
the captured screen into `WorldPerceptionAnchor.EngagedScreen` from the same
`Applications` read (−1 while unengaged, mirrored or possessing), and
`WorldFramePresenter.Dress` hands the row to the seat's `WorldScreenFraming`
(`src/Puck.World.Client`), refitted from the window's size every frame:

- **The picture, not the slab.** `PictureHalfExtents` is the glass inside its
  bezel band (`WorldScreenMappings.Glass`); a window-projection session screen
  has no bezel and uses the whole face. A 4:3 glass showing 256×240 carries the
  console's 8:7 pixels by its own shape: there is no pixel-aspect field and no
  crop.
- **The matte.** `Matte` is the rect of the seat's view the picture fills: each
  source row the largest whole number of window pixels that fits the view
  vertically and horizontally, the width following the picture's aspect, centred
  on whole pixels; below 1× it fills the limiting axis. A 1280×900 window shows
  a 256×240 source at 3× (720 rows), 960 columns wide on a 4:3 glass and 768
  on a square-pixel one.
- **The camera.** `TryFrame` puts the eye on the face normal (`cross(right,
  up)`), far enough that the picture's height is the matte's share of the
  view's at the seat rig's field of view, with `Near` one slab thickness
  (`2 × halfDepth`) short of the glass so the avatar and anything else between
  eye and glass draws nothing. It looks at the picture's centre shifted by
  `MatteOffset`, the half pixel a matte centred on whole pixels sits off the
  view's centre when a gutter is odd, so the picture's edges land on the
  matte's pixel edges. The camera eases to it and back on the seat rig's own
  reported `dynamics` (the Moth's `camera-boom`); a rig with none cuts.
- **The settled view shows only its matte.** Once the ease settles
  (`WorldScreenFraming.Framed`), the seat's view keeps its whole slot and
  renders at native scale (its `RenderScale` and dynamic resolution pinned to
  1), and `WorldViewGraphHost.PlaceViews` places it on the matte with the
  `place` pass's `crop` set to the matte's part of its output, a crop of the
  rect's own pixel extent, so the matte's pixels are copied one for one. What
  no view covers is the root's letterbox, written in the route's
  `display.fill`. A crop rather than a matte-sized view keeps the view's
  extent exact: the render graph quantizes an arbitrary footprint
  (`RenderGraphExtent`), while a whole display or a half is exact.
- **Fidelity.** The ease's weight is the view's screen fidelity
  (`SdfViewSnapshot.ScreenFidelity`, the `screenFidelity` pass-block value, and
  the `place` pass's `fidelity`). At 1 the glass's grille, scanlines, bloom,
  vignette and glint are off and its bezel band is a hard edge
  (`shade/sdf-environment.hlsli`), the views stage and composite clear the
  atmosphere's transmittance, in-scatter and bounded volumes, and the root's
  `place` pass writes the view's native source past its sharpen and tone map.
  Every view that frames nothing carries 0, the in-world look; leaving fades it
  all back in as the camera pulls back.

The route authors only the matte's surround (`screens[*].route.display`,
`WorldScreenDisplay`): `fill` (`#RRGGBB`, default black), refused by name in
`ValidateDisplay`, and only on an engageable route. Emulator-style borders or
filler images around the matte belong there when they are built.
`world.screens` echoes `display:fill=…` per engageable screen. The overlay fact
`ScreenEngaged` (`WorldOverlayFacts`) reads the same anchor, so a panel, the
cursor or the bar can step aside while engaged.

- `WorldViewGraphHost.FrameScreen` records each settled seat's screen and matte,
  and `PublishFramed` publishes the screen's own mapping (`Screens`) on the
  matte as the last pane, its warp dropped because the matte is the picture: the
  picker maps a display point there to the source pixel the ray meets, and the
  cursor never outlines it (`ShowsFramed`).
- An engageable screen with no machine engages when it shows a producer
  (`WorldTick.ShowsEngageTarget`): its pad reaches nothing, and the framed
  picture shows its image.
- A framed capture (a window's, or a whole monitor's) is a passthrough
  candidate: the local user's physical press on its picture opens it
  (`WorldSourcePassthrough`: a pointer press only the window pump delivers, or
  a local pad's bound press, `PadButton`), and the source closes once the seat
  no longer frames the screen. A console `body.engage`, an addon's or a
  scripted press engages without opening it
  ([decision](../../../../docs/decisions/rendering.md#the-frame-graph-and-nesting)).
  While the pointer rests on the candidate's picture the system arrow shows,
  opened or not. A window (`Win32PassthroughWindow`) hears its input as window
  messages and takes the keyboard on a press. A monitor
  (`ISourcePassthroughDesktop`, `Win32DesktopPassthrough`) is reached through
  the system's own input (`SendInput`): a press on its picture is replayed at
  its release at the desktop point the picture shows (move, press, drag,
  release, move back, one batch), a wheel turn the same way, a move reaches
  nothing, and it takes no keyboard (`SourcePassthroughRouter` returns focus
  to the game), so keys go wherever Windows sends them. A replayed press whose
  point shows a window of the host's own process is sent nowhere, so a click
  on the World's mirror in the picture cannot repeat itself.
- **One-step exit.** While a local seat frames a capture, the reserved chord
  Control+Alt+Escape is the host's whoever has the keyboard
  (`SourceFocus.ClaimsChord`, answered through `TakeChord`): it returns the
  keyboard, closes the source (releasing what it and the pad hold) and leaves
  the seat's engagement through `IWorldPassthroughSeats.Leave` (the
  `DissolveControl` the context button's press makes, under the seat's acting
  principal). Over a framed desktop another window may hold the keyboard, so
  the chord is claimed system-wide (`ISourcePassthroughDesktop.ClaimsChord`, a
  `RegisterHotKey` on a message-only window the pump thread pumps, claimed and
  released per frame in `TakeWarp`), and leaving brings the World's window
  back to the front (`ReturnForeground`: `SetForegroundWindow`, then an Alt
  tap, then a click on its caption, which dismisses Start and the shell's
  flyouts). The pad's X leaves through the context button, as at every
  device.
- **The pad as a desktop pointer.** A seat captured on a capture's screen
  reads the `engagement` family's `passthrough` state
  (`WorldSeatContextSync`, from the delivered screen row:
  `WorldImageProducerSettings.IsPassthroughCapture`), so a context row gives it
  a desktop page (the debug world's `desktop-pad` overlay in `desktop.puck`).
  Over a framed desktop the pad is the desktop's mouse instead of a pointer on
  the picture: the stick moves the system cursor across the monitor in
  monitor heights (`ISourcePassthroughDesktop.MovePointer`, any window in
  front), buttons, wheel and arrows are sent at it, and the first press opens
  the source (`OpenFramed`).
  The page binds `source.passthrough.pointer` and `.scroll` (sticks),
  `.button.<left|right|middle|back|forward>` and `.arrow.<up|down|left|right>`
  (`WorldPassthroughPadCommandModule`, core-registered for vocabulary parity).
  Each verb acts only for `WorldPassthroughPad.IsLocalPad`: a binding's
  dispatch of a known `gamepad.*` source from a reported device, so a typed
  line, an authored command, a wheel sector, `player.signal` (no device) and a
  probe (no gamepad source) are refused by name. The pointer stick moves the
  system cursor (`WorldSourcePassthrough.TakeWarp`, through
  `IWindowCursorPolicy.TryTakeWarp` and `INativeWindowCursor.WarpPointer`,
  foreground window only) across the framed picture, clamped to it, at the
  route's `padPointer` feel (`WorldScreenPadPointer`: `speed` in picture
  heights per second, `curve`, `boost` over `rampSeconds`, `scrollSpeed` in
  notches per second; absent moves and scrolls nothing), integrated exactly
  per frame so the travel does not depend on the frame rate. Presses take the
  physical press's door (`OpenEngagedUnderPointer`, then the router); arrows
  reach only an opened window. `source.passthrough` echoes `pad p<n> screen
  <n> '<instance>' stick … scroll … buttons … keys … feel …` or `pad idle`;
  `world.screens` echoes `padPointer:…`. The context family is published by
  the windowed `WorldSimulation` only, so a headless boot never switches to
  the page. `WorldPassthroughPadLawTests`, `WorldPassthroughPadCommandModuleLawTests`,
  `WorldViewPaneMappingLawTests.Engaged`, `WorldViewPaneMappingLawTests.Desktop`,
  `Win32DesktopPassthroughTests` and `WorldSeatContextSyncTabletopLawTests`
  hold it.

Known limits: the matte cuts in when the ease settles and out when it starts
back, while the treatment fades over the ease; the uncovered display takes the
first framed seat's fill. `WorldScreenFramingLawTests` holds the picture, the
matte, the whole-pixel landing, the near plane and the ease;
`WorldViewPlacementLawTests` the matted placement;
`WorldViewPaneMappingLawTests.Engaged` the framed mapping and the passthrough
press; `SdfFrameBlockLawTests` the fidelity's pass-block member. Three-or-more
seat layouts whose slots are not a whole display or a half render at a
quantized extent, so their framed picture is resampled.

## `WorldScreenRoute.Input` — where a pointer hit on the screen goes

`input` is a `Puck.Commands.SourceDestination`: absent or `Presentation` (hover
and highlight), or `Simulation` (a light-gun style pointer). The validator
refuses `Passthrough` by name in `ValidateRoute`: host passthrough exists only
for a source the local user opened, and a document, authored locally or
arrived through a portal, never creates one or sends it input. A `Simulation`
screen maps a pointer ray from the row alone: `WorldScreenMappings.Of(screen,
source, width, height)` publishes the row's `SourceMapping` (the face frame, the
whole source, the glass bezel as an exact-inverse warp), and the ray arrives as
the `source.pointer.origin`/`source.pointer.direction` Axis3D commands
quantized once at the seat verbs `PlayerCommandModule` registers.
`SeatController.HeldIntent` folds them into `PlayerIntent.SourceRay`, which
every intent path carries behind one flag byte; on a windowed host
`WorldPointerRayCapture` produces them from the OS pointer over its seat's
camera (`SourceRay.Through`) and holds them with `InputRouter.Sustain`, so
every tick carries the ray. A typed `source.pointer.origin`/`direction` line
holds its half through the same `Sustain` until it is typed again,
`source.pointer.clear` ends both halves, or the seat is vacated
(`PlayerRoster.SlotVacated`, which the router answers by ending every value the
slot sustained). The typed aim and the capture share one sustained value per
half: the later write rides, and the capture ending its ray ends that value
whichever wrote it. A rule reads the hit as
`$pointer:<seat>:<screenIndex>:x|y|on` (or `press:<channelName>`, that
seat's channel while it points there; `any` in place of the seat reads the
first seat, then live session by ordinal, holding `Control` over the screen
whose ray lands on it), mapped in the tick through
`WorldScreenMappings.Normalized` (source-normalized `x`/`y` in `[0, 1)`, `on`
1 on the source, all 0 otherwise); a screen whose `input` is not `Simulation`
is refused for it at compile time. `world.screens` echoes `input:<destination>`
and the mapping the presentation publishes for the row (named by its source
instance, at its image's extent) per screen, and `body.channels` echoes the
seat's ray and hits. A machine reads the hit as a light gun: the fold aims each
screen application's pad (`MachinePadState.Pointer`, a `MachinePointer` in
1/65536 fractions of the output) with `WorldEngagement.Aim`, which maps the
applied body's `SourceRay` through the same `WorldScreenMappings.Normalized`
mapping, cached per row, and leaves the gun off for no ray, a miss, the bezel or
a non-`Simulation` screen. So the gun reaches a machine only while the seat holds
an application onto the screen (mirrored is enough), and its trigger is a kit
`pad` button. The Humble brick puts the aimed pixel's brightness on its infrared
receive line (`LightGunComponent`); the advanced brick ignores the pointer. An
authored cartridge reads it through the `$light` operand (`rom-forge`), and the
headless `light-gun` canary is the stdin recipe: `body.engage` the screen,
type `source.pointer.origin`/`direction` once per aim, `world.wait` the ticks
the aim should hold, read the cartridge's published byte through a memory
binding, and end with `source.pointer.clear`. The `rendering` skill owns the mapping itself.

## `Dissolve` — three outcomes, no repair

`WorldEngagement.ResolveDissolve` returns a `ControlOutcome` (3 members):

| State | Outcome | Actor check |
|---|---|---|
| no live body, or the set is already the own-body default | `NotApplied` (friendly no-op) | — |
| the actor lacks Control over at least one applied target | `Denied` | yes |
| every applied target checks out | `Dissolved` — the set returns to the default, the latch releases | yes |

There is no repair arm: the latch is derived from the set, and a bare grant
mints no application. `PeekDissolve` is the read-only twin the
client uses to format the echo and decide whether to drop held state (only
`Dissolved` does).

## The one fold

`WorldEngagement.FoldTick`, run inside `WorldServer.Step` after the population
and addon read pump and before machine stepping, visits EVERY live body once:

1. Read the body's principal's application set.
2. Re-assert the capture latch from it (`SetEngaged` short-circuits an unchanged
   value, so this costs nothing).
3. For each member: the own-body member routes nowhere (the avatar's own
   integration in `WorldBody.Advance` IS its delivery, which the latch just
   enabled); a screen member masks `body.EngagedIntent` by the screen's LIVE
   reach, translates through its live kit's compiled pad map, aims the pad's
   light gun through the screen's live mapping (`Aim`, above), and merges into
   the screen's pad (`MachinePadState.Merge`: buttons OR, sticks sum+clamp, the
   first on-screen pointer wins); a body member masks by its application's
   `Reach` and appends a `BodyRouteContribution(TargetBody, Principal, Intent)`.

A screen's policy (reach, pad kit, light-gun mapping) and a kit's pad map are
read from the rows the live document holds (`WorldEngagement.PolicyOf`/`PadOf`),
compiled once per row object against the population's live channel table and
kept while the document still holds that row and the population that table, so
a `world.row.set` of the screen or kit, or a reload that moves the channels,
reaches the next fold with no re-compose (`EngagementPolicyLiveEditLawTests`).
The `Kit`/`Reach` a screen application records are the policy at compose, for
the echoes; the fold does not read them.

`body.EngagedIntent` is captured on EVERY `WorldBody.Advance` call, so it is
available whether or not the avatar is idle. The server passes
`BuildPadSnapshot()` directly to `WorldMachineHost.Advance`; no snapshot or wire
lane carries pads.

`WorldServer.Step`, right after `FoldTick()`, drains `BodyContributions` and
`EnqueueIntent`s each as an ordinary `IntentSubmission` for the NEXT tick —
landing in `ApplyIntentSubmission`/`StageContribution` under the EXACT SAME
Drive-gated co-drive path an addon or a co-driving seat already uses. No second
write path for possession.

## `body.engage` grammar

`body.engage <screen>|screen:<n>|body:<n> [body] [capture:on|off]`. The
bare-integer form is a screen index; `body:<n>`/`screen:<n>` reuse
`GrantSubject.TryParse`'s own grammar (the same tokens `world.grant`/
`world.revoke` accept). `capture:on|off`, when present, is ALWAYS the trailing
token, and maps to `ComposeControl.Exclusive` — on drops the own-body
application, off retains it. A body target skips every screen-only policy check
(engageable, auto-insert, machine presence, engage radius); it only needs a live
body at that index. The driving body is an optional 0-based body index, like
every `body.*` verb's: it defaults to body 0 (seat 1's body), is bounded to
`0..bodies.capacity - 1`, and `body.engage <target> 1` is driven by body:1,
seat 2's body. `body.engage` and `body.disengage [body]` resolve it through the
one `ResolveTarget` the other `body.*` verbs use, a stripped `capture:` token
counting as absent (the quarantined `EngageBodyIndexLawTests`, under
`experimental/tests/Puck.World.Tests/`). `body.disengage` submits
`DissolveControl`.

## Replay visibility

- The `ComposeControl`/`DissolveControl` COMMANDS themselves ARE taped (ordinary
  Command leaves).
- A body member's per-tick channel PASSTHROUGH is synthesized directly into
  `WorldServer`'s own intent queue (`EnqueueIntent`), never through
  `LoopbackTransport`'s `IntentTap` — structurally EXCLUDED from the tape's
  recorded intent list, exactly like a mounted addon's driving. It is RE-DERIVED
  at replay from the taped `ComposeControl` command (which fixes the
  target/exclusivity/kit/reach) plus the source seat's own taped submissions.
- The population hash needs NO format change: it already hashes every active body's
  position/orientation each tick, so a possessed body's motion and a captured
  source's stillness are both covered.

## Machines: server-authoritative

A booted `IMachineRuntime` is CORE state, not presentation-fed. Machine engines are
engine-neutral (`Puck.Abstractions.Machines`): `IMachineEngine` is a factory
keyed by a kebab-case id, DI-collected into `WorldMachineHost` (in
`Puck.World.Machines`, behind the Server seam `IWorldMachineHost`; `WorldBootComposition`
registers the factory (`machineHostFactory`) that builds it over the composed
engines and content providers — `gaming-brick` (SM83 family) and
`advanced-gaming-brick` (ARM7TDMI) are the shipped engines).
`WorldMachineHost` owns boot/step/cable-link/reconfigure/memory-peek for every
declared screen's machine in EVERY boot shape, headless included; stepping happens
inside `WorldServer.Step`, immediately after `FoldTick`, fed the tick's per-screen
pads directly from `WorldEngagement.BuildPadSnapshot()` (in-process, no
client/wire round-trip). For machine output, `WorldScreenBinder` is a read-only
facade over the host. Screen index IS machine identity for screen-hosted machines.

A screen owns no machine: every machine is a `machines` row, and a screen shows
one by name. `screen.select`/`.link`/`.unlink` submit a `WorldScreenOp`
through the ordered submission domain (`IServerLink.SubmitScreenOp`), applied
SYNCHRONOUSLY like `Command`/`Grant`/`Revoke` (never buffered — a
`body.engage` auto-insert precheck submits a `Select` immediately ahead of
the `ComposeControl` that follows it in the same batch, and the second
submission must observe the first's effect). Selection boots nothing.
Tape-covered: `WorldReplayEntry.ScreenOp`, discriminant 6.
Camera/capture/window-capture/jumbotron-view/test-pattern/QR screen sources
remain presentation-only and bind through `WorldScreenBinder` directly.

Console grammars: `screen.insert <index> <contentPath>` (a screen whose source
names a declared machine instance takes a file from its `contentFolders`,
inserted through `machine.operation`'s `content.insert` provider path, and
refuses an engine or options because the instance keeps its authored
configuration; a screen with no declared machine refuses the insert); `screen.eject <index>` (a named
machine display clears only the screen source through a
`WorldMutation.UpsertScreen`); `screen.source <index> <kind> …` (a presentation
source); and the Immediate read-backs `screen.state`, `screen.peek`,
`screen.links`, `world.machines`, `world.screens`. Only `body.engage`'s
auto-insert submits `WorldScreenOp.Select` from a verb; `Link` and `Unlink`
have codec arms but no console verb.

**Swappable content comes from authored folders (the owner's ruling).** The
cartridges a device can swap to are the files in the folders its world author
lists, never a host flag: `machines[].contentFolders`, an ordered list of
portable relative paths each resolved beside the declaring document
(`WorldDocumentPaths`, relocated through imports like every file field),
refused by name in `WorldDefinitionValidator.Machines.cs` when one is rooted or
not spelled with `/`, is listed twice, or the list is empty. A local folder
(the owner's git-ignored `roms/`) is listed like any other and may be absent:
validation reads only the spelling, never the disk, so nothing in validation,
boot, deploy or CI depends on it existing, and a world's boot content comes
from a shipped folder so it boots the same everywhere. `machine.content
<instance>` reads the folders back as they stand on disk at that moment (one
segment per folder with its resolved path, `present=` and file count; one per
file with its folder, `mounted=`, and `refused=<reason>` for a file the engine
cannot mount; `mounted="no cartridge"` when none is), through
`WorldMachineContentFolder`. A file is mountable when the engine's content
provider prepares it, or when the engine judges it, by its header and whatever
its extension, an image its machines run (`IMachineImageRecognizer`: the brick's
header checksum and mapper, the advanced brick's fixed byte and complement
checksum, the deck's iNES signature and board); a raw-content engine with
neither mounts every file. `machine.swap <instance> <name>` resolves a file
name, a `<folder>/<file>` name, or a stem across the folders in order, refuses
an unknown, ambiguous or unmountable name by name, and mounts through
`content.insert`; the engine refuses an unrunnable image again at creation.
The folders bind every mount, not just the swap: `WorldMachineHost`'s operation
preparation admits a content path the declaration does not already name only
when `WorldMachineContentFolder.TryAdmit` finds it directly in one of the
folders (resolved beside the document, `..` collapsed, the folder or its own
link target as the directory, a linked file leading into it), so
`machine.operation … content.insert`, `screen.insert` and `forge.play` onto a
device refuse any other path by name, and a machine with no folders keeps its
authored content. An admitted path is mounted in its folder's own spelling, so
`machine.content` marks it mounted however the operation spelled it. A
machine whose authored content file is absent boots with no cartridge (named
once on stderr, `screen.state … no cartridge`), never refusing the world; an
operation naming absent content is refused. A folder that empties or vanishes
while the World runs shows in the next listing, and the cartridge mounted from
it keeps running from memory. No verb lists a hard-coded set or scans outside
the authored folders. The shipped cartridges sit one folder per forge target
(`worlds/debug/cartridges/cgb/`, `worlds/debug/cartridges/dmg/`, `worlds/debug/cartridges/agb/`),
and the debug world's device row (`worlds/debug/devices.puck`, `devices.md`) lists
each device's shipped folder, then its `roms/` folder. Provider operations, a
swap among them, are refused while `replay.record` runs (`WhileRecording`).

**A local boot default lives in the folder, never the document (the owner's
ruling: ROM names never enter the repository, a build, a package, a log line
or a commit).** A folder may hold `folder.json`
(`WorldMachineContentFolder.SettingsFile`, schema `puck.content-folder.v1`,
one optional `default` naming a bare file in that folder; strict, refused by
name otherwise) and is never listed as content. A machine row with
`preferFolderDefault: true` (validated: it needs `contentFolders`) boots the
default of the first folder, in authored order, whose settings name one or
cannot be read (`WorldMachineContentFolder.TryDefault`), when the folder holds
it and the engine can mount it; otherwise it boots its authored content, and
the refusal is narrated on stderr by folder and settings file, never by the
default's name. `WorldMachineHost.TryPrepareInstances` resolves it on every
preparation (boot, `world.reload`, a machine-affecting mutation) through
`WithFolderDefault`, which swaps the configuration's `content.path`
(`MachineOperationValidation.ReplaceContent`). The host keeps the effective
declaration, so `machine.content` marks the default mounted and a reset
recreates it, while the document side never sees it: `CaptureInstances` and an
operation plan's `Current`/`Candidate` report the authored configuration
whenever the machine runs its folder boot (`Documented`), so the server's
candidate check, `world.save`'s capture and a replay tape's embedded definition
name the shipped cartridge. A replay re-boots from that definition and resolves
the folder default again; machine cores are outside the hash, so the tape
verifies the same with or without the local folders. A machine whose row the
candidate document shares by reference with the current one keeps what it
runs, so a swap the document adopted survives a later machine-affecting
mutation, while a reload, which re-reads every row, decides the folder default
again.
`machine.default <instance> <name>|none` (a local console or seat only) writes
the named file's folder's `folder.json` and names none in the machine's other
folders; `machine.content` echoes `preferDefault=`, `default=`,
`defaultRefused=` and each folder's `default=`/`settingsRefused=`.
`MachineContentFolderLawTests` holds the boot, reload, capture, swap and every
refusal.

## Read-backs

`screen.state`/`world.screens` tag a mirrored participant `p<n>(mirror)` (both
read `WorldEngagement.PlayersOn(screenIndex)`,
`IReadOnlyList<(int Display, bool Capture)>` — `Capture` is derived from own-body
membership). `body.channels` prints an
`applications=<target>[/<kit>](mask=0x…),…` segment ahead of the per-channel fold
breakdown; the own-body member is listed like any other, so its ABSENCE (capture)
is legible rather than inferred. `screen.state` ends its engagement segment with
`gun=off` or `gun=(<x>, <y>)`, the tick's merged aim as source fractions
(`WorldEngagement.PointerOn`), the same numbers `body.channels` prints for the
hit, then `pad=off` (no application reaches the screen) or
`pad=<buttons>/stick(<x>, <y>)` (`WorldEngagement.PadOn`): the merged pad the
machine receives this tick, its buttons as `MachineButtons` names joined by `+`
(`none` for none) and its left stick with +X right and +Y up, which every
host (`BrickPad`, `AdvancedPad`, `HgdPad`) reads as the d-pad. It is the
engine-neutral way to see what a device is pressed with; `screen.peek <screen>
0xC280` shows the brick's own joypad byte only under a forged cartridge (a local
folder default is not one).

## The device frame — a device's controls are the seat's own

The client rotates the movement pair into world axes before it reaches the wire
(`WorldClient.ComposeMoveFrame`), which is right for a body and wrong for a
device: a camera looking +Z would turn the stick's right into the pad's left.
So a seat whose authority holds it captured on a device composes the pair raw:
`WorldAuthorityEndpoint.DrivesDevice(body)`, answered for a local instance by
`WorldEngagement.DrivesDevice` (the set omits its own body, names no other body,
and names a screen the document declares or a tabletop placement). The rows and
the stick then sum unrotated and no facing is written, so a screen's pad and a
board's rules (`chess.puck` turns the seat's ahead and right by `board(side, …)`)
read the keys as pressed. A mirrored set, a possession and a portal face (a
screen with no row) keep camera-relative movement. A remote authority's endpoint
carries no such read, so a seat engaged on a remote world still composes
camera-relative. `ControlApplicationLawTests.OnlyACapturedDeviceSetDrivesADevice`
holds the predicate.

## Verifying

Run the game; drive `body.engage <target> [body] [capture:on|off]` with a
control pair: an actor holding `Control` over the target succeeds, a revoked
actor refuses loudly. For possession, grant Drive over the target body first
(`world.grant seatN drive body:<n>`) — Control alone moves nothing. The debug
world's device row declares engageable machine screens 0 to 2 (kit
`devicePad`, engage radius 3, engage channel `interact`, the Moth's context
button on E and the pad's X; `devices.md` has the stand-there `body.pose`
lines), so the screen path runs in the live world: drive the button through
the real input path with `player.signal keyboard.e press`, a `world.wait`, and
`release`, never `body.engage`, when the probe is what you are showing. Remember actor ≠ target: every seat holds wide grants by default, so
self-targeting discriminates nothing — revoke first, then prove the denial, then
re-grant and prove success. Remember body index vs. player index:
`world.grant … control body:<n>`/`drive body:<n>` is 0-based; `player.*` verbs are
1-based (`body:1` is "player 2").

In-process laws: `tests/Puck.World.Server.Tests/EngageAuthorityLawTests.cs` (the
compose and dissolve authority pairs) and `ControlApplicationLawTests.cs`
(default set, capture-as-membership, mirror, dissolve-restores-default,
revoke-driven dissolution, two-seat pad merge).

**The context-button (`EngageChannel`) shape**: a world with a ZERO-SCREEN boot
cannot gain a screen live for this — the render-provider capacity is probed and
frozen at boot, so a screen added by `world.row.set screens` carries no live
machine. Boot with the machine screen already declared (a scratch world copy,
or the debug world's device row), warp within
`engageRadius`, `body.press <channel>` — assert `screen.state` reads
`engaged=p<n>` and `world.contacts <n>` stays `grounded=true` (no jump); warp
away, press again — assert `world.contacts` reads `grounded=false` instead.
