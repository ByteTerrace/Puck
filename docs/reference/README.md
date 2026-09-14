# Reference

Use these references when you know which command, field, or API you need.
For an introduction, start with the [engine overview](../overview.md) or the
[architecture guide](../architecture/README.md).

| Reference | Contents |
|---|---|
| [State and rules](state.md) | A worked introduction, then chapters on data, expressions, rules, frames, generators, search, and hosting. |
| [Commands and input](commands.md) | Fixed-step input, bindings, text sessions, dispatch identity, and ordering. |
| [Device input](input.md) | Device acquisition, controller motion, haptics, lighting, and platform integration. |
| [Physics kernels](physics.md) | Gravity, motion, contacts, tethers, perception, navigation, and fields. |
| [Deterministic numerics](maths.md) | Worked examples, primitive selection, and the boundary between reproducible simulation and other numeric work. |
| [Seam abstractions](abstractions.md) | Neutral presentation, windowing, machine, and platform contracts. |
| [Hosting and simulation](hosting.md) | Host loop, render nodes, fixed-step clocks, and capability trees. |
| [Wire framing and substrate](networking.md) | Dialect-agnostic length-prefixed framing and local token authentication. |
| [Offline attestation](attestation.md) | Signed claims, key bindings, deterministic CBOR envelopes, and trust pinning. |
| [Capture and recording](recording.md) | Matroska/WebM muxing, Opus audio lanes, frame observers, and overlays. |
| [Scripting and addon ABI](scripting.md) | Deterministic WebAssembly host, 32-byte cell ring ABI, and zero ambient authority. |
| [Content addressing and assets](assets.md) | Content-addressed storage, LRU caching, PNG/APNG, QR, and automatic sequences. |
| [Audio and music direction](audio.md) | Fixed-point mixing, 32-voice synthesizer, music clocks, and babble schedules. |
| [Text and font atlases](text.md) | OpenType readers, MTSDF generation, layout, and distance-field sampling. |
| [Puck CLI](cli.md) | Repository commands, compilation, inspection, and verification tools. Run a command with `--help` for its options. |
| [World commands](../../src/Puck.World/README.md) | The running application's console and launch options. |
| [World schema](../../src/Puck.World.Schema/README.md) | Document fields, validation, and composition. |
| [World name registry](../world-name-registry.md) | Generated names and field ownership; regenerate through the owning CLI command. |
| [Puck DSL](dsl.md) | Language syntax, values, units, templates, and diagnostics. |
| [Shader documents](shaders.md) | Shader manifests, pipeline resources, bindings, and compilation. |
| [Cartridge documents](../emulation/shared/cartridge-forge.md) | Native cartridge authoring, pointer editing, and target restrictions. |
| [Puck API reference](../api/index.md) | Generated members for the libraries selected by the DocFX configuration. |
| [Puck project map](../project-map.md) | Responsibilities and generated dependency layering. |
| [Citations and evidence sources](../citations.md) | Source material for mathematical and hardware claims. |

Reference pages define current contracts. [Plans](../plans/README.md) describe
proposed changes, and [design decisions](../decisions/README.md) preserve their
reasoning. Generated API output and registries are updated from their sources.
