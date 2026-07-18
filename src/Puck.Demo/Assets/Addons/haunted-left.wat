;; haunted-left.wat — the west-wing ghost of the haunted arcade (puck.addon.v1).
;;
;; The autopilot ghost's sibling: identical dead-reckoning brain, different haunt. It drives its
;; exclusive roster slot to the LEFT cabinet and boots it. With three consoles the stands sit 5 units
;; apart along the -Z wall (spacing 5.0, firstX = -5): left at x = -5, z = -6.6. Everything else —
;; the sub+clamp walk, the one-shot North latch, the no-float fixed-point ABI — matches
;; autopilot-ghost.wat line for line; only the target constant differs.
;;
;; Targets in FixedQ4816 raw (One = 65536): TX = -5.0 -> -327680; TZ = -6.6 -> -432538.
;;
;; ABI regions in this module's linear memory (one 64 KiB page):
;;   snapshot region  @ 0   (40 bytes; host writes: tick@0, posX@8, posY@16, posZ@24, buttons@32, rsvd@36)
;;   commands region  @ 64  (cap 4 records * 24 bytes = 96 bytes -> [64, 160), inside page 0)
(module
  (memory (export "memory") 1)

  ;; The one-shot interact latch: 0 until the single PadNorth Started edge fires, then 1 forever
  ;; (a fresh Store on `addon enable`/`addon reload` resets it — a clean, deterministic restart).
  (global $booted (mut i32) (i32.const 0))

  ;; The four pure constant getters the host caches once at instantiation.
  (func (export "puck_abi_version") (result i32) (i32.const 1))
  (func (export "puck_snapshot_ptr") (result i32) (i32.const 0))
  (func (export "puck_commands_ptr") (result i32) (i32.const 64))
  (func (export "puck_commands_cap") (result i32) (i32.const 4))

  ;; clamp(v, -One, +One) — pure compares, no multiply.
  (func $clamp (param $v i64) (result i64)
    (if (result i64) (i64.gt_s (local.get $v) (i64.const 65536))
      (then (i64.const 65536))
      (else
        (if (result i64) (i64.lt_s (local.get $v) (i64.const -65536))
          (then (i64.const -65536))
          (else (local.get $v))))))

  ;; abs(v) — sub only.
  (func $abs (param $v i64) (result i64)
    (if (result i64) (i64.lt_s (local.get $v) (i64.const 0))
      (then (i64.sub (i64.const 0) (local.get $v)))
      (else (local.get $v))))

  ;; The per-tick body: clamp-walk toward the left cabinet (-5, -6.6), then one PadNorth edge.
  (func (export "puck_on_tick") (result i32)
    (local $posX i64)
    (local $posZ i64)
    (local $remX i64)
    (local $remZ i64)

    (local.set $posX (i64.load (i32.const 8)))    ;; snapshot posLocalX (strafe axis)
    (local.set $posZ (i64.load (i32.const 24)))   ;; snapshot posLocalZ (forward axis; cabinets at -Z)

    ;; Remaining vector toward the target (TX = -327680, TZ = -432538).
    (local.set $remX (i64.sub (i64.const -327680) (local.get $posX)))
    (local.set $remZ (i64.sub (i64.const -432538) (local.get $posZ)))

    ;; Record 0 @ 64: PadMove (id 0, Axis2D), phase Active (1). valueX = clamp(remX), valueY = clamp(remZ).
    (i32.store16 (i32.const 64) (i32.const 0))    ;; padId = PadMove
    (i32.store8  (i32.const 66) (i32.const 1))    ;; phase = Active
    (i32.store8  (i32.const 67) (i32.const 0))    ;; reserved0 (must be 0)
    (i32.store   (i32.const 68) (i32.const 0))    ;; reserved1 (must be 0)
    (i64.store   (i32.const 72) (call $clamp (local.get $remX)))   ;; valueX
    (i64.store   (i32.const 80) (call $clamp (local.get $remZ)))   ;; valueY

    ;; Within the interact range (~1.8 units -> 117965 raw) on BOTH axes and not yet booted?
    ;; Fire ONE PadNorth Started edge — the nearest console to the body is then the left stand.
    (if
      (i32.and
        (i32.and
          (i64.lt_s (call $abs (local.get $remX)) (i64.const 117965))
          (i64.lt_s (call $abs (local.get $remZ)) (i64.const 117965)))
        (i32.eqz (global.get $booted)))
      (then
        ;; Record 1 @ 88: PadNorth (id 4, Digital), phase Started (0). valueX = One (pressed).
        (i32.store16 (i32.const 88) (i32.const 4))     ;; padId = PadNorth
        (i32.store8  (i32.const 90) (i32.const 0))     ;; phase = Started
        (i32.store8  (i32.const 91) (i32.const 0))     ;; reserved0
        (i32.store   (i32.const 92) (i32.const 0))     ;; reserved1
        (i64.store   (i32.const 96) (i64.const 65536)) ;; valueX = One (pressed)
        (i64.store   (i32.const 104) (i64.const 0))    ;; valueY = 0 (Digital: must be 0)
        (global.set $booted (i32.const 1))
        (return (i32.const 2))))

    ;; No interact this tick: just the one PadMove record.
    (i32.const 1))
)
