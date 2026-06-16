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
- **The Khronos Group** — the Vulkan and SPIR-V specifications.

## Fonts

- **Caskaydia Mono Nerd Font** — [Cascadia Code](https://github.com/microsoft/cascadia-code) by Microsoft, licensed under the SIL Open Font License 1.1, with glyphs patched in by the [Nerd Fonts](https://github.com/ryanoasis/nerd-fonts) project (patched fonts remain OFL 1.1).

## Tools and Specifications

- **.NET** (Microsoft) — the runtime and scripting.
- **APNG** — Mozilla's animated extension to the PNG specification (W3C/ISO).
- **glslc / shaderc** (Google) and the **Vulkan SDK** (LunarG) — shader compilation and the validation layers.
