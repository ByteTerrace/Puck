;; The gallery guide: when the Moth (body 0) walks into the region in front of an exhibit, it writes the exhibit's name
;; and a one-line note into the `guide` state row, which the guide's HUD panel shows (guide.puck), and the exhibit's
;; position in the table below into `guideAt`; walking out clears both and the panel hides.
;;
;; guide.puck requests one `Observe/region:guide-<exhibit>` subject per exhibit, in the gallery's walking order (every
;; row of shapes.puck's `galleryRows`, each row's exhibits from x = 23). A region event's B lane is the region's position
;; among those requests, so it indexes the exhibit table below, which lists the exhibits in that same order: an
;; exhibit added to, moved in or removed from the gallery moves its line here too.
;;
;; Memory: channel descriptors at 0x0100, the output ring (4 cells) at 0x1000, the input ring (32 cells) at 0x2000, the
;; two mutation payloads at 0x5000 and 0x5400, NUL-terminated literals from 0x6000, 128 bytes apart, and the exhibit
;; table at 0x8000: for each exhibit its title and its note, each NUL-terminated.
(module
  (memory (export "memory") 1)

  ;; Request (BodyPose and SubmitMutation), Response.
  (data (i32.const 0x0100)
    "\02\00\02\00\00\00\00\00\00\00\00\00\00\00\00\00"
    "\03\00\00\00\00\00\00\00\00\00\00\00\00\00\00\00")

  (data (i32.const 0x6000) "State\00")
  (data (i32.const 0x6080) "{\"name\":\"guide\",\"kind\":\"text\",\"cells\":[{\"key\":\"title\",\"value\":\"\00")
  (data (i32.const 0x6100) "\"},{\"key\":\"note\",\"value\":\"\00")
  (data (i32.const 0x6180) "\"}]}\00")
  (data (i32.const 0x6200) "{\"name\":\"guideAt\",\"kind\":\"int\",\"value\":\00")
  (data (i32.const 0x6280) "}\00")

  (data (i32.const 0x8000)
    ;; Primitives, level 0.
    "SPHERE\00The sphere primitive at a uniform scale.\00"
    "BOX\00The box primitive at a uniform scale.\00"
    "TORUS\00The torus primitive at a uniform scale.\00"
    "CYLINDER\00The cylinder primitive at a uniform scale.\00"
    "CAPSULE\00The capsule primitive at a uniform scale.\00"
    "ELLIPSOID\00The ellipsoid primitive at a uniform scale.\00"
    "ROUND CONE\00The round cone primitive at a uniform scale.\00"
    "CONE\00The cone primitive at a uniform scale.\00"
    "SUPERELLIPSOID\00A superellipsoid at exponent 4.\00"
    "PLANE CUT\00A sphere intersected with a tilted plane.\00"
    ;; Stretched.
    "SPHERE XYZ\00A sphere scaled unevenly on all three axes.\00"
    "BOX XYZ\00A box scaled unevenly on all three axes.\00"
    "TORUS XZ\00A torus with X and Z scaled differently.\00"
    "TORUS Y\00A torus with Y alone stretched.\00"
    "CYLINDER XZ\00A cylinder with X and Z scaled differently.\00"
    "CAPSULE XZ\00A capsule with X and Z scaled differently.\00"
    "ELLIPSOID XYZ\00An ellipsoid scaled unevenly.\00"
    "ROUND CONE XYZ\00A round cone scaled unevenly.\00"
    "CONE XZ\00A cone with X and Z scaled differently.\00"
    "SUPER XYZ\00A superellipsoid at exponent 3, scaled unevenly.\00"
    ;; Extruded.
    "TRAPEZOID\00A trapezoid profile, extruded.\00"
    "ROUNDED RECT\00A rounded rectangle profile, extruded.\00"
    "POLYGON 6\00A hexagon profile, extruded.\00"
    "POLYGON 12\00A twelve-sided polygon profile, extruded.\00"
    "POLYGON 12 XY\00The twelve-gon extruded with X and Y unequal.\00"
    "ELLIPSE\00An ellipse profile, extruded.\00"
    "CHAMFER RECT\00A chamfered rectangle profile, extruded.\00"
    "CONVEX\00A convex pentagon profile, extruded.\00"
    "PATH FILL\00A filled path of lines and curves, extruded.\00"
    "PATH STROKE\00An open stroked path, extruded.\00"
    "PATH STROKE XY\00The stroked path extruded with X and Y unequal.\00"
    ;; Revolved.
    "REV TRAPEZOID\00A trapezoid profile revolved into a ring.\00"
    "REV ROUND RECT\00A rounded rectangle revolved into a ring.\00"
    "REV POLYGON 6\00A hexagon revolved into a ring.\00"
    "REV ELLIPSE\00An ellipse revolved into a ring.\00"
    "REV CHAMFER\00A chamfered rectangle revolved into a ring.\00"
    "REV CONVEX\00A convex pentagon revolved into a ring.\00"
    ;; Sweeps and facets, level 1.
    "SWEEP\00A round tube of one radius along a quadratic curve.\00"
    "SWEEP TAPER\00A swept tube tapering, with a mid-span bulge.\00"
    "SWEEP STRANDS\00Three strands twisted round a curve: a rope.\00"
    "ROUNDING\00A cylinder with its rim rounded 0.12 m.\00"
    "CHAMFER\00A box bevelled 0.1 m on every edge.\00"
    "ONION\00A 4 cm shell of a sphere, a window cut in it.\00"
    "DILATE\00A box inflated 0.1 m: rounded, no creases.\00"
    "FLARE\00A cylinder whose section scales along its height.\00"
    "SHEAR\00A box sheared into a curving lean.\00"
    "BUMPS\00Two gaussian pushes: a swelling and a dent.\00"
    "CELLS F1\00Worley F1 relief: raised cells with creased walls.\00"
    "CELLS F2 F1\00F2 minus F1 relief: ridges along the cell walls.\00"
    "POLAR FOLD\00One box folded into six sectors about Y.\00"
    ;; Blends.
    "UNION\00Box and sphere unioned: a hard crease at the seam.\00"
    "SUBTRACTION\00The sphere carved out of the box.\00"
    "INTERSECTION\00Only where the box and the sphere overlap.\00"
    "XOR\00Either but not both: the overlap cut away.\00"
    "SMOOTH UNION\00A 0.15 m fillet where the sphere meets the box.\00"
    "SMOOTH SUBTRACT\00The carve's rim rounded over 0.1 m.\00"
    "SMOOTH INTERSECT\00The intersection's edges rounded over 0.1 m.\00"
    "CHAMFER UNION\00A flat 45 degree bevel along the seam.\00"
    "CHAMFER SUBTRACT\00The carve's rim bevelled at 45 degrees.\00"
    "CHAMFER INTERSECT\00The intersection's edges bevelled at 45 degrees.\00"
    "GROOVE UNION\00A round groove cut along the seam.\00"
    "PIPE UNION\00A round bead laid along the seam.\00"
    "GROOVE SUBTRACT\00A groove cut along the carve's rim.\00"
    "PIPE SUBTRACT\00A bead laid along the carve's rim.\00"
    ;; Folds.
    "SYMMETRY\00A tilted capsule mirrored across x = 0: a V.\00"
    "REPEAT 5\00One sphere repeated 0.25 m apart: five copies.\00"
    "REPEAT 3X3\00One box on a 3 by 3 lattice in x and z.\00"
    "REPEAT EVEN\00A symmetry over a repeat: four spheres.\00"
    "REPEAT CLIPPED\00An unbounded lattice of spheres clipped by a box.\00"
    "POLAR MIRROR\00Five mirrored sectors: a kaleidoscope.\00"
    "POLAR Z\00Eight copies about Z through an origin: a wheel.\00"
    "POLAR STRIDE\00Six sectors, each a palette row: six colours.\00"
    "WALLPAPER PMM\00A tile mirrored across both cell axes.\00"
    "WALLPAPER P4M\00Square symmetry: eight images per cell.\00"
    "WALLPAPER P3M1\00A hex lattice of mirrors, clipped by a hexagon.\00"
    "WALLPAPER P6M\00The richest hex group: twelve images per cell.\00"
    "WALLPAPER STRIDE\00P4M with a material stride: a checkerboard.\00"
    "ERODE\00A lane erodes the sphere, here halfway through.\00"
    "NOISE\00Noise lumps the sphere; the plinth stays clean.\00"
    ;; Forms, level 2.
    "SUPER EXP 2\00A superellipsoid at exponent 2.\00"
    "SUPER EXP 8\00A superellipsoid at exponent 8, nearly a box.\00"
    "TAPER 0\00A prism at taper 0.\00"
    "TAPER 1\00A prism at taper 1.\00"
    "POLYGON 3\00A triangle profile, extruded.\00"
    "PRISM ROUNDING\00An extruded prism with rounded edges.\00"
    "PRISM CHAMFER\00An extruded prism with chamfered edges.\00"
    "CYLINDER CHAMFER\00A cylinder with chamfered rims.\00"
    "REV SOLID\00A profile revolved at offset 0: a solid.\00"
    "PATH HOLES\00An even-odd fill: a cubic edge, a round hole.\00"
    "PATH SHEAR\00An extruded path, sheared.\00"
    "PATH CLOSED\00A closed cubic stroke.\00"
    "PATH RAMP\00A stroke whose radius ramps along an arc.\00"
    "SWEEP BRAID\00Four strands braided along a sweep.\00"
    ;; Surfaces.
    "METAL\00The palette's metal channel.\00"
    "COAT\00A clear coat over the base colour.\00"
    "SHEEN\00The sheen channel: a soft rim like cloth.\00"
    "EMISSIVE\00An emissive surface, lit from within.\00"
    "WRAP SOFTEN\00Wrap and soften on cells relief.\00"
    "FILL BLEED\00Fill, bleed and receive between palette slots.\00"
    "WEAR EDGE\00Edge weathering in two stages.\00"
    "WEAR LINES\00Line weathering across the surface.\00"
    "WEAR SETTLE\00Settle weathering: a deposit on the upper faces.\00"
    "PANEL RECESS\00A recessed panel set into the face.\00"
    "PANEL RAISE\00A raised panel standing off the face.\00"
    "TRIMS\00A trim band round the box's equator.\00"
    "PAINT WORLD\00A world-space paint on a static placement.\00"
    "TEXT ENGRAVE\00Text engraved into the surface.\00"
    "TEXT ON SHAPE\00Text riding a shape, wrapped to a width.\00"
    "VOLUME FLOW\00A flow volume pulsing from a nozzle.\00"
    "VOLUME CLOUD\00A cloud volume.\00"
    "STATE COLOR\00A colour bound to state: galleryInputs.tint.\00"
    ;; Warps II.
    "DOMAIN WARP\00A sphere warped by sines: a smooth wobble.\00"
    "SINE RELIEF\00A sine product added to a box's field.\00"
    "ELONGATE\00A sphere elongated into an exact rounded slab.\00"
    "JITTER\00Each cell moves and recolours its sphere by hash.\00"
    "JITTER TUMBLE\00Boxes tumbled on a blue-noise lattice.\00"
    "LOG SPHERE\00Self-similar shells spiralling inward.\00"
    "STAIRS UNION\00A seam that climbs four steps.\00"
    "STAIRS SUBTRACT\00A carve whose rim steps three times.\00"
    "MORPH\00The box's and sphere's fields averaged: a soft cube.\00"
    ;; Motion, level 3.
    "SPIN\00A rigid rotation on a linear wave.\00"
    "ROCK\00A sine swing about a pivot.\00"
    "SLIDE\00A translation on the gallery clock.\00"
    "PARENT CHAIN\00A chain of parented swings.\00"
    "MOVING BLEND\00A sphere sliding through a smooth union.\00"
    "STATE BLEND\00The blend placed by state; each write rebakes.\00"
    "MOVING CUT\00A cutter sliding across a still box.\00"
    "MOVING MIRROR\00A mirrored shape riding a yawing hinge.\00"
    "MOVING POLAR\00A polar-folded blade spinning on its hub.\00"
    ;; Motion II.
    "MOVING PAINT\00Paints riding a swinging panel.\00"
    "MOVING INSET\00An eye inset that turns with its shape.\00"
    "TWIST\00A twist, drawn on the stamp-pool path.\00"
    "BEND\00A bend, drawn on the stamp-pool path.\00"
    "RADIAL PULSE\00Six petals sliding out from a core and back.\00"
    "MOVING CELLS\00Cells relief on a swinging shape.\00"
    "MOVING VOLUME\00A plume riding a tilting nozzle.\00"
    "IK REACH\00A two-bone arm reaching for a state point.\00"
    "FRAMES\00Timeline frames stepped on the render clock.\00"
    ;; Glass.
    "GLASS\00Tinted, thin, thick and frosted glass.\00")

  (global $out (mut i32) (i32.const 0))
  (global $mutate (mut i32) (i32.const 0))
  (global $mutateIndex (mut i32) (i32.const 0))
  (global $mutateGeneration (mut i32) (i32.const 0))
  (global $askedMutate (mut i32) (i32.const -1))
  ;; The exhibit the Moth stands at, -1 for none, and whether the row still has to be written.
  (global $shown (mut i64) (i64.const -1))
  (global $dirty (mut i32) (i32.const 0))
  (global $cursor (mut i32) (i32.const 0))

  (func (export "puck_abi_version") (result i32) (i32.const 1))
  (func (export "puck_out_ptr") (result i32) (i32.const 0x1000))
  (func (export "puck_out_cap") (result i32) (i32.const 4))
  (func (export "puck_in_ptr") (result i32) (i32.const 0x2000))
  (func (export "puck_in_cap") (result i32) (i32.const 32))
  (func (export "puck_channels_ptr") (result i32) (i32.const 0x0100))
  (func (export "puck_channels_count") (result i32) (i32.const 2))

  (func $emit (param $kind i32) (param $index i32) (param $generation i32) (param $verb i32)
      (param $a i64) (param $b i64) (param $c i64) (result i32)
    (local $cell i32)
    (local.set $cell (i32.add (i32.const 0x1000) (i32.shl (global.get $out) (i32.const 5))))
    (i32.store8 (local.get $cell) (local.get $kind))
    (i32.store8 offset=1 (local.get $cell) (i32.const 0))
    (i32.store16 offset=2 (local.get $cell) (local.get $index))
    (i32.store16 offset=4 (local.get $cell) (local.get $generation))
    (i32.store16 offset=6 (local.get $cell) (local.get $verb))
    (i64.store offset=8 (local.get $cell) (local.get $a))
    (i64.store offset=16 (local.get $cell) (local.get $b))
    (i64.store offset=24 (local.get $cell) (local.get $c))
    (global.set $out (i32.add (global.get $out) (i32.const 1)))
    (i32.sub (global.get $out) (i32.const 1)))

  ;; Copies a NUL-terminated string to the cursor and returns the address after its NUL.
  (func $puts (param $at i32) (result i32)
    (block $end
      (loop $next
        (br_if $end (i32.eqz (i32.load8_u (local.get $at))))
        (i32.store8 (global.get $cursor) (i32.load8_u (local.get $at)))
        (global.set $cursor (i32.add (global.get $cursor) (i32.const 1)))
        (local.set $at (i32.add (local.get $at) (i32.const 1)))
        (br $next)))
    (i32.add (local.get $at) (i32.const 1)))

  ;; The address of exhibit k's title: past 2k NUL-terminated strings of the table.
  (func $exhibit (param $k i64) (result i32)
    (local $at i32) (local $skip i64)
    (local.set $at (i32.const 0x8000))
    (local.set $skip (i64.shl (local.get $k) (i64.const 1)))
    (block $found
      (loop $next
        (br_if $found (i64.eqz (local.get $skip)))
        (if (i32.eqz (i32.load8_u (local.get $at)))
          (then (local.set $skip (i64.sub (local.get $skip) (i64.const 1)))))
        (local.set $at (i32.add (local.get $at) (i32.const 1)))
        (br $next)))
    (local.get $at))

  ;; Submits a payload as one UpsertStateRow (mutation kind 44) through the Mutate handle.
  (func $upsert (param $at i32)
    (drop (call $emit (i32.const 1) (global.get $mutateIndex) (global.get $mutateGeneration) (i32.const 1)
      (i64.const 44) (i64.extend_i32_u (local.get $at)) (i64.extend_i32_u (i32.sub (global.get $cursor) (local.get $at))))))
  ;; Writes both whole rows: `guide`, the shown exhibit's title and note or two empty cells, and `guideAt`, its
  ;; position in the table or -1.
  (func $publish
    (local $at i32) (local $v i64)
    (global.set $cursor (i32.const 0x5000))
    (drop (call $puts (i32.const 0x6080)))
    (if (i64.ge_s (global.get $shown) (i64.const 0))
      (then
        (local.set $at (call $puts (call $exhibit (global.get $shown))))
        (drop (call $puts (i32.const 0x6100)))
        (drop (call $puts (local.get $at))))
      (else (drop (call $puts (i32.const 0x6100)))))
    (drop (call $puts (i32.const 0x6180)))
    (call $upsert (i32.const 0x5000))
    (global.set $cursor (i32.const 0x5400))
    (drop (call $puts (i32.const 0x6200)))
    (if (i64.lt_s (global.get $shown) (i64.const 0))
      (then
        (i32.store8 (global.get $cursor) (i32.const 45))
        (i32.store8 offset=1 (global.get $cursor) (i32.const 49))
        (global.set $cursor (i32.add (global.get $cursor) (i32.const 2))))
      (else
        ;; At most three digits: the table holds fewer than a thousand exhibits.
        (local.set $v (global.get $shown))
        (if (i64.ge_u (local.get $v) (i64.const 100))
          (then
            (i32.store8 (global.get $cursor) (i32.add (i32.const 48) (i32.wrap_i64 (i64.div_u (local.get $v) (i64.const 100)))))
            (global.set $cursor (i32.add (global.get $cursor) (i32.const 1)))))
        (if (i64.ge_u (local.get $v) (i64.const 10))
          (then
            (i32.store8 (global.get $cursor) (i32.add (i32.const 48)
              (i32.wrap_i64 (i64.rem_u (i64.div_u (local.get $v) (i64.const 10)) (i64.const 10)))))
            (global.set $cursor (i32.add (global.get $cursor) (i32.const 1)))))
        (i32.store8 (global.get $cursor) (i32.add (i32.const 48) (i32.wrap_i64 (i64.rem_u (local.get $v) (i64.const 10)))))
        (global.set $cursor (i32.add (global.get $cursor) (i32.const 1)))))
    (drop (call $puts (i32.const 0x6280)))
    (call $upsert (i32.const 0x5400))
    (global.set $dirty (i32.const 0)))

  (func (export "puck_on_tick") (param $count i32) (result i32)
    (local $i i32) (local $cell i32) (local $kind i32) (local $verb i32) (local $verdict i32)
    (global.set $out (i32.const 0))
    (local.set $i (i32.const 0))
    (block $drained
      (loop $read
        (br_if $drained (i32.ge_u (local.get $i) (local.get $count)))
        (local.set $cell (i32.add (i32.const 0x2000) (i32.shl (local.get $i) (i32.const 5))))
        (local.set $kind (i32.load8_u (local.get $cell)))
        (local.set $verb (i32.load8_u offset=9 (local.get $cell)))
        ;; Region enter (verb 1) and exit (verb 2) for the Moth: A is the body, B the exhibit.
        (if (i32.and (i32.eq (local.get $kind) (i32.const 3)) (i64.eqz (i64.load offset=16 (local.get $cell))))
          (then
            (if (i32.eq (local.get $verb) (i32.const 1))
              (then
                (global.set $shown (i64.load offset=24 (local.get $cell)))
                (global.set $dirty (i32.const 1))))
            (if (i32.and (i32.eq (local.get $verb) (i32.const 2))
                  (i64.eq (i64.load offset=24 (local.get $cell)) (global.get $shown)))
              (then
                (global.set $shown (i64.const -1))
                (global.set $dirty (i32.const 1))))))
        (if (i32.and (i32.eq (local.get $kind) (i32.const 2))
              (i32.eq (i32.load16_u offset=2 (local.get $cell)) (global.get $askedMutate)))
          (then
            (global.set $askedMutate (i32.const -1))
            (local.set $verdict (i32.load8_u offset=8 (local.get $cell)))
            (if (i32.and (i32.ge_u (local.get $verdict) (i32.const 1)) (i32.le_u (local.get $verdict) (i32.const 3)))
              (then
                (global.set $mutate (i32.const 1))
                (global.set $mutateIndex (i32.load16_u offset=4 (local.get $cell)))
                (global.set $mutateGeneration (i32.load16_u offset=6 (local.get $cell)))))))
        (local.set $i (i32.add (local.get $i) (i32.const 1)))
        (br $read)))
    (if (i32.and (i32.eqz (global.get $mutate)) (i32.lt_s (global.get $askedMutate) (i32.const 0)))
      (then
        ;; Ask for Mutate over the State section, named by text: the name is 5 bytes at 0x6000.
        (global.set $askedMutate (call $emit (i32.const 2) (i32.const 0) (i32.const 0) (i32.const 3)
          (i64.const 0x6000) (i64.const 8) (i64.const 5)))))
    (if (i32.and (global.get $dirty) (global.get $mutate)) (then (call $publish)))
    (global.get $out))
)
