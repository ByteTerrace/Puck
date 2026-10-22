# Moth concept pack

These sheets are the visual standard for the Moth, the debug world's avatar,
built as the native SDF creation in [moth.puck](../../../../worlds/debug/avatars/moth.puck).
They show her in two forms. Suited, she wears a 2.06 m mech suit in the spirit
of Firefall's battleframes, Mega Man and Anthem's javelins: thick layered
plates, exposed pivots and pistons, and heavy gauntlets, shoulders and boots.
Off-suit, she is the 1.68 m woman inside it. The suit on/off toggle exists for
one demonstration: holding a real-size handheld console in her own hands.

An art-direction pair made the sheets. Codex (its built-in image generation
tool) wrote the prompts, generated every sheet and judged which to keep; Claude
held the owner's constraints and checked each keeper against them. Every panel
in the pack passed an independent written audit by both. Each checked that
every part is attached and continuous; that joints bend within a plausible
range; that the pose is physically coherent; that the parts and proportions
match the turnaround; that the perspective agrees with the camera; that no
compositing seam or scale mismatch shows; and that the hands, braid root,
palette and head scale are right. Panels that failed after three attempts were
removed. The reading order and the authority lines below are Codex's verdicts.
Prompts and how each sheet was edited or composited are in [prompts.md](prompts.md).

## Read the images in this order

| Sheet | Authoritative for |
|---|---|
| [Owner source](00-owner-source.png) | Identity and design lineage. Character 4, bottom left, is the Moth; the other crew members are context. The later decisions below override its head scale, its earrings and its lighter armor. |
| [Suited turnaround](01-suited-turnaround.png) | The suit in front, right side, back and three-quarter views: the silhouette, proportions and plate layout; two thick shoulder slabs on round pivots; one flat chamfered chest slab with a painted ivory border; pistons inside the elbows and behind the knees; hip pivot discs; vents only on the outer faces of the gauntlets and shins; heavy boots on charcoal soles; her real head inside the hood; the palette; where the pack sits. |
| [Isolated wing jetpack](07-isolated-wing-jetpack.png) | The exact exterior of the two-shell flight pack, the lips over the recessed nozzles and how far the shells open. Read it before building the back. Its body-facing view is inferred. |
| [Construction details](02-construction-details.png) | The hood with its dark lining and headroom around her face, and the braid's root; the shoulder stack's plate thickness; the gauntlet's outer face; the elbow hinge disc and piston; the open hand and fist; the hip pivot; the knee's rear piston; and the boot from side, front, rear and below. The gauntlet's front face is shown only on the turnaround. |
| [Off-suit turnaround](04-offsuit-turnaround.png) | The woman inside: an athletic adult, about 1.68 m and 6.4 heads tall, in a charcoal work undersuit with full sleeves, straight-cut work trousers, soft knee and elbow pads, a contact harness, small lavender shoulder patches, ochre fastenings and flat ankle boots. |
| [Scale and fit](05-scale-and-fit.png) | The suit and the woman at exact relative height (400 px per metre), with her drawn inside a translucent suit in front and side. Her eyes meet the suit's face with a 0.21 m raised footbed; the hip and knee offsets measured from the images are shown. It compares, and does not prove, a wearable fit. |
| [Poses and expressions](03-poses-and-expressions.png) | Four expressions in the hood (calm, delight, focus, determined) and three grounded poses: idle, jump preparation and touchdown. It has no running pose; one could not be drawn correctly. |
| [Flight](10-flight.png) | Takeoff, hover and forward flight from one rear three-quarter camera. The pack stays flush to her back, exhaust leaves only the two pod nozzles and opposes her travel, and her legs trail in one continuous line. |
| [Handheld scale](06-handheld-scale.png) | The handheld: an original portrait console in see-through atomic orange, 75 × 133 × 27.5 mm, about 8% of her standing height and about three quarters of her hand's length. Its orthographic front, side and back are scaled to those dimensions; the held views are illustrative perspective. |
| [Thrusters, rear and rear low](12-thrusters-rear-and-rear-low.png) | A larger view of the pack's lips and nozzles, companion to sheet 07. |
| [Rear low](08-thrusters-rear-low.png), [rear three-quarter](09-thrusters-rear-three-quarter.png), [inner section](13-thrusters-inner-section.png) | Enlarged crops of the same pack as sheet 07, with no authority of their own. |

## Decisions

The owner deferred these to the lead, who decided them:

- **Off-suit proportion:** the grounded athletic adult, 1.68 m.
- **Her head inside the hood:** her real head, about 0.26 m, sits inside the
  unchanged hood, so one head model serves both forms and more helmet shows
  around her face.
- **No earrings.**
- **Dusty lavender**, the owner source's value (about `#b186b6` lit), never the
  saturated violet that image generation drifts toward.

## Preserve the character

Keep warm brown skin, expressive brown eyes, black swept hair and one thick
braid rooted beside her right cheek, through the hood's front opening when
suited. The braid can trail during movement; its root never moves, and it never
comes out of the back of the hood or the back of her head. The hood has an
ivory face rim and a lavender shell with round ear discs, and its opening is
one continuous curve.

The suit is built from thick plates that sit on plates, with their edges
showing depth, over charcoal joints that leave room to bend. Pistons are a rod
entering a cylinder, attached at both ends. Lavender and ivory dominate; a
small ochre collar and belt and restrained cyan lights are accents. Panel lines
and colour borders are paint, and vents are a pair of recessed slots, so the
whole suit is buildable from SDF primitives with no surface noise.

The off-suit Moth is a grown, competent woman with an athlete's build and a
working pilot's clothes. Nothing about her is sexualized.

## Flight pack and motion

Keep exactly two rigid curved pods beside a narrow charcoal spine. Each has two
ivory bands, a small dark upper opening, a small grey inset, a long tapered
lower lip and one recessed cyan nozzle under that lip. Upper mounts sit below
the hood and behind the shoulder armor; the lower tips reach the upper pelvis.
The pods open only a few degrees in powered states, with no added segments,
fins or outlets. Exhaust is short and translucent and leaves only the two pod
nozzles; the boots carry small cyan lights and never fire.

The thruster references each show the pods through their own camera, so no
view's perspective belongs in the 3D form. The nozzle sits square to the
opening it fills, its axis the opening's normal (in, down and a little back)
and its face flush with the opening; never aim the nozzle at a camera to
reproduce a sheet.

The owner source and later owner feedback take precedence over every generated
sheet, which are concept interpretations rather than orthographic blueprints.
Small perspective differences do not authorize new parts. For implementation
priorities, see the [hero brief](../armored-chibi-hero-brief.md); the
[studio guide](../../../../worlds/debug/avatars/moth.md) covers the current
model, its controls and live editing.
