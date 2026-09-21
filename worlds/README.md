# Worlds

This is the authoring home for official Puck asset packages, beside the engine's
`src/` tree. A package is a self-contained folder of Puck DSL sources, assets and
a manifest. It can be copied and compiled independently of this checkout.

[Puck Parlor](parlor/README.md) is the first package: physical chess, Chinese
checkers and Hearts sharing one DSL basis. Build instructions and entry points
are in its README; its manifest lists the files to distribute.

The other worlds remain under `src/Puck.World/Assets/worlds` for now. Parlor is
not built into or imported by `Puck.World`; it runs, compiles, and tests from its
own package folder.
