;; GuidV4.wat

(module
  (memory (export "memory") 1)

  (func $pack32 (param $b0 i32) (param $b1 i32) (result i32)
    (local $nibbles i32)

    local.get $b0
    i32.const 240
    i32.and
    i32.const 4
    i32.shr_u

    local.get $b0
    i32.const 15
    i32.and
    i32.const 8
    i32.shl
    i32.or

    local.get $b1
    i32.const 240
    i32.and
    i32.const 12
    i32.shl
    i32.or

    local.get $b1
    i32.const 15
    i32.and
    i32.const 24
    i32.shl
    i32.or
    local.set $nibbles

    local.get $nibbles
    i32.const 0x06060606
    i32.add
    i32.const 4
    i32.shr_u
    i32.const 0x01010101
    i32.and
    i32.const 39
    i32.mul

    local.get $nibbles
    i32.add
    i32.const 0x30303030
    i32.add
  )
  (func $pack64 (param $b0 i32) (param $b1 i32) (param $b2 i32) (param $b3 i32) (result i64)
    local.get $b0
    local.get $b1
    call $pack32
    i64.extend_i32_u

    local.get $b2
    local.get $b3
    call $pack32
    i64.extend_i32_u
    i64.const 32
    i64.shl

    i64.or
  )

  ;; Equivalent to your TypeScript toString() over internal w/x/y/z payload words.
  ;;
  ;; Writes 36 UUID characters plus a trailing NUL byte at out + 36.
  ;;
  ;; Signature:
  ;;   format_payload(out, w, x, y, z) -> out
  ;;
  ;; UUID layout:
  ;;   wwwwwwww-xxxx-4xxx-[8|9|a|b]xxx-xxxxxxxxxxxx
  (func $format_payload (export "format_payload")
    (param $out i32)
    (param $w i32)
    (param $x i32)
    (param $y i32)
    (param $z i32)
    (result i32)

    ;; out+0..7: w as 8 hex chars
    local.get $out
    local.get $w
    i32.const 24
    i32.shr_u
    local.get $w
    i32.const 16
    i32.shr_u
    local.get $w
    i32.const 8
    i32.shr_u
    local.get $w
    call $pack64
    i64.store align=1

    ;; out+8: '-'
    local.get $out
    i32.const 45
    i32.store8 offset=8

    ;; out+9..12: x[31..16] as 4 hex chars
    local.get $out
    local.get $x
    i32.const 24
    i32.shr_u
    local.get $x
    i32.const 16
    i32.shr_u
    call $pack32
    i32.store offset=9 align=1

    ;; out+13: '-'
    local.get $out
    i32.const 45
    i32.store8 offset=13

    ;; out+14..17: version nibble + x[11..4]
    ;;   byte A = 0x40 | ((x >>> 12) & 0x0F)   version nibble forced to '4'
    ;;   byte B = (x >>> 4) & 0xFF
    local.get $out
    local.get $x
    i32.const 12
    i32.shr_u
    i32.const 15
    i32.and
    i32.const 64
    i32.or
    local.get $x
    i32.const 4
    i32.shr_u
    call $pack32
    i32.store offset=14 align=1

    ;; out+18: '-'
    local.get $out
    i32.const 45
    i32.store8 offset=18

    ;; out+19..22: variant nibble + x[3..0] + y[31..22]
    ;;   byte A = 0x80 | ((x & 0x0F) << 2) | (y >>> 30)   variant bits forced to '10'
    ;;   byte B = (y >>> 22) & 0xFF
    local.get $out
    local.get $x
    i32.const 15
    i32.and
    i32.const 2
    i32.shl
    local.get $y
    i32.const 30
    i32.shr_u
    i32.or
    i32.const 128
    i32.or
    local.get $y
    i32.const 22
    i32.shr_u
    call $pack32
    i32.store offset=19 align=1

    ;; out+23: '-'
    local.get $out
    i32.const 45
    i32.store8 offset=23

    ;; out+24..31: y[21..6] + y/z boundary byte + z[23..16]
    ;;   byte A = (y >>> 14) & 0xFF
    ;;   byte B = (y >>> 6)  & 0xFF
    ;;   byte C = ((y & 0x3F) << 2) | ((z >>> 24) & 0x03)
    ;;   byte D = (z >>> 16) & 0xFF
    local.get $out
    local.get $y
    i32.const 14
    i32.shr_u
    local.get $y
    i32.const 6
    i32.shr_u
    local.get $y
    i32.const 63
    i32.and
    i32.const 2
    i32.shl
    local.get $z
    i32.const 24
    i32.shr_u
    i32.const 3
    i32.and
    i32.or
    local.get $z
    i32.const 16
    i32.shr_u
    call $pack64
    i64.store offset=24 align=1

    ;; out+32..35: z[15..0] as 4 hex chars
    local.get $out
    local.get $z
    i32.const 8
    i32.shr_u
    local.get $z
    call $pack32
    i32.store offset=32 align=1

    ;; out+36: trailing NUL
    local.get $out
    i32.const 0
    i32.store8 offset=36

    local.get $out
  )

  ;; Equivalent to GuidV4.new(value).toString(), where the original bigint is
  ;; passed as four big-endian u32 limbs:
  ;;
  ;;   value = p0:p1:p2:p3
  ;;
  ;; For a valid 122-bit value, p0 must be <= 0x03ffffff.
  ;;
  ;; This performs the same logical packing as:
  ;;
  ;;   value = value << 6n
  ;;   w = Number((value >> 96n) & 0xffffffffn)
  ;;   x = Number((value >> 64n) & 0xffffffffn)
  ;;   y = Number((value >> 32n) & 0xffffffffn)
  ;;   z = Number((value >>  6n) & 0xffffffffn)
  ;;
  ;; which simplifies to:
  ;;
  ;;   w = (p0 << 6) | (p1 >>> 26)
  ;;   x = (p1 << 6) | (p2 >>> 26)
  ;;   y = (p2 << 6) | (p3 >>> 26)
  ;;   z = p3
  (func (export "format_from_u128")
    (param $out i32)
    (param $p0 i32)
    (param $p1 i32)
    (param $p2 i32)
    (param $p3 i32)
    (result i32)

    (local $w i32)
    (local $x i32)
    (local $y i32)

    local.get $p0
    i32.const 6
    i32.shl
    local.get $p1
    i32.const 26
    i32.shr_u
    i32.or
    local.set $w

    local.get $p1
    i32.const 6
    i32.shl
    local.get $p2
    i32.const 26
    i32.shr_u
    i32.or
    local.set $x

    local.get $p2
    i32.const 6
    i32.shl
    local.get $p3
    i32.const 26
    i32.shr_u
    i32.or
    local.set $y

    local.get $out
    local.get $w
    local.get $x
    local.get $y
    local.get $p3
    call $format_payload
  )
)
