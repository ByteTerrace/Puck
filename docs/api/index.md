# Puck API Reference

Generated API documentation for the stable, hand-documented Puck libraries. Each library
also has a hand-written manual (its `README.md`) that explains the concepts; this site is
the member-by-member reference built from the XML documentation in the source.

> Scope: the seven libraries below are documented here. Other `Puck.*` projects are
> intentionally excluded while they are in flux.

## Libraries

| Library | What it is | Manual |
|---------|-----------|--------|
| [Puck.Maths](xref:Puck.Maths) | Unsigned fixed-point types and branchless, width-agnostic integer/prime routines. | [README](https://github.com/ByteTerrace/ByteTerrace.Puck/blob/main/src/Puck.Maths/README.md) |
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

