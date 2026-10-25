;; The watchdog: a development tool that checks invariants every tick and records each violation in the `watchdog` and
;; `watchdogLog` state rows (watchdog.puck), where `world.state` and the HUD panel read them.
;;
;; Its invariants, each reported once as it starts to hold and again only after it has cleared:
;;   below-floor             a body more than 1 m under y = 0, the lowest floor anything stands on (fall-respawn
;;                           catches a body at y = -40, long after it fell through);
;;   unnormalised-orientation a body whose orientation quaternion is more than 1/64 away from unit length;
;;   piece-overlap           a collision begin between two chess pieces (bodies 8 to 39), which rest apart;
;;   companion-overlap       a collision begin between the Moth (body 0) and the companion (body 42), which keeps 2 m
;;                           away from her.
;; The host discloses an Observe handle for every body watchdog.puck grants. The guest queries the pose of every body
;; in one BodyPoses bulk read every tick. Collision
;; begins arrive as events on the same grants. A pose answer and an event in the batch of tick T describe the tick that
;; settled before it, T - 1, and that is the tick a violation records.
;;
;; It writes two rows. `watchdog` holds numbers: `count` (violations so far), `tick` (the last one's) and `first` (the
;; first one's), -1 before any. `watchdogLog` holds the `first` and `last` violations as lines such as
;; "t=812 below-floor body=0 y=-1.25". Every value crossing the ABI is FixedQ4816 raw bits: 0x10000 is one.
;;
;; Memory: channel descriptors at 0x0100, the output ring (21 cells) at 0x1000, the input ring (64 cells) at 0x2000,
;; the watch table (body, handle index, handle generation, latches; 16 bytes an entry, 64 entries) at 0x3000, the
;; packed Observe handles at 0x3400 and bulk pose records at 0x7000, the first and last lines at 0x4000 and 0x4100, the line
;; being built at 0x4200, cell declarations at 0x8000, names at 0x9000, and NUL-terminated literals from 0x6000, 128
;; bytes apart.
(module
  (memory (export "memory") 1)

  ;; Request (through WriteStateText, verb 16), Response.
  (data (i32.const 0x0100)
    "\02\00\11\00\00\00\00\00\00\00\00\00\00\00\00\00"
    "\03\00\00\00\00\00\00\00\00\00\00\00\00\00\00\00")

  (data (i32.const 0x6400) "t=\00")
  (data (i32.const 0x6480) " below-floor body=\00")
  (data (i32.const 0x6500) " y=\00")
  (data (i32.const 0x6580) " unnormalised-orientation body=\00")
  (data (i32.const 0x6600) " |q|^2=\00")
  (data (i32.const 0x6680) " piece-overlap bodies=\00")
  (data (i32.const 0x6700) " companion-overlap bodies=\00")
  (data (i32.const 0x6780) "+\00")

  (global $bound (mut i32) (i32.const 0))
  (global $out (mut i32) (i32.const 0))
  (global $tick (mut i64) (i64.const 0))
  (global $watched (mut i32) (i32.const 0))
  (global $askedPoses (mut i32) (i32.const -1))
  (global $poseCount (mut i32) (i32.const 0))
  (global $checked (mut i32) (i32.const 0))
  (global $absent (mut i32) (i32.const 0))
  (global $checkedTick (mut i64) (i64.const -1))

  (global $count (mut i64) (i64.const 0))
  (global $lastTick (mut i64) (i64.const -1))
  (global $firstTick (mut i64) (i64.const -1))
  (global $firstLength (mut i32) (i32.const 0))
  (global $lastLength (mut i32) (i32.const 0))
  (global $dirty (mut i32) (i32.const 0))
  (global $cursor (mut i32) (i32.const 0))

  (func (export "puck_abi_version") (result i32) (i32.const 1))
  (func (export "puck_out_ptr") (result i32) (i32.const 0x1000))
  (func (export "puck_out_cap") (result i32) (i32.const 21))
  (func (export "puck_in_ptr") (result i32) (i32.const 0x2000))
  (func (export "puck_in_cap") (result i32) (i32.const 64))
  (func (export "puck_channels_ptr") (result i32) (i32.const 0x0100))
  (func (export "puck_channels_count") (result i32) (i32.const 2))

  ;; Writes one output cell and returns its ordinal in this batch.
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

  ;; Text building at the cursor.
  (func $put (param $byte i32)
    (i32.store8 (global.get $cursor) (local.get $byte))
    (global.set $cursor (i32.add (global.get $cursor) (i32.const 1))))
  (func $puts (param $at i32)
    (block $end
      (loop $next
        (br_if $end (i32.eqz (i32.load8_u (local.get $at))))
        (call $put (i32.load8_u (local.get $at)))
        (local.set $at (i32.add (local.get $at) (i32.const 1)))
        (br $next))))
  (func $putBytes (param $at i32) (param $length i32)
    (memory.copy (global.get $cursor) (local.get $at) (local.get $length))
    (global.set $cursor (i32.add (global.get $cursor) (local.get $length))))
  ;; A signed decimal integer.
  (func $putInt (param $v i64)
    (local $digits i32) (local $at i32)
    (if (i64.lt_s (local.get $v) (i64.const 0))
      (then
        (call $put (i32.const 45))
        (local.set $v (i64.sub (i64.const 0) (local.get $v)))))
    ;; Digits land backwards at 0x42F0 and copy out forwards.
    (local.set $at (i32.const 0x42F0))
    (loop $digit
      (local.set $at (i32.sub (local.get $at) (i32.const 1)))
      (i32.store8 (local.get $at) (i32.add (i32.const 48) (i32.wrap_i64 (i64.rem_u (local.get $v) (i64.const 10)))))
      (local.set $v (i64.div_u (local.get $v) (i64.const 10)))
      (br_if $digit (i64.ne (local.get $v) (i64.const 0))))
    (call $putBytes (local.get $at) (i32.sub (i32.const 0x42F0) (local.get $at))))
  ;; A FixedQ4816 value to two decimals, truncated toward zero.
  (func $putFixed (param $v i64)
    (local $hundredths i64)
    (if (i64.lt_s (local.get $v) (i64.const 0))
      (then
        (call $put (i32.const 45))
        (local.set $v (i64.sub (i64.const 0) (local.get $v)))))
    (call $putInt (i64.shr_u (local.get $v) (i64.const 16)))
    (call $put (i32.const 46))
    (local.set $hundredths (i64.shr_u (i64.mul (i64.and (local.get $v) (i64.const 0xFFFF)) (i64.const 100)) (i64.const 16)))
    (call $put (i32.add (i32.const 48) (i32.wrap_i64 (i64.div_u (local.get $hundredths) (i64.const 10)))))
    (call $put (i32.add (i32.const 48) (i32.wrap_i64 (i64.rem_u (local.get $hundredths) (i64.const 10))))))

  ;; Starts a violation line for the settled tick this batch describes.
  (func $begin
    (global.set $cursor (i32.const 0x4200))
    (call $puts (i32.const 0x6400))
    (call $putInt (i64.sub (global.get $tick) (i64.const 1))))
  ;; Finishes the line: counts it, keeps it as the last (and, the first time, the first) and marks the row dirty.
  (func $record
    (local $length i32)
    (local.set $length (i32.sub (global.get $cursor) (i32.const 0x4200)))
    (if (i32.gt_u (local.get $length) (i32.const 120)) (then (local.set $length (i32.const 120))))
    (global.set $count (i64.add (global.get $count) (i64.const 1)))
    (global.set $lastTick (i64.sub (global.get $tick) (i64.const 1)))
    (memory.copy (i32.const 0x4100) (i32.const 0x4200) (local.get $length))
    (global.set $lastLength (local.get $length))
    (if (i64.eq (global.get $count) (i64.const 1))
      (then
        (global.set $firstTick (global.get $lastTick))
        (memory.copy (i32.const 0x4000) (i32.const 0x4200) (local.get $length))
        (global.set $firstLength (local.get $length))))
    (global.set $dirty (i32.const 1)))

  ;; A collision begin between bodies a < b.
  (func $collision (param $a i64) (param $b i64)
    (local $pieces i32)
    (local.set $pieces (i32.and
      (i32.and (i64.ge_s (local.get $a) (i64.const 8)) (i64.le_s (local.get $a) (i64.const 39)))
      (i32.and (i64.ge_s (local.get $b) (i64.const 8)) (i64.le_s (local.get $b) (i64.const 39)))))
    (if (local.get $pieces)
      (then
        (call $begin)
        (call $puts (i32.const 0x6680))))
    (if (i32.and (i64.eqz (local.get $a)) (i64.eq (local.get $b) (i64.const 42)))
      (then
        (call $begin)
        (call $puts (i32.const 0x6700))
        (local.set $pieces (i32.const 1))))
    (if (local.get $pieces)
      (then
        (call $putInt (local.get $a))
        (call $puts (i32.const 0x6780))
        (call $putInt (local.get $b))
        (call $record))))

  ;; One body's assembled pose: its height and orientation, checked against both latches of its watch entry.
  (func $judge (param $entry i32) (param $y i64) (param $qx i64) (param $qy i64) (param $qz i64) (param $qw i64)
    (local $at i32) (local $flags i32) (local $norm i64) (local $error i64)
    (local.set $at (i32.add (i32.const 0x3000) (i32.shl (local.get $entry) (i32.const 4))))
    (local.set $flags (i32.load offset=12 (local.get $at)))
    (if (i64.lt_s (local.get $y) (i64.const -0x10000))
      (then
        (if (i32.eqz (i32.and (local.get $flags) (i32.const 1)))
          (then
            (local.set $flags (i32.or (local.get $flags) (i32.const 1)))
            (call $begin)
            (call $puts (i32.const 0x6480))
            (call $putInt (i64.extend_i32_u (i32.load (local.get $at))))
            (call $puts (i32.const 0x6500))
            (call $putFixed (local.get $y))
            (call $record))))
      (else (local.set $flags (i32.and (local.get $flags) (i32.const -2)))))
    (local.set $norm (i64.shr_s (i64.add (i64.add
      (i64.mul (local.get $qx) (local.get $qx)) (i64.mul (local.get $qy) (local.get $qy)))
      (i64.add (i64.mul (local.get $qz) (local.get $qz)) (i64.mul (local.get $qw) (local.get $qw)))) (i64.const 16)))
    (local.set $error (i64.sub (local.get $norm) (i64.const 0x10000)))
    (if (i64.lt_s (local.get $error) (i64.const 0)) (then (local.set $error (i64.sub (i64.const 0) (local.get $error)))))
    (if (i64.gt_s (local.get $error) (i64.const 0x400))
      (then
        (if (i32.eqz (i32.and (local.get $flags) (i32.const 2)))
          (then
            (local.set $flags (i32.or (local.get $flags) (i32.const 2)))
            (call $begin)
            (call $puts (i32.const 0x6580))
            (call $putInt (i64.extend_i32_u (i32.load (local.get $at))))
            (call $puts (i32.const 0x6600))
            (call $putFixed (local.get $norm))
            (call $record))))
      (else (local.set $flags (i32.and (local.get $flags) (i32.const -3)))))
    (i32.store offset=12 (local.get $at) (local.get $flags)))

  ;; Submits a payload as one UpsertStateRow (mutation kind 44) through the Mutate handle.

  ;; Writes both whole rows.
  (func $publish
    (call $write (i32.const 0) (global.get $count))
    (call $write (i32.const 1) (global.get $lastTick))
    (call $write (i32.const 2) (global.get $firstTick))
    (call $write (i32.const 3) (i64.extend_i32_u (global.get $checked)))
    (call $write (i32.const 4) (i64.extend_i32_u (global.get $watched)))
    (call $write (i32.const 5) (global.get $checkedTick))
    (call $write (i32.const 6) (i64.extend_i32_u (global.get $absent)))
    (if (global.get $dirty) (then
      (call $text (i32.const 7) (i32.const 0x4000) (global.get $firstLength))
      (call $text (i32.const 8) (i32.const 0x4100) (global.get $lastLength))
      (global.set $dirty (i32.const 0)))))

  (func (export "puck_on_tick") (param $count i32) (result i32)
    (local $i i32) (local $cell i32) (local $kind i32) (local $verb i32) (local $disclosed i32)
    (local $ordinal i32) (local $verdict i32) (local $entry i32) (local $slot i32)
    (local $at i32)
    (global.set $out (i32.const 0))
    (if (i32.eqz (global.get $bound)) (then
      (drop (call $emit (i32.const 1) (i32.const 0) (i32.const 0) (i32.const 14)
        (i64.const 32768) (i64.const 9) (i64.const 0)))
      (global.set $bound (i32.const 1))))
    (global.set $checked (i32.const 0))
    (global.set $absent (i32.const 0))
    (local.set $i (i32.const 0))
    (block $drained
      (loop $read
        (br_if $drained (i32.ge_u (local.get $i) (local.get $count)))
        (local.set $cell (i32.add (i32.const 0x2000) (i32.shl (local.get $i) (i32.const 5))))
        (local.set $kind (i32.load8_u (local.get $cell)))
        (local.set $verb (i32.load8_u offset=9 (local.get $cell)))
        (if (i32.eq (local.get $kind) (i32.const 1))
          (then (global.set $tick (i64.load offset=16 (local.get $cell)))))
        (if (i32.eq (local.get $kind) (i32.const 3))
          (then
            ;; A GrantedBody disclosure: the first one in a batch replaces the whole watch table.
            (if (i32.eqz (local.get $verb))
              (then
                (if (i32.eqz (local.get $disclosed))
                  (then
                    (local.set $disclosed (i32.const 1))
                    (global.set $watched (i32.const 0))))
                (if (i32.and (i64.eq (i64.load offset=16 (local.get $cell)) (i64.const 2))
                      (i32.lt_u (global.get $watched) (i32.const 64)))
                  (then
                    (local.set $at (i32.add (i32.const 0x3000) (i32.shl (global.get $watched) (i32.const 4))))
                    (i32.store (local.get $at) (i32.wrap_i64 (i64.load offset=24 (local.get $cell))))
                    (i32.store offset=4 (local.get $at) (i32.load16_u offset=4 (local.get $cell)))
                    (i32.store offset=8 (local.get $at) (i32.load16_u offset=6 (local.get $cell)))
                    (i32.store offset=12 (local.get $at) (i32.const 0))
                    (global.set $watched (i32.add (global.get $watched) (i32.const 1)))))))
            ;; A collision begin.
            (if (i32.eq (local.get $verb) (i32.const 5))
              (then (call $collision (i64.load offset=16 (local.get $cell)) (i64.load offset=24 (local.get $cell)))))))
        (if (i32.eq (local.get $kind) (i32.const 2))
          (then
            (local.set $ordinal (i32.load16_u offset=2 (local.get $cell)))
            (local.set $verdict (i32.load8_u offset=8 (local.get $cell)))

            ;; One completion covers the ordered bulk records written after last tick settled.
            (if (i32.and (i32.eq (local.get $ordinal) (global.get $askedPoses))
                  (i32.eq (local.get $verdict) (i32.const 10)))
              (then
                (global.set $checkedTick (i64.load offset=24 (local.get $cell)))
                (local.set $entry (i32.const 0))
                (block $posesDone
                  (loop $pose
                    (br_if $posesDone (i32.ge_u (local.get $entry) (global.get $poseCount)))
                    (local.set $slot (i32.add (i32.const 0x7000) (i32.shl (local.get $entry) (i32.const 6))))
                    (local.set $at (i32.add (i32.const 0x3000) (i32.shl (local.get $entry) (i32.const 4))))
                    (local.set $verdict (i32.load8_u offset=4 (local.get $slot)))
                    (if (i32.eq (local.get $verdict) (i32.const 7))
                      (then (global.set $absent (i32.add (global.get $absent) (i32.const 1)))))
                    (if (i32.and
                          (i32.and (i32.ge_u (local.get $verdict) (i32.const 1)) (i32.le_u (local.get $verdict) (i32.const 3)))
                          (i32.and (i32.lt_u (local.get $entry) (global.get $watched))
                            (i32.eq (i32.load (local.get $slot)) (i32.load (local.get $at)))))
                      (then
                        (call $judge (local.get $entry) (i64.load offset=16 (local.get $slot))
                          (i64.load offset=32 (local.get $slot)) (i64.load offset=40 (local.get $slot))
                          (i64.load offset=48 (local.get $slot)) (i64.load offset=56 (local.get $slot)))
                        (global.set $checked (i32.add (global.get $checked) (i32.const 1)))))
                    (local.set $entry (i32.add (local.get $entry) (i32.const 1)))
                    (br $pose)))))))
        (local.set $i (i32.add (local.get $i) (i32.const 1)))
        (br $read)))

    ;; Copy the observed set's handles into one bulk request. Results do not consume ring cells.
    (global.set $askedPoses (i32.const -1))
    (if (global.get $watched)
      (then
        (local.set $i (i32.const 0))
        (loop $handles
          (local.set $at (i32.add (i32.const 0x3000) (i32.shl (local.get $i) (i32.const 4))))
          (local.set $slot (i32.add (i32.const 0x3400) (i32.shl (local.get $i) (i32.const 2))))
          (i32.store16 (local.get $slot) (i32.load offset=4 (local.get $at)))
          (i32.store16 offset=2 (local.get $slot) (i32.load offset=8 (local.get $at)))
          (local.set $i (i32.add (local.get $i) (i32.const 1)))
          (br_if $handles (i32.lt_u (local.get $i) (global.get $watched))))
        (global.set $poseCount (global.get $watched))
        (global.set $askedPoses (call $emit (i32.const 1) (i32.const 0) (i32.const 0) (i32.const 13)
          (i64.const 0x3400) (i64.extend_i32_u (global.get $watched)) (i64.const 0x7000)))))
    (call $publish)
    (global.get $out))

  (func $write (param $slot i32) (param $value i64)
    (drop (call $emit (i32.const 1) (local.get $slot) (i32.const 1) (i32.const 15)
      (local.get $value) (i64.const 0) (i64.const 0))))
  (func $text (param $slot i32) (param $ptr i32) (param $length i32)
    (drop (call $emit (i32.const 1) (local.get $slot) (i32.const 1) (i32.const 16)
      (i64.extend_i32_u (local.get $ptr)) (i64.extend_i32_u (local.get $length)) (i64.const 0))))

  ;; Cell declarations: row pointer, key pointer, UTF-8 lengths, reserved zero.
  (data (i32.const 32768) "\00\90\00\00\09\90\00\00\08\00\05\00\00\00\00\00\00\90\00\00\0f\90\00\00\08\00\04\00\00\00\00\00\00\90\00\00\14\90\00\00\08\00\05\00\00\00\00\00\00\90\00\00\1a\90\00\00\08\00\07\00\00\00\00\00\00\90\00\00\22\90\00\00\08\00\08\00\00\00\00\00\00\90\00\00\2b\90\00\00\08\00\0b\00\00\00\00\00\00\90\00\00\37\90\00\00\08\00\06\00\00\00\00\00\3e\90\00\00\14\90\00\00\0b\00\05\00\00\00\00\00\3e\90\00\00\4a\90\00\00\0b\00\04\00\00\00\00\00")
  (data (i32.const 36864) "\77\61\74\63\68\64\6f\67\00\63\6f\75\6e\74\00\74\69\63\6b\00\66\69\72\73\74\00\63\68\65\63\6b\65\64\00\6f\62\73\65\72\76\65\64\00\63\68\65\63\6b\65\64\54\69\63\6b\00\61\62\73\65\6e\74\00\77\61\74\63\68\64\6f\67\4c\6f\67\00\6c\61\73\74\00")
)
