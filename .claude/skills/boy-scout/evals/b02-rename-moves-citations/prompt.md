---
max_turns: 12
allowed_tools: [Read, Glob, Grep, Skill]
---

I'm partway through a change in the Puck repository. You can't see the checkout, so answer from the facts below: I'm renaming the public concept 'grant' to 'permit' in src/Puck.World.Server and renaming the heading `## Grants` in docs/architecture/worlds.md to `## Permits`. The rename moves line citations in two skills' references and breaks `#grants` anchors linked from docs and READMEs outside docs/architecture, and deleting the old `GrantsView` helper orphans names too. Is updating those other files part of my change, and how do I confirm nothing is left?
