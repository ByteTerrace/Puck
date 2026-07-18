# Puck API Reference

Generated member reference for the reusable Puck libraries listed below. Project
manuals provide conceptual guidance where available; this site is built directly
from source declarations and XML documentation.

> Scope: API generation currently includes the seven libraries below. Other
> projects remain documented by their project README, subsystem guide, or skill.

## Libraries

| Library | What it is | Manual |
|---------|-----------|--------|
| [Puck.Maths](xref:Puck.Maths) | Signed and unsigned deterministic fixed-point math, spatial primitives, reproducible randomness, and integer algorithms. | [README](https://github.com/ByteTerrace/ByteTerrace.Puck/blob/main/src/Puck.Maths/README.md) |
| [Puck.Commands](xref:Puck.Commands) | The engine-wide command system: typed, named, modality-aware input. | [README](https://github.com/ByteTerrace/ByteTerrace.Puck/blob/main/src/Puck.Commands/README.md) |
| [Puck.Input](xref:Puck.Input) | Cross-platform game-controller input — Nintendo Switch Pro, Sony DualSense, and Xbox pads normalized through the command system. | [README](https://github.com/ByteTerrace/ByteTerrace.Puck/blob/main/src/Puck.Input/README.md) |
| [Puck.Text](xref:Puck.Text) | Render-agnostic font atlas generation and text layout (MSDF/MTSDF). | [README](https://github.com/ByteTerrace/ByteTerrace.Puck/blob/main/src/Puck.Text/README.md) |
| [Puck.Abstractions](xref:Puck.Abstractions) | Backend-neutral GPU, surface, and allocator abstractions shared across the rendering backends. | — |
| [Puck.Vulkan](xref:Puck.Vulkan) | A from-scratch, interface-driven Vulkan layer. | [README](https://github.com/ByteTerrace/ByteTerrace.Puck/blob/main/src/Puck.Vulkan/README.md) |
| [Puck.Assets](xref:Puck.Assets) | Content-addressed asset loading and caching: a byte-source abstraction, SHA-256 content hashing, and an LRU cache. | [README](https://github.com/ByteTerrace/ByteTerrace.Puck/blob/main/src/Puck.Assets/README.md) |

Use the **API** link in the top navigation, or the search box, to browse types and members.

## Building this site

DocFX is pinned as a local .NET tool. From the repository root:

```bash
dotnet tool restore                  # one-time: restore the pinned docfx
dotnet docfx docs/api/docfx.json           # generate metadata + build the static site
dotnet docfx docs/api/docfx.json --serve   # ...or build and serve at http://localhost:8080
```

The generated `docs/api/api/*.yml` and `docs/api/_site/` are build output and are
git-ignored. The reference is regenerated from the projects' XML documentation each run, so
it always tracks the source.

