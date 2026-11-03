---
type: llm
weight: 1
---

The response says the SD/SI ready bits are derived from the emulated link-cable partner (whether a partner console is attached and its state), and not from ambient or host state.

PASS if it says the bits come from the modeled link state, such as which peers are attached to the cable and each one's multiplayer mode and role, and not from a constant. Example: "The bits come from the modeled cable's partner: whether a second console is attached and in multiplayer mode."

FAIL if it derives them from host timing, a configuration flag, a constant 'ready' value, or leaves the source unstated. Example: "Set the SD bit high whenever multiplayer mode is selected."
