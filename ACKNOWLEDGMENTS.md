# Acknowledgments

Puck is written from scratch, but very little of it was invented here. This file credits the research, techniques, fonts, and tools the engine is built on.

## Techniques and Research

- **Inigo Quilez** — the foundation of [everything related to SDFs](https://iquilezles.org/) in this engine.
- **John C. Hart** — *Sphere Tracing: A Geometric Method for the Antialiased Ray Tracing of Implicit Surfaces* (The Visual Computer, 1996). The core marching loop, and the Lipschitz-bound reasoning behind every "safe step" in the compute kernels.
- **Cone/beam marching** — the per-tile conservative beam pre-pass descends from cone marching as popularized in the demoscene (notably by Fulcrum), with *Enhanced Sphere Tracing* (Keinert et al., 2014) as the broader treatment of accelerated sphere-trace stepping.
- **Tiled work culling** — the per-tile creature binning in the tile-culling render tier follows the screen-space binning lineage of tiled deferred shading (Johan Andersson, DICE, SIGGRAPH 2009; Ola Olsson & Ulf Assarsson, 2011).
- **Viktor Chlumský** — multi-channel signed distance field (MSDF) text rendering, from his [msdfgen](https://github.com/Chlumsky/msdfgen) work and master's thesis (*Shape Decomposition for Multi-channel Distance Fields*, CTU Prague, 2015). Used by the terminal's glyph shading.
- **Ken Perlin** — gradient noise (*An Image Synthesizer*, SIGGRAPH 1985) and its quintic interpolant (*Improving Noise*, SIGGRAPH 2002). The procedural-noise stack is gradient noise with the 6t⁵−15t⁴+10t³ fade, sourcing its gradients from a sine-free hash rather than a permutation table.
- **Dave Hoskins** — the "Hash without Sine" family of integer-free GPU hashes ([Shadertoy 4djSRW](https://www.shadertoy.com/view/4djSRW)). Every source of randomness in the shaders routes through these so captures stay bit-identical across hardware — `fract(sin(x) * k)` constructions have no cross-GPU precision guarantee.
- **Martijn Steinrucken** (BigWings / "The Art of Code") — the rain-on-glass drop and trail height field, with refraction driven by the height-field gradient, from *Heartfelt* ([Shadertoy ltffzl](https://www.shadertoy.com/view/ltffzl)).
- **Sebastian Aaltonen** — the closest-approach refinement for soft-shadow sphere marching (fitting a parabola between consecutive samples to recover the ray's true closest pass), used by the world kernel's penumbra march.
- **SignedDistanceTerminal** — the MSDF glyph texel sampling/decode construction (`sdfMsdfGlyph`) adapted for the SDF VM's glyph-atlas shading.
- **Pedro Felzenszwalb & Daniel Huttenlocher** — *Distance Transforms of Sampled Functions* (Theory of Computing, 2012): the separable O(n) lower-envelope distance transform behind the coverage-atlas bake in `Puck.Text`.
- **Jeffrey Hurchalla** — the Newton–Hensel doubling iteration for integer multiplicative inverses modulo 2^w, used by `Puck.Maths`.
- **Michael Wichura** — *Algorithm AS 241: The Percentage Points of the Normal Distribution* (Applied Statistics, 1988): the minimax rational approximations behind the normal quantile in `Puck.Maths`.
- **ThayirSadamLibrary** ([favre49](https://github.com/favre49/ThayirSadamLibrary)) — the sublinear prime-counting method structure adapted in `Puck.Maths`.
- **Daniel Lemire** — the nearly-divisionless bounded random draw used by the fixed-point PRNG.
- **Martin Roberts** — the R2 plastic-number generalization of golden-ratio low-discrepancy sequences.
- **kevtris** — hardware research pinning the half-cycle STAT-edge timing used by the DMG/CGB PPU model.
- **The Khronos Group** — the Vulkan and SPIR-V specifications.
- **Controller-input reverse engineering** — the USB HID protocols of the Nintendo Switch Pro, Sony DualSense, Steam, and Xbox controllers are not publicly specified, so the parsers were written against the community reverse-engineering record: dekuNukem's [Nintendo Switch Reverse Engineering](https://github.com/dekuNukem/Nintendo_Switch_Reverse_Engineering) (the Switch Pro handshake, the `0x30` input report, and HD rumble), the Linux kernel's `hid-playstation`, `hid-nintendo`, and `hid-steam` drivers (report layouts, factory IMU calibration, and calibration-triple ordering), [SDL](https://github.com/libsdl-org/SDL)'s HIDAPI controller drivers — including the Steam and Steam Triton drivers — (cross-referenced for scale factors, rumble safety limits, and feature-report framing), and Nielk1's [TriggerEffectGenerator](https://github.com/Nielk1/TriggerEffectGenerator) (the DualSense adaptive-trigger effect formats).

## Emulation References and Test Corpora

The GamingBrick machines were written against the community's hardware-research
record. Behavioral facts derived from these sources are stated self-contained
where the code uses them; the sources themselves are credited here.

- **SameBoy** ([Lior Halphon / LIJI32](https://sameboy.github.io/)) — reference emulator lineage for DMG/CGB hardware behavior (infrared port, HuC1/HuC3 IR windows, the link-cable printer protocol, PPU mode timing, SM83 STOP/DI/EI semantics), and origin of the **BESS** ("Best Effort Save State") savestate interchange format.
- **mGBA** ([endrift](https://mgba.io/)) — reference emulator for AGB hardware behavior (cartridge GPIO/RTC/rumble/solar/tilt protocols, the game-override hardware table, audio FIFO, DMA count-latching, timer→IRQ latency, SIO timing, PPU sprite-cycle budget) and the debug-log register protocol; the [mgba-emu/suite](https://github.com/mgba-emu/suite) accuracy ROM drives the AGB accuracy-suite stage.
- **ares** ([Near et al.](https://ares-emu.net/)) — the cycle-stepped co-simulator oracle for the AGB lockstep diagnostics, and a hardware-behavior cross-check for SM83 edge cases.
- **Pan Docs** ([gbdev.io](https://gbdev.io/pandocs/)) — the community DMG/CGB hardware reference.
- **GBATEK** (Martin Korth) — the AGB hardware register/timing reference.
- **blargg** — the DMG/CGB conformance test ROM suite behind the `conformance-*` Post stages.
- **Gekkio** — the [mooneye-test-suite](https://github.com/Gekkio/mooneye-test-suite) acceptance ROMs behind the `acceptance-*` Post stages; the oracle of record for EI-sequence and PPU-interrupt timing.
- **SingleStepTests/sm83** — the per-instruction SM83 vector corpus behind the single-step conformance stage.
- **jsmolka** — the [gba-tests](https://github.com/jsmolka/gba-tests) ARM/Thumb/memory/save conformance ROMs.
- **DenSinH** — the [FuzzARM](https://github.com/DenSinH/FuzzARM) randomized ARM/Thumb corpus and the AGSTests aging-cartridge decompilation.
- **pret/pokegold**, **pokemon-speedrunning/symfiles**, and **Bulbapedia** — the disassembly symbol maps and save-offset references behind the crafted trade-cart saves in the link-trade Post battery.

## Fonts

- **Caskaydia Mono Nerd Font** — [Cascadia Code](https://github.com/microsoft/cascadia-code) by Microsoft, licensed under the SIL Open Font License 1.1, with glyphs patched in by the [Nerd Fonts](https://github.com/ryanoasis/nerd-fonts) project (patched fonts remain OFL 1.1).

## Tools and Specifications

- **.NET** (Microsoft) — the runtime and scripting.
- **APNG** — Mozilla's animated extension to the PNG specification (W3C/ISO).
- **DXC** (the DirectX Shader Compiler, Microsoft) — single-source HLSL compiled to both SPIR-V and DXIL — and the **Vulkan SDK** (LunarG) — tooling and the validation layers.
- **mimalloc** ([Microsoft](https://github.com/microsoft/mimalloc)) — the default unmanaged allocator behind `IAllocator`.
- **CsWin32** (Microsoft) — the source-generated Win32 interop used by the platform layer (HID, windowing).
