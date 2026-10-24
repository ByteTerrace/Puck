# Debug world addons

The debug world mounts three WebAssembly addons, each authored as WebAssembly
text under [addons/](addons) and mounted by its own module beside the others,
with its grants authored there too. A text module is source: nothing is compiled
into the package, the module's `hash: "auto"` pins its text, and the World
converts it when it mounts. The addon runtime, its ABI and its authority rules
are in [the addons reference](../../.claude/skills/puck-world/references/addons.md).

| Addon | Module | Guest | What it does |
|---|---|---|---|
| Watchdog | [watchdog.puck](watchdog.puck) | [addons/watchdog.wat](addons/watchdog.wat) | Checks invariants every tick and records each violation with its tick |
| Gallery guide | [guide.puck](guide.puck) | [addons/guide.wat](addons/guide.wat) | Names the exhibit the Moth stands at on a HUD panel |
| Companion | [companion.puck](companion.puck) | [addons/companion.wat](addons/companion.wat) | A stand-in body, body 42, that follows the Moth |

`world.addons` lists all three with their fuel. Measured on a headless run, each
spends a small part of the default budget of 1,000,000 a tick: the watchdog
about 8,000, the companion 1,200 to 2,500, and the guide about 60, rising to
about 30,000 on the tick it writes an exhibit near the end of its table.

## The watchdog

The watchdog reads the pose of every body but the chess pieces every tick, and
of four pieces a tick in turn, and listens for collision begins. It records:

| Violation | When |
|---|---|
| `below-floor` | A body more than 1 m under y = 0, the lowest floor anything stands on; `fall-respawn` only catches a body at y = -40 |
| `unnormalised-orientation` | A body's orientation quaternion more than 1/64 from unit length |
| `piece-overlap` | Two chess pieces (bodies 8 to 39) begin to overlap |
| `companion-overlap` | The Moth (body 0) and the companion (body 42) begin to overlap |

The pieces and both walkers are `Solid` kits, whose contact solve separates two
of them within the tick, and the event feed reads the settled tick, so the two
overlap checks fire only when that solve leaves two of them overlapping; a pose
cannot force one. Collision begins with other bodies (the Moth inside the lift
car's sensor sphere, say) reach the watchdog and are not violations.

Each counts once as it starts, and again only after it has cleared. A violation
records the tick the reading describes: the tick that settled before the batch
the guest read it in.

Two state rows hold the record, and `world.state` reads them over MCP:

- `watchdog`, numbers: `count`, `tick` (the last violation's) and `first` (the
  first's), -1 before any;
- `watchdogLog`, text: `first` and `last`, as lines such as
  `t=61 below-floor body=0 y=-3.01`.

The `watchdog` HUD panel shows the count and the last line while there is one, once the addon HUD toggle is on:
every addon panel stays hidden until `world.state.cell.set addonHud $value 1` ([addon-hud.puck](addon-hud.puck)), and
the rows are readable with `world.state` either way.

### Finding a transient and replaying to its tick

No checkpoint holds a guest's memory, so a recording in this world starts at
boot: `replay.record <name>` must arrive before the world steps, as the first
line of the stdin the World starts with. An MCP attachment arrives after the
first step, too late to arm one, so start the World with that line on stdin and
`--control`, and drive it over MCP from there. Then:

1. Play or script until the watchdog fires, and read `world.state watchdog`
   for the tick.
2. `replay.stop` persists the tape (MATCH proves the guests replayed with it).
3. `replay.drive <name> to <tick + 2>` resets the world to its boot image,
   prepares every guest fresh and re-runs the recording, stopping just after
   the violation. The watchdog writes its rows during tick + 1, so they show
   the violation at the same tick from then on. A drive works in the session
   that recorded the tape, and its ticks are the recording's, so a `world.at`
   armed during the drive (`{"start": <tick - 1>, "steps": […]}`) reads the
   world at exact ticks around the violation; after the drive ends, the world
   stays where it stopped for `body.where`, captures and `world.state`.

Measured over MCP: a violation recorded at t=445 was driven back with the
watchdog reading count 0 at ticks 444 and 445 and count 1, tick 445, at 446,
as it had live.

A forced violation, as `addons-tests.puck` and the MCP check use it:
`body.pose 5 -3 5 0 0 0 0` stands the Moth 3 m under the ground, and the
watchdog records `below-floor` for body 0 on the next settled tick.

## The gallery guide

Every exhibit in the gallery hall has a region in [guide.puck](guide.puck): a
sphere of 1.29 m radius on the level's floor, 1.7 m out from the plinth on its
label's side, where the exhibit's spawn point stands. The guide observes every
region; when the Moth enters one it writes the exhibit's name and a one-line
note into the `guide` row (and its walking-order position into `guideAt`), and
when she leaves it clears them and the panel hides. A region event names its
region by its position in the guide's own requests, which list the exhibits in
the gallery's walking order; the guest's table of names and notes follows the
same order, so an exhibit added to, moved in or removed from the gallery moves
its line in [addons/guide.wat](addons/guide.wat) too. `gallery.go <exhibit>`
stands her at one.

## The companion

The companion stands in for a second player: a plain grey-teal body under the
Moth's `walker` kit, starting at (-2, 0, 2.5). Each tick it reads both poses,
drops a breadcrumb wherever she has moved half a metre, and walks the crumbs in
order, so it takes the stairs and doors she took. It stops 2 m from her. A crumb
more than 6 m from the one before, which a teleport makes, restarts the trail
from her, and when it makes no progress for a second at full speed it jumps once
and skips a crumb. Measured headless: posed 14 m away it closes to 2.5 m in
about three seconds; it trails her by 2.25 m while she walks at 4.25 m/s and
settles 1.9 m from her when she stops; and it follows her up both flights of
the stairs to level 1, stopping 1.9 m from her.

It cannot follow a teleport (`debug.go`, `gallery.go`, a respawn): a guest
drives a body through intents only and cannot pose it, so the companion walks
the straight line to her new place.
