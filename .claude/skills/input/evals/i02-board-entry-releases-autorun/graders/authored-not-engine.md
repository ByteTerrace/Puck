---
type: llm
weight: 1
---

The response puts the weapon-swap continuity in the world's authored mode data, not in engine code or a binding page.

PASS if it proposes declaring the weapon modes as an input-mode family selected from authoritative state (a body register, kit or seat state) and a directed transition that keeps `fire`, with no weapon concept added to the engine, and does not rely on a binding page switch to change what the simulation accepts. Example: "Add a `weapon` family selected by a body register, and a transition row from `rifle` to `pistol` with `keep [\"fire\"]`."

FAIL if it proposes a C# special case, a new engine weapon state, or switching a binding page as the way to keep or change simulation input. Example: "Add a WeaponSwap handler in the server that skips releasing fire."
