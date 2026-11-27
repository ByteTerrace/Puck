# The gallery's visual pass

The gallery is starting to speak for itself. This pass makes it tasteful: futuristic but cartoony, warm and handmade,
and drawing on outside influences and futurist design rather than restating what is already built. It is judged
against [the visual bar](../game/art/README.md#the-visual-bar) and run as the loop below.

## How the pass works

The owner's ruling: rapid visual feedback matters most. Leads have got stuck in the weeds, coding for an hour where
quick bursts would have served better. When an authoring wall appears, the pass takes a short break and a lane
builds the missing feature properly.

- **One World stays up.** Boot the gallery once (`--world worlds/debug/gallery-root.puck`), edit `.puck` and watch
  it reload. A content change never needs a rebuild or a restart.
- **The same shots every time.** Each place keeps a few camera bookmarks (`world.pins save`, restored with `pins`
  on the next start), so every change is judged from the same views against its reference.
- **A burst is one idea:** change, capture, compare with the reference, then keep it or revert it. A burst that
  runs past about a quarter of an hour has stopped being a burst; stop and ask why.
- **A wall is obvious:** reaching for a workaround, faking the system a look depends on, or a second attempt at an
  effect that will not land. Name it, give it to a lane, and move to another place while the lane builds it.
  Never sculpt around it.
- **Two machines, two jobs.**
  - The RTX 4070 carries the art and the authoring gaps, because the live loop needs its fast bakes and the visual
    bar is judged there.
  - The RTX 2060 machine carries the rendering bugs (below). Diagnosing them from captures and debug views works on
    either GPU, and the art machine sees each fix as it lands.

## What comes first

The display stations stay as they are; they are iterated in their own root worlds (`labs/shapes.puck`,
`game-station-root.puck`).

1. **The tree.** It is already the staircase: its branches are the paths to most of the gallery. The main problem
   is how well that pathing is built and how it feels to walk. Work on:
   - how a branch reads as a path, and how it meets a balcony;
   - steps, junctions and rails;
   - the canopy;
   - whimsy: lanterns, nests, platforms, a lookout.

   Climbing is already an ability ([climbing](climbing.md)); feel counts as much as looks.

   The walkway has a route to watch and a test that walks it. `world.benchmark run tree-walk` plays the `tree-walk`
   route (`worlds/debug/benchmark-route.puck`) through seat 1's own controls: from the nave floor to the trunk's foot,
   up the spiral two treads a waypoint, out along each branch to its landing and back, ending on level 3's north
   balcony. Every waypoint is derived from the tree's plan (`worlds/debug/tree-walk.puck`), so the route moves with
   the tree. `puck test worlds/debug/tree-walk-tests.puck` plays the same route headless and fails at the section
   where the walk stops, with the waypoint after which the body fell, stuck, had a step refused, ended a solve too
   deep, or climbed a stretch of treads without a step-up, and whether it stood on each balcony it reached. The
   walker holds the toybox's full-size pass, because a body that walks off a level-1 branch onto the toybox's floor
   without one is made small there and stays small on the tree, too small for the treads' risers. Whether a pass-less
   visitor should be able to climb back up is a design question for the tree; the route does not answer it.
2. **Foliage, planters and decorations** across the hall.
3. **The netting** around the toybox playroom: playful and beautiful, never a fence.
4. **The climbing wall** ([gallery.md](../../worlds/debug/gallery.md#the-climbing-wall)) stands in front of the
   nave's north edge in bay 10, from the floor to a ledge level with level 3, built from the standard's climbing pieces
   (`worlds/standard/climbing.puck`) and climbed with `climbAbility`. Its look is owed a judgement in a window against
   the tree pack's climbing pier, from the nave's floor and from level 2 (`gallery.shot climbing-wall`,
   `climbing-wall-across`): whether the sandstone planes read as canyon rock, the three route colours read as routes
   from across the nave, and the holds read as where hands go while the Moth climbs.
5. **The windows:** frames, mullions, shading fans and sills.

## References

- [The gallery inspiration pack](../game/art/gallery-inspiration-pack/README.md) sets the direction. "The tree is
  the staircase" leads the day look and "the building teaches itself to glow" the night.
- [The gallery tree pack](../game/art/gallery-tree-pack/README.md), painted from a written brief only, covers the
  tree's walkways and junctions, planters, netting, a climbing wall and windows.

## Tools

- **Patterns:** the standard package's pattern library in `worlds/standard/shading.puck`: fractal noise, ridged
  noise and turbulence (each octave fading by the pixel footprint, so distant noise does not shimmer), domain warp, cellular borders and per-cell values, colour ramps and bricks.
- **Relief:** the `relief` form, once it lands, gives any pattern exact bump lighting. Hard steps give no relief;
  soften an edge to carve it.
- **Debug views:** the per-pixel SDF slice and the proximity contours, for finding geometry flaws (the rendering
  skill names their verbs).

## Walls already known

Each of these goes to a lane, not to the artist.

- Glass washes out, and stair-stepped reflection streaks cross glass, floors and piers.
- Light through glass carries no colour: the stained glass reads grey, and the barrier shows haze without its
  rainbow bands.
- Screens and bulbs light their surroundings too weakly for the night look.
- Heavy haze washes out the hall; there is a screen-space black smudge, and pale crescent blotches on the piers.
