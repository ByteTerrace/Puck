---
description: "A pause-menu design told a tap of Menu from a hold with BindingChordTracker's held state and a tick count, a second hold detector beside the authored input patterns."
tags: [capability]
runs: 5
max_turns: 8
allowed_tools: [Read, Glob, Grep, Skill]
---

No Puck checkout is reachable from this session; answer from what you know of Puck.

I'm designing the pause menu's Menu button for a Puck world. While a seat is engaged at an emulated machine, a quick tap of the pad's Menu button should reach the machine as its Start, and holding it for about a second should open the pause menu instead and send the machine nothing. Outside a machine a tap just opens the menu. The tap-versus-hold decision has to be deterministic and survive replay. Which existing Puck mechanism should tell the tap from the hold, how would you express the two gestures with it, and what in that mechanism doesn't cover this case yet?
