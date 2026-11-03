---
type: llm
weight: 1
---

The response says to search the repository beyond the edited directory for other mentions of the changed option and command, to find surfaces the prompt did not list.

PASS if it says to search (`puck search`, `puck references`, grep or a link check) for `IndentSize` or `world.serialize` across docs, skills, scenarios, CI or other directories, to catch surfaces beyond the five named. Example: "Run `puck search -M 0 -l` for `IndentSize` and `world.serialize` across the repository, since the five surfaces you listed may not be all of them."

FAIL if it only updates the five listed surfaces and proposes no search for others. Example: "Update the README, the XML comments, the inline comment, the recipe and regenerate the pages."
