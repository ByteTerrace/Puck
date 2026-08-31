# Puck.AdvancedGamingBrick.Forge

Puck.AdvancedGamingBrick.Forge hand-authors AGB (GBA) cartridge ROMs in C#:
an ARM7TDMI Thumb assembler, a direct-boot cartridge builder, a minimal
polling kernel, and a worked-example cart that self-verifies on the real
`Puck.AdvancedGamingBrick` machine before its bytes are handed out. Output is
deterministic — the same build always yields byte-identical ROMs.

## Components

| Type | Role |
|---|---|
| `ThumbEmitter` | Thumb-1 assembler: labels, forward-reference fixups for the three branch families (`b<cond>`, `b`, `bl`), PC-relative literal pools, byte-exact output. |
| `ArmWords` | The few raw ARM words the forge needs: the header's entry branch and the two-instruction ARM→Thumb handoff. |
| `AgbForgeCartridge` | The 64 KiB direct-boot image: header branch, optional logo, title/game code, complement checksum, fixed code and data windows with size guards. |
| `AgbForgeKernel` | The polling kernel: V-blank sync, keypad edges, frame counter, the house PRNG, and a mode-3 rectangle fill. |
| `AgbForgeMemoryMap` / `AgbHw` | The EWRAM state layout and the hardware addresses the kernel touches. |
| `AgbVerifyMachineDriver` | Boots a forged ROM on a real machine (zeroed BIOS, direct boot) and exposes EWRAM/framebuffer/register observation plus scripted keypad input. |
| `Games/OrbChase*` | The worked example: title → START commits → D-pad chase of a PRNG-placed orb, with its inline verify battery. |

## The direct-boot constraint

Every forged cart targets `AdvancedGamingBrickMachine.DirectBoot` with a
zeroed 16 KiB `ReplacementBios`. Consequences, by construction:

- **No BIOS SWI calls.** Software interrupts would vector into zeroed BIOS
  ROM; the kernel never emits `swi`.
- **No IRQ dispatch.** The IRQ vector at `0x00000018` also sits in zeroed
  BIOS ROM, so interrupts stay unarmed (IME is never written) and the kernel
  is a polled main loop: V-blank sync watches VCOUNT, input is read from
  KEYINPUT once per frame.
- **The header logo is not validated.** `AgbForgeCartridge.Build` zeroes the
  156-byte logo field by default; this machine's direct boot never reads it.
  A retail BIOS boot on real hardware does validate the logo bitmap, so a
  caller targeting hardware must pass those bytes via the `logo` parameter.

## EWRAM state map

The kernel owns `0x02000000..0x0200003F`; a game owns
`AgbForgeMemoryMap.GameRam` (`0x02000040`) upward and the kernel never
touches it.

| Address | Width | Field |
|---|---|---|
| `0x02000000` | u32 | Frame counter (one increment per V-blank sync). |
| `0x02000004` | u16 | Held keys, active-high, KEYINPUT bit order. |
| `0x02000006` | u16 | Pressed edges this frame (`held & ~previous`). |
| `0x02000008` | u16 | Previous frame's held keys. |
| `0x0200000A` | u16 | PRNG state (16-bit LCG). |
| `0x0200000C` | u16 | Game state id. |
| `0x02000040` | — | Game-owned RAM. |

## PRNG doctrine

A 16-bit LCG (`state = state × 5 + 1`, output = the state's high byte),
seeded as `FrameCounter16 XOR 0xA5C3` at the title screen's START press
edge — input entropy only, never a wall clock. Two players pressing on the
same frame replay the same game.

## Verify discipline

A cart's `Build` runs its verify battery on a real emulated machine before
returning bytes (`OrbChaseVerify` is the model): boot with a zeroed BIOS,
step whole frames, press keys with a frame-counted settle
(`AgbVerifyMachineDriver` holds 4 frames and releases 4, so a polled kernel
sees exactly one pressed edge per press), and assert EWRAM values and
framebuffer pixels at each stage. The whole input script runs twice on fresh
machines and the observation streams must match exactly — the cross-run
determinism gate. Memory observation uses the bus's debug path, which never
advances the machine clock.

## Verification

```powershell
dotnet test tests/Puck.AdvancedGamingBrick.Forge.Tests -c Release
```

The tests build the example cart (running its inline verify), pin
build-to-build byte identity, and execute targeted emitter probes on the
real core — the emulator is the encoding oracle: when a probe disagrees
with the core, the emitter is wrong.
