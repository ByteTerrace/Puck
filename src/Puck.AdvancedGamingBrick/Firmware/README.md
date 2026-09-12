# Puck native AGB firmware

This folder owns the source for the bundled 16 KiB ByteTerrace/Puck BIOS.
Its ARM exception handlers and Thumb service code execute on the same emulated
ARM7TDMI as a cartridge. There is no host-side SWI replacement and no dependency
on a retail BIOS image. The firmware source, original pixel lettering and chime,
and generated image are separately available under the [MIT license](LICENSE).

Address zero is the cold reset vector. Address `0x20` is the Puck fast-boot
entry: it follows the same native initialization and register handoff, omitting
only the visible wordmark and sound. Both entries retain the runtime SWI and
IRQ handlers. The firmware does not compare a cartridge's logo with a publisher
bitmap; branding is presentation, not cartridge admission or copy protection.

## Rebuilding

The repository command is `puck firmware agb`. For example, from the repository
root with LLVM 19.1.6 installed:

```powershell
dotnet src/Puck.Cli/publish/Puck.Cli.dll firmware agb --source src/Puck.AdvancedGamingBrick/Firmware --output src/Puck.AdvancedGamingBrick/Firmware/puck-agb.bin --clang "C:/Program Files/LLVM/bin/clang.exe" --linker "C:/Program Files/LLVM/bin/ld.lld.exe" --verify
```

Omit `--verify` to regenerate the image. Verification compiles independently and
compares all bytes; it does not overwrite the checked-in image. Builds make no
network requests and need no Nintendo SDK, libc, or compiler support library.
The linker rejects mutable ROM globals and payloads larger than 16 KiB, then
pads the image to exactly 16,384 bytes.

The source order is `entrypoint.s`, `boot.c`, `services.c`, `codec.c`, `math.c`,
`sound.c`, and `multiboot.c`. The assembly targets `armv4t-none-eabi`,
`arm7tdmi`, ARM state. The C files use the same target in Thumb state, C11,
`-Oz -ffreestanding -fno-builtin -fno-unwind-tables
-fno-asynchronous-unwind-tables -fomit-frame-pointer -Wall -Wextra -Werror`.
`firmware.ld` places the vector section first at address zero; LLD emits the
flat binary directly with `--oformat=binary`. `puck.h` is the shared native
interface, not a package API.

## Provenance and compatibility scope

The exception/IRQ foundation was adapted from the MIT-licensed
[Cult-of-GBA/BIOS](https://github.com/Cult-of-GBA/BIOS/tree/a30e9a96df083628b650724b7d4d7112b4070b98)
at commit `a30e9a96df083628b650724b7d4d7112b4070b98`; its copyright notice is
retained. The C implementations are original code using the public contracts
in [GBATEK](https://mgba-emu.github.io/gbatek/#biosfunctions), the independently
documented [M4A song format](https://github.com/ipatix/m4a2s/blob/master/sappy%20(by%20Bregalad).txt),
and player-management facts from the [GBA BIOS Reference](https://retrointernals.github.io/gba-bios-reference/).
Ambiguous legacy layouts are checked by running caller-authored fixtures against
an explicitly supplied retail image and inspecting returned registers and RAM;
the harness reports only returned values and caller-owned memory. It neither
exports the retail image nor disassembles its instructions.
The ArcTan polynomial coefficients are mathematical compatibility data
documented by that MIT implementation. Sine and equal-tempered pitch tables
are generated from their formulas, not extracted from ROM bytes.

No upstream boot artwork is used. Upstream files explicitly described as
retail-BIOS decompilations (audio, BitUnpack, and Huffman) were not imported.
No GPL emibios code or Nintendo firmware image is included. This is a source
provenance statement, not a legal determination about every distribution.

This replacement does not claim retail instruction timing or undocumented
register/flag parity. The SoundArea PCM driver and native music sequencer are
independent implementations. Legacy SWIs `0x20..0x24`, common exported track
commands, and first-tick initialization have black-box RAM-contract coverage;
all 36 exported slots have native handlers, including termination for reserved
commands. Fixed-rate stereo PCM has 96-sample stereo and channel-state fixtures.
Additional independent fixtures cover six resampling ratios, all twelve sample
rates, reverb inputs, mixer overflow, gate/release behavior, fade dynamics,
modulation, drum pitch/pan overrides, pseudo-echo, and installed PSG extension
callbacks for all four oscillator types. PSG synthesis belongs to those
cartridge-installed extension callbacks; it is not a bundled copy of a
cartridge sound library. Guard fixtures verify native Main/Mode/Clear locking,
nested-call refusal, VSync decisions and queued-PCM preservation. Sequencer
fixtures exercise branching, nested patterns, repeats, finite and tied notes,
and sample-loop boundaries. These are bounded contracts, not a
claim of exhaustive sequencer, PCM or instruction-timing parity. The native
MultiBoot sender and cold-boot
receiver follow the documented link protocol. The receiver is selected when
START and SELECT are held together or no cartridge is detected. A native
peer fixture verifies every modeled wire exchange, encrypted payload and native
downloaded-program handoff in both normal speeds and multiplayer with one or
three children. Multiplayer fixtures also check checksum rejection, empty
cartridge entry, suspended-link replay and repeated reconnection. This proves
those protocol paths, not electrical link timing or every commercial client.
The checksum SWI returns the documented compatibility value; identify this
firmware by its bytes, never by that SWI result.

Source reproducibility establishes which firmware was built. It does not
establish hardware accuracy. Durable service, IRQ, cold/fast boot, frame, and
PCM checks belong in the [Advanced Post battery](../../Puck.AdvancedGamingBrick.Post/README.md),
with independent expected values and explicit asset-gated skips.
