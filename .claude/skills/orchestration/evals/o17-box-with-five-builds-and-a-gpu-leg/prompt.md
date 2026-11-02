---
description: "orchestration 'Schedule machines' and 'Coding lanes and the integration lane': one heavy job per machine, a load governor admits work, and every build passes `-nodeReuse:false`."
max_turns: 8
allowed_tools: [Read, Glob, Grep, Skill]
tags: [regression]
---

One 32 GB machine hosts five agents that all want to build, and a granted GPU leg is due. Each agent's full `Puck.World.Tests` run takes about 7 GB. How do you allocate the work?
