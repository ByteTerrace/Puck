# Worlds

This is the authoring home for official Puck asset packages, beside the engine's
`src/` tree. A package is a self-contained folder of Puck DSL sources, assets and
a manifest. It can be copied and compiled independently of this checkout.

[Puck Parlor](parlor/README.md) is the first package: physical chess, Chinese
checkers and Hearts sharing one DSL basis. Build instructions and entry points
are in its README; its manifest lists the files to distribute.

[Puck Beacons](beacons/README.md) is the worked example of a module: one
parameterized beacon carrying its own tests, used twice across a border, with
tests written both inside the module and beside the source that uses it.

[Puck Rulepush](rulepush/README.md) is a rule-pushing puzzle in which an imp
pushes word tiles to rewrite the rules: one rules module, three levels that are
only their maps, and an overworld whose gate opens from what the visitor has
cleared.

[Puck Alchemy](alchemy/README.md) is a single-player discovery game: combine
two known elements to discover a third, with the recipe book stored as a grid
indexed by the pair.

[Puck Familiars](familiars/README.md) is a creature-taming game fought in real
time: one rules module that carries no setting, a default ruleset of twelve
seasonal elements, and a single-player world.

The other worlds remain under `src/Puck.World/Assets/worlds` for now. Parlor is
not built into or imported by `Puck.World`; it runs, compiles, and tests from its
own package folder.
