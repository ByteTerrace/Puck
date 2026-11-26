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
| A camera that changes with state | `selectProgram` in a camera program (`worlds/standard/camera.puck`) |
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

## The design

The invariant: **a mantle moves a body only to a landing the body's own sweeps proved.** One place enforces it:
`WorldBody.TryBeginMantle` is the only code that starts a mantle, and it starts one only from the landing
`FixedStepUp.TryStep` returned (raise, step in, lower onto walkable support, each through the body's own sweep, the
step a kerb takes). The move then runs through the ordinary swept contact step, as every timed move does. The
guarantee stops at the path between: the move follows a parabola through the top of the proven raise and not the
proven L itself, so contact may shorten it, and a shortened mantle leaves her on the wall or in the air above the
ledge, never inside geometry.

How a climb runs, tick by tick:

1. **Take.** `ResolveHold`'s drive pass casts along the commanded direction. A holdable face inside the row's cone,
   driven into more squarely than `driveAlignment`, is taken at once in the air and after `driveSeconds` on the
   ground. The row's `when` gate and its `spend` register must allow it.
2. **Climb.** The command is laid onto the face and the body travels at the row's `speed`. `SeatToHold` keeps it one
   body radius off the face. Each step the face is probed one body extent ahead of every direction the command
   moves her; where it ends, that part of the command is removed, so she hangs at an edge.
3. **Spend.** At the end of the step the register drains at the moving rate if the step carried her along the face,
   and at the resting rate otherwise.
4. **Mantle.** While she drives up and the face ends within `mantle.height` of her feet, a landing is proven and she
   is carried onto it. No row holds her meanwhile, and the row's `mantle.state` timer runs.
5. **Let go.** A closed `when` gate (the regrab timer, winded) or an emptied register drops the row, and her climb's
   momentum carries into the fall.

The authored ability, `climbAbility` in `worlds/standard/abilities.puck`: the `climb` row (walls from 60 to 120
degrees), the `stamina` register, the `winded` lock an emptied wheel sets until it has refilled, the leap on the jump
channel, letting go on the run channel with the `climbRest` timer, and the mantle. `staminaRestore()` is the restore
an avatar's ground and fall rows author, and `staminaGauge()` the wheel on screen. Every number is a template
parameter; the Moth's import (`worlds/debug/avatars/moth-climb.puck`) takes the defaults, and her rig raises her arms
to the wall and plants each palm in turn (`cling` and `climb` drivers, two hand effectors).

The controls follow from the mechanisms, with no rule of their own. Her keys are heading-framed and she faces the
wall, so W climbs, S descends and A and D traverse. Her stick is camera-framed, so pushing it toward the wall on
screen climbs. Space leaps the way she is moving. Left Shift lets go.

## What is evolved, not copied

- **Edges hold.** The original lets a climber shuffle off the side of a face and fall. Here a face's edge stops her
  and a ledge pulls her up, as a caught ledge does in Assassin's Creed.
- **The mantle is proven.** Other engines play a canned animation over a capsule test. Puck's lands only where the
  body's own sweep stood it, on ground no author marked.
- **A grab is deliberate on the ground and instant in the air.** The dwell removes the accidental grab that walking
  along a wall causes, and keeps the catch when she falls past a face.
- **Rest is the surface's choice.** Hanging is free, as in the original. The two drain rates are authored per row,
  so a world can make one face cost a little to hang on and another nothing.
- **No slip tax.** Rain slipping is the feature players name as the worst. A wet surface is a `when` gate or a row
  with a higher drain, so weather can matter without taking control away.
- **Grip surfaces are a field question.** Both providers answer the hold probe by tracing distance: the field
  provider marches the signed-distance field that draws the world, and the analytic provider sphere-traces its
  colliders, each step skipping all the space the distance proves empty. A carved or blended creation is climbable
  where it is drawn, with no hand-placed markers.

## Coordinating with the tree

The tree's lane owns its geometry. For the tree to be climbable its placement needs `solid` and
`grip { holdable: true }`. Three properties of the climb bear on its shape:

- A face is tracked while its normal stays within 60 degrees of the one she holds, so a round trunk is one face and
  relief deeper than about a hand (0.1 m) with sharp sides reads as separate faces.
- Faces from 60 to 120 degrees from up are climbed. Branch tops within 60 degrees of level are walked, and a mantle
  lands on them whether or not they are holdable.
- A mantle needs a landing 0.8 m in from the edge with her height clear above it.

The gallery's ground-floor walls are holdable (`gallery/shell.puck`). The upper floors' are not: level 1's so that
the toybox is not reached round its lattice, and the others until the tree gives a route to them. Most of the long
walls are window bays, which are not holdable; the plain stretches are the south face either side of the entrance
and the west wall.

## Open work

- **The climbing camera.** A `blend` in the seat rig between the chase framing and a climb framing (a longer boom,
  looking up the wall), weighted by an eased row a rule sets from `HoldingUnwalkable`. `chaseCamera`
  (`worlds/standard/camera.puck`) owns `views.seatRig`, so the climb framing is a parameter of it.
- **A look pass on her climb.** The rig's `cling` pose and hand effectors are authored blind and have not been seen.
  Her legs keep the air pose's tuck: a second effector on a leg would fight the foot's own, so feet on the wall need
  the foot effector's gate widened instead. A mantle pose can read the `mantling` timer through a mirrored row.
- **Outside corners.** The edge guard stops her at a convex corner. Carrying a traverse round it is a second proven
  move, built as the mantle is.
- **A kit-level trigger list.** A kit's own binding for a channel replaces an imported one, so the ability carries
  its fact triggers under `run` and an avatar with its own `run` action must list them. Fact triggers belong to no
  channel; a list of their own would let abilities compose.
- **The wheel per seat.** `staminaGauge` mirrors one row for the world. Several seats need a row keyed by body and a
  panel that reads its seat's key.
- **Wet and icy surfaces** as a grip class per material ([the composition plan](debug-world-composition.md) records
  the precedence the owner asked for), and a rule that raises the drain on them.
- **A vault** for walls between her step height and her waist, which need a jump first.
- **A leap past a face's top** is not guarded: she rises off the face, falls and catches it again, the leap's
  stamina spent.
- **The analytic cast's cost.** Each step of a cast reads every holdable collider. A world that makes everything
  holdable wants the holdable set behind the collider grid the contact solve uses (`FixedColliderGrid`).

---

[Plans](README.md) · [The toybox](toybox.md) · [The debug world as one valley](debug-world-composition.md)
