# Climbing

The owner's ruling: "Yes, our climbing should be BotW/TotK quality or better!" The gallery's central tree is the
first place that needs it ([the toybox](toybox.md)): a player learns, with no words, that she can climb it and walk
its branches to other floors. This plan holds the research round, the systems Puck already had, the gaps a seat's
climb met in them, the design, and the open work. The ability is authored in the standard package
(`worlds/standard/abilities.puck`, `climbAbility`); the engine supplies only mechanisms any author can use for
another ability.

## What players feel in Breath of the Wild and Tears of the Kingdom

"Read" means the page was fetched and read in this round. "Summary" means only a search engine's summary of the page
was seen (the page refused the fetch), so the claim is unverified. "Recalled" is what the lane knows from play and is
also unverified.

| What the player feels | Source |
|---|---|
| Walls stopped being boundaries and became another path; the team called this the change with the most impact, and it produced the loop of preparing for a climb (food, stamina). | Read: [Shacknews on the GDC 2017 talk](https://www.shacknews.com/article/99265/how-breath-of-the-wild-challenged-conventions-in-zelda-games), [Fujibayashi's interview as reported](https://www.gonintendo.com/stories/275899-zelda-botw-devs-on-changing-direction-speedrunners-biggest-inf) |
| Physics is "the kind that fits the needs of the game", not a textbook's; the character is bonded to the wall, not simulated hanging from it. | Read: Shacknews, quoting Dohta |
| There is no free rest on a wall. A cut feature let the player stab a weapon into the rock and rest; it was removed because it could not work everywhere, so stamina alone limits a climb. | Read: [My Nintendo News](https://mynintendonews.com/2017/04/16/zelda-breath-of-the-wild-devs-scrapped-unconventional-stamina-saving-idea-after-miyamotos-feedback/) |
| Stamina drains at a steady rate while climbing; a jump on the wall costs a chunk more; a set bonus halves the jump's cost; gear raises climb speed without lowering the drain. | Summary: Zelda Wiki's Climbing and Climb Jump Stamina Up pages |
| Rain makes the climber slip back every few moves; the sequel adds gear that makes slips rare. Players name it the game's worst feature. | Summary: Zelda Dungeon's Slip Resistance page, a GamesRadar headline |
| Hanging still costs nothing and restores nothing; stamina refills on the ground; an emptied wheel turns red and must refill before it can be used. Jump leaps the way the stick points (up, sideways, or off the wall backwards); the run button lets go. At the top the climber's hands find the edge and she pulls up and steps on. | Recalled |
| The climb cycle is animated, with inverse kinematics keeping hands and feet on the surface, and blends back into the walk at the top. | Summary: the New Frame Plus video's transcript |
| The sequel's camera does not snap in when a wall blocks it, and returns only when the player moves it. | Read: [80 Level](https://80.lv/articles/third-person-camera-movement-trick-in-the-legend-of-zelda-tears-of-the-kingdom) |

## What other games chose

- **Jusant** (read: [Don't Nod's design post](https://dont-nod.com/?p=6122)): each trigger is one hand; stamina
  refills when the climber rests and shakes her arms out; a rope and pitons catch a fall, and nobody dies. After
  release the studio added a mode with no held buttons and a mode with no stamina, because holding triggers was hard
  for some players. Climbing is the whole game, so it asks more of the hands than a traversal verb should.
- **Assassin's Creed Shadows** (read: [Ubisoft's parkour overview](https://www.ubisoft.com/en-gb/game/assassins-creed/news/4TA6gKaTvtOC1mOjZIxCZd/assassins-creed-shadows-parkour-system-overview)):
  only surfaces with handholds are climbable, so designers can lay out routes; a ledge the character drops past is
  caught and held rather than fallen from. Older entries reportedly mark climbable edges by hand (summary only).
- **Unreal mantle write-ups** (read: [a ledge and mantle system](https://danimtz.github.io/devpost/Mantle)): a mantle
  is traces forward from the head downward to find the lip, a capsule test that the landing has room, then an
  animation in two parts, up and then in. The failure cases are all "the landing was not proven".
- **Shadow of the Colossus** (summary only): a grip meter that drains while holding and refills when let go, on
  surfaces that move.
- **Peak** (summary only): began from Breath of the Wild's climbing and made one stamina bar the whole game, with
  injuries taking space in the bar. A GDC 2026 paper on it was downloaded but could not be read here.

## What Puck already had

Rule 7's search, by capability. Each row is a system this plan uses rather than rebuilds.

| Capability | Owner |
|---|---|
| Hold a body on a wall or ceiling, suspend gravity, move in the face's plane | a kit's `holds` row of kind `Pull` (`WorldHold`, `FixedBodyHold`), resolved by `WorldBody.ResolveHold` and seated by `WorldBody.SeatToHold` |
| Take a wall by walking into it | the row's `onDrive` and `driveAlignment` (`WorldBody.TryTakeHoldOnDrive`) |
| Spend a body register while holding | the row's `spend` (`WorldHoldSpend`, `WorldBody.SpendHold`); the register is a `state.body` Counter (`ActionStateSlot`) |
| Which surfaces can be held | `collision.defaultHold` and a placement's `grip { holdable }` (`WorldPlacementGrip`), answered by `IContactField.TryHoldableSurfaceAlongDirection` in both providers |
| Prove a body can rise over an edge and stand on it | `FixedStepUp.TryStep`: raise, move, lower onto support, each through the body's own sweep |
| Move a body along a timed path that owns its translation | `FixedTimedMove` with `Exclusive` and a vector `Arc`, run by `WorldBody.ProposeTimedMove` |
| Buttons that act by the body's state | a kit's `actions` (`onPress`, `onFact`) with gates over facts, registers and timers (`BodyActionSpecFactory.CompileGate`) and effects (`planarImpulse`, `addState`, `startTimer`) |
| Know a body is on a wall | the `HoldingUnwalkable` fact (`ActionFact`), on the wire in `BodyFacts` |
| Hands and feet on the surface | a creation's `effectors` with a `surface` target and a `plant` window (`CreationEffector`), and drivers on the `travel` signal gated on `HoldingUnwalkable` (`CreationAnimation`) |
| A camera that changes with state | `selectProgram` and `blend` in a camera program, a `blend`'s weight bound to a state row whose `dynamics` trait eases what presentation reads (`worlds/standard/camera.puck`) |
| A stick that keeps its meaning while the camera orbits | held free look (`SeatController.FreeLooking`), which resolves movement against the body's heading in `WorldClient.ComposeMoveFrame` |
| A gauge on screen | a `hud` panel's `Gauge` element bound to a state row |

The standard package already had `crawlHold`, which the pond's spiders use to climb a den wall onto its ceiling
(`worlds/debug/garden-tests.puck`). That is a heading-framed creature driven square at its wall; a seat's climb
asks more of the same system.

## What the hold system lacked for a seat

A seat's kit is camera-framed (`moveFrame: World`) and its player drives at walls from any angle. Read against that,
the hold system had these gaps. Each is closed once, in the engine, as a mechanism any kit can use.

| Gap | What the engine does now |
|---|---|
| A camera-framed command was read as heading-framed on a face, so a wall facing east or west climbed sideways, and the take-on-drive probe turned the command by the body's heading a second time. | `WorldBody.CommandOnFace` lays a world-framed command onto a `Pull` face: the part pushing level into the wall climbs, the part pulling away descends, the part along the wall traverses. `WorldBody.PlanarCommand` is the one reading of the commanded direction. |
| A hold on tilted walkable ground replaced a world-framed walker's axes with "uphill". | A `Gravity` row leaves a world-framed body's axes alone. |
| The analytic provider answered a hold's probe with each collider's nearest point, kept only when its bearing matched the probe exactly, so a wall was taken only when driven at square on. | `FixedSurfaceQuery.TryCast` sphere-traces the same nearest-point primitive, so both providers answer the probe with a ray cast. |
| The top of a wall was an instant placement, onto holdable ground only. | A row's `mantle` proves a landing on any walkable ground and carries the body there over time. |
| Nothing stopped a climber walking off the side or the bottom of a face. | A row with a mantle guards its edges (`WorldBody.GuardHeldFace`). |
| A register drained while its holder hung still, and could only refill at once (`resetFact`). | `spend.restingRatePerSecond`, settled from where the step carried the body, and a row's `restore` with its own gate. |
| A hold row had no condition of its own. | A row's `when` gate over facts, registers, timers and held channels. |
| Brushing a wall took it. | A row's `driveSeconds`: a body on the ground takes a face only after driving into it that long; a body in the air takes it at once. |
| A press could not be read in a fact trigger, so one button could not mean "jump" on the ground and "leap" on a face. | `held(channel)` in an action gate. Under an input mapping that leaves the channel out it is never held. |
| An impulse ignored the face, and a climber kept the heading she arrived with. | An impulse under a pull lies in the face; `forward: Surface` faces the body into it. |
| A look that steers the body (`player.look.steer`, `player.steer`) turned a climber off her wall, and with her heading went the meaning of her keys. | A row whose `forward` is `Surface` owns the heading while it holds a face (`WorldBody.HoldOwnsHeading`): a commanded facing turns the camera alone. |
| A camera-framed stick changed meaning as the camera orbited: seen from the side, pushing forward traversed. | `views.seatControl.faceFrame: Body` resolves a seat's movement against its body's heading while the body holds a face (`WorldClient.ComposeMoveFrame`), the frame held free look already used. |
| An outside corner was an edge. | A row's `corner` finds the face past the corner, proves the way round with the body's sweep and carries the body there (`WorldBody.TryBeginCornerWrap`). |
| The edge guard read only the command, so a leap carried a body off the end of its face and its cost was wasted. | The guard reads the command and the impulse together (`WorldBody.GuardHeldFace`): a leap becomes the mantle where a landing is proven and stops at the edge where none is, and a firing whose impulse an edge would stop whole is refused, so it spends nothing (`WorldBody.ImpulseStopsAtFaceEdge`). |
| A blend between a near and a far orbit collapsed once a player zoomed, because the seat's distance replaced both. | A requested distance is read against the orbit the seat steers, and every other orbit keeps its proportion to it (`SdfCameraLook.OrbitDistance`). |

## The design

The invariant: **a mantle moves a body only to a landing the body's own sweeps proved.** One place enforces it:
`WorldBody.TryBeginMantle` is the only code that starts a mantle, and it starts one only from the landing
`FixedStepUp.TryStep` returned (raise, step in, lower onto walkable support, each through the body's own sweep, the
step a kerb takes), and only where `FixedStepUp.Stands` finds ground under her axis at the landing, so she stands on
it rather than resting on its far edge and rolling off. A top narrower than the landing's depth is no landing, and she
hangs under it. The move then runs through the ordinary swept contact step, as every timed move does. The
guarantee stops at the path between: the move follows a parabola through the top of the proven raise and not the
proven L itself, so contact may shorten it, and a shortened mantle leaves her on the wall or in the air above the
ledge, never inside geometry.

A corner wrap holds the same invariant: **a wrap moves a body only along a way its own sweeps proved, onto a face the
row's own probe found behind where they ended.** `WorldBody.TryBeginCornerWrap` is the only code that starts one. It
casts back at the corner from beyond it, sweeps the body out along the traverse and then in along the face it found,
and probes that face again from where the sweeps ended. The same limit applies: the move follows a parabola through
the corner of the proven path, so contact may shorten it, and a shortened wrap leaves her hanging off the new face or
falling beside it.

How a climb runs, tick by tick:

1. **Take.** `ResolveHold`'s drive pass casts along the commanded direction. A holdable face inside the row's cone,
   driven into more squarely than `driveAlignment`, is taken at once in the air and after `driveSeconds` on the
   ground. The row's `when` gate and its `spend` register must allow it.
2. **Climb.** The command is laid onto the face and the body travels at the row's `speed`. `SeatToHold` keeps it one
   body radius off the face. Each step the face is probed one body extent ahead of every direction the command
   moves her; where it ends, that part of the command is removed, so she hangs at an edge.
3. **Spend.** At the end of the step the register drains at the moving rate if the step carried her along the face,
   and at the resting rate otherwise.
4. **Mantle.** While she drives or leaps up and the face ends within `mantle.height` of her feet, a landing is proven
   and she is carried onto it. No row holds her meanwhile, and the row's `mantle.state` timer runs.
5. **Corner.** While she drives or leaps sideways and the face ends one body radius ahead, the row's `corner` looks
   for the face past the corner, proves the way round and carries her onto it, still held and turned to face it.
6. **Let go.** A closed `when` gate (the regrab timer, winded) or an emptied register drops the row, and her climb's
   momentum carries into the fall.

The authored ability, `climbAbility` in `worlds/standard/abilities.puck`: the `climb` row (walls from 60 to 120
degrees), the `stamina` register, the `winded` lock an emptied wheel sets until it has refilled, the leap on the jump
channel, letting go on the run channel with the `climbRest` timer, the mantle and the corner. `staminaRestore()` is
the restore an avatar's ground and fall rows author, and `staminaGauge()` the wheel on screen. Every number is a
template parameter; the Moth's import (`worlds/debug/avatars/moth-climb.puck`) takes the defaults. Her rig raises her
arms to the wall and plants palms and toes in turn on the `climb` driver's cycle: a hand and the opposite foot hold
together, the foot an eighth of a cycle behind its hand (`cling` and `climb` drivers, the `hand-*` and `toe-*`
effectors in `worlds/debug/avatars/moth.puck`).

The controls follow from the mechanisms, with no rule of their own. The row faces her into the wall and owns her
heading there, and the chase camera's `faceFrame: Body` reads her stick in her own frame while she holds a face, so
W or the stick pushed forward climbs, back descends and sideways traverses, wherever the camera has been turned.
Space leaps the way she is moving. Left Shift lets go.

The camera is the standard chase camera's wall framing (`chaseCamera(wall: wallFraming())` in
`worlds/standard/camera.puck`): each clearance case blends its ground framing with a framing that stands further back
and looks up the face from below her shoulders, weighted by the `cameraOnWall` row. A rule sets that row from the
`HoldingUnwalkable` fact and its `dynamics` trait eases what the rig reads, so the camera drifts out as she takes a
wall and back as she lets go or mantles. The right stick and the mouse orbit it as on the ground.

## What is evolved, not copied

- **Edges hold.** The original lets a climber shuffle off the side of a face and fall. Here a face's edge stops her,
  a ledge pulls her up, as a caught ledge does in Assassin's Creed, and an outside corner carries her round.
- **A leap is never wasted.** A leap that would run off the top becomes the pull-up where the top can be stood on,
  and is not made, at no cost, where the face ends at once.
- **The mantle is proven.** Other engines play a canned animation over a capsule test. Puck's lands only where the
  body's own sweep stood it, on ground no author marked.
- **A grab is deliberate on the ground and instant in the air.** The dwell removes the accidental grab that walking
  along a wall causes, and keeps the catch when she falls past a face. The ability's own gate adds that a hop is not
  a fall: within a fifth of a second of leaving the ground she takes nothing, so stepping over a root does not catch
  its flank.
- **Rest is the surface's choice.** Hanging is free, as in the original. The two drain rates are authored per row,
  so a world can make one face cost a little to hang on and another nothing.
- **No slip tax.** Rain slipping is the feature players name as the worst. A wet surface is a `when` gate or a row
  with a higher drain, so weather can matter without taking control away.
- **Grip surfaces are a field question.** Both providers answer the hold probe by tracing distance: the field
  provider marches the signed-distance field that draws the world, and the analytic provider sphere-traces its
  colliders, each step skipping all the space the distance proves empty. A carved or blended creation is climbable
  where it is drawn, with no hand-placed markers.

## Coordinating with the tree

The tree's lane owns its geometry (`gallery/tree.puck`). Its placement is `solid`, and `gallery/shell.puck` adds
`grip { holdable: true }` to it, so its trunk and limbs hold a climber and its treads and branch tops are ground she
walks. Headless, she takes the trunk from its east side and climbs until a tread above stops her; from the west and
the north something at the trunk's foot stops her before she reaches bark she can hold. A look at the tree with the
climb in mind is owed: which stretches of trunk are a route, and whether the treads leave room to climb between
them. Three properties of the climb bear on its shape:

- A face is tracked while its normal stays within 60 degrees of the one she holds, so a round trunk is one face and
  relief deeper than about a hand (0.1 m) with sharp sides reads as separate faces.
- Faces from 60 to 120 degrees from up are climbed. Branch tops within 60 degrees of level are walked, and a mantle
  lands on them whether or not they are holdable.
- A mantle needs a landing 0.8 m in from the edge with her height clear above it and the top still under her middle
  there: a branch thinner than about 0.45 m in the direction she climbs is held, not stood on.

The gallery's ground-floor walls are holdable (`gallery/shell.puck`). The upper floors' are not: level 1's so that
the toybox is not reached round its lattice, and the others until the tree gives a route to them. Most of the long
walls are window bays, which are not holdable; the plain stretches are the south face either side of the entrance
and the west wall.

The gallery's climbing wall is the climb built for it: a 12.95 m face from the nave's floor to a ledge level with
level 3, in front of the north balcony edge ([gallery.md](../../worlds/debug/gallery.md#the-climbing-wall)). Only its
face is holdable, a flat backing under drawn plates and holds, so the climb tracks one plane, stops at the face's
edges and ends in a mantle onto the ledge, which is deep enough to set her down whole. Its holds are drawn only: the
hold system climbs a face, not individual holds, so a route's holds guide the eye and the rig's hands rather than
gate where she can climb.

## Open work

- **A look pass on her climb and its camera.** The `cling` pose, the hand and toe effectors and the wall framing's
  numbers (`wallFraming` in `worlds/standard/camera.puck`) are authored blind and have not been seen in a window. Her
  legs keep the air pose's tuck under the toe effectors. A mantle pose can read the `mantling` timer through a
  mirrored row, and a wrap has no pose of its own.
- **Corners the wrap does not take.** A wall thinner than the row's corner depth ends in an edge, and so does a
  corner whose far face lies beyond the row's `reach`. A top corner (the end of a face that is also near its top) is
  taken as whichever of mantle and wrap the command reaches first.
- **A kit-level trigger list.** A kit's own binding for a channel replaces an imported one, so the ability carries
  its fact triggers under `run` and an avatar with its own `run` action must list them. Fact triggers belong to no
  channel; a list of their own would let abilities compose.
- **The wheel per seat.** `staminaGauge` mirrors one row for the world. Several seats need a row keyed by body and a
  panel that reads its seat's key, as the camera's `cameraOnWall` row is.
- **Wet and icy surfaces** as a grip class per material ([the composition plan](debug-world-composition.md) records
  the precedence the owner asked for), and a rule that raises the drain on them.
- **A vault** for walls between her step height and her waist, which need a jump first.
- **Hands on holds.** Her hand and toe effectors plant where their surface probe lands on the drawn face, so on the
  gallery's climbing wall they meet a hold only by chance. An effector target that seeks the nearest grip a world
  marks (a hold's placement, or a material that carries a grip class) within its reach would put her hands and feet on
  the holds a route sets, and a route could then be climbed as set rather than only followed by eye.
- **The analytic cast's cost.** Each step of a cast reads every holdable collider. A world that makes everything
  holdable wants the holdable set behind the collider grid the contact solve uses (`FixedColliderGrid`).

---

[Plans](README.md) · [The toybox](toybox.md) · [The debug world as one valley](debug-world-composition.md)
