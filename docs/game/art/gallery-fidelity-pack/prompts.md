# Gallery fidelity prompts

Codex used the built-in image generator for every painting and correction. Each block below is the complete submitted prompt, with its actual image inputs listed in order. The tool accepts at most five references: each call therefore selects the relevant live captures first, followed by the owner's baseline B and the relevant kit sheets. Correction calls put the painting being edited last. No CLI or alternate image service was used.

The supplied live captures came from `.claude/scratch/refs/`: `nave-east-stained.png`, `toybox-barrier.png`, `arcade-cabinet.png`, `tree-branches.png`, `exterior-facade.png`, `arcade-pier.png`, `toybox-rentals-night.png`, and `holiday-fir-night.png`. All eight were inspected before painting. They are session reference inputs, not files shipped with this pack. The [gallery guide](../../../../worlds/debug/gallery.md) supplies the durable layout context.

The rejected daylight and exterior intermediates are not shipped. Their initial prompts and exact corrective prompts remain here so the revisions are explicit. The other four paintings were accepted on their first generation. [Artist review](review.md) · [Final paintings](README.md).

## Initial daylight painting (rejected)

References, in tool order:

1. `.claude/scratch/refs/nave-east-stained.png` — supplied live capture.
2. `.claude/scratch/refs/tree-branches.png` — supplied live capture.
3. [canyon-workshop.png](../valley-and-gallery-concepts/canyon-workshop.png) — owner's baseline B.
4. [level-zero-exhibit-hall.png](../gallery-kit-pack/level-zero-exhibit-hall.png) — gallery kit component reference.
5. [branched-pier-and-arcade.png](../gallery-kit-pack/branched-pier-and-arcade.png) — gallery kit component reference.

```text
Use case: stylized-concept.
Paint a landscape environment direction painting for Puck's existing gallery, at a modern stylized game's shipping quality. Input hierarchy: first use the supplied LIVE GALLERY CAPTURES for the existing layout and pieces, not their current rendering quality; next use Canyon workshop, the owner's baseline B, for silhouette language, colour and painted light; finally use the supplied gallery kit paintings for component detail. Preserve the live building rather than replacing it with B's building.
Treatment: a hand-painted animation background with broad gouache planes, decisive shaped shadows, restrained broken-colour brushwork, selective sharp edges, luminous bounced light and clear foreground/middle distance/depth. Chunky rounded cream and ochre shell modules, recessed deep-teal structure, inset turquoise glazing, orange pivoting fan shades on fixed dark hubs, warm terracotta. Push material depth, atmosphere and quiet whimsy while keeping simple readable masses. Distinguish satin teal structure, matte mineral shell, glazed clay, glass and warm brass by their response to light. All junctions carry weight and glass is seated in frames. Keep circulation clear and the scale inviting.
Absolute image constraint: no words, letters, numbers, labels, captions, logos, signatures, watermarks, UI overlays or writing anywhere. Remove all text present in live references. Screens show only simple coloured geometric pictures. Marquees are uninterrupted luminous colour. No photographic finish, generic smooth 3D, clutter, dense greebles or collage of source panels. Landscape format, one unified painting to the edges unless a study sheet is expressly requested.

Subject: the four-storey nave by day, ground floor plus THREE stacked balcony levels, seen from walking height in the west half looking east along its length. Preserve the live hall's rectangular nave, paired side arcades, terracotta aisle, low wide cream-and-ochre stations on cream pads, lattice nets enclosing ONLY level one, glass-and-brass rails on level two and clean openings at the top. A great warm-brown tree stands in the middle of the nave, its walkable branches sweeping upward and landing cleanly at open upper balcony doorways. Compose slightly off axis so the trunk does not conceal the east end wall. The canopy is sculpted into distinct overlapping leafy clumps with two-tone greens and selective golden leaves, lit rims and cool inner shade; leave air between branches, doorways and canopy. Show its real scale spanning all floors without filling the hall with foliage.
At the far east wall keep exactly the single lower tier of THREE broad tall bays: the centre is leaded stained glass showing a golden sun above green hills, blue sky and ruby border; side bays have clear turquoise glazing and orange fan shades. Broad quiet wall above, not a cathedral rose window. Warm sun shafts slant through the tall side windows, with slight airborne dust visible only inside the shafts. Coloured transmitted light from the east stained glass lies in ruby, amber, turquoise and green pools across the floor with dark came shadows. Polished terracotta has clean SOFT reflections, readable grout and gentle glaze variation, never a wet mirror. Display screens glow on cream lecterns; exhibit solids rest on their turntables. Keep the station proportions and clear centre route from the live capture. Lush warm sun against deep cool teal shade, rich yet controlled highlights, no blown-out whites. A few very small unmarked visitors may establish scale, with grounded feet and clear routes. This must feel like the current playable gallery transformed by light, material and botanical craftsmanship, not a fantasy cathedral.
```

