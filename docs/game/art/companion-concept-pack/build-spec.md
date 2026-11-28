# Companion and worn screen build specification

Mintstitch and Tangerine are the Moth’s two swappable companions. Each has one
eye and one active embodiment when selected. Use the [turnarounds](README.md#build-reference)
for the silhouette and construction and the [lighting sheets](README.md#lighting-character)
for expression, light and night-time spill.

## Model frame and authoring

Dimensions and positions are in centimetres at rest. Divide lengths by 100
when writing `.puck`. The companions use `puck.creation.v2` axes: +X is the
creation’s right, +Y is up and −Z is forward. Origin is the body rig pivot.
In the tables, E is an ellipsoid, SE a superellipsoid, and R a rounded box.
Dimensions are full local X × Y × Z unless marked `r` (radius), `Ø` (diameter)
or as a thickness. Use half-dimensions for E, SE, Box and Prism scales.
A cylinder’s default axis is +Y. A face-on cylinder or ring uses axis Z
and a side pin uses axis X. “+” means hard union. “Smooth” means smooth union
with the stated radius. “Cut” means subtraction within that named part.
Use a separate SDF group for each local cut or intersection, and a shared
motion owner for members of a moving group.
[Creation frame and grouping](../../../../.claude/skills/sdf-authoring/SKILL.md#the-creation-frame)
· [Primitive scales](../../../../.claude/skills/sdf-authoring/references/shapes.md#what-scale-means)

`s` means +1 on the creation’s right and −1 on its left. Make mirrored
copies with a loop. Bake each rigid part in its rest pose. Carry the parts
with the existing companion placement, flight, and guide system. Apply the
offsets and angles below to the presentation rig, leaving movement and
targeting on the companion body. Reuse the same flight kit and cue inputs
when the player switches the displayed companion.
[Standard companion](../../../../worlds/standard/companion.puck)
· [Shape rigs](../../../../.claude/skills/sdf-authoring/references/rig.md#attaching-a-shape)

## Mintstitch

### Construction

Four wings, tall above and broad below, sit behind a dark teal body and
scalloped felt ruff. Two brass antenna tips and two side pins are opaque.
Give the eye the same light-facing catchlight throughout the motion set.
The rest envelope is 15.4 × 11.6 × 5.5 cm.

| ID and part | Primitive and dimensions | Position and rotation | Blend | Rigid group | Material / colour |
| --- | --- | --- | --- | --- | --- |
| M1 Body | SE 5.10 × 5.36 × 4.04, exponent 2.25 | (0, 0, 0) | + | Body | Felt `#214D51` |
| M2 Ruff block | SE 5.20 × 3.56 × 3.20, exponent 2.2 | (0, −0.98, −0.58) | Smooth 0.22 to M1 | Body | Felt `#285A58` |
| M3 Ruff lobes ×13 | E 1.02 × 1.34 × 1.20 | (2.36 cos t, 0.05 + 2.12 sin t, −1.27), t = −168° + 13° i, i = 0…12. Roll −20° cos t | Smooth 0.08 to M2 | Body | Alternating felt `#306763` / `#2C5D59` |
| M4 Antennae ×2 | Sweep r 0.135. Straight quadratic | From (s·0.89, 2.27, −1.00) to (s·1.24, 3.46, −1.02). Control point midway | + | Body | Dark brass `#9A6530` |
| M5 Antenna beads ×2 | Sphere Ø0.98 | (s·1.24, 3.46, −1.02) | + | Body | Brass `#DDA65D` |
| M6 Knuckles ×4 | E 1.28 × 1.24 × 1.30 | Upper (s·1.86, 0.88, 0.58), lower (s·1.74, −0.81, 0.58) | + to body, seated inside lobes | Body | Dark felt `#1D4648` |
| M7 Upper wing pair | E 7.44 × 4.16 × 1.24 | (s·4.58, 3.40, 1.05), Z roll s·41° | + | Upper L/R | Teal felt `#286569` |
| M8 Lower wing pair | E 6.72 × 4.08 × 1.14 | (s·4.33, −2.66, 0.99), Z roll −s·35° | + | Lower L/R | Teal felt `#245D63` |
| M9 Quilt pads ×3 per wing | E 5.30 × 1.88 × 1.215 upper / 1.117 lower | Wing-local (0.42, 0.85 k, 0), k = −1…1. Mirror and roll with M7 / M8 | Smooth 0.08 to its wing | Corresponding wing | Middle `#397B7B`, sides `#326F72` |
| M10 Aperture housing | Cyl annulus Ø4.98, bore Ø3.96, depth 0.78, edge 0.09 | (0, 0.65, −2.25), axis Z | + to M1, cylinder cut extended 0.10 at each end | Body | Brass `#DDA65D` |
| M11 Gasket | Cyl annulus Ø4.16, bore Ø3.84, depth 0.22, edge 0.03 | (0, 0.65, −2.56), axis Z | +, cylinder cut | Body | Brown gasket `#705230` |
| M12 Side pins ×2 | Cyl Ø1.26 × 0.52, r0.10 edge. E cap 0.32 × 0.86 × 0.86 | (s·2.45, 0.65, −2.31). Cap 0.27 farther out | + | Body | `#9A6530` / `#DDA65D` |
| M13 Eye globe | Sphere Ø4.00 | (0, 0.65, −1.45) | + | Gaze | Ivory `#EADDC1` with ocular paints |
| M14 Upper lid | Sphere shell Ø4.18 exterior, wall 0.045, clipped cap | Same centre as M13. Cap construction and angle below | Intersection within Lid | Lid | Green `#4D7668`, rim `#274F48` |
| M15 Thread and root seam | Parented Sweep paints, widths below | Both faces of M7–M9 for thread, front only for emission | Paint and flush diffuser | Corresponding wing | `#E2CE9D` / `#B3936D` / `#174746` |
| M16 Housing collar | Cyl annulus Ø4.60, bore Ø3.72, depth 1.14, edge 0.09 | (0, 0.65, −1.42), axis Z | +, joins to M1 and M10. Bore extends 0.10 at each end. | Body | `#705230` |

The wing’s local X runs from root to tip. For each of the three cream
curves use quadratic points (−2.63, 0.35 k), (−0.05, 1.84 k + 0.20),
(2.66, 1.01 k + 0.23), for k = −1, 0, 1. Project each onto the wing face.
Paint them 0.096 cm wide. Use a dashed ochre hem 0.072 cm wide at 92% of the
outline ellipse, with 27 repeats around each face, one dash spanning 6° of
the ellipse parameter. Evaluate the repeat in the wing-local shade so the
hem and all three arcs can fit in a small palette. Keep the broad quilt
relief in M9 and the fine felt texture in surface shading.

The root seam follows the front face through (−2.80, −0.20),
(−1.52, −0.73), (0.41, −0.56) as a quadratic. Use a 0.21 cm-wide flush
band with the region coordinate `t` running root to tip. Give the back a
plain dark welt along the same path. The three cream stitches and the root
seam double as light channels on the front. The dashed hem, back welt, brass
pins and ocular paints keep their reflective finish.

### Materials

| Parts | Surface and hex | Roughness / metal | Finish |
| --- | --- | --- | --- |
| M1–M3, M6 | `#214D51 #285A58 #306763 #2C5D59 #1D4648` | 0.87 / 0 | Mottled felt, sheen 0.14. Keep the ruff readable as soft lobes. |
| M7–M9 | `#286569 #245D63 #397B7B #326F72` | 0.89 / 0 | Quilted wool, sheen 0.18. Satin-free front and backing. |
| M15 | `#E2CE9D #B3936D` | 0.77 / 0 | Three curved cream arcs and dashed ochre hem. |
| M15 | `#174746` dark, `#86FFD2` neutral reference | 0.42 / 0 | Diffusing fibre in the root seam. See [emissive paints](#emissive-paints). |
| M4, M5, M10, M12 | `#9A6530 #DDA65D` | 0.36 / 0.83 | Satin brass, subtle darker ageing in the recesses. |
| M11, M16 | `#705230` | 0.59 / 0 | Matte flexible gasket and supporting collar. |
| M13 | `#EADDC1 #205D51 #43BC96 #0B161A #FFF7E6` | 0.19 / 0 | Ivory globe, dark limbus, mint iris, dark pupil, cream catchlight. |
| M14 | `#4D7668 #274F48` | 0.48 / 0 | Green lid with darker closing edge. |

## Tangerine

### Construction

One orange bell, one brass-rimmed eye, one dark keel lamp and exactly two
spoon flippers. The brass side covers and crown are opaque. The right side
mirrors the left. The rest envelope is 15.6 × 13.6 × 9.2 cm.

| ID and part | Primitive and dimensions | Position and rotation | Blend | Rigid group | Material / colour |
| --- | --- | --- | --- | --- | --- |
| T1 Dome | SE 9.52 × 11.90 × 8.08, exponent 2.12 | (0, 0.50, 0.55) | +, clip at Y −5.15 beneath skirt | Dome | Enamel `#E76D2E` |
| T2 Eye seat cut | Cyl Ø4.90 × 3.75 | (0, 0.85, −2.175), axis Z | Cut T1 only, back at Z −0.30 | Dome | Inside `#4D3427` |
| T3 Skirt | Ellipse Prism 7.90 × 6.952, extruded 1.64, edge 0.29 | (0, −4.55, 0.60), extrusion along Y | + | Body | Charcoal `#31363D` |
| T4 Crown base and cap | Cyl Ø4.10 × 0.86, edge 0.10. Cyl Ø2.32 × 0.48, edge 0.095 | (0, 6.06, 0.55), (0, 6.55, 0.55), axis Y | + | Body | `#9D652C` / `#CC9049` |
| T5 Crown studs ×4 | E 0.44 × 0.40 × 0.44 | 1.60 r around crown at Y 6.47, 45° + 90° i | + | Body | `#DCAF62` |
| T6 Bezel | Cyl annulus Ø6.84, bore Ø4.76, depth 1.32, edge 0.09 | (0, 0.85, −3.18), axis Z | +, cylinder cut extended 0.10 at each end | Body | Satin brass `#C48B41` |
| T7 Lens seat | Cyl annulus Ø5.04, bore Ø4.62, depth 0.22, edge 0.04 | (0, 0.85, −3.69), axis Z | +, cylinder cut | Body | `#4D3427` |
| T8 Bezel rivets ×8 | E 0.52 × 0.52 × 0.36 | (2.97 sin t, 0.85 + 2.97 cos t, −3.83), t = 45° i, i = 0…7 | + | Body | `#DCAF62` |
| T9 Ear covers ×2 | Cyl Ø2.96 × 1.02, edge 0.18. Cyl inset disc Ø2.20 × 0.32, edge 0.11 | (s·4.65, 0.90, 0.52), axis X. Inset 0.51 farther out | + | Body | Brass `#A56A32 #744725` |
| T10 Lower hinge and sleeve | E 1.78 × 1.58 × 1.60. E 1.12 × 1.18 × 1.18 | Pin (s·3.91, −3.85, 0.08), sleeve offset (s·0.45, −0.26, 0) | +, rotate sleeve with its flipper | Body pin / Flipper L/R sleeve | Brass `#9D652C #CC9049` |
| T11 Flipper shank ×2 | Tapered straight Sweep r 0.34 → 0.60 | Pin to (s·5.91, −5.22, 0.10). Control point midway | Smooth 0.12 to bowl and sleeve | Flipper L/R | Cream `#F0D5A3` |
| T12 Spoon bowl ×2 | E 4.44 × 2.00 × 1.10, shallow E cut 2.80 × 1.22 × 0.52 | Same centre as T11 end, Z roll −s·37°. Cut offsets outward 0.20 in bowl-local X and 0.63 toward −Z | +, cut only its bowl | Flipper L/R | Cream with orange tip `#F0D5A3 #E76D2E` |
| T13 Keel light and gasket | One 4.32-wide curved strip. Gasket 0.54 tall, diffuser 0.34 tall | Y −4.75, Z = 0.55 − 0.88√(3.95² − X²), X −2.16…2.16 | Cut gasket seat 0.13 deep into T3. Fill flush, hard join | Body | `#1C2229 #337A7F` |
| T14 Eye globe | Sphere Ø5.04 | (0, 0.85, −2.04) | + | Gaze | `#10434C` with ocular paints |
| T15 Upper lid | Sphere shell Ø5.22 exterior, wall 0.045, clipped cap | Same centre as T14. Cap construction and angle below | Intersection within Lid | Lid | Brown `#604537`, rim `#382A24` |

Cut the bowl a maximum 0.18 cm deep. Mask its rounded tip orange where
outward bowl-local X exceeds 1.29 cm. Add two fine, dark enamel seams as
paint, one across the equator at Y 0.50 and one in the X = 0 meridian.
Set widths to 0.052 and 0.072 cm. Keep brass uninterrupted. Add three
small primer chips at dome-front (X,Y) pairs (−1.70,4.60), (1.82,3.78),
(−2.54,2.30). Each is an ellipse 0.46 × 0.20 cm, rotated −18° and
projected onto the shell. Subdue these to hold the dome colour at palm
scale. The keel’s `t` runs from the creation’s left (−X) to right (+X).

### Materials

| Parts | Surface and hex | Roughness / metal | Finish |
| --- | --- | --- | --- |
| T1, T12 tip | `#E76D2E` | 0.26 / 0 | Burnt orange gloss enamel, coat 0.36. |
| T1 seam and chips | `#A54D29 #B28361` | 0.59 / 0 | Fine seam and three shallow matte chips. |
| T2, T7 | `#4D3427` | 0.64 / 0 | Dark lens seat. |
| T3 | `#31363D` | 0.64 / 0 | Satin charcoal, one continuous skirt. |
| T4–T6, T8–T10 | `#9D652C #CC9049 #DCAF62 #C48B41 #A56A32 #744725` | 0.34 / 0.87 | Warm satin brass. Reserve brightest edges for bezel and rivets. |
| T11, T12 | `#F0D5A3` | 0.30 / 0 | Warm cream enamel, coat 0.30. |
| T13 | `#1C2229 #337A7F` dark, `#76F4F5` neutral reference | 0.39 / 0 | Charcoal gasket and milk-blue diffuser. See [emissive paints](#emissive-paints). |
| T14 | `#10434C #13727C #20D4CA #0B161A #FFF7E6` | 0.17 / 0 | Deep teal globe, dark limbus, turquoise iris, dark pupil, cream catchlight. |
| T15 | `#604537 #382A24` | 0.40 / 0 | Brown lid with darker closing edge. |

## Eye and rig

Use the same eye mechanism in both creations. The globe’s visible surface
uses concentric radial paints in its gaze frame. Project onto the front
hemisphere. Use radial steps at 0.74, 1.46, 1.58 cm for Mintstitch and
1.04, 1.96, 2.08 cm for Tangerine, measured from the neutral forward axis.
Put one 0.106 × globe radius catchlight at viewer-upper-left, 0.33 cm
right (+X), 0.51 cm up in globe-local projection. Use a static high-albedo
paint or reflection lobe with zero emission. Iris, pupil and catchlight
ride one gaze transform. The lid is a concentric spherical shell restricted
by a half-space. In lid local coordinates keep the part above the plane
`Y = −0.11 × outer radius`. Bury the return in the body, keeping the cap
and cutters in the lid group. For the
full blink use a negative-X hinge rotation that brings the lid’s leading
edge down on the front hemisphere. The gasket hides the cap’s side edges.

| Group | Parent and pivot (cm) | Travel and direction |
| --- | --- | --- |
| Body | Companion placement, (0, 0, 0) | Y bob −0.25 to +0.85 cm, nose-down pitch −13°…+19° around −X, bank ±19° around −Z |
| Mintstitch upper L/R | Body, (s·1.86, 0.88, 0.58) | −24°…+25° about −s·Y. Positive sweeps toward the rear, opening the face view. |
| Mintstitch lower L/R | Body, (s·1.74, −0.81, 0.58) | −16°…+17° about −s·Y |
| Tangerine flipper L/R | Body, (s·3.91, −3.85, 0.08) | −20°…+23° about s·Z. Positive raises each spoon from its −37° rest cant. |
| Mintstitch lid | Body, (0, 0.65, −1.45) | Closure 0…100% maps −69°…+87° about −X |
| Tangerine lid | Body, (0, 0.85, −2.04) | Closure 0…100% maps −82°…+87° about −X |
| Mintstitch gaze | Body, (0, 0.65, −1.45) | Yaw ±23° about −Y, elevation −18°…+16° about +X |
| Tangerine gaze | Body, (0, 0.85, −2.04) | Yaw ±20° about −Y, elevation −15°…+14° about +X |

Blink closes in 64 ms, holds 24 ms and reopens in 96 ms on Mintstitch.
Tangerine closes in 80 ms, holds 36 ms and reopens in 120 ms. Ease each
transition with smoothstep. For gaze, use yaw then elevation on the
already-yawed local axis, with a 90 ms ease to a new target, clamped to the
group limits. Blink from the current expression to full closure and back.
Mirror the asymmetric expressions for an event on the other side. Track
the lock point until it clears.

## Light and state

Mintstitch sends light along its embroidery and lets strong feelings soak
through the felt. Tangerine shifts whole areas of its dome, with the bezel,
keel and spoon edges articulating the beat. Hold the pupil, catchlight and
dark contour against the brightest emission.

### Emissive paints

Apply the masks in the moving part's rest frame. Feather a mask by at least
one projected pixel. At distance, spread subpixel stitches into adjacent felt
at lower intensity and preserve their integrated brightness. Keep quilt
relief and the enamel seams visible through the light.
[Moving surface frames](../../../../.claude/skills/sdf-authoring/references/surface.md#soft-volume-shading)

| Region | Parts and footprint | Motion coordinate |
| --- | --- | --- |
| S · Stitches | M15 front. The three 0.096 cm arcs and the 0.21 cm root band emit through the thread. | Path parameter from root (0) to tip (1). Recover it from the monotone local X of each quadratic. The root seam uses the first 0.65 of the range. |
| W · Felt wash | M7–M9 front. Shade through the pile and soften the light approaching the rim. | Linear X along the wing from root to tip. |
| R · Ruff | M3. Diffuse through the face of the lobes with a falloff around their sides. | Linear X across the throat, from left cheek to right. |
| E · Eye channel | M10/M11 join. Paint a 0.12 cm-wide annulus centred at radius 2.145 cm on the front lip, inside the brass face. | One turn, zero at the top, clockwise when seen head-on. |
| D · Dome | T1 skin. Backlight the enamel, keeping the opaque crown, ears and skirt as silhouettes. Exclude the eye cut and subdue the emission over the seams and chips. | Height from skirt cut (0) to crown (1), multiplied by side bias for a cheek. Wrap the colour around the shell. |
| B · Bezel channel | T6/T7 join. Paint a 0.17 cm-wide annulus centred at radius 2.62 cm on the inner face. The brass ring and all eight rivets stay opaque. | Same clock-face coordinate as E. |
| K · Keel | T13 diffuser inside its black gasket. Content uses `#76F4F5`. For other profiles use 85% mood tint and 15% of that cyan in linear light. | `t = (X + 2.16) / 4.32`, left to right. |
| F · Flipper edges | T12 on each spoon. Paint a 0.13 cm-wide channel on the front outer lip of the bowl. Keep the orange tips and both hinge pins opaque. | Follow the outer half of the lip, from the leading edge around the tip to the trailing edge. |

Use one authored envelope for the surface, spill and rig. Each region entry
gives **span % / floor–peak nits** on the selected part before feathering.
Measure the span along the region's motion coordinate. Decode the sRGB tint
to linear RGB and scale it to the target luminance. Add the tinted emission
to the reflected substrate in the shade compose phase. The shader can read
the body's lanes, the moving layer's coordinates and paint coverage.
[Authored shade phases](../../../../worlds/debug/shade-tier-checks.puck)

| Emotion | Tint and HSL saturation | Beats/s | Emission and gesture |
| --- | --- | ---: | --- |
| Content | Seafoam `#70DBC3`, 60% | 0.45 | Slow breath. Light the four roots, ruff and lower eye lip. Give the lower dome and long central keel the same swell. |
| Happy | Gold `#FFD659`, 100% | 1.60 | Send two evenly spaced waves outward on all four wings. Light the dome's equator, lower bezel, keel and both flippers in step with the bob. |
| Delighted | Chartreuse `#C8FF54`, 100% | 2.00 | Fill all four wings outwards over the first quarter beat and hold them wide. Open up the whole dome from crown to skirt with the bezel, keel and flipper rims at full reach. |
| Curious | Cyan `#37DAFF`, 100% | 1.20 | Send one band toward the near upper tip. Bias the dome to the near cheek, sweep the near bezel quadrant and echo it on the near keel and flipper. |
| Thinking | Violet `#A95CFA`, 94% | 0.72 | Send the centre stitch from the far upper wing around to near upper, near lower and far lower. Chase clockwise around the bezel with a subdued pool on the far cheek. |
| Worried | Orange `#FF8A45`, 100% | 1.80 | Make two inward catches at 0.18 and 0.45 beats. The second crest is 68% as strong. Bias the upper wings unequally and stutter through the ruff. Contract the dome's upper cap off-centre with a short answer on the opposite side. |
| Scared | Icy lavender `#EBE3FF`, 100% | 2.50 | Snap tip to root over the first quarter beat, then tremble once per beat in the inner folds and ruff. The upper dome flashes, with opposing bezel arcs and weaker keel and edge accents. |
| Angry | Scarlet `#FF3E4C`, 100% | 1.30 | Drive two hard beats from the centre, a third of a beat apart. Weight the lower wing bars most heavily. Hold a broad horizontal dome belt and strike the keel and flippers together. |
| Sad | Cobalt `#527DFF`, 100% | 0.40 | Drain slowly towards both lower wing tips and leave a little ruff afterglow. Let the blue pool sink below the eye and recede from the bezel and keel. |
| Tired | Ochre `#B9792E`, 60% | 0.30 | Lift once from the bottom two stitch tips or the centre of the keel, then fade through the beat and sag into a low hover. |
| Sleepy | Indigo `#6563BD`, 41% | 0.20 | Use the ruff alone or the base of the dome. Breathe once every five seconds and leave a long pause near the floor. |
| Proud | Orchid `#EB58D1`, 79% | 0.58 | Rise symmetrically through the upper wings and top eye lip, then hold. Cap the dome with an even rise into the crown and carry that shape on the top bezel and two spoon rims. |
| Sheepish | Rose `#FF8DB7`, 100% | 0.92 | Blush briefly near phase 0.24. Bias the lower wing and cheek opposite the gaze. On Tangerine, bias a low cheek, short bezel arc and the tucked flipper. |
| Determined | Spring green `#3BF89A`, 93% | 1.05 | Sweep from the wing roots or across the front belt over 0.55 beats, then hold four forward rails or a broad dome belt with the keel and flippers. |
| Focused | Ice blue `#C5F0FF`, 100% | 0.00 | Hold two short upper stitch roots and two opposed eye arcs. Tangerine holds two opposed bezel arcs and a short centre keel. Track through the bracket. |

Centre spans without a stated location. For two opposed arcs, split the total
span in half at 3 and 9 o'clock. Top and bottom arcs are centred at 12 and 6.
Near and far are relative to the indicated target side. Ease a side change
over 100 ms. Where the floor is above zero, keep the footprint visible between
pulses.

| Emotion | Mintstitch S | W | R | E | Tangerine D | B | K | F |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| Content | 42% / 72–148 | 23% / 23–54 | 72% / 35–86 | 22% / 18–35 | 42% / 55–118 | — | 85% / 54–105 | — |
| Happy | 84% / 88–230 | 55% / 38–115 | 47% / 36–84 | 55% / 30–110 | 66% / 46–128 | 58% / 58–160 | 100% / 70–195 | 65% / 42–120 |
| Delighted | 100% / 170–500 | 100% / 140–380 | 100% / 72–190 | 100% / 110–235 | 100% / 130–420 | 100% / 118–340 | 100% / 125–275 | 100% / 100–280 |
| Curious | 88% / 70–300 | 57% / 62–200 | — | 31% / 30–112 | 62% / 65–240 | 28% / 76–320 | 46% / 60–180 | 49% / 40–121 |
| Thinking | 43% / 45–180 | 22% / 18–66 | — | 18% / 44–128 | 35% / 25–90 | 24% / 70–220 | — | — |
| Worried | 63% / 65–230 | 49% / 50–143 | 38% / 19–104 | — | 54% / 55–190 | 38% / 50–175 | 48% / 32–98 | 27% / 28–82 |
| Scared | 82% / 115–460 | 49% / 64–185 | 97% / 90–360 | 78% / 72–310 | 67% / 105–440 | 60% / 100–420 | 54% / 66–230 | 35% / 40–96 |
| Angry | 85% / 145–390 | 71% / 122–310 | 72% / 68–225 | 60% / 50–145 | 74% / 150–410 | 59% / 100–280 | 100% / 100–295 | 100% / 90–235 |
| Sad | 70% / 63–150 | 77% / 52–164 | 42% / 24–69 | — | 62% / 60–170 | 34% / 30–98 | 36% / 24–63 | — |
| Tired | 31% / 30–108 | — | — | — | — | — | 31% / 35–125 | — |
| Sleepy | — | — | 48% / 24–67 | — | 27% / 30–77 | — | — | — |
| Proud | 92% / 130–325 | 91% / 100–280 | 43% / 50–100 | 66% / 70–190 | 76% / 110–335 | 70% / 85–240 | — | 68% / 54–134 |
| Sheepish | 51% / 68–215 | 39% / 45–120 | 35% / 44–145 | 26% / 30–88 | 43% / 50–156 | 29% / 60–177 | — | 39% / 46–115 |
| Determined | 91% / 128–280 | 48% / 55–135 | — | 30% / 35–100 | 65% / 75–235 | 56% / 60–158 | 83% / 88–209 | 68% / 54–150 |
| Focused | 34% / 180–180 | — | — | 54% / 180–180 | — | 51% / 285–285 | 31% / 113–113 | — |

### Eyes and body

The eye tuple gives lid closure %, gaze yaw ° and elevation °. The body
tuple gives nose-down pitch °, bank °, hinge angle ° and hover offset in mm.
Mintstitch lists upper/lower hinges in that order. Near and far offsets
add to the listed hinge angle. Use the [rig limits](#eye-and-rig) after
blending with flight. At 5 m, footprint and timing carry the expression
as the small details recede.

| Emotion | Mintstitch eye | Mintstitch body | Tangerine eye | Tangerine body |
| --- | --- | --- | --- | --- |
| Content | 32, 0, -2 | 0, 0, -4/0, 0.2 | 30, 0, -2 | 0, 0, -5, 0 |
| Happy | 15, 0, 5 | -4, 3, 12/8, 0.8 | 12, 0, 5 | -3, 3, 12, 0.8 |
| Delighted | 4, 0, 12 | -8, 0, 24/16, 1.8 | 0, 0, 12 | -8, 0, 22, 1.8 |
| Curious | 13, 18, 8 | 4, 14, -9/3, 1 · near upper +17° | 10, 18, 8 | 4, 14, 2, 1 · near spoon +11° |
| Thinking | 43, -16, 13 | 2, -7, -12/-5, 0.2 | 40, -16, 12 | 2, -7, -8, 0.2 |
| Worried | 29, -6, 3 | -4, -9, -6/-4, 0.3 · near upper +13° | 25, -6, 3 | -4, -9, 5, 0.3 · near spoon +8° |
| Scared | 0, 0, 10 | -13, 5, -22/-15, 2.2 | 0, 0, 10 | -11, 5, 22, 2.2 |
| Angry | 42, -8, -3 | 17, -13, -15/-10, 1.2 | 42, -8, -3 | 17, -13, -16, 1.2 |
| Sad | 49, -5, -15 | 11, -11, -11/-8, -1.8 | 48, -5, -14 | 11, -11, -13, -1.8 |
| Tired | 57, 0, -10 | 13, 8, -17/-13, -2.4 | 56, 0, -10 | 13, 8, -17, -2.4 |
| Sleepy | 96, 0, -10 | 3, -2, -6/-5, -2 | 96, 0, -10 | 3, -2, -10, -2 |
| Proud | 36, 0, 14 | -10, 0, 20/11, 1.2 | 32, 0, 13 | -10, 0, 15, 1.2 |
| Sheepish | 51, -19, -8 | 9, -15, -17/-13, -0.6 · far upper +9° | 49, -18, -8 | 9, -15, -12, -0.6 · far spoon +5° |
| Determined | 37, 4, 0 | 16, 4, -20/-12, 0.5 | 33, 4, 0 | 16, 4, -15, 0.5 |
| Focused | 47, tracked | 1, 0, -4/-6, 0 | 43, tracked | 1, 0, -6, 0 |

The figures give the peak pose. Spring and recoil with the Delighted and
Scared accents, then ease back through the hold or tremble. Let Tired sag
through its fade. Close and settle across one Sleepy breath. Keep flight and
target selection on the existing companion body. The eyes keep tracking while
the body banks or speaks.

### Talking

Lay the syllable over the current mood. Shape its 0–1 envelope with a 42 ms
smooth attack and 125 ms release. Group fast speech into at most five
accents per second.
Overlapping releases take the maximum. Use the local blended mood tint even
where a speech region has zero base output. The eye keeps its mood expression.

| Companion | Speech accent at envelope peak |
| --- | --- |
| Mintstitch | Add up to 170 nits to the inner 55% of S on both wing pairs, 125 nits to the central 66% of R, and 27 nits to the lower 54% of E. The root band carries the full pulse, the centre stitch 82% and the outer stitches 51%. Let up to 40 nits spread into the inner 52% of W. |
| Tangerine | Add up to 164 nits to the centre 88% of K, 110 nits to the lower 54% of B, and 100 nits across the lower-middle 61% of D. The keel takes the current mood tint over its cyan base. |

Give speech the same moving paint frames as the mood. Limit the accent on
already bright regions so Delighted and Scared keep their shape.

### Gameplay and blending

| Gameplay signal | Emotion |
| --- | --- |
| Interest | Curious |
| Discovery | Delighted |
| Objective or active guide route | Determined |
| Danger | Worried while anticipating. Scared at an imminent or confirmed threat. For a graded input, enter Scared at 0.72 and return to Worried below 0.50. |
| Combat | Angry |
| Locked target | Focused |
| Low energy | Tired |
| Calm close idle | Content |
| Syllables | Talking over the selected emotion |

Use an authored emotion row for social and story cues. Priority descends from
imminent danger, combat, anticipated danger, forced low-energy return, lock,
route, discovery, ordinary low energy, interest, social cue, to Content. Hold
Delighted for at least 0.45 s. Give Scared its first peak within 95 ms of
entry and allow at least 0.30 s before another onset. One-shots keep elapsed
phase through the hold. Active loops wrap their beat.

Crossfade the two evaluated radiance fields in linear RGB. Each field carries
its own mask, tint, phase and brightness. Use the same eased weight for the
eyes and body. Blend over 180 ms for ordinary changes, complete the blend
into Scared by its first peak, and take 330 ms to settle into Content, Sad or
Sleepy. Keep the outgoing phase live until its blend weight reaches zero.
If a threat interrupts a blend, finish the current blend within 45 ms and
then start Scared. Queue the latest eligible ordinary request during a fade.
Add the speech envelope after the colour and pose crossfade.

The four body lanes can carry the two profile IDs and side bits in slot 0,
two phase samples in slot 1, speech strength in slot 2, and the blend weight
in slot 3. Quantize each phase to 1/512 of a beat and pack each into ten bits.
Keep the first snap separate from Scared's wrapped tremble with the integer
bit of its phase sample. Use the standard companion's existing tick-driven
glow row to derive the new presentation inputs.
[Standard companion](../../../../worlds/standard/companion.puck)
· [Render lanes](../../../../.claude/skills/sdf-authoring/references/surface.md#render-lanes)

For reduced motion, use a representative fixed footprint at the midpoint
between floor and peak. Hold Scared's inner folds or opposing bezel arcs
steadily. Scale pose motion and speech accents to half amplitude and release
speech over 200 ms. Keep the difference between a small local accent and a
whole-wing or whole-dome wash.

### Light on nearby surfaces

Carry one bound light per active companion. Mintstitch uses a short tube
from (−0.055, −0.02, −0.03) to (0.055, −0.02, −0.03) m under the wings,
with a 0.025 m radius. Tangerine uses one point at (0, −0.015, −0.035) m to
give the shell and keel one shared light pool. Start at 2 lm for Content,
10 lm for Delighted and 8 lm at the Scared crest. Scale the other profiles
by luminance times lit area relative to Delighted. Drive tint and power
from the same blended profile as the body. At arm's length, the light
should colour a glove, crystal or stone rim and leave the contact shapes
visible. Check leakage across near walls in the gallery. Anchored lights
and static face emitters use different occlusion paths.
[Carried light expansion](../../../../src/Puck.World.Schema/WorldCreationLights.cs)
· [Photometric units](../../../rendering/sdf/handbook/lighting-and-shading.md#photometric-units-and-exposure)


## Flight keys

Each row starts at clip time zero. Angles are hinge deltas from construction
rest, in degrees. Each slash-separated entry is one key in time order.
Mintstitch columns list upper/lower angles for both sides. Tangerine values
move both flippers. Motion keys have zero bank. Ease between keys with
smoothstep, loop hover/glide, play dart/startle once, then blend back
over 0.12 s. Expression poses take precedence over the clip’s pitch and hinge
channels where specified. Hover bob continues unless the expression sets Y.

| Companion / clip | Time (s) | Hinge angles (°) | Bob Y (cm) | Nose-down pitch (°) |
| --- | --- | --- | --- | --- |
| Mintstitch hover | 0 / 0.18 / 0.36 | −9,−6 / +14,+9 / −9,−6 | 0 / +0.55 / 0 | 0 / −2 / 0 |
| Mintstitch dart | 0 / 0.12 / 0.27 | −4,−3 / −24,−16 / −10,−7 | 0 / −0.25 / +0.25 | 0 / +19 / +12 |
| Mintstitch glide | 0 / 0.40 / 0.80 | −12,−8 / −15,−10 / −12,−8 | 0 / +0.20 / 0 | +7 / +8 / +7 |
| Mintstitch startle | 0 / 0.08 / 0.30 | −5,−3 / +25,+17 / −5,−3 | 0 / +0.85 / +0.20 | 0 / −13 / 0 |
| Tangerine hover | 0 / 0.27 / 0.54 | −5 / +11 / −5 | 0 / +0.40 / 0 | 0 / −1 / 0 |
| Tangerine dart | 0 / 0.16 / 0.32 | 0 / −20 / −9 | 0 / −0.22 / +0.20 | 0 / +15 / +9 |
| Tangerine glide | 0 / 0.45 / 0.90 | +10 / +7 / +10 | 0 / +0.14 / 0 | +5 / +6 / +5 |
| Tangerine startle | 0 / 0.10 / 0.34 | −3 / +23 / −3 | 0 / +0.70 / +0.12 | 0 / −11 / 0 |

## Moth’s left worn screen

The worn screen opens on the dorsal face of the left bracer as the armour
flap folds sideways. Closed, it reads as the continuous ivory and lavender
shell, with a fine perimeter seam. Keep the existing two outboard vents,
charcoal cuff and elbow piston. The wrist assembly follows the left forearm
animation via the bracer. This pack depicts the left by default. When the
player chooses the right side, mirror the module, flap and opening pose
with the arm. Keep the screen artwork and text readable in the same order.
[Moth suit details](../moth-concept-pack/02-construction-details.png)
· [Authored bracer](../../../../worlds/debug/avatars/moth.puck)
· [Menu decision](../../../decisions/play.md#in-world-consoles)

### Construction and finish

Use bracer-left local coordinates `(u,v,n)`. The origin and axes come from
`bracer-left` in its rest frame. +u is the vent side, +v elbowward, +n out
through the front-facing broad side (bracer-local +Z). Use the existing
24 × 26 × 27 cm, exponent-3.6 bracer as the outer blank. The flap is
10.40 cm across × 15.20 cm along the arm, about 58% of the bracer length.
The panel’s active area is 8.50 × 12.80 cm.

| ID and part | Primitive and dimensions | Position (u,v,n) | Blend and group | Material / colour |
| --- | --- | --- | --- | --- |
| W1 Left bracer | Existing SE 24 × 26 × 27 | Bracer origin | Body. Retain existing cuff mouth. | `#F2C49A #744080` |
| W2 Well cut | Rounded-rect Prism 10.86 × 15.56, planar corner r1.06, depth 4.60 | (−1.75, −1.40, 11.70), extrude +n | Cut W1, leaving floor at n 9.40 | Well `#3B3240` |
| W3 Module tray | R 10.13 × 14.97 × 1.94, edge 0.71 | (−1.75, −1.40, 10.34) | +, bracer. Seats in W2 floor. | `#2B2630` |
| W4 Gasket / recess | R 9.07 × 13.60 × 0.45, edge 0.19 | (−1.75, −1.40, 11.36) | +, bracer | `#413848` |
| W5 Glass | R 8.50 × 12.80 × 0.25, edge 0.11 | (−1.75, −1.40, 11.585), face +n | +, bracer | `#17232A` off / `#18333B` on |
| W6 Flap | W1 skin intersected by a W2-style Prism 10.40 × 15.20, planar corner r0.95. Inner skin offset inward 0.45. | Same u,v as W2. Outer face inherits W1’s curve. | Cut skin from its own blank, intersect within flap group | `#F2C49A #744080`, lining `#5D4667` |
| W7 Hinge pair | Two cylinders Ø0.96 × 2.10, sleeves Ø1.06 × 1.34, links R 1.15 × 1.16 × 0.26. Edge r0.11 | Pins and sleeves (4.20, −1.40 ± 4.68, 12.95), axis +v. Links (3.69, −1.40 ± 4.68, 13.05) | +, pins fixed to bracer, sleeves and links ride flap | `#8B9099 #685569` |
| W8 Seam and vents | 0.116 cm flap line. Retain two vent paints. | W6 perimeter. Vent centres u 12, v 7.50 and 4.00, n 0. | Paints on their owners | `#685169 #2B2630` |
| W9 Hinge pockets | Two rounded-rect Prisms 1.75 × 2.34, planar corner r0.20, depth 1.46 | (3.95, −1.40 ± 4.68, 13.05), extrude +n | Cut W1, overlapping W2. Floor at n 12.32. | `#3B3240` |

Carry the existing lavender paint boundary across the flap in the closed
pose. For W6 form the shell between two copies of W1, subtracting the
inner offset, then intersect with its footprint. Keep W2 and W6 cuts local
to their respective groups. Closed, the 0.23 cm side clearance leaves only
the narrow recess visible from normal play distance. In the open pose, W5
stays seated under the armour’s outer crest. Opaque metal hinge knuckles
remain seated on the same pair of pins throughout the reveal.

| Parts | Surface and hex | Roughness / metal | Finish |
| --- | --- | --- | --- |
| W1, W6 outer | Ivory `#F2C49A`, lavender `#744080` | 0.36 / 0 | Share Moth armor paint, specular 0.08, coat 0.28, and its edge wear. |
| W3, W8 vents | Charcoal `#2B2630` | 0.64 / 0 | Low-gloss polymer with dark vents. |
| W2, W4, W6 lining and edge, W9 | `#3B3240 #413848 #5D4667 #685169` | 0.55 / 0 | Soft gasket and quiet satin underside. |
| W5 | `#17232A #18333B` | 0.22 / 0 | Anti-glare glass. Restrained cyan only on the live display. |
| W7 | `#8B9099 #685569` | 0.38 / 0.74 | Satin steel, dark under the shell. |

### Reveal and menagerie

Pivot W6 about (4.20, −1.40, 12.95), +v in the bracer frame. The far
edge lifts toward +n and rolls to the vent side. Keep W1, W3–W5 and W7
still relative to the bracer throughout. The opening takes 0.34 s with
smoothstep between keys. On dismiss, play in reverse. Close before
returning to ordinary interaction and remove screen focus on dismiss.
Raise the reading arm on open. At the end of the stationary raise,
position the screen centre 42 cm ahead of the eye line and 20 cm below
it, with the screen normal toward the face. Ease the camera 8 cm toward
the screen and keep the horizon level. Moving uses a shorter glance with
the centre 48 cm ahead and 28 cm below the eye line, tilted 12° farther
down about +u, with 3 cm camera travel. Use the same 340 ms reveal for
either read while the world continues to run.

| Frame | Time | Flap | Screen |
| --- | --- | --- | --- |
| Closed | 0 ms | 0°, armor flush | Off |
| Unlatch | 75 ms | 14° | Off |
| Fold | 205 ms | 63° | Dim wake at 2% of display white |
| Ready | 340 ms | 106° | Companion roster live. Input becomes available here. |

The worn screen shows the menu. Its companion roster page shows exactly two stacked cards,
Mintstitch first and Tangerine second. Each uses the character’s front-view
portrait, a name and its button label. The active card shows a check, a
clear `Active` label and a cyan keyline. The stored companion shows
`Equip`. Illustrate Mintstitch as active and update both cards on equip.
Persist the selected companion on the player and present one companion
through the standard role. Menu focus and companion equip are separate
actions. Use the wearer’s local worn-screen surface so private menu contents
stay with the player.

The screen’s artwork uses an 850 × 1280 reference canvas (portrait).
Coordinates start at top left. Fill the canvas with `#111D23`. Use
`#F4EDDF` for names and page title, `#A9BBB7` for secondary text,
`#8CF2E2` for active and `#46696A` for the stored border. Use Segoe UI or
the same authored humanist face as the menu. Place `Menu` at (70, 52)
in 34 px and `Companion roster` at (70, 106) in 68 px. Card origins are (68, 278)
and (68, 629), with size 714 × 303 and corner r24. Each card has a
205 × 205 portrait at (+34,+49), a 45 px name at (+255,+83), and a
34 px action or status at (+255,+157). Active outline is 4 px, stored
outline 2 px. Bottom `Back` sits at (70, 1086) in 38 px. Keep the remaining
surface calm. The lamp accents belong to the screen and the companions.
