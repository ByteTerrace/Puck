# Puck API reference

For concepts and practical guides, start with the [engine manual](https://github.com/ByteTerrace/Puck/blob/main/docs/README.md). This page covers generated member documentation.

Generated member reference for the reusable Puck libraries listed below. Manual
topics provide conceptual guidance; this site is built directly from source
declarations and XML documentation.

> Scope: API generation currently includes the libraries below. Other
> projects remain documented by their project README or subsystem guide.

## Libraries

| Library | What it is | Manual |
|---------|-----------|--------|
| [Puck.Abstractions](xref:Puck.Abstractions) | Backend-neutral GPU, surface, and allocator abstractions shared across the rendering backends. |—|
| [Puck.Assets](xref:Puck.Assets) | Content-addressed asset loading and caching: a byte-source abstraction, SHA-256 content hashing, and an LRU cache. | [README](https://github.com/ByteTerrace/Puck/blob/main/src/Puck.Assets/README.md) |
| [Puck.Attestation](xref:Puck.Attestation) | Issuer-signed claims that travel between federated worlds and verify offline against an authored trust list: canonical CBOR, chain walk, replay marks, and sealed attestations. | [README](https://github.com/ByteTerrace/Puck/blob/main/src/Puck.Attestation/README.md) |
| [Puck.Commands](xref:Puck.Commands) | Typed, named input for simulations that advance in fixed steps. | [Commands and input](https://github.com/ByteTerrace/Puck/blob/main/docs/reference/commands.md) |
| [Puck.Hosting](xref:Puck.Hosting) | Recursive render-node hosting, capability propagation, fixed-step simulation context, and cross-thread publish buffers. | [README](https://github.com/ByteTerrace/Puck/blob/main/src/Puck.Hosting/README.md) |
| [Puck.Input](xref:Puck.Input) | Cross-platform game-controller input—Nintendo Switch Pro, Sony DualSense, and Xbox pads normalized through the command system. | [Device input](https://github.com/ByteTerrace/Puck/blob/main/docs/reference/input.md) |
| [Puck.Maths](xref:Puck.Maths) | Signed and unsigned deterministic fixed-point math, spatial primitives, reproducible randomness, and integer algorithms. | [Deterministic numerics](https://github.com/ByteTerrace/Puck/blob/main/docs/reference/maths.md) |
| [Puck.Physics](xref:Puck.Physics) | Deterministic fixed-point simulation kernels: gravity, contact geometry, and a substep sequential-impulse rigid solver. | [Physics kernels](https://github.com/ByteTerrace/Puck/blob/main/docs/reference/physics.md) |
| [Puck.State](xref:Puck.State) | Named state rows, compiled rules, and hypothetical evaluation. | [State and rules](https://github.com/ByteTerrace/Puck/blob/main/docs/reference/state.md) |
| [Puck.Text](xref:Puck.Text) | Render-agnostic font atlas generation and text layout (MSDF/MTSDF). | [README](https://github.com/ByteTerrace/Puck/blob/main/src/Puck.Text/README.md) |
| [Puck.Vulkan](xref:Puck.Vulkan) | A Vulkan implementation of the neutral GPU interfaces. | [README](https://github.com/ByteTerrace/Puck/blob/main/src/Puck.Vulkan/README.md) |

Use the **API** link in the top navigation, or the search box, to browse types and members.

## Build the API site

DocFX is pinned as a local .NET tool. From the repository root:

```bash
dotnet tool restore                  # one-time: restore the pinned docfx
dotnet docfx docs/api/docfx.json           # generate metadata + build the static site
dotnet docfx docs/api/docfx.json --serve   # ...or build and serve at http://localhost:8080
```

The generated `docs/api/api/*.yml` and `docs/api/_site/` are build output and are
git-ignored. The reference is regenerated from the projects' XML documentation each run, so its contents reflect the declarations used by that build.
