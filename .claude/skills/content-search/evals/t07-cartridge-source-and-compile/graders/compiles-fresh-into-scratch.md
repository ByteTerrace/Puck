---
type: llm
weight: 1
---

The response checks the compiled form by compiling the cartridge fresh (with `puck compile`) into a scratch or temporary location and then inspecting or searching that output by naming its path, because generated output is not in the default search walk.

PASS example: "`dotnet lane-cli/Puck.Cli.dll compile worlds/debug/cartridges/cgb/light-gun.cgb.puck --output ../scratch/light-gun.cartridge.json`, then `search -F -n litColumns ../scratch/light-gun.cartridge.json` and look at the `map` values." A reply that defers the exact compile flags but states this procedure also passes.

FAIL if it looks for a committed compiled JSON, asks for one to be committed, assumes an ordinary search of the tree would find generated output, or never addresses the compiled form. Example: "Search `*.cartridge.json` in the repo; if it's empty the value never reaches the cartridge."