## Daylight correction (accepted as nave-day.png)

References, in tool order:

1. `.claude/scratch/refs/nave-east-stained.png` — supplied live capture.
2. `.claude/scratch/refs/tree-branches.png` — supplied live capture.
3. [canyon-workshop.png](../valley-and-gallery-concepts/canyon-workshop.png) — owner's baseline B.
4. Initial daylight painting generated by the preceding daylight prompt — rejected intermediate, not retained.

```text
Edit the final supplied painting of the daylight nave. Reference priority: the first two images are the live gallery's console and tree, third is baseline B for painted treatment, fourth is the PAINTING TO EDIT. Keep the edited painting's composition, four-storey hall, palette, tree canopy, end-wall stained glass, orange fans, people and lighting. Make these precise corrections to bring its components back to the existing gallery:
Every display console must be ONE broad seamless tilted glass screen spanning nearly the whole width of its plinth's front in a slim cream bezel with a narrow brass resting lip, like the live capture. Remove every physical knob, dial and separate button from all plinths. Show only large cyan and amber geometric shapes on those single screens, absolutely no writing.
The floor reflections must be soft, calm and clean, with broad coherent silhouettes blurred by the dry terracotta glaze and unobscured narrow grout. Remove the scribbly broken glints. Keep the beautiful ruby/amber/green transmitted stained-glass pools and their dark came shadows.
The tree's walkable branches should remain organic unadorned bark with no manufactured handrails, while the BALCONY glass/brass rails remain. Branch tips meet simple open balcony thresholds, with no new balconies hanging off the tree. Preserve attachment and clear routes. Improve the warm sun shafts very subtly through the upper side windows so atmosphere has depth without fogging the hall.
Retain the hand-painted animation-background character, chunky rounded cream/ochre modules and deep teal, warm broad light planes, restrained brushwork, two-tone green and golden clumps. Landscape unified image. No words, numbers, labels, letters, logos, watermarks or UI anywhere.
```

## The nave at night

References, in tool order:

1. `.claude/scratch/refs/nave-east-stained.png` — supplied live capture.
2. `.claude/scratch/refs/holiday-fir-night.png` — supplied live capture.
3. [canyon-workshop.png](../valley-and-gallery-concepts/canyon-workshop.png) — owner's baseline B.
4. [level-zero-exhibit-hall.png](../gallery-kit-pack/level-zero-exhibit-hall.png) — gallery kit component reference.
5. [Accepted daylight nave](nave-day.png) — composition reference to relight.

