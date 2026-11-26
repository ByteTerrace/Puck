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

- The spiral stairs' shrinking flights are not yet shown walked end to end: the switchbacks are (a body climbs from
  level 0 into level 1 shrinking to 0.15 and from level 1 to level 2 growing to full size, and walks down both), and
  the spirals' risers keep the same 1.2 margin by construction, but a body walked round the south-east spiral by
  chords stalled at about 1.1 m up at size 0.65. Walk it tangentially and name the cause.
- The rental stalls stand (worlds/debug/gallery/rentals.puck, worlds/debug/gallery.md "The rentals"): a rented
  device rides its renter's hip, shows its pool machine's game and plays in its renter's hands. Open: creating a pool
  machine's runtime only when its `runsWhen` first holds; reading the body's size in `rentalCanRent`, as a station's
  `needs` does (worlds/standard/station.puck), so a small body cannot rent.
- The cartridge library (the owner's request: a folder of curated ROMs per console, a nice diegetic way to switch
  between them, renting known titles welcome). Built: a machine's `contentBy` cell selects a title of its
  `contentFolders` library, the rack at bay 9 lends the deck library's titles through the rentals' loan row, and a
  station's console swaps cartridges (worlds/debug/gallery.md, "The cartridge rack"). Open:
  - A cartridge's label shows the title's file name: a slot past the shipped titles is labelled by its number, since
    a creation's text is authored and no file name enters the document. It needs a presentation-side label the host
    fills from the library (the machine host knows the names; the document and state never carry them).
  - Brick and advanced-brick cartridges for the rented handhelds: a rack slot list per family over the
    `cgb`/`dmg`/`agb` folders and the owner's `roms/hgb` and `roms/agb`, and inserting by engaging the held device
    while holding a cartridge of its family (the pool machines take `contentBy` from a per-body cell).
  - A picker on the television sets: the engaged player's Select (or a bound button) shows the library's titles on
    the screen beside the inserted cartridge's, and ejects. Needs the same presentation-side title list.
  - The rack's place and the console's spot are first guesses (bay 9 against the south wall; 0.9 m to the station's
    +x side); judge them on a walk and captures and move them where they read.
  - A deck runs its own program again when its cartridge's title is missing from this machine's folders; the
    cartridge could say so (a blank label, a refusal tone).
- The flaw: the light tube, then its hollow interior, its openings and exits, and the imprint (light lane in
  progress; the interior and puzzle follow).
- The central tree's look: the tree has buttress fins, wandering bark shelves with shelf fungus and burls, branches
  with collars, forks and knots, bark grain that runs along each branch, and a crown of separate clusters, and it
  collides analytically (gallery.md, "The central tree"). It still misses the visual bar: the trunk is a perfect cone
  whose grain reads as fine speckle rather than twisting streaks; the crown's clusters read as smooth cartoon lobes
  rather than foliage (no leaf-level breakup or skylight through them); each branch's fork ends in a bare point with
  its leaf spray standing apart from it (the fork sweep's author-to-local carry wants checking); and the branch tips
  on the balconies end blunt. Judge it on captures from all three levels at noon and night.
- The tree's shelves: a body pressed against the bark does not step up them (the step-up beside the trunk core's
  cylinders), so the walking line runs a little out from the bark; a look from the physics side.
- Leaving small is the player's puzzle (the owner's ruling): only the stairs and the lift change size; a small body
  that leaves level 1 another way (through a branch's sieve onto the tree) stays small until a route changes it. More
  gameplay builds on this later. An author stuck small resets with `gallery.wonder size 1`.
- Climbing: a real climbing mechanic at Breath of the Wild / Tears of the Kingdom quality or better; [the climbing
  plan](climbing.md) holds its research round, what is built and what is open.
