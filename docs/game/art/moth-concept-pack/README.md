# Moth concept pack

These sheets are the visual standard for the Moth, the debug world's avatar,
built as the native SDF creation in [moth.puck](../../../../worlds/debug/avatars/moth.puck).
They show her in two forms: the suited Moth, a compact powered suit in a
friendly arcade mech style, and the off-suit Moth, the woman inside it. The
suit on/off toggle exists for one demonstration: holding a real-size handheld
console in her own hands.

The sheets were made by an art-direction pair. Codex (its built-in image
generation tool) wrote the prompts, generated every sheet and judged which
ones to keep. Claude held the owner's constraints and checked each keeper
against them: hand and finger counts, the braid's root, the two-shell pack,
the palette, SDF buildability and the handheld's real size. The reading order
and the authority lines below are Codex's verdicts. Prompts and how each sheet
was edited or composited are in [prompts.md](prompts.md).

## Read the images in this order

| Sheet | Authoritative for |
|---|---|
| [Owner source](00-owner-source.png) | Identity. Character 4, bottom left, is the Moth; the other crew members are context. It outranks every generated sheet. |
| [Suited turnaround](01-suited-turnaround.png) | The suit's silhouette, proportions and palette in front, right side, back and three-quarter views: the dusty lavender, the hood, gauntlets and boots as the three main forms, and a quiet torso. The ivory chest sides are paint on one continuous breastplate; the shading that makes them read as raised pads is a known flaw of the image, not geometry to build. |
| [Construction details](02-construction-details.png) | Hood opening and the braid's root, the open hand and the fist, the gauntlet, and the boot from side, front, rear and below. The gauntlet's inside and the sole are proposals; painted seams stay paint and never become grooves. |
| [Isolated wing jetpack](07-isolated-wing-jetpack.png) | The exact exterior of the flight pack. Read it before building the back. Its body-facing view is inferred. |
| [Thrusters, rear low](08-thrusters-rear-low.png), [rear three-quarter](09-thrusters-rear-three-quarter.png), [on the body](10-thrusters-on-body-three-quarter.png), [on the body from behind](11-thrusters-on-body-back.png), [rear and rear low](12-thrusters-rear-and-rear-low.png), [inner section](13-thrusters-inner-section.png) | The owner's chosen thruster views, closeups of the same pack as sheet 07. The rear-low view (08) is the one to get right first. |
| [Poses and expressions](03-poses-and-expressions.png) | Four expressions (calm, delight, focus, determined) and six key poses: idle, run, jump preparation, hover, forward flight and touchdown. The hover and forward-flight figures were generated separately and composited into the sheet, so the panels' scale is illustrative. |
| [Face identity](14-face-identity.png) | Her face and expressions only: the same woman in both forms. Its hood, shoulder and boot studies are an earlier suit and are superseded by sheets 01 and 02. |
| [Off-suit turnaround](04-offsuit-turnaround.png) | The off-suit Moth, option B: an athletic adult about 1.68 m and about 6.4 heads tall, in a charcoal work undersuit with full sleeves, straight-cut work trousers, soft knee and elbow pads, a contact harness, small lavender shoulder patches, ochre fastenings and flat ankle boots. |
| [Scale and fit](05-scale-and-fit.png) | The two off-suit options beside the 2.06 m suit at exact relative height (400 px per metre): A, compact, 1.65 m and about 4.3 heads; B, grounded, 1.68 m and about 6.4 heads. Each is drawn inside a translucent suit with the raised footbed, eye line, headroom, and hip and knee offsets measured from the images. It shows where the fit does not line up; it does not prove a wearable fit. |
| [Handheld scale](06-handheld-scale.png) | The handheld, an original portrait console in see-through atomic orange, 75 × 133 × 27.5 mm, about 8% of her standing height and about three quarters of her hand's length. Its orthographic front, side and back are scaled to those dimensions; the held views are illustrative perspective. |

## Preserve the character

Keep warm brown skin, expressive brown eyes, black swept hair and one thick
braid rooted beside her right cheek, through the hood's front opening when
suited. The braid can trail during movement; its root never moves, and it never
comes out of the back of the hood or the back of her head. The hood has an
ivory face rim and a lavender shell with round ear discs. Its opening follows
the forehead and cheeks in one continuous curve, never a box or a pentagonal
"house" around the face.

The suit is built from broad plate faces, tapered volumes, controlled bevels
and purposeful curves: swept overlapping shoulders with a round front pivot,
large rounded gauntlets, flared shin shells and substantial flat-soled boots.
Charcoal joints separate rigid pieces and leave room to bend. Lavender and
ivory dominate; a small ochre collar and belt and restrained cyan lights are
accents. Seams and colour bands are paint. Geometry carries only the
silhouette, joint gaps, major overlaps and openings, so the whole suit is
buildable from SDF primitives with no surface noise.

The off-suit Moth is a grown, competent woman with an athlete's build and a
working pilot's clothes. Nothing about her is sexualized: no cleavage, no bare
midriff, no clothing that traces her body.

## Flight pack and motion

Keep exactly two rigid curved pods beside a narrow charcoal spine. Each has two
ivory bands, a small dark upper opening, a small grey inset, a long tapered
lower lip and one recessed cyan nozzle under that lip. Upper mounts sit below
the hood and behind the shoulder armor; the lower tips reach the upper pelvis.
The pods open only a few degrees outward and aft in powered states, with no
added segments, fins or outlets and no change in size. Exhaust is short and
translucent and leaves only the two pod nozzles; boot heel ports are small cyan
lights.

The thruster references each show the pods through their own camera, so no
view's perspective belongs in the 3D form. The nozzle sits square to the
opening it fills, its axis the opening's normal (in, down and a little back)
and its face flush with the opening. Its lens faces a viewer only where that
viewer looks along the normal; never aim the nozzle at a camera to reproduce a
sheet.

## Decisions waiting on the owner

Codex's recommendations, recorded here until the owner rules:

- **Off-suit proportion.** Codex recommends B, the grounded adult, because its
  build reads as a grown woman with the same face; A stays on sheet 05 as the
  alternative.
- **Her head inside the hood.** Under B her real head is about 0.26 m, smaller
  than the face every suited sheet draws. Codex recommends building her real
  head inside the unchanged hood, with more helmet around her face, so one head
  serves both forms; sheet 05's B front fit shows how that looks. The
  alternative keeps the larger suited face as a stylized read that is not
  literally her head. The suited sheets keep the larger face until the owner
  rules.
- **Earrings.** Small gold studs appear in the generated sheets but are not in
  the preserved identity; Codex recommends leaving them off the model unless
  the owner wants them.
- **Palette.** Codex recommends keeping the dusty lavender of the owner
  source and rejecting the saturated violet that image generation drifts
  toward.

A suit-up transition sheet was attempted and rejected; none ships. The owner
source and later owner feedback take precedence over every generated sheet,
which are concept interpretations rather than orthographic blueprints. Small
perspective or seam differences do not authorize new parts. For implementation
priorities, see the [hero brief](../armored-chibi-hero-brief.md); the
[studio guide](../../../../worlds/debug/avatars/moth.md) covers the current
model, its controls and live editing.
