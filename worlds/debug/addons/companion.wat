;; The companion: a stand-in body that follows the Moth at a comfortable distance (companion.puck).
;;
;; It holds a Drive handle over its own body and Observe handles over that body and the Moth's (body 0), all disclosed
;; by the host from the grants companion.puck authors. Every tick it asks both poses; the answers arrive in the next
;; batch and describe the tick that settled before it. It drops a breadcrumb wherever the Moth has moved half a metre
;; since the last one and walks the crumbs in order, so it climbs the stairs she climbed and goes through the doors she
;; went through rather than cutting a corner into a wall. Within 2 m of her it stands still and forgets the crumbs
;; behind it. A crumb more than 6 m from the one before (a teleport, a respawn) starts the trail again from her. When
;; it has made no progress for a second while walking, it jumps once and skips a crumb.
;;
;; The kit (`walker`, moveFrame World) reads forward as world -Z and strafe as world +X, so the guest steers in world
;; axes and never turns its body. Every value crossing the ABI is FixedQ4816 raw bits: 0x10000 is one metre.
;;
;; Memory: channel descriptors at 0x0100, channel names at 0x0200, the output ring (8 cells) at 0x1000, the input ring
;; (32 cells) at 0x2000, the trail ring (64 crumbs of x, y, z as i64) at 0x3000.
(module
  (memory (export "memory") 1)

  ;; Input (three names at 0x0200), Request (BodyPose only), Response.
  (data (i32.const 0x0100)
    "\01\00\03\00\00\02\00\00\00\00\00\00\00\00\00\00"
    "\02\00\01\00\00\00\00\00\00\00\00\00\00\00\00\00"
    "\03\00\00\00\00\00\00\00\00\00\00\00\00\00\00\00")
  ;; The declared channel names, each a length byte and its UTF-8: ordinal 0 forward, 1 strafe, 2 jump.
  (data (i32.const 0x0200) "\07forward\06strafe\04jump")

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

  ;; The trail: crumbs written so far, and the next crumb to walk to.
  (global $trailHead (mut i32) (i32.const 0))
  (global $trailNext (mut i32) (i32.const 0))
  ;; The progress check: the position a second ago, and ticks walked since.
  (global $sampleX (mut i64) (i64.const 0))
  (global $sampleZ (mut i64) (i64.const 0))
  (global $walked (mut i32) (i32.const 0))

  (func (export "puck_abi_version") (result i32) (i32.const 1))
  (func (export "puck_out_ptr") (result i32) (i32.const 0x1000))
  (func (export "puck_out_cap") (result i32) (i32.const 8))
  (func (export "puck_in_ptr") (result i32) (i32.const 0x2000))
  (func (export "puck_in_cap") (result i32) (i32.const 32))
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

  (func $steer
    (local $toMoth i64) (local $goalX i64) (local $goalZ i64) (local $at i32)
    (local $dx i64) (local $dz i64) (local $length i64) (local $speed i64)
    (call $trail)
    (local.set $toMoth (call $planar (global.get $selfX) (global.get $selfZ) (global.get $mothX) (global.get $mothZ)))
    ;; Within 2 m of her: stand, and forget the crumbs already behind.
    (if (i64.lt_s (local.get $toMoth) (i64.const 0x20000))
      (then
        (global.set $trailNext (global.get $trailHead))
        (global.set $walked (i32.const 0))
        (global.set $sampleX (global.get $selfX))
        (global.set $sampleZ (global.get $selfZ))
        (return)))
    ;; Pass every crumb already within 0.6 m.
    (block $reached
      (loop $pass
        (br_if $reached (i32.ge_u (global.get $trailNext) (global.get $trailHead)))
        (local.set $at (call $crumb (global.get $trailNext)))
        (br_if $reached (i64.ge_s
          (call $planar (global.get $selfX) (global.get $selfZ) (i64.load (local.get $at)) (i64.load offset=16 (local.get $at)))
          (i64.const 0x999A)))
        (global.set $trailNext (i32.add (global.get $trailNext) (i32.const 1)))
        (br $pass)))
    (if (i32.lt_u (global.get $trailNext) (global.get $trailHead))
      (then
        (local.set $at (call $crumb (global.get $trailNext)))
        (local.set $goalX (i64.load (local.get $at)))
        (local.set $goalZ (i64.load offset=16 (local.get $at))))
      (else
        (local.set $goalX (global.get $mothX))
        (local.set $goalZ (global.get $mothZ))))
    (local.set $dx (i64.sub (local.get $goalX) (global.get $selfX)))
    (local.set $dz (i64.sub (local.get $goalZ) (global.get $selfZ)))
    (local.set $length (call $planar (i64.const 0) (i64.const 0) (local.get $dx) (local.get $dz)))
    (if (i64.eqz (local.get $length)) (then (return)))
    ;; Full speed from 2.25 m out, half speed at 2 m, where it stops: twice the distance past 1.75 m.
    (local.set $speed (i64.shl (i64.sub (local.get $toMoth) (i64.const 0x1C000)) (i64.const 1)))
    (if (i64.gt_s (local.get $speed) (i64.const 0x10000)) (then (local.set $speed (i64.const 0x10000))))
    (call $act (i32.const 1)
      (i64.div_s (i64.mul (local.get $dx) (local.get $speed)) (local.get $length)))
    (call $act (i32.const 0)
      (i64.div_s (i64.mul (i64.sub (i64.const 0) (local.get $dz)) (local.get $speed)) (local.get $length)))
    ;; No 10 cm of progress in a second at full speed: jump once and skip the crumb it is stuck on.
    (if (i64.lt_s (local.get $speed) (i64.const 0x10000))
      (then
        (global.set $walked (i32.const 0))
        (global.set $sampleX (global.get $selfX))
        (global.set $sampleZ (global.get $selfZ))
        (return)))
    (global.set $walked (i32.add (global.get $walked) (i32.const 1)))
    (if (i32.ge_u (global.get $walked) (i32.const 30))
      (then
        (if (i64.lt_s
              (call $planar (global.get $sampleX) (global.get $sampleZ) (global.get $selfX) (global.get $selfZ))
              (i64.const 0x199A))
          (then
            (drop (call $emit (i32.const 1) (i32.const 0) (global.get $driveIndex) (global.get $driveGeneration)
              (i32.const 8) (i64.const 0x10000) (i64.const 0) (i64.const 0)))
            (if (i32.lt_u (global.get $trailNext) (global.get $trailHead))
              (then (global.set $trailNext (i32.add (global.get $trailNext) (i32.const 1)))))))
        (global.set $walked (i32.const 0))
        (global.set $sampleX (global.get $selfX))
        (global.set $sampleZ (global.get $selfZ)))))

  (func (export "puck_on_tick") (param $count i32) (result i32)
    (local $i i32) (local $cell i32) (local $kind i32) (local $disclosed i32)
    (local $ordinal i32) (local $verdict i32) (local $part i32) (local $body i64)
    (global.set $out (i32.const 0))
    (global.set $mothParts (i32.const 0))
    (global.set $selfParts (i32.const 0))
    (local.set $i (i32.const 0))
    (block $drained
      (loop $read
        (br_if $drained (i32.ge_u (local.get $i) (local.get $count)))
        (local.set $cell (i32.add (i32.const 0x2000) (i32.shl (local.get $i) (i32.const 5))))
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
)