```text
Use case: stylized-concept.
Paint a landscape NIGHT lighting target for Puck's existing four-storey gallery. Reference hierarchy: images one and two are live gallery captures defining existing pieces; image three is baseline B defining hand-painted animation-background style, chunky cream/ochre modules, deep teal and turquoise glazing; image four supplies kit component detail; image five is the ACCEPTED DAYLIGHT PAINTING TO RELIGHT. Preserve image five's camera, nave proportions, side arcades, lattice nets on level one, upper balconies, single-screen plinths, great tree and bare organic walkable branches meeting clean upper doorways. Its two-tone green/gold canopy becomes a dark silhouette. Do not redesign the room.
It is deep night, ALL HALL LIGHTS OFF. The only emitters illuminating the room are the station screens, holiday fir lights and sparse neon trim. There is NO daylight, NO sun shaft, NO moon beam, NO lit ceiling, NO downlight, NO wall sconce and NO hidden warm fill. Ceiling fixture faces are dark. Windows show a deep indigo night sky, and the east wall's central sun-and-hills stained glass is DARK, faintly legible as coloured glass with black lead; it transmits no bright sun pattern and casts no coloured daytime floor pool.
Keep each wide seamless sloping station screen luminous with only abstract cyan/amber geometric picture shapes. Each screen casts a distinct broad pool on the tiles immediately before it and softly lights its own cream bezel, brass lip and lower cap; on the back of the station it is dark. Emission lights nearby surroundings with plausible soft shadows, falloff, occlusion and a little bounced colour. Tiny restrained turntable trim counts as neon, with no broad luminous tabletop. Dark exhibit solids are silhouetted against their own stations' reflected glow.
Add the existing two holiday fir exhibits near the east end, separated so both read and do not block the stained glass. One has classic red, green, amber and blue BULBS on a dark softly tiered green fir: small rounded light sources casting distinct local colour onto needles, trunk base and floor. The other is a FIBRE-OPTIC fir with fine feathery needles, light only at thousands of tiny tips, clusters of cyan, violet, rose and green indicating colours that cycle in the engine; darker interior and an unmistakably different light texture from the bulb tree. Both stand on the floor with contact shadows. No overhead decorations or luminous stars.
Very sparse thin turquoise and amber neon trim follows existing architectural edges. Its light decays close to the trim, upper balconies mostly vanish into deep navy/teal darkness. The warm terracotta floor stays mostly dark sienna and reflects only the actual luminous sources in SOFT coherent blurred pools, narrow grout readable near light. Preserve rich dark intervals between all sources. Exposure protects saturated bulbs and screen colours while revealing just enough tree and architecture through their local bounces. Painterly coloured darks, selective luminous edges, extraordinary depth and hush. This must read instantly as the same hall with its lights switched off, not a teal room washed in ambient illumination.
No words, numbers, letters, labels, writing, logos, watermarks, signatures or UI anywhere. One unified landscape painting, no panels.
```

## The toybox, level 1

References, in tool order:

1. `.claude/scratch/refs/toybox-barrier.png` — supplied live capture.
2. `.claude/scratch/refs/arcade-cabinet.png` — supplied live capture.
3. `.claude/scratch/refs/toybox-rentals-night.png` — supplied live capture.
4. [canyon-workshop.png](../valley-and-gallery-concepts/canyon-workshop.png) — owner's baseline B.
5. [branched-pier-and-arcade.png](../gallery-kit-pack/branched-pier-and-arcade.png) — gallery kit component reference.

```text
Use case: stylized-concept.
Paint a landscape environment direction painting for Puck's existing gallery, at a modern stylized game's shipping quality. Input hierarchy: first use the supplied LIVE GALLERY CAPTURES for the existing layout and pieces, not their current rendering quality; next use Canyon workshop, the owner's baseline B, for silhouette language, colour and painted light; finally use the supplied gallery kit paintings for component detail. Preserve the live building rather than replacing it with B's building.
Treatment: a hand-painted animation background with broad gouache planes, decisive shaped shadows, restrained broken-colour brushwork, selective sharp edges, luminous bounced light and clear foreground/middle distance/depth. Chunky rounded cream and ochre shell modules, recessed deep-teal structure, inset turquoise glazing, orange pivoting fan shades on fixed dark hubs, warm terracotta. Push material depth, atmosphere and quiet whimsy while keeping simple readable masses. Distinguish satin teal structure, matte mineral shell, glazed clay, glass and warm brass by their response to light. All junctions carry weight and glass is seated in frames. Keep circulation clear and the scale inviting.
Absolute image constraint: no words, letters, numbers, labels, captions, logos, signatures, watermarks, UI overlays or writing anywhere. Remove all text present in live references. Screens show only simple coloured geometric pictures. Marquees are uninterrupted luminous colour. No photographic finish, generic smooth 3D, clutter, dense greebles or collage of source panels. Landscape format, one unified painting to the edges unless a study sheet is expressly requested.

Subject: one immersive landscape painting along the existing level-one toybox aisle. This is an upper gallery around the nave void, not a separate children's room. Look obliquely from the tree branch landing through its arch toward the long toybox aisle. Foreground at one side: the existing magic barrier fills the arcade arch edge to edge, a pane plus three closely spaced thinner films nested into its rounded arch silhouette. Make the barrier plainly magical: translucent layered shimmering glass, moving-looking curved rainbow colour bands with iridescent interference, thin luminous contours separated in depth, small refracted doubles of the room behind. Keep the room readable through its centre. The branch reaches the threshold without blocking it. The adjacent arcade bays remain enclosed by diamond rope lattice nets in pastel frames, tied at crossings and firmly attached from floor to soffit. Show the tall nave and opposite gallery beyond the net so its location reads.
Along the exterior window wall stand the existing tomato-red and cobalt TWO-PLAYER arcade cabinets with mint trim: deep leaning screens under chamfered hoods, separate ball-top joysticks and two sets of six coloured buttons, twin speaker grilles and a completely blank luminous marquee. Keep their shape from the cabinet capture, delete its writing. Alternate them with the living-room TV corner: chunky sunflower television with rabbit ears on a low stand, two cabled gamepads on a cherry rug, two inviting beanbags. At the spiral stair corner, show an L-shaped handheld rental kiosk: cobalt counter, sunflower lip, candy-pink canopy, rows of softly lit recessed cubbies each holding one brightly coloured handheld with a dark picture screen, blank header, returns tray. Keep the stair opening guarded and passage clear. Cheerful lavender, buttery yellow, pink and blue floor tiles, pastel structural faces on this level within the same cream-and-teal building.
Late afternoon subdued daylight supports warm screen and cubby glows; colour from each emitter falls on its bezel, control deck, cubby side and nearby tiles. Rich material layering, soft contact shadows, believable glass depth, warm worn edges at touch points, playful silhouettes, painterly grouped colour. Foreground magical film, middle-distance cabinets, far TV/rental corner must all be distinct without visual clutter. No extra attractions. No lettering even on buttons, handhelds or marquees.
```

