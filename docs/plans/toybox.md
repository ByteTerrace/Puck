# The toybox: the gallery's Wonderland floor

Level 1 of the gallery (worlds/debug/gallery) is the toybox: a colourful, childlike gaming floor that every other
level looks into across the nave's void. A player reaches it by its corner stairs, its lift or the central tree's
branches, and arrives Alice-sized, too small to use its game stations and rental stalls. A puzzle lets a player enter
at full size and play. This plan holds the owner's requirements for the work still open; what is built is described
in worlds/debug/gallery.md.

## The owner's requirements

- **Arrival makes you small.** Every way onto level 1 (the four corner stairs, the lift, the tree's branches) shrinks
  you smoothly as you go in and grows you as you leave, and outside observers see the true size. The size that
  feels right is chosen by whimsy and player feel, tuned live.
- **No cheating.** Every other floor can be reached by jetpack; level 1 cannot (the lattice). The size puzzles can't
  be bypassed by flying, the lift or squeezing in at full size.
- **Full-size entry is a puzzle that involves the flaw.** The owner's direction: "our full size experiment should
  somehow involve walking through and exiting the flaw in some way ... entrances and exits to the flaw placed in key
  locations that allow a player to come up with the idea of becoming small, walking through the flaw, and then
  exploiting some mechanic to become big again in the toy floor. Make it intuitive so that the player really feels
  like they learned it naturally and on their own." Authors and debuggers get shortcuts to set the pass and their size.
- **The game stations:** twelve humble gaming decks on the floor, six in two-player arcade cabinets and six on
  televisions with proper consoles and controllers, all diegetic.
- **The rental stalls:** two opposite corners each rent out twelve humble gaming bricks and twelve advanced gaming
  bricks. The devices are finite, each one an individual object a later puzzle can name; renting only for now
  (take-home devices are later work). Handhelds must run efficiently at scale with proper scoping.
- **The central tree.** A giant, beautiful, natural tree grows at the hall's exact centre (tile (26, 14), bay 6 on the
  nave's centre row), its trunk taking at least the nine tiles a display's pad does, with two branches reaching each
  of levels 1 to 3. It is a diegetic tutorial: players learn they can climb it and walk its branches to other floors.
  The branches are "magical" too: the pair into level 1 shrinks a player like the stairs do.
- **Climbing at Breath of the Wild / Tears of the Kingdom quality or better.** A real climbing ability, not a
  walkable ramp: grabbing and climbing surfaces, with the feel, camera and animation that quality bar implies.

## Proposed: the flaw remembers your size

A proposal for the full-size puzzle, for the owner to judge. It uses only mechanics a player has already met.

1. **The flaw imprints the size of whoever touches it.** Touching the flaw anywhere stores your current size in
   it, shown in its light as a glowing silhouette of you at that size, which follows you along the tube for a
   moment. Players meet this on level 0 at full size, before they know what it means.
2. **The flaw's openings fit only tiny bodies.** A player on level 1 finds an opening small enough to enter, and
   walks inside. Travelling inside the flaw keeps your size, so the first discovery is the escape: leaving the flaw
   on another floor while still tiny.
3. **An exit restores the imprint.** Leaving the flaw through an exit gives you back the size the flaw last stored
   for you. A player who imprinted at full size, then shrank on the stairs, entered the flaw and left by an exit on
   level 1 arrives full size inside the toybox.
4. **What teaches it:** the silhouette makes the stored size visible every time, and one exit near the stairs on
   level 0 restores a full-size player who is already full size, a harmless first exit. The insight is to imprint
   big, travel small and exit where you want to be big.

## Open work

- The size field on the stairs, lift and branches; the full-size pass and its shortcuts; engagement that needs full
  size (lane in progress).
- The rental stalls stand (worlds/debug/gallery/rentals.puck, worlds/debug/gallery.md "The rentals"): a rented
  device rides its renter's hip and its seat's pool machine runs, but nothing shows its picture. Open: a screen that
  follows a body, so the carried device shows its game and engages wherever its holder stands; creating a pool
  machine's runtime only when its `runsWhen` first holds; adopting the full-size engage condition in
  `rentalCanRent` once it lands.
- The flaw: the light tube, then its hollow interior, its openings and exits, and the imprint (light lane in
  progress; the interior and puzzle follow).
- The central tree's look: the tree stands, walkable (gallery.md, "The central tree"), but its sculpt is not yet at
  the visual bar. The trunk is a plain cone, the treads read as a built stair rather than grown root and bark, the
  branches are even-thickness limbs without knots or forks, and the canopy is a few broad leaf masses. A look pass
  owes it a gnarled, fluted trunk, treads grown into the bark, branches that fork and taper, and a broken, layered
  crown, judged live from every level.
- The tree's level-1 branches shrink: a size field along each of `treeShrinkLimbs` (layout.puck), part of the size
  field work above. Until then the bay-6 gate lets a full-size body into the toybox.
- Climbing: a real climbing mechanic at Breath of the Wild / Tears of the Kingdom quality or better, preceded by a
  research round (the climb's states, stamina, ledge and mantle, the camera, animation and authored surfaces).
