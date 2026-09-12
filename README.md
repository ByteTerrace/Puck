# Puck

Puck is a C# engine for document-defined interactive worlds. It combines
reproducible simulation, GPU rendering, programmable shader pipelines and
Game Boy/Game Boy Color and Game Boy Advance emulation. `Puck.World` is the
application that brings these libraries together.

Start with the **[engine documentation](docs/README.md)** or follow
**[build and run](docs/getting-started.md)**. The
[overview](docs/overview.md) explains the major components and how a document
becomes a running world.

## Explore the repository

| Task | Guide |
|---|---|
| Understand runtime boundaries | [Architecture](docs/architecture/README.md) |
| Author worlds, shaders or cartridges | [Authoring](docs/authoring/README.md) |
| Work on graphics | [Rendering](docs/rendering/README.md) |
| Work on handheld emulation | [Emulation](docs/emulation/README.md) |
| Investigate, test or contribute | [Development](docs/development/README.md) |
| Find a project or API | [Project map](docs/project-map.md) and [reference](docs/reference/README.md) |
| Review upcoming work | [Plans](docs/plans/README.md) |

The [reference game](docs/game/README.md) has its own design and artwork.
It demonstrates engine features without defining the requirements of every
library consumer.

## Packages and verification

Packable projects use `ByteTerrace.Puck.*` package names with `Puck.*`
assemblies and namespaces. Each package includes its project README and license
material. The [release guide](docs/development/ci.md) explains shared versioning,
package selection and validation.

Verification depends on the changed subsystem: simulation laws, native emulator
execution and rendered World behavior answer different questions. Follow the
[development guide](docs/development/README.md) for the appropriate checks and
the scope of their evidence.

See [acknowledgments](ACKNOWLEDGMENTS.md) for the work Puck builds on.

## License

Puck is **source-available and dual-licensed** — not open source. Noncommercial
use (including by individuals, schools, universities, and government bodies) is
free under the [PolyForm Noncommercial License 1.0.0](LICENSE.md); commercial use
requires a paid license. See [LICENSING.md](LICENSING.md) for who needs what and
how to obtain a commercial license.

Separately licensed components, including the bundled GamingBrick firmware,
retain their own terms; see the [license inventory](THIRD-PARTY-NOTICES.md).