## The board game room

References, in tool order:

1. `.claude/scratch/refs/nave-east-stained.png` — supplied live capture.
2. `.claude/scratch/refs/arcade-pier.png` — supplied live capture.
3. [canyon-workshop.png](../valley-and-gallery-concepts/canyon-workshop.png) — owner's baseline B.
4. [floor-and-ceiling.png](../gallery-kit-pack/floor-and-ceiling.png) — gallery kit component reference.
5. [furniture-and-planting.png](../gallery-kit-pack/furniture-and-planting.png) — gallery kit component reference.

```text
Use case: stylized-concept.
Paint a landscape environment direction painting for Puck's existing gallery, at a modern stylized game's shipping quality. Input hierarchy: first use the supplied LIVE GALLERY CAPTURES for the existing layout and pieces, not their current rendering quality; next use Canyon workshop, the owner's baseline B, for silhouette language, colour and painted light; finally use the supplied gallery kit paintings for component detail. Preserve the live building rather than replacing it with B's building.
Treatment: a hand-painted animation background with broad gouache planes, decisive shaped shadows, restrained broken-colour brushwork, selective sharp edges, luminous bounced light and clear foreground/middle distance/depth. Chunky rounded cream and ochre shell modules, recessed deep-teal structure, inset turquoise glazing, orange pivoting fan shades on fixed dark hubs, warm terracotta. Push material depth, atmosphere and quiet whimsy while keeping simple readable masses. Distinguish satin teal structure, matte mineral shell, glazed clay, glass and warm brass by their response to light. All junctions carry weight and glass is seated in frames. Keep circulation clear and the scale inviting.
Absolute image constraint: no words, letters, numbers, labels, captions, logos, signatures, watermarks, UI overlays or writing anywhere. Remove all text present in live references. Screens show only simple coloured geometric pictures. Marquees are uninterrupted luminous colour. No photographic finish, generic smooth 3D, clutter, dense greebles or collage of source panels. Landscape format, one unified painting to the edges unless a study sheet is expressly requested.

Subject: the existing level-two board game room in the south projecting gallery wing, beside its lift landing. Paint a welcoming player-height three-quarter view of TWO contrasting chess tables beside one another with inviting seats and ample knee space. Keep the surrounding architecture of the live gallery: rounded cream bands and ochre panels, deep-teal branched piers carrying a coffered soffit, turquoise window bays with orange fan shades, terracotta floor, and the open nave visible through the arcade at one side with glass/brass balcony rails. Do not add a new building or a grand fantasy throne room. The live captures establish the shell; there is no dedicated board-room capture.
Nearest table is a high-tech DIGITAL game table: sturdy rounded-rectangle ochre-and-cream rim on a recessed teal pedestal, a thick transparent turquoise glass top with a visible edge and clean bevel, and a perfectly legible complete EIGHT-BY-EIGHT chessboard glowing within the glass. Chessmen appear as stylized luminous translucent three-dimensional game forms at the board, with clean recognizable pawn, rook, knight, bishop and king silhouettes; two contrasting teams, sensible midgame spacing. The glass is transparent at its margin so the support below remains visible, reflections subdued enough to read the board. Fine turquoise lit edges cast light onto the rim, pedestal and floor. Simple inset touch areas are unmarked geometric discs. Chairs tuck below the table edge, upholstered warm ochre/terracotta shells with teal supports and rounded cream backs.
Beside it, clearly separate, a physical chess table with an equally complete eight-by-eight cream-and-dark-teal inlaid board and solid carved ivory and deep-brown chessmen. Warm satin wood or ceramic finish, tactile edges and a restrained brass detail distinguish it from the digital glass. Both tables are normal playable dining-table height, their boards horizontal with straight grids in perspective, their four grounded seats facing each other. The tables' footprints and chairs do not overlap. No coordinate letters or digits around either board.
Soft late-day window light and cool luminous tabletop light meet in warm terracotta bounce. Show coloured shadows, clean softened floor reflections, gently worn chair edges, quiet painterly surface variation. Inviting, slightly futuristic and whimsical, with a clear route back to the lift and nave. No people needed; the seats invite the viewer to sit.
```

