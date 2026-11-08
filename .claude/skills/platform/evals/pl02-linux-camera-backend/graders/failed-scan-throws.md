---
type: llm
weight: 1
---

The response rejects reporting a failed scan as an empty device list.

PASS if it says an empty list must mean the machine has no cameras, and a failed enumeration must be reported as a failure (Puck's camera contract throws `InvalidOperationException`) so callers never read a failed scan as every camera unplugged. Example: "Throw on a failed ioctl; `EnumerateDevices` returns empty only when there truly are none, and the scanner keeps failures distinct from empty snapshots."

FAIL if it accepts the empty list on failure, or does not address the failure case. Example: "Returning an empty list on failure keeps callers simple."
