---
type: llm
weight: 1
---

The response derives the SD/SI ready bits from the presence of the modeled link-cable partner, and not from ambient or host state.

PASS example: "The bits come from the modeled `AgbLinkCable` partner, whether a partner console is attached, and never from ambient state."

FAIL if it derives them from host timing, a configuration flag, a constant "ready" value, or leaves the source unstated. Example: "Set the SD bit high whenever multiplayer mode is selected."
