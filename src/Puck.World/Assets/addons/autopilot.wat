;; autopilot.wat — Puck.World's addon-as-principal keystone proof (puck.addon.v1).
;;
;; The smallest honest driver: every tick it emits ONE constant PadMove record, so the body it is granted Drive over
;; walks a steady diagonal (forward along its facing + strafe along its right). No geometry, no state, no math beyond a
;; single-record write — the point is not the behavior but the PRINCIPAL: an addon drives a body through the SAME
;; IServerLink as a human seat, and the server's grant table gates it (world.grant → it moves; world.revoke → the
;; server drops its intents and the body idles).
;;
;; Every value is FixedQ4816 raw i64 bits (One = 0x1_0000 = 65536); no floating point crosses the ABI. The host
;; translates PadMove's valueX -> MoveStrafe and valueY -> MoveForward in the intent's own frame (WorldAddonDriver).
;;
;; ABI regions in this module's one 64 KiB page:
;;   snapshot region @ 0   (40 bytes; host writes tick@0, posX@8, posY@16, posZ@24, buttons@32 — unread here)
;;   commands region @ 64  (cap 1 record * 24 bytes = [64, 88))
;; A command record (24 bytes): padId u16@0, phase u8@2, reserved0 u8@3, reserved1 u32@4, valueX i64@8, valueY i64@16.
;; PadMove = 0 (Axis2D). Phase Active = 1. 0.55 * 65536 = 36045 (forward), 0.35 * 65536 = 22938 (strafe).
(module
  (memory (export "memory") 1)

  ;; The four pure constant getters the host caches once at instantiation.
  (func (export "puck_abi_version") (result i32) (i32.const 1))
  (func (export "puck_snapshot_ptr") (result i32) (i32.const 0))
  (func (export "puck_commands_ptr") (result i32) (i32.const 64))
  (func (export "puck_commands_cap") (result i32) (i32.const 1))

  ;; Emit one constant PadMove every tick: a steady forward+strafe walk.
  (func (export "puck_on_tick") (result i32)
    (i32.store16 (i32.const 64) (i32.const 0))       ;; padId = PadMove
    (i32.store8  (i32.const 66) (i32.const 1))       ;; phase = Active
    (i32.store8  (i32.const 67) (i32.const 0))       ;; reserved0 (must be 0)
    (i32.store   (i32.const 68) (i32.const 0))       ;; reserved1 (must be 0)
    (i64.store   (i32.const 72) (i64.const 22938))   ;; valueX = strafe (+0.35)
    (i64.store   (i32.const 80) (i64.const 36045))   ;; valueY = forward (+0.55)
    (i32.const 1))
)
