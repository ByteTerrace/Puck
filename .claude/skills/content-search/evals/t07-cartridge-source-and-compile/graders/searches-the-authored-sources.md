---
type: llm
weight: 1
---

The response searches the authored `.puck` sources for `litColumns`: the light-gun source and the shared modules it imports.

PASS if the search covers the cartridge source and its imports, by naming the files, by an extension glob such as `-g "*.puck"`, or by searching the `worlds` tree. Example: `dotnet lane-cli/Puck.Cli.dll search -F -n -M 0 -g "*.puck" litColumns`, followed by checking which hits are in modules the cartridge imports.

FAIL if it searches only a compiled `*.cartridge.json`, only C# sources, or only the one cartridge file while ignoring its imports. Example: `puck search litColumns worlds/debug/cartridges/cgb/light-gun.cgb.cartridge.json`.
