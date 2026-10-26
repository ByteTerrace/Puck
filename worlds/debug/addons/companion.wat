;; The faerie flies a colliding body through Drive and Observe. Forward/strafe/rise are existing input roles.
;; The 64-crumb route retains stairs, doors, lifts and portal approaches. Seeded idle choices, loops, pauses,
;; face/exhibit/light/screen curiosity, neighbour clearance and predictive swat evasions are integer guest decisions.
;; companion-senses.puck projects the world's observations. There is no guest pose or document mutation.
;; Memory: descriptors 0x100, rings 0x1000/0x2000, trail 0x3000, senses 0x5000, request map 0x5400,
;; row name 0x6000, length-prefixed keys 0x6100. Q48.16 at the ABI; persistent Puck.Maths PCG stream 1.
(module
  (memory (export "memory") 1)

  ;; Input (three names at 0x0200), Request (pose, numeric sensory reads and bounded logs), Response.
  (data (i32.const 0x0100)
    "\01\00\03\00\00\02\00\00\00\00\00\00\00\00\00\00"
    "\02\00\0e\00\00\00\00\00\00\00\00\00\00\00\00\00"
    "\03\00\00\00\00\00\00\00\00\00\00\00\00\00\00\00")
  ;; The declared channel names, each a length byte and its UTF-8: ordinal 0 forward, 1 strafe, 2 rise.
  (data (i32.const 0x0200) "\07forward\06strafe\04rise")

  (global $out (mut i32) (i32.const 0))
  (global $tick (mut i64) (i64.const 0))

  (global $drive (mut i32) (i32.const 0))
  (global $driveIndex (mut i32) (i32.const 0))
  (global $driveGeneration (mut i32) (i32.const 0))
  (global $selfBody (mut i64) (i64.const -1))
  (global $observeSelf (mut i32) (i32.const 0))
  (global $observeSelfIndex (mut i32) (i32.const 0))
  (global $observeSelfGeneration (mut i32) (i32.const 0))
  (global $observeMoth (mut i32) (i32.const 0))
  (global $observeMothIndex (mut i32) (i32.const 0))
  (global $observeMothGeneration (mut i32) (i32.const 0))

  ;; Last tick's pose query ordinals, -1 when none was asked.
  (global $askedMoth (mut i32) (i32.const -1))
  (global $askedSelf (mut i32) (i32.const -1))
  ;; This batch's answered positions, and which of parts 0 (x, y) and 1 (z) arrived.
  (global $mothX (mut i64) (i64.const 0))
  (global $mothY (mut i64) (i64.const 0))
  (global $mothZ (mut i64) (i64.const 0))
  (global $mothParts (mut i32) (i32.const 0))
  (global $selfX (mut i64) (i64.const 0))
  (global $selfY (mut i64) (i64.const 0))
  (global $selfZ (mut i64) (i64.const 0))
  (global $selfParts (mut i32) (i32.const 0))

  ;; The trail: crumbs written so far, and the next crumb to fly to.
  (global $trailHead (mut i32) (i32.const 0))
  (global $trailNext (mut i32) (i32.const 0))
  (func (export "puck_abi_version") (result i32) (i32.const 1))
  (func (export "puck_out_ptr") (result i32) (i32.const 0x1000))
  (func (export "puck_out_cap") (result i32) (i32.const 32))
  (func (export "puck_in_ptr") (result i32) (i32.const 0x2000))
  (func (export "puck_in_cap") (result i32) (i32.const 64))
  (func (export "puck_channels_ptr") (result i32) (i32.const 0x0100))
  (func (export "puck_channels_count") (result i32) (i32.const 3))

  ;; Writes one output cell and returns its ordinal in this batch.
  (func $emit (param $kind i32) (param $channel i32) (param $index i32) (param $generation i32) (param $verb i32)
      (param $a i64) (param $b i64) (param $c i64) (result i32)
    (local $cell i32)
    (local.set $cell (i32.add (i32.const 0x1000) (i32.shl (global.get $out) (i32.const 5))))
    (i32.store8 (local.get $cell) (local.get $kind))
    (i32.store8 offset=1 (local.get $cell) (local.get $channel))
    (i32.store16 offset=2 (local.get $cell) (local.get $index))
    (i32.store16 offset=4 (local.get $cell) (local.get $generation))
    (i32.store16 offset=6 (local.get $cell) (local.get $verb))
    (i64.store offset=8 (local.get $cell) (local.get $a))
    (i64.store offset=16 (local.get $cell) (local.get $b))
    (i64.store offset=24 (local.get $cell) (local.get $c))
    (global.set $out (i32.add (global.get $out) (i32.const 1)))
    (i32.sub (global.get $out) (i32.const 1)))

  ;; A Bipolar act on a declared channel through the Drive handle; the value is clamped to [-1, 1].
  (func $act (param $ordinal i32) (param $value i64)
    (if (i64.gt_s (local.get $value) (i64.const 0x10000)) (then (local.set $value (i64.const 0x10000))))
    (if (i64.lt_s (local.get $value) (i64.const -0x10000)) (then (local.set $value (i64.const -0x10000))))
    (drop (call $emit (i32.const 1) (i32.const 0) (global.get $driveIndex) (global.get $driveGeneration)
      (i32.shl (local.get $ordinal) (i32.const 2)) (local.get $value) (i64.const 0) (i64.const 0))))

  ;; A BodyPose query through an Observe handle; returns its ordinal.
  (func $askPose (param $index i32) (param $generation i32) (result i32)
    (call $emit (i32.const 1) (i32.const 1) (local.get $index) (local.get $generation) (i32.const 0)
      (i64.const 0) (i64.const 0) (i64.const 0)))

  ;; The integer square root of a non-negative value (Newton's method from above).
  (func $isqrt (param $v i64) (result i64)
    (local $x i64) (local $y i64)
    (if (i64.le_s (local.get $v) (i64.const 0)) (then (return (i64.const 0))))
    (local.set $x (local.get $v))
    (local.set $y (i64.shr_u (i64.add (local.get $x) (i64.const 1)) (i64.const 1)))
    (block $done
      (loop $next
        (br_if $done (i64.ge_u (local.get $y) (local.get $x)))
        (local.set $x (local.get $y))
        (local.set $y (i64.shr_u (i64.add (local.get $x) (i64.div_u (local.get $v) (local.get $x))) (i64.const 1)))
        (br $next)))
    (local.get $x))

  ;; The planar (x, z) distance between two points, in metres as Q16.
  (func $planar (param $ax i64) (param $az i64) (param $bx i64) (param $bz i64) (result i64)
    (local $dx i64) (local $dz i64)
    (local.set $dx (i64.sub (local.get $bx) (local.get $ax)))
    (local.set $dz (i64.sub (local.get $bz) (local.get $az)))
    (call $isqrt (i64.add (i64.mul (local.get $dx) (local.get $dx)) (i64.mul (local.get $dz) (local.get $dz)))))

  (func $crumb (param $k i32) (result i32)
    (i32.add (i32.const 0x3000) (i32.mul (i32.and (local.get $k) (i32.const 63)) (i32.const 24))))

  ;; Drops a crumb where the Moth stands when she has moved half a metre from the last one, and restarts the trail
  ;; when she is more than 6 m from it.
  (func $trail
    (local $last i32) (local $dx i64) (local $dy i64) (local $dz i64) (local $moved i64) (local $at i32)
    (if (i32.gt_u (global.get $trailHead) (i32.const 0))
      (then
        (local.set $last (call $crumb (i32.sub (global.get $trailHead) (i32.const 1))))
        (local.set $dx (i64.sub (global.get $mothX) (i64.load (local.get $last))))
        (local.set $dy (i64.sub (global.get $mothY) (i64.load offset=8 (local.get $last))))
        (local.set $dz (i64.sub (global.get $mothZ) (i64.load offset=16 (local.get $last))))
        (local.set $moved (call $isqrt (i64.add (i64.add
          (i64.mul (local.get $dx) (local.get $dx))
          (i64.mul (local.get $dy) (local.get $dy)))
          (i64.mul (local.get $dz) (local.get $dz)))))
        (if (i64.lt_s (local.get $moved) (i64.const 0x8000)) (then (return)))
        (if (i64.gt_s (local.get $moved) (i64.const 0x60000))
          (then (global.set $trailNext (global.get $trailHead))))))
    (local.set $at (call $crumb (global.get $trailHead)))
    (i64.store (local.get $at) (global.get $mothX))
    (i64.store offset=8 (local.get $at) (global.get $mothY))
    (i64.store offset=16 (local.get $at) (global.get $mothZ))
    (global.set $trailHead (i32.add (global.get $trailHead) (i32.const 1)))
    ;; The ring holds 64 crumbs; a trail longer than that loses its oldest.
    (if (i32.gt_u (i32.sub (global.get $trailHead) (global.get $trailNext)) (i32.const 64))
      (then (global.set $trailNext (i32.sub (global.get $trailHead) (i32.const 64))))))

  ;; Flight decisions use Q48.16 integer arithmetic. PCG is the exact Puck.Maths.Pcg32XshRr transition (stream 1).
  ;; The seed is fixed and all deadlines are simulation ticks. Guest memory is reconstructed under pinned replay.
  (global $rng (mut i64) (i64.const 0))
  (global $seeded (mut i32) (i32.const 0))
  (global $mode (mut i32) (i32.const 0))
  (global $modeEnd (mut i64) (i64.const 0))
  (global $phase (mut i64) (i64.const 0))
  (global $period (mut i64) (i64.const 97))
  (global $wanderX (mut i64) (i64.const 0))
  (global $wanderY (mut i64) (i64.const 0))
  (global $wanderZ (mut i64) (i64.const 0))
  (global $restX (mut i64) (i64.const -78643))
  (global $restZ (mut i64) (i64.const 65536))
  (global $lastMX (mut i64) (i64.const 0))
  (global $lastMY (mut i64) (i64.const 0))
  (global $lastMZ (mut i64) (i64.const 0))
  (global $lastSX (mut i64) (i64.const 0))
  (global $lastSZ (mut i64) (i64.const 0))
  (global $lastDX (mut i64) (i64.const 0))
  (global $lastDZ (mut i64) (i64.const 0))
  (global $qy (mut i64) (i64.const 0))
  (global $qw (mut i64) (i64.const 65536))
  (global $dodgeEnd (mut i64) (i64.const 0))
  (global $dodgeX (mut i64) (i64.const 0))
  (global $dodgeY (mut i64) (i64.const 0))
  (global $dodgeZ (mut i64) (i64.const 0))
  (global $startleEnd (mut i64) (i64.const 0))
  (global $portalEnd (mut i64) (i64.const 0))
  (global $verticalEnd (mut i64) (i64.const 0))
  (global $portalX (mut i64) (i64.const 0))
  (global $portalY (mut i64) (i64.const 0))
  (global $portalZ (mut i64) (i64.const 0))
  (global $stalled (mut i32) (i32.const 0))
  (global $sense (mut i32) (i32.const 0))
  (global $lastNear (mut i32) (i32.const 0))
  (global $nearX (mut i64) (i64.const 0))
  (global $nearY (mut i64) (i64.const 0))
  (global $nearZ (mut i64) (i64.const 0))
  (global $senseIndex (mut i32) (i32.const 0))
  (global $senseGeneration (mut i32) (i32.const 0))
  (global $askedSense (mut i32) (i32.const -1))

  (func $next (result i32)
    (local $old i64)
    (local.set $old (global.get $rng))
    (global.set $rng (i64.add (i64.mul (local.get $old) (i64.const 6364136223846793005)) (i64.const 3)))
    (i32.rotr (i32.wrap_i64 (i64.shr_u (i64.xor (i64.shr_u (local.get $old) (i64.const 18)) (local.get $old)) (i64.const 27)))
      (i32.wrap_i64 (i64.shr_u (local.get $old) (i64.const 59)))))
  (func $random (param $span i32) (result i64)
    (i64.extend_i32_u (i32.rem_u (call $next) (local.get $span))))
  (func $abs (param $v i64) (result i64)
    (if (result i64) (i64.lt_s (local.get $v) (i64.const 0))
      (then (i64.sub (i64.const 0) (local.get $v))) (else (local.get $v))))
  (func $mul (param $a i64) (param $b i64) (result i64)
    (i64.shr_s (i64.mul (local.get $a) (local.get $b)) (i64.const 16)))
  (func $norm (param $x i64) (param $y i64) (param $z i64) (result i64)
    (call $isqrt (i64.add (i64.add (i64.mul (local.get $x) (local.get $x))
      (i64.mul (local.get $y) (local.get $y))) (i64.mul (local.get $z) (local.get $z)))))
  (func $s (param $slot i32) (result i64)
    (i64.load (i32.add (i32.const 0x5000) (i32.shl (local.get $slot) (i32.const 3)))))
  ;; Smooth rational sine approximation over a full turn, no host math or floating-point instructions.
  (func $wave (param $t i64) (param $period i64) (result i64)
    (local $x i64) (local $p i64) (local $value i64)
    (local.set $x (i64.div_u (i64.mul (i64.rem_u (local.get $t) (local.get $period)) (i64.const 131072)) (local.get $period)))
    (local.set $p (i64.and (local.get $x) (i64.const 65535)))
    (local.set $p (call $mul (local.get $p) (i64.sub (i64.const 65536) (local.get $p))))
    (local.set $value (i64.div_s (i64.mul (i64.const 16) (i64.shl (local.get $p) (i64.const 16)))
      (i64.sub (i64.const 327680) (i64.mul (i64.const 4) (local.get $p)))))
    (if (result i64) (i64.ge_u (local.get $x) (i64.const 65536))
      (then (i64.sub (i64.const 0) (local.get $value))) (else (local.get $value))))

  ;; Read-only sensory row bootstrap. Responses are ordinal-mapped: no assumption about when an answer arrives.
  (func $consumeSense (param $cell i32)
    (local $o i32) (local $slot i32) (local $v i64)
    (if (i32.ne (i32.load8_u (local.get $cell)) (i32.const 2)) (then (return)))
    (local.set $o (i32.load16_u offset=2 (local.get $cell)))
    (if (i32.eq (local.get $o) (global.get $askedSense)) (then
      (global.set $askedSense (i32.const -1))
      (if (i32.and (i32.ge_u (i32.load8_u offset=8 (local.get $cell)) (i32.const 1))
            (i32.le_u (i32.load8_u offset=8 (local.get $cell)) (i32.const 3))) (then
        (global.set $sense (i32.const 1))
        (global.set $senseIndex (i32.load16_u offset=4 (local.get $cell)))
        (global.set $senseGeneration (i32.load16_u offset=6 (local.get $cell)))))))
    (if (i32.ge_u (local.get $o) (i32.const 32)) (then (return)))
    (local.set $slot (i32.load (i32.add (i32.const 0x5400) (i32.shl (local.get $o) (i32.const 2)))))
    (if (i32.ge_u (local.get $slot) (i32.const 21)) (then (return)))
    ;; Numeric reads repeat their allowing grant verdict (1..3); text reads use Applied (10).
    (if (i32.or (i32.lt_u (i32.load8_u offset=8 (local.get $cell)) (i32.const 1))
          (i32.gt_u (i32.load8_u offset=8 (local.get $cell)) (i32.const 3))) (then
      (global.set $sense (i32.const 0)) (return)))
    (local.set $v (i64.load offset=16 (local.get $cell)))
    ;; Int/Bool reads carry whole units; Fixed reads carry Q48.16 already.
    (if (i64.ne (i64.load offset=24 (local.get $cell)) (i64.const 1))
      (then (local.set $v (i64.shl (local.get $v) (i64.const 16)))))
    (i64.store (i32.add (i32.const 0x5000) (i32.shl (local.get $slot) (i32.const 3))) (local.get $v)))
  (func $readSenses
    (local $i i32) (local $o i32) (local $p i32)
    (memory.fill (i32.const 0x5400) (i32.const 255) (i32.const 128))
    (if (global.get $sense) (then
      (loop $read
        (local.set $p (i32.add (i32.const 0x6100) (i32.mul (local.get $i) (i32.const 16))))
        (local.set $o (call $emit (i32.const 1) (i32.const 1) (global.get $senseIndex) (global.get $senseGeneration)
          (i32.const 4) (i64.extend_i32_u (i32.add (local.get $p) (i32.const 1)))
          (i64.extend_i32_u (i32.load8_u (local.get $p))) (i64.const 0)))
        (i32.store (i32.add (i32.const 0x5400) (i32.shl (local.get $o) (i32.const 2))) (local.get $i))
        (local.set $i (i32.add (local.get $i) (i32.const 1)))
        (br_if $read (i32.lt_u (local.get $i) (i32.const 21)))))
      (else (if (i32.lt_s (global.get $askedSense) (i32.const 0)) (then
        (global.set $askedSense (call $emit (i32.const 2) (i32.const 1) (i32.const 0) (i32.const 0)
          (i32.const 5) (i64.const 0x6000) (i64.const 2) (i64.const 14))))))))

  ;; A predicted swept point, shared by authored hand threats and body approaches. Dodge direction includes a
  ;; seeded lateral choice, so repeated swats produce different coherent evasions, with a bounded return deadline.
  (func $threat (param $x i64) (param $y i64) (param $z i64) (param $vx i64) (param $vy i64) (param $vz i64)
    (local $dx i64) (local $dy i64) (local $dz i64) (local $dot i64) (local $vv i64) (local $time i64)
    (local $length i64) (local $side i64)
    (if (i64.lt_s (global.get $tick) (global.get $dodgeEnd)) (then (return)))
    (local.set $dx (i64.sub (global.get $selfX) (local.get $x)))
    (local.set $dy (i64.sub (i64.add (global.get $selfY) (i64.const 8192)) (local.get $y)))
    (local.set $dz (i64.sub (global.get $selfZ) (local.get $z)))
    (local.set $vv (i64.add (i64.add (call $mul (local.get $vx) (local.get $vx))
      (call $mul (local.get $vy) (local.get $vy))) (call $mul (local.get $vz) (local.get $vz))))
    (if (i64.lt_s (local.get $vv) (i64.const 16384)) (then (return)))
    (local.set $dot (i64.add (i64.add (call $mul (local.get $dx) (local.get $vx))
      (call $mul (local.get $dy) (local.get $vy))) (call $mul (local.get $dz) (local.get $vz))))
    ;; Only an approaching swing is threatening. Closest approach is limited to the next 0.35 seconds.
    (if (i64.lt_s (local.get $dot) (i64.const 0)) (then (return)))
    (local.set $time (i64.div_s (i64.shl (local.get $dot) (i64.const 16)) (local.get $vv)))
    (if (i64.gt_s (local.get $time) (i64.const 22938)) (then (local.set $time (i64.const 22938))))
    (local.set $dx (i64.sub (local.get $dx) (call $mul (local.get $vx) (local.get $time))))
    (local.set $dy (i64.sub (local.get $dy) (call $mul (local.get $vy) (local.get $time))))
    (local.set $dz (i64.sub (local.get $dz) (call $mul (local.get $vz) (local.get $time))))
    (local.set $length (call $norm (local.get $dx) (local.get $dy) (local.get $dz)))
    (if (i64.gt_s (local.get $length) (i64.const 58982)) (then (return)))
    ;; A direct hit has no away normal: choose a perpendicular of the incoming swing, never a zero vector.
    (if (i64.lt_s (local.get $length) (i64.const 8192)) (then
      (local.set $dx (i64.sub (i64.const 0) (local.get $vz)))
      (local.set $dy (i64.const 22938)) (local.set $dz (local.get $vx))
      (local.set $length (call $norm (local.get $dx) (local.get $dy) (local.get $dz)))))
    (local.set $side (i64.sub (i64.shl (call $random (i32.const 2)) (i64.const 1)) (i64.const 1)))
    (global.set $dodgeX (i64.add (global.get $selfX) (i64.add
      (i64.div_s (i64.mul (local.get $dx) (i64.const 78643)) (local.get $length))
      (i64.mul (local.get $side) (i64.div_s (i64.mul (local.get $dz) (i64.const 32768)) (local.get $length))))))
    (global.set $dodgeZ (i64.add (global.get $selfZ) (i64.sub
      (i64.div_s (i64.mul (local.get $dz) (i64.const 78643)) (local.get $length))
      (i64.mul (local.get $side) (i64.div_s (i64.mul (local.get $dx) (i64.const 32768)) (local.get $length))))))
    (global.set $dodgeY (i64.add (global.get $selfY) (i64.add (i64.const 16384)
      (i64.div_s (i64.mul (local.get $dy) (i64.const 22938)) (local.get $length)))))
    ;; A perpendicular escape must not cut through the Moth. Reflect an inward choice across our current point;
    ;; this preserves the seeded variation while sending its entire first leg away from her occupied capsule.
    (if (i64.lt_s (i64.add
          (call $mul (i64.sub (global.get $dodgeX) (global.get $selfX)) (i64.sub (global.get $selfX) (global.get $mothX)))
          (call $mul (i64.sub (global.get $dodgeZ) (global.get $selfZ)) (i64.sub (global.get $selfZ) (global.get $mothZ))))
        (i64.const 0)) (then
      (global.set $dodgeX (i64.sub (i64.mul (global.get $selfX) (i64.const 2)) (global.get $dodgeX)))
      (global.set $dodgeZ (i64.sub (i64.mul (global.get $selfZ) (i64.const 2)) (global.get $dodgeZ)))))
    (global.set $dodgeEnd (i64.add (global.get $tick) (i64.add (i64.const 12) (call $random (i32.const 12)))))
    (global.set $startleEnd (i64.add (global.get $dodgeEnd) (i64.const 45)))
    (global.set $modeEnd (global.get $startleEnd))
    (drop (call $emit (i32.const 1) (i32.const 1) (i32.const 0) (i32.const 0) (i32.const 5)
      (i64.const 0x6040) (i64.const 5) (i64.const 0))))

  (func $steer
    (local $dx i64) (local $dy i64) (local $dz i64) (local $gx i64) (local $gy i64) (local $gz i64)
    (local $distance i64) (local $length i64) (local $at i32) (local $fx i64) (local $fz i64)
    (local $mx i64) (local $my i64) (local $mz i64) (local $moving i32) (local $speed i64)
    (local $gain i64) (local $route i32)
    (if (i32.eqz (global.get $seeded)) (then
      (global.set $seeded (i32.const 1))
      (drop (call $next)) (global.set $rng (i64.add (global.get $rng) (i64.const 314159))) (drop (call $next))
      (global.set $lastMX (global.get $mothX)) (global.set $lastMY (global.get $mothY)) (global.set $lastMZ (global.get $mothZ))
      (global.set $restX (global.get $selfX)) (global.set $restZ (global.get $selfZ))
      (global.set $lastSX (global.get $selfX)) (global.set $lastSZ (global.get $selfZ))))
    (local.set $mx (i64.sub (global.get $mothX) (global.get $lastMX)))
    (local.set $my (i64.sub (global.get $mothY) (global.get $lastMY)))
    (local.set $mz (i64.sub (global.get $mothZ) (global.get $lastMZ)))
    (local.set $speed (call $norm (local.get $mx) (local.get $my) (local.get $mz)))
    (local.set $distance (call $planar (global.get $selfX) (global.get $selfZ) (global.get $mothX) (global.get $mothZ)))
    ;; Keep the entry approach when the Moth crosses a portal, instead of cutting across to its exit.
    (if (i64.gt_s (local.get $speed) (i64.const 393216)) (then
      (if (i32.and
          (i64.gt_s (call $planar (i64.const 0) (i64.const 0) (global.get $lastDX) (global.get $lastDZ)) (i64.const 655))
          (i64.lt_s (call $planar (global.get $selfX) (global.get $selfZ) (global.get $lastMX) (global.get $lastMZ))
            (i64.const 327680))) (then
        (global.set $portalX (i64.add (global.get $lastMX) (i64.mul (global.get $lastDX) (i64.const 12))))
        (global.set $portalY (i64.add (global.get $lastMY) (i64.const 98304)))
        (global.set $portalZ (i64.add (global.get $lastMZ) (i64.mul (global.get $lastDZ) (i64.const 12))))
        (global.set $portalEnd (i64.add (global.get $tick) (i64.const 120)))))
      (local.set $mx (i64.const 0)) (local.set $my (i64.const 0)) (local.set $mz (i64.const 0))
      (local.set $speed (i64.const 0))))
    (if (i64.gt_s (call $planar (global.get $selfX) (global.get $selfZ) (global.get $lastSX) (global.get $lastSZ))
        (i64.const 393216)) (then
      (global.set $portalEnd (i64.const 0)) (global.set $trailNext (global.get $trailHead))))
    (local.set $moving (i64.gt_s (local.get $speed) (i64.const 1800)))
    ;; A lift or jump changes height in place. Keep its head-height target current instead of trying to descend
    ;; through a rising platform to a breadcrumb on the floor it just left.
    (if (i32.and (i64.lt_s (call $planar (i64.const 0) (i64.const 0) (local.get $mx) (local.get $mz)) (i64.const 655))
        (i32.and (i64.gt_s (call $abs (local.get $my)) (i64.const 655)) (i64.lt_s (local.get $distance) (i64.const 196608))))
      (then (global.set $verticalEnd (i64.add (global.get $tick) (i64.const 90)))))
    (call $trail)
    ;; The idle choice lasts 2.2 to 8.2 seconds; successive choices use a seeded stream, never a short loop.
    (if (i64.ge_u (global.get $tick) (global.get $modeEnd)) (then
      (global.set $mode (i32.wrap_i64 (call $random (i32.const 5))))
      (global.set $modeEnd (i64.add (global.get $tick) (i64.add (i64.const 67) (call $random (i32.const 180)))))
      (global.set $period (i64.add (i64.const 83) (call $random (i32.const 98))))
      (global.set $phase (call $random (i32.const 65536)))
      (global.set $wanderX (i64.sub (call $random (i32.const 52428)) (i64.const 26214)))
      (global.set $wanderZ (i64.sub (call $random (i32.const 52428)) (i64.const 26214)))
      (global.set $wanderY (i64.sub (call $random (i32.const 15728)) (i64.const 7864)))))
    ;; Heading from the Moth's quaternion; side-and-behind keeps her face and hands comfortably clear.
    (local.set $fx (i64.sub (i64.const 0) (i64.mul (i64.const 2) (call $mul (global.get $qy) (global.get $qw)))))
    (local.set $fz (i64.sub (i64.mul (i64.const 2) (call $mul (global.get $qy) (global.get $qy))) (i64.const 65536)))
    ;; Keep the actual approach as the resting anchor, so stopping on a narrow stair never asks for a side offset
    ;; through its divider. Open-space loops and curiosities depart from this safe route point and return to it.
    (if (i32.or (local.get $moving) (i64.gt_s (local.get $distance) (i64.const 117965))) (then
      (global.set $restX (global.get $selfX)) (global.set $restZ (global.get $selfZ))))
    (local.set $gx (global.get $restX))
    (local.set $gz (global.get $restZ))
    (local.set $gy (i64.add (global.get $mothY) (i64.const 98304)))
    (if (i32.and (i32.eqz (local.get $moving)) (i64.lt_s (local.get $distance) (i64.const 196608))) (then
      (if (i32.ne (global.get $mode) (i32.const 0)) (then
        (local.set $gx (i64.add (local.get $gx) (global.get $wanderX)))
        (local.set $gz (i64.add (local.get $gz) (global.get $wanderZ)))
        (local.set $gy (i64.add (local.get $gy) (global.get $wanderY)))
        (local.set $gy (i64.add (local.get $gy) (call $mul (i64.const 6554)
          (call $wave (i64.add (global.get $tick) (global.get $phase)) (i64.const 139)))))))
      (if (i32.eq (global.get $mode) (i32.const 2)) (then
        (local.set $gx (i64.add (local.get $gx) (call $mul (i64.const 26214)
          (call $wave (i64.add (global.get $tick) (global.get $phase)) (global.get $period)))))
        (local.set $gz (i64.add (local.get $gz) (call $mul (i64.const 26214)
          (call $wave (i64.add (i64.add (global.get $tick) (global.get $phase)) (i64.div_u (global.get $period) (i64.const 4))) (global.get $period)))))))
      (if (i32.eq (global.get $mode) (i32.const 3)) (then
        ;; A brief look near the nearest exhibit, light, screen or portal. Never leave the Moth's 4 m neighbourhood.
        (if (i64.gt_s (call $s (i32.const 10)) (i64.const 0)) (then
          (local.set $gx (call $s (i32.const 7))) (local.set $gy (call $s (i32.const 8))) (local.set $gz (call $s (i32.const 9)))))))
      (if (i32.eq (global.get $mode) (i32.const 4)) (then
        (local.set $gx (i64.add (global.get $mothX) (call $mul (local.get $fz) (i64.const 58982))))
        (local.set $gz (i64.sub (global.get $mothZ) (call $mul (local.get $fx) (i64.const 58982))))
        (local.set $gy (i64.add (global.get $mothY) (i64.const 111411)))))))
    ;; Start and run: lead a little. The kit's release response carries the final motion into a stop.
    (if (local.get $moving) (then
      (local.set $gx (i64.add (local.get $gx) (i64.mul (local.get $mx) (i64.const 5))))
      (local.set $gz (i64.add (local.get $gz) (i64.mul (local.get $mz) (i64.const 5))))))
    (local.set $gx (i64.add (local.get $gx) (call $mul (call $s (i32.const 15)) (i64.const 22938))))
    ;; While moving, beyond 1.8 m, or on another height, keep the route: thread doors, stairs and the lift opening.
    (if (i32.or (local.get $moving) (i32.or
          (i64.gt_s (local.get $distance) (i64.const 117965))
          (i64.gt_s (call $abs (i64.sub (global.get $selfY) (local.get $gy))) (i64.const 49152)))) (then
      (block $reached (loop $pass
        (br_if $reached (i32.ge_u (global.get $trailNext) (global.get $trailHead)))
        (local.set $at (call $crumb (global.get $trailNext)))
        (br_if $reached (i64.gt_s (call $planar (global.get $selfX) (global.get $selfZ)
          (i64.load (local.get $at)) (i64.load offset=16 (local.get $at))) (i64.const 26214)))
        (br_if $reached (i32.and (i64.le_u (global.get $verticalEnd) (global.get $tick))
          (i64.gt_s (call $abs (i64.sub (global.get $selfY)
            (i64.add (i64.load offset=8 (local.get $at)) (i64.const 98304)))) (i64.const 32768))))
        (global.set $trailNext (i32.add (global.get $trailNext) (i32.const 1))) (br $pass)))
      (if (i32.lt_u (global.get $trailNext) (global.get $trailHead)) (then
        (local.set $route (i32.const 1))
        (local.set $at (call $crumb (global.get $trailNext)))
        (local.set $gx (i64.load (local.get $at)))
        (local.set $gy (i64.add (i64.load offset=8 (local.get $at)) (i64.const 98304)))
        (local.set $gz (i64.load offset=16 (local.get $at))))
        (else
          (local.set $gx (global.get $mothX)) (local.set $gz (global.get $mothZ))))))
    ;; Keep a metre of comfort while the Moth moves, but leave the crumb cursor ready for her next doorway.
    (if (i32.and (local.get $moving) (i64.lt_s (local.get $distance) (i64.const 65536))) (then
      (local.set $gx (global.get $selfX)) (local.set $gz (global.get $selfZ))))
    ;; A nearby sound briefly turns a curiosity toward its source, without leaving the current floor.
    (if (i32.and (i32.eqz (local.get $moving)) (i64.gt_s (call $s (i32.const 19)) (i64.const 0))) (then
      (if (i64.lt_s (call $planar (global.get $selfX) (global.get $selfZ)
          (call $s (i32.const 16)) (call $s (i32.const 18))) (i64.const 262144)) (then
        (local.set $gx (i64.div_s (i64.add (local.get $gx) (call $s (i32.const 16))) (i64.const 2)))
        (local.set $gz (i64.div_s (i64.add (local.get $gz) (call $s (i32.const 18))) (i64.const 2)))))))
    (if (i64.gt_s (global.get $portalEnd) (global.get $tick)) (then
      (local.set $gx (global.get $portalX)) (local.set $gy (global.get $portalY)) (local.set $gz (global.get $portalZ))))
    ;; Predict a swat before it reaches the body. The caller supplies world point + world velocity, nothing else.
    (if (i64.gt_s (call $s (i32.const 0)) (i64.const 0)) (then
      (call $threat (call $s (i32.const 1)) (call $s (i32.const 2)) (call $s (i32.const 3))
        (call $s (i32.const 4)) (call $s (i32.const 5)) (call $s (i32.const 6)))))
    ;; Neighbour clearance: avoid every nearby body's projected centre, including the Moth while face-inspecting.
    (if (i64.ge_s (call $s (i32.const 14)) (i64.const 0)) (then
      ;; A moving body supplies the same threat point and velocity as a future tracked hand. Ignore target switches
      ;; and teleports (more than 1 m between samples), which are not physical swings through the faerie's space.
      (if (global.get $lastNear) (then
        (local.set $dx (i64.sub (call $s (i32.const 11)) (global.get $nearX)))
        (local.set $dy (i64.sub (call $s (i32.const 12)) (global.get $nearY)))
        (local.set $dz (i64.sub (call $s (i32.const 13)) (global.get $nearZ)))
        (if (i64.lt_s (call $norm (local.get $dx) (local.get $dy) (local.get $dz)) (i64.const 65536)) (then
          (call $threat (call $s (i32.const 11)) (call $s (i32.const 12)) (call $s (i32.const 13))
            (i64.mul (local.get $dx) (i64.const 30)) (i64.mul (local.get $dy) (i64.const 30))
            (i64.mul (local.get $dz) (i64.const 30)))))))
      (local.set $dx (i64.sub (global.get $selfX) (call $s (i32.const 11))))
      (local.set $dy (i64.sub (global.get $selfY) (call $s (i32.const 12))))
      (local.set $dz (i64.sub (global.get $selfZ) (call $s (i32.const 13))))
      (local.set $length (call $norm (local.get $dx) (local.get $dy) (local.get $dz)))
      (if (i32.and (i64.gt_s (local.get $length) (i64.const 4096)) (i64.lt_s (local.get $length) (i64.const 65536))) (then
        (local.set $gx (i64.add (local.get $gx) (i64.div_s (i64.mul (local.get $dx) (i64.const 19661)) (local.get $length))))
        (local.set $gz (i64.add (local.get $gz) (i64.div_s (i64.mul (local.get $dz) (i64.const 19661)) (local.get $length))))))))
    (local.set $gain (i64.const 3))
    (if (local.get $route) (then (local.set $gain (i64.const 24))))
    (if (i64.lt_s (global.get $tick) (global.get $startleEnd)) (then
      (local.set $gain (i64.const 2))))
    (if (i64.lt_s (global.get $tick) (global.get $dodgeEnd)) (then
      (local.set $gx (global.get $dodgeX)) (local.set $gy (global.get $dodgeY)) (local.set $gz (global.get $dodgeZ))
      (local.set $gain (i64.const 10))))
    (if (i32.and (i64.gt_u (global.get $verticalEnd) (global.get $tick))
          (i64.lt_s (local.get $distance) (i64.const 163840))) (then
      (local.set $gy (i64.add (global.get $mothY) (i64.const 98304)))))
    ;; Keep clear of the Moth's whole occupied capsule, including during an evasion or vertical ride. A centre
    ;; sampled at torso height alone does not protect the ends of a tall body while a platform moves it.
    (if (i32.and (i64.lt_s (local.get $distance) (i64.const 49152))
          (i64.lt_s (call $abs (i64.sub (global.get $selfY) (i64.add (global.get $mothY) (i64.const 65536))))
            (i64.const 78643))) (then
      (local.set $dx (i64.sub (global.get $selfX) (global.get $mothX)))
      (local.set $dz (i64.sub (global.get $selfZ) (global.get $mothZ)))
      (local.set $length (call $planar (i64.const 0) (i64.const 0) (local.get $dx) (local.get $dz)))
      (if (i64.lt_s (local.get $length) (i64.const 1024)) (then
        (local.set $dx (i64.const -65536)) (local.set $dz (i64.const 0)) (local.set $length (i64.const 65536))))
      (local.set $gx (i64.add (global.get $mothX) (i64.div_s (i64.mul (local.get $dx) (i64.const 58982)) (local.get $length))))
      (local.set $gz (i64.add (global.get $mothZ) (i64.div_s (i64.mul (local.get $dz) (i64.const 58982)) (local.get $length))))))
    (local.set $dx (i64.sub (local.get $gx) (global.get $selfX)))
    (local.set $dy (i64.sub (local.get $gy) (global.get $selfY)))
    (local.set $dz (i64.sub (local.get $gz) (global.get $selfZ)))
    ;; Progress is measured, not timed against the display. Rise around an obstruction; keep the route until free.
    (if (i32.and (i64.gt_s (call $norm (local.get $dx) (local.get $dy) (local.get $dz)) (i64.const 49152))
        (i64.lt_s (call $planar (global.get $selfX) (global.get $selfZ) (global.get $lastSX) (global.get $lastSZ)) (i64.const 327)))
      (then (global.set $stalled (i32.add (global.get $stalled) (i32.const 1))))
      (else (global.set $stalled (i32.const 0))))
    (if (i32.gt_u (global.get $stalled) (i32.const 12)) (then (local.set $dy (i64.add (local.get $dy) (i64.const 19661)))))
    ;; Proportional velocity, bounded in all three axes together. Drive's rise channel is the same free-flight
    ;; MoveUp role the Moth already uses; there is no guest pose write or artificial attachment.
    (local.set $dx (i64.mul (local.get $dx) (local.get $gain)))
    (local.set $dy (i64.mul (local.get $dy) (local.get $gain)))
    (local.set $dz (i64.mul (local.get $dz) (local.get $gain)))
    (local.set $length (call $norm (local.get $dx) (local.get $dy) (local.get $dz)))
    (if (i64.lt_s (local.get $length) (i64.const 720896)) (then (local.set $length (i64.const 720896))))
    (call $act (i32.const 1) (i64.div_s (i64.shl (local.get $dx) (i64.const 16)) (local.get $length)))
    (call $act (i32.const 0) (i64.div_s (i64.shl (i64.sub (i64.const 0) (local.get $dz)) (i64.const 16)) (local.get $length)))
    (call $act (i32.const 2) (i64.div_s (i64.shl (local.get $dy) (i64.const 16)) (local.get $length)))
    (global.set $lastMX (global.get $mothX)) (global.set $lastMY (global.get $mothY)) (global.set $lastMZ (global.get $mothZ))
    (global.set $lastSX (global.get $selfX)) (global.set $lastSZ (global.get $selfZ))
    (global.set $lastNear (i64.ge_s (call $s (i32.const 14)) (i64.const 0)))
    (global.set $nearX (call $s (i32.const 11))) (global.set $nearY (call $s (i32.const 12)))
    (global.set $nearZ (call $s (i32.const 13)))
    (global.set $lastDX (local.get $mx)) (global.set $lastDZ (local.get $mz)))

  (func (export "puck_on_tick") (param $count i32) (result i32)
    (local $i i32) (local $cell i32) (local $kind i32) (local $disclosed i32)
    (local $ordinal i32) (local $verdict i32) (local $part i32) (local $body i64)
    (global.set $out (i32.const 0))
    (i64.store (i32.const 0x5000) (i64.const 0))
    (global.set $mothParts (i32.const 0))
    (global.set $selfParts (i32.const 0))
    (local.set $i (i32.const 0))
    (block $drained
      (loop $read
        (br_if $drained (i32.ge_u (local.get $i) (local.get $count)))
        (local.set $cell (i32.add (i32.const 0x2000) (i32.shl (local.get $i) (i32.const 5))))
        (call $consumeSense (local.get $cell))
        (local.set $kind (i32.load8_u (local.get $cell)))
        ;; Tick.
        (if (i32.eq (local.get $kind) (i32.const 1))
          (then (global.set $tick (i64.load offset=16 (local.get $cell)))))
        ;; A GrantedBody disclosure: the first one in a batch replaces every handle held.
        (if (i32.and (i32.eq (local.get $kind) (i32.const 3)) (i32.eqz (i32.load8_u offset=9 (local.get $cell))))
          (then
            (if (i32.eqz (local.get $disclosed))
              (then
                (local.set $disclosed (i32.const 1))
                (global.set $drive (i32.const 0))
                (global.set $observeSelf (i32.const 0))
                (global.set $observeMoth (i32.const 0))
                (global.set $askedMoth (i32.const -1))
                (global.set $askedSelf (i32.const -1))))
            (local.set $body (i64.load offset=24 (local.get $cell)))
            (if (i64.eq (i64.load offset=16 (local.get $cell)) (i64.const 1))
              (then
                (global.set $drive (i32.const 1))
                (global.set $driveIndex (i32.load16_u offset=4 (local.get $cell)))
                (global.set $driveGeneration (i32.load16_u offset=6 (local.get $cell)))
                (global.set $selfBody (local.get $body))))
            (if (i64.eq (i64.load offset=16 (local.get $cell)) (i64.const 2))
              (then
                (if (i64.eqz (local.get $body))
                  (then
                    (global.set $observeMoth (i32.const 1))
                    (global.set $observeMothIndex (i32.load16_u offset=4 (local.get $cell)))
                    (global.set $observeMothGeneration (i32.load16_u offset=6 (local.get $cell))))
                  (else
                    (global.set $observeSelf (i32.const 1))
                    (global.set $observeSelfIndex (i32.load16_u offset=4 (local.get $cell)))
                    (global.set $observeSelfGeneration (i32.load16_u offset=6 (local.get $cell)))))))))
        ;; An allowed pose answer part: 0 carries (x, y), 1 carries (z, 0).
        (if (i32.eq (local.get $kind) (i32.const 2))
          (then
            (local.set $ordinal (i32.load16_u offset=2 (local.get $cell)))
            (local.set $verdict (i32.load8_u offset=8 (local.get $cell)))
            (local.set $part (i32.load8_u offset=9 (local.get $cell)))
            (if (i32.and (i32.ge_u (local.get $verdict) (i32.const 1)) (i32.le_u (local.get $verdict) (i32.const 3)))
              (then
                (if (i32.eq (local.get $ordinal) (global.get $askedMoth))
                  (then
                    (if (i32.eqz (local.get $part))
                      (then
                        (global.set $mothX (i64.load offset=16 (local.get $cell)))
                        (global.set $mothY (i64.load offset=24 (local.get $cell)))))
                    (if (i32.eq (local.get $part) (i32.const 1))
                      (then (global.set $mothZ (i64.load offset=16 (local.get $cell)))))
                    (if (i32.eq (local.get $part) (i32.const 2))
                      (then (global.set $qy (i64.load offset=24 (local.get $cell)))))
                    (if (i32.eq (local.get $part) (i32.const 3))
                      (then (global.set $qw (i64.load offset=24 (local.get $cell)))))
                    (if (i32.le_u (local.get $part) (i32.const 1))
                      (then (global.set $mothParts (i32.or (global.get $mothParts)
                        (i32.shl (i32.const 1) (local.get $part))))))))
                (if (i32.eq (local.get $ordinal) (global.get $askedSelf))
                  (then
                    (if (i32.eqz (local.get $part))
                      (then
                        (global.set $selfX (i64.load offset=16 (local.get $cell)))
                        (global.set $selfY (i64.load offset=24 (local.get $cell)))))
                    (if (i32.eq (local.get $part) (i32.const 1))
                      (then (global.set $selfZ (i64.load offset=16 (local.get $cell)))))
                    (if (i32.le_u (local.get $part) (i32.const 1))
                      (then (global.set $selfParts (i32.or (global.get $selfParts)
                        (i32.shl (i32.const 1) (local.get $part))))))))))))
        (local.set $i (i32.add (local.get $i) (i32.const 1)))
        (br $read)))
    (call $readSenses)
    ;; Ask both poses every tick: an answer that never came back was starved, not refused.
    (global.set $askedMoth (i32.const -1))
    (global.set $askedSelf (i32.const -1))
    (if (global.get $observeMoth)
      (then (global.set $askedMoth (call $askPose (global.get $observeMothIndex) (global.get $observeMothGeneration)))))
    (if (global.get $observeSelf)
      (then (global.set $askedSelf (call $askPose (global.get $observeSelfIndex) (global.get $observeSelfGeneration)))))
    (if (i32.and (global.get $drive)
          (i32.and (i32.eq (global.get $mothParts) (i32.const 3)) (i32.eq (global.get $selfParts) (i32.const 3))))
      (then (call $steer)))
    (global.get $out))

  (data (i32.const 0x5400) "\ff\ff\ff\ff\ff\ff\ff\ff\ff\ff\ff\ff\ff\ff\ff\ff\ff\ff\ff\ff\ff\ff\ff\ff\ff\ff\ff\ff\ff\ff\ff\ff\ff\ff\ff\ff\ff\ff\ff\ff\ff\ff\ff\ff\ff\ff\ff\ff\ff\ff\ff\ff\ff\ff\ff\ff\ff\ff\ff\ff\ff\ff\ff\ff\ff\ff\ff\ff\ff\ff\ff\ff\ff\ff\ff\ff\ff\ff\ff\ff\ff\ff\ff\ff\ff\ff\ff\ff\ff\ff\ff\ff\ff\ff\ff\ff\ff\ff\ff\ff\ff\ff\ff\ff\ff\ff\ff\ff\ff\ff\ff\ff\ff\ff\ff\ff\ff\ff\ff\ff\ff\ff\ff\ff\ff\ff\ff\ff")
  (data (i32.const 0x6000) "companionSense")
  (data (i32.const 0x6040) "evade")
  (data (i32.const 24832) "\06active")
  (data (i32.const 24848) "\01x")
  (data (i32.const 24864) "\01y")
  (data (i32.const 24880) "\01z")
  (data (i32.const 24896) "\02vx")
  (data (i32.const 24912) "\02vy")
  (data (i32.const 24928) "\02vz")
  (data (i32.const 24944) "\02ix")
  (data (i32.const 24960) "\02iy")
  (data (i32.const 24976) "\02iz")
  (data (i32.const 24992) "\04kind")
  (data (i32.const 25008) "\02nx")
  (data (i32.const 25024) "\02ny")
  (data (i32.const 25040) "\02nz")
  (data (i32.const 25056) "\04near")
  (data (i32.const 25072) "\04turn")
  (data (i32.const 25088) "\02sx")
  (data (i32.const 25104) "\02sy")
  (data (i32.const 25120) "\02sz")
  (data (i32.const 25136) "\05sound")
  (data (i32.const 25152) "\04door")
)