## Initial exterior painting (rejected)

References, in tool order:

1. `.claude/scratch/refs/exterior-facade.png` — supplied live capture.
2. `.claude/scratch/refs/arcade-pier.png` — supplied live capture.
3. [canyon-workshop.png](../valley-and-gallery-concepts/canyon-workshop.png) — owner's baseline B.
4. [window-bay.png](../gallery-kit-pack/window-bay.png) — gallery kit component reference.

```text
Use case: stylized-concept.
Paint a landscape environment direction painting for Puck's existing gallery, at a modern stylized game's shipping quality. Input hierarchy: first use the supplied LIVE GALLERY CAPTURES for the existing layout and pieces, not their current rendering quality; next use Canyon workshop, the owner's baseline B, for silhouette language, colour and painted light; finally use the supplied gallery kit paintings for component detail. Preserve the live building rather than replacing it with B's building.
Treatment: a hand-painted animation background with broad gouache planes, decisive shaped shadows, restrained broken-colour brushwork, selective sharp edges, luminous bounced light and clear foreground/middle distance/depth. Chunky rounded cream and ochre shell modules, recessed deep-teal structure, inset turquoise glazing, orange pivoting fan shades on fixed dark hubs, warm terracotta. Push material depth, atmosphere and quiet whimsy while keeping simple readable masses. Distinguish satin teal structure, matte mineral shell, glazed clay, glass and warm brass by their response to light. All junctions carry weight and glass is seated in frames. Keep circulation clear and the scale inviting.
Absolute image constraint: no words, letters, numbers, labels, captions, logos, signatures, watermarks, UI overlays or writing anywhere. Remove all text present in live references. Screens show only simple coloured geometric pictures. Marquees are uninterrupted luminous colour. No photographic finish, generic smooth 3D, clutter, dense greebles or collage of source panels. Landscape format, one unified painting to the edges unless a study sheet is expressly requested.

Subject: the live gallery's SOUTH facade from the forecourt at golden hour. Step the camera back to human height so the long four-storey rectangular building, its window rhythm and entrance read clearly, with a three-quarter angle showing its length. Preserve the capture's yellow-and-ochre wall panels, clean repeated turquoise window bays, broad rounded cream horizontal floor bands and orange folding fan shades on fixed dark circular hubs. Preserve the projecting upper south wing carried on a ground-level deep-teal arcade over the forecourt; the entrance sits beneath it and the exposed glass lift tower stands immediately to the left of the entrance zone. At the west end the lower roof terrace steps the silhouette. The current live facade's plan takes precedence over baseline B's different massing. Do not replace it with a round building or a castle. Remove the flying avatar, HUD and all capture artifacts.
Make a real loved building: thick rounded cream corners, deeply recessed glass with convincing seat and mullion pockets, distinct ochre cladding panels and recessed joints, painted reveals, drip edges beneath sills, tiny weather streaks under drainage points, slight sun fading, subtle scuffing at the entrance threshold. Broad teal haunches visibly bear the wing. Orange fan leaves have real thickness and overlap as they spread or stack; the pivot and short bracket are attached to the mullion with clearance from the glass. Let alternating open and partly gathered shades create an elegant rhythm. Reflections of warm canyon walls live in the turquoise panes while selected panes reveal depth inside, never an opaque blue sheet.
A clean wide paved forecourt leads to the open entrance, restrained existing planters and benches sit at its edges, no clutter or extra kiosks. Warm stratified canyon walls and grouped green/gold tree clumps beyond establish the selected valley. Long amber sunlight rakes the facade, making warm cream highlights, deeply coloured teal shadows and terracotta bounce under the arcade; cool lavender canyon distance, a little sunlit dust only in depth, a small pair of moons above the rim if visible. Keep a richly painted animation-background finish with selective material edges and large calm planes, not photoreal grit or generic plastic. One beautiful credible architectural place, immediately recognizable from the live capture.
```

