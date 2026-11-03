---
type: llm
weight: 1
---

The response says AOT and trim analysis (`IsAotCompatible=true`) is the repository's tree-wide default, which is why the new project fails, and that `PublishAot` is a separate per-executable choice that is not what triggers these warnings.

PASS if it says the analyzers run for every project by repository default (for example from `Directory.Build.props`), and that dropping `PublishAot` would not clear IL2026 and IL3050. Example: "`Directory.Build.props` turns `IsAotCompatible` on for the whole tree, so the new project is analysed by default; `PublishAot` is per-executable and does not cause these warnings."

FAIL if it treats the analysis as something this project opted into, or says dropping `PublishAot` would fix the build. Example: "Drop `PublishAot`; the warnings come from publishing for AOT."
