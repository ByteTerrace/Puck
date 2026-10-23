---
max_turns: 12
allowed_tools: [Read, Glob, Grep, Skill]
---

I'm partway through a change in the Puck repository. You can't see the checkout, so answer from the facts below: I changed the public option `IndentSize` on the world serializer and its `world.serialize` console command as requested, and the code and tests pass. The project README, the XML comments on the option, an inline comment above the schema layout, the owning subsystem skill's run recipe, and the generated docfx API pages all still describe the old contract. What is mine to update, how, and what if any surface cannot be updated in this run?
