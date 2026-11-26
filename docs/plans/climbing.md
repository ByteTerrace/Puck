# Climbing

The owner's ruling: "Yes, our climbing should be BotW/TotK quality or better!" The gallery's central tree is the
first place that needs it ([the toybox](toybox.md)): a player learns, with no words, that she can climb it and walk
its branches to other floors. This plan holds the research round, what Puck already had, what was missing, and what
was built. The ability is authored in the standard package (`worlds/standard/abilities.puck`, `climbAbility`); the
engine supplies only mechanisms any author can use for another ability.

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
| Buttons that act by the body's state | a kit's `actions` (`onPress`, `onFact`) with gates over facts, registers and timers (`WorldBodyGate`) and effects (`planarImpulse`, `addState`, `startTimer`) |
| Know a body is on a wall | the `HoldingUnwalkable` fact (`ActionFact`), on the wire in `BodyFacts` |
| Hands and feet on the surface | a creation's `effectors` with a `surface` target and a `plant` window (`CreationEffector`), and drivers on the `travel` signal gated on `HoldingUnwalkable` (`CreationAnimation`) |
| A camera that changes with state | `selectProgram` in a camera program (`worlds/standard/camera.puck`) |
| A gauge on screen | a `hud` panel's `Gauge` element bound to a state row |

The standard package already had `crawlHold`, which the pond's spiders use to climb a den wall onto its ceiling
(`worlds/debug/garden-tests.puck`). No seat had ever climbed.

## What was missing

Reading the hold system against a seat's kit (camera-framed movement, `moveFrame: World`) found defects and gaps.
Each is a cause, fixed in the engine once.

1. **A camera-framed command was read as a heading-framed one on a face.** The seat sends its stick already turned
   into world axes. On a held face the hold read "advance" as "up the face", so a wall facing east or west climbed
   sideways, and the probe that takes a wall on drive turned the command by the body's heading a second time.
2. **A hold on tilted walkable ground replaced the walker's axes** with "uphill", so a holdable ramp or branch
   turned her controls.
3. **The top of a wall was a teleport onto holdable ground only.** A wall whose ledge was ordinary floor left the
   climber falling and re-grabbing at the lip.
4. **Nothing stopped a climber leaving the edge of a face** sideways or downwards; she fell and re-grabbed.
5. **Stamina drained while hanging still, and could only refill at once** (`resetFact`).
6. **A hold row had no condition of its own**, so "not while winded" or "not for a moment after letting go" could
   not be authored.
7. **A press could not be read inside an action's fact trigger**, so one button could not mean "jump" on the ground
   and "leap up the wall" on a face without one kit owning both.
8. **An impulse ignored the face**: a leap on a wall went sideways into the air.

## The design

The invariant: **a climbing body is never moved through space its own sweep has not proved clear.** The mantle is
the only new motion, and it is enforced in one place: `WorldBody.TryBeginMantle` starts a mantle only from the
landing `FixedStepUp.TryStep` proved with the body's own sweeps, and the move then travels through the ordinary
swept contact step like every timed move. The guarantee stops at the path between: the move follows a parabola
through the proven corner, not the proven L itself, so contact may shorten it, and a shortened mantle leaves her on
the wall or in the air above the ledge, never inside geometry.

Engine mechanisms (generic, each usable by any kit):

- **A world-framed command rides a held face.** On a `Pull` face the part of the command pushing into the wall climbs
  it, the part pulling away descends, and the part along the wall traverses (`WorldBody.CommandOnFace`). With the
  camera behind her this is "stick up climbs", and it stays predictable when the camera orbits, because both terms
  are on screen. A hold gravity keeps (the ground) no longer replaces a world-framed walker's axes.
- **`spend.restingRatePerSecond`**: the drain while nothing is commanded. Omitted, it equals the moving rate.
- **`restore { state, ratePerSecond }`**: a hold row refills a register while it holds. The ground restores what the
  wall spends.
- **`when`** on a hold row: a gate over facts, registers, timers and held channels. A row whose gate is closed is
  neither taken nor kept.
- **`mantle { height, depth, seconds, state }`** on a `Pull` row. While the climber drives up and the face ends
  within `height` above her feet, the engine proves a landing `depth` in from the lip and carries her there over
  `seconds`, rising first and then stepping in. `state` names a Timer register the engine starts, so rules and the
  rig can tell a mantle from a climb. A row with a mantle also **guards its edges**: where the face ends and no
  landing is proven, the part of the command that would leave the face is removed, so she hangs at an edge.
- **`held(channel)` in an action gate**, so an `onFact` trigger in `Edge` mode is a press that only exists in one
  state.
- **An impulse on a held face lies in the face.**

The authored ability, `climbAbility` in `worlds/standard/abilities.puck`:

- A `climb` hold: walls from 60 to 120 degrees, taken by walking or falling into them, at its own climb speed.
- A `stamina` register spent while moving on the face, held while hanging, restored on the ground.
- Winded: an emptied register locks climbing until it has refilled, as the red wheel does.
- Jump on the face leaps the way the stick points for a chunk of stamina. Run lets go, and a short timer keeps her
  from grabbing the same wall again at once.
- The mantle at the top.

Every number is a template parameter with a default the standard chose; the Moth's import
(`worlds/debug/avatars/moth-climb.puck`) sets hers.

## What is evolved, not copied

- **Rest is a choice the surface offers, not a cut feature.** Hanging still is free, as in the original. Because the
  drain's two rates are authored per row, an author can make a ledge-less wall cost a little while resting, or a
  rope cost nothing.
- **No slip tax.** Rain slipping is the feature players name as the worst. Wet surfaces are a `when` gate or a
  second row with a higher drain, so a world can make rain matter without taking control away.
- **Edges hold.** The original lets a climber shuffle off the side of a face and fall. Here a face's edge stops her
  and a ledge pulls her up, the way a caught ledge does in Assassin's Creed.
- **Grip surfaces are a field question.** Both contact providers answer the directed hold probe from the same
  signed-distance field that draws the world, so a carved or blended creation (the tree) is climbable where it is
  drawn, with no hand-placed markers. The probes are interval-marched rays: the field's distance bound lets a probe
  skip empty space rather than step it.
- **The mantle is proven, not animated over.** Other engines play a canned animation and hope the capsule fits.
  Puck's lands only where the body's own sweep stood it.

## Coordinating with the tree

The tree's lane owns its geometry. For the tree to be climbable its placement needs `solid` and
`grip { holdable: true }`, and nothing else: bark relief deeper than about a hand (0.1 m) reads as separate faces to
the track probe, so keep the trunk's collision surface smoother than that. Branch tops within 60 degrees of level
are walked, not climbed. The gallery's walls on levels 0, 2 and 3 are holdable (`gallery/shell.puck`); level 1's are
not, so the toybox's size puzzle cannot be climbed around.

## Open work

- The climbing camera: a seat rig case selected while she holds a face (pulled back, looking up the wall), and the
  sequel's "do not snap in" clearance.
- The stamina wheel: a `Gauge` on a rule-mirrored row, drawn beside her only while it is not full.
- The Moth's climb animation: a `travel` driver for the reach cycle, hand and foot effectors planted on the face, a
  mantle pose on the `mantling` timer.
- Outside corners: carry a traverse round a convex corner onto the next face (the edge guard stops her there today).
- Wet surfaces as an authored grip class, per material rather than per placement
  ([the composition plan](debug-world-composition.md) records the precedence the owner asked for).
- A vault for walls between her step height and her waist, which today need a jump.

---

[Plans](README.md) · [The toybox](toybox.md) · [The debug world as one valley](debug-world-composition.md)
