# Companion and wrist journal build specification

Mintstitch and Tangerine are the Moth’s two swappable companions. Each has one
eye and one active embodiment when selected. Use the [reference sheets](README.md#build-reference)
for the silhouette and finish, and the dimensions here for the build.

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
| M15 Thread and root seam | Parented Sweep paints, widths below | Both faces of M7–M9 for thread, front only for emission | Paint and flush diffuser | Corresponding wing | `#E2CE9D` / `#B3936D` / `#174746` / cue |
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
plain dark welt along the same path. Keep the cream thread, brass and eye
outside the emissive mask.

### Materials

| Parts | Surface and hex | Roughness / metal | Finish |
| --- | --- | --- | --- |
| M1–M3, M6 | `#214D51 #285A58 #306763 #2C5D59 #1D4648` | 0.87 / 0 | Mottled felt, sheen 0.14. Keep the ruff readable as soft lobes. |
| M7–M9 | `#286569 #245D63 #397B7B #326F72` | 0.89 / 0 | Quilted wool, sheen 0.18. Satin-free front and backing. |
| M15 | `#E2CE9D #B3936D` | 0.77 / 0 | Three curved cream arcs and dashed ochre hem. |
| M15 | `#174746` dark, `#86FFD2` neutral reference | 0.42 / 0 | Diffusing fibre in the root seam. Apply the cue table in play. |
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
| T13 Keel light and gasket | One 4.32-wide curved strip. Gasket 0.54 tall, diffuser 0.34 tall | Y −4.75, Z = 0.55 − 0.88√(3.95² − X²), X −2.16…2.16 | Cut gasket seat 0.13 deep into T3. Fill flush, hard join | Body | `#1C2229 #337A7F`, cue |
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
| T13 | `#1C2229 #337A7F` dark, `#76F4F5` neutral reference | 0.39 / 0 | Charcoal gasket and milk-blue diffuser. Apply the cue table in play. |
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

| Expression | Mintstitch closure / yaw, elevation | Tangerine closure / yaw, elevation |
| --- | --- | --- |
| Rest | 24% / 0°, 0° | 12% / 0°, 0° |
| Full blink | 24 → 100 → 24% / preserve gaze | 12 → 100 → 12% / preserve gaze |
| Look limits | 24% / −23…+23°, −18…+16° | 12% / −20…+20°, −15…+14° |
| Interest | 16% / +16°, +8° | 8% / +16°, +8° |
| Discovery | 4% / 0°, +12° | 0% / 0°, +12° |
| Objective | 27% / 0°, 0° | 16% / 0°, 0° |
| Danger | 3% / 0°, 0° | 0% / 0°, 0° |
| Combat | 48% / −15°, 0° | 38% / −15°, 0° |
| Locked target | 40% / tracked, clamped | 32% / tracked, clamped |
| Low energy | 64% / 0°, −10° | 53% / 0°, −10° |
| Speaking | Base closure / base gaze | Base closure / base gaze |

Blink closes in 64 ms, holds 24 ms and reopens in 96 ms on Mintstitch.
Tangerine closes in 80 ms, holds 36 ms and reopens in 120 ms. Ease each
transition with smoothstep. For gaze, use yaw then elevation on the
already-yawed local axis, with a 90 ms ease to a new target, clamped to the
group limits. Mirror the interest and combat examples when the event is
on the opposite side. Track the lock point until it clears.

## Light and state

The neutral turnaround shows each diffuser at its idle hue. The idle mask
keeps Mintstitch’s long mint seams and Tangerine’s cyan keel recognizable
between shared gameplay cues.
Regions are intervals of the `t` coordinate on the light footprints.
Give each strip a flush diffuser and paint the cue through an authored
shade program. Use `lanes.slot0` for the cue index 0…7 (table order,
excluding speaking), `slot1` for elapsed cue phase in beats, `slot2`
for the 0…1 speech envelope and `slot3` for the near-side sign. Author the
cue index as a discrete selector, and ease the brightness separately.
On Mintstitch derive t by inverting the monotone quadratic X of M15 in the
rest wing frame. On Tangerine use `t = (X + 2.16) / 4.32`. Clip the
surface shade to the diffuser and keep the rest of the material dark.
Use the table hue as the emission tint at 240 nits peak for Mintstitch
and 300 nits for Tangerine, before the beat and speech multipliers.
Keep the emission local to the material, with no light source or halo mesh.
[Render lanes](../../../../.claude/skills/sdf-authoring/references/surface.md#render-lanes)

| State | Hex | Mintstitch lit region | Tangerine lit region | Rhythm (beats/s) and pattern | Pose delta |
| --- | --- | --- | --- | --- | --- |
| Idle | Mintstitch `#86FFD2`, Tangerine `#76F4F5` | Four long root seams, t 0.02–0.72 | Whole keel, t 0.015–0.985 | 0.65, continuous raised cosine | Hover loop. Neutral roll. |
| Interest | `#88E7FF` | Near upper root, t 0.02–0.46 | Near half, t 0.50–0.985 | 1.4, two raised cosines then hold | Pitch +8°, roll +8°. Raise near wing 8°, or near spoon 5°. |
| Discovery | `#64FFD0` | All seams, t 0.02–0.98 | Whole keel, t 0.015–0.985 | 1.2, one broad bloom then hold | Rise +0.65 cm and pitch +4°. Open upper wings +12°, lower +8°, or spoons +14°. |
| Objective | `#CFACFF` | Long seams, t 0.02–0.98 | Whole keel, t 0.015–0.985 | 1.0, one sweep then full hold. Sweep root to tip or toward route side. | Pitch +16°. Set upper/lower wings to −18°/−12°, or spoons −13°. |
| Danger | `#FF962E` | Both upper seams, t 0.02–0.98 | t 0.015–0.26 and 0.74–0.985 | 2.5, one sharp rise then hold | Brake at −9° pitch. Set upper/lower wings to +12°/+9°, or spoons +18°. |
| Combat | `#FF5473` | Four inner seams, t 0.02–0.78 | Both danger ends plus t 0.38–0.62 | 2.0, one swell on evasive move then hold | Pitch +13°, roll −19°. Sweep upper/lower wings −14°/−9°, or spoons −11°. |
| Locked target | `#EDF8FF` | Both upper roots, t 0.02–0.46 | Centre belt, t 0.20–0.80 | 0, steady | Pitch/roll 0°. Set upper/lower wings −4°/−6°, or spoons −6°. Gaze tracks. |
| Low energy | `#D98D37` | Both lower ends, t 0.82–0.98 | Centre ember, t 0.43–0.57 | 0.35, one fade then dim hold | Pitch +13°, Y −0.20 cm. Upper/lower wings −11°/−8°, or spoons −12°. |
| Speaking | `#80F3DD` | Inner idle roots, t 0.02–0.46 | Idle centre, t 0.29–0.71 | Up to 3.0 peaks/s from syllables | Continue base motion and eye expression. |

Except for low energy, pulse between 0.38 and 1.0 of peak, then hold at
0.66 where shown. Lock holds at 0.8. Low energy fades from 0.45 to 0.22.
For the danger rise use the first quarter of its beat followed by the held
level. For the objective, advance a 0.26-wide full-intensity band over the
held floor in one second. Mirror near-side regions for −X. Apply speaking
as a 0…0.24 brightness accent on an active cue, preserving that cue’s hue
and mask. At idle, tint the speaking region toward the speaking colour
at peak and ease back between syllables. Use 42 ms attack and 125 ms decay.
Priority is danger, combat, forced low-energy return, lock, objective,
discovery, ordinary low energy, interest, idle. Reduced motion holds the
cue mask and brightness at its hold level, using floor values for idle and
speech. Ease pitch/bank with half the amplitude in that mode.

## Flight keys

Each row starts at clip time zero. Angles are hinge deltas from construction
rest, in degrees. Each slash-separated entry is one key in time order.
Mintstitch columns list upper/lower angles for both sides. Tangerine values
move both flippers. Motion keys have zero bank. Ease between keys with
smoothstep, loop hover/glide, play dart/startle once, then blend back
over 0.12 s. Cue poses take precedence over the clip’s pitch and hinge
channels where specified, with hover bob continuing unless the cue sets Y.

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

## Moth’s left wrist journal

The journal opens on the dorsal face of the left bracer as the armour
flap folds sideways. Closed, it reads as the continuous ivory and lavender
shell, with a fine perimeter seam. Keep the existing two outboard vents,
charcoal cuff and elbow piston. The wrist assembly follows the left forearm
animation via the bracer. This pack depicts the left by default. When the
player chooses the right side, mirror the module, flap and opening pose
with the arm. Keep the screen artwork and text readable in the same order.
[Moth suit details](../moth-concept-pack/02-construction-details.png)
· [Authored bracer](../../../../worlds/debug/avatars/moth.puck)
· [Journal decision](../../../decisions/play.md#in-world-consoles)

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
| Ready | 340 ms | 106° | Menagerie live. Input becomes available here. |

The journal is the menu. Its Menagerie page shows exactly two stacked cards,
Mintstitch first and Tangerine second. Each uses the character’s front-view
portrait, a name and its button label. The active card shows a check, a
clear `Active` label and a cyan keyline. The stored companion shows
`Equip`. Illustrate Mintstitch as active and update both cards on equip.
Persist the selected companion on the player and present one companion
through the standard role. Menu focus and companion equip are separate
actions. Use the wearer’s local journal surface so private journal contents
stay with the player.

The screen’s artwork uses an 850 × 1280 reference canvas (portrait).
Coordinates start at top left. Fill the canvas with `#111D23`. Use
`#F4EDDF` for names and page title, `#A9BBB7` for secondary text,
`#8CF2E2` for active and `#46696A` for the stored border. Use Segoe UI or
the same authored humanist face as the journal. Place `Journal` at (70, 52)
in 34 px and `Menagerie` at (70, 106) in 68 px. Card origins are (68, 278)
and (68, 629), with size 714 × 303 and corner r24. Each card has a
205 × 205 portrait at (+34,+49), a 45 px name at (+255,+83), and a
34 px action or status at (+255,+157). Active outline is 4 px, stored
outline 2 px. Bottom `Back` sits at (70, 1086) in 38 px. Keep the remaining
surface calm. The lamp accents belong to the screen and the companions.
