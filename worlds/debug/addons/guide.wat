;; The gallery guide reads the authored guideExhibits row through Observe/State and ReadStateText.
;; Region events index its <ordinal>-title and <ordinal>-note cells in guide.puck's walking order.
;; Both texts finish before publishing guide/guideAt; an exit clears them, and a crossing discards old chunks.
;; Memory: descriptors 0x100, rings 0x1000/0x2000, literals 0x6000, keys 0x6480/0x6500,
;; readers 0x7000/0x7010, text buffers 0x8000/0xa000 (4096 bytes plus one chunk),
;; JSON payloads 0x10000 and 0x5400. Text over 4096 UTF-8 bytes or an encoded row over 8 KiB faults loudly.
(module
  (memory (export "memory") 2)

  ;; Request through ReadStateText (verb 6), Response.
  (data (i32.const 0x0100)
    "\02\00\07\00\00\00\00\00\00\00\00\00\00\00\00\00"
    "\03\00\00\00\00\00\00\00\00\00\00\00\00\00\00\00")

  (data (i32.const 0x6000) "State\00")
  (data (i32.const 0x6080) "{\"name\":\"guide\",\"kind\":\"text\",\"cells\":[{\"key\":\"title\",\"value\":\"\00")
  (data (i32.const 0x6100) "\"},{\"key\":\"note\",\"value\":\"\00")
  (data (i32.const 0x6180) "\"}]}\00")
  (data (i32.const 0x6200) "{\"name\":\"guideAt\",\"kind\":\"int\",\"value\":\00")
  (data (i32.const 0x6280) "}\00")

  (data (i32.const 0x6300) "guideExhibits\00")
  (data (i32.const 0x6380) "-title\00")
  (data (i32.const 0x6400) "-note\00")
  (data (i32.const 0x7000) "\ff\ff\ff\ff")
  (data (i32.const 0x7010) "\ff\ff\ff\ff")

  (global $observe (mut i32) (i32.const 0))
  (global $observeIndex (mut i32) (i32.const 0))
  (global $observeGeneration (mut i32) (i32.const 0))
  (global $askedObserve (mut i32) (i32.const -1))
  (global $loading (mut i32) (i32.const 0))
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


  ;; JSON-escape UTF-8 bytes without decoding them. Control bytes use \u00xx; multi-byte scalars stay intact.
  (func $byte (param $value i32)
    (i32.store8 (global.get $cursor) (local.get $value))
    (global.set $cursor (i32.add (global.get $cursor) (i32.const 1))))
  (func $hex (param $value i32) (result i32)
    (i32.add (local.get $value) (select (i32.const 48) (i32.const 87) (i32.lt_u (local.get $value) (i32.const 10)))))
  (func $quoted (param $at i32)
    (local $v i32)
    (block $end (loop $next
      (local.set $v (i32.load8_u (local.get $at)))
      (br_if $end (i32.eqz (local.get $v)))
      (if (i32.lt_u (local.get $v) (i32.const 32))
        (then
          (call $byte (i32.const 92)) (call $byte (i32.const 117))
          (call $byte (i32.const 48)) (call $byte (i32.const 48))
          (call $byte (call $hex (i32.shr_u (local.get $v) (i32.const 4))))
          (call $byte (call $hex (i32.and (local.get $v) (i32.const 15)))))
        (else
          (if (i32.or (i32.eq (local.get $v) (i32.const 34)) (i32.eq (local.get $v) (i32.const 92)))
            (then (call $byte (i32.const 92))))
          (call $byte (local.get $v))))
      (local.set $at (i32.add (local.get $at) (i32.const 1)))
      (br $next))))
  (func $length (param $at i32) (result i32)
    (local $n i32)
    (block $end (loop $next
      (br_if $end (i32.eqz (i32.load8_u (i32.add (local.get $at) (local.get $n)))))
      (local.set $n (i32.add (local.get $n) (i32.const 1))) (br $next)))
    (local.get $n))
  ;; Decimal cell keys have no fixed exhibit-count ceiling.
  (func $number (param $v i64)
    (if (i64.ge_u (local.get $v) (i64.const 10))
      (then (call $number (i64.div_u (local.get $v) (i64.const 10)))))
    (call $byte (i32.add (i32.const 48) (i32.wrap_i64 (i64.rem_u (local.get $v) (i64.const 10))))))
  ;; Each reader stores its pending request ordinal, byte offset and done flag in 12 bytes.
  (func $chunk (param $reader i32) (param $cell i32) (param $buffer i32)
    (local $offset i32) (local $n i32) (local $at i32) (local $verdict i32)
    (i32.store (local.get $reader) (i32.const -1))
    (local.set $verdict (i32.load8_u offset=8 (local.get $cell)))
    (if (i32.ne (local.get $verdict) (i32.const 10))
      (then
        (global.set $observe (i32.const 0))
        (global.set $dirty (i32.const 1))
        (return)))
    (local.set $offset (i32.load offset=4 (local.get $reader)))
    (local.set $at (i32.add (local.get $buffer) (local.get $offset)))
    (i64.store (local.get $at) (i64.load offset=16 (local.get $cell)))
    (i64.store offset=8 (local.get $at) (i64.load offset=24 (local.get $cell)))
    (block $end (loop $next
      (br_if $end (i32.eq (local.get $n) (i32.const 16)))
      (if (i32.eqz (i32.load8_u (i32.add (local.get $at) (local.get $n))))
        (then (i32.store offset=8 (local.get $reader) (i32.const 1)) (br $end)))
      (local.set $n (i32.add (local.get $n) (i32.const 1))) (br $next)))
    (local.set $offset (i32.add (local.get $offset) (local.get $n)))
    ;; Refuse oversize text loudly instead of truncating it or emitting a split UTF-8 scalar.
    (if (i32.gt_u (local.get $offset) (i32.const 4096)) (then unreachable))
    (i32.store offset=4 (local.get $reader) (local.get $offset)))
  (func $read (param $reader i32) (param $key i32)
    (if (i32.and (i32.eqz (i32.load offset=8 (local.get $reader)))
          (i32.lt_s (i32.load (local.get $reader)) (i32.const 0)))
      (then
        (i32.store (local.get $reader) (call $emit (i32.const 1)
          (global.get $observeIndex) (global.get $observeGeneration) (i32.const 6)
          (i64.extend_i32_u (local.get $key)) (i64.extend_i32_u (call $length (local.get $key)))
          (i64.extend_i32_u (i32.load offset=4 (local.get $reader))))))))

  ;; Submits a payload as one UpsertStateRow (mutation kind 44) through the Mutate handle.
  (func $upsert (param $at i32)
    (if (i32.gt_u (i32.sub (global.get $cursor) (local.get $at)) (i32.const 8192)) (then unreachable))
    (drop (call $emit (i32.const 1) (global.get $mutateIndex) (global.get $mutateGeneration) (i32.const 1)
      (i64.const 44) (i64.extend_i32_u (local.get $at)) (i64.extend_i32_u (i32.sub (global.get $cursor) (local.get $at))))))
  ;; Writes both whole rows: `guide`, the shown exhibit's title and note or two empty cells, and `guideAt`, its
  ;; position in the table or -1.
  (func $publish
    (global.set $cursor (i32.const 0x10000))
    (drop (call $puts (i32.const 0x6080)))
    (if (i64.ge_s (global.get $shown) (i64.const 0))
      (then
        (call $quoted (i32.const 0x8000))
        (drop (call $puts (i32.const 0x6100)))
        (call $quoted (i32.const 0xa000)))
      (else (drop (call $puts (i32.const 0x6100)))))
    (drop (call $puts (i32.const 0x6180)))
    (call $upsert (i32.const 0x10000))
    (global.set $cursor (i32.const 0x5400))
    (drop (call $puts (i32.const 0x6200)))
    (if (i64.lt_s (global.get $shown) (i64.const 0))
      (then
        (i32.store8 (global.get $cursor) (i32.const 45))
        (i32.store8 offset=1 (global.get $cursor) (i32.const 49))
        (global.set $cursor (i32.add (global.get $cursor) (i32.const 2))))
      (else (call $number (global.get $shown))))
    (drop (call $puts (i32.const 0x6280)))
    (call $upsert (i32.const 0x5400))
    (global.set $loading (i32.const 0)))

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

        (if (i32.eq (local.get $kind) (i32.const 2))
          (then
            (if (i32.eq (i32.load16_u offset=2 (local.get $cell)) (i32.load (i32.const 0x7000)))
              (then (call $chunk (i32.const 0x7000) (local.get $cell) (i32.const 0x8000))))
            (if (i32.eq (i32.load16_u offset=2 (local.get $cell)) (i32.load (i32.const 0x7010)))
              (then (call $chunk (i32.const 0x7010) (local.get $cell) (i32.const 0xa000))))
            (if (i32.eq (i32.load16_u offset=2 (local.get $cell)) (global.get $askedObserve))
              (then
                (global.set $askedObserve (i32.const -1))
                (local.set $verdict (i32.load8_u offset=8 (local.get $cell)))
                (if (i32.and (i32.ge_u (local.get $verdict) (i32.const 1)) (i32.le_u (local.get $verdict) (i32.const 3)))
                  (then
                    (global.set $observe (i32.const 1))
                    (global.set $observeIndex (i32.load16_u offset=4 (local.get $cell)))
                    (global.set $observeGeneration (i32.load16_u offset=6 (local.get $cell)))))))))
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

    (if (i32.and (i32.eqz (global.get $observe)) (i32.lt_s (global.get $askedObserve) (i32.const 0)))
      (then
        ;; State (kind 5), Observe (bit 2), exact authored row name.
        (global.set $askedObserve (call $emit (i32.const 2) (i32.const 0) (i32.const 0) (i32.const 5)
          (i64.const 0x6300) (i64.const 2) (i64.const 13)))))
    ;; All old answers were drained before switching keys, so crossing during a read cannot mix two exhibits.
    (if (global.get $dirty)
      (then
        (global.set $dirty (i32.const 0)) (global.set $loading (i32.const 1))
        (i32.store (i32.const 0x7000) (i32.const -1)) (i32.store (i32.const 0x7010) (i32.const -1))
        (i32.store offset=4 (i32.const 0x7000) (i32.const 0)) (i32.store offset=4 (i32.const 0x7010) (i32.const 0))
        (i32.store offset=8 (i32.const 0x7000) (i64.lt_s (global.get $shown) (i64.const 0)))
        (i32.store offset=8 (i32.const 0x7010) (i64.lt_s (global.get $shown) (i64.const 0)))
        (i32.store8 (i32.const 0x8000) (i32.const 0)) (i32.store8 (i32.const 0xa000) (i32.const 0))
        (if (i64.ge_s (global.get $shown) (i64.const 0))
          (then
            (global.set $cursor (i32.const 0x6480)) (call $number (global.get $shown))
            (drop (call $puts (i32.const 0x6380))) (call $byte (i32.const 0))
            (global.set $cursor (i32.const 0x6500)) (call $number (global.get $shown))
            (drop (call $puts (i32.const 0x6400))) (call $byte (i32.const 0))))))
    (if (global.get $loading)
      (then
        (if (i32.and (i32.load offset=8 (i32.const 0x7000)) (i32.load offset=8 (i32.const 0x7010)))
          (then (if (global.get $mutate) (then (call $publish))))
          (else (if (global.get $observe)
            (then (call $read (i32.const 0x7000) (i32.const 0x6480))
                  (call $read (i32.const 0x7010) (i32.const 0x6500))))))))
    (global.get $out))
)
