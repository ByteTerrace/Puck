---
type: llm
weight: 1
---

The response asks for a profile, trace or other measurement showing the path is hot before changing it.

PASS if it says the suspected hot path is unproven and asks for (or takes) a profile, trace, frame counter or allocation reading that shows it before the edit. Example: "A suspicion is not enough: get a `dotnet-trace` or a `puck bench world` reading that shows this pipeline is hot before rewriting it."

FAIL if it goes straight to the rewrite on the suspicion alone. Example: "Replace the LINQ with a loop; here is the code."
