---
description: "A Linux camera backend was planned as an OS check inside the neutral Puck.Platform scanner that answers a failed scan with an empty list, against the per-seam registration shape and the scan-failure contract."
tags: [capability]
runs: 5
max_turns: 8
allowed_tools: [Read, Glob, Grep, Skill]
---

No Puck checkout is reachable from this session; answer from what you know of Puck.

I'm adding a V4L2 camera backend so Puck can use webcams on Linux. My plan: put an `if (OperatingSystem.IsLinux())` branch inside `Puck.Platform`'s `CameraDeviceScanner` that calls the V4L2 enumeration, and when the enumeration ioctl fails, return an empty device list so callers just see no cameras. Where should the backend and the OS choice live in Puck, and is the empty-list behaviour right?
