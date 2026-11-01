# The garden's pond plot

The garden ([garden.puck](garden.puck)) is the destination behind the gallery
wing's garden painting ([debug.md](debug.md)). West of its two frames it grows a
pond plot, the creature garden: a pack of hounds, a den of spiders, a swarm of
dragonflies and a school of fish. Every creature is a Puck program the engine
only schedules ([pond.puck](pond.puck)):

- **The hounds** flock (`ProduceFlockIntent`) round their companion, a target
  register a decision rule (`choose-companion`) points each second at the
  visitor once she stands within six metres, at the pack-mate the hound trusts
  most, or at no one.
- **The spiders** stalk the visitor over the plot's navigation domain (`plot`)
  once she is within ten metres (`stalk-visitor`), turn to face her
  (`FaceSensorTarget`) and stand off her.
- **The crawl.** Driven into a wall or a ceiling, a spider takes the standard's
  crawl hold (`crawlHold` in [abilities.puck](../standard/abilities.puck),
  expanded for the spider kit in [pond-crawl.puck](pond-crawl.puck)): a `Pull`
  hold that lays it on the face and spends its `stamina` Counter, three seconds
  of it, refilled the moment it stands on the ground. The den is two walls and
  a roof 2.6 m up, a ceiling it crosses upside down.
- **The dragonflies** flock in the air over the pond (`reeds`, a volume
  domain). **The fish** school inside the pond's walls (`shallows`); the garden
  has no water field yet, so they hover in air on a full-lift hold, and their
  row becomes a `Medium` hold when the pond holds water (the water lane).
- **The rumour.** A hound standing on the bone claims it for three seconds
  (the interaction `bone-claim`). A hound that sees the holder within nine
  metres trusts it more (`witness-claim`, a cell a pair in `boneHolderTrust`),
  the first to see it reports it (`first-witness`, `reporter`), a hound that
  cannot see the holder but stands within six metres of the reporter hears the
  claim at half a witness's share (`rumor`, once a claim through
  `boneHolderClaimMark`), and the claim expires (`claim-expire`). A hound
  aligns with a pack-mate in proportion to that trust (the pack's
  `alignmentAffinity`), and follows the one it trusts most. On open ground
  every hound sees the holder, so the rumour needs something taller than a
  hound between them: the hedge south of the bone (the pond's knee-high walls
  are too low).

## Where things are

| What | Where |
|---|---|
| The pond: knee-high walls round a 9 m basin | centre (-12, -16) |
| The hounds' yard | (-18, -8) |
| The bone | (-20, -11), a 1.4 m region beside the yard |
| The hedge: 5 m long, 1.6 m high, running east to west | (-19.5, -13.8) |
| The den: back wall, side wall and roof | (-21, -27), open to the north and east |
| The plot's navigation domain | x -30 to 6, z -34 to -2 |

From the arrival frame (3, 0, -8) the plot is ahead and to the right.

## The count

The plot's one parameter is `pondFlocks(creatures)`, the creatures in all
([pond-flocks.puck](pond-flocks.puck) sets the garden's 32). The six hounds and
four spiders are fixed; the dragonflies and fish share the rest. The rules key
the hounds and spiders by body number, and the population numbers inhabitants
from the top of its capacity down, the first placement highest, so the hounds
and spiders are the last placements: the spiders are bodies 8 to 11 and the
hounds 12 to 17 at every count, just above the eight the basis keeps for seats
and travellers.

[garden-crowd.puck](garden-crowd.puck) is the garden at 128 creatures, the
parallel tick's forcing workload. It and the scenarios drop the garden's two
frames, each of which would otherwise boot and tick the whole debug world beside
the plot through its window.

## Watch it

Boot `worlds/debug/garden-tests.puck` headless (`--devices none --headless`)
and pipe this script in. It stands the visitor on the bone, within six metres of
the yard, and hound 17 behind the hedge. `body.targets 12` answers
`companion=body:0` with the hounds gathered round her; `eventSeq` counts the
claims, `boneHolderTrust` holds a cell per witness at 0.3 of the way to full
trust and hound 17's rumour cell at 0.15, and `boneHolderClaimMark` holds the
claim hound 17 heard. Stood ten metres from the den, the visitor is then
stalked: spider 8 answers `navigation=arrived` beside her.

```text
body.pose -20 0 -11.6 0 0 0
world.wait 60
body.pose -19.5 0 -15 0 0 0 17
world.wait 120
body.targets 12
body.where 12
world.state eventSeq
world.state boneHolderTrust
world.state boneHolderClaimMark
body.pose -18 0 -18 0 0 0
world.wait 300
body.where 0
body.where 8
body.targets 8
quit
```

`boneHolder` and `reporter` name a hound only while a claim lasts and read -1
between claims.

`puck test worlds/debug/garden-tests.puck --reproduce` runs the plot's
scenarios (the pack and the bone, the rumour behind the hedge, the stalker, the
crawl) and replays each bit for bit; `puck test worlds/debug/garden-crowd.puck
--reproduce` does the pack's among the 128.
