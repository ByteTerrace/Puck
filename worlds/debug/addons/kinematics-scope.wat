;; kinematics-scope: optional debug development tool; see ../addons.md and ../kinematics-scope.puck.
;; Wire: input names at 0x200; descriptors 0x100; rings 0x1000/0x2000; handles 0x3000;
;; prior-batch ordinal map 0x4000; config 0x4400; trace work 0x5000; cell table 0x10000;
;; read keys 0xc000 and cell names 0x14000. All arithmetic and wire values are integers, fixed values are Q48.16 raw bits.
(module
  (memory (export "memory") 2)
  (data (i32.const 0x100)
    "\01\00\07\00\00\02\00\00\00\00\00\00\00\00\00\00"
    "\02\00\11\00\00\00\00\00\00\00\00\00\00\00\00\00"
    "\03\00\00\00\00\00\00\00\00\00\00\00\00\00\00\00")
  (data (i32.const 0x200) "\07forward\06strafe\04turn\04jump\06flight\08interact\04rise")

  (global $bound (mut i32) (i32.const 0))
  (global $out (mut i32) (i32.const 0))
  (global $tick (mut i64) (i64.const 0))

  (global $pose (mut i32) (i32.const -1))
  (global $kin (mut i32) (i32.const -1))
  (global $poseParts (mut i32) (i32.const 0))
  (global $kinParts (mut i32) (i32.const 0))
  (global $reads (mut i32) (i32.const 0))
  (global $x (mut i64) (i64.const 0))
  (global $y (mut i64) (i64.const 0))
  (global $z (mut i64) (i64.const 0))
  (global $vy (mut i64) (i64.const 0))
  (global $facts (mut i64) (i64.const 0))
  (global $contacts (mut i64) (i64.const 0))
  (global $gaps (mut i64) (i64.const 0))
  (global $refused (mut i64) (i64.const 0))
  (func (export "puck_abi_version") (result i32) (i32.const 1))
  (func (export "puck_out_ptr") (result i32) (i32.const 0x1000))
  (func (export "puck_out_cap") (result i32) (i32.const 40))
  (func (export "puck_in_ptr") (result i32) (i32.const 0x2000))
  (func (export "puck_in_cap") (result i32) (i32.const 64))
  (func (export "puck_channels_ptr") (result i32) (i32.const 0x100))
  (func (export "puck_channels_count") (result i32) (i32.const 3))
  (func $emit (param $kind i32) (param $channel i32) (param $index i32) (param $generation i32) (param $verb i32)
      (param $a i64) (param $b i64) (param $c i64) (result i32)
    (local $p i32) (local $ordinal i32)
    (local.set $ordinal (global.get $out))
    (local.set $p (i32.add (i32.const 0x1000) (i32.shl (local.get $ordinal) (i32.const 5))))
    (i32.store8 (local.get $p) (local.get $kind))
    (i32.store8 offset=1 (local.get $p) (local.get $channel))
    (i32.store16 offset=2 (local.get $p) (local.get $index))
    (i32.store16 offset=4 (local.get $p) (local.get $generation))
    (i32.store16 offset=6 (local.get $p) (local.get $verb))
    (i64.store offset=8 (local.get $p) (local.get $a))
    (i64.store offset=16 (local.get $p) (local.get $b))
    (i64.store offset=24 (local.get $p) (local.get $c))
    (i64.store (i32.add (i32.const 0x4000) (i32.shl (local.get $ordinal) (i32.const 3))) (i64.const 0))
    (global.set $out (i32.add (local.get $ordinal) (i32.const 1)))
    (local.get $ordinal))
  ;; Handles: state/config slots at 0x3000, Drive bodies at 0x3400, Observe bodies at 0x3800.
  ;; Each slot: index u16, generation u16, present u32. Answer maps at 0x4000 (type, destination).
  (func $request (param $h i32) (param $verb i32) (param $a i64) (param $b i64) (result i32)
    (call $emit (i32.const 1) (i32.const 1) (i32.load16_u (local.get $h)) (i32.load16_u offset=2 (local.get $h))
      (local.get $verb) (local.get $a) (local.get $b) (i64.const 0)))
  (func $ask (param $slot i32) (param $kind i32) (param $name i32) (param $length i32) (param $cap i64)
    (local $h i32) (local $o i32) (local $m i32)
    (local.set $h (i32.add (i32.const 0x3000) (i32.shl (local.get $slot) (i32.const 3))))
    (if (i32.load offset=4 (local.get $h)) (then (return)))
    (local.set $o (call $emit (i32.const 2) (i32.const 1) (i32.const 0) (i32.const 0) (local.get $kind)
      (i64.extend_i32_u (local.get $name)) (local.get $cap) (i64.extend_i32_u (local.get $length))))
    (local.set $m (i32.add (i32.const 0x4000) (i32.shl (local.get $o) (i32.const 3))))
    (i32.store (local.get $m) (i32.const 1)) (i32.store offset=4 (local.get $m) (local.get $h)))
  (func $read (param $slot i32) (param $key i32) (param $length i32) (param $dest i32)
    (local $h i32) (local $o i32) (local $m i32)
    (local.set $h (i32.add (i32.const 0x3000) (i32.shl (local.get $slot) (i32.const 3))))
    (if (i32.eqz (i32.load offset=4 (local.get $h))) (then (return)))
    (local.set $o (call $request (local.get $h) (i32.const 4) (i64.extend_i32_u (local.get $key)) (i64.extend_i32_u (local.get $length))))
    (local.set $m (i32.add (i32.const 0x4000) (i32.shl (local.get $o) (i32.const 3))))
    (i32.store (local.get $m) (i32.const 2)) (i32.store offset=4 (local.get $m) (local.get $dest)))

  (func $bodyQueries (param $body i32)
    (local $h i32)
    (global.set $pose (i32.const -1)) (global.set $kin (i32.const -1))
    (if (i32.ge_u (local.get $body) (i32.const 64)) (then (return)))
    (local.set $h (i32.add (i32.const 0x3800) (i32.shl (local.get $body) (i32.const 3))))
    (if (i32.load offset=4 (local.get $h)) (then
      (global.set $pose (call $request (local.get $h) (i32.const 0) (i64.const 0) (i64.const 0)))
      (global.set $kin (call $request (local.get $h) (i32.const 3) (i64.const 0) (i64.const 0))))))

  ;; Signed decimal, including MIN_VALUE (unsigned magnitude after wrapping negation).

  (func $drain (param $count i32)
    (local $i i32) (local $p i32) (local $k i32) (local $o i32) (local $m i32) (local $d i32)
    (local $v i32) (local $part i32) (local $disclosed i32) (local $b i32)
    (global.set $poseParts (i32.const 0)) (global.set $kinParts (i32.const 0)) (global.set $reads (i32.const 0))
    (block $done (loop $next
      (br_if $done (i32.ge_u (local.get $i) (local.get $count)))
      (local.set $p (i32.add (i32.const 0x2000) (i32.shl (local.get $i) (i32.const 5))))
      (local.set $k (i32.load8_u (local.get $p)))
      (if (i32.eq (local.get $k) (i32.const 1)) (then (global.set $tick (i64.load offset=16 (local.get $p)))))
      (if (i32.eq (local.get $k) (i32.const 3)) (then
        (if (i32.eq (i32.load8_u offset=9 (local.get $p)) (i32.const 10))
          (then (global.set $gaps (i64.load offset=16 (local.get $p)))))
        (if (i32.eqz (i32.load8_u offset=9 (local.get $p))) (then
          (if (i32.eqz (local.get $disclosed)) (then
            (memory.fill (i32.const 0x3400) (i32.const 0) (i32.const 0x800)) (local.set $disclosed (i32.const 1))))
          (local.set $b (i32.wrap_i64 (i64.load offset=24 (local.get $p))))
          (if (i32.lt_u (local.get $b) (i32.const 64)) (then
            (local.set $d (i32.add (if (result i32) (i64.eq (i64.load offset=16 (local.get $p)) (i64.const 1))
              (then (i32.const 0x3400)) (else (i32.const 0x3800))) (i32.shl (local.get $b) (i32.const 3))))
            (i32.store (local.get $d) (i32.load offset=4 (local.get $p))) (i32.store offset=4 (local.get $d) (i32.const 1))))))))
      (if (i32.eq (local.get $k) (i32.const 2)) (then
        (local.set $o (i32.load16_u offset=2 (local.get $p)))
        (local.set $v (i32.load8_u offset=8 (local.get $p)))
        (local.set $part (i32.load8_u offset=9 (local.get $p)))
        (local.set $m (i32.add (i32.const 0x4000) (i32.shl (local.get $o) (i32.const 3))))
        (local.set $d (i32.load offset=4 (local.get $m)))
        (if (i32.and (i32.ge_u (local.get $v) (i32.const 1)) (i32.le_u (local.get $v) (i32.const 3))) (then
          (if (i32.eq (i32.load (local.get $m)) (i32.const 1)) (then
            (i32.store (local.get $d) (i32.load offset=4 (local.get $p))) (i32.store offset=4 (local.get $d) (i32.const 1))))
          (if (i32.eq (i32.load (local.get $m)) (i32.const 2)) (then
            (i64.store (local.get $d) (i64.load offset=16 (local.get $p)))
            (global.set $reads (i32.add (global.get $reads) (i32.const 1)))))
          (if (i32.eq (local.get $o) (global.get $pose)) (then
            (if (i32.eqz (local.get $part)) (then
              (global.set $x (i64.load offset=16 (local.get $p))) (global.set $y (i64.load offset=24 (local.get $p)))))
            (if (i32.eq (local.get $part) (i32.const 1)) (then (global.set $z (i64.load offset=16 (local.get $p)))))
            (global.set $poseParts (i32.or (global.get $poseParts) (i32.shl (i32.const 1) (local.get $part))))))
          (if (i32.eq (local.get $o) (global.get $kin)) (then
            (if (i32.eqz (local.get $part)) (then
              (global.set $y (i64.load offset=16 (local.get $p))) (global.set $vy (i64.load offset=24 (local.get $p)))))
            (if (i32.eq (local.get $part) (i32.const 1)) (then
              (global.set $facts (i64.load offset=16 (local.get $p))) (global.set $contacts (i64.load offset=24 (local.get $p)))))
            (global.set $kinParts (i32.or (global.get $kinParts) (i32.shl (i32.const 1) (local.get $part)))))))
        (else
          (if (i32.ne (local.get $v) (i32.const 10)) (then
            (global.set $refused (i64.add (global.get $refused) (i64.const 1)))))
          (if (i32.eq (local.get $v) (i32.const 9)) (then (memory.fill (i32.const 0x3000) (i32.const 0) (i32.const 32))))))))
      (local.set $i (i32.add (local.get $i) (i32.const 1))) (br $next))))

  (global $samples (mut i64) (i64.const 0))
  (global $previous (mut i64) (i64.const -1))
  (global $selected (mut i64) (i64.const -1))

  (func (export "puck_on_tick") (param $count i32) (result i32)
    (local $i i32) (local $dx i64) (local $dy i64) (local $dz i64) (local $r i32) (local $p i32)
    (call $drain (local.get $count))
    (global.set $out (i32.const 0))
    (if (i32.eqz (global.get $bound)) (then
      (drop (call $emit (i32.const 1) (i32.const 1) (i32.const 0) (i32.const 0) (i32.const 14)
        (i64.const 65536) (i64.const 774) (i64.const 0)))
      (global.set $bound (i32.const 1))))

    (if (i32.and (i32.and (i32.eq (global.get $reads) (i32.const 2)) (i64.ne (i64.load (i32.const 17408)) (i64.const 0)))
      (i32.eq (global.get $kinParts) (i32.const 3))) (then
      ;; A selection change discards the in-flight old body's answer; the next tick reads the chosen body.
      (if (i64.ne (global.get $selected) (i64.load (i32.const 17416))) (then
        (global.set $selected (i64.load (i32.const 17416))) (global.set $previous (i64.const -1))
        (memory.fill (i32.const 0x5000) (i32.const 32) (i32.const 128)))
      (else
        (local.set $p (i32.wrap_i64 (i64.and (global.get $samples) (i64.const 127))))
        (call $write (i32.add (i32.const 0) (local.get $p)) (global.get $y))
        (call $write (i32.add (i32.const 128) (local.get $p)) (global.get $vy))
        (call $write (i32.add (i32.const 256) (local.get $p)) (global.get $facts))
        (call $write (i32.add (i32.const 384) (local.get $p)) (global.get $contacts))
        (call $write (i32.add (i32.const 512) (local.get $p)) (i64.sub (global.get $tick) (i64.const 1)))
        ;; Contact events: bit 0 ground-begin, 1 ground-end, 2 obstruction-begin, 3 obstruction-end.
        (local.set $r (i32.const 0))
        (if (i64.ge_s (global.get $previous) (i64.const 0)) (then
          (if (i64.ne (i64.and (global.get $previous) (i64.const 1)) (i64.and (global.get $contacts) (i64.const 1)))
            (then (local.set $r (if (result i32) (i64.eqz (i64.and (global.get $contacts) (i64.const 1))) (then (i32.const 2)) (else (i32.const 1))))))
          (if (i64.ne (i64.and (global.get $previous) (i64.const 2)) (i64.and (global.get $contacts) (i64.const 2)))
            (then (local.set $r (i32.or (local.get $r) (if (result i32) (i64.eqz (i64.and (global.get $contacts) (i64.const 2))) (then (i32.const 8)) (else (i32.const 4)))))))))
        (call $write (i32.add (i32.const 640) (local.get $p)) (i64.extend_i32_u (local.get $r)))
        (global.set $previous (global.get $contacts)) (global.set $samples (i64.add (global.get $samples) (i64.const 1)))
        (call $write (i32.const 768) (global.get $samples))
        (call $write (i32.const 769) (global.get $y))
        (call $write (i32.const 770) (global.get $vy))
        (call $write (i32.const 771) (global.get $facts))
        (call $write (i32.const 772) (global.get $contacts))
        (memory.copy (i32.const 0x5000) (i32.const 0x5001) (i32.const 127))
        (i32.store8 (i32.const 0x507f)
          (if (result i32) (i32.ne (local.get $r) (i32.const 0)) (then (i32.const 43)) (else
            (if (result i32) (i64.ne (i64.and (global.get $facts) (i64.const 1)) (i64.const 0)) (then (i32.const 95)) (else
              (if (result i32) (i64.gt_s (global.get $vy) (i64.const 0)) (then (i32.const 94)) (else (i32.const 118))))))))
        (call $text (i32.const 773) (i32.const 0x5000) (i32.const 128))))))

    (call $ask (i32.const 0) (i32.const 5) (i32.const 49152) (i32.const 5) (i64.const 2))
(call $read (i32.const 0) (i32.const 49164) (i32.const 7) (i32.const 17408))
(call $read (i32.const 0) (i32.const 49172) (i32.const 4) (i32.const 17416))
    (if (i64.ne (i64.load (i32.const 0x4400)) (i64.const 0)) (then
      (call $bodyQueries (i32.wrap_i64 (i64.load (i32.const 0x4408))))))
    (global.get $out))
  (data (i32.const 49152) "\73\63\6f\70\65\00")
  (data (i32.const 49164) "\65\6e\61\62\6c\65\64\00")
  (data (i32.const 49172) "\62\6f\64\79\00")

  (func $write (param $slot i32) (param $value i64)
    (drop (call $emit (i32.const 1) (i32.const 1) (local.get $slot) (i32.const 1) (i32.const 15)
      (local.get $value) (i64.const 0) (i64.const 0))))
  (func $text (param $slot i32) (param $ptr i32) (param $length i32)
    (drop (call $emit (i32.const 1) (i32.const 1) (local.get $slot) (i32.const 1) (i32.const 16)
      (i64.extend_i32_u (local.get $ptr)) (i64.extend_i32_u (local.get $length)) (i64.const 0))))

  ;; Cell declarations: row pointer, key pointer, UTF-8 lengths, reserved zero.

  (data (i32.const 0x14000) "\73\63\6f\70\65\48\65\69\67\68\74\00\73\63\6f\70\65\56\65\6c\6f\63\69\74\79\00\73\63\6f\70\65\46\61\63\74\73\00\73\63\6f\70\65\43\6f\6e\74\61\63\74\73\00\73\63\6f\70\65\54\69\63\6b\73\00\73\63\6f\70\65\45\76\65\6e\74\73\00\73\63\6f\70\65\00\73\61\6d\70\6c\65\73\00\68\65\69\67\68\74\00\76\65\6c\6f\63\69\74\79\00\66\61\63\74\73\00\63\6f\6e\74\61\63\74\73\00\73\63\6f\70\65\54\72\61\63\65\00\24\76\61\6c\75\65\00")
  (data (i32.const 0x14100) "\00\40\01\00\0b\00\00\00\0c\40\01\00\0d\00\00\00\1a\40\01\00\0a\00\00\00\25\40\01\00\0d\00\00\00\33\40\01\00\0a\00\00\00\3e\40\01\00\0b\00\00\00")
  (data (i32.const 0x13000) "\4a\40\01\00\50\40\01\00\05\00\07\00\00\00\00\00\4a\40\01\00\58\40\01\00\05\00\06\00\00\00\00\00\4a\40\01\00\5f\40\01\00\05\00\08\00\00\00\00\00\4a\40\01\00\68\40\01\00\05\00\05\00\00\00\00\00\4a\40\01\00\6e\40\01\00\05\00\08\00\00\00\00\00\77\40\01\00\82\40\01\00\0a\00\06\00\00\00\00\00")
  ;; Declare all six histories once. Keys are three decimal digits at most, in four-byte slots.
  (func (export "puck_init")
    (local $i i32) (local $row i32) (local $key i32) (local $length i32) (local $at i32)
    (loop $keys
      (local.set $key (i32.add (i32.const 0x14400) (i32.shl (local.get $i) (i32.const 2))))
      (local.set $length (i32.const 0))
      (if (i32.ge_u (local.get $i) (i32.const 100)) (then
        (i32.store8 (local.get $key) (i32.const 49)) (local.set $length (i32.const 1))))
      (if (i32.ge_u (local.get $i) (i32.const 10)) (then
        (i32.store8 (i32.add (local.get $key) (local.get $length))
          (i32.add (i32.const 48) (i32.rem_u (i32.div_u (local.get $i) (i32.const 10)) (i32.const 10))))
        (local.set $length (i32.add (local.get $length) (i32.const 1)))))
      (i32.store8 (i32.add (local.get $key) (local.get $length))
        (i32.add (i32.const 48) (i32.rem_u (local.get $i) (i32.const 10))))
      (local.set $length (i32.add (local.get $length) (i32.const 1)))
      (local.set $row (i32.const 0))
      (loop $rows
        (local.set $at (i32.add (i32.const 0x10000)
          (i32.shl (i32.add (i32.mul (local.get $row) (i32.const 128)) (local.get $i)) (i32.const 4))))
        (i32.store (local.get $at) (i32.load (i32.add (i32.const 0x14100) (i32.shl (local.get $row) (i32.const 3)))))
        (i32.store offset=4 (local.get $at) (local.get $key))
        (i32.store16 offset=8 (local.get $at) (i32.load offset=4 (i32.add (i32.const 0x14100) (i32.shl (local.get $row) (i32.const 3)))))
        (i32.store16 offset=10 (local.get $at) (local.get $length))
        (local.set $row (i32.add (local.get $row) (i32.const 1)))
        (br_if $rows (i32.lt_u (local.get $row) (i32.const 6))))
      (local.set $i (i32.add (local.get $i) (i32.const 1)))
      (br_if $keys (i32.lt_u (local.get $i) (i32.const 128)))))
)
