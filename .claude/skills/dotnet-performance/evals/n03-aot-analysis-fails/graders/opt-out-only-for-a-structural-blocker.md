---
type: llm
weight: 1
---

The response rejects `IsAotCompatible=false` as a way to make this build pass, and allows a project-local opt-out at most for a structural blocker that source generation cannot remove, explained where the opt-out is set.

PASS example: "Setting it to false is only for a structural blocker, explained in a comment in the csproj. A reflection call a source-generated context can replace isn't one." A reply that rejects the opt-out outright also passes.

FAIL if it recommends the opt-out, or dropping `PublishAot`, as the fix here, or permits it without requiring a structural reason. Example: "Set `<IsAotCompatible>false</IsAotCompatible>` in the new project; you can revisit AOT later."
