# Puck.AdvancedGamingBrick.Forge

`AgbCartridgeCompiler` compiles `puck.cartridge.v1` data into a native AGB ROM.
Players author that data in Puck's console with `forge.new`, `forge.set`,
`forge.check`, `forge.build`, and `forge.play`, or point a world screen's
`contentPath` at a cartridge source document, which `Puck.World.Machines`
prepares through the neutral content-provider contract at bind. See the shared
[cartridge authoring guide](../Puck.GamingBricks.Forge/README.md) for the
complete schema, command walkthrough, limits, and embedding contract.

The package (`ByteTerrace.Puck.AdvancedGamingBrick.Forge`) depends on the
shared document package and the AGB emulator, with no World dependency.
There are no embedded games or environment-variable settings.

## Native backend

The compiler emits Thumb-1 rules and mode-0 tile graphics. A 64 KiB image
contains code at `0x080000C8`, data at `0x0800C000`, the ARM-to-Thumb entry
stub, title/game code and complement checksum. Variables occupy bytes from
`0x02000040`; compilation returns their exact names and addresses. The
kernel's frame counter and input state occupy `0x02000000..0x0200003F`.
The background is a 32×32 map with packed 4bpp tiles and RGB555 palettes;
objects are 8×8 sprites, or 8×16 with `tallSprites`. Byte arithmetic wraps modulo 256;
wide assignment, addition and subtraction wrap modulo 65536. Wide variables are aligned
and placed before byte variables; saved variables retain both bytes.

`ThumbEmitter`, `ArmWords`, `AgbForgeCartridge`, `AgbForgeKernel`, `AgbHw`,
and `AgbForgeMemoryMap` remain available for lower-level native tooling.
The polling kernel also retains its rectangle and deterministic PRNG helpers.
The document compiler emits no built-in gameplay and uses neither helper.

## Boot and firmware ownership

The document compiler emits no SWIs, IRQ dispatch, or BIOS dependency.
`forge.play` mounts canonical source through the content provider and uses the
engine's bundled cold-boot default. Explicit fast boot retains firmware services.
The default header logo is zeroed; the Puck firmware can start this native image.
A caller building for a retail BIOS/hardware boot can supply the required
logo through `AgbForgeCartridge.Build`'s `logo` parameter; the document
compiler does not claim retail-boot compatibility. No BIOS ships in a ROM.

## Verification

```powershell
dotnet test tests/Puck.AdvancedGamingBrick.Forge.Tests -c Release
```

The tests compile authored documents, compare byte identity after a JSON
round-trip, and execute input, arithmetic, comparison and graphics behavior
on both native emulators. Separate emitter probes assert instruction effects.
`AgbVerifyMachineDriver` provides frame-counted input and clock-free bus
observations. Compilation validates and emits; it does not pretend to prove
an arbitrary player's game correct or run a hidden sample-game verifier.
