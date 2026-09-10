# Moth concept pack

Six concept sheets expand the owner's selected Moth into a visual handoff for character modeling and procedural animation. These images describe the intended quality; the current engine prototype is not the visual reference to reproduce. Generated with the built-in image_gen tool, including targeted image corrections. The complete [prompt set](prompts.md) is included. The September 10 compact-wing sheets follow the owner's preference for two subtle shells and supersede the earlier folding-fan proposal.

## Read the images in this order

| Sheet | Use it for |
|---|---|
| [Owner source](00-owner-source.png) | Character 4, bottom left, is the selected Moth. Other crew members are context, not alternate Moth designs. |
| [Character model sheet](01-character-model-sheet.png) | Overall silhouette, proportions, front/side/back relationships, palette and compact flight-pack appearance. |
| [Face and armor](02-face-and-armor.png) | Facial identity and expression, curved hood opening, swept shoulder layers, tapered boots and dark joint gaps. |
| [Compact-wing owner crop](compact-wing-owner-crop.png) | The owner's September 10 reference for the two curved shells, compact size and band placement. |
| [Compact wing views](05-compact-wing-views.png) | Back, side and rear three-quarter views; matching rest and subtle hover opening. Current pack construction reference. |
| [Compact wing motion](06-compact-wing-motion.png) | Jump preparation, takeoff, hover, forward flight, air brake and touchdown with the same two-shell assembly. Current flight pose reference. |
| [Earlier flight exploration](03-flight-system.png) | Historical folding-fan proposal, superseded by the compact wing views. Do not implement its extra segments. |
| [Earlier movement and weight](04-movement-and-weight.png) | Additional ready, sprint, bank and recovery gestures. Its fan-wing hardware is superseded. |

## Preserve the character

The [Puck Study source](../../../src/Puck.World/Assets/studies/moth.glsl) is the sole shader implementation and renders the character with procedural SDF geometry; the [Study guide](../../../src/Puck.World/README.md#shader-studies) covers launching, live reload and animation controls. Its header documents the pose selector, flight-cycle animation, pack opening, framing, colorway and quality controls. The same rigid parts serve all poses, with separate ankle articulation and pose-driven backpack thrust. It remains an approximation of these concept sheets.

Keep warm brown skin, expressive brown eyes, black swept hair and one thick braid rooted beside her right cheek through the front helmet opening. The braid can trail during movement; its attachment does not change or move through the helmet back. The hood has an ivory face rim and lilac shell. Its opening follows the forehead and cheeks with a continuous shaped curve. Avoid a box or pentagonal house surrounding the face.

Armor combines broad plate faces, tapered volumes, controlled bevels and purposeful curves. Swept overlapping shoulders, large gauntlets, flared shin armor and substantial flat-soled boots establish the silhouette. Charcoal joints separate rigid pieces and provide bending space. Ivory and lilac dominate; muted ochre and restrained cyan lights are accents. Preserve the friendly arcade character and compact chibi proportions instead of adding realistic anatomy, surface noise or excessive effects.

## Interpret the proposed motion

Keep exactly two rigid, curved pods beside a narrow central spine. Each has two ivory bands and one recessed lower propulsion outlet. Upper mounts stay below the helmet and behind the shoulder armor; lower tips remain around the upper pelvis. The shells open only a few degrees outward and aft, with no added segments or changing shell dimensions. Preserve shoulder movement, hip flexion and an exhaust path behind the legs.

Let the body compress before a jump and extend on takeoff, with the shells opening slightly as thrust begins. Hover uses an upright torso and balanced downward jets. Forward flight pitches the body forward; braking pitches it back, changing thrust direction while the pack remains attached. Touchdown transfers weight through both flat boot soles, bent knees and hips, with the shells still slightly open. Settle the body before closing them. These sheets use the two backpack outlets for powered poses; boot ports remain idle cyan lights. Exhaust is short and translucent, and it is off in jump preparation and touchdown.

## Resolve differences deliberately

The owner source and subsequent owner feedback take precedence. Sheet 1 anchors overall identity and costume; sheet 2 clarifies surface treatment and expression. Sheet 5 anchors the compact pack; sheet 6 supplies its movement intent. Sheets 3–4 remain historical explorations. Generated perspective views are not exact orthographic blueprints: use one shared rig to verify hinge travel, armor clearance and exhaust separation through the complete motions. Small perspective or seam differences do not authorize new parts or outlets. Incidental slogans on the sheets are not game copy, lore or implementation requirements.

For implementation priorities, use the [hero brief](../../armored-chibi-hero-brief.md). The [studio guide](../../../src/Puck.World/Assets/worlds/moth.md) documents the existing playable prototype and its live iteration controls; it does not establish the finished visual standard.