## Exterior correction (accepted as exterior-golden-hour.png)

References, in tool order:

1. `.claude/scratch/refs/exterior-facade.png` — supplied live capture.
2. [canyon-workshop.png](../valley-and-gallery-concepts/canyon-workshop.png) — owner's baseline B.
3. [window-bay.png](../gallery-kit-pack/window-bay.png) — gallery kit component reference.
4. Initial exterior painting generated by the preceding exterior prompt — rejected intermediate, not retained.

```text
Edit the last supplied landscape painting of the golden-hour gallery exterior. The first image is the LIVE FACADE for the building's existing layout, the second baseline B defines painterly style, third defines the window/fan component, fourth is the PAINTING TO EDIT. Preserve the fourth image's forecourt camera, loved material finish, canyon, grouped foliage, colours, clean pavement, orange fans and glass lift tower immediately left of the entrance.
Correct its architectural floor count. This is a FOUR-STOREY gallery, with ground floor plus three upper floors. The right-hand projecting south wing occupies the TOP TWO FLOORS, carried above a TWO-STOREY HIGH open teal arcade/forecourt. The previous painting mistakenly compressed that wing into a single glazed storey on a one-storey arch. Give the projecting wing two clearly separated rows of glazed window bays divided by a thick cream horizontal floor band, and make the teal bearing arches tall enough to span ground and first floors beneath it. Add the missing row of windows on the long facade to the left as needed so all four floor levels align with the lift landings. Keep the existing long rectangular footprint, entrance position, terrace at the west end, repeated clean window bays and thick cream bands; do not turn the building into a tower, add a new wing, or change the lift to a different location. Pull the framing back slightly if needed to fit the correct four-storey facade and sky without cropping.
Each orange fan hub remains attached to a mullion by a short bracket, leaves visibly overlapping. Broad golden-hour light with warm grazing highlights, deep cool-teal arcade shade, turquoise glass showing real interior depth and canyon reflections, cream bevels, soft weather streaks under sills and broad restrained painted texture. Hand-painted animation-background treatment, simple chunky slightly futuristic silhouettes, rich light, no excessive detailing. The building stays the subject and the forecourt stays inviting and clear.
Absolutely no words, numbers, lettering, labels, logos, watermarks, signatures or UI anywhere. One unified landscape painting.
```

## Material and light studies

References, in tool order:

1. `.claude/scratch/refs/nave-east-stained.png` — supplied live capture.
2. `.claude/scratch/refs/toybox-barrier.png` — supplied live capture.
3. `.claude/scratch/refs/tree-branches.png` — supplied live capture.
4. [canyon-workshop.png](../valley-and-gallery-concepts/canyon-workshop.png) — owner's baseline B.
5. [branched-pier-and-arcade.png](../gallery-kit-pack/branched-pier-and-arcade.png) — gallery kit component reference.

```text
Use case: stylized-concept.
Paint a landscape environment direction painting for Puck's existing gallery, at a modern stylized game's shipping quality. Input hierarchy: first use the supplied LIVE GALLERY CAPTURES for the existing layout and pieces, not their current rendering quality; next use Canyon workshop, the owner's baseline B, for silhouette language, colour and painted light; finally use the supplied gallery kit paintings for component detail. Preserve the live building rather than replacing it with B's building.
Treatment: a hand-painted animation background with broad gouache planes, decisive shaped shadows, restrained broken-colour brushwork, selective sharp edges, luminous bounced light and clear foreground/middle distance/depth. Chunky rounded cream and ochre shell modules, recessed deep-teal structure, inset turquoise glazing, orange pivoting fan shades on fixed dark hubs, warm terracotta. Push material depth, atmosphere and quiet whimsy while keeping simple readable masses. Distinguish satin teal structure, matte mineral shell, glazed clay, glass and warm brass by their response to light. All junctions carry weight and glass is seated in frames. Keep circulation clear and the scale inviting.
Absolute image constraint: no words, letters, numbers, labels, captions, logos, signatures, watermarks, UI overlays or writing anywhere. Remove all text present in live references. Screens show only simple coloured geometric pictures. Marquees are uninterrupted luminous colour. No photographic finish, generic smooth 3D, clutter, dense greebles or collage of source panels. Landscape format, one unified painting to the edges unless a study sheet is expressly requested.

Subject: one LANDSCAPE material and light study sheet for a modeller and shader author, six equally generous close-up paintings arranged in a strict three-column by two-row grid on a warm ivory field. Only slim clean ivory gutters separate the paintings. All SIX studies have the same painterly finish and palette as the gallery, with coherent physical materials and contact. No titles, labels, numbers, writing, arrows, rulers, swatch names or diagrams. The six distinct subjects, in reading order, are:
TOP LEFT: a low-angle close view of polished terracotta tiles and their narrow grout. One real cream window sill and teal mullion at the back ground their broad softened reflection. The reflection is continuous in perspective across the glaze but interrupted naturally by recessed grout, with fine roughness broadening distant reflections, a subtle clay undercolour and tiny wear at tile edges. It is dry polished ceramic, not standing water.
TOP MIDDLE: the spring point of a deep-teal pier branching into its curved Y-haunch under a thick cream slab, with the existing three slim brass face inlays, clear chamfer planes and a dark reveal at the bearing. Satin paint carries broad restrained brush variation; warm edge bounce and cool shadow reveal thickness and continuous load-bearing attachment.
TOP RIGHT: the live station's single wide tilted glass screen in its cream bezel and ochre plinth. Only simple cyan and amber solid geometric pictures on the screen. In dim surroundings the screen casts a broad cyan pool across its cream lip, overhang underside and tiles directly in front; soft falloff, local occlusion and a faint secondary bounce. Surface brightness and nearby illumination must agree, with no hidden lamp and no physical knobs added.
BOTTOM LEFT: coloured daylight passing THROUGH a leaded stained-glass window onto terracotta. Show a portion of the actual framed glass with ruby, amber, blue and green panes and dark lead came, plus the connected patch of floor below. Its floor pools reproduce those colours with the lead's narrow dark lines and soften with distance. Coloured transmission is distinct from a reflected image.
BOTTOM MIDDLE: the MAGIC FILM filling a cream-and-teal arcade threshold, matching the live barrier. Close oblique view clearly shows the main transparent pane plus three closely spaced arch-shaped films nested before/behind it; iridescent rainbow interference bands bend over them, refracted background tiles remain visible, luminous edges make thin separations readable. A small portion of its branch landing grounds the threshold. It is a magical membrane, not an opaque rainbow door or soap bubble floating free.
BOTTOM RIGHT: two connected leafy clumps on a warm-brown tree fork, one two-tone moss/emerald green with golden accents and one green in deep shade. Broad rounded volumes with clear whole leaf tips at their silhouettes, selected painted leaf faces, warm transmitted edge light and dark cool cores. Keep the leaf masses grouped, with air between, and attach their stems to the fork. No floating loose leaf samples.
Keep each subject fully inside its own panel with clean separation, enough scale to read the material response. This is a polished visual reference, not a technical blueprint or a collage of the supplied images.
```
